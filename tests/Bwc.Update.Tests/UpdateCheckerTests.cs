using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

// Pipeline cases from docs/failure-modes/update-verifier.md (sections A to E, M, R), run against the
// loopback server. CheckAsync is the manifest step only; DownloadAsync also fetches and verifies the
// installer and owns the staging folder.
[TestClass]
public sealed class UpdateCheckerTests
{
    private const int MaxManifestBytes = 256 * 1024;

    [TestMethod]
    public async Task Download_Ready_Writes_Receipt_And_Installer()
    {
        await using var s = new Scenario();
        var payload = s.AddInstaller(Architectures.X64);
        s.Publish();

        var outcome = await new UpdateChecker(s.Options()).DownloadAsync(s.Request(), CancellationToken.None);

        Assert.IsTrue(outcome.Ok, $"{outcome.Failure}: {outcome.Detail}");
        Assert.AreEqual(Scenario.DefaultVersion, outcome.Value!.Receipt.Version);
        Assert.AreEqual(payload.Sha256, outcome.Value.Receipt.Sha256);
        Assert.IsTrue(File.Exists(outcome.Value.ReceiptPath));
        CollectionAssert.AreEqual(payload.Bytes, File.ReadAllBytes(outcome.Value.InstallerPath));
        Assert.AreEqual("installer.exe", Path.GetFileName(outcome.Value.InstallerPath));
    }

    [TestMethod]
    public async Task Check_Does_Not_Download_Or_Stage()
    {
        await using var s = new Scenario();
        s.AddInstaller(Architectures.X64);
        s.Publish();

        var outcome = await new UpdateChecker(s.Options()).CheckAsync(s.Request(), CancellationToken.None);

        Assert.IsTrue(outcome.Ok, $"{outcome.Failure}: {outcome.Detail}");
        Assert.AreEqual(Scenario.DefaultVersion, outcome.Value!.Version);
        Assert.AreEqual(UpdateChannel.Beta, outcome.Value.Channel);
        Assert.AreEqual("https://example.invalid/notes", outcome.Value.NotesUrl!.AbsoluteUri);
        Assert.AreEqual(0, s.Server.Hits("/bwc/win-x64.exe"), "the check must not request the installer");
        Assert.IsFalse(Directory.Exists(s.StagingRoot), "the check must not create a staging folder");
    }

    // The manifest, its signature, and the installer each sit behind a GitHub-shaped 302 chain:
    // latest/download -> releases/download/v0.1.0 -> release-assets with a long signed query.
    [TestMethod]
    public async Task Github_Shaped_Redirect_Chain_Accepted()
    {
        await using var s = new Scenario();
        var payload = s.AddInstaller(Architectures.X64, path: "/releases/download/v0.1.0/win-x64.exe");
        s.Publish();
        var query = GithubShapedQuery.Build();
        var (manifestHop, manifestFinal) = Chain(s, "/latest/download/beta.json", "/releases/download/v0.1.0/beta.json", "manifest", query);
        var (signatureHop, signatureFinal) = Chain(s, "/latest/download/beta.json.minisig", "/releases/download/v0.1.0/beta.json.minisig", "signature", query);
        var (_, installerFinal) = Chain(s, "/releases/download/v0.1.0/win-x64.exe", null, "installer", query);
        s.Server.Map(manifestFinal, new Route(Body: s.ManifestBytes));
        s.Server.Map(signatureFinal, new Route(Body: s.ManifestSignatureBytes));
        s.Server.Map(installerFinal, new Route(Body: payload.Bytes));
        var request = new UpdateRequest(
            s.Server.Url(manifestHop), s.Server.Url(signatureHop), UpdateChannel.Beta, "0.1.0", Architectures.X64, s.StagingRoot);
        var checker = new UpdateChecker(s.Options());

        var check = await checker.CheckAsync(request, CancellationToken.None);
        Assert.IsTrue(check.Ok, $"{check.Failure}: {check.Detail}");
        Assert.AreEqual(1, s.Server.Hits(manifestFinal));

        var download = await checker.DownloadAsync(request, CancellationToken.None);
        Assert.IsTrue(download.Ok, $"{download.Failure}: {download.Detail}");
        CollectionAssert.AreEqual(payload.Bytes, File.ReadAllBytes(download.Value!.InstallerPath));
        Assert.AreEqual(1, s.Server.Hits(installerFinal));
    }

    // Maps a one-hop chain (second is null) or a two-hop chain. Returns the first hop and the final target with query.
    private static (string First, string Final) Chain(Scenario s, string first, string? second, string name, string query)
    {
        var final = $"/github-production-release-asset/{name}/guid?{query}";
        if (second is null)
        {
            s.Server.Map(first, new Route(Status: 302, Location: s.Server.Url(final).AbsoluteUri));
        }
        else
        {
            s.Server.Map(first, new Route(Status: 302, Location: s.Server.Url(second).AbsoluteUri));
            s.Server.Map(second, new Route(Status: 302, Location: s.Server.Url(final).AbsoluteUri));
        }

        return (first, final);
    }

    [TestMethod]
    public async Task Manifest_SignatureNotFound_Reported()
    {
        await using var s = new Scenario();
        s.AddInstaller(Architectures.X64);
        s.Publish(publishSignature: false);

        var outcome = await new UpdateChecker(s.Options()).CheckAsync(s.Request(), CancellationToken.None);

        Expect.Fails(outcome, UpdateFailure.ManifestSignatureDownload);
    }

    [TestMethod]
    public async Task Manifest_OverCap_Rejected()
    {
        await using var s = new Scenario();
        s.AddInstaller(Architectures.X64);
        s.PadManifestTo(MaxManifestBytes + 1);
        s.Publish();

        var outcome = await new UpdateChecker(s.Options()).CheckAsync(s.Request(), CancellationToken.None);

        Expect.Fails(outcome, UpdateFailure.ManifestTooLarge);
    }

    [TestMethod]
    public async Task Manifest_AtCap_Accepted()
    {
        await using var s = new Scenario();
        s.AddInstaller(Architectures.X64);
        s.PadManifestTo(MaxManifestBytes);
        s.Publish();

        Assert.AreEqual(MaxManifestBytes, s.ManifestBytes.Length);
        var outcome = await new UpdateChecker(s.Options()).CheckAsync(s.Request(), CancellationToken.None);

        Assert.IsTrue(outcome.Ok, $"{outcome.Failure}: {outcome.Detail}");
    }

    [TestMethod]
    public async Task Manifest_Version_Mismatch_With_Comment_Rejected()
    {
        await using var s = new Scenario();
        s.AddInstaller(Architectures.X64);
        s.Publish(commentVersion: "0.2.1");

        var outcome = await new UpdateChecker(s.Options()).CheckAsync(s.Request(), CancellationToken.None);

        Expect.Fails(outcome, UpdateFailure.ManifestInvalid);
    }

    [TestMethod]
    public async Task Interrupted_Download_Rejected()
    {
        await using var s = new Scenario();
        var payload = s.AddInstaller(Architectures.X64);
        s.Publish();
        s.Server.Map("/bwc/win-x64.exe", new Route(
            Body: payload.Bytes,
            DeclaredLength: payload.Bytes.Length + 1000,
            StopAfter: payload.Bytes.Length / 2));

        var outcome = await new UpdateChecker(s.Options()).DownloadAsync(s.Request(), CancellationToken.None);

        Expect.Fails(outcome, UpdateFailure.InstallerDownloadInterrupted);
        Assert.AreEqual(0, StagingEntries(s));
    }

    [TestMethod]
    public async Task Stalled_Download_Times_Out()
    {
        await using var s = new Scenario();
        var payload = s.AddInstaller(Architectures.X64);
        s.Publish();
        s.Server.Map("/bwc/win-x64.exe", new Route(Body: payload.Bytes, StopAfter: 1000, Pause: TimeSpan.FromSeconds(10)));

        var outcome = await new UpdateChecker(s.Options(stall: TimeSpan.FromMilliseconds(300)))
            .DownloadAsync(s.Request(), CancellationToken.None);

        Expect.Fails(outcome, UpdateFailure.Timeout);
        Assert.AreEqual(0, StagingEntries(s));
    }

    [TestMethod]
    public async Task Overall_Timeout_Rejected()
    {
        await using var s = new Scenario();
        var payload = s.AddInstaller(Architectures.X64);
        s.Publish();
        s.Server.Map("/bwc/win-x64.exe", new Route(Body: payload.Bytes, TrickleDelay: TimeSpan.FromMilliseconds(30)));

        var options = s.Options(stall: TimeSpan.FromSeconds(5), installerDownload: TimeSpan.FromMilliseconds(400));
        var outcome = await new UpdateChecker(options).DownloadAsync(s.Request(), CancellationToken.None);

        Expect.Fails(outcome, UpdateFailure.Timeout);
    }

    [TestMethod]
    public async Task Http_Error_Rejected()
    {
        await using var s = new Scenario();
        var payload = s.AddInstaller(Architectures.X64);
        s.Publish();
        s.Server.Map("/bwc/win-x64.exe", new Route(Status: 500, Body: payload.Bytes));

        var outcome = await new UpdateChecker(s.Options()).DownloadAsync(s.Request(), CancellationToken.None);

        Expect.Fails(outcome, UpdateFailure.InstallerDownload);
        Assert.IsTrue(outcome.Detail.Contains("500", StringComparison.Ordinal), outcome.Detail);
    }

    [TestMethod]
    public async Task Failed_Download_Leaves_No_Files()
    {
        await using var s = new Scenario();
        var payload = s.AddInstaller(Architectures.X64);
        s.Publish();
        var tampered = (byte[])payload.Bytes.Clone();
        tampered[100] ^= 0x01;
        s.Server.Map("/bwc/win-x64.exe", new Route(Body: tampered));

        var outcome = await new UpdateChecker(s.Options()).DownloadAsync(s.Request(), CancellationToken.None);

        Expect.Fails(outcome, UpdateFailure.InstallerHashMismatch);
        Assert.AreEqual(0, StagingEntries(s));
    }

    [TestMethod]
    public async Task Receipt_Only_After_Verification()
    {
        await using var s = new Scenario();
        var payload = s.AddInstaller(Architectures.X64);
        s.Publish();
        var tampered = (byte[])payload.Bytes.Clone();
        tampered[7] ^= 0x01;
        s.Server.Map("/bwc/win-x64.exe", new Route(Body: tampered));
        var checker = new UpdateChecker(s.Options());

        var failed = await checker.DownloadAsync(s.Request(), CancellationToken.None);
        Expect.Fails(failed, UpdateFailure.InstallerHashMismatch);
        Assert.AreEqual(0, Directory.Exists(s.StagingRoot) ? Directory.GetFiles(s.StagingRoot, "receipt.json", SearchOption.AllDirectories).Length : 0, "the failed attempt wrote no receipt");

        s.Server.Map("/bwc/win-x64.exe", new Route(Body: payload.Bytes));
        var ok = await checker.DownloadAsync(s.Request(), CancellationToken.None);
        Assert.IsTrue(ok.Ok, ok.Detail);
        Assert.AreEqual(1, Directory.GetFiles(s.StagingRoot, "receipt.json", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task Download_Reuses_Verified_Staging()
    {
        await using var s = new Scenario();
        s.AddInstaller(Architectures.X64);
        s.Publish();
        var checker = new UpdateChecker(s.Options());

        var first = await checker.DownloadAsync(s.Request(), CancellationToken.None);
        var second = await checker.DownloadAsync(s.Request(), CancellationToken.None);

        Assert.IsTrue(first.Ok && second.Ok, $"{first.Detail} {second.Detail}");
        Assert.AreEqual(Path.GetDirectoryName(first.Value!.InstallerPath), Path.GetDirectoryName(second.Value!.InstallerPath), "the verified folder is reused");
        Assert.AreEqual(1, s.Server.Hits("/bwc/win-x64.exe"), "the installer is downloaded once");
    }

    [TestMethod]
    public async Task Download_Reuse_Tampered_Installer_Redownloads()
    {
        await using var s = new Scenario();
        var payload = s.AddInstaller(Architectures.X64);
        s.Publish();
        var checker = new UpdateChecker(s.Options());
        var first = await checker.DownloadAsync(s.Request(), CancellationToken.None);
        Assert.IsTrue(first.Ok, first.Detail);

        var bytes = File.ReadAllBytes(first.Value!.InstallerPath);
        bytes[200] ^= 0x01;
        File.WriteAllBytes(first.Value.InstallerPath, bytes);
        var second = await checker.DownloadAsync(s.Request(), CancellationToken.None);

        Assert.IsTrue(second.Ok, $"{second.Failure}: {second.Detail}");
        CollectionAssert.AreEqual(payload.Bytes, File.ReadAllBytes(second.Value!.InstallerPath));
        Assert.AreEqual(2, s.Server.Hits("/bwc/win-x64.exe"), "a tampered staged installer is downloaded again");
    }

    [TestMethod]
    public async Task Download_Ignores_Receipt_Of_Other_Version()
    {
        await using var s = new Scenario();
        var payload = s.AddInstaller(Architectures.X64);
        s.Publish();
        var stale = Path.Combine(s.StagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stale);
        File.WriteAllBytes(Path.Combine(stale, "installer.exe"), payload.Bytes);
        var staleReceipt = Path.Combine(stale, UpdateReceipt.FileName);
        UpdateReceipt.Write(
            staleReceipt,
            new UpdateReceipt("0.1.5", UpdateChannel.Beta, Architectures.X64, payload.Bytes.Length, payload.Sha256, payload.Envelope, payload.Comment));
        Directory.SetCreationTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(staleReceipt, DateTime.UtcNow.AddDays(-2));

        var outcome = await new UpdateChecker(s.Options()).DownloadAsync(s.Request(), CancellationToken.None);

        Assert.IsTrue(outcome.Ok, $"{outcome.Failure}: {outcome.Detail}");
        Assert.AreEqual(1, s.Server.Hits("/bwc/win-x64.exe"), "a receipt for another version is not reused");
        Assert.IsFalse(Directory.Exists(stale), "the folder for another version is pruned once it is older than 24 hours");
    }

    // S12: a folder younger than 24 hours belongs to another install's staged update, so it is kept.
    [TestMethod]
    public async Task Download_Keeps_Younger_Staging()
    {
        await using var s = new Scenario();
        s.AddInstaller(Architectures.X64);
        s.Publish();
        var incomplete = Path.Combine(s.StagingRoot, "0123456789abcdef0123456789abcdef");
        Directory.CreateDirectory(incomplete);
        File.WriteAllText(Path.Combine(incomplete, "installer.exe"), "partial");
        var younger = Path.Combine(s.StagingRoot, "fedcba9876543210fedcba9876543210");
        Directory.CreateDirectory(younger);
        File.WriteAllText(Path.Combine(younger, "receipt.json"), "{}");

        var outcome = await new UpdateChecker(s.Options()).DownloadAsync(s.Request(), CancellationToken.None);

        Assert.IsTrue(outcome.Ok, $"{outcome.Failure}: {outcome.Detail}");
        var folders = Directory.GetDirectories(s.StagingRoot).Order().ToArray();
        var expected = new[] { incomplete, younger, Path.GetDirectoryName(outcome.Value!.InstallerPath)! }.Order().ToArray();
        CollectionAssert.AreEqual(expected, folders);
    }

    // S13: folders older than 24 hours are pruned. Age is the later of folder creation and receipt write time.
    [TestMethod]
    public async Task Download_Prunes_Older_Staging()
    {
        await using var s = new Scenario();
        s.AddInstaller(Architectures.X64);
        s.Publish();
        var incomplete = Path.Combine(s.StagingRoot, "0123456789abcdef0123456789abcdef");
        Directory.CreateDirectory(incomplete);
        File.WriteAllText(Path.Combine(incomplete, "installer.exe"), "partial");
        Directory.SetCreationTimeUtc(incomplete, DateTime.UtcNow.AddDays(-2));
        var older = Path.Combine(s.StagingRoot, "fedcba9876543210fedcba9876543210");
        Directory.CreateDirectory(older);
        File.WriteAllText(Path.Combine(older, "receipt.json"), "{}");
        Directory.SetCreationTimeUtc(older, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(Path.Combine(older, "receipt.json"), DateTime.UtcNow.AddDays(-2));

        var outcome = await new UpdateChecker(s.Options()).DownloadAsync(s.Request(), CancellationToken.None);

        Assert.IsTrue(outcome.Ok, $"{outcome.Failure}: {outcome.Detail}");
        var folders = Directory.GetDirectories(s.StagingRoot);
        CollectionAssert.AreEqual(new[] { Path.GetDirectoryName(outcome.Value!.InstallerPath)! }, folders);
    }

    // S5: a staged installer that another process holds cannot be opened for reuse. The folder is skipped, not fatal.
    [TestMethod]
    public async Task Reuse_Unreadable_Folder_Skipped()
    {
        await using var s = new Scenario();
        var payload = s.AddInstaller(Architectures.X64);
        s.Publish();
        var folder = Path.Combine(s.StagingRoot, "fedcba9876543210fedcba9876543210");
        Directory.CreateDirectory(folder);
        var installer = Path.Combine(folder, "installer.exe");
        File.WriteAllBytes(installer, payload.Bytes);
        UpdateReceipt.Write(
            Path.Combine(folder, UpdateReceipt.FileName),
            new UpdateReceipt(s.Version, UpdateChannel.Beta, Architectures.X64, payload.Bytes.Length, payload.Sha256, payload.Envelope, payload.Comment));

        using var held = File.Open(installer, FileMode.Open, FileAccess.Read, FileShare.None);
        var outcome = await new UpdateChecker(s.Options()).DownloadAsync(s.Request(), CancellationToken.None);
        held.Dispose();

        Assert.IsTrue(outcome.Ok, $"{outcome.Failure}: {outcome.Detail}");
        Assert.AreEqual(1, s.Server.Hits("/bwc/win-x64.exe"), "the locked folder is not reused, so the installer is downloaded");
    }

    [TestMethod]
    public async Task Download_Reports_Progress()
    {
        await using var s = new Scenario();
        var payload = s.AddInstaller(Architectures.X64, size: 300 * 1024);
        s.Publish();
        var reports = new List<DownloadProgress>();
        var progress = new SynchronousProgress(reports.Add);

        var outcome = await new UpdateChecker(s.Options()).DownloadAsync(s.Request(), CancellationToken.None, progress);

        Assert.IsTrue(outcome.Ok, $"{outcome.Failure}: {outcome.Detail}");
        Assert.IsTrue(reports.Count >= 2, "progress is reported more than once");
        Assert.IsTrue(reports.All(r => r.Total == payload.Bytes.Length));
        for (var i = 1; i < reports.Count; i++)
        {
            Assert.IsTrue(reports[i].Received >= reports[i - 1].Received, "progress never goes backward");
        }

        Assert.AreEqual(payload.Bytes.Length, reports[^1].Received);
    }

    [TestMethod]
    public async Task Cancellation_Removes_Partial_File()
    {
        await using var s = new Scenario();
        var payload = s.AddInstaller(Architectures.X64);
        s.Publish();
        s.Server.Map("/bwc/win-x64.exe", new Route(Body: payload.Bytes, TrickleDelay: TimeSpan.FromMilliseconds(50)));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));

        var outcome = await new UpdateChecker(s.Options()).DownloadAsync(s.Request(), cancel.Token);

        Expect.Fails(outcome, UpdateFailure.Canceled);
        Assert.AreEqual(0, StagingEntries(s));
    }

    [TestMethod]
    public async Task Staging_Root_Is_File_Reported()
    {
        await using var s = new Scenario();
        s.AddInstaller(Architectures.X64);
        s.Publish();
        var notAFolder = s.Dir.Write("not-a-folder", [1, 2, 3]);

        var outcome = await new UpdateChecker(s.Options()).DownloadAsync(s.Request() with { StagingRoot = notAFolder }, CancellationToken.None);

        Expect.Fails(outcome, UpdateFailure.Io);
    }

    [TestMethod]
    public async Task Connection_Refused_Reported()
    {
        await using var s = new Scenario();
        var closedPort = ClosedLoopbackPort();
        var request = s.Request() with
        {
            ManifestUrl = new Uri($"http://127.0.0.1:{closedPort}/bwc/beta.json"),
            ManifestSignatureUrl = new Uri($"http://127.0.0.1:{closedPort}/bwc/beta.json.minisig"),
        };

        // Windows can take about two seconds to refuse a loopback connection, so the stall limit must be longer.
        var outcome = await new UpdateChecker(s.Options(stall: TimeSpan.FromSeconds(10))).CheckAsync(request, CancellationToken.None);

        Expect.Fails(outcome, UpdateFailure.DownloadFailed);
    }

    [TestMethod]
    public async Task Html_Page_As_Installer_Rejected()
    {
        await using var s = new Scenario();
        var payload = s.AddInstaller(Architectures.X64);
        s.Publish();
        var html = Encoding.ASCII.GetBytes("<html>" + new string(' ', payload.Bytes.Length - 13) + "</html>");
        s.Server.Map("/bwc/win-x64.exe", new Route(Body: html));

        var outcome = await new UpdateChecker(s.Options()).DownloadAsync(s.Request(), CancellationToken.None);

        Expect.Fails(outcome, UpdateFailure.InstallerHashMismatch);
    }

    [TestMethod]
    public async Task Redirect_Followed_On_Loopback()
    {
        await using var s = new Scenario();
        s.AddInstaller(Architectures.X64, path: "/bwc/link.exe");
        var payload = s.Installer(Architectures.X64);
        s.Publish();
        s.Server.Map("/bwc/link.exe", new Route(Status: 302, Location: "/bwc/final.exe"));
        s.Server.Map("/bwc/final.exe", new Route(Body: payload.Bytes));

        var outcome = await new UpdateChecker(s.Options()).DownloadAsync(s.Request(), CancellationToken.None);

        Assert.IsTrue(outcome.Ok, $"{outcome.Failure}: {outcome.Detail}");
    }

    [TestMethod]
    public async Task Redirect_Loop_Rejected()
    {
        await using var s = new Scenario();
        s.AddInstaller(Architectures.X64, path: "/bwc/a.exe");
        s.Publish();
        s.Server.Map("/bwc/a.exe", new Route(Status: 302, Location: "/bwc/b.exe"));
        s.Server.Map("/bwc/b.exe", new Route(Status: 302, Location: "/bwc/a.exe"));

        var outcome = await new UpdateChecker(s.Options()).DownloadAsync(s.Request(), CancellationToken.None);

        Expect.Fails(outcome, UpdateFailure.DownloadFailed);
    }

    [TestMethod]
    public async Task Local_File_Name_Is_Fixed()
    {
        await using var s = new Scenario();
        s.AddInstaller(Architectures.X64, path: "/bwc/Setup%20(1).exe");
        s.Publish();

        var outcome = await new UpdateChecker(s.Options()).DownloadAsync(s.Request(), CancellationToken.None);

        Assert.IsTrue(outcome.Ok, $"{outcome.Failure}: {outcome.Detail}");
        Assert.AreEqual("installer.exe", Path.GetFileName(outcome.Value!.InstallerPath));
        Assert.AreEqual(Path.Combine(s.StagingRoot, Path.GetFileName(Path.GetDirectoryName(outcome.Value.InstallerPath)!)), Path.GetDirectoryName(outcome.Value.InstallerPath));
    }

    [TestMethod]
    public async Task Http_Url_Rejected_When_Https_Required()
    {
        await using var s = new Scenario();
        s.AddInstaller(Architectures.X64);
        s.Publish();

        var outcome = await new UpdateChecker(s.Options() with { RequireHttps = true })
            .CheckAsync(s.Request(), CancellationToken.None);

        Expect.Fails(outcome, UpdateFailure.UrlRejected);
    }

    [TestMethod]
    public async Task Installer_Larger_Than_Manifest_Rejected()
    {
        await using var s = new Scenario();
        var payload = s.AddInstaller(Architectures.X64);
        s.Publish();
        s.Server.Map("/bwc/win-x64.exe", new Route(Body: [.. payload.Bytes, 1, 2, 3, 4]));

        var outcome = await new UpdateChecker(s.Options()).DownloadAsync(s.Request(), CancellationToken.None);

        Expect.Fails(outcome, UpdateFailure.InstallerTooLarge);
        Assert.AreEqual(0, StagingEntries(s));
    }

    [TestMethod]
    public async Task Installer_Smaller_Than_Manifest_Rejected()
    {
        await using var s = new Scenario();
        var payload = s.AddInstaller(Architectures.X64);
        s.Publish();
        s.Server.Map("/bwc/win-x64.exe", new Route(Body: payload.Bytes[..(payload.Bytes.Length - 96)]));

        var outcome = await new UpdateChecker(s.Options()).DownloadAsync(s.Request(), CancellationToken.None);

        Expect.Fails(outcome, UpdateFailure.InstallerSizeMismatch);
    }

    [TestMethod]
    [DataRow("1.0")]
    [DataRow("")]
    [DataRow("v0.1.0")]
    public async Task Invalid_Current_Version_Rejected(string current)
    {
        await using var s = new Scenario();

        var outcome = await new UpdateChecker(s.Options()).CheckAsync(s.Request(current: current), CancellationToken.None);

        Expect.Fails(outcome, UpdateFailure.InvalidOptions);
    }

    [TestMethod]
    public async Task Shipped_Key_List_Fails_Closed_Until_A_Release_Key_Is_Embedded()
    {
        await using var s = new Scenario();
        s.AddInstaller(Architectures.X64);
        s.Publish();
        var options = s.Options() with { TrustedPublicKeys = TrustedKeys.Embedded };

        // Read through a method, not the constant, so both shipped states compile without unreachable-code warnings.
        if (TrustedKeys.IsPlaceholderList(TrustedKeys.Embedded))
        {
            Assert.IsFalse(MinisignVerifier.ValidatePublicKeys(TrustedKeys.Embedded).Ok, "the shipped placeholder must not validate as a key");
            var outcome = await new UpdateChecker(options).CheckAsync(s.Request(), CancellationToken.None);
            Expect.Fails(outcome, UpdateFailure.TrustedKeysNotConfigured);
        }
        else
        {
            // A release key is embedded (scripts/embed-update-key.js). Every embedded key must validate, and a
            // manifest signed by another key (this scenario's test key) must still be refused.
            Assert.IsTrue(MinisignVerifier.ValidatePublicKeys(TrustedKeys.Embedded).Ok, "every embedded release key must validate");
            var outcome = await new UpdateChecker(options).CheckAsync(s.Request(), CancellationToken.None);
            Assert.IsFalse(outcome.Ok, "a manifest signed by a key that is not embedded must be refused");
        }
    }

    [TestMethod]
    public async Task Malformed_Key_List_Reports_TrustedKeysInvalid()
    {
        await using var s = new Scenario();
        s.AddInstaller(Architectures.X64);
        s.Publish();
        var options = s.Options() with { TrustedPublicKeys = ["untrusted comment: not a key\nNOT-BASE64!"] };

        var outcome = await new UpdateChecker(options).CheckAsync(s.Request(), CancellationToken.None);

        Expect.Fails(outcome, UpdateFailure.TrustedKeysInvalid);
    }

    private static int StagingEntries(Scenario s) =>
        Directory.Exists(s.StagingRoot) ? Directory.GetFileSystemEntries(s.StagingRoot).Length : 0;

    private static int ClosedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Runs the callback on the calling thread, so the test sees every report in order.</summary>
    private sealed class SynchronousProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

// Pipeline cases from docs/failure-modes/update-verifier.md, run against the loopback server.
[TestClass]
public sealed class UpdateCheckerTests
{
    private const int MaxManifestBytes = 256 * 1024;

    [TestMethod]
    public async Task Check_Ready_Writes_Receipt_And_Installer()
    {
        await using var s = new Scenario();
        var payload = s.AddInstaller(Architectures.X64);
        s.Publish();

        var outcome = await new UpdateChecker(s.Options()).CheckAsync(s.Request(), CancellationToken.None);

        Assert.IsTrue(outcome.Ok, $"{outcome.Failure}: {outcome.Detail}");
        Assert.AreEqual(Scenario.DefaultVersion, outcome.Value!.Receipt.Version);
        Assert.AreEqual(payload.Sha256, outcome.Value.Receipt.Sha256);
        Assert.IsTrue(File.Exists(outcome.Value.ReceiptPath));
        CollectionAssert.AreEqual(payload.Bytes, File.ReadAllBytes(outcome.Value.InstallerPath));
        Assert.AreEqual("installer.exe", Path.GetFileName(outcome.Value.InstallerPath));
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

        var outcome = await new UpdateChecker(s.Options()).CheckAsync(s.Request(), CancellationToken.None);

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
            .CheckAsync(s.Request(), CancellationToken.None);

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
        var outcome = await new UpdateChecker(options).CheckAsync(s.Request(), CancellationToken.None);

        Expect.Fails(outcome, UpdateFailure.Timeout);
    }

    [TestMethod]
    public async Task Http_Error_Rejected()
    {
        await using var s = new Scenario();
        var payload = s.AddInstaller(Architectures.X64);
        s.Publish();
        s.Server.Map("/bwc/win-x64.exe", new Route(Status: 500, Body: payload.Bytes));

        var outcome = await new UpdateChecker(s.Options()).CheckAsync(s.Request(), CancellationToken.None);

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

        var outcome = await new UpdateChecker(s.Options()).CheckAsync(s.Request(), CancellationToken.None);

        Expect.Fails(outcome, UpdateFailure.InstallerHashMismatch);
        Assert.AreEqual(0, StagingEntries(s));
    }

    [TestMethod]
    public async Task Receipt_Only_After_Verification()
    {
        await using var s = new Scenario();
        var payload = s.AddInstaller(Architectures.X64);
        s.Publish();
        var checker = new UpdateChecker(s.Options());

        var ok = await checker.CheckAsync(s.Request(), CancellationToken.None);
        Assert.IsTrue(ok.Ok, ok.Detail);
        Assert.AreEqual(1, Directory.GetFiles(s.StagingRoot, "receipt.json", SearchOption.AllDirectories).Length);

        var tampered = (byte[])payload.Bytes.Clone();
        tampered[7] ^= 0x01;
        s.Server.Map("/bwc/win-x64.exe", new Route(Body: tampered));
        var failed = await checker.CheckAsync(s.Request(), CancellationToken.None);

        Expect.Fails(failed, UpdateFailure.InstallerHashMismatch);
        Assert.AreEqual(1, Directory.GetFiles(s.StagingRoot, "receipt.json", SearchOption.AllDirectories).Length, "the failed attempt wrote no receipt");
    }

    [TestMethod]
    public async Task Stale_Installer_Not_Reused()
    {
        await using var s = new Scenario();
        s.AddInstaller(Architectures.X64);
        s.Publish();
        var checker = new UpdateChecker(s.Options());

        var first = await checker.CheckAsync(s.Request(), CancellationToken.None);
        var second = await checker.CheckAsync(s.Request(), CancellationToken.None);

        Assert.IsTrue(first.Ok && second.Ok);
        Assert.AreNotEqual(Path.GetDirectoryName(first.Value!.InstallerPath), Path.GetDirectoryName(second.Value!.InstallerPath));
    }

    [TestMethod]
    public async Task Cancellation_Removes_Partial_File()
    {
        await using var s = new Scenario();
        var payload = s.AddInstaller(Architectures.X64);
        s.Publish();
        s.Server.Map("/bwc/win-x64.exe", new Route(Body: payload.Bytes, TrickleDelay: TimeSpan.FromMilliseconds(50)));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));

        var outcome = await new UpdateChecker(s.Options()).CheckAsync(s.Request(), cancel.Token);

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

        var outcome = await new UpdateChecker(s.Options()).CheckAsync(s.Request() with { StagingRoot = notAFolder }, CancellationToken.None);

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

        var outcome = await new UpdateChecker(s.Options()).CheckAsync(s.Request(), CancellationToken.None);

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

        var outcome = await new UpdateChecker(s.Options()).CheckAsync(s.Request(), CancellationToken.None);

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

        var outcome = await new UpdateChecker(s.Options()).CheckAsync(s.Request(), CancellationToken.None);

        Expect.Fails(outcome, UpdateFailure.DownloadFailed);
    }

    [TestMethod]
    public async Task Local_File_Name_Is_Fixed()
    {
        await using var s = new Scenario();
        s.AddInstaller(Architectures.X64, path: "/bwc/Setup%20(1).exe");
        s.Publish();

        var outcome = await new UpdateChecker(s.Options()).CheckAsync(s.Request(), CancellationToken.None);

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

        var outcome = await new UpdateChecker(s.Options()).CheckAsync(s.Request(), CancellationToken.None);

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

        var outcome = await new UpdateChecker(s.Options()).CheckAsync(s.Request(), CancellationToken.None);

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
    public async Task Placeholder_Key_Fails_Closed()
    {
        await using var s = new Scenario();
        s.AddInstaller(Architectures.X64);
        s.Publish();
        var options = s.Options() with { TrustedPublicKeys = TrustedKeys.Embedded };

        Assert.IsFalse(MinisignVerifier.ValidatePublicKeys(TrustedKeys.Embedded).Ok, "the shipped placeholder must not validate as a key");
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
}

using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

/// <summary>
/// Full path over a loopback server: a signed manifest, the manifest check, the installer download,
/// the same-version refusal, the helper's verify-then-hand-off, and the elevated helper's copy, verify, and start
/// (both on a fake host). It writes artifacts/update-verifier/result.json.
/// </summary>
[TestClass]
public sealed class UpdateEndToEndTests
{
    [TestMethod]
    public async Task Check_Then_Apply_Writes_Artifact()
    {
        var steps = new List<Step>();
        await using var s = new Scenario(version: "0.2.0", channel: "beta");
        var payload = s.AddInstaller(Architectures.X64);
        s.Publish();
        var checker = new UpdateChecker(s.Options());

        var check = await checker.CheckAsync(s.Request(current: "0.1.0"), CancellationToken.None);
        steps.Add(new Step("check: signed manifest with a trusted comment that binds version, channel and expiry", check.Ok, check.Detail));
        Assert.IsTrue(check.Ok, check.Detail);
        Assert.AreEqual(0, s.Server.Hits("/bwc/win-x64.exe"), "the check does not download the installer");

        var download = await checker.DownloadAsync(s.Request(current: "0.1.0"), CancellationToken.None);
        steps.Add(new Step("download: installer verified against the manifest and its signature, receipt written", download.Ok, download.Detail));
        Assert.IsTrue(download.Ok, download.Detail);

        var host = new FakeHost { HelperDirectory = s.Dir.Sub("install") };
        var installDir = host.HelperDirectory;
        var options = s.Options() with { ProgramFilesRoot = null, ProtectedAcl = ProtectedAcl.ForCurrentUser() };
        var request = new ApplyRequest(
            download.Value!.InstallerPath,
            download.Value.ReceiptPath,
            s.StagingRoot,
            "0.1.0",
            Architectures.X64,
            installDir,
            [],
            TimeSpan.FromSeconds(2),
            []);
        var apply = await new UpdateApplier(options, host).ApplyAsync(request, CancellationToken.None);
        var handedOff = apply.Failure == UpdateFailure.None && apply.ElevatedHelperStarted && !apply.InstallerStarted
            && host.ElevatedRuns.Count == 1 && host.Runs.Count == 0 && host.SeenInstallerSha256 == payload.Sha256;
        steps.Add(new Step("apply: verified, then the elevated helper is started with the staging folder only", handedOff, apply.Detail));
        Assert.IsTrue(handedOff, apply.Detail);

        // The elevated half, as the elevated helper runs it: copy into the protected root, verify the copy, start it.
        host.Elevated = true;
        host.SeenInstallerSha256 = null;
        var elevated = new ElevatedApplier(options, host).Apply(
            new ElevatedApplyRequest(host.ElevatedRuns[0].Folder, s.StagingRoot, installDir, "0.1.0", Architectures.X64));
        var startedFromCopy = elevated.Failure == UpdateFailure.None && elevated.InstallerStarted && host.Runs.Count == 1
            && host.SeenInstallerSha256 == payload.Sha256
            && string.Equals(Path.GetDirectoryName(Path.GetDirectoryName(host.Runs[0].Path)), installDir + ProtectedStore.FolderSuffix, StringComparison.OrdinalIgnoreCase);
        steps.Add(new Step("apply-elevated: the copy in the protected root is verified and started, with the verified bytes", startedFromCopy, elevated.Detail));
        Assert.IsTrue(startedFromCopy, elevated.Detail);

        var again = await checker.CheckAsync(s.Request(current: "0.2.0"), CancellationToken.None);
        steps.Add(new Step("refuse: same version is not offered again", !again.Ok && again.Failure == UpdateFailure.SameVersion, again.Detail));
        Assert.AreEqual(UpdateFailure.SameVersion, again.Failure, again.Detail);

        WriteArtifact(steps);
    }

    private sealed record Step(string Name, bool Pass, string Detail);

    private static void WriteArtifact(List<Step> steps)
    {
        var folder = Path.Combine(RepoPaths.Root(), "artifacts", "update-verifier");
        Directory.CreateDirectory(folder);
        var result = new
        {
            schema = 1,
            scenario = "check-then-apply",
            pass = steps.All(step => step.Pass),
            steps = steps.Select(step => new { step.Name, step.Pass }),
        };
        File.WriteAllText(
            Path.Combine(folder, "result.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}

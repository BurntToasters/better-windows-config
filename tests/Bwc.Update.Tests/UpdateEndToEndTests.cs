using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

/// <summary>
/// Full path over a loopback server: a signed manifest, the manifest check, the installer download,
/// the same-version refusal, and the helper's verify-then-start on a fake host. It writes
/// artifacts/update-verifier/result.json.
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

        var host = new FakeHost();
        var installDir = s.Dir.Sub("install");
        var apply = await new UpdateApplier(s.Options(), host).ApplyAsync(
            new ApplyRequest(
                download.Value!.InstallerPath,
                download.Value.ReceiptPath,
                s.StagingRoot,
                "0.1.0",
                Architectures.X64,
                installDir,
                [],
                TimeSpan.FromSeconds(2),
                []),
            CancellationToken.None);
        var startedVerifiedBytes = host.SeenInstallerSha256 == payload.Sha256;
        steps.Add(new Step("apply: installer started with the verified bytes, helper returns without waiting", apply.Failure == UpdateFailure.None && apply.InstallerStarted && startedVerifiedBytes, apply.Detail));
        Assert.AreEqual(UpdateFailure.None, apply.Failure, apply.Detail);
        Assert.IsTrue(startedVerifiedBytes);

        var again = await checker.CheckAsync(s.Request(current: "0.2.0"), CancellationToken.None);
        steps.Add(new Step("refuse: same version is not offered again", !again.Ok && again.Failure == UpdateFailure.SameVersion, again.Detail));
        Assert.AreEqual(UpdateFailure.SameVersion, again.Failure, again.Detail);

        WriteArtifact(steps);
    }

    private sealed record Step(string Name, bool Pass, string Detail);

    private static void WriteArtifact(List<Step> steps, [CallerFilePath] string? thisFile = null)
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile!)!, "..", ".."));
        var folder = Path.Combine(repoRoot, "artifacts", "update-verifier");
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

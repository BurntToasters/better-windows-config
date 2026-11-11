using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

/// <summary>
/// Stands in for the Windows process host. It never starts a real installer. It records what the
/// applier asked for and what the installer file looked like while the applier held it.
/// </summary>
internal sealed class FakeHost : IUpdateProcessHost
{
    public Func<IReadOnlyList<int>, IReadOnlyList<int>> Live { get; set; } = _ => [];

    public int InstallerExitCode { get; set; }

    /// <summary>How long the fake installer runs. Longer than the timeout means the host reports a timeout.</summary>
    public TimeSpan InstallerDelay { get; set; } = TimeSpan.Zero;

    public bool StartSucceeds { get; set; } = true;

    /// <summary>Runs right before the installer is "launched", while the applier holds the file.</summary>
    public Action<string>? BeforeLaunch { get; set; }

    public List<(string Path, string Arguments)> Runs { get; } = [];

    public List<(string Exe, IReadOnlyList<string> Args, string WorkDir)> Started { get; } = [];

    public string? SeenInstallerSha256 { get; private set; }

    public bool WriteOpenBlocked { get; private set; }

    public bool DeleteBlocked { get; private set; }

    public IReadOnlyList<int> LiveProcesses(IReadOnlyList<int> pids, string installDir) => Live(pids);

    public async Task<int?> RunInstallerAsync(string path, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        Runs.Add((path, arguments));
        BeforeLaunch?.Invoke(path);
        SeenInstallerSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

        try
        {
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
            }

            WriteOpenBlocked = false;
        }
        catch (IOException)
        {
            WriteOpenBlocked = true;
        }

        try
        {
            File.Delete(path);
            DeleteBlocked = false;
        }
        catch (IOException)
        {
            DeleteBlocked = true;
        }

        if (InstallerDelay > timeout)
        {
            await Task.Delay(timeout, ct);
            return null;
        }

        return InstallerExitCode;
    }

    public bool StartDetached(string exePath, IReadOnlyList<string> arguments, string workingDirectory)
    {
        Started.Add((exePath, arguments, workingDirectory));
        return StartSucceeds;
    }
}

internal static class Expect
{
    public static void Accepted(VerificationResult result, string? trustedComment = null)
    {
        Assert.IsTrue(result.Ok, $"expected acceptance, got failure: {result.Failure}");
        if (trustedComment is not null)
        {
            Assert.AreEqual(trustedComment, result.TrustedComment);
        }
    }

    public static void Rejected(VerificationResult result, string? reasonContains = null)
    {
        Assert.IsFalse(result.Ok, "expected rejection, but verification succeeded");
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Failure), "a rejection must carry a reason");
        Assert.IsNull(result.TrustedComment, "a rejected result must not expose a trusted comment");
        if (reasonContains is not null)
        {
            Assert.IsTrue(
                result.Failure.Contains(reasonContains, StringComparison.OrdinalIgnoreCase),
                $"expected reason containing '{reasonContains}', got: {result.Failure}");
        }
    }

    public static void Fails<T>(Outcome<T> outcome, UpdateFailure failure)
    {
        Assert.IsFalse(outcome.Ok, $"expected {failure}, but the step succeeded");
        Assert.AreEqual(failure, outcome.Failure, outcome.Detail);
        Assert.IsFalse(string.IsNullOrWhiteSpace(outcome.Detail), "a failure must carry a reason");
    }
}

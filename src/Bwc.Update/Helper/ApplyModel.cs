namespace Bwc.Update;

/// <summary>
/// What the helper needs to apply a staged update. The helper trusts none of it: the installer and
/// receipt are verified again before anything runs.
/// </summary>
public sealed record ApplyRequest(
    string InstallerPath,
    string ReceiptPath,
    string StagingRoot,
    string CurrentVersion,
    string Architecture,
    string InstallDir,
    IReadOnlyList<int> WaitPids,
    TimeSpan WaitTimeout,
    IReadOnlyList<string> InstallerSwitches);

/// <summary>
/// The apply result. Failure is None only when the installer exited 0. HubLaunched says whether
/// the Hub was started again.
/// </summary>
public sealed record ApplyOutcome(UpdateFailure Failure, string Detail, int? InstallerExitCode, bool HubLaunched);

/// <summary>
/// Process operations the helper needs. The Windows implementation is
/// <see cref="WindowsUpdateProcessHost"/>. Tests use a fake, so no real installer ever runs in them.
/// </summary>
public interface IUpdateProcessHost
{
    /// <summary>
    /// Returns the pids still running, plus tool processes running from <paramref name="installDir"/>.
    /// </summary>
    IReadOnlyList<int> LiveProcesses(IReadOnlyList<int> pids, string installDir);

    /// <summary>
    /// Starts the installer with a raw command line (never re-quoted) and waits for it. Returns the
    /// exit code, or null when <paramref name="timeout"/> passes first. The installer is not stopped.
    /// </summary>
    Task<int?> RunInstallerAsync(string installerPath, string arguments, TimeSpan timeout, CancellationToken ct);

    /// <summary>Starts an executable without waiting. Returns false when the file does not exist.</summary>
    bool StartDetached(string exePath, IReadOnlyList<string> arguments, string workingDirectory);
}

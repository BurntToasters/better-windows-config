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
/// The apply result. Failure is None only when the installer was started. InstallerStarted says the
/// installer is running and the helper is done (the installer relaunches the Hub). HubLaunched says the
/// helper started the Hub again with the failure code, which only happens for a refusal.
/// </summary>
public sealed record ApplyOutcome(UpdateFailure Failure, string Detail, bool InstallerStarted, bool HubLaunched);

/// <summary>
/// Process operations the helper needs. The Windows implementation is
/// <see cref="WindowsUpdateProcessHost"/>. Tests use a fake, so no real installer ever runs in them.
/// </summary>
public interface IUpdateProcessHost
{
    /// <summary>
    /// Returns the pids still running, plus BWC processes running from <paramref name="installDir"/>.
    /// A live process whose image cannot be read counts as running.
    /// </summary>
    IReadOnlyList<int> LiveProcesses(IReadOnlyList<int> pids, string installDir);

    /// <summary>True when this process runs elevated or at high integrity.</summary>
    bool IsElevated();

    /// <summary>
    /// Starts the installer with a raw command line (never re-quoted) and returns without waiting for it.
    /// Returns false when it did not start. The installer is never killed by the helper.
    /// </summary>
    bool StartInstaller(string installerPath, string arguments);

    /// <summary>Starts an executable without waiting. Returns false when the file does not exist.</summary>
    bool StartDetached(string exePath, IReadOnlyList<string> arguments, string workingDirectory);
}

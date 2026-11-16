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
/// What the elevated helper (<c>--apply-elevated</c>) takes. Only <see cref="StagingFolder"/> comes from the command
/// line. The rest is the helper's own: its folder is the install dir, its assembly version is the current version, and
/// the staging root is the per-user temp folder of the elevated process. <see cref="E2EProtectedRoot"/> is set only by
/// the E2E flavor's environment seam (<c>BWC_E2E_UPDATE_PROTECTED_ROOT</c>), never by the ship command line.
/// </summary>
public sealed record ElevatedApplyRequest(
    string StagingFolder,
    string StagingRoot,
    string OwnDirectory,
    string OwnVersion,
    string Architecture,
    string? E2EProtectedRoot = null);

/// <summary>
/// The apply result. Failure is None only when the install is under way. InstallerStarted says the installer is
/// running (the elevated helper's result). ElevatedHelperStarted says the medium helper started the elevated helper
/// (the medium helper's result). In both cases this process is done and the installer relaunches the Hub.
/// HubLaunched says the helper started the Hub again with the failure code, which only happens for a refusal.
/// </summary>
public sealed record ApplyOutcome(
    UpdateFailure Failure,
    string Detail,
    bool InstallerStarted,
    bool HubLaunched,
    bool ElevatedHelperStarted = false);

/// <summary>The answer of the elevated helper start. Any other failure is thrown by the host.</summary>
public enum ElevatedStart
{
    Started,

    /// <summary>The user declined the consent prompt (ERROR_CANCELLED, 1223).</summary>
    Declined,
}

/// <summary>The answer of the start at the shell's integrity level.</summary>
public enum ShellStart
{
    Started,

    /// <summary>The executable does not exist.</summary>
    FileMissing,

    /// <summary>This process is elevated and the desktop shell could not be used to start the program unelevated.</summary>
    CannotDeElevate,
}

/// <summary>
/// Process operations the helper needs. The Windows implementation is
/// <see cref="WindowsUpdateProcessHost"/>. Tests use a fake, so no real installer ever runs in them.
/// </summary>
public interface IUpdateProcessHost
{
    /// <summary>The folder of the running <c>BwcUpdate.exe</c>. It is the install dir, never an argument.</summary>
    string HelperDirectory { get; }

    /// <summary>
    /// Returns the pids still running, plus BWC processes running from <paramref name="installDir"/>.
    /// A live process whose image cannot be read counts as running.
    /// </summary>
    IReadOnlyList<int> LiveProcesses(IReadOnlyList<int> pids, string installDir);

    /// <summary>True when this process runs elevated or at high integrity.</summary>
    bool IsElevated();

    /// <summary>
    /// Starts <c>&lt;helperExe&gt; --apply-elevated &lt;stagingFolder&gt;</c> and returns without waiting. The Windows
    /// host asks the shell for elevation (the runas verb); the E2E flavor starts it with a plain start and never prompts.
    /// Returns <see cref="ElevatedStart.Declined"/> when the user declines. Throws for any other failure.
    /// </summary>
    ElevatedStart StartElevatedHelper(string helperExe, string stagingFolder);

    /// <summary>
    /// Starts the installer with a raw command line (never re-quoted) and returns without waiting for it.
    /// Returns false when it did not start. The installer is never killed by the helper. Only the elevated helper
    /// calls this, and it is already elevated, so there is no verb. The installer's TEMP and TMP are
    /// <paramref name="tempFolder"/> (a protected folder), and its environment has no .NET runtime variables
    /// (<see cref="ChildEnvironment.ForInstaller"/>).
    /// </summary>
    bool StartInstaller(string installerPath, string arguments, string tempFolder);

    /// <summary>Starts an executable without waiting. Returns false when the file does not exist.</summary>
    bool StartDetached(string exePath, IReadOnlyList<string> arguments, string workingDirectory);

    /// <summary>
    /// Starts an executable at the integrity level of the desktop shell, without waiting. A process that is not elevated
    /// starts it normally. An elevated process uses the desktop shell (IShellDispatch2 through the shell window), and
    /// returns <see cref="ShellStart.CannotDeElevate"/> without starting anything when that is not possible.
    /// </summary>
    ShellStart StartAtShellIntegrity(string exePath, IReadOnlyList<string> arguments, string workingDirectory);
}

using System.ComponentModel;
using System.Diagnostics;
using Bwc.E2E.Harness;
using Microsoft.Win32;

namespace Bwc.E2E.Installer;

/// <summary>
/// One silent install with its own run id. The per-user sandbox (<see cref="Create"/>) owns a temp install folder, a temp
/// shortcut folder, and the HKCU\Software\BWC-E2E\run id key. The machine mode (<see cref="CreateMachine"/>, only for the
/// Elevated tests) owns %ProgramFiles%\BWC-E2E\run id (and its .new, .old, and .upd siblings), the all-users Start menu
/// folder BWC-E2E-run id, and HKLM\Software\BWC-E2E\run id in the 64-bit view, and nothing else. Both own the processes
/// they started. Dispose removes what the run owns. A process is stopped only when it is one of those started here and its
/// image is under this run's install folder (by PID).
/// </summary>
internal sealed class InstallerRun : IDisposable
{
    public const string HubExeName = "BetterWindowsConfig.exe";
    public const string RegeditExeName = "BwcRegedit.exe";
    public const string UpdaterExeName = "BwcUpdate.exe";
    public const string UninstallerName = "Uninstall.exe";
    private const string ShortcutsVariable = "BWC_E2E_INSTALLER_SHORTCUTS";
    private const string RegistryParent = @"Software\BWC-E2E";
    private const string UninstallSubkey = @"Uninstall\BetterWindowsConfig";
    private const string MachineParentName = "BWC-E2E";
    private const string MachineStartMenuPrefix = "BWC-E2E-";
    private static readonly string[] MachineSiblingSuffixes = ["", ".new", ".old", ".upd"];
    private static readonly string[] ProcessNames = ["BetterWindowsConfig", "BwcRegedit", "BwcUpdate"];
    private static readonly TimeSpan CleanupWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MachineUninstallTimeout = TimeSpan.FromSeconds(120);
    private readonly List<Process> _started = [];
    private Action<string, bool, string> _record = (id, pass, notes) => InstallerChecks.Record(id, pass, notes);
    private bool _disposed;

    private InstallerRun(E2EConfig config, string runId, string root, bool machine, MachineWorkRoot? work)
    {
        Config = config;
        RunId = runId;
        Root = root;
        Machine = machine;
        Work = work;
    }

    /// <summary>
    /// The admin-only work folder that holds this run's folders, or null for the unelevated per-user sandbox in
    /// %TEMP%.
    /// </summary>
    private MachineWorkRoot? Work { get; }

    public E2EConfig Config { get; }

    public string RunId { get; }

    public string Root { get; }

    /// <summary>True for the per-machine sandbox (Program Files, HKLM, all-users Start menu). Elevated tests only.</summary>
    public bool Machine { get; }

    /// <summary>Sends the cleanup checks of this run to another list (the elevated tests keep their own).</summary>
    public InstallerRun UseRecorder(Action<string, bool, string> record)
    {
        _record = record;
        return this;
    }

    /// <summary>
    /// The 64-bit Program Files folder from the shell's known folder (never from ProgramW6432, which the caller controls). It
    /// is where the setup puts the machine run folder.
    /// </summary>
    public static string ProgramFiles64 => MachineNative.ProgramFilesFolder();

    /// <summary>%ProgramFiles%\BWC-E2E: the parent of every machine run folder.</summary>
    public static string MachineParentDir => Path.Combine(ProgramFiles64, MachineParentName);

    public string InstallDir => Machine ? Path.Combine(MachineParentDir, RunId) : Path.Combine(Root, "app");

    /// <summary>Shortcut folder: a temp folder, or in machine mode the all-users Start menu folder BWC-E2E-run id.</summary>
    public string ShortcutDir =>
        Machine
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), MachineStartMenuPrefix + RunId)
            : Path.Combine(Root, "shortcuts");

    /// <summary>The protected folder of the elevated helper, a sibling of the install folder.</summary>
    public string ProtectedDir => InstallDir + ".upd";

    public string HubExe => Path.Combine(InstallDir, HubExeName);

    public string RegeditExe => Path.Combine(InstallDir, RegeditExeName);

    public string UpdaterExe => Path.Combine(InstallDir, UpdaterExeName);

    public string UninstallerExe => Path.Combine(InstallDir, UninstallerName);

    private string RunKey => RegistryParent + @"\" + RunId;

    /// <summary>
    /// Creates the folders for one per-user run. Without <paramref name="work"/> they are in %TEMP% (the unelevated installer
    /// E2E), and the path must have no space because /SHORTCUTS= is passed unquoted. With it (the legacy sandbox of an Elevated
    /// test) they are inside the admin-only work folder, and the shortcut folder reaches the setup through the environment.
    /// </summary>
    public static InstallerRun Create(E2EConfig config, string caseId, MachineWorkRoot? work = null)
    {
        string runId = "inst-" + Guid.NewGuid().ToString("N")[..10] + "-" + caseId;
        string root = work is null ? Path.Combine(Path.GetTempPath(), "bwc-installer-e2e-" + runId) : Path.Combine(work.Path, runId);
        if (work is null && root.Contains(' ', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The temp folder path has a space, which the unquoted /SHORTCUTS= cannot carry: " + root);
        }

        var run = new InstallerRun(config, runId, root, machine: false, work);
        Directory.CreateDirectory(run.ShortcutDir);
        return run;
    }

    /// <summary>
    /// Starts a machine run: only the working folder inside the admin-only work folder is created. The setup creates the
    /// Program Files folder, the HKLM key, and the Start menu folder. The run id is new every time, so nothing from an earlier
    /// run is ever reused.
    /// </summary>
    public static InstallerRun CreateMachine(E2EConfig config, string caseId)
    {
        MachineWorkRoot work = MachineWorkRoot.Required;
        string runId = "mach-" + Guid.NewGuid().ToString("N")[..10] + "-" + caseId;
        if (runId.Length > 64 || !runId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            throw new InvalidOperationException("The machine run id is not valid for the E2E setup: " + runId);
        }

        string root = Path.Combine(work.Path, runId);
        Directory.CreateDirectory(root);
        return new InstallerRun(config, runId, root, machine: true, work).UseRecorder((id, pass, notes) => MachineChecks.Record(id, pass, notes));
    }

    /// <summary>
    /// Runs the E2E setup silently into this run's folders. Returns the exit code, or -1 after a timeout. The machine mode
    /// passes /MACHINE and no folder: the setup picks the Program Files folder itself. <paramref name="legacyHint"/> is the
    /// folder of an earlier per-user install, passed as /D= (last, unquoted) the way an old helper would; the setup only
    /// closes that install's apps and never uses it as the target.
    /// </summary>
    public int RunSetup(string setupPath, TimeSpan timeout, string? legacyHint = null)
    {
        // A shortcut folder with a space cannot ride on the unquoted /SHORTCUTS=, so the setup reads it from the environment.
        bool shortcutsInEnvironment = !Machine && ShortcutDir.Contains(' ', StringComparison.Ordinal);
        string arguments = Machine
            ? "/S /MACHINE /RUNID=" + RunId + (legacyHint is null ? string.Empty : " /D=" + legacyHint)
            : "/S /RUNID=" + RunId + (shortcutsInEnvironment ? string.Empty : " /SHORTCUTS=" + ShortcutDir) + " /D=" + InstallDir;
        using Process process = Start(
            setupPath,
            arguments,
            shortcutsInEnvironment ? new Dictionary<string, string> { [ShortcutsVariable] = ShortcutDir } : null);
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            _ = process.WaitForExit(CleanupWait);
            return -1;
        }

        return process.ExitCode;
    }

    /// <summary>
    /// Runs the uninstaller silently. NSIS uninstallers may copy themselves out and return early, so the test waits
    /// for the install folder to go, not for the process.
    /// </summary>
    public bool Uninstall(TimeSpan timeout)
    {
        using (Process process = Start(UninstallerExe, "/S /RUNID=" + RunId, environment: null))
        {
            _ = process.WaitForExit(timeout);
        }

        return WaitUntil(() => !Directory.Exists(InstallDir), timeout);
    }

    /// <summary>
    /// Runs the registered QuietUninstallString of the machine run, with /MACHINE /RUNID= added before _?= (the E2E setup
    /// writes neither into the string). The uninstaller runs in place, so the process is waited for. Returns the exit code
    /// (-1 after a timeout). <paramref name="commandLine"/> is what was started.
    /// </summary>
    public int UninstallMachine(TimeSpan timeout, out string commandLine)
    {
        string text = ReadUninstallValue("QuietUninstallString") ?? throw new InvalidOperationException("The run has no QuietUninstallString.");
        int close = text.IndexOf('"', 1);
        int inPlace = text.IndexOf("_?=", StringComparison.Ordinal);
        if (!text.StartsWith('"') || close < 0 || inPlace < close)
        {
            throw new InvalidOperationException("Unexpected QuietUninstallString: " + text);
        }

        string exe = text[1..close];
        string arguments = text[(close + 1)..inPlace].Trim() + " /MACHINE /RUNID=" + RunId + " " + text[inPlace..];
        commandLine = "\"" + exe + "\" " + arguments;
        if (!IsOwnMachinePath(exe))
        {
            throw new InvalidOperationException("The uninstaller is not in this run's folder: " + exe);
        }

        using Process process = Start(exe, arguments, environment: null);
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            _ = process.WaitForExit(CleanupWait);
            return -1;
        }

        return process.ExitCode;
    }

    /// <summary>
    /// Runs this run's installed uninstaller in place with <c>_?=</c> naming another folder. The uninstaller must refuse
    /// (installer.md M47); the exit code is returned (-1 after a timeout).
    /// </summary>
    public int UninstallNamingFolder(string folder, TimeSpan timeout, out string commandLine)
    {
        string arguments = "/S /MACHINE /RUNID=" + RunId + " _?=" + folder;
        commandLine = "\"" + UninstallerExe + "\" " + arguments;
        using Process process = Start(UninstallerExe, arguments, environment: null);
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            _ = process.WaitForExit(CleanupWait);
            return -1;
        }

        return process.ExitCode;
    }

    /// <summary>Starts an app from this run and keeps it for cleanup. The environment adds to the caller's.</summary>
    public Process StartTracked(string exePath, IReadOnlyDictionary<string, string> environment)
    {
        Process process = Start(exePath, string.Empty, environment);
        _started.Add(process);
        return process;
    }

    /// <summary>Keeps a process the installer started, so cleanup can stop it if the test ends early.</summary>
    public void Track(Process process) => _started.Add(process);

    /// <summary>The DisplayVersion (or any value) under this run's uninstall key, or null when absent.</summary>
    public string? ReadUninstallValue(string name)
    {
        using RegistryKey? key = OpenUninstallKey();
        return key?.GetValue(name) as string;
    }

    public bool UninstallKeyExists()
    {
        using RegistryKey? key = OpenUninstallKey();
        return key is not null;
    }

    /// <summary>True when the run's key exists under HKLM\Software\WOW6432Node. A broken setup would write there.</summary>
    public bool Wow6432KeyExists()
    {
        using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? key = hklm.OpenSubKey(@"Software\WOW6432Node\" + MachineParentName + @"\" + RunId);
        return key is not null;
    }

    /// <summary>
    /// Writes a stand-in for an old per-user install to this run's sandbox uninstall key (never the real key): the
    /// InstallLocation value only when one is given, and the ShortcutDir value of this run. For the migration E2E.
    /// </summary>
    public void SeedUninstallKey(string? installLocation)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey + @"\" + UninstallSubkey, writable: true);
        if (installLocation is not null)
        {
            key.SetValue("InstallLocation", installLocation, RegistryValueKind.String);
        }

        key.SetValue("ShortcutDir", ShortcutDir, RegistryValueKind.String);
        key.SetValue("DisplayName", "Better Windows Config (E2E stand-in)", RegistryValueKind.String);
    }

    /// <summary>The number of .lnk files the installer wrote to the shortcut folder.</summary>
    public int ShortcutCount() =>
        Directory.Exists(ShortcutDir) ? Directory.GetFiles(ShortcutDir, "*.lnk", SearchOption.AllDirectories).Length : 0;

    /// <summary>The file version of an exe, without the +build metadata, or null when the file is missing.</summary>
    public static string? VersionOf(string exePath)
    {
        if (!File.Exists(exePath))
        {
            return null;
        }

        string? text = FileVersionInfo.GetVersionInfo(exePath).ProductVersion;
        if (text is null)
        {
            return null;
        }

        int plus = text.IndexOf('+', StringComparison.Ordinal);
        return (plus < 0 ? text : text[..plus]).Trim();
    }

    /// <summary>True when the process's image is under this run's install folder.</summary>
    public bool IsUnderInstallDir(Process process)
    {
        try
        {
            string? image = process.MainModule?.FileName;
            string folder = Path.GetFullPath(InstallDir) + Path.DirectorySeparatorChar;
            return image is not null && Path.GetFullPath(image).StartsWith(folder, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopTrackedProcesses();
        if (Machine)
        {
            CleanupMachine();
            return;
        }

        bool folderGone = DeleteRoot();
        _record("cleanup.folder." + RunId, folderGone, folderGone ? "Run folders removed." : "A run folder is still there: " + Root);

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(RunKey, throwOnMissingSubKey: false);
            _record("cleanup.registry." + RunId, true, "Removed " + RunKey + ".");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _record("cleanup.registry." + RunId, false, ex.Message);
        }
    }

    private RegistryKey? OpenUninstallKey()
    {
        if (!Machine)
        {
            return Registry.CurrentUser.OpenSubKey(RunKey + @"\" + UninstallSubkey);
        }

        using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        return hklm.OpenSubKey(RunKey + @"\" + UninstallSubkey);
    }

    /// <summary>
    /// True for the machine run's own names and nothing else: the install folder, its .new, .old, and .upd siblings, the run's
    /// Start menu folder, or a path inside the install folder or its siblings. Every machine delete is checked against this.
    /// </summary>
    private bool IsOwnMachinePath(string path)
    {
        if (!Machine)
        {
            return false;
        }

        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        foreach (string suffix in MachineSiblingSuffixes)
        {
            string own = Path.TrimEndingDirectorySeparator(Path.GetFullPath(InstallDir + suffix));
            if (string.Equals(full, own, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(own + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetFullPath(ShortcutDir)), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The machine cleanup (installer-e2e.md 52 to 54): stop what this run started, run the registered uninstall once, then
    /// remove whatever is left of the run's own names. A link is removed as a link. The shared parents go only when empty.
    /// </summary>
    private void CleanupMachine()
    {
        var problems = new List<string>();
        StopStrayProcesses();
        string uninstall = "No uninstall was needed.";
        try
        {
            if (File.Exists(UninstallerExe) && UninstallKeyExists())
            {
                int code = UninstallMachine(MachineUninstallTimeout, out string command);
                uninstall = "Ran " + command + " and it exited " + code + ".";
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            uninstall = "The uninstall before the force removal did not run: " + ex.Message;
        }

        foreach (string suffix in MachineSiblingSuffixes)
        {
            ForceDeleteOwnFolder(InstallDir + suffix, problems);
        }

        ForceDeleteOwnFolder(ShortcutDir, problems);
        RemoveParentWhenEmpty(MachineParentDir, problems);
        ForceDeleteOwnKey(problems);
        if (!DeleteRoot())
        {
            problems.Add("run folder " + Root);
        }

        string notes = problems.Count == 0
            ? uninstall + " Removed " + InstallDir + " (and .new, .old, .upd), " + ShortcutDir + ", and HKLM\\" + RunKey + "."
            : uninstall + " Could not remove: " + string.Join("; ", problems) + ". Remove them by hand.";
        _record("cleanup.machine." + RunId, problems.Count == 0, notes);
    }

    private void ForceDeleteOwnFolder(string folder, List<string> problems)
    {
        if (!IsOwnMachinePath(folder))
        {
            problems.Add("refused to remove a path that is not this run's: " + folder);
            return;
        }

        for (int attempt = 0; attempt < 5 && Directory.Exists(folder); attempt++)
        {
            try
            {
                // A link (junction or symbolic link) is removed as a link: Delete without recursion never enters it.
                if (new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    Directory.Delete(folder);
                }
                else
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }

        if (Directory.Exists(folder))
        {
            problems.Add(folder);
        }
    }

    private static void RemoveParentWhenEmpty(string folder, List<string> problems)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add(folder + " (" + ex.Message + ")");
        }
    }

    /// <summary>Removes HKLM\Software\BWC-E2E\run id (64-bit view), then the parent key when empty.</summary>
    private void ForceDeleteOwnKey(List<string> problems)
    {
        try
        {
            using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            hklm.DeleteSubKeyTree(RunKey, throwOnMissingSubKey: false);
            bool parentEmpty;
            using (RegistryKey? parent = hklm.OpenSubKey(RegistryParent))
            {
                parentEmpty = parent is not null && parent.SubKeyCount == 0 && parent.ValueCount == 0;
            }

            if (parentEmpty)
            {
                hklm.DeleteSubKey(RegistryParent, throwOnMissingSubKey: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            problems.Add("HKLM\\" + RunKey + " (" + ex.Message + ")");
        }
    }

    /// <summary>
    /// Stops a Hub, Registry Editor, or updater whose image is under this run's own install folder and that is still running,
    /// for example one the setup started. The folder name carries the run id, so no other program can match.
    /// </summary>
    private void StopStrayProcesses()
    {
        foreach (string name in ProcessNames)
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                try
                {
                    if (!process.HasExited && IsUnderInstallDir(process))
                    {
                        process.Kill(entireProcessTree: true);
                        _ = process.WaitForExit(CleanupWait);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // The process exited while it was being stopped.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }

    private Process Start(string file, string arguments, IReadOnlyDictionary<string, string>? environment)
    {
        var info = new ProcessStartInfo(file)
        {
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Root,
        };
        if (environment is not null)
        {
            foreach (KeyValuePair<string, string> pair in environment)
            {
                info.Environment[pair.Key] = pair.Value;
            }
        }

        return Process.Start(info) ?? throw new InvalidOperationException("Process.Start returned no process for " + file);
    }

    private void StopTrackedProcesses()
    {
        foreach (Process process in _started)
        {
            try
            {
                if (!process.HasExited && IsUnderInstallDir(process))
                {
                    process.Kill(entireProcessTree: true);
                    _ = process.WaitForExit(CleanupWait);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // The process exited while it was being stopped.
            }
            finally
            {
                process.Dispose();
            }
        }

        _started.Clear();
    }

    /// <summary>
    /// Removes the run's working folder. An Elevated run deletes only inside its admin-only work folder (installer-e2e.md 86),
    /// by the folder's full name and with no link on the way. The unelevated sandbox in %TEMP% deletes its own folder.
    /// </summary>
    private bool DeleteRoot()
    {
        if (Work is not null && !Work.Owns(Root))
        {
            return !Directory.Exists(Root);
        }

        return DeleteFolder(Root);
    }

    private static bool DeleteFolder(string folder)
    {
        for (int attempt = 0; attempt < 5 && Directory.Exists(folder); attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }

        return !Directory.Exists(folder);
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(200);
        }

        return condition();
    }
}

using System.ComponentModel;
using System.Diagnostics;
using Bwc.E2E.Harness;
using Microsoft.Win32;

namespace Bwc.E2E.Installer;

/// <summary>
/// One silent install under a temp folder with its own run id. It owns the install folder, the shortcut folder, the
/// HKCU\Software\BWC-E2E\run id registry key, and the processes it started. Dispose removes all three. A process is
/// stopped only when it is one of those started here and its image is under this run's install folder (by PID).
/// </summary>
internal sealed class InstallerRun : IDisposable
{
    public const string HubExeName = "BetterWindowsConfig.exe";
    public const string RegeditExeName = "BwcRegedit.exe";
    public const string UninstallerName = "Uninstall.exe";
    private const string RegistryParent = @"Software\BWC-E2E";
    private const string UninstallSubkey = @"Uninstall\BetterWindowsConfig";
    private static readonly TimeSpan CleanupWait = TimeSpan.FromSeconds(5);
    private readonly List<Process> _started = [];
    private bool _disposed;

    private InstallerRun(E2EConfig config, string runId, string root)
    {
        Config = config;
        RunId = runId;
        Root = root;
    }

    public E2EConfig Config { get; }

    public string RunId { get; }

    public string Root { get; }

    public string InstallDir => Path.Combine(Root, "app");

    public string ShortcutDir => Path.Combine(Root, "shortcuts");

    public string HubExe => Path.Combine(InstallDir, HubExeName);

    public string RegeditExe => Path.Combine(InstallDir, RegeditExeName);

    public string UninstallerExe => Path.Combine(InstallDir, UninstallerName);

    private string RunKey => RegistryParent + @"\" + RunId;

    /// <summary>Creates the temp folders for one run. The path must have no space, because /D= is passed unquoted.</summary>
    public static InstallerRun Create(E2EConfig config, string caseId)
    {
        string runId = "inst-" + Guid.NewGuid().ToString("N")[..10] + "-" + caseId;
        string root = Path.Combine(Path.GetTempPath(), "bwc-installer-e2e-" + runId);
        if (root.Contains(' ', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The temp folder path has a space, which the unquoted /D= cannot carry: " + root);
        }

        var run = new InstallerRun(config, runId, root);
        Directory.CreateDirectory(run.ShortcutDir);
        return run;
    }

    /// <summary>Runs the E2E setup silently into this run's folders. Returns the exit code, or -1 after a timeout.</summary>
    public int RunSetup(string setupPath, TimeSpan timeout)
    {
        string arguments = "/S /RUNID=" + RunId + " /SHORTCUTS=" + ShortcutDir + " /D=" + InstallDir;
        using Process process = Start(setupPath, arguments, environment: null);
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
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey + @"\" + UninstallSubkey);
        return key?.GetValue(name) as string;
    }

    public bool UninstallKeyExists()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey + @"\" + UninstallSubkey);
        return key is not null;
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

        bool folderGone = DeleteFolder(Root);
        InstallerChecks.Record("cleanup.folder." + RunId, folderGone, folderGone ? "Temp folders removed." : "A temp folder is still there: " + Root);

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(RunKey, throwOnMissingSubKey: false);
            InstallerChecks.Record("cleanup.registry." + RunId, true, "Removed " + RunKey + ".");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            InstallerChecks.Record("cleanup.registry." + RunId, false, ex.Message);
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

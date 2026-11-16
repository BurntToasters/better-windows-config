using System.ComponentModel;
using System.Diagnostics;
using Bwc.Core;

namespace Bwc.Update;

/// <summary>
/// The real process operations. Uncertainty counts as "running": a process whose image cannot be
/// read is treated as live, so an install never runs over files that might be in use. The running
/// rule is <see cref="RunningImageRule"/>, shared with the Hub and the installer's commands.
/// </summary>
public sealed class WindowsUpdateProcessHost : IUpdateProcessHost
{
#if !BWC_E2E
    /// <summary>ERROR_CANCELLED: the user declined the consent prompt.</summary>
    private const int ErrorCancelled = 1223;

#endif
    public string HelperDirectory =>
        Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory);

    public IReadOnlyList<int> LiveProcesses(IReadOnlyList<int> pids, string installDir)
    {
        ArgumentNullException.ThrowIfNull(pids);
        var live = new List<int>();
        foreach (var pid in pids.Distinct())
        {
            if (RunningImageRule.ReadImage(pid, out _) != ImageStatus.Exited)
            {
                live.Add(pid);
            }
        }

        // Only this session's processes count (a process of another user or session is not ours to wait for).
        var sessionId = ProcessScan.CurrentSessionId();
        foreach (var running in ProcessScan.RunningFrom(installDir, new HashSet<int>(), new SystemProcessSource(), sessionId))
        {
            live.Add(running.Pid);
        }

        return live;
    }

    public bool IsElevated() => ProcessIdentity.IsElevatedOrHighIntegrity();

    public ElevatedStart StartElevatedHelper(string helperExe, string stagingFolder)
    {
        // The working directory is the system folder, so nothing in a user-writable folder is ever loaded.
#if BWC_E2E
        // The E2E flavor never prompts and never uses a verb: a plain start. The elevated helper runs unelevated there and
        // is allowed to only because the E2E seam names a protected root inside the run's temp folder.
        var psi = new ProcessStartInfo(helperExe)
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.SystemDirectory,
        };
        psi.ArgumentList.Add("--apply-elevated");
        psi.ArgumentList.Add(stagingFolder);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("the elevated helper did not start");
        return ElevatedStart.Started;
#else
        // The runas verb makes Windows show its consent prompt. This process never elevates itself.
        var psi = new ProcessStartInfo(helperExe)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Environment.SystemDirectory,
        };
        psi.ArgumentList.Add("--apply-elevated");
        psi.ArgumentList.Add(stagingFolder);
        try
        {
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("the elevated helper did not start");
            return ElevatedStart.Started;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return ElevatedStart.Declined;
        }
#endif
    }

    public bool StartInstaller(string installerPath, string arguments)
    {
        // Only the elevated helper calls this, so the installer inherits its elevation and there is no prompt and no verb.
        // The command line is raw (/D= is last and unquoted). The working directory is the system folder, not the
        // protected folder, so nothing next to the installer is loaded as a DLL.
        var psi = new ProcessStartInfo(installerPath, arguments)
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.SystemDirectory,
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("the installer did not start");
        return true;
    }

    public bool StartDetached(string exePath, IReadOnlyList<string> arguments, string workingDirectory)
    {
        if (!File.Exists(exePath))
        {
            return false;
        }

        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var started = Process.Start(psi);
        return started is not null;
    }

    public ShellStart StartAtShellIntegrity(string exePath, IReadOnlyList<string> arguments, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!File.Exists(exePath))
        {
            return ShellStart.FileMissing;
        }

        // Not elevated: a normal start already has the shell's integrity level.
        if (!IsElevated())
        {
            return StartDetached(exePath, arguments, workingDirectory) ? ShellStart.Started : ShellStart.FileMissing;
        }

        // Elevated: never fall back to a plain start, which would give an elevated Hub.
        return ShellLaunch.TryStartUnelevated(exePath, arguments, workingDirectory) ? ShellStart.Started : ShellStart.CannotDeElevate;
    }
}

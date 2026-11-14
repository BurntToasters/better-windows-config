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

    public bool StartInstaller(string installerPath, string arguments)
    {
        // UseShellExecute lets Windows show its own elevation prompt when the installer's manifest asks for
        // one. This process never requests elevation itself. The installer is not waited for. The working
        // directory is the system folder, not the staging folder, so a planted DLL there is never loaded.
        var psi = new ProcessStartInfo(installerPath, arguments)
        {
            UseShellExecute = true,
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
}

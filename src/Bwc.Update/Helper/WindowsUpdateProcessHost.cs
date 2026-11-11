using System.ComponentModel;
using System.Diagnostics;
using Bwc.Core;

namespace Bwc.Update;

/// <summary>
/// The real process operations. Uncertainty counts as "running": a process whose image cannot be
/// read is treated as live, so an install never runs over files that might be in use.
/// </summary>
public sealed class WindowsUpdateProcessHost : IUpdateProcessHost
{
    private const string HubProcessName = "BetterWindowsConfig";

    private static readonly string[] ToolProcessNames =
        [HubProcessName, .. ToolCatalog.Tools.Select(tool => Path.GetFileNameWithoutExtension(tool.ExeName))];

    public IReadOnlyList<int> LiveProcesses(IReadOnlyList<int> pids, string installDir)
    {
        ArgumentNullException.ThrowIfNull(pids);
        var live = new List<int>();
        foreach (var pid in pids.Distinct())
        {
            if (IsAlive(pid))
            {
                live.Add(pid);
            }
        }

        foreach (var name in ToolProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (IsRunningFrom(process, installDir))
                    {
                        live.Add(process.Id);
                    }
                }
            }
        }

        return live;
    }

    public async Task<int?> RunInstallerAsync(string installerPath, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        // UseShellExecute lets Windows show its own elevation prompt when the installer's manifest asks for
        // one. This process never requests elevation itself.
        var psi = new ProcessStartInfo(installerPath, arguments)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath),
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("the installer did not start");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
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

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return true;
        }
    }

    private static bool IsRunningFrom(Process process, string installDir)
    {
        try
        {
            if (process.HasExited)
            {
                return false;
            }

            var image = process.MainModule?.FileName;
            return image is null || IsUnder(image, installDir);
        }
        catch (Win32Exception)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsUnder(string path, string folder)
    {
        var child = Path.GetFullPath(path);
        var parent = Path.GetFullPath(folder);
        if (!parent.EndsWith(Path.DirectorySeparatorChar))
        {
            parent += Path.DirectorySeparatorChar;
        }

        return child.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }
}

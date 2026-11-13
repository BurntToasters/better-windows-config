using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Bwc.Core;

namespace Bwc.Update;

/// <summary>
/// The two process commands the installer calls (docs/contracts/update-cli.md): close the BWC processes
/// that run from an install folder, and wait for one process to exit. Both poll; neither ever kills a process.
/// </summary>
public static class ProcessCommands
{
    /// <summary>The executables a BWC install runs: the Hub and every tool in the catalog.</summary>
    public static IReadOnlyList<string> ProcessNames { get; } =
        ["BetterWindowsConfig", .. ToolCatalog.Tools.Select(tool => Path.GetFileNameWithoutExtension(tool.ExeName))];

    private const int PollMilliseconds = 250;
    private const uint WmClose = 0x0010;

    /// <summary>
    /// Sends WM_CLOSE to the top-level windows of every BWC process that runs from <paramref name="installDir"/>,
    /// except <paramref name="excluded"/> and this process, and waits up to <paramref name="timeoutSeconds"/>
    /// for none to remain. Returns 0 when none remain, or 3 and prints each remaining name and pid.
    /// </summary>
    public static int CloseRunning(string installDir, IReadOnlyList<int> excluded, int timeoutSeconds, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(installDir);
        ArgumentNullException.ThrowIfNull(excluded);
        ArgumentNullException.ThrowIfNull(output);

        var skip = new HashSet<int>(excluded) { Environment.ProcessId };
        var asked = new HashSet<int>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var running = RunningFrom(installDir, skip);
            if (running.Count == 0)
            {
                return 0;
            }

            foreach (var (pid, _) in running)
            {
                if (asked.Add(pid))
                {
                    PostCloseToWindows(pid);
                }
            }

            if (clock.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
            {
                foreach (var (pid, name) in running)
                {
                    output.WriteLine(name + " pid=" + pid.ToString(CultureInfo.InvariantCulture));
                }

                return 3;
            }

            Thread.Sleep(PollMilliseconds);
        }
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutSeconds"/> for <paramref name="pid"/> to exit. Returns 0 when it
    /// has exited (or never existed), and 3 at the timeout.
    /// </summary>
    public static int WaitForPid(int pid, int timeoutSeconds, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (RunningImageRule.ReadImage(pid, out _) == ImageStatus.Exited)
            {
                return 0;
            }

            if (clock.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
            {
                output.WriteLine("pid=" + pid.ToString(CultureInfo.InvariantCulture) + " is still running");
                return 3;
            }

            Thread.Sleep(PollMilliseconds);
        }
    }

    private static List<(int Pid, string Name)> RunningFrom(string installDir, HashSet<int> skip)
    {
        var found = new List<(int Pid, string Name)>();
        foreach (var name in ProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (skip.Contains(process.Id))
                    {
                        continue;
                    }

                    if (RunningImageRule.IsRunningFrom(process.Id, installDir))
                    {
                        found.Add((process.Id, name));
                    }
                }
            }
        }

        return found;
    }

    private static void PostCloseToWindows(int pid)
    {
        var target = unchecked((uint)pid);
        EnumWindowsProc visit = (hwnd, lParam) =>
        {
            _ = GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner == target)
            {
                _ = PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
            }

            return true;
        };

        // The delegate stays referenced for the whole call, so the native side never sees a collected callback.
        _ = EnumWindows(visit, IntPtr.Zero);
        GC.KeepAlive(visit);
    }

#pragma warning disable SYSLIB1054 // EnumWindows takes a managed callback, which LibraryImport does not support.
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
#pragma warning restore SYSLIB1054
}

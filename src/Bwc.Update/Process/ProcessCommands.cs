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

    /// <summary>Exit code of CloseRunning when a BWC process of the folder runs in another session.</summary>
    public const int OtherSessionExitCode = 4;

    private const int PollMilliseconds = 250;
    private const uint WmClose = 0x0010;

    /// <summary>
    /// Sends WM_CLOSE to the top-level windows of every BWC process that runs from <paramref name="installDir"/>,
    /// except <paramref name="excluded"/> and this process, and waits up to <paramref name="timeoutSeconds"/>
    /// for none to remain. Returns 0 when none remain, or 3 and prints each remaining name and pid.
    /// Before anything is closed, a BWC process of the folder in another session ends the command with
    /// <see cref="OtherSessionExitCode"/>: each one is printed as <c>RunningInOtherSession session=n name pid=n</c>.
    /// </summary>
    public static int CloseRunning(string installDir, IReadOnlyList<int> excluded, int timeoutSeconds, TextWriter output) =>
        CloseRunningScoped(
            installDir,
            excluded,
            timeoutSeconds,
            output,
            new SystemProcessSource(),
            ProcessScan.CurrentSessionId(),
            PostCloseToWindows,
            ProcessIdentity.IsElevatedOrHighIntegrity,
            () => PrivilegeScope.TryEnable(PrivilegeScope.DebugPrivilege));

    /// <summary>
    /// The command with <c>SeDebugPrivilege</c> enabled around the whole scan and close when the process is elevated and the
    /// token holds it, so the image of a process that belongs to another user can be read (update-elevation.md, HC1). The
    /// privilege goes back to its earlier state when the command ends or throws. A token without it, and a process that is
    /// not elevated, run the command as before (HC2, HC4). Tests give the elevation check and the privilege step.
    /// </summary>
    internal static int CloseRunningScoped(
        string installDir,
        IReadOnlyList<int> excluded,
        int timeoutSeconds,
        TextWriter output,
        IProcessSource source,
        int currentSession,
        Action<int> postClose,
        Func<bool> isElevated,
        Func<IDisposable?> enableDebugPrivilege)
    {
        ArgumentNullException.ThrowIfNull(isElevated);
        ArgumentNullException.ThrowIfNull(enableDebugPrivilege);
        using var privilege = isElevated() ? enableDebugPrivilege() : null;
        return CloseRunning(installDir, excluded, timeoutSeconds, output, source, currentSession, postClose);
    }

    /// <summary>
    /// The same command with the process list, the current session, and the "ask this process to close" step given by the
    /// caller. Tests use this overload, so no window is ever closed by a test.
    /// </summary>
    public static int CloseRunning(
        string installDir,
        IReadOnlyList<int> excluded,
        int timeoutSeconds,
        TextWriter output,
        IProcessSource source,
        int currentSession,
        Action<int> postClose)
    {
        ArgumentNullException.ThrowIfNull(installDir);
        ArgumentNullException.ThrowIfNull(excluded);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(postClose);

        var skip = new HashSet<int>(excluded) { Environment.ProcessId };
        var otherSessions = ProcessScan.RunningFromOtherSessions(installDir, skip, source, currentSession);
        if (otherSessions.Count > 0)
        {
            foreach (var (pid, name, session) in otherSessions)
            {
                output.WriteLine(
                    "RunningInOtherSession session=" + session.ToString(CultureInfo.InvariantCulture)
                    + " " + name + " pid=" + pid.ToString(CultureInfo.InvariantCulture));
            }

            return OtherSessionExitCode;
        }

        var asked = new HashSet<int>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var running = ProcessScan.RunningFrom(installDir, skip, source, currentSession);
            if (running.Count == 0)
            {
                return 0;
            }

            foreach (var (pid, _) in running)
            {
                if (asked.Add(pid))
                {
                    postClose(pid);
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

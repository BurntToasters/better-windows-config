using System.Runtime.InteropServices;
using System.Text;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;

namespace Bwc.E2E.Remote;

/// <summary>
/// Finds and closes the top-level windows that Connect and Import open outside the BWC window tree: the
/// computer picker (a Win32 dialog) and the error boxes. Only windows of the app process that are owned by
/// the main window are considered.
/// </summary>
internal static class RemoteWindows
{
    private const uint WmClose = 0x0010;

    /// <summary>Waits for a visible window owned by the main window with exactly this title.</summary>
    internal static nint WaitForOwnedWindow(AppSession session, string title, TimeSpan timeout)
    {
        nint found = 0;
        _ = ControlTestSupport.WaitUntil(
            () => (found = FindOwnedWindow(session, title)) != 0,
            timeout);
        return found;
    }

    internal static string TitleOf(nint window)
    {
        var buffer = new StringBuilder(256);
        return GetWindowText(window, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    internal static bool Exists(nint window) => window != 0 && IsWindow(window);

    /// <summary>Posts WM_CLOSE. A modal dialog treats it as Cancel.</summary>
    internal static bool Close(nint window) => window != 0 && PostMessage(window, WmClose, 0, 0);

    /// <summary>True when the window's thread has not answered for about five seconds.</summary>
    internal static bool IsHung(nint window) => window != 0 && IsHungAppWindow(window);

    /// <summary>
    /// Lists every top-level window of the app process with this title, with its owner, visibility, and class. Used
    /// in a failure detail, so a window that the owner check rejects is still visible in the report.
    /// </summary>
    internal static string DescribeWindowsTitled(AppSession session, string title)
    {
        var parts = new List<string>();
        _ = EnumWindows(
            (window, _) =>
            {
                GetWindowThreadProcessId(window, out uint windowProcessId);
                if (windowProcessId != (uint)session.ProcessId || !string.Equals(TitleOf(window), title, StringComparison.Ordinal))
                {
                    return true;
                }

                var cls = new StringBuilder(256);
                _ = GetClassName(window, cls, cls.Capacity);
                parts.Add(
                    $"hwnd=0x{window:X} owner=0x{Win32.GetWindow(window, Win32.GvOwner):X} " +
                    $"main=0x{session.MainHwnd:X} visible={IsWindowVisible(window)} class='{cls}'");
                return true;
            },
            0);
        return parts.Count == 0 ? "no window with that title" : string.Join("; ", parts);
    }

    private static nint FindOwnedWindow(AppSession session, string title)
    {
        nint found = 0;
        _ = EnumWindows(
            (window, _) =>
            {
                GetWindowThreadProcessId(window, out uint windowProcessId);
                if (windowProcessId != (uint)session.ProcessId ||
                    !IsWindowVisible(window) ||
                    Win32.GetWindow(window, Win32.GvOwner) != session.MainHwnd ||
                    !string.Equals(TitleOf(window), title, StringComparison.Ordinal))
                {
                    return true;
                }

                found = window;
                return false;
            },
            0);

        return found;
    }

    private delegate bool EnumWindowsProc(nint window, nint lParam);

#pragma warning disable SYSLIB1054 // The small window discovery surface does not justify LibraryImport plumbing.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder name, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsHungAppWindow(nint window);
#pragma warning restore SYSLIB1054
}

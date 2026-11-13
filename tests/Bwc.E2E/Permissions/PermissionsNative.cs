using System.Runtime.InteropServices;
using System.Text;

namespace Bwc.E2E.Permissions;

/// <summary>
/// Minimal window discovery for the aclui security editor. The editor is a native #32770 window
/// owned by the Regedit main window, so it is outside the UIA window root used by AppSession.
/// </summary>
internal static class PermissionsNative
{
    internal const int GvOwner = 4;

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

#pragma warning disable SYSLIB1054 // The small P/Invoke surface does not justify LibraryImport plumbing.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowEnabled(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindow(IntPtr hWnd, int command);

#pragma warning restore SYSLIB1054

    internal static string ClassOf(IntPtr hWnd)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(hWnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    internal static string TitleOf(IntPtr hWnd)
    {
        var buffer = new StringBuilder(512);
        return GetWindowText(hWnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }
}

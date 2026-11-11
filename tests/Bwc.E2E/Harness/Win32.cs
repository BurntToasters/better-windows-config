using System.Runtime.InteropServices;

namespace Bwc.E2E.Harness;

/// <summary>
/// Minimal Win32 window helpers used by the harness. All handles are HWNDs owned by the test
/// process or by processes the test started.
/// </summary>
internal static class Win32
{
    internal const int GvOwner = 4;
    internal const int SwRestore = 9;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);

#pragma warning disable SYSLIB1054 // LibraryImport requires unsafe/partial plumbing that this small helper does not need.
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowEnabled(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
#pragma warning restore SYSLIB1054

    internal static bool HasOwner(IntPtr hwnd, IntPtr expectedOwner) =>
        GetWindow(hwnd, GvOwner) == expectedOwner;

    internal static bool IsForeground(IntPtr hwnd) => GetForegroundWindow() == hwnd;

    /// <summary>The process id that owns a window, or 0 when the window is gone.</summary>
    internal static uint ProcessIdOf(IntPtr hwnd)
    {
        _ = GetWindowThreadProcessId(hwnd, out uint processId);
        return processId;
    }

    /// <summary>
    /// Pins a window above the other non-topmost windows of the desktop, so another application cannot cover
    /// it during a session. Owned windows (dialogs of the window) stay above their owner. Clearing the flag
    /// returns the window to normal z-order. Activation is not changed.
    /// </summary>
    internal static bool SetTopmost(IntPtr hwnd, bool topmost) =>
        hwnd != IntPtr.Zero && IsWindow(hwnd) &&
        SetWindowPos(
            hwnd,
            topmost ? HwndTopmost : HwndNoTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate);

    /// <summary>
    /// Brings the window to the foreground so keystrokes reach it. Returns false when it cannot within the timeout.
    /// A minimized window is restored first; a window that is already restored keeps its size and state.
    /// </summary>
    internal static bool Foreground(IntPtr hwnd, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (IsForeground(hwnd))
            {
                return true;
            }

            if (IsIconic(hwnd))
            {
                _ = ShowWindow(hwnd, SwRestore);
            }

            _ = SetForegroundWindow(hwnd);
            Thread.Sleep(100);
            if (DateTime.UtcNow >= deadline)
            {
                return IsForeground(hwnd);
            }
        }
    }

    /// <summary>
    /// Brings the window to the foreground. When Windows refuses the plain request (the foreground lock), this thread
    /// is attached to the foreground thread for the request, which lets the test take focus back from another
    /// window. Every harness path that sends keystrokes or mouse input to a window goes through this helper.
    /// Returns true once the window is the foreground window within the timeout.
    /// </summary>
    internal static bool ForceForeground(IntPtr hwnd, TimeSpan timeout)
    {
        if (Foreground(hwnd, TimeSpan.FromMilliseconds(500)))
        {
            return true;
        }

        IntPtr foreground = GetForegroundWindow();
        uint currentThread = GetCurrentThreadId();
        uint foregroundThread = GetWindowThreadProcessId(foreground, out _);
        bool attached = foregroundThread != 0 && foregroundThread != currentThread &&
            AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            if (IsIconic(hwnd))
            {
                _ = ShowWindow(hwnd, SwRestore);
            }

            _ = BringWindowToTop(hwnd);
            _ = SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                _ = AttachThreadInput(currentThread, foregroundThread, false);
            }
        }

        return Foreground(hwnd, timeout);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

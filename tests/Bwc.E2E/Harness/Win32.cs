using System.Runtime.InteropServices;
using System.Text;

namespace Bwc.E2E.Harness;

/// <summary>
/// Minimal Win32 window helpers used by the harness. All handles are HWNDs owned by the test
/// process or by processes the test started.
/// </summary>
/// <remarks>
/// The harness never changes z order or topmost state. Activation is requested only by
/// <see cref="ForegroundGuard"/>, and only for input after a foreign-foreground event.
/// </remarks>
internal static class Win32
{
    internal const int GwHwndPrev = 3;
    internal const int GvOwner = 4;
    internal const int SwRestore = 9;

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
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);
#pragma warning restore SYSLIB1054

    /// <summary>The window class name, or an empty string. Read only; used in diagnostics.</summary>
    internal static string ClassNameOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(256);
        int length = GetClassName(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString() : string.Empty;
    }

    internal static bool HasOwner(IntPtr hwnd, IntPtr expectedOwner) =>
        GetWindow(hwnd, GvOwner) == expectedOwner;

    internal static bool IsVisible(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindowVisible(hwnd);

    internal static bool IsForeground(IntPtr hwnd) => GetForegroundWindow() == hwnd;

    /// <summary>The process id that owns a window, or 0 when the window is gone.</summary>
    internal static uint ProcessIdOf(IntPtr hwnd)
    {
        _ = GetWindowThreadProcessId(hwnd, out uint processId);
        return processId;
    }

    /// <summary>The window title, or an empty string. Read only.</summary>
    internal static string TitleOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        int length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        _ = GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    /// <summary>
    /// The window directly above <paramref name="hwnd"/> in the z order, or zero at the top. Walking this chain
    /// from an owner shows whether a window is above it.
    /// </summary>
    internal static IntPtr WindowAbove(IntPtr hwnd) => GetWindow(hwnd, GwHwndPrev);

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
    /// window. Only <see cref="ForegroundGuard"/> calls this, and only after a foreign-foreground event.
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

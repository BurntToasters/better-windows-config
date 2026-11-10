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
#pragma warning restore SYSLIB1054

    internal static bool HasOwner(IntPtr hwnd, IntPtr expectedOwner) =>
        GetWindow(hwnd, GvOwner) == expectedOwner;

    internal static bool IsForeground(IntPtr hwnd) => GetForegroundWindow() == hwnd;

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

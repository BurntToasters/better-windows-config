using System.Runtime.InteropServices;

namespace Bwc.Spike.Interop.E2E;

/// <summary>Win32 calls used only by the test harness (window discovery, owner/enabled checks, foreground).</summary>
internal static unsafe partial class Native
{
    public const uint GW_OWNER = 4;
    public const int SW_RESTORE = 9;
    public const int GWL_EXSTYLE = -20;
    public const nint WS_EX_APPWINDOW = 0x00040000;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    private static List<nint>? _enumSink;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    public static partial int GetWindowRect(nint hWnd, out RECT lpRect);

    public static nint GetWindowLongPtrExStyle(nint hwnd) => GetWindowLongPtr(hwnd, GWL_EXSTYLE);

    public static RECT RectOf(nint hwnd)
    {
        GetWindowRect(hwnd, out var rect);
        return rect;
    }

    [LibraryImport("user32.dll")]
    public static partial int EnumWindows(delegate* unmanaged<nint, nint, int> lpEnumFunc, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    public static partial int GetWindowTextW(nint hWnd, char* lpString, int nMaxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    public static partial int GetClassNameW(nint hWnd, char* lpClassName, int nMaxCount);

    [LibraryImport("user32.dll")]
    public static partial nint GetWindow(nint hWnd, uint uCmd);

    [LibraryImport("user32.dll")]
    public static partial int IsWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial int IsWindowEnabled(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial int IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial int SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial int ShowWindow(nint hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    public static partial int AttachThreadInput(uint idAttach, uint idAttachTo, int fAttach);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    /// <summary>All top-level windows (owned ones included), in EnumWindows order.</summary>
    public static List<nint> TopLevelWindows()
    {
        _enumSink = new List<nint>();
        try
        {
            EnumWindows(&CollectWindow, 0);
            return _enumSink;
        }
        finally
        {
            _enumSink = null;
        }
    }

    [UnmanagedCallersOnly]
    private static int CollectWindow(nint hwnd, nint lParam)
    {
        _enumSink?.Add(hwnd);
        return 1;
    }

    public static uint ProcessIdOf(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    public static string TitleOf(nint hwnd)
    {
        Span<char> buffer = stackalloc char[512];
        fixed (char* p = buffer)
        {
            var n = GetWindowTextW(hwnd, p, buffer.Length);
            return new string(p, 0, Math.Max(n, 0));
        }
    }

    public static string ClassOf(nint hwnd)
    {
        Span<char> buffer = stackalloc char[256];
        fixed (char* p = buffer)
        {
            var n = GetClassNameW(hwnd, p, buffer.Length);
            return new string(p, 0, Math.Max(n, 0));
        }
    }

    public static nint OwnerOf(nint hwnd) => GetWindow(hwnd, GW_OWNER);

    /// <summary>Brings a window to the foreground through AttachThreadInput, then verifies it is foreground.</summary>
    public static bool BringToFront(nint hwnd)
    {
        ShowWindow(hwnd, SW_RESTORE);
        var fg = GetForegroundWindow();
        var fgThread = GetWindowThreadProcessId(fg, out _);
        var self = GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != self && AttachThreadInput(self, fgThread, 1) != 0;
        try
        {
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(self, fgThread, 0);
            }
        }
        return GetForegroundWindow() == hwnd;
    }
}

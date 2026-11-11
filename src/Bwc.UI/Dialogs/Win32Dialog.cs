using System.Runtime.InteropServices;

namespace Bwc.UI.Dialogs;

/// <summary>
/// The small Win32 surface the modal dialogs need. Keeping it here prevents a second owner
/// pattern from appearing in the application projects.
/// </summary>
internal static partial class Win32Dialog
{
    internal const int GWLP_HWNDPARENT = -8;
    internal const int GWL_EXSTYLE = -20;
    internal const nint WS_EX_DLGMODALFRAME = 0x00000001;
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_FRAMECHANGED = 0x0020;

    internal static double ScaleForWindow(nint hwnd)
    {
        uint dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    internal static bool TryGetWindowRect(nint hwnd, out WindowRect rect) =>
        GetWindowRect(hwnd, out rect) != 0;

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static partial nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static partial nint GetWindowLongPtr(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [LibraryImport("user32.dll")]
    internal static partial int EnableWindow(nint hWnd, int bEnable);

    [LibraryImport("user32.dll")]
    internal static partial nint SetActiveWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    internal static partial int GetWindowRect(nint hWnd, out WindowRect lpRect);

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;

        public int Height => Bottom - Top;
    }
}

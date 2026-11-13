using System.Runtime.InteropServices;

namespace Bwc.UI.Interop;

/// <summary>Hand-written Win32 surface used by the menu keyboard router.</summary>
internal static unsafe partial class Win32
{
    public const uint GW_CHILD = 5;
    public const uint GW_HWNDNEXT = 2;

    public const uint WM_ACTIVATE = 0x0006;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint WM_SYSKEYUP = 0x0105;
    public const uint WM_SYSCHAR = 0x0106;

    public const int VK_RETURN = 0x0D;
    public const int VK_ESCAPE = 0x1B;
    public const int VK_SPACE = 0x20;
    public const int VK_DOWN = 0x28;
    public const int VK_F10 = 0x79;
    public const int VK_MENU = 0x12;
    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;

    [LibraryImport("user32.dll")]
    public static partial nint GetWindow(nint hWnd, uint uCmd);

    [LibraryImport("user32.dll")]
    public static partial short GetKeyState(int nVirtKey);

    [LibraryImport("comctl32.dll")]
    public static partial int SetWindowSubclass(
        nint hWnd,
        delegate* unmanaged<nint, uint, nuint, nint, nuint, nuint, nint> pfnSubclass,
        nuint uIdSubclass,
        nuint dwRefData);

    [LibraryImport("comctl32.dll")]
    public static partial int RemoveWindowSubclass(
        nint hWnd,
        delegate* unmanaged<nint, uint, nuint, nint, nuint, nuint, nint> pfnSubclass,
        nuint uIdSubclass);

    [LibraryImport("comctl32.dll")]
    public static partial nint DefSubclassProc(nint hWnd, uint uMsg, nuint wParam, nint lParam);
}

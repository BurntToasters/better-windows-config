using System.Runtime.InteropServices;

namespace Bwc.Spike.Interop;

/// <summary>Hand-written P/Invoke surface for the S4 spike. Handles are passed as nint.</summary>
internal static unsafe partial class Win32
{
    public const uint GW_CHILD = 5;
    public const uint GW_HWNDNEXT = 2;
    public const uint GW_OWNER = 4;
    public const int GWLP_HWNDPARENT = -8;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    public const uint WM_ACTIVATE = 0x0006;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint WM_SYSKEYUP = 0x0105;
    public const uint WM_SYSCHAR = 0x0106;
    public const uint WM_NCDESTROY = 0x0082;

    public const int VK_RETURN = 0x0D;
    public const int VK_ESCAPE = 0x1B;
    public const int VK_SPACE = 0x20;
    public const int VK_LEFT = 0x25;
    public const int VK_UP = 0x26;
    public const int VK_RIGHT = 0x27;
    public const int VK_DOWN = 0x28;
    public const int VK_F10 = 0x79;
    public const int VK_MENU = 0x12;
    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;

    public const uint ERROR_SUCCESS = 0;
    public const uint ERROR_FILE_NOT_FOUND = 2;
    public const uint ERROR_INSUFFICIENT_BUFFER = 122;
    public const uint ERROR_PRIVILEGE_NOT_HELD = 1314;
    public const uint OWNER_SECURITY_INFORMATION = 0x1;
    public const uint GROUP_SECURITY_INFORMATION = 0x2;
    public const uint DACL_SECURITY_INFORMATION = 0x4;
    public const uint SACL_SECURITY_INFORMATION = 0x8;

    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_POINTER = unchecked((int)0x80004003);
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_ACCESSDENIED = unchecked((int)0x80070005);
    public const int HRESULT_CANCELLED = unchecked((int)0x800704C7); // HRESULT_FROM_WIN32(ERROR_CANCELLED)

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public uint cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    // user32
    [LibraryImport("user32.dll")] public static partial nint GetWindow(nint hWnd, uint uCmd);
    [LibraryImport("user32.dll")] public static partial int IsWindow(nint hWnd);
    [LibraryImport("user32.dll")] public static partial int IsWindowEnabled(nint hWnd);
    [LibraryImport("user32.dll")] public static partial int IsWindowVisible(nint hWnd);
    [LibraryImport("user32.dll")] public static partial int EnableWindow(nint hWnd, int bEnable);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static partial nint GetWindowLongPtr(nint hWnd, int nIndex);
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static partial nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
    [LibraryImport("user32.dll")] public static partial int SetForegroundWindow(nint hWnd);
    [LibraryImport("user32.dll")] public static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] public static partial nint SetActiveWindow(nint hWnd);
    [LibraryImport("user32.dll")] public static partial int GetWindowRect(nint hWnd, out RECT lpRect);
    [LibraryImport("user32.dll")] public static partial nint MonitorFromWindow(nint hwnd, uint dwFlags);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")] public static partial int GetMonitorInfo(nint hMonitor, MONITORINFO* lpmi);
    [LibraryImport("user32.dll")] public static partial short GetKeyState(int nVirtKey);
    [LibraryImport("user32.dll")] public static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
    [LibraryImport("user32.dll")] public static partial uint GetDpiForWindow(nint hwnd);
    [LibraryImport("user32.dll")] public static partial int AttachThreadInput(uint idAttach, uint idAttachTo, int fAttach);
    [LibraryImport("user32.dll")] public static partial uint GetCurrentThreadId();

    // comctl32 (hand-written so the subclass proc is a [UnmanagedCallersOnly] function pointer, not a delegate)
    [LibraryImport("comctl32.dll")]
    public static partial int SetWindowSubclass(nint hWnd, delegate* unmanaged<nint, uint, nuint, nint, nuint, nuint, nint> pfnSubclass, nuint uIdSubclass, nuint dwRefData);
    [LibraryImport("comctl32.dll")]
    public static partial int RemoveWindowSubclass(nint hWnd, delegate* unmanaged<nint, uint, nuint, nint, nuint, nuint, nint> pfnSubclass, nuint uIdSubclass);
    [LibraryImport("comctl32.dll")]
    public static partial nint DefSubclassProc(nint hWnd, uint uMsg, nuint wParam, nint lParam);

    // kernel32: security descriptors handed to aclui are LocalAlloc'd, because aclui releases them with LocalFree.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint LocalAlloc(uint uFlags, nuint uBytes);

    [LibraryImport("kernel32.dll")]
    public static partial nint LocalFree(nint hMem);

    public const uint ERROR_NOT_ENOUGH_MEMORY = 8;

    // advapi32
    [LibraryImport("advapi32.dll")]
    public static partial uint RegGetKeySecurity(nint hKey, uint SecurityInformation, nint pSecurityDescriptor, uint* lpcbSecurityDescriptor);

    // aclui: EditSecurity takes our managed ISecurityInformation (GeneratedComInterface).
    [LibraryImport("aclui.dll")]
    public static partial int EditSecurity(nint hwndOwner, ISecurityInformation psi);
}

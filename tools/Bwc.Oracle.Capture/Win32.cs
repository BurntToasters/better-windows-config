using System.Runtime.InteropServices;
using System.Text;

namespace Bwc.Oracle.Capture;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct LogFontW
{
    public int Height;
    public int Width;
    public int Escapement;
    public int Orientation;
    public int Weight;
    public byte Italic;
    public byte Underline;
    public byte StrikeOut;
    public byte CharSetId;
    public byte OutPrecision;
    public byte ClipPrecision;
    public byte Quality;
    public byte PitchAndFamily;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string FaceName;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NonClientMetricsW
{
    public uint CbSize;
    public int BorderWidth;
    public int ScrollWidth;
    public int ScrollHeight;
    public int CaptionWidth;
    public int CaptionHeight;
    public LogFontW CaptionFont;
    public int SmCaptionWidth;
    public int SmCaptionHeight;
    public LogFontW SmCaptionFont;
    public int MenuWidth;
    public int MenuHeight;
    public LogFontW MenuFont;
    public LogFontW StatusFont;
    public LogFontW MessageFont;
    public int PaddedBorderWidth;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MenuBarInfo
{
    public uint CbSize;
    public NativeRect RcBar;
    public IntPtr HMenu;
    public IntPtr HwndMenu;
    public int Flags;
}

// Win32 constants used by the metrics pass. Message values are spelled out so they can be checked against winuser.h.
internal static class Msg
{
    internal const uint WM_CLOSE = 0x0010;
    internal const uint WM_GETFONT = 0x0031;
    internal const uint WM_USER = 0x0400;
    internal const uint SB_GETPARTS = WM_USER + 6;     // 0x0406
    internal const uint SB_GETRECT = WM_USER + 10;     // 0x040A
    internal const uint SB_GETTEXTLENGTHW = WM_USER + 12; // 0x040C
    internal const uint SB_GETTEXTW = WM_USER + 13;    // 0x040D

    internal const uint TV_FIRST = 0x1100;
    internal const uint TVM_GETITEMRECT = TV_FIRST + 4;   // 0x1104
    internal const uint TVM_GETCOUNT = TV_FIRST + 5;      // 0x1105
    internal const uint TVM_GETINDENT = TV_FIRST + 6;     // 0x1106
    internal const uint TVM_GETNEXTITEM = TV_FIRST + 10;  // 0x110A
    internal const uint TVM_GETITEMHEIGHT = TV_FIRST + 28; // 0x111C
    internal const uint TVM_GETITEMW = TV_FIRST + 62;     // 0x113E

    internal const uint LVM_FIRST = 0x1000;
    internal const uint LVM_GETITEMCOUNT = LVM_FIRST + 4;   // 0x1004
    internal const uint LVM_GETITEMRECT = LVM_FIRST + 14;   // 0x100E
    internal const uint LVM_GETCOLUMNWIDTH = LVM_FIRST + 29; // 0x101D
    internal const uint LVM_GETHEADER = LVM_FIRST + 31;     // 0x101F
    internal const uint LVM_GETTOPINDEX = LVM_FIRST + 39;   // 0x1027
    internal const uint LVM_GETITEMTEXTW = LVM_FIRST + 115; // 0x1073
    internal const uint LVM_GETCOLUMNW = LVM_FIRST + 95;    // 0x105F

    internal const uint TVGN_CARET = 0x9;
    internal const uint TVIF_TEXT = 0x1;
    internal const uint TVIF_HANDLE = 0x10;
    internal const uint LVCF_FMT = 0x1;
    internal const uint LVCF_WIDTH = 0x2;
    internal const uint LVCF_TEXT = 0x4;
    internal const uint LVCF_SUBITEM = 0x8;
    internal const int LVIR_BOUNDS = 0;
    internal const int LVIR_ICON = 1;
    internal const int LVIR_LABEL = 2;

    internal const uint SMTO_ABORTIFHUNG = 0x0002;
    internal const uint MF_BYPOSITION = 0x0400;
    internal const uint MF_CHECKED = 0x0008;
    internal const uint WM_COMMAND = 0x0111;
    internal const int OBJID_MENU = -3;
    internal const uint SPI_GETNONCLIENTMETRICS = 0x0029;
    internal const uint PW_RENDERFULLCONTENT = 0x00000002;

    internal const int SM_CXSMICON = 49;
    internal const int SM_CYCAPTION = 4;
    internal const int SM_CYMENU = 15;
    internal const int SM_CXVSCROLL = 2;
    internal const int SM_CYHSCROLL = 3;
    internal const int SM_CXEDGE = 45;
    internal const int SM_CYEDGE = 46;

    internal static readonly IntPtr DpiAwarePerMonitorV2 = new(-4);
}

internal static class Win32
{
    internal delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassNameW(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int GetDlgCtrlID(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    internal static extern bool GetClientRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    internal static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetMenu(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetMenuBarInfo(IntPtr hWnd, int idObject, int idItem, ref MenuBarInfo info);

    [DllImport("user32.dll")]
    internal static extern int GetMenuItemCount(IntPtr hMenu);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetSubMenu(IntPtr hMenu, int position);

    [DllImport("user32.dll")]
    internal static extern uint GetMenuItemID(IntPtr hMenu, int position);

    [DllImport("user32.dll")]
    internal static extern uint GetMenuState(IntPtr hMenu, uint item, uint flags);

    [DllImport("user32.dll")]
    internal static extern bool GetMenuItemRect(IntPtr hWnd, IntPtr hMenu, uint item, out NativeRect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetMenuStringW(IntPtr hMenu, uint item, StringBuilder text, int maxCount, uint flags);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SystemParametersInfoW(uint action, uint param, ref NonClientMetricsW info, uint winIni);

    [DllImport("user32.dll")]
    internal static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetObjectW(IntPtr gdiObject, int bufferSize, out LogFontW font);
}

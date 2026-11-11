using System.Runtime.InteropServices;

namespace Bwc.Oracle.Capture;

// Window, menu and control messages used by the dialog, menu, sort and find captures. Kept apart from
// Win32.cs and Msg so the metrics code is untouched. Every value is named after its winuser.h or commctrl.h name.
internal static class Wm
{
    internal const uint KEYDOWN = 0x0100;
    internal const uint CANCELMODE = 0x001F;
    internal const uint CONTEXTMENU = 0x007B;
    internal const uint SYSCOMMAND = 0x0112;
    internal const uint COMMAND = 0x0111;
    internal const uint CLOSE = 0x0010;
    internal const uint LBUTTONDOWN = 0x0201;
    internal const uint LBUTTONUP = 0x0202;
    internal const uint LBUTTONDBLCLK = 0x0203;
    internal const uint RBUTTONDOWN = 0x0204;
    internal const uint RBUTTONUP = 0x0205;
    internal const uint MN_GETHMENU = 0x01E1;

    internal const uint SC_KEYMENU = 0xF100;
    internal const int IDCANCEL = 2;

    internal const uint MF_GRAYED = 0x0001;
    internal const uint MF_CHECKED = 0x0008;
    internal const uint MF_POPUP = 0x0010;
    internal const uint MF_SEPARATOR = 0x0800;
    internal const uint MF_BYPOSITION = 0x0400;

    internal const int VK_RETURN = 0x0D;
    internal const int VK_ESCAPE = 0x1B;
    internal const int VK_F3 = 0x72;

    internal const uint GW_OWNER = 4;

    internal const uint LVM_GETNEXTITEM = 0x1000 + 12;
    internal const uint LVM_SETITEMSTATE = 0x1000 + 43;
    internal const uint LVM_GETITEMTEXTW = 0x1000 + 115;
    internal const uint LVM_ENSUREVISIBLE = 0x1000 + 19;
    internal const int LVNI_FOCUSED = 0x0001;
    internal const int LVNI_SELECTED = 0x0002;
    internal const uint LVIS_FOCUSED = 0x0001;
    internal const uint LVIS_SELECTED = 0x0002;

    internal const uint TVM_SELECTITEM = 0x1100 + 11;
    internal const uint TVM_EDITLABELW = 0x1100 + 65;
    internal const uint TVGN_ROOT = 0;
    internal const uint TVGN_NEXT = 1;
    internal const uint TVGN_CARET = 9;
    internal const uint TVGN_FIRSTVISIBLE = 5;
    internal const uint TVGN_NEXTVISIBLE = 6;
    internal const uint TVGN_PARENT = 3;
    internal const uint TVGN_CHILD = 4;

    internal const uint HDM_GETITEMCOUNT = 0x1200;
    internal const uint HDM_GETITEMRECT = 0x1200 + 7;

    internal const uint WM_SETTEXT = 0x000C;
    internal const uint BM_GETCHECK = 0x00F0;
    internal const uint BM_SETCHECK = 0x00F1;
    internal const uint BM_CLICK = 0x00F5;
    internal const int GWL_STYLE = -16;
    internal const uint MK_LBUTTON = 0x0001;
}

internal static class Win32Ext
{
    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [DllImport("user32.dll")]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDlgItem(IntPtr hDlg, int id);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowEnabled(IntPtr hWnd);

    // Used only for WM_SETTEXT on a dialog's edit control, which the system marshals across processes.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
    internal static extern IntPtr SendMessageText(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    internal static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    internal static extern bool GetGUIThreadInfo(uint idThread, ref GuiThreadInfo info);
}

[StructLayout(LayoutKind.Sequential)]
internal struct GuiThreadInfo
{
    public uint cbSize;
    public uint flags;
    public IntPtr hwndActive;
    public IntPtr hwndFocus;
    public IntPtr hwndCapture;
    public IntPtr hwndMenuOwner;
    public IntPtr hwndMoveSize;
    public IntPtr hwndCaret;
    public NativeRect rcCaret;
}

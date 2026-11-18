using System.Runtime.InteropServices;
using System.Text;

namespace Bwc.Oracle.Capture.Gpedit.Driver;

// Win32 calls the driver needs beyond Win32.cs and Win32Ext.cs: text and combo messages (system marshaled across processes), styles,
// foreground checks. Every message goes through SendMessageTimeout with SMTO_ABORTIFHUNG, so a hung mmc cannot hang the tool.
internal static class DriverNative
{
    internal const uint WmGetText = 0x000D;
    internal const uint WmGetTextLength = 0x000E;
    internal const uint CbGetCurSel = 0x0147;
    internal const uint CbGetCount = 0x0146;
    internal const uint CbGetLbText = 0x0148;
    internal const uint CbGetLbTextLen = 0x0149;
    internal const uint CbSetCurSel = 0x014E;
    internal const uint CbnSelChange = 1;
    internal const uint CbnEditChange = 5;
    internal const uint HdmGetItemW = 0x1200 + 11;
    internal const uint HdiFormat = 0x0004;
    internal const uint HdfSortUp = 0x0400;
    internal const uint HdfSortDown = 0x0200;
    internal const uint EsMultiline = 0x0004;
    internal const uint CbsMask = 0x0003;
    internal const uint CbsDropDown = 0x0002;
    internal const uint CbsDropDownList = 0x0003;
    internal const uint BsTypeMask = 0x000F;
    private const uint TimeoutMs = 5000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeoutString(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeoutBuffer(IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam, uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern bool IsChild(IntPtr parent, IntPtr child);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanW(char ch);

    // Returns the message result, or throws DriverException when the window did not answer within 5 seconds.
    internal static IntPtr Send(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!Win32.SendMessageTimeoutW(hwnd, msg, wParam, lParam, Msg.SMTO_ABORTIFHUNG, TimeoutMs, out IntPtr result))
        {
            throw new DriverException($"window 0x{hwnd:X} did not answer message 0x{msg:X} within {TimeoutMs} ms");
        }

        return result;
    }

    internal static void SetText(IntPtr hwnd, string text)
    {
        if (SendMessageTimeoutString(hwnd, Wm.WM_SETTEXT, IntPtr.Zero, text, Msg.SMTO_ABORTIFHUNG, TimeoutMs, out _) == IntPtr.Zero)
        {
            throw new DriverException($"window 0x{hwnd:X} did not take WM_SETTEXT within {TimeoutMs} ms");
        }
    }

    // WM_GETTEXT, because GetWindowText does not return the text of an edit control of another process.
    internal static string GetText(IntPtr hwnd)
    {
        int length = Send(hwnd, WmGetTextLength, IntPtr.Zero, IntPtr.Zero).ToInt32();
        if (length <= 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(length + 2);
        SendMessageTimeoutBuffer(hwnd, WmGetText, new IntPtr(length + 1), sb, Msg.SMTO_ABORTIFHUNG, TimeoutMs, out _);
        return sb.ToString();
    }

    internal static List<string> ComboItems(IntPtr combo)
    {
        int count = Send(combo, CbGetCount, IntPtr.Zero, IntPtr.Zero).ToInt32();
        var items = new List<string>();
        for (int i = 0; i < count; i++)
        {
            int length = Send(combo, CbGetLbTextLen, new IntPtr(i), IntPtr.Zero).ToInt32();
            var sb = new StringBuilder(Math.Max(length, 0) + 2);
            SendMessageTimeoutBuffer(combo, CbGetLbText, new IntPtr(i), sb, Msg.SMTO_ABORTIFHUNG, TimeoutMs, out _);
            items.Add(sb.ToString());
        }

        return items;
    }

    internal static int ComboSelected(IntPtr combo) => Send(combo, CbGetCurSel, IntPtr.Zero, IntPtr.Zero).ToInt32();

    // Selects an item and tells the parent, as a user click does, so the control raises its selection event.
    internal static void ComboSelect(IntPtr combo, int index)
    {
        Send(combo, CbSetCurSel, new IntPtr(index), IntPtr.Zero);
        NotifyParent(combo, CbnSelChange);
    }

    // WM_COMMAND with the notification code in the high word of wParam and the control id in the low word.
    internal static void NotifyParent(IntPtr control, uint code)
    {
        IntPtr parent = Win32.GetParent(control);
        int id = Win32.GetDlgCtrlID(control);
        IntPtr wParam = new(((long)code << 16) | (uint)(id & 0xFFFF));
        Send(parent, Msg.WM_COMMAND, wParam, control);
    }

    internal static uint Style(IntPtr hwnd) => unchecked((uint)Win32Ext.GetWindowLong(hwnd, Wm.GWL_STYLE));

    internal static int CheckState(IntPtr button) => Send(button, Wm.BM_GETCHECK, IntPtr.Zero, IntPtr.Zero).ToInt32();

    internal static void Click(IntPtr button) => Win32.PostMessageW(button, Wm.BM_CLICK, IntPtr.Zero, IntPtr.Zero);

    internal static uint ProcessOf(IntPtr hwnd)
    {
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    // The virtual key and whether Shift is needed for a character, or null when the layout has no key for it.
    internal static (ushort Vk, bool Shift)? KeyFor(char ch)
    {
        short scan = VkKeyScanW(ch);
        if (scan == -1)
        {
            return null;
        }

        return ((ushort)(scan & 0xFF), (scan & 0x100) != 0);
    }
}

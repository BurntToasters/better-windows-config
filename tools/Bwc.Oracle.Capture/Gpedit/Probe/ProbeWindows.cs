using System.Text;
using System.Text.Json.Nodes;

namespace Bwc.Oracle.Capture.Gpedit.Probe;

internal sealed record WinInfo(IntPtr Hwnd, string Class, string Title, bool Visible, IntPtr Owner, NativeRect Rect);

// Win32 window lists for the mmc.exe process: top-level windows (dialogs and menus included) and the child windows of one window.
internal static class ProbeWindows
{
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        "#32768",
        "tooltips_class32",
        "MSCTFIME UI",
        "IME",
        "GDI+ Hook Window Class",
        "CicMarshalWndClass",
    };

    internal static string ClassOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        Win32.GetClassNameW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    internal static string TitleOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        Win32.GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static WinInfo Info(IntPtr hwnd)
    {
        Win32.GetWindowRect(hwnd, out NativeRect rect);
        return new WinInfo(hwnd, ClassOf(hwnd), TitleOf(hwnd), Win32.IsWindowVisible(hwnd), Win32Ext.GetWindow(hwnd, Wm.GW_OWNER), rect);
    }

    internal static List<WinInfo> TopLevel(int pid)
    {
        var list = new List<WinInfo>();
        Win32.EnumWindows((hwnd, _) =>
        {
            Win32.GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner == (uint)pid)
            {
                list.Add(Info(hwnd));
            }

            return true;
        }, IntPtr.Zero);
        return list;
    }

    // Visible windows of the process that are not the main window and not menus, tooltips or input helpers: dialogs and message boxes.
    internal static List<WinInfo> Dialogs(int pid, IntPtr main) =>
        TopLevel(pid).Where(w => w.Visible && w.Hwnd != main && !IgnoredClasses.Contains(w.Class) && w.Rect.Width > 0 && w.Rect.Height > 0).ToList();

    internal static List<WinInfo> PopupMenus(int pid) => TopLevel(pid).Where(w => w.Visible && w.Class == "#32768").ToList();

    internal static List<WinInfo> Children(IntPtr parent)
    {
        var list = new List<WinInfo>();
        Win32.EnumChildWindows(parent, (hwnd, _) =>
        {
            list.Add(Info(hwnd));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    internal static JsonObject ToJson(WinInfo w) => new()
    {
        ["hwnd"] = "0x" + w.Hwnd.ToString("X"),
        ["class"] = w.Class,
        ["title"] = w.Title,
        ["visible"] = w.Visible,
        ["owner"] = w.Owner == IntPtr.Zero ? null : JsonValue.Create("0x" + w.Owner.ToString("X")),
        ["rect"] = new JsonArray(w.Rect.Left, w.Rect.Top, w.Rect.Width, w.Rect.Height),
        ["dlgCtrlId"] = Win32.GetDlgCtrlID(w.Hwnd),
    };
}

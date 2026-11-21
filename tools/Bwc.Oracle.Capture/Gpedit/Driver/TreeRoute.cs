using Bwc.Oracle.Capture.Gpedit.Probe;

namespace Bwc.Oracle.Capture.Gpedit.Driver;

// The decisions behind the fast lookups of the console tree and of the selected node. They work on plain values, so the self test covers
// them without classic. Every "no" here means the old search runs. Failure mode 34 in docs/failure-modes/oracle-gpedit-driver.md.
internal static class TreeRoute
{
    internal const string TreeClass = "SysTreeView32";

    // The route names written to the result file.
    internal const string ViaHwnd = "hwnd";
    internal const string ViaUiaType = "uiaType";
    internal const string ViaUiaClass = "uiaClass";
    internal const string SelectionPatternRoute = "selectionPattern";

    // The one visible SysTreeView32 window with an area. Zero or several candidates give null (the old search decides), because the old
    // search returned the first match in UI Automation order and the window order of Win32 need not be the same.
    internal static IntPtr? ChooseTreeWindow(IReadOnlyList<WinInfo> windows)
    {
        List<WinInfo> candidates = windows
            .Where(w => w.Visible && string.Equals(w.Class, TreeClass, StringComparison.Ordinal) && w.Rect.Width > 0 && w.Rect.Height > 0)
            .ToList();
        return candidates.Count == 1 ? candidates[0].Hwnd : null;
    }

    // What the old searches accepted: control type Tree, or the class SysTreeView32.
    internal static bool AcceptsTreeElement(bool controlTypeIsTree, string? className) =>
        controlTypeIsTree || string.Equals(className, TreeClass, StringComparison.Ordinal);

    // The window was found, taken as an element, and must still be the same visible window afterwards.
    internal static bool HandleStillValid(IntPtr chosen, IntPtr elementHandle, bool windowExists, bool windowVisible) =>
        windowExists && windowVisible && chosen != IntPtr.Zero && chosen == elementHandle;

    // The answer of SelectionPattern.GetSelection(): null when the item is usable, otherwise the reason for the old search.
    // The item must satisfy what the old search matched on: a TreeItem that reports IsSelected.
    internal static string? JudgeSelection(int itemCount, bool isTreeItem, bool reportsSelected)
    {
        if (itemCount == 0)
        {
            return "noElement";
        }

        if (itemCount > 1)
        {
            return "several";
        }

        if (!isTreeItem)
        {
            return "notTreeItem";
        }

        return reportsSelected ? null : "notSelected";
    }
}

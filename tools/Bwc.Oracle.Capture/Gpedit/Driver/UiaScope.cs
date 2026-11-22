using Bwc.Oracle.Capture.Gpedit.Probe;

namespace Bwc.Oracle.Capture.Gpedit.Driver;

// One visible list view as the Win32 side sees it: shown, on the screen, and its row count (LVM_GETITEMCOUNT), or -1 when it did not answer.
internal readonly record struct ListSize(bool Visible, bool OnScreen, int Rows);

// The decisions behind the narrow lookups of the status bar, the tabs, the toolbars and the column headers. A UI Automation search that starts at
// the main window walks into every row and cell of the result list, which takes minutes under All Settings (about 3400 rows). From
// LargeListRows rows the lookups start at the window handle of their own control instead. Below that, and whenever the row count cannot be read,
// the old search runs, so every node of the fixture is read by the same calls as before. Failure mode 40 in
// docs/failure-modes/oracle-gpedit-driver.md.
internal static class UiaScope
{
    internal const int LargeListRows = 1000;

    // The route names written to the result file as uiaRoute.
    internal const string RouteNarrow = "hwnd";
    internal const string RouteSearch = "search";

    internal const string StatusBarClass = "msctls_statusbar32";
    internal const string TabClass = "AMCCustomTab";
    internal const string ToolbarClass = "ToolbarWindow32";

    // The rows of the largest list that is visible and on screen; 0 when there is none, or when no visible list gave a count.
    internal static int LargestVisibleList(IEnumerable<ListSize> lists)
    {
        int rows = lists.Where(l => l.Visible && l.OnScreen).Select(l => l.Rows).DefaultIfEmpty(0).Max();
        return rows > 0 ? rows : 0;
    }

    internal static string Route(int visibleRows) => visibleRows >= LargeListRows ? RouteNarrow : RouteSearch;

    // The visible windows of one class, in window order. Case counts: the class names are fixed strings of classic.
    internal static List<WinInfo> VisibleOfClass(IEnumerable<WinInfo> windows, string className) =>
        windows.Where(w => w.Visible && string.Equals(w.Class, className, StringComparison.Ordinal)).ToList();
}

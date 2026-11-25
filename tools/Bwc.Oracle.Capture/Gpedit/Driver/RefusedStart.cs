using Bwc.Oracle.Capture.Gpedit.Probe;

namespace Bwc.Oracle.Capture.Gpedit.Driver;

// The decision that classic refused to open the GPO. Decided by facts and window classes, never by the dialog's text, so it does not
// depend on the language of the machine. Failure mode 33 in docs/failure-modes/oracle-gpedit-driver.md.
internal static class RefusedStart
{
    internal const string DialogClass = "#32770";

    // The visible top level dialogs of mmc that make the missing console tree a refusal. Empty means it is not one: the case does not allow
    // a refusal, mmc is gone, the tree is there, or no visible #32770 window with an area exists.
    internal static List<WinInfo> Dialogs(bool allowed, bool mmcAlive, bool treeFound, IReadOnlyList<WinInfo> topLevelWindows)
    {
        if (!allowed || !mmcAlive || treeFound)
        {
            return new List<WinInfo>();
        }

        return topLevelWindows
            .Where(w => w.Visible && string.Equals(w.Class, DialogClass, StringComparison.Ordinal) && w.Rect.Width > 0 && w.Rect.Height > 0)
            .ToList();
    }
}

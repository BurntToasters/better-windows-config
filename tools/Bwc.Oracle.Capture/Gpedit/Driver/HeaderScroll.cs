namespace Bwc.Oracle.Capture.Gpedit.Driver;

// The two decisions of bringing a column header into view before it is clicked, on plain values: whether the header can be clicked where UI
// Automation puts it, and how far the list scrolls to show it. Failure mode 41 in docs/failure-modes/oracle-gpedit-driver.md.
internal static class HeaderScroll
{
    // A header can be clicked when its rectangle is finite, at least 1 by 1, and its centre is inside the list window.
    internal static bool Clickable(System.Windows.Rect rect, NativeRect list)
    {
        if (rect.IsEmpty || double.IsInfinity(rect.X) || double.IsInfinity(rect.Y) || double.IsInfinity(rect.Width) || double.IsInfinity(rect.Height)
            || double.IsNaN(rect.X) || double.IsNaN(rect.Y) || rect.Width < 1 || rect.Height < 1)
        {
            return false;
        }

        double x = rect.X + (rect.Width / 2);
        double y = rect.Y + (rect.Height / 2);
        return x >= list.Left && x < list.Right && y >= list.Top && y < list.Bottom;
    }

    // The horizontal scroll, in pixels (positive scrolls right), that shows the column: columnLeft is the sum of the widths of the columns before
    // it and position the current scroll position, both in the unscrolled coordinates of the list. The smallest scroll that shows the whole
    // column, or its left edge when the column is wider than the view. Zero when it is shown, or when the view has no width.
    internal static int Distance(int columnLeft, int columnWidth, int viewWidth, int position)
    {
        if (viewWidth <= 0)
        {
            return 0;
        }

        if (columnLeft < position)
        {
            return columnLeft - position;
        }

        int overhang = columnLeft + columnWidth - (position + viewWidth);
        return overhang > 0 ? Math.Min(overhang, columnLeft - position) : 0;
    }
}

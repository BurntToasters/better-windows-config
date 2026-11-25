namespace Bwc.Oracle.Capture.Gpedit.Driver;

// What UI Automation says about one cell of a grid row: control type name, the text of its ValuePattern (null when it has none), its name, and
// whether it has GridItemPattern.
internal readonly record struct GridCellInfo(string ControlType, string? Value, string Name, bool HasGridItem);

// One child of the grid: control type name, name, and its own children (the cells, row header included).
internal readonly record struct GridChildInfo(string ControlType, string Name, IReadOnlyList<GridCellInfo> Cells);

// One data row: where it is among the children of the grid, which of its cells is the row header (null when none), which cells are data cells
// and what they read. Texts keep the raw text, so "(null)" stays visible in the evidence.
internal sealed record GridRowRead(int ChildIndex, string Name, int? RowHeader, IReadOnlyList<int> CellIndexes, IReadOnlyList<string> Texts)
{
    internal bool IsEmpty => Texts.All(GridRead.IsEmptyText);
}

// The classification of a grid. All is every data row in order, the new row included. Placeholder is the index of the new row in All, or null.
// Rows is All without the new row: the rows of the list.
internal sealed record GridShape(IReadOnlyList<int> HeaderChildren, IReadOnlyList<GridRowRead> All, int? Placeholder, IReadOnlyList<GridRowRead> Rows);

// The decision "which children of the Show Contents grid are data rows, which cells are data cells, which row is the new row", on plain values,
// so the self test covers it without classic. The grid is a Windows Forms DataGridView: its rows are Custom elements, the first is a header row
// of Header elements, every row starts with a row header cell, and the new row reads "(null)". Failure mode 37 in
// docs/failure-modes/oracle-gpedit-driver.md.
internal static class GridRead
{
    // What Windows Forms accessibility says for a null cell value.
    internal const string NullText = "(null)";

    private static string Kind(string controlType)
    {
        const string prefix = "ControlType.";
        return (controlType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? controlType[prefix.Length..] : controlType).ToLowerInvariant();
    }

    private static bool IsHeader(GridCellInfo cell) => Kind(cell.ControlType) == "header";

    internal static bool IsEmptyText(string? text) => string.IsNullOrWhiteSpace(text) || string.Equals(text, NullText, StringComparison.Ordinal);

    // The text as shown in a result or compared with a wanted text: a null cell is an empty string.
    internal static string Shown(string? text) => string.Equals(text, NullText, StringComparison.Ordinal) ? string.Empty : text ?? string.Empty;

    // A header row: it has cells and all of them are Header elements.
    private static bool IsHeaderRow(GridChildInfo child) => child.Cells.Count > 0 && child.Cells.All(IsHeader);

    internal static GridShape Classify(IReadOnlyList<GridChildInfo> children)
    {
        var headers = new List<int>();
        bool anyDataItem = children.Any(c => Kind(c.ControlType) == "dataitem");
        var rows = new List<GridRowRead>();
        for (int i = 0; i < children.Count; i++)
        {
            GridChildInfo child = children[i];
            string kind = Kind(child.ControlType);
            if (IsHeaderRow(child))
            {
                headers.Add(i);
                continue;
            }

            bool candidate = anyDataItem
                ? kind == "dataitem"
                : kind is not ("header" or "scrollbar" or "edit") && child.Cells.Count > 0;
            if (candidate)
            {
                rows.Add(ReadRow(i, child));
            }
        }

        int? placeholder = rows.Count > 0 && rows[^1].CellIndexes.Count > 0 && rows[^1].IsEmpty ? rows.Count - 1 : null;
        List<GridRowRead> real = placeholder is null ? rows : rows.Take(rows.Count - 1).ToList();
        return new GridShape(headers, rows, placeholder, real);
    }

    private static GridRowRead ReadRow(int index, GridChildInfo child)
    {
        int? rowHeader = null;
        var nonHeader = new List<int>();
        for (int c = 0; c < child.Cells.Count; c++)
        {
            if (IsHeader(child.Cells[c]))
            {
                rowHeader ??= c;
            }
            else
            {
                nonHeader.Add(c);
            }
        }

        List<int> withItem = nonHeader.Where(c => child.Cells[c].HasGridItem).ToList();
        List<int> data = withItem.Count > 0 ? withItem : nonHeader;
        List<string> texts = data.Select(c => TextOf(child.Cells[c])).ToList();
        return new GridRowRead(index, child.Name, rowHeader, data, texts);
    }

    // The value; for a grid item that has none, empty (its name describes the cell); for any other cell, its name.
    private static string TextOf(GridCellInfo cell) => cell.Value ?? (cell.HasGridItem ? string.Empty : cell.Name);
}

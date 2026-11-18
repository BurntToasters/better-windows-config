using Bwc.UI.Icons;
using Windows.Foundation;

namespace Bwc.UI.Console.List;

/// <summary>
/// The four view modes of the classic list view. Switching at run time keeps the selection and the
/// focus.
/// </summary>
public enum ConsoleListViewMode
{
    /// <summary>32 DIP icons with a label below, flowing left to right and wrapping.</summary>
    LargeIcons,

    /// <summary>16 DIP icons with a label at the right, flowing left to right and wrapping.</summary>
    SmallIcons,

    /// <summary>16 DIP icons with a label at the right, flowing top to bottom and wrapping to the next column.</summary>
    List,

    /// <summary>One row per item with the column header and a cell per visible column.</summary>
    Detail,
}

/// <summary>The sort mark of a column.</summary>
public enum ConsoleListSortDirection
{
    /// <summary>Not sorted: the caller's order.</summary>
    None,

    /// <summary>Ascending by the column comparer.</summary>
    Ascending,

    /// <summary>Descending by the column comparer.</summary>
    Descending,
}

/// <summary>Horizontal alignment of a column's header and cells.</summary>
public enum ConsoleListAlignment
{
    /// <summary>Text starts at the left edge of the cell.</summary>
    Left,

    /// <summary>Text is centered.</summary>
    Center,

    /// <summary>Text ends at the right edge of the cell.</summary>
    Right,
}

/// <summary>What a context request was made on.</summary>
public enum ConsoleListContextArea
{
    /// <summary>An item.</summary>
    Item,

    /// <summary>The empty area of the list (or the list with the keyboard focus on no item).</summary>
    Background,

    /// <summary>A column header.</summary>
    Header,
}

/// <summary>
/// One column. The caller defines it; the list resizes and sorts it. <see cref="Id"/> is stable and is what the
/// persisted state names. A row's cell for this column is the cell at the column's position in the definition order,
/// which does not change when the user or the caller reorders the columns.
/// </summary>
public sealed class ConsoleListColumn
{
    private double _width;

    public ConsoleListColumn(
        string id,
        string header,
        double width = 120,
        ConsoleListAlignment alignment = ConsoleListAlignment.Left,
        bool visible = true,
        IComparer<ConsoleListRow>? comparer = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(header);
        Id = id;
        Header = header;
        _width = ConsoleList.ClampWidth(width);
        Alignment = alignment;
        Visible = visible;
        Comparer = comparer;
    }

    /// <summary>The stable identifier, unique in a list.</summary>
    public string Id { get; }

    /// <summary>The header text.</summary>
    public string Header { get; }

    /// <summary>Width in DIPs. Changed by the list (drag, auto-fit, state) and clamped to its limits.</summary>
    public double Width => _width;

    /// <summary>Alignment of the header and the cells.</summary>
    public ConsoleListAlignment Alignment { get; }

    /// <summary>False when the column is hidden.</summary>
    public bool Visible { get; internal set; }

    /// <summary>
    /// The caller's comparer for this column, or null for the default: the cell text with the native collation at the
    /// user locale, ignoring case. A descending sort is a stable descending sort by the same comparer, so rows with an
    /// equal key keep the caller's order in both directions.
    /// </summary>
    public IComparer<ConsoleListRow>? Comparer { get; }

    /// <summary>The position of the column in the definition order, the index of its cell in a row.</summary>
    public int Index { get; internal set; }

    internal void SetWidth(double width) => _width = width;
}

/// <summary>
/// One row. The cells are the text per column in the definition order of the columns; a missing or null cell is empty.
/// The first cell is the label of the row (the UIA name, the text of the icon views, the type-ahead text). A row belongs
/// to one list at a time.
/// </summary>
public sealed class ConsoleListRow
{
    private readonly string[] _cells;

    public ConsoleListRow(IEnumerable<string?> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        _cells = cells.Select(cell => cell ?? string.Empty).ToArray();
    }

    public ConsoleListRow(params string?[] cells)
        : this((IEnumerable<string?>)cells)
    {
    }

    /// <summary>The cell texts in column definition order.</summary>
    public IReadOnlyList<string> Cells => _cells;

    /// <summary>The label: the first cell, or empty.</summary>
    public string Text => _cells.Length > 0 ? _cells[0] : string.Empty;

    /// <summary>The 16 DIP icon of Small Icons, List and Detail. Null shows no icon.</summary>
    public IconReference? SmallIcon { get; init; }

    /// <summary>The 32 DIP icon of Large Icons. Null uses <see cref="SmallIcon"/> enlarged by the provider.</summary>
    public IconReference? LargeIcon { get; init; }

    /// <summary>The caller's object for this row. Equal tags re-select a row after the rows are replaced.</summary>
    public object? Tag { get; init; }

    /// <summary>The position in the list as displayed, or -1 when the row is not in a list.</summary>
    internal int Index { get; set; } = -1;

    /// <summary>The text of one cell by definition index; empty when the row has fewer cells.</summary>
    public string CellText(int column) => column >= 0 && column < _cells.Length ? _cells[column] : string.Empty;
}

/// <summary>The width, visibility and order of one column, for the caller to persist.</summary>
/// <param name="Id">The column id.</param>
/// <param name="Width">The width in DIPs.</param>
/// <param name="Visible">False when hidden.</param>
public sealed record ConsoleListColumnState(string Id, double Width, bool Visible);

/// <summary>Everything a caller persists about a list: the columns in display order, the sort and the view mode.</summary>
/// <param name="Columns">All columns (hidden ones included) in display order.</param>
/// <param name="SortColumnId">The sorted column, or null.</param>
/// <param name="SortDirection">The direction of the sort.</param>
/// <param name="ViewMode">The view mode.</param>
public sealed record ConsoleListState(
    IReadOnlyList<ConsoleListColumnState> Columns,
    string? SortColumnId,
    ConsoleListSortDirection SortDirection,
    ConsoleListViewMode ViewMode);

/// <summary>An item was activated (Enter, double click or UI Automation Invoke).</summary>
public sealed class ConsoleListItemEventArgs : EventArgs
{
    public ConsoleListItemEventArgs(ConsoleListRow row, int index)
    {
        Row = row;
        Index = index;
    }

    /// <summary>The activated row.</summary>
    public ConsoleListRow Row { get; }

    /// <summary>The position of the row as displayed.</summary>
    public int Index { get; }
}

/// <summary>
/// A context menu was asked for (right click, Shift+F10 or the context-menu key). The caller shows its
/// menu.
/// </summary>
public sealed class ConsoleListContextEventArgs : EventArgs
{
    public ConsoleListContextEventArgs(ConsoleListContextArea area, ConsoleListRow? row, string? columnId, Point? position, bool fromKeyboard)
    {
        Area = area;
        Row = row;
        ColumnId = columnId;
        Position = position;
        FromKeyboard = fromKeyboard;
    }

    /// <summary>What was hit.</summary>
    public ConsoleListContextArea Area { get; }

    /// <summary>The row for <see cref="ConsoleListContextArea.Item"/>, otherwise null.</summary>
    public ConsoleListRow? Row { get; }

    /// <summary>The column id for <see cref="ConsoleListContextArea.Header"/>, otherwise null.</summary>
    public string? ColumnId { get; }

    /// <summary>The point in the coordinates of the list for a pointer request, null for a key request.</summary>
    public Point? Position { get; }

    /// <summary>True for Shift+F10 and the context-menu key.</summary>
    public bool FromKeyboard { get; }
}

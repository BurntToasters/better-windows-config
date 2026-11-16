using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace Bwc.UI.List;

/// <summary>
/// UIA peer for the details list. The list is a Table: RowCount is the number of values, ColumnCount
/// is the number of columns, and GetColumnHeaders returns the header items in column order. The Grid
/// pattern is the same object, so a client that reads only Grid sees the same counts. Rows are not
/// realized until they scroll into view, so GetItem returns the list itself for a distant row.
/// </summary>
internal sealed class DetailsListAutomationPeer : FrameworkElementAutomationPeer, ITableProvider, IGridProvider
{
    private readonly DetailsList _list;

    public DetailsListAutomationPeer(DetailsList list)
        : base(list)
    {
        _list = list;
    }

    public int RowCount => _list.Count;

    public int ColumnCount => _list.Columns.Count;

    public RowOrColumnMajor RowOrColumnMajor => RowOrColumnMajor.RowMajor;

    /// <summary>
    /// The cell at a grid position. A realized row gives its cell text (Name, Type, or Data for the column), so the
    /// name cell carries the value name and the data cell carries the data. A row that is not realized gives the list.
    /// </summary>
    public IRawElementProviderSimple GetItem(int row, int column)
    {
        DetailsRowElement? element = _list.RealizedRow(row);
        if (element is null || column < 0 || column >= ColumnCount)
        {
            return ProviderFromPeer(this);
        }

        return ProviderFromPeer(new DetailsCellAutomationPeer(this, row, column, CellText(element.Row, column)));
    }

    /// <summary>The Grid provider of this list, for the ContainingGrid of a cell.</summary>
    internal IRawElementProviderSimple GridProvider() => ProviderFromPeer(this);

    private static string CellText(DetailsRow? row, int column) => row is null
        ? string.Empty
        : column switch
        {
            0 => row.Name,
            1 => row.Type,
            _ => row.Data,
        };

    public IRawElementProviderSimple[] GetColumnHeaders()
    {
        var headers = new IRawElementProviderSimple[ColumnCount];
        for (var column = 0; column < headers.Length; column++)
        {
            HeaderTitle? title = _list.HeaderTitleAt(column);
            headers[column] = title is null ? ProviderFromPeer(this) : ProviderFor(title);
        }

        return headers;
    }

    public IRawElementProviderSimple[] GetRowHeaders() => Array.Empty<IRawElementProviderSimple>();

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Table;

    protected override string GetClassNameCore() => "DetailsList";

    protected override bool IsControlElementCore() => true;

    protected override bool IsContentElementCore() => true;

    protected override object? GetPatternCore(PatternInterface patternInterface)
    {
        return patternInterface switch
        {
            PatternInterface.Table => this,
            PatternInterface.Grid => this,
            _ => base.GetPatternCore(patternInterface),
        };
    }

    /// <summary>The element's peer as a provider. Falls back to the list when the element has no peer.</summary>
    private IRawElementProviderSimple ProviderFor(FrameworkElement element)
    {
        AutomationPeer? peer = FrameworkElementAutomationPeer.FromElement(element)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(element);
        return peer is null ? ProviderFromPeer(this) : ProviderFromPeer(peer);
    }
}

/// <summary>
/// UIA peer for one cell of a details row. A cell is a DataItem with the Value and GridItem patterns: its Name and
/// Value are the cell text, and Row and Column are its grid position. The cell is not in the visual tree and is
/// read only.
/// </summary>
internal sealed class DetailsCellAutomationPeer : AutomationPeer, IValueProvider, IGridItemProvider
{
    private readonly DetailsListAutomationPeer _grid;
    private readonly string _text;

    public DetailsCellAutomationPeer(DetailsListAutomationPeer grid, int row, int column, string text)
    {
        _grid = grid;
        _text = text;
        Row = row;
        Column = column;
    }

    public string Value => _text;

    public bool IsReadOnly => true;

    public int Row { get; }

    public int Column { get; }

    public int RowSpan => 1;

    public int ColumnSpan => 1;

    public IRawElementProviderSimple ContainingGrid => _grid.GridProvider();

    /// <summary>A details cell is read only, so the value cannot be set through UI Automation.</summary>
    public void SetValue(string value)
    {
    }

    protected override string GetClassNameCore() => "DetailsListCell";

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.DataItem;

    protected override string GetNameCore() => _text;

    protected override object? GetPatternCore(PatternInterface patternInterface) => patternInterface switch
    {
        PatternInterface.Value => this,
        PatternInterface.GridItem => this,
        _ => null,
    };
}

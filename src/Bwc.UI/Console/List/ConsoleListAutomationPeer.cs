using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.UI.Console.List;

/// <summary>
/// UIA peer of the console list. The probed classic list is a List with the Scroll, Grid and Table patterns; this one
/// adds Selection (multiple). RowCount is the number of rows and ColumnCount the number of visible columns. Items are
/// realized only near the viewport, so GetItem returns the list itself for a distant row.
/// </summary>
internal sealed class ConsoleListAutomationPeer :
    FrameworkElementAutomationPeer,
    ITableProvider,
    IGridProvider,
    ISelectionProvider,
    IScrollProvider
{
    private const double NoScroll = -1;
    private readonly ConsoleList _list;

    public ConsoleListAutomationPeer(ConsoleList list)
        : base(list)
    {
        _list = list;
    }

    public int RowCount => _list.Count;

    public int ColumnCount => _list.VisibleColumns.Count;

    public RowOrColumnMajor RowOrColumnMajor => RowOrColumnMajor.RowMajor;

    public bool CanSelectMultiple => true;

    public bool IsSelectionRequired => false;

    public double HorizontalScrollPercent => Percent(_list.ScrollHost.HorizontalOffset, _list.ScrollHost.ScrollableWidth);

    public double VerticalScrollPercent => Percent(_list.ScrollHost.VerticalOffset, _list.ScrollHost.ScrollableHeight);

    public double HorizontalViewSize => ViewSize(_list.ScrollHost.ViewportWidth, _list.ScrollHost.ExtentWidth);

    public double VerticalViewSize => ViewSize(_list.ScrollHost.ViewportHeight, _list.ScrollHost.ExtentHeight);

    public bool HorizontallyScrollable => _list.ScrollHost.ScrollableWidth > 0;

    public bool VerticallyScrollable => _list.ScrollHost.ScrollableHeight > 0;

    public IRawElementProviderSimple GetItem(int row, int column)
    {
        ConsoleListItemElement? element = _list.RealizedItem(row);
        if (element?.Row is null || column < 0 || column >= ColumnCount)
        {
            return ProviderFromPeer(this);
        }

        string text = element.Row.CellText(_list.VisibleColumns[column].Index);
        return ProviderFromPeer(new ConsoleListCellAutomationPeer(this, row, column, text));
    }

    internal IRawElementProviderSimple GridProvider() => ProviderFromPeer(this);

    public IRawElementProviderSimple[] GetColumnHeaders()
    {
        var headers = new IRawElementProviderSimple[ColumnCount];
        for (int column = 0; column < headers.Length; column++)
        {
            ConsoleListHeaderItem? title = _list.HeaderPanel.ItemAt(column);
            headers[column] = title is null ? ProviderFromPeer(this) : ProviderFor(title);
        }

        return headers;
    }

    public IRawElementProviderSimple[] GetRowHeaders() => [];

    public IRawElementProviderSimple[] GetSelection()
    {
        var selected = new List<IRawElementProviderSimple>();
        foreach (ConsoleListItemElement item in _list.RealizedItems)
        {
            if (_list.IsRowSelected(item.Index))
            {
                selected.Add(ProviderFor(item));
            }
        }

        return [.. selected];
    }

    public void Scroll(ScrollAmount horizontalAmount, ScrollAmount verticalAmount)
    {
        ScrollViewer host = _list.ScrollHost;
        double x = host.HorizontalOffset + Delta(horizontalAmount, host.ViewportWidth);
        double y = host.VerticalOffset + Delta(verticalAmount, host.ViewportHeight);
        _ = host.ChangeView(Math.Clamp(x, 0, host.ScrollableWidth), Math.Clamp(y, 0, host.ScrollableHeight), null, disableAnimation: true);
    }

    public void SetScrollPercent(double horizontalPercent, double verticalPercent)
    {
        ScrollViewer host = _list.ScrollHost;
        double? x = horizontalPercent < 0 ? null : Math.Clamp(horizontalPercent, 0, 100) / 100 * host.ScrollableWidth;
        double? y = verticalPercent < 0 ? null : Math.Clamp(verticalPercent, 0, 100) / 100 * host.ScrollableHeight;
        _ = host.ChangeView(x, y, null, disableAnimation: true);
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

    protected override string GetClassNameCore() => "ConsoleList";

    protected override bool IsControlElementCore() => true;

    protected override bool IsContentElementCore() => true;

    protected override object? GetPatternCore(PatternInterface patternInterface) => patternInterface switch
    {
        PatternInterface.Table => this,
        PatternInterface.Grid => this,
        PatternInterface.Selection => this,
        PatternInterface.Scroll => this,
        _ => base.GetPatternCore(patternInterface),
    };

    private static double Percent(double offset, double scrollable) => scrollable > 0 ? offset / scrollable * 100 : NoScroll;

    private static double ViewSize(double viewport, double extent) => extent > 0 ? Math.Min(100, viewport / extent * 100) : 100;

    private static double Delta(ScrollAmount amount, double viewport) => amount switch
    {
        ScrollAmount.LargeDecrement => -viewport,
        ScrollAmount.SmallDecrement => -ConsoleListMetrics.RowHeight,
        ScrollAmount.LargeIncrement => viewport,
        ScrollAmount.SmallIncrement => ConsoleListMetrics.RowHeight,
        _ => 0,
    };

    private IRawElementProviderSimple ProviderFor(FrameworkElement element)
    {
        AutomationPeer? peer = FrameworkElementAutomationPeer.FromElement(element)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(element);
        return peer is null ? ProviderFromPeer(this) : ProviderFromPeer(peer);
    }
}

/// <summary>
/// UIA peer of one cell of an item: a DataItem with the Value and GridItem patterns whose name and value are the cell
/// text. It is not in the visual tree and is read only.
/// </summary>
internal sealed class ConsoleListCellAutomationPeer : AutomationPeer, IValueProvider, IGridItemProvider
{
    private readonly ConsoleListAutomationPeer _grid;
    private readonly string _text;

    public ConsoleListCellAutomationPeer(ConsoleListAutomationPeer grid, int row, int column, string text)
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

    public void SetValue(string value)
    {
    }

    protected override string GetClassNameCore() => "ConsoleListCell";

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.DataItem;

    protected override string GetNameCore() => _text;

    protected override object? GetPatternCore(PatternInterface patternInterface) => patternInterface switch
    {
        PatternInterface.Value => this,
        PatternInterface.GridItem => this,
        _ => null,
    };
}

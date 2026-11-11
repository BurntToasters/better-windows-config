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

    public IRawElementProviderSimple GetItem(int row, int column)
    {
        DetailsRowElement? element = _list.RealizedRow(row);
        if (element is null || column < 0 || column >= ColumnCount)
        {
            return ProviderFromPeer(this);
        }

        return ProviderFor(element);
    }

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

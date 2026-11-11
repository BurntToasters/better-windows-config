using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Automation.Text;
using Windows.Foundation;

namespace Bwc.UI.Hex;

/// <summary>
/// UIA peer for the hex editor. The grid has one row per eight bytes and eighteen columns: the
/// offset, eight byte columns, and the ASCII column. Only realized rows have row peers; asking for
/// a distant row returns the editor instead of forcing the virtualized row into existence.
/// </summary>
internal sealed class HexEditorAutomationPeer : FrameworkElementAutomationPeer, IGridProvider
{
    private readonly HexEditor _editor;

    public HexEditorAutomationPeer(HexEditor editor)
        : base(editor)
    {
        _editor = editor;
    }

    public int ColumnCount => _editor.GridColumnCountValue;

    public int RowCount => _editor.RowCount;

    public IRawElementProviderSimple GetItem(int row, int column)
    {
        if (row < 0 || row >= _editor.RowCount)
        {
            return ProviderFromPeer(this);
        }

        HexRowElement? element = _editor.TryGetRowElement(row);
        if (element is null)
        {
            return ProviderFromPeer(this);
        }

        AutomationPeer? peer = FrameworkElementAutomationPeer.FromElement(element)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(element);
        return peer is null ? ProviderFromPeer(this) : ProviderFromPeer(peer);
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;

    protected override string GetClassNameCore() => "HexEditor";

    protected override string GetNameCore() => "Hex editor";

    protected override string GetAutomationIdCore() => "HexEditor";

    protected override bool IsControlElementCore() => true;

    protected override bool IsContentElementCore() => true;

    protected override object? GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Grid ? this : base.GetPatternCore(patternInterface);
}

/// <summary>
/// UIA peer for one realized hex row. The row exposes the full row text through its name and a row
/// level text pattern so a client can read the offset, hex bytes, and ASCII without walking cells.
/// </summary>
internal sealed class HexRowAutomationPeer :
    FrameworkElementAutomationPeer,
    IGridItemProvider,
    ISelectionItemProvider,
    IScrollItemProvider,
    ITextProvider
{
    private readonly HexRowElement _row;

    public HexRowAutomationPeer(HexRowElement row)
        : base(row)
    {
        _row = row;
    }

    public bool IsSelected => _row.Owner?.IsRowSelected(_row.Index) == true;

    public IRawElementProviderSimple SelectionContainer => OwnerProvider();

    public int Column => 0;

    public int ColumnSpan => _row.Owner?.GridColumnCountValue ?? HexEditor.BytesPerRow + 2;

    public int Row => _row.Index;

    public int RowSpan => 1;

    public IRawElementProviderSimple ContainingGrid => OwnerProvider();

    public ITextRangeProvider DocumentRange => new HexRowTextRange(this, _row.RowText, 0, _row.RowText.Length);

    public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;

    internal IRawElementProviderSimple GetProvider() => ProviderFromPeer(this);

    internal void ScrollIntoViewCore() => _row.Owner?.RevealRow(_row.Index);

    public void Select() => _row.Owner?.SelectRow(_row.Index);

    public void AddToSelection() => _row.Owner?.SelectRow(_row.Index);

    public void RemoveFromSelection() => _row.Owner?.ClearSelection();

    public void ScrollIntoView() => _row.Owner?.RevealRow(_row.Index);

    public ITextRangeProvider[] GetSelection()
    {
        if (!IsSelected || _row.RowText.Length == 0)
        {
            return Array.Empty<ITextRangeProvider>();
        }

        return new ITextRangeProvider[] { DocumentRange };
    }

    public ITextRangeProvider[] GetVisibleRanges() =>
        _row.RowText.Length == 0
            ? Array.Empty<ITextRangeProvider>()
            : new ITextRangeProvider[] { DocumentRange };

    public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement) => null!;

    public ITextRangeProvider RangeFromPoint(Point screenLocation) => DocumentRange;

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.DataItem;

    protected override string GetClassNameCore() => "HexRow";

    protected override string GetNameCore() => _row.RowText;

    protected override string GetAutomationIdCore() =>
        _row.Index >= 0 ? "HexRow_" + _row.Index.ToString(System.Globalization.CultureInfo.InvariantCulture) : "HexRow";

    protected override bool IsControlElementCore() => true;

    protected override bool IsContentElementCore() => true;

    protected override void SetFocusCore() => _row.Owner?.FocusEditor();

    protected override object? GetPatternCore(PatternInterface patternInterface) => patternInterface switch
    {
        PatternInterface.GridItem => this,
        PatternInterface.SelectionItem => this,
        PatternInterface.ScrollItem => this,
        PatternInterface.Text => this,
        _ => base.GetPatternCore(patternInterface),
    };

    private IRawElementProviderSimple OwnerProvider()
    {
        if (_row.Owner is not { } owner)
        {
            return null!;
        }

        AutomationPeer? peer = FrameworkElementAutomationPeer.FromElement(owner)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(owner);
        return peer is null ? null! : ProviderFromPeer(peer);
    }
}

/// <summary>
/// Minimal text range for one row. The viewer is read only, so the mutating operations are no-ops.
/// The range is enough for a client to read or find text in a row without materializing the value.
/// </summary>
internal sealed class HexRowTextRange : ITextRangeProvider
{
    private readonly HexRowAutomationPeer _peer;
    private readonly string _text;
    private int _start;
    private int _end;

    public HexRowTextRange(HexRowAutomationPeer peer, string text, int start, int end)
    {
        _peer = peer;
        _text = text;
        _start = Math.Clamp(start, 0, text.Length);
        _end = Math.Clamp(end, _start, text.Length);
    }

    public ITextRangeProvider Clone() => new HexRowTextRange(_peer, _text, _start, _end);

    public bool Compare(ITextRangeProvider textRangeProvider) =>
        textRangeProvider is HexRowTextRange other &&
        ReferenceEquals(_peer, other._peer) &&
        _start == other._start &&
        _end == other._end;

    public int CompareEndpoints(
        TextPatternRangeEndpoint endpoint,
        ITextRangeProvider textRangeProvider,
        TextPatternRangeEndpoint targetEndpoint)
    {
        if (textRangeProvider is not HexRowTextRange other || !ReferenceEquals(_peer, other._peer))
        {
            return 0;
        }

        return Endpoint(endpoint) - other.Endpoint(targetEndpoint);
    }

    public void ExpandToEnclosingUnit(TextUnit unit)
    {
    }

    public ITextRangeProvider FindAttribute(int attributeId, object value, bool backward) => null!;

    public ITextRangeProvider FindText(string text, bool backward, bool ignoreCase)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null!;
        }

        StringComparison comparison = ignoreCase
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        int index = _text.IndexOf(text, _start, comparison);
        if (index < 0 || index + text.Length > _end)
        {
            return null!;
        }

        return new HexRowTextRange(_peer, _text, index, index + text.Length);
    }

    public object GetAttributeValue(int attributeId) => null!;

    public void GetBoundingRectangles(out double[] returnValue)
    {
        returnValue = Array.Empty<double>();
    }

    public IRawElementProviderSimple GetEnclosingElement() => _peer.GetProvider();

    public string GetText(int maxLength)
    {
        int length = _end - _start;
        if (maxLength >= 0)
        {
            length = Math.Min(length, maxLength);
        }

        return _text.Substring(_start, length);
    }

    public int Move(TextUnit unit, int count) => 0;

    public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count) => 0;

    public void MoveEndpointByRange(
        TextPatternRangeEndpoint endpoint,
        ITextRangeProvider textRangeProvider,
        TextPatternRangeEndpoint targetEndpoint)
    {
        if (textRangeProvider is not HexRowTextRange other || !ReferenceEquals(_peer, other._peer))
        {
            return;
        }

        SetEndpoint(endpoint, other.Endpoint(targetEndpoint));
    }

    public void Select()
    {
    }

    public void AddToSelection()
    {
    }

    public void RemoveFromSelection()
    {
    }

    public void ScrollIntoView(bool alignToTop) => _peer.ScrollIntoViewCore();

    public IRawElementProviderSimple[] GetChildren() => Array.Empty<IRawElementProviderSimple>();

    private int Endpoint(TextPatternRangeEndpoint endpoint) =>
        endpoint == TextPatternRangeEndpoint.Start ? _start : _end;

    private void SetEndpoint(TextPatternRangeEndpoint endpoint, int value)
    {
        int clamped = Math.Clamp(value, 0, _text.Length);
        if (endpoint == TextPatternRangeEndpoint.Start)
        {
            _start = Math.Min(clamped, _end);
        }
        else
        {
            _end = Math.Max(clamped, _start);
        }
    }
}

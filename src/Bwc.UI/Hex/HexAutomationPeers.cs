using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Automation.Text;
using Windows.Foundation;

namespace Bwc.UI.Hex;

/// <summary>
/// UIA peer for the hex editor. The grid has one row per eight bytes and ten columns: the offset (column 0), eight byte
/// cells (columns 1 to 8), and the ASCII cell (column 9). GetItem returns a cell for any row, realized or not, so a
/// byte cell reads its own value. Value is the whole content as hex text, separated by spaces.
/// </summary>
internal sealed class HexEditorAutomationPeer : FrameworkElementAutomationPeer, IGridProvider, IValueProvider
{
    private const int AsciiColumn = HexEditor.BytesPerRow + 1;

    private readonly HexEditor _editor;

    public HexEditorAutomationPeer(HexEditor editor)
        : base(editor)
    {
        _editor = editor;
    }

    public int ColumnCount => _editor.GridColumnCountValue;

    public int RowCount => _editor.RowCount;

    public string Value => string.Join(' ', _editor.Data.ToArray().Select(value => value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));

    public bool IsReadOnly => _editor.IsReadOnly;

    /// <summary>The hex editor is changed through its keyboard and menus, not through a value setter.</summary>
    public void SetValue(string value)
    {
    }

    public IRawElementProviderSimple GetItem(int row, int column)
    {
        if (row < 0 || row >= _editor.RowCount || column < 0 || column >= ColumnCount)
        {
            return ProviderFromPeer(this);
        }

        return ProviderFromPeer(new HexCellAutomationPeer(this, row, column, CellText(row, column)));
    }

    /// <summary>The Grid provider of this editor, for the ContainingGrid of a cell.</summary>
    internal IRawElementProviderSimple GridProvider() => ProviderFromPeer(this);

    /// <summary>The text of one cell: the offset, a hex byte (blank past the row's end), or the row's ASCII text.</summary>
    private string CellText(int row, int column)
    {
        int offset = row * HexEditor.BytesPerRow;
        if (column == 0)
        {
            return offset.ToString("X" + _editor.OffsetDigits.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
        }

        ReadOnlySpan<byte> data = _editor.Data.Span;
        int count = _editor.RowByteCount(row);
        if (column == AsciiColumn)
        {
            var ascii = new System.Text.StringBuilder(count);
            for (int index = 0; index < count; index++)
            {
                byte value = data[offset + index];
                ascii.Append(value is >= 32 and <= 126 ? (char)value : '.');
            }

            return ascii.ToString();
        }

        int byteIndex = column - 1;
        return byteIndex < count
            ? data[offset + byteIndex].ToString("X2", System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;

    protected override string GetClassNameCore() => "HexEditor";

    protected override string GetNameCore() => BwcStrings.Get("HexEditorName");

    protected override string GetAutomationIdCore() => "HexEditor";

    protected override bool IsControlElementCore() => true;

    protected override bool IsContentElementCore() => true;

    protected override object? GetPatternCore(PatternInterface patternInterface) => patternInterface switch
    {
        PatternInterface.Grid => this,
        PatternInterface.Value => this,
        _ => base.GetPatternCore(patternInterface),
    };
}

/// <summary>
/// UIA peer for one hex cell: an offset, a byte, or the ASCII text of a row. The cell is a DataItem with the Value and
/// GridItem patterns. Its Name and Value are the cell text, and it is read only.
/// </summary>
internal sealed class HexCellAutomationPeer : AutomationPeer, IValueProvider, IGridItemProvider
{
    private readonly HexEditorAutomationPeer _grid;
    private readonly string _text;

    public HexCellAutomationPeer(HexEditorAutomationPeer grid, int row, int column, string text)
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

    /// <summary>A cell is read only, so its value cannot be set through UI Automation.</summary>
    public void SetValue(string value)
    {
    }

    protected override string GetClassNameCore() => "HexCell";

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.DataItem;

    protected override string GetNameCore() => _text;

    protected override object? GetPatternCore(PatternInterface patternInterface) => patternInterface switch
    {
        PatternInterface.Value => this,
        PatternInterface.GridItem => this,
        _ => null,
    };
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

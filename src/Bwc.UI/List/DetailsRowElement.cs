using Bwc.UI.Theme;
using Bwc.UI.Tree;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Bwc.UI.List;

/// <summary>
/// One realized details row. It is the ItemsRepeater element, so the height is exactly the dense
/// list row height and columns are placed directly from the shared column widths.
/// </summary>
public sealed class DetailsRowElement : Panel
{
    // The value icon sits 2 DIP from the left edge and the name starts at 18 DIP, measured on
    // classic regedit at 200 percent (docs/parity/oracle/regedit-row-metrics.json).
    private const double IconLeft = 2;
    private const double NameLeft = 18;

    private TextBlock[] _cells = [];
    private int _index = -1;
    private bool _selected;
    private bool _focused;
    private double _rowHeight = -1;
    private double _fontSize = -1;
    private readonly Rectangle _focusRect;
    private readonly Image _icon;
    private readonly Rectangle _selectionBorder;

    public DetailsRowElement()
    {
        Background = DenseTheme.Transparent;
        // The focus rectangle belongs to the focused row, which may be unselected. The list sets
        // the state on each row; the stroke is the transparent brush unless this row has focus.
        _focusRect = BwcFocusRect.Create(DenseTheme.Transparent);
        _icon = new Image
        {
            Width = Icons.IconProvider.IconDip,
            Height = Icons.IconProvider.IconDip,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
        };
        _selectionBorder = BwcFocusRect.CreateBorder(DenseTheme.ListSelectionBorder);
        PointerPressed += OnPointerPressed;
        DoubleTapped += OnDoubleTapped;
        RightTapped += OnRightTapped;
        Loaded += (_, _) => ApplyIcon();
    }

    internal DetailsList? Owner { get; private set; }

    internal DetailsRow? Row { get; private set; }

    internal int Index => _index;

    internal void Attach(DetailsList owner, int index, DetailsRow row, bool selected, bool focused)
    {
        Owner = owner;
        _index = index;
        Row = row;

        if (_cells.Length != owner.Columns.Count)
        {
            BuildCells(owner.Columns.Count);
        }

        ApplyMetricsCore();

        for (var i = 0; i < _cells.Length && i < owner.Columns.Count; i++)
        {
            string text = i switch
            {
                0 => row.Name,
                1 => row.Type,
                _ => row.Data,
            };

            if (!string.Equals(_cells[i].Text, text, StringComparison.Ordinal))
            {
                _cells[i].Text = text;
            }
        }

        ApplyIcon();
        _selected = selected;
        _focused = focused;
        ApplyTheme();
    }

    internal void Detach()
    {
        Owner = null;
        Row = null;
    }

    internal void ApplyMetrics()
    {
        ApplyMetricsCore();
        InvalidateMeasure();
        InvalidateArrange();
    }

    internal void ApplyTheme()
    {
        Background = _selected ? DenseTheme.ListSelectionBackground : DenseTheme.Transparent;
        Brush foreground = _selected ? DenseTheme.ListSelectionForeground : DenseTheme.Text;
        foreach (var cell in _cells)
        {
            cell.Foreground = foreground;
        }

        // A focused selected row draws the rectangle in the selection text color, as classic does.
        // A focused unselected row draws it in the window text color.
        _focusRect.Stroke = _focused
            ? (_selected ? DenseTheme.ListSelectionForeground : DenseTheme.Text)
            : DenseTheme.Transparent;
        _selectionBorder.Stroke = _selected ? DenseTheme.ListSelectionBorder : DenseTheme.Transparent;
    }

    /// <summary>Applies the selection and focus state. Only a change re-applies the brushes.</summary>
    internal void SetState(bool selected, bool focused)
    {
        if (_selected == selected && _focused == focused)
        {
            return;
        }

        _selected = selected;
        _focused = focused;
        ApplyTheme();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double height = DenseMetrics.ListRowHeight;
        if (Owner is null)
        {
            return new Size(0, height);
        }

        double total = Owner.ColumnsTotalWidth;
        _icon.Measure(new Size(Icons.IconProvider.IconDip, Icons.IconProvider.IconDip));
        foreach (var column in Owner.Columns)
        {
            int index = IndexOf(column);
            double columnWidth = column.Width;
            double inset = index == 0 ? NameLeft : 4;
            _cells[index].Measure(new Size(Math.Max(0, columnWidth - inset - 4), height));
        }

        double width = double.IsInfinity(availableSize.Width) ? total : Math.Max(total, availableSize.Width);
        BwcFocusRect.Measure(_selectionBorder, this, new Size(width, height));
        BwcFocusRect.Measure(_focusRect, this, new Size(width, height));
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double height = Math.Min(finalSize.Height, DenseMetrics.ListRowHeight);
        if (Owner is null)
        {
            return finalSize;
        }

        double total = Owner.ColumnsTotalWidth;
        double extra = Math.Max(0, finalSize.Width - total);
        double iconSize = Icons.IconProvider.IconDip;
        _icon.Arrange(new Rect(IconLeft, (height - iconSize) / 2, iconSize, iconSize));

        double left = 0;
        for (var i = 0; i < Owner.Columns.Count; i++)
        {
            double width = Owner.Columns[i].Width;
            if (i == Owner.Columns.Count - 1)
            {
                width += extra;
            }

            double inset = i == 0 ? NameLeft : 4;
            double textHeight = Math.Min(height, _cells[i].DesiredSize.Height);
            _cells[i].Arrange(new Rect(left + inset, (height - textHeight) / 2, Math.Max(0, width - inset - 4), textHeight));
            left += Owner.Columns[i].Width;
        }

        BwcFocusRect.Arrange(_selectionBorder, this, new Size(finalSize.Width, height));
        BwcFocusRect.Arrange(_focusRect, this, new Size(finalSize.Width, height));
        return finalSize;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new DetailsRowAutomationPeer(this);

    private int IndexOf(DetailsColumn column)
    {
        if (Owner is null)
        {
            return 0;
        }

        for (var i = 0; i < Owner.Columns.Count; i++)
        {
            if (ReferenceEquals(Owner.Columns[i], column))
            {
                return i;
            }
        }

        return 0;
    }

    private void BuildCells(int count)
    {
        Children.Clear();
        _cells = new TextBlock[count];
        for (var i = 0; i < count; i++)
        {
            var cell = new TextBlock
            {
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false,
                FontFamily = DenseMetrics.FontFamily,
                FontSize = DenseMetrics.FontSize,
                Foreground = DenseTheme.Text,
            };
            _cells[i] = cell;
            Children.Add(cell);
        }

        // Clear removed the overlays and the icon too, so add them back after the cells.
        Children.Add(_icon);
        Children.Add(_selectionBorder);
        Children.Add(_focusRect);
    }

    /// <summary>The value icon follows the row's registry type and the window's pixel scale.</summary>
    private void ApplyIcon()
    {
        if (Row is null)
        {
            return;
        }

        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        _icon.Source = Icons.IconProvider.Get(Icons.IconProvider.ValueIconKind(Row.Type), scale);
    }

    private void ApplyMetricsCore()
    {
        double height = DenseMetrics.ListRowHeight;
        double fontSize = DenseMetrics.FontSize;
        bool fontChanged = fontSize != _fontSize;
        bool heightChanged = height != _rowHeight;
        _fontSize = fontSize;
        _rowHeight = height;

        if (heightChanged)
        {
            Height = height;
        }

        if (fontChanged)
        {
            foreach (var cell in _cells)
            {
                cell.FontSize = fontSize;
                cell.FontFamily = DenseMetrics.FontFamily;
            }
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Owner?.RowPressed(this, e);
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        Owner?.RowDoubleTapped(this, e);
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        Owner?.RowRightTapped(this, e);
        e.Handled = true;
    }
}

/// <summary>
/// UIA peer for one details row. The index is read from the element, which ItemsRepeater updates
/// when the element is recycled, so a sort or scroll cannot leave a stale row number.
/// </summary>
internal sealed class DetailsRowAutomationPeer :
    FrameworkElementAutomationPeer,
    ISelectionItemProvider,
    IScrollItemProvider,
    IGridItemProvider
{
    private readonly DetailsRowElement _row;

    public DetailsRowAutomationPeer(DetailsRowElement row)
        : base(row)
    {
        _row = row;
    }

    public bool IsSelected => _row.Owner?.IsRowSelected(_row.Index) == true;

    public IRawElementProviderSimple SelectionContainer => OwnerProvider();

    public int Column => 0;

    public int ColumnSpan => _row.Owner?.Columns.Count ?? 0;

    public int Row => _row.Index;

    public int RowSpan => 1;

    public IRawElementProviderSimple ContainingGrid => OwnerProvider();

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.DataItem;

    protected override string GetClassNameCore() => "DetailsListRow";

    protected override string GetNameCore() => _row.Row?.Name ?? string.Empty;

    protected override string GetAutomationIdCore()
    {
        return _row.Index >= 0 ? $"ListRow_{_row.Index}" : "ListRow";
    }

    protected override bool IsControlElementCore() => true;

    protected override bool IsContentElementCore() => true;

    protected override object? GetPatternCore(PatternInterface patternInterface)
    {
        return patternInterface switch
        {
            PatternInterface.SelectionItem => this,
            PatternInterface.ScrollItem => this,
            PatternInterface.GridItem => this,
            _ => base.GetPatternCore(patternInterface),
        };
    }

    public void Select()
    {
        _row.Owner?.SelectRow(_row.Index, focus: true);
    }

    public void AddToSelection()
    {
        _row.Owner?.AddRowToSelection(_row.Index);
    }

    public void RemoveFromSelection()
    {
        _row.Owner?.RemoveRowFromSelection(_row.Index);
    }

    public void ScrollIntoView()
    {
        _row.Owner?.RevealIndex(_row.Index);
    }

    /// <summary>The list's peer as a provider. Falls back to this row when the list is not attached.</summary>
    private IRawElementProviderSimple OwnerProvider()
    {
        DetailsList? owner = _row.Owner;
        if (owner is null)
        {
            return ProviderFromPeer(this);
        }

        AutomationPeer? peer = FrameworkElementAutomationPeer.FromElement(owner)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(owner);
        return peer is null ? ProviderFromPeer(this) : ProviderFromPeer(peer);
    }
}

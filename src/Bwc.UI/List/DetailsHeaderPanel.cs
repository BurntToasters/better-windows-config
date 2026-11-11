using Bwc.UI.Theme;
using Bwc.UI.Tree;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Bwc.UI.List;

/// <summary>
/// Column header row. Each column has a title, a 1 DIP divider, and a 4 DIP drag grip centered on
/// the divider. Header clicks do nothing, as in the classic list view, so there is no click target
/// and no sort arrow. A grip drag resizes the column, and a grip double tap auto-sizes it.
/// </summary>
public sealed class DetailsHeaderPanel : Panel
{
    private const double GripWidth = 4;

    private readonly List<HeaderCell> _cells = new();
    private DetailsList? _owner;

    public DetailsHeaderPanel()
    {
        Background = DenseTheme.HeaderBackground;
    }

    /// <summary>The header item for a column, or null before the header is built.</summary>
    internal HeaderTitle? TitleAt(int index) =>
        index >= 0 && index < _cells.Count ? _cells[index].Title : null;

    internal void Attach(DetailsList owner)
    {
        _owner = owner;
        BuildCells();
        Height = owner.HeaderHeight;
        Width = owner.ColumnsTotalWidth;
        ApplyMetrics();
    }

    internal void ApplyMetrics()
    {
        if (_owner is null)
        {
            return;
        }

        Height = _owner.HeaderHeight;
        foreach (var cell in _cells)
        {
            cell.Title.Label.FontSize = DenseMetrics.FontSize;
            cell.Title.Label.FontFamily = DenseMetrics.FontFamily;
            cell.Title.Label.LineHeight = DenseMetrics.FontSize * 4.0 / 3.0;
        }

        InvalidateMeasure();
        InvalidateArrange();
    }

    internal void NotifyColumnWidthsChanged()
    {
        if (_owner is null)
        {
            return;
        }

        Width = _owner.ColumnsTotalWidth;
        InvalidateMeasure();
        InvalidateArrange();
    }

    internal void RefreshTheme()
    {
        Background = DenseTheme.HeaderBackground;
        foreach (var cell in _cells)
        {
            cell.Grip.Background = DenseTheme.Transparent;
            cell.Title.Label.Foreground = DenseTheme.Text;
            cell.Divider.Fill = DenseTheme.Divider;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_owner is null)
        {
            return new Size(0, 0);
        }

        LayoutCells(arrange: false);
        return new Size(_owner.ColumnsTotalWidth, _owner.HeaderHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_owner is null)
        {
            return finalSize;
        }

        LayoutCells(arrange: true);
        return finalSize;
    }

    private void BuildCells()
    {
        if (_owner is null)
        {
            return;
        }

        if (_cells.Count == _owner.Columns.Count)
        {
            for (var i = 0; i < _cells.Count; i++)
            {
                var column = _owner.Columns[i];
                _cells[i].Title.Label.Text = column.Header;
                AutomationProperties.SetName(_cells[i].Title, column.Header);
                AutomationProperties.SetAutomationId(_cells[i].Title, "Header" + column.Key);
            }

            return;
        }

        Children.Clear();
        _cells.Clear();
        var resizeCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

        for (var i = 0; i < _owner.Columns.Count; i++)
        {
            var column = _owner.Columns[i];
            var index = i;

            var title = new HeaderTitle();
            title.Label.Text = column.Header;
            title.Label.FontFamily = DenseMetrics.FontFamily;
            title.Label.FontSize = DenseMetrics.FontSize;
            title.Label.Foreground = DenseTheme.Text;
            AutomationProperties.SetName(title, column.Header);
            AutomationProperties.SetAutomationId(title, "Header" + column.Key);

            var divider = new Rectangle
            {
                Width = 1,
                Fill = DenseTheme.Divider,
                IsHitTestVisible = false,
            };

            // handledEventsToo: a divider drag must start even when an ancestor has marked the press handled.
            // The list owns the drag after the press, so moves and the release are handled there.
            var grip = new GripPanel(resizeCursor) { Width = GripWidth };
            grip.AddHandler(
                UIElement.PointerPressedEvent,
                new PointerEventHandler((_, e) => _owner?.OnDividerPressed(index, e)),
                handledEventsToo: true);
            grip.DoubleTapped += (_, e) =>
            {
                _owner?.AutoSizeColumn(index);
                e.Handled = true;
            };

            _cells.Add(new HeaderCell(title, divider, grip));
        }

        for (var i = 0; i < _cells.Count; i++)
        {
            Children.Add(_cells[i].Title);
            Children.Add(_cells[i].Divider);
        }

        for (var i = 0; i < _cells.Count; i++)
        {
            Children.Add(_cells[i].Grip);
        }
    }

    private void LayoutCells(bool arrange)
    {
        if (_owner is null)
        {
            return;
        }

        double height = _owner.HeaderHeight;
        for (var i = 0; i < _cells.Count; i++)
        {
            var cell = _cells[i];
            double x = _owner.Columns[i].Width <= 0 ? 0 : ColumnLeft(i);
            double width = _owner.Columns[i].Width;
            Place(cell.Title, x, 0, width, height, arrange);
            Place(cell.Divider, x + width - 1, 3, 1, Math.Max(0, height - 6), arrange);
            Place(cell.Grip, x + width - (GripWidth / 2), 0, GripWidth, height, arrange);
        }
    }

    private double ColumnLeft(int index)
    {
        double left = 0;
        for (var i = 0; i < index; i++)
        {
            left += _owner!.Columns[i].Width;
        }

        return left;
    }

    private static void Place(UIElement element, double x, double y, double width, double height, bool arrange)
    {
        width = Math.Max(0, width);
        element.Measure(new Size(width, height));
        if (arrange)
        {
            element.Arrange(new Rect(x, y, width, height));
        }
    }

    private sealed record HeaderCell(HeaderTitle Title, Rectangle Divider, GripPanel Grip);

    /// <summary>
    /// Transparent strip over a divider. A panel subclass is needed to set ProtectedCursor. Its peer is a
    /// Separator, the UIA role of a column divider, so the strip is visible to automation.
    /// </summary>
    private sealed class GripPanel : Panel
    {
        public GripPanel(InputCursor cursor)
        {
            Background = DenseTheme.Transparent;
            ProtectedCursor = cursor;
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new HeaderGripAutomationPeer(this);
    }
}

/// <summary>
/// A column title: one text block inset by the header padding. TextBlock is sealed, so the title is a
/// panel. Its peer reports the HeaderItem control type that the Table pattern lists.
/// </summary>
internal sealed class HeaderTitle : Panel
{
    private const double Pad = 4;

    public HeaderTitle()
    {
        Label = new TextBlock
        {
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        Children.Add(Label);
    }

    public TextBlock Label { get; }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        Label.Measure(new Size(Math.Max(0, width - (Pad * 2)), availableSize.Height));
        return new Size(width, availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Label.Arrange(new Rect(Pad, 0, Math.Max(0, finalSize.Width - (Pad * 2)), finalSize.Height));
        return finalSize;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new HeaderItemAutomationPeer(this);
}

/// <summary>UIA peer for a column divider grip. It reports the Separator role and a fixed name.</summary>
internal sealed class HeaderGripAutomationPeer : FrameworkElementAutomationPeer
{
    public HeaderGripAutomationPeer(FrameworkElement grip)
        : base(grip)
    {
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Separator;

    protected override string GetClassNameCore() => "DetailsListHeaderDivider";

    protected override string GetNameCore() => "Column divider";

    protected override bool IsControlElementCore() => true;

    protected override bool IsContentElementCore() => true;
}

/// <summary>UIA peer for one column title. The name and automation id come from the title element.</summary>
internal sealed class HeaderItemAutomationPeer : FrameworkElementAutomationPeer
{
    public HeaderItemAutomationPeer(HeaderTitle title)
        : base(title)
    {
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.HeaderItem;

    protected override string GetClassNameCore() => "DetailsListHeaderItem";

    protected override bool IsControlElementCore() => true;

    protected override bool IsContentElementCore() => true;
}

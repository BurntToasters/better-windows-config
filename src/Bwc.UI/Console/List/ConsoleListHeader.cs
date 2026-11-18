using Bwc.UI.Console.Shell;
using Bwc.UI.Theme;
using Bwc.UI.Tree;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Bwc.UI.Console.List;

/// <summary>
/// The column header of the Detail view: a title with a sort arrow per visible column, a 1 DIP divider and a 4 DIP
/// grip centered on the divider. A click on a title raises the sort; a drag on a grip resizes the column and a double
/// click on a grip fits it. The panel is one grip wider than the columns so the last grip is fully inside it.
/// </summary>
public sealed class ConsoleListHeader : Panel
{
    internal const double GripWidth = 4;

    private readonly List<HeaderCell> _cells = [];
    private ConsoleList? _owner;

    public ConsoleListHeader()
    {
        Background = DenseTheme.Transparent;
    }

    internal void Attach(ConsoleList owner)
    {
        _owner = owner;
        Rebuild();
    }

    /// <summary>Builds the cells again for the visible columns of the owner.</summary>
    internal void Rebuild()
    {
        if (_owner is null)
        {
            return;
        }

        Children.Clear();
        _cells.Clear();
        IReadOnlyList<ConsoleListColumn> columns = _owner.VisibleColumns;
        InputCursor cursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        foreach (ConsoleListColumn column in columns)
        {
            ConsoleListColumn captured = column;
            var title = new ConsoleListHeaderItem(_owner, captured);
            title.Tapped += (_, e) =>
            {
                _owner?.HeaderClicked(captured);
                e.Handled = true;
            };
            title.RightTapped += (_, e) =>
            {
                _owner?.HeaderRightTapped(captured, e);
                e.Handled = true;
            };

            var divider = new Rectangle { Width = 1, Fill = DenseTheme.Divider, IsHitTestVisible = false };
            var grip = new HeaderGrip(cursor) { Width = GripWidth };
            AutomationProperties.SetAutomationId(grip, "ConsoleListDivider_" + captured.Id);
            grip.AddHandler(
                UIElement.PointerPressedEvent,
                new PointerEventHandler((_, e) => _owner?.DividerPressed(captured, e)),
                handledEventsToo: true);
            grip.DoubleTapped += (_, e) =>
            {
                _owner?.AutoFitColumn(captured.Id);
                e.Handled = true;
            };
            _cells.Add(new HeaderCell(title, divider, grip));
        }

        foreach (HeaderCell cell in _cells)
        {
            Children.Add(cell.Title);
            Children.Add(cell.Divider);
        }

        foreach (HeaderCell cell in _cells)
        {
            Children.Add(cell.Grip);
        }

        ApplyMetrics();
        RefreshSort();
    }

    internal ConsoleListHeaderItem? ItemAt(int index) => index >= 0 && index < _cells.Count ? _cells[index].Title : null;

    internal void ApplyMetrics()
    {
        foreach (HeaderCell cell in _cells)
        {
            cell.Title.ApplyMetrics();
        }

        InvalidateMeasure();
        InvalidateArrange();
    }

    internal void NotifyWidthsChanged()
    {
        InvalidateMeasure();
        InvalidateArrange();
    }

    internal void RefreshTheme()
    {
        foreach (HeaderCell cell in _cells)
        {
            cell.Divider.Fill = DenseTheme.Divider;
            cell.Title.ApplyTheme();
        }
    }

    /// <summary>Shows the arrow on the sorted column only, and sets the item status the screen readers announce.</summary>
    internal void RefreshSort()
    {
        if (_owner is null)
        {
            return;
        }

        foreach (HeaderCell cell in _cells)
        {
            bool sorted = _owner.SortColumnId == cell.Title.Column.Id && _owner.SortDirection != ConsoleListSortDirection.None;
            cell.Title.SetSort(sorted ? _owner.SortDirection : ConsoleListSortDirection.None);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_owner is null)
        {
            return new Size(0, 0);
        }

        double height = ConsoleListMetrics.HeaderHeight;
        foreach (HeaderCell cell in _cells)
        {
            double width = Math.Max(0, cell.Title.Column.Width);
            cell.Title.Measure(new Size(width, height));
            cell.Divider.Measure(new Size(1, Math.Max(0, height - 6)));
            cell.Grip.Measure(new Size(GripWidth, height));
        }

        return new Size(_owner.ColumnsTotalWidth + GripWidth, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_owner is null)
        {
            return finalSize;
        }

        double height = ConsoleListMetrics.HeaderHeight;
        double x = 0;
        foreach (HeaderCell cell in _cells)
        {
            double width = Math.Max(0, cell.Title.Column.Width);
            cell.Title.Arrange(new Rect(x, 0, width, height));
            cell.Divider.Arrange(new Rect(x + width - 1, 3, 1, Math.Max(0, height - 6)));
            cell.Grip.Arrange(new Rect(x + width - (GripWidth / 2), 0, GripWidth, height));
            x += width;
        }

        return finalSize;
    }

    private sealed record HeaderCell(ConsoleListHeaderItem Title, Rectangle Divider, HeaderGrip Grip);

    /// <summary>The transparent strip over a divider. Its peer is a Separator, the role of a column divider.</summary>
    private sealed class HeaderGrip : Panel
    {
        public HeaderGrip(InputCursor cursor)
        {
            Background = DenseTheme.Transparent;
            ProtectedCursor = cursor;
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new Bwc.UI.List.HeaderGripAutomationPeer(this);
    }
}

/// <summary>
/// One column title: the text, aligned as the column is, and the sort arrow at the top center of the
/// cell.
/// </summary>
public sealed class ConsoleListHeaderItem : Panel
{
    private const double Pad = 4;

    private readonly TextBlock _label;
    private readonly Polygon _arrow;
    private ConsoleListSortDirection _sort;

    internal ConsoleListHeaderItem(ConsoleList owner, ConsoleListColumn column)
    {
        Owner = owner;
        Column = column;
        Background = DenseTheme.Transparent;
        _label = new TextBlock
        {
            Text = column.Header,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Foreground = DenseTheme.Text,
            TextAlignment = column.Alignment switch
            {
                ConsoleListAlignment.Right => TextAlignment.Right,
                ConsoleListAlignment.Center => TextAlignment.Center,
                _ => TextAlignment.Left,
            },
        };
        _arrow = new Polygon { Fill = DenseTheme.Text, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        Children.Add(_label);
        Children.Add(_arrow);
        AutomationProperties.SetName(this, column.Header);
        AutomationProperties.SetAutomationId(this, "ConsoleListHeader_" + column.Id);
        ApplyMetrics();
    }

    internal ConsoleList Owner { get; }

    internal ConsoleListColumn Column { get; }

    internal ConsoleListSortDirection Sort => _sort;

    internal void ApplyMetrics()
    {
        _label.FontSize = DenseMetrics.FontSize;
        _label.FontFamily = DenseMetrics.FontFamily;
        _label.LineHeight = DenseMetrics.FontSize * 4.0 / 3.0;
    }

    internal void ApplyTheme()
    {
        _label.Foreground = DenseTheme.Text;
        _arrow.Fill = DenseTheme.Text;
    }

    internal void SetSort(ConsoleListSortDirection direction)
    {
        _sort = direction;
        _arrow.Visibility = direction == ConsoleListSortDirection.None ? Visibility.Collapsed : Visibility.Visible;
        double w = ConsoleListMetrics.SortArrowWidth;
        double h = ConsoleListMetrics.SortArrowHeight;
        _arrow.Points = direction == ConsoleListSortDirection.Ascending
            ? new PointCollection { new Point(0, h), new Point(w / 2, 0), new Point(w, h) }
            : new PointCollection { new Point(0, 0), new Point(w, 0), new Point(w / 2, h) };
        string status = direction switch
        {
            ConsoleListSortDirection.Ascending => ConsoleStrings.Get("ConsoleListSortedAscending"),
            ConsoleListSortDirection.Descending => ConsoleStrings.Get("ConsoleListSortedDescending"),
            _ => string.Empty,
        };
        AutomationProperties.SetItemStatus(this, status);
        InvalidateArrange();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? ConsoleListMetrics.HeaderHeight : availableSize.Height;
        _label.Measure(new Size(Math.Max(0, width - (Pad * 2)), height));
        _arrow.Measure(new Size(ConsoleListMetrics.SortArrowWidth, ConsoleListMetrics.SortArrowHeight));
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _label.Arrange(new Rect(Pad, 0, Math.Max(0, finalSize.Width - (Pad * 2)), finalSize.Height));
        double w = ConsoleListMetrics.SortArrowWidth;
        _arrow.Arrange(new Rect(Math.Max(0, (finalSize.Width - w) / 2), 3, w, ConsoleListMetrics.SortArrowHeight));
        return finalSize;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ConsoleListHeaderItemAutomationPeer(this);
}

/// <summary>UIA peer of a column title: a HeaderItem named by the column text that Invoke sorts by.</summary>
internal sealed class ConsoleListHeaderItemAutomationPeer : FrameworkElementAutomationPeer, IInvokeProvider
{
    private readonly ConsoleListHeaderItem _item;

    public ConsoleListHeaderItemAutomationPeer(ConsoleListHeaderItem item)
        : base(item)
    {
        _item = item;
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.HeaderItem;

    protected override string GetClassNameCore() => "ConsoleListHeaderItem";

    protected override bool IsControlElementCore() => true;

    protected override bool IsContentElementCore() => true;

    protected override object? GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Invoke ? this : base.GetPatternCore(patternInterface);

    public void Invoke() => _item.Owner.HeaderClicked(_item.Column);
}

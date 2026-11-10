using Bwc.UI.Theme;
using Bwc.UI.Tree;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Bwc.UI.List;

/// <summary>
/// Column header row. Each column has a transparent click target (sort), a title, a small sort
/// arrow, a 1 DIP divider, and a 4 DIP drag grip centered on the divider.
/// </summary>
public sealed class DetailsHeaderPanel : Panel
{
    private const double ArrowWidth = 7;
    private const double ArrowHeight = 4;
    private const double ArrowSpace = 12;
    private const double GripWidth = 4;
    private const double Pad = 4;

    private readonly List<HeaderCell> _cells = new();
    private DetailsList? _owner;

    public DetailsHeaderPanel()
    {
        Background = DenseTheme.HeaderBackground;
    }

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
            cell.Title.FontSize = DenseMetrics.FontSize;
            cell.Title.FontFamily = DenseMetrics.FontFamily;
            cell.Title.LineHeight = DenseMetrics.FontSize * 4.0 / 3.0;
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
            cell.Hit.Fill = DenseTheme.Transparent;
            cell.Grip.Background = DenseTheme.Transparent;
            cell.Title.Foreground = DenseTheme.Text;
            cell.Arrow.Fill = DenseTheme.Text;
            cell.Divider.Fill = DenseTheme.Divider;
        }
    }

    internal void SetSort(string? key, bool ascending)
    {
        for (var i = 0; i < _cells.Count; i++)
        {
            var column = _owner!.Columns[i];
            var visible = string.Equals(column.Key, key, StringComparison.Ordinal);
            _cells[i].Arrow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible)
            {
                _cells[i].Arrow.Points = ArrowPoints(ascending);
            }
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
                _cells[i].Title.Text = column.Header;
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

            var hit = new Rectangle { Fill = DenseTheme.Transparent };
            hit.Tapped += (_, _) => _owner?.OnHeaderTapped(index);

            var title = new TextBlock
            {
                Text = column.Header,
                FontFamily = DenseMetrics.FontFamily,
                FontSize = DenseMetrics.FontSize,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Foreground = DenseTheme.Text,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            };
            AutomationProperties.SetName(title, column.Header);
            AutomationProperties.SetAutomationId(title, "Header" + column.Key);

            var arrow = new Polygon
            {
                Width = ArrowWidth,
                Height = ArrowHeight,
                Fill = DenseTheme.Text,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                Points = ArrowPoints(ascending: true),
            };

            var divider = new Rectangle
            {
                Width = 1,
                Fill = DenseTheme.Divider,
                IsHitTestVisible = false,
            };

            var grip = new GripPanel(resizeCursor) { Width = GripWidth };
            grip.PointerPressed += (_, e) => _owner?.OnDividerPressed(index, grip, e);
            grip.PointerMoved += (_, e) => _owner?.OnDividerMoved(index, e);
            grip.PointerReleased += (_, e) => _owner?.OnDividerReleased(index, grip, e);
            grip.DoubleTapped += (_, e) =>
            {
                _owner?.AutoSizeColumn(index);
                e.Handled = true;
            };

            _cells.Add(new HeaderCell(hit, title, arrow, divider, grip));
        }

        for (var i = 0; i < _cells.Count; i++)
        {
            Children.Add(_cells[i].Hit);
        }

        for (var i = 0; i < _cells.Count; i++)
        {
            Children.Add(_cells[i].Title);
            Children.Add(_cells[i].Arrow);
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
            Place(cell.Hit, x, 0, width, height, arrange);
            Place(cell.Title, x + Pad, 0, Math.Max(0, width - Pad - ArrowSpace), height, arrange);
            Place(cell.Arrow, x + width - Pad - ArrowWidth, (height - ArrowHeight) / 2, ArrowWidth, ArrowHeight, arrange);
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

    private static PointCollection ArrowPoints(bool ascending)
    {
        var points = new PointCollection();
        if (ascending)
        {
            points.Add(new Point(0, ArrowHeight));
            points.Add(new Point(ArrowWidth, ArrowHeight));
            points.Add(new Point(ArrowWidth / 2, 0));
        }
        else
        {
            points.Add(new Point(0, 0));
            points.Add(new Point(ArrowWidth, 0));
            points.Add(new Point(ArrowWidth / 2, ArrowHeight));
        }

        return points;
    }

    private sealed record HeaderCell(Rectangle Hit, TextBlock Title, Polygon Arrow, Rectangle Divider, GripPanel Grip);

    /// <summary>Transparent strip over a divider. A panel subclass is needed to set ProtectedCursor.</summary>
    private sealed class GripPanel : Panel
    {
        public GripPanel(InputCursor cursor)
        {
            Background = DenseTheme.Transparent;
            ProtectedCursor = cursor;
        }
    }
}

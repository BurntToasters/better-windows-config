using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Bwc.Spike.ListApp.Model;

namespace Bwc.Spike.ListApp.Controls;

/// <summary>
/// Column header row: per column a click target (sort), title, sort arrow, 1px divider and an 8px drag grip
/// centred on the divider. Cell geometry comes from the shared ColumnModel.
/// </summary>
public sealed class HeaderPanel : Panel
{
    private const int ColumnCount = 3;

    private readonly Rectangle[] _hit = new Rectangle[ColumnCount];
    private readonly TextBlock[] _title = new TextBlock[ColumnCount];
    private readonly Polygon[] _arrow = new Polygon[ColumnCount];
    private readonly Rectangle[] _divider = new Rectangle[ColumnCount];
    private readonly DividerGrip[] _grip = new DividerGrip[ColumnCount];
    private DetailsList _owner;

    /// <summary>Hit-test strip over a column divider (transparent background); shows the east-west resize cursor.</summary>
    private sealed class DividerGrip : Panel
    {
        public DividerGrip(InputCursor cursor)
        {
            Background = Metrics.Transparent;
            ProtectedCursor = cursor;
        }
    }

    public HeaderPanel()
    {
        var resizeCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        for (int i = 0; i < ColumnCount; i++)
        {
            int column = i;
            _hit[i] = new Rectangle { Fill = Metrics.Transparent };
            _hit[i].Tapped += (s, e) => _owner?.OnHeaderTapped(column);

            _title[i] = new TextBlock
            {
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                FontFamily = Metrics.Font,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Foreground = Metrics.Text,
            };

            _arrow[i] = new Polygon
            {
                Width = Metrics.ArrowWidth,
                Height = Metrics.ArrowHeight,
                Fill = Metrics.Text,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                Points = ArrowPoints(true),
            };

            _divider[i] = new Rectangle { Width = 1, Fill = Metrics.Divider, IsHitTestVisible = false };

            _grip[i] = new DividerGrip(resizeCursor) { Width = Metrics.GripWidth };
            _grip[i].PointerPressed += (s, e) => _owner?.OnDividerPressed(column, _grip[column], e);
            _grip[i].PointerMoved += (s, e) => _owner?.OnDividerMoved(column, e);
            _grip[i].PointerReleased += (s, e) => _owner?.OnDividerReleased(column, _grip[column], e);
            _grip[i].DoubleTapped += (s, e) => _owner?.AutoSizeColumn(column);
        }

        // Z-order: click targets first, grips last so they win hit testing at the seams.
        for (int i = 0; i < ColumnCount; i++) { Children.Add(_hit[i]); }
        for (int i = 0; i < ColumnCount; i++) { Children.Add(_title[i]); Children.Add(_arrow[i]); Children.Add(_divider[i]); }
        for (int i = 0; i < ColumnCount; i++) { Children.Add(_grip[i]); }
    }

    internal void Attach(DetailsList owner)
    {
        _owner = owner;
        for (int i = 0; i < ColumnCount; i++)
        {
            _title[i].Text = owner.Columns.Title(i);
        }
    }

    internal void ApplyFont(double fontSize)
    {
        for (int i = 0; i < ColumnCount; i++)
        {
            _title[i].FontSize = fontSize;
            _title[i].LineHeight = fontSize * 4.0 / 3.0;
        }
    }

    internal void SetSort(int column, bool descending)
    {
        for (int i = 0; i < ColumnCount; i++)
        {
            _arrow[i].Visibility = i == column ? Visibility.Visible : Visibility.Collapsed;
        }

        if (column >= 0 && column < ColumnCount)
        {
            _arrow[column].Points = ArrowPoints(descending);
        }
    }

    private static PointCollection ArrowPoints(bool descending)
    {
        var points = new PointCollection();
        if (descending)
        {
            points.Add(new Point(0, 0));
            points.Add(new Point(Metrics.ArrowWidth, 0));
            points.Add(new Point(Metrics.ArrowWidth / 2, Metrics.ArrowHeight));
        }
        else
        {
            points.Add(new Point(0, Metrics.ArrowHeight));
            points.Add(new Point(Metrics.ArrowWidth, Metrics.ArrowHeight));
            points.Add(new Point(Metrics.ArrowWidth / 2, 0));
        }

        return points;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_owner == null)
        {
            return new Size(0, 0);
        }

        LayoutCells(false);
        return new Size(_owner.Columns.TotalWidth, _owner.HeaderHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_owner == null)
        {
            return finalSize;
        }

        LayoutCells(true);
        return finalSize;
    }

    private void LayoutCells(bool arrange)
    {
        ColumnModel cols = _owner.Columns;
        double h = _owner.HeaderHeight;
        for (int i = 0; i < ColumnCount; i++)
        {
            double x = cols.Left(i);
            double w = cols.Width(i);
            Place(_hit[i], x, 0, w, h, arrange);
            Place(_title[i], x + Metrics.Pad, 0, w - 2 * Metrics.Pad - Metrics.ArrowSpace, h, arrange);
            Place(_arrow[i], x + w - Metrics.Pad - Metrics.ArrowWidth, (h - Metrics.ArrowHeight) / 2,
                Metrics.ArrowWidth, Metrics.ArrowHeight, arrange);
            Place(_divider[i], x + w - 1, 3, 1, Math.Max(0, h - 6), arrange);
        }

        for (int i = 0; i < ColumnCount; i++)
        {
            double seam = cols.Left(i) + cols.Width(i);
            Place(_grip[i], seam - Metrics.GripWidth / 2, 0, Metrics.GripWidth, h, arrange);
        }
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
}

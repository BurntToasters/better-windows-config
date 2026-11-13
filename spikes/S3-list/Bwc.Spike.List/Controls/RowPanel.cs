using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Bwc.Spike.ListApp.Model;

namespace Bwc.Spike.ListApp.Controls;

/// <summary>
/// One realized row (the ItemsRepeater element). Fixed Height = row height, no padding or min-height.
/// Cells are placed from the shared ColumnModel in ArrangeOverride, so a column resize only re-arranges.
/// </summary>
public sealed class RowPanel : Panel
{
    private readonly Rectangle _icon;
    private readonly TextBlock _name;
    private readonly TextBlock _type;
    private readonly TextBlock _data;
    private double _rowHeight = -1;
    private double _fontSize = -1;
    private double _iconSize = -1;
    private bool _isSelected;
    private bool _attachedOnce;

    public RowPanel()
    {
        Background = Metrics.Transparent;
        _icon = new Rectangle { IsHitTestVisible = false };
        _name = CreateText();
        _type = CreateText();
        _data = CreateText();
        Children.Add(_icon);
        Children.Add(_name);
        Children.Add(_type);
        Children.Add(_data);
        PointerPressed += OnRowPointerPressed;
    }

    /// <summary>The owning list; set by Attach before the row is measured.</summary>
    internal DetailsList Owner { get; private set; }

    public int Index { get; private set; } = -1;

    internal RegValue Item { get; private set; }

    internal double RowHeight => _rowHeight;

    private static TextBlock CreateText()
    {
        return new TextBlock
        {
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            FontFamily = Metrics.Font,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Foreground = Metrics.Text,
        };
    }

    /// <summary>
    /// Binds a recycled element to an item. Only properties whose value actually changes are written:
    /// layout-affecting writes during ItemsRepeater's ElementPrepared invalidate the parent chain.
    /// </summary>
    internal void Attach(DetailsList owner, int index, RegValue item, double rowHeight, double fontSize, bool selected)
    {
        Owner = owner;
        Index = index;
        Item = item;
        if (_rowHeight != rowHeight)
        {
            _rowHeight = rowHeight;
            Height = rowHeight;
        }

        double iconSize = owner.IconSize;
        if (_iconSize != iconSize)
        {
            _iconSize = iconSize;
            _icon.Width = iconSize;
            _icon.Height = iconSize;
        }

        if (_fontSize != fontSize)
        {
            _fontSize = fontSize;
            double lineHeight = fontSize * 4.0 / 3.0; // Segoe UI natural line box is ~1.333 em
            foreach (TextBlock t in new[] { _name, _type, _data })
            {
                t.FontSize = fontSize;
                t.LineHeight = lineHeight;
            }
        }

        if (_name.Text != item.Name) { _name.Text = item.Name; }
        if (_type.Text != item.Type) { _type.Text = item.Type; }
        if (_data.Text != item.Data) { _data.Text = item.Data; }

        Brush icon = Metrics.IconFor(item.Type);
        if (!ReferenceEquals(_icon.Fill, icon))
        {
            _icon.Fill = icon;
        }

        SetSelected(selected);
    }

    internal void SetSelected(bool selected)
    {
        if (_isSelected == selected && _attachedOnce)
        {
            return;
        }

        _isSelected = selected;
        _attachedOnce = true;
        Background = selected ? Metrics.Selection : Metrics.Transparent;
        Brush fg = selected ? Metrics.SelectedText : Metrics.Text;
        _name.Foreground = fg;
        _type.Foreground = fg;
        _data.Foreground = fg;
    }

    internal string CellText(int column)
    {
        return column == 0 ? Item.Name : column == 1 ? Item.Type : Item.Data;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Owner == null)
        {
            return new Size(0, 0);
        }

        LayoutCells(false);
        return new Size(Owner.Columns.TotalWidth, _rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Owner == null)
        {
            return finalSize;
        }

        LayoutCells(true);
        return finalSize;
    }

    private void LayoutCells(bool arrange)
    {
        ColumnModel cols = Owner.Columns;
        double h = _rowHeight;
        double icon = Owner.IconSize;

        double x0 = cols.Left(0);
        double nameX = x0 + Metrics.Pad + icon + Metrics.IconGap;
        Place(_icon, x0 + Metrics.Pad, (h - icon) / 2, icon, icon, arrange);
        Place(_name, nameX, 0, cols.Width(0) - (nameX - x0) - Metrics.Pad, h, arrange);
        Place(_type, cols.Left(1) + Metrics.Pad, 0, cols.Width(1) - 2 * Metrics.Pad, h, arrange);
        Place(_data, cols.Left(2) + Metrics.Pad, 0, cols.Width(2) - 2 * Metrics.Pad, h, arrange);
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

    private void OnRowPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Owner?.RowPressed(this, e);
    }
}

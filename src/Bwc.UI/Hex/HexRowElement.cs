using System.Globalization;
using System.Text;
using Bwc.UI.Tree;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Bwc.UI.Hex;

/// <summary>
/// One realized hex row. The panel arranges an offset cell, sixteen hex byte cells, and sixteen ASCII
/// cells directly, so the row has no per-item chrome and no nested layout cost. Selection is painted
/// on the byte cells only.
/// </summary>
internal sealed class HexRowElement : Panel
{
    private readonly TextBlock _offsetText;
    private readonly Border[] _hexCells = new Border[HexEditor.BytesPerRow];
    private readonly TextBlock[] _hexTexts = new TextBlock[HexEditor.BytesPerRow];
    private readonly Border[] _asciiCells = new Border[HexEditor.BytesPerRow];
    private readonly TextBlock[] _asciiTexts = new TextBlock[HexEditor.BytesPerRow];
    private int _index = -1;

    public HexRowElement()
    {
        _offsetText = CreateText();
        Children.Add(_offsetText);

        for (var index = 0; index < HexEditor.BytesPerRow; index++)
        {
            TextBlock hexText = CreateText();
            Border hexCell = CreateCell(hexText);
            _hexTexts[index] = hexText;
            _hexCells[index] = hexCell;
            Children.Add(hexCell);

            TextBlock asciiText = CreateText();
            Border asciiCell = CreateCell(asciiText);
            _asciiTexts[index] = asciiText;
            _asciiCells[index] = asciiCell;
            Children.Add(asciiCell);
        }

        PointerPressed += OnPointerPressed;
    }

    internal HexEditor? Owner { get; private set; }

    internal int Index => _index;

    internal string RowText { get; private set; } = string.Empty;

    internal void Attach(HexEditor owner, int index)
    {
        Owner = owner;
        _index = index;
        ApplyContent();
        ApplySelection();
        AutomationProperties.SetAutomationId(this, "HexRow_" + index.ToString(CultureInfo.InvariantCulture));
    }

    internal void Detach()
    {
        Owner = null;
        _index = -1;
        RowText = string.Empty;
    }

    internal void ApplySelection()
    {
        if (Owner is null || _index < 0)
        {
            return;
        }

        int count = Owner.RowByteCount(_index);
        int offset = _index * HexEditor.BytesPerRow;
        for (var index = 0; index < HexEditor.BytesPerRow; index++)
        {
            bool selected = index < count && Owner.IsByteSelected(offset + index);
            _hexCells[index].Background = selected ? DenseTheme.SelectionBackground : null;
            _hexTexts[index].Foreground = selected ? DenseTheme.SelectionForeground : DenseTheme.Text;
            _asciiCells[index].Background = selected ? DenseTheme.SelectionBackground : null;
            _asciiTexts[index].Foreground = selected ? DenseTheme.SelectionForeground : DenseTheme.Text;
        }

        _offsetText.Foreground = DenseTheme.Text;
    }

    /// <summary>Rebuilds the row text after an edit without recreating the element.</summary>
    internal void Refresh()
    {
        ApplyContent();
        ApplySelection();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Owner is null)
        {
            return new Size(0, HexEditor.RowHeight);
        }

        var rowSize = new Size(double.PositiveInfinity, HexEditor.RowHeight);
        foreach (UIElement child in Children)
        {
            child.Measure(rowSize);
        }

        double width = double.IsInfinity(availableSize.Width)
            ? Owner.ContentWidth
            : Math.Max(Owner.ContentWidth, availableSize.Width);
        return new Size(width, HexEditor.RowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Owner is null)
        {
            return finalSize;
        }

        double height = HexEditor.RowHeight;
        _offsetText.Arrange(new Rect(0, 0, Owner.OffsetWidth, height));

        for (var index = 0; index < HexEditor.BytesPerRow; index++)
        {
            double hexLeft = Owner.HexLeft + (index * Owner.ByteStep);
            _hexCells[index].Arrange(new Rect(hexLeft, 0, Owner.ByteStep, height));

            double asciiLeft = Owner.AsciiLeft + (index * Owner.CharWidth);
            _asciiCells[index].Arrange(new Rect(asciiLeft, 0, Owner.CharWidth, height));
        }

        return finalSize;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new HexRowAutomationPeer(this);

    private static TextBlock CreateText() => new()
    {
        Foreground = DenseTheme.Text,
        IsHitTestVisible = false,
        TextWrapping = TextWrapping.NoWrap,
        VerticalAlignment = VerticalAlignment.Top,
    };

    private static Border CreateCell(TextBlock text) => new()
    {
        Background = null,
        BorderThickness = new Thickness(0),
        Child = text,
        Padding = new Thickness(0),
    };

    private void ApplyContent()
    {
        if (Owner is null || _index < 0)
        {
            return;
        }

        int offset = _index * HexEditor.BytesPerRow;
        int count = Owner.RowByteCount(_index);
        ReadOnlySpan<byte> data = Owner.Data.Span;
        var text = new StringBuilder(8 + 2 + (HexEditor.BytesPerRow * 3) + 2 + HexEditor.BytesPerRow);

        _offsetText.Text = offset.ToString("X8", CultureInfo.InvariantCulture);
        _offsetText.FontFamily = Owner.MonospaceFont;
        _offsetText.FontSize = Owner.MonospaceFontSize;
        text.Append(_offsetText.Text).Append("  ");

        for (var index = 0; index < HexEditor.BytesPerRow; index++)
        {
            TextBlock hexText = _hexTexts[index];
            TextBlock asciiText = _asciiTexts[index];
            hexText.FontFamily = Owner.MonospaceFont;
            hexText.FontSize = Owner.MonospaceFontSize;
            asciiText.FontFamily = Owner.MonospaceFont;
            asciiText.FontSize = Owner.MonospaceFontSize;

            if (index < count)
            {
                byte value = data[offset + index];
                string hex = value.ToString("X2", CultureInfo.InvariantCulture);
                hexText.Text = hex;
                asciiText.Text = IsPrintable(value) ? ((char)value).ToString() : ".";
            }
            else
            {
                hexText.Text = string.Empty;
                asciiText.Text = string.Empty;
            }

            if (index > 0)
            {
                text.Append(' ');
            }

            text.Append(index < count ? hexText.Text : "  ");
        }

        text.Append("  ");
        for (var index = 0; index < HexEditor.BytesPerRow; index++)
        {
            text.Append(index < count ? AsciiAt(index) : " ");
        }

        RowText = text.ToString();
    }

    private string AsciiAt(int index) => _asciiTexts[index].Text;

    private static bool IsPrintable(byte value) => value is >= 32 and <= 126;

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Owner is null || _index < 0)
        {
            return;
        }

        double x = e.GetCurrentPoint(this).Position.X;
        int byteInRow = Owner.HitTestByte(x);
        if (byteInRow < 0)
        {
            return;
        }

        bool shift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down)
            == CoreVirtualKeyStates.Down;
        Owner.HandleBytePressed(_index, byteInRow, shift, ascii: x >= Owner.AsciiLeft);
        e.Handled = true;
    }
}

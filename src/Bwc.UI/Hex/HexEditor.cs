using System.Collections;
using System.Globalization;
using System.Text;
using Bwc.UI.Tree;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Bwc.UI.Hex;

/// <summary>
/// Read-only virtualized hex viewer. One row displays sixteen bytes as an eight digit offset, sixteen
/// two digit hex cells, and sixteen ASCII cells. Only rows near the viewport are created; the data is
/// stored once as a <see cref="ReadOnlyMemory{T}"/> and rows are addressed by index.
/// </summary>
public sealed class HexEditor : UserControl
{
    /// <summary>Bytes shown on one row.</summary>
    public const int BytesPerRow = 16;

    /// <summary>Fixed row height in DIP.</summary>
    public const double RowHeight = 16;

    private const double FontSizeDip = 12;
    private const double ColumnGap = 8;
    private const double ByteGap = 2;
    private const int GridColumnCount = BytesPerRow + 2;

    private readonly ScrollViewer _scroll;
    private readonly ItemsRepeater _repeater;
    private readonly HashSet<HexRowElement> _realized = new();
    private readonly FontFamily _fontFamily;
    private readonly double _charWidth;
    private readonly double _offsetWidth;
    private readonly double _hexLeft;
    private readonly double _byteStep;
    private readonly double _asciiLeft;
    private readonly double _contentWidth;

    private ReadOnlyMemory<byte> _data;
    private int _length;
    private int _rowCount;
    private long _anchor = -1;
    private long _caret = -1;
    private long _selectionStart;
    private int _selectionLength;
    private string? _clipboardIssue;

    public HexEditor()
    {
        AutomationProperties.SetAutomationId(this, "HexEditor");
        AutomationProperties.SetName(this, "Hex editor");
        AutomationProperties.SetHelpText(this, "Length=0;SelectedOffset=-1;SelectedLength=0");
        IsTabStop = true;
        UseSystemFocusVisuals = true;

        _fontFamily = ResolveMonospaceFont();
        _charWidth = MeasureText("00000000").Width / 8.0;
        _offsetWidth = _charWidth * 8;
        _hexLeft = _offsetWidth + ColumnGap;
        _byteStep = (_charWidth * 2) + ByteGap;
        _asciiLeft = _hexLeft + (BytesPerRow * _byteStep) + ColumnGap;
        _contentWidth = _asciiLeft + (BytesPerRow * _charWidth) + 2;

        _repeater = new ItemsRepeater
        {
            ItemTemplate = new HexRowFactory(),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _repeater.Layout = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 0,
        };
        _repeater.ElementPrepared += OnElementPrepared;
        _repeater.ElementClearing += OnElementClearing;

        _scroll = new ScrollViewer
        {
            Content = _repeater,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled,
            IsTabStop = false,
            Padding = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Enabled,
            ZoomMode = ZoomMode.Disabled,
        };

        Content = _scroll;
        KeyDown += OnKeyDown;
        ActualThemeChanged += OnActualThemeChanged;
    }

    /// <summary>True for this batch. The control is structured for a later editable mode.</summary>
    public bool IsReadOnly => true;

    /// <summary>Number of bytes in the current value.</summary>
    public int Length => _length;

    /// <summary>First selected byte offset, or -1 when nothing is selected.</summary>
    public long SelectedOffset => _selectionLength > 0 ? _selectionStart : -1;

    /// <summary>Number of selected bytes.</summary>
    public int SelectedLength => _selectionLength;

    /// <summary>Raised after the selected range changes.</summary>
    public event EventHandler? SelectionChanged;

    internal int RowCount => _rowCount;

    internal int SelectionStart => checked((int)_selectionStart);

    internal int SelectionLength => _selectionLength;

    internal ReadOnlyMemory<byte> Data => _data;

    internal FontFamily MonospaceFont => _fontFamily;

    internal double MonospaceFontSize => FontSizeDip;

    internal double CharWidth => _charWidth;

    internal double OffsetWidth => _offsetWidth;

    internal double HexLeft => _hexLeft;

    internal double ByteStep => _byteStep;

    internal double AsciiLeft => _asciiLeft;

    internal double ContentWidth => _contentWidth;

    internal int GridColumnCountValue => GridColumnCount;

    /// <summary>Replaces the displayed bytes and clears the selection.</summary>
    public void SetData(ReadOnlyMemory<byte> data)
    {
        bool hadSelection = _selectionLength > 0;
        _data = data;
        _length = data.Length;
        _rowCount = (_length + BytesPerRow - 1) / BytesPerRow;
        _anchor = -1;
        _caret = -1;
        _selectionStart = 0;
        _selectionLength = 0;

        _repeater.ItemsSource = null;
        foreach (HexRowElement row in _realized)
        {
            row.Detach();
        }

        _realized.Clear();
        _clipboardIssue = null;
        _repeater.ItemsSource = new RowIndexList(_rowCount);
        UpdateHelpText();

        if (hadSelection)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal bool IsByteSelected(long offset)
    {
        if (_selectionLength <= 0 || offset < _selectionStart)
        {
            return false;
        }

        return offset < _selectionStart + _selectionLength;
    }

    internal bool IsRowSelected(int index)
    {
        if (index < 0 || index >= _rowCount || _selectionLength <= 0)
        {
            return false;
        }

        long rowStart = (long)index * BytesPerRow;
        long rowEnd = rowStart + RowByteCount(index);
        long selectionEnd = _selectionStart + _selectionLength;
        return _selectionStart < rowEnd && selectionEnd > rowStart;
    }

    internal int RowByteCount(int index)
    {
        if (index < 0 || index >= _rowCount)
        {
            return 0;
        }

        int offset = index * BytesPerRow;
        return Math.Min(BytesPerRow, _length - offset);
    }

    internal void SelectRow(int index)
    {
        if (index < 0 || index >= _rowCount)
        {
            return;
        }

        long offset = (long)index * BytesPerRow;
        _anchor = offset;
        _caret = offset;
        SetSelection(offset, 1);
        RevealRow(index);
        FocusEditor();
    }

    internal void ClearSelection()
    {
        _anchor = -1;
        _caret = -1;
        SetSelection(0, 0);
    }

    internal void HandleBytePressed(int index, int byteInRow, bool shift)
    {
        if (index < 0 || index >= _rowCount || byteInRow < 0 || byteInRow >= RowByteCount(index))
        {
            return;
        }

        long target = ((long)index * BytesPerRow) + byteInRow;
        if (shift && _anchor >= 0)
        {
            long start = Math.Min(_anchor, target);
            long end = Math.Max(_anchor, target);
            _caret = target;
            SetSelection(start, checked((int)(end - start + 1)));
        }
        else
        {
            _anchor = target;
            _caret = target;
            SetSelection(target, 1);
        }

        FocusEditor();
    }

    internal void FocusEditor()
    {
        _ = Focus(FocusState.Programmatic);
    }

    internal void RevealRow(int index)
    {
        if (index < 0 || index >= _rowCount)
        {
            return;
        }

        double top = index * RowHeight;
        double bottom = top + RowHeight;
        double offset = _scroll.VerticalOffset;
        double viewport = _scroll.ViewportHeight;
        if (viewport <= 0)
        {
            return;
        }

        if (top < offset)
        {
            _scroll.ChangeView(null, top, null, disableAnimation: true);
        }
        else if (bottom > offset + viewport)
        {
            _scroll.ChangeView(null, bottom - viewport, null, disableAnimation: true);
        }
    }

    internal HexRowElement? TryGetRowElement(int index)
    {
        try
        {
            return _repeater.TryGetElement(index) as HexRowElement;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    internal int HitTestByte(double x)
    {
        if (x >= _asciiLeft)
        {
            return Math.Clamp((int)((x - _asciiLeft) / _charWidth), 0, BytesPerRow - 1);
        }

        if (x >= _hexLeft)
        {
            return Math.Clamp((int)((x - _hexLeft) / _byteStep), 0, BytesPerRow - 1);
        }

        return x >= 0 && x < _contentWidth ? 0 : -1;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new HexEditorAutomationPeer(this);

    private static FontFamily ResolveMonospaceFont()
    {
        var cascadia = new FontFamily("Cascadia Mono");
        var probe = new TextBlock
        {
            FontFamily = cascadia,
            FontSize = FontSizeDip,
            Text = "iiiiiiii",
            TextWrapping = TextWrapping.NoWrap,
        };

        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double narrow = probe.DesiredSize.Width;
        probe.Text = "WWWWWWWW";
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double wide = probe.DesiredSize.Width;

        // A missing family falls back to the proportional UI font. Measuring two scripts is a
        // cheap way to confirm the family really is monospace before using fixed column widths.
        return Math.Abs(narrow - wide) < 0.1 ? cascadia : new FontFamily("Consolas");
    }

    private Size MeasureText(string text)
    {
        var probe = new TextBlock
        {
            FontFamily = _fontFamily,
            FontSize = FontSizeDip,
            Text = text,
            TextWrapping = TextWrapping.NoWrap,
        };

        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize;
    }

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not HexRowElement row || args.Index < 0 || args.Index >= _rowCount)
        {
            return;
        }

        row.Attach(this, args.Index);
        _realized.Add(row);
    }

    private void OnElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is HexRowElement row)
        {
            _realized.Remove(row);
            row.Detach();
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool shift = IsDown(VirtualKey.Shift);
        bool control = IsDown(VirtualKey.Control);

        switch (e.Key)
        {
            case VirtualKey.Left:
                MoveCaret((_caret < 0 ? 0 : _caret) - 1, shift);
                e.Handled = true;
                break;
            case VirtualKey.Right:
                MoveCaret((_caret < 0 ? -1 : _caret) + 1, shift);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                MoveCaret((_caret < 0 ? 0 : _caret) - BytesPerRow, shift);
                e.Handled = true;
                break;
            case VirtualKey.Down:
                MoveCaret((_caret < 0 ? 0 : _caret) + BytesPerRow, shift);
                e.Handled = true;
                break;
            case VirtualKey.PageUp:
                MoveCaret((_caret < 0 ? 0 : _caret) - PageBytes(), shift);
                e.Handled = true;
                break;
            case VirtualKey.PageDown:
                MoveCaret((_caret < 0 ? 0 : _caret) + PageBytes(), shift);
                e.Handled = true;
                break;
            case VirtualKey.Home:
                MoveCaret(0, shift);
                e.Handled = true;
                break;
            case VirtualKey.End:
                MoveCaret(_length - 1, shift);
                e.Handled = true;
                break;
            case VirtualKey.A when control:
                SelectAll();
                e.Handled = true;
                break;
            case VirtualKey.C when control:
                CopySelection();
                e.Handled = true;
                break;
        }
    }

    private int PageBytes()
    {
        double viewport = _scroll.ViewportHeight;
        int rows = viewport > 0 ? Math.Max(1, (int)(viewport / RowHeight)) : 1;
        return rows * BytesPerRow;
    }

    private void MoveCaret(long target, bool extend)
    {
        if (_length == 0)
        {
            return;
        }

        long clamped = Math.Clamp(target, 0, _length - 1);
        if (extend && _anchor >= 0)
        {
            long start = Math.Min(_anchor, clamped);
            long end = Math.Max(_anchor, clamped);
            _caret = clamped;
            SetSelection(start, checked((int)(end - start + 1)));
        }
        else
        {
            _anchor = clamped;
            _caret = clamped;
            SetSelection(clamped, 1);
        }

        RevealRow(checked((int)(clamped / BytesPerRow)));
    }

    private void SelectAll()
    {
        if (_length == 0)
        {
            return;
        }

        _anchor = 0;
        _caret = _length - 1;
        SetSelection(0, _length);
        RevealRow(0);
    }

    private void SetSelection(long start, int length)
    {
        if (length < 0)
        {
            length = 0;
        }

        if (start < 0)
        {
            start = 0;
            length = 0;
        }
        else if (start > _length)
        {
            start = _length;
            length = 0;
        }

        if (start + length > _length)
        {
            length = checked((int)(_length - start));
        }

        if (_selectionStart == start && _selectionLength == length)
        {
            return;
        }

        _selectionStart = start;
        _selectionLength = length;
        ApplySelectionToRealized();
        UpdateHelpText();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplySelectionToRealized()
    {
        foreach (HexRowElement row in _realized)
        {
            row.ApplySelection();
        }
    }

    private void CopySelection()
    {
        if (_selectionLength <= 0)
        {
            return;
        }

        var builder = new StringBuilder(_selectionLength * 3);
        ReadOnlySpan<byte> bytes = _data.Span.Slice(SelectionStart, _selectionLength);
        for (var index = 0; index < bytes.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(' ');
            }

            builder.Append(bytes[index].ToString("X2", CultureInfo.InvariantCulture));
        }

        _clipboardIssue = null;
        try
        {
            var package = new DataPackage();
            package.SetText(builder.ToString());
            Clipboard.SetContent(package);
        }
        catch (Exception)
        {
            // Another process can hold the clipboard open. The selection and viewer stay usable.
            _clipboardIssue = "unavailable";
        }

        UpdateHelpText();
    }

    private void UpdateHelpText()
    {
        string text = $"Length={_length};SelectedOffset={SelectedOffset};SelectedLength={_selectionLength}";
        if (_clipboardIssue is not null)
        {
            text += ";Clipboard=" + _clipboardIssue;
        }

        AutomationProperties.SetHelpText(this, text);
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        DenseTheme.Reset();
        ApplySelectionToRealized();
    }

    private static bool IsDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down)
        == CoreVirtualKeyStates.Down;

    private sealed class HexRowFactory : IElementFactory
    {
        public UIElement GetElement(ElementFactoryGetArgs args) => new HexRowElement();

        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
            if (args.Element is HexRowElement row)
            {
                row.Detach();
            }
        }
    }

    /// <summary>
    /// A row source that does not allocate one object per row. The item is the row index, which is
    /// all a realized row needs to slice the data.
    /// </summary>
    private sealed class RowIndexList : IReadOnlyList<int>
    {
        private readonly int _count;

        public RowIndexList(int count)
        {
            _count = count;
        }

        public int Count => _count;

        public int this[int index] => index >= 0 && index < _count
            ? index
            : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<int> GetEnumerator()
        {
            for (var index = 0; index < _count; index++)
            {
                yield return index;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

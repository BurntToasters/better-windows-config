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
/// Virtualized hex editor. One row shows eight bytes as a four digit offset (more digits past 64 KiB),
/// eight two digit hex cells, and eight ASCII cells. Only rows near the viewport are created; the data is
/// stored once as a <see cref="ReadOnlyMemory{T}"/> and rows are addressed by index.
/// </summary>
public sealed class HexEditor : UserControl
{
    /// <summary>Bytes shown on one row. Classic Edit Binary Value shows eight, with offsets 0000, 0008, and so on.</summary>
    public const int BytesPerRow = 8;
    /// <summary>The fewest offset digits, as classic shows them (0000).</summary>
    public const int MinimumOffsetDigits = 4;

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
    private double _offsetWidth;
    private double _hexLeft;
    private readonly double _byteStep;
    private double _asciiLeft;
    private double _contentWidth;
    private int _offsetDigits = MinimumOffsetDigits;

    private byte[] _bytes = Array.Empty<byte>();
    private int _length;
    private int _rowCount;
    private long _anchor = -1;
    private long _caret = -1;
    private long _selectionStart;
    private int _selectionLength;
    private int _nibble;
    private bool _editAscii;
    private bool _insertMode;
    private bool _isReadOnly = true;
    private string? _clipboardIssue;

    public HexEditor()
    {
        AutomationProperties.SetAutomationId(this, "HexEditor");
        AutomationProperties.SetName(this, BwcStrings.Get("HexEditorName"));
#if BWC_E2E
        // E2E-only: the debug state string is UIA help text that screen readers must not announce in ship.
        AutomationProperties.SetHelpText(this, "Length=0;SelectedOffset=-1;SelectedLength=0");
#endif
        IsTabStop = true;
        UseSystemFocusVisuals = true;

        _fontFamily = ResolveMonospaceFont();
        _charWidth = MeasureText("00000000").Width / 8.0;
        _byteStep = (_charWidth * 2) + ByteGap;
        UpdateColumns();

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
        CharacterReceived += OnCharacterReceived;
        ActualThemeChanged += OnActualThemeChanged;
    }

    /// <summary>
    /// True while the snapshot is view only. The viewer keeps the default; the edit dialog sets
    /// false before the data is assigned.
    /// </summary>
    public bool IsReadOnly
    {
        get => _isReadOnly;
        set
        {
            if (_isReadOnly == value)
            {
                return;
            }

            _isReadOnly = value;
            UpdateHelpText();
        }
    }

    /// <summary>Insert mode inserts typed bytes instead of replacing them. Toggled by Insert.</summary>
    public bool InsertMode
    {
        get => _insertMode;
        set
        {
            if (_insertMode == value)
            {
                return;
            }

            _insertMode = value;
            UpdateHelpText();
            ModeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Current edit mode name for a status line.</summary>
    public string Mode => _insertMode ? "Insert" : "Overwrite";

    /// <summary>Which column received the last click. Hex is the default.</summary>
    public string ActiveColumn => _editAscii ? "ASCII" : "Hex";

    /// <summary>Number of bytes in the current value.</summary>
    public int Length => _length;

    /// <summary>A copy of the current bytes. The host writes these after the dialog accepts.</summary>
    public byte[] GetBytes() => _bytes.AsSpan(0, _length).ToArray();

    /// <summary>The current bytes without copying.</summary>
    public ReadOnlyMemory<byte> Bytes => _bytes.AsMemory(0, _length);

    /// <summary>First selected byte offset, or -1 when nothing is selected.</summary>
    public long SelectedOffset => _selectionLength > 0 ? _selectionStart : -1;

    /// <summary>Number of selected bytes.</summary>
    public int SelectedLength => _selectionLength;

    /// <summary>Raised after the selected range changes.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Raised after an edit changes the bytes or the length.</summary>
    public event EventHandler? DataChanged;

    /// <summary>Raised when Insert or Overwrite mode changes.</summary>
    public event EventHandler? ModeChanged;

    internal int RowCount => _rowCount;

    internal int SelectionStart => checked((int)_selectionStart);

    internal int SelectionLength => _selectionLength;

    internal ReadOnlyMemory<byte> Data => _bytes.AsMemory(0, _length);

    internal FontFamily MonospaceFont => _fontFamily;

    internal double MonospaceFontSize => FontSizeDip;

    internal double CharWidth => _charWidth;

    internal double OffsetWidth => _offsetWidth;

    /// <summary>Hex digits in the offset column for the current length. Never fewer than four.</summary>
    internal int OffsetDigits => _offsetDigits;

    internal double HexLeft => _hexLeft;

    internal double ByteStep => _byteStep;

    internal double AsciiLeft => _asciiLeft;

    internal double ContentWidth => _contentWidth;

    internal int GridColumnCountValue => GridColumnCount;

    /// <summary>
    /// Replaces the displayed bytes and clears the selection. The edit mode and read-only state are
    /// preserved. The snapshot is copied so later edits never mutate the caller's array. This is
    /// not an edit, so <see cref="DataChanged"/> is not raised.
    /// </summary>
    public void SetData(ReadOnlyMemory<byte> data)
    {
        bool hadSelection = _selectionLength > 0;
        _bytes = data.ToArray();
        _length = _bytes.Length;
        _rowCount = (_length + BytesPerRow - 1) / BytesPerRow;
        _anchor = -1;
        _caret = -1;
        _selectionStart = 0;
        _selectionLength = 0;
        _nibble = 0;

        ResetRows();
        _clipboardIssue = null;
        UpdateHelpText();

        if (hadSelection)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Selects <paramref name="length"/> bytes starting at <paramref name="offset"/>. Values are
    /// clamped to the data; a zero length leaves the selection empty.
    /// </summary>
    public void SelectRange(long offset, int length)
    {
        if (_length == 0)
        {
            _anchor = -1;
            _caret = -1;
            SetSelection(0, 0);
            return;
        }

        long start = Math.Clamp(offset, 0, _length - 1);
        int available = checked((int)(_length - start));
        int count = Math.Clamp(length, 0, available);
        _anchor = start;
        _caret = count > 0 ? start + count - 1 : start;
        _nibble = 0;
        SetSelection(start, count);
        RevealRow(checked((int)(start / BytesPerRow)));
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
        _nibble = 0;
        SetSelection(offset, 1);
        RevealRow(index);
        FocusEditor();
    }

    internal void ClearSelection()
    {
        _anchor = -1;
        _caret = -1;
        _nibble = 0;
        SetSelection(0, 0);
    }

    internal void HandleBytePressed(int index, int byteInRow, bool shift, bool ascii)
    {
        if (index < 0 || index >= _rowCount || byteInRow < 0 || byteInRow >= RowByteCount(index))
        {
            return;
        }

        _editAscii = ascii;
        _nibble = 0;
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
                MoveCaret(control ? 0 : RowStart(_caret), shift);
                e.Handled = true;
                break;
            case VirtualKey.End:
                MoveCaret(control ? _length - 1 : RowEnd(_caret), shift);
                e.Handled = true;
                break;
            case VirtualKey.Space when !_editAscii:
                MoveCaret((_caret < 0 ? 0 : _caret) + 1, shift);
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
            case VirtualKey.V when control && !_isReadOnly:
                PasteFromClipboard();
                e.Handled = true;
                break;
            case VirtualKey.Insert when !_isReadOnly:
                InsertMode = !InsertMode;
                e.Handled = true;
                break;
            case VirtualKey.Delete when !_isReadOnly:
                DeleteSelectionOrCaret(backspace: false);
                e.Handled = true;
                break;
            case VirtualKey.Back when !_isReadOnly:
                DeleteSelectionOrCaret(backspace: true);
                e.Handled = true;
                break;
        }
    }

    private long RowStart(long offset)
    {
        if (_length == 0)
        {
            return 0;
        }

        long start = Math.Clamp(offset, 0, _length - 1);
        return start - (start % BytesPerRow);
    }

    private long RowEnd(long offset)
    {
        if (_length == 0)
        {
            return 0;
        }

        long end = RowStart(offset) + BytesPerRow - 1;
        return Math.Min(end, _length - 1);
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
        _nibble = 0;
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

    /// <summary>Selects every byte. Exposed for hosts and tests.</summary>
    public void SelectAll()
    {
        if (_length == 0)
        {
            return;
        }

        _anchor = 0;
        _caret = _length - 1;
        _nibble = 0;
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
        ReadOnlySpan<byte> bytes = _bytes.AsSpan(SelectionStart, _selectionLength);
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

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (_isReadOnly || e.Handled || IsDown(VirtualKey.Control))
        {
            return;
        }

        char character = e.Character;
        if (character is '\0' or '\r' or '\n' or '\t')
        {
            return;
        }

        if (_editAscii)
        {
            if (character is >= (char)32 and <= (char)126)
            {
                ApplyAsciiByte((byte)character);
                e.Handled = true;
            }

            return;
        }

        int digit = HexDigit(character);
        if (digit >= 0)
        {
            ApplyHexDigit(digit);
            e.Handled = true;
        }
    }

    private static int HexDigit(char character) => character switch
    {
        >= '0' and <= '9' => character - '0',
        >= 'a' and <= 'f' => character - 'a' + 10,
        >= 'A' and <= 'F' => character - 'A' + 10,
        _ => -1,
    };

    private int CurrentEditOffset()
    {
        if (_length == 0)
        {
            return -1;
        }

        if (_selectionLength > 0)
        {
            return checked((int)_selectionStart);
        }

        return _caret >= 0 ? checked((int)Math.Min(_caret, _length - 1)) : 0;
    }

    /// <summary>
    /// Applies one hex digit. The first digit of a byte replaces the high nibble and clears the
    /// low nibble; the second writes the low nibble and advances. In insert mode the high nibble
    /// inserts a zero byte first so the rest of the buffer shifts right.
    /// </summary>
    private void ApplyHexDigit(int digit)
    {
        int offset = CurrentEditOffset();
        if (offset < 0)
        {
            return;
        }

        if (_selectionLength > 1)
        {
            _nibble = 0;
        }

        if (_selectionLength != 1 || _selectionStart != offset)
        {
            _anchor = offset;
            _caret = offset;
            SetSelection(offset, 1);
        }

        bool rowsChanged = false;
        if (_insertMode && _nibble == 0)
        {
            InsertByteAt(offset, 0);
            rowsChanged = true;
        }

        if (_nibble == 0)
        {
            _bytes[offset] = (byte)(digit << 4);
            _nibble = 1;
            AfterEdit(offset, rowsChanged);
            return;
        }

        _bytes[offset] = (byte)((_bytes[offset] & 0xF0) | digit);
        _nibble = 0;
        int next = Math.Min(offset + 1, _length - 1);
        _anchor = next;
        _caret = next;
        SetSelection(next, 1);
        AfterEdit(next, rowsChanged);
    }

    private void ApplyAsciiByte(byte value)
    {
        int offset = CurrentEditOffset();
        if (offset < 0)
        {
            return;
        }

        if (_selectionLength != 1 || _selectionStart != offset)
        {
            _anchor = offset;
            _caret = offset;
            SetSelection(offset, 1);
        }

        bool rowsChanged = false;
        if (_insertMode)
        {
            InsertByteAt(offset, value);
            rowsChanged = true;
        }
        else
        {
            _bytes[offset] = value;
        }

        int next = Math.Min(offset + 1, _length - 1);
        _anchor = next;
        _caret = next;
        _nibble = 0;
        SetSelection(next, 1);
        AfterEdit(next, rowsChanged);
    }

    private void DeleteSelectionOrCaret(bool backspace)
    {
        int start;
        int count;

        if (_selectionLength > 0)
        {
            start = SelectionStart;
            count = _selectionLength;
        }
        else if (backspace)
        {
            if (_caret <= 0)
            {
                return;
            }

            start = checked((int)_caret - 1);
            count = 1;
        }
        else
        {
            if (_caret < 0 || _caret >= _length)
            {
                return;
            }

            start = checked((int)_caret);
            count = 1;
        }

        RemoveRange(start, count);
        _nibble = 0;

        if (_length == 0)
        {
            _anchor = -1;
            _caret = -1;
            SetSelection(0, 0);
        }
        else
        {
            int next = Math.Clamp(start, 0, _length - 1);
            _anchor = next;
            _caret = next;
            SetSelection(next, 1);
        }

        AfterEdit(_length == 0 ? 0 : SelectionStart, rowsChanged: true);
    }

    private void InsertByteAt(int index, byte value)
    {
        int oldLength = _length;
        Array.Resize(ref _bytes, oldLength + 1);
        if (index < oldLength)
        {
            Array.Copy(_bytes, index, _bytes, index + 1, oldLength - index);
        }

        _bytes[index] = value;
        _length = oldLength + 1;
        _rowCount = (_length + BytesPerRow - 1) / BytesPerRow;
    }

    private void RemoveRange(int start, int count)
    {
        if (start < 0 || count <= 0 || start + count > _length)
        {
            return;
        }

        int tail = _length - (start + count);
        if (tail > 0)
        {
            Array.Copy(_bytes, start + count, _bytes, start, tail);
        }

        _length -= count;
        Array.Resize(ref _bytes, _length);
        _rowCount = (_length + BytesPerRow - 1) / BytesPerRow;
    }

    private void AfterEdit(int revealOffset, bool rowsChanged)
    {
        if (rowsChanged)
        {
            ResetRows();
        }
        else
        {
            RefreshRealizedRows();
        }

        UpdateHelpText();
        DataChanged?.Invoke(this, EventArgs.Empty);
        if (_length > 0)
        {
            RevealRow(checked((int)(Math.Clamp(revealOffset, 0, _length - 1) / BytesPerRow)));
        }
    }

    /// <summary>
    /// Sizes the columns for the offset digits the current length needs. The offset column is never
    /// narrower than the digits it shows, so an offset above FFFF is not clipped.
    /// </summary>
    private void UpdateColumns()
    {
        int largest = Math.Max(0, _length - 1);
        int digits = Math.Max(MinimumOffsetDigits, largest.ToString("X", CultureInfo.InvariantCulture).Length);
        _offsetDigits = digits;
        _offsetWidth = _charWidth * digits;
        _hexLeft = _offsetWidth + ColumnGap;
        _asciiLeft = _hexLeft + (BytesPerRow * _byteStep) + ColumnGap;
        _contentWidth = _asciiLeft + (BytesPerRow * _charWidth) + 2;
    }

    private void ResetRows()
    {
        UpdateColumns();
        _repeater.ItemsSource = null;
        foreach (HexRowElement row in _realized)
        {
            row.Detach();
        }

        _realized.Clear();
        _repeater.ItemsSource = new RowIndexList(_rowCount);
    }

    private void RefreshRealizedRows()
    {
        foreach (HexRowElement row in _realized)
        {
            row.Refresh();
        }
    }

    private void PasteFromClipboard() => _ = PasteFromClipboardAsync();

    private async Task PasteFromClipboardAsync()
    {
        if (_isReadOnly)
        {
            return;
        }

        string text;
        try
        {
            DataPackageView content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                _clipboardIssue = "no-text";
                UpdateHelpText();
                return;
            }

            text = await content.GetTextAsync();
        }
        catch (Exception)
        {
            _clipboardIssue = "unavailable";
            UpdateHelpText();
            return;
        }

        byte[] pasted = ParseHexText(text);
        if (pasted.Length == 0)
        {
            _clipboardIssue = "invalid";
            UpdateHelpText();
            return;
        }

        _clipboardIssue = null;
        ApplyPaste(pasted);
    }

    /// <summary>Accepts hex digits with optional spaces, tabs, line breaks, and commas.</summary>
    private static byte[] ParseHexText(string text)
    {
        var digits = new StringBuilder(text.Length);
        foreach (char character in text)
        {
            if (character is ' ' or '\t' or '\r' or '\n' or ',')
            {
                continue;
            }

            if (HexDigit(character) < 0)
            {
                return Array.Empty<byte>();
            }

            digits.Append(character);
        }

        if (digits.Length < 2)
        {
            return Array.Empty<byte>();
        }

        var result = new byte[digits.Length / 2];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = (byte)((HexDigit(digits[index * 2]) << 4) | HexDigit(digits[(index * 2) + 1]));
        }

        return result;
    }

    private void ApplyPaste(byte[] pasted)
    {
        int start = CurrentEditOffset();
        if (start < 0)
        {
            start = 0;
        }

        bool replacedSelection = _selectionLength > 0;
        if (replacedSelection)
        {
            RemoveRange(start, _selectionLength);
        }

        bool rowsChanged = replacedSelection;
        if (_insertMode || replacedSelection || _length == 0)
        {
            for (var index = 0; index < pasted.Length; index++)
            {
                InsertByteAt(start + index, pasted[index]);
            }

            rowsChanged = true;
        }
        else
        {
            int needed = start + pasted.Length;
            if (needed > _length)
            {
                Array.Resize(ref _bytes, needed);
                _length = needed;
                _rowCount = (_length + BytesPerRow - 1) / BytesPerRow;
                rowsChanged = true;
            }

            Array.Copy(pasted, 0, _bytes, start, pasted.Length);
        }

        _nibble = 0;
        _anchor = start;
        _caret = start + pasted.Length - 1;
        SetSelection(start, pasted.Length);
        AfterEdit(start, rowsChanged);
    }

    // E2E-only: debug state as UIA help text. Conditional drops it from ship without leaving _clipboardIssue unread.
    [System.Diagnostics.Conditional("BWC_E2E")]
    private void UpdateHelpText()
    {
        string text =
            $"Length={_length};SelectedOffset={SelectedOffset};SelectedLength={_selectionLength};Mode={Mode};ReadOnly={_isReadOnly}";
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

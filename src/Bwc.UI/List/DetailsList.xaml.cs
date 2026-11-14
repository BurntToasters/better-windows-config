using System.Globalization;
using System.Text;
using Bwc.UI.Theme;
using Bwc.UI.Tree;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
#if BWC_E2E
using Microsoft.UI.Xaml.Automation;
#endif
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Bwc.UI.List;

/// <summary>F2 rename committed on a details row. The host decides what to do with the new name.</summary>
public sealed class ListRenameRequestedEventArgs : EventArgs
{
    public ListRenameRequestedEventArgs(DetailsRow row, string newName)
    {
        Row = row;
        NewName = newName;
    }

    public DetailsRow Row { get; }

    public string NewName { get; }
}

/// <summary>A row was double tapped after it became the single selection.</summary>
public sealed class DetailsRowActivatedEventArgs : EventArgs
{
    public DetailsRowActivatedEventArgs(DetailsRow row, int index)
    {
        Row = row;
        Index = index;
    }

    public DetailsRow Row { get; }

    public int Index { get; }
}

/// <summary>A right-click or menu key request for a details row or the empty list body.</summary>
public sealed class DetailsRowContextRequestedEventArgs : EventArgs
{
    public DetailsRowContextRequestedEventArgs(DetailsRow? row, FrameworkElement element, Point point)
    {
        Row = row;
        Element = element;
        Point = point;
    }

    /// <summary>The row under the pointer, or null when the request came from the empty body.</summary>
    public DetailsRow? Row { get; }

    public FrameworkElement Element { get; }

    public Point Point { get; }
}

/// <summary>
/// Classic details list. A fixed header row sits above a virtualized ItemsRepeater inside a
/// ScrollViewer. Rows are 18 DIP, columns resize with a shared width model, and rows are kept in
/// the classic name order (native collation, default value first). Header clicks do not reorder.
/// Selection and keyboard focus are separate: the focus rectangle belongs to the focused row, which
/// may be unselected. Large lists sort on the thread pool before the result is applied on the UI
/// thread.
/// </summary>
public sealed partial class DetailsList : UserControl
{
    private const double MinColumnWidth = 40;
    private const double MaxColumnWidth = 4096;
    private const int BackgroundSortThreshold = 10_000;
    private const int AutoSizeSampleRows = 200;

    // Mirrors DetailsRowElement.NameLeft, where the name cell text starts.
    private const double RenameLabelLeft = 18;
    private const double RenameChrome = 2;
    private const double MinimumRenameWidth = 40;

    // Windows incremental search: a pause longer than this starts a new type-ahead buffer.
    private const long TypeAheadTimeoutMs = 1000;

    private readonly List<DetailsColumn> _columns = new();
    private readonly HashSet<DetailsRowElement> _realized = new();
    private readonly HashSet<int> _selected = new();
    private readonly TranslateTransform _headerOffset = new();
    private readonly TextBlock _probe;
    private readonly StringBuilder _typeAhead = new();

    private List<DetailsRow> _rows = new();
    private int _anchor = -1;
    private int _focus = -1;
    private int _renameIndex = -1;
    private bool _renaming;
    private bool _listFocused;
    private int _sortVersion;
    private long _typeAheadTick;
    private int _dragColumn = -1;
    private double _dragStartX;
    private double _dragStartWidth;
#if BWC_E2E
    // E2E-only trace in the UIA help text: divider pointer events, the last key, and Shift as seen by KeyDown.
    private int _e2ePresses;
    private int _e2eMoves;
    private int _e2eReleases;
    private int _e2eLost;
    private bool _e2eCaptured;
    private string _e2eLastKey = "none";
    private string _e2ePrevKey = "none";
    private bool _e2eShift;
    private int _e2eRowPresses;
    private int _e2eDoubleTaps;
    private int _e2eRebuilds;
    private string _e2eDoubleTapResult = "none";
#endif

    public DetailsList()
    {
        InitializeComponent();

        _columns.Add(new DetailsColumn("Name", "Name", 120));
        _columns.Add(new DetailsColumn("Type", "Type", 120));
        _columns.Add(new DetailsColumn("Data", "Data", 380));

        Header.Attach(this);
        Header.RenderTransform = _headerOffset;
        Repeater.ItemTemplate = (DataTemplate)Resources["RowTemplate"];
        Repeater.ItemsSource = _rows;
        Repeater.ElementPrepared += OnElementPrepared;
        Repeater.ElementClearing += OnElementClearing;

        _probe = new TextBlock
        {
            Opacity = 0,
            IsHitTestVisible = false,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = DenseMetrics.FontFamily,
            FontSize = DenseMetrics.FontSize,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        HeaderClip.Children.Add(_probe);

        HeaderClip.SizeChanged += OnHeaderClipSizeChanged;
        Body.ViewChanged += OnBodyViewChanged;
        SizeChanged += OnSizeChanged;
        KeyDown += OnKeyDown;
        CharacterReceived += OnCharacterReceived;
        GotFocus += OnListGotFocus;
        LostFocus += OnListLostFocus;
        RightTapped += OnBackgroundRightTapped;
        AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnListPointerMoved), handledEventsToo: true);
        AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnListPointerReleased), handledEventsToo: true);
        AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnListCaptureLost), handledEventsToo: true);
        Body.PointerPressed += OnBodyPressed;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnActualThemeChanged;
        RenameBox.KeyDown += OnRenameKeyDown;
        RenameBox.LostFocus += OnRenameLostFocus;

        ApplyMetrics();
        UpdateHelpText();
    }

    /// <summary>Column model. Widths change through <see cref="SetColumnWidth(string, double)"/>.</summary>
    public IReadOnlyList<DetailsColumn> Columns => _columns;

    /// <summary>Number of rows.</summary>
    public int Count => _rows.Count;

    /// <summary>Rows selected right now, in visible order.</summary>
    public IReadOnlyList<DetailsRow> SelectedRows
    {
        get
        {
            var rows = new List<DetailsRow>(_selected.Count);
            foreach (var index in _selected.OrderBy(i => i))
            {
                if (index >= 0 && index < _rows.Count)
                {
                    rows.Add(_rows[index]);
                }
            }

            return rows;
        }
    }

    /// <summary>Current row height in DIPs.</summary>
    public double RowHeight => DenseMetrics.ListRowHeight;

    /// <summary>Current header height in DIPs.</summary>
    public double HeaderHeight => DenseMetrics.HeaderHeight;

    /// <summary>Sum of all column widths in DIPs.</summary>
    public double ColumnsTotalWidth
    {
        get
        {
            double total = 0;
            foreach (var column in _columns)
            {
                total += column.Width;
            }

            return total;
        }
    }

    /// <summary>Raised after the selected index set changes.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Raised when F2 rename is committed with Enter.</summary>
    public event EventHandler<ListRenameRequestedEventArgs>? RenameRequested;

    /// <summary>
    /// Raised when a row is double tapped. The row is the single selection at that point; header
    /// clicks, the rename box, and multi-selection gestures do not raise it.
    /// </summary>
    public event EventHandler<DetailsRowActivatedEventArgs>? RowActivated;

    /// <summary>
    /// Raised for a right-click on a row or on the empty list body, and for a keyboard menu
    /// request. A row request carries the row; a background request carries null. The member
    /// hides the UIElement event of the same name because the payload is list specific.
    /// </summary>
    public new event EventHandler<DetailsRowContextRequestedEventArgs>? ContextRequested;

    /// <summary>Raised when Delete is pressed with at least one selected value.</summary>
    public event EventHandler? DeleteRequested;

    /// <summary>
    /// Replaces the rows and clears selection, focus, and the type-ahead buffer. The rows are put in
    /// the classic name order. Above 10,000 rows the order is applied when the background sort ends.
    /// </summary>
    public void SetRows(IEnumerable<DetailsRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        int version = ++_sortVersion;
        var list = rows.ToList();
        if (list.Count > BackgroundSortThreshold)
        {
            ResetRows(list);
            _ = SortInBackgroundAsync(version, list);
            return;
        }

        ResetRows(SortRows(list));
    }

    /// <summary>Reapplies the current DenseMetrics values.</summary>
    public void ApplyMetrics()
    {
        HeaderHost.Height = DenseMetrics.HeaderHeight;
        _probe.FontSize = DenseMetrics.FontSize;
        _probe.FontFamily = DenseMetrics.FontFamily;
        Header.ApplyMetrics();

        foreach (var row in _realized.ToArray())
        {
            row.ApplyMetrics();
        }

        if (_renaming)
        {
            PositionRenameBox();
        }
    }

    /// <summary>Sets one column width, clamped to the 40 DIP minimum. Only arrange is invalidated.</summary>
    public void SetColumnWidth(string columnKey, double width)
    {
        var index = FindColumnIndex(columnKey);
        if (index >= 0)
        {
            SetColumnWidth(index, width);
        }
    }

    /// <summary>Starts the inline rename box for the focused or single selected row. F2 uses the same path.</summary>
    public void BeginRenameSelected() => BeginRename();

    internal int IndexOfRow(DetailsRow row) => _rows.IndexOf(row);

    internal bool IsRowSelected(int index) => index >= 0 && _selected.Contains(index);

    /// <summary>True when the row owns the focus rectangle: the list has keyboard focus and this row is focused.</summary>
    internal bool IsFocusedRow(int index) => _listFocused && index == _focus;

    internal DetailsRowElement? RealizedRow(int index)
    {
        foreach (var row in _realized)
        {
            if (row.Index == index)
            {
                return row;
            }
        }

        return null;
    }

    internal HeaderTitle? HeaderTitleAt(int column) => Header.TitleAt(column);

    internal void SelectRow(int index, bool focus)
    {
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        SetSingle(index);
        _focus = index;
        RefreshRealizedRows();
        if (focus)
        {
            _ = Focus(FocusState.Keyboard);
            RevealIndex(index);
        }
    }

    internal void AddRowToSelection(int index)
    {
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        if (_selected.Add(index))
        {
            _anchor = index;
            RefreshRealizedRows();
            RaiseSelectionChanged();
        }
    }

    internal void RemoveRowFromSelection(int index)
    {
        if (_selected.Remove(index))
        {
            if (_selected.Count == 0)
            {
                _anchor = -1;
            }

            RefreshRealizedRows();
            RaiseSelectionChanged();
        }
    }

    internal void RevealIndex(int index)
    {
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        double rowHeight = DenseMetrics.ListRowHeight;
        double top = index * rowHeight;
        double bottom = top + rowHeight;
        double offset = Body.VerticalOffset;
        double viewport = Body.ViewportHeight;
        if (viewport <= 0)
        {
            return;
        }

        if (top < offset)
        {
            Body.ChangeView(null, top, null, disableAnimation: true);
        }
        else if (bottom > offset + viewport)
        {
            Body.ChangeView(null, bottom - viewport, null, disableAnimation: true);
        }
    }

    internal void AutoSizeColumn(int index)
    {
        if (index < 0 || index >= _columns.Count)
        {
            return;
        }

        double width = MeasureText(_columns[index].Header) + 8 + 12;

        // Measuring 200,000 rows on the UI thread would stall input. A bounded prefix plus the
        // realized rows is representative and keeps the resize responsive.
        int sample = Math.Min(_rows.Count, AutoSizeSampleRows);
        for (var i = 0; i < sample; i++)
        {
            width = Math.Max(width, MeasureText(CellText(_rows[i], index)) + 8);
        }

        foreach (var row in _realized)
        {
            if (row.Row is { } details)
            {
                width = Math.Max(width, MeasureText(CellText(details, index)) + 8);
            }
        }

        SetColumnWidth(index, width);
    }

    /// <summary>
    /// Press on a divider grip starts a drag. The list takes the pointer, so the moves and the release
    /// reach the list for the whole drag, not only while the pointer stays over the 4 DIP grip.
    /// </summary>
    internal void OnDividerPressed(int index, PointerRoutedEventArgs e)
    {
#if BWC_E2E
        _e2ePresses++;
        UpdateHelpText();
#endif
        _dragColumn = index;
        _dragStartX = e.GetCurrentPoint(this).Position.X;
        _dragStartWidth = _columns[index].Width;
        bool captured = CapturePointer(e.Pointer);
#if BWC_E2E
        _e2eCaptured = captured;
        UpdateHelpText();
#endif
        e.Handled = true;
    }

    private void OnListPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragColumn < 0)
        {
            return;
        }

#if BWC_E2E
        _e2eMoves++;
        UpdateHelpText();
#endif
        double x = e.GetCurrentPoint(this).Position.X;
        SetColumnWidth(_dragColumn, _dragStartWidth + (x - _dragStartX));
        e.Handled = true;
    }

    private void OnListPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragColumn < 0)
        {
            return;
        }

#if BWC_E2E
        _e2eReleases++;
        UpdateHelpText();
#endif
        _dragColumn = -1;
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    /// <summary>Capture was taken away (for example by a dialog). The drag ends.</summary>
    private void OnListCaptureLost(object sender, PointerRoutedEventArgs e)
    {
#if BWC_E2E
        _e2eLost++;
        UpdateHelpText();
#endif
        _dragColumn = -1;
    }

    /// <summary>
    /// Left press on a row. Plain press selects the row alone. Ctrl toggles it, Shift extends from the
    /// anchor, and Shift with Ctrl adds the range. Right and middle presses change nothing here; a
    /// right click is handled by <see cref="RowRightTapped"/>, which keeps a multi-selection.
    /// </summary>
    internal void RowPressed(DetailsRowElement row, PointerRoutedEventArgs e)
    {
        _ = Focus(FocusState.Pointer);
        e.Handled = true;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        E2ERowPressed();
        int index = row.Index;
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        bool shift = IsShiftDown();
        bool control = IsKeyDown(VirtualKey.Control);
        if (shift && _anchor >= 0)
        {
            SelectRange(_anchor, index, add: control);
        }
        else if (control)
        {
            ToggleSelection(index);
        }
        else
        {
            SetSingle(index);
        }

        _focus = index;
        RefreshRealizedRows();
    }

    /// <summary>
    /// Right click on a row. A row that is already selected keeps the whole selection, so the
    /// context menu applies to it. Any other row becomes the only selection.
    /// </summary>
    internal void RowRightTapped(DetailsRowElement row, RightTappedRoutedEventArgs e)
    {
        int index = row.Index;
        if (_renaming || index < 0 || index >= _rows.Count)
        {
            return;
        }

        if (!_selected.Contains(index))
        {
            SetSingle(index);
        }

        _focus = index;
        RefreshRealizedRows();
        _ = Focus(FocusState.Pointer);
        ContextRequested?.Invoke(
            this,
            new DetailsRowContextRequestedEventArgs(_rows[index], row, e.GetPosition(row)));
    }

    /// <summary>
    /// Called by a row element on double tap. PointerPressed has already applied the selection,
    /// so the event sees the row as selected. Multi-selection and rename are ignored.
    /// </summary>
    internal void RowDoubleTapped(DetailsRowElement row, DoubleTappedRoutedEventArgs e)
    {
        int index = row.Index;
        if (_renaming || index < 0 || index >= _rows.Count)
        {
            E2EDoubleTap(_renaming ? "renaming" : "stale-index");
            return;
        }

        if (_selected.Count != 1 || !_selected.Contains(index))
        {
            E2EDoubleTap($"not-single-selected({_selected.Count})");
            return;
        }

        E2EDoubleTap("activated");
        _focus = index;
        e.Handled = true;
        RowActivated?.Invoke(this, new DetailsRowActivatedEventArgs(_rows[index], index));
    }

#if BWC_E2E
    // E2E-only trace of the row presses and double taps, shown in the UIA help text of the list.
    private void E2ERowPressed()
    {
        _e2eRowPresses++;
        UpdateHelpText();
    }

    private void E2EDoubleTap(string outcome)
    {
        _e2eDoubleTaps++;
        _e2eDoubleTapResult = outcome;
        UpdateHelpText();
    }
#else
    private static void E2ERowPressed()
    {
    }

    private static void E2EDoubleTap(string outcome)
    {
    }
#endif

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not DetailsRowElement row || args.Index < 0 || args.Index >= _rows.Count)
        {
            return;
        }

        row.Attach(this, args.Index, _rows[args.Index], _selected.Contains(args.Index), IsFocusedRow(args.Index));
        _realized.Add(row);
    }

    private void OnElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is DetailsRowElement row)
        {
            _realized.Remove(row);
            row.Detach();
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_renaming)
        {
            return;
        }

        bool shift = IsShiftDown();
        bool control = IsKeyDown(VirtualKey.Control);
#if BWC_E2E
        _e2ePrevKey = _e2eLastKey;
        _e2eLastKey = e.Key.ToString();
        _e2eShift = shift;
        UpdateHelpText();
#endif
        int page = Math.Max(1, (int)(Body.ViewportHeight / DenseMetrics.ListRowHeight));

        switch (e.Key)
        {
            case VirtualKey.Up:
                Navigate(_focus < 0 ? 0 : _focus - 1, shift, control);
                e.Handled = true;
                break;
            case VirtualKey.Down:
                Navigate(_focus < 0 ? 0 : _focus + 1, shift, control);
                e.Handled = true;
                break;
            case VirtualKey.PageUp:
                Navigate((_focus < 0 ? 0 : _focus) - page, shift, control);
                e.Handled = true;
                break;
            case VirtualKey.PageDown:
                Navigate((_focus < 0 ? 0 : _focus) + page, shift, control);
                e.Handled = true;
                break;
            case VirtualKey.Home:
                Navigate(0, shift, control);
                e.Handled = true;
                break;
            case VirtualKey.End:
                Navigate(_rows.Count - 1, shift, control);
                e.Handled = true;
                break;
            case VirtualKey.Space when control:
                if (_focus >= 0)
                {
                    ToggleSelection(_focus);
                    RefreshRealizedRows();
                }

                e.Handled = true;
                break;
            case VirtualKey.A when control:
                SelectAllRows();
                e.Handled = true;
                break;
            case VirtualKey.F2:
                BeginRename();
                e.Handled = true;
                break;
            case VirtualKey.Delete:
                if (_selected.Count > 0)
                {
                    DeleteRequested?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                }

                break;
            case VirtualKey.Enter:
                if (_selected.Count == 1)
                {
                    int index = _selected.First();
                    if (index >= 0 && index < _rows.Count)
                    {
                        _focus = index;
                        RowActivated?.Invoke(this, new DetailsRowActivatedEventArgs(_rows[index], index));
                        e.Handled = true;
                    }
                }

                break;
            case VirtualKey.F10 when shift:
            case VirtualKey.Application:
                RaiseContextAtSelection();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Windows incremental search. Printable characters form a buffer that resets after a one second
    /// pause. A buffer of one repeated character cycles through the values that start with it, from
    /// the value after the focus. Any other buffer selects the first value that starts with it,
    /// starting at the focused value. Ctrl and Alt combinations are not search input.
    /// </summary>
    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (_renaming || _rows.Count == 0)
        {
            return;
        }

        char character = e.Character;
        if (char.IsControl(character) || IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.Menu))
        {
            return;
        }

        long now = Environment.TickCount64;
        if (_typeAhead.Length == 0 || now - _typeAheadTick > TypeAheadTimeoutMs)
        {
            _typeAhead.Clear();
        }

        _typeAheadTick = now;
        _typeAhead.Append(character);
        string buffer = _typeAhead.ToString();
        bool repeated = IsRepeatedCharacter(buffer);
        string prefix = repeated ? buffer[..1] : buffer;
        int start = _focus < 0 ? 0 : (repeated ? _focus + 1 : _focus);
        int match = FindPrefix(prefix, start);
        if (match >= 0)
        {
            SetSingle(match);
            _focus = match;
            RevealIndex(match);
            RefreshRealizedRows();
        }

        e.Handled = true;
    }

    private static bool IsRepeatedCharacter(string text)
    {
        for (var i = 1; i < text.Length; i++)
        {
            if (text[i] != text[0])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>First row at or after <paramref name="start"/> (wrapping) whose name starts with the prefix.</summary>
    private int FindPrefix(string prefix, int start)
    {
        int count = _rows.Count;
        for (var step = 0; step < count; step++)
        {
            int index = (start + step) % count;
            if (_rows[index].Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private void RaiseContextAtSelection()
    {
        int index = _focus;
        if (index < 0 && _selected.Count == 1)
        {
            index = _selected.First();
        }

        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        double y = DenseMetrics.HeaderHeight + (index * DenseMetrics.ListRowHeight) -
            Body.VerticalOffset + (DenseMetrics.ListRowHeight / 2);
        double minimum = DenseMetrics.HeaderHeight + 4;
        var point = new Point(
            24,
            Math.Clamp(y, minimum, Math.Max(minimum, ActualHeight - 4)));
        ContextRequested?.Invoke(
            this,
            new DetailsRowContextRequestedEventArgs(_rows[index], this, point));
    }

    /// <summary>
    /// A press that no row handled, on the scroll viewer, is a press on the empty area below the rows
    /// (or beside them). Presses on a scroll bar belong to the bar and are ignored here.
    /// </summary>
    private void OnBodyPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Handled || IsWithinScrollBar(e.OriginalSource))
        {
            return;
        }

        _ = Focus(FocusState.Pointer);
        e.Handled = true;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ClearSelection();
        }
    }

    private bool IsWithinScrollBar(object? source)
    {
        for (var current = source as DependencyObject;
            current is not null && !ReferenceEquals(current, Body);
            current = VisualTreeHelper.GetParent(current))
        {
            if (current is ScrollBar)
            {
                return true;
            }
        }

        return false;
    }

    private void OnBackgroundRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.Handled || _renaming ||
            IsWithin(e.OriginalSource, HeaderHost) ||
            IsWithin(e.OriginalSource, RenameBox))
        {
            return;
        }

        _ = Focus(FocusState.Pointer);
        ContextRequested?.Invoke(
            this,
            new DetailsRowContextRequestedEventArgs(null, this, e.GetPosition(this)));
        e.Handled = true;
    }

    private static bool IsWithin(object? source, DependencyObject container)
    {
        for (var current = source as DependencyObject;
            current is not null;
            current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, container))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Keyboard navigation. Plain moves select only the target. Ctrl moves the focus and leaves the
    /// selection alone. Shift extends from the anchor, replacing the selection; Shift with Ctrl adds
    /// the range to it.
    /// </summary>
    private void Navigate(int target, bool shift, bool control)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        target = Math.Clamp(target, 0, _rows.Count - 1);
        if (shift)
        {
            int anchor = _anchor >= 0 ? _anchor : (_focus >= 0 ? _focus : target);
            SelectRange(anchor, target, add: control);
            _anchor = anchor;
        }
        else if (!control)
        {
            SetSingle(target);
        }

        _focus = target;
        RevealIndex(target);
        RefreshRealizedRows();
    }

    private void SetSingle(int index)
    {
        bool changed = _selected.Count != 1 || !_selected.Contains(index);
        _selected.Clear();
        _selected.Add(index);
        _anchor = index;
        RefreshRealizedRows();
        if (changed)
        {
            RaiseSelectionChanged();
        }
    }

    private void ToggleSelection(int index)
    {
        if (!_selected.Remove(index))
        {
            _selected.Add(index);
        }

        _anchor = index;
        RefreshRealizedRows();
        RaiseSelectionChanged();
    }

    private void SelectRange(int from, int to, bool add)
    {
        int low = Math.Min(from, to);
        int high = Math.Max(from, to);
        if (!add)
        {
            _selected.Clear();
        }

        for (var i = low; i <= high; i++)
        {
            _selected.Add(i);
        }

        RefreshRealizedRows();
        RaiseSelectionChanged();
    }

    /// <summary>Ctrl+A selects every row and keeps the focus where it is.</summary>
    private void SelectAllRows()
    {
        if (_rows.Count == 0)
        {
            return;
        }

        _selected.Clear();
        for (var i = 0; i < _rows.Count; i++)
        {
            _selected.Add(i);
        }

        if (_focus < 0)
        {
            _focus = 0;
        }

        if (_anchor < 0)
        {
            _anchor = _focus;
        }

        RefreshRealizedRows();
        RaiseSelectionChanged();
    }

    /// <summary>A left press on the empty area below the rows clears the selection and keeps the focus.</summary>
    private void ClearSelection()
    {
        if (_selected.Count == 0)
        {
            return;
        }

        _selected.Clear();
        _anchor = -1;
        RefreshRealizedRows();
        RaiseSelectionChanged();
    }

    /// <summary>Pushes the selection and focus state to every realized row.</summary>
    private void RefreshRealizedRows()
    {
        foreach (var row in _realized)
        {
            row.SetState(_selected.Contains(row.Index), IsFocusedRow(row.Index));
        }
    }

    /// <summary>Replaces the rows. Selection, focus, anchor, and the type-ahead buffer are reset.</summary>
    private void ResetRows(List<DetailsRow> rows)
    {
        _rows = rows;
        _selected.Clear();
        _anchor = -1;
        _focus = -1;
        _typeAhead.Clear();
        EndRenameCore();
#if BWC_E2E
        _e2eRebuilds++;
#endif
        Repeater.ItemsSource = null;
        Repeater.ItemsSource = _rows;
        RaiseSelectionChanged();
    }

    private async Task SortInBackgroundAsync(int version, List<DetailsRow> rows)
    {
        var sorted = await Task.Run(() => SortRows(rows));
        if (version != _sortVersion)
        {
            return;
        }

        ApplySortedRows(sorted);
    }

    /// <summary>
    /// Puts the background-sorted rows in place without changing what the user or the caller sees. The
    /// selection, focus, anchor, and an open rename are kept by row name, so a caller that selected a row
    /// (new value, rename, F5, find) still has it selected when the sort ends.
    /// </summary>
    private void ApplySortedRows(List<DetailsRow> sorted)
    {
        bool InRange(int index) => index >= 0 && index < _rows.Count;

        List<string> selectedNames = _selected.Where(InRange).Select(index => _rows[index].Name).ToList();
        string? focusName = InRange(_focus) ? _rows[_focus].Name : null;
        string? anchorName = InRange(_anchor) ? _rows[_anchor].Name : null;
        string? renameName = _renaming && InRange(_renameIndex) ? _rows[_renameIndex].Name : null;

        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < sorted.Count; i++)
        {
            _ = indexByName.TryAdd(sorted[i].Name, i);
        }

        _rows = sorted;
        _selected.Clear();
        foreach (string name in selectedNames)
        {
            if (indexByName.TryGetValue(name, out int index))
            {
                _ = _selected.Add(index);
            }
        }

        _focus = focusName is not null && indexByName.TryGetValue(focusName, out int focus) ? focus : -1;
        _anchor = anchorName is not null && indexByName.TryGetValue(anchorName, out int anchor) ? anchor : -1;
        _typeAhead.Clear();
        Repeater.ItemsSource = null;
        Repeater.ItemsSource = _rows;

        if (renameName is not null && indexByName.TryGetValue(renameName, out int renameIndex))
        {
            _renameIndex = renameIndex;
            PositionRenameBox();
        }
        else if (_renaming)
        {
            EndRenameCore();
        }

        RaiseSelectionChanged();
    }

    /// <summary>
    /// Classic name order. The default value keys as empty and sorts first. OrderBy is stable, so
    /// equal names keep their enumeration order.
    /// </summary>
    private static List<DetailsRow> SortRows(IEnumerable<DetailsRow> rows)
    {
        return rows.OrderBy(row => ListCollation.SortKey(row.Name), ListCollation.Comparer).ToList();
    }

    private int FindColumnIndex(string key)
    {
        for (var i = 0; i < _columns.Count; i++)
        {
            if (string.Equals(_columns[i].Key, key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private void SetColumnWidth(int index, double width)
    {
        if (index < 0 || index >= _columns.Count)
        {
            return;
        }

        // Widths come from settings and from drags. A value that is not a number is ignored, and the rest
        // are kept inside the classic range, so a corrupt setting cannot make a column unreadable or huge.
        if (!double.IsFinite(width))
        {
            return;
        }

        double clamped = Math.Clamp(Math.Round(width), MinColumnWidth, MaxColumnWidth);
        if (Math.Abs(_columns[index].Width - clamped) < 0.01)
        {
            return;
        }

        _columns[index].Width = clamped;
        Header.NotifyColumnWidthsChanged();
        foreach (var row in _realized)
        {
            row.InvalidateMeasure();
            row.InvalidateArrange();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0 && _columns.Count > 0)
        {
            double others = 0;
            for (var i = 0; i < _columns.Count - 1; i++)
            {
                others += _columns[i].Width;
            }

            double target = Math.Max(MinColumnWidth, e.NewSize.Width - others);
            SetColumnWidth(_columns.Count - 1, target);
        }
    }

    private void OnHeaderClipSizeChanged(object sender, SizeChangedEventArgs e)
    {
        HeaderClip.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, Math.Max(0, e.NewSize.Width), Math.Max(0, e.NewSize.Height)),
        };
    }

    private void OnBodyViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        _headerOffset.X = -Body.HorizontalOffset;
    }

    private void BeginRename()
    {
        int index = _focus;
        if (index < 0 && _selected.Count == 1)
        {
            index = _selected.First();
        }

        if (index < 0 || index >= _rows.Count || !_rows[index].Renameable)
        {
            return;
        }

        _renaming = true;
        _renameIndex = index;
        RenameBox.Text = _rows[index].Name;
        RenameBox.Visibility = Visibility.Visible;
        PositionRenameBox();
        RenameBox.Focus(FocusState.Programmatic);
        // Select the whole name with the caret at the start, so a long name shows its beginning.
        RenameBox.SelectionStart = 0;
        RenameBox.SelectionLength = RenameBox.Text.Length;
    }

    private void PositionRenameBox()
    {
        if (_renameIndex < 0 || _renameIndex >= _rows.Count)
        {
            return;
        }

        double rowHeight = DenseMetrics.ListRowHeight;
        double labelLeft = RenameLabelLeft;
        double columnWidth = _columns.Count > 0 ? _columns[0].Width : 120;
        double left = labelLeft;
        double visible = Math.Max(0, columnWidth - left - 1);
        double labelWidth = MeasureTextWidth(_rows[_renameIndex].Name);
        double width = Math.Min(
            Math.Max(MinimumRenameWidth, labelWidth + RenameChrome),
            Math.Max(MinimumRenameWidth, visible));

        Canvas.SetLeft(RenameBox, left);
        Canvas.SetTop(RenameBox, _renameIndex * rowHeight);
        RenameBox.Width = width;
        RenameBox.Height = rowHeight + 2;
        RenameBox.FontSize = DenseMetrics.FontSize;
        RenameBox.FontFamily = DenseMetrics.FontFamily;
#if BWC_E2E
        // E2E-only geometry trace: the rename box and the name cell it replaces, in content DIPs.
        AutomationProperties.SetHelpText(
            RenameBox,
            string.Create(
                CultureInfo.InvariantCulture,
                $"LabelLeft={labelLeft:0.##};LabelWidth={labelWidth:0.##};VisibleWidth={visible:0.##};BoxLeft={left:0.##};BoxWidth={width:0.##};BoxHeight={RenameBox.Height:0.##}"));
#endif
    }

    /// <summary>The width of the value name at the current font, in DIPs.</summary>
    private static double MeasureTextWidth(string text)
    {
        var probe = new TextBlock
        {
            Text = text,
            FontFamily = DenseMetrics.FontFamily,
            FontSize = DenseMetrics.FontSize,
            TextWrapping = TextWrapping.NoWrap,
        };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }

    private void OnRenameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            CommitRename();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            CancelRename();
            e.Handled = true;
        }
    }

    private void OnRenameLostFocus(object sender, RoutedEventArgs e)
    {
        // Classic regedit commits the typed name when focus leaves the box, as the tree rename does.
        if (_renaming)
        {
            CommitRename();
        }
    }

    private void CommitRename()
    {
        if (!_renaming || _renameIndex < 0 || _renameIndex >= _rows.Count)
        {
            return;
        }

        var row = _rows[_renameIndex];
        string newName = RenameBox.Text;
        EndRenameCore();
        RenameRequested?.Invoke(this, new ListRenameRequestedEventArgs(row, newName));
    }

    private void CancelRename()
    {
        EndRenameCore();
    }

    private void EndRenameCore()
    {
        if (!_renaming)
        {
            return;
        }

        _renaming = false;
        _renameIndex = -1;
        RenameBox.Visibility = Visibility.Collapsed;
        _ = Focus(FocusState.Keyboard);
    }

    private string CellText(DetailsRow row, int column)
    {
        return column switch
        {
            0 => row.Name,
            1 => row.Type,
            _ => row.Data,
        };
    }

    private double MeasureText(string text)
    {
        _probe.Text = text;
        _probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return _probe.DesiredSize.Width;
    }

    private static bool IsKeyDown(VirtualKey key)
    {
        return (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
    }

    /// <summary>Either Shift key. The generic key and the side-specific keys are all checked.</summary>
    private static bool IsShiftDown() =>
        IsKeyDown(VirtualKey.Shift) || IsKeyDown(VirtualKey.LeftShift) || IsKeyDown(VirtualKey.RightShift);

    private void RaiseSelectionChanged()
    {
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        UpdateHelpText();
    }

    private void UpdateHelpText()
    {
#if BWC_E2E
        // E2E-only: debug counts in UIA help text would be announced by screen readers in ship builds.
        AutomationProperties.SetHelpText(
            this,
            $"Rows={_rows.Count};Selected={_selected.Count};Focus={_focus};Anchor={_anchor};" +
            $"Presses={_e2ePresses};Captured={_e2eCaptured};Moves={_e2eMoves};Releases={_e2eReleases};Lost={_e2eLost};" +
            $"Key={_e2eLastKey};PrevKey={_e2ePrevKey};Shift={_e2eShift};Width0={_columns[0].Width};" +
            $"RowPresses={_e2eRowPresses};DoubleTaps={_e2eDoubleTaps};TapResult={_e2eDoubleTapResult};" +
            $"Rebuilds={_e2eRebuilds}");
#endif
    }

    private void OnListGotFocus(object sender, RoutedEventArgs e)
    {
        _listFocused = true;
        RefreshRealizedRows();
    }

    private void OnListLostFocus(object sender, RoutedEventArgs e)
    {
        _listFocused = false;
        RefreshRealizedRows();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DenseMetrics.Changed += OnMetricsChanged;
        ApplyMetrics();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _sortVersion++;
        DenseMetrics.Changed -= OnMetricsChanged;
        EndRenameCore();
    }

    private void OnMetricsChanged(object? sender, EventArgs e)
    {
        ApplyMetrics();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        DenseTheme.Reset();
        Header.RefreshTheme();
        foreach (var row in _realized)
        {
            row.ApplyTheme();
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new DetailsListAutomationPeer(this);
}

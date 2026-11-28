using System.Globalization;
using System.Text;
using Bwc.UI.Console.Shell;
using Bwc.UI.Theme;
using Bwc.UI.Tree;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Bwc.UI.Console.List;

/// <summary>
/// The result list of the console frame. Columns and rows come from the caller; the list owns the view mode, the
/// sort, the column widths, the selection and the focus. Items are virtualized in every view mode, so a list of
/// thousands of rows has a few dozen elements. Selection, focus and anchor are held as rows, never as indexes, so a
/// sort, a view switch or a row replacement cannot move them to other rows. Failure modes:
/// docs/failure-modes/console-list.md.
/// </summary>
public sealed partial class ConsoleList : UserControl
{
    /// <summary>
    /// The narrowest a column can be made, in DIPs. A BWC choice: a zero width column cannot be grabbed
    /// again.
    /// </summary>
    public const double MinColumnWidth = 12;

    /// <summary>The widest a column can be made, in DIPs.</summary>
    public const double MaxColumnWidth = 4096;

    private const double DefaultColumnWidth = 120;
    private const int LabelSampleRows = 500;
    private const int AutoFitFirstRows = 200;
    private const int AutoFitLongestRows = 100;
    private const long TypeAheadTimeoutMs = 1000;
    private const double HorizontalKeyStep = 16;

    private readonly List<ConsoleListColumn> _all = [];
    private readonly List<ConsoleListColumn> _visible = [];
    private readonly HashSet<ConsoleListItemElement> _realized = [];
    private readonly HashSet<ConsoleListRow> _selected = [];
    private readonly TranslateTransform _headerOffset = new();
    private readonly TextBlock _probe;
    private readonly StringBuilder _typeAhead = new();

    private List<ConsoleListRow> _source = [];
    private List<ConsoleListRow> _rows = [];
    private ConsoleListRow? _focusRow;
    private ConsoleListRow? _anchorRow;
    private ConsoleListViewMode _mode = ConsoleListViewMode.Detail;
    private readonly List<ConsoleListSortKey> _sortStack = [];
    private string? _sortColumnId;
    private ConsoleListSortDirection _sortDirection = ConsoleListSortDirection.None;
    private double _labelItemWidth = 120;
    private bool _listFocused;
    private long _typeAheadTick;
    private ConsoleListColumn? _dragColumn;
    private double _dragStartX;
    private double _dragStartWidth;
    private XamlRoot? _observedRoot;
    private double _observedScale;
    private bool _metricsSubscribed;
    private int _selectionEvents;

    public ConsoleList()
    {
        InitializeComponent();
        AutomationProperties.SetName(this, ConsoleStrings.Get("ConsoleListName"));
        AutomationProperties.SetName(HeaderHost, ConsoleStrings.Get("ConsoleListHeaderName"));

        Header.Attach(this);
        Header.RenderTransform = _headerOffset;
        HeaderHost.Background = BwcBrushes.Face;
        Repeater.ElementPrepared += OnElementPrepared;
        Repeater.ElementClearing += OnElementClearing;
        Repeater.ItemsSource = _rows;

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
        Body.PointerWheelChanged += OnBodyWheel;
        Body.PointerPressed += OnBodyPressed;
        SizeChanged += (_, _) => UpdateHelpText();
        KeyDown += OnKeyDown;
        CharacterReceived += OnCharacterReceived;
        GotFocus += OnListGotFocus;
        LostFocus += OnListLostFocus;
        RightTapped += OnBackgroundRightTapped;
        AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnListPointerMoved), handledEventsToo: true);
        AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnListPointerReleased), handledEventsToo: true);
        AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnListCaptureLost), handledEventsToo: true);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) => RefreshTheme();

        ApplyMetrics();
        ApplyLayout();
        UpdateHelpText();
    }

    // ---- events -----------------------------------------------------------------------------------

    /// <summary>Raised after the set of selected rows changed.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Raised for Enter, a double click on a row and UI Automation Invoke. The row is the focused one.</summary>
    public event EventHandler<ConsoleListItemEventArgs>? ItemActivated;

    /// <summary>
    /// Raised once for a right click on a row, the empty area or a header, and for Shift+F10 and the context-menu key
    /// when the list gets the key. Inside the console shell the shell sees the keys first and the host calls
    /// <see cref="RaiseContextAtFocus"/>.
    /// </summary>
    public event EventHandler<ConsoleListContextEventArgs>? ContextMenuRequested;

    /// <summary>Raised after the sort mark changed (a header click, <see cref="Sort"/>, a hidden sort column).</summary>
    public event EventHandler? SortChanged;

    /// <summary>Raised after the view mode changed.</summary>
    public event EventHandler? ViewModeChanged;

    /// <summary>
    /// Raised after a column width, visibility or order was changed by the user or by a setter, not by
    /// ApplyState.
    /// </summary>
    public event EventHandler? ColumnStateChanged;

    // ---- model ------------------------------------------------------------------------------------

    /// <summary>All columns, hidden ones included, in display order.</summary>
    public IReadOnlyList<ConsoleListColumn> Columns => _all;

    /// <summary>The visible columns in display order.</summary>
    public IReadOnlyList<ConsoleListColumn> VisibleColumns => _visible;

    /// <summary>The number of rows.</summary>
    public int Count => _rows.Count;

    /// <summary>The rows as displayed (after the sort).</summary>
    public IReadOnlyList<ConsoleListRow> Rows => _rows;

    /// <summary>The selected rows in display order.</summary>
    public IReadOnlyList<ConsoleListRow> SelectedRows => _selected.OrderBy(row => row.Index).ToList();

    /// <summary>The focused row (the one with the dotted rectangle), or null.</summary>
    public ConsoleListRow? FocusedRow => _focusRow;

    /// <summary>The display index of the focused row, or -1.</summary>
    public int FocusedIndex => _focusRow?.Index ?? -1;

    /// <summary>The sorted column, or null while the caller's order is shown.</summary>
    public string? SortColumnId => _sortDirection == ConsoleListSortDirection.None ? null : _sortColumnId;

    /// <summary>The direction of the sort.</summary>
    public ConsoleListSortDirection SortDirection => _sortDirection;

    /// <summary>
    /// False (the default): a sort is by one column, stable against the caller's order. True: a sort sorts the order shown
    /// now, so a tie keeps the order the earlier sorts made (<see cref="SortStack"/>). The Local Group Policy Editor sets it.
    /// </summary>
    public bool CumulativeSort { get; set; }

    /// <summary>
    /// The columns sorted so far, oldest first, one entry per column with its last direction. Empty unless
    /// <see cref="CumulativeSort"/> is set. The last entry is the sort mark. Replaying the entries in order over the caller's
    /// order gives the order shown, so the stack is all a caller needs to keep or restore.
    /// </summary>
    public IReadOnlyList<ConsoleListSortKey> SortStack => _sortStack.ToArray();

    /// <summary>The view mode. A change keeps the selection and the focus and reveals the focused row.</summary>
    public ConsoleListViewMode ViewMode
    {
        get => _mode;
        set => SetViewMode(value);
    }

    /// <summary>The accessible name of the list. The default is the internal caption the classic list carries.</summary>
    public string ListName
    {
        get => AutomationProperties.GetName(this);
        set => AutomationProperties.SetName(this, value ?? string.Empty);
    }

    /// <summary>
    /// The UIA name of an item. The default is the label (the first cell). The Local Group Policy host appends the word
    /// the classic host appends; this control adds nothing itself.
    /// </summary>
    public Func<ConsoleListRow, string>? ItemNameFormatter { get; set; }

    /// <summary>
    /// True (the default) lets the list hold several selected rows. False keeps one: Ctrl+A does nothing, and Ctrl+Space,
    /// Ctrl+click, Shift+click and Shift+arrow select only the row they reach.
    /// </summary>
    public bool MultiSelect { get; set; } = true;

    /// <summary>The sum of the visible column widths in DIPs.</summary>
    public double ColumnsTotalWidth => _visible.Sum(column => column.Width);

    internal double LabelItemWidth => _labelItemWidth;

    internal ScrollViewer ScrollHost => Body;

    internal ConsoleListHeader HeaderPanel => Header;

    /// <summary>Row height in DIPs in Detail, Small Icons and List.</summary>
    public double RowHeight => ConsoleListMetrics.RowHeight;

    /// <summary>Header height in DIPs.</summary>
    public double HeaderHeight => ConsoleListMetrics.HeaderHeight;

    /// <summary>Replaces the columns. The rows keep their cells; a sort by a column that is gone is cleared.</summary>
    public void SetColumns(IEnumerable<ConsoleListColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        List<ConsoleListColumn> list = columns.ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ConsoleListColumn column in list)
        {
            if (!seen.Add(column.Id))
            {
                throw new ArgumentException($"The column id '{column.Id}' is used twice.", nameof(columns));
            }
        }

        for (int i = 0; i < list.Count; i++)
        {
            list[i].Index = i;
        }

        _all.Clear();
        _all.AddRange(list);
        RebuildVisible();
        if (CumulativeSort && _sortStack.RemoveAll(key => !seen.Contains(key.ColumnId)) > 0)
        {
            SyncSortFromStack();
            ApplySort();
            Rebind(preserveScroll: true);
        }
        else if (!CumulativeSort && _sortColumnId is not null && !seen.Contains(_sortColumnId))
        {
            _sortColumnId = null;
            _sortDirection = ConsoleListSortDirection.None;
            ApplySort();
            Rebind(preserveScroll: true);
        }

        ColumnsChanged();
    }

    /// <summary>
    /// Replaces the rows. The rows are shown in the current sort. The selection, the focus and the anchor are cleared,
    /// unless <paramref name="keepSelectionByTag"/> is set: then the rows whose tag equals the tag of a selected,
    /// focused or anchor row of the old list take their place, and the others are dropped.
    /// </summary>
    public void SetRows(IEnumerable<ConsoleListRow> rows, bool keepSelectionByTag = false)
    {
        ArgumentNullException.ThrowIfNull(rows);
        List<ConsoleListRow> list = rows.ToList();
        if (list.Any(row => row is null))
        {
            throw new ArgumentException("A row is null.", nameof(rows));
        }

        List<object> selectedTags = [];
        object? focusTag = null;
        object? anchorTag = null;
        if (keepSelectionByTag)
        {
            selectedTags = _selected.Select(row => row.Tag).Where(tag => tag is not null).Cast<object>().ToList();
            focusTag = _focusRow?.Tag;
            anchorTag = _anchorRow?.Tag;
        }

        bool hadSelection = _selected.Count > 0;
        foreach (ConsoleListRow old in _source)
        {
            old.Index = -1;
        }

        _source = list;
        _selected.Clear();
        _focusRow = null;
        _anchorRow = null;
        _typeAhead.Clear();
        ApplySort();

        if (keepSelectionByTag && (selectedTags.Count > 0 || focusTag is not null))
        {
            var byTag = new Dictionary<object, ConsoleListRow>();
            foreach (ConsoleListRow row in _rows)
            {
                if (row.Tag is { } tag)
                {
                    _ = byTag.TryAdd(tag, row);
                }
            }

            foreach (object tag in selectedTags)
            {
                if (byTag.TryGetValue(tag, out ConsoleListRow? row))
                {
                    _ = _selected.Add(row);
                }
            }

            _focusRow = focusTag is not null && byTag.TryGetValue(focusTag, out ConsoleListRow? focus) ? focus : null;
            _anchorRow = anchorTag is not null && byTag.TryGetValue(anchorTag, out ConsoleListRow? anchor) ? anchor : null;
        }

        RecomputeLabelWidth();
        Rebind(preserveScroll: true);
        if (hadSelection || _selected.Count > 0)
        {
            RaiseSelectionChanged();
        }
        else
        {
            UpdateHelpText();
        }
    }

    /// <summary>Sets a column width, clamped to the limits. Raises <see cref="ColumnStateChanged"/>.</summary>
    public void SetColumnWidth(string columnId, double width)
    {
        ConsoleListColumn? column = FindColumn(columnId);
        if (column is not null && SetWidthCore(column, width))
        {
            ColumnStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Shows or hides a column. Hiding the sort column clears the sort.</summary>
    public void SetColumnVisible(string columnId, bool visible)
    {
        ConsoleListColumn? column = FindColumn(columnId);
        if (column is null || column.Visible == visible)
        {
            return;
        }

        column.Visible = visible;
        RebuildVisible();
        bool sortCleared = ClearSortIfHidden();
        ColumnsChanged();
        if (sortCleared)
        {
            SortChanged?.Invoke(this, EventArgs.Empty);
        }

        ColumnStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Sets the display order of the columns. Ids that are unknown or repeated are ignored; columns that are not named
    /// follow in their current order.
    /// </summary>
    public void SetColumnOrder(IEnumerable<string> columnIds)
    {
        ArgumentNullException.ThrowIfNull(columnIds);
        ReorderColumns(columnIds);
        RebuildVisible();
        ColumnsChanged();
        ColumnStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Fits a column to its header and to the widest of the first rows, the longest rows and the realized
    /// rows.
    /// </summary>
    public void AutoFitColumn(string columnId)
    {
        ConsoleListColumn? column = FindColumn(columnId);
        if (column is null)
        {
            return;
        }

        bool first = _visible.Count > 0 && ReferenceEquals(_visible[0], column);
        double inset = first && _mode == ConsoleListViewMode.Detail ? ConsoleListMetrics.LabelLeft : ConsoleListMetrics.CellPad;
        double width = MeasureText(column.Header) + (ConsoleListMetrics.CellPad * 2);

        // Measuring every row of a large list on the UI thread would stall input. The first rows, the longest texts
        // (the widest text of a proportional font is nearly always one of them) and the realized rows are measured.
        var candidates = new HashSet<ConsoleListRow>(_rows.Take(AutoFitFirstRows));
        if (_rows.Count > AutoFitFirstRows)
        {
            foreach (ConsoleListRow row in _rows.OrderByDescending(r => r.CellText(column.Index).Length).Take(AutoFitLongestRows))
            {
                _ = candidates.Add(row);
            }
        }

        foreach (ConsoleListItemElement item in _realized)
        {
            if (item.Row is { } realizedRow)
            {
                _ = candidates.Add(realizedRow);
            }
        }

        foreach (ConsoleListRow row in candidates)
        {
            width = Math.Max(width, MeasureText(row.CellText(column.Index)) + inset + ConsoleListMetrics.CellPad + 2);
        }

        if (SetWidthCore(column, width))
        {
            ColumnStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Sorts by a column, or clears the sort when the column is null or the direction is None. The sort is stable:
    /// rows with an equal key keep the caller's order in both directions.
    /// </summary>
    public void Sort(string? columnId, ConsoleListSortDirection direction)
    {
        ConsoleListColumn? column = columnId is null ? null : FindColumn(columnId);
        if (column is null || direction == ConsoleListSortDirection.None)
        {
            columnId = null;
            direction = ConsoleListSortDirection.None;
        }

        if (_sortColumnId == columnId && _sortDirection == direction)
        {
            return;
        }

        _sortColumnId = columnId;
        _sortDirection = direction;
        if (CumulativeSort)
        {
            PushSortKey(columnId, direction);
        }

        ApplySort();
        Rebind(preserveScroll: false);
        Header.RefreshSort();
        ScrollToFocusOrStart();
        SortChanged?.Invoke(this, EventArgs.Empty);
        UpdateHelpText();
    }

    /// <summary>
    /// Replaces the sort stack of a cumulative list (see <see cref="SortStack"/>). A key of a column that is not shown, a
    /// direction of None and a column that repeats (the last entry wins) are dropped. Raises no event: the caller made the
    /// change. The rows are shown in the new order at once.
    /// </summary>
    public void SetSortStack(IEnumerable<ConsoleListSortKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var wanted = new List<ConsoleListSortKey>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ConsoleListSortKey key in keys.Reverse())
        {
            if (key.Direction != ConsoleListSortDirection.None && FindColumn(key.ColumnId) is { Visible: true } && seen.Add(key.ColumnId))
            {
                wanted.Add(key);
            }
        }

        wanted.Reverse();
        if (wanted.SequenceEqual(_sortStack))
        {
            return;
        }

        _sortStack.Clear();
        _sortStack.AddRange(wanted);
        SyncSortFromStack();
        ApplySort();
        Rebind(preserveScroll: true);
        UpdateHelpText();
    }

    private void PushSortKey(string? columnId, ConsoleListSortDirection direction)
    {
        if (columnId is null || direction == ConsoleListSortDirection.None)
        {
            _sortStack.Clear();
            return;
        }

        _ = _sortStack.RemoveAll(key => key.ColumnId == columnId);
        _sortStack.Add(new ConsoleListSortKey(columnId, direction));
    }

    /// <summary>Makes the sort mark the newest entry of the stack, or none when the stack is empty.</summary>
    private void SyncSortFromStack()
    {
        if (_sortStack.Count == 0)
        {
            _sortColumnId = null;
            _sortDirection = ConsoleListSortDirection.None;
        }
        else
        {
            _sortColumnId = _sortStack[^1].ColumnId;
            _sortDirection = _sortStack[^1].Direction;
        }
    }

    /// <summary>The state a caller persists: the columns in display order, the sort and the view mode.</summary>
    public ConsoleListState GetState() => new(
        _all.Select(column => new ConsoleListColumnState(column.Id, column.Width, column.Visible)).ToList(),
        SortColumnId,
        _sortDirection,
        _mode);

    /// <summary>
    /// Applies a persisted state. A column the list does not define, a repeated id, a width that is not a number and a
    /// sort by an unknown or hidden column are ignored; columns the state does not name keep their place after the
    /// named ones. Raises <see cref="SortChanged"/> and <see cref="ViewModeChanged"/> when those changed, but not
    /// <see cref="ColumnStateChanged"/>: the caller made the change.
    /// </summary>
    public void ApplyState(ConsoleListState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ReorderColumns(state.Columns.Select(column => column.Id));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ConsoleListColumnState entry in state.Columns)
        {
            if (!seen.Add(entry.Id) || FindColumn(entry.Id) is not { } column)
            {
                continue;
            }

            _ = SetWidthCore(column, entry.Width);
            column.Visible = entry.Visible;
        }

        RebuildVisible();
        string? previousSort = SortColumnId;
        ConsoleListSortDirection previousDirection = _sortDirection;
        ConsoleListColumn? sortColumn = state.SortColumnId is null ? null : FindColumn(state.SortColumnId);
        if (sortColumn is { Visible: true } && state.SortDirection != ConsoleListSortDirection.None)
        {
            _sortColumnId = sortColumn.Id;
            _sortDirection = state.SortDirection;
        }
        else
        {
            _sortColumnId = null;
            _sortDirection = ConsoleListSortDirection.None;
        }

        if (CumulativeSort)
        {
            _sortStack.Clear();
            if (_sortColumnId is not null)
            {
                _sortStack.Add(new ConsoleListSortKey(_sortColumnId, _sortDirection));
            }
        }

        bool sortChanged = previousSort != SortColumnId || previousDirection != _sortDirection;
        ApplySort();
        ColumnsChanged();
        if (sortChanged)
        {
            SortChanged?.Invoke(this, EventArgs.Empty);
        }

        if (Enum.IsDefined(state.ViewMode))
        {
            SetViewMode(state.ViewMode);
        }
    }

    /// <summary>Puts the keyboard focus in the list.</summary>
    public void FocusList() => _ = Focus(FocusState.Keyboard);

    /// <summary>
    /// Raises <see cref="ItemActivated"/> for the focused row, or for the single selected row. Nothing
    /// without one.
    /// </summary>
    public void ActivateFocused()
    {
        ConsoleListRow? row = _focusRow;
        if (row is null && _selected.Count == 1)
        {
            row = _selected.First();
        }

        if (row is not null)
        {
            ActivateIndex(row.Index);
        }
    }

    /// <summary>
    /// Raises <see cref="ContextMenuRequested"/> for the focused row (the single selected row when none is focused), or
    /// for the background when there is none. The position is where a menu of the keyboard should appear.
    /// </summary>
    public void RaiseContextAtFocus()
    {
        ConsoleListRow? row = _focusRow;
        if (row is null && _selected.Count == 1)
        {
            row = _selected.First();
        }

        if (row is null || row.Index < 0)
        {
            ContextMenuRequested?.Invoke(
                this,
                new ConsoleListContextEventArgs(ConsoleListContextArea.Background, null, null, new Point(24, HeaderTop() + 4), fromKeyboard: true));
            return;
        }

        ContextMenuRequested?.Invoke(
            this,
            new ConsoleListContextEventArgs(ConsoleListContextArea.Item, row, null, ItemPosition(row.Index), fromKeyboard: true));
    }

    /// <summary>Selects one row and optionally gives it the focus. Out of range is ignored.</summary>
    public void SelectIndex(int index, bool focus)
    {
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        SetSingle(_rows[index]);
        _focusRow = _rows[index];
        RefreshRealized();
        if (focus)
        {
            FocusList();
        }

        RevealIndex(index);
        UpdateHelpText();
    }

    /// <summary>Scrolls so the item is inside the viewport. Out of range is ignored.</summary>
    public void RevealIndex(int index)
    {
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        Rect item = ItemContentRect(index);
        if (_mode == ConsoleListViewMode.List)
        {
            double offset = Body.HorizontalOffset;
            double viewport = Body.ViewportWidth;
            if (viewport <= 0)
            {
                return;
            }

            if (item.Left < offset)
            {
                _ = Body.ChangeView(item.Left, null, null, disableAnimation: true);
            }
            else if (item.Right > offset + viewport)
            {
                _ = Body.ChangeView(item.Right - viewport, null, null, disableAnimation: true);
            }

            return;
        }

        double top = Body.VerticalOffset;
        double height = Body.ViewportHeight;
        if (height <= 0)
        {
            return;
        }

        if (item.Top < top)
        {
            _ = Body.ChangeView(null, item.Top, null, disableAnimation: true);
        }
        else if (item.Bottom > top + height)
        {
            _ = Body.ChangeView(null, item.Bottom - height, null, disableAnimation: true);
        }
    }

    // ---- internal surface for the item, header and peers ------------------------------------------

    internal bool IsRowSelected(int index) => index >= 0 && index < _rows.Count && _selected.Contains(_rows[index]);

    internal string ItemAutomationName(ConsoleListRow row) => ItemNameFormatter?.Invoke(row) ?? row.Text;

    internal ConsoleListItemElement? RealizedItem(int index)
    {
        foreach (ConsoleListItemElement item in _realized)
        {
            if (item.Index == index)
            {
                return item;
            }
        }

        return null;
    }

    internal IEnumerable<ConsoleListItemElement> RealizedItems => _realized;

    internal void AddIndexToSelection(int index)
    {
        if (!MultiSelect)
        {
            if (index >= 0 && index < _rows.Count)
            {
                SetSingle(_rows[index]);
            }

            return;
        }

        if (index >= 0 && index < _rows.Count && _selected.Add(_rows[index]))
        {
            _anchorRow = _rows[index];
            RefreshRealized();
            RaiseSelectionChanged();
        }
    }

    internal void RemoveIndexFromSelection(int index)
    {
        if (index >= 0 && index < _rows.Count && _selected.Remove(_rows[index]))
        {
            RefreshRealized();
            RaiseSelectionChanged();
        }
    }

    internal void ActivateIndex(int index)
    {
        if (index >= 0 && index < _rows.Count)
        {
            _focusRow = _rows[index];
            ItemActivated?.Invoke(this, new ConsoleListItemEventArgs(_rows[index], index));
        }
    }

    internal static double ClampWidth(double width) =>
        double.IsFinite(width) ? Math.Clamp(Math.Round(width), MinColumnWidth, MaxColumnWidth) : DefaultColumnWidth;

    // ---- columns ----------------------------------------------------------------------------------

    private ConsoleListColumn? FindColumn(string id) => _all.FirstOrDefault(column => string.Equals(column.Id, id, StringComparison.Ordinal));

    private void RebuildVisible()
    {
        _visible.Clear();
        _visible.AddRange(_all.Where(column => column.Visible));
    }

    private void ReorderColumns(IEnumerable<string> ids)
    {
        var ordered = new List<ConsoleListColumn>(_all.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            if (FindColumn(id) is { } column && seen.Add(id))
            {
                ordered.Add(column);
            }
        }

        foreach (ConsoleListColumn column in _all)
        {
            if (seen.Add(column.Id))
            {
                ordered.Add(column);
            }
        }

        _all.Clear();
        _all.AddRange(ordered);
    }

    private bool ClearSortIfHidden()
    {
        if (_sortColumnId is not null && FindColumn(_sortColumnId) is not { Visible: true })
        {
            _sortColumnId = null;
            _sortDirection = ConsoleListSortDirection.None;
            _sortStack.Clear();
            ApplySort();
            return true;
        }

        return false;
    }

    private bool SetWidthCore(ConsoleListColumn column, double width)
    {
        // A value that is not a number is ignored, so a corrupt state cannot make a column unreadable or huge.
        if (!double.IsFinite(width))
        {
            return false;
        }

        double clamped = ClampWidth(width);
        if (Math.Abs(column.Width - clamped) < 0.01)
        {
            return false;
        }

        column.SetWidth(clamped);
        Header.NotifyWidthsChanged();
        foreach (ConsoleListItemElement item in _realized)
        {
            item.InvalidateMeasure();
            item.InvalidateArrange();
        }

        UpdateHelpText();
        return true;
    }

    /// <summary>The columns changed: headers, cells, label width and the repeater are rebuilt.</summary>
    private void ColumnsChanged()
    {
        Header.Rebuild();
        RecomputeLabelWidth();
        foreach (ConsoleListItemElement item in _realized)
        {
            item.Refresh();
        }

        Rebind(preserveScroll: true);
    }

    internal void HeaderClicked(ConsoleListColumn column)
    {
        ConsoleListSortDirection next = SortColumnId == column.Id && _sortDirection == ConsoleListSortDirection.Ascending
            ? ConsoleListSortDirection.Descending
            : ConsoleListSortDirection.Ascending;
        Sort(column.Id, next);
    }

    internal void HeaderRightTapped(ConsoleListColumn column, RightTappedRoutedEventArgs e)
    {
        ContextMenuRequested?.Invoke(
            this,
            new ConsoleListContextEventArgs(ConsoleListContextArea.Header, null, column.Id, e.GetPosition(this), fromKeyboard: false));
    }

    internal void DividerPressed(ConsoleListColumn column, PointerRoutedEventArgs e)
    {
        _dragColumn = column;
        _dragStartX = e.GetCurrentPoint(this).Position.X;
        _dragStartWidth = column.Width;
        _ = CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnListPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragColumn is null)
        {
            return;
        }

        double x = e.GetCurrentPoint(this).Position.X;
        _ = SetWidthCore(_dragColumn, _dragStartWidth + (x - _dragStartX));
        e.Handled = true;
    }

    private void OnListPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragColumn is null)
        {
            return;
        }

        bool moved = Math.Abs(_dragColumn.Width - _dragStartWidth) > 0.01;
        _dragColumn = null;
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        if (moved)
        {
            ColumnStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Capture was taken away (for example by a dialog). The drag ends.</summary>
    private void OnListCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        bool moved = _dragColumn is not null && Math.Abs(_dragColumn.Width - _dragStartWidth) > 0.01;
        _dragColumn = null;
        if (moved)
        {
            ColumnStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---- sort -------------------------------------------------------------------------------------

    /// <summary>Builds the displayed order from the caller's order and the sort mark. Stable in both directions.</summary>
    private void ApplySort()
    {
        ConsoleListColumn? column = _sortColumnId is null || _sortDirection == ConsoleListSortDirection.None
            ? null
            : FindColumn(_sortColumnId);
        if (column is null)
        {
            _rows = new List<ConsoleListRow>(_source);
        }
        else
        {
            IComparer<ConsoleListRow> comparer = column.Comparer ?? DefaultComparer(column.Index);
            try
            {
                // A cumulative list replays every key of the stack, oldest first, each a stable sort of the order the earlier
                // ones made; the newest key decides, and a tie keeps the order before it.
                IEnumerable<ConsoleListRow> ordered = _source;
                if (CumulativeSort && _sortStack.Count > 0)
                {
                    foreach (ConsoleListSortKey key in _sortStack)
                    {
                        if (FindColumn(key.ColumnId) is not { } keyColumn || key.Direction == ConsoleListSortDirection.None)
                        {
                            continue;
                        }

                        IComparer<ConsoleListRow> keyComparer = keyColumn.Comparer ?? DefaultComparer(keyColumn.Index);
                        ordered = key.Direction == ConsoleListSortDirection.Ascending
                            ? ordered.OrderBy(row => row, keyComparer)
                            : ordered.OrderByDescending(row => row, keyComparer);
                    }
                }
                else
                {
                    ordered = _sortDirection == ConsoleListSortDirection.Ascending
                        ? _source.OrderBy(row => row, comparer)
                        : _source.OrderByDescending(row => row, comparer);
                }

                _rows = ordered.ToList();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                UiDiagnostics.Warn?.Invoke($"The console list comparer of column '{column.Id}' failed: {ex.Message}");
                _rows = new List<ConsoleListRow>(_source);
                _sortColumnId = null;
                _sortDirection = ConsoleListSortDirection.None;
                _sortStack.Clear();
            }
        }

        for (int i = 0; i < _rows.Count; i++)
        {
            _rows[i].Index = i;
        }

        Header.RefreshSort();
    }

    private static IComparer<ConsoleListRow> DefaultComparer(int columnIndex) =>
        Comparer<ConsoleListRow>.Create((left, right) =>
            Bwc.UI.List.ListCollation.Compare(left.CellText(columnIndex), right.CellText(columnIndex)));

    private void ScrollToFocusOrStart()
    {
        if (_focusRow is { Index: >= 0 } row)
        {
            Body.UpdateLayout();
            RevealIndex(row.Index);
        }
        else
        {
            _ = Body.ChangeView(0, 0, null, disableAnimation: true);
        }
    }

    // ---- view mode and layout ---------------------------------------------------------------------

    private void SetViewMode(ConsoleListViewMode mode)
    {
        if (!Enum.IsDefined(mode) || mode == _mode)
        {
            return;
        }

        _mode = mode;
        ApplyLayout();
        RecomputeLabelWidth();
        Rebind(preserveScroll: false);
        _ = Body.ChangeView(0, 0, null, disableAnimation: true);
        Body.UpdateLayout();
        ConsoleListRow? target = _focusRow ?? _selected.OrderBy(row => row.Index).FirstOrDefault();
        if (target is not null)
        {
            RevealIndex(target.Index);
            _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (target.Index >= 0)
                {
                    RevealIndex(target.Index);
                }

                UpdateHelpText();
            });
        }

        ViewModeChanged?.Invoke(this, EventArgs.Empty);
        UpdateHelpText();
    }

    /// <summary>
    /// Lays the items out on the grid of the current view mode again (the Line Up Icons command). The items have no free
    /// placement, so the grid is always the item order; this makes a new layout object and keeps the mode, the scroll
    /// position, the selection and the focus.
    /// </summary>
    public void LineUpItems()
    {
        ApplyLayout();
        Body.UpdateLayout();
    }

    private void ApplyLayout()
    {
        double itemWidth = _mode == ConsoleListViewMode.LargeIcons ? ConsoleListMetrics.LargeCellWidth : _labelItemWidth;
        double itemHeight = _mode == ConsoleListViewMode.LargeIcons ? ConsoleListMetrics.LargeCellHeight : ConsoleListMetrics.RowHeight;
        switch (_mode)
        {
            case ConsoleListViewMode.Detail:
                Repeater.Layout = new StackLayout { Orientation = Orientation.Vertical, Spacing = 0 };
                Body.HorizontalScrollMode = ScrollMode.Enabled;
                Body.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                Body.VerticalScrollMode = ScrollMode.Enabled;
                Body.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                Repeater.HorizontalAlignment = HorizontalAlignment.Stretch;
                Repeater.VerticalAlignment = VerticalAlignment.Top;
                HeaderHost.Visibility = Visibility.Visible;
                break;
            case ConsoleListViewMode.List:
                Repeater.Layout = GridLayout(Orientation.Vertical, itemWidth, itemHeight);
                Body.HorizontalScrollMode = ScrollMode.Enabled;
                Body.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                Body.VerticalScrollMode = ScrollMode.Disabled;
                Body.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                Repeater.HorizontalAlignment = HorizontalAlignment.Left;
                Repeater.VerticalAlignment = VerticalAlignment.Stretch;
                HeaderHost.Visibility = Visibility.Collapsed;
                break;
            default:
                Repeater.Layout = GridLayout(Orientation.Horizontal, itemWidth, itemHeight);
                Body.HorizontalScrollMode = ScrollMode.Disabled;
                Body.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                Body.VerticalScrollMode = ScrollMode.Enabled;
                Body.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                Repeater.HorizontalAlignment = HorizontalAlignment.Stretch;
                Repeater.VerticalAlignment = VerticalAlignment.Top;
                HeaderHost.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private static UniformGridLayout GridLayout(Orientation orientation, double itemWidth, double itemHeight) => new()
    {
        Orientation = orientation,
        MinItemWidth = itemWidth,
        MinItemHeight = itemHeight,
        MinRowSpacing = 0,
        MinColumnSpacing = 0,
        ItemsStretch = UniformGridLayoutItemsStretch.None,
        ItemsJustification = UniformGridLayoutItemsJustification.Start,
    };

    /// <summary>The vertical StackLayout rounds each detail row to whole device pixels at the current scale.</summary>
    private double ArrangedDetailRowHeight()
    {
        double rowHeight = ConsoleListMetrics.RowHeight;
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        return double.IsFinite(scale) && scale > 0
            ? Math.Round(rowHeight * scale, MidpointRounding.AwayFromZero) / scale
            : rowHeight;
    }

    /// <summary>The size of one item in the current view mode, in DIPs.</summary>
    private Size ItemSize() => _mode switch
    {
        ConsoleListViewMode.Detail => new Size(Math.Max(ColumnsTotalWidth, 1), ArrangedDetailRowHeight()),
        ConsoleListViewMode.LargeIcons => new Size(ConsoleListMetrics.LargeCellWidth, ConsoleListMetrics.LargeCellHeight),
        _ => new Size(_labelItemWidth, ConsoleListMetrics.RowHeight),
    };

    /// <summary>
    /// How many items sit on a line: across in the icon modes, down a column in List, one in Detail. Read from the
    /// size the repeater was given, which is what the layout wraps by.
    /// </summary>
    private int PerLine()
    {
        Size item = ItemSize();
        switch (_mode)
        {
            case ConsoleListViewMode.LargeIcons:
            case ConsoleListViewMode.SmallIcons:
            {
                double available = Repeater.ActualWidth > 0 ? Repeater.ActualWidth : Body.ViewportWidth;
                return Math.Max(1, (int)Math.Floor(available / item.Width));
            }

            case ConsoleListViewMode.List:
            {
                double available = Repeater.ActualHeight > 0 ? Repeater.ActualHeight : Body.ViewportHeight;
                return Math.Max(1, (int)Math.Floor(available / item.Height));
            }

            default:
                return 1;
        }
    }

    /// <summary>The rectangle of an item in the content of the scroll viewer (not the viewport), in DIPs.</summary>
    private Rect ItemContentRect(int index)
    {
        Size item = ItemSize();
        int perLine = PerLine();
        return _mode switch
        {
            ConsoleListViewMode.Detail => new Rect(0, index * item.Height, item.Width, item.Height),
            ConsoleListViewMode.List => new Rect((index / perLine) * item.Width, (index % perLine) * item.Height, item.Width, item.Height),
            _ => new Rect((index % perLine) * item.Width, (index / perLine) * item.Height, item.Width, item.Height),
        };
    }

    private double HeaderTop() => _mode == ConsoleListViewMode.Detail ? ConsoleListMetrics.HeaderHeight : 0;

    /// <summary>Where a keyboard context menu of an item appears, in the coordinates of the list.</summary>
    private Point ItemPosition(int index)
    {
        Rect item = ItemContentRect(index);
        double x = Math.Clamp((_mode == ConsoleListViewMode.Detail ? 24 : item.X + (item.Width / 2)) - Body.HorizontalOffset, 0, Math.Max(0, ActualWidth - 4));
        double y = HeaderTop() + item.Y + (item.Height / 2) - Body.VerticalOffset;
        double minimum = HeaderTop() + 4;
        return new Point(x, Math.Clamp(y, minimum, Math.Max(minimum, ActualHeight - 4)));
    }

    private void RecomputeLabelWidth()
    {
        double widest = 0;
        int count = Math.Min(_source.Count, LabelSampleRows);
        for (int i = 0; i < count; i++)
        {
            widest = Math.Max(widest, MeasureText(_source[i].Text));
        }

        double width = Math.Clamp(
            Math.Ceiling(widest) + ConsoleListMetrics.LabelLeft + 6,
            ConsoleListMetrics.MinLabelItemWidth,
            ConsoleListMetrics.MaxLabelItemWidth);
        if (Math.Abs(width - _labelItemWidth) > 0.01)
        {
            _labelItemWidth = width;
            if (_mode is ConsoleListViewMode.SmallIcons or ConsoleListViewMode.List)
            {
                ApplyLayout();
            }
        }
    }

    /// <summary>Binds the repeater to the displayed rows again. The scroll position is kept (clamped) or reset.</summary>
    private void Rebind(bool preserveScroll)
    {
        double horizontal = Body.HorizontalOffset;
        double vertical = Body.VerticalOffset;
        Repeater.ItemsSource = null;
        Repeater.ItemsSource = _rows;
        if (preserveScroll)
        {
            Body.UpdateLayout();
            _ = Body.ChangeView(
                Math.Min(horizontal, Body.ScrollableWidth),
                Math.Min(vertical, Body.ScrollableHeight),
                null,
                disableAnimation: true);
        }

        UpdateHelpText();
    }

    // ---- metrics and theme ------------------------------------------------------------------------

    /// <summary>Re-reads the dense metrics: heights, fonts and item sizes.</summary>
    public void ApplyMetrics()
    {
        HeaderHost.Height = ConsoleListMetrics.HeaderHeight;
        _probe.FontSize = DenseMetrics.FontSize;
        _probe.FontFamily = DenseMetrics.FontFamily;
        Header.ApplyMetrics();
        foreach (ConsoleListItemElement item in _realized.ToArray())
        {
            item.ApplyMetrics();
        }

        if (_source.Count > 0)
        {
            RecomputeLabelWidth();
        }

        ApplyLayout();
    }

    private void RefreshTheme()
    {
        HeaderHost.Background = BwcBrushes.Face;
        Header.RefreshTheme();
        foreach (ConsoleListItemElement item in _realized)
        {
            item.ApplyTheme();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_metricsSubscribed)
        {
            DenseMetrics.Changed += OnMetricsChanged;
            _metricsSubscribed = true;
        }

        if (XamlRoot is { } root && !ReferenceEquals(_observedRoot, root))
        {
            if (_observedRoot is not null)
            {
                _observedRoot.Changed -= OnXamlRootChanged;
            }

            _observedRoot = root;
            _observedScale = root.RasterizationScale;
            root.Changed += OnXamlRootChanged;
        }

        ApplyMetrics();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_metricsSubscribed)
        {
            DenseMetrics.Changed -= OnMetricsChanged;
            _metricsSubscribed = false;
        }

        if (_observedRoot is not null)
        {
            _observedRoot.Changed -= OnXamlRootChanged;
            _observedRoot = null;
        }
    }

    private void OnMetricsChanged(object? sender, EventArgs e) => ApplyMetrics();

    /// <summary>A new rasterization scale (the window moved to another monitor) needs the icons again.</summary>
    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (Math.Abs(sender.RasterizationScale - _observedScale) < 0.001)
        {
            return;
        }

        _observedScale = sender.RasterizationScale;
        foreach (ConsoleListItemElement item in _realized)
        {
            item.ApplyIcon();
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
        UpdateHelpText();
    }

    /// <summary>The mouse wheel scrolls across in List, where the list scrolls only that way.</summary>
    private void OnBodyWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_mode != ConsoleListViewMode.List)
        {
            return;
        }

        int delta = e.GetCurrentPoint(Body).Properties.MouseWheelDelta;
        double step = Math.Max(ItemSize().Width, 1);
        _ = Body.ChangeView(Math.Clamp(Body.HorizontalOffset - (delta / 120.0 * step), 0, Body.ScrollableWidth), null, null, disableAnimation: true);
        e.Handled = true;
    }

    // ---- items ------------------------------------------------------------------------------------

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not ConsoleListItemElement item || args.Index < 0 || args.Index >= _rows.Count)
        {
            return;
        }

        ConsoleListRow row = _rows[args.Index];
        item.Attach(this, args.Index, row, _selected.Contains(row), IsFocusedRow(args.Index));
        _ = _realized.Add(item);
    }

    private void OnElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is ConsoleListItemElement item)
        {
            _ = _realized.Remove(item);
            item.Detach();
        }
    }

    private bool IsFocusedRow(int index) => _listFocused && _focusRow is not null && _focusRow.Index == index;

    private void RefreshRealized()
    {
        foreach (ConsoleListItemElement item in _realized)
        {
            if (item.Row is { } row)
            {
                item.SetState(_selected.Contains(row), IsFocusedRow(item.Index));
            }
        }
    }

    // ---- pointer ----------------------------------------------------------------------------------

    /// <summary>
    /// Left press on an item. A plain press selects it alone. Ctrl toggles it, Shift extends from the anchor and
    /// Shift with Ctrl adds the range. A right press changes nothing here: the right tap decides.
    /// </summary>
    internal void ItemPressed(ConsoleListItemElement item, PointerRoutedEventArgs e)
    {
        _ = Focus(FocusState.Pointer);
        e.Handled = true;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        int index = item.Index;
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        ConsoleListRow row = _rows[index];
        bool shift = MultiSelect && IsShiftDown();
        bool control = MultiSelect && IsKeyDown(VirtualKey.Control);
        if (shift && _anchorRow is { Index: >= 0 } anchor)
        {
            SelectRange(anchor.Index, index, add: control);
        }
        else if (control)
        {
            ToggleSelection(row);
        }
        else
        {
            SetSingle(row);
        }

        _focusRow = row;
        RefreshRealized();
        UpdateHelpText();
    }

    internal void ItemDoubleTapped(ConsoleListItemElement item, DoubleTappedRoutedEventArgs e)
    {
        int index = item.Index;
        if (index < 0 || index >= _rows.Count || _selected.Count != 1 || !_selected.Contains(_rows[index]))
        {
            return;
        }

        e.Handled = true;
        ActivateIndex(index);
    }

    /// <summary>A right click on a selected row keeps the whole selection; any other row becomes the only selection.</summary>
    internal void ItemRightTapped(ConsoleListItemElement item, RightTappedRoutedEventArgs e)
    {
        int index = item.Index;
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        ConsoleListRow row = _rows[index];
        if (!_selected.Contains(row))
        {
            SetSingle(row);
        }

        _focusRow = row;
        RefreshRealized();
        _ = Focus(FocusState.Pointer);
        UpdateHelpText();
        ContextMenuRequested?.Invoke(
            this,
            new ConsoleListContextEventArgs(ConsoleListContextArea.Item, row, null, e.GetPosition(this), fromKeyboard: false));
    }

    /// <summary>
    /// A press on the empty area (not an item, not a scroll bar) clears the selection and keeps the focus
    /// row.
    /// </summary>
    private void OnBodyPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Handled || IsWithinScrollBar(e.OriginalSource))
        {
            return;
        }

        _ = Focus(FocusState.Pointer);
        e.Handled = true;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && _selected.Count > 0)
        {
            _selected.Clear();
            _anchorRow = null;
            RefreshRealized();
            RaiseSelectionChanged();
        }
    }

    private void OnBackgroundRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.Handled || IsWithin(e.OriginalSource, HeaderHost))
        {
            return;
        }

        _ = Focus(FocusState.Pointer);
        e.Handled = true;
        ContextMenuRequested?.Invoke(
            this,
            new ConsoleListContextEventArgs(ConsoleListContextArea.Background, null, null, e.GetPosition(this), fromKeyboard: false));
    }

    private bool IsWithinScrollBar(object? source)
    {
        for (var current = source as DependencyObject; current is not null && !ReferenceEquals(current, Body); current = VisualTreeHelper.GetParent(current))
        {
            if (current is ScrollBar)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWithin(object? source, DependencyObject container)
    {
        for (var current = source as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, container))
            {
                return true;
            }
        }

        return false;
    }

    // ---- selection --------------------------------------------------------------------------------

    private void SetSingle(ConsoleListRow row)
    {
        bool changed = _selected.Count != 1 || !_selected.Contains(row);
        _selected.Clear();
        _ = _selected.Add(row);
        _anchorRow = row;
        RefreshRealized();
        if (changed)
        {
            RaiseSelectionChanged();
        }
    }

    private void ToggleSelection(ConsoleListRow row)
    {
        if (!MultiSelect)
        {
            SetSingle(row);
            _focusRow = row;
            RefreshRealized();
            UpdateHelpText();
            return;
        }

        if (!_selected.Remove(row))
        {
            _ = _selected.Add(row);
        }

        _anchorRow = row;
        RefreshRealized();
        RaiseSelectionChanged();
    }

    private void SelectRange(int from, int to, bool add)
    {
        int low = Math.Max(0, Math.Min(from, to));
        int high = Math.Min(_rows.Count - 1, Math.Max(from, to));
        if (!add)
        {
            _selected.Clear();
        }

        for (int i = low; i <= high; i++)
        {
            _ = _selected.Add(_rows[i]);
        }

        RefreshRealized();
        RaiseSelectionChanged();
    }

    private void SelectAllRows()
    {
        if (_rows.Count == 0 || !MultiSelect)
        {
            return;
        }

        foreach (ConsoleListRow row in _rows)
        {
            _ = _selected.Add(row);
        }

        _focusRow ??= _rows[0];
        _anchorRow ??= _focusRow;
        RefreshRealized();
        RaiseSelectionChanged();
    }

    private void RaiseSelectionChanged()
    {
        _selectionEvents++;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        UpdateHelpText();
    }

    // ---- keyboard ---------------------------------------------------------------------------------

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool shift = IsShiftDown();
        bool control = IsKeyDown(VirtualKey.Control);
        bool alt = IsKeyDown(VirtualKey.Menu);
        if (alt && e.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.Left or VirtualKey.Right)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Up:
            case VirtualKey.Down:
            case VirtualKey.Left:
            case VirtualKey.Right:
            case VirtualKey.PageUp:
            case VirtualKey.PageDown:
            case VirtualKey.Home:
            case VirtualKey.End:
                e.Handled = true;
                MoveFocus(e.Key, shift, control);
                break;
            case VirtualKey.Space when control:
                if (_focusRow is not null)
                {
                    ToggleSelection(_focusRow);
                }

                e.Handled = true;
                break;
            case VirtualKey.A when control:
                SelectAllRows();
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                ActivateFocused();
                e.Handled = _focusRow is not null || _selected.Count == 1;
                break;
            case VirtualKey.F10 when shift:
            case VirtualKey.Application:
                RaiseContextAtFocus();
                e.Handled = true;
                break;
        }
    }

    /// <summary>The index a navigation key leads to from the focus, or -1 when the key does nothing in this mode.</summary>
    private int TargetFor(VirtualKey key, int current)
    {
        int count = _rows.Count;
        if (key == VirtualKey.Home)
        {
            return 0;
        }

        if (key == VirtualKey.End)
        {
            return count - 1;
        }

        if (current < 0)
        {
            return 0;
        }

        int perLine = PerLine();
        Size item = ItemSize();
        switch (_mode)
        {
            case ConsoleListViewMode.Detail:
                int page = Math.Max(1, (int)(Body.ViewportHeight / item.Height));
                return key switch
                {
                    VirtualKey.Up => Math.Max(0, current - 1),
                    VirtualKey.Down => Math.Min(count - 1, current + 1),
                    VirtualKey.PageUp => Math.Max(0, current - page),
                    VirtualKey.PageDown => Math.Min(count - 1, current + page),
                    _ => -1,
                };
            case ConsoleListViewMode.List:
            {
                int columns = Math.Max(1, (int)(Body.ViewportWidth / item.Width));
                return key switch
                {
                    VirtualKey.Up => Math.Max(0, current - 1),
                    VirtualKey.Down => Math.Min(count - 1, current + 1),
                    VirtualKey.Left => Stride(current, -perLine, perLine),
                    VirtualKey.Right => Stride(current, perLine, perLine),
                    VirtualKey.PageUp => Math.Max(0, current - (columns * perLine)),
                    VirtualKey.PageDown => Math.Min(count - 1, current + (columns * perLine)),
                    _ => -1,
                };
            }

            default:
            {
                int lines = Math.Max(1, (int)(Body.ViewportHeight / item.Height));
                return key switch
                {
                    VirtualKey.Left => Math.Max(0, current - 1),
                    VirtualKey.Right => Math.Min(count - 1, current + 1),
                    VirtualKey.Up => Stride(current, -perLine, perLine),
                    VirtualKey.Down => Stride(current, perLine, perLine),
                    VirtualKey.PageUp => Math.Max(0, current - (lines * perLine)),
                    VirtualKey.PageDown => Math.Min(count - 1, current + (lines * perLine)),
                    _ => -1,
                };
            }
        }
    }

    /// <summary>
    /// A move by one line. Past the start the focus stays; past the end it goes to the last item when the focus is not
    /// on the last line yet (that line is shorter), otherwise it stays.
    /// </summary>
    private int Stride(int current, int delta, int perLine)
    {
        int target = current + delta;
        if (target < 0)
        {
            return current;
        }

        if (target > _rows.Count - 1)
        {
            return current / perLine < (_rows.Count - 1) / perLine ? _rows.Count - 1 : current;
        }

        return target;
    }

    private void MoveFocus(VirtualKey key, bool shift, bool control)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        int current = _focusRow?.Index ?? -1;
        if (_mode == ConsoleListViewMode.Detail && key is VirtualKey.Left or VirtualKey.Right)
        {
            double delta = key == VirtualKey.Left ? -HorizontalKeyStep : HorizontalKeyStep;
            _ = Body.ChangeView(Math.Clamp(Body.HorizontalOffset + delta, 0, Body.ScrollableWidth), null, null, disableAnimation: true);
            return;
        }

        int target = TargetFor(key, current);
        if (target < 0)
        {
            return;
        }

        target = Math.Clamp(target, 0, _rows.Count - 1);
        ConsoleListRow row = _rows[target];
        if (!MultiSelect)
        {
            shift = false;
            control = false;
        }

        if (shift)
        {
            ConsoleListRow anchor = _anchorRow is { Index: >= 0 } ? _anchorRow : (_focusRow ?? row);
            SelectRange(anchor.Index, target, add: control);
            _anchorRow = anchor;
        }
        else if (!control)
        {
            SetSingle(row);
        }

        _focusRow = row;
        RevealIndex(target);
        RefreshRealized();
        UpdateHelpText();
    }

    /// <summary>
    /// Incremental search on the label. Printable characters form a buffer that restarts after one second. One repeated
    /// character cycles through the rows that start with it, from the row after the focus; any other buffer selects the
    /// first row that starts with it, from the focus. Ctrl and Alt combinations are not search input.
    /// </summary>
    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (_rows.Count == 0)
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
            _ = _typeAhead.Clear();
        }

        _typeAheadTick = now;
        _ = _typeAhead.Append(character);
        e.Handled = true;

        // The first half of a surrogate pair cannot be searched for; the second half completes it.
        if (char.IsHighSurrogate(character))
        {
            return;
        }

        string buffer = _typeAhead.ToString();
        bool repeated = IsRepeatedCharacter(buffer);
        string prefix = repeated ? buffer[..1] : buffer;
        int current = _focusRow?.Index ?? -1;
        int start = current < 0 ? 0 : (repeated ? current + 1 : current);
        int match = FindPrefix(prefix, start);
        if (match >= 0)
        {
            ConsoleListRow row = _rows[match];
            SetSingle(row);
            _focusRow = row;
            RevealIndex(match);
            RefreshRealized();
            UpdateHelpText();
        }
    }

    private static bool IsRepeatedCharacter(string text)
    {
        for (int i = 1; i < text.Length; i++)
        {
            if (text[i] != text[0])
            {
                return false;
            }
        }

        return true;
    }

    private int FindPrefix(string prefix, int start)
    {
        int count = _rows.Count;
        for (int step = 0; step < count; step++)
        {
            int index = (start + step) % count;
            if (_rows[index].Text.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsKeyDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

    private static bool IsShiftDown() =>
        IsKeyDown(VirtualKey.Shift) || IsKeyDown(VirtualKey.LeftShift) || IsKeyDown(VirtualKey.RightShift);

    private void OnListGotFocus(object sender, RoutedEventArgs e)
    {
        _listFocused = true;
        RefreshRealized();
    }

    private void OnListLostFocus(object sender, RoutedEventArgs e)
    {
        _listFocused = false;
        RefreshRealized();
    }

    // ---- measuring --------------------------------------------------------------------------------

    private double MeasureText(string text)
    {
        _probe.Text = text;
        _probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return _probe.DesiredSize.Width;
    }

    // ---- automation -------------------------------------------------------------------------------

    protected override AutomationPeer OnCreateAutomationPeer() => new ConsoleListAutomationPeer(this);

    private void UpdateHelpText()
    {
#if BWC_E2E
        // E2E only: the state of the list as UIA help text, because counts would be announced by screen readers.
        CultureInfo c = CultureInfo.InvariantCulture;
        Size item = ItemSize();
        string selectedIdx = string.Join(",", _selected.Select(row => row.Index).OrderBy(i => i).Take(64));
        string selectedTags = string.Join(",", _selected.OrderBy(row => row.Index).Take(64).Select(row => row.Tag?.ToString() ?? string.Empty));
        string widths = string.Join(",", _visible.Select(column => column.Width.ToString("0.##", c)));
        string order = string.Join(",", _visible.Select(column => column.Id));
        string hidden = string.Join(",", _all.Where(column => !column.Visible).Select(column => column.Id));
        string text = string.Create(
            c,
            $"Rows={_rows.Count};Selected={_selected.Count};SelectedIdx={selectedIdx};SelectedTags={selectedTags};" +
            $"Focus={_focusRow?.Index ?? -1};FocusTag={_focusRow?.Tag};Anchor={_anchorRow?.Index ?? -1};Mode={_mode};" +
            $"Sort={SortColumnId ?? "none"};SortDir={_sortDirection};Realized={_realized.Count};PerLine={PerLine()};" +
            $"ItemSize={item.Width:0.##},{item.Height:0.##};Viewport={Body.ViewportWidth:0.##},{Body.ViewportHeight:0.##};" +
            $"Offset={Body.HorizontalOffset:0.##},{Body.VerticalOffset:0.##};Widths={widths};Order={order};Hidden={hidden};" +
            $"SelEvents={_selectionEvents}");
        AutomationProperties.SetHelpText(this, text);
#endif
    }
}

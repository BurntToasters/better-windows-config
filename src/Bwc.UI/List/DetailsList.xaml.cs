using Bwc.UI.Theme;
using Bwc.UI.Tree;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
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

/// <summary>
/// Classic details list. A fixed header row sits above a virtualized ItemsRepeater inside a
/// ScrollViewer. Rows are 18 DIP, columns resize with a shared width model, and sorting of large
/// lists runs on the thread pool before the result is applied on the UI thread.
/// </summary>
public sealed partial class DetailsList : UserControl
{
    private const double MinColumnWidth = 40;
    private const int BackgroundSortThreshold = 10_000;
    private const int AutoSizeSampleRows = 200;

    private readonly List<DetailsColumn> _columns = new();
    private readonly HashSet<DetailsRowElement> _realized = new();
    private readonly HashSet<int> _selected = new();
    private readonly TranslateTransform _headerOffset = new();
    private readonly TextBlock _probe;

    private List<DetailsRow> _rows = new();
    private int _anchor = -1;
    private int _focus = -1;
    private int _renameIndex = -1;
    private bool _renaming;
    private bool _isLoaded;
    private int _sortVersion;
    private string? _sortKey;
    private bool _sortAscending;
    private int _dragColumn = -1;
    private double _dragStartX;
    private double _dragStartWidth;

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
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnActualThemeChanged;
        RenameBox.KeyDown += OnRenameKeyDown;
        RenameBox.LostFocus += OnRenameLostFocus;

        ApplyMetrics();
        UpdateHelpText();
    }

    /// <summary>Column model. Widths change through <see cref="SetColumnWidth"/>.</summary>
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

    /// <summary>Replaces the rows and clears selection and the sort indicator.</summary>
    public void SetRows(IEnumerable<DetailsRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _sortVersion++;
        _rows = rows.ToList();
        _selected.Clear();
        _anchor = -1;
        _focus = -1;
        _sortKey = null;
        _sortAscending = false;
        EndRenameCore();
        Header.SetSort(null, ascending: true);
        Repeater.ItemsSource = null;
        Repeater.ItemsSource = _rows;
        RaiseSelectionChanged();
    }

    /// <summary>
    /// Sorts by a column key. Large lists sort on the thread pool and apply on the UI thread; small
    /// lists sort inline so a click feels immediate.
    /// </summary>
    public void SortBy(string columnKey, bool ascending)
    {
        var column = FindColumn(columnKey);
        if (column is null || !column.Sortable)
        {
            return;
        }

        if (_rows.Count > BackgroundSortThreshold)
        {
            _ = SortInBackgroundAsync(column, ascending);
            return;
        }

        ApplySorted(SortRows(_rows, column, ascending), column, ascending);
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

    internal int IndexOfRow(DetailsRow row) => _rows.IndexOf(row);

    internal bool IsRowSelected(int index) => index >= 0 && _selected.Contains(index);

    internal void SelectRow(int index, bool focus)
    {
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        SetSingle(index);
        _focus = index;
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
            ApplySelectionToRealized();
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

            ApplySelectionToRealized();
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

    internal void OnHeaderTapped(int index)
    {
        if (index < 0 || index >= _columns.Count || !_columns[index].Sortable)
        {
            return;
        }

        var column = _columns[index];
        bool ascending = !(string.Equals(_sortKey, column.Key, StringComparison.Ordinal) && _sortAscending);
        SortBy(column.Key, ascending);
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

    internal void OnDividerPressed(int index, UIElement grip, PointerRoutedEventArgs e)
    {
        _dragColumn = index;
        _dragStartX = e.GetCurrentPoint(this).Position.X;
        _dragStartWidth = _columns[index].Width;
        grip.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    internal void OnDividerMoved(int index, PointerRoutedEventArgs e)
    {
        if (_dragColumn != index)
        {
            return;
        }

        double x = e.GetCurrentPoint(this).Position.X;
        SetColumnWidth(index, _dragStartWidth + (x - _dragStartX));
        e.Handled = true;
    }

    internal void OnDividerReleased(int index, UIElement grip, PointerRoutedEventArgs e)
    {
        if (_dragColumn != index)
        {
            return;
        }

        _dragColumn = -1;
        grip.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    internal void RowPressed(DetailsRowElement row, PointerRoutedEventArgs e)
    {
        _ = Focus(FocusState.Pointer);
        int index = row.Index;
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        bool shift = IsKeyDown(VirtualKey.Shift);
        bool control = IsKeyDown(VirtualKey.Control);
        if (shift && _anchor >= 0)
        {
            SelectRange(_anchor, index);
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
        e.Handled = true;
    }

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not DetailsRowElement row || args.Index < 0 || args.Index >= _rows.Count)
        {
            return;
        }

        row.Attach(this, args.Index, _rows[args.Index], _selected.Contains(args.Index));
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

        bool shift = IsKeyDown(VirtualKey.Shift);
        bool control = IsKeyDown(VirtualKey.Control);
        int page = Math.Max(1, (int)(Body.ViewportHeight / DenseMetrics.ListRowHeight));

        switch (e.Key)
        {
            case VirtualKey.Up:
                MoveTo(_focus < 0 ? 0 : _focus - 1, shift);
                e.Handled = true;
                break;
            case VirtualKey.Down:
                MoveTo(_focus < 0 ? 0 : _focus + 1, shift);
                e.Handled = true;
                break;
            case VirtualKey.PageUp:
                MoveTo((_focus < 0 ? 0 : _focus) - page, shift);
                e.Handled = true;
                break;
            case VirtualKey.PageDown:
                MoveTo((_focus < 0 ? 0 : _focus) + page, shift);
                e.Handled = true;
                break;
            case VirtualKey.Home:
                MoveTo(0, shift);
                e.Handled = true;
                break;
            case VirtualKey.End:
                MoveTo(_rows.Count - 1, shift);
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
        }
    }

    private void MoveTo(int target, bool extend)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        target = Math.Clamp(target, 0, _rows.Count - 1);
        if (extend && _anchor >= 0)
        {
            SelectRange(_anchor, target);
        }
        else
        {
            SetSingle(target);
        }

        _focus = target;
        RevealIndex(target);
    }

    private void SetSingle(int index)
    {
        bool changed = _selected.Count != 1 || !_selected.Contains(index);
        _selected.Clear();
        _selected.Add(index);
        _anchor = index;
        ApplySelectionToRealized();
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
        ApplySelectionToRealized();
        RaiseSelectionChanged();
    }

    private void SelectRange(int from, int to)
    {
        int low = Math.Min(from, to);
        int high = Math.Max(from, to);
        _selected.Clear();
        for (var i = low; i <= high; i++)
        {
            _selected.Add(i);
        }

        ApplySelectionToRealized();
        RaiseSelectionChanged();
    }

    private void SelectAllRows()
    {
        _selected.Clear();
        for (var i = 0; i < _rows.Count; i++)
        {
            _selected.Add(i);
        }

        _anchor = 0;
        _focus = _rows.Count - 1;
        ApplySelectionToRealized();
        RaiseSelectionChanged();
    }

    private void ApplySelectionToRealized()
    {
        foreach (var row in _realized)
        {
            row.SetSelected(_selected.Contains(row.Index));
        }
    }

    private void ApplySorted(List<DetailsRow> sorted, DetailsColumn column, bool ascending)
    {
        _rows = sorted;
        _selected.Clear();
        _anchor = -1;
        _focus = -1;
        _sortKey = column.Key;
        _sortAscending = ascending;
        Header.SetSort(column.Key, ascending);
        Repeater.ItemsSource = null;
        Repeater.ItemsSource = _rows;
        RaiseSelectionChanged();
    }

    private async Task SortInBackgroundAsync(DetailsColumn column, bool ascending)
    {
        int version = ++_sortVersion;
        var copy = new List<DetailsRow>(_rows);
        var sorted = await Task.Run(() => SortRows(copy, column, ascending));
        if (version != _sortVersion || !_isLoaded)
        {
            return;
        }

        ApplySorted(sorted, column, ascending);
    }

    private static List<DetailsRow> SortRows(IEnumerable<DetailsRow> rows, DetailsColumn column, bool ascending)
    {
        Func<DetailsRow, string> selector = column.Key switch
        {
            "Type" => row => row.Type,
            "Data" => row => row.Data,
            _ => row => row.Name,
        };

        // OrderBy is stable. List.Sort is not, and an unstable sort would change the relative order
        // of rows with equal keys between two identical sorts.
        return ascending
            ? rows.OrderBy(selector, StringComparer.OrdinalIgnoreCase).ToList()
            : rows.OrderByDescending(selector, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private DetailsColumn? FindColumn(string key)
    {
        foreach (var column in _columns)
        {
            if (string.Equals(column.Key, key, StringComparison.Ordinal))
            {
                return column;
            }
        }

        return null;
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

        double clamped = Math.Max(MinColumnWidth, Math.Round(width));
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
        RenameBox.SelectAll();
    }

    private void PositionRenameBox()
    {
        if (_renameIndex < 0 || _renameIndex >= _rows.Count)
        {
            return;
        }

        double rowHeight = DenseMetrics.ListRowHeight;
        double width = Math.Max(80, _columns[0].Width - 8);
        Canvas.SetLeft(RenameBox, 4);
        Canvas.SetTop(RenameBox, _renameIndex * rowHeight);
        RenameBox.Width = width;
        RenameBox.Height = rowHeight + 2;
        RenameBox.FontSize = DenseMetrics.FontSize;
        RenameBox.FontFamily = DenseMetrics.FontFamily;
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
        if (_renaming)
        {
            CancelRename();
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

    private void RaiseSelectionChanged()
    {
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        UpdateHelpText();
    }

    private void UpdateHelpText()
    {
        AutomationProperties.SetHelpText(this, $"Rows={_rows.Count};Selected={_selected.Count}");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        DenseMetrics.Changed += OnMetricsChanged;
        ApplyMetrics();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
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
}

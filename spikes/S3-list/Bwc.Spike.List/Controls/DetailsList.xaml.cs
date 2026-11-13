using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using Bwc.Spike.ListApp.Model;

namespace Bwc.Spike.ListApp.Controls;

/// <summary>
/// Classic details list: header row above a virtualized ItemsRepeater (StackLayout) inside a ScrollViewer.
/// Rows are fixed-height RowPanels; columns come from a shared ColumnModel.
/// </summary>
public sealed partial class DetailsList : UserControl
{
    private const double MinAutoSizeWidth = 40;

    private List<RegValue> _items = new List<RegValue>();
    private int _sortVersion;
    private List<RegValue> _spare;
    private readonly HashSet<RowPanel> _realized = new HashSet<RowPanel>();
    private readonly HashSet<int> _selected = new HashSet<int>();
    private readonly TextBlock _probe;
    private readonly TranslateTransform _headerOffset = new TranslateTransform();
    private int _anchor = -1;
    private int _focus = -1;
    private int _sortColumn = -1;
    private bool _sortDescending;
    private int _dragColumn = -1;
    private double _dragStartX;
    private double _dragStartWidth;

    public DetailsList()
    {
        InitializeComponent();

        Columns = new ColumnModel(
            new ColumnSpec("Name", 220, MinAutoSizeWidth),
            new ColumnSpec("Type", 120, MinAutoSizeWidth),
            new ColumnSpec("Data", 380, MinAutoSizeWidth));
        Columns.WidthChanged += OnColumnWidthChanged;

        RowHeight = 20;
        HeaderHeight = 22;
        RowFontSize = 12;
        ExtendedSelection = true;

        Repeater.Layout = new StackLayout();
        Repeater.ItemTemplate = (DataTemplate)Resources["RowTemplate"];
        Header.Attach(this);
        Header.RenderTransform = _headerOffset;

        _probe = new TextBlock
        {
            Opacity = 0,
            IsHitTestVisible = false,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = Metrics.Font,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        HeaderHost.Children.Add(_probe);

        HeaderHost.SizeChanged += (s, e) =>
            HeaderHost.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        Body.ViewChanged += (s, e) => _headerOffset.X = -Body.HorizontalOffset;
        KeyDown += OnKeyDown;
    }

    public ColumnModel Columns { get; }

    public int RowHeight { get; private set; }

    public int HeaderHeight { get; private set; }

    public double RowFontSize { get; private set; }

    /// <summary>Icon placeholder edge length in DIPs; must fit inside the row height.</summary>
    public double IconSize { get; private set; } = 16;

    public bool ExtendedSelection { get; private set; }

    public int Count => _items.Count;

    public int SelectedCount => _selected.Count;

    public double ScrollableHeight => Body.ScrollableHeight;

    public double ViewportHeight => Body.ViewportHeight;

    public double HeaderActualHeight => Header.ActualHeight;

    public int RealizedCount => _realized.Count;

    public RegValue ItemAt(int index) => _items[index];

    public void Configure(int rowHeight, int headerHeight, double fontSize, double iconSize, bool extendedSelection)
    {
        RowHeight = rowHeight;
        HeaderHeight = headerHeight;
        RowFontSize = fontSize;
        IconSize = iconSize;
        ExtendedSelection = extendedSelection;
        HeaderHost.Height = headerHeight;
        Header.Height = headerHeight;
        Header.ApplyFont(fontSize);
        Header.Width = Columns.TotalWidth;
        _probe.FontSize = fontSize;
    }

    public void SetItems(List<RegValue> items)
    {
        _items = items;
        _spare = new List<RegValue>(items.Count); // sort buffer allocated at load, not during a measured sort
        _selected.Clear();
        _anchor = -1;
        _focus = -1;
        Repeater.ItemsSource = _items;
    }

    /// <summary>Synchronous sort on the UI thread (in place), then source reset. Clears selection.</summary>
    public void Sort(int column, bool descending)
    {
        _items.Sort(ComparisonFor(column, descending));
        ApplySort(_items, column, descending);
    }

    /// <summary>
    /// Sorts a copy on a thread-pool thread (RegValue is immutable), then applies it on the UI thread.
    /// Returns the background sort duration in ms. A newer request supersedes an older one.
    /// </summary>
    public async Task<double> SortAsync(int column, bool descending)
    {
        Comparison<RegValue> comparison = ComparisonFor(column, descending);

        // Reuse the previous source list's buffer (taken on the UI thread) so repeated sorts do not churn the LOH.
        List<RegValue> copy = _spare ?? new List<RegValue>(_items.Count);
        _spare = null;
        copy.Clear();
        copy.AddRange(_items);

        int version = ++_sortVersion;
        var sw = Stopwatch.StartNew();
        await Task.Run(() => copy.Sort(comparison));
        double backgroundMs = sw.Elapsed.TotalMilliseconds;
        if (version == _sortVersion)
        {
            List<RegValue> previous = _items;
            ApplySort(copy, column, descending);
            _spare = previous;
        }
        else
        {
            _spare = copy;
        }

        return backgroundMs;
    }

    private void ApplySort(List<RegValue> sorted, int column, bool descending)
    {
        _items = sorted;
        _sortColumn = column;
        _sortDescending = descending;
        _selected.Clear();
        _anchor = -1;
        _focus = -1;
        Header.SetSort(column, descending);

        Repeater.ItemsSource = null;
        Repeater.ItemsSource = _items;
    }

    public void ResizeColumn(int column, double width)
    {
        Columns.SetWidth(column, width);
    }

    /// <summary>Sizes a column to the widest text among realized rows and its header title.</summary>
    public double AutoSizeColumn(int column)
    {
        double headerNeed = MeasureText(Columns.Title(column)) + 2 * Metrics.Pad + Metrics.ArrowSpace;
        double chrome = 2 * Metrics.Pad + 2 + (column == 0 ? IconSize + Metrics.IconGap : 0);
        double width = headerNeed;
        foreach (RowPanel row in _realized)
        {
            width = Math.Max(width, MeasureText(row.CellText(column)) + chrome);
        }

        ResizeColumn(column, Math.Max(MinAutoSizeWidth, width));
        return Columns.Width(column);
    }

    /// <summary>Selects every item (the Ctrl+A path). Returns the selected count.</summary>
    public int SelectAll()
    {
        _selected.Clear();
        for (int i = 0; i < _items.Count; i++)
        {
            _selected.Add(i);
        }

        _anchor = 0;
        _focus = _items.Count - 1;
        ApplySelectionToRealized();
        return _selected.Count;
    }

    public void ScrollVerticalTo(double offset)
    {
        Body.ChangeView(null, offset, null, true);
    }

    /// <summary>Copies the ActualHeight of every realized row into the set (ignores rows not yet arranged).</summary>
    public void SampleRowHeights(ISet<double> heights)
    {
        foreach (RowPanel row in _realized)
        {
            if (row.ActualHeight > 0)
            {
                heights.Add(Math.Round(row.ActualHeight, 2));
            }
        }
    }

    private static Comparison<RegValue> ComparisonFor(int column, bool descending)
    {
        Comparison<RegValue> ascending;
        switch (column)
        {
            case 0:
                ascending = (a, b) => string.CompareOrdinal(a.Name, b.Name);
                break;
            case 1:
                ascending = (a, b) =>
                {
                    int c = string.CompareOrdinal(a.Type, b.Type);
                    return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
                };
                break;
            default:
                ascending = (a, b) =>
                {
                    int c = string.CompareOrdinal(a.Data, b.Data);
                    return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
                };
                break;
        }

        return descending ? (a, b) => ascending(b, a) : ascending;
    }

    private void Repeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        var row = (RowPanel)args.Element;
        row.Attach(this, args.Index, _items[args.Index], RowHeight, RowFontSize, _selected.Contains(args.Index));
        _realized.Add(row);
        PrepareCount++;
    }

    private void Repeater_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        _realized.Remove((RowPanel)args.Element);
        ClearCount++;
    }

    /// <summary>Monotonic counts of ItemsRepeater element prepare / clear events (diagnostics for the bench).</summary>
    public long PrepareCount { get; private set; }

    public long ClearCount { get; private set; }

    private void OnColumnWidthChanged(int column)
    {
        // Only geometry changes: no templates are created or torn down.
        Header.Width = Columns.TotalWidth;
        Header.InvalidateArrange();
        foreach (RowPanel row in _realized)
        {
            row.InvalidateMeasure();
            row.InvalidateArrange();
        }
    }

    internal async void OnHeaderTapped(int column)
    {
        await SortAsync(column, _sortColumn == column && !_sortDescending);
    }

    internal void OnDividerPressed(int column, UIElement grip, PointerRoutedEventArgs e)
    {
        _dragColumn = column;
        _dragStartX = e.GetCurrentPoint(this).Position.X;
        _dragStartWidth = Columns.Width(column);
        grip.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    internal void OnDividerMoved(int column, PointerRoutedEventArgs e)
    {
        if (_dragColumn != column)
        {
            return;
        }

        double x = e.GetCurrentPoint(this).Position.X;
        ResizeColumn(column, _dragStartWidth + (x - _dragStartX));
        e.Handled = true;
    }

    internal void OnDividerReleased(int column, UIElement grip, PointerRoutedEventArgs e)
    {
        if (_dragColumn != column)
        {
            return;
        }

        _dragColumn = -1;
        grip.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    internal void RowPressed(RowPanel row, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        int index = row.Index;
        if (index < 0 || index >= _items.Count)
        {
            return;
        }

        if (!ExtendedSelection)
        {
            SetSingle(index);
        }
        else if (IsKeyDown(VirtualKey.Shift) && _anchor >= 0)
        {
            SelectRange(_anchor, index);
        }
        else if (IsKeyDown(VirtualKey.Control))
        {
            if (!_selected.Remove(index))
            {
                _selected.Add(index);
            }

            _anchor = index;
            ApplySelectionToRealized();
        }
        else
        {
            SetSingle(index);
        }

        _focus = index;
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool shift = IsKeyDown(VirtualKey.Shift);
        bool ctrl = IsKeyDown(VirtualKey.Control);
        int visibleRows = Math.Max(1, (int)(ViewportHeight / RowHeight));
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
                MoveTo((_focus < 0 ? 0 : _focus) - visibleRows, shift);
                e.Handled = true;
                break;
            case VirtualKey.PageDown:
                MoveTo((_focus < 0 ? 0 : _focus) + visibleRows, shift);
                e.Handled = true;
                break;
            case VirtualKey.Home:
                MoveTo(0, shift);
                e.Handled = true;
                break;
            case VirtualKey.End:
                MoveTo(_items.Count - 1, shift);
                e.Handled = true;
                break;
            case VirtualKey.A when ctrl && ExtendedSelection:
                SelectAll();
                e.Handled = true;
                break;
        }
    }

    private void MoveTo(int target, bool extend)
    {
        if (_items.Count == 0)
        {
            return;
        }

        target = Math.Clamp(target, 0, _items.Count - 1);
        if (extend && ExtendedSelection && _anchor >= 0)
        {
            SelectRange(_anchor, target);
        }
        else
        {
            SetSingle(target);
        }

        _focus = target;
        EnsureVisible(target);
    }

    private void SetSingle(int index)
    {
        _selected.Clear();
        _selected.Add(index);
        _anchor = index;
        ApplySelectionToRealized();
    }

    private void SelectRange(int from, int to)
    {
        int lo = Math.Min(from, to);
        int hi = Math.Max(from, to);
        _selected.Clear();
        for (int i = lo; i <= hi; i++)
        {
            _selected.Add(i);
        }

        ApplySelectionToRealized();
    }

    private void ApplySelectionToRealized()
    {
        foreach (RowPanel row in _realized)
        {
            row.SetSelected(_selected.Contains(row.Index));
        }
    }

    private void EnsureVisible(int index)
    {
        double top = index * RowHeight;
        double bottom = top + RowHeight;
        double offset = Body.VerticalOffset;
        double viewport = Body.ViewportHeight;
        if (top < offset)
        {
            Body.ChangeView(null, top, null, true);
        }
        else if (bottom > offset + viewport)
        {
            Body.ChangeView(null, bottom - viewport, null, true);
        }
    }

    private double MeasureText(string text)
    {
        _probe.Text = text ?? string.Empty;
        _probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return _probe.DesiredSize.Width;
    }

    private static bool IsKeyDown(VirtualKey key)
    {
        return (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
    }
}

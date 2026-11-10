using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace Bwc.Spike.Tree
{
    /// <summary>Result of walking the realized rows: counts, the distinct measured heights, and position errors.</summary>
    public sealed record LayoutCheck(
        int Realized,
        int Mismatches,
        double MaxPositionErrorPx,
        double MaxHeightErrorPx,
        double[] Heights);

    /// <summary>
    /// Dense tree host. The model is a flat List of visible TreeRow objects; an ItemsRepeater with a
    /// StackLayout virtualizes it inside a ScrollViewer. Expand and collapse insert or remove one
    /// contiguous range of the list and raise a single range notification.
    /// </summary>
    public sealed partial class DenseTree : UserControl
    {
        private readonly FlatRowList _rows = new FlatRowList();
        private TreeRow? _selected;
        private readonly StringBuilder _typeAhead = new StringBuilder();
        private long _typeAheadTick;

        public DenseTree()
        {
            InitializeComponent();
            Repeater.ItemsSource = _rows;
        }

        public ScrollViewer ScrollHost => Scroller;
        public ItemsRepeater RepeaterHost => Repeater;
        public int RowCount => _rows.Count;
        public TreeRow? SelectedRow => _selected;
        public int SelectedIndex => _selected is null ? -1 : _rows.IndexOf(_selected);

        /// <summary>Sets the root and expands the given nodes before the first layout. No notifications are raised.</summary>
        public void Load(TreeRow root, params TreeRow[] expandOnLoad)
        {
            _rows.Clear();
            _rows.Add(root);
            ExpandCore(root, notify: false);
            foreach (TreeRow r in expandOnLoad)
            {
                ExpandCore(r, notify: false);
            }
        }

        public int IndexOfRow(TreeRow row) => _rows.IndexOf(row);

        public void Expand(TreeRow row) => ExpandCore(row, notify: true);

        public void Collapse(TreeRow row)
        {
            if (!row.IsExpanded) return;
            int idx = _rows.IndexOf(row);
            if (idx < 0)
            {
                row.IsExpanded = false;
                return;
            }

            int end = idx + 1;
            bool selectionInside = false;
            while (end < _rows.Count && _rows[end].Depth > row.Depth)
            {
                if (ReferenceEquals(_rows[end], _selected)) selectionInside = true;
                end++;
            }

            row.IsExpanded = false;
            if (end > idx + 1)
            {
                _rows.RemoveRangeNotify(idx + 1, end - idx - 1);
            }

            if (selectionInside)
            {
                SelectRow(row);
            }
        }

        public void SelectRow(TreeRow row)
        {
            if (ReferenceEquals(_selected, row)) return;
            if (_selected is not null) _selected.IsSelected = false;
            _selected = row;
            row.IsSelected = true;
        }

        public void MoveTo(int index)
        {
            if (_rows.Count == 0) return;
            index = Math.Clamp(index, 0, _rows.Count - 1);
            SelectRow(_rows[index]);
            RevealIndex(index);
        }

        /// <summary>Scrolls the minimum amount needed to make the row at index fully visible.</summary>
        public void RevealIndex(int index)
        {
            double rh = TreeMetrics.RowHeight;
            double top = index * rh;
            double bottom = top + rh;
            double offset = Scroller.VerticalOffset;
            double viewport = Scroller.ViewportHeight;
            if (viewport <= 0) return;
            if (top < offset)
            {
                Scroller.ChangeView(null, top, null, disableAnimation: true);
            }
            else if (bottom > offset + viewport)
            {
                Scroller.ChangeView(null, bottom - viewport, null, disableAnimation: true);
            }
        }

        /// <summary>Keyboard model: Up/Down/Home/End/PageUp/PageDown move; Left collapses or goes to the parent;
        /// Right expands or goes to the first child; Enter toggles.</summary>
        public bool HandleKey(VirtualKey key)
        {
            int count = _rows.Count;
            if (count == 0) return false;
            int cur = SelectedIndex;
            int page = Math.Max(1, (int)(Scroller.ViewportHeight / TreeMetrics.RowHeight) - 1);

            switch (key)
            {
                case VirtualKey.Up:
                    MoveTo(cur < 0 ? 0 : cur - 1);
                    return true;
                case VirtualKey.Down:
                    MoveTo(cur < 0 ? 0 : cur + 1);
                    return true;
                case VirtualKey.PageUp:
                    MoveTo(cur < 0 ? 0 : cur - page);
                    return true;
                case VirtualKey.PageDown:
                    MoveTo(cur < 0 ? 0 : cur + page);
                    return true;
                case VirtualKey.Home:
                    MoveTo(0);
                    return true;
                case VirtualKey.End:
                    MoveTo(count - 1);
                    return true;
                case VirtualKey.Left:
                    if (_selected is null)
                    {
                        MoveTo(0);
                    }
                    else if (_selected.IsExpanded)
                    {
                        Collapse(_selected);
                    }
                    else if (_selected.Parent is TreeRow parent)
                    {
                        MoveTo(_rows.IndexOf(parent));
                    }
                    return true;
                case VirtualKey.Right:
                    if (_selected is null)
                    {
                        MoveTo(0);
                    }
                    else if (_selected.HasChildren && !_selected.IsExpanded)
                    {
                        Expand(_selected);
                    }
                    else if (_selected.IsExpanded)
                    {
                        MoveTo(cur + 1);
                    }
                    return true;
                case VirtualKey.Enter:
                    if (_selected is { HasChildren: true })
                    {
                        if (_selected.IsExpanded) Collapse(_selected);
                        else Expand(_selected);
                    }
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Walks the realized row elements and checks each one against the model. Expected index is
        /// round(Y / rowHeight), and the element's DataContext must be that index's row.
        /// </summary>
        public LayoutCheck CheckRealizedRows()
        {
            double rh = TreeMetrics.RowHeight;
            int realized = 0;
            int mismatches = 0;
            double maxPos = 0;
            double maxH = 0;
            var heights = new SortedSet<double>();

            foreach (FrameworkElement fe in EnumerateRowElements())
            {
                realized++;
                double h = fe.ActualHeight;
                heights.Add(Math.Round(h, 2));
                maxH = Math.Max(maxH, Math.Abs(h - rh));

                int i = Repeater.GetElementIndex(fe);
                if (i < 0 || i >= _rows.Count)
                {
                    mismatches++;
                    continue;
                }

                Point p = fe.TransformToVisual(Repeater).TransformPoint(new Point(0, 0));
                maxPos = Math.Max(maxPos, Math.Abs(p.Y - i * rh));

                TextBlock? label = FindDescendant<TextBlock>(fe);
                if (label is null || label.Text != _rows[i].Label)
                {
                    mismatches++;
                }
            }

            var list = new double[heights.Count];
            heights.CopyTo(list);
            return new LayoutCheck(realized, mismatches, maxPos, maxH, list);
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) return match;
                T? nested = FindDescendant<T>(child);
                if (nested is not null) return nested;
            }
            return null;
        }

        /// <summary>
        /// Index of the realized row that contains d, or -1. ItemsRepeater does not set DataContext on its
        /// elements, so rows are mapped back to their index through GetElementIndex.
        /// </summary>
        private int RowIndexOf(DependencyObject d)
        {
            DependencyObject? cur = d;
            while (cur is not null)
            {
                if (cur is FrameworkElement fe && fe.Tag is string tag && tag == "row")
                {
                    return Repeater.GetElementIndex(fe);
                }
                cur = VisualTreeHelper.GetParent(cur);
            }
            return -1;
        }

        private IEnumerable<FrameworkElement> EnumerateRowElements()
        {
            var stack = new Stack<DependencyObject>();
            stack.Push(Repeater);
            while (stack.Count > 0)
            {
                DependencyObject d = stack.Pop();
                int n = VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < n; i++)
                {
                    DependencyObject child = VisualTreeHelper.GetChild(d, i);
                    if (child is FrameworkElement fe && fe.Tag is string tag && tag == "row")
                    {
                        yield return fe;
                    }
                    else
                    {
                        stack.Push(child);
                    }
                }
            }
        }

        private void ExpandCore(TreeRow row, bool notify)
        {
            if (!row.HasChildren || row.IsExpanded) return;
            int idx = _rows.IndexOf(row);
            if (idx < 0) return;

            if (row.Children is null)
            {
                if (row.Loader is null) return;
                row.Children = row.Loader(row);
            }

            row.IsExpanded = true;
            var visible = new List<TreeRow>();
            CollectVisible(row, visible);
            if (visible.Count == 0) return;

            if (notify)
            {
                _rows.InsertRangeNotify(idx + 1, visible);
            }
            else
            {
                _rows.InsertRange(idx + 1, visible);
            }
        }

        /// <summary>Appends the visible descendants of row. Children that were expanded earlier come back expanded.</summary>
        private static void CollectVisible(TreeRow row, List<TreeRow> acc)
        {
            foreach (TreeRow child in row.Children!)
            {
                acc.Add(child);
                if (child.IsExpanded)
                {
                    CollectVisible(child, acc);
                }
            }
        }

        private void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (HandleKey(e.Key))
            {
                e.Handled = true;
            }
        }

        private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
        {
            char ch = e.Character;
            if (char.IsControl(ch) || _rows.Count == 0) return;

            long now = Environment.TickCount64;
            if (now - _typeAheadTick > 1000) _typeAhead.Clear();
            _typeAheadTick = now;
            _typeAhead.Append(ch);
            string prefix = _typeAhead.ToString();

            int start = Math.Max(0, SelectedIndex);
            int from = prefix.Length == 1 ? start + 1 : start;
            for (int k = 0; k < _rows.Count; k++)
            {
                int i = (from + k) % _rows.Count;
                if (_rows[i].Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    MoveTo(i);
                    break;
                }
            }
            e.Handled = true;
        }

        private void OnRowTapped(object sender, TappedRoutedEventArgs e)
        {
            int idx = RowIndexOf((DependencyObject)sender);
            if (idx >= 0 && idx < _rows.Count)
            {
                SelectRow(_rows[idx]);
                Focus(FocusState.Pointer);
            }
        }

        private void OnExpanderTapped(object sender, TappedRoutedEventArgs e)
        {
            int idx = RowIndexOf((DependencyObject)sender);
            if (idx < 0 || idx >= _rows.Count) return;
            TreeRow row = _rows[idx];
            if (!row.HasChildren) return;

            if (row.IsExpanded) Collapse(row);
            else Expand(row);
            e.Handled = true;
        }
    }
}

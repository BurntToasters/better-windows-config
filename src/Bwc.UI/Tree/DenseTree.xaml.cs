using System.Text;
using Bwc.UI.Theme;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Bwc.UI.Tree;

/// <summary>Selection changed to a different visible node.</summary>
public sealed class TreeNodeEventArgs : EventArgs
{
    public TreeNodeEventArgs(TreeNode node)
    {
        Node = node;
    }

    public TreeNode Node { get; }
}

/// <summary>F2 rename committed. The host decides whether the name is written anywhere.</summary>
public sealed class TreeRenameRequestedEventArgs : EventArgs
{
    public TreeRenameRequestedEventArgs(TreeNode node, string newName)
    {
        Node = node;
        NewName = newName;
    }

    public TreeNode Node { get; }

    public string NewName { get; }
}

/// <summary>A right-click or menu key request for one tree row.</summary>
public sealed class TreeNodeContextRequestedEventArgs : EventArgs
{
    public TreeNodeContextRequestedEventArgs(TreeNode node, FrameworkElement element, Point point)
    {
        Node = node;
        Element = element;
        Point = point;
    }

    public TreeNode Node { get; }

    public FrameworkElement Element { get; }

    public Point Point { get; }
}

/// <summary>
/// Dense classic tree. The visible nodes live in one flat list; ItemsRepeater with StackLayout
/// virtualizes that list inside a ScrollViewer. Expanding a large node inserts rows in chunks over
/// several dispatcher turns and a collapse cancels the remaining chunks.
/// </summary>
public sealed partial class DenseTree : UserControl
{
    private const int ExpandChunkSize = 500;
    private const long TypeAheadWindowMs = 500;
    private const int MaxTypeAheadLength = 32;

    private readonly FlatNodeList _rows = new();
    private readonly List<TreeNode> _roots = new();
    private readonly HashSet<TreeRowElement> _realized = new();
    private readonly Dictionary<TreeNode, CancellationTokenSource> _expandTokens = new();
    private readonly StringBuilder _typeAhead = new();
    private long _typeAheadTick;
    private bool _renaming;
    private TreeNode? _renameNode;
    private bool _metricsSubscribed;

    public DenseTree()
    {
        InitializeComponent();

        Repeater.ItemsSource = _rows;
        Repeater.ElementPrepared += OnElementPrepared;
        Repeater.ElementClearing += OnElementClearing;

        KeyDown += OnKeyDown;
        CharacterReceived += OnCharacterReceived;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnActualThemeChanged;

        RenameBox.KeyDown += OnRenameKeyDown;
        RenameBox.LostFocus += OnRenameLostFocus;
        UpdateHelpText();
    }

    /// <summary>The current roots, in display order.</summary>
    public IReadOnlyList<TreeNode> Roots => _roots;

    /// <summary>The selected node, or null when nothing is selected.</summary>
    public TreeNode? SelectedNode { get; private set; }

    /// <summary>Number of visible flattened rows.</summary>
    public int VisibleRowCount => _rows.Count;

    /// <summary>Raised after the selected node changes.</summary>
    public event EventHandler<TreeNodeEventArgs>? SelectionChanged;

    /// <summary>Raised when F2 rename is committed with Enter. The demo host does not write the registry.</summary>
    public event EventHandler<TreeRenameRequestedEventArgs>? RenameRequested;

    /// <summary>
    /// Raised for a right-click on a row or a keyboard menu request. The row is selected before
    /// the event fires. The member hides the UIElement event of the same name because the payload
    /// is row specific.
    /// </summary>
    public new event EventHandler<TreeNodeContextRequestedEventArgs>? ContextRequested;

    /// <summary>Raised when Delete is pressed with a selected node.</summary>
    public event EventHandler? DeleteRequested;

    /// <summary>Raised when Ctrl+C is pressed with a selected node.</summary>
    public event EventHandler? CopyKeyNameRequested;

    /// <summary>
    /// Replaces the roots. A root that was already expanded before the call is shown with its
    /// visible descendants, which lets the demo open Computer at startup without a load event.
    /// </summary>
    public void SetRoots(IReadOnlyList<TreeNode> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        CancelAllPendingExpands();
        EndRenameCore();

        _roots.Clear();
        _roots.AddRange(roots);
        SelectedNode = null;

        var visible = new List<TreeNode>();
        foreach (var root in _roots)
        {
            root.Depth = 0;
            visible.Add(root);
            if (root.IsExpanded)
            {
                CollectVisible(root, visible);
            }
        }

        _rows.ResetNotify(visible);
        UpdateHelpText();
    }

    /// <summary>Selects a node that is already visible. The scroll is the minimum needed.</summary>
    public void SelectNode(TreeNode node, bool scroll = true)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (_rows.IndexOf(node) < 0)
        {
            return;
        }

        if (!ReferenceEquals(SelectedNode, node))
        {
            SelectedNode = node;
            RefreshRealizedSelection();
            SelectionChanged?.Invoke(this, new TreeNodeEventArgs(node));
            UpdateHelpText();
        }

        if (scroll)
        {
            RevealNode(node);
        }
    }

    /// <summary>Focuses the tree and selects the first row when there is no selection.</summary>
    public void FocusSelected()
    {
        if (_rows.Count == 0)
        {
            return;
        }

        if (SelectedNode is null)
        {
            SelectNode(_rows[0], scroll: false);
        }

        _ = Focus(FocusState.Keyboard);
        if (SelectedNode is { } selected)
        {
            RevealNode(selected);
        }
    }

    /// <summary>Reapplies the current DenseMetrics values to the control and every realized row.</summary>
    public void ApplyMetrics()
    {
        foreach (var row in _realized.ToArray())
        {
            row.ApplyMetrics();
        }

        if (_renaming)
        {
            PositionRenameBox();
        }
    }

    internal int IndexOfVisibleNode(TreeNode? node) => node is null ? -1 : _rows.IndexOf(node);

    internal void RowTapped(TreeRowElement row)
    {
        if (row.Node is not { } node)
        {
            return;
        }

        SelectNode(node, scroll: false);
        _ = Focus(FocusState.Pointer);
    }

    internal void ChevronTapped(TreeRowElement row)
    {
        if (row.Node is not { HasChildren: true } node)
        {
            return;
        }

        SelectNode(node, scroll: false);
        if (node.IsExpanded)
        {
            CollapseNode(node);
        }
        else
        {
            _ = ExpandNodeAsync(node);
        }
    }

    internal void ExpandFromAutomation(TreeNode node)
    {
        _ = ExpandNodeAsync(node);
    }

    /// <summary>Expands the selected node when it has children and is collapsed.</summary>
    public void ExpandSelected()
    {
        if (SelectedNode is { HasChildren: true, IsExpanded: false } node)
        {
            _ = ExpandNodeAsync(node);
        }
    }

    /// <summary>Starts the inline rename box for the selected node. F2 uses the same path.</summary>
    public void BeginRenameSelected() => BeginRename();

    internal void RowRightTapped(TreeRowElement row, RightTappedRoutedEventArgs e)
    {
        if (row.Node is not { } node)
        {
            return;
        }

        SelectNode(node, scroll: false);
        _ = Focus(FocusState.Pointer);
        ContextRequested?.Invoke(
            this,
            new TreeNodeContextRequestedEventArgs(node, row, e.GetPosition(row)));
    }

    internal void RevealNode(TreeNode node)
    {
        var index = _rows.IndexOf(node);
        if (index >= 0)
        {
            RevealIndex(index);
        }
    }

    internal void CollapseNode(TreeNode node)
    {
        if (!node.IsExpanded)
        {
            return;
        }

        if (_expandTokens.TryGetValue(node, out var token))
        {
            token.Cancel();
        }

        node.IsExpanded = false;
        RefreshRealizedExpansion();

        var index = _rows.IndexOf(node);
        if (index < 0)
        {
            UpdateHelpText();
            return;
        }

        var end = index + 1;
        while (end < _rows.Count && _rows[end].Depth > node.Depth)
        {
            end++;
        }

        bool selectionInside = SelectedNode is { } selected && IsDescendantOf(selected, node);
        if (end > index + 1)
        {
            _rows.RemoveRangeNotify(index + 1, end - index - 1);
        }

        if (selectionInside)
        {
            SelectNode(node, scroll: false);
        }

        UpdateHelpText();
    }

    private async Task ExpandNodeAsync(TreeNode node)
    {
        if (!node.HasChildren || node.IsExpanded)
        {
            return;
        }

        if (_expandTokens.TryGetValue(node, out var previous))
        {
            previous.Cancel();
        }

        var tokenSource = new CancellationTokenSource();
        _expandTokens[node] = tokenSource;
        node.IsExpanded = true;
        RefreshRealizedExpansion();

        try
        {
            if (node.Children.Count == 0)
            {
                await node.LoadChildrenAsync(tokenSource.Token);
            }

            if (tokenSource.IsCancellationRequested || !node.IsExpanded)
            {
                return;
            }

            var visible = new List<TreeNode>();
            CollectVisible(node, visible);
            if (visible.Count == 0)
            {
                return;
            }

            for (var offset = 0; offset < visible.Count; offset += ExpandChunkSize)
            {
                tokenSource.Token.ThrowIfCancellationRequested();
                if (!node.IsExpanded)
                {
                    return;
                }

                var count = Math.Min(ExpandChunkSize, visible.Count - offset);
                var chunk = visible.GetRange(offset, count);
                var index = _rows.IndexOf(node);
                if (index < 0)
                {
                    return;
                }

                _rows.InsertRangeNotify(index + 1 + offset, chunk);
                UpdateHelpText();

                if (offset + count < visible.Count)
                {
                    await YieldToDispatcherAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Collapse cancelled the remaining chunks. The rows already inserted are removed by
            // CollapseNode; nothing here may reinsert them.
        }
        finally
        {
            if (_expandTokens.TryGetValue(node, out var current) && ReferenceEquals(current, tokenSource))
            {
                _expandTokens.Remove(node);
                tokenSource.Dispose();
            }
            else
            {
                tokenSource.Dispose();
            }
        }
    }

    private Task YieldToDispatcherAsync()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() => completion.TrySetResult(true)))
        {
            completion.TrySetResult(true);
        }

        return completion.Task;
    }

    private static void CollectVisible(TreeNode node, List<TreeNode> destination)
    {
        foreach (var child in node.Children)
        {
            destination.Add(child);
            if (child.IsExpanded)
            {
                CollectVisible(child, destination);
            }
        }
    }

    private static bool IsDescendantOf(TreeNode candidate, TreeNode ancestor)
    {
        for (var parent = candidate.Parent; parent is not null; parent = parent.Parent)
        {
            if (ReferenceEquals(parent, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not TreeRowElement row || args.Index < 0 || args.Index >= _rows.Count)
        {
            return;
        }

        var node = _rows[args.Index];
        row.Attach(this, node);
        row.SetSelected(ReferenceEquals(node, SelectedNode));
        _realized.Add(row);
    }

    private void OnElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is TreeRowElement row)
        {
            _realized.Remove(row);
            row.Detach();
        }
    }

    private void RefreshRealizedExpansion()
    {
        foreach (var row in _realized)
        {
            row.RefreshExpansion();
        }
    }

    private void RefreshRealizedSelection()
    {
        foreach (var row in _realized)
        {
            row.SetSelected(ReferenceEquals(row.Node, SelectedNode));
        }
    }

    private void RevealIndex(int index)
    {
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        double rowHeight = DenseMetrics.TreeRowHeight;
        double top = index * rowHeight;
        double bottom = top + rowHeight;
        double offset = Scroller.VerticalOffset;
        double viewport = Scroller.ViewportHeight;
        if (viewport <= 0)
        {
            return;
        }

        if (top < offset)
        {
            Scroller.ChangeView(null, top, null, disableAnimation: true);
        }
        else if (bottom > offset + viewport)
        {
            Scroller.ChangeView(null, bottom - viewport, null, disableAnimation: true);
        }
    }

    private void MoveTo(int index)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        index = Math.Clamp(index, 0, _rows.Count - 1);
        SelectNode(_rows[index]);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_renaming)
        {
            return;
        }

        int page = Math.Max(1, (int)(Scroller.ViewportHeight / DenseMetrics.TreeRowHeight) - 1);
        int current = SelectedNode is null ? -1 : _rows.IndexOf(SelectedNode);
        bool shift = IsKeyDown(VirtualKey.Shift);
        bool control = IsKeyDown(VirtualKey.Control);

        switch (e.Key)
        {
            case VirtualKey.Up:
                MoveTo(current < 0 ? 0 : current - 1);
                e.Handled = true;
                break;
            case VirtualKey.Down:
                MoveTo(current < 0 ? 0 : current + 1);
                e.Handled = true;
                break;
            case VirtualKey.PageUp:
                MoveTo(current < 0 ? 0 : current - page);
                e.Handled = true;
                break;
            case VirtualKey.PageDown:
                MoveTo(current < 0 ? 0 : current + page);
                e.Handled = true;
                break;
            case VirtualKey.Home:
                MoveTo(0);
                e.Handled = true;
                break;
            case VirtualKey.End:
                MoveTo(_rows.Count - 1);
                e.Handled = true;
                break;
            case VirtualKey.Left:
                CollapseOrGoToParent();
                e.Handled = true;
                break;
            case VirtualKey.Right:
                ExpandOrGoToChild();
                e.Handled = true;
                break;
            case VirtualKey.Multiply:
                ExpandSelectedSiblings();
                e.Handled = true;
                break;
            case VirtualKey.F2:
                BeginRename();
                e.Handled = true;
                break;
            case VirtualKey.Delete:
                if (SelectedNode is not null)
                {
                    DeleteRequested?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                }

                break;
            case VirtualKey.C when control:
                if (SelectedNode is not null)
                {
                    CopyKeyNameRequested?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                }

                break;
            case VirtualKey.F10 when shift:
            case VirtualKey.Application:
                RaiseContextAtSelection();
                e.Handled = true;
                break;
        }
    }

    private void RaiseContextAtSelection()
    {
        if (SelectedNode is not { } node)
        {
            return;
        }

        int index = _rows.IndexOf(node);
        if (index < 0)
        {
            return;
        }

        double y = (index * DenseMetrics.TreeRowHeight) - Scroller.VerticalOffset +
            (DenseMetrics.TreeRowHeight / 2);
        var point = new Point(24, Math.Clamp(y, 4, Math.Max(4, ActualHeight - 4)));
        ContextRequested?.Invoke(
            this,
            new TreeNodeContextRequestedEventArgs(node, this, point));
    }

    private static bool IsKeyDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) ==
        CoreVirtualKeyStates.Down;

    private void CollapseOrGoToParent()
    {
        var node = SelectedNode;
        if (node is null)
        {
            MoveTo(0);
            return;
        }

        if (node.IsExpanded)
        {
            CollapseNode(node);
            return;
        }

        if (node.Parent is { } parent)
        {
            MoveTo(_rows.IndexOf(parent));
        }
    }

    private void ExpandOrGoToChild()
    {
        var node = SelectedNode;
        if (node is null)
        {
            MoveTo(0);
            return;
        }

        if (node.HasChildren && !node.IsExpanded)
        {
            _ = ExpandNodeAsync(node);
            return;
        }

        if (node.IsExpanded)
        {
            MoveTo(_rows.IndexOf(node) + 1);
        }
    }

    private void ExpandSelectedSiblings()
    {
        var node = SelectedNode;
        if (node is null)
        {
            return;
        }

        IReadOnlyList<TreeNode> siblings = node.Parent is null ? _roots : node.Parent.Children;
        foreach (var sibling in siblings)
        {
            if (sibling.HasChildren && !sibling.IsExpanded)
            {
                _ = ExpandNodeAsync(sibling);
            }
        }
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (_renaming)
        {
            return;
        }

        char character = e.Character;
        if (char.IsControl(character) || _rows.Count == 0)
        {
            return;
        }

        long now = Environment.TickCount64;
        if (now - _typeAheadTick > TypeAheadWindowMs)
        {
            _typeAhead.Clear();
        }

        _typeAheadTick = now;
        if (_typeAhead.Length >= MaxTypeAheadLength)
        {
            _typeAhead.Remove(0, _typeAhead.Length - MaxTypeAheadLength + 1);
        }

        _typeAhead.Append(character);
        string prefix = _typeAhead.ToString();
        int current = SelectedNode is null ? -1 : _rows.IndexOf(SelectedNode);
        int start = prefix.Length == 1 ? current + 1 : Math.Max(0, current);

        for (var step = 0; step < _rows.Count; step++)
        {
            int index = ((start + step) % _rows.Count + _rows.Count) % _rows.Count;
            if (_rows[index].Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                MoveTo(index);
                break;
            }
        }

        e.Handled = true;
    }

    private void BeginRename()
    {
        var node = SelectedNode;
        if (node is null)
        {
            return;
        }

        _renaming = true;
        _renameNode = node;
        RenameBox.Text = node.Name;
        RenameBox.Visibility = Visibility.Visible;
        PositionRenameBox();
        RenameBox.Focus(FocusState.Programmatic);
        RenameBox.SelectAll();
    }

    private void PositionRenameBox()
    {
        if (_renameNode is null)
        {
            return;
        }

        var index = _rows.IndexOf(_renameNode);
        if (index < 0)
        {
            return;
        }

        double rowHeight = DenseMetrics.TreeRowHeight;
        double left = 18 + Math.Max(0, _renameNode.Depth) * DenseMetrics.TreeIndent;
        double available = Scroller.ViewportWidth - left - 8;
        double width = Math.Clamp(available, 120, 400);
        Canvas.SetLeft(RenameBox, left);
        Canvas.SetTop(RenameBox, index * rowHeight);
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
        if (!_renaming || _renameNode is null)
        {
            return;
        }

        var node = _renameNode;
        string newName = RenameBox.Text;
        EndRenameCore();
        RenameRequested?.Invoke(this, new TreeRenameRequestedEventArgs(node, newName));
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
        _renameNode = null;
        RenameBox.Visibility = Visibility.Collapsed;
        _ = Focus(FocusState.Keyboard);
    }

    private void CancelAllPendingExpands()
    {
        foreach (var token in _expandTokens.Values)
        {
            token.Cancel();
        }
    }

    private void UpdateHelpText()
    {
        AutomationProperties.SetHelpText(this, $"Rows={_rows.Count};Selected={SelectedNode?.Name ?? string.Empty}");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_metricsSubscribed)
        {
            DenseMetrics.Changed += OnMetricsChanged;
            _metricsSubscribed = true;
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

        CancelAllPendingExpands();
    }

    private void OnMetricsChanged(object? sender, EventArgs e)
    {
        ApplyMetrics();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        DenseTheme.Reset();
        foreach (var row in _realized)
        {
            row.ApplyTheme();
        }
    }
}

using System.Globalization;
using System.Text;
using Bwc.UI.Theme;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
#if BWC_E2E
using Microsoft.UI.Xaml.Automation;
#endif
using Microsoft.UI.Xaml.Automation.Peers;
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

/// <summary>A child load failed during expand. The node was collapsed again and no rows were added.</summary>
public sealed class TreeNodeErrorEventArgs : EventArgs
{
    public TreeNodeErrorEventArgs(TreeNode node, Exception exception)
    {
        Node = node;
        Exception = exception;
    }

    public TreeNode Node { get; }

    public Exception Exception { get; }
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
    private const long TypeAheadWindowMs = 1000;
    private const int MaxTypeAheadLength = 32;
    private const double MinimumLabelVisibleDip = 40;
    private const double LeftRevealMarginDip = 8;
    private const double RenameChrome = 2;
    private const double MinimumRenameWidth = 40;

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

    /// <summary>
    /// Raised when a child load fails during expand (for example access denied). The host shows the
    /// error. The tree has already collapsed the node, so no spinner or partial children remain.
    /// </summary>
    public event EventHandler<TreeNodeErrorEventArgs>? ExpandFailed;

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
        if (_rows.IndexOfNode(node) < 0)
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

    internal int IndexOfVisibleNode(TreeNode? node) => _rows.IndexOfNode(node);

    internal void RowTapped(TreeRowElement row)
    {
        if (row.Node is not { } node)
        {
            return;
        }

        SelectNode(node, scroll: false);
        _ = Focus(FocusState.Pointer);
    }

    /// <summary>
    /// A double-click on a row's label toggles the node. A double-click on a leaf does nothing, and
    /// the row is selected first, as a classic TreeView does.
    /// </summary>
    internal void RowDoubleTapped(TreeRowElement row)
    {
        if (row.Node is not { HasChildren: true } node)
        {
            return;
        }

        SelectNode(node, scroll: false);
        _ = Focus(FocusState.Pointer);
        ToggleNode(node);
    }

    internal void ChevronTapped(TreeRowElement row)
    {
        if (row.Node is not { HasChildren: true } node)
        {
            return;
        }

        SelectNode(node, scroll: false);
        ToggleNode(node);
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

    /// <summary>Collapses the selected node when it is expanded. The selection stays on that node.</summary>
    public void CollapseSelected()
    {
        if (SelectedNode is { IsExpanded: true } node)
        {
            CollapseNode(node);
        }
    }

    private void ToggleNode(TreeNode node)
    {
        if (node.IsExpanded)
        {
            CollapseNode(node);
        }
        else
        {
            _ = ExpandNodeAsync(node);
        }
    }

    /// <summary>
    /// Expands the selected node and every descendant that has children (numpad *). Each level is
    /// expanded in order through the normal chunked path, so a large subtree never blocks the UI
    /// thread. The walk stops when the root is collapsed while it runs or a child load fails.
    /// </summary>
    public async Task ExpandSubtreeSelectedAsync()
    {
        if (SelectedNode is not { HasChildren: true } root)
        {
            return;
        }

        var pending = new Stack<TreeNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (!ReferenceEquals(node, root) && !root.IsExpanded)
            {
                // The user collapsed the root while the walk ran. Hidden nodes are not loaded.
                break;
            }

            if (!node.HasChildren)
            {
                continue;
            }

            if (!node.IsExpanded)
            {
                await ExpandNodeAsync(node);
            }

            if (!node.IsExpanded)
            {
                // A failed or cancelled load, or the node was collapsed by the user.
                continue;
            }

            for (var i = node.Children.Count - 1; i >= 0; i--)
            {
                pending.Push(node.Children[i]);
            }
        }
    }

    /// <summary>Starts the inline rename box for the selected node. F2 uses the same path.</summary>
    public void BeginRenameSelected() => BeginRename();

    internal void RowRightPressed(TreeRowElement row)
    {
        if (row.Node is not { } node)
        {
            return;
        }

        SelectNode(node, scroll: false);
        _ = Focus(FocusState.Pointer);
    }

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
        var index = _rows.IndexOfNode(node);
        if (index >= 0)
        {
            RevealIndex(index);
            RevealHorizontally(node);
        }
    }

    /// <summary>
    /// Scrolls horizontally only when the selected node's label start is off screen, so a shallow
    /// selection keeps the classic unscrolled geometry. The label start stays a few DIPs in view on
    /// the left, and at least <see cref="MinimumLabelVisibleDip"/> of the label stays visible on the right.
    /// </summary>
    private void RevealHorizontally(TreeNode node)
    {
        double viewport = Scroller.ViewportWidth;
        if (viewport <= 0)
        {
            return;
        }

        double labelStart = TreeRowElement.LabelStartFor(node);
        double offset = Scroller.HorizontalOffset;
        if (labelStart < offset)
        {
            Scroller.ChangeView(Math.Max(0, labelStart - LeftRevealMarginDip), null, null, disableAnimation: true);
        }
        else if (labelStart + MinimumLabelVisibleDip > offset + viewport)
        {
            Scroller.ChangeView(labelStart + MinimumLabelVisibleDip - viewport, null, null, disableAnimation: true);
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

        var index = _rows.IndexOfNode(node);
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

        // A rename box over a row that is about to be removed has no row to sit on, so it ends first.
        if (_renameNode is { } renamed && IsDescendantOf(renamed, node))
        {
            EndRenameCore();
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
                var index = _rows.IndexOfNode(node);
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
        catch (Exception ex)
        {
            // A cancelled load can also fail (the registry changed under it). Only a live expand is
            // a failure the user should see.
            if (!tokenSource.IsCancellationRequested)
            {
                FailExpand(node, ex);
            }
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

    /// <summary>
    /// Collapses a node whose expand failed and removes any rows it managed to insert, so the node
    /// never stays expanded with no children. The retry is the next expand, because the node keeps
    /// HasChildren and its children stay unloaded.
    /// </summary>
    private void FailExpand(TreeNode node, Exception exception)
    {
        if (node.IsExpanded)
        {
            CollapseNode(node);
        }

        ExpandFailed?.Invoke(this, new TreeNodeErrorEventArgs(node, exception));
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
        int current = SelectedNode is null ? -1 : _rows.IndexOfNode(SelectedNode);
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
            case VirtualKey.Back:
                // ACC-025: Backspace selects the parent key. It does not collapse the selected key.
                GoToParent();
                e.Handled = true;
                break;
            case VirtualKey.Add:
                ExpandSelected();
                e.Handled = true;
                break;
            case VirtualKey.Subtract:
                CollapseSelected();
                e.Handled = true;
                break;
            case VirtualKey.Multiply:
                _ = ExpandSubtreeSelectedAsync();
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

        int index = _rows.IndexOfNode(node);
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

        GoToParent();
    }

    /// <summary>Selects the parent of the selected key. The root has no parent, so nothing changes there.</summary>
    private void GoToParent()
    {
        if (SelectedNode?.Parent is { } parent)
        {
            MoveTo(_rows.IndexOfNode(parent));
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

        // An expanded node moves to its first child. A node with no visible child stays put, so the
        // next row (a sibling) is never selected by Right.
        if (node.IsExpanded)
        {
            int next = _rows.IndexOfNode(node) + 1;
            if (next < _rows.Count && _rows[next].Depth > node.Depth)
            {
                MoveTo(next);
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
        int current = SelectedNode is null ? -1 : _rows.IndexOfNode(SelectedNode);

        // One letter repeated (or a single letter) cycles through the rows that start with it, from the
        // row after the selection. Any other prefix is matched from the selected row, inclusive, so a
        // longer prefix keeps the row the shorter prefix already selected.
        bool cycle = IsRepeatedCharacter(prefix);
        string search = cycle ? prefix[..1] : prefix;
        int start = cycle ? current + 1 : Math.Max(0, current);

        for (var step = 0; step < _rows.Count; step++)
        {
            int index = (start + step) % _rows.Count;
            if (_rows[index].Name.StartsWith(search, StringComparison.OrdinalIgnoreCase))
            {
                MoveTo(index);
                break;
            }
        }

        e.Handled = true;
    }

    private static bool IsRepeatedCharacter(string text)
    {
        for (var i = 1; i < text.Length; i++)
        {
            if (char.ToUpperInvariant(text[i]) != char.ToUpperInvariant(text[0]))
            {
                return false;
            }
        }

        return true;
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
        // Select the whole name with the caret at the start, so a long name shows its beginning
        // instead of scrolling to its tail. Typing still replaces the selection.
        RenameBox.SelectionStart = 0;
        RenameBox.SelectionLength = RenameBox.Text.Length;
    }

    private void PositionRenameBox()
    {
        if (_renameNode is null)
        {
            return;
        }

        var index = _rows.IndexOfNode(_renameNode);
        if (index < 0)
        {
            return;
        }

        double rowHeight = DenseMetrics.TreeRowHeight;
        double labelLeft = TreeRowElement.LabelStartFor(_renameNode);
        double viewport = Scroller.ViewportWidth > 0 ? Scroller.ViewportWidth : ActualWidth;
        double left = Math.Max(0, labelLeft);
        double visible = Math.Max(0, viewport - left - 1);
        double labelWidth = MeasureTextWidth(_renameNode.Name);
        double width = Math.Min(
            Math.Max(MinimumRenameWidth, labelWidth + RenameChrome),
            Math.Max(MinimumRenameWidth, visible));

        Canvas.SetLeft(RenameBox, left);
        Canvas.SetTop(RenameBox, index * rowHeight);
        RenameBox.Width = width;
        RenameBox.Height = rowHeight + 2;
        RenameBox.FontSize = DenseMetrics.FontSize;
        RenameBox.FontFamily = DenseMetrics.FontFamily;
#if BWC_E2E
        // E2E-only geometry trace: the rename box and the label it replaces, all in content DIPs.
        AutomationProperties.SetHelpText(
            RenameBox,
            string.Create(
                CultureInfo.InvariantCulture,
                $"LabelLeft={labelLeft:0.##};LabelWidth={labelWidth:0.##};VisibleWidth={visible:0.##};BoxLeft={left:0.##};BoxWidth={width:0.##};BoxHeight={RenameBox.Height:0.##}"));
#endif
    }

    /// <summary>The width of the node label at the current font, in DIPs.</summary>
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

    /// <summary>
    /// Classic behavior: clicking elsewhere applies the rename. CommitRename clears the rename state
    /// before it raises the event, so the focus change that closes the box is a no-op.
    /// </summary>
    private void OnRenameLostFocus(object sender, RoutedEventArgs e)
    {
        if (_renaming)
        {
            CommitRename();
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
#if BWC_E2E
        // E2E-only: debug counts in UIA help text would be announced by screen readers in ship builds.
        AutomationProperties.SetHelpText(this, $"Rows={_rows.Count};Selected={SelectedNode?.Name ?? string.Empty}");
#endif
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

    protected override AutomationPeer OnCreateAutomationPeer() => new DenseTreeAutomationPeer(this);
}

/// <summary>
/// UIA peer for the tree control itself. It reports the Tree control type so clients treat the rows
/// as tree items. The name and automation id come from the XAML AutomationProperties.
/// </summary>
internal sealed class DenseTreeAutomationPeer : FrameworkElementAutomationPeer
{
    public DenseTreeAutomationPeer(DenseTree owner)
        : base(owner)
    {
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Tree;

    protected override string GetClassNameCore() => "DenseTree";

    protected override bool IsControlElementCore() => true;

    protected override bool IsContentElementCore() => true;
}

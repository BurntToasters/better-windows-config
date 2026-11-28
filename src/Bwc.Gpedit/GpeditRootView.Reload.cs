using Bwc.Core;
using Bwc.Gpedit.List;
using Bwc.Gpedit.Policy;
using Bwc.Gpedit.Tree;
using Bwc.Gpo.Store;
using Bwc.UI.Console.List;
using Bwc.UI.Console.Shell;
using Bwc.UI.Tree;

namespace Bwc.Gpedit;

/// <summary>
/// Loading the model and Refresh, which loads it again (piece G2, docs/failure-modes/gpedit-commands.md, sections 5, 6
/// and 9). Each load has a token source of its own. It is cancelled and disposed when the next load starts and when the
/// view closes.
/// A request while a load is in flight is merged into one more load. A load that finishes while any modal is open is held
/// for a fresh load after the dialog closes, so the model never changes under it. The new model, its tree and the selection
/// replace the old ones in one step on the UI thread.
/// </summary>
public sealed partial class GpeditRootView
{
    private const char PathSeparator = '\u001f';

    private CancellationTokenSource? _loadCancel;
    private bool _loadInFlight;
    private bool _loadIsInitial;
    private bool _reloadPending;
    private bool _closed;
    private int _refreshes;

    /// <summary>The selected row, found again after a reload: the policy name and side, or the folder name.</summary>
    private readonly record struct RowKey(string Name, GpoSide? Side);

    // ---- starting a load ----------------------------------------------------------------------------

    private void StartModelLoad()
    {
        _source = new GpeditTreeSource(BeginLoad(initial: true), _filter);
    }

    private Task<GpeditModel> BeginLoad(bool initial)
    {
        CancelLoad();
        var cancel = new CancellationTokenSource();
        _loadCancel = cancel;
        CancellationToken token = cancel.Token;
        Task<GpeditModel> task = Task.Run(() => GpeditModel.Load(token), token);
        _loadInFlight = true;
        _loadIsInitial = initial;
        _ = AwaitLoadAsync(task, cancel, initial);
        return task;
    }

    /// <summary>Cancels and disposes the token source of the load in use, if any.</summary>
    private void CancelLoad()
    {
        CancellationTokenSource? old = _loadCancel;
        _loadCancel = null;
        if (old is null)
        {
            return;
        }

        try
        {
            old.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed: nothing is left to cancel.
        }

        old.Dispose();
    }

    private void CloseLoads()
    {
        _closed = true;
        _reloadPending = false;
        _loadInFlight = false;
        CancelLoad();
    }

    /// <summary>Refresh: reads the templates and the policy files again, after any open modal closes.</summary>
    private void RunRefresh()
    {
        if (_closed)
        {
            return;
        }

        RecordCommand(CommandRefresh);
        RequestReload();
    }

    /// <summary>
    /// Starts a reload, or merges the request into the load in flight (the last request wins). Refresh and the close of the
    /// Add/Remove Templates dialog after a change both come here.
    /// </summary>
    private void RequestReload()
    {
        if (_closed)
        {
            return;
        }

        if (_modalBusy)
        {
            _reloadPending = true;
            UpdateReadout();
            return;
        }

        if (_loadInFlight)
        {
            // The last request wins: the load in flight is cancelled (a reload only) and one more load follows it.
            _reloadPending = true;
            if (!_loadIsInitial)
            {
                try
                {
                    _loadCancel?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The load finished and was disposed between the check and the call.
                }
            }

            UpdateReadout();
            return;
        }

        _ = BeginLoad(initial: false);
        UpdateReadout();
    }

    // ---- finishing a load ---------------------------------------------------------------------------

    private async Task AwaitLoadAsync(Task<GpeditModel> task, CancellationTokenSource owner, bool initial)
    {
        GpeditModel? model = null;
        try
        {
            model = await task;
        }
        catch (OperationCanceledException)
        {
            // Cancelled by a newer request or by the close of the window.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("Local Group Policy Editor model could not be loaded: " + ex.Message);
        }

        try
        {
            FinishLoad(owner, model, initial);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("Local Group Policy Editor model could not be applied: " + ex.Message);
        }
    }

    private void FinishLoad(CancellationTokenSource owner, GpeditModel? model, bool initial)
    {
        if (_closed || !ReferenceEquals(_loadCancel, owner))
        {
            return;
        }

        _loadInFlight = false;
        if (_modalBusy)
        {
            _reloadPending = true;
            StateChanged();
            return;
        }

        bool again = _reloadPending;
        _reloadPending = false;
        if (again && _policyDialog is null)
        {
            if (initial && model is not null)
            {
                ApplyInitialModel(model);
            }

            _ = BeginLoad(initial: false);
            UpdateReadout();
            return;
        }

        if (model is null || (!initial && _policyDialog is not null))
        {
            _reloadFromTemplates = false;
            StateChanged();
            return;
        }

        if (initial)
        {
            ApplyInitialModel(model);
        }
        else
        {
            ApplyReloadedModel(model);
        }
    }

    private void ApplyReloadedModel(GpeditModel model) => ReplaceTree(model, reloaded: true);

    private void ApplyInitialModel(GpeditModel model)
    {
        _model = model;
        UpdateReadOnlyNotice(model);
        UpdateTemplateNotice(model);
        Log.Info(
            $"Local Group Policy Editor model loaded in {model.LoadTime.TotalMilliseconds:0} ms: " +
            $"{model.Templates.Files.Count} templates, {model.Templates.Categories.Count} categories, " +
            $"{model.Templates.Policies.Count} policies, {model.Problems.Count} problems.");
        if (Shell.TreeContent.SelectedNode is { } selected)
        {
            ShowNode(selected);
        }

        StateChanged();
    }

    /// <summary>
    /// Replaces the model, the tree and the list in one step. The expanded nodes, the selected node (or its nearest ancestor
    /// that the new templates still have) and the selected row are found again by name; the history keeps the entries that
    /// still exist. A change of the filter (Filter On, the options, Re-Apply Filter) uses it with the model it has, since the
    /// tree is made once: folders the filter hides are not in it, and a hidden selected node gives way to its nearest visible
    /// ancestor. A reload applies the filter again to the files it read.
    /// </summary>
    private void ReplaceTree(GpeditModel model, bool reloaded)
    {
        TreeNode? selected = Shell.TreeContent.SelectedNode;
        List<string> selectedNames = GpeditTreeSource.NamesOf(selected);
        HashSet<string> expanded = [];
        CollectExpanded(Shell.TreeContent.Roots, expanded);
        if (!reloaded && selected is not null)
        {
            // A new filter rebuilds the tree and opens the node that is current, so its matching children show.
            _ = expanded.Add(KeyOf(selectedNames));
        }

        RowKey? row = CaptureSelectedRow();
        bool listFocused = Shell.FocusedPane == ConsolePane.Result;
        List<List<string>> historyNames = _history.Select(GpeditTreeSource.NamesOf).ToList();
        int oldIndex = _historyIndex;

        if (reloaded)
        {
            _filter.Rebase(model);
        }

        var source = new GpeditTreeSource(Task.FromResult(model), _filter);
        RestoreExpansion(source, model, expanded);
        _model = model;
        _source = source;
        if (reloaded)
        {
            // A reload that closing the Add/Remove Templates dialog started is counted as that, not as a Refresh.
            if (_reloadFromTemplates)
            {
                _reloadFromTemplates = false;
            }
            else
            {
                _refreshes++;
            }

            UpdateReadOnlyNotice(model);
            UpdateTemplateNotice(model);
        }

        TreeNode current;
        bool exact;
        _navigating = true;
        try
        {
            Shell.TreeContent.SetRoots([source.Root]);
            TreeNode? found = source.Resolve(selectedNames, model, out int matched);
            exact = found is not null && matched == selectedNames.Count;
            current = SelectNearest(found, source.Root);
        }
        finally
        {
            _navigating = false;
        }

        RemapHistory(source, model, historyNames, oldIndex, current);
        if (exact && row is { } key)
        {
            SelectRow(key, listFocused);
        }

        if (reloaded)
        {
            Log.Info(
                $"Local Group Policy Editor model reloaded in {model.LoadTime.TotalMilliseconds:0} ms: " +
                $"{model.Templates.Policies.Count} policies, {model.Problems.Count} problems.");
        }

        StateChanged();
    }

    /// <summary>Selects the node, else the nearest ancestor the tree can show, else the root. Returns the selection.</summary>
    private TreeNode SelectNearest(TreeNode? node, TreeNode root)
    {
        for (TreeNode? candidate = node; candidate is not null; candidate = candidate.Parent)
        {
            Shell.TreeContent.SelectNode(candidate);
            if (ReferenceEquals(Shell.TreeContent.SelectedNode, candidate))
            {
                return candidate;
            }
        }

        Shell.TreeContent.SelectNode(root);
        return root;
    }

    /// <summary>Shows the notice of a window that cannot save, with the first reason that applies, or hides it.</summary>
    private void UpdateReadOnlyNotice(GpeditModel model)
    {
        if (!model.IsReadOnly)
        {
            ReadOnlyNotice.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            return;
        }

        string key = model.Access.Reasons.Count == 0
            ? "GpeditReadOnlyNoWriteAccess"
            : model.Access.Reasons[0] switch
            {
                GpoReadOnlyReason.NotElevated => "GpeditReadOnlyNotElevated",
                GpoReadOnlyReason.HomeEdition => "GpeditReadOnlyHomeEdition",
                _ => "GpeditReadOnlyNoWriteAccess",
            };
        string text = GpeditStrings.Tree(key);
        ReadOnlyNotice.Text = text;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ReadOnlyNotice, text);
        ReadOnlyNotice.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
    }

    // ---- what a reload keeps ------------------------------------------------------------------------

    private static string KeyOf(IEnumerable<string> names) => string.Join(PathSeparator, names);

    private static void CollectExpanded(IEnumerable<TreeNode> nodes, HashSet<string> expanded)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.IsExpanded)
            {
                _ = expanded.Add(KeyOf(GpeditTreeSource.NamesOf(node)));
            }

            CollectExpanded(node.Children, expanded);
        }
    }

    /// <summary>
    /// Expands the nodes of the new tree that were expanded in the old one. A node that does not exist in the new
    /// templates is skipped. The categories under an expanded node are created, since expanding shows them.
    /// </summary>
    private static void RestoreExpansion(GpeditTreeSource source, GpeditModel model, HashSet<string> expanded)
    {
        var withExpandedBelow = new HashSet<string>();
        foreach (string path in expanded)
        {
            for (int at = path.LastIndexOf(PathSeparator); at > 0; at = path.LastIndexOf(PathSeparator, at - 1))
            {
                if (!withExpandedBelow.Add(path[..at]))
                {
                    break;
                }
            }
        }

        void Apply(TreeNode node, string path)
        {
            bool open = expanded.Contains(path);
            if (open || withExpandedBelow.Contains(path))
            {
                foreach (TreeNode child in source.EnsureChildren(node, model))
                {
                    Apply(child, path + PathSeparator + child.Name);
                }
            }

            node.IsExpanded = open && node.Children.Count > 0;
        }

        Apply(source.Root, source.Root.Name);
    }

    private RowKey? CaptureSelectedRow()
    {
        IReadOnlyList<ConsoleListRow> selected = _list.SelectedRows;
        if (_placeholderShown || selected.Count != 1 || selected[0].Tag is not GpeditRowTag tag)
        {
            return null;
        }

        return tag is { Kind: GpeditRowKind.Policy, Policy: { } policy }
            ? new RowKey(policy.FullName, tag.Side)
            : new RowKey(selected[0].Text, null);
    }

    private void SelectRow(RowKey key, bool focus)
    {
        IReadOnlyList<ConsoleListRow> rows = _list.Rows;
        for (int i = 0; i < rows.Count; i++)
        {
            bool match = rows[i].Tag is GpeditRowTag tag && (key.Side is { } side
                ? tag is { Kind: GpeditRowKind.Policy, Policy: { } policy } && policy.FullName == key.Name && tag.Side == side
                : tag.Kind == GpeditRowKind.Folder && rows[i].Text == key.Name);
            if (match)
            {
                _list.SelectIndex(i, focus);
                return;
            }
        }
    }

    /// <summary>
    /// Keeps the history entries whose node exists in the new tree, in order and without neighbours that repeat, and drops
    /// the others. The current entry is the node now selected.
    /// </summary>
    private void RemapHistory(GpeditTreeSource source, GpeditModel model, List<List<string>> names, int oldIndex, TreeNode current)
    {
        var mapped = new List<TreeNode>();
        int newIndex = -1;
        for (int i = 0; i < names.Count; i++)
        {
            TreeNode? node = current;
            if (i != oldIndex)
            {
                node = source.Resolve(names[i], model, out int matched);
                if (matched != names[i].Count)
                {
                    node = null;
                }
            }

            if (node is null)
            {
                continue;
            }

            if (mapped.Count == 0 || !ReferenceEquals(mapped[^1], node))
            {
                mapped.Add(node);
            }

            if (i == oldIndex)
            {
                newIndex = mapped.Count - 1;
            }
        }

        if (newIndex < 0)
        {
            mapped.Add(current);
            newIndex = mapped.Count - 1;
        }

        if (mapped.Count > HistoryLimit)
        {
            int excess = mapped.Count - HistoryLimit;
            mapped.RemoveRange(0, excess);
            newIndex = Math.Max(0, newIndex - excess);
        }

        _history.Clear();
        _history.AddRange(mapped);
        _historyIndex = newIndex;
    }
}

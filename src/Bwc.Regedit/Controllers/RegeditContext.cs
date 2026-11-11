using Bwc.Core;
using Bwc.Regedit.Commands;
using Bwc.Regedit.Registry;
using Bwc.Registry;
using Bwc.UI.Dialogs;
using Bwc.UI.List;
using Bwc.UI.Shell;
using Bwc.UI.Tree;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Bwc.Regedit.Controllers;

/// <summary>
/// State and services shared by the Registry Editor controllers: the registry engine, the tree and
/// list panes, selection, the modal gates, the owner window, and the command error boundary.
/// </summary>
internal sealed class RegeditContext
{
    /// <summary>The longest value name the registry accepts, in characters.</summary>
    public const int MaxValueNameLength = 16383;

    /// <summary>The two panes that Delete, Rename, and the Edit menu act on.</summary>
    public enum Pane
    {
        Tree,
        List,
    }

    /// <summary>
    /// List rows and scroll position to restore after a refresh. The rows are applied when the
    /// value list for the new tree selection arrives.
    /// </summary>
    public sealed record PendingListView(IReadOnlyList<string> RowNames, double ListOffset);

    public RegeditContext(
        ClassicWindowShell shell,
        MessageCatalog messages,
        RegistryStore store,
        RegistryTreeSource treeSource,
        RegistryValueSource valueSource,
        FavoritesStore favorites,
        RegistryFinder finder,
        IReadOnlyList<RegistryPath>? findRoots)
    {
        Shell = shell;
        Messages = messages;
        Store = store;
        TreeSource = treeSource;
        ValueSource = valueSource;
        Favorites = favorites;
        Finder = finder;
        FindRoots = findRoots;
    }

    /// <summary>The classic shell that hosts the menu, the tree, the list, and the address bar.</summary>
    public ClassicWindowShell Shell { get; }

    public MessageCatalog Messages { get; }

    /// <summary>The write-capable store. Reads go through its Reader.</summary>
    public RegistryStore Store { get; }

    public RegistryValueSource ValueSource { get; }

    public FavoritesStore Favorites { get; }

    public RegistryFinder Finder { get; }

    public IReadOnlyList<RegistryPath>? FindRoots { get; }

    /// <summary>The in-memory view state. Updated by selection changes, not written to disk.</summary>
    public RegeditSettings Settings { get; } = new();

    public List<RemoteRegistryConnection> RemoteConnections { get; } = new();

    public RegistryTreeSource TreeSource { get; set; }

    /// <summary>The owner window handle. Zero until the window attaches.</summary>
    public nint WindowHwnd { get; set; }

    public bool DialogOpen { get; set; }

    public bool FileDialogOpen { get; set; }

    /// <summary>
    /// True while a navigation writes the tree selection. Selection events raised then are not user
    /// navigation and do not advance the generation.
    /// </summary>
    public bool ApplyingNavigation { get; set; }

    public int NavigationGeneration { get; set; }

    public int ValueGeneration { get; set; }

    public CancellationTokenSource? ValueLoad { get; set; }

    /// <summary>The pane that most recently had keyboard focus. Menu commands use it while the menu bar has focus.</summary>
    public Pane LastFocusedPane { get; set; } = Pane.Tree;

    /// <summary>Set by a keep-view refresh. The next value list to load restores these rows and this scroll.</summary>
    public PendingListView? PendingList { get; set; }

    /// <summary>The pane with keyboard focus, or the last pane that had it when neither pane has focus.</summary>
    public Pane FocusedPane()
    {
        if (IsFocusWithin(Shell.ListContent))
        {
            return Pane.List;
        }

        if (IsFocusWithin(Shell.TreeContent))
        {
            return Pane.Tree;
        }

        return LastFocusedPane;
    }

    /// <summary>
    /// The list row for the synthetic (Default) row: the registry has no default value, so the row has
    /// no raw name. It is listed for every key, as classic regedit does.
    /// </summary>
    public bool IsSyntheticDefault(DetailsRow row) =>
        string.Equals(row.Name, MessageCatalog.DefaultValueLabel, StringComparison.Ordinal) &&
        !ValueSource.RawNamesByName.ContainsKey(MessageCatalog.DefaultValueLabel);

    /// <summary>Number of selected rows that have a registry value behind them.</summary>
    public int SelectedRealValueCount()
    {
        int count = 0;
        foreach (DetailsRow row in Shell.ListContent.SelectedRows)
        {
            if (!IsSyntheticDefault(row))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Runs one command and reports its failure. Callers do not await the result: the command runs
    /// on the UI thread and its errors are shown as dialogs.
    /// </summary>
    public void StartCommand(Func<Task> command)
    {
        _ = RunCommandAsync(command);
    }

    /// <summary>
    /// Runs one registry engine call on the thread pool. The awaiting command resumes on the UI thread,
    /// so the result and any exception are applied there, in the same order as before.
    /// </summary>
    public Task RunRegistryAsync(Action operation, CancellationToken cancellationToken = default) =>
        Task.Run(operation, cancellationToken);

    public Task<T> RunRegistryAsync<T>(Func<T> operation, CancellationToken cancellationToken = default) =>
        Task.Run(operation, cancellationToken);

    /// <summary>
    /// Runs one registry write on the thread pool with the modal gate held. Commands that check the
    /// gate are refused until the write finishes, so no second command starts while it runs.
    /// </summary>
    public async Task RunRegistryWriteAsync(Action operation)
    {
        bool wasOpen = DialogOpen;
        DialogOpen = true;
        try
        {
            await RunRegistryAsync(operation);
        }
        finally
        {
            DialogOpen = wasOpen;
        }
    }

    public async Task<bool> ShowDialogAsync(ModalDialogWindow dialog)
    {
        if (DialogOpen)
        {
            Log.Warn("Registry Editor ignored a dialog request because another modal is already open.");
            return false;
        }

        if (WindowHwnd == 0)
        {
            Log.Warn("Registry Editor ignored a dialog request because the window handle is not attached.");
            return false;
        }

        DialogOpen = true;
        try
        {
            return await dialog.ShowAsync(WindowHwnd);
        }
        catch (Exception ex)
        {
            Log.Warn("Registry Editor could not show a dialog: " + ex.Message);
            return false;
        }
        finally
        {
            DialogOpen = false;
        }
    }

    public Task ShowMessageAsync(string message) =>
        ShowDialogAsync(ErrorDialog.ForMessage(Messages.Get("DialogErrorTitle"), message));

    /// <summary>Shows a validation message under the caption of the command that refused it.</summary>
    public Task ShowMessageAsync(string caption, string message) =>
        ShowDialogAsync(ErrorDialog.ForMessage(caption, message));

    public Task ShowErrorAsync(ErrorDialog dialog) => ShowDialogAsync(dialog);

    /// <summary>
    /// Shows the classic message for a failed registry operation. The action names what the command
    /// was doing. Without one, the engine operation name picks the action. The developer detail is
    /// logged here and not shown.
    /// </summary>
    public async Task ShowRegistryErrorAsync(
        RegistryOperationException ex,
        RegistryAction? action = null,
        string? subject = null)
    {
        Log.Warn(
            $"Registry Editor command failed: {ex.Operation} for {ex.Path.ToDisplayString()}, Win32 {ex.Win32Error}: {ex.Message}");

        RegistryAction resolved = action ?? MessageCatalog.ActionFor(ex.Operation);
        string name = subject ?? DefaultSubject(ex.Path);
        await ShowErrorAsync(Messages.ForRegistryError(resolved, ex, name).ToErrorDialog());

        if (ex.Win32Error == 2)
        {
            // The selected key disappeared under the view. Rebuild the tree so a stale node is
            // not left behind; RefreshTreeAsync falls back to Computer when the path is gone.
            string? current = Shell.TreeContent.SelectedNode?.RegistryPath;
            await RefreshTreeAsync(current);
        }
    }

    /// <summary>
    /// Shows the classic error for a key that cannot be opened for reading. The developer detail
    /// goes to the log only.
    /// </summary>
    public Task ShowUnreadableKeyAsync(RegistryPath path, RegistryOperationException ex)
    {
        Log.Warn(
            $"Registry Editor could not open '{path.ToDisplayString()}' for reading: Win32 {ex.Win32Error} ({ex.Message}).");
        return ShowErrorAsync(Messages.ForUnreadableKey(path.ToDisplayString(), ex.Win32Error).ToErrorDialog());
    }

    /// <summary>
    /// Opens the key with the same rights as a read and returns the failure, or null when it opens.
    /// The value source catches its own failures and would hide this one, so the probe runs first.
    /// </summary>
    public RegistryOperationException? ProbeKeyForRead(RegistryPath path)
    {
        try
        {
            _ = Store.Reader.GetKeyInfo(path);
            return null;
        }
        catch (RegistryOperationException ex)
        {
            return ex;
        }
    }

    /// <summary>The name a message names for a key: the leaf name, or the root name for a root.</summary>
    public static string DefaultSubject(RegistryPath path) =>
        path.Components.Count == 0 ? path.ToDisplayString() : path.Components[^1];

    public Task ShowAddressBarErrorAsync(string message, string attempted)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(attempted))
        {
            lines.Add(MessageCatalog.PathLinePrefix + attempted);
            lines.Add(string.Empty);
        }

        lines.Add(message);
        return ShowErrorAsync(
            ErrorDialog.ForMessage(Messages.Get("DialogErrorTitle"), string.Join(Environment.NewLine, lines)));
    }

    /// <summary>Shows the open or save dialog. Returns null when cancelled or when another modal is open.</summary>
    public string? ShowFileDialog(bool save, string? defaultFileName)
    {
        if (DialogOpen || FileDialogOpen)
        {
            Log.Warn("Registry Editor ignored a file dialog request because another modal is already open.");
            return null;
        }

        if (WindowHwnd == 0)
        {
            Log.Warn("Registry Editor ignored a file dialog request because the window handle is not attached.");
            return null;
        }

        FileDialogOpen = true;
        try
        {
            return save
                ? CommonFileDialogs.ShowSave(WindowHwnd, defaultFileName)
                : CommonFileDialogs.ShowOpen(WindowHwnd, defaultFileName);
        }
        catch (Exception ex)
        {
            Log.Warn("Registry Editor could not show a file dialog: " + ex.Message);
            _ = ShowErrorAsync(Messages.ForCommandFailure().ToErrorDialog());
            return null;
        }
        finally
        {
            FileDialogOpen = false;
        }
    }

    public bool TryGetSelectedKeyPath(out RegistryPath path)
    {
        path = null!;
        TreeNode? node = Shell.TreeContent.SelectedNode;
        if (node?.RegistryPath is not { Length: > 0 } displayPath ||
            !RegistryPath.TryParse(displayPath, out RegistryPath? parsed) ||
            parsed is null)
        {
            return false;
        }

        path = parsed;
        return true;
    }

    /// <summary>
    /// The registry value name behind a list row. False for the synthetic (Default) row, which has no
    /// value behind it.
    /// </summary>
    public bool TryGetRawValueName(DetailsRow row, out string rawName)
    {
        if (ValueSource.RawNamesByName.TryGetValue(row.Name, out string? mapped) && mapped is not null)
        {
            rawName = mapped;
            return true;
        }

        if (string.Equals(row.Name, MessageCatalog.DefaultValueLabel, StringComparison.Ordinal))
        {
            rawName = string.Empty;
            return false;
        }

        rawName = row.Name;
        return true;
    }

    /// <summary>True for a direct child of HKLM or HKU, the only keys that Load Hive and Unload Hive accept.</summary>
    public static bool IsHiveRootChild(RegistryPath path) =>
        path.Components.Count == 1 &&
        path.Root is RegistryRoot.LocalMachine or RegistryRoot.Users;

    /// <summary>Refreshes the enabled state of every command that depends on the selection.</summary>
    public void UpdateCommandStates()
    {
        bool hasKey = TryGetSelectedKeyPath(out RegistryPath key);
        bool mutableKey = hasKey && !key.IsRoot;
        IReadOnlyList<DetailsRow> rows = Shell.ListContent.SelectedRows;
        bool hasRows = rows.Count > 0;
        int realValues = SelectedRealValueCount();
        bool singleRow = rows.Count == 1;
        bool singleRealValue = singleRow && realValues == 1;

        // Delete and Rename act on the pane with focus. The list state applies only when the list is
        // the target, so a selected value does not enable Delete for a key that has focus.
        bool treeTarget = FocusedPane() == Pane.Tree;

        foreach (int id in new[] { 663, 664, 665, 674, 688, 676, 677 })
        {
            RegeditMenu.SetCommandEnabled(Shell.MenuBar, id, hasKey);
        }

        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 913, treeTarget ? mutableKey : realValues > 0);
        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 659, hasKey);
        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 660, hasKey);
        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 679, singleRealValue);
        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 678, hasKey && !hasRows);
        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 675, hasKey);
        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 680, true);
        RegeditMenu.SetCommandEnabled(
            Shell.MenuBar,
            681,
            hasKey && !hasRows && IsHiveRootChild(key));
        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 657, true);
        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 768, RemoteConnections.Count > 0);
        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 912, singleRow);
        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 917, singleRealValue);
        RegeditMenu.SetValueCommandsVisible(Shell.MenuBar, hasRows);

        bool canRename = treeTarget || !hasRows ? mutableKey : singleRow && rows[0].Renameable;
        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 914, canRename);
    }

    /// <summary>Returns focus to the selected tree node after a dialog. Focus errors are logged, not shown.</summary>
    public void FocusTreeAfterDialog()
    {
        try
        {
            Shell.TreeContent.FocusSelected();
        }
        catch (Exception ex)
        {
            Log.Warn("Registry Editor could not return focus to the tree: " + ex.Message);
        }
    }

    public bool IsFocusWithin(DependencyObject root)
    {
        try
        {
            object? focused = FocusManager.GetFocusedElement(Shell.XamlRoot);
            return focused is DependencyObject element && IsDescendantOf(element, root);
        }
        catch (Exception ex)
        {
            Log.Warn("Registry Editor could not read the focused element: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Rebuilds the tree from the current sources, keeps every key that was expanded expanded, and
    /// selects the path, or Computer when it is gone. With <paramref name="keepView"/> (F5), the list
    /// selection and the scroll positions of the tree and the list are kept as well.
    /// </summary>
    public async Task RefreshTreeAsync(string? selectDisplayPath, bool keepView = false)
    {
        IReadOnlyList<string> expanded = CollectExpandedPaths(TreeSource.Roots);
        double treeOffset = FindDescendant<ScrollViewer>(Shell.TreeContent)?.VerticalOffset ?? 0;
        double listOffset = FindDescendant<ScrollViewer>(Shell.ListContent)?.VerticalOffset ?? 0;
        string[] listRowNames = keepView
            ? Shell.ListContent.SelectedRows.Select(row => row.Name).ToArray()
            : Array.Empty<string>();

        var source = new RegistryTreeSource(Store.Reader, RemoteConnections);
        foreach (string path in expanded)
        {
            TreeNode? node = await ResolveNodeAsync(source.Computer, path, expandAncestors: false);
            if (node is { HasChildren: true })
            {
                _ = await node.LoadChildrenAsync(CancellationToken.None);
                node.IsExpanded = true;
            }
        }

        TreeNode? target = string.IsNullOrWhiteSpace(selectDisplayPath)
            ? null
            : await ResolveNodeAsync(source.Computer, selectDisplayPath, expandAncestors: true);

        TreeSource = source;
        if (keepView)
        {
            // Set before the selection, because the selection starts the value load that applies it.
            PendingList = new PendingListView(listRowNames, listOffset);
        }

        Shell.TreeContent.SetRoots(source.Roots);
        Shell.TreeContent.SelectNode(target ?? source.Computer);
        if (keepView)
        {
            RestoreScrollOffset(Shell.TreeContent, treeOffset);
        }

        UpdateCommandStates();
    }

    /// <summary>Refreshes the tree and the value list for F5, keeping the view. See <see cref="RefreshTreeAsync"/>.</summary>
    public Task RefreshViewAsync() =>
        RefreshTreeAsync(Shell.TreeContent.SelectedNode?.RegistryPath, keepView: true);

    /// <summary>
    /// The display paths of every expanded key, parents before children. Computer has no path and is
    /// skipped, because it is always expanded.
    /// </summary>
    private static List<string> CollectExpandedPaths(IEnumerable<TreeNode> nodes)
    {
        var paths = new List<string>();
        CollectExpandedPaths(nodes, paths);
        return paths;
    }

    private static void CollectExpandedPaths(IEnumerable<TreeNode> nodes, List<string> paths)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.IsExpanded && node.RegistryPath is { Length: > 0 } path)
            {
                paths.Add(path);
            }

            CollectExpandedPaths(node.Children, paths);
        }
    }

    /// <summary>
    /// Finds the node for a display path by walking the names from Computer. Each level is loaded on the
    /// way down, so the walk works for local and remote paths alike. Null when a name is missing. With
    /// <paramref name="expandAncestors"/>, every node on the way is expanded, so the result is visible.
    /// </summary>
    private static async Task<TreeNode?> ResolveNodeAsync(TreeNode computer, string displayPath, bool expandAncestors)
    {
        TreeNode current = computer;
        foreach (string name in SplitDisplayPath(displayPath))
        {
            IReadOnlyList<TreeNode> children = await current.LoadChildrenAsync(CancellationToken.None);
            TreeNode? next = null;
            foreach (TreeNode child in children)
            {
                if (string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    next = child;
                    break;
                }
            }

            if (next is null)
            {
                return null;
            }

            if (expandAncestors)
            {
                current.IsExpanded = true;
            }

            current = next;
        }

        return current;
    }

    /// <summary>
    /// Splits a tree display path into node names below Computer. A local path is
    /// <c>Computer\HKEY...\name</c>. A remote path is <c>\\COMPUTER\HKEY...\name</c>, and its first name
    /// is the remote computer node.
    /// </summary>
    private static List<string> SplitDisplayPath(string displayPath)
    {
        var names = new List<string>();
        string rest = displayPath.Trim();
        if (rest.StartsWith(@"\\", StringComparison.Ordinal))
        {
            int separator = rest.IndexOf('\\', 2);
            if (separator < 0)
            {
                names.Add(rest);
                return names;
            }

            names.Add(rest[..separator]);
            rest = rest[(separator + 1)..];
        }
        else if (string.Equals(rest, "Computer", StringComparison.OrdinalIgnoreCase))
        {
            return names;
        }
        else if (rest.StartsWith("Computer\\", StringComparison.OrdinalIgnoreCase))
        {
            rest = rest["Computer\\".Length..];
        }

        names.AddRange(rest.Split('\\', StringSplitOptions.RemoveEmptyEntries));
        return names;
    }

    /// <summary>
    /// Sets the vertical scroll offset of the scroll viewer inside a pane. The offset is applied on a
    /// later dispatcher turn, after the pane has measured its new rows, so a long list is not clamped
    /// to the old height.
    /// </summary>
    private void RestoreScrollOffset(UIElement pane, double offset)
    {
        _ = Shell.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            ScrollViewer? viewer = FindDescendant<ScrollViewer>(pane);
            _ = viewer?.ChangeView(null, offset, null, disableAnimation: true);
        });
    }

    /// <summary>Reloads the list rows for the selected key. Stale results are dropped by generation.</summary>
    public async Task RefreshValuesAsync(string? selectDisplayName)
    {
        ValueLoad?.Cancel();
        ValueLoad = null;
        int generation = ++ValueGeneration;

        if (!TryGetSelectedKeyPath(out RegistryPath path))
        {
            Shell.ListContent.SetRows(Array.Empty<DetailsRow>());
            UpdateCommandStates();
            return;
        }

        IReadOnlyList<DetailsRow> rows;
        RegistryOperationException? unreadable = null;
        try
        {
            unreadable = await RunRegistryAsync(() => ProbeKeyForRead(path));
            rows = unreadable is null
                ? await ValueSource.LoadAsync(path, CancellationToken.None)
                : Array.Empty<DetailsRow>();
        }
        catch (Exception ex)
        {
            Log.Warn($"Registry values for '{path.ToDisplayString()}' could not be refreshed: {ex.Message}");
            rows = Array.Empty<DetailsRow>();
        }

        if (generation != ValueGeneration)
        {
            return;
        }

        Shell.ListContent.SetRows(rows);
        UpdateCommandStates();

        if (unreadable is not null)
        {
            await ShowUnreadableKeyAsync(path, unreadable);
            return;
        }

        if (!string.IsNullOrEmpty(selectDisplayName))
        {
            SelectListRow(rows, selectDisplayName);
        }
    }

    /// <summary>Loads the list rows for a tree node on the thread pool. Stale results are dropped by generation.</summary>
    public async Task LoadValuesAsync(TreeNode node, string? selectDisplayName = null)
    {
        ValueLoad?.Cancel();
        var tokenSource = new CancellationTokenSource();
        ValueLoad = tokenSource;
        int generation = ++ValueGeneration;
        IReadOnlyList<DetailsRow> rows;
        RegistryPath? unreadablePath = null;
        RegistryOperationException? unreadable = null;

        try
        {
            if (node.RegistryPath is not { Length: > 0 } displayPath)
            {
                // Computer is virtual and has no registry values.
                rows = Array.Empty<DetailsRow>();
            }
            else if (TryResolveRemoteNode(node, out RemoteRegistryConnection? remote, out RegistryPath? remotePath))
            {
                // Remote machine and root nodes have no values below the root itself. A remote
                // reader only accepts HKEY_LOCAL_MACHINE and HKEY_USERS paths.
                rows = remotePath.IsRoot
                    ? Array.Empty<DetailsRow>()
                    : await new RegistryValueSource(remote.CreateReader())
                        .LoadAsync(remotePath, tokenSource.Token);
            }
            else if (!RegistryPath.TryParse(displayPath, out RegistryPath? parsed) || parsed is null)
            {
                rows = Array.Empty<DetailsRow>();
            }
            else
            {
                // A key that cannot be opened shows the classic error and an empty list. The probe
                // runs first because the value source reports a failed read as an empty list.
                unreadable = await RunRegistryAsync(() => ProbeKeyForRead(parsed), tokenSource.Token);
                unreadablePath = parsed;
                rows = unreadable is null
                    ? await ValueSource.LoadAsync(parsed, tokenSource.Token)
                    : Array.Empty<DetailsRow>();
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Warn($"Registry values for '{node.Name}' could not be loaded: {ex.Message}");
            rows = Array.Empty<DetailsRow>();
        }
        finally
        {
            if (ReferenceEquals(ValueLoad, tokenSource))
            {
                ValueLoad = null;
            }

            tokenSource.Dispose();
        }

        if (generation != ValueGeneration)
        {
            return;
        }

        Shell.DispatcherQueue.TryEnqueue(() =>
        {
            if (generation == ValueGeneration)
            {
                PendingListView? pending = PendingList;
                PendingList = null;
                Shell.ListContent.SetRows(rows);
                if (unreadablePath is not null && unreadable is not null)
                {
                    _ = ShowUnreadableKeyAsync(unreadablePath, unreadable);
                    return;
                }

                if (pending is not null)
                {
                    SelectListRows(rows, pending.RowNames);
                    RestoreScrollOffset(Shell.ListContent, pending.ListOffset);
                }
                else if (!string.IsNullOrEmpty(selectDisplayName))
                {
                    SelectListRow(rows, selectDisplayName);
                }
            }
        });
    }

    /// <summary>Selects the tree node and makes it the current path. Focus is left to the caller.</summary>
    public void ApplyNavigation(TreeNode node)
    {
        ApplyingNavigation = true;
        try
        {
            Shell.TreeContent.SetRoots(TreeSource.Roots);
            Shell.TreeContent.SelectNode(node);
            Shell.CurrentPath = node.RegistryPath ?? "Computer";
        }
        finally
        {
            ApplyingNavigation = false;
        }

        Shell.TreeContent.FocusSelected();
    }

    /// <summary>
    /// Selects a row by display name through the row peer. The list control has no public select
    /// API, and the row peer's SelectionItem pattern is the supported path from another control.
    /// </summary>
    private void SelectListRow(IReadOnlyList<DetailsRow> rows, string displayName) =>
        SelectListRows(rows, new[] { displayName });

    /// <summary>
    /// Selects the rows with these display names, in the order given. The first row is selected alone
    /// and the rest are added to the selection. Names that are no longer in the list are skipped.
    /// </summary>
    private void SelectListRows(IReadOnlyList<DetailsRow> rows, IReadOnlyList<string> displayNames)
    {
        var indices = new List<int>();
        foreach (string displayName in displayNames)
        {
            for (var i = 0; i < rows.Count; i += 1)
            {
                if (string.Equals(rows[i].Name, displayName, StringComparison.Ordinal))
                {
                    if (!indices.Contains(i))
                    {
                        indices.Add(i);
                    }

                    break;
                }
            }
        }

        if (indices.Count == 0)
        {
            return;
        }

        _ = Shell.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                ItemsRepeater? repeater = FindDescendant<ItemsRepeater>(Shell.ListContent);
                if (repeater is null)
                {
                    return;
                }

                for (var position = 0; position < indices.Count; position += 1)
                {
                    int index = indices[position];
                    UIElement element = repeater.GetOrCreateElement(index);
                    if (element is not FrameworkElement frameworkElement)
                    {
                        continue;
                    }

                    AutomationPeer? peer = FrameworkElementAutomationPeer.CreatePeerForElement(frameworkElement);
                    if (peer is not null &&
                        string.Equals(peer.GetName(), rows[index].Name, StringComparison.Ordinal) &&
                        peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider provider)
                    {
                        if (position == 0)
                        {
                            provider.Select();
                        }
                        else
                        {
                            provider.AddToSelection();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Registry Editor could not select the refreshed rows: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Resolves a selected remote node to its connection and local-style registry path. Local
    /// nodes and the remote machine node fail, which keeps local commands disabled.
    /// </summary>
    private bool TryResolveRemoteNode(
        TreeNode node,
        out RemoteRegistryConnection connection,
        out RegistryPath path)
    {
        connection = null!;
        path = null!;

        if (node.RegistryPath is not { Length: > 0 } displayPath ||
            !RemoteRegistryTreeSource.TryParseDisplayPath(displayPath, out RegistryPath? parsed))
        {
            return false;
        }

        foreach (RemoteRegistryConnection candidate in RemoteConnections)
        {
            string prefix = RemoteRegistryTreeSource.DisplayPrefix(candidate.ComputerName);
            if (displayPath.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
            {
                connection = candidate;
                path = parsed;
                return true;
            }
        }

        return false;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index += 1)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed)
            {
                return typed;
            }

            T? nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private async Task RunCommandAsync(Func<Task> command)
    {
        try
        {
            await command();
        }
        catch (RegistryOperationException ex)
        {
            await ShowRegistryErrorAsync(ex);
        }
        catch (ArgumentException ex)
        {
            Log.Warn("Registry Editor command rejected an argument: " + ex.Message);
            await ShowErrorAsync(Messages.ForCommandFailure().ToErrorDialog());
        }
        catch (Exception ex)
        {
            Log.Exception("Registry Editor command failed.", ex);
            await ShowErrorAsync(Messages.ForCommandFailure().ToErrorDialog());
        }
    }

    private static bool IsDescendantOf(DependencyObject? element, DependencyObject ancestor)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, ancestor))
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }
}


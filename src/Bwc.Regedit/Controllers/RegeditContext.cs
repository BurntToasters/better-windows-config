using Bwc.Core;
using Bwc.Regedit.Commands;
using Bwc.Regedit.Dialogs;
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
internal sealed partial class RegeditContext
{
    /// <summary>Number of registry writes running. Nested writes count, so the gate holds until the last one ends.</summary>
    private int _writeDepth;

    /// <summary>Number of commands started through <see cref="StartCommand"/> that have not finished.</summary>
    private int _commandsRunning;

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

    /// <summary>The listing of a key that cannot be read: no rows and no names.</summary>
    private static readonly RegistryValueListing EmptyListing = new(
        Array.Empty<DetailsRow>(),
        new Dictionary<string, string>(StringComparer.Ordinal));

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
    /// True while a registry write runs on the thread pool. Every command dispatch checks it, and the
    /// owner window is disabled for the same time, so no input reaches the view during the write.
    /// </summary>
    public bool WriteInProgress => _writeDepth > 0;

    /// <summary>Raised on the UI thread when the last registry write ends. The window closes after a held close.</summary>
    public event EventHandler? WriteFinished;

    /// <summary>
    /// Raised on the UI thread when nothing is busy: no command, dialog, file dialog, or registry write runs.
    /// A .reg file forwarded by a second launch waits for this event.
    /// </summary>
    public event EventHandler? BecameIdle;

    /// <summary>True while a command, a modal dialog, a file dialog, or a registry write is running.</summary>
    public bool IsBusy => WriteInProgress || DialogOpen || FileDialogOpen || _commandsRunning > 0;

    private void NotifyIfIdle()
    {
        if (!IsBusy)
        {
            BecameIdle?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Runs one command and reports its failure. Callers do not await the result: the command runs
    /// on the UI thread and its errors are shown as dialogs. A command started while a registry write
    /// runs is ignored, so no command races the write.
    /// </summary>
    public void StartCommand(Func<Task> command)
    {
        if (WriteInProgress)
        {
            Log.Info("Registry Editor ignored a command while a registry write was running.");
            return;
        }

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
    /// Runs one registry write on the thread pool with the write gate held. The owner window is disabled
    /// until the write finishes, and every command that checks the gate is refused.
    /// </summary>
    public async Task RunRegistryWriteAsync(Action operation)
    {
        await RunRegistryWriteAsync(() =>
        {
            operation();
            return 0;
        });
    }

    public async Task<T> RunRegistryWriteAsync<T>(Func<T> operation)
    {
        bool wasOpen = DialogOpen;
        DialogOpen = true;
        BeginWrite();
        try
        {
            return await RunRegistryAsync(operation);
        }
        finally
        {
            EndWrite();
            DialogOpen = wasOpen;
            NotifyIfIdle();
        }
    }

    private void BeginWrite()
    {
        _writeDepth += 1;
        if (_writeDepth == 1)
        {
            SetOwnerEnabled(false);
        }
    }

    private void EndWrite()
    {
        _writeDepth -= 1;
        if (_writeDepth != 0)
        {
            return;
        }

        SetOwnerEnabled(true);
        WriteFinished?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Enables or disables the owner window. A disabled window takes no mouse or keyboard input, which
    /// is what keeps a long write from racing a command. Failures are logged; the write gate still holds.
    /// </summary>
    private void SetOwnerEnabled(bool enabled)
    {
        if (WindowHwnd == 0)
        {
            return;
        }

        try
        {
            _ = EnableWindow(WindowHwnd, enabled ? 1 : 0);
        }
        catch (Exception ex)
        {
            Log.Warn("Registry Editor could not change the owner window enabled state: " + ex.Message);
        }
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial int EnableWindow(nint hWnd, int bEnable);

    /// <summary>Puts keyboard focus back in a pane after a dialog closes. The root view sets it.</summary>
    public Action<Pane>? RestorePaneFocus { get; set; }

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

        Pane focusBefore = FocusedPane();
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
            RestoreFocusAfterDialog(focusBefore);
            NotifyIfIdle();
        }
    }

    /// <summary>
    /// The owner is active again when the dialog has closed. The pane that had focus before the dialog gets
    /// keyboard focus back, so the next key goes to the tree or list rather than to nothing.
    /// </summary>
    private void RestoreFocusAfterDialog(Pane pane)
    {
        try
        {
            RestorePaneFocus?.Invoke(pane);
        }
        catch (Exception ex)
        {
            Log.Warn("Registry Editor could not restore focus after a dialog: " + ex.Message);
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
    public Task ShowUnreadableKeyAsync(RegistryPath path, RegistryOperationException ex) =>
        ShowUnreadableKeyAsync(path.ToDisplayString(), ex);

    /// <summary>
    /// Shows the classic "cannot open key" error for a key whose rows or children could not be read. The
    /// display path may carry a remote computer prefix. The Win32 text is the details line.
    /// </summary>
    public Task ShowUnreadableKeyAsync(string displayPath, RegistryOperationException ex)
    {
        Log.Warn(
            $"Registry Editor could not open '{displayPath}' for reading: Win32 {ex.Win32Error} ({ex.Message}).");
        return ShowErrorAsync(Messages.ForUnreadableKey(displayPath, ex.Win32Error).ToErrorDialog());
    }

    /// <summary>
    /// Shows the classic "cannot open key" error for a failure that has no Win32 code. The message has no
    /// details line. The developer detail goes to the log only.
    /// </summary>
    private Task ShowUnreadableKeyWithoutDetailAsync(string displayPath, Exception exception)
    {
        Log.Warn($"Registry Editor could not read '{displayPath}': {exception.GetType().Name} ({exception.Message}).");
        return ShowErrorAsync(Messages.ForUnreadableKey(displayPath, -1).ToErrorDialog());
    }

    /// <summary>
    /// Shows the classic "cannot open key" error when a tree expand fails with a registry error. Any
    /// Win32 failure of the enumeration is shown, not only access denied. Other exceptions are logged only.
    /// The tree has already collapsed the node, and the next expand retries.
    /// </summary>
    public Task ShowExpandFailedAsync(TreeNode node, Exception exception)
    {
        if (exception is RegistryOperationException ex && node.RegistryPath is { Length: > 0 } displayPath)
        {
            Log.Warn($"Registry Editor could not expand '{displayPath}': Win32 {ex.Win32Error} ({ex.Message}).");
            return ShowErrorAsync(Messages.ForUnreadableKey(displayPath, ex.Win32Error).ToErrorDialog());
        }

        Log.Warn($"Registry Editor could not expand '{node.Name}': {exception.Message}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Opens the key with the given reader and returns the failure, or null when it opens. A failed value
    /// enumeration after a good open comes back in the listing's Failure, so the values are read once.
    /// </summary>
    public RegistryOperationException? ProbeKeyForRead(RegistryPath path, RegistryReader? reader = null)
    {
        try
        {
            RegistryReader source = reader ?? Store.Reader;
            _ = source.GetKeyInfo(path);
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
            lines.Add(Messages.Get("AddressBarPathLine") + attempted);
            lines.Add(string.Empty);
        }

        lines.Add(message);
        return ShowErrorAsync(
            ErrorDialog.ForMessage(Messages.Get("DialogErrorTitle"), string.Join(Environment.NewLine, lines)));
    }

    /// <summary>True when the virtual Computer node is selected, the only selection that can export every root.</summary>
    public bool IsComputerSelected =>
        Shell.TreeContent.SelectedNode is { Parent: null } node &&
        string.Equals(node.Name, "Computer", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// The roots Load Hive accepts: HKEY_LOCAL_MACHINE and HKEY_USERS themselves. The hive loads
    /// under the root that is selected.
    /// </summary>
    public static bool IsLoadHiveRoot(RegistryPath path) =>
        path.IsRoot && path.Root is RegistryRoot.LocalMachine or RegistryRoot.Users;

    /// <summary>
    /// Hive roots that Windows loads at boot. They are not loaded by a user, so Unload Hive stays
    /// disabled for them. The list is the rule for "loaded hive root" in the parity plan (OQ-21).
    /// </summary>
    private static readonly HashSet<string> BootHiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SAM",
        "SECURITY",
        "SOFTWARE",
        "SYSTEM",
        "HARDWARE",
        "COMPONENTS",
        ".DEFAULT",
        "S-1-5-18",
        "S-1-5-19",
        "S-1-5-20",
    };

    /// <summary>True for a user-loadable hive root: a direct child of HKLM or HKU that is not a boot hive.</summary>
    public static bool IsUnloadableHiveRoot(RegistryPath path) =>
        IsHiveRootChild(path) && !BootHiveNames.Contains(path.Components[0]);

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
        // Export on Computer is the classic whole-registry export; every other key is a branch.
        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 659, hasKey || IsComputerSelected);
        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 660, hasKey);
        // Values under a remote computer are read-only, so Display and Modify Binary Data stay disabled there.
        bool remoteValue = RegeditSelectionGuards.IsRemoteSelected(this);
        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 679, singleRealValue && !remoteValue);
        // Permissions and Unload Hive act on the key of the pane with focus. A value selected in the list
        // blocks them only when the list is the target.
        bool keyTarget = treeTarget || !hasRows;
        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 678, hasKey && keyTarget);
        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 675, hasKey);
        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 680, hasKey && IsLoadHiveRoot(key));
        RegeditMenu.SetCommandEnabled(
            Shell.MenuBar,
            681,
            hasKey && keyTarget && IsUnloadableHiveRoot(key));
        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 657, true);
        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 768, RemoteConnections.Count > 0);
        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 912, singleRow && !remoteValue);
        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 917, singleRealValue && !remoteValue);
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
            // A key that cannot be read again stays collapsed; the rest of the tree still rebuilds.
            try
            {
                TreeNode? node = await ResolveNodeAsync(source.Computer, path, expandAncestors: false);
                if (node is { HasChildren: true })
                {
                    _ = await node.LoadChildrenAsync(CancellationToken.None);
                    node.IsExpanded = true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"Registry Editor could not keep '{path}' expanded: {ex.Message}");
            }
        }

        TreeNode? target = null;
        if (!string.IsNullOrWhiteSpace(selectDisplayPath))
        {
            try
            {
                target = await ResolveNodeAsync(source.Computer, selectDisplayPath, expandAncestors: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"Registry Editor could not select '{selectDisplayPath}': {ex.Message}");
            }
        }

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

        // Names are not trimmed: a key may end in spaces, and the walk must match it exactly.
        string rest = displayPath;
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

        RegistryValueListing listing = EmptyListing;
        RegistryOperationException? unreadable = null;
        Exception? failure = null;
        try
        {
            unreadable = await RunRegistryAsync(() => ProbeKeyForRead(path));
            if (unreadable is null)
            {
                listing = await ValueSource.ReadAsync(path, CancellationToken.None);
                unreadable = listing.Failure;
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (generation != ValueGeneration)
        {
            return;
        }

        IReadOnlyList<DetailsRow> rows = listing.Rows;
        ValueSource.Apply(listing);
        Shell.ListContent.SetRows(rows);
        UpdateCommandStates();

        if (unreadable is not null)
        {
            await ShowUnreadableKeyAsync(path, unreadable);
            return;
        }

        if (failure is not null)
        {
            await ShowUnreadableKeyWithoutDetailAsync(path.ToDisplayString(), failure);
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
        RegistryValueListing listing = EmptyListing;
        string? unreadablePath = null;
        RegistryOperationException? unreadable = null;
        Exception? failure = null;

        try
        {
            if (node.RegistryPath is not { Length: > 0 } displayPath)
            {
                // Computer is virtual and has no registry values.
            }
            else if (TryResolveRemoteNode(node, out RemoteRegistryConnection? remote, out RegistryPath? remotePath))
            {
                // Remote machine and root nodes have no values below the root itself. A remote
                // reader only accepts HKEY_LOCAL_MACHINE and HKEY_USERS paths. The probe uses the remote
                // reader, and a failed remote enumeration comes back in the listing's Failure, so it is shown.
                if (!remotePath.IsRoot)
                {
                    RegistryPath remoteKey = remotePath;
                    RegistryReader reader = remote.CreateReader();
                    unreadablePath = displayPath;
                    unreadable = await RunRegistryAsync(() => ProbeKeyForRead(remoteKey, reader), tokenSource.Token);
                    if (unreadable is null)
                    {
                        listing = await new RegistryValueSource(reader).ReadAsync(remoteKey, tokenSource.Token);
                        unreadable = listing.Failure;
                    }
                }
            }
            else if (RegistryPath.TryParse(displayPath, out RegistryPath? parsed) && parsed is not null)
            {
                // A key that cannot be opened or enumerated shows the classic error and an empty list. The probe
                // catches a failed open; a failed enumeration comes back in the listing's Failure.
                unreadablePath = parsed.ToDisplayString();
                unreadable = await RunRegistryAsync(() => ProbeKeyForRead(parsed), tokenSource.Token);
                if (unreadable is null)
                {
                    listing = await ValueSource.ReadAsync(parsed, tokenSource.Token);
                    unreadable = listing.Failure;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            failure = ex;
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
                ValueSource.Apply(listing);
                Shell.ListContent.SetRows(listing.Rows);
                if (unreadablePath is not null && unreadable is not null)
                {
                    _ = ShowUnreadableKeyAsync(unreadablePath, unreadable);
                    return;
                }

                if (failure is not null)
                {
                    _ = ShowUnreadableKeyWithoutDetailAsync(unreadablePath ?? node.Name, failure);
                    return;
                }

                if (pending is not null)
                {
                    SelectListRows(listing.Rows, pending.RowNames);
                    RestoreScrollOffset(Shell.ListContent, pending.ListOffset);
                }
                else if (!string.IsNullOrEmpty(selectDisplayName))
                {
                    SelectListRow(listing.Rows, selectDisplayName);
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
        _commandsRunning += 1;
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
        finally
        {
            _commandsRunning -= 1;
            NotifyIfIdle();
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


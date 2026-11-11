using Bwc.Core;
using Bwc.Regedit.Commands;
using Bwc.Regedit.Registry;
using Bwc.Registry;
using Bwc.UI.Dialogs;
using Bwc.UI.List;
using Bwc.UI.Shell;
using Bwc.UI.Tree;
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
            return true;
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
        bool hasValue = rows.Count > 0;

        foreach (int id in new[] { 663, 664, 665, 674, 688, 676, 677 })
        {
            RegeditMenu.SetCommandEnabled(Shell.MenuBar, id, hasKey);
        }

        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 913, hasValue || mutableKey);
        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 659, hasKey);
        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 660, hasKey);
        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 679, hasValue && rows.Count == 1);
        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 678, hasKey && !hasValue);
        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 680, true);
        RegeditMenu.SetCommandEnabled(
            Shell.MenuBar,
            681,
            hasKey && !hasValue && IsHiveRootChild(key));
        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 657, true);
        RegeditMenu.SetCommandEnabled(Shell.MenuBar, 768, RemoteConnections.Count > 0);

        bool canRename = mutableKey;
        if (hasValue)
        {
            canRename = rows.Count == 1 &&
                !string.Equals(rows[0].Name, MessageCatalog.DefaultValueLabel, StringComparison.Ordinal);
        }

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

    /// <summary>Rebuilds the tree from the current sources and selects the path, or Computer when it is gone.</summary>
    public async Task RefreshTreeAsync(string? selectDisplayPath)
    {
        var source = new RegistryTreeSource(Store.Reader, RemoteConnections);
        TreeNode? target = null;

        if (!string.IsNullOrWhiteSpace(selectDisplayPath))
        {
            target = await source.FindPathAsync(selectDisplayPath);
        }

        TreeSource = source;
        Shell.TreeContent.SetRoots(source.Roots);
        Shell.TreeContent.SelectNode(target ?? source.Computer);
        UpdateCommandStates();
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
                Shell.ListContent.SetRows(rows);
                if (unreadablePath is not null && unreadable is not null)
                {
                    _ = ShowUnreadableKeyAsync(unreadablePath, unreadable);
                    return;
                }

                if (!string.IsNullOrEmpty(selectDisplayName))
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
    private void SelectListRow(IReadOnlyList<DetailsRow> rows, string displayName)
    {
        int index = -1;
        for (var i = 0; i < rows.Count; i += 1)
        {
            if (string.Equals(rows[i].Name, displayName, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
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

                UIElement element = repeater.GetOrCreateElement(index);
                if (element is not FrameworkElement frameworkElement)
                {
                    return;
                }

                AutomationPeer? peer = FrameworkElementAutomationPeer.CreatePeerForElement(frameworkElement);
                if (peer is not null &&
                    string.Equals(peer.GetName(), displayName, StringComparison.Ordinal) &&
                    peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider provider)
                {
                    provider.Select();
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Registry Editor could not select the refreshed row '{displayName}': {ex.Message}");
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


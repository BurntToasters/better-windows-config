using System.Globalization;
using Bwc.Core;
using Bwc.Regedit.Dialogs;
using Bwc.Regedit.Registry;
using Bwc.Registry;
using Bwc.Registry.Privileges;
using Bwc.UI.Dialogs;
using Bwc.UI.Tree;

namespace Bwc.Regedit.Controllers;

/// <summary>Load and Unload Hive, Connect and Disconnect Network Registry, and Permissions.</summary>
internal sealed class HiveRemoteCommands
{
#if BWC_E2E
    /// <summary>E2E only: the computer File > Connect uses instead of the picker. Shipping never reads it.</summary>
    private const string ConnectTargetVariable = "BWC_E2E_CONNECT_TARGET";
#endif

    private readonly RegeditContext _context;
    private bool _remoteConnectionsDisposed;

    public HiveRemoteCommands(RegeditContext context)
    {
        _context = context;
    }

    private string Get(string key) => _context.Messages.Get(key);

    public void LoadHive() => _context.StartCommand(LoadHiveAsync);

    public void UnloadHive() => _context.StartCommand(UnloadHiveAsync);

    public void ConnectNetworkRegistry() => _context.StartCommand(ConnectNetworkRegistryAsync);

    public void DisconnectNetworkRegistry() => _context.StartCommand(DisconnectNetworkRegistryAsync);

    /// <summary>
    /// Opens the standard aclui security editor for the selected registry key. The target is the pane
    /// with focus: a value selected in the list is refused, and the tree's selected key is used otherwise.
    /// </summary>
    public void Permissions()
    {
        bool listTarget = _context.FocusedPane() == RegeditContext.Pane.List;
        _context.StartCommand(() => PermissionsAsync(_context.Shell.TreeContent.SelectedNode, listTarget));
    }

    /// <summary>Opens the security editor for the key of a tree node. The tree context menu uses this.</summary>
    public void PermissionsForNode(TreeNode node) =>
        _context.StartCommand(() => PermissionsAsync(node, targetIsList: false));

    /// <summary>Closes every remote connection. Runs once, when the view unloads.</summary>
    public void DisposeRemoteConnections()
    {
        if (_remoteConnectionsDisposed)
        {
            return;
        }

        _remoteConnectionsDisposed = true;
        foreach (RemoteRegistryConnection connection in _context.RemoteConnections)
        {
            connection.Dispose();
        }

        _context.RemoteConnections.Clear();
        _context.ForgetAllRemoteStores();
    }

    private async Task PermissionsAsync(TreeNode? node, bool targetIsList)
    {
        if (_context.DialogOpen || _context.FileDialogOpen || _context.WindowHwnd == 0)
        {
            Log.Warn(
                "Registry Editor ignored Permissions because another modal is open or the window handle is not attached.");
            return;
        }

        // Security belongs to the key. A value selected in the list has no security of its own, so the
        // list target is refused while a value is selected. The tree target never depends on the list.
        if (targetIsList && _context.Shell.ListContent.SelectedRows.Count > 0)
        {
            await _context.ShowMessageAsync(Get("DialogNoSelection"));
            return;
        }

        if (!_context.TryGetLocationForNode(node, out KeyLocation location))
        {
            await _context.ShowMessageAsync(Get("DialogNoSelection"));
            return;
        }

        int result;
        SecurityWriteFailure? writeFailure;
        _context.DialogOpen = true;
        try
        {
            result = RegistryPermissionsDialog.Show(
                _context.WindowHwnd,
                location.Reader,
                location.Store,
                location.Path,
                location.Connection?.ComputerName,
                out writeFailure);
        }
        finally
        {
            _context.DialogOpen = false;
        }

        if (writeFailure is { } failure)
        {
            Log.Warn($"Registry Editor could not save the permissions for '{location.DisplayPath}': Win32 {failure.Win32Error}.");
            await _context.ShowErrorAsync(
                new ClassicMessage(Get("DialogErrorTitle"), Get(RegistryPermissionsDialog.SaveMessageKey(failure)))
                    .ToErrorDialog());
            return;
        }

        if (result >= 0)
        {
            return;
        }

        // The dialog reports an HRESULT. The Win32 code is its low word when the facility is Win32.
        uint hresult = (uint)result;
        int win32 = (hresult & 0xFFFF0000u) == 0x80070000u ? (int)(hresult & 0xFFFFu) : -1;
        Log.Warn($"Registry Editor could not show the permissions for '{location.DisplayPath}': HRESULT 0x{hresult:X8}.");
        await _context.ShowErrorAsync(_context.Messages.ForPermissions(win32).ToErrorDialog());
    }

    private async Task LoadHiveAsync()
    {
        if (_context.DialogOpen || _context.FileDialogOpen || _context.WindowHwnd == 0)
        {
            Log.Warn(
                "Registry Editor ignored Load Hive because another modal is open or the window handle is not attached.");
            return;
        }

        if (RegeditSelectionGuards.IsRemoteSelected(_context))
        {
            // A hive loads only into the local registry, so a remote selection is refused before any input is asked for.
            await _context.ShowMessageAsync(Get("DialogRemoteReadOnly"));
            return;
        }

        // Load Hive is enabled only at the HKEY_LOCAL_MACHINE or HKEY_USERS root. The hive loads under that root.
        if (!_context.TryGetSelectedKeyPath(out RegistryPath root) || !RegeditContext.IsLoadHiveRoot(root))
        {
            await _context.ShowMessageAsync(Get("DialogNoSelection"));
            return;
        }

        // The privilege is checked before any input is asked for. A process without it cannot load a hive, so
        // the file dialog and the name prompt would only lead to the same refusal.
        if (!HivePrivilegesHeld())
        {
            Log.Info($"Registry Editor refused Load Hive under '{root.ToDisplayString()}' because the privilege is not held.");
            await _context.ShowErrorAsync(new ClassicMessage(
                Get("DialogLoadHiveTitle"),
                string.Format(CultureInfo.CurrentCulture, Get("MsgLoadHivePrivileges"), root.ToDisplayString())).ToErrorDialog());
            return;
        }

        string? filePath = RegeditFileDialogs.ShowLoadHive(_context);
        if (string.IsNullOrEmpty(filePath))
        {
            return;
        }

        var dialog = new TextPromptDialog(
            Get("DialogLoadHiveTitle"),
            Get("DialogLoadHiveNameLabel"),
            string.Empty,
            name => ValidateHiveKeyName(root, name));

        if (!await _context.ShowDialogAsync(dialog))
        {
            return;
        }

        RegistryPath target = root.Append(dialog.Value.Trim());

        try
        {
            await _context.RunRegistryWriteAsync(() => _context.Store.LoadHive(target, filePath));
        }
        catch (RegistryOperationException ex)
        {
            // The subject is the file the user chose, so a bad or missing hive file names that file.
            await _context.ShowRegistryErrorAsync(ex, RegistryAction.LoadHive, filePath);
            return;
        }

        await _context.RefreshTreeAsync(target.ToDisplayString());
        _context.FocusTreeAfterDialog();
    }

    /// <summary>
    /// True when the process holds both hive privileges. A privilege that is present but disabled counts as held,
    /// because enabling it is the hive call's job. Nothing is enabled here.
    /// </summary>
    private static bool HivePrivilegesHeld() =>
        PrivilegeScope.IsAvailable(PrivilegeScope.BackupPrivilege) &&
        PrivilegeScope.IsAvailable(PrivilegeScope.RestorePrivilege);

    private string? ValidateHiveKeyName(RegistryPath root, string name)
    {
        string candidate = name.Trim();
        RegistryPath target;
        try
        {
            target = root.Append(candidate);
        }
        catch (ArgumentException)
        {
            return Get("DialogLoadHiveNameInvalid");
        }

        try
        {
            return _context.Store.Reader.KeyExists(target) ? Get("DialogLoadHiveNameExists") : null;
        }
        catch (RegistryOperationException ex)
        {
            Log.Warn($"Registry Editor could not check a hive name: {ex.Operation} for {ex.Path.ToDisplayString()}, Win32 {ex.Win32Error}: {ex.Message}");
            return _context.Messages.ForRegistryError(RegistryAction.LoadHive, ex, target.ToDisplayString()).Text;
        }
    }

    private async Task UnloadHiveAsync()
    {
        if (_context.DialogOpen || _context.FileDialogOpen || _context.WindowHwnd == 0)
        {
            Log.Warn(
                "Registry Editor ignored Unload Hive because another modal is open or the window handle is not attached.");
            return;
        }

        // The target is the key of the focused pane. A value selected in the list is refused only when
        // the list has focus, so the tree's selected hive is unloaded with a value still selected.
        bool listTarget = _context.FocusedPane() == RegeditContext.Pane.List;
        if ((listTarget && _context.Shell.ListContent.SelectedRows.Count > 0) ||
            !TryGetNodeKeyPath(_context.Shell.TreeContent.SelectedNode, out RegistryPath path) ||
            !RegeditContext.IsUnloadableHiveRoot(path))
        {
            await _context.ShowMessageAsync(Get("DialogNoSelection"));
            return;
        }

        var dialog = new ConfirmDialog(
            Get("MsgCaptionConfirmUnloadHive"),
            Get("MsgUnloadHiveConfirm"),
            Get("DialogYes"),
            Get("DialogNo"));

        if (!await _context.ShowDialogAsync(dialog))
        {
            return;
        }

        try
        {
            await _context.RunRegistryWriteAsync(() => _context.Store.UnloadHive(path));
        }
        catch (RegistryOperationException ex)
        {
            await _context.ShowRegistryErrorAsync(ex, RegistryAction.UnloadHive, path.ToDisplayString());
            return;
        }

        await _context.RefreshTreeAsync(path.Parent!.ToDisplayString());
        _context.FocusTreeAfterDialog();
    }

    private async Task ConnectNetworkRegistryAsync()
    {
        if (_context.DialogOpen || _context.FileDialogOpen || _context.WindowHwnd == 0)
        {
            Log.Warn(
                "Registry Editor ignored Connect Network Registry because another modal is open or the window handle is not attached.");
            return;
        }

        string? picked = await ChooseComputerAsync();
        if (picked is null)
        {
            return;
        }

        string computerName = picked;
        if (_context.RemoteConnections.Any(existing =>
                string.Equals(existing.ComputerName, computerName, StringComparison.OrdinalIgnoreCase)))
        {
            // The picker can return a computer that is already connected. That computer keeps its one connection.
            await _context.ShowMessageAsync(Get("DialogConnectTitle"), Get("DialogConnectDuplicate"));
            return;
        }

        RemoteRegistryConnection connection;
        try
        {
            // The connect runs under the write gate, so a second Connect cannot pass the duplicate check
            // while this one is still connecting.
            connection = await _context.RunRegistryWriteAsync(() => RemoteRegistryConnection.Connect(computerName));
        }
        catch (RemoteRegistryException ex)
        {
            Log.Warn($"Registry Editor could not connect to '{computerName}': {ex.Reason}, Win32 {ex.Win32Error}: {ex.Message}");
            await _context.ShowErrorAsync(_context.Messages.ForConnect(ex.Reason, ex.Win32Error, computerName).ToErrorDialog());
            return;
        }
        catch (ArgumentException ex)
        {
            Log.Warn($"Registry Editor could not connect to '{computerName}': {ex.Message}");
            await _context.ShowErrorAsync(_context.Messages.ForConnect(RemoteRegistryFailureReason.ConnectFailed, 0, computerName).ToErrorDialog());
            return;
        }

        // Checked again after the connect: a connection with the same name that was added meanwhile would
        // leave a second tree node and a leaked connection. The rejected one is disposed.
        if (_context.RemoteConnections.Any(existing =>
                string.Equals(existing.ComputerName, connection.ComputerName, StringComparison.OrdinalIgnoreCase)))
        {
            connection.Dispose();
            Log.Warn($"Registry Editor dropped a second connection to '{connection.ComputerName}'.");
            await _context.ShowMessageAsync(Get("DialogConnectTitle"), Get("DialogConnectDuplicate"));
            return;
        }

        _context.RemoteConnections.Add(connection);
        _context.TreeSource.AddRemoteComputer(connection);
        _context.Shell.TreeContent.SetRoots(_context.TreeSource.Roots);

        TreeNode? node = FindChild(_context.TreeSource.Computer, @"\\" + connection.ComputerName);
        if (node is not null)
        {
            _context.Shell.TreeContent.SelectNode(node);
            node.IsExpanded = true;

            // The remote root list is built in memory, so this does not touch the network. Loading
            // it before the second SetRoots makes HKEY_LOCAL_MACHINE and HKEY_USERS visible while
            // the new computer node is selected and expanded.
            await node.LoadChildrenAsync(CancellationToken.None);
            _context.Shell.TreeContent.SetRoots(_context.TreeSource.Roots);
            _context.Shell.TreeContent.SelectNode(node);
            _context.Shell.TreeContent.FocusSelected();
        }

        _context.UpdateCommandStates();
        Log.Info($"Registry Editor connected to the remote computer '{connection.ComputerName}'.");
    }

    /// <summary>
    /// The computer to connect to, or null when the user cancelled. The picker is the File > Connect choice. The text
    /// prompt is the fallback when the picker cannot run. E2E builds may name the computer in BWC_E2E_CONNECT_TARGET.
    /// </summary>
    private async Task<string?> ChooseComputerAsync()
    {
#if BWC_E2E
        string? target = Environment.GetEnvironmentVariable(ConnectTargetVariable);
        if (!string.IsNullOrWhiteSpace(target))
        {
            Log.Info("Registry Editor connects to the E2E target from BWC_E2E_CONNECT_TARGET, without the picker.");
            return target.Trim();
        }
#endif

        ComputerPicker.Outcome outcome = ShowPicker(out string? picked);
        if (outcome == ComputerPicker.Outcome.Selected)
        {
            return picked;
        }

        if (outcome == ComputerPicker.Outcome.Cancelled)
        {
            return null;
        }

        var prompt = new TextPromptDialog(
            Get("DialogConnectTitle"),
            Get("DialogConnectLabel"),
            string.Empty,
            ValidateRemoteComputerName);

        if (!await _context.ShowDialogAsync(prompt))
        {
            return null;
        }

        return prompt.Value.Trim();
    }

    /// <summary>Runs the picker as a modal of the main window. The modal flag is held while it is open.</summary>
    private ComputerPicker.Outcome ShowPicker(out string? computerName)
    {
        _context.DialogOpen = true;
        try
        {
            return ComputerPicker.Show(_context.WindowHwnd, out computerName);
        }
        finally
        {
            _context.DialogOpen = false;
        }
    }

    private string? ValidateRemoteComputerName(string name)
    {
        string candidate = name.Trim();
        if (candidate.Length == 0)
        {
            return Get("DialogConnectEmpty");
        }

        if (candidate.Length > 255 ||
            candidate.Contains('\0') ||
            candidate.Contains('\\') ||
            candidate.Contains('/'))
        {
            return Get("DialogConnectInvalid");
        }

        foreach (RemoteRegistryConnection connection in _context.RemoteConnections)
        {
            if (string.Equals(connection.ComputerName, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return Get("DialogConnectDuplicate");
            }
        }

        return null;
    }

    private async Task DisconnectNetworkRegistryAsync()
    {
        if (_context.DialogOpen || _context.FileDialogOpen || _context.WindowHwnd == 0)
        {
            Log.Warn(
                "Registry Editor ignored Disconnect Network Registry because another modal is open or the window handle is not attached.");
            return;
        }

        if (_context.RemoteConnections.Count == 0)
        {
            await _context.ShowMessageAsync(Get("DialogDisconnectEmpty"));
            return;
        }

        string[] names = _context.RemoteConnections
            .Select(connection => connection.ComputerName)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var dialog = new RemoteComputerListDialog(
            Get("DialogDisconnectTitle"),
            Get("DialogDisconnectComputerLabel"),
            names,
            Get("DialogDisconnectNoSelection"),
            Get("DialogOk"),
            Get("DialogCancel"));

        if (!await _context.ShowDialogAsync(dialog) || dialog.SelectedNames.Count == 0)
        {
            return;
        }

        // Every selected computer is disconnected, in list order. A name that is no longer connected is skipped.
        foreach (string selected in dialog.SelectedNames)
        {
            RemoteRegistryConnection? connection = _context.RemoteConnections.FirstOrDefault(candidate =>
                string.Equals(candidate.ComputerName, selected, StringComparison.OrdinalIgnoreCase));

            if (connection is null)
            {
                Log.Warn($"Registry Editor could not find the selected remote computer '{selected}'.");
                continue;
            }

            await CloseConnectionAsync(connection);
        }
    }

    /// <summary>Disconnect on a connected computer node (RT_MENU 107, id 768). Closes that computer only.</summary>
    public void DisconnectComputer(TreeNode node) => _context.StartCommand(() => DisconnectComputerAsync(node));

    private async Task DisconnectComputerAsync(TreeNode node)
    {
        RemoteRegistryConnection? connection = _context.RemoteConnections.FirstOrDefault(candidate =>
            string.Equals(
                RemoteRegistryTreeSource.DisplayPrefix(candidate.ComputerName),
                node.RegistryPath,
                StringComparison.OrdinalIgnoreCase));

        if (connection is null)
        {
            Log.Warn($"Registry Editor could not find the connection for the tree node '{node.Name}'.");
            return;
        }

        await CloseConnectionAsync(connection);
    }

    /// <summary>
    /// Removes a connected computer from the tree, forgets its store, and disposes its connection. The
    /// selection moves to the virtual Computer node. Writes nothing to the registry.
    /// </summary>
    private async Task CloseConnectionAsync(RemoteRegistryConnection connection)
    {
        _context.RemoteConnections.Remove(connection);
        _context.ForgetRemoteStore(connection);
        RemoveRemoteTreeNode();
        connection.Dispose();
        await _context.RefreshTreeAsync("Computer");
        _context.Shell.TreeContent.FocusSelected();
        _context.UpdateCommandStates();
        Log.Info($"Registry Editor disconnected the remote computer '{connection.ComputerName}'.");
    }

    /// <summary>
    /// Drops a disconnected remote node before its connection is closed. TreeNode has no removal
    /// API, so the source is rebuilt from the remaining connections; RefreshTreeAsync rebuilds it
    /// once more and selects the virtual Computer node.
    /// </summary>
    private void RemoveRemoteTreeNode()
    {
        _context.TreeSource = new RegistryTreeSource(_context.Store.Reader, _context.RemoteConnections);
        _context.Shell.TreeContent.SetRoots(_context.TreeSource.Roots);
    }

    /// <summary>The local registry path of a tree node. False for Computer, a remote node, or an invalid path.</summary>
    private static bool TryGetNodeKeyPath(TreeNode? node, out RegistryPath path)
    {
        if (node?.RegistryPath is { Length: > 0 } displayPath &&
            RegistryTreeSource.TryParseNodePath(displayPath, out RegistryPath? parsed) &&
            parsed is not null)
        {
            path = parsed;
            return true;
        }

        path = null!;
        return false;
    }

    private static TreeNode? FindChild(TreeNode parent, string name)
    {
        foreach (TreeNode child in parent.Children)
        {
            if (string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }
        }

        return null;
    }
}

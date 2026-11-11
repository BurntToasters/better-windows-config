using Bwc.Core;
using Bwc.Regedit.Dialogs;
using Bwc.Regedit.Registry;
using Bwc.Registry;
using Bwc.UI.Dialogs;
using Bwc.UI.Tree;

namespace Bwc.Regedit.Controllers;

/// <summary>Load and Unload Hive, Connect and Disconnect Network Registry, and Permissions.</summary>
internal sealed class HiveRemoteCommands
{
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

    /// <summary>Opens the standard aclui security editor for the selected registry key.</summary>
    public void Permissions() => _context.StartCommand(PermissionsAsync);

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
    }

    private async Task PermissionsAsync()
    {
        if (_context.DialogOpen || _context.FileDialogOpen || _context.WindowHwnd == 0)
        {
            Log.Warn(
                "Registry Editor ignored Permissions because another modal is open or the window handle is not attached.");
            return;
        }

        if (!_context.TryGetSelectedKeyPath(out RegistryPath path))
        {
            await _context.ShowMessageAsync(Get("DialogNoSelection"));
            return;
        }

        if (_context.Shell.ListContent.SelectedRows.Count > 0)
        {
            // The command is disabled for value selections. Keep the keyboard path consistent.
            await _context.ShowMessageAsync(Get("DialogNoSelection"));
            return;
        }

        int result;
        _context.DialogOpen = true;
        try
        {
            result = RegistryPermissionsDialog.Show(_context.WindowHwnd, _context.Store.Reader, _context.Store, path);
        }
        finally
        {
            _context.DialogOpen = false;
        }

        if (result >= 0)
        {
            return;
        }

        // The dialog reports an HRESULT. The Win32 code is its low word when the facility is Win32.
        uint hresult = (uint)result;
        int win32 = (hresult & 0xFFFF0000u) == 0x80070000u ? (int)(hresult & 0xFFFFu) : -1;
        Log.Warn($"Registry Editor could not show the permissions for '{path.ToDisplayString()}': HRESULT 0x{hresult:X8}.");
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

        string? filePath = _context.ShowFileDialog(save: false, defaultFileName: null);
        if (string.IsNullOrEmpty(filePath))
        {
            return;
        }

        var dialog = new TextPromptDialog(
            Get("DialogLoadHiveTitle"),
            Get("DialogLoadHiveNameLabel"),
            string.Empty,
            ValidateHiveKeyName);

        if (!await _context.ShowDialogAsync(dialog))
        {
            return;
        }

        RegistryPath target = RegistryPath.Parse(@"HKLM\" + dialog.Value.Trim());

        try
        {
            await _context.RunRegistryAsync(() => _context.Store.LoadHive(target, filePath));
        }
        catch (RegistryOperationException ex)
        {
            await _context.ShowRegistryErrorAsync(ex, RegistryAction.LoadHive, target.ToDisplayString());
            return;
        }

        await _context.RefreshTreeAsync(target.ToDisplayString());
        _context.FocusTreeAfterDialog();
    }

    private string? ValidateHiveKeyName(string name)
    {
        string candidate = name.Trim();
        if (!RegistryPath.TryParse(@"HKLM\" + candidate, out RegistryPath? target) ||
            target is null ||
            target.Components.Count != 1)
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

        if (!_context.TryGetSelectedKeyPath(out RegistryPath path) ||
            _context.Shell.ListContent.SelectedRows.Count > 0 ||
            !RegeditContext.IsHiveRootChild(path))
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
            await _context.RunRegistryAsync(() => _context.Store.UnloadHive(path));
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

        var dialog = new TextPromptDialog(
            Get("DialogConnectTitle"),
            Get("DialogConnectLabel"),
            string.Empty,
            ValidateRemoteComputerName);

        if (!await _context.ShowDialogAsync(dialog))
        {
            return;
        }

        string computerName = dialog.Value.Trim();

        RemoteRegistryConnection connection;
        try
        {
            connection = await _context.RunRegistryAsync(() => RemoteRegistryConnection.Connect(computerName));
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

    private string? ValidateRemoteComputerName(string name)
    {
        string candidate = name.Trim();
        if (candidate.Length == 0)
        {
            return Get("DialogConnectEmpty");
        }

        if (RemoteRegistryConnection.IsLocalComputer(candidate))
        {
            return Get("DialogConnectLocal");
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
            Get("DialogDisconnectLabel"),
            names,
            Get("DialogDisconnectNoSelection"),
            Get("DialogOk"),
            Get("DialogCancel"));

        if (!await _context.ShowDialogAsync(dialog) || dialog.SelectedName is not { Length: > 0 } selected)
        {
            return;
        }

        RemoteRegistryConnection? connection = null;
        foreach (RemoteRegistryConnection candidate in _context.RemoteConnections)
        {
            if (string.Equals(candidate.ComputerName, selected, StringComparison.OrdinalIgnoreCase))
            {
                connection = candidate;
                break;
            }
        }

        if (connection is null)
        {
            Log.Warn($"Registry Editor could not find the selected remote computer '{selected}'.");
            return;
        }

        _context.RemoteConnections.Remove(connection);
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

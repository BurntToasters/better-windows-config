using Bwc.Core;
using Bwc.Regedit.Commands;
using Bwc.Regedit.Dialogs;
using Bwc.Registry;
using Bwc.UI.List;
using Bwc.UI.Menu;
using Bwc.UI.Tree;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace Bwc.Regedit.Controllers;

/// <summary>The tree and list context menus. Each menu is built from the current selection when it opens.</summary>
internal sealed class ContextMenuController
{
    private readonly RegeditContext _context;
    private readonly IRegeditEditCommands _editCommands;
    private readonly ImportExportCommands _importExport;
    private readonly HiveRemoteCommands _hiveRemote;
    private readonly KeyCommands _keys;
    private readonly ValueCommands _values;
    private readonly Action _goToRoot;
    private MenuFlyout? _contextFlyout;

    public ContextMenuController(
        RegeditContext context,
        IRegeditEditCommands editCommands,
        ImportExportCommands importExport,
        HiveRemoteCommands hiveRemote,
        KeyCommands keys,
        ValueCommands values,
        Action goToRoot)
    {
        _context = context;
        _editCommands = editCommands;
        _importExport = importExport;
        _hiveRemote = hiveRemote;
        _keys = keys;
        _values = values;
        _goToRoot = goToRoot;
    }

    private string Get(string key) => _context.Messages.Get(key);

    /// <summary>
    /// The root that Go to HKEY_LOCAL_MACHINE (command 918, Ctrl+T) navigates to. Under HKEY_LOCAL_MACHINE
    /// the same command goes to HKEY_CURRENT_USER, and the menu text changes to match.
    /// </summary>
    public static string GoToRootPath(RegeditContext context) =>
        IsUnderLocalMachine(context) ? "Computer\\HKEY_CURRENT_USER" : "Computer\\HKEY_LOCAL_MACHINE";

    private static bool IsUnderLocalMachine(RegeditContext context) =>
        context.TryGetSelectedKeyPath(out RegistryPath key) && key.Root == RegistryRoot.LocalMachine;

    public void ShowTreeContextMenu(TreeNodeContextRequestedEventArgs e)
    {
        if (_context.DialogOpen || _context.FileDialogOpen)
        {
            Log.Warn("Registry Editor ignored a tree context menu request because another modal is open.");
            return;
        }

        // A remote computer has the classic RT_MENU 107 menu. It is read-only, so none of its items writes.
        MenuFlyout flyout = IsRemoteNode(e.Node)
            ? CreateRemoteTreeContextMenu(e.Node)
            : CreateTreeContextMenu(e.Node);
        ShowContextFlyout(flyout, e.Element, e.Point);
    }

    public void ShowListContextMenu(DetailsRowContextRequestedEventArgs e)
    {
        if (_context.DialogOpen || _context.FileDialogOpen)
        {
            Log.Warn("Registry Editor ignored a list context menu request because another modal is open.");
            return;
        }

        MenuFlyout flyout = e.Row is null
            ? CreateBackgroundContextMenu()
            : CreateValueContextMenu(e.Row);
        ShowContextFlyout(flyout, e.Element, e.Point);
    }

    private static bool IsRemoteNode(TreeNode node) =>
        node.RegistryPath is { Length: > 0 } displayPath && displayPath.StartsWith(@"\\", StringComparison.Ordinal);

    private MenuFlyout CreateTreeContextMenu(TreeNode node)
    {
        bool hasKey = _context.TryGetSelectedKeyPath(out RegistryPath key);
        bool mutableKey = hasKey && !key.IsRoot;
        string goToResource = IsUnderLocalMachine(_context) ? "ContextGoToHkcu" : "ContextGoToHklm";

        // Expand (915) becomes Collapse (RT_STRING 25) while the node is expanded, and then collapses it.
        var items = new List<ClassicCommandItem>
        {
            node.IsExpanded
                ? MenuCommand(915, "ContextCollapse", node.HasChildren, _context.Shell.TreeContent.CollapseSelected)
                : MenuCommand(915, "ContextExpand", node.HasChildren, _context.Shell.TreeContent.ExpandSelected),
            MenuCommand(516, "MenuEditNew", hasKey, children: BuildNewSubmenu(hasKey)),
            MenuCommand(662, "ContextFind", isEnabled: true, invoke: _editCommands.Find),
            ClassicCommandItem.Separator,
            MenuCommand(913, "ContextDelete", mutableKey, _keys.DeleteKey),
            MenuCommand(914, "ContextRename", mutableKey, BeginTreeRenameFromMenu),
            ClassicCommandItem.Separator,
            // Export at Computer is the whole-registry export, as on the main menu.
            MenuCommand(659, "ContextExport", hasKey || _context.IsComputerSelected, _importExport.Export),
            MenuCommand(678, "ContextPermissions", hasKey, () => _hiveRemote.PermissionsForNode(node)),
            ClassicCommandItem.Separator,
            MenuCommand(675, "ContextCopyKeyName", hasKey, _keys.CopySelectedKeyName),
            MenuCommand(918, goToResource, isEnabled: true, invoke: _goToRoot),
        };

        return CreateContextFlyout(items);
    }

    /// <summary>
    /// The classic RT_MENU 107 menu for a remote computer: Expand (or Collapse), Export, and Disconnect.
    /// Export is disabled because there is no remote export in this version. Disconnect opens the
    /// Disconnect Network Registry list.
    /// </summary>
    private MenuFlyout CreateRemoteTreeContextMenu(TreeNode node)
    {
        var items = new List<ClassicCommandItem>
        {
            node.IsExpanded
                ? MenuCommand(915, "ContextCollapse", node.HasChildren, _context.Shell.TreeContent.CollapseSelected)
                : MenuCommand(915, "ContextExpand", node.HasChildren, _context.Shell.TreeContent.ExpandSelected),
            ClassicCommandItem.Separator,
            MenuCommand(659, "ContextExport", isEnabled: false),
            ClassicCommandItem.Separator,
            MenuCommand(768, "ContextDisconnect", _context.RemoteConnections.Count > 0, _hiveRemote.DisconnectNetworkRegistry),
        };

        return CreateContextFlyout(items);
    }

    private MenuFlyout CreateValueContextMenu(DetailsRow row)
    {
        IReadOnlyList<DetailsRow> rows = _context.Shell.ListContent.SelectedRows;

        // Values under a remote computer are read-only, so every value command is disabled there.
        bool writable = !RegeditSelectionGuards.IsRemoteSelected(_context);
        bool single = writable && rows.Count == 1;
        bool canRename = single && row.Renameable;

        // Modify is enabled for every single value. Binary and unknown types open the hex editor from it.
        // Modify Binary Data and Delete need a value behind the row, so the synthetic (Default) row
        // does not enable them.
        int realValues = writable ? _context.SelectedRealValueCount() : 0;
        var items = new List<ClassicCommandItem>
        {
            MenuCommand(912, "ContextModify", single, _values.ModifySelectedValue),
            MenuCommand(917, "ContextModifyBinary", single && realValues == 1, _values.ModifyBinaryData),
            ClassicCommandItem.Separator,
            // The value menu deletes values only. It does not go through the focused pane, which can be the
            // tree when the list did not take focus, and would delete the key.
            MenuCommand(913, "ContextDelete", realValues > 0, _values.DeleteValues),
            MenuCommand(914, "ContextRename", canRename, BeginListRenameFromMenu),
        };

        return CreateContextFlyout(items);
    }

    private MenuFlyout CreateBackgroundContextMenu()
    {
        bool hasKey = _context.TryGetSelectedKeyPath(out _) && !RegeditSelectionGuards.IsRemoteSelected(_context);
        var items = new List<ClassicCommandItem>
        {
            MenuCommand(516, "MenuEditNew", hasKey, children: BuildNewSubmenu(hasKey)),
        };

        return CreateContextFlyout(items);
    }

    private IReadOnlyList<ClassicCommandItem> BuildNewSubmenu(bool isEnabled)
    {
        return new ClassicCommandItem[]
        {
            MenuCommand(663, "MenuEditNewKey", isEnabled, _editCommands.NewKey),
            ClassicCommandItem.Separator,
            MenuCommand(664, "MenuEditNewStringValue", isEnabled, _editCommands.NewStringValue),
            MenuCommand(665, "MenuEditNewBinaryValue", isEnabled, _editCommands.NewBinaryValue),
            MenuCommand(674, "MenuEditNewDwordValue", isEnabled, _editCommands.NewDwordValue),
            MenuCommand(688, "MenuEditNewQwordValue", isEnabled, _editCommands.NewQwordValue),
            MenuCommand(676, "MenuEditNewMultiStringValue", isEnabled, _editCommands.NewMultiStringValue),
            MenuCommand(677, "MenuEditNewExpandableStringValue", isEnabled, _editCommands.NewExpandableStringValue),
        };
    }

    private ClassicCommand MenuCommand(
        int id,
        string resourceKey,
        bool isEnabled,
        Action? invoke = null,
        IReadOnlyList<ClassicCommandItem>? children = null)
    {
        var parsed = RegeditMenu.ParseMenuText(Get(resourceKey));
        return new ClassicCommand(
            id,
            parsed.Text,
            parsed.AccessKey,
            parsed.AcceleratorText,
            isEnabled,
            children ?? Array.Empty<ClassicCommandItem>(),
            invoke);
    }

    private static MenuFlyout CreateContextFlyout(IReadOnlyList<ClassicCommandItem> items)
    {
        var flyout = new MenuFlyout();
        foreach (ClassicCommandItem item in items)
        {
            flyout.Items.Add(RegeditMenu.BuildFlyoutItem(item, "ContextMenuCommand_"));
        }

        return flyout;
    }

    private void ShowContextFlyout(MenuFlyout flyout, FrameworkElement element, Point point)
    {
        _contextFlyout?.Hide();
        _contextFlyout = flyout;
        flyout.ShowAt(element, point);
    }

    /// <summary>
    /// Starts the inline rename after the flyout has finished closing. The flyout can restore
    /// focus to its placement target, which would immediately cancel a rename box opened inside
    /// the click handler.
    /// </summary>
    private void BeginTreeRenameFromMenu()
    {
        if (!_context.Shell.DispatcherQueue.TryEnqueue(_context.Shell.TreeContent.BeginRenameSelected))
        {
            _context.Shell.TreeContent.BeginRenameSelected();
        }
    }

    private void BeginListRenameFromMenu()
    {
        if (!_context.Shell.DispatcherQueue.TryEnqueue(_context.Shell.ListContent.BeginRenameSelected))
        {
            _context.Shell.ListContent.BeginRenameSelected();
        }
    }

}

using Bwc.Core;
using Bwc.Gpedit.List;
using Bwc.Gpo.Store;
using Bwc.Policy.Admx;
using Bwc.UI.Console.List;
using Bwc.UI.Console.Shell;
using Bwc.UI.Menu;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Bwc.Gpedit;

/// <summary>
/// The commands of the editor (piece G2): Edit, Refresh and Help, the Action menu and the context menus that carry them,
/// the keys and the history commands. Every command reads its target when it runs, never when its menu was built
/// (docs/failure-modes/gpedit-commands.md).
/// </summary>
public sealed partial class GpeditRootView
{
    /// <summary>The ids of the editor commands. One id is one command in the menu bar and in the context menus.</summary>
    private const int EditCommand = 2001;
    private const int RefreshCommand = 2002;
    private const int HelpCommand = 2003;

    private const string ContextPrefix = "ContextMenuCommand_";
    private const string CommandEdit = "Edit";
    private const string CommandRefresh = "Refresh";
    private const string CommandHelp = "Help";
    private const string CommandBack = "Back";
    private const string CommandForward = "Forward";
    private const string CommandUp = "Up";
    private const double KeyboardMenuOffset = 24;

    private int _helpRequests;
    private string _lastCommand = string.Empty;
    private bool _actionMenuHasEdit;
#if !BWC_E2E
    private bool _helpOpen;
#endif
    private MenuFlyout? _contextFlyout;

    private void RecordCommand(string command) => _lastCommand = command;

    // ---- commands -----------------------------------------------------------------------------------

    /// <summary>The policy of the one selected policy row of the shown list. False for a folder row, none or several.</summary>
    private bool TryGetSelectedPolicy(out PolicyDefinition policy, out GpoSide side)
    {
        policy = null!;
        side = default;
        if (_placeholderShown)
        {
            return false;
        }

        IReadOnlyList<ConsoleListRow> selected = _list.SelectedRows;
        if (selected.Count == 1 && selected[0].Tag is GpeditRowTag { Kind: GpeditRowKind.Policy, Policy: { } found } tag)
        {
            policy = found;
            side = tag.Side;
            return true;
        }

        return false;
    }

    /// <summary>Edit: the setting dialog of the selected policy row. Nothing when no policy row is selected.</summary>
    private void RunEdit()
    {
        if (_policyDialog is null && TryGetSelectedPolicy(out PolicyDefinition policy, out GpoSide side))
        {
            RaisePolicyEdit(policy, side);
        }
    }

    /// <summary>Help: counts the request in the E2E flavor and shows the About box in the ship flavor.</summary>
    private void RunHelp()
    {
        RecordCommand(CommandHelp);
        _helpRequests++;
        UpdateReadout();
#if !BWC_E2E
        ShowHelp();
#endif
    }

#if !BWC_E2E
    private void ShowHelp()
    {
        if (_helpOpen || _policyDialog is not null || XamlRoot?.ContentIslandEnvironment is not { } environment)
        {
            return;
        }

        _helpOpen = true;
        try
        {
            _ = GpeditAbout.Show(
                Win32Interop.GetWindowFromWindowId(environment.AppWindowId),
                GpeditStrings.Get("GpeditTitle"),
                Environment.ProcessPath ?? string.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("The Help command failed: " + ex.Message);
        }
        finally
        {
            _helpOpen = false;
        }
    }
#endif

    // ---- Action menu --------------------------------------------------------------------------------

    private List<ClassicCommandItem> BuildActionItems(bool includeEdit)
    {
        var items = new List<ClassicCommandItem>();
        if (includeEdit)
        {
            items.Add(ClassicMenuBuilder.Command(GpeditStrings.Commands("GpeditCommandEdit"), EditCommand, true, invoke: RunEdit));
        }

        items.Add(ClassicMenuBuilder.Command(GpeditStrings.Commands("GpeditCommandRefresh"), RefreshCommand, true, invoke: RunRefresh));
        items.Add(ClassicCommandItem.Separator);
        items.Add(ClassicMenuBuilder.Command(GpeditStrings.Commands("GpeditCommandHelp"), HelpCommand, true, invoke: RunHelp));
        return items;
    }

    /// <summary>
    /// Brings the Action popup in line with the selection: Edit is shown while a policy row is selected and hidden otherwise.
    /// It runs after every change of the node, the selection and the model, so the popup is never older than the state it
    /// shows. The entries are built once (a removed entry stays in the popup's visual tree until the popup is rebuilt).
    /// </summary>
    private void UpdateActionMenu()
    {
        if (!IsEditorHost)
        {
            return;
        }

        bool includeEdit = TryGetSelectedPolicy(out _, out _);
        if (includeEdit != _actionMenuHasEdit)
        {
            ClassicMenuBuilder.SetCommandVisible(Shell.MenuBar, EditCommand, includeEdit);
            _actionMenuHasEdit = includeEdit;
        }
    }

    /// <summary>Brings the toolbar and the Action popup in line with the selection.</summary>
    private void UpdateCommandStates()
    {
        UpdateToolbarStates();
        UpdateActionMenu();
    }

    private void ApplyPopupWidth(IList<MenuFlyoutItemBase> items)
    {
        var labels = new List<string>();
        var accelerators = new List<string>();
        foreach (MenuFlyoutItemBase entry in items)
        {
            if (entry is MenuFlyoutItem leaf)
            {
                labels.Add(leaf.Text ?? string.Empty);
                accelerators.Add(leaf.KeyboardAcceleratorTextOverride ?? string.Empty);
            }
        }

        double width = ClassicMenuMetrics.PopupItemWidthDip(labels, accelerators, XamlRoot?.RasterizationScale ?? 1.0);
        if (width <= 0)
        {
            return;
        }

        foreach (MenuFlyoutItemBase entry in items)
        {
            if (entry is MenuFlyoutItem leaf)
            {
                leaf.MinWidth = width;
            }
        }
    }

    // ---- context menus ------------------------------------------------------------------------------

    private void OnEditorListContext(object? sender, ConsoleListContextEventArgs e)
    {
        if (e.Area == ConsoleListContextArea.Header)
        {
            return;
        }

        IReadOnlyList<ConsoleListRow> selected = _list.SelectedRows;
        bool policyRow = e.Area == ConsoleListContextArea.Item
            && e.Row is { Tag: GpeditRowTag { Kind: GpeditRowKind.Policy } }
            && selected.Count == 1
            && ReferenceEquals(selected[0], e.Row);
        FrameworkElement target = _placeholderShown ? Shell.ResultHostElement : _list;
        Point point = _placeholderShown || e.Position is null ? new Point(KeyboardMenuOffset, KeyboardMenuOffset) : e.Position.Value;
        ShowContextMenu(BuildActionItems(policyRow), target, point);
    }

    private void ShowTreeContextMenu(ConsoleContextMenuEventArgs e)
    {
        if (e.Position is { } position)
        {
            ShowContextMenu(BuildActionItems(includeEdit: false), Shell, position);
        }
        else
        {
            ShowContextMenu(BuildActionItems(includeEdit: false), Shell.TreeContent, new Point(KeyboardMenuOffset, KeyboardMenuOffset));
        }
    }

    private void ShowContextMenu(IReadOnlyList<ClassicCommandItem> items, FrameworkElement target, Point point)
    {
        if (_policyDialog is not null || XamlRoot is null || !target.IsLoaded)
        {
            return;
        }

        _contextFlyout?.Hide();
        var flyout = new MenuFlyout();
        foreach (ClassicCommandItem item in items)
        {
            flyout.Items.Add(ClassicMenuBuilder.BuildFlyoutItem(item, ContextPrefix));
        }

        ApplyPopupWidth(flyout.Items);
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_contextFlyout, flyout))
            {
                _contextFlyout = null;
            }
        };
        _contextFlyout = flyout;
        try
        {
            flyout.ShowAt(target, point);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _contextFlyout = null;
            Log.Warn("The context menu could not be shown: " + ex.Message);
        }
    }

    // ---- keys ---------------------------------------------------------------------------------------

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>
    /// F5 reloads, F1 is Help, Alt+Enter edits the selected policy row of the result pane and Alt+Up goes up from the tree.
    /// Backspace in the tree, Alt+Left and Alt+Right are the frame's. Delete is no command. Nothing runs while a menu is
    /// open, and a held key runs a command once.
    /// </summary>
    private void OnViewPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || !IsEditorHost || _policyDialog is not null || MenuIsOpen())
        {
            return;
        }

        bool alt = IsDown(VirtualKey.Menu);
        bool shift = IsDown(VirtualKey.Shift);
        bool control = IsDown(VirtualKey.Control);
        if (control || shift)
        {
            return;
        }

        bool repeat = e.KeyStatus.WasKeyDown;
        switch (e.Key)
        {
            case VirtualKey.F5 when !alt:
                e.Handled = true;
                if (!repeat)
                {
                    RunRefresh();
                }

                break;
            case VirtualKey.F1 when !alt:
                e.Handled = true;
                if (!repeat)
                {
                    RunHelp();
                }

                break;
            case VirtualKey.Enter when alt:
                e.Handled = true;
                if (!repeat && Shell.FocusedPane == ConsolePane.Result)
                {
                    RunEdit();
                }

                break;
            case VirtualKey.Up when alt:
                if (Shell.FocusedPane == ConsolePane.Tree)
                {
                    e.Handled = true;
                    if (!repeat)
                    {
                        RunUp();
                    }
                }

                break;
        }
    }

    /// <summary>True while a menu popup (the menu bar's or a context menu) is open. A tool tip does not count.</summary>
    private bool MenuIsOpen()
    {
        if (XamlRoot is not { } root)
        {
            return false;
        }

        foreach (Popup popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
        {
            if (popup.Child is not ToolTip)
            {
                return true;
            }
        }

        return false;
    }
}

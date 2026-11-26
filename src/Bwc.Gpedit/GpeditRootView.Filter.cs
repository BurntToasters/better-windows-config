using System.Globalization;
using Bwc.Core;
using Bwc.Gpedit.Filter;
using Bwc.Gpedit.List;
using Bwc.Gpedit.Tree;
using Bwc.Policy.Filter;
using Bwc.UI.Console.List;
using Bwc.UI.Console.Shell;
using Bwc.UI.Menu;
using Bwc.UI.Tree;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace Bwc.Gpedit;

/// <summary>
/// The filter and the sort of the result lists (piece G5, docs/failure-modes/gpedit-filter.md): Filter On, Filter
/// Options and Re-Apply Filter in the Action and View menus, the context menus and the toolbar, the status bar flag,
/// the Filter Options dialog, and the sort that each node keeps. The filter itself is <see cref="GpeditFilterState"/>;
/// the list and the tree read it, so a change of the filter replaces the tree (<see cref="ReplaceTree"/>).
/// </summary>
public sealed partial class GpeditRootView
{
    private const int FilterToggleCommand = 2004;
    private const int FilterOptionsCommand = 2005;
    private const int FilterReApplyCommand = 2006;
    private const int ViewSubmenuCommand = 2100;
    private const int TasksSubmenuCommand = 2101;
    private const int ArrangeSubmenuCommand = 2104;
    private const int AutoArrangeCommand = 2105;
    private const int LineUpCommand = 2106;

    /// <summary>The tag of the separator after the filter entries of the Action popup, so it hides with them.</summary>
    private const int FilterSeparatorTag = 2102;

    /// <summary>The tag of the separator after the filter entries of the View popup, so it hides with them.</summary>
    private const int FilterViewSeparatorTag = 2103;

    private const int FilterToolbarId = ConsoleToolbarIds.FirstExtension;
    private const string FilterToolbarGlyph = "";
    private const string TasksPrefix = "TasksCommand_";
    private const string ViewSubmenuPrefix = "ViewSubmenuCommand_";
    private const string ArrangePrefix = "ArrangeSubmenuCommand_";
    private const string CommandLineUp = "LineUp";
    private const string CommandAutoArrange = "AutoArrange";
    private const string CommandFilterOn = "FilterOn";
    private const string CommandFilterOptions = "FilterOptions";
    private const string CommandReApplyFilter = "ReApplyFilter";

    private readonly GpeditFilterState _filter = new();
    private readonly Dictionary<string, List<ConsoleListSortKey>> _sortStacks = new(StringComparer.Ordinal);
    private GpeditFilterOptionsDialog? _filterDialog;
    private (bool HasFilter, bool Ready, bool On, bool HasTemplates)? _filterUiState;
    private string? _sortNodeKey;
    private int _filterTotal;
    private int _lineUps;
    private bool _autoArrange;

    // ---- the commands -------------------------------------------------------------------------------

    /// <summary>True for the nodes that have the filter: Administrative Templates, a category and All Settings.</summary>
    private static bool NodeHasFilter(TreeNode? node) => GpeditTreeSource.InfoOf(node) is { HasFilter: true };

    private bool FilterCommandsAllowed => _model is not null && _policyDialog is null && _filterDialog is null && _templatesDialog is null && !_closed;

    /// <summary>Filter On: switches the filter on with the options kept, or off.</summary>
    private void RunToggleFilter()
    {
        if (!FilterCommandsAllowed)
        {
            return;
        }

        RecordCommand(CommandFilterOn);
        if (_filter.IsOn)
        {
            _filter.TurnOff();
        }
        else if (!_filter.TurnOn(_model))
        {
            Log.Warn("Filter On was refused: the options kept by the filter cannot make a filter.");
            return;
        }

        ApplyFilterChange();
    }

    /// <summary>Re-Apply Filter: applies the options again to the policy files as they are now. Grayed while off.</summary>
    private void RunReApplyFilter()
    {
        if (!FilterCommandsAllowed || !_filter.IsOn)
        {
            return;
        }

        RecordCommand(CommandReApplyFilter);
        _filter.Rebase(_model);
        ApplyFilterChange();
    }

    /// <summary>Filter Options: opens the dialog. OK takes the options and turns Filter On on; Cancel does nothing.</summary>
    private void RunFilterOptions()
    {
        if (!FilterCommandsAllowed || _model is not { } model || XamlRoot?.ContentIslandEnvironment is not { } environment)
        {
            return;
        }

        RecordCommand(CommandFilterOptions);
        GpeditFilterOptionsDialog dialog;
        try
        {
            dialog = new GpeditFilterOptionsDialog(_filter.Options, PlatformList.From(model.Templates));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("The Filter Options dialog could not be created: " + ex.Message);
            return;
        }

        _filterDialog = dialog;
        _ = RunFilterDialogAsync(dialog, Win32Interop.GetWindowFromWindowId(environment.AppWindowId));
    }

    private async Task RunFilterDialogAsync(GpeditFilterOptionsDialog dialog, nint owner)
    {
        bool accepted = false;
        try
        {
            accepted = await dialog.ShowAsync(owner);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("The Filter Options dialog failed: " + ex.Message);
        }
        finally
        {
            _filterDialog = null;
        }

        try
        {
            if (accepted && dialog.AcceptedOptions is { } options && _model is { } model && _filter.TryAccept(options, model, out _))
            {
                ApplyFilterChange();
            }

            StateChanged();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("The window could not be updated after the Filter Options dialog closed: " + ex.Message);
        }
    }

    /// <summary>The filter changed: the tree and the list are made again, with the selection kept where it can be.</summary>
    private void ApplyFilterChange()
    {
        if (_model is not { } model)
        {
            return;
        }

        try
        {
            ReplaceTree(model, reloaded: false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("The filter could not be applied: " + ex.Message);
        }

        UpdateReadout();
    }

    /// <summary>Remembers what the node shows that the readout and the status need: the policy count with no filter.</summary>
    private void NoteContent(GpeditListContent content) => _filterTotal = content.IsPlaceholder ? 0 : content.TotalPolicyCount;

    // ---- menus --------------------------------------------------------------------------------------

    /// <summary>The three filter entries. Filter On is a check item; Re-Apply Filter is grayed while it is off.</summary>
    private List<ClassicCommandItem> FilterItems() =>
    [
        ClassicMenuBuilder.Command(GpeditStrings.Commands("GpeditCommandFilterOn"), FilterToggleCommand, _model is not null, invoke: RunToggleFilter, kind: ClassicCommandKind.Check),
        ClassicMenuBuilder.Command(GpeditStrings.Commands("GpeditCommandFilterOptions"), FilterOptionsCommand, _model is not null, invoke: RunFilterOptions),
        ClassicMenuBuilder.Command(GpeditStrings.Commands("GpeditCommandReApplyFilter"), FilterReApplyCommand, _model is not null && _filter.IsOn, invoke: RunReApplyFilter),
    ];

    /// <summary>
    /// The entries of the Action popup of the menu bar, built once: Edit, the filter entries, a separator, the tasks submenu
    /// that repeats them, Refresh and Help. The ones that belong to a node or a selection are shown and hidden as it changes
    /// (<see cref="UpdateFilterUi"/> and <see cref="UpdateActionMenu"/>).
    /// </summary>
    private List<ClassicCommandItem> BuildActionBarItems() => BuildActionItems(includeEdit: true, includeFilter: true, includeTemplates: true);

    /// <summary>
    /// The entries of a context menu or of the Action popup: Edit for a policy row; at the nodes that have the filter the
    /// three filter entries, a separator and the tasks submenu with the same entries (and Edit for a policy row); then
    /// Refresh and Help.
    /// </summary>
    private List<ClassicCommandItem> BuildActionItems(bool includeEdit, bool includeFilter, bool includeTemplates = false)
    {
        var items = new List<ClassicCommandItem>();
        if (includeEdit)
        {
            items.Add(ClassicMenuBuilder.Command(GpeditStrings.Commands("GpeditCommandEdit"), EditCommand, true, invoke: RunEdit));
        }

        if (includeTemplates)
        {
            items.AddRange(TemplateItems());
        }

        if (includeFilter)
        {
            items.AddRange(FilterItems());
            items.Add(ClassicCommandItem.Separator);
            var tasks = new List<ClassicCommandItem>();
            if (includeEdit)
            {
                tasks.Add(ClassicMenuBuilder.Command(GpeditStrings.Commands("GpeditCommandEdit"), EditCommand, true, invoke: RunEdit));
            }

            if (includeTemplates)
            {
                tasks.AddRange(TemplateItems());
            }

            tasks.AddRange(FilterItems());
            items.Add(ClassicMenuBuilder.Command(GpeditStrings.Commands("GpeditCommandTasks"), TasksSubmenuCommand, true, children: tasks));
        }

        items.Add(ClassicMenuBuilder.Command(GpeditStrings.Commands("GpeditCommandRefresh"), RefreshCommand, true, invoke: RunRefresh));
        items.Add(ClassicCommandItem.Separator);
        items.Add(ClassicMenuBuilder.Command(GpeditStrings.Commands("GpeditCommandHelp"), HelpCommand, true, invoke: RunHelp));
        return items;
    }

    /// <summary>
    /// The menu of the result list where no row is, in the order of the captures (F/Menus_Computer, F/Menus_User): Export List
    /// (not at Software Settings), a separator, the View submenu, a separator, the icon arrangement submenu, Line Up Icons,
    /// then Refresh (the DIFF-007 addition), a separator and Help.
    /// </summary>
    private List<ClassicCommandItem> BuildEmptyAreaItems(bool includeFilter, bool includeExport)
    {
        var items = new List<ClassicCommandItem>();
        if (includeExport)
        {
            items.Add(ClassicMenuBuilder.Command(ConsoleStrings.Get("ConsoleMenuActionExportList"), ConsoleCommandIds.ExportList, true, invoke: RunExport));
            items.Add(ClassicCommandItem.Separator);
        }

        items.Add(ClassicMenuBuilder.Command(GpeditStrings.Commands("GpeditCommandViewSubmenu"), ViewSubmenuCommand, true, children: ViewSubmenuItems(includeFilter)));
        items.Add(ClassicCommandItem.Separator);
        items.Add(ClassicMenuBuilder.Command(
            GpeditStrings.Commands("GpeditCommandArrangeIcons"),
            ArrangeSubmenuCommand,
            true,
            children: [ClassicMenuBuilder.Command(GpeditStrings.Commands("GpeditCommandAutoArrange"), AutoArrangeCommand, true, invoke: RunAutoArrange, kind: ClassicCommandKind.Check)]));
        items.Add(ClassicMenuBuilder.Command(GpeditStrings.Commands("GpeditCommandLineUpIcons"), LineUpCommand, true, invoke: RunLineUp));
        items.Add(ClassicMenuBuilder.Command(GpeditStrings.Commands("GpeditCommandRefresh"), RefreshCommand, true, invoke: RunRefresh));
        items.Add(ClassicCommandItem.Separator);
        items.Add(ClassicMenuBuilder.Command(GpeditStrings.Commands("GpeditCommandHelp"), HelpCommand, true, invoke: RunHelp));
        return items;
    }

    /// <summary>
    /// The View submenu of the list menu, as the View popup of the menu bar: Add/Remove Columns, a separator, the four view
    /// modes, a separator, the filter entries and a separator (at the nodes that have the filter), Customize View.
    /// </summary>
    private List<ClassicCommandItem> ViewSubmenuItems(bool includeFilter)
    {
        var items = new List<ClassicCommandItem>
        {
            ClassicMenuBuilder.Command(ConsoleStrings.Get("ConsoleMenuViewColumns"), ConsoleCommandIds.AddRemoveColumns, true, invoke: RunColumns),
            ClassicCommandItem.Separator,
        };
        items.AddRange(ViewModeItems());
        items.Add(ClassicCommandItem.Separator);
        if (includeFilter)
        {
            items.AddRange(FilterItems());
            items.Add(ClassicCommandItem.Separator);
        }

        items.Add(ClassicMenuBuilder.Command(ConsoleStrings.Get("ConsoleMenuViewCustomize"), ConsoleCommandIds.CustomizeView, _options.AllowCustomizeViews, invoke: RunCustomize));
        return items;
    }

    /// <summary>
    /// Line Up Icons. The list has no free icon placement (the items follow the item order on a grid), so the command lays the
    /// grid out again and changes neither the mode, the rows nor the selection.
    /// </summary>
    private void RunLineUp()
    {
        RecordCommand(CommandLineUp);
        _lineUps++;
        _list.LineUpItems();
        UpdateReadout();
    }

    /// <summary>Auto Arrange: a check item. Switching it on lays the grid out again.</summary>
    private void RunAutoArrange()
    {
        RecordCommand(CommandAutoArrange);
        _autoArrange = !_autoArrange;
        if (_autoArrange)
        {
            _list.LineUpItems();
        }

        UpdateReadout();
    }

    /// <summary>Sets the view mode radio items and the Auto Arrange item of a new menu to the state they show.</summary>
    private void SetContextStates(IList<MenuFlyoutItemBase> items)
    {
        int mode = CurrentViewModeCommand();
        foreach (MenuFlyoutItemBase item in items)
        {
            switch (item)
            {
                case RadioMenuFlyoutItem { Tag: int id } radio when radio.GroupName == ViewModeGroup:
                    radio.IsChecked = id == mode;
                    break;
                case FilterToggleMenuItem { Tag: int id } toggle when id == AutoArrangeCommand:
                    toggle.IsChecked = _autoArrange;
                    break;
                case MenuFlyoutSubItem sub:
                    SetContextStates(sub.Items);
                    break;
            }
        }
    }

    /// <summary>The menu of the console tree: the filter entries flat (where the node has the filter), Refresh, Help.</summary>
    private List<ClassicCommandItem> BuildTreeMenuItems(bool includeFilter)
    {
        var items = new List<ClassicCommandItem>();
        if (includeFilter)
        {
            items.AddRange(FilterItems());
            items.Add(ClassicCommandItem.Separator);
        }

        items.Add(ClassicMenuBuilder.Command(GpeditStrings.Commands("GpeditCommandRefresh"), RefreshCommand, true, invoke: RunRefresh));
        items.Add(ClassicCommandItem.Separator);
        items.Add(ClassicMenuBuilder.Command(GpeditStrings.Commands("GpeditCommandHelp"), HelpCommand, true, invoke: RunHelp));
        return items;
    }

    /// <summary>
    /// Gives the built entries their final form: the filter toggle is an entry with the Toggle pattern, and the entries
    /// inside the tasks and View submenus have the ids of their own (<c>TasksCommand_</c> and <c>ViewSubmenuCommand_</c> plus
    /// the command id; the arrangement submenu has <c>ArrangeSubmenuCommand_</c>), so the same command in two places stays
    /// tellable.
    /// </summary>
    private static void FinishItems(IList<MenuFlyoutItemBase> items)
    {
        FilterToggleMenuItem.Replace(items, FilterToggleCommand);
        FilterToggleMenuItem.Replace(items, AutoArrangeCommand);
        foreach (MenuFlyoutItemBase item in items)
        {
            if (item is MenuFlyoutSubItem { Tag: int id } sub && id is TasksSubmenuCommand or ViewSubmenuCommand or ArrangeSubmenuCommand)
            {
                string prefix = id == TasksSubmenuCommand ? TasksPrefix : id == ViewSubmenuCommand ? ViewSubmenuPrefix : ArrangePrefix;
                foreach (MenuFlyoutItemBase child in sub.Items)
                {
                    if (child.Tag is int childId)
                    {
                        AutomationProperties.SetAutomationId(child, prefix + childId.ToString(CultureInfo.InvariantCulture));
                    }
                }
            }
        }
    }

    /// <summary>Finishes the entries of the menu bar, once, after the shell built them.</summary>
    private void PrepareFilterMenus()
    {
        foreach (MenuBarItem top in Shell.MenuBar.Items.OfType<MenuBarItem>())
        {
            FinishItems(top.Items);
            if (top.Tag is ConsoleCommandIds.ActionMenu)
            {
                TagFilterSeparator(top.Items, FilterSeparatorTag);
            }
            else if (top.Tag is ConsoleCommandIds.ViewMenu)
            {
                TagFilterSeparator(top.Items, FilterViewSeparatorTag);
            }
        }
    }

    private static void TagFilterSeparator(IList<MenuFlyoutItemBase> items, int tag)
    {
        for (int i = 0; i + 1 < items.Count; i++)
        {
            if (items[i].Tag is int id && id == FilterReApplyCommand && items[i + 1] is MenuFlyoutSeparator separator)
            {
                separator.Tag = tag;
                return;
            }
        }
    }

    /// <summary>Sets the check mark of every Filter On entry in the list and in its submenus.</summary>
    private static void SetFilterChecks(IList<MenuFlyoutItemBase> items, bool isChecked)
    {
        foreach (MenuFlyoutItemBase item in items)
        {
            if (item is FilterToggleMenuItem toggle && toggle.Tag is int id && id == FilterToggleCommand)
            {
                toggle.IsChecked = isChecked;
            }
            else if (item is MenuFlyoutSubItem sub)
            {
                SetFilterChecks(sub.Items, isChecked);
            }
        }
    }

    /// <summary>
    /// Brings the menus, the toolbar button and the status bar flag in line with the node and the filter: the filter entries
    /// show at the nodes that have the filter, Re-Apply Filter is enabled while the filter is on, Filter On is checked while it
    /// is on, and the flag shows while it is on. It runs after every change and acts only when something it shows changed.
    /// </summary>
    private void UpdateFilterUi()
    {
        if (!IsEditorHost)
        {
            return;
        }

        bool hasFilter = NodeHasFilter(Shell.TreeContent.SelectedNode);
        bool ready = _model is not null;
        bool on = _filter.IsOn;
        bool hasTemplates = NodeHasTemplates(Shell.TreeContent.SelectedNode);
        if (_filterUiState is { } known && known == (hasFilter, ready, on, hasTemplates))
        {
            return;
        }

        _filterUiState = (hasFilter, ready, on, hasTemplates);
        MenuBar bar = Shell.MenuBar;
        foreach (int id in new[] { FilterToggleCommand, FilterOptionsCommand, FilterReApplyCommand, TasksSubmenuCommand, FilterSeparatorTag, FilterViewSeparatorTag })
        {
            ClassicMenuBuilder.SetCommandVisible(bar, id, hasFilter);
        }

        // Add/Remove Templates is at the Administrative Templates node only, and is not a policy write: it stays enabled in every
        // access state while the model is loaded.
        ClassicMenuBuilder.SetCommandVisible(bar, AddRemoveTemplatesCommand, hasTemplates);
        ClassicMenuBuilder.SetCommandEnabled(bar, AddRemoveTemplatesCommand, ready);
        ClassicMenuBuilder.SetCommandEnabled(bar, FilterToggleCommand, ready);
        ClassicMenuBuilder.SetCommandEnabled(bar, FilterOptionsCommand, ready);
        ClassicMenuBuilder.SetCommandEnabled(bar, FilterReApplyCommand, ready && on);
        foreach (MenuBarItem top in bar.Items.OfType<MenuBarItem>())
        {
            SetFilterChecks(top.Items, on);
        }

        Shell.Toolbar.SetButtonVisible(FilterToolbarId, hasFilter);
        Shell.Toolbar.SetButtonEnabled(FilterToolbarId, ready);
        Shell.Toolbar.SetButtonChecked(FilterToolbarId, on);

        Shell.StatusBar.FlagText = on ? GpeditStrings.Tree("GpeditStatusFilterFlag") : string.Empty;
    }

    // ---- sort ---------------------------------------------------------------------------------------

    /// <summary>Reads the sort of each node from the settings. A column or direction that is not known is skipped.</summary>
    private void LoadSortStacks()
    {
        foreach (KeyValuePair<string, List<string>> pair in _settings.Sorts ?? [])
        {
            var stack = new List<ConsoleListSortKey>();
            foreach (string entry in pair.Value ?? [])
            {
                int at = entry.LastIndexOf(':');
                if (at > 0
                    && Enum.TryParse(entry[(at + 1)..], ignoreCase: false, out ConsoleListSortDirection direction)
                    && direction != ConsoleListSortDirection.None
                    && Enum.IsDefined(direction))
                {
                    stack.Add(new ConsoleListSortKey(entry[..at], direction));
                }
            }

            if (stack.Count > 0)
            {
                _sortStacks[pair.Key] = stack;
            }
        }
    }

    /// <summary>The list shows the sort of this node: every click made there, in order, or none.</summary>
    private void ApplySortOf(TreeNode node)
    {
        _sortNodeKey = GpeditTreeSource.PathOf(node);
        _list.SetSortStack(_sortStacks.TryGetValue(_sortNodeKey, out List<ConsoleListSortKey>? stack) ? stack : []);
    }

    /// <summary>A click on a header: the node keeps the new stack, and the settings are saved shortly.</summary>
    private void OnListSortChanged(object? sender, EventArgs e)
    {
        if (_sortNodeKey is not { } key)
        {
            return;
        }

        List<ConsoleListSortKey> stack = _list.SortStack.ToList();
        if (stack.Count == 0)
        {
            _ = _sortStacks.Remove(key);
        }
        else
        {
            _sortStacks[key] = stack;
        }

        ScheduleSettingsSave();
    }
}

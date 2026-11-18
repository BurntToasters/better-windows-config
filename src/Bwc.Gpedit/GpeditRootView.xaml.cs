using System.Globalization;
using System.Runtime.InteropServices;
using Bwc.UI.Console.List;
using Bwc.UI.Console.Shell;
using Bwc.UI.Icons;
using Bwc.UI.Menu;
using Bwc.UI.Theme;
using Bwc.UI.Tree;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.Gpedit;

/// <summary>
/// The Local Group Policy Editor content: the shared console frame with a placeholder tree (the two configuration
/// roots) and the shared console list in the result pane. The tree, the list rows and the commands are replaced by
/// the G pieces; until then the host keeps a small selection history so the toolbar shows host-driven enable states,
/// and it writes the last command, activation or context request into the status bar so a test can see it. The list
/// has the placeholder columns Name, State and Comment and, in the E2E flavor only, synthetic rows.
/// </summary>
public sealed partial class GpeditRootView : UserControl
{
    // Placeholder menu ids, outside the frame's ids (ConsoleCommandIds).
    private const int ViewConsoleTreeCommand = 1001;
    private const int ViewToolbarCommand = 1002;
    private const int ViewStatusBarCommand = 1003;
    private const int ViewLargeIconsCommand = 1004;
    private const int ViewSmallIconsCommand = 1005;
    private const int ViewListCommand = 1006;
    private const int ViewDetailCommand = 1007;
    private const int ExtraFileCommand = 1100;
    private const int SaveStateCommand = 1200;
    private const int RestoreStateCommand = 1201;
    private const string ViewModeGroup = "ConsoleViewMode";

    private const uint WmClose = 0x0010;

    private readonly List<TreeNode> _history = [];
    private int _historyIndex = -1;
    private bool _navigating;
    private int _contextRequests;
    private int _activations;
    private readonly ConsoleList _list = new();
    private readonly List<MenuFlyoutItemBase> _menuItems = [];

    public GpeditRootView()
    {
        InitializeComponent();

        // The palette is pinned on RootGrid, as the other tools do, so this UserControl root keeps no
        // RequestedTheme and its ActualTheme follows Windows. A change of the effective Windows theme
        // raises ActualThemeChanged here, and the palette re-reads.
        ApplyTheme();
        BwcTheme.Changed += (_, _) => ApplyTheme();
        ActualThemeChanged += (_, _) => BwcTheme.RefreshSystem();

        ConfigureList();
        Shell.SetMenuExtensions(CreateMenuExtensions());
        PinMenuItems();
        Shell.ToolbarCommandInvoked += OnToolbarCommand;
        Shell.ContextMenuRequested += OnContextMenuRequested;
        Shell.ViewChanged += (_, _) => SyncViewChecks();

        var computer = new TreeNode("Computer Configuration") { Icon = IconKind.ClosedFolder };
        var user = new TreeNode("User Configuration") { Icon = IconKind.ClosedFolder };
        Shell.TreeContent.SetRoots([computer, user]);
        Shell.TreeContent.SelectionChanged += OnTreeSelectionChanged;
        Shell.TreeContent.SelectNode(computer);
        SyncViewChecks();

#if BWC_E2E
        string? seededStatus = Environment.GetEnvironmentVariable("BWC_E2E_CONSOLE_STATUS_TEXT");
        if (!string.IsNullOrEmpty(seededStatus))
        {
            Shell.StatusText = seededStatus;
        }

        SeedE2ERows();
#endif
    }

    /// <summary>
    /// The placeholder result list: the shared list in the result pane with three neutral columns. The rows come from
    /// the G1 piece; the E2E flavor fills synthetic ones.
    /// </summary>
    private void ConfigureList()
    {
        _list.SetColumns(
        [
            new ConsoleListColumn("name", "Name", 350),
            new ConsoleListColumn("state", "State", 100),
            new ConsoleListColumn("comment", "Comment", 200),
        ]);

        // The classic list names a row by its text plus a word the host adds; the list control adds nothing itself.
        _list.ItemNameFormatter = row => row.Text + " setting";
        _list.ItemActivated += OnListItemActivated;
        _list.ContextMenuRequested += OnListContextMenuRequested;
        _list.ViewModeChanged += (_, _) => SyncViewChecks();
        Shell.ResultContent = _list;
        Shell.ResultFocusTarget = _list;
    }

    private void OnListItemActivated(object? sender, ConsoleListItemEventArgs e)
    {
        _activations++;
        Shell.StatusText = $"Activated: {e.Row.Text} n={_activations}";
    }

    private void OnListContextMenuRequested(object? sender, ConsoleListContextEventArgs e)
    {
        string source = e.FromKeyboard ? "key" : "pointer";
        string target = e.Area switch
        {
            ConsoleListContextArea.Item => $" row={e.Row?.Text}",
            ConsoleListContextArea.Header => " header",
            _ => string.Empty,
        };
        _contextRequests++;
        Shell.StatusText = $"Context menu: pane=Result source={source} n={_contextRequests}{target}";
    }

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = BwcTheme.ElementThemeFor(BwcTheme.Current);
    }

    private ConsoleMenuExtensions CreateMenuExtensions()
    {
        var file = new List<ClassicCommandItem>();
#if BWC_E2E
        // E2E only: a File entry whose access key is the one of Exit, to prove that the host's entry wins it.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BWC_E2E_CONSOLE_COLLIDE")))
        {
            file.Add(ClassicMenuBuilder.Command(
                "E&xtra",
                ExtraFileCommand,
                isEnabled: true,
                invoke: () => Shell.StatusText = "Xtra"));
        }
#endif

        // The view toggles stand in for Customize View until the frame commands exist.
        var view = new List<ClassicCommandItem>
        {
            ClassicMenuBuilder.Command("&Console Tree", ViewConsoleTreeCommand, true, invoke: () => Shell.IsTreeVisible = !Shell.IsTreeVisible, kind: ClassicCommandKind.Check),
            ClassicMenuBuilder.Command("&Toolbar", ViewToolbarCommand, true, invoke: () => Shell.IsToolbarVisible = !Shell.IsToolbarVisible, kind: ClassicCommandKind.Check),
            ClassicMenuBuilder.Command("Stat&us Bar", ViewStatusBarCommand, true, invoke: () => Shell.IsStatusBarVisible = !Shell.IsStatusBarVisible, kind: ClassicCommandKind.Check),
            ClassicMenuBuilder.Command("Lar&ge Icons", ViewLargeIconsCommand, true, invoke: () => SetViewMode(ConsoleListViewMode.LargeIcons), kind: ClassicCommandKind.Radio, radioGroup: ViewModeGroup),
            ClassicMenuBuilder.Command("S&mall Icons", ViewSmallIconsCommand, true, invoke: () => SetViewMode(ConsoleListViewMode.SmallIcons), kind: ClassicCommandKind.Radio, radioGroup: ViewModeGroup),
            ClassicMenuBuilder.Command("&List", ViewListCommand, true, invoke: () => SetViewMode(ConsoleListViewMode.List), kind: ClassicCommandKind.Radio, radioGroup: ViewModeGroup),
            ClassicMenuBuilder.Command("&Detail", ViewDetailCommand, true, invoke: () => SetViewMode(ConsoleListViewMode.Detail), kind: ClassicCommandKind.Radio, radioGroup: ViewModeGroup),
        };
#if BWC_E2E
        // E2E only: save and restore the list state through the host, to exercise GetState and ApplyState.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BWC_E2E_CONSOLE_STATE_MENU")))
        {
            view.Add(ClassicMenuBuilder.Command("E2E Sa&ve State", SaveStateCommand, true, invoke: SaveListState));
            view.Add(ClassicMenuBuilder.Command("E2E &Restore State", RestoreStateCommand, true, invoke: RestoreListState));
        }
#endif

        return new ConsoleMenuExtensions
        {
            FileEntries = file,
            ViewEntries = view,
            Exit = RequestClose,
        };
    }

    /// <summary>
    /// MenuItemActions keys each action by the managed wrapper of its menu item, weakly. A wrapper nothing refers to is
    /// collected, and the menu keyboard then finds no action for the item and closes the popup without running the
    /// command. The view mode radios have no other owner, so the host keeps every menu item reachable.
    /// </summary>
    private void PinMenuItems()
    {
        void Walk(IList<MenuFlyoutItemBase> items)
        {
            foreach (MenuFlyoutItemBase item in items)
            {
                _menuItems.Add(item);
                if (item is MenuFlyoutSubItem sub)
                {
                    Walk(sub.Items);
                }
            }
        }

        _menuItems.Clear();
        foreach (MenuBarItem top in Shell.MenuBar.Items)
        {
            Walk(top.Items);
        }
    }

    /// <summary>
    /// Sets the view mode and gives the list the keyboard focus. A menu command that ends menu mode leaves the focus on the
    /// menu bar item, and the arrow keys must keep driving the list the mode was changed on.
    /// </summary>
    private void SetViewMode(ConsoleListViewMode mode)
    {
        _list.ViewMode = mode;
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, _list.FocusList);
    }

    private void SyncViewChecks()
    {
        ClassicMenuBuilder.SetChecked(Shell.MenuBar, ViewConsoleTreeCommand, Shell.IsTreeVisible);
        ClassicMenuBuilder.SetChecked(Shell.MenuBar, ViewToolbarCommand, Shell.IsToolbarVisible);
        ClassicMenuBuilder.SetChecked(Shell.MenuBar, ViewStatusBarCommand, Shell.IsStatusBarVisible);
        int modeCommand = _list.ViewMode switch
        {
            ConsoleListViewMode.LargeIcons => ViewLargeIconsCommand,
            ConsoleListViewMode.SmallIcons => ViewSmallIconsCommand,
            ConsoleListViewMode.List => ViewListCommand,
            _ => ViewDetailCommand,
        };
        ClassicMenuBuilder.SetRadioChecked(Shell.MenuBar, ViewModeGroup, modeCommand);
    }

    private void OnTreeSelectionChanged(object? sender, TreeNodeEventArgs e)
    {
        if (!_navigating)
        {
            // A new selection ends the forward part of the history, as in a browser.
            if (_historyIndex < _history.Count - 1)
            {
                _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
            }

            _history.Add(e.Node);
            _historyIndex = _history.Count - 1;
        }

        UpdateToolbarStates();
    }

    private void UpdateToolbarStates()
    {
        ConsoleToolbar toolbar = Shell.Toolbar;
        toolbar.SetButtonEnabled(ConsoleToolbarIds.Back, _historyIndex > 0);
        toolbar.SetButtonEnabled(ConsoleToolbarIds.Forward, _historyIndex >= 0 && _historyIndex < _history.Count - 1);
        toolbar.SetButtonEnabled(ConsoleToolbarIds.UpOneLevel, Shell.TreeContent.SelectedNode?.Parent is not null);
        toolbar.SetButtonEnabled(ConsoleToolbarIds.Properties, Shell.TreeContent.SelectedNode is not null);
        toolbar.SetButtonEnabled(ConsoleToolbarIds.Refresh, true);
    }

    private void OnToolbarCommand(object? sender, ConsoleToolbarEventArgs e)
    {
        switch (e.Id)
        {
            case ConsoleToolbarIds.Back when _historyIndex > 0:
                Navigate(_historyIndex - 1);
                break;
            case ConsoleToolbarIds.Forward when _historyIndex < _history.Count - 1:
                Navigate(_historyIndex + 1);
                break;
#if BWC_E2E
            case ConsoleToolbarIds.Refresh:
                RefreshE2ERows();
                break;
#endif
        }

        Shell.StatusText = "Command: " + CommandName(e.Id);
    }

    private void Navigate(int index)
    {
        _navigating = true;
        try
        {
            _historyIndex = index;
            Shell.TreeContent.SelectNode(_history[index]);
        }
        finally
        {
            _navigating = false;
        }

        UpdateToolbarStates();
    }

    private static string CommandName(int id) => id switch
    {
        ConsoleToolbarIds.Back => "Back",
        ConsoleToolbarIds.Forward => "Forward",
        ConsoleToolbarIds.UpOneLevel => "UpOneLevel",
        ConsoleToolbarIds.ShowHideConsoleTree => "ShowHideConsoleTree",
        ConsoleToolbarIds.Properties => "Properties",
        ConsoleToolbarIds.Refresh => "Refresh",
        ConsoleToolbarIds.ExportList => "ExportList",
        ConsoleToolbarIds.Help => "Help",
        _ => "Extension" + id,
    };

    private void OnContextMenuRequested(object? sender, ConsoleContextMenuEventArgs e)
    {
        // The shell sees the context keys first. The list knows the focused row, so the key goes to it and it raises
        // the one request that OnListContextMenuRequested answers.
        if (e.Pane == ConsolePane.Result && e.FromKeyboard)
        {
            _list.RaiseContextAtFocus();
            return;
        }

        string source = e.FromKeyboard ? "key" : "pointer";
        string node = e.Node is null ? string.Empty : $" node={e.Node.Name}";
        _contextRequests++;
        Shell.StatusText = $"Context menu: pane={e.Pane} source={source}{node} n={_contextRequests}";
    }

#if BWC_E2E
    private const int DefaultE2ERows = 8;
    private const int MaxE2ERows = 100_000;
    private static readonly string[] E2EStates = ["Not configured", "Enabled", "Disabled"];
    private static readonly string[] E2ETypeAheadNames =
        ["Alpha", "Beta", "Bravo", "Charlie", "Delta", "Écho", "Foxtrot", "Zulu", "Ünder", "日本語"];

    private int _e2eRowCount = DefaultE2ERows;
    private int _e2eGeneration;
    private bool _e2eRowsRequested;

    /// <summary>
    /// E2E only. BWC_E2E_CONSOLE_ROWS is the number of synthetic rows (default 8; a missing, malformed or negative value
    /// gives the default, more than 100,000 is limited). Row i is named "Setting NNNNN" with a number that is a
    /// permutation of the rows, has the state i % 3 and the comment i % 2, and its tag is i. BWC_E2E_CONSOLE_NAMES set
    /// to typeahead names the first rows from a fixed set that includes non ASCII names, and BWC_E2E_CONSOLE_LONGTEXT
    /// gives row 3 a comment of 5,000 characters. With BWC_E2E_CONSOLE_ROWS set, the toolbar Refresh replaces the rows.
    /// </summary>
    private void SeedE2ERows()
    {
        string? requested = Environment.GetEnvironmentVariable("BWC_E2E_CONSOLE_ROWS");
        _e2eRowsRequested = !string.IsNullOrEmpty(requested);
        if (int.TryParse(requested, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) && count >= 0)
        {
            _e2eRowCount = Math.Min(count, MaxE2ERows);
        }

        _list.SetRows(CreateE2ERows(_e2eRowCount, 0));
    }

    private static List<ConsoleListRow> CreateE2ERows(int count, int generation)
    {
        bool typeAhead = string.Equals(Environment.GetEnvironmentVariable("BWC_E2E_CONSOLE_NAMES"), "typeahead", StringComparison.Ordinal);
        bool longText = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BWC_E2E_CONSOLE_LONGTEXT"));
        var rows = new List<ConsoleListRow>(count);
        for (int i = 0; i < count; i++)
        {
            long key = ((i * 37L) + 11) % Math.Max(1, count);
            string name = typeAhead && i < E2ETypeAheadNames.Length
                ? E2ETypeAheadNames[i]
                : (generation > 0 ? "Refreshed " : "Setting ") + key.ToString("D5", CultureInfo.InvariantCulture);
            string comment = longText && i == 3 ? new string('W', 5000) : (i % 2 == 0 ? "No" : "Yes");

            // Every fourth row names a module icon that does not exist, to exercise the fallback glyph.
            IconReference icon = i % 4 == 3 ? ModuleIcon.ById("bwc-e2e-missing-module.dll", 1) : IconKind.StringValue;
            rows.Add(new ConsoleListRow(name, E2EStates[i % 3], comment) { SmallIcon = icon, LargeIcon = icon, Tag = i });
        }

        return rows;
    }

    /// <summary>E2E only: Refresh replaces the rows by 100 of a new set, then by none, then by the full set again.</summary>
    private void RefreshE2ERows()
    {
        if (!_e2eRowsRequested)
        {
            return;
        }

        _e2eGeneration++;
        int phase = _e2eGeneration % 3;
        int count = phase switch
        {
            1 => Math.Min(100, _e2eRowCount),
            2 => 0,
            _ => _e2eRowCount,
        };
        _list.SetRows(CreateE2ERows(count, phase == 0 ? 0 : _e2eGeneration), keepSelectionByTag: true);
    }

    private void SaveListState()
    {
        ConsoleListState state = _list.GetState();
        string columns = string.Join(
            ",",
            state.Columns.Select(column => column.Id + "=" + column.Width.ToString("0.##", CultureInfo.InvariantCulture) + (column.Visible ? string.Empty : "!")));
        string sort = state.SortColumnId is null ? "none" : $"{state.SortColumnId}:{state.SortDirection}";
        Shell.StatusText = $"State: {columns};sort={sort};mode={state.ViewMode}";
    }

    private void RestoreListState()
    {
        _list.ApplyState(new ConsoleListState(
            [
                new ConsoleListColumnState("comment", 150, true),
                new ConsoleListColumnState("name", 222, true),
                new ConsoleListColumnState("state", 100, false),
            ],
            "name",
            ConsoleListSortDirection.Ascending,
            ConsoleListViewMode.Detail));
    }
#endif

    /// <summary>File > Exit closes the window the way its close button does.</summary>
    private void RequestClose()
    {
        if (XamlRoot?.ContentIslandEnvironment is { } environment)
        {
            _ = PostMessageW(Win32Interop.GetWindowFromWindowId(environment.AppWindowId), WmClose, 0, 0);
        }
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint hWnd, uint msg, nuint wParam, nint lParam);
}

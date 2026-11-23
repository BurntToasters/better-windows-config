using System.Runtime.InteropServices;
using Bwc.Core;
using Bwc.UI.Console.List;
using Bwc.UI.Console.Shell;
using Bwc.UI.Menu;
using Bwc.UI.Theme;
using Bwc.UI.Tree;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.Gpedit;

/// <summary>
/// The Local Group Policy Editor content: the shared console frame (menu bar, toolbar, tree, result pane, status bar) with
/// the editor's tree and result list (GpeditRootView.Editor.cs). This file keeps the frame wiring: the view menu, the
/// navigation history and its toolbar buttons, the theme and Exit. The Action menu, the context menus, the keys and
/// Help are in GpeditRootView.Commands.cs, and the reload that Refresh starts is in GpeditRootView.Reload.cs. In the E2E
/// flavor BWC_E2E_GPEDIT_HOST=console-frame selects the placeholder frame host instead (GpeditRootView.E2EFrame.cs),
/// which the Console_* tests drive.
/// </summary>
public sealed partial class GpeditRootView : UserControl
{
    // Menu ids of the view's own entries, outside the frame's ids (ConsoleCommandIds). The editor commands are in
    // GpeditRootView.Commands.cs.
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
    private const int WrapperGcCommand = 1202;
    private const int FileSmallIconsCommand = 1210;
    private const string ViewModeGroup = "ConsoleViewMode";

    private const uint WmClose = 0x0010;

    private const int HistoryLimit = 100;

    private readonly List<TreeNode> _history = [];
    private int _historyIndex = -1;
    private bool _navigating;
    private readonly ConsoleList _list = new();
    private readonly Bwc.UI.Console.Extended.ConsoleExtendedView _extended = new();

#if BWC_E2E
    private bool _frameMode;
#endif

    public GpeditRootView()
    {
        InitializeComponent();

        // The palette is pinned on RootGrid, as the other tools do, so this UserControl root keeps no
        // RequestedTheme and its ActualTheme follows Windows. A change of the effective Windows theme
        // raises ActualThemeChanged here, and the palette re-reads. BwcTheme.Changed is static, so it is
        // subscribed while the view is loaded only.
        ApplyTheme();
        ActualThemeChanged += OnActualThemeChanged;
        Loaded += OnViewLoaded;
        Unloaded += OnViewUnloaded;
        PreviewKeyDown += OnViewPreviewKeyDown;

#if BWC_E2E
        _frameMode = string.Equals(Environment.GetEnvironmentVariable("BWC_E2E_GPEDIT_HOST"), "console-frame", StringComparison.Ordinal);
#endif

        Shell.SetMenuExtensions(CreateMenuExtensions());
        if (IsEditorHost)
        {
            PrepareFilterMenus();
            ClassicMenuBuilder.SetCommandVisible(Shell.MenuBar, EditCommand, false);
            ClassicMenuBuilder.SetCommandVisible(Shell.MenuBar, AddRemoveTemplatesCommand, false);
            Shell.Toolbar.AddExtensionButton(
                FilterToolbarId,
                GpeditStrings.Commands("GpeditToolbarFilter"),
                FilterToolbarGlyph,
                null,
                idText: "Filter",
                toggle: true);
            Shell.Toolbar.SetButtonVisible(FilterToolbarId, false);
        }

        Shell.ToolbarCommandInvoked += OnToolbarCommand;
        Shell.ContextMenuRequested += OnContextMenuRequested;
        Shell.ViewChanged += (_, _) => SyncViewChecks();

#if BWC_E2E
        if (_frameMode)
        {
            InitializeFrameHost();
            return;
        }
#endif

        InitializeEditor();
    }

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = BwcTheme.ElementThemeFor(BwcTheme.Current);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTheme();

    private void OnActualThemeChanged(FrameworkElement sender, object args) => BwcTheme.RefreshSystem();

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        BwcTheme.Changed -= OnThemeChanged;
        BwcTheme.Changed += OnThemeChanged;
        ApplyTheme();
    }

    private void OnViewUnloaded(object sender, RoutedEventArgs e)
    {
        BwcTheme.Changed -= OnThemeChanged;
    }

    /// <summary>True for the real editor, false for the E2E console frame host.</summary>
    private bool IsEditorHost
    {
        get
        {
#if BWC_E2E
            return !_frameMode;
#else
            return true;
#endif
        }
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

        // E2E only: a File entry with the same text as a View radio, for the action lookup test (see menus.md).
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BWC_E2E_MENU_GC")))
        {
            file.Add(ClassicMenuBuilder.Command(
                "S&mall Icons",
                FileSmallIconsCommand,
                isEnabled: true,
                invoke: () => Shell.StatusText = "File SmallIcons ran"));
        }
#endif

        // The view toggles stand in for Customize View, which the frame does not implement yet.
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
        if (IsEditorHost)
        {
            // The filter entries sit between the view modes and Customize View (the frame adds that one after these).
            view.AddRange(FilterItems());
        }
#if BWC_E2E
        // E2E only: save and restore the list state through the host, to exercise GetState and ApplyState.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BWC_E2E_CONSOLE_STATE_MENU")))
        {
            view.Add(ClassicMenuBuilder.Command("E2E Sa&ve State", SaveStateCommand, true, invoke: SaveListState));
            view.Add(ClassicMenuBuilder.Command("E2E &Restore State", RestoreStateCommand, true, invoke: RestoreListState));
        }

        // E2E only: drops every reference to the menu item wrappers and collects them, to prove the menu keyboard does not
        // depend on a wrapper staying alive.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BWC_E2E_MENU_GC")))
        {
            view.Add(ClassicMenuBuilder.Command("E2E Wra&pper GC", WrapperGcCommand, true, invoke: CollectMenuWrappers));
        }
#endif

        return new ConsoleMenuExtensions
        {
            FileEntries = file,
            ActionEntries = IsEditorHost ? BuildActionBarItems() : [],
            ViewEntries = view,
            Exit = RequestClose,
            HelpTopics = IsEditorHost ? RunHelp : null,
        };
    }

    /// <summary>Sets the view mode. The menu keyboard returns the focus to where it was before the menu was entered.</summary>
    private void SetViewMode(ConsoleListViewMode mode)
    {
        _list.ViewMode = mode;
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
            if (_history.Count > HistoryLimit)
            {
                _history.RemoveRange(0, _history.Count - HistoryLimit);
            }

            _historyIndex = _history.Count - 1;
        }

#if BWC_E2E
        if (_frameMode)
        {
            UpdateToolbarStates();
            UpdateDescription();
            return;
        }
#endif

        ShowNode(e.Node);
        UpdateToolbarStates();
    }

    private void UpdateToolbarStates()
    {
        ConsoleToolbar toolbar = Shell.Toolbar;
        toolbar.SetButtonEnabled(ConsoleToolbarIds.Back, _historyIndex > 0);
        toolbar.SetButtonEnabled(ConsoleToolbarIds.Forward, _historyIndex >= 0 && _historyIndex < _history.Count - 1);
        toolbar.SetButtonEnabled(ConsoleToolbarIds.UpOneLevel, Shell.TreeContent.SelectedNode?.Parent is not null);
        toolbar.SetButtonEnabled(ConsoleToolbarIds.Refresh, true);
        if (IsEditorHost)
        {
            toolbar.SetButtonEnabled(ConsoleToolbarIds.Properties, TryGetSelectedPolicy(out _, out _));
            toolbar.SetButtonEnabled(ConsoleToolbarIds.Help, true);
        }
        else
        {
            toolbar.SetButtonEnabled(ConsoleToolbarIds.Properties, Shell.TreeContent.SelectedNode is not null);
        }
    }

    private void OnToolbarCommand(object? sender, ConsoleToolbarEventArgs e)
    {
        if (IsEditorHost)
        {
            switch (e.Id)
            {
                case ConsoleToolbarIds.Back:
                    RunBack();
                    break;
                case ConsoleToolbarIds.Forward:
                    RunForward();
                    break;
                case ConsoleToolbarIds.UpOneLevel:
                    RunUp();
                    break;
                case ConsoleToolbarIds.Properties:
                    RunEdit();
                    break;
                case ConsoleToolbarIds.Refresh:
                    RunRefresh();
                    break;
                case ConsoleToolbarIds.Help:
                    RunHelp();
                    break;
                case FilterToolbarId:
                    RunToggleFilter();
                    break;
            }

            return;
        }

#if BWC_E2E
        switch (e.Id)
        {
            case ConsoleToolbarIds.Back when _historyIndex > 0:
                Navigate(_historyIndex - 1);
                break;
            case ConsoleToolbarIds.Forward when _historyIndex < _history.Count - 1:
                Navigate(_historyIndex + 1);
                break;
            case ConsoleToolbarIds.Refresh:
                RefreshE2ERows();
                break;
        }

        Shell.StatusText = "Command: " + CommandName(e.Id);
#endif
    }

    private void RunBack()
    {
        if (_historyIndex > 0)
        {
            RecordCommand(CommandBack);
            Navigate(_historyIndex - 1);
        }
    }

    private void RunForward()
    {
        if (_historyIndex < _history.Count - 1)
        {
            RecordCommand(CommandForward);
            Navigate(_historyIndex + 1);
        }
    }

    private void RunUp()
    {
        if (Shell.TreeContent.SelectedNode?.Parent is { } parent)
        {
            RecordCommand(CommandUp);
            Shell.TreeContent.SelectNode(parent);
        }
    }

    /// <summary>
    /// Selects the history entry. A node that the tree cannot show (an ancestor was collapsed since) leaves the selection
    /// where it was and drops the entry, so the buttons never point at a step that does nothing.
    /// </summary>
    private void Navigate(int index)
    {
        TreeNode target = _history[index];
        TreeNode? before = Shell.TreeContent.SelectedNode;
        _navigating = true;
        try
        {
            Shell.TreeContent.SelectNode(target);
        }
        finally
        {
            _navigating = false;
        }

        if (ReferenceEquals(Shell.TreeContent.SelectedNode, target))
        {
            _historyIndex = index;
        }
        else
        {
            _history.RemoveAt(index);
            _historyIndex = index < _historyIndex ? _historyIndex - 1 : Math.Min(_historyIndex, _history.Count - 1);
            Log.Warn($"The history entry '{target.Name}' could not be shown and was dropped (selected: {before?.Name}).");
        }

        UpdateToolbarStates();
    }

    private void OnContextMenuRequested(object? sender, ConsoleContextMenuEventArgs e)
    {
        // The shell sees the context keys first. The list knows the focused row, so the key goes to it and it raises
        // the one request that the list's own handler answers.
        if (e.Pane == ConsolePane.Result && e.FromKeyboard)
        {
            _list.RaiseContextAtFocus();
            return;
        }

#if BWC_E2E
        if (_frameMode)
        {
            string source = e.FromKeyboard ? "key" : "pointer";
            string node = e.Node is null ? string.Empty : $" node={e.Node.Name}";
            _contextRequests++;
            Shell.StatusText = $"Context menu: pane={e.Pane} source={source}{node} n={_contextRequests}";
            return;
        }
#endif

        if (e.Pane == ConsolePane.Tree)
        {
            ShowTreeContextMenu(e);
        }
    }

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

using System.Runtime.InteropServices;
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
/// the editor's tree and result list (GpeditRootView.Editor.cs). The window commands and menus are placeholders until piece
/// G2; this file keeps the shared frame wiring: the view menu, the history toolbar, the theme and Exit. In the E2E flavor
/// BWC_E2E_GPEDIT_HOST=console-frame selects the scaffold's placeholder frame host instead (GpeditRootView.E2EFrame.cs),
/// which the Console_* tests drive.
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
    private const int WrapperGcCommand = 1202;
    private const int FileSmallIconsCommand = 1210;
    private const string ViewModeGroup = "ConsoleViewMode";

    private const uint WmClose = 0x0010;

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
        // raises ActualThemeChanged here, and the palette re-reads.
        ApplyTheme();
        BwcTheme.Changed += (_, _) => ApplyTheme();
        ActualThemeChanged += (_, _) => BwcTheme.RefreshSystem();

        Shell.SetMenuExtensions(CreateMenuExtensions());
        Shell.ToolbarCommandInvoked += OnToolbarCommand;
        Shell.ContextMenuRequested += OnContextMenuRequested;
        Shell.ViewChanged += (_, _) => SyncViewChecks();

#if BWC_E2E
        _frameMode = string.Equals(Environment.GetEnvironmentVariable("BWC_E2E_GPEDIT_HOST"), "console-frame", StringComparison.Ordinal);
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
            ViewEntries = view,
            Exit = RequestClose,
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
            _historyIndex = _history.Count - 1;
        }

        UpdateToolbarStates();
#if BWC_E2E
        if (_frameMode)
        {
            UpdateDescription();
            return;
        }
#endif

        ShowNode(e.Node);
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
            case ConsoleToolbarIds.UpOneLevel when UpOneLevelIsHandled() && Shell.TreeContent.SelectedNode?.Parent is { } parent:
                Shell.TreeContent.SelectNode(parent);
                break;
#if BWC_E2E
            case ConsoleToolbarIds.Refresh:
                RefreshE2ERows();
                break;
#endif
        }

#if BWC_E2E
        if (_frameMode)
        {
            Shell.StatusText = "Command: " + CommandName(e.Id);
        }
#endif
    }

    /// <summary>Up One Level selects the parent node. The frame test host only echoes the command.</summary>
    private bool UpOneLevelIsHandled()
    {
#if BWC_E2E
        return !_frameMode;
#else
        return true;
#endif
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
        }
#endif
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

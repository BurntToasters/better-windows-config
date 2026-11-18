using System.Runtime.InteropServices;
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
/// roots) and an empty result pane. The tree, the result list and the commands are replaced by the G pieces; until
/// then the host keeps a small selection history so the toolbar shows host-driven enable states, and it writes the
/// last command or context request into the status bar so a test can see it.
/// </summary>
public sealed partial class GpeditRootView : UserControl
{
    // Placeholder menu ids, outside the frame's ids (ConsoleCommandIds).
    private const int ViewConsoleTreeCommand = 1001;
    private const int ViewToolbarCommand = 1002;
    private const int ViewStatusBarCommand = 1003;
    private const int ExtraFileCommand = 1100;

    private const uint WmClose = 0x0010;

    private readonly List<TreeNode> _history = [];
    private int _historyIndex = -1;
    private bool _navigating;
    private int _contextRequests;

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
#endif
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
        };

        return new ConsoleMenuExtensions
        {
            FileEntries = file,
            ViewEntries = view,
            Exit = RequestClose,
        };
    }

    private void SyncViewChecks()
    {
        ClassicMenuBuilder.SetChecked(Shell.MenuBar, ViewConsoleTreeCommand, Shell.IsTreeVisible);
        ClassicMenuBuilder.SetChecked(Shell.MenuBar, ViewToolbarCommand, Shell.IsToolbarVisible);
        ClassicMenuBuilder.SetChecked(Shell.MenuBar, ViewStatusBarCommand, Shell.IsStatusBarVisible);
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
        string source = e.FromKeyboard ? "key" : "pointer";
        string node = e.Node is null ? string.Empty : $" node={e.Node.Name}";
        _contextRequests++;
        Shell.StatusText = $"Context menu: pane={e.Pane} source={source}{node} n={_contextRequests}";
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

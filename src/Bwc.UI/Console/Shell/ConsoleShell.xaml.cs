using Bwc.UI.Menu;
using Bwc.UI.Theme;
using Bwc.UI.Tree;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Bwc.UI.Console.Shell;

/// <summary>
/// The MMC 3.0 user-mode frame: menu bar, standard toolbar, console tree, splitter, a result pane slot and a status
/// bar. It knows no snap-in. The host supplies the menu table, the tree nodes, the result content, the enable state of
/// the toolbar buttons and the status text, and answers the events. Local Security Policy reuses it.
/// </summary>
public sealed partial class ConsoleShell : UserControl
{
    /// <summary>The initial width of the console tree in DIPs (312 px at 192 DPI in the probed classic window).</summary>
    public const double DefaultTreePaneWidth = 156;

    /// <summary>The width of the splitter in DIPs (6 px at 192 DPI in the probe).</summary>
    public const double SplitterWidth = 3;

    /// <summary>The narrowest the tree can be dragged, in DIPs. A BWC choice, not measured from classic.</summary>
    public const double MinTreePaneWidth = 48;

    /// <summary>The narrowest the result pane can be made by the splitter, in DIPs. A BWC choice.</summary>
    public const double MinResultPaneWidth = 48;

    private const double MaxTreePaneWidth = 4096;
    private const double MenuBarHeight = 22;

    private double _preferredTreeWidth = DefaultTreePaneWidth;
    private double _effectiveTreeWidth = DefaultTreePaneWidth;
    private double _dragStartWidth = DefaultTreePaneWidth;
    private bool _splitterDragging;
    private bool _treeVisible = true;
    private bool _toolbarVisible = true;
    private bool _snapInToolbarsVisible = true;
    private bool _statusBarVisible = true;
    private ClassicMenuKeyboard? _menuKeyboard;
    private nint _topHwnd;
    private bool _metricsSubscribed;
    private XamlRoot? _observedRoot;
    private double _observedScale;
    private IReadOnlyList<string> _collisions = [];

    public ConsoleShell()
    {
        InitializeComponent();
        AutomationProperties.SetName(TreeControl, ConsoleStrings.Get("ConsoleTreeName"));
        AutomationProperties.SetName(ResultPaneBorder, ConsoleStrings.Get("ConsoleResultName"));
        AutomationProperties.SetName(ResultHost, ConsoleStrings.Get("ConsoleResultName"));
        AutomationProperties.SetName(SplitterThumb, ConsoleStrings.Get("ConsoleSplitterName"));
        AutomationProperties.SetName(MenuBarRoot, ConsoleStrings.Get("ConsoleMenuBarName"));
        AutomationProperties.SetAutomationId(MenuBarRoot, "ConsoleMenuBar");

        BwcTheme.EnsureInitialized();
        RootGrid.RequestedTheme = BwcTheme.ElementThemeFor(BwcTheme.Current);
        ClassicMenuBrushes.Refresh();
        ActualThemeChanged += (_, _) => BwcTheme.RefreshSystem();

        // The focus rectangle is drawn on the focused row, so the system visual is switched off.
        TreeControl.UseSystemFocusVisuals = false;
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        MenuBarRoot.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

        TreeControl.GotFocus += OnPaneFocusEvent;
        TreeControl.LostFocus += OnPaneFocusEvent;
        ResultHost.GotFocus += OnPaneFocusEvent;
        ResultHost.LostFocus += OnPaneFocusEvent;
        TreeControl.SelectionChanged += (_, _) => RefreshPaneFocus();
        TreeControl.ContextRequested += OnTreeContextRequested;
        ResultPaneBorder.RightTapped += OnResultRightTapped;
        MainGrid.SizeChanged += (_, _) => ApplyTreeWidth();
        ToolbarControl.Invoked += OnToolbarInvoked;
        ToolbarControl.VisibleButtonsChanged += (_, _) => UpdateToolbarBand();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        MenuBarRoot.PreviewKeyDown += OnMenuPreviewKeyDown;
        PreviewKeyDown += OnShellPreviewKeyDown;

        // A popup's keys can arrive here before the window hook sees them. Letters are offered to an open popup, and
        // handled events are included so an earlier handler cannot hide the key.
        AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnPopupPreviewKeyDown), handledEventsToo: true);

        ApplyMetrics();
        SetMenuExtensions(new ConsoleMenuExtensions());
    }

    /// <summary>Raised for a toolbar click on an enabled button, and for Alt+Left and Alt+Right (Back, Forward).</summary>
    public event EventHandler<ConsoleToolbarEventArgs>? ToolbarCommandInvoked;

    /// <summary>
    /// Raised for Shift+F10, the context-menu key, a right click in the result pane and a right click in the tree.
    /// The host shows its own menu.
    /// </summary>
    public event EventHandler<ConsoleContextMenuEventArgs>? ContextMenuRequested;

    /// <summary>Raised after the tree, a toolbar group or the status bar is shown or hidden.</summary>
    public event EventHandler? ViewChanged;

    /// <summary>Raised after the preferred tree width changed (a drag, or a set). The host persists it.</summary>
    public event EventHandler? TreePaneWidthChanged;

    /// <summary>The menu bar. The shell fills it from <see cref="SetMenu"/>.</summary>
    public MenuBar MenuBar => MenuBarRoot;

    /// <summary>The standard toolbar. The host sets enable states and adds its extension buttons here.</summary>
    public ConsoleToolbar Toolbar => ToolbarControl;

    /// <summary>The console tree control.</summary>
    public DenseTree TreeContent => TreeControl;

    /// <summary>The container of the tree pane.</summary>
    public FrameworkElement TreeHost => TreePaneBorder;

    /// <summary>The container of the result pane.</summary>
    public FrameworkElement ResultHostElement => ResultPaneBorder;

    /// <summary>The content of the result pane. Any element: the list, a page, or nothing.</summary>
    public object? ResultContent
    {
        get => ResultHost.Content;
        set => ResultHost.Content = value;
    }

    /// <summary>
    /// The element that takes the keyboard focus when the cycle reaches the result pane. When it is null the pane
    /// itself takes it, so an empty pane is still part of the cycle.
    /// </summary>
    public Control? ResultFocusTarget { get; set; }

    /// <summary>
    /// A hook for Tab and Shift+Tab while the focus is inside the result pane: the argument is true for Tab. The handler
    /// moves the focus to another stop of its own content and returns true, or returns false and the shell cycles to the
    /// next pane as usual. F6 and Shift+F6 always cycle the panes. Null (the default) leaves Tab to the pane cycle.
    /// </summary>
    public Func<bool, bool>? ResultPaneTabHandler { get; set; }

    /// <summary>The text of the status bar pane. Kept while the bar is hidden.</summary>
    public string StatusText
    {
        get => StatusBarControl.Text;
        set => StatusBarControl.Text = value;
    }

    /// <summary>The status bar control, for the host that needs more than the text.</summary>
    public ConsoleStatusBar StatusBar => StatusBarControl;

    /// <summary>
    /// The tree width the user chose, in DIPs, which the host persists and sets at startup. A set is clamped to the
    /// splitter range. A window too narrow for it shows a narrower tree without changing this value.
    /// </summary>
    public double TreePaneWidth
    {
        get => _preferredTreeWidth;
        set => SetPreferredTreeWidth(value);
    }

    /// <summary>The tree width in use in DIPs: the preferred width, limited by the window room. Zero while hidden.</summary>
    public double EffectiveTreePaneWidth => _treeVisible ? _effectiveTreeWidth : 0;

    /// <summary>Shows or hides the console tree and its splitter. Hiding moves tree focus to the result pane.</summary>
    public bool IsTreeVisible
    {
        get => _treeVisible;
        set
        {
            if (_treeVisible == value)
            {
                return;
            }

            if (!value && IsWithin(FocusedElement(), TreePaneBorder))
            {
                FocusResultPane();
            }

            _treeVisible = value;
            TreePaneBorder.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            SplitterThumb.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            SplitterColumn.Width = new GridLength(value ? SplitterWidth : 0);
            ApplyTreeWidth();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Shows or hides the standard toolbar, the eight standard buttons (the Standard toolbar switch of Customize View). The
    /// band that holds the toolbar is gone while no button of either group shows.
    /// </summary>
    public bool IsToolbarVisible
    {
        get => _toolbarVisible;
        set
        {
            if (_toolbarVisible == value)
            {
                return;
            }

            _toolbarVisible = value;
            ToolbarControl.StandardGroupVisible = value;
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Shows or hides the toolbars of the snap-in, the host's extension buttons (the Snap-in toolbars switch of Customize
    /// View). A button the host hides for a node stays hidden when the switch is on.
    /// </summary>
    public bool AreSnapInToolbarsVisible
    {
        get => _snapInToolbarsVisible;
        set
        {
            if (_snapInToolbarsVisible == value)
            {
                return;
            }

            _snapInToolbarsVisible = value;
            ToolbarControl.ExtensionGroupVisible = value;
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Shows or hides the status bar. Its text is kept.</summary>
    public bool IsStatusBarVisible
    {
        get => _statusBarVisible;
        set
        {
            if (_statusBarVisible == value)
            {
                return;
            }

            _statusBarVisible = value;
            StatusBarControl.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The pane that holds the keyboard focus.</summary>
    public ConsolePane FocusedPane
    {
        get
        {
            DependencyObject? focused = FocusedElement();
            if (IsWithin(focused, TreePaneBorder))
            {
                return ConsolePane.Tree;
            }

            return IsWithin(focused, ResultPaneBorder) ? ConsolePane.Result : ConsolePane.Other;
        }
    }

    /// <summary>One line for each frame menu entry that lost its access key to a host entry in the last menu set.</summary>
    public IReadOnlyList<string> MenuAccessKeyCollisions => _collisions;

    /// <summary>Replaces the menu with the frame's skeleton extended by the host's entries.</summary>
    public void SetMenuExtensions(ConsoleMenuExtensions extensions)
    {
        ConsoleMenuTable table = ConsoleMenu.Build(extensions);
        _collisions = table.Collisions;
        SetMenu(table.Commands);
    }

    /// <summary>
    /// Replaces the menu with a complete table (a host that does not want the skeleton). The menu keyboard is created
    /// again on the new items, so Alt and F10 reach them.
    /// </summary>
    public void SetMenu(IReadOnlyList<ClassicCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        DetachMenuKeyboard();
        ClassicMenuBuilder.Populate(MenuBarRoot, commands);
        ApplyMenuMetrics();
        if (IsLoaded && _topHwnd != 0)
        {
            CreateMenuKeyboard();
        }
    }

    /// <summary>Puts the keyboard focus in the console tree.</summary>
    public void FocusTree()
    {
        if (_treeVisible)
        {
            _ = TreeControl.Focus(FocusState.Keyboard);
        }
    }

    /// <summary>Puts the keyboard focus in the result pane: the focus target when there is one, else the pane.</summary>
    public void FocusResultPane()
    {
        _ = (ResultFocusTarget ?? ResultHost).Focus(FocusState.Keyboard);
    }

    /// <summary>
    /// Creates the menu keyboard router for a window, or stores the handle until the control is loaded. The shell
    /// finds the window itself when it loads, so a host calls this only for a window the shell cannot find.
    /// </summary>
    public void AttachMenuKeyboard(nint hwnd)
    {
        _topHwnd = hwnd;
        if (IsLoaded)
        {
            CreateMenuKeyboard();
        }
    }

    /// <summary>Removes the menu keyboard router. Safe when it was never created.</summary>
    public void DetachMenuKeyboard()
    {
        _menuKeyboard?.Dispose();
        _menuKeyboard = null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_metricsSubscribed)
        {
            DenseMetrics.Changed += OnMetricsChanged;
            _metricsSubscribed = true;
        }

        BwcTheme.Changed += OnThemeChanged;
        if (XamlRoot is { } root && !ReferenceEquals(_observedRoot, root))
        {
            if (_observedRoot is not null)
            {
                _observedRoot.Changed -= OnXamlRootChanged;
            }

            _observedRoot = root;
            _observedScale = root.RasterizationScale;
            root.Changed += OnXamlRootChanged;
        }

        RootGrid.RequestedTheme = BwcTheme.ElementThemeFor(BwcTheme.Current);
        ClassicMenuBrushes.Refresh();
        ApplyMetrics();
        ToolbarControl.RefreshImages();
        RefreshPaneFocus();
        if (_topHwnd == 0)
        {
            _topHwnd = FindOwnWindow();
        }

        if (_topHwnd != 0)
        {
            CreateMenuKeyboard();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_metricsSubscribed)
        {
            DenseMetrics.Changed -= OnMetricsChanged;
            _metricsSubscribed = false;
        }

        BwcTheme.Changed -= OnThemeChanged;
        if (_observedRoot is not null)
        {
            _observedRoot.Changed -= OnXamlRootChanged;
            _observedRoot = null;
        }

        DetachMenuKeyboard();
    }

    /// <summary>The window that hosts this control, from the content island of the XamlRoot. Zero when not found.</summary>
    private nint FindOwnWindow()
    {
        try
        {
            if (XamlRoot?.ContentIslandEnvironment is { } environment)
            {
                return Win32Interop.GetWindowFromWindowId(environment.AppWindowId);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UiDiagnostics.Warn?.Invoke($"The console shell could not find its window: {ex.Message}");
        }

        return 0;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        RootGrid.RequestedTheme = BwcTheme.ElementThemeFor(BwcTheme.Current);
        ClassicMenuBrushes.Refresh();
        StatusBarControl.ApplyBrushes();
        ToolbarControl.RefreshImages();
    }

    /// <summary>A new rasterization scale (the window moved to another monitor) needs images and menu widths again.</summary>
    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (Math.Abs(sender.RasterizationScale - _observedScale) < 0.001)
        {
            return;
        }

        _observedScale = sender.RasterizationScale;
        ToolbarControl.RefreshImages();
        ApplyMenuMetrics();
    }

    private void OnMetricsChanged(object? sender, EventArgs e) => ApplyMetrics();

    private void ApplyMetrics()
    {
        FontSize = DenseMetrics.FontSize;
        FontFamily = DenseMetrics.FontFamily;
        TreeControl.ApplyMetrics();
        StatusBarControl.ApplyMetrics();
        ApplyMenuMetrics();
        ApplyTreeWidth();
    }

    private void ApplyMenuMetrics()
    {
        MenuBarRoot.Height = MenuBarHeight;
        MenuBarRoot.FontSize = DenseMetrics.FontSize;
        MenuBarRoot.FontFamily = DenseMetrics.FontFamily;

        // Top-level widths come from the GDI label width at the window scale, as for the Registry Editor.
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        foreach (MenuBarItem item in MenuBarRoot.Items.OfType<MenuBarItem>())
        {
            item.Height = MenuBarHeight;
            item.FontSize = DenseMetrics.FontSize;
            item.FontFamily = DenseMetrics.FontFamily;
            double width = ClassicMenuMetrics.BarItemWidthDip(item.Title, scale);
            if (width > 0)
            {
                item.Width = width;
            }
        }

        foreach (MenuBarItem top in MenuBarRoot.Items.OfType<MenuBarItem>())
        {
            ApplyFlyoutWidth(top.Items, scale);
        }
    }

    /// <summary>Sizes a popup as wide as its widest visible item, as Win32 sizes a menu.</summary>
    private static void ApplyFlyoutWidth(IList<MenuFlyoutItemBase> items, double scale)
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
            else if (entry is MenuFlyoutSubItem sub)
            {
                labels.Add(sub.Text ?? string.Empty);
                accelerators.Add(string.Empty);
            }
        }

        double width = ClassicMenuMetrics.PopupItemWidthDip(labels, accelerators, scale);
        foreach (MenuFlyoutItemBase entry in items)
        {
            if (entry is MenuFlyoutItem or MenuFlyoutSubItem)
            {
                var element = (FrameworkElement)entry;
                if (width > 0)
                {
                    element.MinWidth = width;
                }

                ClassicMenuMetrics.ApplyPopupItemHeight(element, scale);
            }
        }

        foreach (MenuFlyoutSubItem sub in items.OfType<MenuFlyoutSubItem>())
        {
            ApplyFlyoutWidth(sub.Items, scale);
        }
    }

    // ---- splitter and widths ----------------------------------------------------------------------

    /// <summary>The widest the tree can be in the current window, never below the minimum tree width.</summary>
    private double AvailableTreeWidth()
    {
        double total = MainGrid.ActualWidth;
        return total <= 0 ? MaxTreePaneWidth : Math.Max(MinTreePaneWidth, total - SplitterWidth - MinResultPaneWidth);
    }

    private void ApplyTreeWidth()
    {
        if (!_treeVisible)
        {
            TreeColumn.Width = new GridLength(0);
            return;
        }

        _effectiveTreeWidth = Math.Min(_preferredTreeWidth, AvailableTreeWidth());
        TreeColumn.Width = new GridLength(_effectiveTreeWidth);
    }

    private void SetPreferredTreeWidth(double width)
    {
        double clamped = Math.Clamp(double.IsFinite(width) ? width : DefaultTreePaneWidth, MinTreePaneWidth, MaxTreePaneWidth);
        if (Math.Abs(clamped - _preferredTreeWidth) < 0.0001)
        {
            ApplyTreeWidth();
            return;
        }

        _preferredTreeWidth = clamped;
        ApplyTreeWidth();
        TreePaneWidthChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSplitterDragStarted(object sender, DragStartedEventArgs e)
    {
        _splitterDragging = true;
        _dragStartWidth = _preferredTreeWidth;

        // Escape cancels a drag through the key handling of this control, which only sees keys that reach it.
        _ = SplitterThumb.Focus(FocusState.Pointer);
    }

    private void OnSplitterDragDelta(object sender, DragDeltaEventArgs e)
    {
        // The change is added to the width in use, so a drag that starts from a clamped width does not jump.
        SetPreferredTreeWidth(Math.Min(_effectiveTreeWidth + e.HorizontalChange, AvailableTreeWidth()));
    }

    private void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _splitterDragging = false;
        if (e.Canceled)
        {
            SetPreferredTreeWidth(_dragStartWidth);
        }
    }

    private void OnSplitterLostFocus(object sender, RoutedEventArgs e)
    {
        _splitterDragging = false;
    }

    // ---- focus ------------------------------------------------------------------------------------

    private DependencyObject? FocusedElement() =>
        XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as DependencyObject : null;

    private static bool IsWithin(DependencyObject? element, DependencyObject pane)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, pane))
            {
                return true;
            }
        }

        return false;
    }

    private void OnPaneFocusEvent(object sender, RoutedEventArgs e)
    {
        // The focused element has not moved yet when the event arrives in some paths.
        _ = DispatcherQueue.TryEnqueue(RefreshPaneFocus);
    }

    /// <summary>Tells the theme which pane holds the focus, so the focus rectangle shows in that pane only.</summary>
    private void RefreshPaneFocus()
    {
        DependencyObject? focused = FocusedElement();
        bool tree = IsWithin(focused, TreePaneBorder);
        bool result = IsWithin(focused, ResultPaneBorder);
        BwcTheme.SetPaneState(BwcPane.Tree, tree, tree && TreeControl.SelectedNode is not null);
        BwcTheme.SetPaneState(BwcPane.List, result, false);
    }

    /// <summary>
    /// Tab and F6 move forward through the panes that are showing, Shift+Tab and Shift+F6 backward: tree, result pane.
    /// The toolbar and the splitter are outside the cycle. A key from the menu bar, or with no pane focused, starts
    /// at the tree (at the result pane going backward). With one pane showing the focus stays there.
    /// </summary>
    private void CyclePanes(bool forward)
    {
        var order = new List<ConsolePane>(2);
        if (_treeVisible)
        {
            order.Add(ConsolePane.Tree);
        }

        order.Add(ConsolePane.Result);

        DependencyObject? focused = FocusedElement();
        int current = -1;
        if (IsWithin(focused, SplitterThumb))
        {
            // The splitter counts as the tree going forward and as the result pane going backward.
            current = forward ? 0 : order.Count - 1;
        }
        else
        {
            current = order.FindIndex(pane => pane == ConsolePane.Tree
                ? IsWithin(focused, TreePaneBorder)
                : IsWithin(focused, ResultPaneBorder));
        }

        int next = current < 0
            ? (forward ? 0 : order.Count - 1)
            : ((current + (forward ? 1 : -1)) + order.Count) % order.Count;
        if (order[next] == ConsolePane.Tree)
        {
            FocusTree();
        }
        else
        {
            FocusResultPane();
        }
    }

    // ---- keys -------------------------------------------------------------------------------------

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void OnShellPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        bool alt = IsDown(VirtualKey.Menu);
        bool shift = IsDown(VirtualKey.Shift);
        bool control = IsDown(VirtualKey.Control);
        if (e.Key == VirtualKey.Tab && !alt && !control && FocusedPane == ConsolePane.Result && ResultPaneTabHandler is { } resultTab && resultTab(!shift))
        {
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Escape when _splitterDragging:
                e.Handled = true;
                SplitterThumb.CancelDrag();
                return;
            case VirtualKey.F6 when !alt && !control:
            case VirtualKey.Tab when !alt && !control:
                e.Handled = true;
                CyclePanes(forward: !shift);
                return;
            case VirtualKey.Left when alt && !control && !shift:
                RaiseKeyCommand(e, ConsoleToolbarIds.Back);
                return;
            case VirtualKey.Right when alt && !control && !shift:
                RaiseKeyCommand(e, ConsoleToolbarIds.Forward);
                return;
            case VirtualKey.Application when !alt && !control:
            case VirtualKey.F10 when shift && !alt && !control:
                e.Handled = true;
                RaiseContextFromKeyboard();
                return;
        }
    }

    /// <summary>Raises the command of a key only while its button is enabled; a disabled one leaves the key alone.</summary>
    private void RaiseKeyCommand(KeyRoutedEventArgs e, int id)
    {
        if (!ToolbarControl.IsButtonEnabled(id))
        {
            return;
        }

        e.Handled = true;
        ToolbarCommandInvoked?.Invoke(this, new ConsoleToolbarEventArgs(id));
    }

    private void RaiseContextFromKeyboard()
    {
        ConsolePane pane = FocusedPane;
        TreeNode? node = pane == ConsolePane.Tree ? TreeControl.SelectedNode : null;
        ContextMenuRequested?.Invoke(this, new ConsoleContextMenuEventArgs(pane, node, null, fromKeyboard: true));
    }

    private void OnTreeContextRequested(object? sender, TreeNodeContextRequestedEventArgs e)
    {
        Point position = e.Element.TransformToVisual(this).TransformPoint(e.Point);
        ContextMenuRequested?.Invoke(
            this,
            new ConsoleContextMenuEventArgs(ConsolePane.Tree, e.Node, position, fromKeyboard: false));
    }

    private void OnResultRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        e.Handled = true;
        ContextMenuRequested?.Invoke(
            this,
            new ConsoleContextMenuEventArgs(ConsolePane.Result, null, e.GetPosition(this), fromKeyboard: false));
    }

    private void UpdateToolbarBand() =>
        ToolbarBand.Visibility = ToolbarControl.HasVisibleButtons ? Visibility.Visible : Visibility.Collapsed;

    private void OnToolbarInvoked(object? sender, ConsoleToolbarEventArgs e)
    {
        if (e.Id == ConsoleToolbarIds.ShowHideConsoleTree)
        {
            IsTreeVisible = !IsTreeVisible;
        }

        ToolbarCommandInvoked?.Invoke(this, e);
    }

    private void CreateMenuKeyboard()
    {
        if (_menuKeyboard is not null || _topHwnd == 0)
        {
            return;
        }

        MenuBarItem[] items = MenuBarRoot.Items.OfType<MenuBarItem>().ToArray();
        ConfigureMenuArrowRing(items);
        _menuKeyboard = new ClassicMenuKeyboard(_topHwnd, items);

        // The island child HWNDs can appear after the first Loaded pass. Hook again once layout has settled so the
        // subclass sees the keyboard messages of those windows.
        _ = DispatcherQueue.TryEnqueue(() => _menuKeyboard?.HookTree(_topHwnd));
    }

    /// <summary>
    /// Left and Right wrap through XYFocus. Up and Down point at the item itself so XAML does not move focus away;
    /// Down is handled by the menu keyboard through HandleKeyFromXaml.
    /// </summary>
    private static void ConfigureMenuArrowRing(IReadOnlyList<MenuBarItem> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            MenuBarItem item = items[i];
            item.XYFocusLeft = items[(i + items.Count - 1) % items.Count];
            item.XYFocusRight = items[(i + 1) % items.Count];
            item.XYFocusUp = item;
            item.XYFocusDown = item;
        }
    }

    private void OnMenuPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_menuKeyboard is not null && _menuKeyboard.HandleKeyFromXaml(e.Key))
        {
            e.Handled = true;
        }
    }

    private void OnPopupPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!e.Handled && _menuKeyboard is not null && _menuKeyboard.HandlePopupKey(e.Key))
        {
            e.Handled = true;
        }
    }
}

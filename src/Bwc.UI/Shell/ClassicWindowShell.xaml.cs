using Bwc.UI.List;
using Bwc.UI.Menu;
using Bwc.UI.Theme;
using Bwc.UI.Tree;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace Bwc.UI.Shell;

/// <summary>Text submitted from the address bar with Enter. The shell does not navigate itself.</summary>
public sealed class AddressBarSubmittedEventArgs : EventArgs
{
    public AddressBarSubmittedEventArgs(string text)
    {
        Text = text ?? string.Empty;
    }

    /// <summary>The text exactly as it was in the address bar when Enter was pressed.</summary>
    public string Text { get; }
}

/// <summary>
/// Classic Registry Editor window chrome: menu bar, optional address bar, tree pane, splitter,
/// and list pane with a header. The shell carries no status bar, matching the oracle.
/// </summary>
public sealed partial class ClassicWindowShell : UserControl
{
    private const double MinTreePaneWidth = 120;
    private const double MaxTreePaneWidth = 600;
    private const double KeyboardSplitStep = 8;

    // Oracle command id of View > Address Bar (RT_MENU 103, id 668).
    private const int AddressBarCommandId = 668;

    private double _treePaneWidth = DenseMetrics.TreePaneWidth;
    private ClassicMenuKeyboard? _menuKeyboard;
    private nint _topHwnd;
    private bool _metricsSubscribed;
    private bool _keyboardSplitMode;
    private bool _splitterDragging;
    private double _splitterDragStartWidth;
    private string _currentPath = "Computer";

    // The pane that held the focus last, and the tree width when keyboard split mode began. Enter and
    // Escape return the focus to that pane, and Escape puts back the width.
    private Pane _lastPane = Pane.Tree;
    private Pane _paneBeforeSplit = Pane.Tree;
    private double _widthBeforeSplit = DenseMetrics.TreePaneWidth;

    // Menu items whose visibility is already watched for a popup width. Each item is watched once.
    private readonly HashSet<FrameworkElement> _observedEntries = [];

    /// <summary>The panes in the classic Tab order. The address bar joins the cycle only while it is visible.</summary>
    private enum Pane
    {
        Tree,
        List,
        Address,
    }

    public ClassicWindowShell()
    {
        InitializeComponent();
        AutomationProperties.SetName(this, BwcStrings.Get("ShellRootName"));
        AutomationProperties.SetName(AddressBox, BwcStrings.Get("ShellAddressBarName"));
        AutomationProperties.SetName(TreePaneBorder, BwcStrings.Get("ShellTreePaneName"));
        AutomationProperties.SetName(ListPaneBorder, BwcStrings.Get("ShellListPaneName"));
        AutomationProperties.SetName(SplitterThumb, BwcStrings.Get("ShellSplitterName"));
        _currentPath = AddressBox.Text;
        ApplyAddressBarPalette();
        ApplyMetrics();

        // The palette is applied here on the UI thread, after the Dense.xaml brushes exist.
        BwcTheme.EnsureInitialized();
        RequestedTheme = BwcTheme.ElementThemeFor(BwcTheme.Current);
        ClassicMenuBrushes.Refresh();

        // Fallback for the WinRT theme events, which an unpackaged app can be refused. A change of
        // the effective Windows theme changes this element's ActualTheme, so the palette re-reads.
        ActualThemeChanged += (_, _) => BwcTheme.RefreshSystem();

        // The system focus visual draws a rounded rectangle around the whole pane. The focus
        // rectangle is drawn on the focused row instead, so the system visual is switched off.
        TreeControl.UseSystemFocusVisuals = false;
        ListControl.UseSystemFocusVisuals = false;

        // The list header band is outside DetailsHeaderPanel and keeps the Fluent layer color
        // unless it is set here. It uses the window brush, like the oracle header.
        if (ListControl.FindName("HeaderHost") is Border headerHost)
        {
            headerHost.Background = BwcBrushes.Window;
        }

        TreeControl.GotFocus += OnPaneFocusEvent;
        TreeControl.LostFocus += OnPaneFocusEvent;
        ListControl.GotFocus += OnPaneFocusEvent;
        ListControl.LostFocus += OnPaneFocusEvent;
        TreeControl.SelectionChanged += (_, _) => RefreshPaneFocus();
        ListControl.SelectionChanged += (_, _) => RefreshPaneFocus();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        MenuBarRoot.PreviewKeyDown += OnMenuPreviewKeyDown;
        PreviewKeyDown += OnShellNavigationKeyDown;
        RegisterAddressBarAccelerators();

        // A popup's keys can arrive here before the window hook sees them. Letters are offered to an
        // open popup, and handled events are included so an earlier handler cannot hide the key.
        AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnShellPreviewKeyDown), handledEventsToo: true);
    }

    /// <summary>
    /// The address bar is flat and classic: the Fluent corner radius is removed, and the border and the
    /// background come from the palette, so focus does not add the accent line.
    /// </summary>
    private void ApplyAddressBarPalette()
    {
        foreach (var key in new[] { "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
        {
            AddressBox.Resources[key] = BwcBrushes.Shadow;
        }

        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused" })
        {
            AddressBox.Resources[key] = BwcBrushes.Window;
        }
    }

    /// <summary>Ctrl+L and Alt+D both focus the address bar (ACC-013 and ACC-014, command 672).</summary>
    private void RegisterAddressBarAccelerators()
    {
        AddAddressBarAccelerator(VirtualKey.L, VirtualKeyModifiers.Control);
        AddAddressBarAccelerator(VirtualKey.D, VirtualKeyModifiers.Menu);
    }

    private void AddAddressBarAccelerator(VirtualKey key, VirtualKeyModifiers modifiers)
    {
        var accelerator = new KeyboardAccelerator
        {
            Key = key,
            Modifiers = modifiers,
            ScopeOwner = this,
        };
        accelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            FocusAddressBar();
        };
        KeyboardAccelerators.Add(accelerator);
    }

    /// <summary>The menu bar. The host application fills it from its command table.</summary>
    public MenuBar MenuBar => MenuBarRoot;

    /// <summary>The address bar text box.</summary>
    public TextBox AddressBar => AddressBox;

    /// <summary>
    /// The last committed display path. Editing the text box does not change this value; Enter
    /// raises AddressBarSubmitted and the host commits a new path through this property.
    /// </summary>
    public string CurrentPath
    {
        get => _currentPath;
        set
        {
            _currentPath = value ?? string.Empty;
            if (!string.Equals(AddressBox.Text, _currentPath, StringComparison.Ordinal))
            {
                AddressBox.Text = _currentPath;
            }
        }
    }

    /// <summary>Raised when Enter is pressed in the address bar. The shell does not navigate.</summary>
    public event EventHandler<AddressBarSubmittedEventArgs>? AddressBarSubmitted;

    /// <summary>The tree pane container.</summary>
    public FrameworkElement TreeHost => TreePaneBorder;

    /// <summary>The list pane container.</summary>
    public FrameworkElement ListHost => ListPaneBorder;

    /// <summary>The dense tree control inside the tree pane.</summary>
    public DenseTree TreeContent => TreeControl;

    /// <summary>The details list control inside the list pane.</summary>
    public DetailsList ListContent => ListControl;

    /// <summary>True while the address bar row is showing.</summary>
    public bool IsAddressBarVisible { get; private set; } = true;

    /// <summary>Raised after the address bar is shown or hidden.</summary>
    public event EventHandler? AddressBarVisibilityChanged;

    /// <summary>Shows or hides the address bar and raises AddressBarVisibilityChanged.</summary>
    public void ToggleAddressBar()
    {
        SetAddressBarVisible(!IsAddressBarVisible);
    }

    /// <summary>
    /// The tree pane width in DIPs. A drag or the Left and Right keys change it, and a value set here is
    /// clamped to the same range as the splitter. The host reads it at close and sets it at startup.
    /// </summary>
    public double TreePaneWidth
    {
        get => _treePaneWidth;
        set => SetTreeWidth(value);
    }

    /// <summary>
    /// Puts keyboard focus on the splitter. Left and Right move the tree pane by 8 DIP.
    /// Enter or Escape leaves keyboard mode and focuses the shell.
    /// </summary>
    public void BeginSplitKeyboardMode()
    {
        _keyboardSplitMode = true;
        _paneBeforeSplit = _lastPane;
        _widthBeforeSplit = _treePaneWidth;

        // The splitter keeps IsTabStop=True: WinUI refuses explicit focus on a Thumb that is not a tab stop. Tab and
        // F6 skip it, because OnShellNavigationKeyDown owns that cycle, so the request below is what focuses it.
        _ = SplitterThumb.Focus(FocusState.Programmatic);

        // A View menu click closes the flyout after the Click handler, and the flyout returns focus to the
        // menu bar when it closes. Low priority runs after that close, so the splitter keeps the focus.
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_keyboardSplitMode)
            {
                _ = SplitterThumb.Focus(FocusState.Programmatic);
            }
        });
    }

    /// <summary>
    /// Creates the menu keyboard router once the shell is loaded, or stores the handle until the
    /// Loaded event. Safe to call more than once.
    /// </summary>
    public void AttachMenuKeyboard(nint hwnd)
    {
        _topHwnd = hwnd;
        if (IsLoaded)
        {
            CreateMenuKeyboard();
        }
    }

    /// <summary>Removes the menu keyboard router. Safe to call when it was never created.</summary>
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
        RequestedTheme = BwcTheme.ElementThemeFor(BwcTheme.Current);
        ClassicMenuBrushes.Refresh();
        ApplyMetrics();
        SyncAddressBarCheck();
        RefreshPaneFocus();
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
        DetachMenuKeyboard();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        RequestedTheme = BwcTheme.ElementThemeFor(BwcTheme.Current);
        ClassicMenuBrushes.Refresh();
    }

    /// <summary>
    /// Focus events arrive before the focused element has moved in some paths, so the read of the
    /// focused element is deferred to the dispatcher queue.
    /// </summary>
    private void OnPaneFocusEvent(object sender, RoutedEventArgs e)
    {
        _ = DispatcherQueue.TryEnqueue(RefreshPaneFocus);
    }

    /// <summary>
    /// Reports which pane has keyboard focus to the theme. A pane has focus when the focused element
    /// is inside its pane border, so a rename box inside the tree still counts as the tree. The
    /// focus rectangle is shown only with a selected tree row, or with exactly one selected list row.
    /// </summary>
    private void RefreshPaneFocus()
    {
        DependencyObject? focused = XamlRoot is { } root
            ? FocusManager.GetFocusedElement(root) as DependencyObject
            : null;

        bool tree = IsWithin(focused, TreePaneBorder);
        bool list = IsWithin(focused, ListPaneBorder);
        if (tree)
        {
            _lastPane = Pane.Tree;
        }
        else if (list)
        {
            _lastPane = Pane.List;
        }
        else if (IsWithin(focused, AddressBox))
        {
            _lastPane = Pane.Address;
        }

        BwcTheme.SetPaneState(BwcPane.Tree, tree, tree && TreeControl.SelectedNode is not null);
        BwcTheme.SetPaneState(BwcPane.List, list, list && ListControl.SelectedRows.Count == 1);
    }

    private static bool IsWithin(DependencyObject? element, DependencyObject pane)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, pane))
            {
                return true;
            }
        }

        return false;
    }

    private void OnMetricsChanged(object? sender, EventArgs e)
    {
        ApplyMetrics();
    }

    private void ApplyMetrics()
    {
        FontSize = DenseMetrics.FontSize;
        FontFamily = DenseMetrics.FontFamily;

        MenuBarRoot.Height = DenseMetrics.MenuBarHeight;
        MenuBarRoot.FontSize = DenseMetrics.FontSize;
        MenuBarRoot.FontFamily = DenseMetrics.FontFamily;

        AddressBox.Height = DenseMetrics.AddressBarHeight;
        AddressBox.MinHeight = DenseMetrics.AddressBarHeight;
        AddressBox.FontSize = DenseMetrics.FontSize;
        AddressBox.FontFamily = DenseMetrics.FontFamily;

        TreeControl.ApplyMetrics();
        ListControl.ApplyMetrics();

        // Top-level widths come from the GDI label width at the window scale, so they match the oracle.
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        foreach (var item in MenuBarRoot.Items.OfType<MenuBarItem>())
        {
            item.Height = DenseMetrics.MenuBarHeight;
            item.FontSize = DenseMetrics.FontSize;
            item.FontFamily = DenseMetrics.FontFamily;
            double width = ClassicMenuMetrics.BarItemWidthDip(item.Title, scale);
            if (width > 0)
            {
                item.Width = width;
            }
        }

        ApplyPopupWidths(MenuBarRoot.Items.OfType<MenuBarItem>(), scale);

        AddressRow.Height = IsAddressBarVisible ? GridLength.Auto : new GridLength(0);
        SetTreeWidth(_treePaneWidth);
    }

    /// <summary>
    /// Sizes every popup the way Win32 sizes a menu: as wide as its widest visible item. Every leaf and submenu
    /// item of a popup takes that width as its MinWidth, so the flyout presenter takes it too. A collapsed item
    /// does not count, as in the classic menu, and a change of its visibility sizes the popup again.
    /// </summary>
    private void ApplyPopupWidths(IEnumerable<MenuBarItem> tops, double scale)
    {
        foreach (var top in tops)
        {
            ApplyFlyoutWidth(top.Items, scale);
        }
    }

    /// <summary>Sizes the popups again from the current visibility of their items.</summary>
    private void RefreshPopupWidths()
    {
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        ApplyPopupWidths(MenuBarRoot.Items.OfType<MenuBarItem>(), scale);
    }

    private void ApplyFlyoutWidth(IList<MenuFlyoutItemBase> items, double scale)
    {
        var labels = new List<string>();
        var accelerators = new List<string>();
        foreach (var entry in items)
        {
            if (entry is not (MenuFlyoutItem or MenuFlyoutSubItem) || ((FrameworkElement)entry).Visibility != Visibility.Visible)
            {
                continue;
            }

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
        if (width > 0)
        {
            foreach (var entry in items)
            {
                if (entry is MenuFlyoutItem or MenuFlyoutSubItem)
                {
                    var element = (FrameworkElement)entry;
                    element.MinWidth = width;
                    ObserveEntryVisibility(element);
                }
            }
        }

        foreach (var sub in items.OfType<MenuFlyoutSubItem>())
        {
            ApplyFlyoutWidth(sub.Items, scale);
        }
    }

    /// <summary>Watches an item's visibility once, so a value item that appears or hides resizes its popup.</summary>
    private void ObserveEntryVisibility(FrameworkElement entry)
    {
        if (!_observedEntries.Add(entry))
        {
            return;
        }

        _ = entry.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => RefreshPopupWidths());
    }

    private void SetAddressBarVisible(bool visible)
    {
        if (IsAddressBarVisible == visible)
        {
            return;
        }

        IsAddressBarVisible = visible;
        AddressBox.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        AddressRow.Height = visible ? GridLength.Auto : new GridLength(0);
        if (!visible && IsWithin(FocusedElement(), AddressBox))
        {
            _ = TreeControl.Focus(FocusState.Keyboard);
        }

        SyncAddressBarCheck();
        AddressBarVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows the address bar when it is hidden, then puts keyboard focus in it with its text selected.</summary>
    private void FocusAddressBar()
    {
        if (!IsAddressBarVisible)
        {
            SetAddressBarVisible(true);
        }

        _ = AddressBox.Focus(FocusState.Keyboard);
        AddressBox.SelectAll();
    }

    private DependencyObject? FocusedElement()
    {
        return XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as DependencyObject : null;
    }

    /// <summary>
    /// Puts a check mark on View &gt; Address Bar while the address bar is showing. The item is a
    /// ToggleMenuFlyoutItem; its check glyph sits in the reserved gutter, so the label does not move.
    /// </summary>
    private void SyncAddressBarCheck()
    {
        foreach (var top in MenuBarRoot.Items.OfType<MenuBarItem>())
        {
            SetCheck(top.Items, AddressBarCommandId, IsAddressBarVisible);
        }
    }

    private static void SetCheck(IList<MenuFlyoutItemBase> items, int commandId, bool isChecked)
    {
        foreach (var entry in items)
        {
            if (entry is ToggleMenuFlyoutItem toggle && toggle.Tag is int id && id == commandId)
            {
                toggle.IsChecked = isChecked;
            }

            if (entry is MenuFlyoutSubItem subItem)
            {
                SetCheck(subItem.Items, commandId, isChecked);
            }
        }
    }

    private void CreateMenuKeyboard()
    {
        if (_menuKeyboard is not null || _topHwnd == 0)
        {
            return;
        }

        var items = MenuBarRoot.Items.OfType<MenuBarItem>().ToArray();
        ConfigureMenuArrowRing(items);
        _menuKeyboard = new ClassicMenuKeyboard(_topHwnd, items);

        // The island child HWNDs can appear after the first Loaded pass. Hook again once layout
        // has settled so the subclass sees keyboard messages from those windows.
        _ = DispatcherQueue.TryEnqueue(() => _menuKeyboard?.HookTree(_topHwnd));
    }

    /// <summary>
    /// Left and Right wrap through XYFocus. Up and Down point at the item itself so XAML does not
    /// move focus away; Down is handled by ClassicMenuKeyboard through HandleKeyFromXaml.
    /// </summary>
    private static void ConfigureMenuArrowRing(IReadOnlyList<MenuBarItem> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
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

    private void OnShellPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!e.Handled && _menuKeyboard is not null && _menuKeyboard.HandlePopupKey(e.Key))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Enter publishes the text for the host to navigate. Escape discards the edit, restores the
    /// last committed path, and returns keyboard focus to the tree.
    /// </summary>
    private void OnAddressBarKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                AddressBarSubmitted?.Invoke(this, new AddressBarSubmittedEventArgs(AddressBox.Text));
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                AddressBox.Text = _currentPath;
                _ = TreeControl.Focus(FocusState.Keyboard);
                break;
        }
    }

    /// <summary>
    /// Tab and F6 move through the panes in the classic order: tree, list, and the address bar when it is
    /// visible. Shift+Tab goes the other way. The splitter is not in the cycle, so Tab from it goes to the
    /// list and Shift+Tab to the tree. Escape during a mouse drag of the splitter cancels the drag.
    /// </summary>
    private void OnShellNavigationKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        if (e.Key == VirtualKey.Escape && _splitterDragging)
        {
            e.Handled = true;
            SplitterThumb.CancelDrag();
            return;
        }

        if (e.Key != VirtualKey.Tab && e.Key != VirtualKey.F6)
        {
            return;
        }

        // Focus on the menu bar is outside the cycle, so Tab from it starts the cycle at the tree (Shift+Tab at the address bar).
        // Handling the key here keeps XAML's own tab order, which would reach the splitter, out of the route.
        var focused = FocusedElement();
        bool backward = e.Key == VirtualKey.Tab && IsShiftDown();
        e.Handled = true;
        if (IsWithin(focused, SplitterThumb))
        {
            FocusPane(backward ? Pane.Tree : Pane.List);
            return;
        }

        var order = IsAddressBarVisible
            ? new[] { Pane.Tree, Pane.List, Pane.Address }
            : new[] { Pane.Tree, Pane.List };
        int count = order.Length;
        int index = PaneIndex(focused, order) ?? (backward ? count : -1);
        int next = backward ? (index - 1 + count) % count : (index + 1) % count;
        FocusPane(order[next]);
    }

    private static bool IsShiftDown()
    {
        return InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
    }

    /// <summary>The position of the pane that holds the focus in the cycle, or null when no cycle pane has it.</summary>
    private int? PaneIndex(DependencyObject? focused, IReadOnlyList<Pane> order)
    {
        for (var i = 0; i < order.Count; i++)
        {
            bool holds = order[i] switch
            {
                Pane.Tree => IsWithin(focused, TreePaneBorder),
                Pane.List => IsWithin(focused, ListPaneBorder),
                _ => IsWithin(focused, AddressBox),
            };
            if (holds)
            {
                return i;
            }
        }

        return null;
    }

    private void FocusPane(Pane pane)
    {
        switch (pane)
        {
            case Pane.Tree:
                _ = TreeControl.Focus(FocusState.Keyboard);
                break;
            case Pane.List:
                _ = ListControl.Focus(FocusState.Keyboard);
                break;
            default:
                // A hidden address bar cannot take focus, so the focus goes to the tree.
                if (IsAddressBarVisible)
                {
                    _ = AddressBox.Focus(FocusState.Keyboard);
                }
                else
                {
                    _ = TreeControl.Focus(FocusState.Keyboard);
                }

                break;
        }
    }

    private void OnSplitterDragStarted(object sender, DragStartedEventArgs e)
    {
        _keyboardSplitMode = false;
        _splitterDragging = true;

        // Escape cancels a drag through the shell's key handling, which only sees keys that reach the shell's
        // XAML tree. A press that leaves the keyboard focus outside the shell would lose the Escape.
        _ = SplitterThumb.Focus(FocusState.Pointer);
        _splitterDragStartWidth = _treePaneWidth;
    }

    /// <summary>A canceled drag (Escape) puts back the split that was in effect when the drag started.</summary>
    private void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _splitterDragging = false;
        if (e.Canceled)
        {
            SetTreeWidth(_splitterDragStartWidth);
        }
    }

    private void OnSplitterDragDelta(object sender, DragDeltaEventArgs e)
    {
        SetTreeWidth(_treePaneWidth + e.HorizontalChange);
    }

    private void OnSplitterKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Left:
                SetTreeWidth(_treePaneWidth - KeyboardSplitStep);
                e.Handled = true;
                break;
            case VirtualKey.Right:
                SetTreeWidth(_treePaneWidth + KeyboardSplitStep);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                e.Handled = true;
                EndSplitKeyboardMode(restoreWidth: false);
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                EndSplitKeyboardMode(restoreWidth: true);
                break;
        }
    }

    /// <summary>
    /// Leaves keyboard split mode and returns the focus to the pane that had it before the split. Escape
    /// also puts back the tree width from before the split began; Enter keeps the width.
    /// </summary>
    private void EndSplitKeyboardMode(bool restoreWidth)
    {
        _keyboardSplitMode = false;
        if (restoreWidth)
        {
            SetTreeWidth(_widthBeforeSplit);
        }

        FocusPane(_paneBeforeSplit);
    }

    private void OnSplitterLostFocus(object sender, RoutedEventArgs e)
    {
        _keyboardSplitMode = false;
    }

    private void SetTreeWidth(double width)
    {
        _treePaneWidth = Math.Clamp(width, MinTreePaneWidth, MaxTreePaneWidth);
        TreeColumn.Width = new GridLength(_treePaneWidth);
    }
}

using Bwc.UI.List;
using Bwc.UI.Menu;
using Bwc.UI.Theme;
using Bwc.UI.Tree;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

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

    private double _treePaneWidth = DenseMetrics.TreePaneWidth;
    private ClassicMenuKeyboard? _menuKeyboard;
    private nint _topHwnd;
    private bool _metricsSubscribed;
    private bool _keyboardSplitMode;
    private string _currentPath = "Computer";

    public ClassicWindowShell()
    {
        InitializeComponent();
        _currentPath = AddressBox.Text;
        ApplyMetrics();

        // The palette is applied here on the UI thread, after the Dense.xaml brushes exist.
        BwcTheme.EnsureInitialized();
        RequestedTheme = BwcTheme.ElementThemeFor(BwcTheme.Current);

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
    /// Puts keyboard focus on the splitter. Left and Right move the tree pane by 8 DIP.
    /// Enter or Escape leaves keyboard mode and focuses the shell.
    /// </summary>
    public void BeginSplitKeyboardMode()
    {
        _keyboardSplitMode = true;
        _ = SplitterThumb.Focus(FocusState.Keyboard);

        // A View menu click closes the flyout after the Click handler. Focus again after that
        // close so the flyout cannot take focus back from the splitter.
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (_keyboardSplitMode)
            {
                _ = SplitterThumb.Focus(FocusState.Keyboard);
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
        ApplyMetrics();
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

        foreach (var item in MenuBarRoot.Items.OfType<MenuBarItem>())
        {
            item.Height = DenseMetrics.MenuBarHeight;
            item.FontSize = DenseMetrics.FontSize;
            item.FontFamily = DenseMetrics.FontFamily;
        }

        AddressRow.Height = IsAddressBarVisible ? GridLength.Auto : new GridLength(0);
        SetTreeWidth(_treePaneWidth);
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
        AddressBarVisibilityChanged?.Invoke(this, EventArgs.Empty);
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

    private void OnSplitterDragStarted(object sender, DragStartedEventArgs e)
    {
        _keyboardSplitMode = false;
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
            case VirtualKey.Escape:
                _keyboardSplitMode = false;
                _ = Focus(FocusState.Programmatic);
                e.Handled = true;
                break;
        }
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

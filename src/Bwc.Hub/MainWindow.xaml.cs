using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Bwc.Core;
using Bwc.Hub.Theme;
using Bwc.Hub.Tools;
using Bwc.Hub.Updates;
using Bwc.UI;
using Bwc.UI.Menu;
using Bwc.UI.Theme;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace Bwc.Hub;

/// <summary>
/// The Hub window: a classic menu bar, a dense tool list with Name, Description, and Status
/// columns, a Launch button, and a status line. Menus are in MainWindow.Menus.cs and the update
/// flows are in MainWindow.Updates.cs.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const string ToolId = "hub";
    private const int DefaultWidthDip = 600;
    private const int MinimumWidth = 420;
    private const int MinimumHeight = 160;
    private const double ListFrameBorderDip = 2;
    private const double ListHeaderDividerDip = 1;
    private const int MaxSizingAttempts = 20;
    private const double IconDip = 16;
    private const double IconColumnDip = 24;
    private const double NameColumnDip = 170;
    private const double StatusColumnDip = 110;
    private const double FallbackFontSize = 12;

    private readonly SettingsStore<HubSettings> _settingsStore;
    private readonly UpdateService _updates = new();
    private readonly Dictionary<string, RowParts> _rows = new(StringComparer.Ordinal);
    private readonly nint _hwnd;
    private HubSettings _settings = new();
    private HubVersion _version = new("unknown", null);
    private ClassicMenuKeyboard? _menuKeyboard;
    private bool _initialSizeApplied;
    private bool _startupCheckQueued;
    private WindowBounds? _normalBounds;

    /// <summary>The check that is running now, if any, with its cancel source. Null when no check runs.</summary>
    private Task<UpdateCheckResult>? _activeCheck;
    private CancellationTokenSource? _activeCancel;

    /// <summary>Set when a manual check waits on the running check, which then leaves its result to that wait.</summary>
    private bool _activeCheckHandedOver;

    public MainWindow()
    {
        _settingsStore = new SettingsStore<HubSettings>(
            AppPaths.SettingsFile(ToolId),
            HubSettingsContext.Default.HubSettings);
        _settings = LoadSettings();

        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        CrashHandler.SetOwnerWindow(_hwnd);

        Title = HubStrings.Get("ProductName");
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "bwc.ico"));

        _version = HubVersion.Resolve(typeof(MainWindow).Assembly);
        VersionText.Text = HubStrings.Format("StatusVersion", _version.Display);
        StatusText.Text = HubStrings.Get("StatusReady");

        ApplyTheme();
        BwcTheme.Changed += OnThemeChanged;

        // HubRoot is pinned to the palette, so a system theme change is seen here, on the unpinned probe.
        ThemeProbe.ActualThemeChanged += (_, _) => BwcTheme.RefreshSystem();

        ConfigureListBrushes();
        ToolList.Header = BuildColumnHeader();
        foreach (ToolCatalog.Tool tool in HubTools.All)
        {
            ToolList.Items.Add(BuildRow(tool));
        }

        BuildMenuBar();
        RestoreWindowBounds();

        HubRoot.Loaded += OnHubRootLoaded;
        AppWindow.Closing += OnAppWindowClosing;
        AppWindow.Changed += OnAppWindowChanged;
        Closed += OnClosed;
        ToolList.SelectionChanged += OnSelectionChanged;
        // The item consumes Enter before the list sees it, so this handler also receives handled events.
        ToolList.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnToolListKeyDown), handledEventsToo: true);
        ToolList.DoubleTapped += OnToolListDoubleTapped;
        LaunchButton.Content = HubStrings.Get("LaunchButton");
        LaunchButton.AccessKey = "L";
        LaunchButton.Click += OnLaunchClicked;
        LaunchButton.IsEnabled = false;

        Log.Info("Hub window opened. Version " + _version.Display + ".");
    }

    private static double DenseFontSize
    {
        get
        {
            if (Application.Current.Resources.TryGetValue("BwcFontSize", out object? value) &&
                value is double size)
            {
                return size;
            }

            return FallbackFontSize;
        }
    }

    /// <summary>The tool rows, keyed by tool id. Selection and status changes update these parts.</summary>
    private sealed record RowParts(ListViewItem Item, Grid Content, TextBlock Name, TextBlock Description, TextBlock Status, Image Icon);

    private void ConfigureListBrushes()
    {
        // The list item template reads these theme keys. The Hub maps them to the Dense theme
        // brushes so selection looks like the Registry Editor list. The brushes update in place.
        HubRoot.Resources["ListViewItemBackgroundSelected"] = HubBrushes.Selection;
        HubRoot.Resources["ListViewItemBackgroundSelectedPointerOver"] = HubBrushes.Selection;
        HubRoot.Resources["ListViewItemBackgroundSelectedPressed"] = HubBrushes.Selection;
        HubRoot.Resources["ListViewItemBackgroundSelectedDisabled"] = HubBrushes.Selection;
        HubRoot.Resources["ListViewItemForegroundSelected"] = HubBrushes.SelectionText;
        HubRoot.Resources["ListViewItemForegroundSelectedPointerOver"] = HubBrushes.SelectionText;
        HubRoot.Resources["ListViewItemBackgroundPointerOver"] = HubBrushes.Face;
        HubRoot.Resources["ListViewItemBackgroundPressed"] = HubBrushes.Face;
    }

    private UIElement BuildColumnHeader()
    {
        var header = new Grid
        {
            Background = HubBrushes.Face,
            Height = DenseMetrics.HeaderHeight,
        };
        AutomationProperties.SetAutomationId(header, "ToolListHeader");
        ConfigureColumns(header);

        header.Children.Add(HeaderCell(HubStrings.Get("ColumnName"), 1, showDivider: true));
        header.Children.Add(HeaderCell(HubStrings.Get("ColumnDescription"), 2, showDivider: true));
        header.Children.Add(HeaderCell(HubStrings.Get("ColumnStatus"), 3, showDivider: false));

        var border = new Border
        {
            Child = header,
            BorderBrush = HubBrushes.Shadow,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        return border;
    }

    private Border HeaderCell(string text, int column, bool showDivider)
    {
        var cell = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = DenseFontSize,
            Foreground = HubBrushes.WindowText,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var border = new Border
        {
            Child = cell,
            BorderBrush = HubBrushes.Shadow,
            BorderThickness = new Thickness(0, 0, showDivider ? 1 : 0, 0),
        };
        Grid.SetColumn(border, column);
        return border;
    }

    private static void ConfigureColumns(Grid grid)
    {
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(IconColumnDip) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NameColumnDip) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(StatusColumnDip) });
    }

    private ListViewItem BuildRow(ToolCatalog.Tool tool)
    {
        var content = new Grid
        {
            Height = DenseMetrics.ListRowHeight,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ConfigureColumns(content);

        var icon = new Image
        {
            Width = IconDip,
            Height = IconDip,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),

        };
        Grid.SetColumn(icon, 0);
        AutomationProperties.SetAutomationId(icon, "ToolIcon_" + tool.Id);

        var name = CellText(tool.DisplayName);
        Grid.SetColumn(name, 1);
        var description = CellText(HubTools.Description(tool));
        Grid.SetColumn(description, 2);
        var status = CellText(HubTools.StatusText(tool));
        Grid.SetColumn(status, 3);

        content.Children.Add(icon);
        content.Children.Add(name);
        content.Children.Add(description);
        content.Children.Add(status);

        var item = new ListViewItem
        {
            Content = content,
            Tag = tool.Id,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            MinHeight = 0,
            MinWidth = 0,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            UseSystemFocusVisuals = false,
        };
        AutomationProperties.SetAutomationId(item, HubTools.RowAutomationId(tool));
        AutomationProperties.SetName(item, tool.DisplayName);

        _rows[tool.Id] = new RowParts(item, content, name, description, status, icon);
        _ = LoadIconAsync(tool, icon);
        return item;
    }

    private static TextBlock CellText(string text)
    {
        return new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = DenseFontSize,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = HubBrushes.WindowText,
        };
    }

    private static async Task LoadIconAsync(ToolCatalog.Tool tool, Image icon)
    {
        ImageSource? source = await HubTools.LoadIconAsync(tool, (int)IconDip);
        if (source is not null)
        {
            icon.Source = source;
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (RowParts row in _rows.Values)
        {
            bool selected = ReferenceEquals(row.Item, ToolList.SelectedItem);
            row.Content.Background = selected ? HubBrushes.Selection : null;
            Brush text = selected ? HubBrushes.SelectionText : HubBrushes.WindowText;
            row.Name.Foreground = text;
            row.Description.Foreground = text;
            row.Status.Foreground = text;
        }

        LaunchButton.IsEnabled = SelectedTool() is not null;
    }

    private void OnToolListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;

        // A held Enter repeats the key down. Only the first press launches, so one hold starts one tool.
        if (e.KeyStatus.WasKeyDown)
        {
            return;
        }

        LaunchSelected();
    }

    private void OnToolListDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Only a tap on a tool row launches. The header, the empty area, and the scroll bar are not rows.
        ListViewItem? item = RowUnder(e.OriginalSource as DependencyObject);
        if (item is null)
        {
            return;
        }

        ToolList.SelectedItem = item;
        LaunchSelected();
    }

    /// <summary>The tool row that holds the element, or null when the element is outside every row.</summary>
    private ListViewItem? RowUnder(DependencyObject? source)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ListViewItem item)
            {
                return item;
            }

            if (ReferenceEquals(current, ToolList))
            {
                return null;
            }
        }

        return null;
    }

    private void OnLaunchClicked(object sender, RoutedEventArgs e)
    {
        LaunchSelected();
    }

    private ToolCatalog.Tool? SelectedTool()
    {
        if (ToolList.SelectedItem is not ListViewItem item || item.Tag is not string id)
        {
            return null;
        }

        return HubTools.All.FirstOrDefault(tool => string.Equals(tool.Id, id, StringComparison.Ordinal));
    }

    private void LaunchSelected()
    {
        ToolCatalog.Tool? tool = SelectedTool();
        if (tool is null)
        {
            StatusText.Text = HubStrings.Get("StatusSelectTool");
            return;
        }

        Launch(tool);
    }

    /// <summary>Win32 ERROR_CANCELLED: the user declined the UAC consent prompt.</summary>
    private const int ErrorCancelled = 1223;

    private void Launch(ToolCatalog.Tool tool)
    {
        string exe = HubTools.ExePath(tool);
        if (!File.Exists(exe))
        {
            string missing = HubStrings.Format("StatusLaunchMissing", tool.DisplayName, exe);
            StatusText.Text = missing;
            Log.Warn(missing);
            RefreshStatus(tool);
            return;
        }

        try
        {
            // UseShellExecute lets Windows apply the tool's own manifest (highestAvailable), as Explorer would.
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory,
            })?.Dispose();
            StatusText.Text = HubStrings.Format("StatusLaunched", tool.DisplayName);
            Log.Info("Launched " + tool.DisplayName + " from " + exe + ".");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            // The user canceled the consent prompt of an elevating tool. That is a choice, not a failure.
            StatusText.Text = HubStrings.Get("StatusLaunchCanceled");
            Log.Info("Launch of " + tool.DisplayName + " was canceled at the consent prompt.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            StatusText.Text = HubStrings.Format("StatusLaunchFailed", tool.DisplayName, ex.Message);
            Log.Exception("Launch failed for " + tool.DisplayName + " (" + exe + ").", ex);
        }
    }

    private void RefreshStatus(ToolCatalog.Tool tool)
    {
        if (_rows.TryGetValue(tool.Id, out RowParts? row))
        {
            row.Status.Text = HubTools.StatusText(tool);
        }
    }

    private void OnHubRootLoaded(object sender, RoutedEventArgs e)
    {
        ApplyMenuBarMetrics();
        if (!_initialSizeApplied)
        {
            _initialSizeApplied = true;
            ApplyDefaultSizeIfNone();
        }

        _menuKeyboard ??= CreateMenuKeyboard();
        if (!_startupCheckQueued && _settings.CheckForUpdatesOnStartup)
        {
            _startupCheckQueued = true;
            RunCommand(() => CheckForUpdatesAsync(interactive: false));
        }

        if (ToolList.Items.Count > 0 && ToolList.SelectedItem is null)
        {
            ToolList.SelectedIndex = 0;
        }

        _ = ToolList.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// The window's scale factor from its DPI. AppWindow sizes are physical pixels, so the default
    /// size in DIP must be multiplied by this value. The XamlRoot scale is not valid before the first
    /// layout on every display, so the window DPI is read directly.
    /// </summary>
    private double WindowScale()
    {
        uint dpi = GetDpiForWindow(_hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

#pragma warning disable SYSLIB1054 // A single user32 call does not justify LibraryImport plumbing.
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
#pragma warning restore SYSLIB1054

    /// <summary>
    /// Sizes a first-run window so the tool list is exactly as tall as its rows, with no empty band
    /// under the last row. The height the list needs is computed from the dense metrics; the rest of
    /// the client area (menu bar, Launch row, status line) keeps the height it was laid out with.
    /// </summary>
    private void ApplyDefaultSizeIfNone(int attempt = 0)
    {
        if (_settings.WindowBounds is not null)
        {
            return;
        }

        double chromeDip = HubRoot.ActualHeight - ListFrame.ActualHeight;
        if (ListFrame.ActualHeight <= 0 || chromeDip <= 0)
        {
            // The first layout pass has not run yet. Try again after it, a few times at most.
            if (attempt < MaxSizingAttempts)
            {
                _ = DispatcherQueue.TryEnqueue(() => ApplyDefaultSizeIfNone(attempt + 1));
            }

            return;
        }

        double listDip = ListFrameBorderDip + DenseMetrics.HeaderHeight + ListHeaderDividerDip +
            ToolList.Items.Count * DenseMetrics.ListRowHeight;
        double scale = WindowScale();
        AppWindow.ResizeClient(new SizeInt32(
            (int)Math.Round(DefaultWidthDip * scale),
            (int)Math.Round((chromeDip + listDip) * scale)));
    }

    private void ApplyTheme()
    {
        HubRoot.RequestedTheme = BwcTheme.ElementThemeFor(BwcTheme.Current);

        // The menu brushes are derived from the palette, so they are recomputed with it.
        ClassicMenuBrushes.Refresh();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        ApplyTheme();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        CrashHandler.SetOwnerWindow(0);
        BwcTheme.Changed -= OnThemeChanged;
        _menuKeyboard?.Dispose();
        _menuKeyboard = null;
        AppWindow.Closing -= OnAppWindowClosing;
        AppWindow.Changed -= OnAppWindowChanged;
        HubRoot.Loaded -= OnHubRootLoaded;
    }

    private HubSettings LoadSettings()
    {
        try
        {
            bool existed = File.Exists(_settingsStore.Path);
            HubSettings settings = _settingsStore.Load();
            if (settings.Migrate() && existed)
            {
                // The migration runs once per file: the saved file carries the new version, so a later "off" is kept.
                try
                {
                    _settingsStore.Save(settings);
                    Log.Info("Hub settings were migrated to version " + HubSettings.CurrentSettingsVersion + ".");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn("Hub settings could not be migrated: " + ex.Message);
                }
            }

            return settings;
        }
        catch (IOException ex)
        {
            Log.Warn("Hub settings could not be loaded: " + ex.Message);
            return new HubSettings();
        }
    }

    private void SaveSettings()
    {
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Hub settings could not be saved: " + ex.Message);
            throw;
        }
    }

    private void RestoreWindowBounds()
    {
        WindowBounds? bounds = _settings.WindowBounds;
        if (bounds is null)
        {
            return;
        }

        try
        {
            // The display is the one the saved rectangle is on, as Regedit does, not the display the window was created on.
            var saved = new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            DisplayArea display = DisplayArea.GetFromRect(saved, DisplayAreaFallback.Nearest);
            RectInt32 workArea = display.WorkArea;
            int width = Math.Min(Math.Max(bounds.Width, MinimumWidth), workArea.Width);
            int height = Math.Min(Math.Max(bounds.Height, MinimumHeight), workArea.Height);
            if (width <= 0 || height <= 0)
            {
                return;
            }

            int x = Math.Clamp(bounds.X, workArea.X, workArea.X + Math.Max(0, workArea.Width - width));
            int y = Math.Clamp(bounds.Y, workArea.Y, workArea.Y + Math.Max(0, workArea.Height - height));

            // One call sets position and size, as Regedit does. Two calls left the top edge at the wrong place.
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
            _normalBounds = new WindowBounds { X = x, Y = y, Width = width, Height = height };
            if (_settings.WindowMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or COMException)
        {
            Log.Warn("Hub window bounds could not be restored: " + ex.Message);
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        try
        {
            if (sender.Presenter is OverlappedPresenter presenter &&
                presenter.State == OverlappedPresenterState.Minimized)
            {
                return;
            }

            bool maximized = sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            if (!maximized)
            {
                _normalBounds = CurrentBounds();
            }

            // A maximized window saves the rectangle it had before it was maximized.
            if (_normalBounds is not null)
            {
                _settings.WindowBounds = _normalBounds;
            }

            _settings.WindowMaximized = maximized;
            SaveSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn("Hub window bounds could not be saved: " + ex.Message);
        }
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange && !args.DidSizeChange)
        {
            return;
        }

        if (sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            _normalBounds = CurrentBounds();
        }
    }

    private WindowBounds CurrentBounds()
    {
        PointInt32 position = AppWindow.Position;
        SizeInt32 size = AppWindow.Size;
        return new WindowBounds { X = position.X, Y = position.Y, Width = size.Width, Height = size.Height };
    }
}

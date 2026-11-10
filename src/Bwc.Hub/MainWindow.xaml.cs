using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using Bwc.Core;
using Bwc.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Bwc.Hub;

public sealed partial class MainWindow : Window
{
    private const string ToolId = "hub";
    private const int MinimumWidth = 640;
    private const int MinimumHeight = 480;
    private const double FallbackFontSize = 12;

    private static readonly FontFamily HubFontFamily = new("Segoe UI");

    private readonly SettingsStore<HubSettings> _settingsStore;
    private HubSettings _settings = new();
    private Button? _firstLaunch;

    public MainWindow()
    {
        InitializeComponent();
        Title = BwcStrings.ProductName;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "bwc.ico"));

        string version = ResolveVersion();
        ProductTitle.Text = BwcStrings.ProductName;
        VersionText.Text = "Version " + version;
        HubStatus.Text = "Ready.";
        HubAbout.Text = BwcStrings.ProductName + " " + version + Environment.NewLine +
            "Licensed under the Mozilla Public License 2.0 (MPL-2.0)." + Environment.NewLine +
            "License text: https://www.mozilla.org/MPL/2.0/";

        _settingsStore = new SettingsStore<HubSettings>(
            AppPaths.SettingsFile(ToolId),
            HubSettingsContext.Default.HubSettings);
        LoadSettings();
        RestoreWindowBounds();

        foreach (var tool in ToolCatalog.Tools)
        {
            ToolRows.Children.Add(CreateRow(tool));
        }

        HubRoot.Loaded += OnHubRootLoaded;
        AppWindow.Closing += OnAppWindowClosing;
        Log.Info("Hub window opened. Version " + version + ".");
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

    private UIElement CreateRow(ToolCatalog.Tool tool)
    {
        var grid = new Grid
        {
            ColumnSpacing = 8,
            Padding = new Thickness(0, 2, 0, 2),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = CreateText(tool.DisplayName);
        name.VerticalAlignment = VerticalAlignment.Center;

        var description = CreateText(tool.Description);
        description.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(description, 1);

        var launch = new Button
        {
            Content = "Launch",
            FontFamily = HubFontFamily,
            FontSize = DenseFontSize,
            MinHeight = 26,
            Padding = new Thickness(10, 2, 10, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAutomationId(launch, "Launch_" + tool.Id);
        AutomationProperties.SetName(launch, "Launch " + tool.DisplayName);
        launch.Click += (_, _) => Launch(tool);
        Grid.SetColumn(launch, 2);

        grid.Children.Add(name);
        grid.Children.Add(description);
        grid.Children.Add(launch);

        var row = new ContentControl
        {
            Content = grid,
            Padding = new Thickness(0),
            MinHeight = 0,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAutomationId(row, "ToolRow_" + tool.Id);
        AutomationProperties.SetName(row, tool.DisplayName);

        _firstLaunch ??= launch;
        return row;
    }

    private static TextBlock CreateText(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = HubFontFamily,
            FontSize = DenseFontSize,
            TextWrapping = TextWrapping.Wrap,
        };
    }

    private void OnHubRootLoaded(object sender, RoutedEventArgs e)
    {
        HubRoot.Loaded -= OnHubRootLoaded;
        _firstLaunch?.Focus(FocusState.Programmatic);
    }

    private void Launch(ToolCatalog.Tool tool)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, tool.ExeName);
        if (!File.Exists(exe))
        {
            string missing = tool.DisplayName + " is not installed. Expected: " + exe;
            HubStatus.Text = missing;
            Log.Warn(missing);
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
            HubStatus.Text = "Launched " + tool.DisplayName + ".";
            Log.Info("Launched " + tool.DisplayName + " from " + exe + ".");
        }
        catch (Win32Exception ex)
        {
            HubStatus.Text = tool.DisplayName + " did not start: " + ex.Message;
            Log.Exception("Launch failed for " + tool.DisplayName + " (" + exe + ").", ex);
        }
        catch (Exception ex)
        {
            HubStatus.Text = tool.DisplayName + " did not start: " + ex.Message;
            Log.Exception("Launch failed for " + tool.DisplayName + " (" + exe + ").", ex);
        }
    }

    private static string ResolveVersion()
    {
        Assembly assembly = typeof(MainWindow).Assembly;
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational;
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }

    private void LoadSettings()
    {
        try
        {
            _settings = _settingsStore.Load();
        }
        catch (IOException ex)
        {
            Log.Warn("Hub settings could not be loaded: " + ex.Message);
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
            DisplayArea? display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            if (display is null)
            {
                return;
            }

            RectInt32 workArea = display.WorkArea;
            int width = Math.Min(Math.Max(bounds.Width, MinimumWidth), workArea.Width);
            int height = Math.Min(Math.Max(bounds.Height, MinimumHeight), workArea.Height);
            if (width <= 0 || height <= 0)
            {
                return;
            }

            int x = Math.Clamp(bounds.X, workArea.X, workArea.X + Math.Max(0, workArea.Width - width));
            int y = Math.Clamp(bounds.Y, workArea.Y, workArea.Y + Math.Max(0, workArea.Height - height));

            AppWindow.Move(new PointInt32(x, y));
            AppWindow.Resize(new SizeInt32(width, height));
        }
        catch (Exception ex)
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

            PointInt32 position = sender.Position;
            SizeInt32 size = sender.Size;
            _settings.WindowBounds = new WindowBounds
            {
                X = position.X,
                Y = position.Y,
                Width = size.Width,
                Height = size.Height,
            };
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            Log.Warn("Hub window bounds could not be saved: " + ex.Message);
        }
    }
}

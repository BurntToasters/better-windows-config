using Bwc.Core;
using Bwc.Regedit;
using Bwc.Regedit.Registry;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace Bwc.Regedit.App;

public sealed partial class MainWindow : Window
{
    private const int DefaultWidthDip = 1000;
    private const int DefaultHeightDip = 700;
    private const string ToolId = "regedit";

    private readonly SettingsStore<RegeditSettings> _settingsStore;

    private bool _initialSizeApplied;

    public MainWindow()
    {
        _settingsStore = new SettingsStore<RegeditSettings>(
            AppPaths.SettingsFile(ToolId),
            RegeditSettingsContext.Default.RegeditSettings);

        RegeditSettings settings = LoadSettings();

        InitializeComponent();

        Title = RegeditStrings.Get("RegeditTitle");
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "bwc.ico"));
        AppWindow.Resize(new SizeInt32(DefaultWidthDip, DefaultHeightDip));

        RootView.ApplySettings(settings);
        RootView.ExitRequested += OnExitRequested;
        RootView.Loaded += OnRootViewLoaded;
        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnClosed;

        // Dialogs are separate owned windows and need the main HWND before a command can open one.
        RootView.AttachWindow(WindowNative.GetWindowHandle(this));

        // RegeditRootView forwards this to the shell. The shell creates the router on Loaded.
        RootView.AttachMenuKeyboard(WindowNative.GetWindowHandle(this));
    }

    private void OnRootViewLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialSizeApplied)
        {
            return;
        }

        _initialSizeApplied = true;

        // AppWindow.Resize takes physical pixels. Scale the DIP default once the XamlRoot knows
        // the monitor scale, so 1000x700 stays 1000x700 DIP on a 200 percent display.
        var scale = RootView.XamlRoot?.RasterizationScale ?? 1.0;
        AppWindow.Resize(new SizeInt32(
            (int)Math.Round(DefaultWidthDip * scale),
            (int)Math.Round(DefaultHeightDip * scale)));
    }

    private void OnExitRequested(object? sender, EventArgs e)
    {
        Close();
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        try
        {
            _settingsStore.Save(RootView.CaptureSettings());
        }
        catch (Exception ex)
        {
            Log.Warn("Registry Editor settings could not be saved: " + ex.Message);
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        RootView.ExitRequested -= OnExitRequested;
        RootView.Loaded -= OnRootViewLoaded;
        AppWindow.Closing -= OnAppWindowClosing;
        RootView.DetachMenuKeyboard();
    }

    private RegeditSettings LoadSettings()
    {
        try
        {
            return _settingsStore.Load();
        }
        catch (IOException ex)
        {
            Log.Warn("Registry Editor settings could not be loaded: " + ex.Message);
            return new RegeditSettings();
        }
    }
}

using Bwc.Core;
using Bwc.Regedit;
using Bwc.Regedit.Controllers;
using Bwc.Regedit.Registry;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace Bwc.Regedit.App;

public sealed partial class MainWindow : Window
{
    private const int DefaultWidthDip = 1000;
    private const int DefaultHeightDip = 700;
    private const string ToolId = "regedit";

    /// <summary>How long a setting must stay unchanged before it is saved.</summary>
    private const int SettingsSaveDelayMs = 500;

    private readonly SettingsStore<RegeditSettings> _settingsStore;
    private readonly string? _startupRegFile;

    private bool _initialSizeApplied;
    private bool _placementApplied;
    private bool _closeWhenWritesEnd;
    private DispatcherQueueTimer? _settingsTimer;

    /// <summary>The last rectangle seen while the window was not maximized or minimized. Saved at close.</summary>
    private WindowBounds? _normalBounds;

    public MainWindow(string? startupRegFile = null)
    {
        _settingsStore = new SettingsStore<RegeditSettings>(
            AppPaths.SettingsFile(ToolId),
            RegeditSettingsContext.Default.RegeditSettings);
        _startupRegFile = startupRegFile;

        RegeditSettings settings = LoadSettings();

        InitializeComponent();

        Title = RegeditStrings.Get("RegeditTitle");
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "bwc.ico"));

        // Placement is applied before the window is shown. Without a saved placement the DIP default is
        // scaled once the XamlRoot knows the monitor scale.
        _placementApplied = ApplySavedPlacement(settings);
        if (!_placementApplied)
        {
            AppWindow.Resize(new SizeInt32(DefaultWidthDip, DefaultHeightDip));
        }

        RootView.ApplySettings(settings);
        RootView.ExitRequested += OnExitRequested;
        RootView.SettingsChanged += OnSettingsChanged;
        RootView.WriteFinished += OnWriteFinished;
        RootView.Loaded += OnRootViewLoaded;
        AppWindow.Closing += OnAppWindowClosing;
        AppWindow.Changed += OnAppWindowChanged;
        Closed += OnClosed;

        // Dialogs are separate owned windows and need the main HWND before a command can open one.
        RootView.AttachWindow(WindowNative.GetWindowHandle(this));

        // RegeditRootView forwards this to the shell. The shell creates the router on Loaded.
        RootView.AttachMenuKeyboard(WindowNative.GetWindowHandle(this));
    }

    /// <summary>
    /// Brings the window forward for a second launch. A minimized window is restored first. A .reg path
    /// from the second launch opens the import confirmation.
    /// </summary>
    internal void HandleActivation(string? regFile)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized)
        {
            presenter.Restore();
        }

        Activate();
        _ = SetForegroundWindow(WindowNative.GetWindowHandle(this));

        if (regFile is not null)
        {
            RootView.OpenRegistryFile(regFile);
        }
    }

    private void OnRootViewLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialSizeApplied)
        {
            return;
        }

        _initialSizeApplied = true;

        if (!_placementApplied)
        {
            // AppWindow.Resize takes physical pixels. Scale the DIP default once the XamlRoot knows
            // the monitor scale, so 1000x700 stays 1000x700 DIP on a 200 percent display.
            var scale = RootView.XamlRoot?.RasterizationScale ?? 1.0;
            AppWindow.Resize(new SizeInt32(
                (int)Math.Round(DefaultWidthDip * scale),
                (int)Math.Round(DefaultHeightDip * scale)));
        }

        if (_startupRegFile is not null)
        {
            RootView.OpenRegistryFile(_startupRegFile);
        }
    }

    private void OnExitRequested(object? sender, EventArgs e)
    {
        Close();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange && !args.DidSizeChange)
        {
            return;
        }

        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            _normalBounds = CurrentBounds();
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (RootView.IsWriteInProgress)
        {
            // A registry write cannot be cancelled. The close waits for it, and runs when it ends.
            args.Cancel = true;
            _closeWhenWritesEnd = true;
            return;
        }

        _settingsTimer?.Stop();
        SaveSettings();
    }

    private void OnWriteFinished(object? sender, EventArgs e)
    {
        if (!_closeWhenWritesEnd)
        {
            return;
        }

        _closeWhenWritesEnd = false;
        Close();
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => ScheduleSettingsSave();

    /// <summary>
    /// Saves the settings a short time after the last change, so a burst of font or theme changes writes
    /// the file once and not on the UI thread for every step. Closing the window saves at once.
    /// </summary>
    private void ScheduleSettingsSave()
    {
        _settingsTimer ??= CreateSettingsTimer();
        _settingsTimer.Stop();
        _settingsTimer.Start();
    }

    private DispatcherQueueTimer CreateSettingsTimer()
    {
        DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(SettingsSaveDelayMs);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => SaveSettings();
        return timer;
    }

    /// <summary>
    /// Writes the view state and the window placement. Runs at close and after a setting change settles,
    /// so a font or theme choice is kept even when the window does not get its closing event.
    /// </summary>
    private void SaveSettings()
    {
        try
        {
            RegeditSettings snapshot = RootView.CaptureSettings();
            bool restored = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored };
            WindowBounds bounds = restored ? CurrentBounds() : _normalBounds ?? CurrentBounds();

            snapshot.WindowX = bounds.X;
            snapshot.WindowY = bounds.Y;
            snapshot.WindowWidth = bounds.Width;
            snapshot.WindowHeight = bounds.Height;
            snapshot.WindowMaximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            _settingsStore.Save(snapshot);
        }
        catch (Exception ex)
        {
            Log.Warn("Registry Editor settings could not be saved: " + ex.Message);
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        RootView.ExitRequested -= OnExitRequested;
        RootView.SettingsChanged -= OnSettingsChanged;
        RootView.WriteFinished -= OnWriteFinished;
        RootView.Loaded -= OnRootViewLoaded;
        AppWindow.Closing -= OnAppWindowClosing;
        AppWindow.Changed -= OnAppWindowChanged;
        RootView.DetachMenuKeyboard();
        (Application.Current as App)?.ReleaseSingleInstance();
    }

    private WindowBounds CurrentBounds() =>
        new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);

    /// <summary>
    /// Moves and sizes the window from the saved settings, fitted into the work area of the display it
    /// overlaps. Returns false when no usable position was saved, which keeps the default size.
    /// </summary>
    private bool ApplySavedPlacement(RegeditSettings settings)
    {
        if (settings.WindowX is not { } x || settings.WindowY is not { } y ||
            settings.WindowWidth is not { } width || settings.WindowHeight is not { } height)
        {
            return false;
        }

        var saved = new WindowBounds(x, y, width, height);
        if (!WindowPlacement.IsUsable(saved))
        {
            return false;
        }

        var savedRect = new RectInt32(saved.X, saved.Y, saved.Width, saved.Height);
        DisplayArea display = DisplayArea.GetFromRect(savedRect, DisplayAreaFallback.Nearest);
        RectInt32 work = display.WorkArea;
        WindowBounds fitted = WindowPlacement.FitInto(
            saved,
            new WindowBounds(work.X, work.Y, work.Width, work.Height));

        AppWindow.MoveAndResize(new RectInt32(fitted.X, fitted.Y, fitted.Width, fitted.Height));
        _normalBounds = fitted;
        if (settings.WindowMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }

        return true;
    }

    private RegeditSettings LoadSettings()
    {
        bool firstRun = !File.Exists(_settingsStore.Path);
        try
        {
            RegeditSettings settings = _settingsStore.Load();
            if (firstRun)
            {
                ApplyFirstRunLastKey(settings);
            }

            return settings;
        }
        catch (IOException ex)
        {
            Log.Warn("Registry Editor settings could not be loaded: " + ex.Message);
            var settings = new RegeditSettings();
            if (firstRun)
            {
                ApplyFirstRunLastKey(settings);
            }

            return settings;
        }
    }

    /// <summary>
    /// On the first run only, starts at classic regedit's LastKey. The value is read and never written.
    /// An isolated data root (BWC_DATA_ROOT) skips it, so test runs do not depend on the classic state.
    /// </summary>
    private static void ApplyFirstRunLastKey(RegeditSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.DataRootEnvironmentVariable)))
        {
            return;
        }

        string? classic = ClassicLastKey.Read();
        if (!string.IsNullOrWhiteSpace(classic))
        {
            settings.LastKey = classic;
        }
    }

#pragma warning disable SYSLIB1054 // The app project does not allow unsafe code, which LibraryImport needs.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);
#pragma warning restore SYSLIB1054
}

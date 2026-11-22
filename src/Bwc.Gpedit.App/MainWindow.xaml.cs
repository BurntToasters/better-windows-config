using Bwc.Gpedit;
using Bwc.Regedit.Controllers;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Bwc.Gpedit.App;

public sealed partial class MainWindow : Window
{
    private const int DefaultWidthDip = 1000;
    private const int DefaultHeightDip = 700;

    private readonly bool _placementApplied;
    private bool _initialSizeApplied;

    public MainWindow()
    {
        InitializeComponent();

        Title = GpeditStrings.Get("GpeditTitle");
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "bwc-gpo.ico"));

        // The saved place is applied here, before App activates the window, so the window is first shown where it will
        // stay. The view saves the rectangle again at close. Without a saved place the DIP default is scaled once the
        // XamlRoot knows the monitor scale.
        _placementApplied = ApplySavedPlacement(RootView.SavedPlacement);
        if (!_placementApplied)
        {
            AppWindow.Resize(new SizeInt32(DefaultWidthDip, DefaultHeightDip));
        }

        RootView.Loaded += OnRootViewLoaded;
        Closed += OnClosed;
    }

    private void OnRootViewLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialSizeApplied)
        {
            return;
        }

        _initialSizeApplied = true;
        if (_placementApplied)
        {
            return;
        }

        double scale = RootView.XamlRoot?.RasterizationScale ?? 1.0;
        AppWindow.Resize(new SizeInt32(
            (int)Math.Round(DefaultWidthDip * scale),
            (int)Math.Round(DefaultHeightDip * scale)));
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        RootView.Loaded -= OnRootViewLoaded;
        Closed -= OnClosed;
    }

    /// <summary>
    /// Moves and sizes the window from the saved rectangle, fitted into the work area of the display it overlaps (or the
    /// nearest one). Returns false when no usable rectangle was saved, which keeps the default size.
    /// </summary>
    private bool ApplySavedPlacement(GpeditSavedPlacement? placement)
    {
        if (placement is not { } saved)
        {
            return false;
        }

        var bounds = new WindowBounds(saved.X, saved.Y, saved.Width, saved.Height);
        if (!WindowPlacement.IsUsable(bounds))
        {
            return false;
        }

        DisplayArea display = DisplayArea.GetFromRect(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height), DisplayAreaFallback.Nearest);
        RectInt32 work = display.WorkArea;
        WindowBounds fitted = WindowPlacement.FitInto(bounds, new WindowBounds(work.X, work.Y, work.Width, work.Height));
        AppWindow.MoveAndResize(new RectInt32(fitted.X, fitted.Y, fitted.Width, fitted.Height));
        RootView.NoteNormalBounds(fitted.X, fitted.Y, fitted.Width, fitted.Height);
        if (saved.Maximized && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }

        return true;
    }
}

using Bwc.Gpedit;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Bwc.Gpedit.App;

public sealed partial class MainWindow : Window
{
    private const int DefaultWidthDip = 1000;
    private const int DefaultHeightDip = 700;

    private bool _initialSizeApplied;

    public MainWindow()
    {
        InitializeComponent();

        Title = GpeditStrings.Get("GpeditTitle");
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "bwc-gpo.ico"));

        // AppWindow.Resize takes physical pixels. The DIP default is scaled once the XamlRoot knows the
        // monitor scale. Persisted placement is added with the window state in a later piece.
        AppWindow.Resize(new SizeInt32(DefaultWidthDip, DefaultHeightDip));
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
        double scale = RootView.XamlRoot?.RasterizationScale ?? 1.0;
        AppWindow.Resize(new SizeInt32(
            (int)Math.Round(DefaultWidthDip * scale),
            (int)Math.Round(DefaultHeightDip * scale)));
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        RootView.Loaded -= OnRootViewLoaded;
    }
}

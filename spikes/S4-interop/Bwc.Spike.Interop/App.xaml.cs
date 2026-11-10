using Microsoft.UI.Xaml;

namespace Bwc.Spike.Interop;

public partial class App : Application
{
    private MainWindow? _main;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _main = new MainWindow();
        _main.Activate();
    }
}

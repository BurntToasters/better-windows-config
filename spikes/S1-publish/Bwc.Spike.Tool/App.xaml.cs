using Bwc.Spike.UI;
using Microsoft.UI.Xaml;

namespace Bwc.Spike.Tool;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        // Must run before the first window is created.
        ProcessProbe.SetAppUserModelId("BWC.Spike.Tool");
        SpikeOptions.FromCommandLine();
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
        SpikeOptions.Current.Attach(_window);
    }
}

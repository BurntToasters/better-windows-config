using Bwc.Spike.UI;
using Microsoft.UI.Xaml;

namespace Bwc.Spike.Hub;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        SpikeOptions.FromCommandLine();
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Must run before the first window is created.
        ProcessProbe.SetAppUserModelId("BWC.Spike.Hub");
        _window = new MainWindow();
        _window.Activate();
        SpikeOptions.Current.Attach(_window);
    }
}

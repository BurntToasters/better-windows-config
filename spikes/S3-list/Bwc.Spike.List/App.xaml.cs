using System;
using Microsoft.UI.Xaml;

namespace Bwc.Spike.ListApp;

public partial class App : Application
{
    private MainWindow _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        SpikeOptions options;
        try
        {
            options = SpikeOptions.Parse(Environment.GetCommandLineArgs());
        }
        catch (Exception)
        {
            Environment.Exit(2);
            return;
        }

        _window = new MainWindow(options);
        _window.Activate();
    }
}

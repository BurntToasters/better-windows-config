using Bwc.Core;
using Microsoft.UI.Xaml;

namespace Bwc.Regedit.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Must run before the first window is created.
        AppIdentity.SetCurrentProcessAppUserModelId(AppIdentity.RegeditAppUserModelId);
        _window = new MainWindow();
        _window.Activate();
    }
}

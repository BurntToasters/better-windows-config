using Bwc.Core;
using Bwc.Regedit.CommandLine;
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
        string[] commandLine = Environment.GetCommandLineArgs();
        string[] arguments = commandLine.Length > 1 ? commandLine[1..] : Array.Empty<string>();

        if (RegeditCommandLine.TryRun(arguments, out int exitCode))
        {
            Environment.Exit(exitCode);
            return;
        }

        // Must run before the first window is created.
        AppIdentity.SetCurrentProcessAppUserModelId(AppIdentity.RegeditAppUserModelId);
        _window = new MainWindow();
        _window.Activate();
    }
}

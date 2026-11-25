using Bwc.Core;
using Microsoft.UI.Xaml;

namespace Bwc.Gpedit.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Logging and crash reporting start before anything else in the window path can fail.
        Log.Initialize(ToolId);
        Bwc.UI.UiDiagnostics.Warn = Log.Warn;
        CrashHandler.Install(ToolId, showDialog: true);
        UnhandledException += OnUnhandledException;
        Log.Info("Local Group Policy Editor starting.");

        // Must run before the first window is created.
        AppIdentity.SetCurrentProcessAppUserModelId(AppIdentity.GpeditAppUserModelId);
        Log.Info("AppUserModelID: " + (AppIdentity.GetCurrentProcessAppUserModelId() ?? "(none)"));
        _window = new MainWindow();
        _window.Activate();
    }

    private const string ToolId = "gpedit";

    /// <summary>
    /// Reports an exception thrown by a window handler. CrashHandler logs it first and shows the classic error
    /// box. The exception is not marked handled, so the process ends after the report, as in the other tools.
    /// </summary>
    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        CrashHandler.Report(e.Exception, "Local Group Policy Editor.UnhandledException");

        // Intentionally not handled: a fatal error still terminates after it is reported.
    }
}

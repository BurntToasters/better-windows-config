using Bwc.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Bwc.Hub;

public partial class App : Application
{
    private const string ToolId = "hub";
    private const string ActivateMessage = "activate";

    private Window? _window;
    private SingleInstance? _instance;
    private bool _shutdown;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log.Initialize(ToolId);
        Log.Info("Hub starting.");
        CrashHandler.Install(ToolId, showDialog: true);
        UnhandledException += OnUnhandledException;

        // Must run before the first window is created.
        AppIdentity.SetCurrentProcessAppUserModelId(AppIdentity.HubAppUserModelId);

        _instance = SingleInstance.Create(ToolId);
        if (!_instance.IsFirstInstance)
        {
            Log.Info("Another Hub instance is running. Requesting activation.");
            SendActivate();
            Shutdown();
            Exit();
            return;
        }

        _instance.MessageReceived += OnMessageReceived;
        _window = new MainWindow();
        _window.Closed += OnWindowClosed;
        _window.Activate();
    }

    private void Shutdown()
    {
        if (_shutdown)
        {
            return;
        }

        _shutdown = true;
        UnhandledException -= OnUnhandledException;

        if (_instance is not null)
        {
            _instance.MessageReceived -= OnMessageReceived;
            _instance.Dispose();
            _instance = null;
        }

        Log.Info("Hub stopped.");
        Log.Shutdown();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        Shutdown();
    }

    private void SendActivate()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (_instance is not null && _instance.SendMessage(ActivateMessage))
            {
                return;
            }

            Thread.Sleep(200);
        }

        Log.Warn("Another Hub instance is running but did not accept the activate message.");
    }

    private void OnMessageReceived(string message)
    {
        if (!string.Equals(message, ActivateMessage, StringComparison.Ordinal))
        {
            return;
        }

        Window? window = _window;
        if (window is null)
        {
            return;
        }

        window.DispatcherQueue.TryEnqueue(() =>
        {
            window.Activate();
            if (window.AppWindow.Presenter is OverlappedPresenter presenter &&
                presenter.State == OverlappedPresenterState.Minimized)
            {
                presenter.Restore();
            }
        });
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        CrashHandler.Report(e.Exception, "Hub.UnhandledException");

        // Intentionally not handled: a fatal error still terminates after it is reported.
    }
}

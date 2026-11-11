using Bwc.Core;
using Bwc.Regedit.CommandLine;
using Bwc.Regedit.Controllers;
using Bwc.Regedit.Dialogs;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Bwc.Regedit.App;

public partial class App : Application
{
    private Window? _window;
    private SingleInstance? _singleInstance;

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

        // Logging and crash reporting start before anything else in the window path can fail.
        Log.Initialize("regedit");
        CrashHandler.Install("regedit", showDialog: true);
        Log.Info("Registry Editor " + AboutBox.VersionText(System.Reflection.Assembly.GetEntryAssembly() ?? typeof(App).Assembly) + " starting.");

        string? regFile = RegeditCommandLine.StartupRegFile(arguments);
        if (!RegeditCommandLine.AllowsMultipleInstances(arguments) && !TakeOrForwardSingleInstance(regFile))
        {
            Environment.Exit(0);
            return;
        }

        // Must run before the first window is created.
        AppIdentity.SetCurrentProcessAppUserModelId(AppIdentity.RegeditAppUserModelId);
        _window = new MainWindow(regFile);
        _window.Activate();
    }

    /// <summary>
    /// Returns true when this process should open a window. A second launch sends its request to the
    /// running instance and returns false. When the message cannot be delivered, the launch opens its
    /// own window and does not hold the gate.
    /// </summary>
    private bool TakeOrForwardSingleInstance(string? regFile)
    {
        SingleInstance gate = SingleInstance.Create("regedit");
        if (gate.IsFirstInstance)
        {
            DispatcherQueue queue = DispatcherQueue.GetForCurrentThread();
            gate.MessageReceived += message => queue.TryEnqueue(() => OnActivationMessage(message));
            _singleInstance = gate;
            return true;
        }

        bool delivered = gate.SendMessage(RegeditActivation.Encode(regFile));
        gate.Dispose();
        if (delivered)
        {
            return false;
        }

        Log.Warn("Registry Editor could not reach the running instance. This launch opens its own window.");
        return true;
    }

    private void OnActivationMessage(string message)
    {
        if (_window is not MainWindow window)
        {
            return;
        }

        window.HandleActivation(RegeditActivation.TryDecode(message, out string? regFile) ? regFile : null);
    }

    /// <summary>
    /// Releases the single-instance gate when the window closes, so the next launch becomes the first
    /// instance.
    /// </summary>
    internal void ReleaseSingleInstance()
    {
        SingleInstance? gate = _singleInstance;
        _singleInstance = null;
        gate?.Dispose();
    }
}

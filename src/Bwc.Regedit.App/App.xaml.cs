using System.Globalization;
using System.Runtime.InteropServices;
using Bwc.Core;
using Bwc.Regedit.CommandLine;
using Bwc.Regedit.Controllers;
using Bwc.Regedit.Dialogs;
using Bwc.Regedit.Registry;
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
        Bwc.UI.UiDiagnostics.Warn = Log.Warn;
        CrashHandler.Install("regedit", showDialog: true);
        UnhandledException += OnUnhandledException;
        Log.Info("Registry Editor " + AboutBox.VersionText(System.Reflection.Assembly.GetEntryAssembly() ?? typeof(App).Assembly) + " starting.");

        // An administrator can disable registry editing. The window is not created then.
        int policy = RegeditPolicy.ReadDisableRegistryTools();
        if (RegeditPolicy.DisablesRegistryEditing(policy))
        {
            Log.Warn($"Registry Editor is disabled by the DisableRegistryTools policy ({policy}).");
            RegeditPolicy.ShowRegistryEditingDisabled();
            Environment.Exit(1);
            return;
        }

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

        // The process the user just started holds the right to grant foreground. The grant comes before the message.
        AllowForegroundForRunningInstance();
        bool delivered = gate.SendMessage(RegeditActivation.Encode(AbsoluteRegFile(regFile)));
        gate.Dispose();
        if (delivered)
        {
            return false;
        }

        Log.Warn("Registry Editor could not reach the running instance. This launch opens its own window.");
        return true;
    }

    /// <summary>
    /// The .reg path as this launch sees it. The running instance has its own working folder, so a relative
    /// name is made absolute here. A path that cannot be resolved is logged, and the launch only activates.
    /// </summary>
    private static string? AbsoluteRegFile(string? regFile)
    {
        if (string.IsNullOrWhiteSpace(regFile))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(regFile);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Log.Warn("Registry Editor could not resolve the .reg path of a second launch: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Lets the running instance take the foreground for the activation that follows. A failed call is logged, and
    /// the handoff still runs.
    /// </summary>
    private static void AllowForegroundForRunningInstance()
    {
        try
        {
            if (!AllowSetForegroundWindow(AllowAnyProcess))
            {
                Log.Warn("The running Registry Editor was not given foreground rights. Win32 error " +
                    Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture) + ".");
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warn("AllowSetForegroundWindow is unavailable: " + ex.Message);
        }
    }

#pragma warning disable SYSLIB1054 // A single user32 call does not justify LibraryImport plumbing.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AllowSetForegroundWindow(uint processId);
#pragma warning restore SYSLIB1054

    /// <summary>ASFW_ANY: any process may take the foreground next.</summary>
    private const uint AllowAnyProcess = unchecked((uint)-1);

    private void OnActivationMessage(string message)
    {
        if (_window is not MainWindow window)
        {
            return;
        }

        window.HandleActivation(RegeditActivation.TryDecode(message, out string? regFile) ? regFile : null);
    }

    /// <summary>
    /// Reports an exception thrown by a window handler. CrashHandler logs it first and shows the classic error
    /// box. The exception is not marked handled, so the process ends after the report, as the Hub does.
    /// </summary>
    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        CrashHandler.Report(e.Exception, "Registry Editor.UnhandledException");

        // Intentionally not handled: a fatal error still terminates after it is reported.
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

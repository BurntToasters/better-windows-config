using System.Runtime.InteropServices;

namespace Bwc.Core;

/// <summary>
/// Last-chance reporting for unhandled exceptions. Reporting never throws and is guarded
/// against recursive reports from a dialog message loop.
/// </summary>
public static partial class CrashHandler
{
    private const uint MessageBoxOk = 0x00000000;
    private const uint MessageBoxIconError = 0x00000010;

    private static readonly object Gate = new();

    private static bool _installed;
    private static bool _showDialog;
    private static string _toolId = "unknown";
    private static int _reporting;
    private static nint _owner;

    public static void Install(string toolId, bool showDialog)
    {
        try
        {
            AppPaths.ValidateToolId(toolId);
            lock (Gate)
            {
                _toolId = toolId;
                _showDialog = showDialog;
                if (!_installed)
                {
                    AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                    TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
                    _installed = true;
                }
            }
        }
        catch (Exception ex)
        {
            SafeTrace.WriteLine("CrashHandler.Install failed: " + ex);
        }
    }

    public static void Uninstall()
    {
        try
        {
            lock (Gate)
            {
                if (!_installed)
                {
                    return;
                }

                AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
                TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
                _installed = false;
            }
        }
        catch (Exception ex)
        {
            SafeTrace.WriteLine("CrashHandler.Uninstall failed: " + ex);
        }
    }

    /// <summary>
    /// Sets the window that owns the crash dialog. Zero means no owner. Call with zero when that window closes.
    /// </summary>
    public static void SetOwnerWindow(nint hwnd)
    {
        try
        {
            lock (Gate)
            {
                _owner = hwnd;
            }
        }
        catch (Exception ex)
        {
            SafeTrace.WriteLine("CrashHandler.SetOwnerWindow failed: " + ex);
        }
    }

    public static void Report(System.Exception ex, string context)
    {
        Report(ex, context, allowDialog: true);
    }

    /// <summary>Logs first, always. The dialog is shown only when allowed and not nested in a dialog.</summary>
    private static void Report(System.Exception? ex, string context, bool allowDialog)
    {
        try
        {
            if (ex is null)
            {
                SafeTrace.WriteLine("CrashHandler.Report received a null exception.");
                return;
            }

            string safeContext = string.IsNullOrWhiteSpace(context) ? "Unhandled exception" : context;
            Log.Exception(_toolId + ": " + safeContext, ex);
            if (!allowDialog || !_showDialog)
            {
                return;
            }

            if (Interlocked.Exchange(ref _reporting, 1) != 0)
            {
                SafeTrace.WriteLine("CrashHandler.Report skipped the dialog of a nested report.");
                return;
            }

            try
            {
                string logPath = Log.CurrentFile ?? Log.Directory ?? "(log file unavailable)";
                string text = safeContext + Environment.NewLine + Environment.NewLine +
                    ex.GetType().Name + ": " + ex.Message + Environment.NewLine + Environment.NewLine +
                    "Log: " + logPath;
                nint owner;
                lock (Gate)
                {
                    owner = _owner;
                }

                _ = MessageBoxW(owner, text, "Better Windows Config", MessageBoxOk | MessageBoxIconError);
            }
            finally
            {
                Interlocked.Exchange(ref _reporting, 0);
            }
        }
        catch (Exception reportError)
        {
            SafeTrace.WriteLine("CrashHandler.Report failed: " + reportError);
        }
    }

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is System.Exception ex)
        {
            Report(ex, "AppDomain.UnhandledException");
            return;
        }

        Report(
            new InvalidOperationException("An unknown object was reported as an unhandled exception."),
            "AppDomain.UnhandledException");
    }

    /// <summary>Logs only. This handler can run on the finalizer thread, where a modal dialog would block it.</summary>
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Report(e.Exception, "TaskScheduler.UnobservedTaskException", allowDialog: false);
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint hWnd, string text, string caption, uint type);
}

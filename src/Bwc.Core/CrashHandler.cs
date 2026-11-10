using System.Diagnostics;
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
            Trace.WriteLine("CrashHandler.Install failed: " + ex);
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
            Trace.WriteLine("CrashHandler.Uninstall failed: " + ex);
        }
    }

    public static void Report(System.Exception ex, string context)
    {
        try
        {
            if (ex is null)
            {
                Trace.WriteLine("CrashHandler.Report received a null exception.");
                return;
            }

            string safeContext = string.IsNullOrWhiteSpace(context) ? "Unhandled exception" : context;
            if (Interlocked.Exchange(ref _reporting, 1) != 0)
            {
                Trace.WriteLine("CrashHandler.Report skipped a recursive report.");
                return;
            }

            try
            {
                Log.Exception(_toolId + ": " + safeContext, ex);
                if (_showDialog)
                {
                    string logPath = Log.CurrentFile ?? Log.Directory ?? "(log file unavailable)";
                    string text = safeContext + Environment.NewLine + Environment.NewLine +
                        ex.GetType().Name + ": " + ex.Message + Environment.NewLine + Environment.NewLine +
                        "Log: " + logPath;
                    _ = MessageBoxW(
                        IntPtr.Zero,
                        text,
                        "Better Windows Config",
                        MessageBoxOk | MessageBoxIconError);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _reporting, 0);
            }
        }
        catch (Exception reportError)
        {
            Trace.WriteLine("CrashHandler.Report failed: " + reportError);
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

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Report(e.Exception, "TaskScheduler.UnobservedTaskException");
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}

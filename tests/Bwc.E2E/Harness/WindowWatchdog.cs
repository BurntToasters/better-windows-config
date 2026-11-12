namespace Bwc.E2E.Harness;

/// <summary>
/// Keeps every visible top level window of one application process above other applications for the
/// length of a session. The main window alone is not enough: a WinUI flyout, a tooltip, or an owned
/// dialog is a separate top level window and would otherwise sit in the normal z order band, where a
/// foreign window covers it and takes the click. The watchdog raises the windows on a background thread
/// with SWP_NOACTIVATE, so the app's own popups participate without changing which window is active.
/// The class clears the flag from every window when the session closes. A failure is recorded as a
/// failed check, never swallowed.
/// </summary>
internal sealed class WindowWatchdog : IDisposable
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly object _sync = new();
    private readonly uint _processId;
    private readonly ManualResetEventSlim _stop = new(initialState: false);
    private readonly Thread _thread;
    private bool _disposed;

    internal WindowWatchdog(uint processId)
    {
        _processId = processId;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"BwcWindowWatchdog-{processId}",
        };

        _thread.Start();
    }

    private void Run()
    {
        try
        {
            while (!_stop.IsSet)
            {
                _ = Win32.RaiseProcessWindows(_processId);
                if (_stop.Wait(Interval))
                {
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            E2EResults.Record(
                "gui.windows.watchdog",
                false,
                $"The window watchdog for process {_processId} stopped after {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _stop.Set();
        bool joined = _thread.Join(StopTimeout);
        if (!joined)
        {
            E2EResults.Record(
                "gui.windows.watchdog",
                false,
                $"The window watchdog for process {_processId} did not stop within {StopTimeout.TotalSeconds:0} s.");
        }
        else
        {
            // The event is only disposed once its reader has stopped.
            _stop.Dispose();
        }

        _ = Win32.ClearTopmost(_processId);
    }
}

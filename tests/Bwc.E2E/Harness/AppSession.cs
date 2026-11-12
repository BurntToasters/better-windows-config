using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace Bwc.E2E.Harness;

/// <summary>
/// Owns one launched application process, its main window handle, and the UIA3 automation used to
/// query it. The process is polled directly because FlaUI's Application wrapper disposes the
/// Process instance it is given.
/// </summary>
public sealed class AppSession : IDisposable
{
    private static readonly TimeSpan DefaultWindowTimeout = TimeSpan.FromSeconds(30);

    private readonly E2EConfig _config;
    private readonly string _exePath;
    private readonly string _exeName;
    private readonly Process _process;
    private readonly UIA3Automation _automation;
    private readonly WindowWatchdog _watchdog;
    private bool _disposed;
    private bool _foregroundRecorded;

    public AppSession(
        E2EConfig config,
        string exePath,
        TimeSpan? windowTimeout = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        _config = config;
        _exePath = exePath;
        _exeName = Path.GetFileName(exePath);

        var startInfo = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            WorkingDirectory = config.PublishDir,
        };
        // Keeps a highestAvailable manifest from raising a UAC prompt.
        startInfo.Environment["__COMPAT_LAYER"] = "RunAsInvoker";

        // Every session gets an isolated settings folder. A caller can replace the root, which is
        // what the Navigation test does when it relaunches against the same settings file.
        startInfo.Environment[E2EConfig.DataRootEnvironmentVariable] = config.DataRoot;
        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Process.Start returned no process for '{exePath}'.");

        UIA3Automation automation;
        try
        {
            automation = new UIA3Automation();
        }
        catch
        {
            Kill(process);
            process.Dispose();
            throw;
        }

        _process = process;
        _automation = automation;

        try
        {
            MainHwnd = WaitForMainWindow(windowTimeout ?? DefaultWindowTimeout);
        }
        catch
        {
            automation.Dispose();
            Kill(process);
            process.Dispose();
            throw;
        }

        ProcessId = process.Id;

        // The app under test stays above other applications for the whole session, so a window the user
        // runs cannot cover it and take the keystrokes. The watchdog also raises the app's own popups and
        // owned dialogs, which are separate top level windows and would otherwise stay below the main
        // window. Close stops the watchdog, which clears the flag from every window.
        _ = Win32.SetTopmost(MainHwnd, true);
        _watchdog = new WindowWatchdog(unchecked((uint)ProcessId));

        E2EResults.CurrentSession = this;
    }

    public Window Window => _automation.FromHandle(MainHwnd).AsWindow();

    public IntPtr MainHwnd { get; }

    public int ProcessId { get; }

    public bool IsEnabled => Win32.IsWindowEnabled(MainHwnd);

    /// <summary>
    /// Looks up an automation id below the current main window. The root element is re-acquired
    /// from MainHwnd on every call so stale element references do not survive a modal or rebuild.
    /// </summary>
    public AutomationElement? Find(string automationId)
    {
        try
        {
            return FindFromRoot(automationId);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public AutomationElement WaitForElement(string automationId, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(15);
        var deadline = DateTime.UtcNow + limit;
        Exception? lastError = null;

        do
        {
            try
            {
                var element = FindFromRoot(automationId);
                if (element is not null)
                {
                    return element;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            Thread.Sleep(100);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException(
            $"Element '{automationId}' was not found in '{_exeName}' within {limit.TotalSeconds:0.#} s. " +
            (lastError is null ? "The element was not present." : $"Last UIA error: {lastError.Message}"));
    }

    public string? WaitForText(string automationId, string expected, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(15);
        var deadline = DateTime.UtcNow + limit;
        string? lastText = null;

        do
        {
            var element = Find(automationId);
            if (element is not null)
            {
                lastText = TextOf(element);
                if (string.Equals(lastText, expected, StringComparison.Ordinal))
                {
                    return lastText;
                }
            }

            Thread.Sleep(100);
        }
        while (DateTime.UtcNow < deadline);

        return lastText;
    }

    public string TextOf(AutomationElement element)
    {
        // Prefer the Value pattern so a TextBox reports its text instead of its accessible name.
        try
        {
            if (element.Patterns.Value.IsSupported)
            {
                return element.Patterns.Value.Pattern.Value.Value ?? string.Empty;
            }
        }
        catch (Exception)
        {
            // Fall through to the name.
        }

        return element.Name ?? string.Empty;
    }

    /// <summary>
    /// Sends keys only to the foreground window. If the target cannot be made foreground within
    /// three seconds a failed check is recorded and nothing is typed.
    /// </summary>
    public void SendKeys(params string[] keys)
    {
        if (keys.Length == 0)
        {
            return;
        }

        var checkId = $"gui.foreground.{Path.GetFileNameWithoutExtension(_exePath).ToLowerInvariant()}";
        if (!EnsureForeground(TimeSpan.FromSeconds(3)))
        {
            var actual = Win32.GetForegroundWindow();
            var notes = $"Foreground HWND 0x{actual.ToInt64():X} is not target 0x{MainHwnd.ToInt64():X} for '{_exeName}'.";
            E2EResults.Record(checkId, false, notes);
            throw new InvalidOperationException(notes);
        }

        // The first successful input per session is logged once as a note. Failures stay checks.
        if (!_foregroundRecorded)
        {
            E2EResults.Note(checkId, $"Target HWND 0x{MainHwnd.ToInt64():X} is foreground.");
            _foregroundRecorded = true;
        }

        foreach (var key in keys)
        {
            SendKey(key);
        }
    }

    /// <summary>
    /// Raises every visible top level window of the app process, including owned dialogs and popup
    /// windows, without changing the foreground window.
    /// </summary>
    internal void RaiseWindows() => _ = Win32.RaiseProcessWindows(unchecked((uint)ProcessId));

    public void Close()
    {
        _watchdog.Dispose();
        if (_process.HasExited)
        {
            return;
        }

        _ = Win32.SetTopmost(MainHwnd, false);
        try
        {
            _process.CloseMainWindow();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (!_process.WaitForExit(TimeSpan.FromSeconds(10)))
        {
            Kill(_process);
            _process.WaitForExit(TimeSpan.FromSeconds(5));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (ReferenceEquals(E2EResults.CurrentSession, this))
        {
            E2EResults.CurrentSession = null;
        }

        try
        {
            _watchdog.Dispose();
            Close();
        }
        finally
        {
            _automation.Dispose();
            _process.Dispose();
        }
    }

    private static void SendKey(string key)
    {
        if (key.Length == 1)
        {
            Keyboard.Type(key);
            return;
        }

        var parts = key.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1)
        {
            if (Enum.TryParse<VirtualKeyShort>(parts[0], ignoreCase: true, out var single))
            {
                Keyboard.Press(single);
            }
            else
            {
                Keyboard.Type(key);
            }

            return;
        }

        var keys = new VirtualKeyShort[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!Enum.TryParse<VirtualKeyShort>(parts[i], ignoreCase: true, out keys[i]))
            {
                throw new ArgumentException($"Unknown key '{parts[i]}' in chord '{key}'.", nameof(key));
            }
        }

        Keyboard.TypeSimultaneously(keys);
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process already exited.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Access was lost while the process was exiting.
        }
    }

    private AutomationElement? FindFromRoot(string automationId)
    {
        var window = _automation.FromHandle(MainHwnd).AsWindow();
        return window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
    }

    private IntPtr WaitForMainWindow(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            _process.Refresh();
            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"{_exeName} exited with code 0x{_process.ExitCode:X8} before showing a main window. " +
                    $"Publish folder: {_config.PublishDir}.");
            }

            var hwnd = _process.MainWindowHandle;
            if (hwnd != IntPtr.Zero && Win32.IsWindow(hwnd) && Win32.IsVisible(hwnd))
            {
                return hwnd;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"{_exeName} showed no main window within {timeout.TotalSeconds:0} s. " +
                    $"Publish folder: {_config.PublishDir}.");
            }

            Thread.Sleep(200);
        }
    }

    /// <summary>
    /// Makes the main window the foreground window and returns true once it is, within the timeout. This is the
    /// harness's one foreground routine: every keystroke batch and every mouse click aimed at the app is preceded by
    /// a check through it. The window is kept topmost, activated through UI Automation, and then through the
    /// attached-thread path in <see cref="Win32.ForceForeground"/> when Windows refuses the plain request.
    /// </summary>
    internal bool EnsureForeground(TimeSpan? timeout = null)
    {
        // The app's own popups and dialogs participate in the raise, so a menu or message that is open
        // is brought above the main window before the foreground request and the input that follows.
        RaiseWindows();
        if (Win32.IsForeground(MainHwnd))
        {
            return true;
        }

        _ = Win32.SetTopmost(MainHwnd, true);
        try
        {
            Window.SetForeground();
        }
        catch (Exception)
        {
            // The attached-thread request below still runs.
        }

        return Win32.ForceForeground(MainHwnd, timeout ?? TimeSpan.FromSeconds(3));
    }
}

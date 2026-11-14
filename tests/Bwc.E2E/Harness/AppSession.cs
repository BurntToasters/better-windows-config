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
    private bool _disposed;
    private bool _foregroundRecorded;
    private readonly List<(string Name, Action Action)> _onDispose = new();

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

        // The window keeps its own z order. Nothing here raises it: a dialog that opens behind the main
        // window, or behind another application, must stay visible to the z-order checks.
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
    /// Sends keys only to the foreground window. The <see cref="ForegroundGuard"/> decides first: a foreign
    /// foreground is re-asserted, and a failure of the main window to become foreground is a failed check
    /// that stops the batch before any key is typed.
    /// </summary>
    public void SendKeys(params string[] keys)
    {
        if (keys.Length == 0)
        {
            return;
        }

        var checkId = ForegroundCheckId;
        if (!EnsureForeground(TimeSpan.FromSeconds(3)))
        {
            var notes = $"The main window 0x{MainHwnd.ToInt64():X} of '{_exeName}' is not the foreground window; no key was sent.";
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

    /// <summary>The check id for the main window's foreground state before input.</summary>
    internal string ForegroundCheckId => $"gui.foreground.{Path.GetFileNameWithoutExtension(_exePath).ToLowerInvariant()}";

    public void Close()
    {
        if (_process.HasExited)
        {
            return;
        }

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

    /// <summary>
    /// Registers work that runs after this session's own process has exited, in reverse order of registration.
    /// Used to restore state the application wrote on exit. A failing action is recorded as a failed
    /// <c>session.cleanup.&lt;name&gt;</c> check and does not stop the others.
    /// </summary>
    public void OnDispose(string name, Action action) => _onDispose.Add((name, action));

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
            Close();
        }
        finally
        {
            _automation.Dispose();
            _process.Dispose();
            for (int i = _onDispose.Count - 1; i >= 0; i--)
            {
                (string name, Action action) = _onDispose[i];
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    E2EResults.Record("session.cleanup." + name, false, ex.Message);
                }
            }
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
            if (TryParseKey(parts[0], out var single))
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
            if (!TryParseKey(parts[i], out keys[i]))
            {
                throw new ArgumentException($"Unknown key '{parts[i]}' in chord '{key}'.", nameof(key));
            }
        }

        Keyboard.TypeSimultaneously(keys);
    }

    /// <summary>
    /// Maps one key name to its virtual key. Accepts the <see cref="VirtualKeyShort"/> names (<c>ESCAPE</c>, <c>F10</c>,
    /// <c>KEY_T</c>, <c>DELETE</c>), a single letter or digit (<c>T</c>, <c>5</c>, which are the <c>KEY_</c> names),
    /// and <c>ENTER</c>, whose enum name is <c>RETURN</c>. A name that is not a key is false, never a number.
    /// </summary>
    private static bool TryParseKey(string name, out VirtualKeyShort key)
    {
        key = default;
        var normalized = name.Trim();
        if (normalized.Length == 0 || char.IsDigit(normalized[0]) && normalized.Length > 1)
        {
            return false;
        }

        if (normalized.Length == 1 && char.IsAsciiLetterOrDigit(normalized[0]))
        {
            normalized = "KEY_" + char.ToUpperInvariant(normalized[0]);
        }
        else if (string.Equals(normalized, "ENTER", StringComparison.OrdinalIgnoreCase))
        {
            normalized = nameof(VirtualKeyShort.RETURN);
        }

        return Enum.TryParse(normalized, ignoreCase: true, out key) && Enum.IsDefined(key);
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
    /// Returns true once the main window is the foreground window. The decision belongs to
    /// <see cref="ForegroundGuard.Ensure"/>: no re-assert when the app itself holds the foreground, and a
    /// re-assert only after a foreign-foreground event. A false result is already recorded as a failed check.
    /// </summary>
    internal bool EnsureForeground(TimeSpan? timeout = null) =>
        ForegroundGuard.Ensure(MainHwnd, ForegroundCheckId, timeout);
}

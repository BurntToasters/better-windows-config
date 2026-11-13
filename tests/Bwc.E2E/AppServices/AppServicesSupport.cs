using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using Bwc.E2E.List;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace Bwc.E2E.AppServices;

/// <summary>
/// A Registry Editor process that a test starts with its own command line. The shared
/// <see cref="AppSession"/> takes no arguments, so the file import, /m, and second launch checks
/// use this wrapper. It follows the same rules: the data root is always set, the compatibility
/// layer is RunAsInvoker so no UAC prompt can appear, and the process is killed on dispose.
/// </summary>
internal sealed class RegeditProcess : IDisposable
{
    private readonly Process _process;
    private readonly UIA3Automation _automation;
    private bool _disposed;

    private RegeditProcess(Process process, UIA3Automation automation, IntPtr mainHwnd)
    {
        _process = process;
        _automation = automation;
        MainHwnd = mainHwnd;
    }

    public IntPtr MainHwnd { get; }

    public int ProcessId => _process.Id;

    public bool HasExited
    {
        get
        {
            _process.Refresh();
            return _process.HasExited;
        }
    }

    public Window Window => _automation.FromHandle(MainHwnd).AsWindow();

    /// <summary>The UI Automation window for a native or owned dialog of this process.</summary>
    public Window WindowOf(IntPtr hwnd) => _automation.FromHandle(hwnd).AsWindow();

    /// <summary>Starts the editor with the given arguments and waits for its main window.</summary>
    public static RegeditProcess Start(
        E2EConfig config,
        string dataRoot,
        IReadOnlyList<string> arguments,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        Process process = StartProcess(config, dataRoot, arguments, environment);
        UIA3Automation automation = new();
        IntPtr hwnd;
        try
        {
            hwnd = WaitForMainWindow(process, timeout ?? TimeSpan.FromSeconds(30));
        }
        catch
        {
            automation.Dispose();
            AppServicesSupport.Kill(process);
            process.Dispose();
            throw;
        }

        return new RegeditProcess(process, automation, hwnd);
    }

    /// <summary>Starts an editor process without waiting for a window, for launches that exit at once.</summary>
    public static Process StartProcess(
        E2EConfig config,
        string dataRoot,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo(config.RegeditExePath)
        {
            UseShellExecute = false,
            WorkingDirectory = config.PublishDir,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["__COMPAT_LAYER"] = "RunAsInvoker";
        startInfo.Environment[E2EConfig.DataRootEnvironmentVariable] = dataRoot;
        if (environment is not null)
        {
            foreach (KeyValuePair<string, string> pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Process.Start returned no process for '{config.RegeditExePath}'.");
    }

    public AutomationElement? Find(string automationId)
    {
        try
        {
            return Window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public AutomationElement? WaitFor(string automationId, TimeSpan timeout)
    {
        AutomationElement? found = null;
        _ = ControlTestSupport.WaitUntil(() => (found = Find(automationId)) is not null, timeout);
        return found;
    }

    /// <summary>Opens and invokes a chain of menu commands, such as View then Theme then Dark.</summary>
    public void InvokeMenu(params int[] commandIds)
    {
        Focus();
        foreach (int commandId in commandIds)
        {
            AutomationElement? element = WaitFor("MenuCommand_" + commandId, TimeSpan.FromSeconds(10));
            if (element is null)
            {
                throw new InvalidOperationException($"Menu command {commandId} did not appear.");
            }

            if (element.Patterns.ExpandCollapse.IsSupported && commandIds.Length > 1 && commandId != commandIds[^1])
            {
                element.Patterns.ExpandCollapse.Pattern.Expand();
                Thread.Sleep(400);
                continue;
            }

            // A real click on the command. UI Automation Invoke waits for the command to return, and a
            // command that opens a modal dialog would hold the test thread until that dialog closes.
            // The click goes to whatever window is under the point, so a flyout that is not on top is a
            // product finding, not something the harness hides.
            var bounds = element.BoundingRectangle;
            AppServicesSupport.Click(bounds.Left + (bounds.Width / 2), bounds.Top + (bounds.Height / 2));
            Thread.Sleep(400);
        }
    }

    /// <summary>Makes the main window the foreground window through the guard. Returns false when it is not.</summary>
    public bool Focus() => ForegroundGuard.Ensure(MainHwnd, "gui.foreground.regedit");

    /// <summary>
    /// Closes the window the way a user does: the window takes the foreground, then Alt+F4 goes in as raw
    /// keyboard input. Returns false when the window never took the foreground or the process did not exit
    /// in time. The process is killed in that case, so the test can go on.
    /// </summary>
    public bool CloseWithAltF4(TimeSpan timeout)
    {
        if (_process.HasExited)
        {
            return true;
        }

        bool foreground = ForegroundGuard.Ensure(MainHwnd, "gui.foreground.regedit.close");
        if (foreground)
        {
            RawKeyboard.WithHeld(RawKeyboard.Alt, () => RawKeyboard.Tap(RawKeyboard.F4));
        }

        bool exited = _process.WaitForExit(timeout);
        if (!exited)
        {
            AppServicesSupport.Kill(_process);
            _ = _process.WaitForExit(TimeSpan.FromSeconds(5));
        }

        return foreground && exited;
    }

    /// <summary>Closes the window the way a user does, so the settings are saved on close.</summary>
    public void Close()
    {
        if (_process.HasExited)
        {
            return;
        }

        try
        {
            _ = _process.CloseMainWindow();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (!_process.WaitForExit(TimeSpan.FromSeconds(15)))
        {
            AppServicesSupport.Kill(_process);
            _ = _process.WaitForExit(TimeSpan.FromSeconds(5));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            Close();
        }
        finally
        {
            _automation.Dispose();
            _process.Dispose();
        }
    }

    private static IntPtr WaitForMainWindow(Process process, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            process.Refresh();
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"BwcRegedit exited with code 0x{process.ExitCode:X8} before showing a main window.");
            }

            IntPtr hwnd = process.MainWindowHandle;
            if (hwnd != IntPtr.Zero && AppServicesSupport.IsWindow(hwnd) && Win32.IsVisible(hwnd))
            {
                return hwnd;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"BwcRegedit showed no main window within {timeout.TotalSeconds:0} s.");
            }

            Thread.Sleep(200);
        }
    }
}

/// <summary>
/// Win32 and waiting helpers for the app services checks. Every handle is a window of a process
/// the test started. Mouse and keyboard use the OS input path, not UI Automation patterns.
/// </summary>
internal static class AppServicesSupport
{
    private const int GwOwner = 4;
    private const int GwChild = 5;
    private const int SwMinimize = 6;
    private const int SwRestore = 9;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const uint SpiGetWorkArea = 0x0030;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventfMove = 0x0001;
    private const uint MouseEventfAbsolute = 0x8000;
    private const uint MouseEventfVirtualDesk = 0x4000;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const uint GaRoot = 2;
    private static readonly TimeSpan ForegroundWait = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan ButtonReadyWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ButtonStablePause = TimeSpan.FromMilliseconds(150);

    /// <summary>A directory under the run's data root that holds no settings from an earlier test.</summary>
    internal static string FreshDataRoot(E2EConfig config, string scope)
    {
        string path = Path.Combine(config.DataRoot, scope);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        return config.DataRootFor(scope);
    }

    internal static bool WaitUntil(Func<bool> condition, TimeSpan timeout) =>
        ControlTestSupport.WaitUntil(condition, timeout);

    internal static void Kill(Process process)
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
            // Already exited.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Access was lost while the process was exiting.
        }
    }

    internal static bool IsWindow(IntPtr hwnd) => Win32.IsWindow(hwnd);

    internal static bool IsForeground(IntPtr hwnd) => Win32.IsForeground(hwnd);

    internal static bool IsEnabled(IntPtr hwnd) => Win32.IsWindowEnabled(hwnd);

    /// <summary>True when the window is minimized.</summary>
    internal static bool IsMinimized(IntPtr hwnd) => IsIconic(hwnd);

    internal static void Minimize(IntPtr hwnd) => ShowWindow(hwnd, SwMinimize);

    internal static void Restore(IntPtr hwnd) => ShowWindow(hwnd, SwRestore);

    /// <summary>Moves and sizes a window in physical pixels.</summary>
    internal static void MoveAndResize(IntPtr hwnd, int x, int y, int width, int height) =>
        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SwpNoZOrder | SwpNoActivate);

    internal static (int Left, int Top, int Right, int Bottom) RectOf(IntPtr hwnd)
    {
        _ = Win32.GetWindowRect(hwnd, out Win32.NativeRect rect);
        return (rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    internal static (int Left, int Top, int Right, int Bottom) WorkArea()
    {
        RECT area = default;
        _ = SystemParametersInfo(SpiGetWorkArea, 0, ref area, 0);
        return (area.Left, area.Top, area.Right, area.Bottom);
    }

    internal static (int Width, int Height) ScreenSize() =>
        (GetSystemMetrics(SmCxScreen), GetSystemMetrics(SmCyScreen));

    internal static uint DpiOf(IntPtr hwnd)
    {
        uint dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 96u : dpi;
    }

    /// <summary>
    /// Makes a window of the app the foreground window for input, through <see cref="ForegroundGuard"/>. A false
    /// result is already recorded as the failed <paramref name="checkId"/> check.
    /// </summary>
    internal static bool Activate(IntPtr hwnd, string checkId) => ForegroundGuard.Ensure(hwnd, checkId);

    /// <summary>
    /// Dismisses a modal message with Enter, its default button. The message is checked for z order and
    /// foreground first. Enter is sent only through the guard, so the key cannot reach another window. A
    /// retry happens only after a foreign-foreground event, at most three attempts, and the attempt count is
    /// recorded in a note. Returns true when the message is gone.
    /// </summary>
    internal static bool DismissMessage(IntPtr message, TimeSpan timeout)
    {
        if (!IsWindow(message))
        {
            return true;
        }

        OwnedWindowChecks.Record(message);
        int attempts = 0;
        int foreignBefore = ForegroundGuard.ForeignEvents;
        DateTime deadline = DateTime.UtcNow + timeout;
        while (IsWindow(message) && DateTime.UtcNow < deadline && attempts < MaxInputAttempts)
        {
            attempts++;
            if (!Activate(message, "dialog.message.foreground"))
            {
                break;
            }

            Keyboard.Press(VirtualKeyShort.ENTER);
            if (WaitUntil(() => !IsWindow(message), TimeSpan.FromSeconds(2)))
            {
                break;
            }

            if (ForegroundGuard.ForeignEvents == foreignBefore)
            {
                break;
            }

            foreignBefore = ForegroundGuard.ForeignEvents;
        }

        bool gone = !IsWindow(message);
        if (attempts > 1)
        {
            E2EResults.Note("harness.retry.dismissmessage", $"Enter on the message was sent {attempts} time(s); gone={gone}.");
        }

        if (!gone && attempts > 0)
        {
            E2EResults.Record(
                "dialog.message.dismissed",
                false,
                $"The message 0x{message.ToInt64():X} stayed open after {attempts} Enter attempt(s).");
        }

        return gone;
    }

    /// <summary>
    /// Input attempts a helper makes after foreign-foreground events. The first failure of a product step is final.
    /// </summary>
    internal const int MaxInputAttempts = 3;

    /// <summary>A real left click at a screen point.</summary>
    internal static void Click(int x, int y)
    {
        _ = SetCursorPos(x, y);
        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
    }

    /// <summary>
    /// The UI Automation names under a dialog, joined into one text. Message dialogs expose it this way.
    /// The dialog is raised first, so the read cannot be answered by the window that covers it.
    /// </summary>
    internal static string DialogText(IntPtr dialog)
    {
        using var automation = new UIA3Automation();
        AutomationElement window = automation.FromHandle(dialog);
        return string.Join(" | ", window.FindAllDescendants().Select(element => ControlTestSupport.SafeName(element)));
    }

    /// <summary>
    /// Reads the dialog text until <paramref name="matches"/> accepts it, within the timeout. The
    /// dialog is raised before each read, and a dialog that is still building its content is read
    /// again instead of being reported as empty. The last text is returned when the timeout passes.
    /// </summary>
    internal static string WaitForDialogText(IntPtr dialog, Func<string, bool> matches, TimeSpan timeout)
    {
        string text = string.Empty;
        _ = WaitUntil(
            () =>
            {
                if (!IsWindow(dialog))
                {
                    return false;
                }

                text = DialogText(dialog);
                return matches(text);
            },
            timeout);
        return text;
    }

    /// <summary>
    /// Clicks the dialog button whose name matches, with a real mouse press. A press is sent only when the button is
    /// ready: the dialog is the foreground window, the button is enabled and on screen with the same bounds on two
    /// reads, the window at the press point is the dialog, and UI Automation reports the button at that point. A
    /// button that is not ready is waited for, and a press is never sent to a point that is not the button. A press
    /// that leaves the dialog open is repeated at most <see cref="MaxInputAttempts"/> times, and only when a
    /// foreign-foreground event happened during it. Any other press that leaves the dialog open fails, and a note
    /// records the state at that press. A dialog that is still open at the end throws, so the caller fails.
    /// </summary>
    internal static void ClickButton(RegeditProcess app, IntPtr dialog, string name)
    {
        if (!IsWindow(dialog))
        {
            return;
        }

        OwnedWindowChecks.Record(dialog);
        int attempts = 0;
        string lastState = string.Empty;
        while (IsWindow(dialog) && attempts < MaxInputAttempts)
        {
            attempts++;
            WaitForForeground(dialog);
            if (!Activate(dialog, "dialog.button.foreground." + OwnedWindowChecks.LabelOf(dialog)))
            {
                break;
            }

            System.Drawing.Point? press = WaitForPressPoint(app, dialog, name);
            if (press is not { } point)
            {
                break;
            }

            // Counted from the press, so a foreign event during the wait above does not make this press a retry.
            int foreignAtPress = ForegroundGuard.ForeignEvents;
            Click(point.X, point.Y);
            if (WaitUntil(() => !IsWindow(dialog), TimeSpan.FromSeconds(2)))
            {
                break;
            }

            lastState = DescribePress(dialog, name, point);
            if (ForegroundGuard.ForeignEvents == foreignAtPress)
            {
                break;
            }
        }

        if (attempts > 1)
        {
            E2EResults.Note("harness.retry.clickbutton", $"'{name}' was clicked {attempts} time(s); dialog open={IsWindow(dialog)}.");
        }

        if (IsWindow(dialog))
        {
            if (lastState.Length > 0)
            {
                E2EResults.Note("harness.click.notclosed", $"'{name}' press left the dialog open. {lastState}");
            }

            throw new InvalidOperationException(
                $"The '{name}' button did not close the dialog 0x{dialog.ToInt64():X} after {attempts} attempt(s).");
        }
    }

    /// <summary>
    /// Waits, up to <see cref="ForegroundWait"/>, for the dialog to be the foreground window. Only polls: the
    /// activation itself is made by the caller through the guard, which re-asserts a foreign foreground.
    /// </summary>
    private static void WaitForForeground(IntPtr dialog)
    {
        _ = WaitUntil(
            () => !IsWindow(dialog) || ForegroundGuard.Observe(dialog, out _) == ForegroundState.Target,
            ForegroundWait);
    }

    /// <summary>
    /// The screen point of a dialog button that is ready to press, read fresh. Returns null when the dialog closed
    /// while the button was being looked for. Throws when the button is not ready before <see cref="ButtonReadyWait"/>,
    /// with the state that stopped it, so the failure names the cause and no press was sent.
    /// </summary>
    private static System.Drawing.Point? WaitForPressPoint(RegeditProcess app, IntPtr dialog, string name)
    {
        DateTime deadline = DateTime.UtcNow + ButtonReadyWait;
        while (true)
        {
            if (!IsWindow(dialog))
            {
                return null;
            }

            System.Drawing.Point? point;
            string state;
            try
            {
                point = ReadyPressPoint(app, dialog, name, out state);
            }
            catch (Exception) when (!IsWindow(dialog))
            {
                return null;
            }

            if (point is not null)
            {
                return point;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException(
                    $"The '{name}' button of dialog 0x{dialog.ToInt64():X} was not ready to press: {state}. No press was sent.");
            }

            Thread.Sleep(100);
        }
    }

    /// <summary>
    /// The center of the named button when it is ready now, or null with the reason in <paramref name="state"/>.
    /// </summary>
    private static System.Drawing.Point? ReadyPressPoint(RegeditProcess app, IntPtr dialog, string name, out string state)
    {
        AutomationElement? button = FindButton(app, dialog, name);
        if (button is null)
        {
            state = $"the dialog has no '{name}' button";
            return null;
        }

        if (!button.IsEnabled)
        {
            state = $"the '{name}' button is disabled";
            return null;
        }

        if (button.IsOffscreen)
        {
            state = $"the '{name}' button is off screen";
            return null;
        }

        System.Drawing.Rectangle first = button.BoundingRectangle;
        if (first.Width <= 0 || first.Height <= 0)
        {
            state = $"the '{name}' button has no bounds";
            return null;
        }

        Thread.Sleep(ButtonStablePause);
        System.Drawing.Rectangle second = button.BoundingRectangle;
        if (second != first)
        {
            state = $"the '{name}' button is still moving: {first} then {second}";
            return null;
        }

        var center = new System.Drawing.Point(first.Left + (first.Width / 2), first.Top + (first.Height / 2));
        AutomationElement? atPoint;
        try
        {
            using var automation = new UIA3Automation();
            atPoint = automation.FromPoint(center);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            state = $"UI Automation has no element at {center}: {ex.Message}";
            return null;
        }

        if (!IsButtonOrChildNamed(atPoint, name))
        {
            state = $"UI Automation reports '{DescribeElement(atPoint)}' at {center}, not the '{name}' button";
            return null;
        }

        IntPtr root = RootWindowAt(center);
        if (root != dialog)
        {
            state = $"the window at {center} is 0x{root.ToInt64():X} ('{TitleOf(root)}'), not the dialog";
            return null;
        }

        state = string.Empty;
        return center;
    }

    /// <summary>True when the element is the named button, or a child of it within three levels.</summary>
    private static bool IsButtonOrChildNamed(AutomationElement? element, string name)
    {
        for (int depth = 0; element is not null && depth < 3; depth++)
        {
            if (element.ControlType == ControlType.Button &&
                string.Equals((element.Name ?? string.Empty).Replace("&", string.Empty), name, StringComparison.Ordinal))
            {
                return true;
            }

            element = element.Parent;
        }

        return false;
    }

    private static string DescribeElement(AutomationElement? element) =>
        element is null ? "nothing" : $"{element.ControlType} '{element.Name}'";

    /// <summary>The top-level window that owns the window at a screen point, or zero.</summary>
    private static IntPtr RootWindowAt(System.Drawing.Point point)
    {
        IntPtr hit = WindowFromPoint(new POINT { X = point.X, Y = point.Y });
        return hit == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hit, GaRoot);
    }

    /// <summary>The state after a press that left the dialog open: foreground and the window at the point.</summary>
    private static string DescribePress(IntPtr dialog, string name, System.Drawing.Point point)
    {
        _ = ForegroundGuard.Observe(dialog, out string foreground);
        IntPtr root = RootWindowAt(point);
        return $"Pressed '{name}' at {point}. Foreground: {foreground}. Window at the point: 0x{root.ToInt64():X} " +
            $"('{TitleOf(root)}'), dialog 0x{dialog.ToInt64():X}.";
    }

    private static AutomationElement? FindButton(RegeditProcess app, IntPtr dialog, string name) =>
        app.WindowOf(dialog)
            .FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .FirstOrDefault(b => string.Equals((b.Name ?? string.Empty).Replace("&", string.Empty), name, StringComparison.Ordinal));

    /// <summary>
    /// A real left button drag from one screen point to another. The moves are absolute mouse_event moves on the
    /// virtual desktop. A SetCursorPos move with the button held does not reach the splitter or a column divider
    /// here, so the drag reads as a press and a release with no movement.
    /// </summary>
    internal static void Drag(int fromX, int fromY, int toX, int toY)
    {
        MoveCursor(fromX, fromY);
        Thread.Sleep(150);
        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(150);
        const int steps = 12;
        for (int step = 1; step <= steps; step++)
        {
            MoveCursor(fromX + ((toX - fromX) * step / steps), fromY + ((toY - fromY) * step / steps));
            Thread.Sleep(40);
        }

        Thread.Sleep(200);
        mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
    }

    /// <summary>Moves the cursor with an absolute mouse_event, which sends move messages during a held drag.</summary>
    private static void MoveCursor(int x, int y)
    {
        int left = GetSystemMetrics(SmXVirtualScreen);
        int top = GetSystemMetrics(SmYVirtualScreen);
        int width = Math.Max(1, GetSystemMetrics(SmCxVirtualScreen) - 1);
        int height = Math.Max(1, GetSystemMetrics(SmCyVirtualScreen) - 1);
        double nx = Math.Clamp((x - left) * 65535.0 / width, 0, 65535);
        double ny = Math.Clamp((y - top) * 65535.0 / height, 0, 65535);
        mouse_event(MouseEventfMove | MouseEventfAbsolute | MouseEventfVirtualDesk, (uint)Math.Round(nx), (uint)Math.Round(ny), 0, UIntPtr.Zero);
    }

    /// <summary>The center point of a window rectangle.</summary>
    internal static (int X, int Y) CenterOf(IntPtr hwnd)
    {
        var rect = RectOf(hwnd);
        return ((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
    }

    /// <summary>
    /// Finds a visible top-level window owned by <paramref name="owner"/>. The class and title are
    /// optional filters; a title filter is an exact match. <paramref name="except"/> skips one handle,
    /// so a caller that waits for the next dialog cannot be answered by the one it is closing.
    /// </summary>
    internal static IntPtr FindOwnedWindow(IntPtr owner, string? className, string? title, IntPtr except = default)
    {
        IntPtr found = IntPtr.Zero;
        _ = EnumWindows(
            (hwnd, _) =>
            {
                if (hwnd == except || GetWindow(hwnd, GwOwner) != owner || !Win32.IsVisible(hwnd))
                {
                    return true;
                }

                if (className is not null && ClassOf(hwnd) != className)
                {
                    return true;
                }

                if (title is not null && TitleOf(hwnd) != title)
                {
                    return true;
                }

                found = hwnd;
                return false;
            },
            IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Waits for an owned window to appear and returns its handle, or zero on timeout. A window that is found gets
    /// the z-order and foreground checks (<see cref="OwnedWindowChecks"/>) once, as it appeared, without any raise.
    /// </summary>
    internal static IntPtr WaitForOwnedWindow(
        IntPtr owner,
        string? className,
        string? title,
        TimeSpan timeout,
        IntPtr except = default)
    {
        IntPtr found = IntPtr.Zero;
        _ = WaitUntil(
            () =>
            {
                found = FindOwnedWindow(owner, className, title, except);
                if (found != IntPtr.Zero)
                {
                    OwnedWindowChecks.Record(found);
                }

                return found != IntPtr.Zero;
            },
            timeout);
        return found;
    }

    /// <summary>
    /// Waits for an owned window whose dialog text matches. Windows that do not match are skipped,
    /// including the import progress dialog, which has the same title as the result message. The
    /// owned windows are scanned again until the matching window appears or the timeout passes. The
    /// window that matches gets the z-order and foreground checks once, as it appeared.
    /// </summary>
    internal static IntPtr WaitForOwnedWindowText(
        IntPtr owner,
        string? className,
        Func<string, bool> matches,
        TimeSpan timeout,
        params IntPtr[] exclude)
    {
        IntPtr found = IntPtr.Zero;
        _ = WaitUntil(
            () =>
            {
                found = FindOwnedWindowText(owner, className, matches, exclude);
                if (found != IntPtr.Zero)
                {
                    OwnedWindowChecks.Record(found);
                }

                return found != IntPtr.Zero;
            },
            timeout);
        return found;
    }

    /// <summary>
    /// Finds the first owned window whose dialog text matches. The caller's excluded handles are
    /// skipped so a confirmation or a progress dialog that is still closing cannot answer.
    /// </summary>
    internal static IntPtr FindOwnedWindowText(
        IntPtr owner,
        string? className,
        Func<string, bool> matches,
        params IntPtr[] exclude)
    {
        IntPtr found = IntPtr.Zero;
        _ = EnumWindows(
            (hwnd, _) =>
            {
                if (exclude.Contains(hwnd) || GetWindow(hwnd, GwOwner) != owner || !Win32.IsVisible(hwnd))
                {
                    return true;
                }

                if (className is not null && ClassOf(hwnd) != className)
                {
                    return true;
                }

                string text = DialogText(hwnd);
                if (!matches(text))
                {
                    return true;
                }

                found = hwnd;
                return false;
            },
            IntPtr.Zero);
        return found;
    }

    /// <summary>Every top-level window owned by <paramref name="owner"/>, with its class and title.</summary>
    internal static IReadOnlyList<(IntPtr Hwnd, string Class, string Title)> OwnedWindows(IntPtr owner)
    {
        var result = new List<(IntPtr, string, string)>();
        _ = EnumWindows(
            (hwnd, _) =>
            {
                if (GetWindow(hwnd, GwOwner) == owner)
                {
                    result.Add((hwnd, ClassOf(hwnd), TitleOf(hwnd)));
                }

                return true;
            },
            IntPtr.Zero);
        return result;
    }

    /// <summary>The text of every child window, in z order. Used for static text in native dialogs.</summary>
    internal static IReadOnlyList<string> ChildTexts(IntPtr parent)
    {
        var texts = new List<string>();
        _ = EnumChildWindows(
            parent,
            (hwnd, _) =>
            {
                string text = TitleOf(hwnd);
                if (text.Length > 0)
                {
                    texts.Add(text);
                }

                return true;
            },
            IntPtr.Zero);
        return texts;
    }

    internal static string ClassOf(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        _ = GetClassNameW(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    internal static string TitleOf(IntPtr hwnd)
    {
        int length = GetWindowTextLengthW(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        _ = GetWindowTextW(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    internal static IntPtr ChildOf(IntPtr parent, int controlId) => GetDlgItem(parent, controlId);

    /// <summary>The first child window in z order, or zero. The size combo of the font dialog has its edit here.</summary>
    internal static IntPtr FirstChildOf(IntPtr parent) => GetWindow(parent, GwChild);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

#pragma warning disable SYSLIB1054 // Small set of user32 calls. LibraryImport plumbing is not needed here.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, int relationship);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDlgItem(IntPtr dialog, int controlId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref RECT value, uint winIni);
#pragma warning restore SYSLIB1054
}

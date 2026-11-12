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
    private readonly WindowWatchdog _watchdog;
    private bool _disposed;

    private RegeditProcess(Process process, UIA3Automation automation, IntPtr mainHwnd)
    {
        _process = process;
        _automation = automation;
        MainHwnd = mainHwnd;

        // Same rule as AppSession: every visible window of this process, including owned dialogs,
        // stays above other applications until Close.
        _ = Win32.SetTopmost(mainHwnd, true);
        _watchdog = new WindowWatchdog(unchecked((uint)process.Id));
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
        // A menu flyout or an owned dialog is a separate top level window. Raise the process windows
        // before every read so the element is not read or clicked while another window covers it.
        RaiseWindows();
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
            // Raise the flyout first, or the click would land on the topmost main window behind it.
            RaiseWindows();
            var bounds = element.BoundingRectangle;
            AppServicesSupport.Click(bounds.Left + (bounds.Width / 2), bounds.Top + (bounds.Height / 2));
            Thread.Sleep(400);
        }
    }

    /// <summary>Raises the visible top level windows of this process, owned dialogs included, without activating one.</summary>
    public void RaiseWindows() => _ = Win32.RaiseProcessWindows(unchecked((uint)ProcessId));

    /// <summary>Brings the main window forward with a real click on its title bar when focus is refused.</summary>
    public void Focus()
    {
        RaiseWindows();
        if (AppServicesSupport.ForceForeground(MainHwnd))
        {
            return;
        }

        var rect = AppServicesSupport.RectOf(MainHwnd);
        AppServicesSupport.Click(rect.Left + ((rect.Right - rect.Left) / 2), rect.Top + 8);
        _ = AppServicesSupport.WaitUntil(() => AppServicesSupport.IsForeground(MainHwnd), TimeSpan.FromSeconds(3));
    }

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

        bool foreground = AppServicesSupport.ForceForeground(MainHwnd);
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
        _watchdog.Dispose();
        if (_process.HasExited)
        {
            return;
        }

        _ = Win32.SetTopmost(MainHwnd, false);
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
            _watchdog.Dispose();
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

    /// <summary>Activates a window. Returns true when it became the foreground window within the wait.</summary>
    internal static bool ForceForeground(IntPtr hwnd) => Win32.ForceForeground(hwnd, TimeSpan.FromSeconds(2));

    /// <summary>
    /// Raises one window of the app above the app's other windows and above foreign windows, without
    /// activating it. The process windows are raised first, then this window last, so it ends on top
    /// of its owner and of any other popup.
    /// </summary>
    internal static void RaiseWindow(IntPtr hwnd)
    {
        uint processId = Win32.ProcessIdOf(hwnd);
        if (processId != 0)
        {
            _ = Win32.RaiseProcessWindows(processId);
        }

        _ = Win32.SetTopmost(hwnd, true);
    }

    /// <summary>
    /// Dismisses a modal message with Enter, its default button. The message and its process windows
    /// are raised first, and Enter is sent only after the message has the foreground, so the key
    /// cannot reach another window. A message that does not take the foreground, or stays open after
    /// one press, is tried again until the timeout. Returns true when the message is gone.
    /// </summary>
    internal static bool DismissMessage(IntPtr message, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (IsWindow(message) && DateTime.UtcNow < deadline)
        {
            RaiseWindow(message);
            if (ForceForeground(message))
            {
                Keyboard.Press(VirtualKeyShort.ENTER);
            }

            _ = WaitUntil(() => !IsWindow(message), TimeSpan.FromSeconds(2));
        }

        return !IsWindow(message);
    }

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
        RaiseWindow(dialog);
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
    /// Clicks the dialog button whose name matches, after the dialog and its process windows are raised
    /// and the dialog takes the foreground. Each click uses fresh bounds. A click that leaves the dialog
    /// open is retried within a short bounded time, so a click that landed while the dialog was moving is
    /// not the end of the test. A missing button throws, so the caller fails with the button name.
    /// </summary>
    internal static void ClickButton(RegeditProcess app, IntPtr dialog, string name)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (true)
        {
            if (!IsWindow(dialog))
            {
                return;
            }

            RaiseWindow(dialog);
            AutomationElement? button;
            try
            {
                button = FindButton(app, dialog, name);
            }
            catch (Exception) when (!IsWindow(dialog))
            {
                return;
            }

            if (button is null)
            {
                if (!IsWindow(dialog))
                {
                    return;
                }

                if (DateTime.UtcNow >= deadline)
                {
                    throw new InvalidOperationException($"The dialog has no '{name}' button.");
                }

                Thread.Sleep(100);
                continue;
            }

            var rect = button.BoundingRectangle;
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new InvalidOperationException($"The dialog's '{name}' button has no clickable bounds.");
                }

                Thread.Sleep(100);
                continue;
            }

            _ = ForceForeground(dialog);
            Click((int)(rect.Left + (rect.Width / 2)), (int)(rect.Top + (rect.Height / 2)));
            if (WaitUntil(() => !IsWindow(dialog), TimeSpan.FromSeconds(1)) || DateTime.UtcNow >= deadline)
            {
                return;
            }
        }
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
    /// Waits for an owned window to appear and returns its handle, or zero on timeout. A window that
    /// is found is raised above its owner and above foreign windows before it is returned, so the
    /// caller's read or click cannot land on another window.
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
                    RaiseWindow(found);
                }

                return found != IntPtr.Zero;
            },
            timeout);
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

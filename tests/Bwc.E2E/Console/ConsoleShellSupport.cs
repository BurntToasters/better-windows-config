using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using Bwc.E2E.List;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.ConsoleFrame;

/// <summary>
/// One running Local Group Policy Editor with the console shell, and the helpers the Console_Shell tests share:
/// element lookup by automation id, focus reading, raw keys, the splitter drag, window placement, and the
/// artifacts (a measurements file and a window picture under artifacts/e2e/visual/console-shell).
/// </summary>
internal sealed class ShellRun : IDisposable
{
    internal const string ShellId = "ConsoleMenuBar";
    internal const string TreePaneId = "TreeControl";
    internal const string ResultPaneId = "ConsoleResultPane";
    internal const string SplitterId = "ConsoleSplitter";
    internal const string ToolbarId = "ConsoleToolbar";
    internal const string MenuBarId = "ConsoleMenuBar";
    internal const string StatusBarId = "StatusBar";
    internal const string StatusPaneId = "StatusBar.Pane0";
    internal const string TreeControlId = "TreeControl";

    internal const ushort VkTab = 0x09;
    internal const ushort VkEscape = 0x1B;
    internal const ushort VkLeft = 0x25;
    internal const ushort VkRight = 0x27;
    internal const ushort VkDown = 0x28;
    internal const ushort VkF6 = 0x75;
    internal const ushort VkF10 = 0x79;
    internal const ushort VkApps = 0x5D;
    internal const ushort VkF = 0x46;
    internal const ushort VkV = 0x56;
    internal const ushort VkX = 0x58;

    internal static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan KeySettle = TimeSpan.FromMilliseconds(300);

    private static readonly string[] StandardButtons =
    [
        "Back",
        "Forward",
        "UpOneLevel",
        "ShowHideConsoleTree",
        "Properties",
        "Refresh",
        "ExportList",
        "Help",
    ];

    private readonly Dictionary<string, object?> _measurements = new(StringComparer.Ordinal);

    private ShellRun(AppSession session, UIA3Automation automation, string name)
    {
        Session = session;
        Automation = automation;
        Name = name;
        Dpi = GetDpiForWindow(session.MainHwnd);
        Scale = Dpi / 96.0;
    }

    internal AppSession Session { get; }

    internal UIA3Automation Automation { get; }

    internal string Name { get; }

    internal uint Dpi { get; }

    internal double Scale { get; }

    internal static IReadOnlyList<string> StandardButtonIds => StandardButtons;

    /// <summary>Starts the editor under a data root scope, with extra environment, and waits for the shell.</summary>
    internal static ShellRun Open(string name, IReadOnlyDictionary<string, string>? extraEnvironment = null)
    {
        E2EConfig config = GuiGate.Config;
        config.EnsureExecutablesExist();
        string scope = "console-" + name;
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> pair in config.DataRootEnvironment(scope))
        {
            environment[pair.Key] = pair.Value;
        }

        foreach (KeyValuePair<string, string> pair in config.TempEnvironment(scope))
        {
            environment[pair.Key] = pair.Value;
        }

        if (extraEnvironment is not null)
        {
            foreach (KeyValuePair<string, string> pair in extraEnvironment)
            {
                environment[pair.Key] = pair.Value;
            }
        }

        var session = new AppSession(config, config.GpeditExePath, TimeSpan.FromSeconds(30), environment);
        E2EResults.CurrentSession = session;
        UIA3Automation? automation = null;
        try
        {
            automation = new UIA3Automation();
            _ = session.WaitForElement(ShellId, UiTimeout);
            _ = ControlTestSupport.EnsureForeground(session);
            return new ShellRun(session, automation, name);
        }
        catch (TimeoutException ex)
        {
            string dump = DescribeTree(session);
            automation?.Dispose();
            session.Dispose();
            throw new TimeoutException($"{ex.Message} Elements seen: {dump}", ex);
        }
        catch
        {
            automation?.Dispose();
            session.Dispose();
            throw;
        }
    }

    private static string DescribeTree(AppSession session)
    {
        try
        {
            return string.Join(
                "; ",
                session.Window.FindAllDescendants().Take(80).Select(e => $"{SafeType(e)}:{SafeId(e)}:{SafeName(e)}"));
        }
        catch (Exception ex)
        {
            return "(unreadable: " + ex.Message + ")";
        }
    }

    public void Dispose()
    {
        Automation.Dispose();
        Session.Dispose();
    }

    // ---- elements ---------------------------------------------------------------------------------

    internal AutomationElement? Find(string automationId) => Session.Find(automationId);

    internal static string ButtonId(string name) => "ConsoleToolbar_" + name;

    /// <summary>The element rectangle in physical pixels, or null when the element is absent or collapsed.</summary>
    internal Rectangle? Rect(string automationId)
    {
        try
        {
            AutomationElement? element = Find(automationId);
            if (element is null || element.IsOffscreen)
            {
                return null;
            }

            Rectangle rect = element.BoundingRectangle;
            return rect.Width <= 0 || rect.Height <= 0 ? null : rect;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal bool IsHidden(string automationId) => Rect(automationId) is null;

    internal double Logical(int physicalPixels) => physicalPixels / Scale;

    internal string StatusText() => SafeName(Find(StatusPaneId));

    internal string HelpTextOf(string automationId)
    {
        try
        {
            return Find(automationId)?.Properties.HelpText.ValueOrDefault ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    internal bool ButtonEnabled(string name) => Find(ButtonId(name))?.IsEnabled ?? false;

    internal string SelectedTreeNode()
    {
        const string marker = "Selected=";
        string help = HelpTextOf(TreeControlId);
        int at = help.IndexOf(marker, StringComparison.Ordinal);
        return at < 0 ? string.Empty : help[(at + marker.Length)..];
    }

    /// <summary>The pane of the shell that holds the keyboard focus: tree, result, splitter, toolbar, menu or other.</summary>
    internal string FocusedPane()
    {
        AutomationElement? focused;
        try
        {
            focused = Automation.FocusedElement();
        }
        catch (Exception)
        {
            return "none";
        }

        for (AutomationElement? current = focused; current is not null; current = SafeParent(current))
        {
            switch (SafeId(current))
            {
                case TreePaneId:
                    return "tree";
                case ResultPaneId:
                    return "result";
                case SplitterId:
                    return "splitter";
                case ToolbarId:
                    return "toolbar";
                case MenuBarId:
                    return "menu";
            }
        }

        return "other(" + FocusChain(focused) + ")";
    }

    private static string FocusChain(AutomationElement? start)
    {
        var parts = new List<string>();
        int depth = 0;
        for (AutomationElement? current = start; current is not null && depth < 8; current = SafeParent(current), depth++)
        {
            parts.Add(SafeType(current) + ":" + SafeId(current));
        }

        return string.Join(">", parts);
    }

    /// <summary>The automation id of the element that holds the keyboard focus, or an empty string.</summary>
    internal string FocusedId()
    {
        try
        {
            return SafeId(Automation.FocusedElement());
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    internal static string SafeName(AutomationElement? element)
    {
        try
        {
            return element?.Properties.Name.ValueOrDefault ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    internal static string SafeType(AutomationElement? element)
    {
        try
        {
            return element?.ControlType.ToString() ?? string.Empty;
        }
        catch (Exception)
        {
            return "?";
        }
    }

    internal static string SafeId(AutomationElement? element)
    {
        try
        {
            return element?.AutomationId ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static AutomationElement? SafeParent(AutomationElement element)
    {
        try
        {
            return element.Parent;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- keys and pointer -------------------------------------------------------------------------

    internal void Tap(ushort key)
    {
        _ = ControlTestSupport.EnsureForeground(Session);
        RawKeyboard.Tap(key);
        Thread.Sleep(KeySettle);
    }

    /// <summary>Holds a modifier while a key is tapped.</summary>
    internal void Chord(ushort modifier, ushort key)
    {
        _ = ControlTestSupport.EnsureForeground(Session);
        RawKeyboard.WithHeld(modifier, () => RawKeyboard.Tap(key));
        Thread.Sleep(KeySettle);
    }

    /// <summary>Clicks a tree row by its name so the keyboard focus is in the tree with that row selected.</summary>
    internal void ClickTreeNode(string name)
    {
        _ = ControlTestSupport.EnsureForeground(Session);
        AutomationElement? row = Session.Window.FindFirstDescendant(cf => cf.ByName(name));
        row?.Click();
        Thread.Sleep(KeySettle);

        // A click leaves the UIA focus on the window until a key arrives, so Tab reaches the tree as the shell tests do.
        for (int i = 0; i < 6 && FocusedPane() != "tree"; i++)
        {
            Tap(VkTab);
        }
    }

    internal static bool WaitUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? UiTimeout);
        do
        {
            try
            {
                if (condition())
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // The next poll retries.
            }

            Thread.Sleep(100);
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }

    /// <summary>Presses the left button at a point and moves it in steps, leaving the button down.</summary>
    internal static void DragStart(Point start, int dx)
    {
        MoveCursor(start);
        Thread.Sleep(150);
        Mouse.Down(MouseButton.Left);
        Thread.Sleep(150);
        DragMove(start, dx);
    }

    /// <summary>Moves the cursor by dx from the start point in steps, with the button already down.</summary>
    internal static void DragMove(Point start, int dx)
    {
        const int steps = 12;
        for (int i = 1; i <= steps; i++)
        {
            MoveCursor(new Point(start.X + (dx * i / steps), start.Y));
            Thread.Sleep(40);
        }

        Thread.Sleep(250);
    }

    internal static void DragEnd()
    {
        Mouse.Up(MouseButton.Left);
        Thread.Sleep(300);
    }

    private static void MoveCursor(Point target)
    {
        int left = GetSystemMetrics(SmXVirtualScreen);
        int top = GetSystemMetrics(SmYVirtualScreen);
        int width = Math.Max(1, GetSystemMetrics(SmCxVirtualScreen) - 1);
        int height = Math.Max(1, GetSystemMetrics(SmCyVirtualScreen) - 1);
        double x = Math.Clamp((target.X - left) * 65535.0 / width, 0, 65535);
        double y = Math.Clamp((target.Y - top) * 65535.0 / height, 0, 65535);
        mouse_event(MouseEventfMove | MouseEventfAbsolute | MouseEventfVirtualDesk, (uint)Math.Round(x), (uint)Math.Round(y), 0, UIntPtr.Zero);
    }

    // ---- window placement -------------------------------------------------------------------------

    internal static int ScreenWidth => GetSystemMetrics(SmCxScreen);

    internal Rectangle WindowRect()
    {
        _ = GetWindowRect(Session.MainHwnd, out NativeRect rect);
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    /// <summary>Places the main window at the given outer rectangle in physical pixels.</summary>
    internal void PlaceWindow(int left, int top, int width, int height)
    {
        _ = SetWindowPos(Session.MainHwnd, IntPtr.Zero, left, top, width, height, SwpNoZOrder | SwpNoActivate);
        Thread.Sleep(600);
    }

    // ---- artifacts --------------------------------------------------------------------------------

    internal void Measure(string key, object? value) => _measurements[key] = value;

    /// <summary>Writes the measurements as JSON and, when asked, a picture of the client area.</summary>
    internal void WriteArtifacts(string pictureName) => WriteArtifacts(pictureName, "console-shell");

    /// <summary>
    /// Writes the measurements and the picture under artifacts/e2e/visual/&lt;area&gt; as
    /// &lt;area&gt;-&lt;name&gt;.json.
    /// </summary>
    internal void WriteArtifacts(string pictureName, string area)
    {
        string dir = Path.Combine(GuiGate.Config.ArtifactsDir, "visual", area);
        Directory.CreateDirectory(dir);
        _measurements["test"] = Name;
        _measurements["dpi"] = Dpi;
        _measurements["scale"] = Scale;
        File.WriteAllText(
            Path.Combine(dir, $"{area}-{Name}.json"),
            JsonSerializer.Serialize(_measurements, new JsonSerializerOptions { WriteIndented = true }));
        if (pictureName.Length == 0)
        {
            return;
        }

        if (!GetClientRect(Session.MainHwnd, out NativeRect client))
        {
            return;
        }

        var origin = new NativePoint { X = 0, Y = 0 };
        _ = ClientToScreen(Session.MainHwnd, ref origin);
        try
        {
            using Bitmap shot = WindowCapture.CaptureRegion(
                Session.MainHwnd,
                new Point(origin.X, origin.Y),
                client.Right - client.Left,
                client.Bottom - client.Top);
            shot.Save(Path.Combine(dir, pictureName), ImageFormat.Png);
        }
        catch (WindowCapture.NotJudgedException ex)
        {
            E2EResults.Note("console.shell.capture." + pictureName, $"picture not written: {ex.Message}");
        }
    }

    /// <summary>
    /// A verified picture of the client area and the screen position of its top left corner, or null when
    /// it cannot be judged.
    /// </summary>
    internal Bitmap? CaptureClient(out Point origin)
    {
        origin = Point.Empty;
        if (!GetClientRect(Session.MainHwnd, out NativeRect client))
        {
            return null;
        }

        var topLeft = new NativePoint { X = 0, Y = 0 };
        _ = ClientToScreen(Session.MainHwnd, ref topLeft);
        origin = new Point(topLeft.X, topLeft.Y);
        try
        {
            return WindowCapture.CaptureRegion(
                Session.MainHwnd,
                origin,
                client.Right - client.Left,
                client.Bottom - client.Top);
        }
        catch (WindowCapture.NotJudgedException ex)
        {
            E2EResults.Note("console.capture", $"picture not judged: {ex.Message}");
            return null;
        }
    }

    // ---- native -----------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

#pragma warning disable SYSLIB1054 // A few user32 calls; LibraryImport plumbing is not needed.
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
#pragma warning restore SYSLIB1054

    private const uint MouseEventfMove = 0x0001;
    private const uint MouseEventfAbsolute = 0x8000;
    private const uint MouseEventfVirtualDesk = 0x4000;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int SmCxScreen = 0;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
}

/// <summary>Records the checks of one test under a common id prefix and asserts them at the end.</summary>
internal sealed class ShellChecks
{
    private readonly string _prefix;
    private readonly List<string> _failures = [];

    internal ShellChecks(string name, string area = "shell")
    {
        _prefix = $"console.{area}.{name}";
    }

    internal void Record(string id, bool pass, string notes)
    {
        string fullId = $"{_prefix}.{id}";
        _ = E2EResults.Record(fullId, pass, notes);
        if (!pass)
        {
            _failures.Add($"{fullId}: {notes}");
        }
    }

    internal void Note(string id, string text) => E2EResults.Note($"{_prefix}.{id}", text);

    internal void AssertAllPassed() =>
        Assert.AreEqual(
            0,
            _failures.Count,
            $"Console shell checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, _failures)}");
}

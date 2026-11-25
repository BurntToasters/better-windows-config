using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.ContextMenus;
using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using Bwc.E2E.List;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace Bwc.E2E.Gpedit.Commands;

/// <summary>
/// The command ids of the editor menus. One id is one command, in the menu bar (automation id
/// MenuCommand_id) and in the context menus (ContextMenuCommand_id). The ids are BWC's own; the
/// contract is in docs/failure-modes/gpedit-commands.md.
/// </summary>
internal static class GpeditCommandIds
{
    internal const int Edit = 2001;
    internal const int Refresh = 2002;
    internal const int Help = 2003;
    internal const int HelpTopics = 12804;
    internal const int ActionMenu = 59002;
    internal const int HelpMenu = 59004;
    internal const string MenuPrefix = "MenuCommand_";
}

/// <summary>Virtual key codes the command tests press.</summary>
internal static class GpeditKeys
{
    internal const ushort Back = 0x08;
    internal const ushort Enter = 0x0D;
    internal const ushort Escape = 0x1B;
    internal const ushort Left = 0x25;
    internal const ushort Up = 0x26;
    internal const ushort Right = 0x27;
    internal const ushort Delete = 0x2E;
    internal const ushort Apps = 0x5D;
    internal const ushort F1 = 0x70;
    internal const ushort F5 = 0x74;
    internal const ushort F6 = 0x75;
    internal const ushort F10 = 0x79;
}

/// <summary>Names of the fixture nodes and policy rows the command tests use.</summary>
internal static class GpeditNames
{
    internal const string Root = GpeditRun.RootName;
    internal const string Computer = "Computer Configuration";
    internal const string User = "User Configuration";
    internal const string Admin = "Administrative Templates";
    internal const string Oracle = "BWC Oracle";
    internal const string BasicsName = "Basics";
    internal const string AllSettings = "All Settings";

    internal const string MachineFlag = "Class machine flag setting";
    internal const string BothFlag = "Class both flag setting";
    internal const string DecimalValues = "Decimal enabled and disabled values setting";

    internal const string MachineFlagFull = "BWC.Policies.Oracle:Pol_Class_Machine";
    internal const string BothFlagFull = "BWC.Policies.Oracle:Pol_Class_Both";
    internal const string DecimalValuesFull = "BWC.Policies.Oracle:Pol_Value_Decimal";

    internal static readonly string[] BasicsPath = [Root, Computer, Admin, Oracle, BasicsName];
    internal static readonly string[] OraclePath = [Root, Computer, Admin, Oracle];
}

/// <summary>One entry of an open menu: command id, text, enabled state and top edge on screen.</summary>
internal sealed record GpeditMenuEntry(int Id, string Name, bool Enabled, double Top);

/// <summary>Helpers over a running editor that the command tests share.</summary>
internal static class GpeditCommandRun
{
    private static readonly int[] TopLevelIds = [59001, 59002, 59003, 59004];

    /// <summary>The integer value of a readout key, or -1 when the key is missing or not a number.</summary>
    internal static int Count(this GpeditRun run, string key) =>
        int.TryParse(run.Str(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : -1;

    internal static bool WaitCount(this GpeditRun run, string key, int expected, double seconds = 20) =>
        ShellRun.WaitUntil(() => run.Count(key) == expected, TimeSpan.FromSeconds(seconds));

    internal static bool WaitNode(this GpeditRun run, params string[] path) =>
        ShellRun.WaitUntil(() => run.NodePath().SequenceEqual(path), TimeSpan.FromSeconds(8));

    /// <summary>True when a reload is finished: not in flight and the model is loaded.</summary>
    internal static bool WaitReloadSettled(this GpeditRun run, double seconds = 30) =>
        ShellRun.WaitUntil(() => run.Count("Reloading") == 0 && run.Count("Loaded") == 1, TimeSpan.FromSeconds(seconds));

    /// <summary>Moves the keyboard focus to the console tree or to the result pane ("tree" or "result") with F6.</summary>
    internal static bool FocusPane(this GpeditRun run, string pane)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (run.Shell.FocusedPane() == pane)
            {
                return true;
            }

            run.Shell.Tap(GpeditKeys.F6);
        }

        if (pane == "tree" && run.TreeItem(run.NodePath().LastOrDefault() ?? string.Empty) is { } item)
        {
            item.Click();
            Thread.Sleep(ShellRun.KeySettle.Milliseconds);
        }

        return run.Shell.FocusedPane() == pane;
    }

    internal static void Press(this GpeditRun run, ushort key) => run.Shell.Tap(key);

    internal static void PressAlt(this GpeditRun run, ushort key) => run.Shell.Chord(RawKeyboard.Alt, key);

    internal static void PressShift(this GpeditRun run, ushort key) => run.Shell.Chord(RawKeyboard.Shift, key);

    /// <summary>The first tree item with this name (Computer Configuration comes before User Configuration).</summary>
    internal static AutomationElement? TreeItem(this GpeditRun run, string name)
    {
        try
        {
            return run.Shell.Find(ShellRun.TreePaneId)?
                .FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
                .FirstOrDefault(item => ShellRun.SafeName(item) == name);
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static string[] TreeItemNames(this GpeditRun run)
    {
        try
        {
            return run.Shell.Find(ShellRun.TreePaneId)?
                .FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
                .Select(ShellRun.SafeName).ToArray() ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>The element of a list row by its UI Automation name, or null.</summary>
    internal static AutomationElement? RowElement(this GpeditRun run, string rowName)
    {
        GpeditRow? row = run.Rows().FirstOrDefault(r => r.Name == rowName);
        return row is null ? null : run.Shell.Find("ConsoleListRow_" + row.Index.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The State cell of a list row, or "?" when the row is not there.</summary>
    internal static string StateOf(this GpeditRun run, string rowName) =>
        run.Rows().FirstOrDefault(r => r.Name == rowName)?.Cells.ElementAtOrDefault(1) ?? "?";

    /// <summary>Invokes a standard toolbar button through UI Automation. False when it is missing, disabled or fails.</summary>
    internal static bool InvokeButton(this GpeditRun run, string button, bool foreground = true)
    {
        try
        {
            if (foreground)
            {
                _ = ControlTestSupport.EnsureForeground(run.Shell.Session);
            }

            AutomationElement? element = run.Shell.Find(ShellRun.ButtonId(button));
            if (element is null || !element.IsEnabled)
            {
                return false;
            }

            element.Patterns.Invoke.Pattern.Invoke();
            Thread.Sleep(ShellRun.KeySettle);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Waits until a toolbar button has the given enabled state.</summary>
    internal static bool WaitButton(this GpeditRun run, string button, bool enabled) =>
        ShellRun.WaitUntil(() => run.Shell.Find(ShellRun.ButtonId(button)) is { } element && element.IsEnabled == enabled, TimeSpan.FromSeconds(2));

    /// <summary>The command entries of every open menu popup of the process whose automation id has the prefix.</summary>
    internal static List<GpeditMenuEntry> MenuEntries(this GpeditRun run, string prefix)
    {
        var found = new List<GpeditMenuEntry>();
        AutomationElement[] windows;
        try
        {
            windows = ProcessWindows.Of(run.Shell.Automation, run.Shell.Session.ProcessId);
        }
        catch (Exception)
        {
            return found;
        }

        foreach (AutomationElement window in windows)
        {
            AutomationElement[] items;
            try
            {
                items = window.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem));
            }
            catch (Exception)
            {
                continue;
            }

            foreach (AutomationElement item in items)
            {
                try
                {
                    string id = ShellRun.SafeId(item);
                    if (!id.StartsWith(prefix, StringComparison.Ordinal) ||
                        !int.TryParse(id.AsSpan(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int commandId) ||
                        Array.IndexOf(TopLevelIds, commandId) >= 0 ||
                        found.Any(e => e.Id == commandId) ||
                        item.Properties.IsOffscreen.Value)
                    {
                        continue;
                    }

                    Rectangle bounds = item.BoundingRectangle;
                    if (bounds.Width > 0 && bounds.Height > 0)
                    {
                        found.Add(new GpeditMenuEntry(commandId, item.Name ?? string.Empty, item.Properties.IsEnabled.Value, bounds.Top));
                    }
                }
                catch (Exception)
                {
                    // A stale element is skipped; the next poll reads the tree again.
                }
            }
        }

        found.Sort((a, b) => a.Top.CompareTo(b.Top));
        return found;
    }

    /// <summary>Opens a top level menu of the menu bar by UI Automation and returns its entries once any show.</summary>
    internal static List<GpeditMenuEntry> OpenTopMenu(this GpeditRun run, int topId)
    {
        using (var driver = new EditingDriver(run.Shell.Session))
        {
            driver.OpenMenu(topId);
        }

        var entries = new List<GpeditMenuEntry>();
        _ = ShellRun.WaitUntil(() => (entries = run.MenuEntries(GpeditCommandIds.MenuPrefix)).Count > 0, TimeSpan.FromSeconds(6));
        return entries;
    }

    /// <summary>Opens a top level menu and invokes one of its entries. False when the entry did not show.</summary>
    internal static bool InvokeMenuEntry(this GpeditRun run, int topId, int commandId)
    {
        try
        {
            using var driver = new EditingDriver(run.Shell.Session);
            driver.OpenMenu(topId, commandId);
            Thread.Sleep(ShellRun.KeySettle);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Closes any open menu with Escape and waits until no command entry is on screen.</summary>
    internal static bool CloseMenus(this GpeditRun run, string prefix = GpeditCommandIds.MenuPrefix)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (run.MenuEntries(prefix).Count == 0)
            {
                return true;
            }

            run.Press(GpeditKeys.Escape);
        }

        return ShellRun.WaitUntil(() => run.MenuEntries(prefix).Count == 0, TimeSpan.FromSeconds(3));
    }

    /// <summary>Converts the entries of a context menu snapshot.</summary>
    internal static List<GpeditMenuEntry> EntriesOf(MenuSnapshot? snapshot) =>
        snapshot is null
            ? []
            : snapshot.Entries.Select(e => new GpeditMenuEntry(e.Id, e.Name, e.Enabled, e.Top)).ToList();

    /// <summary>Waits for a context menu and returns its entries (empty when none opened).</summary>
    internal static List<GpeditMenuEntry> WaitContextMenu(ContextMenuDriver driver) =>
        EntriesOf(driver.WaitForMenu(TimeSpan.FromSeconds(6)));

    /// <summary>
    /// Records the checks every command menu shares: Edit is the first entry and enabled exactly with a
    /// policy row selected, Refresh is enabled, Help is the last entry and enabled, and the three labels
    /// are the BWC words.
    /// </summary>
    internal static void CheckMenu(GpeditChecks checks, string id, IReadOnlyList<GpeditMenuEntry> entries, bool expectEdit, string where)
    {
        GpeditMenuEntry? edit = entries.FirstOrDefault(e => e.Id == GpeditCommandIds.Edit);
        GpeditMenuEntry? refresh = entries.FirstOrDefault(e => e.Id == GpeditCommandIds.Refresh);
        GpeditMenuEntry? help = entries.FirstOrDefault(e => e.Id == GpeditCommandIds.Help);
        string listing = string.Join("|", entries.Select(e => $"{e.Id}:{e.Name}:{(e.Enabled ? "on" : "off")}"));
        checks.Record(
            id + ".edit",
            expectEdit ? edit is { Enabled: true } && entries.Count > 0 && entries[0].Id == GpeditCommandIds.Edit : edit is null,
            $"{where}: Edit {(expectEdit ? "first and enabled" : "absent")}; entries={listing}.");
        checks.Record(id + ".refresh", refresh is { Enabled: true }, $"{where}: Refresh present and enabled; entries={listing}.");
        checks.Record(
            id + ".help",
            help is { Enabled: true } && entries.Count > 0 && entries[^1].Id == GpeditCommandIds.Help,
            $"{where}: Help present, enabled and last; entries={listing}.");
        checks.Record(
            id + ".labels",
            (edit is null || edit.Name == "Edit") && (refresh is null || refresh.Name == "Refresh") && (help is null || help.Name == "Help"),
            $"{where}: the entries are labelled Edit, Refresh and Help; entries={listing}.");
    }

    /// <summary>A short id for a check: the letters and digits of the last path segment.</summary>
    internal static string Slug(string text) => new(text.Where(char.IsLetterOrDigit).ToArray());
}

/// <summary>
/// Samples the rectangle of every visible top level window of a process by name, from before the
/// process starts, so the first place at which the window is ever visible is known. A sample is kept
/// only when the rectangle of that window changed.
/// </summary>
internal sealed class GpeditWindowTrace : IDisposable
{
    private const uint GwOwner = 4;

    private readonly string _processName;
    private readonly Thread _thread;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<IntPtr, Rectangle> _last = [];
    private readonly Dictionary<uint, string> _names = [];
    private readonly List<(long Ms, Rectangle Rect)> _samples = [];
    private volatile bool _stop;

    internal GpeditWindowTrace(string processName)
    {
        _processName = processName;
        _thread = new Thread(Run) { IsBackground = true, Name = "gpedit-window-trace" };
        _thread.Start();
    }

    /// <summary>The distinct rectangles in the order they were first seen, with the time since the trace started.</summary>
    internal IReadOnlyList<(long Ms, Rectangle Rect)> Samples
    {
        get
        {
            lock (_samples)
            {
                return _samples.ToArray();
            }
        }
    }

    internal static int ScreenWidth => GetSystemMetrics(0);

    internal static int ScreenHeight => GetSystemMetrics(1);

    public void Dispose()
    {
        _stop = true;
        _ = _thread.Join(2000);
    }

    private void Run()
    {
        while (!_stop)
        {
            try
            {
                Sample();
            }
            catch (Exception)
            {
                // The next pass samples again; a window that closed during the pass is simply missed.
            }

            Thread.Sleep(3);
        }
    }

    private void Sample()
    {
        _ = EnumWindows(
            (hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GwOwner) != IntPtr.Zero)
                {
                    return true;
                }

                if (GetWindowThreadProcessId(hwnd, out uint pid) == 0 || !IsTracked(pid) || !GetWindowRect(hwnd, out NativeRect rect))
                {
                    return true;
                }

                var current = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
                if (current.Width < 200 || current.Height < 150 || (_last.TryGetValue(hwnd, out Rectangle previous) && previous == current))
                {
                    return true;
                }

                _last[hwnd] = current;
                lock (_samples)
                {
                    _samples.Add((_clock.ElapsedMilliseconds, current));
                }

                return true;
            },
            IntPtr.Zero);
    }

    private bool IsTracked(uint pid)
    {
        if (!_names.TryGetValue(pid, out string? name))
        {
            try
            {
                using Process process = Process.GetProcessById(unchecked((int)pid));
                name = process.ProcessName;
            }
            catch (Exception)
            {
                name = string.Empty;
            }

            _names[pid] = name;
        }

        return string.Equals(name, _processName, StringComparison.OrdinalIgnoreCase);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

#pragma warning disable SYSLIB1054 // A few user32 calls; LibraryImport plumbing is not needed.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
#pragma warning restore SYSLIB1054
}

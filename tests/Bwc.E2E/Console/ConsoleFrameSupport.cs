using System.Drawing;
using System.Globalization;
using System.Security.Cryptography;
using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Gpedit;
using Bwc.E2E.Gpedit.Commands;
using Bwc.E2E.Harness;
using Bwc.E2E.ImportExport;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace Bwc.E2E.ConsoleFrame;

/// <summary>
/// The ids of the U3a frame features that the tests use. The contract is docs/failure-modes/console-frame.md: the View
/// menu keeps the classic layout (MENU-020, MENU-021) with Add/Remove Columns first and Customize View last, Export List
/// joins the Action menu, and the two dialogs are their own top level windows of the process whose parts are named after
/// the classic dialog controls (DLG-061, DLG-073, DLG-117, DLG-118).
/// </summary>
internal static class FrameIds
{
    internal const int ActionMenu = 59002;
    internal const int ViewMenu = 59003;

    internal const int ColumnsCommand = 1000;
    internal const int CustomizeCommand = 13231;
    internal const int ExportCommand = 13436;

    /// <summary>The ids the View menu held before U3a (VIEW-1): Console Tree, Toolbar and Status Bar.</summary>
    internal static readonly int[] RemovedViewToggles = [1001, 1002, 1003];

    /// <summary>The four list modes of the View menu, in menu order.</summary>
    internal static readonly int[] ViewModes = [1004, 1005, 1006, 1007];

    internal const string ColumnsDialog = "ConsoleColumns";
    internal const string Available = "ConsoleColumns.Available";
    internal const string Displayed = "ConsoleColumns.Displayed";
    internal const string Add = "ConsoleColumns.Add";
    internal const string Remove = "ConsoleColumns.Remove";
    internal const string MoveUp = "ConsoleColumns.MoveUp";
    internal const string MoveDown = "ConsoleColumns.MoveDown";
    internal const string Restore = "ConsoleColumns.Restore";
    internal const string ColumnsOk = "ConsoleColumns.Ok";
    internal const string ColumnsCancel = "ConsoleColumns.Cancel";

    /// <summary>The nine parts of the columns dialog in the order of DLG-061 (control ids 4084 to 4090, 1, 2).</summary>
    internal static readonly string[] ColumnParts =
        [Available, Add, Remove, Displayed, MoveUp, MoveDown, Restore, ColumnsOk, ColumnsCancel];

    internal const string CustomizeDialog = "ConsoleCustomize";
    internal const string ConsoleTree = "ConsoleCustomize.ConsoleTree";
    internal const string StandardMenus = "ConsoleCustomize.StandardMenus";
    internal const string StandardToolbar = "ConsoleCustomize.StandardToolbar";
    internal const string StatusBar = "ConsoleCustomize.StatusBar";
    internal const string DescriptionBar = "ConsoleCustomize.DescriptionBar";
    internal const string TaskpadTabs = "ConsoleCustomize.TaskpadTabs";
    internal const string ActionPane = "ConsoleCustomize.ActionPane";
    internal const string SnapInMenus = "ConsoleCustomize.SnapInMenus";
    internal const string SnapInToolbars = "ConsoleCustomize.SnapInToolbars";
    internal const string CustomizeOk = "ConsoleCustomize.Ok";
    internal const string CustomizeCancel = "ConsoleCustomize.Cancel";

    /// <summary>The nine check boxes in the order of DLG-117 and DLG-073, top to bottom.</summary>
    internal static readonly string[] CustomizeBoxes =
        [ConsoleTree, StandardMenus, StandardToolbar, StatusBar, DescriptionBar, TaskpadTabs, ActionPane, SnapInMenus, SnapInToolbars];

    /// <summary>The check boxes BWC honors. The other four are shown disabled (docs/failure-modes/console-frame.md).</summary>
    internal static readonly string[] BuiltBoxes = [ConsoleTree, StandardToolbar, StatusBar, DescriptionBar, SnapInToolbars];

    internal static readonly string[] UnbuiltBoxes = [StandardMenus, TaskpadTabs, ActionPane, SnapInMenus];

    internal const string DescriptionPaneId = "ConsoleDescriptionPane";
    internal const string FilterButton = "ConsoleToolbar_Filter";
    internal const string ListId = "ConsoleList";

    /// <summary>The save dialog filters in order: ANSI tab, comma, Unicode tab, comma (mmcbase.dll 13347, 13348).</summary>
    internal static readonly (string Word, bool Unicode)[] ExportTypes =
        [("Tab", false), ("Comma", false), ("Tab", true), ("Comma", true)];
}

/// <summary>A dialog of the frame (the columns chooser or Customize View), read through UI Automation.</summary>
internal sealed class FrameDialog : IDisposable
{
    private readonly EditingDriver _driver;

    private FrameDialog(EditingDriver driver, string rootId)
    {
        _driver = driver;
        RootId = rootId;
    }

    internal string RootId { get; }

    /// <summary>Walks a menu path (top level menu, then entries) and waits for the dialog. Null when it did not open.</summary>
    internal static FrameDialog? Open(ShellRun run, string rootId, params int[] menuPath)
    {
        var dialog = new FrameDialog(new EditingDriver(run.Session), rootId);
        try
        {
            using var menu = new EditingDriver(run.Session);
            menu.OpenMenu(menuPath);
        }
        catch (InvalidOperationException)
        {
            dialog.Dispose();
            return null;
        }

        if (dialog._driver.WaitForElement(rootId, TimeSpan.FromSeconds(10)) is null)
        {
            dialog.Dispose();
            return null;
        }

        return dialog;
    }

    /// <summary>Waits for a dialog that something else opened (a key chord, for example). Null when it did not open.</summary>
    internal static FrameDialog? Attach(ShellRun run, string rootId)
    {
        var dialog = new FrameDialog(new EditingDriver(run.Session), rootId);
        if (dialog._driver.WaitForElement(rootId, TimeSpan.FromSeconds(10)) is null)
        {
            dialog.Dispose();
            return null;
        }

        return dialog;
    }

    public void Dispose() => _driver.Dispose();

    internal AutomationElement? Root => _driver.WaitForElement(RootId, TimeSpan.FromSeconds(3));

    internal bool IsOpen => _driver.WaitForElement(RootId, TimeSpan.FromMilliseconds(300)) is not null;

    internal bool WaitClosed(double seconds = 10) => ShellRun.WaitUntil(() => !IsOpen, TimeSpan.FromSeconds(seconds));

    internal AutomationElement? Find(string id, double seconds = 2) => _driver.WaitForElement(id, TimeSpan.FromSeconds(seconds));

    internal bool Has(string id) => Find(id, 1) is not null;

    internal bool IsEnabled(string id) => Find(id, 1)?.IsEnabled ?? false;

    internal IntPtr Handle() => Root is { } root ? ClassicDialogTestsBridge.HandleOf(root) : IntPtr.Zero;

    internal Rectangle? RectOf(string id)
    {
        try
        {
            Rectangle box = Find(id, 1)?.BoundingRectangle ?? Rectangle.Empty;
            return box.Width > 0 && box.Height > 0 ? box : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Invokes a button and gives the dialog a moment to settle.</summary>
    internal bool Press(string id)
    {
        bool done = _driver.Invoke(id);
        Thread.Sleep(300);
        return done;
    }

    /// <summary>OK, then waits for the dialog to go. False when the button is missing or the dialog stays.</summary>
    internal bool PressAndClose(string id) => Press(id) && WaitClosed();

    /// <summary>The names of the items of a list box, in screen order.</summary>
    internal string[] Items(string listId)
    {
        try
        {
            return (Find(listId, 2)?.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)) ?? [])
                .Select(ShellRun.SafeName)
                .ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>The name of the selected item of a list box, or empty when none is selected.</summary>
    internal string SelectedItem(string listId)
    {
        try
        {
            foreach (AutomationElement item in Find(listId, 2)?.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)) ?? [])
            {
                if (item.Patterns.SelectionItem.IsSupported && item.Patterns.SelectionItem.Pattern.IsSelected.Value)
                {
                    return ShellRun.SafeName(item);
                }
            }
        }
        catch (Exception)
        {
            // A list that is rebuilding returns nothing; the caller polls.
        }

        return string.Empty;
    }

    internal bool Select(string listId, string name)
    {
        try
        {
            AutomationElement? item = Find(listId, 2)?.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .FirstOrDefault(i => ShellRun.SafeName(i) == name);
            if (item is null)
            {
                return false;
            }

            if (item.Patterns.SelectionItem.IsSupported)
            {
                item.Patterns.SelectionItem.Pattern.Select();
            }
            else
            {
                item.Click();
            }

            Thread.Sleep(250);
            return SelectedItem(listId) == name;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal bool Checked(string id)
    {
        try
        {
            return Find(id, 1)?.Patterns.Toggle.PatternOrDefault?.ToggleState.Value == ToggleState.On;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Sets a check box to the wanted state through its Toggle pattern and reads it back.</summary>
    internal bool SetChecked(string id, bool wanted)
    {
        try
        {
            AutomationElement? box = Find(id, 2);
            if (box is null || !box.IsEnabled)
            {
                return false;
            }

            if (Checked(id) != wanted)
            {
                box.Patterns.Toggle.Pattern.Toggle();
                Thread.Sleep(300);
            }

            return Checked(id) == wanted;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Escape sent to the dialog window.</summary>
    internal void PressEscape()
    {
        _ = ClassicDialogTestsBridge.EnsureForeground(Handle());
        Bwc.E2E.List.RawKeyboard.Tap(ShellRun.VkEscape);
    }

    internal string Screenshot(string name)
    {
        string dir = Path.Combine(GuiGate.Config.ArtifactsDir, "visual", FrameRun.Area);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        try
        {
            return Root is { } root ? ClassicDialogTestsBridge.SaveScreenshot(root, path) : string.Empty;
        }
        catch (Exception ex)
        {
            E2EResults.Note("console.frame.capture." + name, $"picture not written: {ex.Message}");
            return string.Empty;
        }
    }
}

/// <summary>Menu reading that works for the frame host and the policy editor alike (both are a ShellRun).</summary>
internal static class FrameMenu
{
    private static readonly int[] TopLevelIds = [59001, 59002, 59003, 59004];

    /// <summary>The command entries of every open popup of the process, top to bottom.</summary>
    internal static List<GpeditMenuEntry> Entries(ShellRun run)
    {
        var found = new List<GpeditMenuEntry>();
        AutomationElement[] windows;
        try
        {
            windows = ProcessWindows.Of(run.Automation, run.Session.ProcessId);
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
                    if (!id.StartsWith(GpeditCommandIds.MenuPrefix, StringComparison.Ordinal) ||
                        !int.TryParse(id.AsSpan(GpeditCommandIds.MenuPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int commandId) ||
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

    /// <summary>Opens a top level menu through UI Automation and returns its entries once any show.</summary>
    internal static List<GpeditMenuEntry> OpenTop(ShellRun run, int topId)
    {
        using (var driver = new EditingDriver(run.Session))
        {
            driver.OpenMenu(topId);
        }

        var entries = new List<GpeditMenuEntry>();
        _ = ShellRun.WaitUntil(() => (entries = Entries(run)).Count > 0, TimeSpan.FromSeconds(6));
        return entries;
    }

    /// <summary>Closes every open popup with Escape.</summary>
    internal static bool Close(ShellRun run)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (Entries(run).Count == 0)
            {
                return true;
            }

            run.Tap(ShellRun.VkEscape);
        }

        return ShellRun.WaitUntil(() => Entries(run).Count == 0, TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// The sizes of the groups the separators make: a gap between two entries that is wider than the usual step starts a
    /// new group. A menu with one separator between two entries gives two groups.
    /// </summary>
    internal static int[] GroupSizes(IReadOnlyList<GpeditMenuEntry> entries)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        double pitch = double.MaxValue;
        for (int i = 1; i < entries.Count; i++)
        {
            pitch = Math.Min(pitch, entries[i].Top - entries[i - 1].Top);
        }

        var sizes = new List<int> { 1 };
        for (int i = 1; i < entries.Count; i++)
        {
            if (pitch > 0 && entries[i].Top - entries[i - 1].Top > pitch * 1.15)
            {
                sizes.Add(1);
            }
            else
            {
                sizes[^1]++;
            }
        }

        return sizes.ToArray();
    }

    internal static string Describe(IReadOnlyList<GpeditMenuEntry> entries) =>
        string.Join("|", entries.Select(e => $"{e.Id}:{e.Name}:{(e.Enabled ? "on" : "off")}"));
}

/// <summary>Reading of the shell and the list of a running host that the frame tests share.</summary>
internal static class FrameRun
{
    internal const string Area = "console-frame";

    /// <summary>One key of the list readout (the help text of ConsoleList, E2E flavor only).</summary>
    internal static string ListKey(ShellRun run, string key)
    {
        foreach (string part in run.HelpTextOf(FrameIds.ListId).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int at = part.IndexOf('=', StringComparison.Ordinal);
            if (at > 0 && part[..at] == key)
            {
                return part[(at + 1)..];
            }
        }

        return string.Empty;
    }

    /// <summary>The visible columns, in display order (the Order key of the list readout).</summary>
    internal static string[] Order(ShellRun run) => ListKey(run, "Order").Split(',', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The hidden columns (the Hidden key of the list readout).</summary>
    internal static string[] Hidden(ShellRun run) => ListKey(run, "Hidden").Split(',', StringSplitOptions.RemoveEmptyEntries);

    internal static bool WaitOrder(ShellRun run, params string[] expected) =>
        ShellRun.WaitUntil(() => Order(run).SequenceEqual(expected), TimeSpan.FromSeconds(8));

    /// <summary>The cell texts of one realized row, left to right, through the grid pattern of the list.</summary>
    internal static string[] Cells(ShellRun run, int rowIndex)
    {
        try
        {
            AutomationElement? list = run.Find(FrameIds.ListId);
            var grid = list?.Patterns.Grid.PatternOrDefault;
            int columns = grid?.ColumnCount.ValueOrDefault ?? 0;
            var cells = new string[columns];
            for (int column = 0; column < columns; column++)
            {
                cells[column] = ShellRun.SafeName(grid?.GetItem(rowIndex, column));
            }

            return cells;
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>The header names of the given column ids; an id with no header gives an empty entry.</summary>
    internal static string[] HeaderNames(ShellRun run, params string[] columnIds) =>
        columnIds.Select(id => ShellRun.SafeName(run.Find("ConsoleListHeader_" + id))).ToArray();

    /// <summary>Clicks a list header through its Invoke pattern (a sort click).</summary>
    internal static bool ClickHeader(ShellRun run, string columnId)
    {
        try
        {
            _ = ControlTestSupport.EnsureForeground(run.Session);
            AutomationElement? header = run.Find("ConsoleListHeader_" + columnId);
            if (header is null)
            {
                return false;
            }

            header.Patterns.Invoke.Pattern.Invoke();
            Thread.Sleep(ShellRun.KeySettle);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The dialog size in DIPs.</summary>
    internal static (double Width, double Height) SizeDip(FrameDialog dialog, ShellRun run)
    {
        Rectangle? root = dialog.RectOf(dialog.RootId);
        return root is { } r ? (r.Width / run.Scale, r.Height / run.Scale) : (-1, -1);
    }

    /// <summary>True when a visible element with the id has a rectangle.</summary>
    internal static bool Shows(ShellRun run, string id) => !run.IsHidden(id);

    /// <summary>The main window of the editor process is enabled again after a modal dialog closed.</summary>
    internal static bool OwnerEnabled(ShellRun run) =>
        ShellRun.WaitUntil(() => run.Session.IsEnabled, TimeSpan.FromSeconds(5));

    /// <summary>The window text of a check box or button, empty when the part is missing.</summary>
    internal static string NameOf(FrameDialog dialog, string id) => ShellRun.SafeName(dialog.Find(id, 1));
}

/// <summary>The export file side of the tests: the real save dialog, a run folder path, and the artifact.</summary>
internal static class FrameExport
{
    /// <summary>The folder inside the run data root that every export of a run is written to.</summary>
    internal static string RunFolder(string scope)
    {
        string folder = Path.Combine(GuiGate.Config.DataRootFor(scope), "export");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>A fixture file under tests/Bwc.E2E/Console/ExportFixtures (BWC fixture text, byte exact).</summary>
    internal static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(GuiGate.Config.RepoRoot, "tests", "Bwc.E2E", "Console", "ExportFixtures", name));

    /// <summary>The export folder of a frame host run (inside the run data root of its scope).</summary>
    internal static string FolderOf(ShellRun run) => RunFolder("console-" + run.Name);

    /// <summary>The export folder under a data root that a sandbox owns.</summary>
    internal static string FolderUnder(string dataRoot)
    {
        string folder = Path.Combine(dataRoot, "export");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>The bytes of a file once its size has stopped changing, or null when it never appeared.</summary>
    internal static byte[]? WaitForFile(string path, double seconds = 20)
    {
        long last = -1;
        byte[]? bytes = null;
        _ = ShellRun.WaitUntil(
            () =>
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                long size = new FileInfo(path).Length;
                bool stable = size == last && size > 0;
                last = size;
                if (stable)
                {
                    bytes = File.ReadAllBytes(path);
                }

                return stable;
            },
            TimeSpan.FromSeconds(seconds));
        return bytes;
    }

    /// <summary>
    /// Waits for the save dialog the host opened, picks the filter (1 to 4), types the path and presses Save. The checks
    /// are recorded under <paramref name="id"/>; the result is true when the dialog accepted the path and went away.
    /// </summary>
    internal static bool SaveThroughDialog(ShellRun run, ShellChecks checks, string id, int filter, string path)
    {
        using var pickers = new SystemFileDialogDriver(run.Session);
        IntPtr dialog = pickers.WaitForDialog(TimeSpan.FromSeconds(15));
        checks.Record(id + ".dialogOpened", dialog != IntPtr.Zero, "a save dialog opened after the export command.");
        if (dialog == IntPtr.Zero)
        {
            return false;
        }

        (string word, bool unicode) = FrameIds.ExportTypes[filter - 1];
        bool selected = pickers.SelectFileType(dialog, filter, word, out string typeNotes);
        string typeName = pickers.ReadFileType(dialog);
        checks.Record(
            id + ".fileType",
            selected && typeName.Contains("Unicode", StringComparison.OrdinalIgnoreCase) == unicode,
            $"filter {filter} should be a {(unicode ? "Unicode" : "plain")} {word} type; {typeNotes}");

        bool typed = pickers.TrySetFileName(dialog, path, out string typedNotes);
        string acceptNotes = "not attempted";
        bool accepted = typed && pickers.TryAccept(dialog, "Save", out acceptNotes);
        bool closed = accepted && pickers.WaitGone(dialog, TimeSpan.FromSeconds(10));
        checks.Record(id + ".saved", typed && accepted && closed, $"{typedNotes} {acceptNotes} closed={closed}.");
        if (!closed)
        {
            _ = pickers.TryCancel(dialog);
        }

        return closed;
    }

    /// <summary>
    /// Compares the exported file with the fixture byte for byte and records one check. The notes name the first byte that
    /// differs and the two sizes, never the file text.
    /// </summary>
    internal static void CompareWithFixture(ShellChecks checks, string id, byte[]? actual, string fixtureName)
    {
        byte[] expected = Fixture(fixtureName);
        if (actual is null)
        {
            checks.Record(id, false, $"no file was written; the fixture {fixtureName} has {expected.Length} bytes.");
            return;
        }

        int common = Math.Min(actual.Length, expected.Length);
        int first = -1;
        for (int i = 0; i < common; i++)
        {
            if (actual[i] != expected[i])
            {
                first = i;
                break;
            }
        }

        bool same = first < 0 && actual.Length == expected.Length;
        string where = first >= 0 ? $"the first difference is at byte {first}" : "one is a prefix of the other";
        checks.Record(
            id,
            same,
            same
                ? $"the file equals the fixture {fixtureName} ({expected.Length} bytes)."
                : $"the file ({actual.Length} bytes) differs from the fixture {fixtureName} ({expected.Length} bytes); {where}.");
    }

    /// <summary>Copies the exported file next to the run measurements and records its SHA-256 as a measurement.</summary>
    internal static void Publish(ShellRun run, string name, string exportedPath)
    {
        string dir = Path.Combine(GuiGate.Config.ArtifactsDir, "visual", FrameRun.Area);
        Directory.CreateDirectory(dir);
        if (!File.Exists(exportedPath))
        {
            run.Measure(name + ".sha256", "(no file)");
            return;
        }

        string target = Path.Combine(dir, $"{FrameRun.Area}-{run.Name}-{name}{Path.GetExtension(exportedPath)}");
        File.Copy(exportedPath, target, overwrite: true);
        run.Measure(name + ".sha256", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))));
    }
}

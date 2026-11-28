using System.Text;
using Bwc.E2E.Controls;
using Bwc.E2E.Gpedit;
using Bwc.E2E.Gpedit.Commands;
using Bwc.E2E.Gpedit.Filter;
using Bwc.E2E.Harness;
using Bwc.E2E.ImportExport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Bwc.E2E.Gpedit.Commands.GpeditNames;

namespace Bwc.E2E.ConsoleFrame;

/// <summary>
/// Export List of the console frame (MENU-017, TOOL-011, TOOL-016): where the command is, the save dialog and its four file
/// types (mmcbase.dll strings 13347 and 13348), and the exported text compared byte for byte with fixture files under
/// tests/Bwc.E2E/Console/ExportFixtures. The frame host rows are known (the E2E seam), so its fixtures are exact; the
/// policy editor is checked on the Shared keys node, whose names hold commas, and on a filtered node. Every export goes to
/// a folder inside the run data root and is copied to artifacts/e2e/visual/console-frame with its SHA-256 in the
/// measurements. Failure modes: docs/failure-modes/console-frame.md, sections 9 to 13.
/// </summary>
[TestClass]
public sealed class ConsoleExportTests
{
    private const string FrameArea = "frame";

    /// <summary>The capture node kinds; Action holds Export List at each (MENU-007 to MENU-009).</summary>
    private static readonly (string[] Path, string Id)[] Kinds =
    [
        ([Root], "root"),
        ([Root, Computer], "computerConfiguration"),
        ([Root, Computer, "Software Settings"], "softwarePlaceholder"),
        ([Root, Computer, "Windows Settings"], "windowsPlaceholder"),
        ([Root, Computer, Admin], "administrativeTemplates"),
        (OraclePath, "category"),
        (BasicsPath, "categoryWithPolicies"),
        ([Root, Computer, Admin, AllSettings], "allSettings"),
        ([Root, User], "userConfiguration"),
        ([Root, User, Admin], "userAdministrativeTemplates"),
        ([Root, User, Admin, Oracle, BasicsName], "userCategoryWithPolicies"),
        ([Root, User, Admin, AllSettings], "userAllSettings"),
    ];

    private static readonly string[] SharedKeysPath = [Root, Computer, Admin, Oracle, "Shared keys"];

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("Console")]
    [TestCategory("E2EOnly")]
    public void Console_Export_Menu_NodeKinds()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("export", "menu.nodes");
        const string name = "console-export-menu-nodes";
        GpeditSandbox sandbox = GpeditSandbox.Create(name);
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open(name, sandbox);

        // MENU-007 to MENU-009: every node kind has Export List, enabled, and Help stays last. The toolbar button is enabled
        // everywhere (TOOL-011).
        foreach ((string[] path, string id) in Kinds)
        {
            run.GoTo(path);
            _ = run.WaitNode(path);
            string where = string.Join(" > ", path);
            List<GpeditMenuEntry> entries = run.OpenTopMenu(GpeditCommandIds.ActionMenu);
            GpeditMenuEntry? export = entries.FirstOrDefault(e => e.Id == FrameIds.ExportCommand);
            string listing = FrameMenu.Describe(entries);
            checks.Record(id + ".present", export is { Enabled: true }, $"{where}: Export List is present and enabled; entries={listing}.");
            checks.Record(id + ".label", export?.Name.StartsWith("Export List", StringComparison.Ordinal) ?? false, $"{where}: label '{export?.Name}' starts with Export List.");
            checks.Record(id + ".helpLast", entries.Count > 0 && entries[^1].Id == GpeditCommandIds.Help, $"{where}: Help is still last; entries={listing}.");
            checks.Record(id + ".beforeHelp", export is not null && entries.FindIndex(e => e.Id == FrameIds.ExportCommand) < entries.Count - 1, $"{where}: Export List comes before Help.");
            _ = run.CloseMenus();
            checks.Record(id + ".button", run.Shell.ButtonEnabled("ExportList"), $"{where}: the Export List button is enabled.");
        }

        // MENU-010: with a policy row selected the Action menu has no Export List, and the button is still enabled (TOOL-016).
        run.GoTo(BasicsPath);
        run.SelectRow(MachineFlag);
        List<GpeditMenuEntry> rowEntries = run.OpenTopMenu(GpeditCommandIds.ActionMenu);
        checks.Record("policyRow.absent", rowEntries.All(e => e.Id != FrameIds.ExportCommand), $"entries={FrameMenu.Describe(rowEntries)}.");
        _ = run.CloseMenus();
        checks.Record("policyRow.button", run.Shell.ButtonEnabled("ExportList"), "the Export List button stays enabled with a policy row selected.");

        // The entry is back as soon as the selection goes (no stale selection).
        checks.Record("rowGone.up", run.InvokeButton("UpOneLevel") && run.WaitNode(OraclePath), $"Up One Level leaves Basics; node={run.Str("Node")}.");
        List<GpeditMenuEntry> after = run.OpenTopMenu(GpeditCommandIds.ActionMenu);
        checks.Record("rowGone.present", after.Any(e => e.Id == FrameIds.ExportCommand), $"entries={FrameMenu.Describe(after)}.");
        _ = run.CloseMenus();

        run.WriteArtifacts("console-export-menu-nodes.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("E2EOnly")]
    public void Console_Export_Dialog_Types()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new ShellChecks("export.dialog", FrameArea);
        using ListRun run = ListRun.Open("frame-export-dialog", 8);
        string folder = FrameExport.FolderOf(run.Shell);

        checks.Record("button.enabled", run.Shell.ButtonEnabled("ExportList"), "the Export List button is enabled at the start (TOOL-011).");
        checks.Record("pressed", PressExport(run.Shell), "the Export List button runs.");
        using var pickers = new SystemFileDialogDriver(run.Shell.Session);
        IntPtr dialog = pickers.WaitForDialog(TimeSpan.FromSeconds(15));
        checks.Record("opens", dialog != IntPtr.Zero, "a save dialog opens.");
        if (dialog == IntPtr.Zero)
        {
            checks.AssertAllPassed();
            return;
        }

        checks.Record("modal", !run.Shell.Session.IsEnabled, "the main window is disabled while the save dialog is open.");

        // The first type is the default; the four types follow the order of the two strings.
        string first = pickers.ReadFileType(dialog);
        checks.Record("default", first.Contains("Tab", StringComparison.OrdinalIgnoreCase) && !first.Contains("Unicode", StringComparison.OrdinalIgnoreCase), $"the dialog starts on '{first}': the plain tab type.");
        for (int filter = 2; filter <= 4; filter++)
        {
            (string word, bool unicode) = FrameIds.ExportTypes[filter - 1];
            bool picked = pickers.SelectFileType(dialog, filter, word, out string notes);
            string typeName = pickers.ReadFileType(dialog);
            checks.Record("type." + filter, picked && typeName.Contains("Unicode", StringComparison.OrdinalIgnoreCase) == unicode, $"filter {filter}: '{typeName}'; {notes}");
        }

        _ = pickers.SaveScreenshot(dialog, Path.Combine(GuiGate.Config.ArtifactsDir, "visual", FrameRun.Area, "console-frame-export-dialog.png"));

        // Cancel writes nothing and leaves the host as it was.
        string[] before = Directory.GetFileSystemEntries(folder);
        checks.Record("cancel", pickers.TryCancel(dialog), "Escape closes the save dialog.");
        checks.Record("ownerEnabledAgain", FrameRun.OwnerEnabled(run.Shell), "the main window is enabled again.");
        checks.Record("cancelWritesNothing", Directory.GetFileSystemEntries(folder).SequenceEqual(before) && before.Length == 0, $"the export folder holds {before.Length} entries after Cancel.");
        checks.Record("listUntouched", run.Int("Rows") == 8 && FrameRun.Order(run.Shell).SequenceEqual(["name", "state", "comment"]), $"Rows={run.Int("Rows")}, Order={string.Join(",", FrameRun.Order(run.Shell))}.");
        checks.Record("buttonStillWorks", PressExport(run.Shell) && pickers.WaitForDialog(TimeSpan.FromSeconds(15)) is var again && again != IntPtr.Zero && pickers.TryCancel(again), "the button opens the dialog a second time.");
        run.Shell.WriteArtifacts("console-frame-export-dialog.png", FrameRun.Area);
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("E2EOnly")]
    public void Console_Export_File_Ansi()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new ShellChecks("export.ansi", FrameArea);
        using ListRun run = ListRun.Open("frame-export-ansi", 8);
        string folder = FrameExport.FolderOf(run.Shell);

        // Filter 1 and 2: plain text in the system ANSI code page, a header line and a line per row, CRLF after each line.
        string tab = Path.Combine(folder, "ansi-tab.txt");
        checks.Record("tab.pressed", PressExport(run.Shell), "the Export List button runs.");
        _ = FrameExport.SaveThroughDialog(run.Shell, checks, "tab", 1, tab);
        byte[]? tabBytes = FrameExport.WaitForFile(tab);
        FrameExport.CompareWithFixture(checks, "tab.fixture", tabBytes, "frame-ansi-tab.txt");
        FrameExport.Publish(run.Shell, "ansi-tab", tab);

        string comma = Path.Combine(folder, "ansi-comma.csv");
        checks.Record("comma.pressed", PressExport(run.Shell), "the Export List button runs a second time.");
        _ = FrameExport.SaveThroughDialog(run.Shell, checks, "comma", 2, comma);
        byte[]? commaBytes = FrameExport.WaitForFile(comma);
        FrameExport.CompareWithFixture(checks, "comma.fixture", commaBytes, "frame-ansi-comma.csv");
        FrameExport.Publish(run.Shell, "ansi-comma", comma);

        // Exporting changes nothing in the list.
        checks.Record("listUntouched", run.Int("Rows") == 8 && FrameRun.Order(run.Shell).SequenceEqual(["name", "state", "comment"]) && run.Str("Sort") == "none", $"Rows={run.Int("Rows")}, Order={string.Join(",", FrameRun.Order(run.Shell))}, Sort={run.Str("Sort")}.");
        checks.Record("ownerEnabled", FrameRun.OwnerEnabled(run.Shell), "the main window is enabled again.");
        run.Shell.WriteArtifacts("console-frame-export-ansi.png", FrameRun.Area);
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("E2EOnly")]
    public void Console_Export_CommaCellQuotesLineBreaks()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new ShellChecks("export.comma-multiline", FrameArea);
        using ListRun run = ListRun.Open(
            "frame-export-comma-multiline",
            8,
            new Dictionary<string, string> { ["BWC_E2E_CONSOLE_MULTILINE"] = "true" });
        string path = Path.Combine(FrameExport.FolderOf(run.Shell), "comma-multiline.csv");

        checks.Record("comma.pressed", PressExport(run.Shell), "the Export List button runs.");
        _ = FrameExport.SaveThroughDialog(run.Shell, checks, "comma", 2, path);
        byte[]? actual = FrameExport.WaitForFile(path);
        string baseline = Encoding.ASCII.GetString(FrameExport.Fixture("frame-ansi-comma.csv"));
        string expectedText = baseline.Replace(
            "Setting 00002,Not configured,Yes\r\n",
            "Setting 00002,Not configured,\"first line\r\nsecond line\"\r\n",
            StringComparison.Ordinal);
        byte[] expected = Encoding.ASCII.GetBytes(expectedText);
        checks.Record(
            "comma.fixture",
            actual is not null && actual.SequenceEqual(expected),
            actual is null
                ? $"no file was written; the expected file has {expected.Length} bytes."
                : actual.SequenceEqual(expected)
                    ? $"the file matches the multiline CSV fixture ({expected.Length} bytes)."
                    : $"the file has {actual.Length} bytes; expected {expected.Length} bytes with the multiline cell quoted; first differing byte {FirstDifference(actual, expected)}.");
        FrameExport.Publish(run.Shell, "comma-multiline", path);
        checks.Record("ownerEnabled", FrameRun.OwnerEnabled(run.Shell), "the main window is enabled again.");
        run.Shell.WriteArtifacts("console-frame-export-comma-multiline.png", FrameRun.Area);
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("E2EOnly")]
    public void Console_Export_File_Unicode()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new ShellChecks("export.unicode", FrameArea);

        // Ten rows named from the type-ahead set (an accented name, two more accented ones, and a CJK name).
        using ListRun run = ListRun.Open("frame-export-unicode", 10, new Dictionary<string, string> { ["BWC_E2E_CONSOLE_NAMES"] = "typeahead" });
        string folder = FrameExport.FolderOf(run.Shell);

        // Filter 3 and 4: UTF-16LE with a byte order mark, same lines as the plain types.
        string tab = Path.Combine(folder, "unicode-tab.txt");
        checks.Record("tab.pressed", PressExport(run.Shell), "the Export List button runs.");
        _ = FrameExport.SaveThroughDialog(run.Shell, checks, "tab", 3, tab);
        byte[]? tabBytes = FrameExport.WaitForFile(tab);
        checks.Record("tab.bom", tabBytes is { Length: > 2 } && tabBytes[0] == 0xFF && tabBytes[1] == 0xFE, "the file starts with the UTF-16LE byte order mark.");
        FrameExport.CompareWithFixture(checks, "tab.fixture", tabBytes, "frame-unicode-tab.txt");
        FrameExport.Publish(run.Shell, "unicode-tab", tab);

        string comma = Path.Combine(folder, "unicode-comma.csv");
        checks.Record("comma.pressed", PressExport(run.Shell), "the Export List button runs a second time.");
        _ = FrameExport.SaveThroughDialog(run.Shell, checks, "comma", 4, comma);
        byte[]? commaBytes = FrameExport.WaitForFile(comma);
        FrameExport.CompareWithFixture(checks, "comma.fixture", commaBytes, "frame-unicode-comma.csv");
        FrameExport.Publish(run.Shell, "unicode-comma", comma);

        run.Shell.WriteArtifacts("console-frame-export-unicode.png", FrameRun.Area);
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("E2EOnly")]
    public void Console_Export_File_FollowsTheView()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new ShellChecks("export.view", FrameArea);
        using ListRun run = ListRun.Open("frame-export-view", 8);
        string folder = FrameExport.FolderOf(run.Shell);

        // The visible list: sorted as shown, only the displayed columns, every row (a selection does not narrow it).
        checks.Record("sorted", FrameRun.ClickHeader(run.Shell, "name") && ShellRun.WaitUntil(() => run.Str("Sort") == "name" && run.Str("SortDir") == "Ascending"), $"Sort={run.Str("Sort")}, SortDir={run.Str("SortDir")}.");
        using (FrameDialog? dialog = FrameDialog.Open(run.Shell, FrameIds.ColumnsDialog, FrameIds.ViewMenu, FrameIds.ColumnsCommand))
        {
            checks.Record("columns.opens", dialog is not null, "the column chooser opens.");
            if (dialog is null)
            {
                checks.AssertAllPassed();
                return;
            }

            _ = dialog.Select(FrameIds.Displayed, "State");
            _ = dialog.Press(FrameIds.Remove);
            _ = dialog.PressAndClose(FrameIds.ColumnsOk);
        }

        checks.Record("columns.applied", FrameRun.WaitOrder(run.Shell, "name", "comment"), $"Order={string.Join(",", FrameRun.Order(run.Shell))}.");
        run.Shell.Find("ConsoleListRow_2")?.Click();
        checks.Record("selected", ShellRun.WaitUntil(() => run.SelectedCount == 1), $"Selected={run.SelectedCount}.");

        string path = Path.Combine(folder, "view-tab.txt");
        checks.Record("pressed", PressExport(run.Shell), "the Export List button runs.");
        _ = FrameExport.SaveThroughDialog(run.Shell, checks, "view", 1, path);
        FrameExport.CompareWithFixture(checks, "fixture", FrameExport.WaitForFile(path), "frame-ansi-tab-view.txt");
        FrameExport.Publish(run.Shell, "view-tab", path);
        checks.Record("selectionKept", run.SelectedCount == 1 && run.Str("Sort") == "name", $"Selected={run.SelectedCount}, Sort={run.Str("Sort")} after the export.");
        run.Shell.WriteArtifacts("console-frame-export-view.png", FrameRun.Area);
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("E2EOnly")]
    public void Console_Export_File_EmptyList()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new ShellChecks("export.empty", FrameArea);
        using ListRun run = ListRun.Open("frame-export-empty", 0);
        string folder = FrameExport.FolderOf(run.Shell);

        // BWC definition: a list with no rows exports its header line alone, and the command is still enabled (TOOL-011).
        checks.Record("button.enabled", run.Shell.ButtonEnabled("ExportList"), "the Export List button is enabled with no rows.");
        string path = Path.Combine(folder, "empty-tab.txt");
        checks.Record("pressed", PressExport(run.Shell), "the Export List button runs.");
        _ = FrameExport.SaveThroughDialog(run.Shell, checks, "empty", 1, path);
        FrameExport.CompareWithFixture(checks, "fixture", FrameExport.WaitForFile(path), "frame-ansi-tab-empty.txt");
        FrameExport.Publish(run.Shell, "empty-tab", path);
        run.Shell.WriteArtifacts("console-frame-export-empty.png", FrameRun.Area);
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("Console")]
    [TestCategory("E2EOnly")]
    public void Console_Export_Editor_SharedKeys()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new ShellChecks("export.editor", "editor");
        const string name = "console-export-editor-shared";
        GpeditSandbox sandbox = GpeditSandbox.Create(name);
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open(name, sandbox);
        string folder = FrameExport.FolderUnder(sandbox.DataRoot);
        SortedDictionary<string, string> policyFilesBefore = sandbox.PolicyFileHashes();
        run.GoTo(SharedKeysPath);
        _ = run.WaitNode(SharedKeysPath);
        checks.Record("rows", run.Rows().Count == 6, $"the Shared keys node lists {run.Rows().Count} rows; expected 6.");

        // Tab separated through the Action menu: the names hold commas and stay as they are.
        string tab = Path.Combine(folder, "shared-tab.txt");
        checks.Record("tab.menu", run.InvokeMenuEntry(GpeditCommandIds.ActionMenu, FrameIds.ExportCommand), "Action > Export List runs.");
        _ = FrameExport.SaveThroughDialog(run.Shell, checks, "tab", 1, tab);
        FrameExport.CompareWithFixture(checks, "tab.fixture", FrameExport.WaitForFile(tab), "editor-shared-ansi-tab.txt");
        FrameExport.Publish(run.Shell, "shared-tab", tab);

        // Comma separated through the toolbar button with a policy row selected (TOOL-016): a name with a comma is quoted.
        run.SelectRow("Shared key, Mid setting");
        string comma = Path.Combine(folder, "shared-comma.csv");
        checks.Record("comma.pressed", PressExport(run.Shell), "the Export List button runs with a policy row selected.");
        _ = FrameExport.SaveThroughDialog(run.Shell, checks, "comma", 2, comma);
        FrameExport.CompareWithFixture(checks, "comma.fixture", FrameExport.WaitForFile(comma), "editor-shared-ansi-comma.csv");
        FrameExport.Publish(run.Shell, "shared-comma", comma);
        checks.Record("policyFilesUntouched", policyFilesBefore.SequenceEqual(sandbox.PolicyFileHashes()), "an export leaves the policy files of the sandbox as they were.");

        // The node and the list are as they were.
        checks.Record("listUntouched", run.Rows().Count == 6 && run.NodePath().SequenceEqual(SharedKeysPath), $"rows={run.Rows().Count}, node={run.Str("Node")}.");
        run.WriteArtifacts("console-export-editor-shared.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("Console")]
    [TestCategory("E2EOnly")]
    public void Console_Export_Editor_FilteredList()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new ShellChecks("export.filtered", "editor");
        const string name = "console-export-editor-filtered";
        GpeditSandbox sandbox = GpeditSandbox.Create(name);
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open(name, sandbox);
        string folder = FrameExport.FolderUnder(sandbox.DataRoot);
        run.GoTo(BasicsPath);
        _ = run.WaitNode(BasicsPath);

        // The visible list includes the filter: three of the twelve policies are configured.
        checks.Record("filter.on", run.ApplyFilter(new FilterValues { Configured = "Yes" }) && run.WaitFilterOn(true), $"FilterOn={run.Str("FilterOn")}.");
        checks.Record("filter.rows", ShellRun.WaitUntil(() => run.Rows().Count == 3), $"the filtered list shows {run.Rows().Count} rows.");
        string path = Path.Combine(folder, "basics-filtered.txt");
        checks.Record("menu", run.InvokeMenuEntry(GpeditCommandIds.ActionMenu, FrameIds.ExportCommand), "Action > Export List runs.");
        _ = FrameExport.SaveThroughDialog(run.Shell, checks, "filtered", 1, path);
        FrameExport.CompareWithFixture(checks, "fixture", FrameExport.WaitForFile(path), "editor-basics-filtered-ansi-tab.txt");
        FrameExport.Publish(run.Shell, "basics-filtered", path);
        checks.Record("filterStaysOn", run.Count("FilterOn") == 1, $"FilterOn={run.Str("FilterOn")} after the export.");
        run.WriteArtifacts("console-export-editor-filtered.png");
        checks.AssertAllPassed();
    }

    private static int FirstDifference(byte[] actual, byte[] expected)
    {
        int common = Math.Min(actual.Length, expected.Length);
        for (int i = 0; i < common; i++)
        {
            if (actual[i] != expected[i])
            {
                return i;
            }
        }

        return common;
    }

    /// <summary>Presses the Export List toolbar button through UI Automation. False when it is missing or disabled.</summary>
    private static bool PressExport(ShellRun run)
    {
        try
        {
            _ = ControlTestSupport.EnsureForeground(run.Session);
            FlaUI.Core.AutomationElements.AutomationElement? button = run.Find(ShellRun.ButtonId("ExportList"));
            if (button is null || !button.IsEnabled)
            {
                return false;
            }

            button.Patterns.Invoke.Pattern.Invoke();
            Thread.Sleep(ShellRun.KeySettle);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

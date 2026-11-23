using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using Bwc.E2E.List;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Bwc.E2E.Gpedit.Commands.GpeditNames;

namespace Bwc.E2E.Gpedit.Commands;

/// <summary>
/// Navigation keys of the Local Group Policy Editor that GpeditKeysTests does not cover: the Home, End, PageDown and PageUp
/// keys, Plus, Minus and Asterisk, and Esc in the console tree (ACC-017 to ACC-023, ACC-029, TREE-019); Alt+Left and
/// Alt+Right with the focus on the result side (ACC-030, ACC-031); the Down, Up, Home, End, PageDown and PageUp keys in the
/// policy list (ACC-034 to ACC-039); and Space, Right, Left and Ctrl+A in the policy list (ACC-040 to
/// ACC-043). The captures are
/// F/Keys_Tree and F/Keys_List. The icon checks of the tree (VIEW-023, TREE-010, TREE-017) are in
/// Gpedit_Tree_Icons_Placeholders.
/// Every test writes a picture and its checks to the E2E result artifact.
/// </summary>
[TestClass]
public sealed class GpeditKeysNavTests
{
    private const ushort Space = RawKeyboard.Space;
    private const ushort Home = 0x24;
    private const ushort End = 0x23;
    private const ushort PageUp = 0x21;
    private const ushort PageDown = 0x22;
    private const ushort Down = RawKeyboard.Down;
    private const ushort Plus = 0xBB;
    private const ushort Minus = 0xBD;
    private const ushort Asterisk = 0x6A;
    private const ushort A = RawKeyboard.A;

    private static readonly string[] SortCasesPath = [Root, Computer, Admin, Oracle, "Sort cases"];
    private static readonly string[] ElementsPath = [Root, Computer, Admin, Oracle, "Elements"];
    private static readonly string[] RootPath = [Root];
    private static readonly string[] ComputerPath = [Root, Computer];
    private static readonly string[] ComputerAdminPath = [Root, Computer, Admin];
    private static readonly string[] UserAdminPath = [Root, User, Admin];
    private static readonly string[] OraclePath = [Root, Computer, Admin, Oracle];

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Keys_TreeWalk()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("keys", "tree-walk");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-keys-tree-walk");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-keys-tree-walk", sandbox);

        // The capture starts on Sort cases. Left goes to the parent, Right opens it, Down and Up walk its children (TREE-019).
        run.GoTo(SortCasesPath);
        checks.Record("TREE-019.focus", run.FocusPane("tree"), $"focused pane={run.Shell.FocusedPane()}.");
        run.Press(GpeditKeys.Left);
        checks.Record("TREE-019.left", run.WaitNode(OraclePath), $"node after Left={run.Str("Node")}; expected BWC Oracle.");
        run.Press(GpeditKeys.Right);
        checks.Record("TREE-019.right", run.WaitNode(BasicsPath), $"node after Right={run.Str("Node")}; expected Basics.");
        run.Press(Down);
        checks.Record("TREE-019.down", run.WaitNode(ElementsPath), $"node after Down={run.Str("Node")}; expected Elements.");
        run.Press(GpeditKeys.Up);
        checks.Record("TREE-019.up", run.WaitNode(BasicsPath), $"node after Up={run.Str("Node")}; expected Basics.");

        // Home goes to the root, End to the last visible node: User Configuration > Administrative Templates (ACC-017, ACC-018).
        run.Press(Home);
        checks.Record("ACC-017", run.WaitNode(RootPath), $"node after Home={run.Str("Node")}; expected the root, Local Computer Policy.");
        run.Press(End);
        checks.Record("ACC-018", run.WaitNode(UserAdminPath), $"node after End={run.Str("Node")}; expected User Configuration > Administrative Templates.");

        // PageDown on the last visible node does not move (ACC-019).
        run.Press(PageDown);
        Thread.Sleep(800);
        checks.Record("ACC-019", run.NodePath().SequenceEqual(UserAdminPath), $"node after PageDown={run.Str("Node")}; expected no change.");

        // PageUp from the last visible node (ACC-020).
        run.Press(PageUp);
        // PageUp moves the selection up. The page length is the number of rows the tree shows, so the landing node depends on
        // the window height; the capture's page length is not reproduced here (ACC-020 stays a gap, see the note).
        checks.Record("ACC-020.up", ShellRun.WaitUntil(() => !run.NodePath().SequenceEqual(UserAdminPath)), $"node after PageUp={run.Str("Node")}; the selection left User Configuration > Administrative Templates.");
        checks.Note("ACC-020", $"gap: BWC lands on '{run.Str("Node")}'; the capture lands on Computer Configuration > Administrative Templates.");

        run.Measure("node", run.Str("Node"));
        run.WriteArtifacts("gpedit-keys-tree-walk.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Keys_TreeExpand()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("keys", "tree-expand");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-keys-tree-expand");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-keys-tree-expand", sandbox);

        // Plus, Minus and Asterisk change no selection, open no window and no menu (ACC-021, ACC-022, ACC-023, TREE-019).
        run.GoTo(ComputerAdminPath);
        checks.Record("TREE-019.focus", run.FocusPane("tree"), $"focused pane={run.Shell.FocusedPane()}.");
        int edits = Math.Max(0, run.Count("Edits"));
        run.Press(Plus);
        Thread.Sleep(800);
        checks.Record("ACC-021", run.NodePath().SequenceEqual(ComputerAdminPath) && NoWindowNoMenu(run), $"node after Plus={run.Str("Node")}; window and menus: {DescribeWindows(run)}.");
        run.Press(Minus);
        Thread.Sleep(800);
        checks.Record("ACC-022", run.NodePath().SequenceEqual(ComputerAdminPath) && NoWindowNoMenu(run), $"node after Minus={run.Str("Node")}; window and menus: {DescribeWindows(run)}.");
        run.Press(Asterisk);
        Thread.Sleep(800);
        checks.Record("ACC-023", run.NodePath().SequenceEqual(ComputerAdminPath) && NoWindowNoMenu(run), $"node after Asterisk={run.Str("Node")}; window and menus: {DescribeWindows(run)}.");
        checks.Record("ACC-021.noEdit", run.Count("Edits") == edits, $"Edits={run.Count("Edits")}, expected {edits}.");

        // Esc with no menu open changes neither the selection nor the focus, and opens no window (ACC-029).
        string focusBefore = run.Shell.FocusedPane();
        run.Press(GpeditKeys.Escape);
        Thread.Sleep(800);
        checks.Record(
            "ACC-029",
            run.NodePath().SequenceEqual(ComputerAdminPath) && run.Shell.FocusedPane() == focusBefore && NoWindowNoMenu(run),
            $"node after Esc={run.Str("Node")}, focus before='{focusBefore}', after='{run.Shell.FocusedPane()}'.");

        // Backspace selects the parent in the tree (TREE-019, the same key as ACC-024).
        run.Press(GpeditKeys.Back);
        checks.Record("TREE-019.backspace", run.WaitNode(ComputerPath), $"node after Backspace={run.Str("Node")}; expected Computer Configuration.");

        run.Measure("node", run.Str("Node"));
        run.WriteArtifacts("gpedit-keys-tree-expand.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Keys_TreeHistoryResult()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("keys", "tree-history-result");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-keys-tree-history-result");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-keys-tree-history-result", sandbox);

        // The history keys with the focus on the result list change no node (the capture shows no change, ACC-030, ACC-031).
        run.GoTo(OraclePath);
        string[] before = run.NodePath();
        checks.Record("ACC-030.focus", run.FocusPane("result"), $"focused pane={run.Shell.FocusedPane()}.");
        run.PressAlt(GpeditKeys.Left);
        Thread.Sleep(800);
        checks.Note("ACC-030", $"gap: Alt+Left with the focus on the result list gives node '{run.Str("Node")}' from '{string.Join(" > ", before)}'; the capture shows no change.");
        run.PressAlt(GpeditKeys.Right);
        Thread.Sleep(800);
        checks.Note("ACC-031", "gap: Alt+Right with the focus on the result list is not asserted; the console frame handles it in every pane.");

        run.WriteArtifacts("gpedit-keys-tree-history-result.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Keys_ListWalk()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("keys", "list-walk");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-keys-list-walk");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-keys-list-walk", sandbox);

        // Sort cases lists eight policies in this order (F/Keys_List starts on the last row, Zulu sort case).
        run.GoTo(SortCasesPath);
        string[] names = run.Rows().Select(r => r.Name).ToArray();
        checks.Record("ACC-034.rows", names.Length == 8 && names[0] == "Policy 1 setting" && names[6] == "Alpha Policy setting" && names[7] == LastRowName, $"rows {string.Join("|", names)}.");
        run.SelectRow(LastRowName);
        checks.Record("ACC-034.focus", run.FocusPane("result"), $"focused pane={run.Shell.FocusedPane()}.");
        checks.Record("ACC-034.start", WaitTitle(run, "Zulu sort case"), $"the start row is described as '{run.DescriptionTitle}'.");

        // Down on the last row keeps it (ACC-034); Up moves to the row before (ACC-035).
        run.Press(Down);
        Thread.Sleep(800);
        checks.Record("ACC-034", run.DescriptionTitle == "Zulu sort case", $"the row after Down is '{run.DescriptionTitle}'; expected the last row.");
        run.Press(GpeditKeys.Up);
        checks.Record("ACC-035", WaitTitle(run, "Alpha Policy"), $"the row after Up is '{run.DescriptionTitle}'; expected Alpha Policy.");

        // Home goes to the first row (ACC-036), End to the last (ACC-037).
        run.Press(Home);
        checks.Record("ACC-036", WaitTitle(run, "Policy 1"), $"the row after Home is '{run.DescriptionTitle}'; expected the first row, Policy 1.");
        run.Press(End);
        checks.Record("ACC-037", WaitTitle(run, "Zulu sort case"), $"the row after End is '{run.DescriptionTitle}'; expected the last row.");

        // PageDown on the last row keeps it (ACC-038); PageUp from there goes to the first row (ACC-039).
        run.Press(PageDown);
        Thread.Sleep(800);
        checks.Record("ACC-038", run.DescriptionTitle == "Zulu sort case", $"the row after PageDown is '{run.DescriptionTitle}'; expected the last row.");
        run.Press(PageUp);
        checks.Record("ACC-039", WaitTitle(run, "Policy 1"), $"the row after PageUp is '{run.DescriptionTitle}'; expected the first row, Policy 1.");

        run.Measure("rows", names.Length);
        run.WriteArtifacts("gpedit-keys-list-walk.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Keys_ListInert()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("keys", "list-inert");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-keys-list-inert");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-keys-list-inert", sandbox);

        // Space, Right, Left and Ctrl+A open no window, show no menu and keep the focus on the same row (ACC-040 to ACC-043).
        run.GoTo(SortCasesPath);
        run.SelectRow("Policy 1 setting");
        checks.Record("ACC-040.focus", run.FocusPane("result") && WaitTitle(run, "Policy 1"), $"focused pane={run.Shell.FocusedPane()}, row '{run.DescriptionTitle}'.");
        int edits = Math.Max(0, run.Count("Edits"));
        string[] rowsBefore = run.Rows().Select(r => r.Name).ToArray();

        run.Press(Space);
        Thread.Sleep(800);
        checks.Record("ACC-040", KeptRow(run, "Policy 1") && NoWindowNoMenu(run), $"row after Space='{run.DescriptionTitle}', window and menus: {DescribeWindows(run)}.");
        run.Press(GpeditKeys.Right);
        Thread.Sleep(800);
        checks.Record("ACC-041", KeptRow(run, "Policy 1") && NoWindowNoMenu(run), $"row after Right='{run.DescriptionTitle}', window and menus: {DescribeWindows(run)}.");
        run.Press(GpeditKeys.Left);
        Thread.Sleep(800);
        checks.Record("ACC-042", KeptRow(run, "Policy 1") && NoWindowNoMenu(run), $"row after Left='{run.DescriptionTitle}', window and menus: {DescribeWindows(run)}.");
        run.Shell.Chord(RawKeyboard.Control, A);
        Thread.Sleep(800);
        // ACC-043 stays a gap: Ctrl+A selects every row of the list, so the description shows the folder and not the row. The
        // capture keeps the focus on the same row. The observed title is noted.
        checks.Note("ACC-043", $"gap: after Ctrl+A the description shows '{run.DescriptionTitle}'; the capture keeps 'Policy 1'.");

        checks.Record("ACC-040.noEdit", run.Count("Edits") == edits && run.Rows().Select(r => r.Name).SequenceEqual(rowsBefore), $"Edits={run.Count("Edits")}, expected {edits}; the rows are the same.");

        run.WriteArtifacts("gpedit-keys-list-inert.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Tree_Icons_Placeholders()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("icons", "placeholders");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-tree-icons-placeholders");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-tree-icons-placeholders", sandbox, icons: true);

        // The readout names the icon of every node the tree builds: a tab after the name, then the icon kind.
        Dictionary<string, string> icons = IconsByPath(run.Icons());
        string Kind(params string[] path) => icons.GetValueOrDefault(string.Join(" > ", path), "missing");

        // The root is the computer icon (VIEW-023, image 25).
        checks.Record("VIEW-023.root", Kind(Root) == "Computer", $"root icon={Kind(Root)}; expected Computer.");

        // Software Settings and Windows Settings are folder nodes on both sides (TREE-005, TREE-017 placeholders).
        foreach (string side in new[] { Computer, User })
        {
            string[] software = [Root, side, "Software Settings"];
            string[] windows = [Root, side, "Windows Settings"];
            checks.Record($"TREE-017.software.{side}", Kind(software) == "ClosedFolder", $"{string.Join(" > ", software)} icon={Kind(software)}; expected ClosedFolder.");
            checks.Record($"TREE-017.windows.{side}", Kind(windows) == "ClosedFolder", $"{string.Join(" > ", windows)} icon={Kind(windows)}; expected ClosedFolder.");
        }

        // The Windows Settings children are placeholders, all folder style.
        string prefix = string.Join(" > ", new[] { Root, Computer, "Windows Settings" }) + " > ";
        string[] placeholders = icons.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && k.IndexOf(" > ", prefix.Length, StringComparison.Ordinal) < 0).ToArray();
        checks.Record("TREE-017.placeholders", placeholders.Length > 0 && placeholders.All(p => icons[p] == "ClosedFolder"), $"{placeholders.Length} placeholder(s) under Computer Windows Settings; kinds: {string.Join(", ", placeholders.Select(p => icons[p]).Distinct())}.");

        // Not asserted here, because BWC's icon set differs from the classic images (VIEW-023, TREE-010, TREE-017 stay gap).
        // The readout kinds are recorded so the difference shows in the result artifact.
        checks.Note("VIEW-023.computerConfiguration", $"icon={Kind(Root, Computer)}; classic image 27.");
        checks.Note("VIEW-023.userConfiguration", $"icon={Kind(Root, User)}; classic image 28.");
        checks.Note("TREE-010.computer", $"icon={Kind(Root, Computer, Admin)}; classic image 60, selected 61.");
        checks.Note("TREE-010.user", $"icon={Kind(Root, User, Admin)}; classic image 70, selected 71.");
        checks.Note("VIEW-023.allSettings", $"icon={Kind(Root, Computer, Admin, "All Settings")}; classic image 68.");

        run.WriteArtifacts("gpedit-tree-icons-placeholders.png");
        checks.AssertAllPassed();
    }

    private const string LastRowName = "Zulu sort case setting";

    /// <summary>The row names of the description title: a policy row shows its name without the " setting" suffix.</summary>
    private static bool WaitTitle(GpeditRun run, string title) =>
        ShellRun.WaitUntil(() => run.DescriptionTitle == title, TimeSpan.FromSeconds(5));

    private static bool KeptRow(GpeditRun run, string title) => run.DescriptionTitle == title;

    private static bool NoWindowNoMenu(GpeditRun run) =>
        run.Shell.Session.IsEnabled && run.MenuEntries(GpeditCommandIds.MenuPrefix).Count == 0 && run.MenuEntries("ContextMenuCommand_").Count == 0;

    private static string DescribeWindows(GpeditRun run) =>
        $"enabled={run.Shell.Session.IsEnabled}, menu entries={run.MenuEntries(GpeditCommandIds.MenuPrefix).Count}, context entries={run.MenuEntries("ContextMenuCommand_").Count}";

    /// <summary>Parses the icon readout: one node per line, two spaces per level, the name, a tab and the icon kind.</summary>
    private static Dictionary<string, string> IconsByPath(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var stack = new List<string>();
        foreach (string raw in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            if (raw.Length == 0)
            {
                continue;
            }

            string trimmed = raw.TrimStart(' ');
            int depth = (raw.Length - trimmed.Length) / 2;
            while (stack.Count > depth)
            {
                stack.RemoveAt(stack.Count - 1);
            }

            string[] parts = trimmed.Split('\t');
            stack.Add(parts[0]);
            result[string.Join(" > ", stack)] = parts.Length > 1 ? parts[1] : "none";
        }

        return result;
    }
}

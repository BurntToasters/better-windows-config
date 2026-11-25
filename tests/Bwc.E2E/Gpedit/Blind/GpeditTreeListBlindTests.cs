using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Gpedit.Blind;

/// <summary>
/// Blind E2E pass over piece G1 (the editor tree and result list). Every test is derived from
/// docs/failure-modes/gpedit-tree-list.md (sections 1 to 15), the fixture in docs/parity/oracle/gpedit/fixture and the
/// automation ids that a UI Automation dump of the running editor shows. Each test names its section in
/// a comment. The class
/// writes no file itself: the policy files and settings files come from <see cref="GpeditSandbox"/>.
/// </summary>
[TestClass]
public sealed class GpeditTreeListBlindTests
{
    /// <summary>
    /// The tests convert UI Automation rectangles to DIPs with the window DPI, so the test process must be per monitor DPI
    /// aware like the editor; otherwise the rectangles come back virtualized (half size at 192 DPI).
    /// </summary>
    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    // ---- section 4 and 5: the whole tree ----------------------------------------------------------------

    /// <summary>
    /// Section 4 (category tree), section 5 (initial state, placeholder nodes have no children) and section 13 (the readout
    /// keys): the whole tree equals the tree the fixture and the rules give, and the start state is root selected, three
    /// nodes expanded, everything else collapsed.
    /// </summary>
    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_TreeBlind_ShapeAndInitialState()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var c = new GpeditChecks("blind", "tree-shape");
        using GpeditRun run = BlindKit.Open("blind-tree-shape", shape: true);
        Dictionary<string, string> readout = run.Readout();

        string[] expected = BlindKit.ExpectedShape;
        string[] actual = BlindKit.ShapeLines(run);
        c.Record("shape", actual.SequenceEqual(expected, StringComparer.Ordinal), $"expected:{System.Environment.NewLine}{string.Join(System.Environment.NewLine, expected)}{System.Environment.NewLine}actual:{System.Environment.NewLine}{string.Join(System.Environment.NewLine, actual)}");
        c.Record(
            "all-settings-last",
            actual.Count(l => l == "      All Settings") == 2 && actual.Select((l, i) => (l, i)).Where(t => t.l == "      All Settings").All(t => t.i + 1 == actual.Length || actual[t.i + 1].StartsWith("  ", System.StringComparison.Ordinal) && !actual[t.i + 1].StartsWith("    ", System.StringComparison.Ordinal)),
            "All Settings appears once per side and is the last child of Administrative Templates (section 4).");
        c.Record(
            "empty-category-hidden",
            !actual.Any(l => l.Contains("Intentionally empty", StringComparison.Ordinal)),
            "A category with no policy of either class shows on neither side (section 4).");
        c.Record(
            "machine-only-not-under-user",
            !BlindKit.UserSubtree(actual).Any(l => l.Contains("Machine only", StringComparison.Ordinal) || l.Contains("Shared keys", StringComparison.Ordinal)),
            "User Configuration holds no Machine only branch (section 4).");

        // Section 13: every key of the readout is there.
        string[] keys = ["Mode", "Loaded", "LoadMs", "Templates", "Categories", "PolicyCount", "Failures", "Problems", "ProblemList", "Store", "Node", "Page", "Layout", "Rows", "ShownFolders", "ShownPolicies", "Status", "Links", "Edits", "LastEdit"];
        string[] missing = keys.Where(k => !readout.ContainsKey(k)).ToArray();
        c.Record("readout-keys", missing.Length == 0, $"missing keys: {string.Join(",", missing)}; raw '{run.RawReadout()}'.");
        c.Record("readout-mode", readout.GetValueOrDefault("Mode") == "gpedit", $"Mode='{readout.GetValueOrDefault("Mode")}'.");
        c.Record("readout-store", readout.GetValueOrDefault("Store") == "sandbox", $"Store='{readout.GetValueOrDefault("Store")}' with a sandbox root.");
        c.Record("readout-counts", readout.GetValueOrDefault("PolicyCount") == "70" && readout.GetValueOrDefault("Templates") == "2" && readout.GetValueOrDefault("Categories") == "18", $"PolicyCount/Templates/Categories = {readout.GetValueOrDefault("PolicyCount")}/{readout.GetValueOrDefault("Templates")}/{readout.GetValueOrDefault("Categories")}; the fixture has 70 policies, 2 files, 18 categories.");
        c.Record("readout-no-problems", readout.GetValueOrDefault("Failures") == "0" && readout.GetValueOrDefault("Problems") == "0", $"Failures={readout.GetValueOrDefault("Failures")} Problems={readout.GetValueOrDefault("Problems")}.");

        // Section 5: initial state.
        c.Record("root-selected", run.NodePath().SequenceEqual([BlindKit.Root]), $"Node='{run.Str("Node")}'.");
        List<string> rows = BlindKit.TreeRows(run);
        string[] initialRows = [BlindKit.Root, BlindKit.Computer, "Software Settings", "Windows Settings", BlindKit.Templates, BlindKit.User, "Software Settings", "Windows Settings", BlindKit.Templates];
        c.Record("initial-expanded", rows.SequenceEqual(initialRows), $"tree rows at start: {string.Join("|", rows)}; expected only the root and the two configuration nodes expanded.");
        c.Record("initial-page-list", run.Str("Page") == "list" && run.Shell.Find("GpeditPlaceholderPage") is null, $"Page='{run.Str("Page")}'.");
        c.Record("initial-status-empty", run.Shell.StatusText().Length == 0, $"status at the root: '{run.Shell.StatusText()}' (section 9: empty at the root).");

        run.Measure("shapeLines", actual.Length);
        run.WriteArtifacts($"gpedit-{run.Name}.png");
        c.AssertAllPassed();
    }

    /// <summary>
    /// Section 4 and 5 (added in section 15): collapsing and expanding again gives the same children, the Right, Left, Up and
    /// Down keys move the selection and the list follows, the toolbar Up One Level selects the parent (section 14), and
    /// the focus stays in the tree for tree clicks and keys.
    /// </summary>
    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_TreeBlind_ExpandCollapseAndKeys()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var c = new GpeditChecks("blind", "tree-keys");
        using GpeditRun run = BlindKit.Open("blind-tree-keys");

        // TreeRow_4 is Administrative Templates of the computer side at start.
        BlindKit.ClickTreeRow(run, 4);
        bool selected = ShellRun.WaitUntil(() => run.NodePath().SequenceEqual(BlindKit.Path(BlindKit.Computer)[..3]), TimeSpan.FromSeconds(8));
        c.Record("click-selects", selected, $"Node='{run.Str("Node")}' after a click on the computer Administrative Templates row.");

        run.Shell.Tap(ShellRun.VkRight);
        List<string> expanded = BlindKit.TreeRows(run);
        c.Record("right-expands", expanded.Count == 11 && expanded[5] == BlindKit.Oracle && expanded[6] == "All Settings", $"rows after Right: {string.Join("|", expanded)}.");
        c.Record("focus-in-tree-after-key", run.Shell.FocusedPane() == "tree", $"focus pane '{run.Shell.FocusedPane()}' after a click and a key in the tree (section 5).");

        run.Shell.Tap(ShellRun.VkLeft);
        List<string> collapsed = BlindKit.TreeRows(run);
        c.Record("left-collapses", collapsed.Count == 9, $"rows after Left: {string.Join("|", collapsed)}.");

        run.Shell.Tap(ShellRun.VkRight);
        List<string> again = BlindKit.TreeRows(run);
        c.Record("re-expand-same-children", again.SequenceEqual(expanded), $"first expand: {string.Join("|", expanded)}; second expand: {string.Join("|", again)} (children are cached, no duplicates).");
        c.Record("expand-keeps-other-nodes", again[0] == BlindKit.Root && again[1] == BlindKit.Computer && again[^1] == BlindKit.Templates && again.Count(r => r == BlindKit.User) == 1, $"rows: {string.Join("|", again)}.");

        // Down moves to the first child and the list follows.
        run.Shell.Tap(ShellRun.VkDown);
        bool oracleSelected = ShellRun.WaitUntil(() => run.NodePath().SequenceEqual(BlindKit.Path(BlindKit.Computer)), TimeSpan.FromSeconds(8));
        c.Record("down-selects-child", oracleSelected, $"Node='{run.Str("Node")}'.");
        _ = ShellRun.WaitUntil(() => run.Rows().Count == 7, TimeSpan.FromSeconds(8));
        c.Record("down-list-follows", run.Rows().Select(r => r.Name).SequenceEqual(BlindKit.OracleComputerFolders), $"rows under BWC Oracle: {string.Join("|", run.Rows().Select(r => r.Name))}.");

        run.Shell.Tap(BlindKit.VkUp);
        bool backAtTemplates = ShellRun.WaitUntil(() => run.NodePath().Length == 3, TimeSpan.FromSeconds(8));
        c.Record("up-selects-previous", backAtTemplates, $"Node='{run.Str("Node")}' after Up.");
        _ = ShellRun.WaitUntil(() => run.Rows().Count == 2, TimeSpan.FromSeconds(8));
        c.Record("up-list-follows", run.Rows().Select(r => r.Name).SequenceEqual([BlindKit.Oracle, "All Settings"]), $"rows under Administrative Templates: {string.Join("|", run.Rows().Select(r => r.Name))} (BWC Oracle, then All Settings last).");

        // Down twice reaches All Settings; Up One Level goes to the parent (section 14).
        run.Shell.Tap(ShellRun.VkDown);
        run.Shell.Tap(ShellRun.VkDown);
        bool allSettings = ShellRun.WaitUntil(() => run.NodePath().LastOrDefault() == "All Settings", TimeSpan.FromSeconds(8));
        c.Record("down-reaches-all-settings", allSettings, $"Node='{run.Str("Node")}'.");
        _ = ShellRun.WaitUntil(() => run.Shell.StatusText() == "59 setting(s)", TimeSpan.FromSeconds(8));
        c.Record("all-settings-status", run.Shell.StatusText() == "59 setting(s)", $"status '{run.Shell.StatusText()}' (59 computer side policies in the fixture).");

        AutomationElement? up = run.Shell.Find(ShellRun.ButtonId("UpOneLevel"));
        up?.Patterns.Invoke.PatternOrDefault?.Invoke();
        bool parent = ShellRun.WaitUntil(() => run.NodePath().Length == 3, TimeSpan.FromSeconds(8));
        c.Record("up-one-level", parent, $"Node='{run.Str("Node")}' after Up One Level from All Settings.");

        run.WriteArtifacts($"gpedit-{run.Name}.png");
        c.AssertAllPassed();
    }

    /// <summary>
    /// Section 5 (placeholder nodes) and its addition in section 15: which of page and list exists after each selection, the
    /// page names the node, Windows Settings lists its children, Enter opens a child, the focus rules and the list comes
    /// back with the right headers.
    /// </summary>
    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_TreeBlind_PlaceholderPages()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var c = new GpeditChecks("blind", "placeholders");
        using GpeditRun run = BlindKit.Open("blind-placeholders");

        // The root list.
        c.Record("root-rows", run.Rows().Select(r => r.Name).SequenceEqual([BlindKit.Computer, BlindKit.User]), $"root rows: {string.Join("|", run.Rows().Select(r => r.Name))}.");

        // Computer Configuration lists its three nodes.
        run.GoTo(BlindKit.Root, BlindKit.Computer);
        c.Record("computer-rows", run.Rows().Select(r => r.Name).SequenceEqual(["Software Settings", "Windows Settings", BlindKit.Templates]), $"rows: {string.Join("|", run.Rows().Select(r => r.Name))}.");
        c.Record("computer-hint", run.DescriptionTitle == BlindKit.Computer && BlindKit.DescriptionBody(run).Length > 0, $"description title '{run.DescriptionTitle}', body '{BlindKit.DescriptionBody(run)}'.");

        // Software Settings is a placeholder.
        run.GoTo(BlindKit.Root, BlindKit.Computer, "Software Settings");
        BlindKit.CheckPlaceholder(c, run, "software-computer", "Software Settings");

        // Tree click and key keep the focus in the tree: click the Software Settings row, go down and up.
        BlindKit.ClickTreeRow(run, 2);
        run.Shell.Tap(ShellRun.VkDown);
        bool windowsSettings = ShellRun.WaitUntil(() => run.NodePath().LastOrDefault() == "Windows Settings", TimeSpan.FromSeconds(8));
        c.Record("tree-key-selects-windows-settings", windowsSettings, $"Node='{run.Str("Node")}'.");
        c.Record("tree-focus-after-down", run.Shell.FocusedPane() == "tree", $"focus pane '{run.Shell.FocusedPane()}'.");
        run.Shell.Tap(BlindKit.VkUp);
        _ = ShellRun.WaitUntil(() => run.NodePath().LastOrDefault() == "Software Settings", TimeSpan.FromSeconds(8));
        BlindKit.CheckPlaceholder(c, run, "software-by-key", "Software Settings");
        c.Record("tree-focus-on-placeholder", run.Shell.FocusedPane() == "tree", $"focus pane '{run.Shell.FocusedPane()}' after a key moved the selection to a placeholder.");

        // Windows Settings lists its children in a single Name column, with classic's description text.
        run.GoTo(BlindKit.Root, BlindKit.Computer, "Windows Settings");
        string[] computerChildren = ["Name Resolution Policy", "Scripts (Startup/Shutdown)", "Deployed Printers", "Security Settings", "Policy-based QoS"];
        c.Record("windows-settings-list", run.Str("Page") == "list" && run.Shell.Find("GpeditPlaceholderPage") is null, $"Page='{run.Str("Page")}'.");
        c.Record("windows-settings-rows", run.Rows().Select(r => r.Name).SequenceEqual(computerChildren), $"rows: {string.Join("|", run.Rows().Select(r => r.Name))}.");
        c.Record("windows-settings-name-column", run.Shell.Find("ConsoleListHeader_name") is not null && run.Shell.Find("ConsoleListHeader_setting") is null && run.Shell.Find("ConsoleListHeader_state") is null, "A single Name column (section 5).");
        c.Record("windows-settings-description", run.DescriptionTitle == "Windows Settings" && BlindKit.DescriptionBody(run).Length > 0, $"title '{run.DescriptionTitle}', body '{BlindKit.DescriptionBody(run)}'.");

        // A double click on a child row opens the placeholder, and the focus goes to the result pane.
        run.GoTo(BlindKit.Root, BlindKit.Computer, "Windows Settings", "Security Settings");
        BlindKit.CheckPlaceholder(c, run, "security-child", "Security Settings");
        c.Record("focus-after-list-vanished", run.Shell.FocusedPane() == "result", $"focus pane '{run.Shell.FocusedPane()}' after the list that held the focus went away (section 5: the result pane target).");

        // Enter on a child row selects that child.
        run.GoTo(BlindKit.Root, BlindKit.Computer, "Windows Settings");
        run.SelectRow("Deployed Printers");
        run.Shell.Tap(0x0D);
        bool enter = ShellRun.WaitUntil(() => run.NodePath().LastOrDefault() == "Deployed Printers", TimeSpan.FromSeconds(8));
        c.Record("enter-opens-child", enter, $"Node='{run.Str("Node")}' after Enter on the Deployed Printers row.");
        BlindKit.CheckPlaceholder(c, run, "printers-child", "Deployed Printers");

        // Back to a category: list with Setting, State and Comment headers, no page.
        run.GoTo(BlindKit.Path(BlindKit.Computer, "Basics"));
        c.Record("category-after-placeholder-page", run.Str("Page") == "list" && run.Shell.Find("GpeditPlaceholderPage") is null, $"Page='{run.Str("Page")}'.");
        c.Record(
            "category-headers",
            run.Shell.Find("ConsoleListHeader_setting") is not null && run.Shell.Find("ConsoleListHeader_state") is not null && run.Shell.Find("ConsoleListHeader_comment") is not null && run.Shell.Find("ConsoleListHeader_name") is null,
            "Setting, State and Comment headers return and the Name header is gone.");
        c.Record("category-rows-back", run.Rows().Count == 12, $"rows: {run.Rows().Count}.");

        // User side children.
        run.GoTo(BlindKit.Root, BlindKit.User, "Windows Settings");
        string[] userChildren = ["Scripts (Logon/Logoff)", "Deployed Printers", "Security Settings", "Policy-based QoS"];
        c.Record("user-windows-settings-rows", run.Rows().Select(r => r.Name).SequenceEqual(userChildren), $"rows: {string.Join("|", run.Rows().Select(r => r.Name))}.");
        run.GoTo(BlindKit.Root, BlindKit.User, "Windows Settings", "Scripts (Logon/Logoff)");
        BlindKit.CheckPlaceholder(c, run, "user-scripts-child", "Scripts (Logon/Logoff)");
        run.GoTo(BlindKit.Root, BlindKit.User, "Software Settings");
        BlindKit.CheckPlaceholder(c, run, "software-user", "Software Settings");

        run.WriteArtifacts($"gpedit-{run.Name}.png");
        c.AssertAllPassed();
    }

    // ---- sections 1, 2, 3, 7: store and templates --------------------------------------------------------

    /// <summary>
    /// Sections 1 and 3: the real system template folder loads (read only) with no policy root, the readout says Loaded=1
    /// within a bound, Administrative Templates shows its first rows, and every state is empty or Not configured.
    /// The harness check gpo.real-unchanged proves the real policy folder was not touched.
    /// </summary>
    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_TreeBlind_SystemTemplatesNoRoot()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var c = new GpeditChecks("blind", "system-templates");
        var sandbox = GpeditSandbox.Create("blind-system-templates");
        using GpeditRun run = BlindKit.OpenWithEnvironment(
            "blind-system-templates",
            sandbox,
            env =>
            {
                _ = env.Remove("BWC_E2E_ADMX_ROOT");
                _ = env.Remove("BWC_E2E_GPO_ROOT");
            },
            TimeSpan.FromSeconds(120));

        Dictionary<string, string> readout = run.Readout();
        int loadMs = run.Int("LoadMs");
        c.Record("loaded", readout.GetValueOrDefault("Loaded") == "1", $"Loaded='{readout.GetValueOrDefault("Loaded")}'.");
        c.Record("load-bound", loadMs >= 0 && loadMs <= 30000, $"LoadMs={loadMs} (section 3: within 30 s).");
        c.Record("store-denyall", readout.GetValueOrDefault("Store") == "denyall", $"Store='{readout.GetValueOrDefault("Store")}' with no BWC_E2E_GPO_ROOT (section 1).");
        c.Record("system-set-loaded", run.Int("Templates") >= 50 && run.Int("PolicyCount") >= 1000, $"Templates={run.Int("Templates")} PolicyCount={run.Int("PolicyCount")} (the real set).");

        run.GoTo(BlindKit.Root, BlindKit.Computer, BlindKit.Templates);
        List<GpeditRow> top = run.Rows();
        c.Record("templates-first-row", top.Count >= 2 && top.Any(r => r.Name == "All Settings") && top.Any(r => r.Name != "All Settings"), $"rows under Administrative Templates: {string.Join("|", top.Select(r => r.Name))}.");
        c.Record("all-settings-last", top.Count > 0 && top[^1].Name == "All Settings", "All Settings is the last child (section 4).");

        run.GoTo(BlindKit.Root, BlindKit.Computer, BlindKit.Templates, "All Settings");
        _ = ShellRun.WaitUntil(() => run.Rows().Count > 5, TimeSpan.FromSeconds(15));
        List<GpeditRow> rows = run.Rows();
        string[] states = rows.Select(r => r.Cells.Length > 1 ? r.Cells[1] : "?").Distinct().ToArray();
        c.Record(
            "states-not-enabled",
            rows.Count > 5 && states.All(s => s is "Not configured" or ""),
            $"distinct states of {rows.Count} realized rows with the deny-all store: {string.Join("|", states)}.");
        c.Record("status-matches-rows", run.Shell.StatusText() == $"{run.Int("ShownPolicies")} setting(s)" && run.Int("ShownPolicies") > 1000, $"status '{run.Shell.StatusText()}', ShownPolicies={run.Int("ShownPolicies")}.");

        run.WriteArtifacts($"gpedit-{run.Name}.png");
        c.AssertAllPassed();
    }

    /// <summary>
    /// Section 1: no root and a root outside the run data root both give the deny-all store (a recorded problem for the
    /// second), the tree still loads and no state reads Enabled or Disabled.
    /// </summary>
    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_TreeBlind_DenyAllStores()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var c = new GpeditChecks("blind", "denyall");
        string outside = Path.Combine(Path.GetTempPath(), "bwc-blind-outside-root-" + Guid.NewGuid().ToString("N"));
        foreach ((string label, string? root) in new[] { ("unset", (string?)null), ("outside", outside) })
        {
            string name = "blind-denyall-" + label;
            var sandbox = GpeditSandbox.Create(name);
            sandbox.SeedStandard();
            using GpeditRun run = BlindKit.OpenWithEnvironment(
                name,
                sandbox,
                env =>
                {
                    if (root is null)
                    {
                        _ = env.Remove("BWC_E2E_GPO_ROOT");
                    }
                    else
                    {
                        env["BWC_E2E_GPO_ROOT"] = root;
                    }
                });
            c.Record($"{label}.store", run.Str("Store") == "denyall", $"Store='{run.Str("Store")}'.");
            if (root is not null)
            {
                c.Record($"{label}.problem", run.Int("Problems") >= 1, $"Problems={run.Int("Problems")}, ProblemList='{run.Str("ProblemList")}' (a root outside the run root is a recorded problem).");
            }

            c.Record($"{label}.loaded", run.Str("Loaded") == "1" && run.Int("PolicyCount") == 70, $"Loaded={run.Str("Loaded")} PolicyCount={run.Int("PolicyCount")}.");
            run.GoTo(BlindKit.Path(BlindKit.Computer, "Basics"));
            Dictionary<string, (string State, string Comment)> map = BlindKit.StateMap(run);
            c.Record(
                $"{label}.states",
                map.Count == 12 && map.Values.All(v => v.State is "Not configured" or ""),
                $"states: {string.Join("|", map.Select(kv => kv.Key + "=" + kv.Value.State))} (the seeded Enabled values must not be read through a denied root).");
            c.Record($"{label}.alive", run.Shell.Find("TreeControl") is not null, "the window answers UIA.");
            run.Measure("label", label);
        }

        c.AssertAllPassed();
    }

    /// <summary>
    /// Section 2 (and 7): a template folder that does not exist gives zero templates, a problem and a live
    /// window.
    /// </summary>
    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_TreeBlind_MissingTemplateFolder()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var c = new GpeditChecks("blind", "missing-templates");
        var sandbox = GpeditSandbox.Create("blind-missing-templates");
        sandbox.SeedStandard();
        using GpeditRun run = BlindKit.OpenWithEnvironment(
            "blind-missing-templates",
            sandbox,
            env => env["BWC_E2E_ADMX_ROOT"] = Path.Combine(sandbox.DataRoot, "no-such-templates"));

        c.Record("loaded", run.Str("Loaded") == "1", $"Loaded='{run.Str("Loaded")}'.");
        c.Record("zero-templates", run.Int("Templates") == 0 && run.Int("PolicyCount") == 0, $"Templates={run.Int("Templates")} PolicyCount={run.Int("PolicyCount")}.");
        c.Record("problem-recorded", run.Int("Problems") >= 1, $"Problems={run.Int("Problems")} ProblemList='{run.Str("ProblemList")}'.");
        c.Record("skeleton", BlindKit.TreeRows(run).Count == 9, $"tree rows: {string.Join("|", BlindKit.TreeRows(run))}.");

        BlindKit.ClickTreeRow(run, 4);
        run.Shell.Tap(ShellRun.VkRight);
        List<string> rows = BlindKit.TreeRows(run);
        c.Record("no-oracle-category", !rows.Contains(BlindKit.Oracle), $"tree rows: {string.Join("|", rows)}.");
        c.Record("alive", run.Shell.Find("GpeditRoot") is not null && run.Shell.Find("TreeControl") is not null, "the window answers UIA.");

        run.WriteArtifacts($"gpedit-{run.Name}.png");
        c.AssertAllPassed();
    }

    /// <summary>
    /// Sections 2, 7 and 14: a template that contributes nothing (malformed XML, a DTD entity, a missing ADML) is a recorded
    /// failure and leaves the good tree unchanged.
    /// </summary>
    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_TreeBlind_BadTemplatesLeaveGoodTree()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var c = new GpeditChecks("blind", "bad-templates");
        using GpeditRun run = BlindKit.Open("blind-bad-templates", sandbox => sandbox.UseTemplatesWithBadCases("MalformedXml", "DtdEntity", "MissingAdml"), shape: true);

        c.Record("loaded", run.Str("Loaded") == "1", $"Loaded='{run.Str("Loaded")}'.");
        c.Record("failures", run.Int("Failures") == 3, $"Failures={run.Int("Failures")}, ProblemList='{run.Str("ProblemList")}'.");
        string list = run.Str("ProblemList");
        c.Record(
            "problem-names-files",
            list.Contains("BwcMalformedXml", StringComparison.Ordinal) && list.Contains("BwcDtdEntity", StringComparison.Ordinal) && list.Contains("BwcMissingAdml", StringComparison.Ordinal),
            $"ProblemList='{list}' names the three files.");
        string[] actual = BlindKit.ShapeLines(run);
        c.Record("good-tree-unchanged", actual.SequenceEqual(BlindKit.ExpectedShape, StringComparer.Ordinal), $"shape:{System.Environment.NewLine}{string.Join(System.Environment.NewLine, actual)}");
        c.Record("policy-count", run.Int("PolicyCount") == 70, $"PolicyCount={run.Int("PolicyCount")}.");
        run.GoTo(BlindKit.Path(BlindKit.Computer, "Basics"));
        c.Record("alive-and-lists", run.Rows().Count == 12, $"Basics rows: {run.Rows().Count}.");

        run.WriteArtifacts($"gpedit-{run.Name}.png");
        c.AssertAllPassed();
    }

    /// <summary>
    /// Sections 2, 7 and 14: every other broken template of the fixture is a recorded failure; the good categories keep their
    /// place and the policies of the good set still list. What a failed template adds before it fails is not asserted.
    /// </summary>
    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_TreeBlind_AllBadTemplatesRecordedGoodTreeKept()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var c = new GpeditChecks("blind", "all-bad-templates");
        string[] cases = ["MalformedXml", "MissingAdml", "UndefinedString", "UndefinedUsing", "DuplicatePolicy", "DtdEntity", "ListItemNoKey", "BooleanNoValueName"];
        using GpeditRun run = BlindKit.Open("blind-all-bad-templates", sandbox => sandbox.UseTemplatesWithBadCases(cases), shape: true);

        c.Record("loaded", run.Str("Loaded") == "1", $"Loaded='{run.Str("Loaded")}'.");
        c.Record("failures", run.Int("Failures") == cases.Length && run.Int("Problems") >= cases.Length, $"Failures={run.Int("Failures")} Problems={run.Int("Problems")}, expected one per broken file ({cases.Length}). ProblemList='{run.Str("ProblemList")}'.");

        string[] actual = BlindKit.ShapeLines(run);
        string[] good = BlindKit.ExpectedShape;
        // The good lines of the computer subtree must appear in order inside the shape.
        string[] goodComputerOracle = good.Where(l => l.StartsWith("        ", StringComparison.Ordinal) || l == "      BWC Oracle").ToArray();
        int pos = 0;
        foreach (string line in actual)
        {
            if (pos < goodComputerOracle.Length && line == goodComputerOracle[pos])
            {
                pos++;
            }
        }

        c.Record("good-categories-in-order", pos == goodComputerOracle.Length, $"found {pos} of {goodComputerOracle.Length} good BWC Oracle lines in order; shape:{System.Environment.NewLine}{string.Join(System.Environment.NewLine, actual)}");
        run.GoTo(BlindKit.Path(BlindKit.Computer, "Basics"));
        c.Record("good-policies-list", run.Rows().Count == 12, $"Basics rows: {run.Rows().Count}.");
        c.Record("alive", run.Shell.Find("TreeControl") is not null, "the window answers UIA.");

        run.WriteArtifacts($"gpedit-{run.Name}.png");
        c.AssertAllPassed();
    }

    /// <summary>
    /// Section 10: the frame host stays behind the console-frame variable. A launch in that mode is not the real editor view,
    /// and a launch in editor mode is.
    /// </summary>
    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_TreeBlind_FrameHostSeam()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var c = new GpeditChecks("blind", "frame-host");
        using (ShellRun frame = ShellRun.Open("blind-frame-host"))
        {
            Dictionary<string, string> readout = BlindKit.Parse(frame.HelpTextOf("GpeditRoot"));
            c.Record("frame-mode-not-editor", readout.GetValueOrDefault("Mode") != "gpedit" && !readout.ContainsKey("Page"), $"readout in console-frame mode: '{frame.HelpTextOf("GpeditRoot")}'.");
            c.Record("frame-tree-exists", frame.Find("TreeControl") is not null, "the frame host has a tree.");
        }

        using GpeditRun run = BlindKit.Open("blind-frame-host-editor");
        c.Record("editor-mode", run.Str("Mode") == "gpedit" && run.Str("Loaded") == "1", $"Mode='{run.Str("Mode")}' Loaded='{run.Str("Loaded")}'.");
        run.WriteArtifacts($"gpedit-{run.Name}.png");
        c.AssertAllPassed();
    }

    // ---- section 11: persistence -------------------------------------------------------------------------

    /// <summary>
    /// Section 11: the tree width, a column width, the Extended or Standard tab and the window placement come back after a
    /// restart on the same data root, and the root is selected again (the last node is not persisted).
    /// </summary>
    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_TreeBlind_PersistenceRestart()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var c = new GpeditChecks("blind", "persistence");
        int treeAfter;
        int columnAfter;
        Rectangle windowAfter;
        int width = Math.Min(1400, ShellRun.ScreenWidth - 200);
        using (GpeditRun first = BlindKit.Open("blind-persistence", seed: false))
        {
            first.GoTo(BlindKit.Path(BlindKit.Computer, "Basics"));
            int tree0 = first.Rect("TreeControl")?.Width ?? 0;
            int column0 = first.Rect("ConsoleListHeader_setting")?.Width ?? 0;
            BlindKit.DragBy(first, "ConsoleSplitter", 120);
            BlindKit.DragBy(first, "ConsoleListDivider_setting", 60);
            AutomationElement? standard = first.Shell.Find("ConsoleExtendedTab_Standard");
            standard?.Click();
            Thread.Sleep(500);
            first.Shell.PlaceWindow(60, 120, width, 850);
            Thread.Sleep(500);
            treeAfter = first.Rect("TreeControl")?.Width ?? 0;
            columnAfter = first.Rect("ConsoleListHeader_setting")?.Width ?? 0;
            windowAfter = first.Shell.WindowRect();
            c.Record("first.tree-width-changed", treeAfter - tree0 >= 80, $"tree width {tree0} to {treeAfter} px after a splitter drag of 120 px.");
            c.Record("first.column-width-changed", columnAfter - column0 >= 40, $"Setting column {column0} to {columnAfter} px after a divider drag of 60 px.");
            c.Record("first.tab-standard", BlindKit.IsSelected(first.Shell.Find("ConsoleExtendedTab_Standard")), "the Standard tab is selected before closing.");
            Thread.Sleep(1500);
        }

        string? file = GpeditSandbox.Create("blind-persistence").SettingsFilePath();
        c.Record("file-written", file is not null && new FileInfo(file).Length > 2, $"settings file: '{file}'.");

        using GpeditRun second = BlindKit.Open("blind-persistence", seed: false, placeWindow: false);
        Rectangle windowNow = second.Shell.WindowRect();
        c.Record(
            "window-restored",
            Math.Abs(windowNow.X - windowAfter.X) <= 3 && Math.Abs(windowNow.Y - windowAfter.Y) <= 3 && Math.Abs(windowNow.Width - windowAfter.Width) <= 3 && Math.Abs(windowNow.Height - windowAfter.Height) <= 3,
            $"window before {windowAfter}, after restart {windowNow}.");
        int treeNow = second.Rect("TreeControl")?.Width ?? 0;
        c.Record("tree-width-restored", Math.Abs(treeNow - treeAfter) <= 3, $"tree width before {treeAfter}, after restart {treeNow} px.");
        c.Record("tab-restored", BlindKit.IsSelected(second.Shell.Find("ConsoleExtendedTab_Standard")), "the Standard tab is selected after the restart.");
        c.Record("root-selected-again", second.NodePath().SequenceEqual([BlindKit.Root]), $"Node='{second.Str("Node")}' (the last node is not persisted).");
        second.GoTo(BlindKit.Path(BlindKit.Computer, "Basics"));
        int columnNow = second.Rect("ConsoleListHeader_setting")?.Width ?? 0;
        c.Record("column-width-restored", Math.Abs(columnNow - columnAfter) <= 3, $"Setting column before {columnAfter}, after restart {columnNow} px.");

        second.WriteArtifacts($"gpedit-{second.Name}.png");
        c.AssertAllPassed();
    }

    /// <summary>
    /// Section 11: column widths in the settings file are DIPs. A file that holds Setting 500 shows a 500 DIP column at any
    /// DPI (not scaled again), and after the user drags the divider the file holds the new DIP width, which a restart shows
    /// at the same size.
    /// </summary>
    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_ListBlind_PersistedColumnWidthIsDip()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var c = new GpeditChecks("blind", "persisted-width-dip");
        double dragDip;
        double afterDragDip;
        using (GpeditRun first = BlindKit.Open("blind-persisted-width-dip", sandbox => sandbox.WriteSettingsFile("{ \"ColumnWidths\": { \"setting\": 500 } }"), seed: false))
        {
            first.GoTo(BlindKit.Path(BlindKit.Computer, "Basics"));
            first.Shell.Find("ConsoleExtendedTab_Standard")?.Click();
            Thread.Sleep(500);
            double loaded = (first.Rect("ConsoleListHeader_setting")?.Width ?? 0) / first.Scale;
            c.Record("file-width-shown-in-dip", Math.Abs(loaded - 500) <= 3, $"file holds 500, header {loaded:0.#} DIP at scale {first.Scale}.");
            dragDip = 40;
            BlindKit.DragBy(first, "ConsoleListDivider_setting", (int)Math.Round(dragDip * first.Scale));
            afterDragDip = (first.Rect("ConsoleListHeader_setting")?.Width ?? 0) / first.Scale;
            c.Record("drag-kept-as-dip", Math.Abs(afterDragDip - (500 + dragDip)) <= 4, $"after a drag of {dragDip} DIP the header is {afterDragDip:0.#} DIP, expected {500 + dragDip}.");
            Thread.Sleep(1500);
        }

        string? file = GpeditSandbox.Create("blind-persisted-width-dip").SettingsFilePath();
        double saved = -1;
        if (file is not null && JsonNode.Parse(BlindKit.ReadSettings(file)) is JsonObject root)
        {
            foreach (KeyValuePair<string, JsonNode?> pair in root)
            {
                if (pair.Value is JsonObject inner && pair.Key.Contains("Column", StringComparison.OrdinalIgnoreCase) && inner["setting"] is JsonNode value)
                {
                    saved = value.GetValue<double>();
                }
            }
        }

        c.Record("file-holds-dip", Math.Abs(saved - afterDragDip) <= 4, $"settings file holds Setting {saved}, header was {afterDragDip:0.#} DIP.");

        using GpeditRun second = BlindKit.Open("blind-persisted-width-dip", seed: false, placeWindow: true);
        second.GoTo(BlindKit.Path(BlindKit.Computer, "Basics"));
        second.Shell.Find("ConsoleExtendedTab_Standard")?.Click();
        Thread.Sleep(500);
        double restored = (second.Rect("ConsoleListHeader_setting")?.Width ?? 0) / second.Scale;
        c.Record("restart-same-dip", Math.Abs(restored - afterDragDip) <= 3, $"before {afterDragDip:0.#} DIP, after restart {restored:0.#} DIP.");
        c.AssertAllPassed();
    }

    /// <summary>
    /// Section 11 and 15: a settings file with absurd numbers, unknown names, garbage, a truncated file or a huge file never
    /// stops the editor from opening on screen with usable panes. The variants are made from the file the product wrote.
    /// </summary>
    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_TreeBlind_BadSettingsFiles()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var c = new GpeditChecks("blind", "bad-settings");
        using (GpeditRun first = BlindKit.Open("blind-bad-settings", seed: false))
        {
            BlindKit.DragBy(first, "ConsoleSplitter", 40);
            Thread.Sleep(1200);
        }

        var probe = GpeditSandbox.Create("blind-bad-settings");
        string? path = probe.SettingsFilePath();
        c.Record("baseline-file", path is not null, $"settings file '{path}'.");
        if (path is null)
        {
            c.AssertAllPassed();
            return;
        }

        string baseline = BlindKit.ReadSettings(path);
        JsonNode? parsed = JsonNode.Parse(baseline);
        int numbers = BlindKit.Mutate(parsed, new BlindKit.Counter(), v => v).Count;
        c.Record("baseline-has-numbers", numbers > 0, $"the product's settings file holds {numbers} number(s).");

        (string Label, string Text)[] variants =
        [
            ("zero", BlindKit.Rewrite(baseline, n => JsonValue.Create(0))),
            ("negative", BlindKit.Rewrite(baseline, n => JsonValue.Create(-5))),
            ("huge", BlindKit.Rewrite(baseline, n => JsonValue.Create(1000000000))),
            ("offscreen", BlindKit.Rewrite(baseline, n => JsonValue.Create(50000))),
            ("max-double", BlindKit.Rewrite(baseline, n => JsonValue.Create(1.7e308))),
            ("unknown-names", BlindKit.RewriteStrings(baseline, "bogus-value")),
            ("garbage", "{{{ this is not json \u0001\u0002"),
            ("truncated", baseline[..(baseline.Length / 2)]),
            ("oversize", "{\"pad\":\"" + new string('x', 8_000_000) + "\"}"),
        ];

        int screenWidth = ShellRun.ScreenWidth;
        int screenHeight = GetSystemMetrics(1);
        foreach ((string label, string text) in variants)
        {
            probe.WriteSettingsFile(text);
            try
            {
                using GpeditRun run = BlindKit.Open("blind-bad-settings", seed: false, placeWindow: false);
                Rectangle window = run.Shell.WindowRect();
                bool onScreen = window.Right > 50 && window.Left < screenWidth - 50 && window.Bottom > 50 && window.Top < screenHeight - 50 && window.Width >= 300 && window.Height >= 200;
                c.Record($"{label}.window-on-screen", onScreen, $"window {window} on a {screenWidth} x {screenHeight} screen.");
                int tree = run.Rect("TreeControl")?.Width ?? 0;
                c.Record($"{label}.tree-width-sane", tree >= 40 && tree <= window.Width * 0.8, $"tree width {tree} px in a window of {window.Width} px.");
                run.GoTo(BlindKit.Path(BlindKit.Computer, "Basics"));
                int column = run.Rect("ConsoleListHeader_setting")?.Width ?? 0;
                c.Record($"{label}.column-width-sane", column >= 20 && column <= window.Width, $"Setting column {column} px.");
                c.Record($"{label}.root-and-alive", run.Int("Rows") == 12 && run.Str("Loaded") == "1", $"Rows={run.Str("Rows")} Loaded={run.Str("Loaded")}.");
            }
            catch (Exception ex)
            {
                c.Record($"{label}.starts", false, $"the editor did not start or navigate: {ex.GetType().Name}: {ex.Message}");
            }

            if (label is "garbage" or "truncated")
            {
                string? folder = Path.GetDirectoryName(path);
                bool kept = folder is not null && Directory.GetFiles(folder).Any(f => !string.Equals(f, path, StringComparison.OrdinalIgnoreCase) && BlindKit.ReadSettings(f).Contains(label == "garbage" ? "this is not json" : "{", StringComparison.Ordinal) && new FileInfo(f).Length > 0);
                c.Record($"{label}.bad-file-kept", kept, $"a copy of the bad file beside '{path}' (section 11).");
            }
        }

        c.AssertAllPassed();
    }

    // ---- sections 4, 6, 9: list contents -----------------------------------------------------------------

    /// <summary>
    /// Evidence list of the doc intro (oracle facts): the default column widths in DIP: Name 180 on a
    /// Windows Settings list, Setting 350 and State
    /// 100 on an Administrative Templates category. The header rectangles are physical pixels, divided by the window scale.
    /// </summary>
    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_ListBlind_DefaultColumnWidthsInDip()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var c = new GpeditChecks("blind", "column-widths");
        using GpeditRun run = BlindKit.Open("blind-column-widths");
        run.GoTo(BlindKit.Root, BlindKit.Computer, "Windows Settings");
        double? nameWidth = run.Rect("ConsoleListHeader_name")?.Width / run.Scale;
        c.Record("name-width", nameWidth is not null && Math.Abs(nameWidth.Value - 180) <= 4, $"Name column {nameWidth} DIP at scale {run.Scale}, expected 180 (oracle facts).");
        run.GoTo(BlindKit.Path(BlindKit.Computer, "Basics"));
        double? settingWidth = run.Rect("ConsoleListHeader_setting")?.Width / run.Scale;
        double? stateWidth = run.Rect("ConsoleListHeader_state")?.Width / run.Scale;
        double? commentWidth = run.Rect("ConsoleListHeader_comment")?.Width / run.Scale;
        c.Record("setting-width", settingWidth is not null && Math.Abs(settingWidth.Value - 350) <= 4, $"Setting {settingWidth} DIP at scale {run.Scale}, expected 350.");
        c.Record("state-width", stateWidth is not null && Math.Abs(stateWidth.Value - 100) <= 4, $"State {stateWidth} DIP at scale {run.Scale}, expected 100.");
        c.Record("comment-width", commentWidth is not null && commentWidth.Value > 0, $"Comment {commentWidth} DIP, the rest.");
        run.Measure("nameWidthPx", run.Rect("ConsoleListHeader_name")?.Width);
        run.WriteArtifacts($"gpedit-{run.Name}.png");
        c.AssertAllPassed();
    }

    /// <summary>
    /// Sections 4 and 6 and 9: folder rows come first with a plain name and no State or Comment, the policy rows carry the
    /// word setting, the columns are Setting, State and Comment at their widths, and the status counts policy rows.
    /// </summary>
    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_ListBlind_CategoryRows()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var c = new GpeditChecks("blind", "category");
        using GpeditRun run = BlindKit.Open("blind-category");

        // The computer BWC Oracle category: subcategories only.
        run.GoTo(BlindKit.Path(BlindKit.Computer));
        List<GpeditRow> oracle = run.Rows();
        c.Record("oracle-computer-folders", oracle.Select(r => r.Name).SequenceEqual(BlindKit.OracleComputerFolders), $"rows: {string.Join("|", oracle.Select(r => r.Name))}.");
        c.Record("folder-rows-no-word-setting", oracle.All(r => !r.Name.EndsWith(" setting", StringComparison.Ordinal)), "a folder row has no 'setting' in its name (section 6).");
        c.Record("folder-rows-no-state-comment", oracle.All(r => r.Cells.Skip(1).All(s => s.Length == 0)), $"folder cells: {string.Join(" / ", oracle.Select(r => string.Join(",", r.Cells)))}.");
        c.Record("oracle-computer-status", run.Shell.StatusText() == "0 setting(s)", $"status '{run.Shell.StatusText()}' for a category with only subcategories (section 15: 0 setting(s)).");

        // The user BWC Oracle category: three folders and one policy; folders first.
        run.GoTo(BlindKit.Path(BlindKit.User));
        List<string> userNames = run.Rows().Select(r => r.Name).ToList();
        c.Record("oracle-user-rows", userNames.SequenceEqual(["Basics", "Elements", "User only", "Top level user flag setting"]), $"rows: {string.Join("|", userNames)} (folders before the one policy).");
        c.Record("oracle-user-status", run.Shell.StatusText() == "1 setting(s)", $"status '{run.Shell.StatusText()}'; the format has no singular form.");

        // Sub-folders of Elements on both sides.
        run.GoTo(BlindKit.Path(BlindKit.Computer, "Elements"));
        c.Record("elements-computer", run.Rows().Select(r => r.Name).SequenceEqual(["Booleans", "Choices", "Lists", "Numbers", "Texts"]), $"rows: {string.Join("|", run.Rows().Select(r => r.Name))}.");
        run.GoTo(BlindKit.Path(BlindKit.User, "Elements"));
        c.Record("elements-user", run.Rows().Select(r => r.Name).SequenceEqual(["Booleans", "Choices", "Lists", "Numbers", "Texts"]), $"rows: {string.Join("|", run.Rows().Select(r => r.Name))}.");

        // The user only branch: the first level has no policy of its own.
        run.GoTo(BlindKit.Path(BlindKit.User, "User only"));
        c.Record("user-only-rows", run.Rows().Select(r => r.Name).SequenceEqual(["User nested"]), $"rows: {string.Join("|", run.Rows().Select(r => r.Name))}.");
        c.Record("user-only-status", run.Shell.StatusText() == "0 setting(s)", $"status '{run.Shell.StatusText()}'.");
        run.GoTo(BlindKit.Path(BlindKit.User, "User only", "User nested"));
        c.Record("user-nested-rows", run.Rows().Select(r => r.Name).SequenceEqual(["Nested user policy, deep level setting"]), $"rows: {string.Join("|", run.Rows().Select(r => r.Name))}.");

        // The machine only branch, three levels.
        run.GoTo(BlindKit.Path(BlindKit.Computer, "Machine only", "Machine nested", "Machine deep"));
        c.Record("machine-deep-rows", run.Rows().Select(r => r.Name).SequenceEqual(["Nested machine policy, deep level setting"]), $"rows: {string.Join("|", run.Rows().Select(r => r.Name))}.");

        // Basics on the computer side: names, order, columns.
        run.GoTo(BlindKit.Path(BlindKit.Computer, "Basics"));
        List<GpeditRow> basics = run.Rows();
        c.Record("basics-computer-rows", basics.Select(r => r.Name).SequenceEqual(BlindKit.BasicsComputer.Select(n => n + " setting")), $"rows: {string.Join("|", basics.Select(r => r.Name))}.");
        c.Record("basics-computer-status", run.Shell.StatusText() == "12 setting(s)", $"status '{run.Shell.StatusText()}'.");
        c.Record("basics-cells-three-columns", basics.All(r => r.Cells.Length == 3), $"cells per row: {string.Join(",", basics.Select(r => r.Cells.Length).Distinct())}.");
        c.Record("basics-first-cell-is-name", basics.All(r => r.Cells[0] == r.Name || r.Cells[0] + " setting" == r.Name), $"first cells: {string.Join("|", basics.Take(3).Select(r => r.Cells[0]))}.");
        string[] headers = run.HeaderNames("setting", "state", "comment");
        c.Record("headers", headers.SequenceEqual(["Setting", "State", "Comment"]), $"headers: {string.Join("|", headers)}.");
        // Basics on the user side.
        run.GoTo(BlindKit.Path(BlindKit.User, "Basics"));
        c.Record("basics-user-rows", run.Rows().Select(r => r.Name).SequenceEqual(BlindKit.BasicsUser.Select(n => n + " setting")), $"rows: {string.Join("|", run.Rows().Select(r => r.Name))}.");
        c.Record("basics-user-status", run.Shell.StatusText() == "4 setting(s)", $"status '{run.Shell.StatusText()}'.");

        run.Measure("basicsComputerRows", basics.Count);
        run.WriteArtifacts($"gpedit-{run.Name}.png");
        c.AssertAllPassed();
    }

    /// <summary>
    /// Section 6: State and Comment of the seeded policy files, the class Both policy read per side, no row object kept
    /// from another node, Comment Yes only for the side that has the note.
    /// </summary>
    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_ListBlind_States()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var c = new GpeditChecks("blind", "states");
        using GpeditRun run = BlindKit.Open("blind-states");

        run.GoTo(BlindKit.Path(BlindKit.Computer, "Basics"));
        Dictionary<string, (string State, string Comment)> computer = BlindKit.StateMap(run);
        c.Record("computer.machine-flag", computer.GetValueOrDefault("Class machine flag") == ("Enabled", "Yes"), $"Class machine flag = {BlindKit.Show(computer, "Class machine flag")}.");
        c.Record("computer.both-flag", computer.GetValueOrDefault("Class both flag") == ("Enabled", "Yes"), $"Class both flag = {BlindKit.Show(computer, "Class both flag")}.");
        c.Record("computer.decimal-disabled", computer.GetValueOrDefault("Decimal enabled and disabled values") == ("Disabled", "No"), $"Decimal ... = {BlindKit.Show(computer, "Decimal enabled and disabled values")} (value 9 is the disabled value).");
        string[] untouched = ["Enabled value only", "Disabled value only", "Enabled list only", "Key outside the policy roots", "Class both with an element", "Element key differs from policy key"];
        c.Record("computer.others-not-configured", untouched.All(n => computer.GetValueOrDefault(n) == ("Not configured", "No")), $"others: {string.Join("|", untouched.Select(n => n + "=" + BlindKit.Show(computer, n)))}.");

        run.GoTo(BlindKit.Path(BlindKit.User, "Basics"));
        Dictionary<string, (string State, string Comment)> user = BlindKit.StateMap(run);
        c.Record("user.user-flag", user.GetValueOrDefault("Class user flag") == ("Enabled", "No"), $"Class user flag = {BlindKit.Show(user, "Class user flag")}.");
        c.Record("user.both-flag-per-side", user.GetValueOrDefault("Class both flag") == ("Disabled", "No"), $"Class both flag on the user side = {BlindKit.Show(user, "Class both flag")}; the computer side said Enabled (section 6: the document of the node's side, and the note is of the computer side only).");
        c.Record("user.string-not-configured", user.GetValueOrDefault("String enabled and disabled values") == ("Not configured", "No"), $"String ... = {BlindKit.Show(user, "String enabled and disabled values")}.");

        // Back to the computer node: rows are rebuilt from the model, no state leaks from the user node.
        run.GoTo(BlindKit.Path(BlindKit.Computer, "Basics"));
        Dictionary<string, (string State, string Comment)> computerAgain = BlindKit.StateMap(run);
        c.Record("computer-again", computerAgain.GetValueOrDefault("Class both flag") == ("Enabled", "Yes") && computerAgain.Count == 12, $"Class both flag = {BlindKit.Show(computerAgain, "Class both flag")}, rows {computerAgain.Count}.");

        // Other categories have only Not configured.
        run.GoTo(BlindKit.Path(BlindKit.Computer, "Shared keys"));
        Dictionary<string, (string State, string Comment)> shared = BlindKit.StateMap(run);
        c.Record("shared-not-configured", shared.Count == 6 && shared.Values.All(v => v == ("Not configured", "No")), $"Shared keys: {string.Join("|", shared.Select(kv => kv.Key + "=" + kv.Value.State))}.");

        run.WriteArtifacts($"gpedit-{run.Name}.png");
        c.AssertAllPassed();
    }

    /// <summary>
    /// Section 6 and 15: All Settings lists every policy of the side exactly once (59 and 13 in the fixture), policies only,
    /// sorted by display name, with the seeded states, and the status text counts them. The captures F/AllSettings_Computer and
    /// F/AllSettings_User do not track the rows ("depends on the machine's own templates"), so the order of this node is not
    /// a capture fact; ADMX-022 says by display name and no capture contradicts it. The headers of both captures list four
    /// columns, the fourth being the path.
    /// </summary>
    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_ListBlind_AllSettings()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var c = new GpeditChecks("blind", "all-settings");
        using GpeditRun run = BlindKit.Open("blind-all-settings");

        run.GoTo(BlindKit.Root, BlindKit.User, BlindKit.Templates, "All Settings");
        _ = ShellRun.WaitUntil(() => run.Int("Rows") == 13 && run.Rows().Count == 13, TimeSpan.FromSeconds(10));
        List<GpeditRow> user = run.Rows();
        c.Record("user.count", run.Int("ShownPolicies") == 13 && run.Int("ShownFolders") == 0 && run.Int("Rows") == 13, $"Rows={run.Str("Rows")} ShownPolicies={run.Str("ShownPolicies")} ShownFolders={run.Str("ShownFolders")}.");
        c.Record("user.status", run.Shell.StatusText() == "13 setting(s)", $"status '{run.Shell.StatusText()}'.");
        c.Record("user.names", user.Select(r => r.Name).SequenceEqual(BlindKit.AllUser.Select(n => n + " setting")), $"rows: {string.Join("|", user.Select(r => r.Name))}.");
        c.Record("user.both-once", user.Count(r => r.Name == "Class both flag setting") == 1, "a class Both policy shows once on the side.");
        c.Record("user.no-machine-policy", user.All(r => r.Name != "Class machine flag setting"), "a class Machine policy never shows on the user side.");
        Dictionary<string, (string State, string Comment)> userStates = BlindKit.StateMap(run);
        c.Record("user.states", userStates.GetValueOrDefault("Class user flag") == ("Enabled", "No") && userStates.GetValueOrDefault("Class both flag") == ("Disabled", "No"), $"Class user flag = {BlindKit.Show(userStates, "Class user flag")}, Class both flag = {BlindKit.Show(userStates, "Class both flag")}.");
        c.Record("user.four-columns", user.All(r => r.Cells.Length == 4), $"Setting, State, Comment and Path: F/AllSettings_User lists four headers (VIEW-009); cells per row {string.Join(",", user.Select(r => r.Cells.Length).Distinct())}.");
        c.Record("user.path-header", run.HeaderNames("path")[0].Length > 0, "the fourth header (id path) exists at All Settings.");

        run.GoTo(BlindKit.Root, BlindKit.Computer, BlindKit.Templates, "All Settings");
        _ = ShellRun.WaitUntil(() => run.Int("Rows") == 59 && run.Rows().Count > 20, TimeSpan.FromSeconds(10));
        List<GpeditRow> computer = run.Rows();
        c.Record("computer.count", run.Int("ShownPolicies") == 59 && run.Int("ShownFolders") == 0 && run.Int("Rows") == 59, $"Rows={run.Str("Rows")} ShownPolicies={run.Str("ShownPolicies")} ShownFolders={run.Str("ShownFolders")}.");
        c.Record("computer.status", run.Shell.StatusText() == "59 setting(s)", $"status '{run.Shell.StatusText()}'.");
        c.Record("computer.no-user-policy", computer.All(r => r.Name != "Class user flag setting" && r.Name != "Top level user flag setting"), "a class User policy never shows on the computer side.");
        string[] names = computer.Select(r => r.Name[..^" setting".Length]).ToArray();
        int disorder = 0;
        for (int i = 1; i < names.Length; i++)
        {
            if (string.Compare(names[i - 1], names[i], CultureInfo.CurrentCulture, CompareOptions.IgnoreCase) > 0)
            {
                disorder++;
            }
        }

        c.Record("computer.sorted", disorder == 0, $"{disorder} adjacent pair(s) out of order among {names.Length} realized rows: {string.Join("|", names)}.");
        Dictionary<string, (string State, string Comment)> states = BlindKit.StateMap(run);
        c.Record("computer.states", states.GetValueOrDefault("Class both flag") == ("Enabled", "Yes"), $"Class both flag = {BlindKit.Show(states, "Class both flag")}.");

        run.WriteArtifacts($"gpedit-{run.Name}.png");
        c.AssertAllPassed();
    }

    /// <summary>
    /// Sections 4 and 6 and 15: a node with no sort mark lists its policies in the order of the policy ids, not of the display
    /// names (capture F/Sort_Columns op 5, F/Nodes_Computer op 28). The ids of the Sort cases node are Pol_Sort_Digit1,
    /// Pol_Sort_Digit10, Pol_Sort_Digit2, Pol_Sort_LeadingSymbol, Pol_Sort_LowerAlpha, Pol_Sort_NonAscii, Pol_Sort_UpperAlpha
    /// and Pol_Sort_Zulu. The sort by display name comes from a header click (Gpedit_Sort_Columns_ThreeClicksEach).
    /// </summary>
    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_ListBlind_SortCasesOrder()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var c = new GpeditChecks("blind", "sort-cases");
        using GpeditRun run = BlindKit.Open("blind-sort-cases");
        run.GoTo(BlindKit.Path(BlindKit.Computer, "Sort cases"));
        string[] names = run.Rows().Select(r => r.Name).ToArray();
        string[] classic =
        [
            "Policy 1 setting", "Policy 10 setting", "Policy 2 setting", "#Symbol policy setting",
            "alpha policy setting", "Éclair policy setting", "Alpha Policy setting", "Zulu sort case setting",
        ];
        c.Record("count", names.Length == 8 && run.Shell.StatusText() == "8 setting(s)", $"rows {names.Length}, status '{run.Shell.StatusText()}'.");
        c.Record("default-order", names.SequenceEqual(classic), $"rows {string.Join("|", names)}; classic (F/Sort_Columns op 5): {string.Join("|", classic)}.");
        c.Record("case-tie-source-order", names.Length > 5 && names[4] == "alpha policy setting" && names[6] == "Alpha Policy setting", "alpha policy comes before Alpha Policy (their ids order them, F/Sort_Columns op 5).");
        run.WriteArtifacts($"gpedit-{run.Name}.png");
        c.AssertAllPassed();
    }

    /// <summary>
    /// Section 15: the order of runs of digits. Answered by the capture F/Sort_Columns (SORT-1): classic compares digits as
    /// text, so Policy 10 comes before Policy 2 in the Setting column (op 6, the first click on Setting: Policy 1, Policy 10,
    /// Policy 2). The default order, by policy id, puts the three in the same relative order (op 5).
    /// </summary>
    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_ListBlind_SortCasesDigitRuns()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var c = new GpeditChecks("blind", "sort-digits");
        using GpeditRun run = BlindKit.Open("blind-sort-digits");
        run.GoTo(BlindKit.Path(BlindKit.Computer, "Sort cases"));
        string[] names = run.Rows().Select(r => r.Name).ToArray();
        string[] digits = names.Where(n => n.StartsWith("Policy ", StringComparison.Ordinal)).ToArray();
        c.Record("digit-runs", digits.SequenceEqual(["Policy 1 setting", "Policy 10 setting", "Policy 2 setting"]), $"digit rows: {string.Join("|", digits)}; classic compares digit runs as text (F/Sort_Columns).");
        c.AssertAllPassed();
    }

    /// <summary>
    /// Section 15: a literal ampersand shows as written in the tree, the folder row, the policy row, the
    /// description title and the
    /// readout, and the category count is right.
    /// </summary>
    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_ListBlind_LiteralAmpersand()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var c = new GpeditChecks("blind", "ampersand");
        using GpeditRun run = BlindKit.Open("blind-ampersand");
        run.GoTo(BlindKit.Path(BlindKit.Computer));
        c.Record("folder-row", run.Rows().Any(r => r.Name == "Tools & Utilities"), $"rows: {string.Join("|", run.Rows().Select(r => r.Name))}.");
        run.GoTo(BlindKit.Path(BlindKit.Computer, "Tools & Utilities"));
        c.Record("tree-row", BlindKit.TreeRows(run).Contains("Tools & Utilities"), $"tree rows: {string.Join("|", BlindKit.TreeRows(run))}.");
        c.Record("readout-node", run.NodePath().LastOrDefault() == "Tools & Utilities", $"Node='{run.Str("Node")}'.");
        c.Record("description-title", run.DescriptionTitle == "Tools & Utilities", $"title '{run.DescriptionTitle}'.");
        string[] names = run.Rows().Select(r => r.Name).ToArray();
        c.Record("policy-rows", names.OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(["Label with a double ampersand setting", "Labels with single ampersands setting", "Research & Development flag setting"]), $"rows: {string.Join("|", names)}.");
        c.Record("status", run.Shell.StatusText() == "3 setting(s)", $"status '{run.Shell.StatusText()}'.");
        run.SelectRow("Research & Development flag setting");
        c.Record("policy-title", run.DescriptionTitle == "Research & Development flag", $"title '{run.DescriptionTitle}'.");
        c.Record("policy-explain", BlindKit.Norm(run.DescriptionText("description")).StartsWith("The display name has a literal ampersand", StringComparison.Ordinal), $"description '{run.DescriptionText("description")}'.");
        run.WriteArtifacts($"gpedit-{run.Name}.png");
        c.AssertAllPassed();
    }

    /// <summary>
    /// Section 4, 5 and 14: a double click on a folder row selects that child and leaves the parent expanded in the tree; the
    /// toolbar Up One Level returns to the parent and the list shows the parent's rows.
    /// </summary>
    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_ListBlind_FolderRowNavigation()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var c = new GpeditChecks("blind", "folder-navigation");
        using GpeditRun run = BlindKit.Open("blind-folder-navigation");
        run.GoTo(BlindKit.Path(BlindKit.Computer, "Elements"));
        c.Record("child-selected", run.NodePath().SequenceEqual(BlindKit.Path(BlindKit.Computer, "Elements")), $"Node='{run.Str("Node")}'.");
        List<string> tree = BlindKit.TreeRows(run);
        c.Record("parent-expanded", tree.Contains(BlindKit.Oracle) && tree.Contains("Elements") && tree.Contains("Basics") && tree.Contains("Explain text"), $"tree rows: {string.Join("|", tree)}.");

        AutomationElement? up = run.Shell.Find(ShellRun.ButtonId("UpOneLevel"));
        up?.Patterns.Invoke.PatternOrDefault?.Invoke();
        bool parent = ShellRun.WaitUntil(() => run.NodePath().SequenceEqual(BlindKit.Path(BlindKit.Computer)), TimeSpan.FromSeconds(8));
        c.Record("up-one-level", parent, $"Node='{run.Str("Node")}'.");
        _ = ShellRun.WaitUntil(() => run.Rows().Count == 7, TimeSpan.FromSeconds(8));
        c.Record("parent-rows", run.Rows().Select(r => r.Name).SequenceEqual(BlindKit.OracleComputerFolders), $"rows: {string.Join("|", run.Rows().Select(r => r.Name))}.");
        run.WriteArtifacts($"gpedit-{run.Name}.png");
        c.AssertAllPassed();
    }

    /// <summary>Section 9: the status text of every kind of node.</summary>
    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_ListBlind_StatusBar()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var c = new GpeditChecks("blind", "status");
        using GpeditRun run = BlindKit.Open("blind-status");

        void Expect(string id, string text, params string[] path)
        {
            run.GoTo(path);
            _ = ShellRun.WaitUntil(() => run.Shell.StatusText() == text, TimeSpan.FromSeconds(5));
            c.Record(id, run.Shell.StatusText() == text, $"node '{string.Join(" > ", path)}': status '{run.Shell.StatusText()}', expected '{text}'.");
        }

        Expect("root", string.Empty, BlindKit.Root);
        Expect("computer", string.Empty, BlindKit.Root, BlindKit.Computer);
        Expect("software", string.Empty, BlindKit.Root, BlindKit.Computer, "Software Settings");
        Expect("windows-settings", string.Empty, BlindKit.Root, BlindKit.Computer, "Windows Settings");
        Expect("windows-child", string.Empty, BlindKit.Root, BlindKit.Computer, "Windows Settings", "Security Settings");
        Expect("templates", string.Empty, BlindKit.Root, BlindKit.Computer, BlindKit.Templates);
        Expect("basics", "12 setting(s)", BlindKit.Path(BlindKit.Computer, "Basics"));
        Expect("windows-settings-after-category", string.Empty, BlindKit.Root, BlindKit.Computer, "Windows Settings");
        Expect("oracle-computer", "0 setting(s)", BlindKit.Path(BlindKit.Computer));
        Expect("all-settings-computer", "59 setting(s)", BlindKit.Root, BlindKit.Computer, BlindKit.Templates, "All Settings");
        Expect("oracle-user", "1 setting(s)", BlindKit.Path(BlindKit.User));
        Expect("user-only", "0 setting(s)", BlindKit.Path(BlindKit.User, "User only"));
        Expect("user-nested", "1 setting(s)", BlindKit.Path(BlindKit.User, "User only", "User nested"));
        Expect("all-settings-user", "13 setting(s)", BlindKit.Root, BlindKit.User, BlindKit.Templates, "All Settings");
        Expect("back-to-root", string.Empty, BlindKit.Root);
        run.WriteArtifacts($"gpedit-{run.Name}.png");
        c.AssertAllPassed();
    }

    // ---- section 8: the Extended pane ---------------------------------------------------------------------

    /// <summary>
    /// Section 8: the description pane for a policy row (title, link, Requirements, Description), a policy with no help text,
    /// a folder row, a category, other nodes, no stale text after the node changed, and the edit request counter.
    /// </summary>
    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_ListBlind_ExtendedPane()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var c = new GpeditChecks("blind", "extended-pane");
        using GpeditRun run = BlindKit.Open("blind-extended-pane");

        // A category with nothing selected in the list: the category's own title and explain text, no link.
        run.GoTo(BlindKit.Path(BlindKit.Computer, "Basics"));
        c.Record("category.title", run.DescriptionTitle == "Basics", $"title '{run.DescriptionTitle}'.");
        c.Record("category.text", BlindKit.DescriptionBody(run) == "Class, value forms, value lists, and key placement.", $"text '{BlindKit.DescriptionBody(run)}'.");
        c.Record("category.no-link", run.EditLink is null, "no Edit link for a category (section 8).");

        // A policy row: title, link, requirements, description.
        run.SelectRow("Class machine flag setting");
        c.Record("policy.title", run.DescriptionTitle == "Class machine flag", $"title '{run.DescriptionTitle}'.");
        c.Record("policy.link", run.EditLink is not null && ShellRun.SafeName(run.EditLink) == "Edit policy setting", $"link '{ShellRun.SafeName(run.EditLink)}'.");
        c.Record("policy.requirements", run.DescriptionText("requirements") == "BWC oracle fixture: any system", $"requirements '{run.DescriptionText("requirements")}'.");
        c.Record("policy.description", run.DescriptionText("description") == "A machine policy with a value name and no explicit values. Enabled writes 1 and Disabled writes 0.", $"description '{run.DescriptionText("description")}'.");
        c.Record("policy.labels", ShellRun.SafeName(run.Shell.Find("ConsoleDescriptionLabel_requirements")) == "Requirements" && ShellRun.SafeName(run.Shell.Find("ConsoleDescriptionLabel_description")) == "Description", "section labels Requirements and Description.");

        // Another supported-on text and a policy of class Both.
        run.SelectRow("Class both flag setting");
        c.Record("policy2.requirements", run.DescriptionText("requirements") == "BWC oracle fixture: older systems only", $"requirements '{run.DescriptionText("requirements")}'.");
        c.Record("policy2.title-changed", run.DescriptionTitle == "Class both flag", $"title '{run.DescriptionTitle}'.");
        run.SelectRow("Class both with an element setting");
        c.Record("policy3.requirements", run.DescriptionText("requirements") == "BWC oracle fixture: defined in the same file as the policy", $"requirements '{run.DescriptionText("requirements")}'.");

        // Policies with no help text and an empty help text: the pane still has a title and a link.
        run.GoTo(BlindKit.Path(BlindKit.Computer, "Explain text"));
        run.SelectRow("Explain text attribute omitted setting");
        c.Record("omitted.pane", run.DescriptionTitle == "Explain text attribute omitted" && run.EditLink is not null && BlindKit.Norm(run.DescriptionText("description")).Length == 0, $"title '{run.DescriptionTitle}', link {(run.EditLink is null ? "absent" : "present")}, description '{run.DescriptionText("description")}'.");
        run.SelectRow("Explain text that is an empty string setting");
        c.Record("empty-string.pane", run.DescriptionTitle == "Explain text that is an empty string" && run.EditLink is not null && BlindKit.Norm(run.DescriptionText("description")).Length == 0, $"title '{run.DescriptionTitle}', description '{run.DescriptionText("description")}'.");
        run.SelectRow("Explain text with several paragraphs setting");
        string expectedExplain = BlindKit.Norm(BlindKit.FixtureString("Pol_Explain_MultiParagraph_Help"));
        c.Record("multi-paragraph", BlindKit.Norm(run.DescriptionText("description")) == expectedExplain && expectedExplain.Length > 40, $"description '{BlindKit.Norm(run.DescriptionText("description"))}', expected '{expectedExplain}'.");

        // A folder row selected in the list: its title and explain text, no link.
        run.GoTo(BlindKit.Path(BlindKit.Computer));
        run.SelectRow("Basics");
        c.Record("folder.title", run.DescriptionTitle == "Basics", $"title '{run.DescriptionTitle}'.");
        c.Record("folder.text", BlindKit.DescriptionBody(run) == "Class, value forms, value lists, and key placement.", $"text '{BlindKit.DescriptionBody(run)}'.");
        c.Record("folder.no-link", run.EditLink is null, "no Edit link for a folder row.");
        run.SelectRow("Elements");
        c.Record("folder2.title-and-no-stale-text", run.DescriptionTitle == "Elements" && !BlindKit.DescriptionBody(run).Contains("Class, value forms", StringComparison.Ordinal), $"title '{run.DescriptionTitle}', text '{BlindKit.DescriptionBody(run)}'.");

        // The category node: title and explain text of the category (the base file's text), no link. The folder row
        // selected above stays selected when the node does not change, so the test leaves the node first.
        run.GoTo(BlindKit.Root);
        run.GoTo(BlindKit.Path(BlindKit.Computer));
        c.Record("oracle.title", run.DescriptionTitle == BlindKit.Oracle, $"title '{run.DescriptionTitle}'.");
        string oracleExplain = BlindKit.Norm(BlindKit.BaseFixtureString("BwcOracle_Help"));
        c.Record("oracle.text", BlindKit.Norm(BlindKit.DescriptionBody(run)) == oracleExplain, $"text '{BlindKit.DescriptionBody(run)}', expected '{oracleExplain}'.");

        // Navigation nodes: some hint text and the node's own title.
        foreach (string[] node in new[] { new[] { BlindKit.Root }, new[] { BlindKit.Root, BlindKit.User }, new[] { BlindKit.Root, BlindKit.Computer, BlindKit.Templates }, new[] { BlindKit.Root, BlindKit.Computer, BlindKit.Templates, "All Settings" } })
        {
            run.GoTo(node);
            string title = run.DescriptionTitle;
            c.Record($"node.{string.Join("-", node.Skip(1))}.pane", title == node[^1] && BlindKit.DescriptionBody(run).Length > 0 && run.EditLink is null, $"node '{string.Join(" > ", node)}': title '{title}', text '{BlindKit.DescriptionBody(run)}'.");
        }

        // The edit link, Enter and a double click raise the edit request.
        run.GoTo(BlindKit.Path(BlindKit.Computer, "Basics"));
        run.SelectRow("Class machine flag setting");
        int before = run.Int("Edits");
        AutomationElement? link = run.EditLink;
        link?.Patterns.Invoke.PatternOrDefault?.Invoke();
        bool linkRaised = ShellRun.WaitUntil(() => run.Int("Edits") == before + 1, TimeSpan.FromSeconds(5));
        c.Record("edit.link", linkRaised && run.Str("LastEdit") == "BWC.Policies.Oracle:Pol_Class_Machine", $"Edits {before} to {run.Int("Edits")}, LastEdit '{run.Str("LastEdit")}'.");

        run.SelectRow("Class both flag setting");
        run.Shell.Tap(0x0D);
        bool enterRaised = ShellRun.WaitUntil(() => run.Int("Edits") == before + 2, TimeSpan.FromSeconds(5));
        c.Record("edit.enter", enterRaised && run.Str("LastEdit") == "BWC.Policies.Oracle:Pol_Class_Both", $"Edits {run.Int("Edits")}, LastEdit '{run.Str("LastEdit")}'.");

        GpeditRow? row = run.Rows().FirstOrDefault(r => r.Name == "Decimal enabled and disabled values setting");
        if (row is not null)
        {
            _ = ControlTestSupport.EnsureForeground(run.Shell.Session);
            run.Shell.Find("ConsoleListRow_" + row.Index.ToString(CultureInfo.InvariantCulture))?.DoubleClick();
        }

        bool doubleRaised = ShellRun.WaitUntil(() => run.Int("Edits") == before + 3, TimeSpan.FromSeconds(5));
        c.Record("edit.double-click", doubleRaised && run.Str("LastEdit") == "BWC.Policies.Oracle:Pol_Value_Decimal", $"Edits {run.Int("Edits")}, LastEdit '{run.Str("LastEdit")}'.");

        // A folder row never raises the request: opening it selects the folder.
        int count = run.Int("Edits");
        run.GoTo(BlindKit.Path(BlindKit.Computer));
        run.GoTo(BlindKit.Path(BlindKit.Computer, "Basics"));
        c.Record("edit.folder-no-request", run.Int("Edits") == count, $"Edits stayed {run.Int("Edits")} after opening folder rows.");

        run.WriteArtifacts($"gpedit-{run.Name}.png");
        c.AssertAllPassed();
    }

    /// <summary>
    /// Section 8: the wording of the fallback hint. Classic shows "Select an item to view its description." (probe.json, 247
    /// times) on the root and on a folder node with no explain text, in the tree and as a selected folder row.
    /// </summary>
    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_ListBlind_ExtendedPaneFallbackWording()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        const string classic = "Select an item to view its description.";
        var c = new GpeditChecks("blind", "fallback-wording");
        using GpeditRun run = BlindKit.Open("blind-fallback-wording");
        c.Record("root", BlindKit.DescriptionBody(run) == classic, $"root text '{BlindKit.DescriptionBody(run)}'.");

        // A folder node with no explain text: the Elements category.
        run.GoTo(BlindKit.Path(BlindKit.Computer, "Elements"));
        c.Record("folder-node", run.DescriptionTitle == "Elements" && BlindKit.DescriptionBody(run) == classic, $"title '{run.DescriptionTitle}', text '{BlindKit.DescriptionBody(run)}'.");

        // The same folder as a selected row of its parent category.
        run.GoTo(BlindKit.Path(BlindKit.Computer));
        run.SelectRow("Elements");
        c.Record("folder-row", run.DescriptionTitle == "Elements" && BlindKit.DescriptionBody(run) == classic, $"title '{run.DescriptionTitle}', text '{BlindKit.DescriptionBody(run)}'.");
        c.AssertAllPassed();
    }

    // ---- sections 6, 7: unreadable and absent policy files ---------------------------------------------------

    /// <summary>
    /// Sections 6 and 7: an unreadable policy file of one side gives empty States for that side, the comments still work, the
    /// other side is unaffected, a problem is recorded and the window keeps answering.
    /// </summary>
    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_ListBlind_UnreadablePolicyFile()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var c = new GpeditChecks("blind", "unreadable-pol");
        using GpeditRun run = BlindKit.Open("blind-unreadable-pol", sandbox => sandbox.WriteUnreadablePolicyFile(Bwc.Gpo.Store.GpoSide.Machine));

        c.Record("problem-recorded", run.Int("Problems") >= 1 && run.Str("ProblemList").Length > 0, $"Problems={run.Int("Problems")}, ProblemList='{run.Str("ProblemList")}'.");
        c.Record("loaded", run.Str("Loaded") == "1", $"Loaded='{run.Str("Loaded")}'.");

        run.GoTo(BlindKit.Path(BlindKit.Computer, "Basics"));
        Dictionary<string, (string State, string Comment)> computer = BlindKit.StateMap(run);
        c.Record("computer.empty-states", computer.Count == 12 && computer.Values.All(v => v.State.Length == 0), $"states: {string.Join("|", computer.Select(kv => kv.Key + "=" + kv.Value.State))}.");
        c.Record("computer.comments-still-work", computer.GetValueOrDefault("Class machine flag").Comment == "Yes" && computer.GetValueOrDefault("Enabled value only").Comment == "No", $"comments: machine flag '{computer.GetValueOrDefault("Class machine flag").Comment}', enabled value only '{computer.GetValueOrDefault("Enabled value only").Comment}'.");

        run.GoTo(BlindKit.Path(BlindKit.User, "Basics"));
        Dictionary<string, (string State, string Comment)> user = BlindKit.StateMap(run);
        c.Record("user.unaffected", user.GetValueOrDefault("Class user flag").State == "Enabled" && user.GetValueOrDefault("Class both flag").State == "Disabled", $"user states: user flag '{user.GetValueOrDefault("Class user flag").State}', both flag '{user.GetValueOrDefault("Class both flag").State}'.");
        c.Record("alive", run.Shell.Find("TreeControl") is not null && run.Rows().Count == 4, "the window answers UIA and lists the user side.");

        run.WriteArtifacts($"gpedit-{run.Name}.png");
        c.AssertAllPassed();
    }

    /// <summary>
    /// Sections 1 and 6 (section 15): a side with no policy file at all reads as nothing configured: never Enabled or Disabled,
    /// comments No.
    /// </summary>
    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_ListBlind_AbsentPolicyFiles()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var c = new GpeditChecks("blind", "absent-pol");
        using GpeditRun run = BlindKit.Open("blind-absent-pol", seed: false);
        run.GoTo(BlindKit.Path(BlindKit.Computer, "Basics"));
        Dictionary<string, (string State, string Comment)> computer = BlindKit.StateMap(run);
        c.Record("computer.states", computer.Count == 12 && computer.Values.All(v => v.State is "Not configured" or ""), $"states: {string.Join("|", computer.Select(kv => kv.Key + "=" + kv.Value.State))}.");
        c.Record("computer.comments", computer.Values.All(v => v.Comment is "No" or ""), $"comments: {string.Join("|", computer.Select(kv => kv.Key + "=" + kv.Value.Comment))}.");
        run.GoTo(BlindKit.Path(BlindKit.User, "Basics"));
        Dictionary<string, (string State, string Comment)> user = BlindKit.StateMap(run);
        c.Record("user.states", user.Count == 4 && user.Values.All(v => v.State is "Not configured" or ""), $"states: {string.Join("|", user.Select(kv => kv.Key + "=" + kv.Value.State))}.");
        c.Note("observed-state-text", $"computer state of 'Class machine flag' with no file: '{computer.GetValueOrDefault("Class machine flag").State}'.");
        c.Record("alive", run.Shell.Find("TreeControl") is not null, "the window answers UIA.");
        run.WriteArtifacts($"gpedit-{run.Name}.png");
        c.AssertAllPassed();
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}

/// <summary>
/// Helpers local to the blind tests: opening the editor with an edited environment, reading the tree
/// and rows.
/// </summary>
internal static class BlindKit
{
    internal const string Root = GpeditRun.RootName;
    internal const string Computer = "Computer Configuration";
    internal const string User = "User Configuration";
    internal const string Templates = "Administrative Templates";
    internal const string Oracle = "BWC Oracle";
    internal const ushort VkUp = 0x26;

    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// The whole tree the fixture and the rules of the doc give (section 4 and 5), indented two spaces per
    /// level.
    /// </summary>
    internal static readonly string[] ExpectedShape =
    [
        "Local Computer Policy",
        "  Computer Configuration",
        "    Software Settings",
        "    Windows Settings",
        "      Name Resolution Policy",
        "      Scripts (Startup/Shutdown)",
        "      Deployed Printers",
        "      Security Settings",
        "      Policy-based QoS",
        "    Administrative Templates",
        "      BWC Oracle",
        "        Basics",
        "        Elements",
        "          Booleans",
        "          Choices",
        "          Lists",
        "          Numbers",
        "          Texts",
        "        Explain text",
        "        Machine only",
        "          Machine nested",
        "            Machine deep",
        "        Shared keys",
        "        Sort cases",
        "        Tools & Utilities",
        "      All Settings",
        "  User Configuration",
        "    Software Settings",
        "    Windows Settings",
        "      Scripts (Logon/Logoff)",
        "      Deployed Printers",
        "      Security Settings",
        "      Policy-based QoS",
        "    Administrative Templates",
        "      BWC Oracle",
        "        Basics",
        "        Elements",
        "          Booleans",
        "          Choices",
        "          Lists",
        "          Numbers",
        "          Texts",
        "        User only",
        "          User nested",
        "      All Settings",
    ];

    internal static readonly string[] OracleComputerFolders = ["Basics", "Elements", "Explain text", "Machine only", "Shared keys", "Sort cases", "Tools & Utilities"];

    // The default order of policy rows is the order of the policy ids (F/Nodes_Computer op 4, F/Nodes_User op 4 and
    // F/Sort_Columns op 17), so the list below is not alphabetical.
    internal static readonly string[] BasicsComputer =
    [
        "Class both with an element",
        "Class both flag",
        "Class machine flag",
        "Element key differs from policy key",
        "Key outside the policy roots",
        "Enabled list and disabled list with value name",
        "Decimal enabled and disabled values",
        "Disabled list only",
        "Disabled value only",
        "Enabled list only",
        "Enabled value only",
        "No value name, elements only",
    ];

    internal static readonly string[] BasicsUser =
    [
        "Class both with an element",
        "Class both flag",
        "Class user flag",
        "String enabled and disabled values",
    ];

    internal static readonly string[] AllUser =
    [
        "Boolean with string values",
        "Choice with string items",
        "Class both flag",
        "Class both with an element",
        "Class user flag",
        "Decimal that is required",
        "List with a value prefix",
        "Long decimal required, soft, stored as text",
        "Multiple text lines with limits",
        "Nested user policy, deep level",
        "String enabled and disabled values",
        "Text with a length limit",
        "Top level user flag",
    ];

    /// <summary>The path from the root to a BWC Oracle category of a side.</summary>
    internal static string[] Path(string side, params string[] rest) => [Root, side, Templates, Oracle, .. rest];

    internal static GpeditRun Open(string name, Action<GpeditSandbox>? prepare = null, bool seed = true, bool shape = false, bool placeWindow = true)
    {
        var sandbox = GpeditSandbox.Create(name);
        if (seed)
        {
            sandbox.SeedStandard();
        }

        prepare?.Invoke(sandbox);
        return GpeditRun.Open(name, sandbox, shape, placeWindow);
    }

    /// <summary>Opens the editor with the environment of the sandbox changed by <paramref name="edit"/>.</summary>
    internal static GpeditRun OpenWithEnvironment(string name, GpeditSandbox sandbox, Action<Dictionary<string, string>> edit, TimeSpan? loadTimeout = null)
    {
        Dictionary<string, string> environment = sandbox.Environment();
        edit(environment);
        ShellRun shell = ShellRun.Open(name, environment);
        ConstructorInfo constructor = typeof(GpeditRun).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(ShellRun), typeof(GpeditSandbox)], null)
            ?? throw new InvalidOperationException("GpeditRun has no (ShellRun, GpeditSandbox) constructor.");
        var run = (GpeditRun)constructor.Invoke([shell, sandbox]);
        try
        {
            shell.PlaceWindow(20, 20, Math.Min(1900, ShellRun.ScreenWidth - 60), 1000);
            bool ready = ShellRun.WaitUntil(() => run.Readout().GetValueOrDefault("Loaded") == "1", loadTimeout ?? TimeSpan.FromSeconds(60));
            if (!ready)
            {
                throw new TimeoutException($"The templates did not load. Readout: '{run.RawReadout()}'.");
            }

            return run;
        }
        catch
        {
            shell.Dispose();
            throw;
        }
    }

    internal static Dictionary<string, string> Parse(string raw)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int at = part.IndexOf('=', StringComparison.Ordinal);
            if (at > 0)
            {
                map[part[..at]] = part[(at + 1)..];
            }
        }

        return map;
    }

    /// <summary>The lines of the whole tree text, the trailing blanks removed.</summary>
    internal static string[] ShapeLines(GpeditRun run) =>
        run.Shape().Split('\n').Select(l => l.TrimEnd('\r', ' ')).Where(l => l.Length > 0).ToArray();

    /// <summary>The lines of the shape from the User Configuration node on.</summary>
    internal static IEnumerable<string> UserSubtree(string[] lines)
    {
        int index = Array.FindIndex(lines, l => l == "  User Configuration");
        return index < 0 ? [] : lines.Skip(index);
    }

    /// <summary>The names of the tree rows in display order.</summary>
    internal static List<string> TreeRows(GpeditRun run)
    {
        var rows = new List<(int Index, string Name)>();
        AutomationElement? tree = run.Shell.Find("TreeControl");
        if (tree is null)
        {
            return [];
        }

        foreach (AutomationElement item in tree.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem)))
        {
            string id = ShellRun.SafeId(item);
            const string prefix = "TreeRow_";
            if (id.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(id[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            {
                rows.Add((index, ShellRun.SafeName(item)));
            }
        }

        return rows.OrderBy(r => r.Index).Select(r => r.Name).ToList();
    }

    internal static void ClickTreeRow(GpeditRun run, int index)
    {
        _ = ControlTestSupport.EnsureForeground(run.Shell.Session);
        run.Shell.Find("TreeRow_" + index.ToString(CultureInfo.InvariantCulture))?.Click();
        Thread.Sleep(ShellRun.KeySettle);
    }

    /// <summary>The State and Comment cells of the realized rows by display name (without the word setting).</summary>
    internal static Dictionary<string, (string State, string Comment)> StateMap(GpeditRun run)
    {
        var map = new Dictionary<string, (string State, string Comment)>(StringComparer.Ordinal);
        foreach (GpeditRow row in run.Rows())
        {
            string name = row.Name.EndsWith(" setting", StringComparison.Ordinal) ? row.Name[..^" setting".Length] : row.Name;
            map[name] = (row.Cells.Length > 1 ? row.Cells[1] : "?", row.Cells.Length > 2 ? row.Cells[2] : "?");
        }

        return map;
    }

    internal static string Show(Dictionary<string, (string State, string Comment)> map, string name) =>
        map.TryGetValue(name, out (string State, string Comment) value) ? $"({value.State}, {value.Comment})" : "(absent)";

    /// <summary>The body text of the description pane: the policy description, or the hint or explain text of a node.</summary>
    internal static string DescriptionBody(GpeditRun run)
    {
        AutomationElement? hint = run.Shell.Find("ConsoleDescriptionText_hint");
        return hint is not null ? ShellRun.SafeName(hint) : run.DescriptionText("description");
    }

    internal static string Norm(string text) => Spaces.Replace(text, " ").Trim();

    internal static bool IsSelected(AutomationElement? element)
    {
        try
        {
            return element?.Patterns.SelectionItem.PatternOrDefault?.IsSelected.ValueOrDefault ?? false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Records the checks of one placeholder page: the page exists once, names the node and the list rows
    /// are gone.
    /// </summary>
    internal static void CheckPlaceholder(GpeditChecks c, GpeditRun run, string id, string node)
    {
        AutomationElement? list = run.Shell.Session.Window.FindFirstDescendant(cf => cf.ByAutomationId("ConsoleList"));
        int pages = run.Shell.Session.Window.FindAllDescendants(cf => cf.ByAutomationId("GpeditPlaceholderPage")).Length;
        AutomationElement? page = run.Shell.Find("GpeditPlaceholderPage");
        string text = string.Join(" ", run.Shell.Session.Window.FindAllDescendants(cf => cf.ByAutomationId("GpeditPlaceholderPage")).SelectMany(p => p.FindAllDescendants()).Select(ShellRun.SafeName)) + " " + ShellRun.SafeName(page);
        c.Record($"{id}.page-once", pages == 1, $"{pages} element(s) with the id GpeditPlaceholderPage.");
        c.Record($"{id}.page-names-node", text.Contains(node, StringComparison.Ordinal), $"page text '{text}' names '{node}'.");
        c.Record($"{id}.rows-cleared", run.Rows().Count == 0 && run.Str("Page") == "placeholder", $"rows {run.Rows().Count}, Page='{run.Str("Page")}', list element {(list is null ? "absent" : "present")}.");
        c.Record($"{id}.tabs-kept", run.Shell.Find("ConsoleExtendedTabs") is not null, "the Extended tab strip stays (the layout does not jump).");
        c.Record($"{id}.title", run.DescriptionTitle == node, $"description title '{run.DescriptionTitle}'.");
    }

    /// <summary>Drags the center of an element sideways by <paramref name="dx"/> physical pixels.</summary>
    internal static void DragBy(GpeditRun run, string id, int dx)
    {
        Rectangle? rect = run.Rect(id);
        if (rect is null)
        {
            throw new InvalidOperationException($"No visible element '{id}'.");
        }

        _ = ControlTestSupport.EnsureForeground(run.Shell.Session);
        var center = new Point(rect.Value.X + (rect.Value.Width / 2), rect.Value.Y + (rect.Value.Height / 2));
        ShellRun.DragStart(center, dx);
        ShellRun.DragEnd();
        Thread.Sleep(300);
    }

    internal static string ReadSettings(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal sealed class Counter
    {
        internal int Count { get; set; }
    }

    /// <summary>Walks a JSON tree and counts its numbers; the callback is applied to each number.</summary>
    internal static Counter Mutate(JsonNode? node, Counter counter, Func<JsonNode?, JsonNode?> replace)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (string key in obj.Select(kv => kv.Key).ToArray())
                {
                    JsonNode? child = obj[key];
                    if (IsNumber(child))
                    {
                        counter.Count++;
                        obj[key] = replace(child);
                    }
                    else
                    {
                        _ = Mutate(child, counter, replace);
                    }
                }

                break;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    JsonNode? child = array[i];
                    if (IsNumber(child))
                    {
                        counter.Count++;
                        array[i] = replace(child);
                    }
                    else
                    {
                        _ = Mutate(child, counter, replace);
                    }
                }

                break;
            default:
                break;
        }

        return counter;
    }

    /// <summary>The settings text with every number replaced.</summary>
    internal static string Rewrite(string json, Func<JsonNode?, JsonNode?> replace)
    {
        JsonNode? root = JsonNode.Parse(json);
        _ = Mutate(root, new Counter(), replace);
        return root?.ToJsonString() ?? json;
    }

    /// <summary>The settings text with every string value replaced.</summary>
    internal static string RewriteStrings(string json, string value)
    {
        JsonNode? root = JsonNode.Parse(json);
        ReplaceStrings(root, value);
        return root?.ToJsonString() ?? json;
    }

    private static void ReplaceStrings(JsonNode? node, string value)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (string key in obj.Select(kv => kv.Key).ToArray())
                {
                    JsonNode? child = obj[key];
                    if (IsString(child))
                    {
                        obj[key] = JsonValue.Create(value);
                    }
                    else
                    {
                        ReplaceStrings(child, value);
                    }
                }

                break;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    JsonNode? child = array[i];
                    if (IsString(child))
                    {
                        array[i] = JsonValue.Create(value);
                    }
                    else
                    {
                        ReplaceStrings(child, value);
                    }
                }

                break;
            default:
                break;
        }
    }

    private static bool IsNumber(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number;

    private static bool IsString(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String;

    /// <summary>The text of a string of the fixture's policy resources (en-US).</summary>
    internal static string FixtureString(string id) => ReadString(System.IO.Path.Combine(GpeditSandbox.FixtureRoot, "en-US", "BwcOracle.adml"), id);

    internal static string BaseFixtureString(string id) => ReadString(System.IO.Path.Combine(GpeditSandbox.FixtureRoot, "en-US", "BwcOracleBase.adml"), id);

    private static string ReadString(string file, string id)
    {
        XDocument document = XDocument.Load(file);
        return document.Descendants().First(e => e.Name.LocalName == "string" && (string?)e.Attribute("id") == id).Value;
    }
}

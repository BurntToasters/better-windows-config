using System.Text.Json.Nodes;
using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.Controls;
using Bwc.E2E.Gpedit.Commands;
using Bwc.E2E.Harness;
using Bwc.Policy.Tests.Golden;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Bwc.E2E.Gpedit.Commands.GpeditNames;

namespace Bwc.E2E.Gpedit.Filter;

/// <summary>
/// Filter result tests of the Local Group Policy Editor: each filter setting of F/Filter_Options gives the rows classic
/// showed and the narrowed count, Filter On toggles and stays on across nodes, Re-Apply Filter, folders with no matching
/// policy are hidden, and the filtered All Settings node. The failure modes are in docs/failure-modes/gpedit-filter.md,
/// sections 4 to 9. Every test writes artifacts/e2e/visual/gpedit/gpedit-&lt;name&gt;.json and a picture, and its
/// checks to the E2E result artifact.
/// </summary>
[TestClass]
public sealed class GpeditFilterResultsTests
{
    private const string CaseId = "F/Filter_Options";

    private static readonly string[] AllSortRows = FilterNames.Rows("Policy 1", "Policy 10", "Policy 2", "#Symbol policy", "alpha policy", "Éclair policy", "Alpha Policy", "Zulu sort case");

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Filter_CaptureReplay()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("filter", "replay");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-filter-replay");
        FilterSeed.SortCases(sandbox);
        using GpeditRun run = GpeditRun.Open("gpedit-filter-replay", sandbox);
        GoldenCase golden = GoldenCase.Load(CaseId);

        // The unfiltered list of the capture (op 5) and its status count.
        run.GoTo(FilterNames.SortCasesPath);
        string[] unfiltered = Names(golden, 5);
        checks.Record("unfiltered.rows", run.WaitRowsAsSet(unfiltered).Length == unfiltered.Length, $"rows {string.Join("|", run.RowNames())}; capture {string.Join("|", unfiltered)}.");
        checks.Record("unfiltered.status", run.WaitStatusNumbers(8) && !run.StatusFlagShown(), $"status numbers {string.Join(",", run.StatusNumbers())}; flag shown={run.StatusFlagShown()}.");

        // Each setFilter operation: the dialog values are the ones the capture recorded after OK, the rows the ones it read two
        // operations later. Operation 62 (the refusal) is the dialog refusal test, operation 66 differs by design (FC11) and
        // operation 70 is the kept-on test.
        foreach (GoldenOp op in golden.Ops.Where(o => o.Kind == "setFilter" && o.Index is not (62 or 70)))
        {
            string label = op.Text("label") ?? op.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string id = "op" + op.Index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
            run.GoTo(FilterNames.SortCasesPath);
            FilterValues values = ValuesOf(op);
            checks.Record(id + ".applied", run.ApplyFilter(values), $"{label}: the dialog was set and OK closed it.");
            checks.Record(id + ".on", run.WaitFilterOn(true), $"{label}: FilterOn={run.Str("FilterOn")} after OK.");
            JsonObject? read = golden.Ops.Single(o => o.Index == op.Index + 2).Data;
            bool hidden = op.Index != 66 && read?["nodeHidden"]?.GetValue<bool>() == true;
            if (hidden)
            {
                string[] selected = [Root, .. read!["selectedPath"]!.AsArray().Select(n => n!.GetValue<string>())];
                checks.Record(id + ".nodeHidden", ShellRun.WaitUntil(() => run.NodePath().SequenceEqual(selected)), $"{label}: the Sort cases node is hidden and the selection moved to '{string.Join(" > ", selected)}'; node={run.Str("Node")}.");
                checks.Record(id + ".treeHidden", !run.TreeItemNames().Contains(FilterNames.SortCases) || selected.Length < 4, $"{label}: no tree item named Sort cases while the filter hides it; items={string.Join("|", run.TreeItemNames())}.");
            }
            else
            {
                // Operation 66 selects every platform of BWC's own list, and every Sort cases policy has a supported-on of it.
                string[] expected = op.Index == 66 ? AllSortRows : Names(golden, op.Index + 2);
                string[] shown = run.WaitRowsAsSet(expected);
                checks.Record(id + ".rows", shown.Length == expected.Length && !shown.Except(expected).Any(), $"{label}: rows [{string.Join("|", shown)}]; classic showed [{string.Join("|", expected)}].");
                checks.Record(id + ".status", run.WaitStatusNumbers(expected.Length, 8), $"{label}: status numbers [{string.Join(",", run.StatusNumbers())}]; expected [{expected.Length},8].");
                checks.Record(id + ".flag", run.StatusFlagShown(), $"{label}: the status bar shows the filter flag.");
            }

            // Clear: switch the filter off. The list returns to all eight rows and the flag goes.
            checks.Record(id + ".off", run.ToggleFilterByMenu() && run.WaitFilterOn(false), $"{label}: Filter On switched off.");
            run.GoTo(FilterNames.SortCasesPath);
            checks.Record(id + ".backToAll", run.WaitRowsAsSet(AllSortRows).Length == 8 && run.WaitStatusNumbers(8) && !run.StatusFlagShown(), $"{label}: rows {run.RowNames().Length}, status [{string.Join(",", run.StatusNumbers())}], flag={run.StatusFlagShown()}.");
        }

        run.WriteArtifacts("gpedit-filter-replay.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Filter_OnOffAndStaysOn()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("filter", "onoff");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-filter-onoff");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-filter-onoff", sandbox);

        // F/Filter_Options ops 62 to 64: Filter On with no restriction lists every row and shows the two-number status.
        run.GoTo(BasicsPath);
        checks.Record("start.off", run.Count("FilterOn") == 0 && !run.StatusFlagShown() && run.WaitStatusNumbers(12), $"FilterOn={run.Str("FilterOn")}, status [{string.Join(",", run.StatusNumbers())}].");
        checks.Record("toggleOn", run.ToggleFilterByMenu() && run.WaitFilterOn(true), $"FilterOn={run.Str("FilterOn")}.");
        checks.Record("on.rows", run.WaitRowsAsSet(run.RowNames()).Length == 12 && run.Count("Rows") == 12, $"rows={run.Count("Rows")}; a filter with no restriction hides nothing.");
        checks.Record("on.status", run.WaitStatusNumbers(12, 12), $"status numbers [{string.Join(",", run.StatusNumbers())}]; expected [12,12].");
        checks.Record("on.flag", run.StatusFlagShown(), "the status bar shows the filter flag.");
        checks.Record("on.counts", run.Count("FilterShown") == 12 && run.Count("FilterTotal") == 12, $"FilterShown={run.Str("FilterShown")}, FilterTotal={run.Str("FilterTotal")}.");
        run.WriteArtifacts("gpedit-filter-onoff-on.png");

        // The filter stays on when the node changes (VIEW-042): the next node shows its own two numbers.
        run.GoTo(FilterNames.SortCasesPath);
        checks.Record("stays.node", run.Count("FilterOn") == 1 && run.WaitStatusNumbers(8, 8) && run.StatusFlagShown(), $"FilterOn={run.Str("FilterOn")}, status [{string.Join(",", run.StatusNumbers())}], flag={run.StatusFlagShown()}.");
        run.GoTo(Root, User, Admin, Oracle, BasicsName);
        checks.Record("stays.user", run.Count("FilterOn") == 1 && run.StatusFlagShown(), $"FilterOn={run.Str("FilterOn")} on the user side too.");
        run.GoTo(Root, Computer);
        checks.Record("stays.configNode", run.Count("FilterOn") == 1, $"FilterOn={run.Str("FilterOn")} at a node that has no filter entries.");

        // Switching it off restores the single number.
        run.GoTo(BasicsPath);
        checks.Record("toggleOff", run.ToggleFilterByMenu() && run.WaitFilterOn(false), $"FilterOn={run.Str("FilterOn")}.");
        checks.Record("off.status", run.WaitStatusNumbers(12) && !run.StatusFlagShown(), $"status [{string.Join(",", run.StatusNumbers())}], flag={run.StatusFlagShown()}.");

        // The toolbar button toggles the same state.
        checks.Record("button.present", run.WaitFilterButton(true), "the Filter button shows at a category node.");
        _ = run.InvokeButton(FilterIds.ToolbarButton);
        checks.Record("button.on", run.WaitFilterOn(true), $"FilterOn={run.Str("FilterOn")} after the button.");
        _ = run.InvokeButton(FilterIds.ToolbarButton);
        checks.Record("button.off", run.WaitFilterOn(false), $"FilterOn={run.Str("FilterOn")} after the second press.");

        run.WriteArtifacts("gpedit-filter-onoff.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Filter_FoldersWithNoMatchAreHidden()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("filter", "folders");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-filter-folders");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-filter-folders", sandbox);

        // The standard seed configures three policies, all in Basics. Configured Yes at BWC Oracle: only Basics stays.
        run.GoTo(OraclePath);
        checks.Record("before.folders", ShellRun.WaitUntil(() => run.Count("ShownFolders") == 7), $"folder rows before the filter: {run.Str("ShownFolders")}; classic lists 7 (F/Nodes_Computer).");
        checks.Record("applied", run.ApplyFilter(new FilterValues { Configured = "Yes" }) && run.WaitFilterOn(true), $"FilterOn={run.Str("FilterOn")}.");
        checks.Record("rows.onlyBasics", run.WaitRowsInOrder([BasicsName]).SequenceEqual([BasicsName]) && run.Count("ShownFolders") == 1, $"rows after the filter: {string.Join("|", run.RowNames())}; ShownFolders={run.Str("ShownFolders")}.");
        string[] tree = run.TreeItemNames();
        checks.Record("tree.onlyBasics", tree.Contains(BasicsName) && !tree.Contains("Sort cases") && !tree.Contains("Shared keys") && !tree.Contains("Elements"), $"tree items: {string.Join("|", tree)}.");
        checks.Record("node.stays", run.NodePath().SequenceEqual(OraclePath), $"node={run.Str("Node")}; a node that has a matching descendant stays selected.");
        run.WriteArtifacts("gpedit-filter-folders-on.png");

        // Basics shows its three configured policies out of twelve.
        run.GoTo(BasicsPath);
        string[] basics = FilterNames.Rows(MachineFlagName, BothFlagName, DecimalValuesName);
        checks.Record("basics.rows", run.WaitRowsAsSet(basics).SequenceEqual(run.RowNames()) && run.RowNames().Length == 3, $"Basics rows: {string.Join("|", run.RowNames())}.");
        checks.Record("basics.status", run.WaitStatusNumbers(3, 12) && run.StatusFlagShown(), $"Basics status [{string.Join(",", run.StatusNumbers())}]; expected [3,12].");

        // The Sort cases node has no configured policy: filtering while it is selected moves the selection to its parent
        // (F/Filter_Options op 27 and 29). The filter goes off first, because a hidden folder cannot be opened.
        checks.Record("off", run.ToggleFilterByMenu() && run.WaitFilterOn(false), $"FilterOn={run.Str("FilterOn")}.");
        run.GoTo(FilterNames.SortCasesPath);
        checks.Record("hiddenSelected.applied", run.ApplyFilter(new FilterValues { Configured = "Yes" }) && run.WaitFilterOn(true), $"FilterOn={run.Str("FilterOn")}.");
        checks.Record("hiddenSelected.moved", ShellRun.WaitUntil(() => run.NodePath().SequenceEqual(OraclePath)), $"node after the selected node was hidden: {run.Str("Node")}; expected BWC Oracle.");
        checks.Record("hiddenSelected.noRow", !run.RowNames().Contains(FilterNames.SortCases), "the Sort cases folder row is gone while it has no match.");

        // Switching the filter off brings every folder back.
        checks.Record("off2", run.ToggleFilterByMenu() && run.WaitFilterOn(false), $"FilterOn={run.Str("FilterOn")}.");
        checks.Record("off.folders", ShellRun.WaitUntil(() => run.Count("ShownFolders") == 7), $"folder rows after the filter went off: {run.Str("ShownFolders")}.");
        checks.Record("off.tree", run.TreeItemNames().Contains("Sort cases") && run.TreeItemNames().Contains("Shared keys"), $"tree items: {string.Join("|", run.TreeItemNames())}.");
        run.GoTo(FilterNames.SortCasesPath);
        checks.Record("off.sortCasesBack", run.NodePath().SequenceEqual(FilterNames.SortCasesPath) && run.WaitRowsAsSet(AllSortRows).Length == 8, $"node={run.Str("Node")}, rows={run.RowNames().Length}.");

        run.WriteArtifacts("gpedit-filter-folders.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Filter_NoMatchHidesWholeBranch()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("filter", "branch");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-filter-branch");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-filter-branch", sandbox);

        // A keyword that no policy holds hides every category, so BWC Oracle goes; the selection moves up to Administrative
        // Templates (F/Filter_Options op 67 and 55), and All Settings stays in the tree. The row list of All Settings is empty.
        run.GoTo(BasicsPath);
        checks.Record("applied", run.ApplyFilter(new FilterValues { Keywords = true, Text = "zzqqxx", InTitle = true, InHelp = true, InComment = true }) && run.WaitFilterOn(true), $"FilterOn={run.Str("FilterOn")}.");
        checks.Record("selectionMoves", ShellRun.WaitUntil(() => run.NodePath().SequenceEqual([Root, Computer, Admin])), $"node={run.Str("Node")}; expected the Administrative Templates node.");
        checks.Record("oracleHidden", ShellRun.WaitUntil(() => !run.TreeItemNames().Contains(Oracle)) && !run.RowNames().Contains(Oracle), $"tree items={string.Join("|", run.TreeItemNames())}, rows={string.Join("|", run.RowNames())}.");
        checks.Record("allSettingsStays", run.TreeItemNames().Contains(AllSettings), $"tree items={string.Join("|", run.TreeItemNames())}.");
        run.GoTo(FilterNames.AllSettingsComputer);
        checks.Record("allSettings.empty", run.WaitStatusNumbers(0, 59) && run.Count("Rows") == 0, $"All Settings status [{string.Join(",", run.StatusNumbers())}], rows={run.Str("Rows")}; expected 0 of 59.");
        checks.Record("allSettings.node", run.NodePath().SequenceEqual(FilterNames.AllSettingsComputer), $"node={run.Str("Node")}; All Settings is never hidden.");

        // Filter off: the whole branch is back.
        checks.Record("off", run.ToggleFilterByMenu() && run.WaitFilterOn(false), $"FilterOn={run.Str("FilterOn")}.");
        run.GoTo(BasicsPath);
        checks.Record("back", run.NodePath().SequenceEqual(BasicsPath) && run.WaitRowsAsSet(run.RowNames()).Length == 12, $"node={run.Str("Node")}, rows={run.RowNames().Length}.");

        run.WriteArtifacts("gpedit-filter-branch.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Filter_KeptOnAcrossNodesAndReApply()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("filter", "reapply");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-filter-reapply");
        sandbox.SeedStandard();
        sandbox.DialogEnabled = true;
        using GpeditRun run = GpeditRun.Open("gpedit-filter-reapply", sandbox);
        run.GoTo(BasicsPath);

        // Re-Apply Filter is grayed while no filter is on (MENU-015) and enabled while one is.
        List<Commands.GpeditMenuEntry> entries = run.OpenTopMenu(Commands.GpeditCommandIds.ActionMenu);
        GpeditFilterRun.CheckFilterEntries(checks, "off", entries, expected: true, filterOn: false, "the Action menu with the filter off");
        _ = run.CloseMenus();
        checks.Record("applied", run.ApplyFilter(new FilterValues { Configured = "Yes" }) && run.WaitFilterOn(true), $"FilterOn={run.Str("FilterOn")}.");
        entries = run.OpenTopMenu(Commands.GpeditCommandIds.ActionMenu);
        GpeditFilterRun.CheckFilterEntries(checks, "on", entries, expected: true, filterOn: true, "the Action menu with the filter on");
        _ = run.CloseMenus();
        string[] before = FilterNames.Rows(MachineFlagName, BothFlagName, DecimalValuesName);
        checks.Record("rows.before", run.WaitRowsAsSet(before).Length == 3, $"rows: {string.Join("|", run.RowNames())}.");

        // Re-Apply with nothing changed keeps the list and the count.
        checks.Record("reapply.runs", run.InvokeMenuEntry(Commands.GpeditCommandIds.ActionMenu, FilterIds.ReApply), "Re-Apply Filter runs from the Action menu.");
        checks.Record("reapply.same", run.WaitRowsAsSet(before).Length == 3 && run.WaitStatusNumbers(3, 12) && run.Count("FilterOn") == 1, $"rows {string.Join("|", run.RowNames())}, status [{string.Join(",", run.StatusNumbers())}].");

        // A policy edited while the filter is on: after Re-Apply the list is the filter applied to the new states.
        using (GpeditDialog? dialog = GpeditDialog.Open(run, FilterNames.Row(BothFlagName)))
        {
            checks.Record("dialog.opens", dialog is not null, "the setting dialog opened for Class both flag.");
            if (dialog is not null)
            {
                checks.Record("dialog.notConfigured", dialog.Select(GpeditDialog.NotConfigured), "Not Configured chosen.");
                checks.Record("dialog.ok", dialog.Invoke(GpeditDialog.Ok) && dialog.WaitClosed(), "OK closed the dialog.");
            }
        }

        checks.Record("reapply.afterEdit.runs", run.InvokeMenuEntry(Commands.GpeditCommandIds.ActionMenu, FilterIds.ReApply), "Re-Apply Filter runs after the edit.");
        string[] after = FilterNames.Rows(MachineFlagName, DecimalValuesName);
        checks.Record("reapply.afterEdit.rows", run.WaitRowsAsSet(after).Length == 2 && !run.RowNames().Contains(FilterNames.Row(BothFlagName)), $"rows {string.Join("|", run.RowNames())}; expected {string.Join("|", after)}.");
        checks.Record("reapply.afterEdit.status", run.WaitStatusNumbers(2, 12), $"status [{string.Join(",", run.StatusNumbers())}]; expected [2,12].");

        // The same filter works at another node and on the other side: the options are one set for the whole editor.
        run.GoTo(Root, User, Admin, Oracle, BasicsName);
        checks.Record("user.rows", run.WaitRowsAsSet(FilterNames.Rows("Class user flag", BothFlagName)).Length == 2, $"user rows with Configured Yes: {string.Join("|", run.RowNames())}.");
        checks.Record("user.status", run.WaitStatusNumbers(2, 4), $"user status [{string.Join(",", run.StatusNumbers())}].");

        // Filter off grays Re-Apply again.
        checks.Record("off", run.ToggleFilterByMenu() && run.WaitFilterOn(false), $"FilterOn={run.Str("FilterOn")}.");
        entries = run.OpenTopMenu(Commands.GpeditCommandIds.ActionMenu);
        GpeditFilterRun.CheckFilterEntries(checks, "offAgain", entries, expected: true, filterOn: false, "the Action menu after the filter went off");
        _ = run.CloseMenus();

        run.WriteArtifacts("gpedit-filter-reapply.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Filter_Results_AllSettings()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("filter", "allsettings");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-filter-allsettings");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-filter-allsettings", sandbox);

        // The seed: Computer side configures Class machine flag, Class both flag and the decimal policy; commented are the
        // first two. User side configures Class user flag and Class both flag.
        run.GoTo(FilterNames.AllSettingsComputer);
        checks.Record("computer.unfiltered", ShellRun.WaitUntil(() => run.Count("Rows") == 59) && run.WaitStatusNumbers(59), $"rows={run.Str("Rows")}, status [{string.Join(",", run.StatusNumbers())}].");

        // VIEW-009: the headers of F/AllSettings_Computer and F/AllSettings_User are four: Setting, State, Comment and a fourth
        // that holds the path. Every other node has three.
        string[] headers = run.HeaderNames("setting", "state", "comment", "path");
        checks.Record("computer.pathHeader", headers.All(h => h.Length > 0), $"headers at All Settings: {string.Join("|", headers)}; the fourth is the Path column.");
        checks.Record("computer.fourCells", run.Rows().Count > 0 && run.Rows().All(r => r.Cells.Length == 4 && r.Cells[3].Length > 0), $"cells per row {string.Join(",", run.Rows().Select(r => r.Cells.Length).Distinct())}; the Path cell is not empty.");
        checks.Record("computer.apply", run.ApplyFilter(new FilterValues { Configured = "Yes" }) && run.WaitFilterOn(true), $"FilterOn={run.Str("FilterOn")}.");
        string[] configured = FilterNames.Rows(MachineFlagName, BothFlagName, DecimalValuesName);
        checks.Record("computer.configured", run.WaitRowsAsSet(configured).Length == 3 && run.WaitStatusNumbers(3, 59), $"rows {string.Join("|", run.RowNames())}, status [{string.Join(",", run.StatusNumbers())}]; expected 3 of 59.");
        checks.Record("computer.flag", run.StatusFlagShown(), "the filter flag shows at All Settings.");
        run.WriteArtifacts("gpedit-filter-allsettings-computer.png");

        checks.Record("computer.commented", run.ApplyFilter(new FilterValues { Commented = "Yes" }) && run.WaitRowsAsSet(FilterNames.Rows(MachineFlagName, BothFlagName)).Length == 2 && run.WaitStatusNumbers(2, 59), $"commented rows {string.Join("|", run.RowNames())}.");
        checks.Record("computer.keywordComment", run.ApplyFilter(new FilterValues { Keywords = true, Text = "both note", Match = "Exact", InTitle = false, InHelp = false, InComment = true }) && run.WaitRowsAsSet(FilterNames.Rows(BothFlagName)).Length == 1 && run.WaitStatusNumbers(1, 59), $"keyword rows {string.Join("|", run.RowNames())}.");
        checks.Record("computer.noMatch", run.ApplyFilter(new FilterValues { Keywords = true, Text = "zzqqxx" }) && ShellRun.WaitUntil(() => run.Count("Rows") == 0) && run.WaitStatusNumbers(0, 59), $"rows={run.Str("Rows")}, status [{string.Join(",", run.StatusNumbers())}]; an empty result is 0 of 59, and the node stays.");
        checks.Record("computer.nodeStays", run.NodePath().SequenceEqual(FilterNames.AllSettingsComputer), $"node={run.Str("Node")}.");

        // The user side, same filter object, with the user seed.
        _ = run.ApplyFilter(new FilterValues { Configured = "Yes" });
        run.GoTo(FilterNames.AllSettingsUser);
        string[] userConfigured = FilterNames.Rows("Class user flag", BothFlagName);
        checks.Record("user.configured", run.WaitRowsAsSet(userConfigured).Length == 2 && run.WaitStatusNumbers(2, 13), $"user rows {string.Join("|", run.RowNames())}, status [{string.Join(",", run.StatusNumbers())}]; expected 2 of 13.");
        checks.Record("user.cells", run.Rows().Count == 2 && run.Rows().All(r => r.Cells.Length == 4), "the filtered rows keep their four cells.");

        // Off: all 13 user settings are back and the flag is gone.
        checks.Record("off", run.ToggleFilterByMenu() && run.WaitFilterOn(false) && run.WaitStatusNumbers(13) && !run.StatusFlagShown(), $"status [{string.Join(",", run.StatusNumbers())}], flag={run.StatusFlagShown()}.");

        run.WriteArtifacts("gpedit-filter-allsettings.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Filter_RequirementsByPlatform()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("filter", "requirements");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-filter-requirements");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-filter-requirements", sandbox);
        run.GoTo(BasicsPath);

        // BWC lists the supported-on definitions of the loaded templates (FC11: classic listed the machine's own products).
        // Basics on the Computer side: Class both flag needs the older definition, Class both with an element the local one,
        // every other policy the any definition.
        checks.Record("older", run.ApplyFilter(new FilterValues { Requirements = true, Platforms = [FilterIds.PlatformOlder] }) && run.WaitRowsAsSet(FilterNames.Rows(BothFlagName)).Length == 1, $"older rows {string.Join("|", run.RowNames())}.");
        checks.Record("anyOfTwo", run.ApplyFilter(new FilterValues { Requirements = true, Platforms = [FilterIds.PlatformOlder, FilterIds.PlatformLocal] }) && run.WaitRowsAsSet(FilterNames.Rows(BothFlagName, "Class both with an element")).Length == 2, $"Any of two rows {string.Join("|", run.RowNames())}.");
        checks.Record("allOfTwo", run.ApplyFilter(new FilterValues { Requirements = true, PlatformMatch = "All", Platforms = [FilterIds.PlatformOlder, FilterIds.PlatformLocal] }) && ShellRun.WaitUntil(() => run.NodePath().SequenceEqual([Root, Computer, Admin])), $"All of two: node={run.Str("Node")}; no policy has two supported-on definitions, so no folder has a match, BWC Oracle is hidden too and the selection moves up to Administrative Templates.");
        checks.Record("selectAll", run.ApplyFilter(new FilterValues { Requirements = true, SelectAllPlatforms = true }), "Select All then OK.");
        run.GoTo(BasicsPath);
        checks.Record("selectAll.rows", run.WaitRowsAsSet(run.RowNames()).Length == 12 && run.WaitStatusNumbers(12, 12), $"Select All shows every Basics policy: status [{string.Join(",", run.StatusNumbers())}].");

        run.WriteArtifacts("gpedit-filter-requirements.png");
        checks.AssertAllPassed();
    }

    private const string MachineFlagName = "Class machine flag";
    private const string BothFlagName = "Class both flag";
    private const string DecimalValuesName = "Decimal enabled and disabled values";

    private static string[] Names(GoldenCase golden, int index)
    {
        JsonObject data = golden.Ops.Single(o => o.Index == index).Data ?? throw new AssertFailedException($"{CaseId} operation {index}: no list was recorded.");
        return (data["rows"] as JsonArray ?? new JsonArray()).Select(r => FilterNames.Row(r!["name"]!.GetValue<string>())).ToArray();
    }

    private static bool Box(JsonNode? node) => node?["checkState"]?.GetValue<int>() == 1;

    private static string Pick(JsonNode? node) => node?["selectedText"]?.GetValue<string>() ?? "Any";

    /// <summary>The dialog values as the capture recorded them after OK. Platforms: none or every platform.</summary>
    private static FilterValues ValuesOf(GoldenOp op)
    {
        JsonNode after = op.Data?["after"] ?? throw new AssertFailedException($"{CaseId} operation {op.Index}: no dialog was recorded after OK.");
        bool all = op.Op["filter"]?["platforms"]?.GetValue<string>() == "all";
        return new FilterValues
        {
            Managed = Pick(after["managed"]),
            Configured = Pick(after["configured"]),
            Commented = Pick(after["commented"]),
            Keywords = Box(after["keywordsEnabled"]),
            Text = after["keywords"]?["text"]?.GetValue<string>() ?? string.Empty,
            Match = Pick(after["match"]),
            InTitle = Box(after["inTitle"]),
            InHelp = Box(after["inHelp"]),
            InComment = Box(after["inComment"]),
            Requirements = Box(after["requirementsEnabled"]),
            SelectAllPlatforms = all,
        };
    }
}

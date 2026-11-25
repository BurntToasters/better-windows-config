using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using Bwc.E2E.List;
using Bwc.Gpo.Store;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Bwc.E2E.Gpedit.Commands.GpeditNames;

namespace Bwc.E2E.Gpedit.Commands;

/// <summary>
/// Refresh tests of the Local Group Policy Editor: F5 reloads the template set and the policy files
/// from the sandbox, keeps the selection, survives a vanished node, a burst, a failing file and an open
/// dialog, and leaves no stale history or handler behind (BEH-022, G2-1, GPE-4, GPE-5, EVT-1). The
/// tests change the sandbox files on disk through the sandbox helpers while the editor runs. The
/// failure modes are in docs/failure-modes/gpedit-commands.md, sections 5, 6 and 9. Every test writes
/// artifacts/e2e/visual/gpedit/gpedit-&lt;name&gt;.json and a picture, and its checks to the E2E result
/// artifact.
/// </summary>
[TestClass]
public sealed class GpeditRefreshTests
{
    private static readonly (string Name, uint Value)[] Standard = [("MachineFlag", 1u), ("BothFlag", 1u), ("ModeNumber", 9u)];
    private static readonly (string Name, uint Value)[] DecimalEnabled = [("MachineFlag", 1u), ("BothFlag", 1u), ("ModeNumber", 5u)];
    private static readonly (string Name, uint Value)[] DecimalGone = [("MachineFlag", 1u), ("BothFlag", 1u)];

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Refresh_Reloads()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("refresh", "reloads");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-refresh-reloads");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-refresh-reloads", sandbox);

        run.GoTo(BasicsPath);
        run.SelectRow(MachineFlag);
        checks.Record("startState", run.StateOf(DecimalValues) == "Disabled", $"state of the decimal policy at the start='{run.StateOf(DecimalValues)}'.");
        checks.Record("startCount", run.Count("Refreshes") == 0, $"Refreshes at the start={run.Count("Refreshes")}.");

        // The file changes on disk: nothing shows before a refresh, classic reads on request only.
        sandbox.WritePolicyFile(GpoSide.Machine, DecimalEnabled);
        SortedDictionary<string, string> filesAfterWrite = sandbox.PolicyFileHashes();
        Thread.Sleep(1500);
        checks.Record("unchangedBeforeRefresh", run.StateOf(DecimalValues) == "Disabled" && run.Count("Refreshes") == 0, $"state='{run.StateOf(DecimalValues)}', Refreshes={run.Count("Refreshes")}.");

        run.Press(GpeditKeys.F5);
        checks.Record("firstRefresh", run.WaitCount("Refreshes", 1) && run.WaitReloadSettled(), $"Refreshes={run.Count("Refreshes")}, Reloading={run.Str("Reloading")}.");
        checks.Record(
            "firstState",
            ShellRun.WaitUntil(() => run.StateOf(DecimalValues) == "Enabled"),
            $"state of the decimal policy after the first F5='{run.StateOf(DecimalValues)}'; the file now holds the enabled number.");
        checks.Record("filesUntouched", filesAfterWrite.SequenceEqual(sandbox.PolicyFileHashes()), "Refresh wrote no policy file, comment file or version file.");
        checks.Record("keepsNode", run.NodePath().SequenceEqual(BasicsPath), $"node={run.Str("Node")}.");
        checks.Record("noProblems", run.Count("Problems") == 0, $"Problems={run.Count("Problems")}.");

        // A second F5 also works: the load can run twice (GPE-4).
        sandbox.WritePolicyFile(GpoSide.Machine, DecimalGone);
        run.Press(GpeditKeys.F5);
        checks.Record("secondRefresh", run.WaitCount("Refreshes", 2) && run.WaitReloadSettled(), $"Refreshes={run.Count("Refreshes")}.");
        checks.Record(
            "secondState",
            ShellRun.WaitUntil(() => run.StateOf(DecimalValues) == "Not configured") && run.StateOf(MachineFlag) == "Enabled",
            $"decimal='{run.StateOf(DecimalValues)}', machine flag='{run.StateOf(MachineFlag)}'.");

        // History after a reload: Back and Forward land on nodes of the new model and show its state.
        sandbox.WritePolicyFile(GpoSide.Machine, DecimalEnabled);
        run.Press(GpeditKeys.F5);
        checks.Record("thirdRefresh", run.WaitCount("Refreshes", 3) && run.WaitReloadSettled(), $"Refreshes={run.Count("Refreshes")}.");
        checks.Record("upFromBasics", run.InvokeButton("UpOneLevel") && run.WaitNode(OraclePath), $"node={run.Str("Node")}.");
        checks.Record("backToBasics", run.InvokeButton("Back") && run.WaitNode(BasicsPath), $"node after Back={run.Str("Node")}.");
        checks.Record(
            "backForward",
            ShellRun.WaitUntil(() => run.StateOf(DecimalValues) == "Enabled" && run.Rows().Count == 12),
            $"after Back the decimal policy reads '{run.StateOf(DecimalValues)}' with {run.Rows().Count} rows; the state of the new model.");
        checks.Record("forwardToOracle", run.InvokeButton("Forward") && run.WaitNode(OraclePath), $"node after Forward={run.Str("Node")}.");

        run.Measure("refreshes", run.Count("Refreshes"));
        run.WriteArtifacts("gpedit-refresh-reloads.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Refresh_Templates()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("refresh", "templates");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-refresh-templates");
        sandbox.SeedStandard();
        sandbox.UseTemplatesWithBadCases();
        using GpeditRun run = GpeditRun.Open("gpedit-refresh-templates", sandbox, shape: true);

        checks.Record("startClean", run.Count("Failures") == 0 && run.Count("Problems") == 0, $"Failures={run.Count("Failures")}, Problems={run.Count("Problems")}.");
        checks.Record("startShape", run.Shape().ReplaceLineEndings("\n") == GpeditTreeTests.ExpectedShape.ReplaceLineEndings("\n"), "the tree has the fixture shape at the start.");

        // A broken template appears while the editor runs: F5 reads the template folder again and reports it.
        sandbox.AddBadTemplateCase("MalformedXml");
        run.GoTo(Root, Computer);
        checks.Record("treeFocus", run.FocusPane("tree"), $"focused pane={run.Shell.FocusedPane()}.");
        run.Press(GpeditKeys.F5);
        checks.Record("refreshes", run.WaitCount("Refreshes", 1) && run.WaitReloadSettled(), $"Refreshes={run.Count("Refreshes")}.");
        checks.Record("failureReported", ShellRun.WaitUntil(() => run.Count("Failures") == 1), $"Failures={run.Count("Failures")}; the template folder was read again.");
        checks.Record(
            "problemListed",
            run.Str("ProblemList").Contains("TemplateFailure", StringComparison.Ordinal),
            $"ProblemList='{run.Str("ProblemList")}'.");
        checks.Record(
            "goodTreeUnchanged",
            run.Shape().ReplaceLineEndings("\n") == GpeditTreeTests.ExpectedShape.ReplaceLineEndings("\n"),
            "the broken template adds nothing to the tree after the reload.");
        run.GoTo(BasicsPath);
        checks.Record("basicsListed", run.Rows().Count == 12, $"rows in Basics={run.Rows().Count}.");

        run.Measure("failures", run.Count("Failures"));
        run.Measure("problems", run.Str("ProblemList"));
        run.WriteArtifacts("gpedit-refresh-templates.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Refresh_KeepsSelection()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("refresh", "selection");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-refresh-selection");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-refresh-selection", sandbox);

        run.GoTo(BasicsPath);
        run.SelectRow(BothFlag);
        string statusBefore = run.Shell.StatusText();
        string[] treeBefore = run.TreeItemNames();
        checks.Record("rowSelected", ShellRun.WaitUntil(() => run.DescriptionTitle == "Class both flag"), $"title='{run.DescriptionTitle}'.");

        run.Press(GpeditKeys.F5);
        checks.Record("reloaded", run.WaitCount("Refreshes", 1) && run.WaitReloadSettled(), $"Refreshes={run.Count("Refreshes")}.");
        checks.Record("node", run.NodePath().SequenceEqual(BasicsPath), $"node after F5={run.Str("Node")}; the root is not selected.");
        checks.Record("rowKept", ShellRun.WaitUntil(() => run.DescriptionTitle == "Class both flag"), $"title after F5='{run.DescriptionTitle}'.");
        checks.Record("rows", run.Rows().Count == 12, $"rows={run.Rows().Count}.");
        checks.Record("status", ShellRun.WaitUntil(() => run.Shell.StatusText() == statusBefore), $"status before='{statusBefore}', after='{run.Shell.StatusText()}'.");
        string[] treeAfter = run.TreeItemNames();
        checks.Record(
            "treeStaysExpanded",
            treeBefore.All(name => treeAfter.Contains(name)),
            $"tree items before={treeBefore.Length}, after={treeAfter.Length}; missing={string.Join("|", treeBefore.Where(n => !treeAfter.Contains(n)))}.");

        // The kept row is the target of the next command.
        int edits = Math.Max(0, run.Count("Edits"));
        _ = run.FocusPane("result");
        run.PressAlt(GpeditKeys.Enter);
        checks.Record(
            "rowIsTarget",
            ShellRun.WaitUntil(() => run.Count("Edits") == edits + 1 && run.Str("LastEdit") == BothFlagFull),
            $"Edits={run.Count("Edits")}, LastEdit='{run.Str("LastEdit")}'; expected {BothFlagFull}.");

        run.Measure("treeItemsBefore", treeBefore);
        run.Measure("treeItemsAfter", treeAfter);
        run.WriteArtifacts("gpedit-refresh-selection.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Refresh_VanishedNode()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("refresh", "vanished");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-refresh-vanished");
        sandbox.SeedStandard();
        sandbox.UseTemplatesWithBadCases();
        using GpeditRun run = GpeditRun.Open("gpedit-refresh-vanished", sandbox);

        // A deep history, then the template that holds every BWC Oracle category is removed and the editor refreshes.
        run.GoTo(BasicsPath);
        run.GoTo(Root, Computer, Admin, Oracle, "Machine only", "Machine nested");
        checks.Record("deepNode", run.NodePath().Length == 6, $"node={run.Str("Node")}.");
        sandbox.RemoveTemplate("BwcOracle");
        checks.Record("treeFocus", run.FocusPane("tree"), $"focused pane={run.Shell.FocusedPane()}.");
        run.Press(GpeditKeys.F5);
        checks.Record("reloaded", run.WaitCount("Refreshes", 1) && run.WaitReloadSettled(), $"Refreshes={run.Count("Refreshes")}.");

        // The selection falls back to the nearest ancestor that still exists, and the list is its list.
        string[] adminPath = [Root, Computer, Admin];
        checks.Record("nearestAncestor", ShellRun.WaitUntil(() => run.NodePath().SequenceEqual(adminPath)), $"node after the reload={run.Str("Node")}; expected {string.Join(" > ", adminPath)}.");
        checks.Record("listPage", run.Str("Page") == "list", $"Page={run.Str("Page")}.");
        List<GpeditRow> rows = run.Rows();
        checks.Record("rowsMatchReadout", rows.Count == run.Count("Rows"), $"realized rows={rows.Count}, readout rows={run.Count("Rows")}.");
        checks.Record(
            "rowsOfNewModel",
            rows.Any(r => r.Name == AllSettings) && rows.All(r => r.Name != Oracle),
            $"rows={string.Join("|", rows.Select(r => r.Name))}; BWC Oracle is gone and All Settings stays.");
        checks.Record("treeNoOracle", !run.TreeItemNames().Contains(Oracle), "the tree no longer has the BWC Oracle node.");

        // Back and Forward walk only nodes of the new model (GPE-5).
        bool historyExists = true;
        var visited = new List<string>();
        for (int step = 0; step < 8 && run.WaitButton("Back", enabled: true); step++)
        {
            if (!run.InvokeButton("Back"))
            {
                break;
            }

            _ = run.WaitReloadSettled();
            Thread.Sleep(300);
            string[] path = run.NodePath();
            visited.Add(string.Join(" > ", path));
            bool exists = path.Length > 0 && path[0] == Root && !path.Contains(Oracle) && !path.Contains(BasicsName) &&
                          (run.Str("Page") == "placeholder" || run.Rows().Count == run.Count("Rows"));
            historyExists &= exists;
        }

        checks.Record("historyExists", historyExists && visited.Count > 0, $"nodes reached by Back={string.Join(" | ", visited)}.");
        checks.Record("windowAlive", run.Shell.Find(GpeditRun.RootId) is not null && run.Count("Loaded") == 1, "the window still answers UI Automation with a loaded model.");

        run.Measure("visited", visited);
        run.WriteArtifacts("gpedit-refresh-vanished.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Refresh_Burst()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("refresh", "burst");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-refresh-burst");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-refresh-burst", sandbox);

        run.GoTo(BasicsPath);
        run.SelectRow(MachineFlag);
        sandbox.WritePolicyFile(GpoSide.Machine, DecimalEnabled);

        // Three F5 presses with no pause between them: the loads must not race.
        _ = ControlTestSupport.EnsureForeground(run.Shell.Session);
        RawKeyboard.Tap(GpeditKeys.F5);
        RawKeyboard.Tap(GpeditKeys.F5);
        RawKeyboard.Tap(GpeditKeys.F5);
        checks.Record(
            "settles",
            ShellRun.WaitUntil(() => run.Count("Reloading") == 0 && run.Count("Refreshes") >= 1 && run.Count("Loaded") == 1, TimeSpan.FromSeconds(40)),
            $"Reloading={run.Str("Reloading")}, Refreshes={run.Count("Refreshes")}, Loaded={run.Str("Loaded")}.");
        int refreshes = run.Count("Refreshes");
        checks.Record("countBounded", refreshes >= 1 && refreshes <= 3, $"Refreshes={refreshes} after three presses; a request during a load is merged or runs once, never more than asked.");
        checks.Record("lastStateWins", ShellRun.WaitUntil(() => run.StateOf(DecimalValues) == "Enabled"), $"decimal='{run.StateOf(DecimalValues)}'; the newest disk state is shown.");
        Thread.Sleep(1500);
        checks.Record("stillSettled", run.Count("Reloading") == 0 && run.Count("Refreshes") == refreshes, $"Reloading={run.Str("Reloading")}, Refreshes={run.Count("Refreshes")}, was {refreshes}; no load starts by itself.");
        checks.Record("node", run.NodePath().SequenceEqual(BasicsPath), $"node={run.Str("Node")}.");

        // After the reloads one Enter is one edit request (no handler was added twice).
        run.SelectRow(MachineFlag);
        int edits = Math.Max(0, run.Count("Edits"));
        run.Press(GpeditKeys.Enter);
        _ = ShellRun.WaitUntil(() => run.Count("Edits") >= edits + 1, TimeSpan.FromSeconds(5));
        Thread.Sleep(800);
        checks.Record("noDuplicateEdit", run.Count("Edits") == edits + 1, $"Edits={run.Count("Edits")}, expected {edits + 1}.");

        // One move is one history step: Up, then Back, lands on Basics and not on a duplicate of Oracle.
        checks.Record("up", run.InvokeButton("UpOneLevel") && run.WaitNode(OraclePath), $"node={run.Str("Node")}.");
        checks.Record("backIsPrevious", run.InvokeButton("Back") && run.WaitNode(BasicsPath), $"node after Back={run.Str("Node")}; a duplicated selection handler would add a second Oracle entry.");

        run.Measure("refreshes", refreshes);
        run.WriteArtifacts("gpedit-refresh-burst.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Refresh_Failure()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("refresh", "failure");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-refresh-failure");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-refresh-failure", sandbox);

        run.GoTo(BasicsPath);
        run.SelectRow(MachineFlag);
        checks.Record("startState", run.StateOf(MachineFlag) == "Enabled" && run.Count("Problems") == 0, $"state='{run.StateOf(MachineFlag)}', Problems={run.Count("Problems")}.");

        // The policy file becomes unreadable: the reload reports it, the window keeps working and keeps its node.
        sandbox.WriteUnreadablePolicyFile(GpoSide.Machine);
        run.Press(GpeditKeys.F5);
        checks.Record("reloads", run.WaitCount("Refreshes", 1) && run.WaitReloadSettled(), $"Refreshes={run.Count("Refreshes")}.");
        checks.Record(
            "problemReported",
            ShellRun.WaitUntil(() => run.Count("Problems") >= 1 && run.Str("ProblemList").Contains("PolicyFile", StringComparison.Ordinal)),
            $"Problems={run.Count("Problems")}, list='{run.Str("ProblemList")}'.");
        checks.Record("statesBlank", ShellRun.WaitUntil(() => run.Rows().All(r => (r.Cells.ElementAtOrDefault(1) ?? "?").Length == 0)), "an unreadable file shows no state.");
        checks.Record("keepsNode", run.NodePath().SequenceEqual(BasicsPath) && run.Rows().Count == 12, $"node={run.Str("Node")}, rows={run.Rows().Count}.");
        checks.Record("windowAlive", run.Shell.Session.IsEnabled && run.Shell.Find(GpeditRun.RootId) is not null, "the window answers and is enabled.");
        run.WriteArtifacts("gpedit-refresh-failure.png");

        // The file is fixed: the next Refresh recovers.
        sandbox.WritePolicyFile(GpoSide.Machine, Standard);
        run.Press(GpeditKeys.F5);
        checks.Record("recovers", run.WaitCount("Refreshes", 2) && run.WaitReloadSettled(), $"Refreshes={run.Count("Refreshes")}.");
        checks.Record(
            "recoveredState",
            ShellRun.WaitUntil(() => run.Count("Problems") == 0 && run.StateOf(MachineFlag) == "Enabled" && run.StateOf(DecimalValues) == "Disabled"),
            $"Problems={run.Count("Problems")}, machine='{run.StateOf(MachineFlag)}', decimal='{run.StateOf(DecimalValues)}'.");

        run.Measure("problems", run.Str("ProblemList"));
        run.WriteArtifacts("gpedit-refresh-failure-recovered.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Refresh_WhileDialogOpen()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("refresh", "dialog");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-refresh-dialog");
        sandbox.DialogEnabled = true;
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-refresh-dialog", sandbox);

        run.GoTo(BasicsPath);
        using GpeditDialog? dialog = GpeditDialog.Open(run, MachineFlag);
        checks.Record("opens", dialog is not null, "Enter on a policy row opens the setting dialog.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        string policyBefore = dialog.Read("Policy");
        sandbox.WritePolicyFile(GpoSide.Machine, DecimalEnabled);

        // A Refresh request while the dialog is open: the dialog keeps its model and stays open. The editor may refuse the
        // request or defer it to the close of the dialog; both are fine, a swap under the dialog is not.
        bool invoked = run.InvokeButton("Refresh", foreground: false);
        Thread.Sleep(2000);
        checks.Record("dialogStaysOpen", dialog.Root is not null && dialog.Read("Policy") == policyBefore && policyBefore.Length > 0, $"policy before='{policyBefore}', now='{dialog.Read("Policy")}', invoked={invoked}.");
        checks.Record("ownerStaysDisabled", !run.Shell.Session.IsEnabled, "the main window stays disabled while the dialog is open.");
        run.Measure("refreshInvokedWhileOpen", invoked);

        _ = dialog.Invoke(GpeditDialog.Cancel);
        checks.Record("closes", dialog.WaitClosed(), "Cancel closes the dialog.");
        checks.Record("ownerEnabledAgain", ShellRun.WaitUntil(() => run.Shell.Session.IsEnabled), "the main window is enabled again.");
        checks.Record("settledAfterClose", run.WaitReloadSettled(), $"Reloading={run.Str("Reloading")}, Loaded={run.Str("Loaded")}.");

        // After the dialog is closed Refresh works and shows the new file.
        _ = run.FocusPane("result");
        run.Press(GpeditKeys.F5);
        checks.Record(
            "refreshAfterClose",
            ShellRun.WaitUntil(() => run.Count("Refreshes") >= 1 && run.Count("Reloading") == 0, TimeSpan.FromSeconds(30)),
            $"Refreshes={run.Count("Refreshes")}, Reloading={run.Str("Reloading")}.");
        checks.Record("newState", ShellRun.WaitUntil(() => run.StateOf(DecimalValues) == "Enabled"), $"decimal='{run.StateOf(DecimalValues)}'.");
        checks.Record("keepsNode", run.NodePath().SequenceEqual(BasicsPath), $"node={run.Str("Node")}.");

        run.WriteArtifacts("gpedit-refresh-dialog.png");
        checks.AssertAllPassed();
    }
}

using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Bwc.E2E.Gpedit.Commands.GpeditNames;

namespace Bwc.E2E.Gpedit.Commands;

/// <summary>
/// Key tests of the Local Group Policy Editor: F5, F1, Enter, Alt+Enter, Delete, Backspace, Alt+Left,
/// Alt+Right and Alt+Up, in the result list and in the console tree, against the captures F/Keys_List
/// and F/Keys_Tree (ACC-004 to ACC-048). F1 and the help command are asserted only through the E2E help
/// counter. The failure modes are in docs/failure-modes/gpedit-commands.md, section 7. Every test
/// writes artifacts/e2e/visual/gpedit/gpedit-&lt;name&gt;.json and a picture, and its checks to the E2E
/// result artifact.
/// </summary>
[TestClass]
public sealed class GpeditKeysTests
{
    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Keys_F5()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("keys", "f5");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-keys-f5");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-keys-f5", sandbox);

        // In the list: F5 reloads, opens no window and no menu, and keeps the node and the selected row (ACC-044).
        run.GoTo(BasicsPath);
        run.SelectRow(MachineFlag);
        checks.Record("listFocus", run.Shell.FocusedPane() == "result", $"focused pane={run.Shell.FocusedPane()}.");
        int edits = Math.Max(0, run.Count("Edits"));
        int refreshes = Math.Max(0, run.Count("Refreshes"));
        run.Press(GpeditKeys.F5);
        checks.Record("listReloads", run.WaitCount("Refreshes", refreshes + 1) && run.WaitReloadSettled(), $"Refreshes={run.Count("Refreshes")}, Reloading={run.Str("Reloading")}.");
        checks.Record("listKeepsNode", run.NodePath().SequenceEqual(BasicsPath), $"node after F5={run.Str("Node")}.");
        checks.Record("listKeepsRow", ShellRun.WaitUntil(() => run.DescriptionTitle == "Class machine flag"), $"the selected row is described as '{run.DescriptionTitle}'.");
        checks.Record(
            "listNoWindowNoMenu",
            run.Shell.Session.IsEnabled && run.MenuEntries(GpeditCommandIds.MenuPrefix).Count == 0 && run.MenuEntries("ContextMenuCommand_").Count == 0,
            "no dialog is open and no menu is shown after F5.");
        checks.Record("listNoEdit", run.Count("Edits") == edits, $"Edits={run.Count("Edits")}, expected {edits}; F5 edits nothing.");
        checks.Record("listLastCommand", run.Str("LastCommand") == "Refresh", $"LastCommand='{run.Str("LastCommand")}'.");

        // In the tree: the same, and the selection stays (ACC-027).
        checks.Record("treeFocus", run.FocusPane("tree"), $"focused pane={run.Shell.FocusedPane()}.");
        run.Press(GpeditKeys.F5);
        checks.Record("treeReloads", run.WaitCount("Refreshes", refreshes + 2) && run.WaitReloadSettled(), $"Refreshes={run.Count("Refreshes")}.");
        checks.Record("treeKeepsNode", run.NodePath().SequenceEqual(BasicsPath), $"node after F5 in the tree={run.Str("Node")}.");
        checks.Record("treeNoWindow", run.Shell.Session.IsEnabled && run.MenuEntries(GpeditCommandIds.MenuPrefix).Count == 0, "no dialog and no menu after F5 in the tree.");

        run.Measure("refreshes", run.Count("Refreshes"));
        run.WriteArtifacts("gpedit-keys-f5.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Keys_Edit()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("keys", "edit");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-keys-edit");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-keys-edit", sandbox);

        // Enter on a policy row opens the setting dialog (ACC-048); with the dialog off the editor counts the request.
        run.GoTo(BasicsPath);
        run.SelectRow(MachineFlag);
        int edits = Math.Max(0, run.Count("Edits"));
        run.Press(GpeditKeys.Enter);
        checks.Record(
            "enter",
            ShellRun.WaitUntil(() => run.Count("Edits") == edits + 1 && run.Str("LastEdit") == MachineFlagFull),
            $"Edits={run.Count("Edits")}, LastEdit='{run.Str("LastEdit")}'; expected {MachineFlagFull}.");

        // Alt+Enter on a policy row is the same command, for the row that is selected now.
        run.SelectRow(BothFlag);
        edits = Math.Max(0, run.Count("Edits"));
        run.PressAlt(GpeditKeys.Enter);
        checks.Record(
            "altEnter",
            ShellRun.WaitUntil(() => run.Count("Edits") == edits + 1 && run.Str("LastEdit") == BothFlagFull),
            $"Edits={run.Count("Edits")}, LastEdit='{run.Str("LastEdit")}'; expected {BothFlagFull}.");
        checks.Record("altEnterLastCommand", run.Str("LastCommand") == "Edit", $"LastCommand='{run.Str("LastCommand")}'.");

        // Alt+Enter on a folder row does nothing; Enter on it opens the folder.
        run.GoTo(OraclePath);
        run.SelectRow(BasicsName);
        edits = Math.Max(0, run.Count("Edits"));
        run.PressAlt(GpeditKeys.Enter);
        Thread.Sleep(800);
        checks.Record("altEnterFolder", run.Count("Edits") == edits && run.NodePath().SequenceEqual(OraclePath), $"Edits={run.Count("Edits")}, node={run.Str("Node")}.");
        run.Press(GpeditKeys.Enter);
        checks.Record("enterFolder", run.WaitNode(BasicsPath), $"node after Enter on a folder row={run.Str("Node")}.");
        checks.Record("enterFolderNoEdit", run.Count("Edits") == edits, $"Edits={run.Count("Edits")}, expected {edits}.");

        run.WriteArtifacts("gpedit-keys-edit.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Keys_WrongPane()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("keys", "wrongpane");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-keys-wrongpane");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-keys-wrongpane", sandbox);

        // A policy row is selected in the list, then the focus moves to the tree: the edit keys must not reach the row.
        run.GoTo(BasicsPath);
        run.SelectRow(MachineFlag);
        checks.Record("treeFocus", run.FocusPane("tree"), $"focused pane={run.Shell.FocusedPane()}.");
        int edits = Math.Max(0, run.Count("Edits"));
        run.PressAlt(GpeditKeys.Enter);
        Thread.Sleep(800);
        checks.Record("altEnterInTree", run.Count("Edits") == edits, $"Edits={run.Count("Edits")}, expected {edits}; Alt+Enter in the tree edits nothing.");
        run.Press(GpeditKeys.Delete);
        Thread.Sleep(500);
        checks.Record("deleteInTree", run.Count("Edits") == edits && run.NodePath().SequenceEqual(BasicsPath), $"Edits={run.Count("Edits")}, node={run.Str("Node")}.");

        // The same key in the list, on the same row, edits it: the key works, so the tree result above is a refusal.
        checks.Record("listFocus", run.FocusPane("result"), $"focused pane={run.Shell.FocusedPane()}.");
        run.PressAlt(GpeditKeys.Enter);
        checks.Record(
            "altEnterInList",
            ShellRun.WaitUntil(() => run.Count("Edits") == edits + 1 && run.Str("LastEdit") == MachineFlagFull),
            $"Edits={run.Count("Edits")}, LastEdit='{run.Str("LastEdit")}'; expected {MachineFlagFull}.");

        run.WriteArtifacts("gpedit-keys-wrongpane.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Keys_F1()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("keys", "f1");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-keys-f1");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-keys-f1", sandbox);

        // F1 fires the Help command once in any pane (ACC-002). What Help shows is not asserted: only that it fired.
        run.GoTo(BasicsPath);
        run.SelectRow(MachineFlag);
        int edits = Math.Max(0, run.Count("Edits"));
        int helps = Math.Max(0, run.Count("HelpRequests"));
        run.Press(GpeditKeys.F1);
        checks.Record("listF1", run.WaitCount("HelpRequests", helps + 1), $"HelpRequests={run.Count("HelpRequests")}, expected {helps + 1}.");
        checks.Record("listLastCommand", run.Str("LastCommand") == "Help", $"LastCommand='{run.Str("LastCommand")}'.");

        checks.Record("treeFocus", run.FocusPane("tree"), $"focused pane={run.Shell.FocusedPane()}.");
        run.Press(GpeditKeys.F1);
        checks.Record("treeF1", run.WaitCount("HelpRequests", helps + 2), $"HelpRequests={run.Count("HelpRequests")}, expected {helps + 2}.");

        // One press is one request: no repeat, and the key edits nothing.
        Thread.Sleep(800);
        checks.Record("exactlyTwo", run.Count("HelpRequests") == helps + 2, $"HelpRequests={run.Count("HelpRequests")} after a second wait.");
        checks.Record("noEdit", run.Count("Edits") == edits, $"Edits={run.Count("Edits")}, expected {edits}.");
        checks.Record("windowEnabled", run.Shell.Session.IsEnabled, "the main window is still enabled after F1.");

        run.WriteArtifacts("gpedit-keys-f1.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Keys_Delete()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("keys", "delete");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-keys-delete");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-keys-delete", sandbox);

        // Classic has no Delete entry in any menu and no capture shows a window or a write for it: Delete does nothing.
        run.GoTo(BasicsPath);
        run.SelectRow(MachineFlag);
        SortedDictionary<string, string> filesBefore = sandbox.PolicyFileHashes();
        string stateBefore = run.StateOf(MachineFlag);
        string[] rowsBefore = run.Rows().Select(r => r.Name).ToArray();
        int edits = Math.Max(0, run.Count("Edits"));
        string commandBefore = run.Str("LastCommand");

        run.Press(GpeditKeys.Delete);
        Thread.Sleep(1000);
        checks.Record("listNoEdit", run.Count("Edits") == edits && run.Str("LastCommand") == commandBefore, $"Edits={run.Count("Edits")}, LastCommand='{run.Str("LastCommand")}'.");
        checks.Record("listStateKept", run.StateOf(MachineFlag) == stateBefore, $"state before='{stateBefore}', after='{run.StateOf(MachineFlag)}'.");
        checks.Record("listRowsKept", run.Rows().Select(r => r.Name).SequenceEqual(rowsBefore), "the rows are the same after Delete.");
        checks.Record("listNoWindow", run.Shell.Session.IsEnabled && run.NodePath().SequenceEqual(BasicsPath), $"window enabled={run.Shell.Session.IsEnabled}, node={run.Str("Node")}.");

        checks.Record("treeFocus", run.FocusPane("tree"), $"focused pane={run.Shell.FocusedPane()}.");
        run.Press(GpeditKeys.Delete);
        Thread.Sleep(1000);
        checks.Record("treeNoChange", run.NodePath().SequenceEqual(BasicsPath) && run.Count("Edits") == edits, $"node={run.Str("Node")}, Edits={run.Count("Edits")}.");
        checks.Record("treeRowsKept", run.Rows().Select(r => r.Name).SequenceEqual(rowsBefore), "the rows are the same after Delete in the tree.");

        SortedDictionary<string, string> filesAfter = sandbox.PolicyFileHashes();
        checks.Record("filesUntouched", filesBefore.SequenceEqual(filesAfter), "no policy file of the sandbox changed.");

        run.Measure("filesBefore", filesBefore);
        run.Measure("filesAfter", filesAfter);
        run.WriteArtifacts("gpedit-keys-delete.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Keys_History()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("keys", "history");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-keys-history");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-keys-history", sandbox);

        // The history is Root, Computer, Administrative Templates, BWC Oracle. Alt+Left and Alt+Right walk it from the tree.
        run.GoTo(OraclePath);
        checks.Record("treeFocus", run.FocusPane("tree"), $"focused pane={run.Shell.FocusedPane()}.");
        run.PressAlt(GpeditKeys.Left);
        checks.Record("altLeft1", run.WaitNode(Root, Computer, Admin), $"node after Alt+Left={run.Str("Node")}.");
        run.PressAlt(GpeditKeys.Left);
        checks.Record("altLeft2", run.WaitNode(Root, Computer), $"node after the second Alt+Left={run.Str("Node")}.");
        run.PressAlt(GpeditKeys.Right);
        checks.Record("altRight", run.WaitNode(Root, Computer, Admin), $"node after Alt+Right={run.Str("Node")}.");
        checks.Record("altRightLastCommand", run.Str("LastCommand") == "Forward", $"LastCommand='{run.Str("LastCommand")}' after Alt+Right.");

        // Backspace in the tree selects the parent (ACC-024). The last node is User Configuration, whose parent is the root
        // and whose history predecessor is another node, so the two meanings differ.
        run.GoTo(BasicsPath);
        AutomationElement? user = run.TreeItem(User);
        checks.Record("userItem", user is not null, "the User Configuration tree item exists.");
        user?.Click();
        checks.Record("userSelected", run.WaitNode(Root, User), $"node after the click={run.Str("Node")}.");
        checks.Record("treeFocusAgain", run.FocusPane("tree"), $"focused pane={run.Shell.FocusedPane()}.");
        run.Press(GpeditKeys.Back);
        checks.Record("backspaceParent", run.WaitNode(Root), $"node after Backspace={run.Str("Node")}; expected the parent, not the history predecessor.");

        // Alt+Up with the focus in the result pane changes nothing (ACC-032).
        run.GoTo(OraclePath);
        checks.Record("resultFocus", run.FocusPane("result"), $"focused pane={run.Shell.FocusedPane()}.");
        run.PressAlt(GpeditKeys.Up);
        Thread.Sleep(800);
        checks.Record("altUpResult", run.NodePath().SequenceEqual(OraclePath), $"node after Alt+Up in the result pane={run.Str("Node")}.");

        run.WriteArtifacts("gpedit-keys-history.png");
        checks.AssertAllPassed();
    }
}

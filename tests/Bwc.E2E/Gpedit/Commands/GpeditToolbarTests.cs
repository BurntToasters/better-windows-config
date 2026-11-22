using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Bwc.E2E.Gpedit.Commands.GpeditNames;

namespace Bwc.E2E.Gpedit.Commands;

/// <summary>
/// Toolbar tests of the Local Group Policy Editor: the enabled state of Back, Forward, Up One Level,
/// Show/Hide Console Tree, Properties, Refresh and Help per node kind and with a row selected
/// (F/Toolbar_States, TOOL-004 to TOOL-016), the history buttons, and what each command does. The
/// failure modes are in docs/failure-modes/gpedit-commands.md, section 4. Every test writes
/// artifacts/e2e/visual/gpedit/gpedit-&lt;name&gt;.json and a picture, and its checks to the E2E result
/// artifact.
/// </summary>
[TestClass]
public sealed class GpeditToolbarTests
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
    public void Gpedit_Toolbar_States()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("toolbar", "states");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-toolbar-states");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-toolbar-states", sandbox);

        // Classic: Up is enabled at every node that has a parent, the tree toggle and Help at every node, and the states do
        // not depend on the node kind. Properties and Refresh are BWC buttons: Refresh is always enabled, Properties only
        // while a policy row is selected, because its command is Edit.
        (string[] Path, string Id, bool Up)[] kinds =
        [
            ([Root], "root", false),
            ([Root, Computer], "computerConfiguration", true),
            ([Root, Computer, "Software Settings"], "softwarePlaceholder", true),
            ([Root, Computer, Admin], "administrativeTemplates", true),
            (OraclePath, "category", true),
            (BasicsPath, "categoryWithPolicies", true),
            ([Root, Computer, Admin, AllSettings], "allSettings", true),
            ([Root, User], "userConfiguration", true),
            ([Root, User, Admin, Oracle, BasicsName], "userCategoryWithPolicies", true),
        ];
        foreach ((string[] path, string id, bool up) in kinds)
        {
            run.GoTo(path);
            _ = run.WaitNode(path);
            CheckStates(run, checks, id, new Dictionary<string, bool>
            {
                ["UpOneLevel"] = up,
                ["ShowHideConsoleTree"] = true,
                ["Properties"] = false,
                ["Refresh"] = true,
                ["Help"] = true,
            });
        }

        // A policy row selected: Properties is enabled and the other states are unchanged (TOOL-016).
        run.GoTo(BasicsPath);
        run.SelectRow(MachineFlag);
        CheckStates(run, checks, "policyRow", new Dictionary<string, bool>
        {
            ["UpOneLevel"] = true,
            ["ShowHideConsoleTree"] = true,
            ["Properties"] = true,
            ["Refresh"] = true,
            ["Help"] = true,
        });
        run.WriteArtifacts("gpedit-toolbar-states-row.png");

        // A folder row is not a policy, and a node change clears the row selection.
        _ = run.InvokeButton("UpOneLevel");
        _ = run.WaitNode(OraclePath);
        CheckStates(run, checks, "afterUp", new Dictionary<string, bool> { ["Properties"] = false, ["Refresh"] = true, ["Help"] = true });
        run.SelectRow(BasicsName);
        CheckStates(run, checks, "folderRow", new Dictionary<string, bool> { ["Properties"] = false, ["Refresh"] = true, ["Help"] = true });

        run.WriteArtifacts("gpedit-toolbar-states.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Toolbar_History()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("toolbar", "history");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-toolbar-history");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-toolbar-history", sandbox);

        CheckStates(run, checks, "start", new Dictionary<string, bool> { ["Back"] = false, ["Forward"] = false, ["UpOneLevel"] = false });

        run.GoTo(Root, Computer);
        CheckStates(run, checks, "afterFirstMove", new Dictionary<string, bool> { ["Back"] = true, ["Forward"] = false, ["UpOneLevel"] = true });
        run.GoTo(Root, Computer, Admin);

        // Back twice to the root, with Forward turning on; Forward twice again to the start node.
        checks.Record("back1", run.InvokeButton("Back") && run.WaitNode(Root, Computer), $"node after Back={run.Str("Node")}.");
        checks.Record("back1LastCommand", run.Str("LastCommand") == "Back", $"LastCommand='{run.Str("LastCommand")}'.");
        CheckStates(run, checks, "afterBack1", new Dictionary<string, bool> { ["Back"] = true, ["Forward"] = true });
        checks.Record("back2", run.InvokeButton("Back") && run.WaitNode(Root), $"node after the second Back={run.Str("Node")}.");
        CheckStates(run, checks, "atStart", new Dictionary<string, bool> { ["Back"] = false, ["Forward"] = true, ["UpOneLevel"] = false });
        checks.Record("forward1", run.InvokeButton("Forward") && run.WaitNode(Root, Computer), $"node after Forward={run.Str("Node")}.");
        checks.Record("forward1LastCommand", run.Str("LastCommand") == "Forward", $"LastCommand='{run.Str("LastCommand")}'.");
        checks.Record("forward2", run.InvokeButton("Forward") && run.WaitNode(Root, Computer, Admin), $"node after the second Forward={run.Str("Node")}.");
        CheckStates(run, checks, "atEnd", new Dictionary<string, bool> { ["Back"] = true, ["Forward"] = false });

        // A new selection after Back ends the forward part, as in a browser.
        checks.Record("back3", run.InvokeButton("Back") && run.WaitNode(Root, Computer), $"node after Back={run.Str("Node")}.");
        run.GoTo(Root, Computer, "Software Settings");
        CheckStates(run, checks, "newSelection", new Dictionary<string, bool> { ["Back"] = true, ["Forward"] = false });

        run.WriteArtifacts("gpedit-toolbar-history.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Toolbar_Commands()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("toolbar", "commands");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-toolbar-commands");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-toolbar-commands", sandbox);

        // Properties edits the selected row.
        run.GoTo(BasicsPath);
        run.SelectRow(BothFlag);
        run.SelectRow(MachineFlag);
        int edits = Math.Max(0, run.Count("Edits"));
        bool invoked = run.InvokeButton("Properties");
        checks.Record(
            "propertiesEdits",
            invoked && ShellRun.WaitUntil(() => run.Count("Edits") == edits + 1 && run.Str("LastEdit") == MachineFlagFull),
            $"invoked={invoked}, Edits={run.Count("Edits")}, LastEdit='{run.Str("LastEdit")}'; expected {MachineFlagFull}.");
        checks.Record("propertiesLastCommand", run.Str("LastCommand") == "Edit", $"LastCommand='{run.Str("LastCommand")}'.");

        // Refresh reloads and keeps the node.
        int refreshes = Math.Max(0, run.Count("Refreshes"));
        invoked = run.InvokeButton("Refresh");
        checks.Record(
            "refreshReloads",
            invoked && run.WaitCount("Refreshes", refreshes + 1) && run.WaitReloadSettled(),
            $"invoked={invoked}, Refreshes={run.Count("Refreshes")}, Reloading={run.Str("Reloading")}.");
        checks.Record("refreshKeepsNode", run.NodePath().SequenceEqual(BasicsPath), $"node after Refresh={run.Str("Node")}.");
        checks.Record("refreshLastCommand", run.Str("LastCommand") == "Refresh", $"LastCommand='{run.Str("LastCommand")}'.");

        // Help fires the Help command once.
        int helps = Math.Max(0, run.Count("HelpRequests"));
        invoked = run.InvokeButton("Help");
        checks.Record("helpFires", invoked && run.WaitCount("HelpRequests", helps + 1), $"invoked={invoked}, HelpRequests={run.Count("HelpRequests")}.");
        checks.Record("helpLastCommand", run.Str("LastCommand") == "Help", $"LastCommand='{run.Str("LastCommand")}'.");

        // Up One Level selects the parent.
        invoked = run.InvokeButton("UpOneLevel");
        checks.Record("upSelectsParent", invoked && run.WaitNode(OraclePath), $"node after Up={run.Str("Node")}.");
        checks.Record("upLastCommand", run.Str("LastCommand") == "Up", $"LastCommand='{run.Str("LastCommand")}'.");

        // Show/Hide Console Tree hides and shows the tree.
        invoked = run.InvokeButton("ShowHideConsoleTree");
        checks.Record("treeHides", invoked && ShellRun.WaitUntil(() => run.Shell.IsHidden(ShellRun.TreePaneId)), $"tree hidden={run.Shell.IsHidden(ShellRun.TreePaneId)}.");
        invoked = run.InvokeButton("ShowHideConsoleTree");
        checks.Record("treeShows", invoked && ShellRun.WaitUntil(() => !run.Shell.IsHidden(ShellRun.TreePaneId)), $"tree hidden={run.Shell.IsHidden(ShellRun.TreePaneId)}.");

        run.Measure("edits", run.Count("Edits"));
        run.Measure("refreshes", run.Count("Refreshes"));
        run.Measure("helpRequests", run.Count("HelpRequests"));
        run.WriteArtifacts("gpedit-toolbar-commands.png");
        checks.AssertAllPassed();
    }

    private static void CheckStates(GpeditRun run, GpeditChecks checks, string stage, Dictionary<string, bool> expected)
    {
        foreach ((string button, bool enabled) in expected)
        {
            bool actual = run.WaitButton(button, enabled);
            checks.Record($"{stage}.{button}", actual, $"{button} enabled={run.Shell.ButtonEnabled(button)}, expected {enabled}.");
        }
    }
}

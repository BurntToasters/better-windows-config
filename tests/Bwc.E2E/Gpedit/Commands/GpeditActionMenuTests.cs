using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Bwc.E2E.Gpedit.Commands.GpeditNames;

namespace Bwc.E2E.Gpedit.Commands;

/// <summary>
/// Action menu tests of the Local Group Policy Editor: the entries per node kind and with a policy row
/// selected, compared with the captures F/Menus_Computer and F/Menus_User by command role (Edit,
/// Refresh, Help), never by Microsoft text, and what each entry does. Filter, Add/Remove Templates and
/// Export List entries belong to other pieces and are not asserted. The failure modes are in
/// docs/failure-modes/gpedit-commands.md, sections 1 and 2. Every test writes
/// artifacts/e2e/visual/gpedit/gpedit-&lt;name&gt;.json and a picture, and its checks to the E2E result
/// artifact.
/// </summary>
[TestClass]
public sealed class GpeditActionMenuTests
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
    public void Gpedit_Menu_Action_NodeKinds()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("menu", "action.nodes");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-menu-action-nodes");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-menu-action-nodes", sandbox);

        // Both configuration sides give the same menu at every node kind (CTX-008); none has a policy row selected.
        (string[] Path, string Id)[] kinds =
        [
            ([Root], "root"),
            ([Root, Computer], "computerConfiguration"),
            ([Root, Computer, "Software Settings"], "softwarePlaceholder"),
            ([Root, Computer, "Windows Settings"], "windowsSettings"),
            ([Root, Computer, Admin], "administrativeTemplates"),
            (OraclePath, "category"),
            (BasicsPath, "categoryWithPolicies"),
            ([Root, Computer, Admin, AllSettings], "allSettings"),
            ([Root, User], "userConfiguration"),
            ([Root, User, Admin], "userAdministrativeTemplates"),
            ([Root, User, Admin, Oracle, BasicsName], "userCategoryWithPolicies"),
        ];
        var listings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string[] path, string id) in kinds)
        {
            run.GoTo(path);
            _ = run.WaitNode(path);
            List<GpeditMenuEntry> entries = run.OpenTopMenu(GpeditCommandIds.ActionMenu);
            GpeditCommandRun.CheckMenu(checks, id, entries, expectEdit: false, string.Join(" > ", path));
            listings[id] = string.Join("|", entries.Select(e => e.Name));
            checks.Record(id + ".closes", run.CloseMenus(), "Escape closes the Action popup.");
        }

        // The access key of the Action menu opens it (classic: A).
        run.PressAlt((ushort)'A');
        checks.Record("altAOpens", ShellRun.WaitUntil(() => run.MenuEntries(GpeditCommandIds.MenuPrefix).Count > 0, TimeSpan.FromSeconds(5)), "Alt+A opens the Action popup.");
        _ = run.CloseMenus();

        run.Measure("entriesByNode", listings);
        run.WriteArtifacts("gpedit-menu-action-nodes.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Menu_Action_PolicyRow()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("menu", "action.row");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-menu-action-row");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-menu-action-row", sandbox);

        run.GoTo(BasicsPath);
        run.SelectRow(MachineFlag);
        List<GpeditMenuEntry> entries = run.OpenTopMenu(GpeditCommandIds.ActionMenu);
        GpeditCommandRun.CheckMenu(checks, "edit", entries, expectEdit: true, "a policy row selected");
        _ = run.CloseMenus();
        run.WriteArtifacts("gpedit-menu-action-row.png");

        // The node changes and the selection is gone: the menu is built again and has no Edit (no stale selection).
        checks.Record("upRuns", run.InvokeButton("UpOneLevel") && run.WaitNode(OraclePath), $"Up One Level leaves Basics; node={run.Str("Node")}.");
        entries = run.OpenTopMenu(GpeditCommandIds.ActionMenu);
        GpeditCommandRun.CheckMenu(checks, "staleSelection", entries, expectEdit: false, "the node changed after a policy row was selected");
        _ = run.CloseMenus();

        // A folder row is selected: still no Edit.
        run.SelectRow(BasicsName);
        entries = run.OpenTopMenu(GpeditCommandIds.ActionMenu);
        GpeditCommandRun.CheckMenu(checks, "folderRow", entries, expectEdit: false, "a folder row selected");
        _ = run.CloseMenus();

        // Back on a policy row: the menu has Edit again, on the user side too.
        run.GoTo(BasicsPath);
        run.SelectRow(BothFlag);
        entries = run.OpenTopMenu(GpeditCommandIds.ActionMenu);
        GpeditCommandRun.CheckMenu(checks, "again", entries, expectEdit: true, "a policy row selected again");
        _ = run.CloseMenus();

        run.GoTo(Root, User, Admin, Oracle, BasicsName);
        run.SelectRow("Class user flag setting");
        entries = run.OpenTopMenu(GpeditCommandIds.ActionMenu);
        GpeditCommandRun.CheckMenu(checks, "userRow", entries, expectEdit: true, "a user policy row selected");
        _ = run.CloseMenus();

        run.WriteArtifacts("gpedit-menu-action-row-user.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Menu_Action_Commands()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("menu", "action.commands");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-menu-action-commands");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-menu-action-commands", sandbox);

        // Edit acts on the selected row, not on a row selected before.
        run.GoTo(BasicsPath);
        run.SelectRow(BothFlag);
        run.SelectRow(DecimalValues);
        int edits = Math.Max(0, run.Count("Edits"));
        bool invoked = run.InvokeMenuEntry(GpeditCommandIds.ActionMenu, GpeditCommandIds.Edit);
        checks.Record(
            "editRuns",
            invoked && ShellRun.WaitUntil(() => run.Count("Edits") == edits + 1 && run.Str("LastEdit") == DecimalValuesFull),
            $"invoked={invoked}, Edits={run.Count("Edits")}, LastEdit='{run.Str("LastEdit")}'; expected {DecimalValuesFull}.");
        checks.Record("editLastCommand", run.Str("LastCommand") == "Edit", $"LastCommand='{run.Str("LastCommand")}'.");

        // Refresh reloads and keeps the node and the selected row.
        run.SelectRow(BothFlag);
        int refreshes = Math.Max(0, run.Count("Refreshes"));
        invoked = run.InvokeMenuEntry(GpeditCommandIds.ActionMenu, GpeditCommandIds.Refresh);
        checks.Record(
            "refreshRuns",
            invoked && run.WaitCount("Refreshes", refreshes + 1) && run.WaitReloadSettled(),
            $"invoked={invoked}, Refreshes={run.Count("Refreshes")}, Reloading={run.Str("Reloading")}.");
        checks.Record("refreshKeepsNode", run.NodePath().SequenceEqual(BasicsPath), $"node after Refresh={run.Str("Node")}.");
        checks.Record(
            "refreshKeepsRow",
            ShellRun.WaitUntil(() => run.DescriptionTitle == "Class both flag"),
            $"the selected row after Refresh is described as '{run.DescriptionTitle}'.");
        checks.Record("refreshLastCommand", run.Str("LastCommand") == "Refresh", $"LastCommand='{run.Str("LastCommand")}'.");

        // Help fires the Help command once.
        int helps = Math.Max(0, run.Count("HelpRequests"));
        invoked = run.InvokeMenuEntry(GpeditCommandIds.ActionMenu, GpeditCommandIds.Help);
        checks.Record("helpRuns", invoked && run.WaitCount("HelpRequests", helps + 1), $"invoked={invoked}, HelpRequests={run.Count("HelpRequests")}.");
        checks.Record("helpOpensNoDialog", run.Shell.Session.IsEnabled, "the main window is still enabled after Help.");

        run.Measure("edits", run.Count("Edits"));
        run.Measure("refreshes", run.Count("Refreshes"));
        run.Measure("helpRequests", run.Count("HelpRequests"));
        run.WriteArtifacts("gpedit-menu-action-commands.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Menu_Help_Topics()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("menu", "help.topics");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-menu-help-topics");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-menu-help-topics", sandbox);

        // Help Topics is enabled in classic at every node (MENU-033) and runs the same command as F1.
        List<GpeditMenuEntry> entries = run.OpenTopMenu(GpeditCommandIds.HelpMenu);
        GpeditMenuEntry? topics = entries.FirstOrDefault(e => e.Id == GpeditCommandIds.HelpTopics);
        checks.Record(
            "enabled",
            topics is { Enabled: true },
            $"Help Topics present and enabled; entries={string.Join("|", entries.Select(e => $"{e.Id}:{e.Name}:{e.Enabled}"))}.");
        _ = run.CloseMenus();

        int helps = Math.Max(0, run.Count("HelpRequests"));
        bool invoked = run.InvokeMenuEntry(GpeditCommandIds.HelpMenu, GpeditCommandIds.HelpTopics);
        checks.Record("runs", invoked && run.WaitCount("HelpRequests", helps + 1), $"invoked={invoked}, HelpRequests={run.Count("HelpRequests")}.");
        checks.Record("lastCommand", run.Str("LastCommand") == "Help", $"LastCommand='{run.Str("LastCommand")}'.");

        run.WriteArtifacts("gpedit-menu-help-topics.png");
        checks.AssertAllPassed();
    }
}

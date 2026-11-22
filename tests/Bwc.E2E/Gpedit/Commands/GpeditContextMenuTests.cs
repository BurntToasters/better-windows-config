using System.Drawing;
using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.ContextMenus;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Bwc.E2E.Gpedit.Commands.GpeditNames;

namespace Bwc.E2E.Gpedit.Commands;

/// <summary>
/// Context menu tests of the Local Group Policy Editor: the menu of the result list with a policy row,
/// with a folder row and on the empty area, opened by a right click, the context-menu key and
/// Shift+F10, and the menu of the console tree. Entries are compared with the captures F/Menus_Computer
/// and F/Menus_User by command role. The failure modes are in docs/failure-modes/gpedit-commands.md,
/// sections 2 and 3. Every test writes artifacts/e2e/visual/gpedit/gpedit-&lt;name&gt;.json and a
/// picture, and its checks to the E2E result artifact.
/// </summary>
[TestClass]
public sealed class GpeditContextMenuTests
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
    public void Gpedit_ContextMenu_List_PolicyRow()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("contextmenu", "list.row");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-contextmenu-list-row");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-contextmenu-list-row", sandbox);
        using var context = new ContextMenuDriver(run.Shell.Session);

        // A right click on a row that is not the selected one: the menu is for the clicked row.
        run.GoTo(BasicsPath);
        run.SelectRow(DecimalValues);
        bool clicked = context.RightClick(run.RowElement(MachineFlag));
        List<GpeditMenuEntry> entries = GpeditCommandRun.WaitContextMenu(context);
        checks.Record("clicked", clicked, "the right click was sent to a policy row.");
        GpeditCommandRun.CheckMenu(checks, "menu", entries, expectEdit: true, "right click on a policy row");
        run.WriteArtifacts("gpedit-contextmenu-list-row.png");

        int edits = Math.Max(0, run.Count("Edits"));
        bool invoked = context.Invoke(GpeditCommandIds.Edit);
        checks.Record(
            "clickedRowEdited",
            invoked && ShellRun.WaitUntil(() => run.Count("Edits") == edits + 1 && run.Str("LastEdit") == MachineFlagFull),
            $"invoked={invoked}, Edits={run.Count("Edits")}, LastEdit='{run.Str("LastEdit")}'; expected the clicked row {MachineFlagFull}, not the selected one.");
        checks.Record("menuClosedAfterCommand", context.WaitForNoMenu(), "the menu closes after a command ran.");

        // The context-menu key opens the menu of the focused row; Escape closes it and leaves the focus in the list.
        run.SelectRow(BothFlag);
        edits = Math.Max(0, run.Count("Edits"));
        run.Press(GpeditKeys.Apps);
        entries = GpeditCommandRun.WaitContextMenu(context);
        GpeditCommandRun.CheckMenu(checks, "key", entries, expectEdit: true, "the context-menu key on a policy row");
        run.Press(GpeditKeys.Escape);
        checks.Record("dismissCloses", context.WaitForNoMenu(), "Escape closes the menu.");
        checks.Record(
            "dismissKeepsFocus",
            ShellRun.WaitUntil(() => run.Shell.FocusedPane() == "result"),
            $"focused pane after Escape={run.Shell.FocusedPane()}; a dismissed menu leaves the focus where it was.");
        checks.Record("dismissRunsNothing", run.Count("Edits") == edits,$"Edits={run.Count("Edits")}, expected {edits}.");

        // Shift+F10 gives the same menu, and its Refresh entry runs the Refresh command.
        run.PressShift(GpeditKeys.F10);
        entries = GpeditCommandRun.WaitContextMenu(context);
        GpeditCommandRun.CheckMenu(checks, "shiftF10", entries, expectEdit: true, "Shift+F10 on a policy row");
        int refreshes = Math.Max(0, run.Count("Refreshes"));
        invoked = context.Invoke(GpeditCommandIds.Refresh);
        checks.Record(
            "refreshRuns",
            invoked && run.WaitCount("Refreshes", refreshes + 1),
            $"invoked={invoked}, Refreshes={run.Count("Refreshes")}.");
        checks.Record("refreshKeepsNode", run.NodePath().SequenceEqual(BasicsPath), $"node after Refresh={run.Str("Node")}.");

        run.WriteArtifacts("gpedit-contextmenu-list-row-after.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_ContextMenu_List_NoRow()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("contextmenu", "list.norow");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-contextmenu-list-norow");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-contextmenu-list-norow", sandbox);
        using var context = new ContextMenuDriver(run.Shell.Session);

        // The folder list of BWC Oracle, a right click on the empty area below the rows (CTX-001, CTX-008).
        run.GoTo(OraclePath);
        Rectangle? list = run.Rect(GpeditRun.ListId);
        checks.Record("listFound", list is not null, "the result list has a rectangle.");
        if (list is { } area)
        {
            bool clicked = context.RightClick(area.Left + (area.Width / 2), area.Bottom - 8);
            checks.Record("emptyAreaClicked", clicked, "the right click was sent to the empty area of the list.");
        }

        List<GpeditMenuEntry> entries = GpeditCommandRun.WaitContextMenu(context);
        GpeditCommandRun.CheckMenu(checks, "emptyArea", entries, expectEdit: false, "right click on the empty area");
        run.WriteArtifacts("gpedit-contextmenu-list-norow.png");
        run.Press(GpeditKeys.Escape);
        checks.Record("emptyAreaCloses", context.WaitForNoMenu(), "Escape closes the menu.");

        // A folder row is not a policy: no Edit, and the right click does not open the folder.
        bool rowClicked = context.RightClick(run.RowElement(BasicsName));
        entries = GpeditCommandRun.WaitContextMenu(context);
        checks.Record("folderRowClicked", rowClicked, "the right click was sent to a folder row.");
        GpeditCommandRun.CheckMenu(checks, "folderRow", entries, expectEdit: false, "right click on a folder row");
        checks.Record("folderStays", run.NodePath().SequenceEqual(OraclePath), $"node after the right click={run.Str("Node")}; the folder is not opened.");

        // Help in a context menu is the Help command.
        int helps = Math.Max(0, run.Count("HelpRequests"));
        bool invoked = context.Invoke(GpeditCommandIds.Help);
        checks.Record("helpRuns", invoked && run.WaitCount("HelpRequests", helps + 1), $"invoked={invoked}, HelpRequests={run.Count("HelpRequests")}.");

        // The menu key with no row selected opens the same menu without Edit.
        run.Press(GpeditKeys.Escape);
        _ = context.WaitForNoMenu();
        run.GoTo(Root, Computer, Admin);
        _ = run.FocusPane("result");
        run.Press(GpeditKeys.Apps);
        entries = GpeditCommandRun.WaitContextMenu(context);
        GpeditCommandRun.CheckMenu(checks, "keyNoRow", entries, expectEdit: false, "the context-menu key with no row selected");
        run.Press(GpeditKeys.Escape);
        _ = context.WaitForNoMenu();

        run.WriteArtifacts("gpedit-contextmenu-list-norow-key.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_ContextMenu_Tree()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("contextmenu", "tree");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-contextmenu-tree");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-contextmenu-tree", sandbox);
        using var context = new ContextMenuDriver(run.Shell.Session);

        // A right click on a tree node: Refresh and Help, no Edit (CTX-009: the capture has no tree menu, BWC decides).
        run.GoTo(OraclePath);
        AutomationElement? item = run.TreeItem(Computer);
        checks.Record("itemFound", item is not null, "the Computer Configuration tree item exists.");
        bool clicked = context.RightClick(item);
        List<GpeditMenuEntry> entries = GpeditCommandRun.WaitContextMenu(context);
        checks.Record("clicked", clicked, "the right click was sent to the tree item.");
        GpeditCommandRun.CheckMenu(checks, "pointer", entries, expectEdit: false, "right click on a tree node");
        run.WriteArtifacts("gpedit-contextmenu-tree.png");
        run.Press(GpeditKeys.Escape);
        checks.Record("pointerCloses", context.WaitForNoMenu(), "Escape closes the tree menu.");

        // Shift+F10 with the focus in the tree gives the same menu, and Refresh from it keeps the selected node.
        run.GoTo(OraclePath);
        checks.Record("treeFocus", run.FocusPane("tree"), $"focused pane={run.Shell.FocusedPane()}.");
        string nodeBefore = run.Str("Node");
        run.PressShift(GpeditKeys.F10);
        entries = GpeditCommandRun.WaitContextMenu(context);
        GpeditCommandRun.CheckMenu(checks, "shiftF10", entries, expectEdit: false, "Shift+F10 in the tree");
        int refreshes = Math.Max(0, run.Count("Refreshes"));
        bool invoked = context.Invoke(GpeditCommandIds.Refresh);
        checks.Record("refreshRuns", invoked && run.WaitCount("Refreshes", refreshes + 1), $"invoked={invoked}, Refreshes={run.Count("Refreshes")}.");
        checks.Record("refreshKeepsNode", run.Str("Node") == nodeBefore, $"node before='{nodeBefore}', after='{run.Str("Node")}'.");

        run.WriteArtifacts("gpedit-contextmenu-tree-after.png");
        checks.AssertAllPassed();
    }
}

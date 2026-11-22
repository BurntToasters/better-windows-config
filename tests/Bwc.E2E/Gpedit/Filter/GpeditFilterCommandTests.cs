using System.Drawing;
using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.ContextMenus;
using Bwc.E2E.Controls;
using Bwc.E2E.Gpedit.Commands;
using Bwc.E2E.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Bwc.E2E.Gpedit.Commands.GpeditNames;

namespace Bwc.E2E.Gpedit.Filter;

/// <summary>
/// Command tests of the filter: the entries in the Action menu, the View menu, the list and tree context menus and the
/// toolbar Filter button, per node kind (F/Menus_Computer, F/Menus_User, F/Toolbar_States; MENU-008 to MENU-015, MENU-021,
/// MENU-027 to MENU-029, CTX-005, CTX-007, TOOL-015). The failure modes are in docs/failure-modes/gpedit-filter.md,
/// sections 10 to 13. Every test writes artifacts/e2e/visual/gpedit/gpedit-&lt;name&gt;.json and a picture, and its
/// checks to the E2E result artifact.
/// </summary>
[TestClass]
public sealed class GpeditFilterCommandTests
{
    /// <summary>The node kinds of the captures with the answer the captures give: do the filter entries exist there.</summary>
    private static readonly (string[] Path, string Id, bool Filter)[] Kinds =
    [
        ([Root], "root", false),
        ([Root, Computer], "computerConfiguration", false),
        ([Root, Computer, "Software Settings"], "softwarePlaceholder", false),
        ([Root, Computer, "Windows Settings"], "windowsPlaceholder", false),
        ([Root, Computer, Admin], "administrativeTemplates", true),
        (OraclePath, "category", true),
        (BasicsPath, "categoryWithPolicies", true),
        ([Root, Computer, Admin, AllSettings], "allSettings", true),
        ([Root, User], "userConfiguration", false),
        ([Root, User, Admin], "userAdministrativeTemplates", true),
        ([Root, User, Admin, Oracle, BasicsName], "userCategoryWithPolicies", true),
        ([Root, User, Admin, AllSettings], "userAllSettings", true),
    ];

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Filter_Menu_Action()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("filter", "menu.action");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-filter-menu-action");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-filter-menu-action", sandbox);

        // MENU-007 to MENU-009, MENU-013 to MENU-016: both sides give the same answer at every node kind. At the nodes that
        // have the filter the entries are flat in the popup and again in a tasks submenu after them; elsewhere neither exists.
        foreach ((string[] path, string id, bool filter) in Kinds)
        {
            run.GoTo(path);
            _ = run.WaitNode(path);
            List<GpeditMenuEntry> entries = run.OpenTopMenu(GpeditCommandIds.ActionMenu);
            GpeditFilterRun.CheckFilterEntries(checks, id, entries, filter, filterOn: false, string.Join(" > ", path));
            if (filter)
            {
                List<GpeditMenuEntry> tasks = run.OpenPopupSubmenu(FilterIds.TasksSubmenu, FilterIds.TasksPrefix);
                GpeditFilterRun.CheckSubmenu(checks, id, entries, FilterIds.TasksSubmenu, tasks, filterOn: false, withEdit: false, afterFlat: true, string.Join(" > ", path));
            }
            else
            {
                GpeditFilterRun.CheckSubmenuAbsent(checks, id, entries, FilterIds.TasksSubmenu, string.Join(" > ", path));
            }

            _ = run.CloseMenus();
        }

        // MENU-010: with a policy row selected the order is Edit, the filter entries, Refresh and Help, and Help is last.
        run.GoTo(BasicsPath);
        run.SelectRow(MachineFlag);
        List<GpeditMenuEntry> rowEntries = run.OpenTopMenu(GpeditCommandIds.ActionMenu);
        GpeditCommandRun.CheckMenu(checks, "policyRow.base", rowEntries, expectEdit: true, "a policy row selected");
        GpeditFilterRun.CheckFilterEntries(checks, "policyRow", rowEntries, expected: true, filterOn: false, "a policy row selected");
        int editAt = rowEntries.FindIndex(e => e.Id == GpeditCommandIds.Edit);
        int toggleAt = rowEntries.FindIndex(e => e.Id == FilterIds.Toggle);
        checks.Record("policyRow.editBeforeFilter", editAt == 0 && toggleAt > editAt, $"Edit at {editAt}, Filter On at {toggleAt}.");
        List<GpeditMenuEntry> rowTasks = run.OpenPopupSubmenu(FilterIds.TasksSubmenu, FilterIds.TasksPrefix);
        GpeditFilterRun.CheckSubmenu(checks, "policyRow", rowEntries, FilterIds.TasksSubmenu, rowTasks, filterOn: false, withEdit: true, afterFlat: true, "a policy row selected");
        GpeditMenuEntry? tasksEntry = rowEntries.FirstOrDefault(e => e.Id == FilterIds.TasksSubmenu);
        GpeditMenuEntry? helpEntry = rowEntries.FirstOrDefault(e => e.Id == GpeditCommandIds.Help);
        checks.Record("policyRow.submenuBeforeHelp", tasksEntry is not null && helpEntry is not null && tasksEntry.Top < helpEntry.Top, $"the submenu is above Help; submenu top {tasksEntry?.Top}, Help top {helpEntry?.Top}.");
        _ = run.CloseMenus();

        // Filter Options opens the dialog, Filter On toggles, and the toggle entry shows the state (a check).
        run.GoTo(BasicsPath);
        bool opens = run.InvokeMenuEntry(GpeditCommandIds.ActionMenu, FilterIds.Options);
        checks.Record("options.opensDialog", opens && run.WaitDialog(open: true), "Filter Options opens the dialog.");
        run.CancelDialog();
        checks.Record("toggle.off", run.Count("FilterOn") == 0, $"FilterOn={run.Str("FilterOn")}.");
        _ = run.OpenTopMenu(GpeditCommandIds.ActionMenu);
        checks.Record("toggle.unchecked", GpeditFilterRun.ToggledOn(run.Shell.Find("MenuCommand_" + FilterIds.Toggle)) == false, "the Filter On entry is not checked while the filter is off.");
        _ = run.CloseMenus();
        checks.Record("toggle.runs", run.ToggleFilterByMenu() && run.WaitFilterOn(true), $"FilterOn={run.Str("FilterOn")}.");
        List<GpeditMenuEntry> onEntries = run.OpenTopMenu(GpeditCommandIds.ActionMenu);
        GpeditFilterRun.CheckFilterEntries(checks, "policyRow.on", onEntries, expected: true, filterOn: true, "the Action menu with the filter on");
        checks.Record("toggle.checked", GpeditFilterRun.ToggledOn(run.Shell.Find("MenuCommand_" + FilterIds.Toggle)) == true, "the Filter On entry is checked while the filter is on.");
        List<GpeditMenuEntry> onTasks = run.OpenPopupSubmenu(FilterIds.TasksSubmenu, FilterIds.TasksPrefix);
        GpeditFilterRun.CheckFilterEntries(checks, "policyRow.on.tasks", onTasks, expected: true, filterOn: true, "the tasks submenu with the filter on");
        _ = run.CloseMenus();

        run.WriteArtifacts("gpedit-filter-menu-action.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Filter_Menu_View()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("filter", "menu.view");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-filter-menu-view");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-filter-menu-view", sandbox);

        // MENU-021: the View menu has the filter entries where the Action menu has them, flat, between the view modes and
        // Customize View, and no submenu copy.
        foreach ((string[] path, string id, bool filter) in Kinds)
        {
            run.GoTo(path);
            _ = run.WaitNode(path);
            List<GpeditMenuEntry> entries = run.OpenTopMenu(FilterIds.ViewMenu);
            GpeditFilterRun.CheckFilterEntries(checks, id, entries, filter, filterOn: false, "View menu at " + string.Join(" > ", path));
            if (filter)
            {
                double lastMode = entries.Where(e => FilterIds.ViewModes.Contains(e.Id)).Select(e => e.Top).DefaultIfEmpty(double.MaxValue).Max();
                double toggleTop = entries.FirstOrDefault(e => e.Id == FilterIds.Toggle)?.Top ?? double.NaN;
                double reapplyTop = entries.FirstOrDefault(e => e.Id == FilterIds.ReApply)?.Top ?? double.NaN;
                double customizeTop = entries.FirstOrDefault(e => e.Id == FilterIds.CustomizeView)?.Top ?? double.NaN;
                checks.Record(id + ".afterModes", toggleTop > lastMode, $"the filter entries follow the view modes; toggle at {toggleTop}, last mode at {lastMode}.");
                checks.Record(id + ".beforeCustomize", reapplyTop < customizeTop, $"the filter entries come before Customize View; re-apply at {reapplyTop}, Customize View at {customizeTop}.");
            }

            _ = run.CloseMenus();
        }

        // The entries of the View menu run the same commands as those of the Action menu.
        run.GoTo(BasicsPath);
        checks.Record("toggle.runs", run.InvokeMenuEntry(FilterIds.ViewMenu, FilterIds.Toggle) && run.WaitFilterOn(true), $"FilterOn={run.Str("FilterOn")} after View > Filter On.");
        List<GpeditMenuEntry> on = run.OpenTopMenu(FilterIds.ViewMenu);
        GpeditFilterRun.CheckFilterEntries(checks, "on", on, expected: true, filterOn: true, "View menu with the filter on");
        checks.Record("toggle.checked", GpeditFilterRun.ToggledOn(run.Shell.Find("MenuCommand_" + FilterIds.Toggle)) == true, "View > Filter On is checked while the filter is on.");
        _ = run.CloseMenus();
        bool reapplied = run.InvokeMenuEntry(FilterIds.ViewMenu, FilterIds.ReApply);
        checks.Record("reapply.runs", reapplied && run.Count("FilterOn") == 1, $"View > Re-Apply Filter ran={reapplied}, FilterOn={run.Str("FilterOn")}.");
        bool optionsOpened = run.InvokeMenuEntry(FilterIds.ViewMenu, FilterIds.Options);
        checks.Record("options.opensDialog", optionsOpened && run.WaitDialog(open: true), "View > Filter Options opens the dialog.");
        run.CancelDialog();
        checks.Record("toggle.off", run.InvokeMenuEntry(FilterIds.ViewMenu, FilterIds.Toggle) && run.WaitFilterOn(false), $"FilterOn={run.Str("FilterOn")} after the second View > Filter On.");

        run.WriteArtifacts("gpedit-filter-menu-view.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Filter_ContextMenu_List()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("filter", "contextmenu.list");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-filter-contextmenu-list");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-filter-contextmenu-list", sandbox);
        using var context = new ContextMenuDriver(run.Shell.Session);

        // CTX-007: a policy row. Edit first, then the filter entries flat, then a tasks submenu with the same four entries,
        // Help last.
        run.GoTo(BasicsPath);
        bool clicked = context.RightClick(run.RowElement(MachineFlag));
        List<GpeditMenuEntry> entries = GpeditCommandRun.WaitContextMenu(context);
        checks.Record("row.clicked", clicked, "the right click was sent to a policy row.");
        GpeditCommandRun.CheckMenu(checks, "row.base", entries, expectEdit: true, "right click on a policy row");
        GpeditFilterRun.CheckFilterEntries(checks, "row", entries, expected: true, filterOn: false, "right click on a policy row");
        List<GpeditMenuEntry> rowTasks = run.OpenContextSubmenu(context, FilterIds.TasksSubmenu, FilterIds.TasksPrefix);
        GpeditFilterRun.CheckSubmenu(checks, "row", entries, FilterIds.TasksSubmenu, rowTasks, filterOn: false, withEdit: true, afterFlat: true, "right click on a policy row");
        run.WriteArtifacts("gpedit-filter-contextmenu-row.png");
        _ = run.CloseMenus(ContextMenuDriver.ContextPrefix);

        // The Filter On entry of the context menu toggles the filter, and the menu then closes.
        _ = context.RightClick(run.RowElement(MachineFlag));
        _ = GpeditCommandRun.WaitContextMenu(context);
        checks.Record("row.toggleRuns", context.Invoke(FilterIds.Toggle) && run.WaitFilterOn(true), $"FilterOn={run.Str("FilterOn")}.");
        checks.Record("row.menuCloses", context.WaitForNoMenu(), "the menu closes after the command ran.");
        run.Press(GpeditKeys.Apps);
        entries = GpeditCommandRun.WaitContextMenu(context);
        GpeditFilterRun.CheckFilterEntries(checks, "rowOn", entries, expected: true, filterOn: true, "the context-menu key with the filter on");
        List<GpeditMenuEntry> onTasks = run.OpenContextSubmenu(context, FilterIds.TasksSubmenu, FilterIds.TasksPrefix);
        GpeditFilterRun.CheckFilterEntries(checks, "rowOn.tasks", onTasks, expected: true, filterOn: true, "the tasks submenu with the filter on");
        _ = run.CloseMenus(ContextMenuDriver.ContextPrefix);
        run.Press(GpeditKeys.Apps);
        _ = GpeditCommandRun.WaitContextMenu(context);
        bool reapply = context.Invoke(FilterIds.ReApply);
        checks.Record("row.reapplyRuns", reapply && run.Count("FilterOn") == 1, $"Re-Apply ran={reapply}, FilterOn={run.Str("FilterOn")}.");
        _ = context.WaitForNoMenu();
        run.Press(GpeditKeys.Apps);
        _ = GpeditCommandRun.WaitContextMenu(context);
        bool optionsInvoked = context.Invoke(FilterIds.Options);
        checks.Record("row.optionsOpens", optionsInvoked && run.WaitDialog(open: true), "Filter Options from the context menu opens the dialog.");
        run.CancelDialog();
        _ = run.ToggleFilterByMenu();
        _ = run.WaitFilterOn(false);

        // CTX-005, CTX-006: the menu of the empty area has no flat filter entry. At the nodes that have the filter its View
        // submenu holds them; the nodes the captures leave out of the Action menu leave them out of this menu too.
        foreach ((string[] path, string id, bool filter) in Kinds.Where(k => k.Path.Length >= 2 && k.Path[1] == Computer))
        {
            run.GoTo(path);
            Rectangle? list = run.Rect(GpeditRun.ListId);
            checks.Record(id + ".listFound", list is not null, "the result list has a rectangle.");
            if (list is { } area)
            {
                _ = context.RightClick(area.Left + (area.Width / 2), area.Bottom - 8);
            }

            entries = GpeditCommandRun.WaitContextMenu(context);
            string where = "context menu on the empty area at " + string.Join(" > ", path);
            GpeditFilterRun.CheckFilterEntries(checks, id + ".empty.flat", entries, expected: false, filterOn: false, where);
            GpeditFilterRun.CheckSubmenuAbsent(checks, id + ".empty.tasks", entries, FilterIds.TasksSubmenu, where);
            List<GpeditMenuEntry> view = run.OpenContextSubmenu(context, FilterIds.ViewSubmenu, FilterIds.ViewSubmenuPrefix);
            if (filter)
            {
                GpeditFilterRun.CheckSubmenu(checks, id + ".empty", entries, FilterIds.ViewSubmenu, view, filterOn: false, withEdit: false, afterFlat: false, where);
            }
            else
            {
                GpeditFilterRun.CheckFilterEntries(checks, id + ".empty.view", view, expected: false, filterOn: false, where + " (View submenu, if any)");
            }

            _ = run.CloseMenus(ContextMenuDriver.ContextPrefix);
        }

        // A folder row has no Edit. Classic's menu of a folder row is not in the captures, so only the base entries are asserted.
        run.GoTo(OraclePath);
        _ = context.RightClick(run.RowElement(BasicsName));
        entries = GpeditCommandRun.WaitContextMenu(context);
        GpeditCommandRun.CheckMenu(checks, "folderRow.base", entries, expectEdit: false, "right click on a folder row");
        run.Press(GpeditKeys.Escape);
        _ = context.WaitForNoMenu();

        run.WriteArtifacts("gpedit-filter-contextmenu-list.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Filter_ContextMenu_Tree()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("filter", "contextmenu.tree");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-filter-contextmenu-tree");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-filter-contextmenu-tree", sandbox);
        using var context = new ContextMenuDriver(run.Shell.Session);

        // Shift+F10 with the focus in the tree opens the menu of the selected node, so no click has to select it.
        foreach ((string[] path, string id, bool filter) in Kinds)
        {
            run.GoTo(path);
            _ = run.WaitNode(path);
            checks.Record(id + ".treeFocus", run.FocusPane("tree"), $"focused pane={run.Shell.FocusedPane()}.");
            run.PressShift(GpeditKeys.F10);
            List<GpeditMenuEntry> entries = GpeditCommandRun.WaitContextMenu(context);
            GpeditFilterRun.CheckFilterEntries(checks, id, entries, filter, filterOn: false, "tree menu at " + string.Join(" > ", path));
            run.Press(GpeditKeys.Escape);
            _ = context.WaitForNoMenu();
        }

        // The tree menu entry toggles the same filter.
        run.GoTo(BasicsPath);
        _ = run.FocusPane("tree");
        run.PressShift(GpeditKeys.F10);
        _ = GpeditCommandRun.WaitContextMenu(context);
        checks.Record("toggleRuns", context.Invoke(FilterIds.Toggle) && run.WaitFilterOn(true), $"FilterOn={run.Str("FilterOn")}.");
        run.PressShift(GpeditKeys.F10);
        List<GpeditMenuEntry> on = GpeditCommandRun.WaitContextMenu(context);
        GpeditFilterRun.CheckFilterEntries(checks, "on", on, expected: true, filterOn: true, "tree menu with the filter on");
        run.Press(GpeditKeys.Escape);
        _ = context.WaitForNoMenu();

        run.WriteArtifacts("gpedit-filter-contextmenu-tree.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Filter_Toolbar_Button()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("filter", "toolbar");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-filter-toolbar");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-filter-toolbar", sandbox);

        // TOOL-015: the button shows at the Administrative Templates node, category nodes and All Settings, and with a policy
        // row selected, never at the configuration nodes and the placeholders. It is enabled and not pressed while no
        // filter is on.
        foreach ((string[] path, string id, bool filter) in Kinds)
        {
            run.GoTo(path);
            _ = run.WaitNode(path);
            checks.Record(id + ".presence", run.WaitFilterButton(filter), $"{string.Join(" > ", path)}: the Filter button is {(filter ? "shown" : "absent")}.");
            if (filter && run.FilterButton() is { } button)
            {
                checks.Record(id + ".state", button.IsEnabled && GpeditFilterRun.ToggledOn(button) == false, $"enabled={button.IsEnabled}, pressed={GpeditFilterRun.ToggledOn(button)}.");
            }
        }

        run.GoTo(BasicsPath);
        run.SelectRow(MachineFlag);
        checks.Record("row.present", run.WaitFilterButton(true), "the button shows with a policy row selected.");

        // The button sits after the Help button, behind a separator (TOOL-003: the last button at 193 px).
        Rectangle? filterRect = run.Shell.Rect(ShellRun.ButtonId(FilterIds.ToolbarButton));
        Rectangle? helpRect = run.Shell.Rect(ShellRun.ButtonId("Help"));
        checks.Record("afterHelp", filterRect is { } f && helpRect is { } h && f.Left > h.Right, $"Filter at {filterRect}, Help at {helpRect}.");

        // A press toggles the filter, the button shows it pressed, and the state follows a change made by the menu.
        checks.Record("press.on", run.InvokeButton(FilterIds.ToolbarButton) && run.WaitFilterOn(true), $"FilterOn={run.Str("FilterOn")}.");
        checks.Record("press.pressed", ShellRun.WaitUntil(() => GpeditFilterRun.ToggledOn(run.FilterButton()) == true), "the button is pressed while the filter is on.");
        run.GoTo(OraclePath);
        checks.Record("pressedAtOtherNode", GpeditFilterRun.ToggledOn(run.FilterButton()) == true, "the button stays pressed at another node.");
        checks.Record("menuOff", run.ToggleFilterByMenu() && run.WaitFilterOn(false), $"FilterOn={run.Str("FilterOn")}.");
        checks.Record("press.released", ShellRun.WaitUntil(() => GpeditFilterRun.ToggledOn(run.FilterButton()) == false), "the button is released when the menu switched the filter off.");

        // The other buttons keep their states (TOOL-016).
        checks.Record("othersUnchanged", run.Shell.ButtonEnabled("Refresh") && run.Shell.ButtonEnabled("Help") && run.Shell.ButtonEnabled("UpOneLevel"), "Refresh, Help and Up One Level stay enabled.");

        run.WriteArtifacts("gpedit-filter-toolbar.png");
        checks.AssertAllPassed();
    }
}

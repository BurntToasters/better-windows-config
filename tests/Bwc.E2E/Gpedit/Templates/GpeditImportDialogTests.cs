using System.Diagnostics;
using System.Drawing;
using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.ContextMenus;
using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Gpedit.Commands;
using Bwc.E2E.Gpedit.Filter;
using Bwc.E2E.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Bwc.E2E.Gpedit.Commands.GpeditNames;

namespace Bwc.E2E.Gpedit.Templates;

/// <summary>
/// Add/Remove Templates menu entry and dialog tests of the Local Group Policy Editor: where the entry
/// is (MENU-008, MENU-011,
/// CTX-005), and the parts and layout of the dialog (DLG-033, DLG-115). The failure modes are in
/// docs/failure-modes/gpedit-templates.md, sections 1 and 2. Every test writes
/// artifacts/e2e/visual/gpedit/gpedit-&lt;name&gt;.json and a picture, and its checks to the E2E result artifact.
/// </summary>
[TestClass]
public sealed class GpeditImportDialogTests
{
    /// <summary>The node kinds of the captures with the answer the captures give: is Add/Remove Templates
    /// in the Action menu there.</summary>
    private static readonly (string[] Path, string Id, bool Entry, bool Filter)[] Kinds =
    [
        ([Root], "root", false, false),
        ([Root, Computer], "computerConfiguration", false, false),
        ([Root, Computer, "Software Settings"], "softwarePlaceholder", false, false),
        ([Root, Computer, "Windows Settings"], "windowsPlaceholder", false, false),
        ([Root, Computer, Admin], "administrativeTemplates", true, true),
        (OraclePath, "category", false, true),
        (BasicsPath, "categoryWithPolicies", false, true),
        ([Root, Computer, Admin, AllSettings], "allSettings", false, true),
        ([Root, User], "userConfiguration", false, false),
        ([Root, User, Admin], "userAdministrativeTemplates", true, true),
        ([Root, User, Admin, Oracle, BasicsName], "userCategoryWithPolicies", false, true),
        ([Root, User, Admin, AllSettings], "userAllSettings", false, true),
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
    public void Gpedit_Import_Menu_Placement()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("import", "menu.placement");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-import-menu-placement");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-import-menu-placement", sandbox);

        // MENU-008, MENU-011: both sides give the same answer. The entry is first at the Administrative
        // Templates node, in the
        // tasks submenu there, and at no other node.
        foreach ((string[] path, string id, bool entry, bool filter) in Kinds)
        {
            run.GoTo(path);
            _ = run.WaitNode(path);
            string where = string.Join(" > ", path);
            List<GpeditMenuEntry> entries = run.OpenTopMenu(GpeditCommandIds.ActionMenu);
            string listing = string.Join("|", entries.Select(e => $"{e.Id}:{e.Name}:{(e.Enabled ? "on" : "off")}"));
            GpeditMenuEntry? add = entries.FirstOrDefault(e => e.Id == TemplateIds.AddRemove);
            checks.Record(id + ".present", (add is not null) == entry, $"{where}: Add/Remove Templates is {(entry ? "present" : "absent")}; entries={listing}.");
            if (entry)
            {
                checks.Record(id + ".first", entries.Count > 0 && entries[0].Id == TemplateIds.AddRemove, $"{where}: it is the first entry; entries={listing}.");
                checks.Record(id + ".enabled", add is { Enabled: true }, $"{where}: it is enabled; entries={listing}.");
                checks.Record(id + ".label", add is not null && add.Name.StartsWith("Add/Remove Templates", StringComparison.Ordinal), $"{where}: label '{add?.Name}' starts with Add/Remove Templates.");
                GpeditFilterRun.CheckFilterEntries(checks, id + ".filter", entries, expected: true, filterOn: false, where);
                int addAt = entries.FindIndex(e => e.Id == TemplateIds.AddRemove);
                int toggleAt = entries.FindIndex(e => e.Id == FilterIds.Toggle);
                checks.Record(id + ".beforeFilter", addAt >= 0 && toggleAt > addAt, $"{where}: Add/Remove Templates (at {addAt}) comes before Filter On (at {toggleAt}).");
                List<GpeditMenuEntry> tasks = run.OpenPopupSubmenu(FilterIds.TasksSubmenu, FilterIds.TasksPrefix);
                checks.Record(id + ".tasks", tasks.Any(e => e.Id == TemplateIds.AddRemove), $"{where}: the tasks submenu holds Add/Remove Templates; entries={string.Join("|", tasks.Select(e => e.Id + ":" + e.Name))}.");
            }
            else if (filter)
            {
                List<GpeditMenuEntry> tasks = run.OpenPopupSubmenu(FilterIds.TasksSubmenu, FilterIds.TasksPrefix);
                checks.Record(id + ".tasksAbsent", tasks.All(e => e.Id != TemplateIds.AddRemove), $"{where}: the tasks submenu has no Add/Remove Templates.");
            }

            checks.Record(id + ".helpLast", entries.Count > 0 && entries[^1].Id == GpeditCommandIds.Help, $"{where}: Help is still last; entries={listing}.");
            _ = run.CloseMenus();
        }

        // MENU-010: with a policy row selected there is no Add/Remove Templates.
        run.GoTo(BasicsPath);
        run.SelectRow(MachineFlag);
        List<GpeditMenuEntry> rowEntries = run.OpenTopMenu(GpeditCommandIds.ActionMenu);
        checks.Record("policyRow.absent", rowEntries.All(e => e.Id != TemplateIds.AddRemove), $"entries={string.Join("|", rowEntries.Select(e => e.Id + ":" + e.Name))}.");
        _ = run.CloseMenus();

        // CTX-005: not in the context menu of the list at the Administrative Templates node, and not in the tree menu.
        using var context = new ContextMenuDriver(run.Shell.Session);
        run.GoTo(GpeditTemplatesRun.AdminComputer);
        _ = run.WaitNode(GpeditTemplatesRun.AdminComputer);
        Rectangle? list = run.Rect(GpeditRun.ListId);
        checks.Record("listFound", list is not null, "the result list has a rectangle.");
        if (list is { } area)
        {
            bool clicked = context.RightClick(area.Left + (area.Width / 2), area.Bottom - 8);
            List<GpeditMenuEntry> listMenu = GpeditCommandRun.WaitContextMenu(context);
            checks.Record("listMenu.clicked", clicked && listMenu.Count > 0, $"the right click opened a menu with {listMenu.Count} entries.");
            checks.Record("listMenu.absent", listMenu.All(e => e.Id != TemplateIds.AddRemove), $"entries={string.Join("|", listMenu.Select(e => e.Id + ":" + e.Name))}.");
            run.Press(GpeditKeys.Escape);
            _ = context.WaitForNoMenu();
        }

        checks.Record("treeFocus", run.FocusPane("tree"), $"focused pane={run.Shell.FocusedPane()}.");
        run.PressShift(GpeditKeys.F10);
        List<GpeditMenuEntry> treeMenu = GpeditCommandRun.WaitContextMenu(context);
        checks.Record("treeMenu.opened", treeMenu.Count > 0, $"the tree menu has {treeMenu.Count} entries.");
        checks.Record("treeMenu.absent", treeMenu.All(e => e.Id != TemplateIds.AddRemove), $"entries={string.Join("|", treeMenu.Select(e => e.Id + ":" + e.Name))}.");
        run.Press(GpeditKeys.Escape);
        _ = context.WaitForNoMenu();

        run.WriteArtifacts("gpedit-import-menu-placement.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Import_Dialog_Layout()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("import", "dialog.layout");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-import-dialog-layout");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-import-dialog-layout", sandbox);
        run.GoTo(GpeditTemplatesRun.AdminComputer);
        _ = run.WaitNode(GpeditTemplatesRun.AdminComputer);

        using GpeditTemplatesDialog? dialog = GpeditTemplatesDialog.Open(run);
        checks.Record("opens", dialog is not null, "Add/Remove Templates from the Action menu opens a window with the id GpeditTemplates.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        // DLG-115: the list, Add, Remove and Close all exist.
        foreach (string id in TemplateIds.Parts)
        {
            checks.Record("part." + id["GpeditTemplates.".Length..], dialog.Has(id), $"the control {id} exists.");
        }

        // The store of the sandbox is empty (the system folder is not listed), Remove needs a selected row.
        checks.Record("listEmptyAtStart", dialog.Rows().Count == 0, $"rows at start: {dialog.Rows().Describe()}.");
        checks.Record("removeDisabledAtStart", !dialog.IsEnabled(TemplateIds.Remove), "Remove is disabled while no row is selected.");
        checks.Record("addCloseEnabled", dialog.IsEnabled(TemplateIds.Add) && dialog.IsEnabled(TemplateIds.Close), "Add and Close are enabled.");

        // DLG-033: 288 x 150 dialog units, 432 x 225 DIP at the classic font, with a tolerance.
        Rectangle? root = dialog.RectOf(TemplateIds.Dialog);
        double width = root is { } r ? r.Width / run.Scale : -1;
        double height = root is { } q ? q.Height / run.Scale : -1;
        checks.Record("size", width is >= 360 and <= 580 && height is >= 190 and <= 340, $"dialog size {width:0} x {height:0} DIP; expected about 432 x 225.");
        run.Measure("dialogSizeDip", $"{width:0} x {height:0}");

        // Reading order: the list, then Add, Remove and Close (a tie in the top edge is read from the left).
        Rectangle?[] boxes = TemplateIds.Parts.Select(dialog.RectOf).ToArray();
        bool Before(int a, int b)
        {
            if (boxes[a] is not { } x || boxes[b] is not { } y)
            {
                return false;
            }

            return x.Top < y.Top - 4 || (Math.Abs(x.Top - y.Top) <= 4 && x.Left < y.Left);
        }

        checks.Record("order", Before(0, 1) && Before(1, 2) && Before(2, 3), $"list, Add, Remove, Close in reading order; boxes={string.Join(" ", boxes.Select(b => b is { } v ? $"({v.Left},{v.Top})" : "(none)"))}.");

        // The dialog is modal: the editor window is disabled while it is open.
        checks.Record("modal", !run.Shell.Session.IsEnabled, "the main window is disabled while the dialog is open.");
        _ = dialog.Screenshot("gpedit-import-dialog-layout.png");

        // Escape closes the dialog and nothing changed.
        _ = ClassicDialogTestsBridge.EnsureForeground(dialog.Handle());
        Bwc.E2E.List.RawKeyboard.Tap(GpeditKeys.Escape);
        checks.Record("escapeCloses", dialog.WaitClosed(), "Escape closes the dialog.");
        checks.Record("ownerEnabledAgain", ShellRun.WaitUntil(() => run.Shell.Session.IsEnabled, TimeSpan.FromSeconds(5)), "the main window is enabled again.");

        // Close closes it too, with the button.
        using GpeditTemplatesDialog? again = GpeditTemplatesDialog.Open(run);
        checks.Record("opensAgain", again is not null, "the dialog opens a second time.");
        checks.Record("closeCloses", again is not null && again.Close(), "Close closes the dialog.");
        checks.Record("noReloadForNothing", run.Count("TemplateReloads") == 0, $"TemplateReloads={run.Str("TemplateReloads")}; nothing changed, so nothing reloaded.");

        run.WriteArtifacts("gpedit-import-dialog-layout.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Import_Dialog_ModalRace()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("import", "dialog.modalRace");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-import-dialog-modal-race");
        sandbox.DialogEnabled = true;
        sandbox.TemplateReadDelayMilliseconds = 15000;
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-import-dialog-modal-race", sandbox);
        using var driver = new EditingDriver(run.Shell.Session);
        int windowsBefore = ProcessWindows.Of(run.Shell.Automation, run.Shell.Session.ProcessId).Length;

        run.GoTo(GpeditTemplatesRun.AdminComputer);
        _ = run.WaitNode(GpeditTemplatesRun.AdminComputer);
        bool started = run.InvokeMenuEntry(GpeditCommandIds.ActionMenu, TemplateIds.AddRemove);
        checks.Record("preload.started", started, "Add/Remove Templates starts its delayed store listing.");
        checks.Record(
            "preload.noDialogYet",
            driver.WaitForElement(TemplateIds.Dialog, TimeSpan.FromMilliseconds(300)) is null,
            "the delayed listing has not opened its dialog yet.");

        // While the list is being read, none of the other modal entrypoints may open over the same owner.
        run.GoTo(BasicsPath);
        run.SelectRow(MachineFlag);
        bool editInvoked = run.InvokeMenuEntry(GpeditCommandIds.ActionMenu, GpeditCommandIds.Edit);
        bool editStayedClosed = DismissUnexpectedDialog(driver, "DialogRoot", GpeditDialog.Cancel);
        checks.Record("policy.blocked", editInvoked && editStayedClosed, "Edit cannot open a setting dialog during template preload.");

        bool filterInvoked = run.InvokeMenuEntry(FilterIds.ViewMenu, FilterIds.Options);
        bool filterStayedClosed = DismissUnexpectedDialog(driver, FilterIds.Dialog, FilterIds.Cancel);
        checks.Record("filter.blocked", filterInvoked && filterStayedClosed, "Filter Options cannot open during template preload.");

        bool frameInvoked = run.InvokeMenuEntry(FrameIds.ViewMenu, FrameIds.ColumnsCommand);
        bool frameStayedClosed = DismissUnexpectedDialog(driver, FrameIds.ColumnsDialog, FrameIds.ColumnsCancel);
        checks.Record("frame.blocked", frameInvoked && frameStayedClosed, "Add/Remove Columns cannot open during template preload.");

        bool optionsInvoked = run.InvokeMenuEntry(OptionsIds.FileMenu, OptionsIds.OptionsCommand);
        bool optionsStayedClosed = DismissUnexpectedDialog(driver, OptionsIds.Dialog, OptionsIds.Cancel);
        checks.Record("options.blocked", optionsInvoked && optionsStayedClosed, "File Options cannot open during template preload.");

        string aboutTitle = AboutProbe.TitleFor(OptionsDialogRead.WindowTitle(run.Shell));
        bool aboutInvoked = run.InvokeMenuEntry(OptionsIds.HelpMenu, OptionsIds.AboutCommand);
        IntPtr about = AboutProbe.IsOpen(run, aboutTitle) ? AboutProbe.Wait(run, aboutTitle) : IntPtr.Zero;
        bool aboutStayedClosed = about == IntPtr.Zero;
        if (about != IntPtr.Zero)
        {
            _ = AboutProbe.Close(about, "gpedit.import.modalRace.about.foreground");
        }

        checks.Record("helpDialog.blocked", aboutInvoked && aboutStayedClosed, "Help About cannot open during template preload.");

        int helps = run.Count("HelpRequests");
        _ = run.InvokeMenuEntry(GpeditCommandIds.ActionMenu, GpeditCommandIds.Help);
        checks.Record("help.blocked", run.Count("HelpRequests") == helps, $"HelpRequests stayed at {helps} during template preload.");

        bool refreshInvoked = run.InvokeButton("Refresh", foreground: false);
        Thread.Sleep(750);
        checks.Record(
            "refresh.queued",
            refreshInvoked && run.Count("Refreshes") == 0,
            $"Refresh was queued while the list was reading; Refreshes={run.Count("Refreshes")}.");

        bool templateOpened = driver.WaitForElement(TemplateIds.Dialog, TimeSpan.FromSeconds(20)) is not null;
        checks.Record("template.dialogOpens", templateOpened, "the original template request opens after preload.");
        bool closed = templateOpened && driver.Invoke(TemplateIds.Close)
            && ShellRun.WaitUntil(
                () => ProcessWindows.Of(run.Shell.Automation, run.Shell.Session.ProcessId).Length == windowsBefore,
                TimeSpan.FromSeconds(8));
        checks.Record("template.dialogCloses", closed, "closing the template dialog releases its modal reservation.");
        checks.Record(
            "refresh.afterClose",
            run.WaitCount("Refreshes", 1) && run.WaitReloadSettled(),
            $"one queued refresh applies after close; Refreshes={run.Count("Refreshes")}, Reloading={run.Str("Reloading")}.");
        checks.Record("refresh.lastCommand", run.Str("LastCommand") == "Refresh", $"LastCommand='{run.Str("LastCommand")}'.");

        run.WriteArtifacts("gpedit-import-dialog-modal-race.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Import_Dialog_ClosedDuringPreload()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("import", "dialog.closedDuringPreload");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-import-dialog-closed-during-preload");
        sandbox.TemplateReadDelayMilliseconds = 5000;
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-import-dialog-closed-during-preload", sandbox);
        using var driver = new EditingDriver(run.Shell.Session);

        run.GoTo(GpeditTemplatesRun.AdminComputer);
        _ = run.WaitNode(GpeditTemplatesRun.AdminComputer);
        bool started = run.InvokeMenuEntry(GpeditCommandIds.ActionMenu, TemplateIds.AddRemove);
        bool awaiting = started && driver.WaitForElement(TemplateIds.Dialog, TimeSpan.FromMilliseconds(300)) is null;
        checks.Record("preload.awaiting", awaiting, "the editor closes while the store listing is still pending.");
        run.WriteArtifacts("gpedit-import-dialog-closed-during-preload.png");

        int processId = run.Shell.Session.ProcessId;
        run.Shell.Session.Close();
        Thread.Sleep(5500);
        bool exited;
        try
        {
            using Process process = Process.GetProcessById(processId);
            exited = process.HasExited;
        }
        catch (ArgumentException)
        {
            exited = true;
        }

        checks.Record("owner.closed", exited, "the owner has closed after the pending preload completed.");
        checks.Record("noWindowsRemain", ProcessWindows.Of(run.Shell.Automation, processId).Length == 0, "no modal window remains for the closed process.");
        checks.AssertAllPassed();
    }

    private static bool DismissUnexpectedDialog(EditingDriver driver, string dialogId, string cancelId)
    {
        if (driver.WaitForElement(dialogId, TimeSpan.FromMilliseconds(250)) is null)
        {
            return true;
        }

        _ = driver.Invoke(cancelId);
        return false;
    }
}

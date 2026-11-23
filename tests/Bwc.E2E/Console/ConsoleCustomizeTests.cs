using System.Drawing;
using Bwc.E2E.Controls;
using Bwc.E2E.Gpedit;
using Bwc.E2E.Gpedit.Commands;
using Bwc.E2E.Gpedit.Filter;
using Bwc.E2E.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Bwc.E2E.Gpedit.Commands.GpeditNames;

namespace Bwc.E2E.ConsoleFrame;

/// <summary>
/// View > Customize View of the console frame (MENU-030, DLG-073, DLG-117, TOOL-017, VIEW-1): the View menu no longer holds
/// the Console Tree, Toolbar and Status Bar toggles; the dialog holds the switches, they act at once, and the policy editor
/// keeps them across restarts. The frame host (placeholder tree, synthetic rows) shows the dialog and the effects; the
/// policy editor adds the snap-in toolbar (the Filter button) and the saved settings. Failure modes:
/// docs/failure-modes/console-frame.md, sections 5 to 8. Every test writes
/// artifacts/e2e/visual/console-frame/console-frame-&lt;name&gt;.json and a picture, and its checks to the E2E result
/// artifact.
/// </summary>
[TestClass]
public sealed class ConsoleCustomizeTests
{
    private const string FrameArea = "frame";

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("E2EOnly")]
    public void Console_Customize_Menu_NoToggles()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new ShellChecks("customize.menu", FrameArea);
        using ShellRun run = ShellRun.Open("frame-customize-menu");

        // VIEW-1: classic keeps the three switches in Customize View only. The View menu has Customize View last.
        List<GpeditMenuEntry> entries = FrameMenu.OpenTop(run, FrameIds.ViewMenu);
        checks.Record("noToggleIds", entries.All(e => Array.IndexOf(FrameIds.RemovedViewToggles, e.Id) < 0), $"entries {FrameMenu.Describe(entries)}.");
        string[] words = ["Console Tree", "Toolbar", "Status Bar"];
        checks.Record("noToggleNames", entries.All(e => words.All(w => !e.Name.Contains(w, StringComparison.OrdinalIgnoreCase))), "no entry is named after the Console Tree, the Toolbar or the Status Bar.");
        checks.Record("customizeLast", entries.Count > 0 && entries[^1].Id == FrameIds.CustomizeCommand && entries[^1].Enabled, $"Customize View is the last entry and enabled; entries {FrameMenu.Describe(entries)}.");
        _ = FrameMenu.Close(run);

        using FrameDialog? dialog = FrameDialog.Open(run, FrameIds.CustomizeDialog, FrameIds.ViewMenu, FrameIds.CustomizeCommand);
        checks.Record("opens", dialog is not null, "View > Customize View opens a window with the id ConsoleCustomize.");
        if (dialog is not null)
        {
            checks.Record("okCloses", dialog.PressAndClose(FrameIds.CustomizeOk), "OK closes the dialog.");
        }

        checks.Record("ownerEnabledAgain", FrameRun.OwnerEnabled(run), "the main window is enabled again.");
        run.WriteArtifacts("console-frame-customize-menu.png", FrameRun.Area);
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("E2EOnly")]
    public void Console_Customize_Dialog_Layout()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new ShellChecks("customize.layout", FrameArea);
        using ShellRun run = ShellRun.Open("frame-customize-layout");
        using FrameDialog? dialog = FrameDialog.Open(run, FrameIds.CustomizeDialog, FrameIds.ViewMenu, FrameIds.CustomizeCommand);
        checks.Record("opens", dialog is not null, "View > Customize View opens the dialog.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        // DLG-117: nine check boxes and an OK button only (no Cancel).
        foreach (string id in FrameIds.CustomizeBoxes)
        {
            checks.Record("part." + id["ConsoleCustomize.".Length..], dialog.Has(id), $"the check box {id} exists.");
        }

        checks.Record("part.Ok", dialog.Has(FrameIds.CustomizeOk), "the OK button exists.");
        checks.Record("noCancel", !dialog.Has(FrameIds.CustomizeCancel), "there is no Cancel button (DLG-117: an OK button only).");

        // The names carry the classic roles in BWC's own words.
        string[] roles = ["console tree", "menus", "toolbar", "status bar", "description bar", "taskpad", "action pane", "menus", "toolbars"];
        for (int i = 0; i < roles.Length; i++)
        {
            string label = FrameRun.NameOf(dialog, FrameIds.CustomizeBoxes[i]);
            checks.Record("name." + FrameIds.CustomizeBoxes[i]["ConsoleCustomize.".Length..], label.Contains(roles[i], StringComparison.OrdinalIgnoreCase), $"the label '{label}' names the role '{roles[i]}'.");
        }

        // DLG-073: 214 x 223 dialog units, about 321 x 362 DIP at the classic font, with a tolerance.
        (double width, double height) = FrameRun.SizeDip(dialog, run);
        checks.Record("size", width is >= 270 and <= 400 && height is >= 300 and <= 450, $"dialog size {width:0} x {height:0} DIP; expected about 321 x 362.");
        run.Measure("dialogSizeDip", $"{width:0} x {height:0}");

        // The reading order of DLG-073: the nine boxes top to bottom, then OK.
        double[] tops = FrameIds.CustomizeBoxes.Select(id => (double?)dialog.RectOf(id)?.Top ?? double.NaN).ToArray();
        bool ascending = tops.Zip(tops.Skip(1), (a, b) => a < b).All(x => x);
        checks.Record("order", ascending, $"the check boxes top to bottom: {string.Join(",", tops)}.");
        double okTop = dialog.RectOf(FrameIds.CustomizeOk)?.Top ?? double.NaN;
        checks.Record("okLast", okTop > tops.Max(), $"OK is below the last check box ({okTop} vs {tops.Max()}).");

        // BWC definition: the switches BWC has are enabled and on at the start; the four it does not have are disabled.
        foreach (string id in FrameIds.BuiltBoxes)
        {
            checks.Record("built." + id["ConsoleCustomize.".Length..], dialog.IsEnabled(id) && dialog.Checked(id), $"{id} is enabled and checked at the start.");
        }

        foreach (string id in FrameIds.UnbuiltBoxes)
        {
            checks.Record("unbuilt." + id["ConsoleCustomize.".Length..], !dialog.IsEnabled(id), $"{id} is disabled: BWC has no such part.");
        }

        checks.Record("modal", !run.Session.IsEnabled, "the main window is disabled while the dialog is open.");
        _ = dialog.Screenshot("console-frame-customize-layout-dialog.png");
        checks.Record("okCloses", dialog.PressAndClose(FrameIds.CustomizeOk), "OK closes the dialog.");
        run.WriteArtifacts("console-frame-customize-layout.png", FrameRun.Area);
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("E2EOnly")]
    public void Console_Customize_Switches_ActAtOnce()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new ShellChecks("customize.switches", FrameArea);
        using ShellRun run = ShellRun.Open("frame-customize-switches");
        run.ClickTreeNode(Computer);
        checks.Record("start", Visible(run, ShellRun.TreePaneId) && Visible(run, ShellRun.ToolbarId) && Visible(run, ShellRun.StatusBarId) && Visible(run, FrameIds.DescriptionPaneId), "the tree, toolbar, status bar and description pane show at the start.");
        Rectangle? listBefore = run.Rect(FrameIds.ListId);

        // The tree toggle button and the check box are one switch (TOOL-008, TOOL-017).
        _ = ControlTestSupport.EnsureForeground(run.Session);
        run.Find(ShellRun.ButtonId("ShowHideConsoleTree"))?.Patterns.Invoke.Pattern.Invoke();
        checks.Record("button.hides", ShellRun.WaitUntil(() => run.IsHidden(ShellRun.TreePaneId)), "the Show/Hide Console Tree button hides the tree.");
        using (FrameDialog? sync = FrameDialog.Open(run, FrameIds.CustomizeDialog, FrameIds.ViewMenu, FrameIds.CustomizeCommand))
        {
            checks.Record("sync.opens", sync is not null, "Customize View opens with the tree hidden.");
            if (sync is null)
            {
                checks.AssertAllPassed();
                return;
            }

            checks.Record("sync.unchecked", !sync.Checked(FrameIds.ConsoleTree), "the Console tree box is off after the button hid the tree.");
            checks.Record("sync.checkShows", sync.SetChecked(FrameIds.ConsoleTree, true) && ShellRun.WaitUntil(() => Visible(run, ShellRun.TreePaneId)), "ticking the box shows the tree at once, with the dialog still open.");
            _ = sync.PressAndClose(FrameIds.CustomizeOk);
        }

        // Each switch acts when it is changed, before OK closes the dialog.
        using (FrameDialog? dialog = FrameDialog.Open(run, FrameIds.CustomizeDialog, FrameIds.ViewMenu, FrameIds.CustomizeCommand))
        {
            checks.Record("first.opens", dialog is not null, "Customize View opens.");
            if (dialog is null)
            {
                checks.AssertAllPassed();
                return;
            }

            checks.Record("tree.off", dialog.SetChecked(FrameIds.ConsoleTree, false) && ShellRun.WaitUntil(() => run.IsHidden(ShellRun.TreePaneId) && run.IsHidden(ShellRun.SplitterId)), "clearing Console tree hides the tree and the splitter at once.");
            Rectangle? listWide = run.Rect(FrameIds.ListId);
            checks.Record("tree.listWider", listWide is { } wide && listBefore is { } narrow && wide.Width > narrow.Width, $"the list is wider without the tree ({listBefore?.Width} to {listWide?.Width} px).");

            checks.Record("toolbar.off", dialog.SetChecked(FrameIds.StandardToolbar, false) && ShellRun.WaitUntil(() => run.IsHidden(ShellRun.ToolbarId)), "clearing Standard toolbar hides the toolbar at once.");
            checks.Record("menuBarStays", Visible(run, ShellRun.MenuBarId), "the menu bar stays.");

            checks.Record("status.off", dialog.SetChecked(FrameIds.StatusBar, false) && ShellRun.WaitUntil(() => run.IsHidden(ShellRun.StatusBarId)), "clearing Status bar hides the status bar at once.");

            checks.Record("description.off", dialog.SetChecked(FrameIds.DescriptionBar, false) && ShellRun.WaitUntil(() => run.IsHidden(FrameIds.DescriptionPaneId)), "clearing Description bar hides the description pane at once.");
            checks.Record("description.listFills", ShellRun.WaitUntil(() => run.Rect(FrameIds.ListId) is { } now && listWide is { } before && now.Left < before.Left), "the list takes the room of the description pane.");
            _ = dialog.Screenshot("console-frame-customize-switches-dialog.png");
            checks.Record("ok", dialog.PressAndClose(FrameIds.CustomizeOk), "OK closes the dialog.");
        }

        checks.Record("afterOk.stillHidden", run.IsHidden(ShellRun.TreePaneId) && run.IsHidden(ShellRun.ToolbarId) && run.IsHidden(ShellRun.StatusBarId) && run.IsHidden(FrameIds.DescriptionPaneId), "the four parts stay hidden after OK.");
        checks.Record("afterOk.paneCycle", ShellRun.WaitUntil(() => run.Find(ShellRun.ResultPaneId) is not null), "the result pane is still there.");
        run.WriteArtifacts("console-frame-customize-switches-hidden.png", FrameRun.Area);

        // The dialog opens on the state it left, and each box brings its part back at once.
        using (FrameDialog? dialog = FrameDialog.Open(run, FrameIds.CustomizeDialog, FrameIds.ViewMenu, FrameIds.CustomizeCommand))
        {
            checks.Record("second.opens", dialog is not null, "Customize View opens again.");
            if (dialog is null)
            {
                checks.AssertAllPassed();
                return;
            }

            bool allOff = new[] { FrameIds.ConsoleTree, FrameIds.StandardToolbar, FrameIds.StatusBar, FrameIds.DescriptionBar }.All(id => !dialog.Checked(id));
            checks.Record("second.boxesOff", allOff, "the four boxes show off.");
            checks.Record("second.snapInOn", dialog.Checked(FrameIds.SnapInToolbars), "the snap-in toolbar box was not touched and stays on.");
            checks.Record("tree.on", dialog.SetChecked(FrameIds.ConsoleTree, true) && ShellRun.WaitUntil(() => Visible(run, ShellRun.TreePaneId) && Visible(run, ShellRun.SplitterId)), "ticking Console tree shows the tree and the splitter.");
            checks.Record("toolbar.on", dialog.SetChecked(FrameIds.StandardToolbar, true) && ShellRun.WaitUntil(() => Visible(run, ShellRun.ToolbarId)), "ticking Standard toolbar shows the toolbar.");
            checks.Record("status.on", dialog.SetChecked(FrameIds.StatusBar, true) && ShellRun.WaitUntil(() => Visible(run, ShellRun.StatusBarId)), "ticking Status bar shows the status bar.");
            checks.Record("description.on", dialog.SetChecked(FrameIds.DescriptionBar, true) && ShellRun.WaitUntil(() => Visible(run, FrameIds.DescriptionPaneId)), "ticking Description bar shows the description pane.");
            checks.Record("ok", dialog.PressAndClose(FrameIds.CustomizeOk), "OK closes the dialog.");
        }

        checks.Record("restored", Visible(run, ShellRun.TreePaneId) && Visible(run, ShellRun.ToolbarId) && Visible(run, ShellRun.StatusBarId) && Visible(run, FrameIds.DescriptionPaneId), "every part is back.");
        run.WriteArtifacts("console-frame-customize-switches.png", FrameRun.Area);
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("Console")]
    [TestCategory("E2EOnly")]
    public void Console_Customize_SnapInToolbars_Editor()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("customize", "snapin");
        const string name = "console-customize-snapin";
        GpeditSandbox sandbox = GpeditSandbox.Create(name);
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open(name, sandbox);
        string[] adminPath = [Root, Computer, Admin];
        run.GoTo(adminPath);
        _ = run.WaitNode(adminPath);

        // The Filter button is the snap-in toolbar of the editor (TOOL-015); Back belongs to the standard toolbar.
        checks.Record("start", Visible(run.Shell, FrameIds.FilterButton) && Visible(run.Shell, ShellRun.ButtonId("Back")), "the Filter button and Back show at Administrative Templates.");
        using FrameDialog? dialog = FrameDialog.Open(run.Shell, FrameIds.CustomizeDialog, FrameIds.ViewMenu, FrameIds.CustomizeCommand);
        checks.Record("opens", dialog is not null, "Customize View opens in the policy editor.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        checks.Record("snapIn.off", dialog.SetChecked(FrameIds.SnapInToolbars, false) && ShellRun.WaitUntil(() => !Visible(run.Shell, FrameIds.FilterButton)), "clearing Snap-in toolbars hides the Filter button at once.");
        checks.Record("snapIn.standardStays", Visible(run.Shell, ShellRun.ButtonId("Back")) && Visible(run.Shell, ShellRun.ToolbarId), "the standard buttons stay.");
        checks.Record("standard.off", dialog.SetChecked(FrameIds.StandardToolbar, false) && ShellRun.WaitUntil(() => run.Shell.IsHidden(ShellRun.ToolbarId)), "with both groups off the toolbar band is gone.");
        checks.Record("standard.on", dialog.SetChecked(FrameIds.StandardToolbar, true) && ShellRun.WaitUntil(() => Visible(run.Shell, ShellRun.ButtonId("Back"))), "ticking Standard toolbar brings the standard buttons back.");
        checks.Record("standard.filterStaysOff", !Visible(run.Shell, FrameIds.FilterButton), "the Filter button stays hidden while Snap-in toolbars is off.");
        checks.Record("ok", dialog.PressAndClose(FrameIds.CustomizeOk), "OK closes the dialog.");

        // A node change does not bring the Filter button back (the editor shows it per node; the switch wins).
        run.GoTo(BasicsPath);
        _ = run.WaitNode(BasicsPath);
        checks.Record("node.filterHidden", !Visible(run.Shell, FrameIds.FilterButton), "the Filter button stays hidden after a node change.");
        checks.Record("node.filterToggleAction", run.InvokeMenuEntry(GpeditCommandIds.ActionMenu, FilterIds.Toggle) && run.WaitFilterOn(true), "Filter On from the menu still works with the button hidden.");
        _ = run.InvokeMenuEntry(GpeditCommandIds.ActionMenu, FilterIds.Toggle);

        using FrameDialog? again = FrameDialog.Open(run.Shell, FrameIds.CustomizeDialog, FrameIds.ViewMenu, FrameIds.CustomizeCommand);
        checks.Record("again.opens", again is not null, "Customize View opens again.");
        if (again is not null)
        {
            checks.Record("again.on", again.SetChecked(FrameIds.SnapInToolbars, true) && ShellRun.WaitUntil(() => Visible(run.Shell, FrameIds.FilterButton)), "ticking Snap-in toolbars shows the Filter button again at a node that has it.");
            _ = again.PressAndClose(FrameIds.CustomizeOk);
        }

        run.GoTo(Root, Computer);
        _ = run.WaitNode(Root, Computer);
        checks.Record("noFilterNode", !Visible(run.Shell, FrameIds.FilterButton), "at a node without the filter the button is hidden whatever the switch says.");
        run.WriteArtifacts("console-customize-snapin.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("Console")]
    [TestCategory("E2EOnly")]
    public void Console_Customize_Persist_AcrossRestart()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("customize", "persist");
        const string name = "console-customize-persist";
        GpeditSandbox sandbox = GpeditSandbox.Create(name);
        sandbox.SeedStandard();
        string[] adminPath = [Root, Computer, Admin];

        using (GpeditRun first = GpeditRun.Open(name, sandbox))
        {
            first.GoTo(adminPath);
            _ = first.WaitNode(adminPath);
            using (FrameDialog? dialog = FrameDialog.Open(first.Shell, FrameIds.CustomizeDialog, FrameIds.ViewMenu, FrameIds.CustomizeCommand))
            {
                checks.Record("first.opens", dialog is not null, "Customize View opens.");
                if (dialog is null)
                {
                    checks.AssertAllPassed();
                    return;
                }

                bool changed = dialog.SetChecked(FrameIds.ConsoleTree, false) & dialog.SetChecked(FrameIds.StatusBar, false)
                    & dialog.SetChecked(FrameIds.DescriptionBar, false) & dialog.SetChecked(FrameIds.SnapInToolbars, false);
                checks.Record("first.changed", changed, "four switches cleared; the standard toolbar stays on.");
                _ = dialog.PressAndClose(FrameIds.CustomizeOk);
            }

            checks.Record("first.applied", first.Shell.IsHidden(ShellRun.TreePaneId) && first.Shell.IsHidden(ShellRun.StatusBarId) && first.Shell.IsHidden(FrameIds.DescriptionPaneId) && !Visible(first.Shell, FrameIds.FilterButton) && Visible(first.Shell, ShellRun.ToolbarId), "tree, status bar, description pane and Filter button are hidden; the toolbar shows.");
            first.WriteArtifacts("console-customize-persist-first.png");
        }

        string? file = sandbox.SettingsFilePath();
        string text = file is null ? string.Empty : File.ReadAllText(file);
        checks.Record("settingsFileWritten", text.Length > 0, $"settings file '{file}' was written.");
        string[] keys = ["ViewConsoleTree", "ViewStandardToolbar", "ViewSnapInToolbars", "ViewStatusBar", "ViewDescriptionBar"];
        checks.Record("settingsKeys", keys.All(k => text.Contains(k, StringComparison.Ordinal)), "the file holds the five view switches: " + string.Join(", ", keys.Where(k => !text.Contains(k, StringComparison.Ordinal))) + " missing.");

        using (GpeditRun second = GpeditRun.Open(name, sandbox, placeWindow: false))
        {
            second.GoTo(adminPath);
            _ = second.WaitNode(adminPath);
            checks.Record("second.hiddenAgain", ShellRun.WaitUntil(() => second.Shell.IsHidden(ShellRun.TreePaneId) && second.Shell.IsHidden(ShellRun.StatusBarId) && second.Shell.IsHidden(FrameIds.DescriptionPaneId)), "the tree, the status bar and the description pane are hidden after the restart.");
            checks.Record("second.toolbar", Visible(second.Shell, ShellRun.ButtonId("Back")) && !Visible(second.Shell, FrameIds.FilterButton), "the standard toolbar shows and the Filter button does not.");
            using (FrameDialog? dialog = FrameDialog.Open(second.Shell, FrameIds.CustomizeDialog, FrameIds.ViewMenu, FrameIds.CustomizeCommand))
            {
                checks.Record("second.opens", dialog is not null, "Customize View opens.");
                if (dialog is null)
                {
                    checks.AssertAllPassed();
                    return;
                }

                bool boxes = !dialog.Checked(FrameIds.ConsoleTree) && dialog.Checked(FrameIds.StandardToolbar) && !dialog.Checked(FrameIds.StatusBar)
                    && !dialog.Checked(FrameIds.DescriptionBar) && !dialog.Checked(FrameIds.SnapInToolbars);
                checks.Record("second.boxes", boxes, "the boxes show the saved choice.");
                bool restored = dialog.SetChecked(FrameIds.ConsoleTree, true) & dialog.SetChecked(FrameIds.StatusBar, true)
                    & dialog.SetChecked(FrameIds.DescriptionBar, true) & dialog.SetChecked(FrameIds.SnapInToolbars, true);
                checks.Record("second.restored", restored, "the four switches are set again.");
                _ = dialog.PressAndClose(FrameIds.CustomizeOk);
            }
        }

        using (GpeditRun third = GpeditRun.Open(name, sandbox, placeWindow: false))
        {
            third.GoTo(adminPath);
            _ = third.WaitNode(adminPath);
            checks.Record("third.allShow", ShellRun.WaitUntil(() => Visible(third.Shell, ShellRun.TreePaneId) && Visible(third.Shell, ShellRun.StatusBarId) && Visible(third.Shell, FrameIds.DescriptionPaneId) && Visible(third.Shell, FrameIds.FilterButton)), "everything shows after the third start; the newest choice is the one saved.");
            third.Measure("settingsFile", text);
            third.WriteArtifacts("console-customize-persist.png");
        }

        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("Console")]
    [TestCategory("E2EOnly")]
    public void Console_Customize_Persist_EverythingHiddenInFile()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("customize", "allhidden");
        const string name = "console-customize-allhidden";
        GpeditSandbox sandbox = GpeditSandbox.Create(name);
        sandbox.SeedStandard();

        // A saved choice that hides every part must leave the way back: the menu bar and View > Customize View.
        sandbox.WriteSettingsFile("""
            {
              "ViewConsoleTree": false,
              "ViewStandardToolbar": false,
              "ViewSnapInToolbars": false,
              "ViewStatusBar": false,
              "ViewDescriptionBar": false
            }
            """);
        using GpeditRun run = GpeditRun.Open(name, sandbox, placeWindow: false);
        checks.Record("allHidden", ShellRun.WaitUntil(() => run.Shell.IsHidden(ShellRun.TreePaneId) && run.Shell.IsHidden(ShellRun.ToolbarId) && run.Shell.IsHidden(ShellRun.StatusBarId)), "the tree, the toolbar and the status bar are hidden as the file says.");
        checks.Record("menuBarStays", Visible(run.Shell, ShellRun.MenuBarId), "the menu bar shows.");
        using FrameDialog? dialog = FrameDialog.Open(run.Shell, FrameIds.CustomizeDialog, FrameIds.ViewMenu, FrameIds.CustomizeCommand);
        checks.Record("customizeOpens", dialog is not null, "View > Customize View opens with everything hidden.");
        if (dialog is not null)
        {
            bool allOff = FrameIds.BuiltBoxes.All(id => !dialog.Checked(id));
            checks.Record("boxesOff", allOff, "the five boxes show off.");
            bool back = dialog.SetChecked(FrameIds.ConsoleTree, true) & dialog.SetChecked(FrameIds.StandardToolbar, true) & dialog.SetChecked(FrameIds.StatusBar, true);
            checks.Record("back", back && ShellRun.WaitUntil(() => Visible(run.Shell, ShellRun.TreePaneId) && Visible(run.Shell, ShellRun.ToolbarId) && Visible(run.Shell, ShellRun.StatusBarId)), "the three parts come back at once.");
            _ = dialog.PressAndClose(FrameIds.CustomizeOk);
        }

        run.WriteArtifacts("console-customize-allhidden.png");
        checks.AssertAllPassed();
    }

    private static bool Visible(ShellRun run, string id) => !run.IsHidden(id);
}

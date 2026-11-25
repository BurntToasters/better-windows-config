using Bwc.E2E.Controls;
using Bwc.E2E.Gpedit;
using Bwc.E2E.Gpedit.Commands;
using Bwc.E2E.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Bwc.E2E.Gpedit.Commands.GpeditNames;

namespace Bwc.E2E.ConsoleFrame;

/// <summary>
/// View > Add/Remove Columns of the console frame (MENU-020 to MENU-022, DLG-061, DLG-118): the menu layout, the parts and
/// rules of the column chooser, OK and Cancel, Restore Defaults, and the saved order and visibility of the policy editor.
/// The frame host rows are the Name, State and Comment columns of the E2E seam; the policy editor has Setting, State and
/// Comment (and Path at All Settings). Failure modes: docs/failure-modes/console-frame.md, sections 1 to 4. Every test
/// writes artifacts/e2e/visual/console-frame/console-frame-&lt;name&gt;.json and a picture, and its checks to the E2E
/// result artifact.
/// </summary>
[TestClass]
public sealed class ConsoleColumnsTests
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
    public void Console_Columns_Menu_Layout()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new ShellChecks("columns.menu", FrameArea);
        using ListRun run = ListRun.Open("frame-columns-menu", 8);

        // MENU-020: columns command, separator, four view modes, separator, Customize View. VIEW-1: no toggles.
        List<GpeditMenuEntry> entries = FrameMenu.OpenTop(run.Shell, FrameIds.ViewMenu);
        int[] ids = entries.Select(e => e.Id).ToArray();
        int[] expected = [FrameIds.ColumnsCommand, .. FrameIds.ViewModes, FrameIds.CustomizeCommand];
        checks.Record("entries", ids.SequenceEqual(expected), $"View entries [{string.Join(",", ids)}], expected [{string.Join(",", expected)}]; {FrameMenu.Describe(entries)}.");
        checks.Record("groups", FrameMenu.GroupSizes(entries).SequenceEqual([1, 4, 1]), $"separator groups [{string.Join(",", FrameMenu.GroupSizes(entries))}], expected [1,4,1].");
        checks.Record("noOldToggles", entries.All(e => Array.IndexOf(FrameIds.RemovedViewToggles, e.Id) < 0), "the Console Tree, Toolbar and Status Bar entries are not in the View menu (VIEW-1).");

        GpeditMenuEntry? columns = entries.FirstOrDefault(e => e.Id == FrameIds.ColumnsCommand);
        GpeditMenuEntry? customize = entries.FirstOrDefault(e => e.Id == FrameIds.CustomizeCommand);
        checks.Record("columnsEnabled", columns is { Enabled: true }, "Add/Remove Columns is enabled.");
        checks.Record("customizeEnabled", customize is { Enabled: true }, "Customize View is enabled (MENU-030: not grayed in the capture).");
        checks.Record("columnsLabel", columns?.Name.StartsWith("Add/Remove Columns", StringComparison.Ordinal) ?? false, $"label '{columns?.Name}' starts with Add/Remove Columns.");
        checks.Record("customizeLabel", customize?.Name.StartsWith("Customize View", StringComparison.Ordinal) ?? false, $"label '{customize?.Name}' starts with Customize View.");
        checks.Record("detailStart", run.Mode == "Detail", $"the list starts in mode {run.Mode} (MENU-026).");
        _ = FrameMenu.Close(run.Shell);

        // The entry opens the chooser, and a menu run leaves the list as it was.
        using FrameDialog? dialog = FrameDialog.Open(run.Shell, FrameIds.ColumnsDialog, FrameIds.ViewMenu, FrameIds.ColumnsCommand);
        checks.Record("opens", dialog is not null, "View > Add/Remove Columns opens a window with the id ConsoleColumns.");
        if (dialog is not null)
        {
            checks.Record("cancelCloses", dialog.PressAndClose(FrameIds.ColumnsCancel), "Cancel closes the dialog.");
        }

        checks.Record("listUntouched", FrameRun.WaitOrder(run.Shell, "name", "state", "comment"), $"Order={string.Join(",", FrameRun.Order(run.Shell))} after Cancel.");
        run.Shell.WriteArtifacts("console-frame-columns-menu.png", FrameRun.Area);
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("E2EOnly")]
    public void Console_Columns_Dialog_Layout()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new ShellChecks("columns.layout", FrameArea);
        using ListRun run = ListRun.Open("frame-columns-layout", 8);
        using FrameDialog? dialog = FrameDialog.Open(run.Shell, FrameIds.ColumnsDialog, FrameIds.ViewMenu, FrameIds.ColumnsCommand);
        checks.Record("opens", dialog is not null, "View > Add/Remove Columns opens a window with the id ConsoleColumns.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        // DLG-061, DLG-118: the nine parts.
        foreach (string id in FrameIds.ColumnParts)
        {
            checks.Record("part." + id["ConsoleColumns.".Length..], dialog.Has(id), $"the control {id} exists.");
        }

        // DLG-061: 346 x 172 dialog units, about 519 x 279 DIP at the classic font, with a tolerance.
        (double width, double height) = FrameRun.SizeDip(dialog, run.Shell);
        checks.Record("size", width is >= 430 and <= 620 && height is >= 230 and <= 340, $"dialog size {width:0} x {height:0} DIP; expected about 519 x 279.");
        run.Shell.Measure("dialogSizeDip", $"{width:0} x {height:0}");

        // Reading order of DLG-061: Available, the Add and Remove buttons, Displayed, the Move buttons, OK and Cancel.
        var boxes = FrameIds.ColumnParts.ToDictionary(id => id, dialog.RectOf);
        bool LeftOf(string a, string b) => boxes[a] is { } x && boxes[b] is { } y && x.Right <= y.Left + 4;
        bool Above(string a, string b) => boxes[a] is { } x && boxes[b] is { } y && x.Top < y.Top;
        checks.Record("listsAcross", LeftOf(FrameIds.Available, FrameIds.Displayed), "the Available list is left of the Displayed list.");
        checks.Record("addRemoveBetween", LeftOf(FrameIds.Available, FrameIds.Add) && LeftOf(FrameIds.Add, FrameIds.Displayed) && Above(FrameIds.Add, FrameIds.Remove), "Add above Remove, between the two lists.");
        checks.Record("moveButtonsRight", LeftOf(FrameIds.Displayed, FrameIds.MoveUp) && Above(FrameIds.MoveUp, FrameIds.MoveDown) && Above(FrameIds.MoveDown, FrameIds.Restore), "Move Up, Move Down and Restore Defaults right of Displayed, in that order.");
        checks.Record("okCancelLast", Above(FrameIds.Restore, FrameIds.ColumnsOk) && LeftOf(FrameIds.ColumnsOk, FrameIds.ColumnsCancel), "OK left of Cancel, below everything else.");

        // The start: every column shows, nothing is left to add, and the dialog is modal.
        checks.Record("displayedAtStart", dialog.Items(FrameIds.Displayed).SequenceEqual(["Name", "State", "Comment"]), $"Displayed={string.Join(",", dialog.Items(FrameIds.Displayed))}.");
        checks.Record("availableEmptyAtStart", dialog.Items(FrameIds.Available).Length == 0, $"Available={string.Join(",", dialog.Items(FrameIds.Available))}.");
        checks.Record("addDisabledWithNothing", !dialog.IsEnabled(FrameIds.Add), "Add is disabled while the Available list is empty.");
        checks.Record("restoreOkCancelEnabled", dialog.IsEnabled(FrameIds.Restore) && dialog.IsEnabled(FrameIds.ColumnsOk) && dialog.IsEnabled(FrameIds.ColumnsCancel), "Restore Defaults, OK and Cancel are enabled.");
        checks.Record("modal", !run.Shell.Session.IsEnabled, "the main window is disabled while the dialog is open.");
        _ = dialog.Screenshot("console-frame-columns-layout-dialog.png");

        // Escape is Cancel.
        dialog.PressEscape();
        checks.Record("escapeCloses", dialog.WaitClosed(), "Escape closes the dialog.");
        checks.Record("ownerEnabledAgain", FrameRun.OwnerEnabled(run.Shell), "the main window is enabled again.");
        checks.Record("listUntouched", FrameRun.Order(run.Shell).SequenceEqual(["name", "state", "comment"]) && FrameRun.Hidden(run.Shell).Length == 0, $"Order={string.Join(",", FrameRun.Order(run.Shell))} after Escape.");
        run.Shell.WriteArtifacts("console-frame-columns-layout.png", FrameRun.Area);
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("E2EOnly")]
    public void Console_Columns_Dialog_Operations()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new ShellChecks("columns.operations", FrameArea);
        using ListRun run = ListRun.Open("frame-columns-operations", 8);
        using FrameDialog? dialog = FrameDialog.Open(run.Shell, FrameIds.ColumnsDialog, FrameIds.ViewMenu, FrameIds.ColumnsCommand);
        checks.Record("opens", dialog is not null, "View > Add/Remove Columns opens the dialog.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        // BWC definition: the first column stays first and cannot be removed (the captures do not show the chooser).
        checks.Record("fixed.selected", dialog.Select(FrameIds.Displayed, "Name"), "the Name column can be selected.");
        checks.Record("fixed.removeDisabled", !dialog.IsEnabled(FrameIds.Remove), "Remove is disabled for the first column.");
        checks.Record("fixed.moveDisabled", !dialog.IsEnabled(FrameIds.MoveUp) && !dialog.IsEnabled(FrameIds.MoveDown), "Move Up and Move Down are disabled for the first column.");

        // The second column cannot pass the first one.
        _ = dialog.Select(FrameIds.Displayed, "State");
        checks.Record("second.upDisabled", !dialog.IsEnabled(FrameIds.MoveUp), "Move Up is disabled for the second column (it would pass the first).");
        checks.Record("second.downRemoveEnabled", dialog.IsEnabled(FrameIds.MoveDown) && dialog.IsEnabled(FrameIds.Remove), "Move Down and Remove are enabled for the second column.");

        // The last column cannot move down; Move Up swaps it with its neighbor and the selection follows it.
        _ = dialog.Select(FrameIds.Displayed, "Comment");
        checks.Record("last.downDisabled", !dialog.IsEnabled(FrameIds.MoveDown) && dialog.IsEnabled(FrameIds.MoveUp), "the last column has Move Up and no Move Down.");
        checks.Record("last.up", dialog.Press(FrameIds.MoveUp) && dialog.Items(FrameIds.Displayed).SequenceEqual(["Name", "Comment", "State"]), $"after Move Up Displayed={string.Join(",", dialog.Items(FrameIds.Displayed))}.");
        checks.Record("last.selectionFollows", dialog.SelectedItem(FrameIds.Displayed) == "Comment", $"the selection stays on '{dialog.SelectedItem(FrameIds.Displayed)}'.");
        checks.Record("last.upDisabledNow", !dialog.IsEnabled(FrameIds.MoveUp), "Move Up is disabled once the column is second.");
        checks.Record("last.down", dialog.Press(FrameIds.MoveDown) && dialog.Items(FrameIds.Displayed).SequenceEqual(["Name", "State", "Comment"]), $"after Move Down Displayed={string.Join(",", dialog.Items(FrameIds.Displayed))}.");

        // Remove moves a column to the Available list; Add brings it back at the end of Displayed.
        _ = dialog.Select(FrameIds.Displayed, "State");
        checks.Record("remove", dialog.Press(FrameIds.Remove), "Remove runs on State.");
        checks.Record("remove.displayed", dialog.Items(FrameIds.Displayed).SequenceEqual(["Name", "Comment"]), $"Displayed={string.Join(",", dialog.Items(FrameIds.Displayed))}.");
        checks.Record("remove.available", dialog.Items(FrameIds.Available).SequenceEqual(["State"]), $"Available={string.Join(",", dialog.Items(FrameIds.Available))}.");
        checks.Record("remove.nothingAppliedYet", FrameRun.Order(run.Shell).SequenceEqual(["name", "state", "comment"]), $"the list still shows Order={string.Join(",", FrameRun.Order(run.Shell))} before OK.");
        _ = dialog.Select(FrameIds.Available, "State");
        checks.Record("add.enabled", dialog.IsEnabled(FrameIds.Add), "Add is enabled with an Available column selected.");
        checks.Record("add", dialog.Press(FrameIds.Add), "Add runs on State.");
        checks.Record("add.displayed", dialog.Items(FrameIds.Displayed).SequenceEqual(["Name", "Comment", "State"]), $"State comes back at the end: Displayed={string.Join(",", dialog.Items(FrameIds.Displayed))}.");
        checks.Record("add.available", dialog.Items(FrameIds.Available).Length == 0, $"Available={string.Join(",", dialog.Items(FrameIds.Available))}.");

        // The Available list keeps the definition order, whatever the order of removal.
        _ = dialog.Select(FrameIds.Displayed, "State");
        _ = dialog.Press(FrameIds.Remove);
        _ = dialog.Select(FrameIds.Displayed, "Comment");
        _ = dialog.Press(FrameIds.Remove);
        checks.Record("available.definitionOrder", dialog.Items(FrameIds.Available).SequenceEqual(["State", "Comment"]), $"Available={string.Join(",", dialog.Items(FrameIds.Available))}.");
        checks.Record("onlyFixedLeft", dialog.Items(FrameIds.Displayed).SequenceEqual(["Name"]), $"Displayed={string.Join(",", dialog.Items(FrameIds.Displayed))}.");
        _ = dialog.Screenshot("console-frame-columns-operations-dialog.png");

        checks.Record("cancelCloses", dialog.PressAndClose(FrameIds.ColumnsCancel), "Cancel closes the dialog.");
        checks.Record("cancelChangesNothing", FrameRun.Order(run.Shell).SequenceEqual(["name", "state", "comment"]) && FrameRun.Hidden(run.Shell).Length == 0, $"after Cancel Order={string.Join(",", FrameRun.Order(run.Shell))}.");
        run.Shell.WriteArtifacts("console-frame-columns-operations.png", FrameRun.Area);
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("E2EOnly")]
    public void Console_Columns_Apply_OkAndRestore()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new ShellChecks("columns.apply", FrameArea);
        using ListRun run = ListRun.Open("frame-columns-apply", 8);

        // Hide State and put Comment second: OK applies both to the header and to the cells.
        using (FrameDialog? dialog = FrameDialog.Open(run.Shell, FrameIds.ColumnsDialog, FrameIds.ViewMenu, FrameIds.ColumnsCommand))
        {
            checks.Record("first.opens", dialog is not null, "the dialog opens.");
            if (dialog is null)
            {
                checks.AssertAllPassed();
                return;
            }

            _ = dialog.Select(FrameIds.Displayed, "State");
            _ = dialog.Press(FrameIds.Remove);
            checks.Record("first.ok", dialog.PressAndClose(FrameIds.ColumnsOk), "OK closes the dialog.");
        }

        checks.Record("hidden.order", FrameRun.WaitOrder(run.Shell, "name", "comment"), $"Order={string.Join(",", FrameRun.Order(run.Shell))}; State is gone from the header.");
        checks.Record("hidden.list", FrameRun.Hidden(run.Shell).SequenceEqual(["state"]), $"Hidden={string.Join(",", FrameRun.Hidden(run.Shell))}.");
        checks.Record("hidden.header", run.Shell.IsHidden("ConsoleListHeader_state") && !run.Shell.IsHidden("ConsoleListHeader_comment"), "the State header is not shown and the Comment header is.");
        string[] cells = FrameRun.Cells(run.Shell, 0);
        checks.Record("hidden.cells", cells.SequenceEqual([run.ExpectedName(0), ListRun.ExpectedComment(0)]), $"the first row reads [{string.Join(" | ", cells)}].");

        // The chooser opens on the applied state.
        using (FrameDialog? dialog = FrameDialog.Open(run.Shell, FrameIds.ColumnsDialog, FrameIds.ViewMenu, FrameIds.ColumnsCommand))
        {
            checks.Record("second.opens", dialog is not null, "the dialog opens again.");
            if (dialog is null)
            {
                checks.AssertAllPassed();
                return;
            }

            checks.Record("second.displayed", dialog.Items(FrameIds.Displayed).SequenceEqual(["Name", "Comment"]), $"Displayed={string.Join(",", dialog.Items(FrameIds.Displayed))}.");
            checks.Record("second.available", dialog.Items(FrameIds.Available).SequenceEqual(["State"]), $"Available={string.Join(",", dialog.Items(FrameIds.Available))}.");

            // Restore Defaults works on the dialog only; Cancel leaves the applied state.
            checks.Record("restore.press", dialog.Press(FrameIds.Restore), "Restore Defaults runs.");
            checks.Record("restore.displayed", dialog.Items(FrameIds.Displayed).SequenceEqual(["Name", "State", "Comment"]), $"Displayed={string.Join(",", dialog.Items(FrameIds.Displayed))}.");
            checks.Record("restore.available", dialog.Items(FrameIds.Available).Length == 0, $"Available={string.Join(",", dialog.Items(FrameIds.Available))}.");
            checks.Record("restore.notAppliedYet", FrameRun.Order(run.Shell).SequenceEqual(["name", "comment"]), $"the list still shows Order={string.Join(",", FrameRun.Order(run.Shell))}.");
            checks.Record("restore.cancel", dialog.PressAndClose(FrameIds.ColumnsCancel), "Cancel closes the dialog.");
        }

        checks.Record("cancelKeepsHidden", FrameRun.Order(run.Shell).SequenceEqual(["name", "comment"]) && FrameRun.Hidden(run.Shell).SequenceEqual(["state"]), $"after Cancel Order={string.Join(",", FrameRun.Order(run.Shell))}.");

        // Reorder, then Restore Defaults and OK: the definition order and every column show again.
        using (FrameDialog? dialog = FrameDialog.Open(run.Shell, FrameIds.ColumnsDialog, FrameIds.ViewMenu, FrameIds.ColumnsCommand))
        {
            checks.Record("third.opens", dialog is not null, "the dialog opens a third time.");
            if (dialog is null)
            {
                checks.AssertAllPassed();
                return;
            }

            _ = dialog.Select(FrameIds.Available, "State");
            _ = dialog.Press(FrameIds.Add);
            _ = dialog.Select(FrameIds.Displayed, "State");
            _ = dialog.Press(FrameIds.MoveUp);
            checks.Record("third.reordered", dialog.Items(FrameIds.Displayed).SequenceEqual(["Name", "State", "Comment"]), $"Displayed={string.Join(",", dialog.Items(FrameIds.Displayed))}.");
            _ = dialog.Press(FrameIds.MoveDown);
            checks.Record("third.reorderedAgain", dialog.Items(FrameIds.Displayed).SequenceEqual(["Name", "Comment", "State"]), $"Displayed={string.Join(",", dialog.Items(FrameIds.Displayed))}.");
            _ = dialog.Press(FrameIds.Restore);
            checks.Record("third.ok", dialog.PressAndClose(FrameIds.ColumnsOk), "OK closes the dialog.");
        }

        checks.Record("restored.order", FrameRun.WaitOrder(run.Shell, "name", "state", "comment"), $"Order={string.Join(",", FrameRun.Order(run.Shell))} after Restore Defaults and OK.");
        checks.Record("restored.hidden", FrameRun.Hidden(run.Shell).Length == 0, $"Hidden={string.Join(",", FrameRun.Hidden(run.Shell))}.");
        string[] restoredCells = FrameRun.Cells(run.Shell, 0);
        checks.Record("restored.cells", restoredCells.SequenceEqual([run.ExpectedName(0), ListRun.ExpectedState(0), ListRun.ExpectedComment(0)]), $"the first row reads [{string.Join(" | ", restoredCells)}].");

        // A hidden sort column clears the sort (the list rule), so the chooser does not leave a mark on a missing header.
        checks.Record("sort.click", FrameRun.ClickHeader(run.Shell, "state") && ShellRun.WaitUntil(() => run.Str("Sort") == "state"), $"Sort={run.Str("Sort")}.");
        using (FrameDialog? dialog = FrameDialog.Open(run.Shell, FrameIds.ColumnsDialog, FrameIds.ViewMenu, FrameIds.ColumnsCommand))
        {
            if (dialog is not null)
            {
                _ = dialog.Select(FrameIds.Displayed, "State");
                _ = dialog.Press(FrameIds.Remove);
                _ = dialog.PressAndClose(FrameIds.ColumnsOk);
            }
        }

        checks.Record("sort.cleared", ShellRun.WaitUntil(() => run.Str("Sort") == "none"), $"Sort={run.Str("Sort")} after the sorted column was hidden.");
        run.Shell.WriteArtifacts("console-frame-columns-apply.png", FrameRun.Area);
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("Console")]
    [TestCategory("E2EOnly")]
    public void Console_Columns_Persist_AcrossRestart()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("columns", "persist");
        const string name = "console-columns-persist";
        GpeditSandbox sandbox = GpeditSandbox.Create(name);
        sandbox.SeedStandard();

        using (GpeditRun first = GpeditRun.Open(name, sandbox))
        {
            first.GoTo(BasicsPath);
            _ = first.WaitNode(BasicsPath);
            checks.Record("first.start", FrameRun.Order(first.Shell).SequenceEqual(["setting", "state", "comment"]), $"Order={string.Join(",", FrameRun.Order(first.Shell))}.");

            // Setting stays first, State is hidden, and the three can be told apart by the saved order.
            using (FrameDialog? dialog = FrameDialog.Open(first.Shell, FrameIds.ColumnsDialog, FrameIds.ViewMenu, FrameIds.ColumnsCommand))
            {
                checks.Record("first.opens", dialog is not null, "the dialog opens at Basics.");
                if (dialog is null)
                {
                    checks.AssertAllPassed();
                    return;
                }

                _ = dialog.Select(FrameIds.Displayed, "Comment");
                _ = dialog.Press(FrameIds.MoveUp);
                _ = dialog.Select(FrameIds.Displayed, "State");
                _ = dialog.Press(FrameIds.Remove);
                checks.Record("first.ok", dialog.PressAndClose(FrameIds.ColumnsOk), "OK closes the dialog.");
            }

            checks.Record("first.applied", FrameRun.WaitOrder(first.Shell, "setting", "comment") && FrameRun.Hidden(first.Shell).SequenceEqual(["state"]), $"Order={string.Join(",", FrameRun.Order(first.Shell))}, Hidden={string.Join(",", FrameRun.Hidden(first.Shell))}.");
            first.WriteArtifacts("console-columns-persist-first.png");
        }

        string? file = sandbox.SettingsFilePath();
        string text = file is null ? string.Empty : File.ReadAllText(file);
        checks.Record("settingsFileWritten", text.Length > 0, $"settings file '{file}' was written.");
        checks.Record("settingsNameColumns", text.Contains("ColumnOrder", StringComparison.Ordinal) && text.Contains("HiddenColumns", StringComparison.Ordinal), "the file holds ColumnOrder and HiddenColumns.");

        using (GpeditRun second = GpeditRun.Open(name, sandbox, placeWindow: false))
        {
            second.GoTo(BasicsPath);
            _ = second.WaitNode(BasicsPath);
            checks.Record("second.restored", FrameRun.WaitOrder(second.Shell, "setting", "comment") && FrameRun.Hidden(second.Shell).SequenceEqual(["state"]), $"Order={string.Join(",", FrameRun.Order(second.Shell))}, Hidden={string.Join(",", FrameRun.Hidden(second.Shell))} after the restart.");
            GpeditRow? row = second.Rows().FirstOrDefault();
            checks.Record("second.cells", row is not null && row.Cells.Length == 2, $"the first row has {row?.Cells.Length} cells; expected 2.");

            // The saved choice is one for the list, so it follows to a node with another column set. The Path column
            // is not named by the saved state, so it follows the named ones.
            second.GoTo(Root, Computer, Admin, AllSettings);
            _ = second.WaitNode(Root, Computer, Admin, AllSettings);
            checks.Record("second.allSettings", FrameRun.WaitOrder(second.Shell, "setting", "comment", "path") && FrameRun.Hidden(second.Shell).SequenceEqual(["state"]), $"at All Settings Order={string.Join(",", FrameRun.Order(second.Shell))}, Hidden={string.Join(",", FrameRun.Hidden(second.Shell))}.");

            // Restore Defaults, OK and a restart bring the definition order back.
            using (FrameDialog? dialog = FrameDialog.Open(second.Shell, FrameIds.ColumnsDialog, FrameIds.ViewMenu, FrameIds.ColumnsCommand))
            {
                checks.Record("second.opens", dialog is not null, "the dialog opens at All Settings.");
                if (dialog is not null)
                {
                    _ = dialog.Press(FrameIds.Restore);
                    _ = dialog.PressAndClose(FrameIds.ColumnsOk);
                }
            }

            checks.Record("second.restoredDefaults", FrameRun.WaitOrder(second.Shell, "setting", "state", "comment", "path"), $"Order={string.Join(",", FrameRun.Order(second.Shell))} after Restore Defaults.");
        }

        using (GpeditRun third = GpeditRun.Open(name, sandbox, placeWindow: false))
        {
            third.GoTo(BasicsPath);
            _ = third.WaitNode(BasicsPath);
            checks.Record("third.defaults", FrameRun.WaitOrder(third.Shell, "setting", "state", "comment") && FrameRun.Hidden(third.Shell).Length == 0, $"Order={string.Join(",", FrameRun.Order(third.Shell))}; the newest choice is the one saved.");
            third.Measure("settingsFile", text);
            third.WriteArtifacts("console-columns-persist.png");
        }

        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("Console")]
    [TestCategory("E2EOnly")]
    public void Console_Columns_Persist_BadSettingsFile()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("columns", "badfile");
        const string name = "console-columns-badfile";
        GpeditSandbox sandbox = GpeditSandbox.Create(name);
        sandbox.SeedStandard();

        // Unknown ids, a repeated id, the first column listed as hidden, and an id that no list has.
        sandbox.WriteSettingsFile("""
            {
              "ColumnOrder": ["bogus", "comment", "comment", "state"],
              "HiddenColumns": ["setting", "nope"]
            }
            """);
        using GpeditRun run = GpeditRun.Open(name, sandbox, placeWindow: false);
        run.GoTo(BasicsPath);
        _ = run.WaitNode(BasicsPath);
        checks.Record("opens", run.Str("Node").Length > 0, $"the window opens and shows the node '{run.Str("Node")}'.");
        checks.Record("order", FrameRun.WaitOrder(run.Shell, "setting", "comment", "state"), $"Order={string.Join(",", FrameRun.Order(run.Shell))}; unknown and repeated ids are ignored and the first column stays first.");
        checks.Record("hidden", FrameRun.Hidden(run.Shell).Length == 0, $"Hidden={string.Join(",", FrameRun.Hidden(run.Shell))}; the first column cannot be hidden by a file and an unknown id hides nothing.");
        checks.Record("rowsShown", run.Rows().Count > 0, $"the list shows {run.Rows().Count} rows.");

        using FrameDialog? dialog = FrameDialog.Open(run.Shell, FrameIds.ColumnsDialog, FrameIds.ViewMenu, FrameIds.ColumnsCommand);
        checks.Record("dialogOpens", dialog is not null, "the chooser opens on the repaired state.");
        if (dialog is not null)
        {
            checks.Record("dialogDisplayed", dialog.Items(FrameIds.Displayed).SequenceEqual(["Setting", "Comment", "State"]), $"Displayed={string.Join(",", dialog.Items(FrameIds.Displayed))}.");
            _ = dialog.PressAndClose(FrameIds.ColumnsCancel);
        }

        run.WriteArtifacts("console-columns-badfile.png");
        checks.AssertAllPassed();
    }
}

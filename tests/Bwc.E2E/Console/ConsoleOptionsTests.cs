using System.Text.RegularExpressions;
using Bwc.E2E.Controls;
using Bwc.E2E.Gpedit;
using Bwc.E2E.Gpedit.Commands;
using Bwc.E2E.Harness;
using Bwc.E2E.List;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Bwc.E2E.Gpedit.Commands.GpeditNames;

namespace Bwc.E2E.ConsoleFrame;

/// <summary>
/// File > Options of the console frame (MENU-002 to MENU-005, DLG-119): the File popup is Options, a separator and Exit at
/// every node kind; Options opens a dialog with the parts of the classic page, BWC's own fixed values for the parts that
/// belong to console authoring (which BWC does not have), and one real choice, "allow the user to customize views", which
/// enables or disables View > Customize View and is saved across restarts. The frame host (placeholder tree) shows the
/// dialog and its effect on the View menu; the policy editor shows the File popup at every node kind and the saved choice.
/// Failure modes: docs/failure-modes/console-options.md, sections 1 to 7. Every test writes
/// artifacts/e2e/visual/console-frame/console-frame-&lt;name&gt;.json (frame host) or
/// artifacts/e2e/visual/gpedit/gpedit-&lt;name&gt;.json (policy editor) and a picture, and its checks to the E2E result
/// artifact.
/// </summary>
[TestClass]
public sealed class ConsoleOptionsTests
{
    private const string FrameArea = "frame";

    private static readonly string[] SwitchKeys = ["ViewConsoleTree", "ViewStandardToolbar", "ViewSnapInToolbars", "ViewStatusBar", "ViewDescriptionBar"];

    /// <summary>The node kinds of the captures (F/Menus_Computer, F/Menus_User); the File popup is the same at each.</summary>
    private static readonly (string[] Path, string Id)[] Kinds =
    [
        ([Root], "root"),
        ([Root, Computer], "computerConfiguration"),
        ([Root, Computer, "Software Settings"], "softwarePlaceholder"),
        ([Root, Computer, "Windows Settings"], "windowsPlaceholder"),
        ([Root, Computer, Admin], "administrativeTemplates"),
        (OraclePath, "category"),
        (BasicsPath, "categoryWithPolicies"),
        ([Root, Computer, Admin, AllSettings], "allSettings"),
        ([Root, User], "userConfiguration"),
        ([Root, User, "Software Settings"], "userSoftwarePlaceholder"),
        ([Root, User, "Windows Settings"], "userWindowsPlaceholder"),
        ([Root, User, Admin], "userAdministrativeTemplates"),
        ([Root, User, Admin, Oracle, BasicsName], "userCategoryWithPolicies"),
    ];

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("Console")]
    [TestCategory("E2EOnly")]
    public void Console_Options_Menu_FileLayout()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("options", "menu.nodes");
        const string name = "console-options-menu-nodes";
        GpeditSandbox sandbox = GpeditSandbox.Create(name);
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open(name, sandbox);

        // MENU-002 to MENU-005: Options, a separator, Exit; nothing else; enabled; the same at every node kind. The nodes
        // alternate between the two sides so a popup that was built once and kept would show.
        var listings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string[] path, string id) in Kinds)
        {
            run.GoTo(path);
            _ = run.WaitNode(path);
            listings[id] = CheckFilePopup(checks.Record, run.Shell, id, string.Join(" > ", path));
        }

        // With a policy row selected the popup is the same (MENU-003).
        run.GoTo(BasicsPath);
        run.SelectRow(MachineFlag);
        listings["policyRow"] = CheckFilePopup(checks.Record, run.Shell, "policyRow", "a policy row selected");
        run.GoTo(Root, User, Admin, Oracle, BasicsName);
        run.SelectRow("Class user flag setting");
        listings["userPolicyRow"] = CheckFilePopup(checks.Record, run.Shell, "userPolicyRow", "a user policy row selected");

        // The access keys of the two entries (MENU-003: P, MENU-004: X).
        List<ShapeEntry> entries = MenuShape.OpenTop(run.Shell, OptionsIds.FileMenu);
        string optionsKey = MenuShape.Entry(entries, OptionsIds.OptionsCommand)?.AccessKey ?? string.Empty;
        string exitKey = MenuShape.Entry(entries, OptionsIds.ExitCommand)?.AccessKey ?? string.Empty;
        checks.Record("accessKeys", optionsKey.EndsWith('P') || optionsKey.EndsWith('p'), $"the access key of Options is '{optionsKey}', expected P; Exit has '{exitKey}'.");
        checks.Record("exitKey", exitKey.EndsWith('X') || exitKey.EndsWith('x'), $"the access key of Exit is '{exitKey}', expected X.");
        _ = MenuShape.Close(run.Shell);

        // The menu bar has no Favorites entry (MENU-037, removed by decision): File, Action, View, Help only.
        string[] top = MenuShape.TopLevelNames(run.Shell);
        checks.Record("noFavorites", top.Length == 4 && top[0].Contains("File", StringComparison.Ordinal) && top[3].Contains("Help", StringComparison.Ordinal), $"top level menus [{string.Join(", ", top)}]; expected File, Action, View, Help.");

        run.Measure("entriesByNode", listings);
        run.WriteArtifacts("gpedit-console-options-menu-nodes.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("E2EOnly")]
    public void Console_Options_Menu_AccessKey()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new ShellChecks("options.menu", FrameArea);
        using ShellRun run = ShellRun.Open("frame-options-menu");
        run.ClickTreeNode(Computer);

        // The frame host has the same File popup (the layout is the frame's, not the editor's).
        _ = CheckFilePopup(checks.Record, run, "frameHost", "the frame host");

        // Alt+F then P is the keyboard way to Options (MENU-003). The test waits for the tree focus first: a late click
        // would dismiss the popup the chord opened (docs/failure-modes/menus.md).
        _ = ShellRun.WaitUntil(() => run.FocusedPane() == "tree", TimeSpan.FromSeconds(3));
        run.Chord(RawKeyboard.Alt, ShellRun.VkF);
        _ = ShellRun.WaitUntil(() => MenuShape.Read(run).Count > 0, TimeSpan.FromSeconds(5));
        run.Tap(OptionsIds.VkP);
        using FrameDialog? dialog = FrameDialog.Attach(run, OptionsIds.Dialog);
        checks.Record("altFP.opens", dialog is not null, "Alt+F then P opens the Options dialog.");
        if (dialog is not null)
        {
            checks.Record("altFP.cancel", dialog.PressAndClose(OptionsIds.Cancel), "Cancel closes the dialog.");
        }

        checks.Record("ownerEnabledAgain", FrameRun.OwnerEnabled(run), "the main window is enabled again.");
        run.WriteArtifacts("console-frame-options-menu.png", FrameRun.Area);
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("E2EOnly")]
    public void Console_Options_Dialog_Layout()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new ShellChecks("options.layout", FrameArea);
        using ShellRun run = ShellRun.Open("frame-options-layout");
        run.ClickTreeNode(Computer);
        using FrameDialog? dialog = FrameDialog.Open(run, OptionsIds.Dialog, OptionsIds.FileMenu, OptionsIds.OptionsCommand);
        checks.Record("opens", dialog is not null, "File > Options opens a window with the id ConsoleOptions.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        // DLG-119: the name box, the icon button, the mode drop-down with its description, the two check boxes; the sheet
        // adds OK and Cancel.
        foreach (string id in OptionsIds.Parts)
        {
            checks.Record("part." + id["ConsoleOptions.".Length..], dialog.Has(id), $"the part {id} exists.");
        }

        string caption = OptionsDialogRead.Caption(dialog);
        checks.Record("caption", caption.Contains("Options", StringComparison.OrdinalIgnoreCase), $"the window caption is '{caption}', expected to name Options.");

        // BWC has no console authoring: the name is the tool's own name, read-only; the icon and the mode cannot change.
        string title = OptionsDialogRead.WindowTitle(run);
        string shownName = OptionsDialogRead.TextOf(dialog, OptionsIds.Name);
        checks.Record("name.isToolName", title.Length > 0 && shownName == title, $"the name box shows '{shownName}'; the window title is '{title}'.");
        checks.Record("name.readOnly", OptionsDialogRead.IsReadOnly(dialog, OptionsIds.Name), "the name box is read-only.");
        checks.Record("icon.disabled", !dialog.IsEnabled(OptionsIds.ChangeIcon), "the Change Icon button is disabled: the icon is fixed.");
        string mode = OptionsDialogRead.TextOf(dialog, OptionsIds.Mode);
        string description = OptionsDialogRead.TextOf(dialog, OptionsIds.ModeDescription);
        checks.Record("mode.disabled", !dialog.IsEnabled(OptionsIds.Mode), "the console mode drop-down is disabled: the mode is fixed.");
        checks.Record("mode.text", mode.Length > 0, "the drop-down shows the one mode.");
        checks.Record("mode.description", description.Length > 0 && description != mode, $"the description area has text of its own: '{description}' under the mode '{mode}'.");
        checks.Record("noSave.fixed", !dialog.IsEnabled(OptionsIds.NoSave) && dialog.Checked(OptionsIds.NoSave), "the not-saving check box is checked and disabled: BWC never writes a console file.");
        checks.Record("customize.on", dialog.IsEnabled(OptionsIds.CustomizeViews) && dialog.Checked(OptionsIds.CustomizeViews), "the customize-views check box is enabled and checked at the start.");
        checks.Record("ok.enabled", dialog.IsEnabled(OptionsIds.Ok), "OK is enabled.");
        checks.Record("cancel.enabled", dialog.IsEnabled(OptionsIds.Cancel), "Cancel is enabled.");

        // The size: the classic page is 252 x 218 dialog units (about 378 x 354 DIP); BWC adds the button row.
        (double width, double height) = FrameRun.SizeDip(dialog, run);
        checks.Record("size", width is >= 340 and <= 540 && height is >= 380 and <= 600, $"dialog size {width:0} x {height:0} DIP; expected about 400 x 430 (the page is 378 x 354).");
        run.Measure("dialogSizeDip", $"{width:0} x {height:0}");

        // The reading order of the page: name box with the icon button on its right, then the mode, its description, the
        // not-saving box, the customize-views box, then the buttons with OK left of Cancel.
        System.Drawing.Rectangle? name = dialog.RectOf(OptionsIds.Name);
        System.Drawing.Rectangle? icon = dialog.RectOf(OptionsIds.ChangeIcon);
        System.Drawing.Rectangle? modeBox = dialog.RectOf(OptionsIds.Mode);
        System.Drawing.Rectangle? desc = dialog.RectOf(OptionsIds.ModeDescription);
        System.Drawing.Rectangle? noSave = dialog.RectOf(OptionsIds.NoSave);
        System.Drawing.Rectangle? customize = dialog.RectOf(OptionsIds.CustomizeViews);
        System.Drawing.Rectangle? ok = dialog.RectOf(OptionsIds.Ok);
        System.Drawing.Rectangle? cancel = dialog.RectOf(OptionsIds.Cancel);
        int slack = 2;
        bool rects = name is not null && icon is not null && modeBox is not null && desc is not null && noSave is not null && customize is not null && ok is not null && cancel is not null;
        checks.Record("rects", rects, "every part has a rectangle.");
        if (rects)
        {
            checks.Record("order.iconRightOfName", icon!.Value.Left >= name!.Value.Right - slack && Math.Abs(icon.Value.Top - name.Value.Top) <= 8 * run.Scale, $"icon {icon}, name {name}.");
            checks.Record("order.modeBelow", modeBox!.Value.Top >= Math.Max(name.Value.Bottom, icon.Value.Bottom) - slack, $"mode {modeBox}, name {name}, icon {icon}.");
            checks.Record("order.descriptionBelowMode", desc!.Value.Top >= modeBox.Value.Bottom - slack, $"description {desc}, mode {modeBox}.");
            checks.Record("order.noSaveBelowDescription", noSave!.Value.Top >= desc.Value.Bottom - slack, $"not-saving box {noSave}, description {desc}.");
            checks.Record("order.customizeBelowNoSave", customize!.Value.Top > noSave.Value.Top, $"customize box {customize}, not-saving box {noSave}.");
            checks.Record("order.buttonsLast", ok!.Value.Top >= customize.Value.Bottom - slack && cancel!.Value.Top >= customize.Value.Bottom - slack, $"OK {ok}, Cancel {cancel}, customize box {customize}.");
            checks.Record("order.okLeftOfCancel", ok.Value.Right <= cancel!.Value.Left + slack, $"OK {ok}, Cancel {cancel}.");
        }

        checks.Record("modal", !run.Session.IsEnabled, "the main window is disabled while the dialog is open.");
        _ = dialog.Screenshot("console-frame-options-layout-dialog.png");
        checks.Record("cancelCloses", dialog.PressAndClose(OptionsIds.Cancel), "Cancel closes the dialog.");
        checks.Record("ownerEnabledAgain", FrameRun.OwnerEnabled(run), "the main window is enabled again.");
        run.WriteArtifacts("console-frame-options-layout.png", FrameRun.Area);
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("E2EOnly")]
    public void Console_Options_Dialog_OkCancel()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new ShellChecks("options.okcancel", FrameArea);
        using ShellRun run = ShellRun.Open("frame-options-okcancel");
        run.ClickTreeNode(Computer);
        Action<string, bool, string> record = checks.Record;

        // The start: Customize View is enabled and last in the View popup.
        (ShapeEntry? start, List<ShapeEntry> startEntries) = ReadCustomizeEntry(run);
        checks.Record("start.enabled", start is { Enabled: true }, $"Customize View is present and enabled at the start; entries {MenuShape.Describe(startEntries)}.");

        // Cancel changes nothing.
        checks.Record("cancel.closes", ChangeAllowed(run, record, "cancel", allowed: false, closeWith: OptionsIds.Cancel), "the box was cleared and Cancel pressed.");
        (ShapeEntry? afterCancel, _) = ReadCustomizeEntry(run);
        checks.Record("cancel.keeps", afterCancel is { Enabled: true }, "after Cancel Customize View is still enabled.");
        using (FrameDialog? reopened = FrameDialog.Open(run, OptionsIds.Dialog, OptionsIds.FileMenu, OptionsIds.OptionsCommand))
        {
            checks.Record("cancel.reopenChecked", reopened is not null && reopened.Checked(OptionsIds.CustomizeViews), "the dialog opens again with the box checked: Cancel kept nothing.");
            _ = reopened?.PressAndClose(OptionsIds.Cancel);
        }

        // OK applies at once: Customize View stays in its place (last) and is disabled.
        checks.Record("ok.closes", ChangeAllowed(run, record, "ok", allowed: false, closeWith: OptionsIds.Ok), "the box was cleared and OK pressed.");
        (ShapeEntry? afterOk, List<ShapeEntry> afterOkEntries) = ReadCustomizeEntry(run);
        checks.Record("ok.disables", afterOk is { Enabled: false }, $"after OK Customize View is disabled; entries {MenuShape.Describe(afterOkEntries)}.");
        checks.Record("ok.staysLast", afterOkEntries.Count > 0 && afterOkEntries[^1].Id == FrameIds.CustomizeCommand, "Customize View is still the last entry of the View popup.");
        checks.Record("ok.groupsKept", MenuShape.Groups(afterOkEntries).SequenceEqual(MenuShape.Groups(startEntries)), $"the groups [{string.Join(",", MenuShape.Groups(afterOkEntries))}] equal those at the start [{string.Join(",", MenuShape.Groups(startEntries))}].");
        using (FrameDialog? reopened = FrameDialog.Open(run, OptionsIds.Dialog, OptionsIds.FileMenu, OptionsIds.OptionsCommand))
        {
            checks.Record("ok.reopenUnchecked", reopened is not null && !reopened.Checked(OptionsIds.CustomizeViews), "the dialog opens again with the box cleared.");
            _ = reopened?.PressAndClose(OptionsIds.Cancel);
        }

        // Escape is Cancel: the box set again is not kept.
        using (FrameDialog? escaped = FrameDialog.Open(run, OptionsIds.Dialog, OptionsIds.FileMenu, OptionsIds.OptionsCommand))
        {
            bool set = escaped is not null && escaped.SetChecked(OptionsIds.CustomizeViews, true);
            escaped?.PressEscape();
            checks.Record("escape.closes", set && escaped is not null && escaped.WaitClosed(), "Escape closes the dialog.");
        }

        (ShapeEntry? afterEscape, _) = ReadCustomizeEntry(run);
        checks.Record("escape.cancels", afterEscape is { Enabled: false }, "after Escape Customize View is still disabled.");

        // OK with the box set again enables Customize View, and the command opens its dialog.
        checks.Record("again.closes", ChangeAllowed(run, record, "again", allowed: true, closeWith: OptionsIds.Ok), "the box was set and OK pressed.");
        (ShapeEntry? afterAgain, _) = ReadCustomizeEntry(run);
        checks.Record("again.enables", afterAgain is { Enabled: true }, "after OK Customize View is enabled again.");
        using (FrameDialog? customize = FrameDialog.Open(run, FrameIds.CustomizeDialog, FrameIds.ViewMenu, FrameIds.CustomizeCommand))
        {
            checks.Record("again.customizeOpens", customize is not null, "View > Customize View opens again.");
            _ = customize?.PressAndClose(FrameIds.CustomizeOk);
        }

        checks.Record("ownerEnabledAgain", FrameRun.OwnerEnabled(run), "the main window is enabled again.");
        run.WriteArtifacts("console-frame-options-okcancel.png", FrameRun.Area);
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Console")]
    [TestCategory("E2EOnly")]
    public void Console_Options_CustomizeViews_KeepsSwitches()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new ShellChecks("options.switches", FrameArea);
        using ShellRun run = ShellRun.Open("frame-options-switches");
        run.ClickTreeNode(Computer);
        Action<string, bool, string> record = checks.Record;

        // Hide the status bar and the description pane through Customize View first.
        using (FrameDialog? customize = FrameDialog.Open(run, FrameIds.CustomizeDialog, FrameIds.ViewMenu, FrameIds.CustomizeCommand))
        {
            bool cleared = customize is not null && customize.SetChecked(FrameIds.StatusBar, false) & customize.SetChecked(FrameIds.DescriptionBar, false);
            checks.Record("hide.cleared", cleared, "the status bar and description bar boxes were cleared.");
            _ = customize?.PressAndClose(FrameIds.CustomizeOk);
        }

        checks.Record("hide.applied", run.IsHidden(ShellRun.StatusBarId) && run.IsHidden(FrameIds.DescriptionPaneId), "the status bar and the description pane are hidden.");

        // Forbidding customization must not undo what is applied: the parts stay as they are.
        checks.Record("forbid.closes", ChangeAllowed(run, record, "forbid", allowed: false, closeWith: OptionsIds.Ok), "the customize-views box was cleared and OK pressed.");
        checks.Record("forbid.statusStaysHidden", run.IsHidden(ShellRun.StatusBarId), "the status bar is still hidden.");
        checks.Record("forbid.descriptionStaysHidden", run.IsHidden(FrameIds.DescriptionPaneId), "the description pane is still hidden.");
        checks.Record("forbid.treeStaysShown", !run.IsHidden(ShellRun.TreePaneId) && !run.IsHidden(ShellRun.ToolbarId), "the tree and the toolbar are still shown.");
        (ShapeEntry? disabled, _) = ReadCustomizeEntry(run);
        checks.Record("forbid.customizeDisabled", disabled is { Enabled: false }, "Customize View is disabled.");

        // Allowing it again brings the command back with the switches as they were.
        checks.Record("allow.closes", ChangeAllowed(run, record, "allow", allowed: true, closeWith: OptionsIds.Ok), "the box was set and OK pressed.");
        using (FrameDialog? customize = FrameDialog.Open(run, FrameIds.CustomizeDialog, FrameIds.ViewMenu, FrameIds.CustomizeCommand))
        {
            checks.Record("allow.opens", customize is not null, "Customize View opens again.");
            if (customize is not null)
            {
                checks.Record("allow.kept", !customize.Checked(FrameIds.StatusBar) && !customize.Checked(FrameIds.DescriptionBar) && customize.Checked(FrameIds.ConsoleTree), "the boxes show the switches as they were: status bar and description off, tree on.");
                bool restored = customize.SetChecked(FrameIds.StatusBar, true) & customize.SetChecked(FrameIds.DescriptionBar, true);
                checks.Record("allow.restored", restored, "the two boxes were set again.");
                _ = customize.PressAndClose(FrameIds.CustomizeOk);
            }
        }

        checks.Record("allow.shown", ShellRun.WaitUntil(() => !run.IsHidden(ShellRun.StatusBarId) && !run.IsHidden(FrameIds.DescriptionPaneId)), "the status bar and the description pane show again.");
        run.WriteArtifacts("console-frame-options-switches.png", FrameRun.Area);
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("Console")]
    [TestCategory("E2EOnly")]
    public void Console_Options_Persist_AcrossRestart()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("options", "persist");
        const string name = "console-options-persist";
        GpeditSandbox sandbox = GpeditSandbox.Create(name);
        sandbox.SeedStandard();
        Action<string, bool, string> record = checks.Record;

        using (GpeditRun first = GpeditRun.Open(name, sandbox))
        {
            checks.Record("first.closes", ChangeAllowed(first.Shell, record, "first", allowed: false, closeWith: OptionsIds.Ok), "the customize-views box was cleared and OK pressed.");
            (ShapeEntry? entry, _) = ReadCustomizeEntry(first.Shell);
            checks.Record("first.applied", entry is { Enabled: false }, "Customize View is disabled at once.");

            // The file is written shortly after the change settles; the test waits for it before the window goes.
            bool written = ShellRun.WaitUntil(() => FileSaysAllowed(sandbox) == false, TimeSpan.FromSeconds(20));
            checks.Record("first.fileWritten", written, $"the settings file '{sandbox.SettingsFilePath()}' holds {OptionsIds.SettingsKey} false.");
            first.WriteArtifacts("gpedit-console-options-persist-first.png");
        }

        string? file = sandbox.SettingsFilePath();
        string text = file is null ? string.Empty : File.ReadAllText(file);
        checks.Record("settingsFileWritten", text.Length > 0, $"settings file '{file}' was written.");
        checks.Record("settingsKey", FileSaysAllowed(sandbox) == false, $"the file holds {OptionsIds.SettingsKey} false.");
        checks.Record("otherKeysKept", SwitchKeys.All(k => text.Contains(k, StringComparison.Ordinal)), "the five view switches are still in the file: " + string.Join(", ", SwitchKeys.Where(k => !text.Contains(k, StringComparison.Ordinal))) + " missing.");

        using (GpeditRun second = GpeditRun.Open(name, sandbox, placeWindow: false))
        {
            (ShapeEntry? entry, List<ShapeEntry> entries) = ReadCustomizeEntry(second.Shell);
            checks.Record("second.disabled", entry is { Enabled: false }, $"Customize View is disabled after the restart; entries {MenuShape.Describe(entries)}.");
            using (FrameDialog? dialog = FrameDialog.Open(second.Shell, OptionsIds.Dialog, OptionsIds.FileMenu, OptionsIds.OptionsCommand))
            {
                checks.Record("second.opens", dialog is not null, "File > Options opens.");
                if (dialog is null)
                {
                    checks.AssertAllPassed();
                    return;
                }

                checks.Record("second.box", !dialog.Checked(OptionsIds.CustomizeViews), "the box shows the saved choice: cleared.");
                checks.Record("second.set", dialog.SetChecked(OptionsIds.CustomizeViews, true), "the box was set again.");
                _ = dialog.PressAndClose(OptionsIds.Ok);
            }

            (ShapeEntry? now, _) = ReadCustomizeEntry(second.Shell);
            checks.Record("second.enabled", now is { Enabled: true }, "Customize View is enabled at once.");
            checks.Record("second.fileWritten", ShellRun.WaitUntil(() => FileSaysAllowed(sandbox) != false, TimeSpan.FromSeconds(20)), "the file no longer says false.");
        }

        using (GpeditRun third = GpeditRun.Open(name, sandbox, placeWindow: false))
        {
            (ShapeEntry? entry, _) = ReadCustomizeEntry(third.Shell);
            checks.Record("third.enabled", entry is { Enabled: true }, "Customize View is enabled after the third start: the newest choice is the one saved.");
            third.Measure("settingsFile", File.ReadAllText(sandbox.SettingsFilePath() ?? string.Empty));
            third.WriteArtifacts("gpedit-console-options-persist.png");
        }

        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("Console")]
    [TestCategory("E2EOnly")]
    public void Console_Options_Persist_EverythingHiddenInFile()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("options", "allhidden");
        const string name = "console-options-allhidden";
        GpeditSandbox sandbox = GpeditSandbox.Create(name);
        sandbox.SeedStandard();

        // A saved choice that hides every part and forbids customizing must leave the way back: the menu bar, and
        // File > Options, which is the only place that can allow customizing again.
        sandbox.WriteSettingsFile("""
            {
              "AllowCustomizeViews": false,
              "ViewConsoleTree": false,
              "ViewStandardToolbar": false,
              "ViewSnapInToolbars": false,
              "ViewStatusBar": false,
              "ViewDescriptionBar": false
            }
            """);
        using GpeditRun run = GpeditRun.Open(name, sandbox, placeWindow: false);
        checks.Record("allHidden", ShellRun.WaitUntil(() => run.Shell.IsHidden(ShellRun.TreePaneId) && run.Shell.IsHidden(ShellRun.ToolbarId) && run.Shell.IsHidden(ShellRun.StatusBarId)), "the tree, the toolbar and the status bar are hidden as the file says.");
        checks.Record("menuBarStays", !run.Shell.IsHidden(ShellRun.MenuBarId), "the menu bar shows.");
        (ShapeEntry? customize, _) = ReadCustomizeEntry(run.Shell);
        checks.Record("customizeDisabled", customize is { Enabled: false }, "View > Customize View is disabled as the file says.");
        List<ShapeEntry> file = MenuShape.OpenTop(run.Shell, OptionsIds.FileMenu);
        checks.Record("optionsEnabled", MenuShape.Entry(file, OptionsIds.OptionsCommand) is { Enabled: true }, $"File > Options is enabled; entries {MenuShape.Describe(file)}.");
        _ = MenuShape.Close(run.Shell);

        using FrameDialog? dialog = FrameDialog.Open(run.Shell, OptionsIds.Dialog, OptionsIds.FileMenu, OptionsIds.OptionsCommand);
        checks.Record("optionsOpens", dialog is not null, "File > Options opens with everything hidden.");
        if (dialog is not null)
        {
            checks.Record("boxOff", !dialog.Checked(OptionsIds.CustomizeViews), "the customize-views box shows the saved choice: cleared.");
            checks.Record("boxSet", dialog.SetChecked(OptionsIds.CustomizeViews, true), "the box was set.");
            _ = dialog.PressAndClose(OptionsIds.Ok);
        }

        (ShapeEntry? after, _) = ReadCustomizeEntry(run.Shell);
        checks.Record("customizeBack", after is { Enabled: true }, "View > Customize View is enabled again.");
        using (FrameDialog? view = FrameDialog.Open(run.Shell, FrameIds.CustomizeDialog, FrameIds.ViewMenu, FrameIds.CustomizeCommand))
        {
            checks.Record("customizeOpens", view is not null, "Customize View opens.");
            if (view is not null)
            {
                checks.Record("boxesOff", FrameIds.BuiltBoxes.All(id => !view.Checked(id)), "the five switch boxes show off, as the file says.");
                bool back = view.SetChecked(FrameIds.ConsoleTree, true) & view.SetChecked(FrameIds.StandardToolbar, true) & view.SetChecked(FrameIds.StatusBar, true);
                checks.Record("partsBack", back && ShellRun.WaitUntil(() => !run.Shell.IsHidden(ShellRun.TreePaneId) && !run.Shell.IsHidden(ShellRun.ToolbarId) && !run.Shell.IsHidden(ShellRun.StatusBarId)), "the three parts come back at once.");
                _ = view.PressAndClose(FrameIds.CustomizeOk);
            }
        }

        run.WriteArtifacts("gpedit-console-options-allhidden.png");
        checks.AssertAllPassed();
    }

    /// <summary>
    /// Opens the File popup, records the layout checks of one node kind under <paramref name="id"/> and closes it. Returns
    /// the listing for the measurements file.
    /// </summary>
    private static string CheckFilePopup(Action<string, bool, string> record, ShellRun run, string id, string where)
    {
        List<ShapeEntry> entries = MenuShape.OpenTop(run, OptionsIds.FileMenu);
        string listing = MenuShape.Describe(entries);
        record(id + ".entries", MenuShape.Ids(entries).SequenceEqual([OptionsIds.OptionsCommand, OptionsIds.ExitCommand]), $"{where}: the File popup is Options and Exit only; entries {listing}.");
        record(id + ".groups", MenuShape.Groups(entries).SequenceEqual([1, 1]), $"{where}: one separator between Options and Exit; entries {listing}.");
        record(id + ".enabled", entries.Count == 2 && entries.All(e => e.Enabled), $"{where}: Options and Exit are enabled; entries {listing}.");
        record(
            id + ".labels",
            entries.Count == 2 && entries[0].Name.Contains("Options", StringComparison.OrdinalIgnoreCase) && entries[1].Name.Contains("Exit", StringComparison.OrdinalIgnoreCase),
            $"{where}: the entries are labelled Options and Exit; entries {listing}.");
        _ = MenuShape.Close(run);
        return listing;
    }

    /// <summary>Reads the View popup and returns the Customize View entry and all entries; the popup is closed again.</summary>
    private static (ShapeEntry? Entry, List<ShapeEntry> Entries) ReadCustomizeEntry(ShellRun run)
    {
        List<ShapeEntry> entries = MenuShape.OpenTop(run, FrameIds.ViewMenu);
        ShapeEntry? entry = MenuShape.Entry(entries, FrameIds.CustomizeCommand);
        _ = MenuShape.Close(run);
        return (entry, entries);
    }

    /// <summary>
    /// Opens Options, sets the customize-views box and presses a button. Records <paramref name="id"/>.opens, .set and .closed.
    /// True when the dialog opened, the box took the value and the dialog closed.
    /// </summary>
    private static bool ChangeAllowed(ShellRun run, Action<string, bool, string> record, string id, bool allowed, string closeWith)
    {
        using FrameDialog? dialog = FrameDialog.Open(run, OptionsIds.Dialog, OptionsIds.FileMenu, OptionsIds.OptionsCommand);
        record(id + ".opens", dialog is not null, "File > Options opens the dialog.");
        if (dialog is null)
        {
            return false;
        }

        bool set = dialog.SetChecked(OptionsIds.CustomizeViews, allowed);
        record(id + ".set", set, $"the customize-views box was set to {allowed}.");
        bool closed = dialog.PressAndClose(closeWith);
        record(id + ".closed", closed, "the dialog closed.");
        return set && closed;
    }

    /// <summary>The value of the customize-views key in the settings file: null when the file or the key is absent.</summary>
    private static bool? FileSaysAllowed(GpeditSandbox sandbox)
    {
        string? path = sandbox.SettingsFilePath();
        if (path is null)
        {
            return null;
        }

        try
        {
            Match match = Regex.Match(File.ReadAllText(path), "\"" + OptionsIds.SettingsKey + "\"\\s*:\\s*(true|false)", RegexOptions.CultureInvariant);
            return match.Success ? match.Groups[1].Value == "true" : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

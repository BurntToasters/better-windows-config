using System.Drawing;
using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Bwc.E2E.Dialogs.ClassicDialogTests;

namespace Bwc.E2E.Gpedit;

/// <summary>
/// Tests of the policy-setting dialog of the Local Group Policy Editor over the F1 fixture templates and a sandbox policy
/// object that the test seeds. The failure modes are in docs/failure-modes/gpedit-dialog.md; the section number is named in
/// each test. The dialog is opened the way a user opens it (Enter or a double click on a policy row), driven through UI
/// Automation and real keys, and the checks go to the E2E result artifact. Nothing is written to the policy files in this
/// piece, so the sandbox files are only hashed. Every test writes
/// artifacts/e2e/visual/gpedit/gpedit-&lt;name&gt;.json and pictures.
/// </summary>
[TestClass]
public sealed class GpeditDialogTests
{
    private const string Root = GpeditRun.RootName;
    private const string Computer = "Computer Configuration";
    private const string User = "User Configuration";
    private const string Admin = "Administrative Templates";
    private const string Oracle = "BWC Oracle";
    private const string Basics = "Basics";

    private const string MachineFlag = "Class machine flag setting";
    private const string BothFlag = "Class both flag setting";
    private const string BothElement = "Class both with an element setting";
    private const string DecimalValues = "Decimal enabled and disabled values setting";
    private const string LastBasics = "No value name, elements only setting";

    // The default order of the policy rows is the order of the policy ids (F/Sort_Columns op 17), so Previous and Next walk
    // Class both with an element, Class both flag, Class machine flag, Element key differs from policy key, and so on.
    private const string NextAfterMachine = "Element key differs from policy key setting";
    private const string NextAfterMachineTitle = "Element key differs from policy key";

    private const string MachineHelp = "A machine policy with a value name and no explicit values. Enabled writes 1 and Disabled writes 0.";
    private const string AnySystem = "BWC oracle fixture: any system";

    private const double ClientWidth = 684;
    private const double ClientHeight = 604;
    private const double MinimumClientHeight = 601;
    private const double Tolerance = 2.5;

    /// <summary>
    /// The control rectangles of the classic form at 100 percent: x, y,
    /// width, height in DIP, relative to the client.
    /// </summary>
    private static readonly (string Id, float X, float Y, float W, float H)[] FormLayout =
    [
        ("pictureBox", 6, 14, 16, 16),
        (GpeditDialog.Title, 28, 11, 340, 23),
        (GpeditDialog.Previous, 374, 14, 102, 25),
        (GpeditDialog.Next, 482, 14, 102, 25),
        (GpeditDialog.NotConfigured, 9, 54, 108, 19),
        (GpeditDialog.Enabled, 9, 79, 67, 19),
        (GpeditDialog.Disabled, 9, 104, 70, 19),
        ("labelComment", 129, 51, 82, 78),
        ("labelSupportedOn", 129, 129, 82, 52),
        (GpeditDialog.Comment, 217, 54, 458, 72),
        (GpeditDialog.SupportedOn, 217, 132, 458, 46),
        ("label2", 3, 190, 52, 15),
        (GpeditDialog.OptionsPane, 3, 227, 298, 329),
        ("label1", 309, 190, 35, 15),
        (GpeditDialog.Help, 309, 227, 370, 329),
        (GpeditDialog.Ok, 438, 567, 75, 25),
        (GpeditDialog.Cancel, 519, 567, 75, 25),
        (GpeditDialog.Apply, 600, 567, 75, 25),
    ];

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    // ---- 2, 4, 5, 12: the frame ----------------------------------------------------------------

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Dialog_Frame()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("dialog", "frame");
        using GpeditRun run = Start("gpedit-dialog-frame", out _);
        run.GoTo(Root, Computer, Admin, Oracle, Basics);

        using GpeditDialog? dialog = GpeditDialog.Open(run, MachineFlag, byKey: false);
        checks.Record("opens", dialog is not null, "a double click on a policy row opens the setting dialog.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        checks.Record("caption", dialog.Caption() == "Class machine flag", $"caption='{dialog.Caption()}'.");
        checks.Record("titleLabel", dialog.TextOf(GpeditDialog.Title) == "Class machine flag", $"title label='{dialog.TextOf(GpeditDialog.Title)}'.");
        checks.Record("supportedOn", dialog.TextOf(GpeditDialog.SupportedOn) == AnySystem, $"supported on='{dialog.TextOf(GpeditDialog.SupportedOn)}'.");
        checks.Record("help", dialog.TextOf(GpeditDialog.Help) == MachineHelp, $"help='{dialog.TextOf(GpeditDialog.Help)}'.");
        checks.Record("supportedOnReadOnly", dialog.IsReadOnly(GpeditDialog.SupportedOn), "the Supported on box is read-only.");
        checks.Record("helpReadOnly", dialog.IsReadOnly(GpeditDialog.Help), "the Help box is read-only.");
        checks.Record("commentEditable", !dialog.IsReadOnly(GpeditDialog.Comment), "the Comment box is editable.");
        checks.Record(
            "radios",
            dialog.IsSelected(GpeditDialog.Enabled) && !dialog.IsSelected(GpeditDialog.NotConfigured) && !dialog.IsSelected(GpeditDialog.Disabled),
            "Enabled is the selected state of an enabled policy.");
        checks.Record("comment", dialog.TextOf(GpeditDialog.Comment) == "machine note", $"comment='{dialog.TextOf(GpeditDialog.Comment)}'.");
        checks.Record(
            "buttons",
            dialog.IsEnabled(GpeditDialog.Ok) && dialog.IsEnabled(GpeditDialog.Cancel) && !dialog.IsEnabled(GpeditDialog.Apply),
            "OK and Cancel are enabled and Apply is disabled when nothing changed.");
        checks.Record("navigation", dialog.IsEnabled(GpeditDialog.Previous) && dialog.IsEnabled(GpeditDialog.Next), "Previous and Next are enabled in the middle of the list.");
        checks.Record("ownerDisabled", !run.Shell.Session.IsEnabled, "the main window is disabled while the dialog is open.");

        string[] names = new[] { GpeditDialog.Previous, GpeditDialog.Next, GpeditDialog.NotConfigured, GpeditDialog.Enabled, GpeditDialog.Disabled, GpeditDialog.Ok, GpeditDialog.Cancel }
            .Select(id => ShellRun.SafeName(dialog.Find(id, 1))).ToArray();
        checks.Record(
            "names",
            names.SequenceEqual(["Previous Setting", "Next Setting", "Not Configured", "Enabled", "Disabled", "OK", "Cancel"]),
            $"names={string.Join("|", names)}.");
        string applyName = ShellRun.SafeName(dialog.Find(GpeditDialog.Apply, 1));
        checks.Record("applyName", applyName == "Apply", $"apply name='{applyName}'.");

        // Geometry against the forms data of classic (client 684 x 604 DIP, rectangles relative to the client).
        SizeF client = dialog.ClientDip();
        checks.Record(
            "clientSize",
            Math.Abs(client.Width - ClientWidth) <= Tolerance && Math.Abs(client.Height - ClientHeight) <= Tolerance,
            $"client={client.Width:0.#}x{client.Height:0.#} DIP, expected {ClientWidth}x{ClientHeight}.");
        var layoutNotes = new List<string>();
        bool layoutOk = true;
        foreach ((string id, float x, float y, float w, float h) in FormLayout)
        {
            RectangleF? rect = dialog.RectDip(id);
            if (rect is not { } r)
            {
                layoutOk = false;
                layoutNotes.Add($"{id}: missing");
                continue;
            }

            bool near = Math.Abs(r.X - x) <= Tolerance && Math.Abs(r.Y - y) <= Tolerance
                && Math.Abs(r.Width - w) <= Tolerance && Math.Abs(r.Height - h) <= Tolerance;
            layoutOk &= near;
            layoutNotes.Add($"{id}: {r.X:0.#},{r.Y:0.#},{r.Width:0.#},{r.Height:0.#} (expected {x},{y},{w},{h}){(near ? string.Empty : " DIFFERS")}");
        }

        checks.Record("layout", layoutOk, string.Join("; ", layoutNotes));
        run.Measure("layout", layoutNotes);

        // Focus and tab order: the probe of classic starts on Previous Setting, then goes through the controls in this order.
        checks.Record("initialFocus", ShellRun.WaitUntil(() => dialog.FocusedId() == GpeditDialog.Previous, TimeSpan.FromSeconds(3)), $"focus='{dialog.FocusedId()}'.");
        string[] tabs = dialog.TabSequence(10);
        string[] expectedTabs =
        [
            GpeditDialog.Next, GpeditDialog.NotConfigured, GpeditDialog.Enabled, GpeditDialog.Disabled, GpeditDialog.Comment,
            GpeditDialog.SupportedOn, GpeditDialog.Help, GpeditDialog.Ok, GpeditDialog.Cancel, GpeditDialog.Previous,
        ];
        checks.Record("tabOrder", tabs.SequenceEqual(expectedTabs), $"tabs={string.Join(",", tabs)}.");
        run.Measure("tabs", tabs);
        _ = dialog.Screenshot("gpedit-dialog-frame-dialog.png");

        // Escape cancels, gives the owner back and leaves the focus on the list with the same row selected.
        dialog.Press(VirtualKeyShort.ESCAPE);
        checks.Record("escapeCloses", dialog.WaitClosed(), "Escape closes the dialog.");
        checks.Record("ownerEnabled", ShellRun.WaitUntil(() => run.Shell.Session.IsEnabled, TimeSpan.FromSeconds(5)), "the main window is enabled after the dialog closes.");
        checks.Record("focusReturns", ShellRun.WaitUntil(() => run.Shell.FocusedPane() == "result", TimeSpan.FromSeconds(5)), $"focused pane={run.Shell.FocusedPane()}.");
        checks.Record("selectionKept", run.DescriptionTitle == "Class machine flag", $"description title='{run.DescriptionTitle}'.");
        run.WriteArtifacts("gpedit-dialog-frame.png");
        checks.AssertAllPassed();
    }

    // ---- 1, 12: initial state ------------------------------------------------------------------

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Dialog_InitialStates()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("dialog", "states");
        using GpeditRun run = Start("gpedit-dialog-states", out _);

        run.GoTo(Root, Computer, Admin, Oracle, Basics);
        (string Row, string State, string Comment, bool ByKey)[] computer =
        [
            (BothFlag, GpeditDialog.Enabled, "both note", true),
            (BothElement, GpeditDialog.NotConfigured, string.Empty, false),
            (DecimalValues, GpeditDialog.Disabled, string.Empty, true),
            (MachineFlag, GpeditDialog.Enabled, "machine note", false),
        ];
        foreach ((string row, string state, string comment, bool byKey) in computer)
        {
            CheckInitial(checks, run, "computer", row, state, comment, byKey);
        }

        // A class Both policy reads the file of the side that is shown: Enabled with a comment on the computer side, Disabled
        // with none on the user side.
        run.GoTo(Root, User, Admin, Oracle, Basics);
        CheckInitial(checks, run, "user", BothFlag, GpeditDialog.Disabled, string.Empty, byKey: true);
        run.WriteArtifacts("gpedit-dialog-states.png");
        checks.AssertAllPassed();
    }

    private static void CheckInitial(GpeditChecks checks, GpeditRun run, string side, string row, string state, string comment, bool byKey)
    {
        string key = side + "." + row.Replace(' ', '_');
        using GpeditDialog? dialog = GpeditDialog.Open(run, row, byKey);
        if (dialog is null)
        {
            checks.Record(key + ".opens", false, $"no dialog for '{row}' (byKey={byKey}).");
            return;
        }

        string[] ids = [GpeditDialog.NotConfigured, GpeditDialog.Enabled, GpeditDialog.Disabled];
        string selected = string.Join(",", ids.Where(dialog.IsSelected));
        checks.Record(key + ".state", selected == state, $"selected='{selected}', expected '{state}'.");
        checks.Record(key + ".comment", dialog.TextOf(GpeditDialog.Comment) == comment, $"comment='{dialog.TextOf(GpeditDialog.Comment)}', expected '{comment}'.");
        checks.Record(key + ".clean", !dialog.IsEnabled(GpeditDialog.Apply), "Apply is disabled at open.");
        dialog.Press(VirtualKeyShort.ESCAPE);
        checks.Record(key + ".closed", dialog.WaitClosed() && ShellRun.WaitUntil(() => run.Shell.Session.IsEnabled, TimeSpan.FromSeconds(5)), "Escape closes the dialog and the owner is enabled.");
    }

    // ---- 3: ampersand --------------------------------------------------------------------------

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Dialog_Literal()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("dialog", "literal");
        using GpeditRun run = Start("gpedit-dialog-literal", out _);
        run.GoTo(Root, Computer, Admin, Oracle, "Tools & Utilities");

        const string Name = "Research & Development flag";
        using GpeditDialog? dialog = GpeditDialog.Open(run, Name + " setting");
        checks.Record("opens", dialog is not null, "the ampersand policy opens.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        checks.Record("caption", dialog.Caption() == Name, $"caption='{dialog.Caption()}'.");
        string label = dialog.TextOf(GpeditDialog.Title);
        string labelName = ShellRun.SafeName(dialog.Find(GpeditDialog.Title, 1));
        checks.Record("titleLabel", label == Name && labelName == Name, $"label text='{label}', UIA name='{labelName}'.");
        checks.Record(
            "help",
            dialog.TextOf(GpeditDialog.Help).StartsWith("The display name has a literal ampersand.", StringComparison.Ordinal),
            $"help='{dialog.TextOf(GpeditDialog.Help)}'.");

        // The real access keys still work: Alt+E selects Enabled, Alt+D selects Disabled.
        bool applyBefore = dialog.IsEnabled(GpeditDialog.Apply);
        _ = SendAltLetter('E');
        bool enabled = ShellRun.WaitUntil(() => dialog.IsSelected(GpeditDialog.Enabled), TimeSpan.FromSeconds(3));
        checks.Record("altEnabled", !applyBefore && enabled && dialog.IsEnabled(GpeditDialog.Apply), $"Alt+E selected Enabled={enabled}, Apply enabled={dialog.IsEnabled(GpeditDialog.Apply)}.");
        _ = SendAltLetter('D');
        checks.Record("altDisabled", ShellRun.WaitUntil(() => dialog.IsSelected(GpeditDialog.Disabled), TimeSpan.FromSeconds(3)), "Alt+D selected Disabled.");
        checks.Record("noThrow", dialog.Find(GpeditDialog.Ok, 1) is not null, "the dialog is still alive and shows OK.");
        dialog.Press(VirtualKeyShort.ESCAPE);
        _ = dialog.WaitClosed();
        run.WriteArtifacts("gpedit-dialog-literal.png");
        checks.AssertAllPassed();
    }

    // ---- 6, 7: Apply and OK --------------------------------------------------------------------

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Dialog_ApplyOk()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("dialog", "applyok");
        using GpeditRun run = Start("gpedit-dialog-applyok", out _);
        run.GoTo(Root, Computer, Admin, Oracle, Basics);

        using (GpeditDialog? dialog = GpeditDialog.Open(run, MachineFlag))
        {
            checks.Record("opens", dialog is not null, "the dialog opens.");
            if (dialog is null)
            {
                checks.AssertAllPassed();
                return;
            }

            checks.Record("applyCleanAtOpen", !dialog.IsEnabled(GpeditDialog.Apply), "Apply is disabled at open.");
            _ = dialog.Select(GpeditDialog.Disabled);
            checks.Record("applyAfterState", dialog.IsEnabled(GpeditDialog.Apply), "Apply is enabled after a state change.");
            _ = dialog.Select(GpeditDialog.Enabled);
            checks.Record("applyRevert", !dialog.IsEnabled(GpeditDialog.Apply), "Apply is disabled again after the state is changed back.");
            _ = dialog.SetComment("changed note");
            checks.Record("applyAfterComment", dialog.IsEnabled(GpeditDialog.Apply), "Apply is enabled after a comment-only change.");
            _ = dialog.SetComment("machine note");
            checks.Record("applyCommentRevert", !dialog.IsEnabled(GpeditDialog.Apply), "Apply is disabled after the comment is restored.");

            _ = dialog.Select(GpeditDialog.Disabled);
            _ = dialog.SetComment("changed note");
            checks.Record("dirtyReadout", dialog.Read("Dirty") == "1", $"readout Dirty={dialog.Read("Dirty")}.");
            _ = dialog.Invoke(GpeditDialog.Apply);
            checks.Record("applyKeepsOpen", dialog.Find(GpeditDialog.Ok, 2) is not null, "Apply does not close the dialog.");
            checks.Record("applyCleansDirty", ShellRun.WaitUntil(() => !dialog.IsEnabled(GpeditDialog.Apply), TimeSpan.FromSeconds(3)), "Apply is disabled after a successful Apply.");
            string[] cells = CellsOf(run, MachineFlag);
            checks.Record("listRefreshedByApply", cells.ElementAtOrDefault(1) == "Disabled" && cells.ElementAtOrDefault(2) == "Yes", $"list cells={string.Join("|", cells)}.");
            dialog.Screenshot("gpedit-dialog-applyok-dialog.png");

            _ = dialog.Invoke(GpeditDialog.Ok);
            checks.Record("okCloses", dialog.WaitClosed(), "OK closes the dialog.");
        }

        checks.Record("ownerBack", ShellRun.WaitUntil(() => run.Shell.Session.IsEnabled, TimeSpan.FromSeconds(5)), "the main window is enabled again.");
        checks.Record("focusAfterOk", ShellRun.WaitUntil(() => run.Shell.FocusedPane() == "result", TimeSpan.FromSeconds(5)), $"focused pane={run.Shell.FocusedPane()}.");

        // The values stayed in memory: the dialog reads them again.
        using (GpeditDialog? again = GpeditDialog.Open(run, MachineFlag))
        {
            checks.Record("reopens", again is not null, "the policy opens again.");
            if (again is not null)
            {
                checks.Record("reopenState", again.IsSelected(GpeditDialog.Disabled), "Disabled is shown after the reopen.");
                checks.Record("reopenComment", again.TextOf(GpeditDialog.Comment) == "changed note", $"comment='{again.TextOf(GpeditDialog.Comment)}'.");
                checks.Record("reopenClean", !again.IsEnabled(GpeditDialog.Apply), "Apply is disabled after the reopen.");
                _ = again.SetComment(string.Empty);
                _ = again.Invoke(GpeditDialog.Ok);
                _ = again.WaitClosed();
            }
        }

        string[] cleared = CellsOf(run, MachineFlag);
        checks.Record("clearedComment", cleared.ElementAtOrDefault(1) == "Disabled" && cleared.ElementAtOrDefault(2) == "No", $"list cells after clearing={string.Join("|", cleared)}.");

        // OK with nothing changed closes and changes nothing.
        using (GpeditDialog? plain = GpeditDialog.Open(run, BothFlag))
        {
            string[] before = CellsOf(run, BothFlag);
            if (plain is not null)
            {
                _ = plain.Invoke(GpeditDialog.Ok);
                checks.Record("okNoChangeCloses", plain.WaitClosed(), "OK with nothing changed closes the dialog.");
            }
            else
            {
                checks.Record("okNoChangeCloses", false, "no dialog.");
            }

            string[] after = CellsOf(run, BothFlag);
            checks.Record("okNoChangeKeeps", before.SequenceEqual(after) && after.ElementAtOrDefault(1) == "Enabled", $"before={string.Join("|", before)}, after={string.Join("|", after)}.");
        }

        run.WriteArtifacts("gpedit-dialog-applyok.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Dialog_Cancel()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("dialog", "cancel");
        using GpeditRun run = Start("gpedit-dialog-cancel", out _);
        run.GoTo(Root, Computer, Admin, Oracle, Basics);

        foreach (string how in new[] { "button", "escape" })
        {
            using GpeditDialog? dialog = GpeditDialog.Open(run, MachineFlag);
            checks.Record(how + ".opens", dialog is not null, "the dialog opens.");
            if (dialog is null)
            {
                continue;
            }

            _ = dialog.Select(GpeditDialog.Disabled);
            _ = dialog.SetComment("never kept");
            if (how == "button")
            {
                _ = dialog.Invoke(GpeditDialog.Cancel);
            }
            else
            {
                dialog.Press(VirtualKeyShort.ESCAPE);
            }

            checks.Record(how + ".closed", dialog.WaitClosed(), "the dialog closes.");
            _ = ShellRun.WaitUntil(() => run.Shell.Session.IsEnabled, TimeSpan.FromSeconds(5));
            string[] cells = CellsOf(run, MachineFlag);
            checks.Record(how + ".unchanged", cells.ElementAtOrDefault(1) == "Enabled" && cells.ElementAtOrDefault(2) == "Yes", $"list cells={string.Join("|", cells)}.");
        }

        using (GpeditDialog? last = GpeditDialog.Open(run, MachineFlag))
        {
            checks.Record("reopenKeepsOriginal", last is not null && last.IsSelected(GpeditDialog.Enabled) && last.TextOf(GpeditDialog.Comment) == "machine note", "a canceled change is not shown again.");
            last?.Press(VirtualKeyShort.ESCAPE);
            _ = last?.WaitClosed();
        }

        run.WriteArtifacts("gpedit-dialog-cancel.png");
        checks.AssertAllPassed();
    }

    // ---- 8: OK and Apply save to disk ----------------------------------------------------------

    // G4 changed this test: in G3a OK and Apply changed memory only and the test required that no file changed (it was
    // called Gpedit_Dialog_OkWritesNothing). From G4 on they save the policy object, so the test requires that the files
    // changed, that the list shows the saved state, and that the saved files stay as they are after the editor exits.
    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Dialog_OkSaves()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("dialog", "save");
        GpeditRun run = Start("gpedit-dialog-save", out GpeditSandbox sandbox);
        SortedDictionary<string, string> before = sandbox.PolicyFileHashes();
        SortedDictionary<string, string>? saved = null;
        try
        {
            checks.Record("seeded", before.Values.Any(v => v != "absent"), $"hashes before={Describe(before)}.");
            run.GoTo(Root, Computer, Admin, Oracle, Basics);

            using (GpeditDialog? dialog = GpeditDialog.Open(run, MachineFlag))
            {
                checks.Record("opens", dialog is not null, "the dialog opens.");
                if (dialog is not null)
                {
                    _ = dialog.Select(GpeditDialog.Disabled);
                    _ = dialog.SetComment("not on disk");
                    _ = dialog.Invoke(GpeditDialog.Apply);
                    _ = dialog.Invoke(GpeditDialog.Ok);
                    checks.Record("okCloses", dialog.WaitClosed(), "OK closes the dialog.");
                }
            }

            using (GpeditDialog? second = GpeditDialog.Open(run, BothFlag))
            {
                if (second is not null)
                {
                    _ = second.Select(GpeditDialog.NotConfigured);
                    _ = second.Invoke(GpeditDialog.Ok);
                    _ = second.WaitClosed();
                }
            }

            string[] applied = CellsOf(run, MachineFlag);
            string[] both = CellsOf(run, BothFlag);
            checks.Record(
                "appliedShown",
                applied.ElementAtOrDefault(1) == "Disabled" && applied.ElementAtOrDefault(2) == "Yes" && both.ElementAtOrDefault(1) == "Not configured",
                $"machine flag={string.Join("|", applied)}, both flag={string.Join("|", both)}: the list shows what was saved.");

            SortedDictionary<string, string> after = sandbox.PolicyFileHashes();
            saved = after;
            checks.Record("filesChanged", !before.SequenceEqual(after), $"before={Describe(before)}; after={Describe(after)}.");
            run.Measure("hashesBefore", before);
            run.Measure("hashesAfter", after);
            run.WriteArtifacts("gpedit-dialog-save.png");
        }
        finally
        {
            run.Dispose();
        }

        // The editor has exited: closing it saves nothing more, and what OK saved is still on disk.
        checks.Record("filesKeptAfterExit", saved is not null && saved.SequenceEqual(sandbox.PolicyFileHashes()), "the policy files are still the saved bytes after the editor exited.");
        checks.AssertAllPassed();
    }

    // ---- 9: Previous and Next ------------------------------------------------------------------

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Dialog_PreviousNext()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("dialog", "nav");
        using GpeditRun run = Start("gpedit-dialog-nav", out _);
        run.GoTo(Root, Computer, Admin, Oracle, Basics);

        using (GpeditDialog? dialog = GpeditDialog.Open(run, MachineFlag))
        {
            checks.Record("opens", dialog is not null, "the dialog opens on the third policy of the list.");
            if (dialog is null)
            {
                checks.AssertAllPassed();
                return;
            }

            checks.Record("position", dialog.Read("Index") == "2" && dialog.Read("Count") == "12", $"index={dialog.Read("Index")}, count={dialog.Read("Count")}.");

            _ = dialog.Invoke(GpeditDialog.Next);
            checks.Record("nextTitle", ShellRun.WaitUntil(() => dialog.Caption() == NextAfterMachineTitle, TimeSpan.FromSeconds(3)), $"caption='{dialog.Caption()}'.");
            checks.Record("nextTitleLabel", dialog.TextOf(GpeditDialog.Title) == NextAfterMachineTitle, $"title label='{dialog.TextOf(GpeditDialog.Title)}'.");
            checks.Record("nextState", dialog.IsSelected(GpeditDialog.NotConfigured) && dialog.TextOf(GpeditDialog.Comment).Length == 0, "the next policy shows its own state and comment.");
            checks.Record("nextClean", !dialog.IsEnabled(GpeditDialog.Apply), "Apply is disabled after a move.");
            checks.Record("nextPosition", dialog.Read("Index") == "3", $"index={dialog.Read("Index")}.");
            checks.Record("nextTexts", dialog.TextOf(GpeditDialog.Help) != MachineHelp && dialog.TextOf(GpeditDialog.Help).Length > 0, "the help text changed with the policy.");

            // A typed comment on this policy is applied by the move, as Apply does (OQ-D1).
            _ = dialog.SetComment("decimal note");
            _ = dialog.Invoke(GpeditDialog.Previous);
            checks.Record("previousTitle", ShellRun.WaitUntil(() => dialog.Caption() == "Class machine flag", TimeSpan.FromSeconds(3)), $"caption='{dialog.Caption()}'.");
            checks.Record("previousState", dialog.IsSelected(GpeditDialog.Enabled) && dialog.TextOf(GpeditDialog.Comment) == "machine note", "the previous policy shows its own state and comment.");
            string[] nextCells = CellsOf(run, NextAfterMachine);
            checks.Record("commentKeptOnMove", nextCells.ElementAtOrDefault(2) == "Yes", $"next row cells={string.Join("|", nextCells)}.");

            // A pending state change is applied by the move too.
            _ = dialog.Select(GpeditDialog.Disabled);
            _ = dialog.Invoke(GpeditDialog.Next);
            _ = ShellRun.WaitUntil(() => dialog.Caption() == NextAfterMachineTitle, TimeSpan.FromSeconds(3));
            checks.Record("commentRestored", dialog.TextOf(GpeditDialog.Comment) == "decimal note", $"comment='{dialog.TextOf(GpeditDialog.Comment)}'.");
            string[] machineCells = CellsOf(run, MachineFlag);
            checks.Record("stateAppliedOnMove", machineCells.ElementAtOrDefault(1) == "Disabled", $"machine flag cells={string.Join("|", machineCells)}.");
            dialog.Screenshot("gpedit-dialog-nav-dialog.png");
            dialog.Press(VirtualKeyShort.ESCAPE);
            checks.Record("closes", dialog.WaitClosed(), "Escape closes the dialog on the second policy.");
        }

        // After closing the list selects the last policy shown and holds the focus.
        checks.Record("selectionFollows", ShellRun.WaitUntil(() => run.DescriptionTitle == NextAfterMachineTitle, TimeSpan.FromSeconds(5)), $"description title='{run.DescriptionTitle}'.");
        checks.Record("focusFollows", ShellRun.WaitUntil(() => run.Shell.FocusedPane() == "result", TimeSpan.FromSeconds(5)), $"focused pane={run.Shell.FocusedPane()}.");

        // The ends of the list.
        using (GpeditDialog? first = GpeditDialog.Open(run, BothElement))
        {
            checks.Record("firstEnds", first is not null && !first.IsEnabled(GpeditDialog.Previous) && first.IsEnabled(GpeditDialog.Next), "Previous is disabled on the first policy and Next is enabled.");
            first?.Press(VirtualKeyShort.ESCAPE);
            _ = first?.WaitClosed();
        }

        using (GpeditDialog? last = GpeditDialog.Open(run, LastBasics))
        {
            checks.Record("lastEnds", last is not null && last.IsEnabled(GpeditDialog.Previous) && !last.IsEnabled(GpeditDialog.Next), "Next is disabled on the last policy and Previous is enabled.");
            last?.Press(VirtualKeyShort.ESCAPE);
            _ = last?.WaitClosed();
        }

        // Folder rows are not policies: the BWC Oracle user node lists three folders and one policy.
        run.GoTo(Root, User, Admin, Oracle);
        using (GpeditDialog? single = GpeditDialog.Open(run, "Top level user flag setting"))
        {
            checks.Record(
                "singleEnds",
                single is not null && !single.IsEnabled(GpeditDialog.Previous) && !single.IsEnabled(GpeditDialog.Next) && single.Read("Count") == "1",
                $"count={single?.Read("Count")}: folders are not counted and a single policy has no neighbor.");
            single?.Press(VirtualKeyShort.ESCAPE);
            _ = single?.WaitClosed();
        }

        run.WriteArtifacts("gpedit-dialog-nav.png");
        checks.AssertAllPassed();
    }

    // ---- 10: read-only -------------------------------------------------------------------------

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Dialog_ReadOnly()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("dialog", "readonly");
        using GpeditRun run = Start("gpedit-dialog-readonly", out GpeditSandbox sandbox, readOnly: true);
        run.GoTo(Root, Computer, Admin, Oracle, Basics);

        using GpeditDialog? dialog = GpeditDialog.Open(run, MachineFlag);
        checks.Record("opens", dialog is not null, "the dialog opens in a read-only store.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        checks.Record("readout", dialog.Read("ReadOnly") == "1", $"readout ReadOnly={dialog.Read("ReadOnly")}.");
        checks.Record(
            "radiosDisabled",
            !dialog.IsEnabled(GpeditDialog.NotConfigured) && !dialog.IsEnabled(GpeditDialog.Enabled) && !dialog.IsEnabled(GpeditDialog.Disabled),
            "the state radio buttons are disabled.");
        checks.Record("stateShown", dialog.IsSelected(GpeditDialog.Enabled), "the state is still shown.");
        checks.Record("commentReadOnly", dialog.IsReadOnly(GpeditDialog.Comment) && dialog.TextOf(GpeditDialog.Comment) == "machine note", "the Comment box is read-only and shows the comment.");
        checks.Record("applyDisabled", !dialog.IsEnabled(GpeditDialog.Apply), "Apply is disabled.");

        // G4: the dialog says why it cannot change anything, so OK closing without a save is not silent.
        string notice = dialog.Find("DialogReadOnlyNotice", 3) is null ? string.Empty : dialog.TextOf("DialogReadOnlyNotice");
        checks.Record("notice", !string.IsNullOrWhiteSpace(notice), $"the dialog shows the read-only notice '{notice}'.");
        checks.Record("navigationWorks", dialog.IsEnabled(GpeditDialog.Previous) && dialog.IsEnabled(GpeditDialog.Next), "Previous and Next still move.");
        bool changed = dialog.Select(GpeditDialog.Disabled);
        checks.Record("noChange", !changed || dialog.IsSelected(GpeditDialog.Enabled), "a state cannot be selected.");

        // classic-dialog.md 3.6: a disabled radio button does not take its access key, as a disabled button does not.
        _ = SendAltLetter('D');
        Thread.Sleep(500);
        checks.Record(
            "altDoesNotChangeDisabledRadio",
            dialog.IsSelected(GpeditDialog.Enabled) && !dialog.IsSelected(GpeditDialog.Disabled) && !dialog.IsEnabled(GpeditDialog.Apply),
            $"after Alt+D: Enabled selected={dialog.IsSelected(GpeditDialog.Enabled)}, Disabled selected={dialog.IsSelected(GpeditDialog.Disabled)}, Apply enabled={dialog.IsEnabled(GpeditDialog.Apply)}.");
        SortedDictionary<string, string> hashes = sandbox.PolicyFileHashes();
        _ = dialog.Invoke(GpeditDialog.Ok);
        checks.Record("okCloses", dialog.WaitClosed(), "OK closes the dialog without applying (the notice says why).");
        checks.Record("okWritesNothing", hashes.SequenceEqual(sandbox.PolicyFileHashes()), "OK in read-only mode wrote no file.");
        string[] cells = CellsOf(run, MachineFlag);
        checks.Record("listUnchanged", cells.ElementAtOrDefault(1) == "Enabled" && cells.ElementAtOrDefault(2) == "Yes", $"list cells={string.Join("|", cells)}.");
        run.WriteArtifacts("gpedit-dialog-readonly.png");
        checks.AssertAllPassed();
    }

    // ---- 11: validation ------------------------------------------------------------------------

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Dialog_Validation()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("dialog", "validation");
        using GpeditRun run = Start("gpedit-dialog-validation", out _);
        run.GoTo(Root, Computer, Admin, Oracle, "Elements", "Texts");

        using GpeditDialog? dialog = GpeditDialog.Open(run, "Text that is required setting");
        checks.Record("opens", dialog is not null, "a policy with a required element opens.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        _ = dialog.Select(GpeditDialog.Enabled);
        _ = dialog.Invoke(GpeditDialog.Ok);
        AutomationElement? message = dialog.Find("DialogMessage", 8);
        checks.Record("messageShown", message is not null, "OK with an empty required element shows a message.");
        string text = ShellRun.SafeName(message);
        checks.Record("messageText", text.StartsWith("No value was entered for this field.", StringComparison.Ordinal), $"message='{text}'.");
        AutomationElement? messageWindow = message is null ? null : ClassicDialogTestsBridge.WindowOf(message);
        string caption = ShellRun.SafeName(messageWindow);
        checks.Record(
            "messageCaption",
            caption.StartsWith("Field \"", StringComparison.Ordinal) && caption.Contains("Required text", StringComparison.Ordinal) && caption.EndsWith("is required", StringComparison.Ordinal),
            $"caption='{caption}'.");

        dialog.Press(VirtualKeyShort.RETURN);
        checks.Record("messageDismissed", ShellRun.WaitUntil(() => dialog.Find("DialogMessage", 0.3) is null, TimeSpan.FromSeconds(8)), "Enter dismisses the message.");
        checks.Record("dialogStaysOpen", dialog.Find(GpeditDialog.Ok, 3) is not null && dialog.IsSelected(GpeditDialog.Enabled), "the dialog stays open with Enabled selected.");
        checks.Record("stillDirty", dialog.IsEnabled(GpeditDialog.Apply), "Apply stays enabled: the change is not lost.");
        string[] cells = CellsOf(run, "Text that is required setting");
        checks.Record("nothingApplied", cells.ElementAtOrDefault(1) == "Not configured", $"list cells={string.Join("|", cells)}.");
        dialog.Screenshot("gpedit-dialog-validation-dialog.png");
        dialog.Press(VirtualKeyShort.ESCAPE);
        checks.Record("cancels", dialog.WaitClosed(), "Escape closes the dialog.");
        run.WriteArtifacts("gpedit-dialog-validation.png");
        checks.AssertAllPassed();
    }

    // ---- 4: resize -----------------------------------------------------------------------------

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Dialog_Resize()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("dialog", "resize");
        using GpeditRun run = Start("gpedit-dialog-resize", out _);
        run.GoTo(Root, Computer, Admin, Oracle, Basics);

        using GpeditDialog? dialog = GpeditDialog.Open(run, MachineFlag);
        checks.Record("opens", dialog is not null, "the dialog opens.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        RectangleF commentBefore = dialog.RectDip(GpeditDialog.Comment) ?? default;
        RectangleF previousBefore = dialog.RectDip(GpeditDialog.Previous) ?? default;
        RectangleF optionsBefore = dialog.RectDip(GpeditDialog.OptionsPane) ?? default;
        RectangleF helpBefore = dialog.RectDip(GpeditDialog.Help) ?? default;

        const double Grow = 100;
        dialog.ResizeClientDip(ClientWidth + Grow, ClientHeight + Grow);
        SizeF grown = dialog.ClientDip();
        checks.Record("grows", Math.Abs(grown.Width - (ClientWidth + Grow)) <= Tolerance && Math.Abs(grown.Height - (ClientHeight + Grow)) <= Tolerance, $"client={grown.Width:0.#}x{grown.Height:0.#}.");

        RectangleF comment = dialog.RectDip(GpeditDialog.Comment) ?? default;
        RectangleF supported = dialog.RectDip(GpeditDialog.SupportedOn) ?? default;
        RectangleF previous = dialog.RectDip(GpeditDialog.Previous) ?? default;
        RectangleF options = dialog.RectDip(GpeditDialog.OptionsPane) ?? default;
        RectangleF help = dialog.RectDip(GpeditDialog.Help) ?? default;
        checks.Record("commentStretches", Math.Abs(comment.Width - (commentBefore.Width + Grow)) <= Tolerance && Math.Abs(comment.Height - commentBefore.Height) <= Tolerance, $"comment width {commentBefore.Width:0.#} to {comment.Width:0.#}.");
        checks.Record("supportedStretches", Math.Abs(supported.Right - comment.Right) <= Tolerance, $"supported right={supported.Right:0.#}, comment right={comment.Right:0.#}.");
        checks.Record("headerFixed", Math.Abs(previous.X - previousBefore.X) <= Tolerance && Math.Abs(previous.Y - previousBefore.Y) <= Tolerance, $"previous at {previous.X:0.#},{previous.Y:0.#}.");
        checks.Record(
            "panesGrow",
            Math.Abs(options.Height - (optionsBefore.Height + Grow)) <= Tolerance && Math.Abs(help.Height - (helpBefore.Height + Grow)) <= Tolerance,
            $"options height {optionsBefore.Height:0.#} to {options.Height:0.#}, help height {helpBefore.Height:0.#} to {help.Height:0.#}.");
        double totalWidth = (options.Width + 3) + 5 + (help.Width + 2);
        checks.Record("panesFillWidth", Math.Abs(totalWidth - (grown.Width - 6)) <= Tolerance + 1, $"options+help+splitter={totalWidth:0.#}, expected {grown.Width - 6:0.#}.");
        checks.Record("splitKeepsOrder", help.X > options.Right && options.Width > optionsBefore.Width && help.Width > helpBefore.Width, $"options {options.Width:0.#} wide, help {help.Width:0.#} wide at x={help.X:0.#}.");

        var gaps = new List<string>();
        bool anchored = true;
        foreach (string id in new[] { GpeditDialog.Ok, GpeditDialog.Cancel })
        {
            RectangleF rect = dialog.RectDip(id) ?? default;
            (float x, float y, float w, float h) = id == GpeditDialog.Ok ? (438f, 567f, 75f, 25f) : (519f, 567f, 75f, 25f);
            double rightGap = grown.Width - rect.Right;
            double bottomGap = grown.Height - rect.Bottom;
            double rightGapBefore = ClientWidth - (x + w);
            double bottomGapBefore = ClientHeight - (y + h);
            bool ok = Math.Abs(rightGap - rightGapBefore) <= Tolerance && Math.Abs(bottomGap - bottomGapBefore) <= Tolerance;
            anchored &= ok;
            gaps.Add($"{id}: right {rightGap:0.#} (was {rightGapBefore:0.#}), bottom {bottomGap:0.#} (was {bottomGapBefore:0.#})");
        }

        RectangleF apply = dialog.RectDip(GpeditDialog.Apply) ?? default;
        anchored &= Math.Abs((grown.Width - apply.Right) - 9) <= Tolerance;
        checks.Record("buttonsAnchored", anchored, string.Join("; ", gaps));
        dialog.Screenshot("gpedit-dialog-resize-grown.png");

        // Far below the minimum: the client stops at 684 x 601 DIP and the controls are where the form puts them.
        dialog.ResizeClientDip(300, 300);
        SizeF shrunk = dialog.ClientDip();
        checks.Record(
            "minimum",
            Math.Abs(shrunk.Width - ClientWidth) <= Tolerance && Math.Abs(shrunk.Height - MinimumClientHeight) <= Tolerance,
            $"client after shrinking={shrunk.Width:0.#}x{shrunk.Height:0.#}, minimum {ClientWidth}x{MinimumClientHeight}.");
        RectangleF okAtMinimum = dialog.RectDip(GpeditDialog.Ok) ?? default;
        checks.Record("controlsAtMinimum", Math.Abs(okAtMinimum.X - 438) <= Tolerance && okAtMinimum.Bottom <= shrunk.Height + Tolerance, $"OK at {okAtMinimum.X:0.#},{okAtMinimum.Y:0.#} in {shrunk.Width:0.#}x{shrunk.Height:0.#}.");

        dialog.Press(VirtualKeyShort.ESCAPE);
        checks.Record("closes", dialog.WaitClosed(), "Escape closes the resized dialog.");
        run.WriteArtifacts("gpedit-dialog-resize.png");
        checks.AssertAllPassed();
    }

    // ---- support -------------------------------------------------------------------------------

    private static GpeditRun Start(string name, out GpeditSandbox sandbox, bool readOnly = false)
    {
        sandbox = GpeditSandbox.Create(name);
        sandbox.DialogEnabled = true;
        sandbox.ReadOnly = readOnly;
        sandbox.SeedStandard();
        return GpeditRun.Open(name, sandbox);
    }

    private static string[] CellsOf(GpeditRun run, string rowName) =>
        run.Rows().FirstOrDefault(r => r.Name == rowName)?.Cells ?? [];

    private static string Describe(SortedDictionary<string, string> hashes) =>
        string.Join(", ", hashes.Select(pair => $"{pair.Key}={(pair.Value.Length > 12 ? pair.Value[..12] : pair.Value)}"));
}

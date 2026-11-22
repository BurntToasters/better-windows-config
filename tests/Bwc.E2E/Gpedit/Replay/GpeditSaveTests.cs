using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.Controls;
using Bwc.E2E.Gpedit.Commands;
using Bwc.E2E.Harness;
using Bwc.Gpo.Store;
using FlaUI.Core.WindowsAPI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Gpedit.Replay;

/// <summary>
/// Saving through the setting dialog on a sandbox policy object (piece G4, dispatch T): the three ways a save can
/// fail on disk
/// that a user can cause (a read-only policy file, a policy file that turned malformed between open and save, a
/// write lock held
/// by another process), what the dialog shows for each, that the dialog stays open and nothing is written, that
/// the loaded
/// model is the saved result, and that Previous Setting and Next Setting save a pending change. The failure modes are in
/// docs/failure-modes/gpedit-dialog.md, section 15. The messages are found by automation id
/// (<c>DialogSaveError.&lt;Kind&gt;</c>, the text box of the message), never by the wording of any text.
/// </summary>
[TestClass]
public sealed class GpeditSaveTests
{
    private const string Root = GpeditRun.RootName;
    private const string Computer = "Computer Configuration";
    private const string Admin = "Administrative Templates";
    private const string Oracle = "BWC Oracle";
    private const string Basics = "Basics";
    private const string MachineFlag = "Class machine flag setting";

    private static readonly SaveFault ReadOnlyFile = new("ReadOnlyFile", "DialogSaveError.ReadOnlyFile", 20);
    private static readonly SaveFault Malformed = new("Malformed", "DialogSaveError.Malformed", 20);
    private static readonly SaveFault LockHeld = new("LockTimeout", "DialogSaveError.LockTimeout", 50);
    private static readonly SaveFault[] AllFaults = [ReadOnlyFile, LockHeld, Malformed];

    private sealed record SaveFault(string Kind, string Id, double WaitSeconds);

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    // ---- the errors -------------------------------------------------------------------------------

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Save_Errors_ReadOnlyFile() => OneFault(ReadOnlyFile);

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Save_Errors_MalformedAtSave() => OneFault(Malformed);

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Save_Errors_LockHeld() => OneFault(LockHeld);

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Save_Errors_TextsDiffer()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("save", "errorsdiffer");
        var sandbox = ReplaySandbox.Create("gpedit-save-errors-differ");
        sandbox.Sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-save-errors-differ", sandbox.Sandbox);

        var texts = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (SaveFault fault in AllFaults)
        {
            texts[fault.Kind] = RunFault(run, sandbox, checks, fault);
        }

        foreach (SaveFault a in AllFaults)
        {
            foreach (SaveFault b in AllFaults.Where(f => string.CompareOrdinal(f.Kind, a.Kind) > 0))
            {
                bool differ = !string.IsNullOrWhiteSpace(texts[a.Kind]) && !string.IsNullOrWhiteSpace(texts[b.Kind]) && texts[a.Kind] != texts[b.Kind];
                checks.Record($"differ.{a.Kind}.{b.Kind}", differ, $"the {a.Kind} message is '{texts[a.Kind]}' and the {b.Kind} message is '{texts[b.Kind]}'.");
            }
        }

        checks.AssertAllPassed();
    }

    private static void OneFault(SaveFault fault)
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("save", "errors." + fault.Kind.ToLowerInvariant());
        string name = "gpedit-save-errors-" + fault.Kind.ToLowerInvariant();
        var sandbox = ReplaySandbox.Create(name);
        sandbox.Sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open(name, sandbox.Sandbox);
        _ = RunFault(run, sandbox, checks, fault);
        checks.AssertAllPassed();
    }

    /// <summary>
    /// Opens the machine flag, changes its state, causes the fault, presses OK and checks the message, the open dialog
    /// and the
    /// files. Returns the text of the message, or null when none appeared. The read-only file and the held lock go away
    /// afterwards and OK is pressed again, which must save; a malformed file stays and the dialog is cancelled.
    /// </summary>
    private static string? RunFault(GpeditRun run, ReplaySandbox sandbox, GpeditChecks checks, SaveFault fault)
    {
        string p = fault.Kind.ToLowerInvariant();
        string polKey = Path.GetRelativePath(sandbox.GpoRoot, GpoFileNames.PolPath(sandbox.GpoRoot, GpoSide.Machine));
        run.GoTo(Root, Computer, Admin, Oracle, Basics);
        using GpeditDialog? dialog = GpeditDialog.Open(run, MachineFlag);
        checks.Record($"{p}.opens", dialog is not null, "the dialog of the machine flag opens.");
        if (dialog is null)
        {
            return null;
        }

        string target = dialog.IsSelected(GpeditDialog.Enabled) ? GpeditDialog.Disabled : GpeditDialog.Enabled;
        _ = dialog.Select(target);
        string stateBefore = run.StateOf(MachineFlag);
        FileStream? held = null;
        try
        {
            switch (fault.Kind)
            {
                case "ReadOnlyFile":
                    sandbox.SetPolReadOnly(GpoSide.Machine, true);
                    break;
                case "Malformed":
                    sandbox.Sandbox.WriteUnreadablePolicyFile(GpoSide.Machine);
                    break;
                default:
                    held = sandbox.HoldLock();
                    break;
            }

            SortedDictionary<string, string> armed = sandbox.Sandbox.PolicyFileHashes();
            _ = dialog.Invoke(GpeditDialog.Ok);
            bool shown = dialog.Find(fault.Id, fault.WaitSeconds) is not null;
            string? text = shown ? dialog.TextOf(fault.Id) : null;
            checks.Record($"{p}.message", shown && !string.IsNullOrWhiteSpace(text), $"the save error '{fault.Id}' shows '{text}'.");
            foreach (SaveFault other in AllFaults.Where(f => f.Id != fault.Id))
            {
                checks.Record($"{p}.notOther.{other.Kind}", dialog.Find(other.Id, 0.3) is null, $"the message of the {other.Kind} failure ('{other.Id}') is not shown for a {fault.Kind} failure.");
            }

            if (shown)
            {
                dialog.Press(VirtualKeyShort.RETURN);
                checks.Record($"{p}.dismissed", ShellRun.WaitUntil(() => dialog.Find(fault.Id, 0.3) is null, TimeSpan.FromSeconds(8)), "Enter dismisses the message.");
            }

            checks.Record($"{p}.staysOpen", dialog.Find(GpeditDialog.Ok, 2) is not null, "the dialog stays open after the error.");
            checks.Record($"{p}.keepsChange", dialog.IsSelected(target) && dialog.Read("Dirty") == "1" && dialog.IsEnabled(GpeditDialog.Apply), $"the chosen state is kept and still pending (Dirty={dialog.Read("Dirty")}).");
            checks.Record($"{p}.writesNothing", armed.SequenceEqual(sandbox.Sandbox.PolicyFileHashes()), "no policy file, comment file or version file changed.");
            checks.Record($"{p}.listKept", run.StateOf(MachineFlag) == stateBefore, $"the list still shows '{run.StateOf(MachineFlag)}', expected '{stateBefore}'.");
            if (fault.Kind == "ReadOnlyFile")
            {
                checks.Record($"{p}.attributeKept", sandbox.PolIsReadOnly(GpoSide.Machine), "the read-only attribute was not cleared.");
            }

            if (fault.Kind == "Malformed")
            {
                _ = dialog.Invoke(GpeditDialog.Cancel);
                checks.Record($"{p}.cancelCloses", dialog.WaitClosed(), "Cancel closes the dialog.");
                return text;
            }

            if (fault.Kind == "ReadOnlyFile")
            {
                sandbox.SetPolReadOnly(GpoSide.Machine, false);
            }
            else
            {
                held?.Dispose();
                held = null;
            }

            _ = dialog.Invoke(GpeditDialog.Ok);
            checks.Record($"{p}.retryCloses", dialog.WaitClosed(), "OK saves and closes once the cause is gone.");
            SortedDictionary<string, string> after = sandbox.Sandbox.PolicyFileHashes();
            checks.Record($"{p}.retryWrites", after[polKey] != armed[polKey], "the retry wrote the policy file.");
            return text;
        }
        finally
        {
            held?.Dispose();
            if (sandbox.PolIsReadOnly(GpoSide.Machine))
            {
                sandbox.SetPolReadOnly(GpoSide.Machine, false);
            }
        }
    }

    // ---- the model --------------------------------------------------------------------------------

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Save_ModelReplaced()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("save", "modelreplaced");
        var sandbox = ReplaySandbox.Create("gpedit-save-model");
        sandbox.Sandbox.SeedStandard();
        SortedDictionary<string, string> seeded = sandbox.Sandbox.PolicyFileHashes();
        using GpeditRun run = GpeditRun.Open("gpedit-save-model", sandbox.Sandbox);
        run.GoTo(Root, Computer, Admin, Oracle, Basics);

        using (GpeditDialog? dialog = GpeditDialog.Open(run, MachineFlag))
        {
            checks.Record("opens", dialog is not null, "the dialog opens.");
            if (dialog is null)
            {
                checks.AssertAllPassed();
                return;
            }

            _ = dialog.Select(GpeditDialog.Disabled);
            _ = dialog.SetComment("saved note");
            _ = dialog.Invoke(GpeditDialog.Ok);
            checks.Record("okCloses", dialog.WaitClosed(), "OK saves and closes.");
        }

        SortedDictionary<string, string> saved = sandbox.Sandbox.PolicyFileHashes();
        string[] changed = saved.Where(pair => seeded[pair.Key] != pair.Value).Select(pair => pair.Key).ToArray();
        checks.Record("onDisk", changed.Length >= 3, $"OK wrote files: [{string.Join(", ", changed)}].");
        checks.Record("listAfterOk", ShellRun.WaitUntil(() => ListShowsSaved(run), TimeSpan.FromSeconds(5)), $"the list shows the saved state: [{string.Join("|", CellsOf(run))}].");
        ReopenShowsSaved(run, checks, "afterOk");

        run.SelectRow(MachineFlag);
        run.Press(GpeditKeys.F5);
        checks.Record("refreshed", run.WaitCount("Refreshes", 1) && run.WaitReloadSettled(), $"Refreshes={run.Count("Refreshes")}.");
        checks.Record("listAfterF5", ShellRun.WaitUntil(() => ListShowsSaved(run), TimeSpan.FromSeconds(8)), $"after F5 the list shows the saved state: [{string.Join("|", CellsOf(run))}].");
        checks.Record("f5WritesNothing", saved.SequenceEqual(sandbox.Sandbox.PolicyFileHashes()), "F5 wrote no file.");
        ReopenShowsSaved(run, checks, "afterF5");
        checks.AssertAllPassed();
    }

    private static string[] CellsOf(GpeditRun run) => run.Rows().FirstOrDefault(r => r.Name == MachineFlag)?.Cells ?? [];

    private static bool ListShowsSaved(GpeditRun run)
    {
        string[] cells = CellsOf(run);
        return cells.ElementAtOrDefault(1) == "Disabled" && cells.ElementAtOrDefault(2) == "Yes";
    }

    private static void ReopenShowsSaved(GpeditRun run, GpeditChecks checks, string moment)
    {
        using GpeditDialog? again = GpeditDialog.Open(run, MachineFlag);
        checks.Record($"{moment}.reopens", again is not null, "the policy opens again.");
        if (again is null)
        {
            return;
        }

        checks.Record($"{moment}.state", again.IsSelected(GpeditDialog.Disabled), "Disabled is shown.");
        checks.Record($"{moment}.comment", again.TextOf(GpeditDialog.Comment) == "saved note", $"the comment is '{again.TextOf(GpeditDialog.Comment)}'.");
        checks.Record($"{moment}.clean", !again.IsEnabled(GpeditDialog.Apply), "Apply is disabled: the dialog baseline is the saved result.");
        _ = again.Invoke(GpeditDialog.Cancel);
        _ = again.WaitClosed();
    }

    // ---- Previous Setting and Next Setting --------------------------------------------------------

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Save_PreviousNextSavesPending()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("save", "previousnext");
        var sandbox = ReplaySandbox.Create("gpedit-save-prevnext");
        sandbox.Sandbox.SeedStandard();
        SortedDictionary<string, string> seeded = sandbox.Sandbox.PolicyFileHashes();
        using GpeditRun run = GpeditRun.Open("gpedit-save-prevnext", sandbox.Sandbox);
        run.GoTo(Root, Computer, Admin, Oracle, Basics);

        using GpeditDialog? dialog = GpeditDialog.Open(run, MachineFlag);
        checks.Record("opens", dialog is not null, "the dialog opens.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        string first = dialog.Caption();
        _ = dialog.Select(GpeditDialog.Disabled);
        _ = dialog.Invoke(GpeditDialog.Next);
        checks.Record("moved", ShellRun.WaitUntil(() => dialog.Caption() != first, TimeSpan.FromSeconds(10)), $"Next moves to another policy (caption '{dialog.Caption()}').");
        checks.Record("noMessage", dialog.Find("DialogMessage", 0.3) is null, "no message box appears.");
        checks.Record("pendingSaved", ShellRun.WaitUntil(() => !seeded.SequenceEqual(sandbox.Sandbox.PolicyFileHashes()), TimeSpan.FromSeconds(8)), "the pending change was written before the dialog moved.");
        checks.Record("listShowsIt", run.StateOf(MachineFlag) == "Disabled", $"the list shows '{run.StateOf(MachineFlag)}' for the policy left behind.");
        _ = dialog.Invoke(GpeditDialog.Previous);
        checks.Record("back", ShellRun.WaitUntil(() => dialog.Caption() == first, TimeSpan.FromSeconds(10)), $"Previous returns (caption '{dialog.Caption()}').");
        checks.Record("backShowsSaved", dialog.IsSelected(GpeditDialog.Disabled) && !dialog.IsEnabled(GpeditDialog.Apply), "the first policy shows Disabled and Apply is disabled.");
        _ = dialog.Invoke(GpeditDialog.Cancel);
        checks.Record("cancelCloses", dialog.WaitClosed(), "Cancel closes the dialog.");
        checks.AssertAllPassed();
    }
}

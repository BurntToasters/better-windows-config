using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.Controls;
using Bwc.E2E.Gpedit.Commands;
using Bwc.E2E.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Bwc.E2E.Gpedit.Commands.GpeditNames;

namespace Bwc.E2E.Gpedit.Templates;

/// <summary>
/// Add and Remove tests of the Add/Remove Templates dialog: a good template is picked and imported, the
/// list and the tree
/// reload after Close, a cancelled picker writes nothing, a removal deletes the files, a template that
/// does not load can be
/// removed, dependents block a removal, a shadowed import is shown as shadowed and an unusable store is
/// refused. The failure
/// modes are in docs/failure-modes/gpedit-templates.md, sections 3, 4 and 6 to 9. Every test writes
/// artifacts/e2e/visual/gpedit/gpedit-&lt;name&gt;.json and a picture, and its checks to the E2E result
/// artifact. The file picker
/// is the real common item dialog, driven with real input. Source files, the import store and the
/// sandbox template copy are all
/// below the run data root of the test.
/// </summary>
[TestClass]
public sealed class GpeditImportFlowTests
{
    private static readonly TemplateSpec One = new("One", "BWC.Policies.Imported.One");
    private static readonly TemplateSpec Two = new("Two", "BWC.Policies.Imported.Two");
    private static readonly TemplateSpec Base = new("Base", "BWC.Policies.Imported.Base");
    private static readonly TemplateSpec Dependent = new("Dep", "BWC.Policies.Imported.Dep", "BWC.Policies.Imported.Base", "ImportRootBase");

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    private static string[] StoreOf(TemplateSpec spec) => [spec.AdmxName, Path.Combine("en-US", spec.File + ".adml")];

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Import_Add_GoodTemplate()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("import", "add.good");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-import-add-good");
        sandbox.SeedStandard();
        var sources = new ImportSources(sandbox);
        string source = sources.Write("one", One);
        SortedDictionary<string, string> systemBefore = ImportSources.Hashes(sandbox.TemplateRoot);
        SortedDictionary<string, string> sourceBefore = ImportSources.Hashes(sources.Root);
        using GpeditRun run = GpeditRun.Open("gpedit-import-add-good", sandbox);
        int templates = run.Count("Templates");
        int policies = run.Count("PolicyCount");
        checks.Record("startClean", run.Count("Problems") == 0 && run.Notice().Length == 0, $"Problems={run.Str("Problems")}, notice='{run.Notice()}'.");
        run.GoTo(GpeditTemplatesRun.AdminComputer);
        _ = run.WaitNode(GpeditTemplatesRun.AdminComputer);
        checks.Record("notThereYet", !run.RowNamesNow().Contains(One.CategoryText), $"rows={string.Join("|", run.RowNamesNow())}.");

        using GpeditTemplatesDialog? dialog = GpeditTemplatesDialog.Open(run);
        checks.Record("opens", dialog is not null, "Add/Remove Templates opens the dialog.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        AddOutcome outcome = dialog.AddFile(source, out string notes);
        checks.Record("pickerOpens", outcome != AddOutcome.NoPicker, "Add opens the file picker and the path was accepted. " + notes);
        checks.Record("listRow", outcome == AddOutcome.Added, $"outcome={outcome}; rows={dialog.Rows().Describe()}; details='{dialog.DetailsText()}'.");
        TemplateRow? row = dialog.Rows().FirstOrDefault(r => r.Name == One.AdmxName);
        checks.Record("rowNamespace", row?.Namespace == One.Namespace, $"namespace '{row?.Namespace}', expected {One.Namespace}.");
        checks.Record("rowStatus", row?.Status == TemplateIds.StatusLoaded, $"status '{row?.Status}', expected {TemplateIds.StatusLoaded}.");
        checks.Record("noDetails", !dialog.HasDetails, "no details box shows after a good import.");
        checks.Record("notReloadedYet", run.Count("TemplateReloads") == 0, $"TemplateReloads={run.Str("TemplateReloads")} while the dialog is open.");

        // The store holds the two files, byte for byte (ADMX-027), and nothing else.
        checks.Record("storeOnlyTwoFiles", sources.StoreFiles().SequenceEqual(StoreOf(One)), $"store holds {string.Join(", ", sources.StoreFiles())}.");
        string storeAdmx = Path.Combine(sandbox.ImportStore, One.AdmxName);
        string storeAdml = Path.Combine(sandbox.ImportStore, "en-US", One.File + ".adml");
        checks.Record("storeAdmxBytes", File.Exists(storeAdmx) && File.ReadAllBytes(storeAdmx).AsSpan().SequenceEqual(File.ReadAllBytes(source)), "the stored ADMX is the source ADMX.");
        checks.Record(
            "storeAdmlBytes",
            File.Exists(storeAdml) && File.ReadAllBytes(storeAdml).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(source)!, "en-US", One.File + ".adml"))),
            "the stored ADML is the source ADML.");

        // After Close the tree and list reload once and show the new policies, at the same node.
        checks.Record("closes", dialog.Close(), "Close closes the dialog.");
        checks.Record("reload.count", run.WaitDialogReload(1), $"TemplateReloads={run.Str("TemplateReloads")}.");
        checks.Record("reload.node", run.NodePath().SequenceEqual(GpeditTemplatesRun.AdminComputer), $"node after the reload={run.Str("Node")}.");
        checks.Record("reload.templates", run.Count("Templates") == templates + 1, $"Templates={run.Str("Templates")}, expected {templates + 1}.");
        checks.Record("reload.policyCount", run.Count("PolicyCount") == policies + 1, $"PolicyCount={run.Str("PolicyCount")}, expected {policies + 1}.");
        checks.Record("reload.category", run.WaitRow(One.CategoryText), $"rows={string.Join("|", run.RowNamesNow())}.");
        checks.Record("reload.noProblems", run.Count("Problems") == 0 && run.Notice().Length == 0, $"Problems={run.Str("Problems")}, notice='{run.Notice()}'.");
        run.GoTo(Root, Computer, Admin, One.CategoryText);
        checks.Record("reload.policyRow", run.WaitRow(One.PolicyRow), $"rows={string.Join("|", run.RowNamesNow())}.");

        // The system folder and the source folder were never written (BEH-007, DIFF-005).
        checks.Record("systemUnchanged", ImportSources.SameHashes(systemBefore, ImportSources.Hashes(sandbox.TemplateRoot)), "the system template folder is as it was.");
        checks.Record("sourceUnchanged", ImportSources.SameHashes(sourceBefore, ImportSources.Hashes(sources.Root)), "the source folder is as it was.");

        run.Measure("storeFiles", string.Join("|", sources.StoreFiles()));
        run.Measure("systemFileCount", ImportSources.Hashes(sandbox.TemplateRoot).Count);
        run.WriteArtifacts("gpedit-import-add-good.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Import_Add_SurvivesRestart()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("import", "add.restart");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-import-add-restart");
        sandbox.SeedStandard();
        var sources = new ImportSources(sandbox);
        string source = sources.Write("one", One);
        int policies;
        using (GpeditRun first = GpeditRun.Open("gpedit-import-add-restart", sandbox))
        {
            policies = first.Count("PolicyCount");
            first.GoTo(GpeditTemplatesRun.AdminComputer);
            using GpeditTemplatesDialog? dialog = GpeditTemplatesDialog.Open(first);
            AddOutcome outcome = dialog?.AddFile(source, out _) ?? AddOutcome.NoPicker;
            checks.Record("first.added", outcome == AddOutcome.Added, $"outcome={outcome}.");
            _ = dialog?.Close();
            checks.Record("first.reloaded", first.WaitDialogReload(1) && first.Count("PolicyCount") == policies + 1, $"PolicyCount={first.Str("PolicyCount")}.");
        }

        // A new process on the same data root loads the template from the store on its own (no import happens now).
        using GpeditRun second = GpeditRun.Open("gpedit-import-add-restart", sandbox);
        checks.Record("restart.policy", second.Count("PolicyCount") == policies + 1, $"PolicyCount={second.Str("PolicyCount")}, expected {policies + 1}.");
        checks.Record("restart.clean", second.Count("Problems") == 0 && second.Notice().Length == 0, $"Problems={second.Str("Problems")}.");
        second.GoTo(GpeditTemplatesRun.AdminComputer);
        checks.Record("restart.category", second.WaitRow(One.CategoryText), $"rows={string.Join("|", second.RowNamesNow())}.");
        using GpeditTemplatesDialog? reopened = GpeditTemplatesDialog.Open(second);
        checks.Record("restart.row", reopened is not null && reopened.Rows().Any(r => r.Name == One.AdmxName && r.Status == TemplateIds.StatusLoaded), $"rows={reopened?.Rows().Describe()}.");
        _ = reopened?.Close();
        checks.Record("restart.noReload", second.Count("TemplateReloads") == 0, $"TemplateReloads={second.Str("TemplateReloads")}.");

        second.Measure("storeFiles", string.Join("|", sources.StoreFiles()));
        second.WriteArtifacts("gpedit-import-add-restart.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Import_Add_PickerCancelWritesNothing()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("import", "add.cancel");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-import-add-cancel");
        sandbox.SeedStandard();
        var sources = new ImportSources(sandbox);
        _ = sources.Write("one", One);
        using GpeditRun run = GpeditRun.Open("gpedit-import-add-cancel", sandbox);
        run.GoTo(GpeditTemplatesRun.AdminComputer);

        using GpeditTemplatesDialog? dialog = GpeditTemplatesDialog.Open(run);
        checks.Record("opens", dialog is not null, "the dialog opens.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        checks.Record("cancelled", dialog.CancelPicker(out string notes), notes);
        checks.Record("dialogStillOpen", dialog.IsOpen, "the dialog is still open after the picker was cancelled.");
        checks.Record("listUnchanged", dialog.Rows().Count == 0, $"rows={dialog.Rows().Describe()}.");
        checks.Record("noDetails", !dialog.HasDetails, "no details box shows.");
        checks.Record("noStore", !Directory.Exists(sandbox.ImportStore), "the import store folder was not created.");
        checks.Record("closes", dialog.Close(), "Close closes the dialog.");
        checks.Record("noReload", run.Count("TemplateReloads") == 0, $"TemplateReloads={run.Str("TemplateReloads")}.");

        run.WriteArtifacts("gpedit-import-add-cancel.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Import_Remove_Template()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("import", "remove");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-import-remove");
        sandbox.SeedStandard();
        var sources = new ImportSources(sandbox);
        string first = sources.Write("one", One);
        string second = sources.Write("two", Two);
        using GpeditRun run = GpeditRun.Open("gpedit-import-remove", sandbox);
        int templates = run.Count("Templates");
        run.GoTo(GpeditTemplatesRun.AdminComputer);

        using GpeditTemplatesDialog? dialog = GpeditTemplatesDialog.Open(run);
        checks.Record("opens", dialog is not null, "the dialog opens.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        checks.Record("addOne", dialog.AddFile(first, out _) == AddOutcome.Added, $"rows={dialog.Rows().Describe()}.");
        checks.Record("addTwo", dialog.AddFile(second, out _) == AddOutcome.Added, $"rows={dialog.Rows().Describe()}.");
        checks.Record("listOrdered", dialog.Rows().Select(r => r.Name).SequenceEqual([One.AdmxName, Two.AdmxName]), $"rows={dialog.Rows().Describe()}; ordered by file name.");

        // Remove acts on the selected row only, at once, and removes both files.
        checks.Record("remove", dialog.RemoveRow(One.AdmxName), "the row was selected and Remove pressed.");
        checks.Record("removed.row", dialog.WaitRows(rows => rows.Select(r => r.Name).SequenceEqual([Two.AdmxName])), $"rows={dialog.Rows().Describe()}.");
        checks.Record("removed.storeOnlyTwo", sources.StoreFiles().SequenceEqual(StoreOf(Two)), $"store holds {string.Join(", ", sources.StoreFiles())}.");
        checks.Record("removed.noDetails", !dialog.HasDetails, "no details box shows after a removal.");
        checks.Record("removed.removeDisabled", ShellRun.WaitUntil(() => !dialog.IsEnabled(TemplateIds.Remove), TimeSpan.FromSeconds(3)), "Remove is disabled again: no row is selected.");
        checks.Record("removed.otherKept", dialog.Rows().Any(r => r.Name == Two.AdmxName && r.Status == TemplateIds.StatusLoaded), "the other template is untouched.");

        // One reload when the dialog closes: One is gone from the tree, Two is in it.
        checks.Record("closes", dialog.Close(), "Close closes the dialog.");
        checks.Record("removed.reload", run.WaitDialogReload(1), $"TemplateReloads={run.Str("TemplateReloads")}; one reload for two changes.");
        checks.Record("removed.templates", run.Count("Templates") == templates + 1, $"Templates={run.Str("Templates")}, expected {templates + 1}.");
        checks.Record("removed.treeLost", run.WaitRow(One.CategoryText, present: false), $"rows={string.Join("|", run.RowNamesNow())}.");
        checks.Record("removed.treeKept", run.WaitRow(Two.CategoryText), $"rows={string.Join("|", run.RowNamesNow())}.");

        // A removed template can be imported again.
        using GpeditTemplatesDialog? again = GpeditTemplatesDialog.Open(run);
        checks.Record("removed.importAgain", again is not null && again.AddFile(first, out _) == AddOutcome.Added, $"rows={again?.Rows().Describe()}.");
        _ = again?.Close();
        checks.Record("again.reload", run.WaitDialogReload(2), $"TemplateReloads={run.Str("TemplateReloads")}.");

        run.WriteArtifacts("gpedit-import-remove.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Import_Remove_BrokenFile()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("import", "remove.broken");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-import-remove-broken");
        sandbox.SeedStandard();
        var sources = new ImportSources(sandbox);
        string source = sources.Write("one", One);
        using GpeditRun run = GpeditRun.Open("gpedit-import-remove-broken", sandbox);
        int templates = run.Count("Templates");
        run.GoTo(GpeditTemplatesRun.AdminComputer);

        using (GpeditTemplatesDialog? import = GpeditTemplatesDialog.Open(run))
        {
            checks.Record("import", import is not null && import.AddFile(source, out _) == AddOutcome.Added, "the template is imported first.");
            _ = import?.Close();
        }

        checks.Record("imported.reload", run.WaitDialogReload(1), $"TemplateReloads={run.Str("TemplateReloads")}.");

        // The file in the store stops loading while the editor runs (a damaged file). The dialog reads the
        // store when it opens.
        string stored = Path.Combine(sandbox.ImportStore, One.AdmxName);
        if (!File.Exists(stored))
        {
            checks.Record("stored", false, "the imported file is not in the store, so it cannot be damaged.");
            checks.AssertAllPassed();
            return;
        }

        ImportSources.Corrupt(stored);
        using GpeditTemplatesDialog? dialog = GpeditTemplatesDialog.Open(run);
        checks.Record("opens", dialog is not null, "the dialog opens.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        TemplateRow? row = dialog.Rows().FirstOrDefault(r => r.Name == One.AdmxName);
        checks.Record("broken.row", row is not null, $"rows={dialog.Rows().Describe()}; a file that does not load is still listed.");
        checks.Record("broken.status", row?.Status == TemplateIds.StatusNotLoaded && row.Namespace.Length == 0, $"status '{row?.Status}', namespace '{row?.Namespace}'.");

        checks.Record("remove", dialog.RemoveRow(One.AdmxName), "the broken row was selected and Remove pressed.");
        checks.Record("broken.removed", dialog.WaitRows(rows => rows.Count == 0), $"rows={dialog.Rows().Describe()}.");
        checks.Record("broken.fileGone", sources.StoreFiles().Length == 0, $"store holds {string.Join(", ", sources.StoreFiles())}; the ADMX and the ADML are both removed.");
        checks.Record("broken.noDetails", !dialog.HasDetails, "no details box shows.");
        checks.Record("closes", dialog.Close(), "Close closes the dialog.");
        checks.Record("broken.reload", run.WaitDialogReload(2), $"TemplateReloads={run.Str("TemplateReloads")}.");
        checks.Record("broken.templates", run.Count("Templates") == templates && run.Count("Problems") == 0, $"Templates={run.Str("Templates")} (expected {templates}), Problems={run.Str("Problems")}.");
        checks.Record("broken.treeLost", run.WaitRow(One.CategoryText, present: false), $"rows={string.Join("|", run.RowNamesNow())}.");

        run.WriteArtifacts("gpedit-import-remove-broken.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Import_Dependents()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("import", "dependents");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-import-dependents");
        sandbox.SeedStandard();
        var sources = new ImportSources(sandbox);
        string basePath = sources.Write("pair", Base);
        string dependentPath = sources.Write("pair", Dependent);
        using GpeditRun run = GpeditRun.Open("gpedit-import-dependents", sandbox);
        run.GoTo(GpeditTemplatesRun.AdminComputer);

        using GpeditTemplatesDialog? dialog = GpeditTemplatesDialog.Open(run);
        checks.Record("opens", dialog is not null, "the dialog opens.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        // C15: the dependent uses a namespace that only its neighbour in the source folder defines.
        AddOutcome first = dialog.AddFile(dependentPath, out _);
        string text = dialog.DetailsText();
        checks.Record("neighbour.box", first == AddOutcome.Refused, $"outcome={first}.");
        checks.Record("neighbour.code", text.Contains("UsingNotInstalled", StringComparison.Ordinal), $"details='{text}'.");
        checks.Record("neighbour.namespace", text.Contains(Base.Namespace, StringComparison.Ordinal), $"details='{text}'.");
        checks.Record("neighbour.closes", dialog.CloseDetails(), "the details box closes.");
        checks.Record("neighbour.noStore", !Directory.Exists(sandbox.ImportStore) && dialog.Rows().Count == 0, $"store exists={Directory.Exists(sandbox.ImportStore)}, rows={dialog.Rows().Describe()}.");

        // The base first, then the dependent: both import.
        checks.Record("base.added", dialog.AddFile(basePath, out _) == AddOutcome.Added, $"rows={dialog.Rows().Describe()}.");
        checks.Record("dependent.added", dialog.AddFile(dependentPath, out _) == AddOutcome.Added, $"rows={dialog.Rows().Describe()}.");
        checks.Record("bothListed", dialog.Rows().Select(r => r.Name).SequenceEqual([Base.AdmxName, Dependent.AdmxName]), $"rows={dialog.Rows().Describe()}.");
        checks.Record("bothLoaded", dialog.Rows().All(r => r.Status == TemplateIds.StatusLoaded), $"rows={dialog.Rows().Describe()}.");

        // E2: the base cannot be removed while the dependent uses it.
        int files = sources.StoreFiles().Length;
        checks.Record("removeBase", dialog.RemoveRow(Base.AdmxName) && dialog.WaitDetails(), "Remove on the base shows the details box.");
        string refusal = dialog.DetailsText();
        checks.Record("removeBase.code", refusal.Contains("UsedByTemplate", StringComparison.Ordinal), $"details='{refusal}'.");
        checks.Record("removeBase.names", refusal.Contains(Dependent.AdmxName, StringComparison.Ordinal), $"details='{refusal}' names the dependent.");
        checks.Record("removeBase.filesKept", sources.StoreFiles().Length == files && dialog.Rows().Count == 2, $"store holds {sources.StoreFiles().Length} files (expected {files}), rows={dialog.Rows().Describe()}.");
        _ = dialog.CloseDetails();

        // The dependent first, then the base.
        checks.Record("removeDependent", dialog.RemoveRow(Dependent.AdmxName) && dialog.WaitRows(rows => rows.Select(r => r.Name).SequenceEqual([Base.AdmxName])), $"rows={dialog.Rows().Describe()}.");
        checks.Record("removeBaseAfter", dialog.RemoveRow(Base.AdmxName) && dialog.WaitRows(rows => rows.Count == 0), $"rows={dialog.Rows().Describe()}.");
        checks.Record("storeEmpty", sources.StoreFiles().Length == 0, $"store holds {string.Join(", ", sources.StoreFiles())}.");
        checks.Record("closes", dialog.Close(), "Close closes the dialog.");
        checks.Record("oneReload", run.WaitDialogReload(1), $"TemplateReloads={run.Str("TemplateReloads")}; changes happened, so one reload.");

        run.WriteArtifacts("gpedit-import-dependents.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Import_Close_NoChangeNoReload()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("import", "close.nochange");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-import-close-nochange");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-import-close-nochange", sandbox);
        run.GoTo(GpeditTemplatesRun.AdminComputer);

        using (GpeditTemplatesDialog? idle = GpeditTemplatesDialog.Open(run))
        {
            checks.Record("idle.closes", idle is not null && idle.Close(), "the dialog opens and closes.");
        }

        checks.Record("idle.noReload", run.Count("TemplateReloads") == 0, $"TemplateReloads={run.Str("TemplateReloads")}.");

        // Every attempt refused: nothing changed, nothing reloads.
        using (GpeditTemplatesDialog? refused = GpeditTemplatesDialog.Open(run))
        {
            AddOutcome outcome = refused?.AddFile(ImportSources.BadFixture("MalformedXml"), out _) ?? AddOutcome.NoPicker;
            checks.Record("refused.box", outcome == AddOutcome.Refused, $"outcome={outcome}.");
            _ = refused?.CloseDetails();
            checks.Record("refused.closes", refused is not null && refused.Close(), "the dialog closes after a refusal.");
        }

        checks.Record("refused.noReload", run.Count("TemplateReloads") == 0, $"TemplateReloads={run.Str("TemplateReloads")}.");

        using (GpeditTemplatesDialog? cancelled = GpeditTemplatesDialog.Open(run))
        {
            checks.Record("cancel.picker", cancelled is not null && cancelled.CancelPicker(out _), "the picker is cancelled.");
            checks.Record("cancel.closes", cancelled is not null && cancelled.Close(), "the dialog closes.");
        }

        checks.Record("cancel.noReload", run.Count("TemplateReloads") == 0, $"TemplateReloads={run.Str("TemplateReloads")}.");
        checks.Record("keepsNode", run.NodePath().SequenceEqual(GpeditTemplatesRun.AdminComputer), $"node={run.Str("Node")}.");

        run.WriteArtifacts("gpedit-import-close-nochange.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Import_Shadowed()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("import", "shadowed");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-import-shadowed");
        sandbox.SeedStandard();
        sandbox.UseTemplatesWithBadCases();
        var sources = new ImportSources(sandbox);
        string source = sources.Write("one", One);
        using GpeditRun run = GpeditRun.Open("gpedit-import-shadowed", sandbox);
        run.GoTo(GpeditTemplatesRun.AdminComputer);

        using (GpeditTemplatesDialog? import = GpeditTemplatesDialog.Open(run))
        {
            checks.Record("import", import is not null && import.AddFile(source, out _) == AddOutcome.Added, "the template is imported while the system folder lacks it.");
            _ = import?.Close();
        }

        checks.Record("imported.reload", run.WaitDialogReload(1), $"TemplateReloads={run.Str("TemplateReloads")}.");
        int policies = run.Count("PolicyCount");

        // A system template with the same namespace appears (the way an update of Windows would add one), and
        // the editor reads again.
        sources.AddToSystemFolder(source);
        checks.Record("treeFocus", run.FocusPane("tree"), $"focused pane={run.Shell.FocusedPane()}.");
        run.Press(GpeditKeys.F5);
        checks.Record("refreshed", run.WaitCount("Refreshes", 1) && run.WaitReloadSettled(), $"Refreshes={run.Str("Refreshes")}.");
        checks.Record("policiesOnce", run.Count("PolicyCount") == policies, $"PolicyCount={run.Str("PolicyCount")}, expected {policies}: the shadowed policies do not show twice.");
        run.GoTo(GpeditTemplatesRun.AdminComputer);
        checks.Record("categoryOnce", run.RowNamesNow().Count(n => n == One.CategoryText) == 1, $"rows={string.Join("|", run.RowNamesNow())}.");

        using GpeditTemplatesDialog? dialog = GpeditTemplatesDialog.Open(run);
        checks.Record("opens", dialog is not null, "the dialog opens.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        TemplateRow? row = dialog.Rows().FirstOrDefault(r => r.Name == One.AdmxName);
        checks.Record("status", row?.Status == TemplateIds.StatusShadowed, $"status '{row?.Status}', expected {TemplateIds.StatusShadowed}; rows={dialog.Rows().Describe()}.");
        checks.Record("namespace", row?.Namespace == One.Namespace, $"namespace '{row?.Namespace}'.");
        checks.Record("problems.listed", dialog.Problems().Any(p => p.Code == "NamespaceShadowed"), $"problems={dialog.Problems().Describe()}.");
        checks.Record("removable", dialog.RemoveRow(One.AdmxName) && dialog.WaitRows(rows => rows.Count == 0), $"rows={dialog.Rows().Describe()}.");
        checks.Record("removed.storeEmpty", sources.StoreFiles().Length == 0, $"store holds {string.Join(", ", sources.StoreFiles())}.");
        checks.Record("closes", dialog.Close(), "Close closes the dialog.");
        checks.Record("removed.reload", run.WaitDialogReload(2), $"TemplateReloads={run.Str("TemplateReloads")}.");
        checks.Record("removed.stillOnce", run.Count("PolicyCount") == policies, $"PolicyCount={run.Str("PolicyCount")}: the system template still provides the policy.");

        run.WriteArtifacts("gpedit-import-shadowed.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Import_StoreUnavailable()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("import", "store.unavailable");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-import-store-unavailable");
        sandbox.SeedStandard();
        var sources = new ImportSources(sandbox);
        string source = sources.Write("one", One);
        sources.MakeStoreAFile();
        string storeText = File.ReadAllText(sandbox.ImportStore);
        using GpeditRun run = GpeditRun.Open("gpedit-import-store-unavailable", sandbox);
        run.GoTo(GpeditTemplatesRun.AdminComputer);
        checks.Record("loads", run.Str("Loaded") == "1", $"Loaded='{run.Str("Loaded")}': the system templates still load.");

        using GpeditTemplatesDialog? dialog = GpeditTemplatesDialog.Open(run);
        checks.Record("opens", dialog is not null, "the dialog opens although the store is not a folder.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        checks.Record("listEmpty", dialog.Rows().Count == 0, $"rows={dialog.Rows().Describe()}.");
        checks.Record("problems.store", dialog.Problems().Any(p => p.Kind == "TemplateStore"), $"problems={dialog.Problems().Describe()}.");
        checks.Record("notice", run.WaitNotice(shown: true), "the status bar shows the notice for the store problem.");
        AddOutcome outcome = dialog.AddFile(source, out _);
        string text = dialog.DetailsText();
        checks.Record("add.box", outcome == AddOutcome.Refused, $"outcome={outcome}.");
        checks.Record("add.code", text.Contains("StoreUnavailable", StringComparison.Ordinal), $"details='{text}'.");
        _ = dialog.CloseDetails();
        checks.Record("add.noWrite", File.Exists(sandbox.ImportStore) && File.ReadAllText(sandbox.ImportStore) == storeText, "the file in the place of the store is untouched.");
        checks.Record("closes", dialog.Close(), "Close closes the dialog.");
        checks.Record("noReload", run.Count("TemplateReloads") == 0, $"TemplateReloads={run.Str("TemplateReloads")}.");

        run.WriteArtifacts("gpedit-import-store-unavailable.png");
        checks.AssertAllPassed();
    }
}

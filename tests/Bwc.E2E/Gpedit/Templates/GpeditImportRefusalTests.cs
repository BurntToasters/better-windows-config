using Bwc.E2E.Controls;
using Bwc.E2E.Gpedit.Commands;
using Bwc.E2E.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Gpedit.Templates;

/// <summary>
/// Refusal tests of the Add/Remove Templates dialog: every bad fixture of the H group is refused with
/// its typed problem in the
/// details box and writes nothing (DLG-116), a classic .adm file and other files are refused (BEH-008,
/// DIFF-005), and a
/// namespace the system folder or the store already has, or a file name the store already has, is
/// refused. The failure modes
/// are in docs/failure-modes/gpedit-templates.md, section 5. Every test writes
/// artifacts/e2e/visual/gpedit/gpedit-&lt;name&gt;.json
/// and a picture, and its checks to the E2E result artifact.
/// </summary>
[TestClass]
public sealed class GpeditImportRefusalTests
{
    /// <summary>
    /// The bad fixtures, the import problem code and the loader code that the details box shows for each (the engine's
    /// TemplateImport codes, docs/failure-modes/template-store.md, section C). The loader code is empty
    /// when none belongs.
    /// </summary>
    private static readonly (string Case, string Problem, string Load)[] BadCases =
    [
        ("MalformedXml", "LoadFailed", "MalformedXml"),
        ("DtdEntity", "LoadFailed", "DtdProhibited"),
        ("MissingAdml", "AdmlMissing", "MissingAdml"),
        ("UndefinedString", "LoadFailed", "UndefinedString"),
        ("DuplicatePolicy", "LoadFailed", "DuplicatePolicy"),
        ("ListItemNoKey", "LoadFailed", "MissingAttribute"),
        ("BooleanNoValueName", "LoadFailed", "MissingAttribute"),
        ("UndefinedUsing", "LoadFailed", "UndefinedUsing"),
        ("ClassLowerCase", "LoadFailed", "InvalidAttributeValue"),
        ("BoolAttributeUpper", "LoadFailed", "InvalidAttributeValue"),
        ("BoolAttributeMixed", "LoadFailed", "InvalidAttributeValue"),
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
    public void Gpedit_Import_Refused_BadFixtures()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("import", "refused.fixtures");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-import-refused-fixtures");
        sandbox.SeedStandard();
        var sources = new ImportSources(sandbox);
        SortedDictionary<string, string> systemBefore = ImportSources.Hashes(sandbox.TemplateRoot);
        using GpeditRun run = GpeditRun.Open("gpedit-import-refused-fixtures", sandbox);
        run.GoTo(GpeditTemplatesRun.AdminComputer);

        using GpeditTemplatesDialog? dialog = GpeditTemplatesDialog.Open(run);
        checks.Record("opens", dialog is not null, "the dialog opens.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        foreach ((string name, string problem, string load) in BadCases)
        {
            string path = ImportSources.BadFixture(name);
            SortedDictionary<string, string> sourceBefore = ImportSources.Hashes(Path.GetDirectoryName(path)!);
            AddOutcome outcome = dialog.AddFile(path, out string notes);
            string text = dialog.DetailsText();
            checks.Record(name + ".box", outcome == AddOutcome.Refused, $"outcome={outcome}; {notes}");
            checks.Record(name + ".names", text.Contains("Bwc" + name, StringComparison.Ordinal), $"details='{text}' names the file.");
            checks.Record(name + ".codes", text.Contains(problem, StringComparison.Ordinal) && text.Contains(load, StringComparison.Ordinal), $"details='{text}' has {problem} and {load}.");
            checks.Record(name + ".closes", dialog.CloseDetails(), "the details box closes with its Close button.");
            checks.Record(name + ".dialogStays", dialog.IsOpen && dialog.IsEnabled(TemplateIds.Add), "the dialog is still open and Add is enabled.");
            checks.Record(name + ".listUnchanged", dialog.Rows().Count == 0, $"rows={dialog.Rows().Describe()}.");
            checks.Record(name + ".noStore", !Directory.Exists(sandbox.ImportStore), $"store exists={Directory.Exists(sandbox.ImportStore)}.");
            checks.Record(name + ".sourceUnchanged", ImportSources.SameHashes(sourceBefore, ImportSources.Hashes(Path.GetDirectoryName(path)!)), "the fixture folder is as it was.");
        }

        // The next Add works after a refusal, and a bad neighbour in the source folder does not block it (C18).
        TemplateSpec good = new("Two", "BWC.Policies.Imported.Two");
        string goodPath = sources.Write("neighbour", good);
        _ = sources.WriteFile("neighbour", "BwcBrokenNeighbour.admx", "<not xml");
        checks.Record("afterRefusal.add", dialog.AddFile(goodPath, out string goodNotes) == AddOutcome.Added, $"rows={dialog.Rows().Describe()}; {goodNotes}");
        checks.Record("afterRefusal.storeOnlyGood", sources.StoreFiles().SequenceEqual([good.AdmxName, Path.Combine("en-US", good.File + ".adml")]), $"store holds {string.Join(", ", sources.StoreFiles())}; the neighbour was not copied.");
        checks.Record("closes", dialog.Close(), "Close closes the dialog.");
        checks.Record("oneReload", run.WaitDialogReload(1), $"TemplateReloads={run.Str("TemplateReloads")}: only the good import changed anything.");
        checks.Record("systemUnchanged", ImportSources.SameHashes(systemBefore, ImportSources.Hashes(sandbox.TemplateRoot)), "the system template folder is as it was.");

        run.Measure("cases", string.Join("|", BadCases.Select(c => c.Case)));
        run.WriteArtifacts("gpedit-import-refused-fixtures.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Import_Refused_ClassicAdm()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("import", "refused.adm");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-import-refused-adm");
        sandbox.SeedStandard();
        var sources = new ImportSources(sandbox);
        string adm = sources.WriteFile("classic", "BwcOld.adm", "CLASS MACHINE\r\nCATEGORY \"BWC old template\"\r\nEND CATEGORY\r\n");
        string text = sources.WriteFile("classic", "BwcNotes.txt", "not a template");
        using GpeditRun run = GpeditRun.Open("gpedit-import-refused-adm", sandbox);
        run.GoTo(GpeditTemplatesRun.AdminComputer);

        using GpeditTemplatesDialog? dialog = GpeditTemplatesDialog.Open(run);
        checks.Record("opens", dialog is not null, "the dialog opens.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        // BEH-008, DIFF-005: classic .adm files are not accepted.
        AddOutcome outcome = dialog.AddFile(adm, out string notes);
        string admText = dialog.DetailsText();
        checks.Record("adm.box", outcome == AddOutcome.Refused, $"outcome={outcome}; {notes}");
        checks.Record("adm.code", admText.Contains("ClassicAdmNotSupported", StringComparison.Ordinal), $"details='{admText}'.");
        checks.Record("adm.names", admText.Contains("BwcOld.adm", StringComparison.Ordinal), $"details='{admText}' names the file.");
        _ = dialog.CloseDetails();

        outcome = dialog.AddFile(text, out notes);
        string otherText = dialog.DetailsText();
        checks.Record("other.box", outcome == AddOutcome.Refused, $"outcome={outcome}; {notes}");
        checks.Record("other.code", otherText.Contains("NotAdmx", StringComparison.Ordinal) && !otherText.Contains("ClassicAdmNotSupported", StringComparison.Ordinal), $"details='{otherText}'.");
        _ = dialog.CloseDetails();

        checks.Record("dialogStays", dialog.IsOpen, "the dialog is still open.");
        checks.Record("listUnchanged", dialog.Rows().Count == 0, $"rows={dialog.Rows().Describe()}.");
        checks.Record("noStore", !Directory.Exists(sandbox.ImportStore), $"store exists={Directory.Exists(sandbox.ImportStore)}.");
        checks.Record("closes", dialog.Close(), "Close closes the dialog.");
        checks.Record("noReload", run.Count("TemplateReloads") == 0, $"TemplateReloads={run.Str("TemplateReloads")}.");

        run.WriteArtifacts("gpedit-import-refused-adm.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Import_Refused_Duplicates()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("import", "refused.duplicates");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-import-refused-duplicates");
        sandbox.SeedStandard();
        var sources = new ImportSources(sandbox);
        TemplateSpec one = new("One", "BWC.Policies.Imported.One");
        TemplateSpec sameName = new("One", "BWC.Policies.Imported.OneOther");
        string onePath = sources.Write("one", one);
        string sameNamePath = sources.Write("same-name", sameName);
        string systemPath = sources.CopyFixtureBase("system-namespace");
        using GpeditRun run = GpeditRun.Open("gpedit-import-refused-duplicates", sandbox);
        run.GoTo(GpeditTemplatesRun.AdminComputer);

        using GpeditTemplatesDialog? dialog = GpeditTemplatesDialog.Open(run);
        checks.Record("opens", dialog is not null, "the dialog opens.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        checks.Record("first.added", dialog.AddFile(onePath, out _) == AddOutcome.Added, $"rows={dialog.Rows().Describe()}.");
        string[] storeAfterFirst = sources.StoreFiles();

        // The same file again: the namespace is in the store (C13).
        AddOutcome outcome = dialog.AddFile(onePath, out _);
        string text = dialog.DetailsText();
        checks.Record("again.box", outcome == AddOutcome.Refused, $"outcome={outcome}.");
        checks.Record("again.code", text.Contains("NamespaceAlreadyImported", StringComparison.Ordinal), $"details='{text}'.");
        _ = dialog.CloseDetails();

        // A template whose namespace the system folder defines (C12, ADMX-001): refused, never imported as shadowed.
        outcome = dialog.AddFile(systemPath, out _);
        text = dialog.DetailsText();
        checks.Record("system.box", outcome == AddOutcome.Refused, $"outcome={outcome}.");
        checks.Record("system.code", text.Contains("NamespaceDefinedBySystem", StringComparison.Ordinal), $"details='{text}'.");
        _ = dialog.CloseDetails();

        // Another namespace under a file name the store has (C14): refused, the stored file is not overwritten.
        outcome = dialog.AddFile(sameNamePath, out _);
        text = dialog.DetailsText();
        checks.Record("fileName.box", outcome == AddOutcome.Refused, $"outcome={outcome}.");
        checks.Record("fileName.code", text.Contains("FileNameInUse", StringComparison.Ordinal), $"details='{text}'.");
        _ = dialog.CloseDetails();

        checks.Record("storeUnchanged", sources.StoreFiles().SequenceEqual(storeAfterFirst) && File.ReadAllBytes(Path.Combine(sandbox.ImportStore, one.AdmxName)).AsSpan().SequenceEqual(File.ReadAllBytes(onePath)), $"store holds {string.Join(", ", sources.StoreFiles())}.");
        checks.Record("listUnchanged", dialog.Rows().Select(r => r.Name).SequenceEqual([one.AdmxName]) && dialog.Rows().All(r => r.Status == TemplateIds.StatusLoaded), $"rows={dialog.Rows().Describe()}.");
        checks.Record("closes", dialog.Close(), "Close closes the dialog.");
        checks.Record("oneReload", run.WaitDialogReload(1), $"TemplateReloads={run.Str("TemplateReloads")}: only the first import changed anything.");

        run.WriteArtifacts("gpedit-import-refused-duplicates.png");
        checks.AssertAllPassed();
    }
}

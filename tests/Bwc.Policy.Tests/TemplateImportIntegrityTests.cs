using Bwc.Policy.Store;

namespace Bwc.Policy.Tests;

/// <summary>
/// Findings F16 and F17 of the 0.2.0 audit, written before the fix
/// (docs/failure-modes/template-store.md, section I).
/// F16: the ADMX that the check parses and the ADMX and ADML that the import writes come from different
/// reads, so a source
/// that changes between the reads is imported unchecked. F17: the check loads the whole source folder
/// and nothing limits the
/// number of files it reads. Each test names the failure mode it covers (I1 to I10).
/// </summary>
[TestClass]
public sealed class TemplateImportIntegrityTests
{
    // The cap of I7. The failure-modes page names the value; the test builds a folder one file above it and one at it.
    private const int SourceFileCap = 1000;

    private static readonly byte[] NotXml = "<not xml"u8.ToArray();

    /// <summary>The same template with a trailing XML comment: still valid, different bytes.</summary>
    private static byte[] Variant(string source) =>
        System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(source) + "\n<!-- changed -->\n");

    private static string[] StoreFiles(TemplateStoreSandbox box) => TemplateStoreSandbox.FilesBelow(box.StoreFolder).ToArray();

    private static string Describe(TemplateImportCheck check) =>
        string.Join(", ", check.Problems.Select(p => p.Code + (p.LoadCode is null ? string.Empty : "/" + p.LoadCode)));

    /// <summary>A check that raised the stage the seam promises; a missing stage fails the test for the
    /// right reason.</summary>
    private static void AssertStages(IReadOnlyList<TemplateImportStage> seen, params TemplateImportStage[] expected) =>
        CollectionAssert.AreEqual(expected, seen.ToArray(), "The observer must see " + string.Join(", ", expected) + " (failure-modes I8).");

    // ---- F16: the bytes that are parsed are the bytes that are written
    // -----------------------------------------------

    /// <summary>I1: the source is X when it is first read, a valid P while the loader parses it, and X
    /// again afterwards.</summary>
    [TestMethod]
    public void Check_SourceThatIsOtherBytesWhileItLoadsIsNotChecked_I1()
    {
        using var box = new TemplateStoreSandbox();
        var source = box.AddTemplate(box.Folder("src"), "Imp", "T.Imp");
        var parsedBytes = File.ReadAllBytes(source);
        File.WriteAllBytes(source, NotXml);
        var seen = new List<TemplateImportStage>();
        var importer = new TemplateImport(box.Sources(), stage =>
        {
            seen.Add(stage);
            File.WriteAllBytes(source, stage == TemplateImportStage.AdmxRead ? parsedBytes : NotXml);
        });

        var check = importer.Check(source);
        AssertStages(seen, TemplateImportStage.AdmxRead, TemplateImportStage.Loaded);
        Assert.IsTrue(
            !check.CanImport || parsedBytes.AsSpan().SequenceEqual(check.Admx),
            "The check passed, and the bytes it would write are not the bytes it parsed. Problems: " + Describe(check));
    }

    /// <summary>I1 at the import: the store must not end up with a file that the check never parsed.</summary>
    [TestMethod]
    public void Import_SourceThatIsOtherBytesWhileItLoadsLeavesNothingInTheStore_I1()
    {
        using var box = new TemplateStoreSandbox();
        var folder = box.Folder("src");
        box.AddTemplate(folder, "Base", "T.Base");
        var source = box.AddTemplate(folder, "Dep", "T.Dep");
        var parsedBytes = File.ReadAllBytes(source);
        var usesNeighbour = System.Text.Encoding.UTF8.GetBytes(
            TemplateStoreSandbox.AdmxText("T.Dep", "t", """<using prefix="b" namespace="T.Base" />""", TemplateStoreSandbox.PolicyText("P1")));
        File.WriteAllBytes(source, usesNeighbour);
        var importer = new TemplateImport(box.Sources(), stage =>
            File.WriteAllBytes(source, stage == TemplateImportStage.AdmxRead ? parsedBytes : usesNeighbour));

        var result = importer.Import(source);
        Assert.IsFalse(result.Imported, "A template that uses a namespace only a neighbour defines was imported (UsingNotInstalled was never checked).");
        Assert.AreEqual(0, StoreFiles(box).Length, "The store holds " + string.Join(", ", StoreFiles(box)));
    }

    /// <summary>I2: the ADML is read after the load and copied without being compared with what the loader
    /// parsed.</summary>
    [TestMethod]
    public void Check_AdmlThatChangesAfterTheLoadIsNotCopiedUnchecked_I2()
    {
        using var box = new TemplateStoreSandbox();
        var source = box.AddTemplate(box.Folder("src"), "Imp", "T.Imp");
        var adml = Path.Combine(Path.GetDirectoryName(source)!, "en-US", "Imp.adml");
        var parsedBytes = File.ReadAllBytes(adml);
        var seen = new List<TemplateImportStage>();
        var importer = new TemplateImport(box.Sources(), stage =>
        {
            seen.Add(stage);
            if (stage == TemplateImportStage.Loaded)
            {
                File.WriteAllBytes(adml, NotXml);
            }
        });

        var check = importer.Check(source);
        AssertStages(seen, TemplateImportStage.AdmxRead, TemplateImportStage.Loaded);
        Assert.IsTrue(
            !check.CanImport || parsedBytes.AsSpan().SequenceEqual(check.Adml),
            "The check passed, and the ADML it would write is not the ADML it parsed. Problems: " + Describe(check));
    }

    /// <summary>I2 at the import.</summary>
    [TestMethod]
    public void Import_AdmlThatChangesAfterTheLoadLeavesNothingInTheStore_I2()
    {
        using var box = new TemplateStoreSandbox();
        var source = box.AddTemplate(box.Folder("src"), "Imp", "T.Imp");
        var adml = Path.Combine(Path.GetDirectoryName(source)!, "en-US", "Imp.adml");
        var importer = new TemplateImport(box.Sources(), stage =>
        {
            if (stage == TemplateImportStage.Loaded)
            {
                File.WriteAllBytes(adml, NotXml);
            }
        });

        var result = importer.Import(source);
        Assert.IsFalse(result.Imported, "A broken ADML was imported.");
        Assert.AreEqual(0, StoreFiles(box).Length, "The store holds " + string.Join(", ", StoreFiles(box)));
    }

    /// <summary>I3: the ADMX changes after the load and stays changed. Refused today (SourceChanged); it
    /// must stay refused.</summary>
    [TestMethod]
    public void Check_AdmxThatChangesAfterTheLoadIsRefusedOrConsistent_I3()
    {
        using var box = new TemplateStoreSandbox();
        var source = box.AddTemplate(box.Folder("src"), "Imp", "T.Imp");
        var parsedBytes = File.ReadAllBytes(source);
        var other = Variant(source);
        var importer = new TemplateImport(box.Sources(), stage =>
        {
            if (stage == TemplateImportStage.Loaded)
            {
                File.WriteAllBytes(source, other);
            }
        });

        var check = importer.Check(source);
        Assert.IsTrue(
            !check.CanImport || parsedBytes.AsSpan().SequenceEqual(check.Admx),
            "The check passed with bytes it did not parse. Problems: " + Describe(check));
    }

    /// <summary>I4: the ADMX changes between its first read and the load and stays changed. Refused today;
    /// it must stay refused.</summary>
    [TestMethod]
    public void Check_AdmxThatChangesBeforeTheLoadIsRefusedOrConsistent_I4()
    {
        using var box = new TemplateStoreSandbox();
        var source = box.AddTemplate(box.Folder("src"), "Imp", "T.Imp");
        var firstBytes = File.ReadAllBytes(source);
        var other = Variant(source);
        var importer = new TemplateImport(box.Sources(), stage =>
        {
            if (stage == TemplateImportStage.AdmxRead)
            {
                File.WriteAllBytes(source, other);
            }
        });

        var check = importer.Check(source);
        Assert.IsTrue(
            !check.CanImport || other.AsSpan().SequenceEqual(check.Admx),
            "The check passed with bytes it did not parse. First bytes: " + firstBytes.Length + ". Problems: " + Describe(check));
    }

    /// <summary>I5: an unchanged source still imports, and the stored bytes are the bytes that were parsed
    /// (the seam changes nothing).</summary>
    [TestMethod]
    public void Import_UnchangedSourceStillImportsByteForByte_I5()
    {
        using var box = new TemplateStoreSandbox();
        var source = box.AddTemplate(box.Folder("src"), "Imp", "T.Imp");
        var seen = new List<TemplateImportStage>();
        var importer = new TemplateImport(box.Sources(), seen.Add);

        var result = importer.Import(source);
        AssertStages(seen, TemplateImportStage.AdmxRead, TemplateImportStage.Loaded);
        Assert.IsTrue(result.Imported, string.Join(", ", result.Problems.Select(p => p.Code)));
        CollectionAssert.AreEqual(File.ReadAllBytes(source), File.ReadAllBytes(result.AdmxPath!));
    }

    // ---- F17: the number of files read from the source folder is capped
    // ---------------------------------------------

    /// <summary>Fills a source folder with neighbours that are not templates (they only have to be named
    /// .admx).</summary>
    private static string SourceWithNeighbours(TemplateStoreSandbox box, int neighbours)
    {
        var folder = box.Folder("src");
        for (var i = 0; i < neighbours; i++)
        {
            File.WriteAllText(Path.Combine(folder, "N" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + ".admx"), "x");
        }

        return box.AddTemplate(folder, "Imp", "T.Imp");
    }

    /// <summary>I6: one file over the cap is refused with a typed problem, before the store is touched.</summary>
    [TestMethod]
    public void Check_SourceFolderOverTheCapIsRefused_I6()
    {
        using var box = new TemplateStoreSandbox();
        var source = SourceWithNeighbours(box, SourceFileCap);
        var check = box.Importer().Check(source);
        Assert.IsFalse(check.CanImport, "A source folder with " + (SourceFileCap + 1) + " ADMX files was accepted.");
        Assert.IsTrue(
            check.Problems.Any(p => p.Code == TemplateImportProblemCode.SourceFolderTooLarge),
            "Expected SourceFolderTooLarge in: " + Describe(check));
        Assert.IsFalse(Directory.Exists(box.StoreFolder));
    }

    /// <summary>I6 at the import: nothing is written.</summary>
    [TestMethod]
    public void Import_SourceFolderOverTheCapWritesNothing_I6()
    {
        using var box = new TemplateStoreSandbox();
        var source = SourceWithNeighbours(box, SourceFileCap);
        var result = box.Importer().Import(source);
        Assert.IsFalse(result.Imported);
        Assert.IsTrue(result.Problems.Any(p => p.Code == TemplateImportProblemCode.SourceFolderTooLarge), string.Join(", ", result.Problems.Select(p => p.Code)));
        Assert.IsFalse(Directory.Exists(box.StoreFolder));
    }

    /// <summary>I7: a folder exactly at the cap is accepted (the limit is the count of ADMX files including
    /// the chosen one).</summary>
    [TestMethod]
    public void Check_SourceFolderAtTheCapIsAccepted_I7()
    {
        using var box = new TemplateStoreSandbox();
        var source = SourceWithNeighbours(box, SourceFileCap - 1);
        var check = box.Importer().Check(source);
        Assert.IsTrue(check.CanImport, "A source folder with exactly " + SourceFileCap + " ADMX files was refused: " + Describe(check));
    }

    /// <summary>I7: files that are not ADMX files do not count.</summary>
    [TestMethod]
    public void Check_FilesThatAreNotAdmxDoNotCountTowardTheCap_I7()
    {
        using var box = new TemplateStoreSandbox();
        var folder = box.Folder("src");
        for (var i = 0; i < SourceFileCap + 500; i++)
        {
            File.WriteAllText(Path.Combine(folder, "N" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + ".txt"), "x");
        }

        var source = box.AddTemplate(folder, "Imp", "T.Imp");
        var check = box.Importer().Check(source);
        Assert.IsTrue(check.CanImport, "Other files were counted: " + Describe(check));
    }
}

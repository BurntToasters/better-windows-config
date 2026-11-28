using Bwc.Policy.Admx;
using Bwc.Policy.Store;
using Bwc.Update;

namespace Bwc.Policy.Tests;

[TestClass]
public sealed class TemplateImportTests
{
    private static void AssertRefused(TemplateImportResult result, TemplateImportProblemCode code)
    {
        Assert.IsFalse(result.Imported);
        Assert.IsTrue(result.Problems.Any(p => p.Code == code), "Expected " + code + " in: " + string.Join(", ", result.Problems.Select(p => p.Code + " " + p.Detail)));
    }

    [TestMethod]
    public void Import_CopiesTheAdmxAndAdmlAndTheSetLoadsThem()
    {
        using var box = new TemplateStoreSandbox();
        box.AddTemplate(box.SystemFolder, "Sys", "T.Sys");
        var source = box.AddTemplate(box.Folder("src"), "Imp", "T.Imp", policy: "ImportedPolicy");
        var result = box.Importer().Import(source);
        Assert.IsTrue(result.Imported, string.Join(", ", result.Problems.Select(p => p.Code)));
        Assert.AreEqual("T.Imp", result.Namespace);
        Assert.AreEqual(Path.Combine(box.StoreFolder, "Imp.admx"), result.AdmxPath);
        CollectionAssert.AreEqual(File.ReadAllBytes(source), File.ReadAllBytes(result.AdmxPath!));
        CollectionAssert.AreEqual(
            File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(source)!, "en-US", "Imp.adml")),
            File.ReadAllBytes(result.AdmlPath!));
        var loaded = box.Sources().Load();
        Assert.AreEqual(0, loaded.Set.Failures.Count, FailureAssert.Describe(loaded.Set));
        Assert.IsNotNull(loaded.Set.FindPolicy("T.Imp", "ImportedPolicy"));
        Assert.AreEqual("T.Imp", loaded.ImportedFiles.Single().Namespace);
        Assert.AreEqual(0, TemplateStoreSandbox.FilesBelow(box.StoreFolder).Count(f => f.EndsWith(".tmp", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Import_NeverWritesTheSystemFolderOrTheSourceFolder()
    {
        using var box = new TemplateStoreSandbox();
        box.AddTemplate(box.SystemFolder, "Sys", "T.Sys");
        var sourceFolder = box.Folder("src");
        var source = box.AddTemplate(sourceFolder, "Imp", "T.Imp");
        var before = (TemplateStoreSandbox.FilesBelow(box.SystemFolder), TemplateStoreSandbox.FilesBelow(sourceFolder));
        Assert.IsTrue(box.Importer().Import(source).Imported);
        CollectionAssert.AreEqual(before.Item1.ToArray(), TemplateStoreSandbox.FilesBelow(box.SystemFolder).ToArray());
        CollectionAssert.AreEqual(before.Item2.ToArray(), TemplateStoreSandbox.FilesBelow(sourceFolder).ToArray());
    }

    [TestMethod]
    public void Import_TheStoreFolderGetsTheProtectedAcl()
    {
        using var box = new TemplateStoreSandbox();
        var source = box.AddTemplate(box.Folder("src"), "Imp", "T.Imp");
        Assert.IsTrue(box.Importer().Import(source).Imported);
        Assert.AreEqual(TemplateStoreStatus.Ready, box.Sources().InspectStore().Status);
    }

    [TestMethod]
    public void Import_FallsBackToEnUsWhenTheCultureHasNoAdml()
    {
        using var box = new TemplateStoreSandbox();
        var source = box.AddTemplate(box.Folder("src"), "Imp", "T.Imp");
        var result = box.Importer().Import(source, "de-DE");
        Assert.IsTrue(result.Imported);
        Assert.IsTrue(File.Exists(Path.Combine(box.StoreFolder, "en-US", "Imp.adml")));
        Assert.IsFalse(Directory.Exists(Path.Combine(box.StoreFolder, "de-DE")));
    }

    [TestMethod]
    public void Import_CopiesTheAdmlOfTheUiCultureWhenItExists()
    {
        using var box = new TemplateStoreSandbox();
        var folder = box.Folder("src");
        var source = box.AddTemplate(folder, "Imp", "T.Imp", culture: "de-DE");
        var result = box.Importer().Import(source, "de-DE");
        Assert.IsTrue(result.Imported);
        Assert.IsTrue(File.Exists(Path.Combine(box.StoreFolder, "de-DE", "Imp.adml")));
        Assert.AreEqual("de-DE", box.Sources().Load("de-DE").Set.Files.Single().Culture);
    }

    [TestMethod]
    public void Check_PredictsTheAdmlOfTheLanguageChain_NotOnlyTheExactCulture()
    {
        using var box = new TemplateStoreSandbox();
        var source = box.AddTemplate(box.Folder("src"), "Imp", "T.Imp", culture: "de");
        var check = box.Importer().Check(source, "de-AT");
        Assert.IsTrue(check.CanImport, string.Join(", ", check.Problems.Select(p => p.Code)));
        Assert.AreEqual("de", check.Culture);
    }

    [TestMethod]
    public void Import_RefusesATemplateWithNoAdmlAndCreatesNoStore()
    {
        using var box = new TemplateStoreSandbox();
        var folder = box.Folder("src");
        var source = box.AddTemplate(folder, "Imp", "T.Imp");
        Directory.Delete(Path.Combine(folder, "en-US"), recursive: true);
        AssertRefused(box.Importer().Import(source), TemplateImportProblemCode.AdmlMissing);
        Assert.IsFalse(Directory.Exists(box.StoreFolder));
    }

    [TestMethod]
    public void Import_RefusesClassicAdmAndOtherExtensionsAndMissingFiles()
    {
        using var box = new TemplateStoreSandbox();
        var folder = box.Folder("src");
        var adm = Path.Combine(folder, "Old.adm");
        File.WriteAllText(adm, "CLASS MACHINE");
        var adml = box.AddTemplate(folder, "Imp", "T.Imp").Replace(".admx", ".adml", StringComparison.Ordinal);
        File.WriteAllText(adml, "x");
        var importer = box.Importer();
        AssertRefused(importer.Import(adm), TemplateImportProblemCode.ClassicAdmNotSupported);
        AssertRefused(importer.Import(Path.ChangeExtension(adm, ".ADM")), TemplateImportProblemCode.ClassicAdmNotSupported);
        AssertRefused(importer.Import(adml), TemplateImportProblemCode.NotAdmx);
        AssertRefused(importer.Import(Path.Combine(folder, "Nope.admx")), TemplateImportProblemCode.SourceMissing);
        AssertRefused(importer.Import(string.Empty), TemplateImportProblemCode.SourceMissing);
        AssertRefused(importer.Import("relative.admx"), TemplateImportProblemCode.SourceMissing);
        AssertRefused(importer.Import(folder), TemplateImportProblemCode.NotAdmx);
        Assert.IsFalse(Directory.Exists(box.StoreFolder));
    }

    [TestMethod]
    public void Import_RefusesAFileOverTheSizeLimit()
    {
        using var box = new TemplateStoreSandbox();
        var big = Path.Combine(box.Folder("src"), "Big.admx");
        using (var stream = File.Create(big))
        {
            stream.SetLength(AdmxLimits.MaxFileBytes + 1);
        }

        var result = box.Importer().Import(big);
        AssertRefused(result, TemplateImportProblemCode.SourceTooLarge);
        Assert.IsFalse(Directory.Exists(box.StoreFolder));
    }

    [TestMethod]
    public void Import_RefusesASourceFolderThatIsAJunction()
    {
        using var box = new TemplateStoreSandbox();
        var real = box.Folder("real");
        box.AddTemplate(real, "Imp", "T.Imp");
        var link = Path.Combine(box.Root, "link");
        box.MakeJunction(link, real);
        AssertRefused(box.Importer().Import(Path.Combine(link, "Imp.admx")), TemplateImportProblemCode.SourceReparsePoint);
        Assert.IsFalse(Directory.Exists(box.StoreFolder));
    }

    [TestMethod]
    public void Import_RefusesEveryBadFixtureAndWritesNothing()
    {
        var cases = new (string Folder, string File, TemplateFailureCode Code)[]
        {
            ("MalformedXml", "BwcMalformedXml.admx", TemplateFailureCode.MalformedXml),
            ("DtdEntity", "BwcDtdEntity.admx", TemplateFailureCode.DtdProhibited),
            ("DuplicatePolicy", "BwcDuplicatePolicy.admx", TemplateFailureCode.DuplicatePolicy),
            ("UndefinedString", "BwcUndefinedString.admx", TemplateFailureCode.UndefinedString),
            ("UndefinedUsing", "BwcUndefinedUsing.admx", TemplateFailureCode.UndefinedUsing),
        };
        foreach (var (folder, file, code) in cases)
        {
            using var box = new TemplateStoreSandbox();
            var result = box.Importer().Import(Path.Combine(Fixture.Bad(folder), file));
            AssertRefused(result, TemplateImportProblemCode.LoadFailed);
            Assert.IsTrue(result.Problems.Any(p => p.LoadCode == code), folder + ": " + string.Join(", ", result.Problems.Select(p => p.LoadCode)));
            Assert.IsFalse(Directory.Exists(box.StoreFolder), folder + " left a store behind.");
        }

        using var missing = new TemplateStoreSandbox();
        var noAdml = missing.Importer().Import(Path.Combine(Fixture.Bad("MissingAdml"), "BwcMissingAdml.admx"));
        AssertRefused(noAdml, TemplateImportProblemCode.AdmlMissing);
        Assert.IsFalse(Directory.Exists(missing.StoreFolder));
    }

    [TestMethod]
    public void Import_RefusesANamespaceTheSystemFolderDefines()
    {
        using var box = new TemplateStoreSandbox();
        box.AddTemplate(box.SystemFolder, "Sys", "T.Same");
        var source = box.AddTemplate(box.Folder("src"), "Imp", "t.same");
        AssertRefused(box.Importer().Import(source), TemplateImportProblemCode.NamespaceDefinedBySystem);
        Assert.IsFalse(Directory.Exists(box.StoreFolder));
    }

    [TestMethod]
    public void Import_RefusesANamespaceAlreadyImported()
    {
        using var box = new TemplateStoreSandbox();
        var first = box.AddTemplate(box.Folder("one"), "Imp", "T.Imp");
        var second = box.AddTemplate(box.Folder("two"), "Other", "T.Imp");
        Assert.IsTrue(box.Importer().Import(first).Imported);
        AssertRefused(box.Importer().Import(second), TemplateImportProblemCode.NamespaceAlreadyImported);
        AssertRefused(box.Importer().Import(first), TemplateImportProblemCode.NamespaceAlreadyImported);
        Assert.AreEqual(1, box.Sources().Load().ImportedFiles.Count);
    }

    [TestMethod]
    public void Import_RefusesAFileNameThatAnotherTemplateOfTheStoreHas()
    {
        using var box = new TemplateStoreSandbox();
        var first = box.AddTemplate(box.Folder("one"), "Same", "T.One");
        var second = box.AddTemplate(box.Folder("two"), "Same", "T.Two");
        Assert.IsTrue(box.Importer().Import(first).Imported);
        AssertRefused(box.Importer().Import(second), TemplateImportProblemCode.FileNameInUse);
        Assert.AreEqual("T.One", box.Sources().Load().ImportedFiles.Single().Namespace);
    }

    [TestMethod]
    public void Import_ResolvesUsingsAgainstTheSystemFolder()
    {
        using var box = new TemplateStoreSandbox();
        box.AddTemplate(box.SystemFolder, "Sys", "T.Sys");
        var source = box.AddTemplate(box.Folder("src"), "Imp", "T.Imp", usings: """<using prefix="s" namespace="T.Sys" />""");
        Assert.IsTrue(box.Importer().Import(source).Imported);
    }

    [TestMethod]
    public void Import_RefusesAUsingThatOnlyANeighbourOfTheSourceDefines()
    {
        using var box = new TemplateStoreSandbox();
        var folder = box.Folder("src");
        box.AddTemplate(folder, "Base", "T.Base");
        var dependent = box.AddTemplate(folder, "Dep", "T.Dep", usings: """<using prefix="b" namespace="T.Base" />""");
        var result = box.Importer().Import(dependent);
        AssertRefused(result, TemplateImportProblemCode.UsingNotInstalled);
        Assert.AreEqual("T.Base", result.Problems.Single(p => p.Code == TemplateImportProblemCode.UsingNotInstalled).Detail);
        Assert.IsFalse(Directory.Exists(box.StoreFolder));
        Assert.IsTrue(box.Importer().Import(Path.Combine(folder, "Base.admx")).Imported);
        Assert.IsTrue(box.Importer().Import(dependent).Imported, "After the base is imported the dependent links.");
    }

    [TestMethod]
    public void Import_IgnoresABadNeighbourOfTheChosenFile()
    {
        using var box = new TemplateStoreSandbox();
        var folder = box.Folder("src");
        var good = box.AddTemplate(folder, "Good", "T.Good");
        File.WriteAllText(Path.Combine(folder, "Broken.admx"), "<not xml");
        Assert.IsTrue(box.Importer().Import(good).Imported);
        Assert.IsFalse(File.Exists(Path.Combine(box.StoreFolder, "Broken.admx")));
    }

    [TestMethod]
    public void Import_TheOracleFixtureImportsInOrderAndBaseFirst()
    {
        using var box = new TemplateStoreSandbox();
        var importer = box.Importer();
        var oracle = Path.Combine(Fixture.Root, "BwcOracle.admx");
        var baseFile = Path.Combine(Fixture.Root, "BwcOracleBase.admx");
        AssertRefused(importer.Import(oracle), TemplateImportProblemCode.UsingNotInstalled);
        Assert.IsTrue(importer.Import(baseFile).Imported);
        Assert.IsTrue(importer.Import(oracle).Imported);
        var loaded = box.Sources().Load();
        Assert.AreEqual(0, loaded.Set.Failures.Count, FailureAssert.Describe(loaded.Set));
        Assert.AreEqual(Fixture.Load().Policies.Count, loaded.Set.Policies.Count);
    }

    [TestMethod]
    public void Import_RefusesAStoreInsideTheSystemFolder()
    {
        using var box = new TemplateStoreSandbox();
        var source = box.AddTemplate(box.Folder("src"), "Imp", "T.Imp");
        var inside = Path.Combine(box.SystemFolder, "inner");
        var importer = new TemplateImport(new TemplateSources(box.SystemFolder, inside, box.Acl));
        AssertRefused(importer.Import(source), TemplateImportProblemCode.StoreUnavailable);
        Assert.IsFalse(Directory.Exists(inside));
    }

    [TestMethod]
    public void Import_RefusesAnUntrustedStoreBeforeCopying()
    {
        using var box = new TemplateStoreSandbox();
        box.Writer().EnsureStore();
        TemplateStoreSandbox.GrantEveryoneWrite(box.StoreFolder);
        var source = box.AddTemplate(box.Folder("src"), "Imp", "T.Imp");
        AssertRefused(box.Importer().Import(source), TemplateImportProblemCode.StoreUnavailable);
        Assert.AreEqual(0, TemplateStoreSandbox.FilesBelow(box.StoreFolder).Count);
    }

    [TestMethod]
    public void Import_ReportsAWriteFailureAsATypedProblem()
    {
        using var box = new TemplateStoreSandbox();
        var source = box.AddTemplate(box.Folder("src"), "Imp", "T.Imp");
        Directory.CreateDirectory(box.StoreFolder);
        File.WriteAllText(Path.Combine(box.StoreFolder, "en-US"), "a file where the language folder goes");
        var result = box.Importer().Import(source);
        AssertRefused(result, TemplateImportProblemCode.WriteFailed);
        Assert.AreEqual(TemplateStoreFailure.NotADirectory, result.Problems.Single(p => p.Code == TemplateImportProblemCode.WriteFailed).StoreFailure);
        Assert.IsFalse(File.Exists(Path.Combine(box.StoreFolder, "Imp.admx")));
    }

    [TestMethod]
    public void Import_WithShipRulesInANonElevatedProcessFailsTypedAndLeavesNothing()
    {
        using var box = new TemplateStoreSandbox();
        var source = box.AddTemplate(box.Folder("src"), "Imp", "T.Imp");
        // The store's parent is missing: an existing parent made by this user is refused up front as an untrusted owner.
        var store = Path.Combine(box.Root, "outer", "store");
        var importer = new TemplateImport(new TemplateSources(box.SystemFolder, store, ProtectedAcl.Ship()));
        var result = importer.Import(source);
        if (result.Imported)
        {
            Assert.Inconclusive("The test process can own a folder as Administrators (it is elevated).");
        }

        var problem = result.Problems.Single();
        Assert.AreEqual(TemplateImportProblemCode.WriteFailed, problem.Code);
        Assert.IsTrue(problem.StoreFailure is TemplateStoreFailure.AccessDenied or TemplateStoreFailure.OwnerNotAllowed, problem.StoreFailure?.ToString());
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(store)));
    }

    [TestMethod]
    public void Import_CancellationThrowsAndWritesNothing()
    {
        using var box = new TemplateStoreSandbox();
        var source = box.AddTemplate(box.Folder("src"), "Imp", "T.Imp");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => box.Importer().Import(source, null, cancelled.Token));
        Assert.IsFalse(Directory.Exists(box.StoreFolder));
    }

    [TestMethod]
    public void Check_WritesNothing()
    {
        using var box = new TemplateStoreSandbox();
        var source = box.AddTemplate(box.Folder("src"), "Imp", "T.Imp");
        var check = box.Importer().Check(source);
        Assert.IsTrue(check.CanImport);
        Assert.AreEqual("T.Imp", check.Namespace);
        Assert.AreEqual("Imp.admx", check.AdmxFileName);
        Assert.AreEqual("en-US", check.Culture);
        Assert.IsFalse(Directory.Exists(box.StoreFolder));
    }

    [TestMethod]
    public void Remove_DeletesTheTemplateBothFilesAndTheSetNoLongerHasIt()
    {
        using var box = new TemplateStoreSandbox();
        var source = box.AddTemplate(box.Folder("src"), "Imp", "T.Imp");
        var importer = box.Importer();
        Assert.IsTrue(importer.Import(source).Imported);
        var removed = importer.Remove("T.Imp");
        Assert.IsTrue(removed.Removed, string.Join(", ", removed.Problems.Select(p => p.Code)));
        Assert.AreEqual(2, removed.RemovedPaths.Count);
        Assert.AreEqual(0, TemplateStoreSandbox.FilesBelow(box.StoreFolder).Count);
        Assert.IsNull(box.Sources().Load().Set.FindFile("T.Imp"));
        Assert.IsTrue(importer.Import(source).Imported, "A removed template can be imported again.");
    }

    [TestMethod]
    public void Remove_DeletesANonEnglishOnlyTemplateWhenUiCultureMatches()
    {
        using var box = new TemplateStoreSandbox();
        var source = box.AddTemplate(box.Folder("src"), "Imp", "T.Imp", culture: "de-DE");
        var importer = box.Importer();
        Assert.IsTrue(importer.Import(source, "de-DE").Imported);
        var removed = importer.Remove("T.Imp", "de-DE");
        Assert.IsTrue(removed.Removed, string.Join(", ", removed.Problems.Select(p => p.Code)));
        Assert.AreEqual(2, removed.RemovedPaths.Count);
        Assert.AreEqual(0, TemplateStoreSandbox.FilesBelow(box.StoreFolder).Count);
        Assert.IsNull(box.Sources().Load("de-DE").Set.FindFile("T.Imp"));
    }

    [TestMethod]
    public void Remove_RefusesWhileAnotherImportUsesTheNamespace()
    {
        using var box = new TemplateStoreSandbox();
        var folder = box.Folder("src");
        var baseFile = box.AddTemplate(folder, "Base", "T.Base");
        var dependent = box.AddTemplate(folder, "Dep", "T.Dep", usings: """<using prefix="b" namespace="T.Base" />""");
        var importer = box.Importer();
        Assert.IsTrue(importer.Import(baseFile).Imported);
        Assert.IsTrue(importer.Import(dependent).Imported);
        var refused = importer.Remove("T.Base");
        Assert.IsFalse(refused.Removed);
        var problem = refused.Problems.Single();
        Assert.AreEqual(TemplateImportProblemCode.UsedByTemplate, problem.Code);
        Assert.AreEqual(Path.Combine(box.StoreFolder, "Dep.admx"), problem.Path);
        Assert.IsTrue(File.Exists(Path.Combine(box.StoreFolder, "Base.admx")));
        Assert.IsTrue(importer.Remove("T.Dep").Removed);
        Assert.IsTrue(importer.Remove("T.Base").Removed);
    }

    [TestMethod]
    public void Remove_RefusesWhileANonEnglishOnlyDependentUsesTheNamespace()
    {
        using var box = new TemplateStoreSandbox();
        var folder = box.Folder("src");
        var baseFile = box.AddTemplate(folder, "Base", "T.Base", culture: "de-DE");
        var dependent = box.AddTemplate(folder, "Dep", "T.Dep", culture: "de-DE", usings: """<using prefix="b" namespace="T.Base" />""");
        var importer = box.Importer();
        Assert.IsTrue(importer.Import(baseFile, "de-DE").Imported);
        Assert.IsTrue(importer.Import(dependent, "de-DE").Imported);
        var refused = importer.Remove("T.Base", "de-DE");
        Assert.IsFalse(refused.Removed);
        var problem = refused.Problems.Single();
        Assert.AreEqual(TemplateImportProblemCode.UsedByTemplate, problem.Code);
        Assert.AreEqual(Path.Combine(box.StoreFolder, "Dep.admx"), problem.Path);
        Assert.IsTrue(File.Exists(Path.Combine(box.StoreFolder, "Base.admx")));
        Assert.IsTrue(importer.Remove("T.Dep", "de-DE").Removed);
        Assert.IsTrue(importer.Remove("T.Base", "de-DE").Removed);
    }

    [TestMethod]
    public void Remove_NeverTouchesTheSystemFolder()
    {
        using var box = new TemplateStoreSandbox();
        box.AddTemplate(box.SystemFolder, "Sys", "T.Sys");
        var source = box.AddTemplate(box.Folder("src"), "Imp", "T.Imp");
        var importer = box.Importer();
        Assert.IsTrue(importer.Import(source).Imported);
        var before = TemplateStoreSandbox.FilesBelow(box.SystemFolder).ToArray();
        Assert.AreEqual(TemplateImportProblemCode.NotImported, importer.Remove("T.Sys").Problems.Single().Code);
        Assert.AreEqual(TemplateImportProblemCode.NotImported, importer.RemoveFile("Sys.admx").Problems.Single().Code);
        Assert.AreEqual(TemplateImportProblemCode.NotImported, importer.Remove("T.Unknown").Problems.Single().Code);
        CollectionAssert.AreEqual(before, TemplateStoreSandbox.FilesBelow(box.SystemFolder).ToArray());
    }

    [TestMethod]
    public void Remove_WithNoStoreIsNotImported()
    {
        using var box = new TemplateStoreSandbox();
        Assert.AreEqual(TemplateImportProblemCode.NotImported, box.Importer().Remove("T.Imp").Problems.Single().Code);
        Assert.IsFalse(Directory.Exists(box.StoreFolder));
    }

    [TestMethod]
    public void RemoveFile_RefusesUnsafeNamesAndOutsideFiles()
    {
        using var box = new TemplateStoreSandbox();
        var outside = Path.Combine(box.Root, "victim.admx");
        File.WriteAllText(outside, "keep");
        var source = box.AddTemplate(box.Folder("src"), "Imp", "T.Imp");
        var importer = box.Importer();
        Assert.IsTrue(importer.Import(source).Imported);
        foreach (var name in new[] { @"..\victim.admx", outside, "a/b.admx", "con.admx", "x.txt", string.Empty })
        {
            Assert.AreEqual(TemplateImportProblemCode.UnsafeFileName, importer.RemoveFile(name).Problems.Single().Code, name);
        }

        Assert.IsTrue(File.Exists(outside));
    }

    [TestMethod]
    public void RemoveFile_RemovesATemplateThatDoesNotLoad()
    {
        using var box = new TemplateStoreSandbox();
        box.Writer().EnsureStore();
        File.WriteAllText(Path.Combine(box.StoreFolder, "Broken.admx"), "<not xml");
        var result = box.Importer().RemoveFile("Broken.admx");
        Assert.IsTrue(result.Removed);
        Assert.IsFalse(File.Exists(Path.Combine(box.StoreFolder, "Broken.admx")));
    }
}

using Bwc.Policy.Admx;
using Bwc.Policy.Store;

namespace Bwc.Policy.Tests;

[TestClass]
public sealed class TemplateSourcesTests
{
    [TestMethod]
    public void Load_WithNoStoreLoadsTheSystemFolderAlone()
    {
        using var box = new TemplateStoreSandbox();
        box.AddTemplate(box.SystemFolder, "Sys", "T.Sys");
        var result = box.Sources().Load();
        Assert.AreEqual(TemplateStoreStatus.Missing, result.Store);
        Assert.AreEqual(0, result.Set.Failures.Count, FailureAssert.Describe(result.Set));
        Assert.AreEqual(1, result.Set.Files.Count);
        Assert.AreEqual(0, result.ImportedFiles.Count);
        Assert.AreEqual(0, result.Shadowed.Count);
    }

    [TestMethod]
    public void Load_ReadsTheSystemFolderFirstAndThenTheStore()
    {
        using var box = new TemplateStoreSandbox();
        box.AddTemplate(box.SystemFolder, "Sys", "T.Sys");
        box.Writer().EnsureStore();
        box.AddTemplate(box.StoreFolder, "Imp", "T.Imp");
        var result = box.Sources().Load();
        Assert.AreEqual(TemplateStoreStatus.Ready, result.Store);
        CollectionAssert.AreEqual(new[] { "T.Sys", "T.Imp" }, result.Set.Files.Select(f => f.Namespace).ToArray());
        CollectionAssert.AreEqual(
            new[] { TemplateSources.SystemSourceIndex, TemplateSources.StoreSourceIndex },
            result.Set.Files.Select(f => f.SourceIndex).ToArray());
        Assert.AreEqual("T.Imp", result.ImportedFiles.Single().Namespace);
    }

    [TestMethod]
    public void Load_TheSystemFileWinsAndTheImportIsReportedAsShadowed()
    {
        using var box = new TemplateStoreSandbox();
        var system = box.AddTemplate(box.SystemFolder, "Sys", "T.Same", policy: "FromSystem");
        box.Writer().EnsureStore();
        var shadowed = box.AddTemplate(box.StoreFolder, "Imp", "t.same", policy: "FromImport");
        var result = box.Sources().Load();
        Assert.AreEqual("FromSystem", result.Set.Policies.Single().Name);
        var entry = result.Shadowed.Single();
        Assert.AreEqual(shadowed, entry.Path);
        Assert.AreEqual(system, entry.WinnerPath);
        Assert.AreEqual("t.same", entry.Namespace);
        Assert.AreEqual(0, result.ImportedFiles.Count);
    }

    [TestMethod]
    public void Load_TwoStoreFilesWithOneNamespaceAreADuplicateNotAShadow()
    {
        using var box = new TemplateStoreSandbox();
        box.Writer().EnsureStore();
        box.AddTemplate(box.StoreFolder, "A", "T.Dup");
        box.AddTemplate(box.StoreFolder, "B", "T.Dup");
        var result = box.Sources().Load();
        FailureAssert.Single(result.Set, TemplateFailureCode.DuplicateNamespace);
        Assert.AreEqual(0, result.Shadowed.Count);
    }

    [TestMethod]
    public void Load_ASystemTemplateCanBeUsedByAnImport()
    {
        using var box = new TemplateStoreSandbox();
        box.AddTemplate(box.SystemFolder, "Sys", "T.Sys");
        box.Writer().EnsureStore();
        box.AddTemplate(box.StoreFolder, "Imp", "T.Imp", usings: """<using prefix="s" namespace="T.Sys" />""");
        var result = box.Sources().Load();
        Assert.AreEqual(0, result.Set.Failures.Count, FailureAssert.Describe(result.Set));
        Assert.AreEqual(2, result.Set.Files.Count);
    }

    [TestMethod]
    public void Load_AStoreThatAnotherTrusteeCanWriteIsNotLoaded()
    {
        using var box = new TemplateStoreSandbox();
        box.AddTemplate(box.SystemFolder, "Sys", "T.Sys");
        box.Writer().EnsureStore();
        box.AddTemplate(box.StoreFolder, "Planted", "T.Planted");
        TemplateStoreSandbox.GrantEveryoneWrite(box.StoreFolder);
        var result = box.Sources().Load();
        Assert.AreEqual(TemplateStoreStatus.AclRejected, result.Store);
        Assert.IsNull(result.Set.FindFile("T.Planted"));
        Assert.IsNotNull(result.Set.FindFile("T.Sys"), "The system folder still loads.");
        Assert.AreEqual(0, result.ImportedFiles.Count);
    }

    [TestMethod]
    public void Load_AStoreThatIsAJunctionIsNotLoaded()
    {
        using var box = new TemplateStoreSandbox();
        var elsewhere = box.Folder("elsewhere");
        box.AddTemplate(elsewhere, "Far", "T.Far");
        box.MakeJunction(box.StoreFolder, elsewhere);
        var result = box.Sources().Load();
        Assert.AreEqual(TemplateStoreStatus.ReparsePoint, result.Store);
        Assert.IsNull(result.Set.FindFile("T.Far"));
    }

    [TestMethod]
    public void Load_AFileInPlaceOfTheStoreIsReported()
    {
        using var box = new TemplateStoreSandbox();
        File.WriteAllText(box.StoreFolder, "x");
        Assert.AreEqual(TemplateStoreStatus.NotADirectory, box.Sources().Load().Store);
    }

    [TestMethod]
    public void Load_ABadStoreTemplateDoesNotBlockTheOthers()
    {
        using var box = new TemplateStoreSandbox();
        box.AddTemplate(box.SystemFolder, "Sys", "T.Sys");
        box.Writer().EnsureStore();
        box.AddTemplate(box.StoreFolder, "Good", "T.Good");
        File.WriteAllText(Path.Combine(box.StoreFolder, "Bad.admx"), "<not xml");
        var result = box.Sources().Load();
        Assert.AreEqual(2, result.Set.Files.Count);
        FailureAssert.Single(result.Set, TemplateFailureCode.MalformedXml);
    }

    [TestMethod]
    public void Load_AMissingSystemFolderStillLoadsTheStore()
    {
        using var box = new TemplateStoreSandbox();
        Directory.Delete(box.SystemFolder);
        box.Writer().EnsureStore();
        box.AddTemplate(box.StoreFolder, "Imp", "T.Imp");
        var result = box.Sources().Load();
        FailureAssert.Single(result.Set, TemplateFailureCode.FolderNotFound);
        Assert.IsNotNull(result.Set.FindFile("T.Imp"));
    }

    [TestMethod]
    public void Load_UsesTheCultureAndFallsBackToEnUs()
    {
        using var box = new TemplateStoreSandbox();
        box.AddTemplate(box.SystemFolder, "Sys", "T.Sys");
        var result = box.Sources().Load("de-DE");
        Assert.AreEqual("en-US", result.Set.Files.Single().Culture);
        Assert.AreEqual(0, result.Set.Failures.Count);
    }

    [TestMethod]
    public void Load_CancellationThrows()
    {
        using var box = new TemplateStoreSandbox();
        box.AddTemplate(box.SystemFolder, "Sys", "T.Sys");
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => box.Sources().Load(null, source.Token));
    }

    [TestMethod]
    public void Load_NeverCreatesTheStore()
    {
        using var box = new TemplateStoreSandbox();
        box.Sources().Load();
        Assert.IsFalse(Directory.Exists(box.StoreFolder));
    }
}

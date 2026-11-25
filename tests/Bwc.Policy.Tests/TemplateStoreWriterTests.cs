using System.Text;
using Bwc.Policy.Store;

namespace Bwc.Policy.Tests;

[TestClass]
public sealed class TemplateStoreWriterTests
{
    private static readonly byte[] Admx = Encoding.UTF8.GetBytes("admx");
    private static readonly byte[] Adml = Encoding.UTF8.GetBytes("adml");

    private static void AssertRefused(TemplateStoreFailure reason, Action action)
    {
        var e = Assert.ThrowsExactly<TemplateStoreException>(action);
        Assert.AreEqual(reason, e.Reason, e.Message);
    }

    [TestMethod]
    public void Constructor_RefusesUnusableRoots()
    {
        using var box = new TemplateStoreSandbox();
        AssertRefused(TemplateStoreFailure.StoreRootForbidden, () => new TemplateStoreWriter("store", box.SystemFolder, box.Acl));
        AssertRefused(TemplateStoreFailure.StoreRootForbidden, () => new TemplateStoreWriter(string.Empty, box.SystemFolder, box.Acl));
        AssertRefused(TemplateStoreFailure.StoreRootForbidden, () => new TemplateStoreWriter(Path.GetPathRoot(box.Root)!, box.SystemFolder, box.Acl));
        AssertRefused(TemplateStoreFailure.StoreRootNotLocal, () => new TemplateStoreWriter(@"\\server\share\store", box.SystemFolder, box.Acl));
    }

    [TestMethod]
    public void Constructor_RefusesAStoreThatOverlapsTheSystemFolder()
    {
        using var box = new TemplateStoreSandbox();
        AssertRefused(TemplateStoreFailure.StoreRootForbidden, () => new TemplateStoreWriter(box.SystemFolder, box.SystemFolder, box.Acl));
        AssertRefused(TemplateStoreFailure.StoreRootForbidden, () => new TemplateStoreWriter(box.SystemFolder + @"\", box.SystemFolder, box.Acl));
        AssertRefused(TemplateStoreFailure.StoreRootForbidden, () => new TemplateStoreWriter(Path.Combine(box.SystemFolder, "inner"), box.SystemFolder, box.Acl));
        AssertRefused(TemplateStoreFailure.StoreRootForbidden, () => new TemplateStoreWriter(box.Root, box.SystemFolder, box.Acl));
        AssertRefused(TemplateStoreFailure.StoreRootForbidden, () => new TemplateStoreWriter(Path.Combine(box.SystemFolder, "..", "sys", "x"), box.SystemFolder, box.Acl));
    }

    [TestMethod]
    public void EnsureStore_CreatesTheFolderOnce()
    {
        using var box = new TemplateStoreSandbox();
        var writer = box.Writer();
        Assert.IsFalse(Directory.Exists(box.StoreFolder));
        writer.EnsureStore();
        Assert.IsTrue(Directory.Exists(box.StoreFolder));
        writer.EnsureStore();
        Assert.AreEqual(TemplateStoreStatus.Ready, box.Sources().InspectStore().Status);
    }

    [TestMethod]
    public void WriteTemplate_WritesBothFilesAndNoTempFile()
    {
        using var box = new TemplateStoreSandbox();
        box.Writer().WriteTemplate("Foo.admx", Admx, "en-US", Adml);
        CollectionAssert.AreEqual(new[] { "Foo.admx", @"en-US\Foo.adml" }, TemplateStoreSandbox.FilesBelow(box.StoreFolder).ToArray());
        CollectionAssert.AreEqual(Admx, File.ReadAllBytes(Path.Combine(box.StoreFolder, "Foo.admx")));
        CollectionAssert.AreEqual(Adml, File.ReadAllBytes(Path.Combine(box.StoreFolder, "en-US", "Foo.adml")));
    }

    [TestMethod]
    public void WriteTemplate_ReplacesAnExistingPair()
    {
        using var box = new TemplateStoreSandbox();
        var writer = box.Writer();
        writer.WriteTemplate("Foo.admx", Admx, "en-US", Adml);
        writer.WriteTemplate("Foo.admx", Encoding.UTF8.GetBytes("second"), "en-US", Encoding.UTF8.GetBytes("second-adml"));
        Assert.AreEqual("second", File.ReadAllText(Path.Combine(box.StoreFolder, "Foo.admx")));
        Assert.AreEqual(2, TemplateStoreSandbox.FilesBelow(box.StoreFolder).Count);
    }

    [TestMethod]
    public void WriteTemplate_RefusesUnsafeNamesBeforeAnyWrite()
    {
        using var box = new TemplateStoreSandbox();
        var writer = box.Writer();
        foreach (var name in new[]
                 {
                     @"..\x.admx", "a/b.admx", @"a\b.admx", "con.admx", "NUL.admx", "x.admx:stream", "x.txt", ".admx", "x.admx.", "x .admx ", "",
                     @"C:\x.admx",
                 })
        {
            AssertRefused(TemplateStoreFailure.UnsafeName, () => writer.WriteTemplate(name, Admx, "en-US", Adml));
        }

        foreach (var culture in new[] { "..", ".", @"..\x", "a/b", "", "con", "en-US:s" })
        {
            AssertRefused(TemplateStoreFailure.UnsafeName, () => writer.WriteTemplate("Foo.admx", Admx, culture, Adml));
        }

        Assert.IsFalse(Directory.Exists(box.StoreFolder), "No name check may leave a store behind.");
        Assert.AreEqual(0, TemplateStoreSandbox.FilesBelow(box.Root).Count);
    }

    [TestMethod]
    public void WriteTemplate_RefusesALanguageFolderThatIsAJunction()
    {
        using var box = new TemplateStoreSandbox();
        var elsewhere = box.Folder("elsewhere");
        box.Writer().EnsureStore();
        box.MakeJunction(Path.Combine(box.StoreFolder, "en-US"), elsewhere);
        AssertRefused(TemplateStoreFailure.ReparsePoint, () => box.Writer().WriteTemplate("Foo.admx", Admx, "en-US", Adml));
        Assert.AreEqual(0, TemplateStoreSandbox.FilesBelow(elsewhere).Count, "Nothing may be written through the link.");
        Assert.IsFalse(File.Exists(Path.Combine(box.StoreFolder, "Foo.admx")));
    }

    [TestMethod]
    public void EnsureStore_RefusesAStoreThatIsAJunction()
    {
        using var box = new TemplateStoreSandbox();
        var elsewhere = box.Folder("elsewhere");
        box.MakeJunction(box.StoreFolder, elsewhere);
        AssertRefused(TemplateStoreFailure.ReparsePoint, () => box.Writer().EnsureStore());
        AssertRefused(TemplateStoreFailure.ReparsePoint, () => box.Writer().WriteTemplate("Foo.admx", Admx, "en-US", Adml));
        Assert.AreEqual(0, TemplateStoreSandbox.FilesBelow(elsewhere).Count);
    }

    [TestMethod]
    public void EnsureStore_RefusesAFileInPlaceOfTheStore()
    {
        using var box = new TemplateStoreSandbox();
        File.WriteAllText(box.StoreFolder, "not a folder");
        AssertRefused(TemplateStoreFailure.NotADirectory, () => box.Writer().EnsureStore());
    }

    [TestMethod]
    public void WriteTemplate_RollsBackANewAdmlWhenTheAdmxCannotBeWritten()
    {
        using var box = new TemplateStoreSandbox();
        Directory.CreateDirectory(Path.Combine(box.StoreFolder, "Foo.admx"));
        box.Writer().EnsureStore();
        var e = Assert.ThrowsExactly<TemplateStoreException>(() => box.Writer().WriteTemplate("Foo.admx", Admx, "en-US", Adml));
        Assert.AreEqual(TemplateStoreFailure.Io, e.Reason);
        Assert.IsFalse(File.Exists(Path.Combine(box.StoreFolder, "en-US", "Foo.adml")), "A partial copy must not leave the ADML.");
        Assert.AreEqual(0, Directory.EnumerateFiles(box.StoreFolder, "*", SearchOption.AllDirectories).Count());
    }

    [TestMethod]
    public void WriteTemplate_KeepsAnAdmlThatExistedBeforeTheFailedCall()
    {
        using var box = new TemplateStoreSandbox();
        var writer = box.Writer();
        writer.WriteTemplate("Other.admx", Admx, "en-US", Adml);
        File.WriteAllBytes(Path.Combine(box.StoreFolder, "en-US", "Foo.adml"), Encoding.UTF8.GetBytes("old"));
        Directory.CreateDirectory(Path.Combine(box.StoreFolder, "Foo.admx"));
        AssertRefused(TemplateStoreFailure.Io, () => writer.WriteTemplate("Foo.admx", Admx, "en-US", Adml));
        Assert.IsTrue(File.Exists(Path.Combine(box.StoreFolder, "en-US", "Foo.adml")));
    }

    [TestMethod]
    public void WriteTemplate_ReportsAFileInUseAndKeepsTheOldFile()
    {
        using var box = new TemplateStoreSandbox();
        var writer = box.Writer();
        writer.WriteTemplate("Foo.admx", Admx, "en-US", Adml);
        var target = Path.Combine(box.StoreFolder, "Foo.admx");
        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var e = Assert.ThrowsExactly<TemplateStoreException>(() => writer.WriteTemplate("Foo.admx", Encoding.UTF8.GetBytes("new"), "en-US", Adml));
            Assert.AreEqual(TemplateStoreFailure.InUse, e.Reason, e.Message);
            Assert.IsTrue(e.Win32Error is 32 or 33);
            Assert.AreEqual(target, e.Path);
        }

        Assert.AreEqual("admx", File.ReadAllText(target));
        CollectionAssert.AreEqual(new[] { "Foo.admx", @"en-US\Foo.adml" }, TemplateStoreSandbox.FilesBelow(box.StoreFolder).ToArray(), "No temp file may stay.");
    }

    [TestMethod]
    public void RemoveTemplate_DeletesTheAdmxAndEveryLanguageAdml()
    {
        using var box = new TemplateStoreSandbox();
        var writer = box.Writer();
        writer.WriteTemplate("Foo.admx", Admx, "en-US", Adml);
        writer.WriteTemplate("Bar.admx", Admx, "en-US", Adml);
        writer.WriteTemplate("Foo.admx", Admx, "de-DE", Adml);
        var removed = writer.RemoveTemplate("Foo.admx");
        Assert.AreEqual(3, removed.Count);
        CollectionAssert.AreEqual(new[] { "Bar.admx", @"en-US\Bar.adml" }, TemplateStoreSandbox.FilesBelow(box.StoreFolder).ToArray());
    }

    [TestMethod]
    public void RemoveTemplate_NeverFollowsALanguageJunction()
    {
        using var box = new TemplateStoreSandbox();
        var writer = box.Writer();
        writer.WriteTemplate("Foo.admx", Admx, "en-US", Adml);
        var elsewhere = box.Folder("elsewhere");
        File.WriteAllBytes(Path.Combine(elsewhere, "Foo.adml"), Adml);
        box.MakeJunction(Path.Combine(box.StoreFolder, "fr-FR"), elsewhere);
        writer.RemoveTemplate("Foo.admx");
        Assert.IsTrue(File.Exists(Path.Combine(elsewhere, "Foo.adml")), "A file behind a junction must survive.");
    }

    [TestMethod]
    public void RemoveTemplate_OnAMissingStoreDoesNothing()
    {
        using var box = new TemplateStoreSandbox();
        Assert.AreEqual(0, box.Writer().RemoveTemplate("Foo.admx").Count);
        Assert.IsFalse(Directory.Exists(box.StoreFolder));
    }

    [TestMethod]
    public void RemoveTemplate_RefusesUnsafeNames()
    {
        using var box = new TemplateStoreSandbox();
        var outside = Path.Combine(box.Root, "victim.admx");
        File.WriteAllText(outside, "keep");
        box.Writer().EnsureStore();
        AssertRefused(TemplateStoreFailure.UnsafeName, () => box.Writer().RemoveTemplate(@"..\victim.admx"));
        AssertRefused(TemplateStoreFailure.UnsafeName, () => box.Writer().RemoveTemplate(outside));
        Assert.IsTrue(File.Exists(outside));
    }

    [TestMethod]
    public void WriteTemplate_RefusesAStoreWithForeignWriteAccess()
    {
        using var box = new TemplateStoreSandbox();
        box.Writer().EnsureStore();
        TemplateStoreSandbox.GrantEveryoneWrite(box.StoreFolder);
        AssertRefused(TemplateStoreFailure.StoreAclRejected, () => box.Writer().EnsureStore());
        AssertRefused(TemplateStoreFailure.StoreAclRejected, () => box.Writer().WriteTemplate("Foo.admx", Admx, "en-US", Adml));
        AssertRefused(TemplateStoreFailure.StoreAclRejected, () => box.Writer().RemoveTemplate("Foo.admx"));
        Assert.AreEqual(0, TemplateStoreSandbox.FilesBelow(box.StoreFolder).Count);
    }

    [TestMethod]
    public void FileNames_FollowTheSafeSegmentRules()
    {
        Assert.IsTrue(TemplateFileNames.IsSafeAdmxFileName("Windows.admx"));
        Assert.IsTrue(TemplateFileNames.IsSafeAdmxFileName("Odd Name (1).ADMX"));
        Assert.IsFalse(TemplateFileNames.IsSafeAdmxFileName("Windows.adml"));
        Assert.IsFalse(TemplateFileNames.IsSafeAdmxFileName("COM1.admx"));
        Assert.IsFalse(TemplateFileNames.IsSafeAdmxFileName("lpt9.txt.admx"));
        Assert.IsFalse(TemplateFileNames.IsSafeAdmxFileName(new string('a', 252) + ".admx"));
        Assert.IsTrue(TemplateFileNames.IsSafeSegment("en-US"));
        Assert.IsFalse(TemplateFileNames.IsSafeSegment(" en-US"));
        Assert.IsFalse(TemplateFileNames.IsSafeSegment("en-US."));
        Assert.IsFalse(TemplateFileNames.IsSafeSegment("a\0b"));
        Assert.IsFalse(TemplateFileNames.IsSafeSegment(null));
    }
}

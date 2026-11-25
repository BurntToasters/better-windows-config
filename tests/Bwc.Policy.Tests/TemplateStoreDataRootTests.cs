using Bwc.Policy.Store;
using Bwc.Update;

namespace Bwc.Policy.Tests;

/// <summary>
/// AF5 (PD-1, STORE-1): the folder that holds the import store (the BWC data root) is created protected and checked
/// with the store and the language folders. Each case runs in a temp sandbox with the current user rules, so nothing
/// touches the real common application data folder. The cases are in docs/failure-modes/template-store.md, section H.
/// </summary>
[TestClass]
public sealed class TemplateStoreDataRootTests
{
    private static (TemplateStoreSandbox Box, string Root, string Store) NewBox()
    {
        var box = new TemplateStoreSandbox();
        var root = Path.Combine(box.Root, "data");
        return (box, root, Path.Combine(root, "store"));
    }

    private static TemplateStoreWriter WriterFor(TemplateStoreSandbox box, string store, ProtectedAcl? acl = null) =>
        new(store, box.SystemFolder, acl ?? box.Acl);

    private static void AssertRefusedNaming(TemplateStoreException failure, TemplateStoreFailure reason, string folder)
    {
        Assert.AreEqual(reason, failure.Reason, failure.Message);
        Assert.AreEqual(folder, failure.Path, "the refusal must name the folder that failed");
        StringAssert.Contains(failure.Message, folder);
    }

    [TestMethod]
    public void WriteTemplate_CreatesAMissingDataRootWithTheProtectedAccessList()
    {
        var (box, root, store) = NewBox();
        using (box)
        {
            WriterFor(box, store).WriteTemplate("Foo.admx", [1], "en-US", [2]);

            foreach (var folder in new[] { root, store, Path.Combine(store, "en-US") })
            {
                var security = new DirectoryInfo(folder).GetAccessControl();
                Assert.IsTrue(security.AreAccessRulesProtected, folder + " must not inherit its access list.");
                Assert.IsNull(box.Acl.Inspect(security), folder);
            }
        }
    }

    [TestMethod]
    public void EnsureStore_RefusesADataRootThatIsAJunction_AndCreatesNothingThroughIt()
    {
        var (box, root, store) = NewBox();
        using (box)
        {
            var elsewhere = box.Folder("elsewhere");
            box.MakeJunction(root, elsewhere);

            var failure = Assert.ThrowsExactly<TemplateStoreException>(() => WriterFor(box, store).EnsureStore());

            AssertRefusedNaming(failure, TemplateStoreFailure.ReparsePoint, root);
            Assert.AreEqual(0, Directory.GetFileSystemEntries(elsewhere).Length);
        }
    }

    [TestMethod]
    public void EnsureStore_RefusesADataRootThatGrantsWriteToAnotherTrustee_AndCreatesNothingInIt()
    {
        var (box, root, store) = NewBox();
        using (box)
        {
            Directory.CreateDirectory(root);
            TemplateStoreSandbox.GrantEveryoneWrite(root);

            var failure = Assert.ThrowsExactly<TemplateStoreException>(() => WriterFor(box, store).EnsureStore());

            AssertRefusedNaming(failure, TemplateStoreFailure.StoreAclRejected, root);
            StringAssert.Contains(failure.Message, "S-1-1-0");
            Assert.AreEqual(0, Directory.GetFileSystemEntries(root).Length);
        }
    }

    [TestMethod]
    public void EnsureStore_RefusesADataRootWithAForeignOwner()
    {
        // A folder this process made is owned by this user; under the ship rules only Administrators may own. (Setting a
        // foreign owner needs a restore privilege that an unelevated test does not have.)
        var (box, root, store) = NewBox();
        using (box)
        {
            Directory.CreateDirectory(root);
            var why = ProtectedAcl.Ship().Inspect(new DirectoryInfo(root).GetAccessControl());
            if (why is null || !why.StartsWith("the owner", StringComparison.Ordinal))
            {
                Assert.Inconclusive("The test folder is owned by Administrators (an elevated test process).");
            }

            var failure = Assert.ThrowsExactly<TemplateStoreException>(() => WriterFor(box, store, ProtectedAcl.Ship()).EnsureStore());

            AssertRefusedNaming(failure, TemplateStoreFailure.OwnerNotAllowed, root);
            Assert.AreEqual(1307, failure.Win32Error);
            Assert.AreEqual(0, Directory.GetFileSystemEntries(root).Length);
        }
    }

    [TestMethod]
    public void WriteTemplate_RefusesALanguageFolderThatIsAJunction_AndLeavesTheTargetUntouched()
    {
        var (box, _, store) = NewBox();
        using (box)
        {
            var writer = WriterFor(box, store);
            writer.EnsureStore();
            var elsewhere = box.Folder("elsewhere");
            var language = Path.Combine(store, "en-US");
            box.MakeJunction(language, elsewhere);

            var failure = Assert.ThrowsExactly<TemplateStoreException>(() => writer.WriteTemplate("Foo.admx", [1], "en-US", [2]));

            AssertRefusedNaming(failure, TemplateStoreFailure.ReparsePoint, language);
            Assert.AreEqual(0, Directory.GetFileSystemEntries(elsewhere).Length);
            Assert.IsFalse(File.Exists(Path.Combine(store, "Foo.admx")));
        }
    }

    [TestMethod]
    public void WriteTemplate_RefusesAStoreThatIsAJunction_AndLeavesTheTargetUntouched()
    {
        var (box, root, store) = NewBox();
        using (box)
        {
            Directory.CreateDirectory(root);
            var elsewhere = box.Folder("elsewhere");
            box.MakeJunction(store, elsewhere);

            var failure = Assert.ThrowsExactly<TemplateStoreException>(() => WriterFor(box, store).WriteTemplate("Foo.admx", [1], "en-US", [2]));

            AssertRefusedNaming(failure, TemplateStoreFailure.ReparsePoint, store);
            Assert.AreEqual(0, Directory.GetFileSystemEntries(elsewhere).Length);
        }
    }

    [TestMethod]
    public void WriteTemplate_RefusesAfterTheDataRootIsSwappedForAJunction()
    {
        var (box, root, store) = NewBox();
        using (box)
        {
            var writer = WriterFor(box, store);
            writer.WriteTemplate("Foo.admx", [1], "en-US", [2]);
            Directory.Delete(root, recursive: true);
            var elsewhere = box.Folder("elsewhere");
            box.MakeJunction(root, elsewhere);

            var failure = Assert.ThrowsExactly<TemplateStoreException>(() => writer.WriteTemplate("Bar.admx", [1], "en-US", [2]));

            AssertRefusedNaming(failure, TemplateStoreFailure.ReparsePoint, root);
            Assert.AreEqual(0, Directory.GetFileSystemEntries(elsewhere).Length);
        }
    }

    [TestMethod]
    public void RemoveTemplate_RefusesADataRootThatIsAJunction_AndDeletesNothing()
    {
        var (box, root, store) = NewBox();
        using (box)
        {
            var elsewhere = box.Folder("elsewhere");
            WriterFor(box, Path.Combine(elsewhere, "store")).WriteTemplate("Foo.admx", [1], "en-US", [2]);
            box.MakeJunction(root, elsewhere);
            var before = TemplateStoreSandbox.FilesBelow(elsewhere);

            var failure = Assert.ThrowsExactly<TemplateStoreException>(() => WriterFor(box, store).RemoveTemplate("Foo.admx"));

            AssertRefusedNaming(failure, TemplateStoreFailure.ReparsePoint, root);
            CollectionAssert.AreEqual(before.ToArray(), TemplateStoreSandbox.FilesBelow(elsewhere).ToArray());
        }
    }

    [TestMethod]
    public void InspectStore_DoesNotLoadAStoreInADataRootThatIsAJunction()
    {
        var (box, root, store) = NewBox();
        using (box)
        {
            var elsewhere = box.Folder("elsewhere");
            WriterFor(box, Path.Combine(elsewhere, "store")).EnsureStore();
            box.MakeJunction(root, elsewhere);

            var (status, detail) = new TemplateSources(box.SystemFolder, store, box.Acl).InspectStore();

            Assert.AreEqual(TemplateStoreStatus.ReparsePoint, status);
            StringAssert.Contains(detail, root);
        }
    }

    [TestMethod]
    public void InspectStore_DoesNotLoadAStoreInADataRootThatAnotherTrusteeCanWrite()
    {
        var (box, root, store) = NewBox();
        using (box)
        {
            WriterFor(box, store).EnsureStore();
            TemplateStoreSandbox.GrantEveryoneWrite(root);

            var (status, detail) = new TemplateSources(box.SystemFolder, store, box.Acl).InspectStore();

            Assert.AreEqual(TemplateStoreStatus.AclRejected, status);
            StringAssert.Contains(detail, root);
        }
    }

    [TestMethod]
    public void InspectStore_ReportsAMissingDataRootAsAMissingStore()
    {
        var (box, root, store) = NewBox();
        using (box)
        {
            Assert.AreEqual(TemplateStoreStatus.Missing, new TemplateSources(box.SystemFolder, store, box.Acl).InspectStore().Status);
            Assert.IsFalse(Directory.Exists(root), "a read creates nothing");
        }
    }

    [TestMethod]
    [DataRow("COM\u00B9")]
    [DataRow("COM\u00B2")]
    [DataRow("COM\u00B3")]
    [DataRow("LPT\u00B9")]
    [DataRow("LPT\u00B2")]
    [DataRow("LPT\u00B3")]
    public void SuperscriptDeviceNames_AreNotSafeSegments(string name)
    {
        Assert.IsFalse(TemplateFileNames.IsSafeSegment(name), name);
        Assert.IsFalse(TemplateFileNames.IsSafeSegment(name.ToLowerInvariant()), name);
        Assert.IsFalse(TemplateFileNames.IsSafeSegment(name + ".txt"), name);
        Assert.IsFalse(TemplateFileNames.IsSafeSegment(name + " "), name);
        Assert.IsFalse(TemplateFileNames.IsSafeAdmxFileName(name + ".admx"), name);
        Assert.IsTrue(TemplateFileNames.IsSafeSegment("COM\u00B9x"), "a longer name is not a device name");
    }
}

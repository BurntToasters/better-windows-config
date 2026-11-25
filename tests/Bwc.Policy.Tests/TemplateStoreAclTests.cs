using System.Security.AccessControl;
using System.Security.Principal;
using Bwc.Policy.Store;
using Bwc.Update;

namespace Bwc.Policy.Tests;

[TestClass]
public sealed class TemplateStoreAclTests
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private static void AssertOnlyFullControl(FileSystemSecurity security, params SecurityIdentifier[] expected)
    {
        Assert.IsTrue(security.AreAccessRulesProtected, "Inheritance must be off.");
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();
        Assert.IsTrue(rules.All(r => !r.IsInherited), "No rule may be inherited.");
        Assert.IsTrue(rules.All(r => r.AccessControlType == AccessControlType.Allow), "Only allow entries.");
        Assert.IsTrue(rules.All(r => (r.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl));
        CollectionAssert.AreEquivalent(
            expected.Select(s => s.Value).ToArray(),
            rules.Select(r => ((SecurityIdentifier)r.IdentityReference).Value).Distinct().ToArray());
    }

    [TestMethod]
    public void ShipRules_DescribeAdministratorsAndSystemOnly()
    {
        var security = ProtectedAcl.Ship().NewSecurity();
        AssertOnlyFullControl(security, System, Administrators);
        Assert.AreEqual(Administrators, security.GetOwner(typeof(SecurityIdentifier)));
    }

    [TestMethod]
    public void EnsureStore_SetsTheProtectedAclOfTheSandboxRules()
    {
        using var box = new TemplateStoreSandbox();
        box.Writer().EnsureStore();
        var user = WindowsIdentity.GetCurrent().User!;
        var security = new DirectoryInfo(box.StoreFolder).GetAccessControl();
        AssertOnlyFullControl(security, System, user);
        Assert.AreEqual(user, security.GetOwner(typeof(SecurityIdentifier)));
        Assert.IsNull(box.Acl.Inspect(security));
    }

    [TestMethod]
    public void WriteTemplate_CreatesTheLanguageFolderWithTheSameAcl()
    {
        using var box = new TemplateStoreSandbox();
        box.Writer().WriteTemplate("Foo.admx", [1], "en-US", [2]);
        var user = WindowsIdentity.GetCurrent().User!;
        AssertOnlyFullControl(new DirectoryInfo(Path.Combine(box.StoreFolder, "en-US")).GetAccessControl(), System, user);
    }

    [TestMethod]
    public void EnsureStore_CreatesMissingParentsWithTheSameAcl()
    {
        using var box = new TemplateStoreSandbox();
        var nested = Path.Combine(box.Root, "outer", "store");
        new TemplateStoreWriter(nested, box.SystemFolder, box.Acl).EnsureStore();
        var user = WindowsIdentity.GetCurrent().User!;
        AssertOnlyFullControl(new DirectoryInfo(Path.Combine(box.Root, "outer")).GetAccessControl(), System, user);
    }

    [TestMethod]
    public void EnsureStore_AcceptsNoStoreThatAnotherTrusteeCanWrite()
    {
        using var box = new TemplateStoreSandbox();
        box.Writer().EnsureStore();
        TemplateStoreSandbox.GrantEveryoneWrite(box.StoreFolder);
        var status = box.Sources().InspectStore();
        Assert.AreEqual(TemplateStoreStatus.AclRejected, status.Status);
        Assert.IsTrue(status.Detail.Contains("S-1-1-0", StringComparison.Ordinal), status.Detail);
    }

    [TestMethod]
    public void ShipRules_InAProcessThatCannotOwnAsAdministratorsFailTyped()
    {
        using var box = new TemplateStoreSandbox();
        var writer = new TemplateStoreWriter(box.StoreFolder, box.SystemFolder, ProtectedAcl.Ship());
        try
        {
            writer.EnsureStore();
        }
        catch (TemplateStoreException e)
        {
            Assert.IsTrue(
                e.Reason is TemplateStoreFailure.AccessDenied or TemplateStoreFailure.OwnerNotAllowed,
                "Unexpected reason " + e.Reason + ": " + e.Message);
            Assert.AreEqual(box.Root, e.Path, "The refusal names the data root (the store's parent), which this user owns.");
            Assert.IsFalse(Directory.Exists(box.StoreFolder), "A refused create must leave nothing.");
            return;
        }

        Assert.Inconclusive("The test process can own a folder as Administrators (it is elevated), so the refusal path was not reached.");
    }
}

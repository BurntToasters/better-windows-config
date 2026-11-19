using System.Security.AccessControl;
using System.Security.Principal;
using Bwc.Gpo.Refresh;
using Bwc.Gpo.Store;
using Bwc.Update;

namespace Bwc.Gpo.Tests;

/// <summary>
/// AF5 (PD-1, GPO-1): the BWC data root and the folders below it are created protected and refused when they are not.
/// Every case runs in a temp sandbox with the current user rules (<see cref="ProtectedAcl.ForCurrentUser"/>); nothing
/// touches the real common application data folder. The cases are in docs/failure-modes/gpo-store.md, section K.
/// </summary>
[TestClass]
public sealed class ProtectedDataRootTests
{
    private static readonly ProtectedAcl Acl = ProtectedAcl.ForCurrentUser();

    private static SandboxGpoFixture NewBox() => new();

    private static void MakeJunctionOrSkip(SandboxGpoFixture fx, string link, string target)
    {
        if (!fx.TryMakeJunction(link, target))
        {
            Assert.Inconclusive("mklink /J is not available here.");
        }
    }

    private static void GrantUsersWrite(string folder)
    {
        var info = new DirectoryInfo(folder);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.WriteData | FileSystemRights.AppendData,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    private static void AssertProtected(string folder)
    {
        var security = new DirectoryInfo(folder).GetAccessControl();
        Assert.IsTrue(security.AreAccessRulesProtected, folder + " must not inherit its access list.");
        Assert.IsNull(Acl.Inspect(security), folder + " must pass the protected rules.");
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.IsTrue(rules.All(r => !r.IsInherited), "No rule may be inherited.");
        Assert.IsTrue(rules.All(r => r.AccessControlType == AccessControlType.Allow));
    }

    private static ProtectedFolderProblem AssertNames(ProtectedFolderProblem? problem, string folder, ProtectedFolderStatus status)
    {
        Assert.IsNotNull(problem, "the folder must be refused");
        Assert.AreEqual(status, problem.Status, problem.Detail);
        Assert.AreEqual(folder, problem.Folder, "the refusal must name the folder that failed");
        return problem;
    }

    [TestMethod]
    public void Ensure_CreatesAMissingRootAndEveryFolderBelowItProtected()
    {
        using var fx = NewBox();
        var root = Path.Combine(fx.RunRoot, "data");
        var target = Path.Combine(root, "gpedit", "backup");

        Assert.IsNull(ProtectedDataRoot.Ensure(root, target, Acl));
        AssertProtected(root);
        AssertProtected(Path.Combine(root, "gpedit"));
        AssertProtected(target);
        Assert.IsNull(ProtectedDataRoot.Ensure(root, target, Acl), "a second call accepts what the first made");
    }

    [TestMethod]
    public void Ensure_RefusesARootThatIsAJunction_AndCreatesNothingThroughIt()
    {
        using var fx = NewBox();
        var root = Path.Combine(fx.RunRoot, "data");
        var elsewhere = Path.Combine(fx.RunRoot, "elsewhere");
        MakeJunctionOrSkip(fx, root, elsewhere);

        var problem = ProtectedDataRoot.Ensure(root, Path.Combine(root, "gpedit", "backup"), Acl);

        AssertNames(problem, root, ProtectedFolderStatus.ReparsePoint);
        Assert.AreEqual(0, Directory.GetFileSystemEntries(elsewhere).Length, "nothing may be created through the junction");
    }

    [TestMethod]
    public void Ensure_RefusesARootThatGrantsWriteToAnotherTrustee_AndCreatesNothingInIt()
    {
        using var fx = NewBox();
        var root = Path.Combine(fx.RunRoot, "data");
        Assert.IsNull(ProtectedDataRoot.Ensure(root, root, Acl));
        GrantUsersWrite(root);

        var problem = ProtectedDataRoot.Ensure(root, Path.Combine(root, "gpedit", "backup"), Acl);

        var named = AssertNames(problem, root, ProtectedFolderStatus.AclRejected);
        StringAssert.Contains(named.Detail, "S-1-5-32-545");
        Assert.AreEqual(0, Directory.GetFileSystemEntries(root).Length);
    }

    [TestMethod]
    public void Ensure_RefusesARootWithAForeignOwner()
    {
        // A folder this process made is owned by this user. Under the ship rules only Administrators may own, so the
        // owner rule refuses it. (Setting a foreign owner needs a restore privilege an unelevated test does not have.)
        using var fx = NewBox();
        var root = Path.Combine(fx.RunRoot, "data");
        Directory.CreateDirectory(root);
        var why = ProtectedAcl.Ship().Inspect(new DirectoryInfo(root).GetAccessControl());
        if (why is null || !why.StartsWith("the owner", StringComparison.Ordinal))
        {
            Assert.Inconclusive("The test folder is owned by Administrators (an elevated test process).");
        }

        var problem = ProtectedDataRoot.Ensure(root, Path.Combine(root, "gpedit"), ProtectedAcl.Ship());

        var named = AssertNames(problem, root, ProtectedFolderStatus.OwnerRejected);
        StringAssert.Contains(named.Detail, "owner");
        Assert.AreEqual(0, Directory.GetFileSystemEntries(root).Length);
    }

    [TestMethod]
    public void Ensure_RefusesAJunctionOnAFolderBetweenTheRootAndTheTarget_AndLeavesTheTargetUntouched()
    {
        using var fx = NewBox();
        var root = Path.Combine(fx.RunRoot, "data");
        Assert.IsNull(ProtectedDataRoot.Ensure(root, root, Acl));
        var middle = Path.Combine(root, "gpedit");
        var elsewhere = Path.Combine(fx.RunRoot, "elsewhere");
        MakeJunctionOrSkip(fx, middle, elsewhere);

        var problem = ProtectedDataRoot.Ensure(root, Path.Combine(middle, "backup"), Acl);

        AssertNames(problem, middle, ProtectedFolderStatus.ReparsePoint);
        Assert.AreEqual(0, Directory.GetFileSystemEntries(elsewhere).Length, "nothing may be created through the junction");
    }

    [TestMethod]
    public void Ensure_RefusesAJunctionOnTheTargetItself()
    {
        using var fx = NewBox();
        var root = Path.Combine(fx.RunRoot, "data");
        Assert.IsNull(ProtectedDataRoot.Ensure(root, Path.Combine(root, "gpedit"), Acl));
        var target = Path.Combine(root, "gpedit", "backup");
        var elsewhere = Path.Combine(fx.RunRoot, "elsewhere");
        MakeJunctionOrSkip(fx, target, elsewhere);

        AssertNames(ProtectedDataRoot.Ensure(root, target, Acl), target, ProtectedFolderStatus.ReparsePoint);
        Assert.AreEqual(0, Directory.GetFileSystemEntries(elsewhere).Length);
    }

    [TestMethod]
    public void Ensure_RefusesAFolderBelowTheRootThatGrantsWriteToAnotherTrustee()
    {
        using var fx = NewBox();
        var root = Path.Combine(fx.RunRoot, "data");
        var middle = Path.Combine(root, "gpedit");
        Assert.IsNull(ProtectedDataRoot.Ensure(root, middle, Acl));
        GrantUsersWrite(middle);

        var problem = ProtectedDataRoot.Ensure(root, Path.Combine(middle, "backup"), Acl);

        AssertNames(problem, middle, ProtectedFolderStatus.AclRejected);
        Assert.AreEqual(0, Directory.GetFileSystemEntries(middle).Length);
    }

    [TestMethod]
    public void Ensure_RefusesAFileWhereAFolderIsNeeded()
    {
        using var fx = NewBox();
        var root = Path.Combine(fx.RunRoot, "data");
        Assert.IsNull(ProtectedDataRoot.Ensure(root, root, Acl));
        var middle = Path.Combine(root, "gpedit");
        fx.PutRawFile(middle, [1]);

        AssertNames(ProtectedDataRoot.Ensure(root, Path.Combine(middle, "backup"), Acl), middle, ProtectedFolderStatus.NotADirectory);
    }

    [TestMethod]
    public void Verify_NeverCreatesAnything_AndReportsTheMissingFolder()
    {
        using var fx = NewBox();
        var root = Path.Combine(fx.RunRoot, "data");

        AssertNames(ProtectedDataRoot.Verify(root, Path.Combine(root, "gpedit"), Acl), root, ProtectedFolderStatus.Missing);
        Assert.IsFalse(Directory.Exists(root));
    }

    [TestMethod]
    public void Ensure_RefusesATargetOutsideTheRoot()
    {
        using var fx = NewBox();
        var root = Path.Combine(fx.RunRoot, "data");
        Assert.ThrowsExactly<ArgumentException>(() => ProtectedDataRoot.Ensure(root, Path.Combine(fx.RunRoot, "data2", "x"), Acl));
        Assert.ThrowsExactly<ArgumentException>(() => ProtectedDataRoot.Ensure(root, Path.Combine(root, "..", "x"), Acl));
        Assert.IsFalse(Directory.Exists(root));
    }

    [TestMethod]
    public void DefaultPath_IsTheCommonApplicationDataFolderPlusTheProductFolder()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BetterWindowsConfig");
        Assert.AreEqual(expected, ProtectedDataRoot.DefaultPath);
    }
}

/// <summary>
/// The system policy's backup root chain (GPO-1), built over a sandbox data root and a sandbox GPO folder so no test
/// reaches the real common application data folder or the real policy folder.
/// </summary>
[TestClass]
public sealed class SystemPolicyBackupRootTests
{
    private sealed class NoRefresh : IPolicyRefresher
    {
        public void Refresh(GpoSide side) => throw new InvalidOperationException("never refreshes");
    }

    private static SystemGpoStorePolicy NewPolicy(SandboxGpoFixture fx, out string dataRoot, out string gpoRoot)
    {
        dataRoot = Path.Combine(fx.RunRoot, "data");
        gpoRoot = Path.Combine(fx.RunRoot, "live");
        return new SystemGpoStorePolicy(gpoRoot, dataRoot, ProtectedAcl.ForCurrentUser(), new NoRefresh());
    }

    private static string[] OneFile(string gpoRoot) => [Path.Combine(gpoRoot, "Machine", "x.bin")];

    [TestMethod]
    public void BackupFiles_CreatesTheRootAndTheChainProtected()
    {
        using var fx = new SandboxGpoFixture();
        var policy = NewPolicy(fx, out var dataRoot, out var gpoRoot);
        var acl = ProtectedAcl.ForCurrentUser();

        var folder = new GpoFileWriter(policy).BackupFiles(OneFile(gpoRoot));

        Assert.IsTrue(File.Exists(Path.Combine(folder, "Machine", "x.bin.absent")));
        foreach (var protectedFolder in new[] { dataRoot, Path.Combine(dataRoot, "gpedit"), policy.BackupRoot })
        {
            var security = new DirectoryInfo(protectedFolder).GetAccessControl();
            Assert.IsTrue(security.AreAccessRulesProtected, protectedFolder);
            Assert.IsNull(acl.Inspect(security), protectedFolder);
        }
    }

    [TestMethod]
    public void BackupFiles_RefusesARootThatIsAJunction_AndWritesNothingThroughIt()
    {
        using var fx = new SandboxGpoFixture();
        var policy = NewPolicy(fx, out var dataRoot, out var gpoRoot);
        var elsewhere = Path.Combine(fx.RunRoot, "elsewhere");
        Assert.IsTrue(fx.TryMakeJunction(dataRoot, elsewhere), "mklink /J failed");

        var failure = Assert.ThrowsExactly<GpoStoreException>(() => new GpoFileWriter(policy).BackupFiles(OneFile(gpoRoot)));

        Assert.AreEqual(GpoStoreFailure.PolicyDenied, failure.Reason);
        StringAssert.Contains(failure.Message, dataRoot);
        Assert.AreEqual(0, Directory.GetFileSystemEntries(elsewhere).Length);
    }

    [TestMethod]
    public void BackupFiles_RefusesAJunctionOnTheGpeditFolder_AndWritesNothingThroughIt()
    {
        using var fx = new SandboxGpoFixture();
        var policy = NewPolicy(fx, out var dataRoot, out var gpoRoot);
        Assert.IsNull(ProtectedDataRoot.Ensure(dataRoot, dataRoot, ProtectedAcl.ForCurrentUser()));
        var gpedit = Path.Combine(dataRoot, "gpedit");
        var elsewhere = Path.Combine(fx.RunRoot, "elsewhere");
        Assert.IsTrue(fx.TryMakeJunction(gpedit, elsewhere), "mklink /J failed");

        var failure = Assert.ThrowsExactly<GpoStoreException>(() => new GpoFileWriter(policy).BackupFiles(OneFile(gpoRoot)));

        Assert.AreEqual(GpoStoreFailure.PolicyDenied, failure.Reason);
        StringAssert.Contains(failure.Message, gpedit);
        Assert.AreEqual(0, Directory.GetFileSystemEntries(elsewhere).Length, "the junction target stays untouched");
    }

    [TestMethod]
    public void BackupFiles_RefusesARootThatAnotherTrusteeCanWrite()
    {
        using var fx = new SandboxGpoFixture();
        var policy = NewPolicy(fx, out var dataRoot, out var gpoRoot);
        Assert.IsNull(ProtectedDataRoot.Ensure(dataRoot, dataRoot, ProtectedAcl.ForCurrentUser()));
        var info = new DirectoryInfo(dataRoot);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.WriteData,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        info.SetAccessControl(security);

        var failure = Assert.ThrowsExactly<GpoStoreException>(() => new GpoFileWriter(policy).BackupFiles(OneFile(gpoRoot)));

        StringAssert.Contains(failure.Message, dataRoot);
        Assert.AreEqual(0, Directory.GetFileSystemEntries(dataRoot).Length);
    }

    [TestMethod]
    public void PruneBackups_RefusesAJunctionOnTheGpeditFolder_AndDeletesNothing()
    {
        using var fx = new SandboxGpoFixture();
        var policy = NewPolicy(fx, out var dataRoot, out _);
        Assert.IsNull(ProtectedDataRoot.Ensure(dataRoot, dataRoot, ProtectedAcl.ForCurrentUser()));
        var gpedit = Path.Combine(dataRoot, "gpedit");
        var elsewhere = Path.Combine(fx.RunRoot, "elsewhere");
        Assert.IsTrue(fx.TryMakeJunction(gpedit, elsewhere), "mklink /J failed");
        var planted = Path.Combine(elsewhere, "backup");
        for (var i = 0; i < 12; i++)
        {
            fx.MakeFolder(Path.Combine(planted, "2000010" + (i % 10) + "-00000" + (i / 10) + "-001"));
        }

        var before = Directory.GetDirectories(planted).Length;
        var failure = Assert.ThrowsExactly<GpoStoreException>(() => new GpoFileWriter(policy).PruneBackups());

        StringAssert.Contains(failure.Message, gpedit);
        Assert.AreEqual(before, Directory.GetDirectories(planted).Length, "nothing may be removed through the junction");
    }

    [TestMethod]
    public void EnsureWriteAllowed_RefusesAPathUnderTheBackupRootOnceTheChainIsSwappedForAJunction()
    {
        using var fx = new SandboxGpoFixture();
        var policy = NewPolicy(fx, out var dataRoot, out var gpoRoot);
        var writer = new GpoFileWriter(policy);
        var first = writer.BackupFiles(OneFile(gpoRoot));
        Assert.IsTrue(Directory.Exists(first));

        var gpedit = Path.Combine(dataRoot, "gpedit");
        Directory.Delete(gpedit, recursive: true);
        var elsewhere = Path.Combine(fx.RunRoot, "elsewhere");
        Assert.IsTrue(fx.TryMakeJunction(gpedit, elsewhere), "mklink /J failed");

        var failure = Assert.ThrowsExactly<GpoStoreException>(() => policy.EnsureWriteAllowed(Path.Combine(policy.BackupRoot, "20000101-000000-001")));
        StringAssert.Contains(failure.Message, gpedit);
        Assert.ThrowsExactly<GpoStoreException>(() => writer.BackupFiles(OneFile(gpoRoot)));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(elsewhere).Length);
    }
}

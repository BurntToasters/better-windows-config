using System.Diagnostics;
using Bwc.Gpo.Pol;
using Bwc.Gpo.Refresh;
using Bwc.Gpo.Store;
using Bwc.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Gpo.Tests;

/// <summary>
/// AF6 (GPO-2): the write lock is a file in the protected backup root, opened with no sharing. Every case runs in a temp
/// sandbox; nothing touches the real common application data folder or the real GPO folder. The cases are in
/// docs/failure-modes/gpo-store.md, section F.
/// </summary>
[TestClass]
public sealed class GpoWriteLockTests
{
    private const string Key = "Software\\Policies\\BWC-Test";

    private sealed class NoRefresh : IPolicyRefresher
    {
        public void Refresh(GpoSide side)
        {
        }
    }

    private static void SetAlpha(GpoEditSession session) => GpoStorePolHelper.SetDword(session.Pol, Key, "Alpha", 1);

    private static void AssertNothingWritten(SandboxGpoFixture fx)
    {
        Assert.IsFalse(Directory.Exists(fx.GpoRoot), "no GPO folder may be created");
        Assert.AreEqual(0, fx.Events.Count);
    }

    [TestMethod]
    public void LockPath_IsTheWriteLockFileInTheBackupRoot_ForTheSandboxPolicy()
    {
        using var fx = new SandboxGpoFixture();

        Assert.AreEqual(Path.Combine(fx.BackupRoot, "write.lock"), fx.Policy.LockPath);
    }

    [TestMethod]
    public void Save_CreatesTheLockFileInTheBackupRoot_AndLeavesItInPlace()
    {
        using var fx = new SandboxGpoFixture();

        fx.NewStore().Save(GpoSide.Machine, SetAlpha);

        Assert.IsTrue(File.Exists(fx.Policy.LockPath));
        SandboxGpoFixture.OpenSharedForRead(fx.Policy.LockPath);
        Assert.AreEqual(0, new FileInfo(fx.Policy.LockPath).Length, "the lock file holds no data");
        Assert.AreEqual(1, fx.BackupFolders().Length, "the lock file is not a backup folder and is never pruned");
    }

    [TestMethod]
    public void SecondStore_WaitsThenTimesOut_WhileTheFirstHoldsTheLockFile()
    {
        using var fx = new SandboxGpoFixture();
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = Task.Run(() => fx.NewStore().Save(GpoSide.Machine, s =>
        {
            SetAlpha(s);
            held.Set();
            release.Wait(TimeSpan.FromSeconds(20));
        }));
        Assert.IsTrue(held.Wait(TimeSpan.FromSeconds(10)));
        var ran = false;

        try
        {
            Assert.IsTrue(File.Exists(fx.Policy.LockPath), "the first save holds a lock file");
            Assert.ThrowsExactly<IOException>(() => SandboxGpoFixture.OpenSharedForRead(fx.Policy.LockPath), "no one else may open the held lock file");

            var second = fx.NewStore(o => o.LockTimeout = TimeSpan.FromMilliseconds(400));
            var clock = Stopwatch.StartNew();
            var ex = Assert.ThrowsExactly<GpoStoreException>(() => second.Save(GpoSide.Machine, _ => ran = true));
            clock.Stop();

            Assert.AreEqual(GpoStoreFailure.LockTimeout, ex.Reason);
            Assert.AreEqual(fx.Policy.LockPath, ex.Path);
            Assert.IsTrue(clock.Elapsed >= TimeSpan.FromMilliseconds(300), $"gave up after only {clock.Elapsed}");
            Assert.IsFalse(ran, "the second edit must not run");
            Assert.AreEqual(0, fx.Events.Count, "nothing is written while the first save holds the lock");
        }
        finally
        {
            release.Set();
        }

        Assert.IsTrue(first.Wait(TimeSpan.FromSeconds(10)));
        Assert.IsTrue(first.Result.Wrote);
        var third = fx.NewStore().Save(GpoSide.Machine, s => GpoStorePolHelper.SetDword(s.Pol, Key, "Beta", 2));
        Assert.IsTrue(third.Wrote, "the lock is free again once the first save ends");
    }

    [TestMethod]
    public void SecondStore_WaitsForTheFirstToFinish_ThenSaves()
    {
        using var fx = new SandboxGpoFixture();
        var lockHeld = fx.HoldLockFile();
        var releaser = Task.Run(() =>
        {
            Thread.Sleep(400);
            lockHeld.Dispose();
        });

        var clock = Stopwatch.StartNew();
        var result = fx.NewStore().Save(GpoSide.Machine, SetAlpha);
        clock.Stop();
        releaser.Wait();

        Assert.IsTrue(result.Wrote);
        Assert.IsTrue(clock.Elapsed >= TimeSpan.FromMilliseconds(250), $"waited only {clock.Elapsed}");
    }

    [TestMethod]
    public void LeftoverLockFile_WithNoHolder_IsFree_AndStaysInPlace()
    {
        using var fx = new SandboxGpoFixture();
        fx.HoldLockFile().Dispose();
        Assert.IsTrue(File.Exists(fx.Policy.LockPath));

        var result = fx.NewStore(o => o.LockTimeout = TimeSpan.FromMilliseconds(200)).Save(GpoSide.Machine, SetAlpha);

        Assert.IsTrue(result.Wrote);
        Assert.IsTrue(File.Exists(fx.Policy.LockPath), "the lock file is never deleted");
    }

    [TestMethod]
    public void LockIsReleasedWhenTheEditThrows_AndWhenTheSaveFails()
    {
        using var fx = new SandboxGpoFixture();
        Assert.ThrowsExactly<InvalidOperationException>(() => fx.NewStore().Save(GpoSide.Machine, _ => throw new InvalidOperationException("boom")));

        var result = fx.NewStore(o => o.LockTimeout = TimeSpan.FromMilliseconds(200)).Save(GpoSide.Machine, SetAlpha);

        Assert.IsTrue(result.Wrote);
    }

    [TestMethod]
    public void Save_WithTheBackupRootSwappedForAJunction_IsRefused_AndNothingIsWrittenThroughIt()
    {
        using var fx = new SandboxGpoFixture();
        var elsewhere = Path.Combine(fx.RunRoot, "elsewhere");
        if (!fx.TryMakeJunction(fx.BackupRoot, elsewhere))
        {
            Assert.Inconclusive("mklink /J is not available here.");
        }

        var ran = false;
        var ex = Assert.ThrowsExactly<GpoStoreException>(() => fx.NewStore().Save(GpoSide.Machine, s =>
        {
            ran = true;
            SetAlpha(s);
        }));

        Assert.AreEqual(GpoStoreFailure.PolicyDenied, ex.Reason);
        Assert.IsFalse(ran);
        Assert.AreEqual(0, Directory.GetFileSystemEntries(elsewhere).Length, "no lock file may be created through the junction");
        AssertNothingWritten(fx);
    }

    [TestMethod]
    public void Save_WithTheLockPathBeingAJunction_IsRefused_AndNothingIsWrittenThroughIt()
    {
        using var fx = new SandboxGpoFixture();
        fx.MakeFolder(fx.BackupRoot);
        var elsewhere = Path.Combine(fx.RunRoot, "elsewhere");
        if (!fx.TryMakeJunction(fx.Policy.LockPath, elsewhere))
        {
            Assert.Inconclusive("mklink /J is not available here.");
        }

        var ex = Assert.ThrowsExactly<GpoStoreException>(() => fx.NewStore().Save(GpoSide.Machine, SetAlpha));

        Assert.AreEqual(GpoStoreFailure.PolicyDenied, ex.Reason);
        Assert.AreEqual(0, Directory.GetFileSystemEntries(elsewhere).Length);
        AssertNothingWritten(fx);
    }

    [TestMethod]
    public void Save_WithTheLockPathBeingAFileSymlink_IsRefused_AndTheTargetIsUntouched()
    {
        using var fx = new SandboxGpoFixture();
        fx.MakeFolder(fx.BackupRoot);
        var victim = Path.Combine(fx.RunRoot, "victim.bin");
        byte[] original = [1, 2, 3, 4];
        fx.PutRawFile(victim, original);
        if (!SandboxGpoFixture.TryMakeFileSymlink(fx.Policy.LockPath, victim))
        {
            Assert.Inconclusive("This account may not create file symbolic links.");
        }

        var ex = Assert.ThrowsExactly<GpoStoreException>(() => fx.NewStore().Save(GpoSide.Machine, SetAlpha));

        Assert.AreEqual(GpoStoreFailure.PolicyDenied, ex.Reason);
        CollectionAssert.AreEqual(original, fx.Read(victim), "the link target is never written");
        SandboxGpoFixture.OpenSharedForRead(victim);
        AssertNothingWritten(fx);
    }

    [TestMethod]
    public void Save_WithTheLockPathBeingAFolder_IsRefused()
    {
        using var fx = new SandboxGpoFixture();
        fx.MakeFolder(fx.Policy.LockPath);

        var ex = Assert.ThrowsExactly<GpoStoreException>(() => fx.NewStore().Save(GpoSide.Machine, SetAlpha));

        Assert.AreEqual(GpoStoreFailure.PolicyDenied, ex.Reason);
        AssertNothingWritten(fx);
    }

    [TestMethod]
    public void Save_WithTheBackupRootBeingAFile_IsALockUnavailableFailure_AndNothingIsWritten()
    {
        using var fx = new SandboxGpoFixture();
        fx.PutRawFile(fx.BackupRoot, [1]);

        var ex = Assert.ThrowsExactly<GpoStoreException>(() => fx.NewStore().Save(GpoSide.Machine, SetAlpha));

        Assert.AreEqual(GpoStoreFailure.LockUnavailable, ex.Reason);
        Assert.AreEqual(fx.Policy.LockPath, ex.Path);
        Assert.AreNotEqual(0, ex.Win32Error);
        AssertNothingWritten(fx);
    }

    [TestMethod]
    public void SystemPolicy_LockPath_IsTheWriteLockFileInItsBackupRoot()
    {
        var policy = new SystemGpoStorePolicy();

        Assert.AreEqual(Path.Combine(policy.BackupRoot, "write.lock"), policy.LockPath);
    }

    [TestMethod]
    public void SystemPolicy_CreatesTheProtectedChainThenTheLockFile_AndASecondHolderTimesOut()
    {
        using var fx = new SandboxGpoFixture();
        var dataRoot = Path.Combine(fx.RunRoot, "data");
        var policy = new SystemGpoStorePolicy(Path.Combine(fx.RunRoot, "live"), dataRoot, ProtectedAcl.ForCurrentUser(), new NoRefresh());
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = Task.Run(() => new GpoStore(policy).Save(GpoSide.Machine, s =>
        {
            SetAlpha(s);
            held.Set();
            release.Wait(TimeSpan.FromSeconds(20));
        }));
        Assert.IsTrue(held.Wait(TimeSpan.FromSeconds(10)));

        try
        {
            Assert.AreEqual(Path.Combine(dataRoot, "gpedit", "backup", "write.lock"), policy.LockPath);
            Assert.IsTrue(File.Exists(policy.LockPath));
            Assert.IsNull(ProtectedDataRoot.Verify(dataRoot, policy.BackupRoot, ProtectedAcl.ForCurrentUser()), "the lock file sits in a protected chain");
            var second = new GpoStore(policy, new GpoStoreOptions { LockTimeout = TimeSpan.FromMilliseconds(300) });
            var ex = Assert.ThrowsExactly<GpoStoreException>(() => second.Save(GpoSide.Machine, SetAlpha));
            Assert.AreEqual(GpoStoreFailure.LockTimeout, ex.Reason);
        }
        finally
        {
            release.Set();
        }

        Assert.IsTrue(first.Wait(TimeSpan.FromSeconds(10)));
    }

    [TestMethod]
    public void SystemPolicy_WithADataRootAnotherUserOwns_IsRefusedBeforeAnyLockFileExists()
    {
        // A standard user may pre-create the data root and own it. Under the ship rules only Administrators may own it, so
        // the chain check refuses it. (An elevated test process owns the folder as Administrators: inconclusive there.)
        using var fx = new SandboxGpoFixture();
        var dataRoot = Path.Combine(fx.RunRoot, "data");
        fx.MakeFolder(dataRoot);
        var why = ProtectedAcl.Ship().Inspect(new DirectoryInfo(dataRoot).GetAccessControl());
        if (why is null)
        {
            Assert.Inconclusive("The test folder is owned by Administrators (an elevated test process).");
        }

        var policy = new SystemGpoStorePolicy(Path.Combine(fx.RunRoot, "live"), dataRoot, ProtectedAcl.Ship(), new NoRefresh());
        var ran = false;

        var ex = Assert.ThrowsExactly<GpoStoreException>(() => new GpoStore(policy).Save(GpoSide.Machine, _ => ran = true));

        Assert.AreEqual(GpoStoreFailure.PolicyDenied, ex.Reason);
        StringAssert.Contains(ex.Message, dataRoot);
        Assert.IsFalse(ran);
        Assert.AreEqual(0, Directory.GetFileSystemEntries(dataRoot).Length, "no folder and no lock file is created in a root that is not protected");
    }

    [TestMethod]
    public void SystemPolicy_WithABackupRootAnotherTrusteeCanWrite_IsRefusedBeforeTheLockFileIsCreated()
    {
        // The backup root grants write to another trustee (a standard user could create the lock file there first).
        using var fx = new SandboxGpoFixture();
        var dataRoot = Path.Combine(fx.RunRoot, "data");
        var policy = new SystemGpoStorePolicy(Path.Combine(fx.RunRoot, "live"), dataRoot, ProtectedAcl.ForCurrentUser(), new NoRefresh());
        Assert.IsNull(ProtectedDataRoot.Ensure(dataRoot, policy.BackupRoot, ProtectedAcl.ForCurrentUser()));
        var info = new DirectoryInfo(policy.BackupRoot);
        var security = info.GetAccessControl();
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
            System.Security.AccessControl.FileSystemRights.WriteData | System.Security.AccessControl.FileSystemRights.CreateFiles,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None,
            System.Security.AccessControl.AccessControlType.Allow));
        info.SetAccessControl(security);

        var ex = Assert.ThrowsExactly<GpoStoreException>(() => new GpoStore(policy).Save(GpoSide.Machine, SetAlpha));

        Assert.AreEqual(GpoStoreFailure.PolicyDenied, ex.Reason);
        StringAssert.Contains(ex.Message, policy.BackupRoot);
        Assert.IsFalse(File.Exists(policy.LockPath), "no lock file is created in a folder another trustee can write");
        Assert.IsFalse(Directory.Exists(Path.Combine(fx.RunRoot, "live")));
    }
}

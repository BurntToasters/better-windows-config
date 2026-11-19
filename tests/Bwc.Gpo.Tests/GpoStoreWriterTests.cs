using Bwc.Gpo.Refresh;
using Bwc.Gpo.Store;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Gpo.Tests;

/// <summary>A policy that denies every write and counts how often it was asked (the deny-all of the E2E flavor).</summary>
internal sealed class DenyAllPolicy(string root, string backupRoot) : IGpoStorePolicy
{
    public int Asked { get; private set; }

    public string GpoRoot => root;

    public string BackupRoot => backupRoot;

    public string LockPath => Path.Combine(backupRoot, "write.lock");

    public IPolicyRefresher Refresher => throw new InvalidOperationException("never refreshes");

    public void EnsureWriteAllowed(string path)
    {
        Asked++;
        throw new GpoStoreException(GpoStoreFailure.PolicyDenied, "Write", path, 0, "deny all");
    }

    public void EnsureRemoveAllowed(string path)
    {
        Asked++;
        throw new GpoStoreException(GpoStoreFailure.PolicyDenied, "Remove", path, 0, "deny all");
    }
}

[TestClass]
public sealed class GpoStoreWriterTests
{
    [TestMethod]
    public void WriteAtomic_CreatesFoldersAndFile_ThenReplaces()
    {
        using var fx = new SandboxGpoFixture();
        var path = fx.PolPath(GpoSide.Machine);

        fx.Seeder.WriteAtomic(path, [1, 2, 3]);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, fx.Read(path));
        fx.Seeder.WriteAtomic(path, [9]);
        CollectionAssert.AreEqual(new byte[] { 9 }, fx.Read(path));
        Assert.AreEqual(0, fx.LeftoverTempFiles().Length);
    }

    [TestMethod]
    public void EveryPublicWriteMethod_AsksThePolicyFirst_AndNothingHappensWhenItDenies()
    {
        using var fx = new SandboxGpoFixture();
        fx.PutRawFile(Path.Combine(fx.BackupRoot, "20000101-000000-001", "x.bin"), [1]);
        fx.PutRawFile(fx.GptPath, [1]);
        var deny = new DenyAllPolicy(fx.GpoRoot, fx.BackupRoot);
        var writer = new GpoFileWriter(deny);
        var target = fx.PolPath(GpoSide.Machine);

        Assert.AreEqual(GpoStoreFailure.PolicyDenied, Assert.ThrowsExactly<GpoStoreException>(() => writer.WriteAtomic(target, [1])).Reason);
        Assert.AreEqual(GpoStoreFailure.PolicyDenied, Assert.ThrowsExactly<GpoStoreException>(() => writer.AppendLine(target, "x")).Reason);
        Assert.AreEqual(GpoStoreFailure.PolicyDenied, Assert.ThrowsExactly<GpoStoreException>(() => writer.BackupFiles([fx.GptPath])).Reason);
        Assert.AreEqual(GpoStoreFailure.PolicyDenied, Assert.ThrowsExactly<GpoStoreException>(() => writer.PruneBackups(0)).Reason);

        Assert.AreEqual(4, deny.Asked);
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(target)), "no folder was created");
        Assert.IsTrue(Directory.Exists(Path.Combine(fx.BackupRoot, "20000101-000000-001")), "nothing was pruned");
        Assert.AreEqual(1, fx.BackupFolders().Length);
    }

    [TestMethod]
    public void WriteAtomic_ToAnExistingFolderPath_IsDenied()
    {
        using var fx = new SandboxGpoFixture();
        fx.MakeFolder(fx.PolPath(GpoSide.Machine));
        var ex = Assert.ThrowsExactly<GpoStoreException>(() => fx.Seeder.WriteAtomic(fx.PolPath(GpoSide.Machine), [1]));
        Assert.AreEqual(GpoStoreFailure.PolicyDenied, ex.Reason);
    }

    [TestMethod]
    public void WriteAtomic_ToAnAlternateDataStream_IsDenied()
    {
        using var fx = new SandboxGpoFixture();
        var ex = Assert.ThrowsExactly<GpoStoreException>(() => fx.Seeder.WriteAtomic(fx.PolPath(GpoSide.Machine) + ":hidden", [1]));
        Assert.AreEqual(GpoStoreFailure.PolicyDenied, ex.Reason);
    }

    [TestMethod]
    public void WriteAtomic_ReadOnlyTarget_IsRefused_AndTheAttributeIsNeverCleared()
    {
        using var fx = new SandboxGpoFixture();
        var path = fx.PolPath(GpoSide.Machine);
        fx.Seed(path, [1]);
        fx.SetAttributes(path, FileAttributes.ReadOnly);

        var ex = Assert.ThrowsExactly<GpoStoreException>(() => fx.Seeder.WriteAtomic(path, [2]));

        Assert.AreEqual(GpoStoreFailure.ReadOnlyTarget, ex.Reason);
        Assert.IsTrue(fx.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
        CollectionAssert.AreEqual(new byte[] { 1 }, fx.Read(path));
        Assert.AreEqual(0, fx.LeftoverTempFiles().Length);
    }

    [TestMethod]
    public void EnsureWritable_ReportsReadOnlyWithoutWriting()
    {
        using var fx = new SandboxGpoFixture();
        var path = fx.PolPath(GpoSide.Machine);
        fx.Seed(path, [1]);
        fx.Seeder.EnsureWritable(path);
        fx.Seeder.EnsureWritable(fx.PolPath(GpoSide.User));

        fx.SetAttributes(path, FileAttributes.ReadOnly);
        Assert.AreEqual(GpoStoreFailure.ReadOnlyTarget, Assert.ThrowsExactly<GpoStoreException>(() => fx.Seeder.EnsureWritable(path)).Reason);
    }

    [TestMethod]
    public void WriteAtomic_KeepsHiddenAndSystemAttributes()
    {
        using var fx = new SandboxGpoFixture();
        var path = fx.GptPath;
        fx.Seed(path, [1]);
        fx.SetAttributes(path, FileAttributes.Hidden | FileAttributes.System);

        fx.Seeder.WriteAtomic(path, [2]);

        var attributes = fx.GetAttributes(path);
        Assert.IsTrue(attributes.HasFlag(FileAttributes.Hidden));
        Assert.IsTrue(attributes.HasFlag(FileAttributes.System));
        Assert.IsFalse(attributes.HasFlag(FileAttributes.ReadOnly));
    }

    [TestMethod]
    public void WriteAtomic_KeepsAnExplicitAclOnTheExistingFile()
    {
        using var fx = new SandboxGpoFixture();
        var path = fx.GptPath;
        fx.Seed(path, [1]);
        var sid = fx.AddExplicitRule(path);
        Assert.IsTrue(fx.HasExplicitRule(path, sid));

        fx.Seeder.WriteAtomic(path, [2]);

        Assert.IsTrue(fx.HasExplicitRule(path, sid), "the replaced file's DACL carries over");
        CollectionAssert.AreEqual(new byte[] { 2 }, fx.Read(path));
    }

    [TestMethod]
    public void WriteAtomic_FileInUse_RetriesThenFailsAsInUse()
    {
        using var fx = new SandboxGpoFixture();
        var path = fx.PolPath(GpoSide.Machine);
        fx.Seed(path, [1]);
        fx.HoldOpenWithoutDelete(path);
        var writer = new GpoFileWriter(fx.Policy) { RetryCount = 3, RetryDelay = TimeSpan.FromMilliseconds(1) };

        var ex = Assert.ThrowsExactly<GpoStoreException>(() => writer.WriteAtomic(path, [2]));

        Assert.AreEqual(GpoStoreFailure.InUse, ex.Reason);
        Assert.AreEqual("Replace", ex.Operation);
        fx.ReleaseHolds();
        CollectionAssert.AreEqual(new byte[] { 1 }, fx.Read(path));
        Assert.AreEqual(0, fx.LeftoverTempFiles().Length);
    }

    [TestMethod]
    public void WriteAtomic_TellsTheObserverOnlyAfterTheFileIsInPlace()
    {
        using var fx = new SandboxGpoFixture();
        var seen = new List<byte[]?>();
        var writer = new GpoFileWriter(fx.Policy, e => seen.Add(fx.Read(e.Path)));

        writer.WriteAtomic(fx.GptPath, [4, 5]);

        Assert.AreEqual(1, seen.Count);
        CollectionAssert.AreEqual(new byte[] { 4, 5 }, seen[0]);
    }

    [TestMethod]
    public void AppendLine_AppendsOneLinePerCall_AndCreatesTheFile()
    {
        using var fx = new SandboxGpoFixture();
        var path = Path.Combine(fx.GpoRoot, "log.txt");

        fx.Seeder.AppendLine(path, "one");
        fx.Seeder.AppendLine(path, "two");

        Assert.AreEqual("one\r\ntwo\r\n", System.Text.Encoding.UTF8.GetString(fx.Read(path)!));
    }

    [TestMethod]
    public void BackupFiles_CopiesExistingFiles_MarksAbsentOnes_AndNeverReusesAFolder()
    {
        using var fx = new SandboxGpoFixture();
        fx.Seed(fx.GptPath, [1, 2]);
        var files = GpoFileNames.AllFiles(fx.GpoRoot);

        var first = fx.Seeder.BackupFiles(files);
        var second = fx.Seeder.BackupFiles(files);

        Assert.AreNotEqual(first, second);
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, File.ReadAllBytes(Path.Combine(first, GpoFileNames.GptIniFile)));
        Assert.IsTrue(File.Exists(Path.Combine(first, GpoFileNames.MachineFolder, GpoFileNames.RegistryPol + ".absent")));
        Assert.IsFalse(File.Exists(Path.Combine(first, GpoFileNames.GptIniFile + ".absent")));
        Assert.AreEqual(2, fx.BackupFolders().Length);
    }

    [TestMethod]
    public void BackupFiles_RefusesAFileOutsideTheGpoRoot()
    {
        using var fx = new SandboxGpoFixture();
        var outside = Path.Combine(fx.RunRoot, "other.txt");
        Assert.ThrowsExactly<GpoStoreException>(() => fx.Seeder.BackupFiles([outside]));
        Assert.AreEqual(0, fx.BackupFolders().Length);
    }

    [TestMethod]
    public void PruneBackups_OrdersSuffixesNumerically_AndIgnoresForeignNames()
    {
        using var fx = new SandboxGpoFixture();
        var names = new List<string> { "20240101-000000-000" };
        for (var i = 1; i <= 11; i++)
        {
            names.Add("20240101-000000-000-" + i);
        }

        names.Add("20240101-000001-000");
        foreach (var name in names.Concat(["notes", "2024-01-01", "20240101-000000-000x"]))
        {
            fx.PutRawFile(Path.Combine(fx.BackupRoot, name, "f"), [1]);
        }

        var failure = fx.Seeder.PruneBackups(10);

        Assert.IsNull(failure);
        var remaining = fx.BackupFolders();
        CollectionAssert.DoesNotContain(remaining, "20240101-000000-000");
        CollectionAssert.DoesNotContain(remaining, "20240101-000000-000-1");
        CollectionAssert.DoesNotContain(remaining, "20240101-000000-000-2");
        CollectionAssert.Contains(remaining, "20240101-000000-000-3");
        CollectionAssert.Contains(remaining, "20240101-000000-000-10");
        CollectionAssert.Contains(remaining, "20240101-000000-000-11");
        CollectionAssert.Contains(remaining, "20240101-000001-000");
        CollectionAssert.Contains(remaining, "notes");
        CollectionAssert.Contains(remaining, "2024-01-01");
        CollectionAssert.Contains(remaining, "20240101-000000-000x");
        Assert.AreEqual(13, remaining.Length);
    }

    [TestMethod]
    public void PruneBackups_OfAJunction_RemovesTheLinkAndNeverTheTarget()
    {
        using var fx = new SandboxGpoFixture();
        var target = Path.Combine(fx.RunRoot, "precious");
        fx.PutRawFile(Path.Combine(target, "keep.txt"), [1]);
        fx.MakeFolder(fx.BackupRoot);
        var link = Path.Combine(fx.BackupRoot, "20000101-000000-000");
        if (!fx.TryMakeJunction(link, target))
        {
            Assert.Inconclusive("mklink /J is not available here.");
        }

        fx.PutRawFile(Path.Combine(fx.BackupRoot, "20000101-000000-001", "f"), [1]);
        var failure = fx.Seeder.PruneBackups(1);

        Assert.IsNull(failure);
        Assert.IsFalse(Directory.Exists(link));
        Assert.IsTrue(File.Exists(Path.Combine(target, "keep.txt")), "the junction target is untouched");
    }
}

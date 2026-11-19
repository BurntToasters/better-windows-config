using Bwc.Gpo.Pol;
using Bwc.Gpo.Store;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Gpo.Tests;

/// <summary>The save sequence in a temp sandbox. Never touches the real GPO folder or the real refresher.</summary>
[TestClass]
public sealed class GpoSaveTests
{
    private const string Key = "Software\\Policies\\BWC-Test";
    private const string Namespace = "BWC.Policies.Test";
    private static readonly string WroteMachineCmtx = "write:Machine/" + GpoFileNames.CommentCmtx;
    private static readonly string WroteMachinePol = "write:Machine/" + GpoFileNames.RegistryPol;
    private static readonly string WroteUserPol = "write:User/" + GpoFileNames.RegistryPol;
    private static readonly string WroteGpt = "write:" + GpoFileNames.GptIniFile;

    private static void SetValue(GpoEditSession session, string name, uint value) =>
        GpoStorePolHelper.SetDword(session.Pol, Key, name, value);

    private static void SetAlpha(GpoEditSession session) => SetValue(session, "Alpha", 1);

    private static byte[] PolBytesWith(string name, uint value)
    {
        var doc = PolDocument.Create();
        GpoStorePolHelper.SetDword(doc, Key, name, value);
        return doc.ToBytes();
    }

    [TestMethod]
    public void Save_WritesCmtxThenPolThenGptThenRefreshes()
    {
        using var fx = new SandboxGpoFixture();
        var store = fx.NewStore();

        var result = store.Save(GpoSide.Machine, s =>
        {
            SetAlpha(s);
            s.Comments.SetComment(Namespace, "P", "note");
        });

        CollectionAssert.AreEqual(
            new[] { WroteMachineCmtx, WroteMachinePol, WroteGpt, "refresh:Machine" },
            fx.Log);
        Assert.IsTrue(result.Wrote);
        Assert.IsTrue(result.Refreshed);
        Assert.IsNull(result.RefreshFailure);
        Assert.AreEqual(3, result.WrittenFiles.Count);
        Assert.AreEqual((uint?)1u, result.GptVersion);
        Assert.AreEqual(
            "[General]\r\ngPCMachineExtensionNames=[" + GptIniProbe.RegistryCse + GptIniProbe.MachineTool + "]\r\nVersion=1\r\n",
            GptIniProbe.Latin1(fx.Read(fx.GptPath)!));
        Assert.IsNotNull(PolDocument.Parse(fx.Read(fx.PolPath(GpoSide.Machine))!).GetValue(Key, "Alpha"));
        Assert.AreEqual(0, fx.LeftoverTempFiles().Length);
    }

    [TestMethod]
    public void Save_WithNoChange_WritesNothingBacksUpNothingAndDoesNotRefresh()
    {
        using var fx = new SandboxGpoFixture();
        var store = fx.NewStore();

        var result = store.Save(GpoSide.Machine, _ => { });

        Assert.IsFalse(result.Wrote);
        Assert.IsFalse(result.Refreshed);
        Assert.AreEqual(0, result.WrittenFiles.Count);
        Assert.IsNull(result.BackupFolder);
        Assert.AreEqual(0, fx.Log.Count);
        Assert.AreEqual(0, fx.Events.Count);
        Assert.IsFalse(Directory.Exists(fx.GpoRoot), "not even the folder is created");
        Assert.AreEqual(0, fx.BackupFolders().Length, "no backup folder (the write lock file may exist)");
    }

    [TestMethod]
    public void Save_WithAnEditThatIsUndone_IsNoChange()
    {
        using var fx = new SandboxGpoFixture();
        fx.Seed(fx.PolPath(GpoSide.Machine), PolBytesWith("Beta", 2));
        fx.Seed(fx.GptPath, GptIniProbe.Bytes());
        fx.Events.Clear();
        fx.Log.Clear();

        var result = fx.NewStore().Save(GpoSide.Machine, s =>
        {
            SetAlpha(s);
            s.Pol.RemoveValue(Key, "Alpha");
            SetValue(s, "Beta", 2);
        });

        Assert.IsFalse(result.Wrote);
        Assert.AreEqual(0, fx.Events.Count);
        CollectionAssert.AreEqual(GptIniProbe.Bytes(), fx.Read(fx.GptPath));
    }

    [TestMethod]
    public void Save_ReReadsUnderTheLock_AndKeepsAnOutsideEdit()
    {
        using var fx = new SandboxGpoFixture();
        var store = fx.NewStore();
        fx.Seed(fx.PolPath(GpoSide.Machine), PolBytesWith("Gamma", 3));
        var snapshot = store.Open();
        Assert.IsNotNull(snapshot.Machine.Pol!.GetValue(Key, "Gamma"));

        fx.Seed(fx.PolPath(GpoSide.Machine), PolBytesWith("Beta", 2));
        store.Save(GpoSide.Machine, SetAlpha);

        var final = PolDocument.Parse(fx.Read(fx.PolPath(GpoSide.Machine))!);
        Assert.IsNotNull(final.GetValue(Key, "Alpha"));
        Assert.IsNotNull(final.GetValue(Key, "Beta"), "the outside edit survives");
        Assert.IsNull(final.GetValue(Key, "Gamma"), "the stale snapshot is never written back");
    }

    [TestMethod]
    public void Save_UserSide_BumpsHighHalf_AndTouchesNoMachineFile()
    {
        using var fx = new SandboxGpoFixture();
        fx.Seed(fx.GptPath, GptIniProbe.Bytes());
        fx.Log.Clear();

        fx.NewStore().Save(GpoSide.User, SetAlpha);

        CollectionAssert.AreEqual(new[] { WroteUserPol, WroteGpt, "refresh:User" }, fx.Log);
        CollectionAssert.AreEqual(GptIniProbe.Bytes(720908 + 65536), fx.Read(fx.GptPath));
        Assert.IsFalse(Directory.Exists(Path.Combine(fx.GpoRoot, GpoFileNames.MachineFolder)));
    }

    [TestMethod]
    public void Save_MachineSide_BumpsLowHalf_FromAnExistingFile()
    {
        using var fx = new SandboxGpoFixture();
        fx.Seed(fx.GptPath, GptIniProbe.Bytes());

        fx.NewStore().Save(GpoSide.Machine, SetAlpha);

        CollectionAssert.AreEqual(GptIniProbe.Bytes(720909), fx.Read(fx.GptPath));
    }

    [TestMethod]
    public void Save_APolicyOfClassBoth_IsOneSavePerRoot_EachBumpingItsOwnHalf()
    {
        using var fx = new SandboxGpoFixture();
        var store = fx.NewStore();

        store.Save(GpoSide.Machine, SetAlpha);
        store.Save(GpoSide.User, SetAlpha);

        Assert.AreEqual(GptIniProbe.Text(65537), GptIniProbe.Latin1(fx.Read(fx.GptPath)!));
    }

    [TestMethod]
    public void Save_CommentOnly_WritesOnlyCmtx_NoVersionBump_NoRefresh()
    {
        using var fx = new SandboxGpoFixture();
        fx.Seed(fx.GptPath, GptIniProbe.Bytes());
        fx.Log.Clear();

        var result = fx.NewStore().Save(GpoSide.Machine, s => s.Comments.SetComment(Namespace, "P", "note"));

        CollectionAssert.AreEqual(new[] { WroteMachineCmtx }, fx.Log);
        CollectionAssert.AreEqual(GptIniProbe.Bytes(), fx.Read(fx.GptPath));
        Assert.IsTrue(result.Wrote);
        Assert.IsFalse(result.Refreshed);
        Assert.IsFalse(File.Exists(fx.PolPath(GpoSide.Machine)));
    }

    [TestMethod]
    public void Save_KeepsUnknownGptIniContent()
    {
        using var fx = new SandboxGpoFixture();
        var original = "[General]\r\nVersion=4\r\n; mine\r\n[Extra]\r\nk=v\r\n";
        fx.Seed(fx.GptPath, GptIniProbe.Latin1(original));

        fx.NewStore().Save(GpoSide.Machine, SetAlpha);

        var text = GptIniProbe.Latin1(fx.Read(fx.GptPath)!);
        StringAssert.Contains(text, "Version=5\r\n; mine\r\n[Extra]\r\nk=v\r\n");
        StringAssert.Contains(text, "gPCMachineExtensionNames=[");
    }

    [TestMethod]
    public void Save_ReplacesAtomically_LeavesNoTempFile_AndSweepsAStaleOne()
    {
        using var fx = new SandboxGpoFixture();
        var stale = fx.PolPath(GpoSide.Machine) + ".bwc-dead.tmp";
        fx.PutRawFile(stale, [1, 2, 3]);

        var store = fx.NewStore();
        store.Save(GpoSide.Machine, SetAlpha);
        store.Save(GpoSide.Machine, s => SetValue(s, "Alpha", 5));

        Assert.AreEqual(0, fx.LeftoverTempFiles().Length);
        Assert.IsFalse(File.Exists(stale));
    }

    [TestMethod]
    public void Save_BackupIsMadeOncePerSession_WithAbsentMarkers()
    {
        using var fx = new SandboxGpoFixture();
        var originalPol = PolBytesWith("Beta", 2);
        fx.Seed(fx.PolPath(GpoSide.Machine), originalPol);
        fx.Seed(fx.GptPath, GptIniProbe.Bytes());
        var store = fx.NewStore();

        var first = store.Save(GpoSide.Machine, SetAlpha);
        var second = store.Save(GpoSide.Machine, s => SetValue(s, "Alpha", 9));

        Assert.IsNotNull(first.BackupFolder);
        Assert.IsNull(second.BackupFolder, "later saves in the session make no backup");
        Assert.AreEqual(1, fx.BackupFolders().Length);
        var folder = first.BackupFolder!;
        CollectionAssert.AreEqual(originalPol, File.ReadAllBytes(Path.Combine(folder, GpoFileNames.MachineFolder, GpoFileNames.RegistryPol)));
        CollectionAssert.AreEqual(GptIniProbe.Bytes(), File.ReadAllBytes(Path.Combine(folder, GpoFileNames.GptIniFile)));
        Assert.IsTrue(File.Exists(Path.Combine(folder, GpoFileNames.MachineFolder, GpoFileNames.CommentCmtx + ".absent")));
        Assert.IsTrue(File.Exists(Path.Combine(folder, GpoFileNames.UserFolder, GpoFileNames.RegistryPol + ".absent")));
        Assert.IsTrue(File.Exists(Path.Combine(folder, GpoFileNames.UserFolder, GpoFileNames.CommentCmtx + ".absent")));
        Assert.IsFalse(File.Exists(Path.Combine(folder, GpoFileNames.MachineFolder, GpoFileNames.RegistryPol + ".absent")));
    }

    [TestMethod]
    public void Save_BackupOfAnEmptyGpoFolder_MarksAllFiveFilesAbsent()
    {
        using var fx = new SandboxGpoFixture();
        var result = fx.NewStore().Save(GpoSide.Machine, SetAlpha);
        var markers = Directory.GetFiles(result.BackupFolder!, "*.absent", SearchOption.AllDirectories);
        Assert.AreEqual(5, markers.Length);
    }

    [TestMethod]
    public void Save_BackupIsPrunedToTheLastTen_AndForeignFoldersStay()
    {
        using var fx = new SandboxGpoFixture();
        fx.MakeFolder(Path.Combine(fx.BackupRoot, "keep-me"));
        var folders = new List<string>();

        for (var i = 0; i < 12; i++)
        {
            var value = (uint)i;
            var result = fx.NewStore().Save(GpoSide.Machine, s => SetValue(s, "Alpha", value + 100));
            folders.Add(result.BackupFolder!);
        }

        Assert.AreEqual(12, folders.Distinct().Count());
        for (var i = 0; i < 2; i++)
        {
            Assert.IsFalse(Directory.Exists(folders[i]), "the two oldest are removed");
        }

        for (var i = 2; i < 12; i++)
        {
            Assert.IsTrue(Directory.Exists(folders[i]), "the newest ten stay");
        }

        Assert.IsTrue(Directory.Exists(Path.Combine(fx.BackupRoot, "keep-me")));
        Assert.AreEqual(11, fx.BackupFolders().Length);
    }

    [TestMethod]
    public void Save_PruneFailure_IsReported_NotThrown_AndTheSaveCompletes()
    {
        using var fx = new SandboxGpoFixture();
        for (var i = 1; i <= 11; i++)
        {
            var name = $"20000101-000000-{i:000}";
            fx.PutRawFile(Path.Combine(fx.BackupRoot, name, "x.bin"), [1]);
        }

        fx.HoldOpenWithoutDelete(Path.Combine(fx.BackupRoot, "20000101-000000-001", "x.bin"));

        var result = fx.NewStore().Save(GpoSide.Machine, SetAlpha);

        Assert.IsTrue(result.Wrote);
        Assert.IsNotNull(result.PruneFailure);
        Assert.AreEqual(GpoStoreFailure.Io, result.PruneFailure.Reason);
        Assert.IsFalse(Directory.Exists(Path.Combine(fx.BackupRoot, "20000101-000000-002")), "the next one is still pruned");
        Assert.IsNotNull(PolDocument.Parse(fx.Read(fx.PolPath(GpoSide.Machine))!).GetValue(Key, "Alpha"));
    }

    [TestMethod]
    public void Save_BackupFailure_StopsBeforeAnyGpoFileIsWritten()
    {
        using var fx = new SandboxGpoFixture();
        fx.Seed(fx.PolPath(GpoSide.User), PolBytesWith("Beta", 2));
        fx.HoldExclusive(fx.PolPath(GpoSide.User));
        fx.Log.Clear();
        var store = fx.NewStore();

        var ex = Assert.ThrowsExactly<GpoStoreException>(() => store.Save(GpoSide.Machine, SetAlpha));

        Assert.AreEqual(GpoStoreFailure.BackupFailed, ex.Reason);
        Assert.AreEqual(0, ex.WrittenFiles.Count);
        Assert.AreEqual(0, fx.Log.Count);
        Assert.IsFalse(File.Exists(fx.PolPath(GpoSide.Machine)));
        Assert.AreEqual(0, fx.Refresher.Calls.Count);
    }

    [TestMethod]
    public void Save_ReadOnlyTarget_IsRefusedBeforeAnyFileIsWritten()
    {
        using var fx = new SandboxGpoFixture();
        fx.Seed(fx.PolPath(GpoSide.Machine), PolBytesWith("Beta", 2));
        fx.Seed(fx.GptPath, GptIniProbe.Bytes());
        var polBefore = fx.Read(fx.PolPath(GpoSide.Machine));
        fx.SetAttributes(fx.PolPath(GpoSide.Machine), FileAttributes.ReadOnly);
        fx.Log.Clear();

        var ex = Assert.ThrowsExactly<GpoStoreException>(() => fx.NewStore().Save(GpoSide.Machine, s =>
        {
            SetAlpha(s);
            s.Comments.SetComment(Namespace, "P", "note");
        }));

        Assert.AreEqual(GpoStoreFailure.ReadOnlyTarget, ex.Reason);
        Assert.AreEqual(fx.PolPath(GpoSide.Machine), ex.Path);
        Assert.AreEqual(0, fx.Log.Count, "the comment file was not written either");
        Assert.IsFalse(File.Exists(fx.CmtxPath(GpoSide.Machine)));
        CollectionAssert.AreEqual(polBefore, fx.Read(fx.PolPath(GpoSide.Machine)));
        CollectionAssert.AreEqual(GptIniProbe.Bytes(), fx.Read(fx.GptPath));
        Assert.AreEqual(0, fx.LeftoverTempFiles().Length);
    }

    [TestMethod]
    public void Save_KeepsTheHiddenAttribute_OfAnExistingFile()
    {
        using var fx = new SandboxGpoFixture();
        fx.Seed(fx.PolPath(GpoSide.Machine), PolBytesWith("Beta", 2));
        fx.SetAttributes(fx.PolPath(GpoSide.Machine), FileAttributes.Hidden | FileAttributes.Archive);

        fx.NewStore().Save(GpoSide.Machine, SetAlpha);

        Assert.IsTrue(fx.GetAttributes(fx.PolPath(GpoSide.Machine)).HasFlag(FileAttributes.Hidden));
    }

    [TestMethod]
    public void Save_FileInUse_IsATypedFailure_TargetUntouched_NoTempLeft_GptNotBumped()
    {
        using var fx = new SandboxGpoFixture();
        fx.Seed(fx.PolPath(GpoSide.Machine), PolBytesWith("Beta", 2));
        fx.Seed(fx.GptPath, GptIniProbe.Bytes());
        var before = fx.Read(fx.PolPath(GpoSide.Machine));
        fx.HoldOpenWithoutDelete(fx.PolPath(GpoSide.Machine));
        fx.Log.Clear();

        var ex = Assert.ThrowsExactly<GpoStoreException>(() => fx.NewStore().Save(GpoSide.Machine, SetAlpha));

        Assert.AreEqual(GpoStoreFailure.InUse, ex.Reason);
        Assert.AreEqual(fx.PolPath(GpoSide.Machine), ex.Path);
        Assert.AreNotEqual(0, ex.Win32Error);
        Assert.AreEqual(0, fx.Log.Count);
        fx.ReleaseHolds();
        CollectionAssert.AreEqual(before, fx.Read(fx.PolPath(GpoSide.Machine)));
        CollectionAssert.AreEqual(GptIniProbe.Bytes(), fx.Read(fx.GptPath));
        Assert.AreEqual(0, fx.LeftoverTempFiles().Length);
        Assert.AreEqual(0, fx.Refresher.Calls.Count);
    }

    [TestMethod]
    public void Save_RefreshFailure_IsReported_NotThrown_AndFilesStayWritten()
    {
        using var fx = new SandboxGpoFixture();
        fx.Refresher.Failure = new InvalidOperationException("refresh refused");

        var result = fx.NewStore().Save(GpoSide.Machine, SetAlpha);

        Assert.IsTrue(result.Wrote);
        Assert.IsFalse(result.Refreshed);
        Assert.IsNotNull(result.RefreshFailure);
        Assert.AreEqual(GpoStoreFailure.RefreshFailed, result.RefreshFailure.Reason);
        Assert.IsTrue(File.Exists(fx.PolPath(GpoSide.Machine)));
        Assert.IsTrue(File.Exists(fx.GptPath));
    }

    [TestMethod]
    public void Save_InReadOnlyMode_ThrowsBeforeTheEditRunsAndBeforeAnyFileOperation()
    {
        using var fx = new SandboxGpoFixture();
        var store = fx.NewStore(o => o.Access = new GpoAccessResult([GpoReadOnlyReason.NotElevated]));
        var ran = false;

        var ex = Assert.ThrowsExactly<GpoStoreException>(() => store.Save(GpoSide.Machine, _ => ran = true));

        Assert.AreEqual(GpoStoreFailure.ReadOnlyMode, ex.Reason);
        Assert.IsFalse(ran);
        Assert.IsFalse(Directory.Exists(fx.GpoRoot));
        Assert.IsFalse(Directory.Exists(fx.BackupRoot));
        Assert.AreEqual(0, fx.Events.Count);
    }

    [TestMethod]
    public void Save_EditThatThrows_WritesNothing_AndReleasesTheLock()
    {
        using var fx = new SandboxGpoFixture();
        var store = fx.NewStore();

        Assert.ThrowsExactly<InvalidOperationException>(() => store.Save(GpoSide.Machine, s =>
        {
            SetAlpha(s);
            throw new InvalidOperationException("boom");
        }));

        Assert.AreEqual(0, fx.Log.Count);
        Assert.AreEqual(0, fx.BackupFolders().Length);
        var task = Task.Run(() => fx.NewStore(o => o.LockTimeout = TimeSpan.FromSeconds(2)).Save(GpoSide.Machine, SetAlpha));
        Assert.IsTrue(task.Wait(TimeSpan.FromSeconds(10)));
        Assert.IsTrue(task.Result.Wrote, "another thread can take the lock");
    }

    [TestMethod]
    public void Save_Cancelled_BeforeTheFirstWrite_ChangesNothing()
    {
        using var fx = new SandboxGpoFixture();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() => fx.NewStore().Save(GpoSide.Machine, SetAlpha, cts.Token));

        Assert.IsFalse(Directory.Exists(fx.GpoRoot));
        Assert.AreEqual(0, fx.Events.Count);
    }

    [TestMethod]
    public void Save_LockTimeout_IsATypedFailure_AndNothingIsRead()
    {
        using var fx = new SandboxGpoFixture();
        fx.HoldLockFile();
        var ran = false;

        var store = fx.NewStore(o => o.LockTimeout = TimeSpan.FromMilliseconds(200));
        var ex = Assert.ThrowsExactly<GpoStoreException>(() => store.Save(GpoSide.Machine, _ => ran = true));

        Assert.AreEqual(GpoStoreFailure.LockTimeout, ex.Reason);
        Assert.AreEqual(fx.Policy.LockPath, ex.Path);
        Assert.AreEqual(0, ex.WrittenFiles.Count);
        Assert.IsFalse(ran);
        Assert.AreEqual(0, fx.Events.Count);
    }

    [TestMethod]
    public void Save_WaitsForAnotherInstanceToFinish_ThenSucceeds()
    {
        using var fx = new SandboxGpoFixture();
        var holder = fx.HoldLockFile();
        var releaser = Task.Run(() =>
        {
            Thread.Sleep(400);
            holder.Dispose();
        });

        var started = DateTime.UtcNow;
        var result = fx.NewStore().Save(GpoSide.Machine, SetAlpha);
        var waited = DateTime.UtcNow - started;
        releaser.Wait();

        Assert.IsTrue(result.Wrote);
        Assert.IsTrue(waited >= TimeSpan.FromMilliseconds(250), $"waited only {waited}");
    }

    [TestMethod]
    public void Save_ALockFileLeftByADeadHolder_IsFree_AndTheStateIsReReadFromDisk()
    {
        using var fx = new SandboxGpoFixture();
        fx.HoldLockFile().Dispose();
        Assert.IsTrue(File.Exists(fx.Policy.LockPath));

        var result = fx.NewStore().Save(GpoSide.Machine, SetAlpha);

        Assert.IsTrue(result.Wrote);
    }

    [TestMethod]
    public void Save_FailureAtRegistryPol_AfterTheCommentFileWasWritten_NamesTheWrittenFile()
    {
        using var fx = new SandboxGpoFixture();
        fx.Seed(fx.PolPath(GpoSide.Machine), PolBytesWith("Beta", 2));
        fx.Seed(fx.GptPath, GptIniProbe.Bytes());
        var polBefore = fx.Read(fx.PolPath(GpoSide.Machine));
        fx.HoldOpenWithoutDelete(fx.PolPath(GpoSide.Machine));
        fx.Log.Clear();

        var ex = Assert.ThrowsExactly<GpoStoreException>(() => fx.NewStore().Save(GpoSide.Machine, s =>
        {
            SetAlpha(s);
            s.Comments.SetComment(Namespace, "P", "note");
        }));

        Assert.AreEqual(GpoStoreFailure.InUse, ex.Reason, "the reason the caller sees is unchanged");
        Assert.AreEqual(fx.PolPath(GpoSide.Machine), ex.Path);
        CollectionAssert.AreEqual(new[] { fx.CmtxPath(GpoSide.Machine) }, ex.WrittenFiles.ToArray());
        Assert.IsTrue(File.Exists(fx.CmtxPath(GpoSide.Machine)), "the comment file really is on disk");
        fx.ReleaseHolds();
        CollectionAssert.AreEqual(polBefore, fx.Read(fx.PolPath(GpoSide.Machine)));
        CollectionAssert.AreEqual(GptIniProbe.Bytes(), fx.Read(fx.GptPath));
        Assert.AreEqual(0, fx.Refresher.Calls.Count);
    }

    [TestMethod]
    public void Save_FailureAtGptIni_AfterTheOtherTwoFilesWereWritten_NamesBothInOrder()
    {
        using var fx = new SandboxGpoFixture();
        fx.Seed(fx.GptPath, GptIniProbe.Bytes());
        fx.HoldOpenWithoutDelete(fx.GptPath);

        var ex = Assert.ThrowsExactly<GpoStoreException>(() => fx.NewStore().Save(GpoSide.Machine, s =>
        {
            SetAlpha(s);
            s.Comments.SetComment(Namespace, "P", "note");
        }));

        Assert.AreEqual(GpoStoreFailure.InUse, ex.Reason);
        Assert.AreEqual(fx.GptPath, ex.Path);
        CollectionAssert.AreEqual(new[] { fx.CmtxPath(GpoSide.Machine), fx.PolPath(GpoSide.Machine) }, ex.WrittenFiles.ToArray());
    }

    [TestMethod]
    public void Save_FailureAtTheFirstWrite_HasNoWrittenFiles()
    {
        using var fx = new SandboxGpoFixture();
        fx.Seed(fx.PolPath(GpoSide.Machine), PolBytesWith("Beta", 2));
        fx.HoldOpenWithoutDelete(fx.PolPath(GpoSide.Machine));

        var ex = Assert.ThrowsExactly<GpoStoreException>(() => fx.NewStore().Save(GpoSide.Machine, SetAlpha));

        Assert.AreEqual(GpoStoreFailure.InUse, ex.Reason);
        Assert.IsNotNull(ex.WrittenFiles);
        Assert.AreEqual(0, ex.WrittenFiles.Count);
    }

    [TestMethod]
    public void Save_RefusalBeforeAnyWrite_HasNoWrittenFiles()
    {
        using var fx = new SandboxGpoFixture();
        fx.PutRawFile(fx.GptPath, GptIniProbe.Latin1("[General]\r\nVersion=abc\r\n"));

        var ex = Assert.ThrowsExactly<GpoStoreException>(() => fx.NewStore().Save(GpoSide.Machine, SetAlpha));

        Assert.AreEqual(GpoStoreFailure.Malformed, ex.Reason);
        Assert.AreEqual(0, ex.WrittenFiles.Count);
    }

    [TestMethod]
    public void Save_CommentFileNestedFarTooDeep_IsMalformed_OnlyWhenTheEditNeedsIt()
    {
        using var fx = new SandboxGpoFixture();
        var deep = CmtxSafetyTests.DeepFile(10_000);
        fx.PutRawFile(fx.CmtxPath(GpoSide.Machine), deep);
        var store = fx.NewStore();

        var result = store.Save(GpoSide.Machine, SetAlpha);
        Assert.IsTrue(result.Wrote, "a save that does not need the comment file leaves it alone");
        CollectionAssert.AreEqual(deep, fx.Read(fx.CmtxPath(GpoSide.Machine)));

        var ex = Assert.ThrowsExactly<GpoStoreException>(() => store.Save(GpoSide.Machine, s => s.Comments.SetComment(Namespace, "P", "x")));
        Assert.AreEqual(GpoStoreFailure.Malformed, ex.Reason);
        CollectionAssert.AreEqual(deep, fx.Read(fx.CmtxPath(GpoSide.Machine)));
    }

    [TestMethod]
    public void Save_MalformedPol_IsRefused_FileUntouched_ButACommentOnlySaveStillWorks()
    {
        using var fx = new SandboxGpoFixture();
        byte[] garbage = [1, 2, 3, 4, 5, 6, 7, 8, 9];
        fx.PutRawFile(fx.PolPath(GpoSide.Machine), garbage);
        var store = fx.NewStore();

        var ex = Assert.ThrowsExactly<GpoStoreException>(() => store.Save(GpoSide.Machine, SetAlpha));
        Assert.AreEqual(GpoStoreFailure.Malformed, ex.Reason);
        Assert.AreEqual(fx.PolPath(GpoSide.Machine), ex.Path);
        CollectionAssert.AreEqual(garbage, fx.Read(fx.PolPath(GpoSide.Machine)));
        Assert.AreEqual(0, fx.Log.Count);

        var result = store.Save(GpoSide.Machine, s => s.Comments.SetComment(Namespace, "P", "x"));
        Assert.IsTrue(result.Wrote);
        CollectionAssert.AreEqual(garbage, fx.Read(fx.PolPath(GpoSide.Machine)));
    }

    [TestMethod]
    public void Save_MalformedGptIni_IsRefusedBeforeAnyWriteOrBackup()
    {
        using var fx = new SandboxGpoFixture();
        fx.PutRawFile(fx.GptPath, GptIniProbe.Latin1("[General]\r\nVersion=abc\r\n"));

        var ex = Assert.ThrowsExactly<GpoStoreException>(() => fx.NewStore().Save(GpoSide.Machine, SetAlpha));

        Assert.AreEqual(GpoStoreFailure.Malformed, ex.Reason);
        Assert.AreEqual(fx.GptPath, ex.Path);
        Assert.AreEqual(0, fx.Log.Count);
        Assert.AreEqual(0, fx.BackupFolders().Length);
        Assert.IsFalse(File.Exists(fx.PolPath(GpoSide.Machine)));
    }

    [TestMethod]
    public void Save_MalformedCmtx_IsRefusedOnlyWhenTheEditNeedsIt()
    {
        using var fx = new SandboxGpoFixture();
        byte[] garbage = [0x3C, 0x78];
        fx.PutRawFile(fx.CmtxPath(GpoSide.Machine), garbage);
        var store = fx.NewStore();

        var result = store.Save(GpoSide.Machine, SetAlpha);
        Assert.IsTrue(result.Wrote);
        CollectionAssert.AreEqual(garbage, fx.Read(fx.CmtxPath(GpoSide.Machine)));

        var ex = Assert.ThrowsExactly<GpoStoreException>(() => store.Save(GpoSide.Machine, s => s.Comments.SetComment(Namespace, "P", "x")));
        Assert.AreEqual(GpoStoreFailure.Malformed, ex.Reason);
        CollectionAssert.AreEqual(garbage, fx.Read(fx.CmtxPath(GpoSide.Machine)));
    }

    [TestMethod]
    public void Save_CreatesMissingFolders()
    {
        using var fx = new SandboxGpoFixture();
        Assert.IsFalse(Directory.Exists(fx.GpoRoot));

        fx.NewStore().Save(GpoSide.User, SetAlpha);

        Assert.IsTrue(File.Exists(fx.PolPath(GpoSide.User)));
        Assert.IsTrue(File.Exists(fx.GptPath));
    }

    [TestMethod]
    public void Save_FileWrittenOutsideTheSandbox_IsImpossible_PolicyDeniesEveryPathOutsideTheRoots()
    {
        using var fx = new SandboxGpoFixture();
        var outside = Path.Combine(fx.RunRoot, "elsewhere", GpoFileNames.RegistryPol);
        var ex = Assert.ThrowsExactly<GpoStoreException>(() => fx.Seeder.WriteAtomic(outside, [1]));
        Assert.AreEqual(GpoStoreFailure.PolicyDenied, ex.Reason);
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(outside)));
    }
}

using Bwc.Gpo.Store;
using Bwc.Gpedit.Editor.Dialog;
using Bwc.Gpedit.Save;
using Bwc.Policy.Admx;
using Bwc.Policy.Model;

namespace Bwc.Policy.Tests.Golden;

/// <summary>The dialog save adapter keeps its model current after a partial write
/// without treating the edit as applied.</summary>
[TestClass]
public sealed class PolicySaveStoreTests
{
    private const string CommentFile = "Machine.comment.cmtx";
    private const string PolicyFile = "Machine.Registry.pol";

    [TestMethod]
    public void PartialCommentWrite_RefreshesSnapshot_PreservesFailure_AndCanRetryThePendingEdit()
    {
        using var sandbox = new GoldenSandbox();
        PolicyDefinition policy = Fixture.Load().Policy("Pol_Class_Machine");
        SeedEmptyPolicy(sandbox);
        GpoSnapshot initial = sandbox.Store.Open();
        var store = new PolicySaveStore(sandbox.Store, initial, readOnly: false);
        var target = new PolicyRef(policy, GpoSide.Machine);
        sandbox.FailNextWriteAfter(CommentFile, PolicyFile);

        GpoStoreException failure = Assert.Throws<GpoStoreException>(
            () => store.Apply(target, PolicyEdit.Disabled, "pending comment"));

        Assert.AreEqual(GpoStoreFailure.ReadOnlyTarget, failure.Reason);
        CollectionAssert.AreEqual(new[] { sandbox.PathOf(CommentFile) }, failure.WrittenFiles.ToArray());
        GpoSnapshot partial = store.Snapshot!;
        Assert.AreNotSame(initial, partial, "the store must publish a snapshot read after the partial write");
        Assert.AreEqual("pending comment", partial.Side(GpoSide.Machine).Comments?.GetComment(policy.Namespace, policy.Name));
        Assert.AreEqual(PolicyState.NotConfigured, PolicyStateReader.Read(policy, partial.Side(GpoSide.Machine).Pol!).State);
        Assert.AreEqual(0, sandbox.Refresher.Calls.Count, "a partial write does not refresh policy");

        sandbox.AllowWrite(PolicyFile);
        PolicyApplyOutcome retry = store.Apply(target, PolicyEdit.Disabled, "pending comment");

        Assert.IsTrue(retry.IsApplied);
        Assert.IsTrue(retry.Changed);
        Assert.AreEqual(PolicyState.Disabled, PolicyStateReader.Read(policy, store.Snapshot!.Side(GpoSide.Machine).Pol!).State);
        Assert.AreEqual("pending comment", store.Snapshot.Side(GpoSide.Machine).Comments?.GetComment(policy.Namespace, policy.Name));
        Assert.AreEqual(1, sandbox.Refresher.Calls.Count);
    }

    [TestMethod]
    public void PartialPolicyWriteBeforeGptFailure_RefreshesSnapshotFromDisk()
    {
        using var sandbox = new GoldenSandbox();
        PolicyDefinition policy = Fixture.Load().Policy("Pol_Class_Machine");
        SeedEmptyPolicy(sandbox);
        GpoSnapshot initial = sandbox.Store.Open();
        var store = new PolicySaveStore(sandbox.Store, initial, readOnly: false);
        var target = new PolicyRef(policy, GpoSide.Machine);
        sandbox.FailNextWriteAfter(PolicyFile, GpoFileNames.GptIniFile);

        GpoStoreException failure = Assert.Throws<GpoStoreException>(
            () => store.Apply(target, PolicyEdit.Disabled, "pending comment"));

        Assert.AreEqual(GpoStoreFailure.ReadOnlyTarget, failure.Reason);
        CollectionAssert.AreEqual(new[] { sandbox.PathOf(CommentFile), sandbox.PathOf(PolicyFile) }, failure.WrittenFiles.ToArray());
        GpoSnapshot partial = store.Snapshot!;
        Assert.AreNotSame(initial, partial, "the snapshot must include the policy and comment that reached disk");
        Assert.AreEqual(PolicyState.Disabled, PolicyStateReader.Read(policy, partial.Side(GpoSide.Machine).Pol!).State);
        Assert.AreEqual("pending comment", partial.Side(GpoSide.Machine).Comments?.GetComment(policy.Namespace, policy.Name));
        Assert.AreEqual(initial.GptIni?.Version, partial.GptIni?.Version, "gpt.ini was the refused write and remains as read before it");
        Assert.AreEqual(0, sandbox.Refresher.Calls.Count);

        sandbox.AllowWrite(GpoFileNames.GptIniFile);
        PolicyApplyOutcome retry = store.Apply(target, PolicyEdit.Disabled, "pending comment");
        GpoSnapshot recovered = store.Snapshot!;
        Assert.IsTrue(retry.IsApplied);
        Assert.IsTrue(retry.Changed, "recovery writes the metadata even though the edit is now a byte-level no-op");
        Assert.AreEqual((initial.GptIni?.Version ?? 0) + 1, recovered.GptIni?.Version);
        Assert.AreEqual(1, sandbox.Refresher.Calls.Count);

        PolicyApplyOutcome repeat = store.Apply(target, PolicyEdit.Disabled, "pending comment");
        Assert.IsFalse(repeat.Changed);
        Assert.AreEqual(recovered.GptIni?.Version, store.Snapshot?.GptIni?.Version, "a repeated retry does not bump twice");
        Assert.AreEqual(1, sandbox.Refresher.Calls.Count, "a completed recovery does not refresh again");
    }

    [TestMethod]
    public void CommentOnlyPartialWrite_RetryCreatesMissingPolicyAndCompletesMetadataAndRefresh()
    {
        using var sandbox = new GoldenSandbox();
        PolicyDefinition policy = Fixture.Load().Policy("Pol_Class_Machine");
        sandbox.Seed(sandbox.GptPath, GptIni.CreateNew().ToBytes());
        GpoSnapshot initial = sandbox.Store.Open();
        var store = new PolicySaveStore(sandbox.Store, initial, readOnly: false);
        var target = new PolicyRef(policy, GpoSide.Machine);
        sandbox.FailNextWriteAfter(CommentFile, PolicyFile);

        GpoStoreException failure = Assert.Throws<GpoStoreException>(
            () => store.Apply(target, null, "comment only"));

        Assert.AreEqual(GpoStoreFailure.ReadOnlyTarget, failure.Reason);
        CollectionAssert.AreEqual(new[] { sandbox.PathOf(CommentFile) }, failure.WrittenFiles.ToArray());
        Assert.IsTrue(store.Snapshot!.Machine.PolExists, "the fault leaves a valid read-only target where the missing Registry.pol was expected");
        Assert.AreEqual("comment only", store.Snapshot.Machine.Comments?.GetComment(policy.Namespace, policy.Name));

        sandbox.AllowWrite(PolicyFile);
        PolicyApplyOutcome retry = store.Apply(target, null, "comment only");

        Assert.IsTrue(retry.IsApplied);
        Assert.IsTrue(retry.Changed);
        Assert.IsTrue(store.Snapshot!.Machine.PolExists, "recovery creates the empty first-save Registry.pol");
        Assert.AreEqual((initial.GptIni?.Version ?? 0) + 1, store.Snapshot.GptIni?.Version);
        Assert.AreEqual(1, sandbox.Refresher.Calls.Count);
    }

    [TestMethod]
    public void RecoveryOnOneSide_DoesNotClearTheOtherSidesPendingVersionBump()
    {
        using var sandbox = new GoldenSandbox();
        PolicyDefinition policy = Fixture.Load().Policy("Pol_Class_Machine");
        SeedEmptyPolicy(sandbox);
        GpoSnapshot initial = sandbox.Store.Open();
        var store = new PolicySaveStore(sandbox.Store, initial, readOnly: false);
        var machine = new PolicyRef(policy, GpoSide.Machine);
        var user = new PolicyRef(policy, GpoSide.User);
        sandbox.FailNextWriteAfter(PolicyFile, GpoFileNames.GptIniFile);

        _ = Assert.Throws<GpoStoreException>(() => store.Apply(machine, PolicyEdit.Disabled, "machine"));
        sandbox.AllowWrite(GpoFileNames.GptIniFile);

        PolicyApplyOutcome userSave = store.Apply(user, PolicyEdit.Disabled, "user");
        Assert.IsTrue(userSave.Changed);
        Assert.AreEqual(65536u, store.Snapshot!.GptIni?.Version);
        Assert.AreEqual(1, sandbox.Refresher.Calls.Count);
        CollectionAssert.AreEqual(new[] { GpoSide.User }, sandbox.Refresher.Calls);

        PolicyApplyOutcome machineRetry = store.Apply(machine, PolicyEdit.Disabled, "machine");
        Assert.IsTrue(machineRetry.Changed);
        Assert.AreEqual(65537u, store.Snapshot!.GptIni?.Version);
        CollectionAssert.AreEqual(new[] { GpoSide.User, GpoSide.Machine }, sandbox.Refresher.Calls);
    }

    [TestMethod]
    public void RetryWithOriginalComment_StillDiffersFromThePartialDiskBaseline()
    {
        using var sandbox = new GoldenSandbox();
        PolicyDefinition policy = Fixture.Load().Policy("Pol_Class_Machine");
        SeedEmptyPolicy(sandbox);
        var seedComments = Bwc.Gpo.Comments.CmtxDocument.CreateEmpty();
        seedComments.SetComment(policy.Namespace, policy.Name, "original");
        seedComments.RestrictNamespacesTo(policy.Namespace);
        sandbox.Seed(sandbox.CmtxPath(GpoSide.Machine), seedComments.ToBytes());
        GpoSnapshot initial = sandbox.Store.Open();
        var store = new PolicySaveStore(sandbox.Store, initial, readOnly: false);
        var target = new PolicyRef(policy, GpoSide.Machine);
        sandbox.FailNextWriteAfter(CommentFile, GpoFileNames.GptIniFile);

        _ = Assert.Throws<GpoStoreException>(() => store.Apply(target, null, "partial comment"));
        string partialComment = store.Snapshot!.Machine.Comments!.GetComment(policy.Namespace, policy.Name)!;
        Assert.AreEqual("partial comment", partialComment);
        Assert.AreNotEqual(
            initial.Machine.Comments!.GetComment(policy.Namespace, policy.Name),
            partialComment,
            "restoring the original comment control is dirty against the comment that actually reached disk");

        sandbox.AllowWrite(GpoFileNames.GptIniFile);
        PolicyApplyOutcome retry = store.Apply(target, null, "original");
        Assert.IsTrue(retry.Changed, "the original control value must be applied over the partial disk value");
        Assert.AreEqual("original", store.Snapshot!.Machine.Comments?.GetComment(policy.Namespace, policy.Name));
        Assert.AreEqual(1u, store.Snapshot.GptIni?.Version);
    }

    [TestMethod]
    public void DialogDirtyState_UsesPartialDiskBaselineAndKeepsExplicitRecoveryDirty()
    {
        Assert.IsTrue(PolicyEditDirtyState.IsDirty(
            PolicyState.NotConfigured,
            "original",
            optionsDirty: false,
            baselineState: PolicyState.NotConfigured,
            baselineComment: "partial comment",
            recoveryPending: false), "restoring the original control value is dirty against the partial disk comment");

        Assert.IsTrue(PolicyEditDirtyState.IsDirty(
            PolicyState.NotConfigured,
            "partial comment",
            optionsDirty: false,
            baselineState: PolicyState.NotConfigured,
            baselineComment: "partial comment",
            recoveryPending: true), "matching the partial snapshot still requires the pending save recovery");

        Assert.IsFalse(PolicyEditDirtyState.IsDirty(
            PolicyState.NotConfigured,
            "partial comment",
            optionsDirty: false,
            baselineState: PolicyState.NotConfigured,
            baselineComment: "partial comment",
            recoveryPending: false), "a completed recovery leaves the unchanged dialog clean");
    }

    [TestMethod]
    public void ZeroWriteFailure_LeavesSnapshotUnchanged()
    {
        using var sandbox = new GoldenSandbox();
        PolicyDefinition policy = Fixture.Load().Policy("Pol_Class_Machine");
        sandbox.Seed(sandbox.PolPath(GpoSide.Machine), [0x4E, 0x6F, 0x70, 0x65]);
        GpoSnapshot initial = sandbox.Store.Open();
        var store = new PolicySaveStore(sandbox.Store, initial, readOnly: false);

        GpoStoreException failure = Assert.Throws<GpoStoreException>(
            () => store.Apply(new PolicyRef(policy, GpoSide.Machine), PolicyEdit.Disabled, "pending comment"));

        Assert.AreEqual(GpoStoreFailure.Malformed, failure.Reason);
        Assert.AreEqual(0, failure.WrittenFiles.Count);
        Assert.AreSame(initial, store.Snapshot, "a failure before the first write must not publish or notify from a reread");
        Assert.AreEqual(0, sandbox.Refresher.Calls.Count);
    }

    [TestMethod]
    public void PartialWrite_RereadFailureDoesNotReplaceOriginalFailure()
    {
        using var sandbox = new GoldenSandbox();
        PolicyDefinition policy = Fixture.Load().Policy("Pol_Class_Machine");
        SeedEmptyPolicy(sandbox);
        GpoSnapshot initial = sandbox.Store.Open();
        var policyWithReadFault = new OpenFaultPolicy(sandbox.Policy);
        var gpoStore = new GpoStore(policyWithReadFault, new GpoStoreOptions
        {
            LockTimeout = TimeSpan.FromSeconds(5),
            WriteRetryCount = 2,
            WriteRetryDelay = TimeSpan.FromMilliseconds(10),
            WriteObserver = e =>
            {
                if (e.Kind == GpoWriteKind.FileWritten && Path.GetFullPath(e.Path).Equals(Path.GetFullPath(sandbox.PolPath(GpoSide.Machine)), StringComparison.OrdinalIgnoreCase))
                {
                    sandbox.MarkReadOnly(GpoFileNames.GptIniFile);
                    policyWithReadFault.FailOpen = true;
                }
            },
        });
        var store = new PolicySaveStore(gpoStore, initial, readOnly: false);

        GpoStoreException failure = Assert.Throws<GpoStoreException>(
            () => store.Apply(new PolicyRef(policy, GpoSide.Machine), PolicyEdit.Disabled, "pending comment"));

        Assert.AreEqual(GpoStoreFailure.ReadOnlyTarget, failure.Reason, "the write failure remains the reported failure");
        Assert.AreEqual(sandbox.GptPath, failure.Path);
        CollectionAssert.AreEqual(new[] { sandbox.PathOf(CommentFile), sandbox.PathOf(PolicyFile) }, failure.WrittenFiles.ToArray());
        Assert.AreEqual(1, policyWithReadFault.FailedOpenAttempts, "the partial-save snapshot reread is attempted");
        Assert.AreSame(initial, store.Snapshot, "a failed reread cannot replace the last usable snapshot");
    }

    private static void SeedEmptyPolicy(GoldenSandbox sandbox)
    {
        sandbox.Seed(sandbox.PolPath(GpoSide.Machine), PolicyModelTestSupport.Pol());
        sandbox.Seed(sandbox.GptPath, GptIni.CreateNew().ToBytes());
    }

    private sealed class OpenFaultPolicy(IGpoStorePolicy inner) : IGpoStorePolicy
    {
        public bool FailOpen { get; set; }

        public int FailedOpenAttempts { get; private set; }

        public string GpoRoot
        {
            get
            {
                if (FailOpen)
                {
                    FailedOpenAttempts++;
                    throw new IOException("injected snapshot reread failure");
                }

                return inner.GpoRoot;
            }
        }

        public string BackupRoot => inner.BackupRoot;

        public string LockPath => inner.LockPath;

        public Bwc.Gpo.Refresh.IPolicyRefresher Refresher => inner.Refresher;

        public void EnsureWriteAllowed(string path) => inner.EnsureWriteAllowed(path);

        public void EnsureRemoveAllowed(string path) => inner.EnsureRemoveAllowed(path);
    }
}

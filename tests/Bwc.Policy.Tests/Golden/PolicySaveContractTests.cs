using Bwc.Gpo.Store;
using Bwc.Policy.Model;

namespace Bwc.Policy.Tests.Golden;

/// <summary>
/// The contract of <see cref="PolicySave.Save"/> that no group A or K capture exercises
/// (docs/failure-modes/policy-save.md, S1, S5, S14, S15). Each test runs in a temp sandbox and names its
/// failure mode.
/// </summary>
[TestClass]
public sealed class PolicySaveContractTests
{
    private static readonly byte[] NotAPolicyFile = [0x4E, 0x6F, 0x70, 0x65];

    [TestMethod]
    public void S1_InvalidEdit_ReturnsProblems_ReadsLocksAndWritesNothing()
    {
        using var sandbox = new GoldenSandbox();
        var policy = Fixture.Load().Policy("Pol_Decimal_MinMax");
        var tooBig = PolicyEdit.Enabled(("MinMaxNumber", ElementValue.FromNumber(5000)));

        var outcome = PolicySave.Save(sandbox.Store, GpoSide.Machine, policy, tooBig, "a comment that must not be kept");

        Assert.IsFalse(outcome.IsValid);
        Assert.IsTrue(outcome.Problems.Any(p => p.Kind == PolicyProblemKind.AboveMaximum), "an above-maximum problem is expected");
        Assert.IsFalse(outcome.Changed);
        Assert.IsNull(outcome.Result);
        Assert.AreEqual(0, sandbox.FilesOnDisk().Length, "no file may exist");
        Assert.IsFalse(sandbox.BackupRootExists, "no lock file or backup may be made for an invalid edit");
        Assert.AreEqual(0, sandbox.Refresher.Calls.Count);
    }

    [TestMethod]
    public void S5_SaveThatChangesNothing_WritesNothing_AndAddsNoCommentFile()
    {
        using var sandbox = new GoldenSandbox();
        var policy = Fixture.Load().Policy("Pol_Class_Machine");

        var outcome = PolicySave.Save(sandbox.Store, GpoSide.Machine, policy, PolicyEdit.NotConfigured, string.Empty);

        Assert.IsTrue(outcome.IsValid);
        Assert.IsFalse(outcome.Changed);
        Assert.IsNotNull(outcome.Result);
        Assert.IsFalse(outcome.Result.Wrote);
        Assert.AreEqual(0, sandbox.FilesOnDisk().Length, "a save that changes nothing must not add a using line to a new comment file");
        Assert.AreEqual(0, sandbox.Refresher.Calls.Count);
    }

    [TestMethod]
    public void S14_CancelledBeforeTheFirstWrite_WritesNothing()
    {
        using var sandbox = new GoldenSandbox();
        var policy = Fixture.Load().Policy("Pol_Class_Machine");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => PolicySave.Save(sandbox.Store, GpoSide.Machine, policy, PolicyEdit.Enabled(), string.Empty, cancelled.Token));

        Assert.AreEqual(0, sandbox.FilesOnDisk().Length);
    }

    [TestMethod]
    public void S15_CommentOnlySave_OverAMalformedPolicyFile_ThrowsMalformed_AndWritesNothing()
    {
        using var sandbox = new GoldenSandbox();
        var policy = Fixture.Load().Policy("Pol_Class_Machine");
        sandbox.Seed(sandbox.PolPath(GpoSide.Machine), NotAPolicyFile);

        var ex = Assert.Throws<GpoStoreException>(
            () => PolicySave.Save(sandbox.Store, GpoSide.Machine, policy, null, "note"));

        Assert.AreEqual(GpoStoreFailure.Malformed, ex.Reason);
        CollectionAssert.AreEqual(NotAPolicyFile, File.ReadAllBytes(sandbox.PolPath(GpoSide.Machine)));
        Assert.AreEqual(1, sandbox.FilesOnDisk().Length, "only the seeded file may exist");
    }
}

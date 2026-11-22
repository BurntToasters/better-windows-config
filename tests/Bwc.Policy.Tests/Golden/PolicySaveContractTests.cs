using System.Collections;
using Bwc.Gpo.Store;
using Bwc.Policy.Admx;
using Bwc.Policy.Model;

namespace Bwc.Policy.Tests.Golden;

/// <summary>
/// The contract of <see cref="PolicySave.Save"/> that no group A or K capture exercises
/// (docs/failure-modes/policy-save.md, S1, S5, S14, S15, S17). Each test runs in a temp sandbox and names its
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

    /// <summary>The elements of a policy whose template "changes" once the write lock is held: before the lock file exists
    /// the policy has one optional text element, after it a second required one. It stands for a template that changed
    /// between the dialog and the save.</summary>
    private sealed class ElementsThatChangeUnderTheLock(string lockPath) : IReadOnlyList<PolicyElement>
    {
        private static readonly TextElement Note = new("Note", null, @"Software\Policies\BWC-Late", "Note");
        private static readonly TextElement Extra = new("Extra", null, @"Software\Policies\BWC-Late", "Extra") { Required = true };

        private PolicyElement[] Now => File.Exists(lockPath) ? [Note, Extra] : [Note];

        public int Count => Now.Length;

        public PolicyElement this[int index] => Now[index];

        public IEnumerator<PolicyElement> GetEnumerator() => ((IEnumerable<PolicyElement>)Now).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [TestMethod]
    public void S17_ProblemsFoundUnderTheLock_ReturnWithNoWriteNoCommentAndNoRefresh()
    {
        using var sandbox = new GoldenSandbox();
        var file = new TemplateFile("late.admx", "BWC.Late", "late", []);
        var policy = new PolicyDefinition(file, "Late", PolicyClass.Machine, @"Software\Policies\BWC-Late", null, null, 0, 0)
        {
            Elements = new ElementsThatChangeUnderTheLock(sandbox.Policy.LockPath),
        };
        var start = PolicyModelTestSupport.Pol(PolicyModelTestSupport.RecDw(@"Software\Policies\BWC-Other", "Kept", 7));
        sandbox.Seed(sandbox.PolPath(GpoSide.Machine), start);
        var edit = PolicyEdit.Enabled(("Note", ElementValue.FromText("typed")));

        Assert.AreEqual(0, PolicyEditValidator.Validate(policy, edit).Count, "before the lock the edit is valid, so the problem can only be found under the lock");
        var outcome = PolicySave.Save(sandbox.Store, GpoSide.Machine, policy, edit, "a comment that must not be kept");

        Assert.IsFalse(outcome.IsValid);
        Assert.IsTrue(outcome.Problems.Any(p => p is { ElementId: "Extra", Kind: PolicyProblemKind.Required }), "the required element found under the lock is reported");
        Assert.IsFalse(outcome.Changed);
        Assert.IsNull(outcome.Result);
        CollectionAssert.AreEqual(start, File.ReadAllBytes(sandbox.PolPath(GpoSide.Machine)), "the policy file must be as it was");
        CollectionAssert.AreEqual(new[] { GpoFileNames.FolderName(GpoSide.Machine) + "/" + GpoFileNames.RegistryPol }, sandbox.FilesOnDisk(), "no comment file and no version file may appear");
        Assert.AreEqual(0, sandbox.Refresher.Calls.Count);
    }
}

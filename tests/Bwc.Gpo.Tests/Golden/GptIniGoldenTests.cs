using Bwc.Gpo.Store;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Gpo.Tests.Golden;

/// <summary>
/// The gpt.ini rules alone, from the group A and K captures (docs/failure-modes/policy-save.md, section
/// G). Each test names its case, saves through <see cref="GpoStore"/> in a temp sandbox with an edit that
/// makes the same kind of change classic made, and compares gpt.ini byte for byte with the capture.
/// </summary>
[TestClass]
public sealed class GptIniGoldenTests
{
    private const string Ns = "BWC.Policies.Oracle";
    private const string Key = "Software\\Policies\\BWC-Oracle\\Basics";

    private static void SeedGpt(SandboxGpoFixture fx, string caseId, string step)
    {
        var start = GoldenData.Gpt(caseId, step);
        if (start is not null)
        {
            fx.Seed(fx.GptPath, start);
        }
    }

    private static GpoSaveResult SaveRecord(GpoStore store, GpoSide side) =>
        store.Save(side, s => GpoStorePolHelper.SetDword(s.Pol, Key, "FlagValue", 1));

    private static GpoSaveResult SaveCommentOnly(GpoStore store, GpoSide side) =>
        store.Save(side, s => s.Comments.SetComment(Ns, "Pol_Class_Machine", "bwc comment one"));

    private static void AssertGpt(SandboxGpoFixture fx, string caseId, string step)
    {
        var expected = GoldenData.Gpt(caseId, step);
        Assert.IsNotNull(expected, $"{caseId} {step}: the golden has a version file");
        CollectionAssert.AreEqual(expected, fx.Read(fx.GptPath), $"{caseId} {step}: the version file differs from classic");
    }

    [TestMethod]
    public void A_Machine_Enabled_FirstMachineSave_WritesVersionOneAndTheMachinePair()
    {
        using var fx = new SandboxGpoFixture();
        SeedGpt(fx, "A/Machine_Enabled", "step-01-start");

        SaveRecord(fx.NewStore(), GpoSide.Machine);

        AssertGpt(fx, "A/Machine_Enabled", "step-02-saved");
    }

    [TestMethod]
    public void D2_A_User_Enabled_FirstUserSave_AppendsTheMissingKeyAtTheEndOfTheSection()
    {
        using var fx = new SandboxGpoFixture();
        SeedGpt(fx, "A/User_Enabled", "step-01-start");

        SaveRecord(fx.NewStore(), GpoSide.User);

        AssertGpt(fx, "A/User_Enabled", "step-02-saved");
    }

    [TestMethod]
    public void A_Machine_Then_User_SecondSideAppendsItsPair_VersionCarriesBothHalves()
    {
        using var fx = new SandboxGpoFixture();
        SeedGpt(fx, "A/Machine_Then_User", "step-01-start");
        var store = fx.NewStore();

        SaveRecord(store, GpoSide.Machine);
        AssertGpt(fx, "A/Machine_Then_User", "step-02-machine");
        SaveRecord(store, GpoSide.User);
        AssertGpt(fx, "A/Machine_Then_User", "step-03-user");
    }

    [TestMethod]
    public void D2_A_User_Then_Machine_MachinePairIsAppendedAfterTheUserPair()
    {
        using var fx = new SandboxGpoFixture();
        SeedGpt(fx, "A/User_Then_Machine", "step-01-start");
        var store = fx.NewStore();

        SaveRecord(store, GpoSide.User);
        AssertGpt(fx, "A/User_Then_Machine", "step-02-user");
        SaveRecord(store, GpoSide.Machine);
        AssertGpt(fx, "A/User_Then_Machine", "step-03-machine");
    }

    [TestMethod]
    public void D3_D11_A_Machine_CommentOnly_WritesGptIniWithVersionOneAndNoExtensionLine()
    {
        using var fx = new SandboxGpoFixture();
        SeedGpt(fx, "A/Machine_CommentOnly", "step-01-start");

        SaveCommentOnly(fx.NewStore(), GpoSide.Machine);

        AssertGpt(fx, "A/Machine_CommentOnly", "step-02-saved");
    }

    [TestMethod]
    public void D3_D11_A_User_CommentOnly_RaisesTheUserHalfWithNoExtensionLine()
    {
        using var fx = new SandboxGpoFixture();
        SeedGpt(fx, "A/User_CommentOnly", "step-01-start");

        SaveCommentOnly(fx.NewStore(), GpoSide.User);

        AssertGpt(fx, "A/User_CommentOnly", "step-02-saved");
    }

    [TestMethod]
    public void A_Machine_NotConfigured_NoChange_WritesNothingAndLeavesGptIniAsFound()
    {
        using var fx = new SandboxGpoFixture();
        SeedGpt(fx, "A/Machine_NotConfigured", "step-01-start");

        var result = fx.NewStore().Save(GpoSide.Machine, _ => { });

        Assert.IsFalse(result.Wrote);
        AssertGpt(fx, "A/Machine_NotConfigured", "step-02-saved");
    }

    [TestMethod]
    public void A_Machine_Apply_OK_SecondSaveWithTheSameEdit_KeepsVersionOne()
    {
        using var fx = new SandboxGpoFixture();
        SeedGpt(fx, "A/Machine_Apply_OK", "step-01-start");
        var store = fx.NewStore();

        SaveRecord(store, GpoSide.Machine);
        var second = SaveRecord(store, GpoSide.Machine);

        Assert.IsFalse(second.Wrote, "the second press changes nothing");
        AssertGpt(fx, "A/Machine_Apply_OK", "step-02-saved");
    }

    [TestMethod]
    public void D4_K_KeyTrailingBackslash_Enable_ClearingTheLastRecord_KeepsTheExtensionLineWithOneSpace()
    {
        const string caseId = "K/KeyTrailingBackslash_Enable";
        using var fx = new SandboxGpoFixture();
        fx.Seed(fx.GptPath, GoldenData.Gpt(caseId, "step-01-enabled")!);
        fx.Seed(fx.PolPath(GpoSide.Machine), GoldenData.Pol(caseId, "step-01-enabled", GpoSide.Machine)!);

        fx.NewStore().Save(GpoSide.Machine, s => s.Pol.RemoveValue("Software\\Policies\\BWC-Oracle\\Edge\\Trailing", "EdgeValue"));

        AssertGpt(fx, caseId, "step-02-not-configured");
    }
}

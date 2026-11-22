using Bwc.Gpo.Comments;
using Bwc.Gpo.Store;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Gpo.Tests.Golden;

/// <summary>
/// The comment.cmtx rules alone, from the group A and K captures (docs/failure-modes/policy-save.md, S6
/// and section G). Each test names its case and compares bytes with the capture.
/// </summary>
[TestClass]
public sealed class CmtxGoldenTests
{
    private const string Oracle = "BWC.Policies.Oracle";
    private const string Comment = "bwc comment one";

    public static IEnumerable<object[]> CmtxGoldens() =>
    [
        ["A/Machine_Enabled", "step-02-saved", GpoSide.Machine],
        ["A/Machine_EnabledWithComment", "step-02-saved", GpoSide.Machine],
        ["A/Machine_CommentOnly", "step-02-saved", GpoSide.Machine],
        ["A/User_Enabled", "step-02-saved", GpoSide.User],
        ["A/User_EnabledWithComment", "step-02-saved", GpoSide.User],
        ["A/Both_Computer_Then_User", "step-03-user", GpoSide.User],
        ["K/KeyLeadingBackslash_Enable", "step-02-not-configured", GpoSide.Machine],
        ["K/KeyTrailingBackslash_Enable", "step-02-not-configured", GpoSide.Machine],
    ];

    private static void AssertBytes(string caseId, string step, GpoSide side, CmtxDocument actual)
    {
        var expected = GoldenData.Cmtx(caseId, step, side);
        Assert.IsNotNull(expected, $"{caseId} {step}: the golden has a comment file");
        CollectionAssert.AreEqual(expected, actual.ToBytes(), $"{caseId} {step}: the comment file differs from classic");
    }

    [TestMethod]
    [DynamicData(nameof(CmtxGoldens))]
    public void ClassicsFile_ParsesAndSerializesToTheSameBytes(string caseId, string step, GpoSide side)
    {
        var expected = GoldenData.Cmtx(caseId, step, side)!;

        var parsed = CmtxDocument.Parse(expected);

        Assert.IsFalse(parsed.HasChanges, $"{caseId}: parsing alone is no change");
        CollectionAssert.AreEqual(expected, parsed.ToBytes(), $"{caseId}: the file does not round trip");
    }

    [TestMethod]
    public void D5_A_Machine_Enabled_FirstSaveWithNoComment_HasOneUsingLine()
    {
        var document = CmtxDocument.CreateEmpty();

        document.RestrictNamespacesTo(Oracle);

        AssertBytes("A/Machine_Enabled", "step-02-saved", GpoSide.Machine, document);
    }

    [TestMethod]
    public void D5_A_User_Enabled_FirstUserSaveWithNoComment_HasOneUsingLine()
    {
        var document = CmtxDocument.CreateEmpty();

        document.RestrictNamespacesTo(Oracle);

        AssertBytes("A/User_Enabled", "step-02-saved", GpoSide.User, document);
    }

    [TestMethod]
    public void A_Machine_EnabledWithComment_HasTheEntryAndItsString()
    {
        var document = CmtxDocument.CreateEmpty();

        document.SetComment(Oracle, "Pol_Class_Machine", Comment);
        document.RestrictNamespacesTo(Oracle);

        AssertBytes("A/Machine_EnabledWithComment", "step-02-saved", GpoSide.Machine, document);
    }

    [TestMethod]
    public void A_User_EnabledWithComment_HasTheEntryAndItsString()
    {
        var document = CmtxDocument.CreateEmpty();

        document.SetComment(Oracle, "Pol_Class_User", Comment);
        document.RestrictNamespacesTo(Oracle);

        AssertBytes("A/User_EnabledWithComment", "step-02-saved", GpoSide.User, document);
    }

    [TestMethod]
    public void A_Machine_CommentOnly_HasTheSameBytesAsEnabledWithAComment_ForTheSamePolicy()
    {
        var document = CmtxDocument.CreateEmpty();

        document.SetComment(Oracle, "Pol_Class_Machine", Comment);
        document.RestrictNamespacesTo(Oracle);

        AssertBytes("A/Machine_CommentOnly", "step-02-saved", GpoSide.Machine, document);
    }

    [TestMethod]
    public void A_Both_UnderComputer_CommentOnly_NamesTheBothPolicyInTheMachineFile()
    {
        var document = CmtxDocument.CreateEmpty();

        document.SetComment(Oracle, "Pol_Class_Both", Comment);
        document.RestrictNamespacesTo(Oracle);

        AssertBytes("A/Both_UnderComputer_CommentOnly", "step-02-saved", GpoSide.Machine, document);
    }

    [TestMethod]
    public void K_KeyLeadingBackslash_Enable_UsesTheEdgeNamespaceOnly()
    {
        var document = CmtxDocument.CreateEmpty();

        document.RestrictNamespacesTo("BWC.Policies.Edge.KeyLeadingBackslash");

        AssertBytes("K/KeyLeadingBackslash_Enable", "step-01-enabled", GpoSide.Machine, document);
    }

    [TestMethod]
    public void A_Both_Computer_Then_User_EachSideHasItsOwnFileWithOneUsingLine()
    {
        var machine = CmtxDocument.CreateEmpty();
        machine.RestrictNamespacesTo(Oracle);
        var user = CmtxDocument.CreateEmpty();
        user.RestrictNamespacesTo(Oracle);

        AssertBytes("A/Both_Computer_Then_User", "step-02-computer", GpoSide.Machine, machine);
        AssertBytes("A/Both_Computer_Then_User", "step-03-user", GpoSide.User, user);
    }

    [TestMethod]
    public void D5_FirstSaveThroughTheStore_WritesTheCommentFileBeforeThePolicyFile()
    {
        using var fx = new SandboxGpoFixture();
        var store = fx.NewStore();

        store.Save(GpoSide.Machine, s =>
        {
            GpoStorePolHelper.SetDword(s.Pol, "Software\\Policies\\BWC-Oracle\\Basics", "FlagValue", 1);
            s.Comments.RestrictNamespacesTo(Oracle);
        });

        var expected = GoldenData.Cmtx("A/Machine_Enabled", "step-02-saved", GpoSide.Machine);
        CollectionAssert.AreEqual(expected, fx.Read(fx.CmtxPath(GpoSide.Machine)), "A/Machine_Enabled: the comment file differs from classic");
        Assert.IsTrue(fx.Log.IndexOf("write:Machine/" + GpoFileNames.CommentCmtx) < fx.Log.IndexOf("write:Machine/" + GpoFileNames.RegistryPol));
    }

    [TestMethod]
    public void A_Machine_NotConfigured_NoChange_LeavesTheCommentFileAbsent()
    {
        using var fx = new SandboxGpoFixture();

        var result = fx.NewStore().Save(GpoSide.Machine, _ => { });

        Assert.IsFalse(result.Wrote);
        Assert.IsNull(GoldenData.Cmtx("A/Machine_NotConfigured", "step-02-saved", GpoSide.Machine), "classic wrote no comment file");
        Assert.IsNull(fx.Read(fx.CmtxPath(GpoSide.Machine)));
    }
}

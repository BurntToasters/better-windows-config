using Bwc.Policy.Model;
using static Bwc.Policy.Tests.PolicyModelTestSupport;

namespace Bwc.Policy.Tests;

/// <summary>The classic probe of 2026-11-18, replayed through the policy layer on fixture policies in one key.</summary>
[TestClass]
public sealed class PolicyWriteProbeTests
{
    [TestMethod]
    public void ZetaThenAlphaEnabled_ThenZetaDisabled_GivesAlphaThenZeta()
    {
        var document = NewDocument();

        Assert.IsTrue(Apply("Pol_Shared_Zeta", document, PolicyEdit.Enabled()).Changed);
        AssertBytes(document, RecDw(Shared, "Zeta", 1));

        Assert.IsTrue(Apply("Pol_Shared_Alpha", document, PolicyEdit.Enabled()).Changed);
        AssertBytes(document, RecDw(Shared, "Zeta", 1), RecDw(Shared, "Alpha", 1));

        Assert.IsTrue(Apply("Pol_Shared_Zeta", document, PolicyEdit.Disabled).Changed);
        AssertBytes(document, RecDw(Shared, "Alpha", 1), RecDw(Shared, "Zeta", 0));

        Assert.IsTrue(Apply("Pol_Shared_Alpha", document, PolicyEdit.NotConfigured).Changed);
        AssertBytes(document, RecDw(Shared, "Zeta", 0));
    }

    [TestMethod]
    public void DisabledWithoutDisabledValue_RemovesTheValueAndAppendsDeleteInstruction()
    {
        var document = NewDocument();
        Apply("Pol_Value_Decimal", document, PolicyEdit.Enabled());
        Apply("Pol_Value_EnabledOnly", document, PolicyEdit.Enabled());
        AssertBytes(document, RecDw(Basics, "ModeNumber", 5), RecDw(Basics, "EnabledOnly", 7));

        Apply("Pol_Value_EnabledOnly", document, PolicyEdit.Disabled);
        AssertBytes(document, RecDw(Basics, "ModeNumber", 5), Del(Basics, "EnabledOnly"));
        Assert.AreEqual(PolicyState.Disabled, Read("Pol_Value_EnabledOnly", document).State);
    }

    [TestMethod]
    public void DisabledWithDisabledValue_WritesItAndMovesTheValueToTheEnd()
    {
        var document = NewDocument();
        Apply("Pol_Value_Decimal", document, PolicyEdit.Enabled());
        Apply("Pol_Value_EnabledOnly", document, PolicyEdit.Enabled());
        AssertBytes(document, RecDw(Basics, "ModeNumber", 5), RecDw(Basics, "EnabledOnly", 7));

        Apply("Pol_Value_Decimal", document, PolicyEdit.Disabled);

        AssertBytes(document, RecDw(Basics, "EnabledOnly", 7), RecDw(Basics, "ModeNumber", 9));
    }

    [TestMethod]
    public void NotConfigured_RemovesTheValueAndWritesNoInstruction()
    {
        var document = NewDocument();
        Apply("Pol_Value_Decimal", document, PolicyEdit.Enabled());
        Apply("Pol_Value_EnabledOnly", document, PolicyEdit.Enabled());

        var result = Apply("Pol_Value_EnabledOnly", document, PolicyEdit.NotConfigured);

        Assert.IsTrue(result.Changed);
        AssertBytes(document, RecDw(Basics, "ModeNumber", 5));

        Apply("Pol_Value_Decimal", document, PolicyEdit.NotConfigured);
        Assert.IsTrue(document.IsEmpty);
    }

    [TestMethod]
    public void NotConfigured_ClearsTheDeleteInstructionToo_SoThePolicyCannotReadAsDisabled()
    {
        var document = Parse(Del(Basics, "EnabledOnly"));

        Apply("Pol_Value_EnabledOnly", document, PolicyEdit.NotConfigured);

        Assert.IsTrue(document.IsEmpty);
        Assert.AreEqual(PolicyState.NotConfigured, Read("Pol_Value_EnabledOnly", document).State);
    }

    [TestMethod]
    public void ApplyThatChangesNothing_LeavesHasChangesFalse()
    {
        var document = Parse(RecDw(Shared, "Zeta", 1), RecDw(Shared, "Alpha", 1));

        var same = Apply("Pol_Shared_Zeta", document, PolicyEdit.Enabled());
        var untouched = Apply("Pol_Shared_Mid", document, PolicyEdit.NotConfigured);

        Assert.IsFalse(same.Changed);
        Assert.IsFalse(untouched.Changed);
        Assert.IsFalse(document.HasChanges);
        AssertBytes(document, RecDw(Shared, "Zeta", 1), RecDw(Shared, "Alpha", 1));
    }

    [TestMethod]
    public void DisabledTwice_WritesTheDeleteInstructionOnlyOnce()
    {
        var document = NewDocument();
        Apply("Pol_Value_EnabledOnly", document, PolicyEdit.Disabled);
        var again = Reload(document);

        var result = Apply("Pol_Value_EnabledOnly", again, PolicyEdit.Disabled);

        Assert.IsFalse(result.Changed);
        Assert.IsFalse(again.HasChanges);
        AssertBytes(again, Del(Basics, "EnabledOnly"));
    }

    [TestMethod]
    public void EnabledAfterDelete_RemovesTheDeleteInstruction()
    {
        var document = Parse(Del(Basics, "EnabledOnly"));

        Apply("Pol_Value_EnabledOnly", document, PolicyEdit.Enabled());

        AssertBytes(document, RecDw(Basics, "EnabledOnly", 7));
    }

    [TestMethod]
    public void OnlyDisabledValue_EnabledWritesTheDefault_DisabledWritesTheValue()
    {
        var document = NewDocument();

        Apply("Pol_Value_DisabledOnly", document, PolicyEdit.Enabled());
        AssertBytes(document, RecDw(Basics, "DisabledOnly", 1));

        Apply("Pol_Value_DisabledOnly", document, PolicyEdit.Disabled);
        AssertBytes(document, RecDw(Basics, "DisabledOnly", 3));
        Assert.AreEqual(PolicyState.Disabled, Read("Pol_Value_DisabledOnly", document).State);
    }

    [TestMethod]
    public void StringValues_AreWrittenAsRegSz()
    {
        var document = NewDocument();

        Apply("Pol_Value_String", document, PolicyEdit.Enabled());
        AssertBytes(document, RecSz(Basics, "ModeText", "on"));

        Apply("Pol_Value_String", document, PolicyEdit.Disabled);
        AssertBytes(document, RecSz(Basics, "ModeText", "off"));
    }

    [TestMethod]
    public void ForeignValuesAndInstructionsInTheKey_AreKeptByteForByte()
    {
        var foreign = RecDw(Shared, "Other", 42);
        var unknown = Rec(Shared, "**custom.Thing", 1, [0x20, 0x00, 0x00, 0x00]);
        var document = Parse(foreign, unknown);

        Apply("Pol_Shared_Alpha", document, PolicyEdit.Enabled());
        Apply("Pol_Shared_Alpha", document, PolicyEdit.Disabled);
        Apply("Pol_Shared_Alpha", document, PolicyEdit.NotConfigured);

        AssertBytes(document, foreign, unknown);
    }

    [TestMethod]
    public void ClassBoth_IsAppliedToEachDocumentOnItsOwn()
    {
        var machine = NewDocument();
        var user = NewDocument();

        Apply("Pol_Class_Both", machine, PolicyEdit.Enabled());

        Assert.AreEqual(PolicyState.Enabled, Read("Pol_Class_Both", machine).State);
        Assert.AreEqual(PolicyState.NotConfigured, Read("Pol_Class_Both", user).State);
        Apply("Pol_Class_Both", user, PolicyEdit.Disabled);
        Assert.AreEqual(PolicyState.Enabled, Read("Pol_Class_Both", machine).State);
        Assert.AreEqual(PolicyState.Disabled, Read("Pol_Class_Both", user).State);
    }

    [TestMethod]
    public void KeyOutsideThePolicyRoots_IsWrittenAsGiven()
    {
        var document = NewDocument();

        Apply("Pol_Outside_KeyOutsideRoots", document, PolicyEdit.Enabled());

        AssertBytes(document, RecDw(@"Software\BWC-Oracle-Gpedit", "OutsideFlag", 1));
    }
}

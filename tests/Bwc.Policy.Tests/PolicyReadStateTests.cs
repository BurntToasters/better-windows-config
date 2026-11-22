using Bwc.Gpo.Pol;
using Bwc.Policy.Model;
using static Bwc.Policy.Tests.PolicyModelTestSupport;

namespace Bwc.Policy.Tests;

/// <summary>State and element values read from documents, including partial, foreign and wrong-type files.</summary>
[TestClass]
public sealed class PolicyReadStateTests
{
    private static readonly byte[] DeleteData = [0x20, 0x00, 0x00, 0x00];

    [TestMethod]
    public void EmptyDocument_EveryFixturePolicyIsNotConfigured()
    {
        var set = Fixture.Load();
        var document = NewDocument();

        foreach (var policy in set.Policies)
        {
            var snapshot = PolicyStateReader.Read(policy, document);
            Assert.AreEqual(PolicyState.NotConfigured, snapshot.State, policy.Name);
            Assert.AreEqual(0, snapshot.Elements.Count, policy.Name);
            Assert.AreEqual(0, snapshot.OwnedRecords.Count, policy.Name);
        }
    }

    [TestMethod]
    public void Reading_NeverChangesTheDocument()
    {
        var set = Fixture.Load();
        var document = Parse(RecDw(Shared, "Alpha", 1), Del(Shared, "Zeta"), RecSz(Lists + @"\Plain", "a", "a"));

        foreach (var policy in set.Policies)
        {
            PolicyStateReader.Read(policy, document);
        }

        Assert.IsFalse(document.HasChanges);
        Assert.AreEqual(0L, document.Version);
    }

    [TestMethod]
    public void ValueName_ReadsEnabledAndDisabledByTheDefaultNumbers()
    {
        Assert.AreEqual(PolicyState.Enabled, Read("Pol_Shared_Alpha", Parse(RecDw(Shared, "Alpha", 1))).State);
        Assert.AreEqual(PolicyState.Disabled, Read("Pol_Shared_Alpha", Parse(RecDw(Shared, "Alpha", 0))).State);
    }

    [TestMethod]
    public void ValueName_FromTheTemplateValues()
    {
        Assert.AreEqual(PolicyState.Enabled, Read("Pol_Value_Decimal", Parse(RecDw(Basics, "ModeNumber", 5))).State);
        Assert.AreEqual(PolicyState.Disabled, Read("Pol_Value_Decimal", Parse(RecDw(Basics, "ModeNumber", 9))).State);
        Assert.AreEqual(PolicyState.Enabled, Read("Pol_Value_String", Parse(RecSz(Basics, "ModeText", "on"))).State);
        Assert.AreEqual(PolicyState.Disabled, Read("Pol_Value_String", Parse(RecSz(Basics, "ModeText", "off"))).State);
    }

    [TestMethod]
    public void ValueName_ThatMatchesNeitherLiteralFollowsTheUnmatchedRule()
    {
        var expected = PolicyRules.UnmatchedValueState;

        // Changed for the plain flag: captures D/Mismatch_FlagValue2 and D/WrongType_FlagAsString show classic reading 2 and a
        // REG_SZ "1" at a flag as Enabled, so only the policies that declare a value follow the unmatched rule.
        Assert.AreEqual(PolicyState.Enabled, Read("Pol_Shared_Alpha", Parse(RecDw(Shared, "Alpha", 2))).State);
        Assert.AreEqual(PolicyState.Enabled, Read("Pol_Shared_Alpha", Parse(RecSz(Shared, "Alpha", "1"))).State);
        Assert.AreEqual(expected, Read("Pol_Value_EnabledOnly", Parse(RecDw(Basics, "EnabledOnly", 1))).State);
        Assert.AreEqual(expected, Read("Pol_Value_Decimal", Parse(Rec(Basics, "ModeNumber", 11, Qw(5)))).State);
    }

    [TestMethod]
    public void DeleteInstructionOnly_FollowsTheDeleteRule()
    {
        var expected = PolicyRules.DeleteInstructionOnlyIsDisabled ? PolicyState.Disabled : PolicyState.NotConfigured;

        Assert.AreEqual(expected, Read("Pol_Shared_Alpha", Parse(Del(Shared, "Alpha"))).State);

        // D7: a policy that declares an enabledValue reads a lone delete instruction as Not Configured (B/Pol_Value_EnabledOnly,
        // the dialog of step 3 shows Not Configured over a file that holds only the instruction).
        Assert.AreEqual(PolicyState.NotConfigured, Read("Pol_Value_EnabledOnly", Parse(Del(Basics, "EnabledOnly"))).State);
    }

    [TestMethod]
    public void DeleteInstructionOfAnotherPolicy_IsIgnored()
    {
        Assert.AreEqual(PolicyState.NotConfigured, Read("Pol_Shared_Alpha", Parse(Del(Shared, "Zeta"))).State);
    }

    [TestMethod]
    public void KeysAndNamesCompareIgnoringCase()
    {
        var document = Parse(RecDw(Shared.ToLowerInvariant(), "ALPHA", 1));

        Assert.AreEqual(PolicyState.Enabled, Read("Pol_Shared_Alpha", document).State);
    }

    [TestMethod]
    public void FileWithOnlyForeignValues_IsNotConfigured_AndOwnsNothing()
    {
        var document = Parse(RecDw(Shared, "Unrelated", 1), RecSz(Shared, "Other", "x"));

        var snapshot = Read("Pol_Shared_Alpha", document);

        Assert.AreEqual(PolicyState.NotConfigured, snapshot.State);
        Assert.AreEqual(0, snapshot.OwnedRecords.Count);
    }

    [TestMethod]
    public void OwnedRecords_AreTheValueItsInstructionsAndNothingElse()
    {
        var document = Parse(
            RecDw(Shared, "Alpha", 1),
            Del(Shared, "Alpha"),
            Rec(Shared, "**soft.Alpha", 1, DeleteData),
            RecDw(Shared, "Mid", 1),
            Del(Shared, "Zeta"),
            Rec(Shared, "**custom.Alpha", 1, DeleteData));

        var owned = Read("Pol_Shared_Alpha", document).OwnedRecords.Select(r => r.ValueName).ToArray();

        CollectionAssert.AreEquivalent(new[] { "Alpha", "**del.Alpha", "**soft.Alpha" }, owned);
    }

    [TestMethod]
    public void OwnedRecords_OfAList_AreEveryRecordOfItsKey()
    {
        var key = Lists + @"\Plain";
        var document = Parse(RecSz(key, "a", "a"), RecDw(key, "number", 1), Rec(key, "**delvals.", 1, DeleteData), RecSz(Lists + @"\Other", "z", "z"));

        var owned = Read("Pol_List_Plain", document).OwnedRecords.Select(r => r.ValueName).ToArray();

        CollectionAssert.AreEquivalent(new[] { "a", "number", "**delvals." }, owned);
    }

    [TestMethod]
    public void OwnedRecords_OfAPolicyWithElementKeys_CoverEveryKey()
    {
        var elementKey = Basics + @"\ElementKey";
        var document = Parse(RecDw(Basics, "ElementKeyFlag", 1), RecSz(elementKey, "OverrideText", "v"), RecSz(elementKey, "Foreign", "x"));

        var owned = Read("Pol_Element_KeyOverride", document).OwnedRecords;

        CollectionAssert.AreEquivalent(
            new[] { new PolicyOwnedRecord(Basics, "ElementKeyFlag"), new PolicyOwnedRecord(elementKey, "OverrideText") },
            owned.ToArray());
    }

    [TestMethod]
    public void Elements_ArePartialWhenSomeRecordsAreMissing()
    {
        var document = Parse(RecDw(Shared, "Gate", 1), RecDw(Shared, "AlphaElement", 3));

        var snapshot = Read("Pol_Shared_ElementsReverseOrder", document);

        Assert.AreEqual(PolicyState.Enabled, snapshot.State);
        Assert.AreEqual(1, snapshot.Elements.Count);
        Assert.AreEqual(new NumberValue(3), snapshot.Elements["alphaelement"]);
    }

    [TestMethod]
    public void Elements_AreEmptyWhenThePolicyIsNotEnabled()
    {
        var document = Parse(RecDw(Shared, "Gate", 0), RecSz(Shared, "ZetaElement", "z"));

        var snapshot = Read("Pol_Shared_ElementsReverseOrder", document);

        Assert.AreEqual(PolicyState.Disabled, snapshot.State);
        Assert.AreEqual(0, snapshot.Elements.Count);
    }

    [TestMethod]
    public void Elements_OfAnotherRegistryType_AreReadFromTheirData()
    {
        // Changed from "are not values": captures D/WrongType_DecimalAsString (a text box reads a DWORD), D/WrongType_LongAsDword
        // and D/WrongType_ExpandableAsPlain show classic reading an element whatever the type of its record.
        var document = Parse(RecDw(Shared, "Gate", 1), RecDw(Shared, "ZetaElement", 1), RecSz(Shared, "AlphaElement", "2"));

        var snapshot = Read("Pol_Shared_ElementsReverseOrder", document);

        Assert.AreEqual(PolicyState.Enabled, snapshot.State);
        Assert.AreEqual(2, snapshot.Elements.Count);
    }

    [TestMethod]
    public void Text_ExpandableAndPlain_ReadEitherStringType()
    {
        // Changed from "must match their type": capture D/WrongType_ExpandableAsPlain shows Enabled for both mismatches.
        Assert.AreEqual(1, Read("Pol_Text_Expandable", Parse(RecSz(Text, "ExpandText", "x"))).Elements.Count);
        Assert.AreEqual(PolicyState.Enabled, Read("Pol_Text_Plain", Parse(Rec(Text, "PlainText", 2, Sz("x")))).State);
        Assert.AreEqual(PolicyState.Enabled, Read("Pol_Text_Expandable", Parse(Rec(Text, "ExpandText", 2, Sz("x")))).State);
    }

    [TestMethod]
    public void NoValueNameElementsAndWrongTypes_ReadFromTheirData()
    {
        // Changed from "read Not Configured": D/WrongType_StoreAsTextAsDword, D/WrongType_LongAsDword and
        // D/WrongType_MultiAsString show classic reading Enabled for these records. Enum values stay exact
        // (D/Mismatch_EnumUnknownItem).
        Assert.AreEqual(PolicyState.Enabled, Read("Pol_Decimal_StoreAsText", Parse(RecDw(Number, "TextNumber", 5))).State);
        Assert.AreEqual(PolicyState.Enabled, Read("Pol_Decimal_StoreAsText", Parse(RecSz(Number, "TextNumber", "abc"))).State);
        Assert.AreEqual(PolicyState.Enabled, Read("Pol_LongDecimal_Basic", Parse(RecDw(Number, "LongNumber", 5))).State);
        Assert.AreEqual(PolicyState.Enabled, Read("Pol_MultiText_Basic", Parse(RecSz(Text, "MultiLines", "a"))).State);
        Assert.AreEqual(PolicyState.NotConfigured, Read("Pol_Enum_DecimalItems", Parse(RecDw(Choice, "DecimalChoice", 99))).State);
        Assert.AreEqual(PolicyState.NotConfigured, Read("Pol_Enum_StringItems", Parse(RecSz(Choice, "StringChoice", "purple"))).State);
    }

    [TestMethod]
    public void Enum_ReadsTheItemWhoseValueMatches()
    {
        var snapshot = Read("Pol_Enum_StringItems", Parse(RecSz(Choice, "StringChoice", "blue")));

        Assert.AreEqual(PolicyState.Enabled, snapshot.State);
        Assert.AreEqual(new ChoiceValue(2), snapshot.Elements["StringChoice"]);
    }

    [TestMethod]
    public void List_ReadsStringValuesOnly_AndKeepsForeignTypesOut()
    {
        var key = Lists + @"\Plain";
        // The names equal the data: capture D/List_PlainValues shows a plain list whose names differ from its data reading Not
        // Configured, B/Pol_List_Plain shows names equal to the data reading Enabled.
        var document = Parse(RecSz(key, "one", "one"), RecDw(key, "n", 4), Rec(key, "**delvals.", 1, DeleteData), RecSz(key, "two", "two"));

        var list = (ListValue)Read("Pol_List_Plain", document).Elements["PlainList"];

        CollectionAssert.AreEqual(new[] { "one", "two" }, list.Entries.Select(e => e.Data).ToArray());
        CollectionAssert.AreEqual(new[] { "one", "two" }, list.Entries.Select(e => e.Name).ToArray());
    }

    [TestMethod]
    public void List_WithOnlyTheDeleteValuesInstruction_FollowsTheDelvalsRule()
    {
        var document = Parse(Rec(Lists + @"\Plain", "**delvals.", 1, DeleteData));

        var state = Read("Pol_List_Plain", document).State;

        Assert.AreEqual(PolicyRules.ListDelvalsOnlyIsDisabled ? PolicyState.Disabled : PolicyState.NotConfigured, state);
    }

    [TestMethod]
    public void EnabledList_WithOneItemMissing_IsNotConfigured()
    {
        var def = Basics + @"\EnabledListKey";
        var document = Parse(RecDw(def, "ListItemOne", 1), RecSz(Basics + @"\ItemKeyOverride", "ListItemTwo", "two"));

        Assert.AreEqual(PolicyState.NotConfigured, Read("Pol_Value_EnabledListOnly", document).State);
    }

    [TestMethod]
    public void EnabledList_WithOneItemOfTheWrongData_IsNotConfigured()
    {
        var def = Basics + @"\EnabledListKey";
        var document = Parse(
            RecDw(def, "ListItemOne", 1),
            RecDw(def, "ListItemThree", 4),
            RecSz(Basics + @"\ItemKeyOverride", "ListItemTwo", "two"));

        Assert.AreEqual(PolicyState.NotConfigured, Read("Pol_Value_EnabledListOnly", document).State);
    }

    [TestMethod]
    public void ClassBoth_ReadsEachHiveFromItsOwnDocument()
    {
        var machine = Parse(RecDw(Basics, "BothFlag", 1));
        var user = Parse(RecDw(Basics, "BothFlag", 0));

        Assert.AreEqual(PolicyState.Enabled, Read("Pol_Class_Both", machine).State);
        Assert.AreEqual(PolicyState.Disabled, Read("Pol_Class_Both", user).State);
    }

    [TestMethod]
    public void SnapshotElementsFedBackToTheWriter_ChangeNothing()
    {
        var document = NewDocument();
        Apply("Pol_Both_WithElements", document, PolicyEdit.Enabled(("BothNumber", ElementValue.FromNumber(42))));
        var enabled = Read("Pol_Both_WithElements", document);
        var again = Apply("Pol_Both_WithElements", document, PolicyEdit.EnabledWith(enabled.Elements));

        Assert.AreEqual(PolicyState.Enabled, enabled.State);
        Assert.AreEqual(new NumberValue(42), enabled.Elements["BothNumber"]);
        Assert.IsFalse(again.Changed);
    }
}

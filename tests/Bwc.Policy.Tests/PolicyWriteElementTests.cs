using Bwc.Gpo.Pol;
using Bwc.Policy.Model;
using static Bwc.Policy.Tests.PolicyModelTestSupport;

namespace Bwc.Policy.Tests;

/// <summary>Each element kind written, read back, and applied again with no change.</summary>
[TestClass]
public sealed class PolicyWriteElementTests
{
    private static ElementValue Value(PolicyStateSnapshot snapshot, string id) =>
        snapshot.Elements.TryGetValue(id, out var value) ? value : throw new AssertFailedException("No value for " + id);

    [TestMethod]
    public void Boolean_TrueAndFalseValues()
    {
        var on = ApplyTwice("Pol_Bool_TrueFalseValues", PolicyEdit.Enabled(("TrueFalseCheck", ElementValue.FromBoolean(true))));
        AssertBytes(on, RecDw(Bool, "TrueFalseCheck", 10));
        Assert.AreEqual(new BooleanValue(true), Value(Read("Pol_Bool_TrueFalseValues", on), "TrueFalseCheck"));

        Apply("Pol_Bool_TrueFalseValues", on, PolicyEdit.Enabled(("TrueFalseCheck", ElementValue.FromBoolean(false))));
        AssertBytes(on, RecDw(Bool, "TrueFalseCheck", 20));
        Assert.AreEqual(new BooleanValue(false), Value(Read("Pol_Bool_TrueFalseValues", on), "TrueFalseCheck"));
    }

    [TestMethod]
    public void Boolean_StringValues()
    {
        var document = ApplyTwice("Pol_Bool_StringValues", PolicyEdit.Enabled(("StringCheck", ElementValue.FromBoolean(true))));
        AssertBytes(document, RecSz(Bool, "StringCheck", "yes"));

        Apply("Pol_Bool_StringValues", document, PolicyEdit.Enabled(("StringCheck", ElementValue.FromBoolean(false))));
        AssertBytes(document, RecSz(Bool, "StringCheck", "no"));
    }

    [TestMethod]
    public void Boolean_WithoutFalseValue_UncheckedRemovesTheValue()
    {
        var document = ApplyTwice("Pol_Bool_NoFalseValue", PolicyEdit.Enabled(("NoFalseCheck", ElementValue.FromBoolean(true))));
        AssertBytes(document, RecDw(Bool, "NoFalseCheck", 1));

        var result = Apply("Pol_Bool_NoFalseValue", document, PolicyEdit.Enabled(("NoFalseCheck", ElementValue.FromBoolean(false))));

        Assert.IsTrue(result.Changed);
        Assert.IsTrue(document.IsEmpty);
        Assert.AreEqual(PolicyRules.FalseBooleanWithoutFalseValueWritesDeleteInstruction, document.GetValue(Bool, "**del.NoFalseCheck") is not null);
    }

    [TestMethod]
    public void ElementsOnly_ForeignValueInTheKeyStaysAndIsNotOwned()
    {
        var amp = K + @"\Amp";
        var document = Parse(RecDw(amp, "AmpName", 1));

        Apply("Pol_Amp_LabelSingle", document, PolicyEdit.Enabled(("AmpNumber", ElementValue.FromNumber(3))));
        var snapshot = Read("Pol_Amp_LabelSingle", document);

        AssertBytes(document, RecDw(amp, "AmpName", 1), RecDw(amp, "AmpNumber", 3));
        Assert.AreEqual(PolicyState.Enabled, snapshot.State);
        Assert.AreEqual(new NumberValue(3), Value(snapshot, "AmpNumber"));
        CollectionAssert.AreEqual(new[] { new PolicyOwnedRecord(amp, "AmpNumber") }, snapshot.OwnedRecords.ToArray());
    }

    [TestMethod]
    public void Boolean_TrueAndFalseLists_WriteTheirItemsInTheirKeys()
    {
        var t1 = Bool + @"\TrueListKey";
        var t2 = Bool + @"\TrueListOtherKey";
        var f = Bool + @"\FalseListKey";
        var document = ApplyTwice("Pol_Bool_TrueFalseLists", PolicyEdit.Enabled(("ListCheck", ElementValue.FromBoolean(true))));
        AssertBytes(document, RecDw(t1, "TrueListOne", 1), RecSz(t2, "TrueListTwo", "t2"));
        Assert.AreEqual(new BooleanValue(true), Value(Read("Pol_Bool_TrueFalseLists", document), "ListCheck"));

        Apply("Pol_Bool_TrueFalseLists", document, PolicyEdit.Enabled(("ListCheck", ElementValue.FromBoolean(false))));

        AssertBytes(document, RecDw(f, "FalseListOne", 0), RecSz(f, "FalseListTwo", "f2"));
        Assert.AreEqual(new BooleanValue(false), Value(Read("Pol_Bool_TrueFalseLists", document), "ListCheck"));
    }

    [TestMethod]
    public void Boolean_TwoChecksInReverseNameOrder_AreWrittenInTemplateOrder()
    {
        var key = Bool + @"\Order";
        var document = ApplyTwice(
            "Pol_Bool_TwoChecksReverseOrder",
            PolicyEdit.Enabled(("AlphaCheck", ElementValue.FromBoolean(true)), ("ZetaCheck", ElementValue.FromBoolean(true))));
        AssertBytes(document, RecDw(key, "ZetaCheck", 1), RecDw(key, "AlphaCheck", 1));

        Apply(
            "Pol_Bool_TwoChecksReverseOrder",
            document,
            PolicyEdit.Enabled(("AlphaCheck", ElementValue.FromBoolean(true)), ("ZetaCheck", ElementValue.FromBoolean(false))));

        AssertBytes(document, RecDw(key, "AlphaCheck", 1), RecDw(key, "ZetaCheck", 0));
    }

    [TestMethod]
    public void Boolean_WithNoValuesAtAll_DefaultsToOneAndZero()
    {
        var amp = K + @"\Amp";
        var document = ApplyTwice("Pol_Amp_LabelSingle", PolicyEdit.Enabled(("AmpCheck", ElementValue.FromBoolean(true))));
        AssertBytes(document, RecDw(amp, "AmpCheck", 1));

        Apply("Pol_Amp_LabelSingle", document, PolicyEdit.Enabled(("AmpCheck", ElementValue.FromBoolean(false))));

        AssertBytes(document, RecDw(amp, "AmpCheck", 0));
        Assert.AreEqual(new BooleanValue(false), Value(Read("Pol_Amp_LabelSingle", document), "AmpCheck"));
    }

    [TestMethod]
    public void Decimal_IsADword_AndReadsBack()
    {
        var document = ApplyTwice("Pol_Decimal_MinMax", PolicyEdit.Enabled(("MinMaxNumber", ElementValue.FromNumber(100))));

        AssertBytes(document, RecDw(Number, "MinMaxNumber", 100));
        Assert.AreEqual(new NumberValue(100), Value(Read("Pol_Decimal_MinMax", document), "MinMaxNumber"));
    }

    [TestMethod]
    public void Decimal_StoreAsText_IsRegSzDigits()
    {
        var document = ApplyTwice("Pol_Decimal_StoreAsText", PolicyEdit.Enabled(("TextNumber", ElementValue.FromNumber(80))));

        AssertBytes(document, RecSz(Number, "TextNumber", "80"));
        Assert.AreEqual(new NumberValue(80), Value(Read("Pol_Decimal_StoreAsText", document), "TextNumber"));
    }

    [TestMethod]
    public void Decimal_RequiredAndSoft_AreWrittenLikeAnyDecimal()
    {
        var required = ApplyTwice("Pol_Decimal_Required", PolicyEdit.Enabled(("RequiredNumber", ElementValue.FromNumber(7))));
        var soft = ApplyTwice("Pol_Decimal_Soft", PolicyEdit.Enabled(("SoftNumber", ElementValue.FromNumber(50))));

        AssertBytes(required, RecDw(Number, "RequiredNumber", 7));
        AssertBytes(soft, RecDw(Number, "SoftNumber", 50));
    }

    [TestMethod]
    public void LongDecimal_IsAQword_AndReadsBack()
    {
        var document = ApplyTwice("Pol_LongDecimal_Basic", PolicyEdit.Enabled(("LongNumber", ElementValue.FromNumber(5_000_000_000_000))));

        AssertBytes(document, Rec(Number, "LongNumber", 11, Qw(5_000_000_000_000)));
        Assert.AreEqual(new NumberValue(5_000_000_000_000), Value(Read("Pol_LongDecimal_Basic", document), "LongNumber"));
    }

    [TestMethod]
    public void LongDecimal_StoreAsText_IsRegSzDigits()
    {
        var document = ApplyTwice("Pol_LongDecimal_RequiredSoftText", PolicyEdit.Enabled(("LongTextNumber", ElementValue.FromNumber(18_000_000_000_000_000_000))));

        AssertBytes(document, RecSz(Number, "LongTextNumber", "18000000000000000000"));
        Assert.AreEqual(
            new NumberValue(18_000_000_000_000_000_000),
            Value(Read("Pol_LongDecimal_RequiredSoftText", document), "LongTextNumber"));
    }

    [TestMethod]
    public void Text_PlainIsRegSz_ExpandableIsRegExpandSz()
    {
        var plain = ApplyTwice("Pol_Text_Plain", PolicyEdit.Enabled(("PlainText", ElementValue.FromText("hello"))));
        var expand = ApplyTwice("Pol_Text_Expandable", PolicyEdit.Enabled(("ExpandText", ElementValue.FromText("%PATH%"))));

        AssertBytes(plain, RecSz(Text, "PlainText", "hello"));
        AssertBytes(expand, Rec(Text, "ExpandText", 2, Sz("%PATH%")));
        Assert.AreEqual(new TextValue("hello"), Value(Read("Pol_Text_Plain", plain), "PlainText"));
        Assert.AreEqual(new TextValue("%PATH%"), Value(Read("Pol_Text_Expandable", expand), "ExpandText"));
    }

    [TestMethod]
    public void Text_AtTheMaxLength_IsAccepted()
    {
        var document = ApplyTwice("Pol_Text_MaxLength", PolicyEdit.Enabled(("ShortText", ElementValue.FromText("123456789012"))));

        AssertBytes(document, RecSz(Text, "ShortText", "123456789012"));
    }

    [TestMethod]
    public void Text_EmptyOptional_RemovesTheValue()
    {
        var document = ApplyTwice("Pol_Text_Plain", PolicyEdit.Enabled(("PlainText", ElementValue.FromText("x"))));

        var result = Apply("Pol_Text_Plain", document, PolicyEdit.Enabled(("PlainText", ElementValue.FromText(string.Empty))));

        Assert.IsTrue(result.IsValid);
        Assert.IsTrue(result.Changed);
        Assert.IsTrue(document.IsEmpty);
    }

    [TestMethod]
    public void MultiText_IsRegMultiSz_AndEmptyLinesAreDropped()
    {
        var document = ApplyTwice("Pol_MultiText_Basic", PolicyEdit.Enabled(("MultiLines", ElementValue.FromLines("a", string.Empty, "bc"))));

        AssertBytes(document, Rec(Text, "MultiLines", 7, Multi("a", "bc")));
        var read = (MultiTextValue)Value(Read("Pol_MultiText_Basic", document), "MultiLines");
        CollectionAssert.AreEqual(new[] { "a", "bc" }, read.Lines.ToArray());
    }

    [TestMethod]
    public void NoValueNameElementsOnly_PartialValuesReadBackPartial()
    {
        var document = ApplyTwice("Pol_Value_NoValueName_ElementsOnly", PolicyEdit.Enabled(("OnlyNumber", ElementValue.FromNumber(4))));

        AssertBytes(document, RecDw(Basics, "OnlyNumber", 4));
        var snapshot = Read("Pol_Value_NoValueName_ElementsOnly", document);
        Assert.AreEqual(PolicyState.Enabled, snapshot.State);
        Assert.AreEqual(1, snapshot.Elements.Count);
        Assert.AreEqual(new NumberValue(4), Value(snapshot, "OnlyNumber"));
    }

    [TestMethod]
    public void ElementKeyOverride_WritesTheElementInItsOwnKey()
    {
        var document = ApplyTwice("Pol_Element_KeyOverride", PolicyEdit.Enabled(("OverrideText", ElementValue.FromText("v"))));

        AssertBytes(document, RecDw(Basics, "ElementKeyFlag", 1), RecSz(Basics + @"\ElementKey", "OverrideText", "v"));
        Assert.AreEqual(new TextValue("v"), Value(Read("Pol_Element_KeyOverride", document), "OverrideText"));
    }

    [TestMethod]
    public void PolicyValueAndElementsInOneKey_AreWrittenValueFirstThenElementsInTemplateOrder()
    {
        var document = ApplyTwice(
            "Pol_Shared_ElementsReverseOrder",
            PolicyEdit.Enabled(("AlphaElement", ElementValue.FromNumber(2)), ("ZetaElement", ElementValue.FromText("z"))));

        AssertBytes(document, RecDw(Shared, "Gate", 1), RecSz(Shared, "ZetaElement", "z"), RecDw(Shared, "AlphaElement", 2));
    }

    [TestMethod]
    public void Enum_DecimalAndStringItems()
    {
        var numbers = ApplyTwice("Pol_Enum_DecimalItems", PolicyEdit.Enabled(("DecimalChoice", ElementValue.FromChoice(2))));
        var strings = ApplyTwice("Pol_Enum_StringItems", PolicyEdit.Enabled(("StringChoice", ElementValue.FromChoice(1))));

        AssertBytes(numbers, RecDw(Choice, "DecimalChoice", 2));
        AssertBytes(strings, RecSz(Choice, "StringChoice", "green"));
        Assert.AreEqual(new ChoiceValue(2), Value(Read("Pol_Enum_DecimalItems", numbers), "DecimalChoice"));
        Assert.AreEqual(new ChoiceValue(1), Value(Read("Pol_Enum_StringItems", strings), "StringChoice"));
    }

    [TestMethod]
    public void Enum_ItemValueList_SwitchingTheItemRemovesTheOldListValues()
    {
        var a = Choice + @"\ProfileA";
        var aOther = Choice + @"\ProfileAOther";
        var document = ApplyTwice("Pol_Enum_ItemValueList", PolicyEdit.Enabled(("ProfileChoice", ElementValue.FromChoice(0))));
        AssertBytes(document, RecDw(Choice, "ProfileChoice", 1), RecDw(a, "ProfileAFirst", 100), RecSz(aOther, "ProfileASecond", "second"));
        Assert.AreEqual(new ChoiceValue(0), Value(Read("Pol_Enum_ItemValueList", document), "ProfileChoice"));

        Apply("Pol_Enum_ItemValueList", document, PolicyEdit.Enabled(("ProfileChoice", ElementValue.FromChoice(2))));
        AssertBytes(document, RecDw(Choice, "ProfileChoice", 3), RecDw(Choice, "ProfileCOnly", 300));

        Apply("Pol_Enum_ItemValueList", document, PolicyEdit.Enabled(("ProfileChoice", ElementValue.FromChoice(1))));
        AssertBytes(document, RecDw(Choice, "ProfileChoice", 2));
    }

    [TestMethod]
    public void List_Plain_NameIsTheData_AndAReplaceRewritesTheKey()
    {
        var key = Lists + @"\Plain";
        var document = ApplyTwice("Pol_List_Plain", PolicyEdit.Enabled(("PlainList", ElementValue.FromList(ListEntry.Of("b"), ListEntry.Of("a")))));
        var delvals = Rec(key, "**delvals.", 1, [0x20, 0x00, 0x00, 0x00]);
        AssertBytes(document, delvals, RecSz(key, "b", "b"), RecSz(key, "a", "a"));
        var read = (ListValue)Value(Read("Pol_List_Plain", document), "PlainList");
        CollectionAssert.AreEqual(new[] { "b", "a" }, read.Entries.Select(e => e.Data).ToArray());

        Apply("Pol_List_Plain", document, PolicyEdit.Enabled(("PlainList", ElementValue.FromList(ListEntry.Of("a"), ListEntry.Of("c")))));

        AssertBytes(document, delvals, RecSz(key, "a", "a"), RecSz(key, "c", "c"));
        Assert.AreEqual(PolicyRules.ListReplaceWritesDelvals, document.GetValue(key, "**delvals.") is not null);
    }

    [TestMethod]
    public void List_Replace_RemovesForeignValuesOfTheKey()
    {
        var key = Lists + @"\Plain";
        var document = Parse(RecDw(key, "number", 5), RecSz(key, "old", "old"));

        Apply("Pol_List_Plain", document, PolicyEdit.Enabled(("PlainList", ElementValue.FromList(ListEntry.Of("new")))));

        AssertBytes(document, Rec(key, "**delvals.", 1, [0x20, 0x00, 0x00, 0x00]), RecSz(key, "new", "new"));
    }

    [TestMethod]
    public void List_EmptyPrefix_NameIsTheData()
    {
        var document = ApplyTwice("Pol_List_EmptyPrefix", PolicyEdit.Enabled(("EmptyPrefixList", ElementValue.FromList(ListEntry.Of("x")))));

        AssertBytes(document, Rec(Lists + @"\EmptyPrefix", "**delvals.", 1, [0x20, 0x00, 0x00, 0x00]), RecSz(Lists + @"\EmptyPrefix", "x", "x"));
    }

    [TestMethod]
    public void List_ValuePrefix_NamesEntriesByPosition()
    {
        var document = ApplyTwice("Pol_List_ValuePrefix", PolicyEdit.Enabled(("PrefixList", ElementValue.FromList(ListEntry.Of("p"), ListEntry.Of("q")))));

        AssertBytes(
            document,
            Rec(Lists + @"\Prefix", "**delvals.", 1, [0x20, 0x00, 0x00, 0x00]),
            RecSz(Lists + @"\Prefix", "Item1", "p"),
            RecSz(Lists + @"\Prefix", "Item2", "q"));
    }

    [TestMethod]
    public void List_ExplicitValue_UsesNameAndValuePairs()
    {
        var key = Lists + @"\Explicit";
        var document = ApplyTwice(
            "Pol_List_ExplicitValue",
            PolicyEdit.Enabled(("ExplicitList", ElementValue.FromList(new ListEntry("n1", "v1"), new ListEntry("n2", "v2")))));

        AssertBytes(document, Rec(key, "**delvals.", 1, [0x20, 0x00, 0x00, 0x00]), RecSz(key, "n1", "v1"), RecSz(key, "n2", "v2"));
        var read = (ListValue)Value(Read("Pol_List_ExplicitValue", document), "ExplicitList");
        CollectionAssert.AreEqual(new[] { "n1", "n2" }, read.Entries.Select(e => e.Name).ToArray());
    }

    [TestMethod]
    public void List_Expandable_IsRegExpandSz()
    {
        var document = ApplyTwice("Pol_List_Expandable", PolicyEdit.Enabled(("ExpandList", ElementValue.FromList(ListEntry.Of("%a%")))));

        AssertBytes(document, Rec(Lists + @"\Expand", "**delvals.", 1, [0x20, 0x00, 0x00, 0x00]), Rec(Lists + @"\Expand", "%a%", 2, Sz("%a%")));
        Assert.AreEqual(1, ((ListValue)Value(Read("Pol_List_Expandable", document), "ExpandList")).Entries.Count);
    }

    [TestMethod]
    public void List_Additive_FollowsTheAdditiveRule()
    {
        var key = Lists + @"\Additive";
        var document = Parse(RecSz(key, "keep", "keep"));

        Apply("Pol_List_Additive", document, PolicyEdit.Enabled(("AdditiveList", ElementValue.FromList(ListEntry.Of("a")))));

        if (PolicyRules.AdditiveListKeepsOtherValues)
        {
            AssertBytes(document, RecSz(key, "keep", "keep"), RecSz(key, "a", "a"));
        }
        else
        {
            AssertBytes(document, Rec(key, "**delvals.", 1, [0x20, 0x00, 0x00, 0x00]), RecSz(key, "a", "a"));
        }
    }

    [TestMethod]
    public void List_UnderAPolicyValueName_WritesTheSwitchAndTheList()
    {
        var key = Lists + @"\Switched";
        var document = ApplyTwice("Pol_List_PolicyValueName", PolicyEdit.Enabled(("SwitchedList", ElementValue.FromList(ListEntry.Of("z")))));
        var delvals = Rec(key, "**delvals.", 1, [0x20, 0x00, 0x00, 0x00]);
        AssertBytes(document, RecDw(Lists, "ListSwitch", 1), delvals, RecSz(key, "z", "z"));

        Apply("Pol_List_PolicyValueName", document, PolicyEdit.Disabled);
        // Capture A/Machine_Disabled: Disabled of a plain value-name policy writes the delete instruction, not DWORD 0.
        AssertBytes(document, Del(Lists, "ListSwitch"), delvals);
        Assert.AreEqual(PolicyState.Disabled, Read("Pol_List_PolicyValueName", document).State);

        Apply("Pol_List_PolicyValueName", document, PolicyEdit.NotConfigured);
        Assert.IsTrue(document.IsEmpty);
    }

    [TestMethod]
    public void List_EnabledWithNoEntries_ClearsTheKey()
    {
        var key = Lists + @"\Switched";
        var document = Parse(RecDw(Lists, "ListSwitch", 1), RecSz(key, "z", "z"));

        Apply("Pol_List_PolicyValueName", document, PolicyEdit.Enabled(("SwitchedList", ElementValue.FromList())));

        AssertBytes(document, RecDw(Lists, "ListSwitch", 1));
    }

    [TestMethod]
    public void SharedListKey_TwoPoliciesSeeTheSameEntries_AndNotConfiguredClearsTheKey()
    {
        var key = Shared + @"\SharedList";
        var document = ApplyTwice("Pol_SharedList_One", PolicyEdit.Enabled(("SharedListOne", ElementValue.FromList(ListEntry.Of("s")))));
        AssertBytes(document, Rec(key, "**delvals.", 1, [0x20, 0x00, 0x00, 0x00]), RecSz(key, "s", "s"));

        Assert.AreEqual(PolicyState.Enabled, Read("Pol_SharedList_Two", document).State);

        Apply("Pol_SharedList_Two", document, PolicyEdit.NotConfigured);

        Assert.IsTrue(document.IsEmpty);
        Assert.AreEqual(PolicyState.NotConfigured, Read("Pol_SharedList_One", document).State);
    }

    [TestMethod]
    public void EnabledListOnly_WritesItemsInTheirKeys_AndDisabledWritesDeleteInstructions()
    {
        var def = Basics + @"\EnabledListKey";
        var over = Basics + @"\ItemKeyOverride";
        var document = ApplyTwice("Pol_Value_EnabledListOnly", PolicyEdit.Enabled());
        AssertBytes(document, RecDw(def, "ListItemOne", 1), RecDw(def, "ListItemThree", 3), RecSz(over, "ListItemTwo", "two"));
        Assert.AreEqual(PolicyState.Enabled, Read("Pol_Value_EnabledListOnly", document).State);

        Apply("Pol_Value_EnabledListOnly", document, PolicyEdit.Disabled);

        if (PolicyRules.DisabledClearsElementsWithDelete)
        {
            AssertBytes(document, Del(def, "ListItemOne"), Del(def, "ListItemThree"), Del(over, "ListItemTwo"));
            Assert.AreEqual(PolicyState.Disabled, Read("Pol_Value_EnabledListOnly", document).State);
        }

        Apply("Pol_Value_EnabledListOnly", document, PolicyEdit.NotConfigured);
        Assert.IsTrue(document.IsEmpty);
    }

    [TestMethod]
    public void DisabledListOnly_WritesItsItemsWhenDisabled_AndEnabledRemovesThem()
    {
        var def = Basics + @"\DisabledListKey";
        var over = Basics + @"\ItemKeyOverride";
        var document = ApplyTwice("Pol_Value_DisabledListOnly", PolicyEdit.Disabled);
        AssertBytes(document, RecDw(def, "OffItemOne", 0), RecSz(over, "OffItemTwo", "zero"));
        Assert.AreEqual(PolicyState.Disabled, Read("Pol_Value_DisabledListOnly", document).State);

        Apply("Pol_Value_DisabledListOnly", document, PolicyEdit.Enabled());

        Assert.IsTrue(document.IsEmpty);
    }

    [TestMethod]
    public void BothLists_WithAValueName_SwitchBetweenTheTwoLists()
    {
        var other = Basics + @"\BothListKey";
        var document = ApplyTwice("Pol_Value_BothLists", PolicyEdit.Enabled());
        AssertBytes(document, RecDw(Basics, "ListMaster", 1), RecDw(Basics, "BothListOn", 1), RecDw(other, "BothListOnOther", 11));

        Apply("Pol_Value_BothLists", document, PolicyEdit.Disabled);

        var disabled = Read("Pol_Value_BothLists", document);
        Assert.AreEqual(PolicyState.Disabled, disabled.State);
        Assert.IsNotNull(document.GetValue(Basics, "BothListOff"));
        Assert.IsNull(document.GetValue(Basics, "BothListOn"));
        // Capture A/Machine_Disabled: the switch is deleted with an instruction, not set to DWORD 0.
        Assert.IsNull(document.GetValue(Basics, "ListMaster"));
        Assert.IsNotNull(document.GetValue(Basics, "**del.ListMaster"));
    }
}

using System.Text.Json.Nodes;
using Bwc.Policy.Admx;
using Bwc.Policy.Model;

namespace Bwc.Policy.Tests.Golden;

/// <summary>
/// The option mapping of the replay against itself (docs/failure-modes/policy-defaults.md, section O): every kind
/// of element maps from its display value, and every value it cannot represent fails with a message. None of these
/// calls <see cref="PolicySave"/> or <see cref="PolicyDefaults"/>.
/// </summary>
[TestClass]
public sealed class GoldenOptionsTests
{
    private static readonly TemplateSet Fixtures = Fixture.Load();

    private static PolicyDefinition P(string name) => Fixtures.Policy(name);

    private static JsonArray Opts(string json) => (JsonArray)JsonNode.Parse(json)!;

    private static GoldenOptions.Mapped Map(PolicyDefinition policy, string json, IReadOnlyDictionary<string, ElementValue>? stored = null) =>
        GoldenOptions.Map("X/Case", 7, policy, Opts(json), stored);

    private static void Fails(PolicyDefinition policy, string json, string expectedText, IReadOnlyDictionary<string, ElementValue>? stored = null)
    {
        var ex = Assert.Throws<AssertFailedException>(() => Map(policy, json, stored));
        StringAssert.Contains(ex.Message, expectedText);
        StringAssert.Contains(ex.Message, "X/Case operation 7");
    }

    private static IReadOnlyDictionary<string, ElementValue> Stored(string id, ElementValue value) =>
        new Dictionary<string, ElementValue>(StringComparer.OrdinalIgnoreCase) { [id] = value };

    [TestMethod]
    public void Maps_EveryKindOfElement_FromItsDisplayValue()
    {
        var policy = P("Pol_Amp_LabelSingle");

        var mapped = Map(
            policy,
            """
            [{"label":"Name & address:","expectKind":"textBox","text":"bwc-alt","elementId":"AmpText"},
             {"label":"Save & close","expectKind":"checkBox","checked":true,"elementId":"AmpCheck"},
             {"label":"Rock & roll count:","expectKind":"decimalTextBox","text":"77","elementId":"AmpNumber"},
             {"label":"Pick & choose:","expectKind":"dropdownList","item":"Tom & Jerry","elementId":"AmpChoice"}]
            """);

        Assert.IsNull(mapped.DialogRefusal);
        Assert.AreEqual(ElementValue.FromText("bwc-alt"), mapped.Values["AmpText"]);
        Assert.AreEqual(ElementValue.FromBoolean(true), mapped.Values["AmpCheck"]);
        Assert.AreEqual(ElementValue.FromNumber(77), mapped.Values["AmpNumber"]);
        var choice = P("Pol_Amp_LabelSingle").Elements.OfType<EnumElement>().Single();
        Assert.AreEqual(ElementValue.FromChoice(choice.Items.ToList().FindIndex(i => i.DisplayName == "Tom & Jerry")), mapped.Values["AmpChoice"]);
    }

    [TestMethod]
    public void Maps_ALongNumberFullWidth_AComboItemAndMultiTextLines()
    {
        var longNumber = Map(P("Pol_LongDecimal_Basic"), """[{"expectKind":"longDecimalTextBox","text":"18446744073709551615","elementId":"LongNumber"}]""");
        var combo = Map(P("Pol_Text_ComboBoxNoSortRequired"), """[{"expectKind":"comboBox","item":"Small","elementId":"ComboNoSortText"}]""");
        var typed = Map(P("Pol_Text_ComboBoxNoSortRequired"), """[{"expectKind":"comboBox","text":"my own","elementId":"ComboNoSortText"}]""");
        var lines = Map(P("Pol_MultiText_Basic"), """[{"index":0,"expectKind":"multiText","lines":["alpha","beta"],"elementId":"MultiLines"}]""");
        var empty = Map(P("Pol_MultiText_Basic"), """[{"index":0,"expectKind":"multiText","lines":[""],"elementId":"MultiLines"}]""");

        Assert.AreEqual(ElementValue.FromNumber(ulong.MaxValue), longNumber.Values["LongNumber"]);
        Assert.AreEqual(ElementValue.FromText("Small"), combo.Values["ComboNoSortText"]);
        Assert.AreEqual(ElementValue.FromText("my own"), typed.Values["ComboNoSortText"]);
        CollectionAssert.AreEqual(new[] { "alpha", "beta" }, ((MultiTextValue)lines.Values["MultiLines"]).Lines.ToArray());
        CollectionAssert.AreEqual(new[] { string.Empty }, ((MultiTextValue)empty.Values["MultiLines"]).Lines.ToArray());
    }

    [TestMethod]
    public void O1_AnOptionWithNoElementIdOrAnUnknownOne_Fails()
    {
        Fails(P("Pol_Decimal_MinMax"), """[{"expectKind":"decimalTextBox","text":"1"}]""", "no elementId");
        Fails(P("Pol_Decimal_MinMax"), """[{"expectKind":"decimalTextBox","text":"1","elementId":"Nope"}]""", "no element 'Nope'");
    }

    [TestMethod]
    public void O2_AnExpectKindThatIsNotTheKindOfTheControl_Fails()
    {
        Fails(P("Pol_Text_ComboBoxNoSortRequired"), """[{"expectKind":"textBox","text":"a","elementId":"ComboNoSortText"}]""", "expectKind 'textBox'");
        Fails(P("Pol_Decimal_MinMax"), """[{"expectKind":"longDecimalTextBox","text":"1","elementId":"MinMaxNumber"}]""", "expectKind 'longDecimalTextBox'");
        Fails(P("Pol_Bool_StringValues"), """[{"expectKind":"decimalTextBox","checked":true,"elementId":"StringCheck"}]""", "expectKind 'decimalTextBox'");
        Assert.IsNull(Map(P("Pol_Decimal_MinMax"), """[{"expectKind":"spinner","text":"20","elementId":"MinMaxNumber"}]""").DialogRefusal);
    }

    [TestMethod]
    public void O3_AnOptionWithNoValueMemberOrTwoOfThem_Fails()
    {
        Fails(P("Pol_Decimal_MinMax"), """[{"expectKind":"decimalTextBox","elementId":"MinMaxNumber"}]""", "exactly one value member and has 0");
        Fails(P("Pol_Decimal_MinMax"), """[{"expectKind":"decimalTextBox","text":"1","item":"x","elementId":"MinMaxNumber"}]""", "exactly one value member and has 2");
        Fails(P("Pol_Bool_StringValues"), """[{"expectKind":"checkBox","text":"yes","elementId":"StringCheck"}]""", "holds 'text'");
        Fails(P("Pol_Bool_StringValues"), """[{"expectKind":"checkBox","checked":"yes","elementId":"StringCheck"}]""", "boolean checked");
    }

    [TestMethod]
    public void O4_AnUnknownMember_Fails()
    {
        Fails(P("Pol_Decimal_MinMax"), """[{"expectKind":"decimalTextBox","text":"1","elementId":"MinMaxNumber","pasted":true}]""", "unknown member 'pasted'");
        Assert.IsNull(Map(P("Pol_Decimal_MinMax"), """[{"label":"x","expectKind":"decimalTextBox","text":"1","typed":true,"index":0,"elementId":"MinMaxNumber"}]""").DialogRefusal);
    }

    [TestMethod]
    public void O5_AnEnumLabelThatMatchesNoItem_OrMatchesByAnotherCase_Fails()
    {
        Fails(P("Pol_Enum_StringItems"), """[{"expectKind":"dropdownList","item":"Purple","elementId":"StringChoice"}]""", "'Purple' is not an item label");
        Fails(P("Pol_Enum_StringItems"), """[{"expectKind":"dropdownList","item":"red","elementId":"StringChoice"}]""", "'red' is not an item label");
        Assert.AreEqual(
            ElementValue.FromChoice(P("Pol_Enum_StringItems").Elements.OfType<EnumElement>().Single().Items.ToList().FindIndex(i => i.DisplayName == "Red")),
            Map(P("Pol_Enum_StringItems"), """[{"expectKind":"dropdownList","item":"Red","elementId":"StringChoice"}]""").Values["StringChoice"]);
    }

    [TestMethod]
    public void O5_AnEnumLabelThatNamesTwoItems_IsAmbiguousAndFails()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "Twins",
            "T.Twins",
            """
            <policy name="P" class="User" displayName="$(string.P)" key="K" presentation="$(presentation.P)">
              <parentCategory ref="Root" /><supportedOn ref="SUP" />
              <elements><enum id="E" valueName="V">
                <item displayName="$(string.Same)"><value><decimal value="1" /></value></item>
                <item displayName="$(string.Same)"><value><decimal value="2" /></value></item>
              </enum></elements>
            </policy>
            """,
            """<string id="P">P</string><string id="Same">Same</string>""",
            presentations: """<presentation id="P"><dropdownList refId="E">Pick</dropdownList></presentation>""");
        var policy = temp.Load().Policies.Single();
        Assert.AreEqual(2, policy.Elements.OfType<EnumElement>().Single().Items.Count, "the loader keeps both items");

        Fails(policy, """[{"expectKind":"dropdownList","item":"Same","elementId":"E"}]""", "names 2 items");
    }

    [TestMethod]
    public void O6_ItemIndex_IsNotSupported()
    {
        Fails(P("Pol_Enum_StringItems"), """[{"expectKind":"dropdownList","itemIndex":1,"elementId":"StringChoice"}]""", "itemIndex");
    }

    [TestMethod]
    public void O7_ANumberThatIsNotDigits_IsARefusalOfTheDialog_AndLeadingZerosAreANumber()
    {
        foreach (var text in new[] { "-5", "12a", string.Empty, " 5", "1,000", "99999999999999999999999" })
        {
            var mapped = Map(P("Pol_Decimal_MinMax"), $$"""[{"expectKind":"decimalTextBox","text":"{{text}}","elementId":"MinMaxNumber"}]""");

            Assert.IsNotNull(mapped.DialogRefusal, $"'{text}' must be a refusal");
            Assert.IsFalse(mapped.Values.ContainsKey("MinMaxNumber"), $"'{text}' must not be mapped");
        }

        var padded = Map(P("Pol_Decimal_MinMax"), """[{"expectKind":"decimalTextBox","text":"007","elementId":"MinMaxNumber"}]""");
        Assert.AreEqual(ElementValue.FromNumber(7), padded.Values["MinMaxNumber"]);
    }

    [TestMethod]
    public void O8_ADecimalAboveTheThirtyTwoBitRange_KeepsTheNumber_AndTheValidatorFindsIt()
    {
        var mapped = Map(P("Pol_Decimal_MinMax"), """[{"expectKind":"decimalTextBox","text":"4294967296","elementId":"MinMaxNumber"}]""");

        Assert.AreEqual(ElementValue.FromNumber(4294967296UL), mapped.Values["MinMaxNumber"]);
    }

    [TestMethod]
    public void O9_AComboItemThatIsNoSuggestion_OrAnItemForATextBox_Fails()
    {
        Fails(P("Pol_Text_ComboBoxNoSortRequired"), """[{"expectKind":"comboBox","item":"Gigantic","elementId":"ComboNoSortText"}]""", "not a suggestion");
        Fails(P("Pol_Text_Plain"), """[{"expectKind":"textBox","item":"x","elementId":"PlainText"}]""", "holds 'item'");
    }

    [TestMethod]
    public void O11_MultiTextLines_MustBeAnArrayOfStrings()
    {
        Fails(P("Pol_MultiText_Basic"), """[{"expectKind":"multiText","lines":"a\nb","elementId":"MultiLines"}]""", "not an array");
        Fails(P("Pol_MultiText_Basic"), """[{"expectKind":"multiText","lines":["a",1],"elementId":"MultiLines"}]""", "not a string");
    }

    [TestMethod]
    public void O12_AListThatIsNotCleared_AddsToTheRowsShown_AndAClearedListStartsEmpty()
    {
        var policy = P("Pol_List_Plain");
        var stored = Stored("PlainList", ElementValue.FromList(ListEntry.Of("old-1"), ListEntry.Of("old-2")));

        var added = Map(policy, """[{"expectKind":"list","rows":[["new"]],"elementId":"PlainList"}]""", stored);
        var replaced = Map(policy, """[{"expectKind":"list","rows":[["new"]],"clearRows":true,"elementId":"PlainList"}]""", stored);
        var cleared = Map(policy, """[{"expectKind":"list","clearRows":true,"elementId":"PlainList"}]""", stored);
        var fresh = Map(policy, """[{"expectKind":"list","rows":[["a"],["b"]],"clearRows":true,"elementId":"PlainList"}]""");

        CollectionAssert.AreEqual(new[] { "old-1", "old-2", "new" }, ((ListValue)added.Values["PlainList"]).Entries.Select(e => e.Data).ToArray());
        CollectionAssert.AreEqual(new[] { "new" }, ((ListValue)replaced.Values["PlainList"]).Entries.Select(e => e.Data).ToArray());
        Assert.AreEqual(0, ((ListValue)cleared.Values["PlainList"]).Entries.Count);
        CollectionAssert.AreEqual(new[] { "a", "b" }, ((ListValue)fresh.Values["PlainList"]).Entries.Select(e => e.Data).ToArray());
    }

    [TestMethod]
    public void O12_AnExplicitValueListMapsNameAndValueRows()
    {
        var mapped = Map(P("Pol_List_ExplicitValue"), """[{"expectKind":"list","rows":[["name1","value1"],["name2","value2"]],"clearRows":true,"elementId":"ExplicitList"}]""");

        var entries = ((ListValue)mapped.Values["ExplicitList"]).Entries;
        Assert.AreEqual(new ListEntry("name1", "value1"), entries[0]);
        Assert.AreEqual(new ListEntry("name2", "value2"), entries[1]);
    }

    [TestMethod]
    public void O13_ARowWithTheWrongNumberOfCells_Fails()
    {
        Fails(P("Pol_List_ExplicitValue"), """[{"expectKind":"list","rows":[["only-a-name"]],"clearRows":true,"elementId":"ExplicitList"}]""", "needs a name and a value");
        Fails(P("Pol_List_Plain"), """[{"expectKind":"list","rows":[["a","b"]],"clearRows":true,"elementId":"PlainList"}]""", "needs one cell");
        Fails(P("Pol_List_Plain"), """[{"expectKind":"list","rows":["a"],"clearRows":true,"elementId":"PlainList"}]""", "not an array");
    }

    [TestMethod]
    public void O14_AnAdditiveList_ListsTheStoredRowsThatAreNoLongerThere()
    {
        var stored = Stored("AdditiveList", ElementValue.FromList(ListEntry.Of("keep"), ListEntry.Of("drop")));

        var mapped = Map(P("Pol_List_Additive"), """[{"expectKind":"list","rows":[["keep"],["added"]],"clearRows":true,"elementId":"AdditiveList"}]""", stored);

        var value = (ListValue)mapped.Values["AdditiveList"];
        CollectionAssert.AreEqual(new[] { "keep", "added" }, value.Entries.Select(e => e.Data).ToArray());
        CollectionAssert.AreEqual(new[] { "drop" }, value.Removed.Select(e => e.Data).ToArray());
    }

    [TestMethod]
    public void O14_AnExplicitListMatchesRemovedRowsByName_IgnoringCase()
    {
        var stored = Stored("ExplicitList", ElementValue.FromList(new ListEntry("Name1", "v1"), new ListEntry("name2", "v2")));

        var mapped = Map(P("Pol_List_ExplicitValue"), """[{"expectKind":"list","rows":[["NAME1","changed"]],"clearRows":true,"elementId":"ExplicitList"}]""", stored);

        CollectionAssert.AreEqual(new[] { "name2" }, ((ListValue)mapped.Values["ExplicitList"]).Removed.Select(e => e.Name).ToArray());
    }

    [TestMethod]
    public void O15_TwoOptionsForOneElement_Fail()
    {
        Fails(
            P("Pol_Decimal_MinMax"),
            """[{"expectKind":"decimalTextBox","text":"1","elementId":"MinMaxNumber"},{"expectKind":"decimalTextBox","text":"2","elementId":"MinMaxNumber"}]""",
            "two options name the element 'MinMaxNumber'");
    }

    [TestMethod]
    public void O16_AnElementWithNoControl_Fails()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "NoControl",
            "T.NoControl",
            """<policy name="P" class="User" displayName="$(string.P)" key="K"><parentCategory ref="Root" /><supportedOn ref="SUP" /><elements><decimal id="N" valueName="N" /></elements></policy>""",
            """<string id="P">P</string>""");
        var policy = temp.Load().Policies.Single();
        Assert.AreEqual(1, policy.Elements.Count);

        Fails(policy, """[{"expectKind":"decimalTextBox","text":"1","elementId":"N"}]""", "no control in the presentation");
    }

    [TestMethod]
    public void O17_OptionsWithADisablingState_Fail()
    {
        var options = Opts("""[{"expectKind":"decimalTextBox","text":"1","elementId":"MinMaxNumber"}]""");

        Assert.Throws<AssertFailedException>(() => GoldenOptions.RequireEnabledForOptions("X/Case", 7, "Disabled", options));
        Assert.Throws<AssertFailedException>(() => GoldenOptions.RequireEnabledForOptions("X/Case", 7, "Not Configured", options));
        GoldenOptions.RequireEnabledForOptions("X/Case", 7, "Enabled", options);
        GoldenOptions.RequireEnabledForOptions("X/Case", 7, null, null);
        GoldenOptions.RequireEnabledForOptions("X/Case", 7, "Disabled", null);
    }

    [TestMethod]
    public void NoOptions_MapNothing()
    {
        var mapped = GoldenOptions.Map("X/Case", 7, P("Pol_Decimal_MinMax"), null);

        Assert.AreEqual(0, mapped.Values.Count);
        Assert.IsNull(mapped.DialogRefusal);
    }
}

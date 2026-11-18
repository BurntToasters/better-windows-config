using Bwc.Policy.Admx;

namespace Bwc.Policy.Tests;

[TestClass]
public sealed class AdmxParserTests
{
    private static TemplateSet fixture = null!;

    [ClassInitialize]
    public static void Init(TestContext context) => fixture = Fixture.Load();

    [TestMethod]
    public void Policy_ClassKeyAndValueName_AreRecordedAsWritten()
    {
        var machine = fixture.Policy("Pol_Class_Machine");
        Assert.AreEqual(PolicyClass.Machine, machine.Class);
        Assert.AreEqual(@"Software\Policies\BWC-Oracle\Basics", machine.Key);
        Assert.AreEqual("MachineFlag", machine.ValueName);
        Assert.IsTrue(machine.AppliesToMachine);
        Assert.IsFalse(machine.AppliesToUser);

        var user = fixture.Policy("Pol_Class_User");
        Assert.AreEqual(PolicyClass.User, user.Class);
        Assert.IsFalse(user.AppliesToMachine);
        Assert.IsTrue(user.AppliesToUser);
    }

    [TestMethod]
    public void Policy_ClassBoth_AppearsOnceAndAppliesToBothRoots()
    {
        var both = fixture.Policy("Pol_Class_Both");
        Assert.AreEqual(PolicyClass.Both, both.Class);
        Assert.IsTrue(both.AppliesToMachine);
        Assert.IsTrue(both.AppliesToUser);
        Assert.AreEqual(1, fixture.Policies.Count(p => p.Name == "Pol_Class_Both"));
        Assert.AreEqual(1, both.Category!.Policies.Count(p => p.Name == "Pol_Class_Both"));
    }

    [TestMethod]
    public void Policy_WithoutValueName_HasNullValueName()
    {
        Assert.IsNull(fixture.Policy("Pol_Value_NoValueName_ElementsOnly").ValueName);
        Assert.IsNull(fixture.Policy("Pol_Value_EnabledListOnly").ValueName);
        Assert.IsNull(fixture.Policy("Pol_Value_DisabledListOnly").ValueName);
    }

    [TestMethod]
    public void Policy_EnabledAndDisabledValues_DecimalAndString()
    {
        var number = fixture.Policy("Pol_Value_Decimal");
        Assert.AreEqual(PolicyValueKind.Decimal, number.EnabledValue!.Kind);
        Assert.AreEqual(5UL, number.EnabledValue.Number);
        Assert.AreEqual(9UL, number.DisabledValue!.Number);

        var text = fixture.Policy("Pol_Value_String");
        Assert.AreEqual(PolicyValueKind.String, text.EnabledValue!.Kind);
        Assert.AreEqual("on", text.EnabledValue.Text);
        Assert.AreEqual("off", text.DisabledValue!.Text);
    }

    [TestMethod]
    public void Policy_OnlyOneValueGiven_TheOtherIsNull()
    {
        var enabledOnly = fixture.Policy("Pol_Value_EnabledOnly");
        Assert.AreEqual(7UL, enabledOnly.EnabledValue!.Number);
        Assert.IsNull(enabledOnly.DisabledValue);

        var disabledOnly = fixture.Policy("Pol_Value_DisabledOnly");
        Assert.IsNull(disabledOnly.EnabledValue);
        Assert.AreEqual(3UL, disabledOnly.DisabledValue!.Number);
    }

    [TestMethod]
    public void Policy_EnabledList_ItemKeyOverridesDefaultKey()
    {
        var list = fixture.Policy("Pol_Value_EnabledListOnly").EnabledList!;
        Assert.AreEqual(@"Software\Policies\BWC-Oracle\Basics\EnabledListKey", list.DefaultKey);
        Assert.AreEqual(3, list.Items.Count);
        Assert.AreEqual("ListItemOne", list.Items[0].ValueName);
        Assert.IsNull(list.Items[0].Key);
        Assert.AreEqual(@"Software\Policies\BWC-Oracle\Basics\EnabledListKey", list.Items[0].EffectiveKey);
        Assert.AreEqual(@"Software\Policies\BWC-Oracle\Basics\ItemKeyOverride", list.Items[1].Key);
        Assert.AreEqual(@"Software\Policies\BWC-Oracle\Basics\ItemKeyOverride", list.Items[1].EffectiveKey);
        Assert.AreEqual("two", list.Items[1].Value!.Text);
        Assert.AreEqual(3UL, list.Items[2].Value!.Number);
    }

    [TestMethod]
    public void Policy_DisabledListAndBothLists_AreRead()
    {
        var disabled = fixture.Policy("Pol_Value_DisabledListOnly").DisabledList!;
        Assert.AreEqual(2, disabled.Items.Count);
        Assert.IsNull(fixture.Policy("Pol_Value_DisabledListOnly").EnabledList);

        var both = fixture.Policy("Pol_Value_BothLists");
        Assert.AreEqual("ListMaster", both.ValueName);
        Assert.AreEqual(2, both.EnabledList!.Items.Count);
        Assert.AreEqual(1, both.DisabledList!.Items.Count);
        Assert.AreEqual(@"Software\Policies\BWC-Oracle\Basics", both.EnabledList.DefaultKey);
        Assert.AreEqual(both.Key, both.EnabledList.Items[0].EffectiveKey);
        Assert.AreEqual(@"Software\Policies\BWC-Oracle\Basics\BothListKey", both.EnabledList.Items[1].EffectiveKey);
    }

    [TestMethod]
    public void Policy_KeyOutsidePolicyRoots_IsKeptAsWritten()
    {
        Assert.AreEqual(@"Software\BWC-Oracle-Gpedit", fixture.Policy("Pol_Outside_KeyOutsideRoots").Key);
    }

    [TestMethod]
    public void Element_KeyOverride_ReplacesPolicyKeyForThatElement()
    {
        var policy = fixture.Policy("Pol_Element_KeyOverride");
        var text = policy.Element<TextElement>("OverrideText");
        Assert.AreEqual(@"Software\Policies\BWC-Oracle\Basics\ElementKey", text.Key);
        Assert.AreEqual(@"Software\Policies\BWC-Oracle\Basics\ElementKey", text.EffectiveKey);
        Assert.AreEqual(@"Software\Policies\BWC-Oracle\Basics", policy.Key);
        Assert.AreEqual("OverrideText", text.ValueName);

        var plain = fixture.Policy("Pol_Text_Plain").Element<TextElement>("PlainText");
        Assert.IsNull(plain.Key);
        Assert.AreEqual(fixture.Policy("Pol_Text_Plain").Key, plain.EffectiveKey);
    }

    [TestMethod]
    public void Element_Boolean_ValuesAndLists()
    {
        var values = fixture.Policy("Pol_Bool_TrueFalseValues").Element<BooleanElement>("TrueFalseCheck");
        Assert.AreEqual(10UL, values.TrueValue!.Number);
        Assert.AreEqual(20UL, values.FalseValue!.Number);

        var strings = fixture.Policy("Pol_Bool_StringValues").Element<BooleanElement>("StringCheck");
        Assert.AreEqual("yes", strings.TrueValue!.Text);
        Assert.AreEqual("no", strings.FalseValue!.Text);

        var lists = fixture.Policy("Pol_Bool_TrueFalseLists").Element<BooleanElement>("ListCheck");
        Assert.AreEqual("ListCheck", lists.ValueName);
        Assert.IsNull(lists.TrueValue);
        Assert.AreEqual(2, lists.TrueList!.Items.Count);
        Assert.AreEqual(2, lists.FalseList!.Items.Count);
        Assert.AreEqual(@"Software\Policies\BWC-Oracle\Elements\Bool\TrueListOtherKey", lists.TrueList.Items[1].EffectiveKey);
        Assert.AreEqual(@"Software\Policies\BWC-Oracle\Elements\Bool\FalseListKey", lists.FalseList.Items[1].EffectiveKey);

        var noFalse = fixture.Policy("Pol_Bool_NoFalseValue").Element<BooleanElement>("NoFalseCheck");
        Assert.IsNull(noFalse.FalseValue);
        Assert.AreEqual(1UL, noFalse.TrueValue!.Number);
    }

    [TestMethod]
    public void Element_Boolean_KeepsDocumentOrderNotNameOrder()
    {
        var elements = fixture.Policy("Pol_Bool_TwoChecksReverseOrder").Elements;
        CollectionAssert.AreEqual(new[] { "ZetaCheck", "AlphaCheck" }, elements.Select(e => e.Id).ToArray());

        var shared = fixture.Policy("Pol_Shared_ElementsReverseOrder").Elements;
        CollectionAssert.AreEqual(new[] { "ZetaElement", "AlphaElement" }, shared.Select(e => e.Id).ToArray());
        Assert.AreEqual(PolicyElementKind.Text, shared[0].Kind);
        Assert.AreEqual(PolicyElementKind.Decimal, shared[1].Kind);
    }

    [TestMethod]
    public void Element_Decimal_RangeRequiredSoftAndStoreAsText()
    {
        var minMax = fixture.Policy("Pol_Decimal_MinMax").Element<DecimalElement>("MinMaxNumber");
        Assert.AreEqual(PolicyElementKind.Decimal, minMax.Kind);
        Assert.AreEqual(10UL, minMax.MinValue);
        Assert.AreEqual(500UL, minMax.MaxValue);
        Assert.IsFalse(minMax.Required);
        Assert.IsFalse(minMax.Soft);
        Assert.IsFalse(minMax.StoreAsText);

        var required = fixture.Policy("Pol_Decimal_Required").Element<DecimalElement>("RequiredNumber");
        Assert.IsTrue(required.Required);
        Assert.IsNull(required.MinValue);
        Assert.IsNull(required.MaxValue);

        Assert.IsTrue(fixture.Policy("Pol_Decimal_Soft").Element<DecimalElement>("SoftNumber").Soft);

        var asText = fixture.Policy("Pol_Decimal_StoreAsText").Element<DecimalElement>("TextNumber");
        Assert.IsTrue(asText.StoreAsText);
        Assert.AreEqual(65535UL, asText.MaxValue);
    }

    [TestMethod]
    public void Element_LongDecimal_IsItsOwnKindWithAllFlags()
    {
        var basic = fixture.Policy("Pol_LongDecimal_Basic").Element<DecimalElement>("LongNumber");
        Assert.AreEqual(PolicyElementKind.LongDecimal, basic.Kind);
        Assert.AreEqual(9000000000000UL, basic.MaxValue);

        var flags = fixture.Policy("Pol_LongDecimal_RequiredSoftText").Element<DecimalElement>("LongTextNumber");
        Assert.IsTrue(flags.Required);
        Assert.IsTrue(flags.Soft);
        Assert.IsTrue(flags.StoreAsText);
    }

    [TestMethod]
    public void Element_Text_ExpandableMaxLengthRequired()
    {
        Assert.IsTrue(fixture.Policy("Pol_Text_Expandable").Element<TextElement>("ExpandText").Expandable);
        Assert.IsFalse(fixture.Policy("Pol_Text_Plain").Element<TextElement>("PlainText").Expandable);
        Assert.AreEqual(12UL, fixture.Policy("Pol_Text_MaxLength").Element<TextElement>("ShortText").MaxLength);
        Assert.IsNull(fixture.Policy("Pol_Text_Plain").Element<TextElement>("PlainText").MaxLength);
        Assert.IsTrue(fixture.Policy("Pol_Text_Required").Element<TextElement>("RequiredText").Required);
    }

    [TestMethod]
    public void Element_MultiText_LimitsAreRead()
    {
        var limits = fixture.Policy("Pol_MultiText_Limits").Element<MultiTextElement>("LimitedLines");
        Assert.IsTrue(limits.Required);
        Assert.AreEqual(20UL, limits.MaxLength);
        Assert.AreEqual(3UL, limits.MaxStrings);

        var basic = fixture.Policy("Pol_MultiText_Basic").Element<MultiTextElement>("MultiLines");
        Assert.IsNull(basic.MaxStrings);
    }

    [TestMethod]
    public void Element_Enum_ItemsKeepOrderAndValues()
    {
        var numbers = fixture.Policy("Pol_Enum_DecimalItems").Element<EnumElement>("DecimalChoice");
        CollectionAssert.AreEqual(new[] { "Zero", "One", "Two", "Three" }, numbers.Items.Select(i => i.DisplayName).ToArray());
        CollectionAssert.AreEqual(new ulong[] { 0, 1, 2, 3 }, numbers.Items.Select(i => i.Value!.Number).ToArray());

        var colors = fixture.Policy("Pol_Enum_StringItems").Element<EnumElement>("StringChoice");
        CollectionAssert.AreEqual(new[] { "red", "green", "blue" }, colors.Items.Select(i => i.Value!.Text).ToArray());
        Assert.IsTrue(fixture.Policy("Pol_Enum_Required").Element<EnumElement>("RequiredChoice").Required);
    }

    [TestMethod]
    public void Element_EnumItemValueList_DefaultKeyAndKeyOverride()
    {
        var profile = fixture.Policy("Pol_Enum_ItemValueList").Element<EnumElement>("ProfileChoice");
        Assert.AreEqual(3, profile.Items.Count);
        var a = profile.Items[0].ValueList!;
        Assert.AreEqual(@"Software\Policies\BWC-Oracle\Elements\Choice\ProfileA", a.DefaultKey);
        Assert.AreEqual(2, a.Items.Count);
        Assert.AreEqual(100UL, a.Items[0].Value!.Number);
        Assert.AreEqual(@"Software\Policies\BWC-Oracle\Elements\Choice\ProfileAOther", a.Items[1].EffectiveKey);
        Assert.IsNull(profile.Items[1].ValueList);
        var c = profile.Items[2].ValueList!;
        Assert.IsNull(c.DefaultKey);
        Assert.AreEqual(profile.EffectiveKey, c.Items[0].EffectiveKey);
    }

    [TestMethod]
    public void Element_List_AttributesAndValuePrefixAbsentVersusEmpty()
    {
        var plain = fixture.Policy("Pol_List_Plain").Element<ListElement>("PlainList");
        Assert.IsFalse(plain.Additive);
        Assert.IsFalse(plain.ExplicitValue);
        Assert.IsFalse(plain.Expandable);
        Assert.IsNull(plain.ValuePrefix);
        Assert.AreEqual(@"Software\Policies\BWC-Oracle\Lists\Plain", plain.Key);

        Assert.AreEqual(string.Empty, fixture.Policy("Pol_List_EmptyPrefix").Element<ListElement>("EmptyPrefixList").ValuePrefix);
        Assert.IsTrue(fixture.Policy("Pol_List_Additive").Element<ListElement>("AdditiveList").Additive);
        Assert.IsTrue(fixture.Policy("Pol_List_ExplicitValue").Element<ListElement>("ExplicitList").ExplicitValue);
        Assert.AreEqual("Item", fixture.Policy("Pol_List_ValuePrefix").Element<ListElement>("PrefixList").ValuePrefix);
        Assert.IsTrue(fixture.Policy("Pol_List_Expandable").Element<ListElement>("ExpandList").Expandable);

        var switched = fixture.Policy("Pol_List_PolicyValueName");
        Assert.AreEqual("ListSwitch", switched.ValueName);
        Assert.AreEqual(@"Software\Policies\BWC-Oracle\Lists\Switched", switched.Element<ListElement>("SwitchedList").EffectiveKey);
    }

    [TestMethod]
    public void Elements_TotalsByKind_MatchTheFixture()
    {
        var all = fixture.Policies.SelectMany(p => p.Elements).ToList();
        Assert.AreEqual(45, all.Count);
        Assert.AreEqual(7, all.Count(e => e.Kind == PolicyElementKind.Boolean));
        Assert.AreEqual(8, all.Count(e => e.Kind == PolicyElementKind.Decimal));
        Assert.AreEqual(2, all.Count(e => e.Kind == PolicyElementKind.LongDecimal));
        Assert.AreEqual(11, all.Count(e => e.Kind == PolicyElementKind.Text));
        Assert.AreEqual(2, all.Count(e => e.Kind == PolicyElementKind.MultiText));
        Assert.AreEqual(6, all.Count(e => e.Kind == PolicyElementKind.Enum));
        Assert.AreEqual(9, all.Count(e => e.Kind == PolicyElementKind.List));
        Assert.AreEqual(39, fixture.Policies.Count(p => p.Elements.Count > 0));
    }

    [TestMethod]
    public void Parser_UnknownElementsAndAttributes_AreIgnoredWithoutFailure()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "Odd",
            "T.Odd",
            """
            <policy name="P" class="User" displayName="$(string.P)" key="K\A" valueName="V" futureAttribute="x" presentation="$(presentation.P)">
              <parentCategory ref="Root" />
              <supportedOn ref="SUP" />
              <futureChild><nested /></futureChild>
              <elements>
                <futureKind id="F" valueName="F" />
                <decimal id="D" valueName="D" oSort="true" defaultItem="3" />
              </elements>
            </policy>
            """,
            extraStrings: """<string id="P">P</string>""",
            presentations: """<presentation id="P"><decimalTextBox refId="D" newAttr="1">Label</decimalTextBox></presentation>""");
        var set = temp.Load();
        FailureAssert.None(set);
        var policy = set.FindPolicy("T.Odd", "P")!;
        Assert.AreEqual(1, policy.Elements.Count);
        Assert.AreEqual("D", policy.Elements[0].Id);
    }

    [TestMethod]
    public void Parser_PolicyWithoutNamespaceDeclaration_StillLoads()
    {
        using var temp = new TempTemplates();
        temp.AddSimple("NoNs", "T.NoNs", """<policy name="P" class="Machine" displayName="$(string.P)" key="K" valueName="V"><parentCategory ref="Root" /><supportedOn ref="SUP" /></policy>""", """<string id="P">P</string>""");
        var set = temp.Load();
        FailureAssert.None(set);
        Assert.AreEqual(1, set.Policies.Count);
    }

    [TestMethod]
    public void Parser_PolicyMissingKeyClassOrName_IsDroppedAndRecorded()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "Missing",
            "T.Missing",
            """
            <policy name="NoKey" class="Machine" displayName="$(string.P)" valueName="V"><parentCategory ref="Root" /></policy>
            <policy name="NoClass" displayName="$(string.P)" key="K" valueName="V"><parentCategory ref="Root" /></policy>
            <policy class="Machine" displayName="$(string.P)" key="K"><parentCategory ref="Root" /></policy>
            <policy name="BadClass" class="Everyone" displayName="$(string.P)" key="K"><parentCategory ref="Root" /></policy>
            <policy name="Good" class="Machine" displayName="$(string.P)" key="K"><parentCategory ref="Root" /></policy>
            """,
            """<string id="P">P</string>""");
        var set = temp.Load();
        Assert.AreEqual(1, set.Policies.Count);
        Assert.AreEqual("Good", set.Policies[0].Name);
        Assert.AreEqual(3, set.Failures.Count(f => f.Code == TemplateFailureCode.MissingAttribute));
        var details = set.Failures.Where(f => f.Code == TemplateFailureCode.MissingAttribute).Select(f => f.Detail).OrderBy(d => d, StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(new[] { "class", "key", "name" }, details);
        Assert.AreEqual("class", FailureAssert.Single(set, TemplateFailureCode.InvalidAttributeValue).Detail);
    }

    [TestMethod]
    public void Parser_ClassIsCaseInsensitive()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "Case",
            "T.Case",
            """<policy name="P" class="both" displayName="$(string.P)" key="K"><parentCategory ref="Root" /></policy>""",
            """<string id="P">P</string>""");
        var set = temp.Load();
        FailureAssert.None(set);
        Assert.AreEqual(PolicyClass.Both, set.Policies[0].Class);
    }

    [TestMethod]
    public void Parser_DuplicatePolicyCategoryAndSupportedOn_KeepTheFirst()
    {
        using var temp = new TempTemplates();
        temp.Add(
            "Dups",
            "T.Dups",
            "t",
            """
            <supportedOn><definitions>
              <definition name="S" displayName="$(string.First)" />
              <definition name="s" displayName="$(string.Second)" />
            </definitions></supportedOn>
            <categories>
              <category name="Root" displayName="$(string.First)" />
              <category name="ROOT" displayName="$(string.Second)" />
            </categories>
            <policies>
              <policy name="P" class="User" displayName="$(string.First)" key="K" valueName="A"><parentCategory ref="Root" /></policy>
              <policy name="p" class="User" displayName="$(string.Second)" key="K" valueName="B"><parentCategory ref="Root" /></policy>
            </policies>
            """,
            """<string id="First">First</string><string id="Second">Second</string>""");
        var set = temp.Load();
        Assert.AreEqual(1, set.Policies.Count);
        Assert.AreEqual("A", set.Policies[0].ValueName);
        Assert.AreEqual("First", set.Categories.Single().DisplayName);
        Assert.AreEqual("First", set.SupportedOn.Single().DisplayName);
        FailureAssert.Single(set, TemplateFailureCode.DuplicatePolicy);
        FailureAssert.Single(set, TemplateFailureCode.DuplicateCategory);
        FailureAssert.Single(set, TemplateFailureCode.DuplicateSupportedOn);
    }

    [TestMethod]
    public void Parser_DuplicateElementId_DropsTheWholePolicy()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "DupEl",
            "T.DupEl",
            """
            <policy name="Bad" class="Machine" displayName="$(string.P)" key="K"><parentCategory ref="Root" />
              <elements><text id="A" valueName="A" /><decimal id="a" valueName="B" /></elements></policy>
            <policy name="Good" class="Machine" displayName="$(string.P)" key="K"><parentCategory ref="Root" /></policy>
            """,
            """<string id="P">P</string>""");
        var set = temp.Load();
        Assert.AreEqual(1, set.Policies.Count);
        Assert.AreEqual("Good", set.Policies[0].Name);
        Assert.AreEqual("a", FailureAssert.Single(set, TemplateFailureCode.DuplicateElement).Detail);
    }

    [TestMethod]
    public void Parser_ElementWithoutId_DropsTheWholePolicy()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "NoId",
            "T.NoId",
            """<policy name="Bad" class="Machine" displayName="$(string.P)" key="K"><parentCategory ref="Root" /><elements><text valueName="A" /></elements></policy>""",
            """<string id="P">P</string>""");
        var set = temp.Load();
        Assert.AreEqual(0, set.Policies.Count);
        Assert.AreEqual("id", FailureAssert.Single(set, TemplateFailureCode.MissingAttribute).Detail);
    }

    [TestMethod]
    public void Parser_InvalidNumbersAndBooleans_AreRecordedAndIgnored()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "Invalid",
            "T.Invalid",
            """
            <policy name="P" class="Machine" displayName="$(string.P)" key="K"><parentCategory ref="Root" />
              <elements><decimal id="D" valueName="D" minValue="abc" maxValue="-5" soft="maybe" required="1" /></elements></policy>
            """,
            """<string id="P">P</string>""");
        var set = temp.Load();
        var element = set.Policies.Single().Element<DecimalElement>("D");
        Assert.IsNull(element.MinValue);
        Assert.IsNull(element.MaxValue);
        Assert.IsFalse(element.Soft);
        Assert.IsTrue(element.Required);
        var details = set.Failures.Where(f => f.Code == TemplateFailureCode.InvalidAttributeValue).Select(f => f.Detail).OrderBy(d => d, StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(new[] { "maxValue", "minValue", "soft" }, details);
    }

    [TestMethod]
    public void Parser_DeleteAndLongDecimalValues_AreRead()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "Values",
            "T.Values",
            """
            <policy name="P" class="Machine" displayName="$(string.P)" key="K" valueName="V"><parentCategory ref="Root" />
              <enabledValue><longDecimal value="5000000000" /></enabledValue>
              <disabledValue><delete /></disabledValue></policy>
            """,
            """<string id="P">P</string>""");
        var policy = temp.Load().Policies.Single();
        Assert.AreEqual(PolicyValueKind.LongDecimal, policy.EnabledValue!.Kind);
        Assert.AreEqual(5000000000UL, policy.EnabledValue.Number);
        Assert.AreEqual(PolicyValueKind.Delete, policy.DisabledValue!.Kind);
    }

    [TestMethod]
    public void Parser_NamesAndKeysOverTheLimits_AreRefused()
    {
        using var temp = new TempTemplates();
        var longName = new string('n', AdmxLimits.MaxNameLength + 1);
        var longComponent = "K\\" + new string('c', AdmxLimits.MaxKeyComponentLength + 1);
        var longValue = new string('v', AdmxLimits.MaxValueNameLength + 1);
        temp.AddSimple(
            "Long",
            "T.Long",
            $"""
            <policy name="{longName}" class="Machine" displayName="$(string.P)" key="K"><parentCategory ref="Root" /></policy>
            <policy name="LongKey" class="Machine" displayName="$(string.P)" key="{longComponent}"><parentCategory ref="Root" /></policy>
            <policy name="LongValue" class="Machine" displayName="$(string.P)" key="K" valueName="{longValue}"><parentCategory ref="Root" /></policy>
            <policy name="LongElement" class="Machine" displayName="$(string.P)" key="K"><parentCategory ref="Root" /><elements><text id="{longName}" valueName="x" /></elements></policy>
            <policy name="Good" class="Machine" displayName="$(string.P)" key="K"><parentCategory ref="Root" /></policy>
            """,
            """<string id="P">P</string>""");
        var set = temp.Load();
        Assert.AreEqual("Good", set.Policies.Single().Name);
        var details = set.Failures.Where(f => f.Code == TemplateFailureCode.NameTooLong).Select(f => f.Detail).OrderBy(d => d, StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(new[] { "id", "key", "name", "valueName" }, details);
    }

    [TestMethod]
    public void Parser_LimitsAreTheDocumentedConstants()
    {
        Assert.AreEqual(16L * 1024 * 1024, Value(AdmxLimits.MaxFileBytes));
        Assert.AreEqual(100, Value(AdmxLimits.MaxXmlDepth));
        Assert.AreEqual(255, Value(AdmxLimits.MaxFileNameLength));
        Assert.AreEqual(255, Value(AdmxLimits.MaxNameLength));
        Assert.AreEqual(32767, Value(AdmxLimits.MaxKeyLength));
        Assert.AreEqual(255, Value(AdmxLimits.MaxKeyComponentLength));
        Assert.AreEqual(16383, Value(AdmxLimits.MaxValueNameLength));
        Assert.AreEqual(1024 * 1024, Value(AdmxLimits.MaxStringLength));
    }

    private static T Value<T>(T value) => value;

    [TestMethod]
    public void Parser_PolicyOrderAndSourceOrder_FollowTheDocument()
    {
        var sortNames = fixture.Category("SortCases").Policies.Select(p => p.Name).ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                "Pol_Sort_Zulu", "Pol_Sort_LowerAlpha", "Pol_Sort_Digit10", "Pol_Sort_Digit2",
                "Pol_Sort_LeadingSymbol", "Pol_Sort_NonAscii", "Pol_Sort_Digit1", "Pol_Sort_UpperAlpha",
            },
            sortNames);
        Assert.AreEqual("Pol_Top_DirectUnderRoot", fixture.Policies[0].Name);
        for (var i = 0; i < fixture.Policies.Count; i++)
        {
            Assert.AreEqual(i, fixture.Policies[i].SourceOrder);
        }
    }
}

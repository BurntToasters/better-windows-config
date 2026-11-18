using Bwc.Policy.Model;
using static Bwc.Policy.Tests.PolicyModelTestSupport;

namespace Bwc.Policy.Tests;

/// <summary>
/// Enabled, Disabled and Not Configured over a policy of every shape of the fixture: what is read back after each
/// apply, that a repeated apply changes nothing, and that Not Configured leaves nothing behind.
/// </summary>
[TestClass]
public sealed class PolicyStateFlowTests
{
    private sealed record Sample(string Policy, PolicyEdit Edit, bool EnabledReadable = true, bool DisabledReadable = true);

    private static ElementValue B(bool value) => ElementValue.FromBoolean(value);

    private static ElementValue N(ulong value) => ElementValue.FromNumber(value);

    private static ElementValue T(string value) => ElementValue.FromText(value);

    private static ElementValue C(int index) => ElementValue.FromChoice(index);

    private static ElementValue L(params string[] data) => ElementValue.FromList(data.Select(ListEntry.Of).ToArray());

    private static IEnumerable<Sample> Samples()
    {
        var plain = PolicyEdit.Enabled();
        foreach (var name in new[]
                 {
                     "Pol_Top_DirectUnderRoot", "Pol_Class_Machine", "Pol_Class_User", "Pol_Class_Both", "Pol_Value_Decimal",
                     "Pol_Value_String", "Pol_Value_EnabledOnly", "Pol_Value_DisabledOnly", "Pol_Value_EnabledListOnly",
                     "Pol_Value_BothLists", "Pol_Outside_KeyOutsideRoots", "Pol_Shared_Zeta", "Pol_Shared_Alpha", "Pol_Shared_Mid",
                     "Pol_Sort_Zulu", "Pol_Amp_DisplayName", "Pol_Explain_MultiParagraph", "Pol_Nested_MachineTop",
                 })
        {
            yield return new Sample(name, plain);
        }

        yield return new Sample("Pol_Value_DisabledListOnly", plain, EnabledReadable: false);
        yield return new Sample("Pol_Value_NoValueName_ElementsOnly", PolicyEdit.Enabled(("OnlyText", T("t")), ("OnlyNumber", N(3))));
        yield return new Sample("Pol_Element_KeyOverride", PolicyEdit.Enabled(("OverrideText", T("v"))));
        yield return new Sample("Pol_Both_WithElements", PolicyEdit.Enabled(("BothNumber", N(5))));
        yield return new Sample("Pol_Bool_TrueFalseValues", PolicyEdit.Enabled(("TrueFalseCheck", B(true))));
        yield return new Sample("Pol_Bool_StringValues", PolicyEdit.Enabled(("StringCheck", B(false))));
        yield return new Sample("Pol_Bool_TrueFalseLists", PolicyEdit.Enabled(("ListCheck", B(true))));
        yield return new Sample("Pol_Bool_NoFalseValue", PolicyEdit.Enabled(("NoFalseCheck", B(true))));
        yield return new Sample("Pol_Bool_TwoChecksReverseOrder", PolicyEdit.Enabled(("ZetaCheck", B(true)), ("AlphaCheck", B(false))));
        yield return new Sample("Pol_Decimal_MinMax", PolicyEdit.Enabled(("MinMaxNumber", N(100))));
        yield return new Sample("Pol_Decimal_Required", PolicyEdit.Enabled(("RequiredNumber", N(1))));
        yield return new Sample("Pol_Decimal_Soft", PolicyEdit.Enabled(("SoftNumber", N(5))));
        yield return new Sample("Pol_Decimal_StoreAsText", PolicyEdit.Enabled(("TextNumber", N(1))));
        yield return new Sample("Pol_LongDecimal_Basic", PolicyEdit.Enabled(("LongNumber", N(1))));
        yield return new Sample("Pol_LongDecimal_RequiredSoftText", PolicyEdit.Enabled(("LongTextNumber", N(1))));
        yield return new Sample("Pol_Text_Plain", PolicyEdit.Enabled(("PlainText", T("a"))));
        yield return new Sample("Pol_Text_Expandable", PolicyEdit.Enabled(("ExpandText", T("b"))));
        yield return new Sample("Pol_Text_MaxLength", PolicyEdit.Enabled(("ShortText", T("c"))));
        yield return new Sample("Pol_Text_Required", PolicyEdit.Enabled(("RequiredText", T("d"))));
        yield return new Sample("Pol_Text_ComboBoxSuggestions", PolicyEdit.Enabled(("ComboText", T("e"))));
        yield return new Sample("Pol_Text_ComboBoxNoSortRequired", PolicyEdit.Enabled(("ComboNoSortText", T("f"))));
        yield return new Sample("Pol_MultiText_Basic", PolicyEdit.Enabled(("MultiLines", ElementValue.FromLines("a"))));
        yield return new Sample("Pol_MultiText_Limits", PolicyEdit.Enabled(("LimitedLines", ElementValue.FromLines("a", "b"))));
        yield return new Sample("Pol_Enum_DecimalItems", PolicyEdit.Enabled(("DecimalChoice", C(1))));
        yield return new Sample("Pol_Enum_StringItems", PolicyEdit.Enabled(("StringChoice", C(0))));
        yield return new Sample("Pol_Enum_ItemValueList", PolicyEdit.Enabled(("ProfileChoice", C(0))));
        yield return new Sample("Pol_Enum_Required", PolicyEdit.Enabled(("RequiredChoice", C(1))));
        yield return new Sample("Pol_Enum_NoSortDefaultItem", PolicyEdit.Enabled(("UnsortedChoice", C(2))));
        yield return new Sample("Pol_List_Plain", PolicyEdit.Enabled(("PlainList", L("a"))), DisabledReadable: false);
        yield return new Sample("Pol_List_EmptyPrefix", PolicyEdit.Enabled(("EmptyPrefixList", L("a"))), DisabledReadable: false);
        yield return new Sample("Pol_List_Additive", PolicyEdit.Enabled(("AdditiveList", L("a"))), DisabledReadable: false);
        yield return new Sample(
            "Pol_List_ExplicitValue",
            PolicyEdit.Enabled(("ExplicitList", ElementValue.FromList(new ListEntry("n", "v")))),
            DisabledReadable: false);
        yield return new Sample("Pol_List_ValuePrefix", PolicyEdit.Enabled(("PrefixList", L("a"))), DisabledReadable: false);
        yield return new Sample("Pol_List_Expandable", PolicyEdit.Enabled(("ExpandList", L("a"))), DisabledReadable: false);
        yield return new Sample("Pol_List_PolicyValueName", PolicyEdit.Enabled(("SwitchedList", L("a"))));
        yield return new Sample("Pol_SharedList_One", PolicyEdit.Enabled(("SharedListOne", L("a"))), DisabledReadable: false);
        yield return new Sample("Pol_Shared_ElementsReverseOrder", PolicyEdit.Enabled(("ZetaElement", T("z")), ("AlphaElement", N(1))));
        yield return new Sample("Pol_Amp_LabelSingle", PolicyEdit.Enabled(("AmpText", T("t")), ("AmpChoice", C(1))));
        yield return new Sample("Pol_Amp_LabelDouble", PolicyEdit.Enabled(("DoubleAmpText", T("x"))));
    }

    private static bool SameValue(ElementValue expected, ElementValue actual) =>
        (expected, actual) switch
        {
            (MultiTextValue e, MultiTextValue a) => e.Lines.SequenceEqual(a.Lines),
            (ListValue e, ListValue a) => e.Entries.Select(x => x.Data).SequenceEqual(a.Entries.Select(x => x.Data)),
            _ => expected.Equals(actual),
        };

    [TestMethod]
    public void EverySampleShape_EnabledDisabledNotConfigured()
    {
        var samples = Samples().ToList();
        Assert.IsGreaterThanOrEqualTo(55, samples.Count);

        foreach (var sample in samples)
        {
            var document = ApplyTwice(sample.Policy, sample.Edit);

            var enabled = Read(sample.Policy, document);
            if (sample.EnabledReadable)
            {
                Assert.AreEqual(PolicyState.Enabled, enabled.State, sample.Policy + " enabled");
                foreach (var (id, value) in sample.Edit.Elements)
                {
                    Assert.IsTrue(enabled.Elements.TryGetValue(id, out var read), sample.Policy + " element " + id);
                    Assert.IsTrue(SameValue(value, read!), sample.Policy + " element " + id + " value");
                }
            }

            Assert.IsTrue(Apply(sample.Policy, document, PolicyEdit.Disabled).IsValid);
            var disabledDocument = Reload(document);
            var disabledAgain = Apply(sample.Policy, disabledDocument, PolicyEdit.Disabled);
            Assert.IsFalse(disabledAgain.Changed, sample.Policy + " disabled twice");
            var disabled = Read(sample.Policy, document);
            Assert.AreEqual(
                sample.DisabledReadable ? PolicyState.Disabled : PolicyState.NotConfigured,
                disabled.State,
                sample.Policy + " disabled");
            Assert.AreEqual(0, disabled.Elements.Count, sample.Policy + " disabled elements");

            Apply(sample.Policy, document, PolicyEdit.NotConfigured);
            Assert.IsTrue(document.IsEmpty, sample.Policy + " not configured after disabled");
            Assert.AreEqual(PolicyState.NotConfigured, Read(sample.Policy, document).State, sample.Policy);

            var direct = ApplyTwice(sample.Policy, sample.Edit);
            Apply(sample.Policy, direct, PolicyEdit.NotConfigured);
            Assert.IsTrue(direct.IsEmpty, sample.Policy + " not configured after enabled");
            Assert.AreEqual(0, Read(sample.Policy, direct).OwnedRecords.Count, sample.Policy);
        }
    }

    [TestMethod]
    public void NotConfigured_OnAnEmptyDocument_ChangesNothing()
    {
        foreach (var sample in Samples())
        {
            var document = NewDocument();

            var result = Apply(sample.Policy, document, PolicyEdit.NotConfigured);

            Assert.IsFalse(result.Changed, sample.Policy);
            Assert.IsFalse(document.HasChanges, sample.Policy);
        }
    }

    [TestMethod]
    public void Enabled_WhenNoRecordIsWritten_ReadsNotConfigured()
    {
        var document = NewDocument();

        var result = Apply("Pol_Bool_NoFalseValue", document, PolicyEdit.Enabled(("NoFalseCheck", B(false))));

        Assert.IsTrue(result.IsValid);
        Assert.IsFalse(result.Changed);
        Assert.AreEqual(PolicyState.NotConfigured, Read("Pol_Bool_NoFalseValue", document).State);
    }

    [TestMethod]
    public void TwoPoliciesInOneKey_DoNotDisturbEachOther()
    {
        var document = NewDocument();
        Apply("Pol_Shared_Zeta", document, PolicyEdit.Enabled());
        Apply("Pol_Shared_Alpha", document, PolicyEdit.Disabled);
        Apply("Pol_Shared_Mid", document, PolicyEdit.Enabled());

        Apply("Pol_Shared_Alpha", document, PolicyEdit.NotConfigured);

        AssertBytes(document, RecDw(Shared, "Zeta", 1), RecDw(Shared, "Mid", 1));
        Assert.AreEqual(PolicyState.Enabled, Read("Pol_Shared_Zeta", document).State);
        Assert.AreEqual(PolicyState.Enabled, Read("Pol_Shared_Mid", document).State);
        Assert.AreEqual(PolicyState.NotConfigured, Read("Pol_Shared_Alpha", document).State);
    }
}

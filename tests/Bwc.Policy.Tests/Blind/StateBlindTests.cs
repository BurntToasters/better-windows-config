using System.Text;
using Bwc.Gpo.Pol;
using Bwc.Policy.Admx;
using Bwc.Policy.Model;
using static Bwc.Policy.Tests.Blind.BlindKit;

namespace Bwc.Policy.Tests.Blind;

/// <summary>
/// Blind tests of the policy state reader, writer and validator (piece E4). Derived from docs/failure-modes/policy-state.md
/// and the "Ordered model" part of docs/contracts/policy-engine.md without reading the implementation.
/// </summary>
[TestClass]
public sealed class StateBlindTests
{
    private const string K = @"Software\Policies\BWC-Oracle";
    private const string Basics = K + @"\Basics";

    private static readonly TemplateSet Set = Fixture.Load();

    private static PolicyDefinition Pol(string name) => Set.Policy(name);

    private static PolicyApplyResult Apply(string policy, PolDocument document, PolicyEdit edit) =>
        PolicyStateWriter.Apply(Pol(policy), document, edit);

    private static PolicyStateSnapshot Read(string policy, PolDocument document) =>
        PolicyStateReader.Read(Pol(policy), document);

    private static PolDocument Reparse(PolDocument document) => PolDocument.Parse(document.ToBytes());

    private static uint? TypeOf(PolDocument d, string key, string name) => d.GetValue(key, name)?.Type;

    private static uint? Dw(PolDocument d, string key, string name)
    {
        var r = d.GetValue(key, name);
        return r is not null && r.Type == (uint)PolValueType.DWord ? PolData.TryGetDWord(r.Data.Span) : null;
    }

    private static ulong? Qw(PolDocument d, string key, string name)
    {
        var r = d.GetValue(key, name);
        return r is not null && r.Type == (uint)PolValueType.QWord ? PolData.TryGetQWord(r.Data.Span) : null;
    }

    private static string? Sz(PolDocument d, string key, string name, PolValueType type = PolValueType.String)
    {
        var r = d.GetValue(key, name);
        return r is not null && r.Type == (uint)type ? PolData.TryGetString(r.Data.Span) : null;
    }

    private static string[] Names(PolDocument d, string key) => d.GetRecords(key).Select(r => r.ValueName).ToArray();

    /// <summary>A document read from crafted bytes, so a test can hold instructions the document API refuses to set.</summary>
    private static PolDocument Crafted(params (string Key, string Name, uint Type, byte[] Data)[] records)
    {
        var bytes = new List<byte> { 0x50, 0x52, 0x65, 0x67, 0x01, 0x00, 0x00, 0x00 };
        foreach (var (key, name, type, data) in records)
        {
            bytes.AddRange(Encoding.Unicode.GetBytes("[" + key + "\0;" + name + "\0;"));
            bytes.AddRange(BitConverter.GetBytes(type));
            bytes.AddRange(Encoding.Unicode.GetBytes(";"));
            bytes.AddRange(BitConverter.GetBytes(data.Length));
            bytes.AddRange(Encoding.Unicode.GetBytes(";"));
            bytes.AddRange(data);
            bytes.AddRange(Encoding.Unicode.GetBytes("]"));
        }

        return PolDocument.Parse(bytes.ToArray());
    }

    private static readonly byte[] Marker = [0x20, 0, 0, 0];

    private static void Set1(PolDocument d, string key, string name, uint value) =>
        d.SetValue(key, name, PolValueType.DWord, PolData.FromDWord(value));

    private static void SetSz(PolDocument d, string key, string name, string value, PolValueType type = PolValueType.String) =>
        d.SetValue(key, name, type, PolData.FromString(value));

    private static PolicyDefinition Tmp(string name, string inner, string? valueName = "V", string key = Key, string cls = "Machine")
    {
        using var t = new TempTemplates();
        Std(t, "s", "Blind.S", P(name, inner, valueName: valueName, key: key, cls: cls), [name]);
        var set = t.Load();
        FailureAssert.None(set);
        return set.FindPolicy("Blind.S", name)!;
    }

    private static void AssertNoChange(PolicyApplyResult result, PolDocument document)
    {
        Assert.IsTrue(result.IsValid);
        Assert.IsFalse(result.Changed, "Changed");
        Assert.IsFalse(document.HasChanges, "HasChanges");
    }

    private static ElementValue ValueOf(PolicyStateSnapshot snapshot, string id) => snapshot.Elements[id];

    // ---------------------------------------------------------------- 1. Policy value

    [TestMethod]
    public void Sec01_PlainValueName_EnabledWritesDword1_DisabledDword0_NotConfiguredRemoves()
    {
        var d = PolDocument.Create();
        Assert.IsTrue(Apply("Pol_Class_Machine", d, PolicyEdit.Enabled()).Changed);
        Assert.AreEqual(1u, Dw(d, Basics, "MachineFlag"));
        Assert.AreEqual(PolicyState.Enabled, Read("Pol_Class_Machine", d).State);

        Apply("Pol_Class_Machine", d, PolicyEdit.Disabled);
        Assert.AreEqual(0u, Dw(d, Basics, "MachineFlag"));
        Assert.AreEqual(PolicyState.Disabled, Read("Pol_Class_Machine", d).State);

        Apply("Pol_Class_Machine", d, PolicyEdit.NotConfigured);
        Assert.IsNull(d.GetValue(Basics, "MachineFlag"));
        Assert.IsNull(d.GetValue(Basics, "**del.MachineFlag"));
        Assert.AreEqual(PolicyState.NotConfigured, Read("Pol_Class_Machine", d).State);
    }

    [TestMethod]
    public void Sec01_ExplicitDecimalValues()
    {
        var d = PolDocument.Create();
        Apply("Pol_Value_Decimal", d, PolicyEdit.Enabled());
        Assert.AreEqual(5u, Dw(d, Basics, "ModeNumber"));
        Assert.AreEqual(PolicyState.Enabled, Read("Pol_Value_Decimal", d).State);
        Apply("Pol_Value_Decimal", d, PolicyEdit.Disabled);
        Assert.AreEqual(9u, Dw(d, Basics, "ModeNumber"));
        Assert.AreEqual(PolicyState.Disabled, Read("Pol_Value_Decimal", d).State);
    }

    [TestMethod]
    public void Sec01_StringValues_AreRegSz()
    {
        var d = PolDocument.Create();
        Apply("Pol_Value_String", d, PolicyEdit.Enabled());
        Assert.AreEqual("on", Sz(d, Basics, "ModeText"));
        Assert.AreEqual(PolicyState.Enabled, Read("Pol_Value_String", d).State);
        Apply("Pol_Value_String", d, PolicyEdit.Disabled);
        Assert.AreEqual("off", Sz(d, Basics, "ModeText"));
        Assert.AreEqual(PolicyState.Disabled, Read("Pol_Value_String", d).State);
    }

    [TestMethod]
    public void Sec01_EnabledOnly_DisabledRemovesTheValueAndAppendsDeleteInstruction()
    {
        var d = PolDocument.Create();
        Apply("Pol_Value_EnabledOnly", d, PolicyEdit.Enabled());
        Assert.AreEqual(7u, Dw(d, Basics, "EnabledOnly"));
        Apply("Pol_Value_EnabledOnly", d, PolicyEdit.Disabled);
        Assert.IsNull(d.GetValue(Basics, "EnabledOnly"));
        var del = d.GetValue(Basics, "**del.EnabledOnly");
        Assert.IsNotNull(del);
        CollectionAssert.AreEqual(new byte[] { 0x20, 0, 0, 0 }, del.Data.ToArray());
        Assert.AreEqual((uint)PolValueType.String, del.Type);
        Assert.AreEqual(PolicyState.Disabled, Read("Pol_Value_EnabledOnly", d).State);
        Assert.AreEqual("**del.EnabledOnly", d.GetRecords(Basics).Last().ValueName);
    }

    [TestMethod]
    public void Sec01_DisabledOnly_EnabledWritesDefaultOne_DisabledWritesItsValue()
    {
        var d = PolDocument.Create();
        Apply("Pol_Value_DisabledOnly", d, PolicyEdit.Enabled());
        Assert.AreEqual(1u, Dw(d, Basics, "DisabledOnly"));
        Assert.AreEqual(PolicyState.Enabled, Read("Pol_Value_DisabledOnly", d).State);
        Apply("Pol_Value_DisabledOnly", d, PolicyEdit.Disabled);
        Assert.AreEqual(3u, Dw(d, Basics, "DisabledOnly"));
        Assert.AreEqual(PolicyState.Disabled, Read("Pol_Value_DisabledOnly", d).State);
    }

    [TestMethod]
    public void Sec01_BigEnabledValue_IsQword_AndDeleteLiteralWritesTheDeleteInstruction()
    {
        var big = Tmp("Pol", "<enabledValue><longDecimal value=\"5000000000\" /></enabledValue><disabledValue><delete /></disabledValue>");
        var d = PolDocument.Create();
        PolicyStateWriter.Apply(big, d, PolicyEdit.Enabled());
        Assert.AreEqual(5000000000UL, Qw(d, Key, "V"));
        Assert.AreEqual(PolicyState.Enabled, PolicyStateReader.Read(big, d).State);
        PolicyStateWriter.Apply(big, d, PolicyEdit.Disabled);
        Assert.IsNull(d.GetValue(Key, "V"));
        Assert.IsNotNull(d.GetValue(Key, "**del.V"));
    }

    [TestMethod]
    public void Sec01_NotConfiguredClearsDeleteAndSoftInstructions_SoItCannotReadBackAsDisabled()
    {
        var d = PolDocument.Create();
        Apply("Pol_Value_EnabledOnly", d, PolicyEdit.Disabled);
        d.AddSoftInstruction(Basics, "EnabledOnly");
        Apply("Pol_Value_EnabledOnly", d, PolicyEdit.NotConfigured);
        Assert.IsNull(d.GetValue(Basics, "**del.EnabledOnly"));
        Assert.IsNull(d.GetValue(Basics, "**soft.EnabledOnly"));
        Assert.AreEqual(PolicyState.NotConfigured, Read("Pol_Value_EnabledOnly", d).State);
        Assert.IsTrue(d.IsEmpty);
    }

    [TestMethod]
    public void Sec01_NotConfiguredWritesNoInstruction()
    {
        var d = PolDocument.Create();
        Apply("Pol_Value_Decimal", d, PolicyEdit.Enabled());
        Apply("Pol_Value_Decimal", d, PolicyEdit.NotConfigured);
        Assert.IsTrue(d.IsEmpty);
    }

    // ---------------------------------------------------------------- 1.1 Value that does not match

    [TestMethod]
    public void Sec01_1_ADwordThatMatchesNeitherLiteral_ReadsNotConfigured_AndEnablingMovesItToTheEnd()
    {
        var d = PolDocument.Create();
        Set1(d, Basics, "MachineFlag", 2);
        Set1(d, Basics, "UserFlag", 1);
        Assert.AreEqual(PolicyState.NotConfigured, Read("Pol_Class_Machine", d).State);
        Apply("Pol_Class_Machine", d, PolicyEdit.Enabled());
        CollectionAssert.AreEqual(new[] { "UserFlag", "MachineFlag" }, Names(d, Basics));
        Assert.AreEqual(1u, Dw(d, Basics, "MachineFlag"));
    }

    [TestMethod]
    public void Sec01_1_TypeComparisonIsExact()
    {
        var d = PolDocument.Create();
        SetSz(d, Basics, "MachineFlag", "1");
        Assert.AreEqual(PolicyState.NotConfigured, Read("Pol_Class_Machine", d).State, "REG_SZ digits are not a DWORD");

        var e = PolDocument.Create();
        SetSz(e, Basics, "ModeText", "on", PolValueType.ExpandString);
        Assert.AreEqual(PolicyState.NotConfigured, Read("Pol_Value_String", e).State, "REG_EXPAND_SZ is not REG_SZ");

        var f = PolDocument.Create();
        SetSz(f, Basics, "ModeNumber", "5");
        Assert.AreEqual(PolicyState.NotConfigured, Read("Pol_Value_Decimal", f).State);
    }

    // ---------------------------------------------------------------- 1.2 Delete instructions

    [TestMethod]
    public void Sec01_2_OnlyADeleteInstruction_ReadsDisabled()
    {
        var d = PolDocument.Create();
        d.AddDeleteInstruction(Basics, "MachineFlag");
        Assert.AreEqual(PolicyState.Disabled, Read("Pol_Class_Machine", d).State);
    }

    [TestMethod]
    public void Sec01_2_EnabledRemovesTheDeleteInstructionOfEveryNameItWrites()
    {
        var d = PolDocument.Create();
        d.AddDeleteInstruction(Basics, "MachineFlag");
        Apply("Pol_Class_Machine", d, PolicyEdit.Enabled());
        Assert.IsNull(d.GetValue(Basics, "**del.MachineFlag"));
        Assert.AreEqual(1u, Dw(d, Basics, "MachineFlag"));
    }

    [TestMethod]
    public void Sec01_2_InstructionsOfOthersAndUnknownInstructions_AreNeverTouched()
    {
        var d = Crafted((Basics, "**unknownthing", (uint)PolValueType.String, PolData.FromString("keep")));
        d.AddDeleteInstruction(Basics, "SomeoneElse");
        d.AddSoftInstruction(Basics, "SomeoneElse");
        var before = d.GetRecords(Basics).Select(r => r.RawBytes.ToArray()).ToList();
        Apply("Pol_Class_Machine", d, PolicyEdit.Enabled());
        Apply("Pol_Class_Machine", d, PolicyEdit.Disabled);
        Apply("Pol_Class_Machine", d, PolicyEdit.NotConfigured);
        var after = d.GetRecords(Basics).Select(r => r.RawBytes.ToArray()).ToList();
        Assert.AreEqual(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
        {
            CollectionAssert.AreEqual(before[i], after[i]);
        }
    }

    [TestMethod]
    public void Sec14_ADeleteInstructionOfAnotherPolicy_IsNotOwned()
    {
        var d = PolDocument.Create();
        d.AddDeleteInstruction(Basics, "SomeoneElse");
        Apply("Pol_Class_Machine", d, PolicyEdit.Enabled());
        var snap = Read("Pol_Class_Machine", d);
        Assert.IsFalse(snap.OwnedRecords.Any(r => r.ValueName.Equals("**del.SomeoneElse", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(snap.OwnedRecords.Any(r => r.ValueName == "MachineFlag"));
    }

    // ---------------------------------------------------------------- 2. No value name

    [TestMethod]
    public void Sec02_EnabledListOnly_WritesEveryItemAtItsEffectiveKey()
    {
        var d = PolDocument.Create();
        Assert.IsTrue(Apply("Pol_Value_EnabledListOnly", d, PolicyEdit.Enabled()).Changed);
        Assert.AreEqual(1u, Dw(d, Basics + @"\EnabledListKey", "ListItemOne"));
        Assert.AreEqual("two", Sz(d, Basics + @"\ItemKeyOverride", "ListItemTwo"));
        Assert.AreEqual(3u, Dw(d, Basics + @"\EnabledListKey", "ListItemThree"));
        Assert.AreEqual(PolicyState.Enabled, Read("Pol_Value_EnabledListOnly", d).State);
    }

    [TestMethod]
    public void Sec02_EnabledListOnly_ReadsNotConfiguredWhenAnItemIsMissing()
    {
        var d = PolDocument.Create();
        Apply("Pol_Value_EnabledListOnly", d, PolicyEdit.Enabled());
        d.RemoveValue(Basics + @"\ItemKeyOverride", "ListItemTwo");
        Assert.AreEqual(PolicyState.NotConfigured, Read("Pol_Value_EnabledListOnly", d).State);
    }

    // Doc section 2 table, enabledList only: Disabled writes "nothing for the list; no value record can say Disabled".
    [TestMethod]
    [Ignore("open: enabledList-only Disabled writes **del. per item or nothing; pending matrix B")]
    public void Sec02_EnabledListOnly_DisabledOnAnEmptyDocument_WritesNothing()
    {
        var e = PolDocument.Create();
        var result = Apply("Pol_Value_EnabledListOnly", e, PolicyEdit.Disabled);
        Assert.AreEqual(0, e.RecordCount, "Disabled must write nothing for an enabledList-only policy");
        AssertNoChange(result, e);
        Assert.AreEqual(PolicyState.NotConfigured, Read("Pol_Value_EnabledListOnly", e).State);
    }

    [TestMethod]
    public void Sec02_EnabledListOnly_DisabledRemovesTheValuesOfBothKeys()
    {
        var d = PolDocument.Create();
        Apply("Pol_Value_EnabledListOnly", d, PolicyEdit.Enabled());
        Apply("Pol_Value_EnabledListOnly", d, PolicyEdit.Disabled);
        Assert.IsNull(d.GetValue(Basics + @"\EnabledListKey", "ListItemOne"));
        Assert.IsNull(d.GetValue(Basics + @"\ItemKeyOverride", "ListItemTwo"));
        Assert.IsNull(d.GetValue(Basics + @"\EnabledListKey", "ListItemThree"));
    }

    [TestMethod]
    public void Sec02_DisabledListOnly_EnabledWritesNothing_DisabledWritesEveryItem()
    {
        var d = PolDocument.Create();
        AssertNoChange(Apply("Pol_Value_DisabledListOnly", d, PolicyEdit.Enabled()), d);
        Assert.AreEqual(PolicyState.NotConfigured, Read("Pol_Value_DisabledListOnly", d).State);

        Apply("Pol_Value_DisabledListOnly", d, PolicyEdit.Disabled);
        Assert.AreEqual(0u, Dw(d, Basics + @"\DisabledListKey", "OffItemOne"));
        Assert.AreEqual("zero", Sz(d, Basics + @"\ItemKeyOverride", "OffItemTwo"));
        Assert.AreEqual(PolicyState.Disabled, Read("Pol_Value_DisabledListOnly", d).State);

        d.RemoveValue(Basics + @"\DisabledListKey", "OffItemOne");
        Assert.AreEqual(PolicyState.NotConfigured, Read("Pol_Value_DisabledListOnly", d).State);
    }

    [TestMethod]
    public void Sec02_DisablingAPolicyWithATwoKeyList_RemovesValuesFromBothKeys()
    {
        var d = PolDocument.Create();
        Apply("Pol_Value_EnabledListOnly", d, PolicyEdit.Enabled());
        Apply("Pol_Value_EnabledListOnly", d, PolicyEdit.NotConfigured);
        Assert.IsNull(d.GetValue(Basics + @"\EnabledListKey", "ListItemOne"));
        Assert.IsNull(d.GetValue(Basics + @"\ItemKeyOverride", "ListItemTwo"));
        Assert.IsTrue(d.IsEmpty);
    }

    [TestMethod]
    public void Sec02_BothLists_NoValueName_EnabledWritesEnabledListAndRemovesDisabledList_DisabledTheReverseWithDeleteInstructions()
    {
        var policy = Tmp("Pol",
            "<enabledList defaultKey=\"Software\\Policies\\BlindT\\E\"><item valueName=\"On\"><value><decimal value=\"1\" /></value></item></enabledList>" +
            "<disabledList defaultKey=\"Software\\Policies\\BlindT\\D\"><item valueName=\"Off\"><value><decimal value=\"0\" /></value></item></disabledList>",
            valueName: null);
        var d = PolDocument.Create();
        PolicyStateWriter.Apply(policy, d, PolicyEdit.Disabled);
        Assert.AreEqual(0u, Dw(d, Key + @"\D", "Off"));
        Assert.AreEqual(PolicyState.Disabled, PolicyStateReader.Read(policy, d).State);

        PolicyStateWriter.Apply(policy, d, PolicyEdit.Enabled());
        Assert.AreEqual(1u, Dw(d, Key + @"\E", "On"));
        Assert.IsNull(d.GetValue(Key + @"\D", "Off"), "the disabled list is removed by Enabled");
        Assert.AreEqual(PolicyState.Enabled, PolicyStateReader.Read(policy, d).State);

        PolicyStateWriter.Apply(policy, d, PolicyEdit.Disabled);
        Assert.IsNull(d.GetValue(Key + @"\E", "On"), "the enabled list is removed by Disabled");
        Assert.IsNotNull(d.GetValue(Key + @"\E", "**del.On"), "and a delete instruction is written");
        Assert.AreEqual(0u, Dw(d, Key + @"\D", "Off"));
    }

    [TestMethod]
    public void Sec02_BothListsWithAValueName_StateComesFromTheValueOnly()
    {
        var d = PolDocument.Create();
        Apply("Pol_Value_BothLists", d, PolicyEdit.Enabled());
        Assert.AreEqual(1u, Dw(d, Basics, "ListMaster"));
        Assert.AreEqual(1u, Dw(d, Basics, "BothListOn"));
        Assert.AreEqual(11u, Dw(d, Basics + @"\BothListKey", "BothListOnOther"));
        Assert.IsNull(d.GetValue(Basics, "BothListOff"));
        Assert.AreEqual(PolicyState.Enabled, Read("Pol_Value_BothLists", d).State);

        Apply("Pol_Value_BothLists", d, PolicyEdit.Disabled);
        Assert.AreEqual(0u, Dw(d, Basics, "ListMaster"));
        Assert.AreEqual(0u, Dw(d, Basics, "BothListOff"));
        Assert.IsNull(d.GetValue(Basics, "BothListOn"));
        Assert.IsNull(d.GetValue(Basics + @"\BothListKey", "BothListOnOther"));
        Assert.AreEqual(PolicyState.Disabled, Read("Pol_Value_BothLists", d).State);
    }

    [TestMethod]
    public void Sec02_ElementsOnly_WrittenInDocumentOrder_DisabledRemovesWithDeleteInstructions()
    {
        var d = PolDocument.Create();
        var edit = PolicyEdit.Enabled(("OnlyNumber", ElementValue.FromNumber(5)), ("OnlyText", ElementValue.FromText("hi")));
        Assert.IsTrue(Apply("Pol_Value_NoValueName_ElementsOnly", d, edit).Changed);
        CollectionAssert.AreEqual(new[] { "OnlyText", "OnlyNumber" }, Names(d, Basics));
        Assert.AreEqual("hi", Sz(d, Basics, "OnlyText"));
        Assert.AreEqual(5u, Dw(d, Basics, "OnlyNumber"));
        Assert.AreEqual(PolicyState.Enabled, Read("Pol_Value_NoValueName_ElementsOnly", d).State);

        Apply("Pol_Value_NoValueName_ElementsOnly", d, PolicyEdit.Disabled);
        Assert.IsNull(d.GetValue(Basics, "OnlyText"));
        Assert.IsNull(d.GetValue(Basics, "OnlyNumber"));
        Assert.IsNotNull(d.GetValue(Basics, "**del.OnlyText"));
        Assert.IsNotNull(d.GetValue(Basics, "**del.OnlyNumber"));
        Assert.AreEqual(PolicyState.Disabled, Read("Pol_Value_NoValueName_ElementsOnly", d).State);
    }

    [TestMethod]
    public void Sec02_ElementsOnly_EnabledWhenAnyElementHasAValue_AndNotConfiguredWhenNone()
    {
        var d = PolDocument.Create();
        Assert.AreEqual(PolicyState.NotConfigured, Read("Pol_Value_NoValueName_ElementsOnly", d).State);
        Set1(d, Basics, "OnlyNumber", 3);
        var snap = Read("Pol_Value_NoValueName_ElementsOnly", d);
        Assert.AreEqual(PolicyState.Enabled, snap.State);
        Assert.IsTrue(snap.Elements.ContainsKey("OnlyNumber"));
        Assert.IsFalse(snap.Elements.ContainsKey("OnlyText"), "a partial set of element values is normal");
    }

    [TestMethod]
    public void Sec02_ADeleteInstructionWithAnElementValue_StillReadsEnabled()
    {
        var d = PolDocument.Create();
        d.AddDeleteInstruction(Basics, "OnlyText");
        Set1(d, Basics, "OnlyNumber", 3);
        Assert.AreEqual(PolicyState.Enabled, Read("Pol_Value_NoValueName_ElementsOnly", d).State);
    }

    [TestMethod]
    public void Sec02_Sec05_PolicyWithNothingToWrite_WritesNothingAndReportsNoChange()
    {
        var policy = Tmp("Pol", "", valueName: null);
        var d = PolDocument.Create();
        AssertNoChange(PolicyStateWriter.Apply(policy, d, PolicyEdit.Enabled()), d);
        Assert.IsTrue(d.IsEmpty);
        Assert.AreEqual(PolicyState.NotConfigured, PolicyStateReader.Read(policy, d).State);
    }

    [TestMethod]
    public void Sec02_AnItemWithNoValue_IsSkipped()
    {
        var policy = Tmp("Pol",
            "<enabledList defaultKey=\"Software\\Policies\\BlindT\\E\"><item valueName=\"NoValue\" /><item valueName=\"Has\"><value><decimal value=\"1\" /></value></item></enabledList>",
            valueName: null);
        var d = PolDocument.Create();
        PolicyStateWriter.Apply(policy, d, PolicyEdit.Enabled());
        CollectionAssert.AreEqual(new[] { "Has" }, Names(d, Key + @"\E"));
    }

    [TestMethod]
    public void Sec02_UncheckedBooleanWithNoFalseSide_AndNoValueName_ReadsNotConfigured_AndReportsNoChange()
    {
        var policy = Tmp("Pol",
            "<elements><boolean id=\"B\" valueName=\"B\"><trueValue><decimal value=\"1\" /></trueValue></boolean></elements>",
            valueName: null);
        var d = PolDocument.Create();
        var result = PolicyStateWriter.Apply(policy, d, PolicyEdit.Enabled(("B", ElementValue.FromBoolean(false))));
        AssertNoChange(result, d);
        Assert.AreEqual(PolicyState.NotConfigured, PolicyStateReader.Read(policy, d).State);
    }

    // ---------------------------------------------------------------- 3. Element kinds

    [TestMethod]
    public void Sec03_RequiredEmptyElement_IsARequiredProblem_AndNothingIsWritten()
    {
        var d = PolDocument.Create();
        var result = Apply("Pol_Decimal_Required", d, PolicyEdit.Enabled());
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(PolicyProblemKind.Required, result.Problems.Single().Kind);
        Assert.AreEqual("RequiredNumber", result.Problems.Single().ElementId, true);
        Assert.IsFalse(result.Changed);
        Assert.IsTrue(d.IsEmpty);
    }

    [TestMethod]
    public void Sec03_OptionalEmptyElement_RemovesItsValue_AndWritesNoInstruction()
    {
        var d = PolDocument.Create();
        Apply("Pol_Decimal_MinMax", d, PolicyEdit.Enabled(("MinMaxNumber", ElementValue.FromNumber(20))));
        Assert.AreEqual(20u, Dw(d, K + @"\Elements\Number", "MinMaxNumber"));
        Apply("Pol_Decimal_MinMax", d, PolicyEdit.Enabled());
        Assert.IsNull(d.GetValue(K + @"\Elements\Number", "MinMaxNumber"));
        Assert.IsNull(d.GetValue(K + @"\Elements\Number", "**del.MinMaxNumber"));
    }

    // 3.1 boolean

    [TestMethod]
    public void Sec03_1_BooleanWithTrueAndFalseValues()
    {
        var d = PolDocument.Create();
        var key = K + @"\Elements\Bool";
        Apply("Pol_Bool_TrueFalseValues", d, PolicyEdit.Enabled(("TrueFalseCheck", ElementValue.FromBoolean(true))));
        Assert.AreEqual(10u, Dw(d, key, "TrueFalseCheck"));
        Assert.AreEqual(true, ((BooleanValue)ValueOf(Read("Pol_Bool_TrueFalseValues", d), "TrueFalseCheck")).Checked);
        Apply("Pol_Bool_TrueFalseValues", d, PolicyEdit.Enabled(("TrueFalseCheck", ElementValue.FromBoolean(false))));
        Assert.AreEqual(20u, Dw(d, key, "TrueFalseCheck"));
        Assert.AreEqual(false, ((BooleanValue)ValueOf(Read("Pol_Bool_TrueFalseValues", d), "TrueFalseCheck")).Checked);
    }

    [TestMethod]
    public void Sec03_1_BooleanWithStringValues_IsRegSz()
    {
        var d = PolDocument.Create();
        var key = K + @"\Elements\Bool";
        Apply("Pol_Bool_StringValues", d, PolicyEdit.Enabled(("StringCheck", ElementValue.FromBoolean(true))));
        Assert.AreEqual("yes", Sz(d, key, "StringCheck"));
        Apply("Pol_Bool_StringValues", d, PolicyEdit.Enabled(("StringCheck", ElementValue.FromBoolean(false))));
        Assert.AreEqual("no", Sz(d, key, "StringCheck"));
    }

    [TestMethod]
    public void Sec03_1_BooleanWithoutFalseValue_FalseRemovesTheValue_WithoutAnInstruction()
    {
        var d = PolDocument.Create();
        var key = K + @"\Elements\Bool";
        Apply("Pol_Bool_NoFalseValue", d, PolicyEdit.Enabled(("NoFalseCheck", ElementValue.FromBoolean(true))));
        Assert.AreEqual(1u, Dw(d, key, "NoFalseCheck"));
        Apply("Pol_Bool_NoFalseValue", d, PolicyEdit.Enabled(("NoFalseCheck", ElementValue.FromBoolean(false))));
        Assert.IsNull(d.GetValue(key, "NoFalseCheck"));
        Assert.IsNull(d.GetValue(key, "**del.NoFalseCheck"));
        // Reads: no record means false.
        var snap = Read("Pol_Bool_NoFalseValue", d);
        if (snap.Elements.TryGetValue("NoFalseCheck", out var v))
        {
            Assert.AreEqual(false, ((BooleanValue)v).Checked);
        }
    }

    [TestMethod]
    public void Sec03_1_BooleanWithLists_WritesTheTrueItemsOrTheFalseItems_AtTheirKeys()
    {
        var d = PolDocument.Create();
        var bool_ = K + @"\Elements\Bool";
        Apply("Pol_Bool_TrueFalseLists", d, PolicyEdit.Enabled(("ListCheck", ElementValue.FromBoolean(true))));
        Assert.AreEqual(1u, Dw(d, bool_ + @"\TrueListKey", "TrueListOne"));
        Assert.AreEqual("t2", Sz(d, bool_ + @"\TrueListOtherKey", "TrueListTwo"));
        Assert.IsNull(d.GetValue(bool_ + @"\FalseListKey", "FalseListOne"));
        Assert.AreEqual(true, ((BooleanValue)ValueOf(Read("Pol_Bool_TrueFalseLists", d), "ListCheck")).Checked);

        Apply("Pol_Bool_TrueFalseLists", d, PolicyEdit.Enabled(("ListCheck", ElementValue.FromBoolean(false))));
        Assert.AreEqual(0u, Dw(d, bool_ + @"\FalseListKey", "FalseListOne"));
        Assert.AreEqual("f2", Sz(d, bool_ + @"\FalseListKey", "FalseListTwo"));
        Assert.IsNull(d.GetValue(bool_ + @"\TrueListKey", "TrueListOne"), "true items are removed");
        Assert.IsNull(d.GetValue(bool_ + @"\TrueListOtherKey", "TrueListTwo"));
        Assert.AreEqual(false, ((BooleanValue)ValueOf(Read("Pol_Bool_TrueFalseLists", d), "ListCheck")).Checked);

        Apply("Pol_Bool_TrueFalseLists", d, PolicyEdit.Enabled());
        Assert.IsNull(d.GetValue(bool_ + @"\FalseListKey", "FalseListOne"), "empty removes the values of both sides");
        Assert.IsNull(d.GetValue(bool_ + @"\TrueListKey", "TrueListOne"));
    }

    [TestMethod]
    public void Sec03_1_BooleanWithAValueNameAndNoValues_DefaultsToOneAndZero()
    {
        var d = PolDocument.Create();
        var key = K + @"\Amp";
        Apply("Pol_Amp_LabelSingle", d, PolicyEdit.Enabled(("AmpCheck", ElementValue.FromBoolean(true))));
        Assert.AreEqual(1u, Dw(d, key, "AmpCheck"));
        Apply("Pol_Amp_LabelSingle", d, PolicyEdit.Enabled(("AmpCheck", ElementValue.FromBoolean(false))));
        Assert.AreEqual(0u, Dw(d, key, "AmpCheck"));
        Assert.AreEqual(false, ((BooleanValue)ValueOf(Read("Pol_Amp_LabelSingle", d), "AmpCheck")).Checked);
    }

    [TestMethod]
    public void Sec03_1_BooleanValueOfAnotherNumber_ReadsAsAbsent()
    {
        var d = PolDocument.Create();
        Set1(d, K + @"\Elements\Bool", "TrueFalseCheck", 99);
        var snap = Read("Pol_Bool_TrueFalseValues", d);
        Assert.IsFalse(snap.Elements.ContainsKey("TrueFalseCheck"));
    }

    // 3.2 numbers

    [TestMethod]
    public void Sec03_2_DecimalRangeAndLimits()
    {
        var d = PolDocument.Create();
        var below = Apply("Pol_Decimal_MinMax", d, PolicyEdit.Enabled(("MinMaxNumber", ElementValue.FromNumber(9))));
        Assert.AreEqual(PolicyProblemKind.BelowMinimum, below.Problems.Single().Kind);
        Assert.AreEqual(10UL, below.Problems.Single().Limit);
        var above = Apply("Pol_Decimal_MinMax", d, PolicyEdit.Enabled(("MinMaxNumber", ElementValue.FromNumber(501))));
        Assert.AreEqual(PolicyProblemKind.AboveMaximum, above.Problems.Single().Kind);
        Assert.AreEqual(500UL, above.Problems.Single().Limit);
        Assert.IsTrue(d.IsEmpty, "an invalid edit writes nothing");
        Assert.IsTrue(Apply("Pol_Decimal_MinMax", d, PolicyEdit.Enabled(("MinMaxNumber", ElementValue.FromNumber(10)))).IsValid);
        Assert.IsTrue(Apply("Pol_Decimal_MinMax", d, PolicyEdit.Enabled(("MinMaxNumber", ElementValue.FromNumber(500)))).IsValid);
        Assert.AreEqual(500u, Dw(d, K + @"\Elements\Number", "MinMaxNumber"));
    }

    [TestMethod]
    public void Sec11_DefaultMaximumOfADecimal_Is4294967295_AndALongDecimalTakesTheUlongMaximum()
    {
        var d = PolDocument.Create();
        var over = Apply("Pol_Decimal_Required", d, PolicyEdit.Enabled(("RequiredNumber", ElementValue.FromNumber(4294967296))));
        Assert.AreEqual(PolicyProblemKind.AboveMaximum, over.Problems.Single().Kind);
        Assert.AreEqual(4294967295UL, over.Problems.Single().Limit);
        Assert.IsTrue(Apply("Pol_Decimal_Required", d, PolicyEdit.Enabled(("RequiredNumber", ElementValue.FromNumber(4294967295)))).IsValid);
        Assert.AreEqual(uint.MaxValue, Dw(d, K + @"\Elements\Number", "RequiredNumber"));

        var e = PolDocument.Create();
        var edit = PolicyEdit.Enabled(("LongTextNumber", ElementValue.FromNumber(ulong.MaxValue)));
        Assert.IsTrue(Apply("Pol_LongDecimal_RequiredSoftText", e, edit).IsValid);
        Assert.AreEqual("18446744073709551615", Sz(e, K + @"\Elements\Number", "LongTextNumber"));
    }

    [TestMethod]
    public void Sec03_2_LongDecimal_WritesQword_AndHonoursItsMaximum()
    {
        var d = PolDocument.Create();
        Apply("Pol_LongDecimal_Basic", d, PolicyEdit.Enabled(("LongNumber", ElementValue.FromNumber(9000000000000))));
        Assert.AreEqual(9000000000000UL, Qw(d, K + @"\Elements\Number", "LongNumber"));
        Assert.AreEqual(9000000000000UL, ((NumberValue)ValueOf(Read("Pol_LongDecimal_Basic", d), "LongNumber")).Number);
        var over = Apply("Pol_LongDecimal_Basic", d, PolicyEdit.Enabled(("LongNumber", ElementValue.FromNumber(9000000000001))));
        Assert.AreEqual(PolicyProblemKind.AboveMaximum, over.Problems.Single().Kind);
        Assert.AreEqual(9000000000000UL, over.Problems.Single().Limit);
    }

    [TestMethod]
    public void Sec03_2_StoreAsText_WritesRegSzDigits_AndReadsThemBack()
    {
        var d = PolDocument.Create();
        Apply("Pol_Decimal_StoreAsText", d, PolicyEdit.Enabled(("TextNumber", ElementValue.FromNumber(8080))));
        Assert.AreEqual("8080", Sz(d, K + @"\Elements\Number", "TextNumber"));
        Assert.AreEqual(8080UL, ((NumberValue)ValueOf(Read("Pol_Decimal_StoreAsText", d), "TextNumber")).Number);
    }

    [TestMethod]
    public void Sec03_2_SoftDoesNotChangeTheRangeCheck()
    {
        var d = PolDocument.Create();
        var result = Apply("Pol_Decimal_Soft", d, PolicyEdit.Enabled(("SoftNumber", ElementValue.FromNumber(101))));
        Assert.AreEqual(PolicyProblemKind.AboveMaximum, result.Problems.Single().Kind);
    }

    [TestMethod]
    public void Sec03_2_ReadingAWrongTypeOrBadDigits_IsAbsent()
    {
        var d = PolDocument.Create();
        SetSz(d, K + @"\Elements\Number", "MinMaxNumber", "20");
        SetSz(d, K + @"\Elements\Number", "TextNumber", "not a number");
        Assert.IsFalse(Read("Pol_Decimal_MinMax", d).Elements.ContainsKey("MinMaxNumber"));
        Assert.IsFalse(Read("Pol_Decimal_StoreAsText", d).Elements.ContainsKey("TextNumber"));
    }

    // 3.3 text

    [TestMethod]
    public void Sec03_3_TextIsRegSzWithOneTerminator_ExpandableIsRegExpandSz()
    {
        var d = PolDocument.Create();
        var key = K + @"\Elements\Text";
        Apply("Pol_Text_Plain", d, PolicyEdit.Enabled(("PlainText", ElementValue.FromText("abc"))));
        var r = d.GetValue(key, "PlainText")!;
        Assert.AreEqual((uint)PolValueType.String, r.Type);
        Assert.AreEqual((3 + 1) * 2, r.Data.Length, "one terminator");
        Apply("Pol_Text_Expandable", d, PolicyEdit.Enabled(("ExpandText", ElementValue.FromText("%X%"))));
        Assert.AreEqual((uint)PolValueType.ExpandString, d.GetValue(key, "ExpandText")!.Type);
        Assert.AreEqual("%X%", ((TextValue)ValueOf(Read("Pol_Text_Expandable", d), "ExpandText")).Text);
    }

    [TestMethod]
    public void Sec03_3_ReadRequiresTheExactType()
    {
        var d = PolDocument.Create();
        var key = K + @"\Elements\Text";
        SetSz(d, key, "ExpandText", "x", PolValueType.String);
        SetSz(d, key, "PlainText", "y", PolValueType.ExpandString);
        Assert.IsFalse(Read("Pol_Text_Expandable", d).Elements.ContainsKey("ExpandText"));
        Assert.IsFalse(Read("Pol_Text_Plain", d).Elements.ContainsKey("PlainText"));
    }

    [TestMethod]
    public void Sec03_3_MaxLength_Nul_AndTheDefaultLimit()
    {
        var d = PolDocument.Create();
        Assert.IsTrue(Apply("Pol_Text_MaxLength", d, PolicyEdit.Enabled(("ShortText", ElementValue.FromText("123456789012")))).IsValid);
        var long13 = Apply("Pol_Text_MaxLength", d, PolicyEdit.Enabled(("ShortText", ElementValue.FromText("1234567890123"))));
        Assert.AreEqual(PolicyProblemKind.TooLong, long13.Problems.Single().Kind);
        Assert.AreEqual(12UL, long13.Problems.Single().Limit);

        var nul = Apply("Pol_Text_Plain", d, PolicyEdit.Enabled(("PlainText", ElementValue.FromText("a\0b"))));
        Assert.AreEqual(PolicyProblemKind.InvalidCharacter, nul.Problems.Single().Kind);

        Assert.IsTrue(Apply("Pol_Text_Plain", d, PolicyEdit.Enabled(("PlainText", ElementValue.FromText(new string('x', 1023))))).IsValid);
        var over = Apply("Pol_Text_Plain", d, PolicyEdit.Enabled(("PlainText", ElementValue.FromText(new string('x', 1024)))));
        Assert.AreEqual(PolicyProblemKind.TooLong, over.Problems.Single().Kind);
        Assert.AreEqual(1023UL, over.Problems.Single().Limit);
    }

    [TestMethod]
    public void Sec03_3_EmptyTextIsEmpty_RequiredEmptyIsAProblem_OptionalEmptyRemovesTheValue()
    {
        var d = PolDocument.Create();
        var req = Apply("Pol_Text_Required", d, PolicyEdit.Enabled(("RequiredText", ElementValue.FromText(""))));
        Assert.AreEqual(PolicyProblemKind.Required, req.Problems.Single().Kind);
        Apply("Pol_Text_Plain", d, PolicyEdit.Enabled(("PlainText", ElementValue.FromText("x"))));
        Apply("Pol_Text_Plain", d, PolicyEdit.Enabled(("PlainText", ElementValue.FromText(""))));
        Assert.IsNull(d.GetValue(K + @"\Elements\Text", "PlainText"));
    }

    [TestMethod]
    public void Sec09_TextRoundTrip_KeepsBlanksAndNonBasicPlaneCharacters()
    {
        var d = PolDocument.Create();
        var text = "  lead \U0001F600 and trail  ";
        Apply("Pol_Text_Plain", d, PolicyEdit.Enabled(("PlainText", ElementValue.FromText(text))));
        Assert.AreEqual(text, ((TextValue)ValueOf(Read("Pol_Text_Plain", Reparse(d)), "PlainText")).Text);
    }

    // 3.4 multiText

    [TestMethod]
    public void Sec03_4_MultiText_DropsEmptyLines_AndEndsWithTwoNuls()
    {
        var d = PolDocument.Create();
        var key = K + @"\Elements\Text";
        Apply("Pol_MultiText_Basic", d, PolicyEdit.Enabled(("MultiLines", ElementValue.FromLines("a", "", "b", ""))));
        var r = d.GetValue(key, "MultiLines")!;
        Assert.AreEqual((uint)PolValueType.MultiString, r.Type);
        Assert.AreEqual("a\0b\0\0", Encoding.Unicode.GetString(r.Data.Span));
        var read = (MultiTextValue)ValueOf(Read("Pol_MultiText_Basic", d), "MultiLines");
        CollectionAssert.AreEqual(new[] { "a", "b" }, read.Lines.ToArray());
    }

    [TestMethod]
    public void Sec03_4_MultiText_NoLinesIsEmpty_RemovesTheValue()
    {
        var d = PolDocument.Create();
        Apply("Pol_MultiText_Basic", d, PolicyEdit.Enabled(("MultiLines", ElementValue.FromLines("a"))));
        Apply("Pol_MultiText_Basic", d, PolicyEdit.Enabled(("MultiLines", ElementValue.FromLines("", ""))));
        Assert.IsNull(d.GetValue(K + @"\Elements\Text", "MultiLines"));
    }

    [TestMethod]
    public void Sec03_4_MultiText_Limits()
    {
        var d = PolDocument.Create();
        var many = Apply("Pol_MultiText_Limits", d, PolicyEdit.Enabled(("LimitedLines", ElementValue.FromLines("a", "b", "c", "d"))));
        Assert.AreEqual(PolicyProblemKind.TooManyLines, many.Problems.Single().Kind);
        Assert.AreEqual(3UL, many.Problems.Single().Limit);

        var tooLong = Apply("Pol_MultiText_Limits", d, PolicyEdit.Enabled(("LimitedLines", ElementValue.FromLines("aaaaaaaaaa", "bbbbbbbbbb"))));
        Assert.AreEqual(PolicyProblemKind.TooLong, tooLong.Problems.Single().Kind);
        Assert.AreEqual(20UL, tooLong.Problems.Single().Limit);

        Assert.IsTrue(Apply("Pol_MultiText_Limits", d, PolicyEdit.Enabled(("LimitedLines", ElementValue.FromLines("aaaaaaaaaa", "bbbbbbbbb")))).IsValid);
        var req = Apply("Pol_MultiText_Limits", d, PolicyEdit.Enabled(("LimitedLines", ElementValue.FromLines())));
        Assert.AreEqual(PolicyProblemKind.Required, req.Problems.Single().Kind);
    }

    [TestMethod]
    public void Sec03_4_MultiText_ReadsOnlyRegMultiSz()
    {
        var d = PolDocument.Create();
        SetSz(d, K + @"\Elements\Text", "MultiLines", "a");
        Assert.IsFalse(Read("Pol_MultiText_Basic", d).Elements.ContainsKey("MultiLines"));
    }

    // 3.5 enum

    [TestMethod]
    public void Sec03_5_EnumWritesTheItemValue_AndReadsTheFirstMatchingItem()
    {
        var d = PolDocument.Create();
        var key = K + @"\Elements\Choice";
        Apply("Pol_Enum_DecimalItems", d, PolicyEdit.Enabled(("DecimalChoice", ElementValue.FromChoice(2))));
        Assert.AreEqual(2u, Dw(d, key, "DecimalChoice"));
        Assert.AreEqual(2, ((ChoiceValue)ValueOf(Read("Pol_Enum_DecimalItems", d), "DecimalChoice")).ItemIndex);
        Apply("Pol_Enum_StringItems", d, PolicyEdit.Enabled(("StringChoice", ElementValue.FromChoice(1))));
        Assert.AreEqual("green", Sz(d, key, "StringChoice"));
        Assert.AreEqual(1, ((ChoiceValue)ValueOf(Read("Pol_Enum_StringItems", d), "StringChoice")).ItemIndex);
    }

    [TestMethod]
    public void Sec03_5_EnumIndexOutsideTheItems_IsInvalidChoice()
    {
        var d = PolDocument.Create();
        foreach (var index in new[] { -1, 4, 100 })
        {
            var result = Apply("Pol_Enum_DecimalItems", d, PolicyEdit.Enabled(("DecimalChoice", ElementValue.FromChoice(index))));
            Assert.AreEqual(PolicyProblemKind.InvalidChoice, result.Problems.Single().Kind, "index " + index);
        }

        Assert.IsTrue(d.IsEmpty);
    }

    [TestMethod]
    public void Sec03_5_EnumForeignValue_IsAbsent_AndRequiredEmptyIsAProblem()
    {
        var d = PolDocument.Create();
        Set1(d, K + @"\Elements\Choice", "DecimalChoice", 77);
        Assert.IsFalse(Read("Pol_Enum_DecimalItems", d).Elements.ContainsKey("DecimalChoice"));
        var req = Apply("Pol_Enum_Required", d, PolicyEdit.Enabled());
        Assert.AreEqual(PolicyProblemKind.Required, req.Problems.Single().Kind);
    }

    [TestMethod]
    public void Sec03_5_EnumValueLists_WrittenForTheChosenItem_AndTheOthersRemoved()
    {
        var d = PolDocument.Create();
        var choice = K + @"\Elements\Choice";
        Apply("Pol_Enum_ItemValueList", d, PolicyEdit.Enabled(("ProfileChoice", ElementValue.FromChoice(0))));
        Assert.AreEqual(1u, Dw(d, choice, "ProfileChoice"));
        Assert.AreEqual(100u, Dw(d, choice + @"\ProfileA", "ProfileAFirst"));
        Assert.AreEqual("second", Sz(d, choice + @"\ProfileAOther", "ProfileASecond"));

        Apply("Pol_Enum_ItemValueList", d, PolicyEdit.Enabled(("ProfileChoice", ElementValue.FromChoice(2))));
        Assert.AreEqual(3u, Dw(d, choice, "ProfileChoice"));
        Assert.AreEqual(300u, Dw(d, choice, "ProfileCOnly"));
        Assert.IsNull(d.GetValue(choice + @"\ProfileA", "ProfileAFirst"), "no stale list stays");
        Assert.IsNull(d.GetValue(choice + @"\ProfileAOther", "ProfileASecond"));

        Apply("Pol_Enum_ItemValueList", d, PolicyEdit.Enabled(("ProfileChoice", ElementValue.FromChoice(1))));
        Assert.AreEqual(2u, Dw(d, choice, "ProfileChoice"));
        Assert.IsNull(d.GetValue(choice, "ProfileCOnly"));
        Assert.AreEqual(1, ((ChoiceValue)ValueOf(Read("Pol_Enum_ItemValueList", d), "ProfileChoice")).ItemIndex);
    }

    [TestMethod]
    public void Sec03_5_EnumItemWithNoValueWritesOnlyItsValueList()
    {
        var policy = Tmp("Pol",
            "<elements><enum id=\"E\" valueName=\"E\">" +
            "<item displayName=\"$(string.Pol)\"><valueList defaultKey=\"Software\\Policies\\BlindT\\L\"><item valueName=\"x\"><value><decimal value=\"4\" /></value></item></valueList></item>" +
            "</enum></elements>", valueName: null);
        var d = PolDocument.Create();
        PolicyStateWriter.Apply(policy, d, PolicyEdit.Enabled(("E", ElementValue.FromChoice(0))));
        Assert.IsNull(d.GetValue(Key, "E"));
        Assert.AreEqual(4u, Dw(d, Key + @"\L", "x"));
    }

    // 3.6 lists

    private static ElementValue Entries(params string[] data) => ElementValue.FromList(data.Select(ListEntry.Of).ToArray());

    private static PolicyEdit EnableList(string id, params string[] data) => PolicyEdit.Enabled((id, Entries(data)));

    [TestMethod]
    public void Sec03_6_PlainList_NameIsTheData_AndDelvalsComesFirst()
    {
        var d = PolDocument.Create();
        var key = K + @"\Lists\Plain";
        Apply("Pol_List_Plain", d, EnableList("PlainList", "alpha", "beta"));
        CollectionAssert.AreEqual(new[] { "**delvals.", "alpha", "beta" }, Names(d, key));
        Assert.AreEqual("alpha", Sz(d, key, "alpha"));
        var snap = Read("Pol_List_Plain", d);
        Assert.AreEqual(PolicyState.Enabled, snap.State);
        var list = (ListValue)ValueOf(snap, "PlainList");
        CollectionAssert.AreEqual(new[] { "alpha", "beta" }, list.Entries.Select(e => e.Data).ToArray());
    }

    [TestMethod]
    public void Sec03_6_EmptyPrefixListNamesByData_PrefixedListNamesPrefixOneBased()
    {
        var d = PolDocument.Create();
        Apply("Pol_List_EmptyPrefix", d, EnableList("EmptyPrefixList", "p", "q"));
        CollectionAssert.AreEqual(new[] { "**delvals.", "p", "q" }, Names(d, K + @"\Lists\EmptyPrefix"));
        Apply("Pol_List_ValuePrefix", d, EnableList("PrefixList", "x", "y"));
        var key = K + @"\Lists\Prefix";
        Assert.AreEqual("x", Sz(d, key, "Item1"));
        Assert.AreEqual("y", Sz(d, key, "Item2"));
        var read = (ListValue)ValueOf(Read("Pol_List_ValuePrefix", d), "PrefixList");
        CollectionAssert.AreEqual(new[] { "x", "y" }, read.Entries.Select(e => e.Data).ToArray());
    }

    [TestMethod]
    public void Sec03_6_ExplicitValueList_UsesTheGivenNames_AndAMissingNameIsAProblem()
    {
        var d = PolDocument.Create();
        var key = K + @"\Lists\Explicit";
        var ok = PolicyEdit.Enabled(("ExplicitList", ElementValue.FromList(new ListEntry("n1", "d1"), new ListEntry("n2", "d2"))));
        Assert.IsTrue(Apply("Pol_List_ExplicitValue", d, ok).IsValid);
        Assert.AreEqual("d1", Sz(d, key, "n1"));
        Assert.AreEqual("d2", Sz(d, key, "n2"));
        var read = (ListValue)ValueOf(Read("Pol_List_ExplicitValue", d), "ExplicitList");
        CollectionAssert.AreEqual(new[] { "n1", "n2" }, read.Entries.Select(e => e.Name).ToArray());

        var bad = Apply("Pol_List_ExplicitValue", PolDocument.Create(),
            PolicyEdit.Enabled(("ExplicitList", ElementValue.FromList(new ListEntry("n1", "d1"), new ListEntry(null, "d2")))));
        var problem = bad.Problems.Single();
        Assert.AreEqual(PolicyProblemKind.EntryNameMissing, problem.Kind);
        Assert.AreEqual(1, problem.EntryIndex);
    }

    [TestMethod]
    public void Sec03_6_ExpandableListIsRegExpandSz()
    {
        var d = PolDocument.Create();
        Apply("Pol_List_Expandable", d, EnableList("ExpandList", "%X%"));
        Assert.AreEqual((uint)PolValueType.ExpandString, d.GetValue(K + @"\Lists\Expand", "%X%")!.Type);
    }

    [TestMethod]
    public void Sec03_6_ListEntryProblems()
    {
        var d = PolDocument.Create();
        var empty = Apply("Pol_List_Plain", d, EnableList("PlainList", "ok", ""));
        Assert.AreEqual(PolicyProblemKind.EntryEmpty, empty.Problems.Single().Kind);
        Assert.AreEqual(1, empty.Problems.Single().EntryIndex);

        var dup = Apply("Pol_List_Plain", d, EnableList("PlainList", "same", "SAME"));
        Assert.AreEqual(PolicyProblemKind.DuplicateEntry, dup.Problems.Single().Kind);
        Assert.IsNotNull(dup.Problems.Single().EntryIndex);

        var star = Apply("Pol_List_Plain", d, EnableList("PlainList", "**instruction"));
        Assert.AreEqual(PolicyProblemKind.InvalidCharacter, star.Problems.Single().Kind);

        var nul = Apply("Pol_List_Plain", d, EnableList("PlainList", "a\0b"));
        Assert.AreEqual(PolicyProblemKind.InvalidCharacter, nul.Problems.Single().Kind);

        var starName = Apply("Pol_List_ExplicitValue", d,
            PolicyEdit.Enabled(("ExplicitList", ElementValue.FromList(new ListEntry("**del.x", "d")))));
        Assert.AreEqual(PolicyProblemKind.InvalidCharacter, starName.Problems.Single().Kind);
        Assert.IsTrue(d.IsEmpty, "invalid edits write nothing");
    }

    [TestMethod]
    public void Sec03_6_EmptyListRemovesEveryValueOfTheKey_RequiredEmptyIsAProblem()
    {
        var d = PolDocument.Create();
        var key = K + @"\Lists\Plain";
        Apply("Pol_List_Plain", d, EnableList("PlainList", "a", "b"));
        SetSz(d, key, "foreign", "x");
        Apply("Pol_List_Plain", d, PolicyEdit.Enabled(("PlainList", ElementValue.FromList())));
        Assert.AreEqual(0, d.GetRecords(key).Count(r => !r.IsInstruction), "every value of the key is removed");

        var required = Tmp("Pol", "<elements><list id=\"L\" key=\"Software\\Policies\\BlindT\\L\" required=\"true\" /></elements>", valueName: null);
        var result = PolicyStateWriter.Apply(required, PolDocument.Create(), PolicyEdit.Enabled(("L", ElementValue.FromList())));
        Assert.AreEqual(PolicyProblemKind.Required, result.Problems.Single().Kind);
    }

    [TestMethod]
    public void Sec03_6_NonAdditive_ReplacesTheWholeKey()
    {
        var d = PolDocument.Create();
        var key = K + @"\Lists\Plain";
        SetSz(d, key, "foreign", "x");
        Apply("Pol_List_Plain", d, EnableList("PlainList", "a"));
        CollectionAssert.AreEqual(new[] { "**delvals.", "a" }, Names(d, key));
    }

    [TestMethod]
    public void Sec03_6_ReaderIgnoresForeignTypes_AndKeepsModelOrder()
    {
        var d = PolDocument.Create();
        var key = K + @"\Lists\Plain";
        SetSz(d, key, "one", "one");
        Set1(d, key, "dwordIsForeign", 5);
        SetSz(d, key, "two", "two");
        var list = (ListValue)ValueOf(Read("Pol_List_Plain", d), "PlainList");
        CollectionAssert.AreEqual(new[] { "one", "two" }, list.Entries.Select(e => e.Data).ToArray());
    }

    [TestMethod]
    public void Sec03_6_TwoPoliciesSharingAListKey_ReadTheSameEntries_AndNotConfiguredClearsTheKey()
    {
        var d = PolDocument.Create();
        var key = K + @"\Shared\SharedList";
        Apply("Pol_SharedList_One", d, EnableList("SharedListOne", "a", "b"));
        var two = (ListValue)ValueOf(Read("Pol_SharedList_Two", d), "SharedListTwo");
        CollectionAssert.AreEqual(new[] { "a", "b" }, two.Entries.Select(e => e.Data).ToArray());
        Apply("Pol_SharedList_Two", d, PolicyEdit.NotConfigured);
        Assert.AreEqual(0, d.GetRecords(key).Count);
        Assert.AreEqual(PolicyState.NotConfigured, Read("Pol_SharedList_One", d).State);
    }

    // ---------------------------------------------------------------- 4. Order and 5. foreign data

    [TestMethod]
    public void Sec04_ProbeReplay_SharedZetaThenAlpha_ThenDisableZeta_GivesAlphaThenZeta()
    {
        var d = PolDocument.Create();
        var key = K + @"\Shared";
        Apply("Pol_Shared_Zeta", d, PolicyEdit.Enabled());
        Apply("Pol_Shared_Alpha", d, PolicyEdit.Enabled());
        CollectionAssert.AreEqual(new[] { "Zeta", "Alpha" }, Names(d, key));
        Apply("Pol_Shared_Zeta", d, PolicyEdit.Disabled);
        CollectionAssert.AreEqual(new[] { "Alpha", "Zeta" }, Names(d, key));
        Assert.AreEqual(0u, Dw(d, key, "Zeta"));
    }

    [TestMethod]
    public void Sec04_SetWithIdenticalDataStays_SetWithDifferentDataMovesToTheEnd()
    {
        var d = PolDocument.Create();
        Apply("Pol_Class_Machine", d, PolicyEdit.Enabled());
        Apply("Pol_Class_User", d, PolicyEdit.Enabled());
        Apply("Pol_Class_Machine", d, PolicyEdit.Enabled());
        CollectionAssert.AreEqual(new[] { "MachineFlag", "UserFlag" }, Names(d, Basics));
        Apply("Pol_Class_Machine", d, PolicyEdit.Disabled);
        CollectionAssert.AreEqual(new[] { "UserFlag", "MachineFlag" }, Names(d, Basics));
    }

    [TestMethod]
    public void Sec13_ElementsAreWrittenInTemplateOrder_NotNameOrder()
    {
        var d = PolDocument.Create();
        var key = K + @"\Shared";
        var edit = PolicyEdit.Enabled(("AlphaElement", ElementValue.FromNumber(1)), ("ZetaElement", ElementValue.FromText("z")));
        Apply("Pol_Shared_ElementsReverseOrder", d, edit);
        CollectionAssert.AreEqual(new[] { "Gate", "ZetaElement", "AlphaElement" }, Names(d, key));

        var e = PolDocument.Create();
        Apply("Pol_Bool_TwoChecksReverseOrder", e, PolicyEdit.Enabled(
            ("AlphaCheck", ElementValue.FromBoolean(true)), ("ZetaCheck", ElementValue.FromBoolean(true))));
        CollectionAssert.AreEqual(new[] { "ZetaCheck", "AlphaCheck" }, Names(e, K + @"\Elements\Bool\Order"));
    }

    [TestMethod]
    public void Sec05_ForeignValuesStayByteForByte_AndAreNotOwned()
    {
        var d = PolDocument.Create();
        SetSz(d, Basics, "Foreign", "keep me");
        var before = d.GetValue(Basics, "Foreign")!.RawBytes.ToArray();
        Apply("Pol_Class_Machine", d, PolicyEdit.Enabled());
        Apply("Pol_Class_Machine", d, PolicyEdit.Disabled);
        var snap = Read("Pol_Class_Machine", d);
        CollectionAssert.AreEqual(before, d.GetValue(Basics, "Foreign")!.RawBytes.ToArray());
        Assert.IsFalse(snap.OwnedRecords.Any(r => r.ValueName.Equals("Foreign", StringComparison.OrdinalIgnoreCase)));
        Apply("Pol_Class_Machine", d, PolicyEdit.NotConfigured);
        Assert.IsNotNull(d.GetValue(Basics, "Foreign"));
    }

    [TestMethod]
    public void Sec05_OwnedRecords_ListValuesInstructionsAndEveryRecordOfAListKey()
    {
        var d = PolDocument.Create();
        Apply("Pol_Value_EnabledOnly", d, PolicyEdit.Disabled);
        var owned = Read("Pol_Value_EnabledOnly", d).OwnedRecords;
        Assert.IsTrue(owned.Any(r => r.ValueName == "**del.EnabledOnly" && r.Key.Equals(Basics, StringComparison.OrdinalIgnoreCase)));

        var l = PolDocument.Create();
        var key = K + @"\Lists\Plain";
        SetSz(l, key, "a", "a");
        SetSz(l, key, "b", "b");
        var listOwned = Read("Pol_List_Plain", l).OwnedRecords;
        Assert.IsTrue(listOwned.Any(r => r.ValueName == "a"));
        Assert.IsTrue(listOwned.Any(r => r.ValueName == "b"));
    }

    [TestMethod]
    public void Sec05_ClassBoth_EachDocumentIsIndependent()
    {
        var machine = PolDocument.Create();
        var user = PolDocument.Create();
        Apply("Pol_Class_Both", machine, PolicyEdit.Enabled());
        Assert.AreEqual(PolicyState.Enabled, Read("Pol_Class_Both", machine).State);
        Assert.AreEqual(PolicyState.NotConfigured, Read("Pol_Class_Both", user).State);
        Apply("Pol_Class_Both", user, PolicyEdit.Disabled);
        Assert.AreEqual(PolicyState.Enabled, Read("Pol_Class_Both", machine).State);
        Assert.AreEqual(PolicyState.Disabled, Read("Pol_Class_Both", user).State);
    }

    [TestMethod]
    public void Sec05_ATemplateKeyThePolDocumentRefuses_ThrowsArgumentException()
    {
        var leading = Tmp("Pol", "", key: @"\Software\Bad");
        Assert.Throws<ArgumentException>(() => PolicyStateWriter.Apply(leading, PolDocument.Create(), PolicyEdit.Enabled()));
        var star = Tmp("Pol", "", valueName: "**oops");
        Assert.Throws<ArgumentException>(() => PolicyStateWriter.Apply(star, PolDocument.Create(), PolicyEdit.Enabled()));
    }

    [TestMethod]
    public void Sec12_RecordsAreFoundIgnoringTheCaseOfKeyAndName()
    {
        var d = PolDocument.Create();
        Set1(d, Basics.ToUpperInvariant(), "MACHINEFLAG", 1);
        Assert.AreEqual(PolicyState.Enabled, Read("Pol_Class_Machine", d).State);
    }

    // ---------------------------------------------------------------- 6. Validation

    [TestMethod]
    public void Sec06_UnknownElement_IsAProblem_WithTheIdAsGiven()
    {
        var d = PolDocument.Create();
        var result = Apply("Pol_Decimal_MinMax", d, PolicyEdit.Enabled(("NoSuchElement", ElementValue.FromNumber(1))));
        var problem = result.Problems.Single();
        Assert.AreEqual(PolicyProblemKind.UnknownElement, problem.Kind);
        Assert.AreEqual("NoSuchElement", problem.ElementId);
        Assert.IsTrue(d.IsEmpty);
    }

    [TestMethod]
    public void Sec06_WrongValueKind_IsAProblem()
    {
        var d = PolDocument.Create();
        var result = Apply("Pol_Decimal_MinMax", d, PolicyEdit.Enabled(("MinMaxNumber", ElementValue.FromText("12"))));
        Assert.AreEqual(PolicyProblemKind.WrongValueKind, result.Problems.Single().Kind);
        Assert.IsTrue(d.IsEmpty);
        var bool_ = Apply("Pol_Bool_TrueFalseValues", d, PolicyEdit.Enabled(("TrueFalseCheck", ElementValue.FromNumber(1))));
        Assert.AreEqual(PolicyProblemKind.WrongValueKind, bool_.Problems.Single().Kind);
    }

    [TestMethod]
    public void Sec08_NullArgumentsAreArgumentErrors()
    {
        var policy = Pol("Pol_Class_Machine");
        Assert.Throws<ArgumentNullException>(() => PolicyStateReader.Read(null!, PolDocument.Create()));
        Assert.Throws<ArgumentNullException>(() => PolicyStateReader.Read(policy, null!));
        Assert.Throws<ArgumentNullException>(() => PolicyStateWriter.Apply(null!, PolDocument.Create(), PolicyEdit.Enabled()));
        Assert.Throws<ArgumentNullException>(() => PolicyStateWriter.Apply(policy, null!, PolicyEdit.Enabled()));
        Assert.Throws<ArgumentNullException>(() => PolicyStateWriter.Apply(policy, PolDocument.Create(), null!));
        Assert.Throws<ArgumentNullException>(() => PolicyEditValidator.Validate(null!, PolicyEdit.Enabled()));
        Assert.Throws<ArgumentNullException>(() => PolicyEditValidator.Validate(policy, null!));
    }

    [TestMethod]
    public void Sec08_ReadDoesNotChangeTheDocument_AndElementIdsIgnoreCase()
    {
        var d = PolDocument.Create();
        Apply("Pol_Decimal_MinMax", d, PolicyEdit.Enabled(("minmaxnumber", ElementValue.FromNumber(20))));
        var parsed = Reparse(d);
        var bytes = parsed.ToBytes();
        var snap = Read("Pol_Decimal_MinMax", parsed);
        CollectionAssert.AreEqual(bytes, parsed.ToBytes());
        Assert.IsFalse(parsed.HasChanges);
        Assert.AreEqual(20UL, ((NumberValue)snap.Elements["MinMaxNumber"]).Number);
        Assert.AreEqual(20UL, ((NumberValue)snap.Elements["MINMAXNUMBER"]).Number);
    }

    [TestMethod]
    public void Sec10_EveryProblemIsListed_InElementOrder_AndNothingIsWritten()
    {
        var d = PolDocument.Create();
        var edit = PolicyEdit.Enabled(
            ("AlphaElement", ElementValue.FromText("wrong kind")),
            ("Ghost", ElementValue.FromNumber(1)),
            ("ZetaElement", ElementValue.FromText("fine\0nul")));
        var result = Apply("Pol_Shared_ElementsReverseOrder", d, edit);
        var kinds = result.Problems.Select(p => (p.ElementId, p.Kind)).ToList();
        Assert.AreEqual(3, kinds.Count, string.Join(",", kinds));
        CollectionAssert.Contains(kinds, ("Ghost", PolicyProblemKind.UnknownElement));
        var ofElements = kinds.Where(k => k.ElementId != "Ghost").ToList();
        Assert.AreEqual(("ZetaElement", PolicyProblemKind.InvalidCharacter), ofElements[0], "template order of the elements");
        Assert.AreEqual(("AlphaElement", PolicyProblemKind.WrongValueKind), ofElements[1]);
        Assert.IsTrue(d.IsEmpty);
        CollectionAssert.AreEqual(
            result.Problems.ToArray(), PolicyEditValidator.Validate(Pol("Pol_Shared_ElementsReverseOrder"), edit).ToArray());
    }

    // ---------------------------------------------------------------- 9. Idempotence and round trips

    [TestMethod]
    public void Sec09_EveryEditAppliedTwice_ChangesNothingTheSecondTime()
    {
        var cases = new (string Policy, PolicyEdit Edit)[]
        {
            ("Pol_Class_Machine", PolicyEdit.Enabled()),
            ("Pol_Class_Machine", PolicyEdit.Disabled),
            ("Pol_Value_EnabledOnly", PolicyEdit.Disabled),
            ("Pol_Value_EnabledListOnly", PolicyEdit.Enabled()),
            ("Pol_Value_DisabledListOnly", PolicyEdit.Disabled),
            ("Pol_Value_NoValueName_ElementsOnly", PolicyEdit.Disabled),
            ("Pol_Decimal_MinMax", PolicyEdit.Enabled(("MinMaxNumber", ElementValue.FromNumber(30)))),
            ("Pol_Text_Plain", PolicyEdit.Enabled(("PlainText", ElementValue.FromText("same")))),
            ("Pol_MultiText_Basic", PolicyEdit.Enabled(("MultiLines", ElementValue.FromLines("a", "b")))),
            ("Pol_Enum_ItemValueList", PolicyEdit.Enabled(("ProfileChoice", ElementValue.FromChoice(0)))),
            ("Pol_Bool_TrueFalseLists", PolicyEdit.Enabled(("ListCheck", ElementValue.FromBoolean(false)))),
            ("Pol_List_Plain", EnableList("PlainList", "a", "b")),
            ("Pol_List_Plain", PolicyEdit.Disabled),
            ("Pol_List_ValuePrefix", EnableList("PrefixList", "a", "b")),
        };
        foreach (var (policy, edit) in cases)
        {
            var d = PolDocument.Create();
            Apply(policy, d, edit);
            var again = Reparse(d);
            var second = Apply(policy, again, edit);
            Assert.IsTrue(second.IsValid, policy);
            Assert.IsFalse(second.Changed, policy + " Changed");
            Assert.IsFalse(again.HasChanges, policy + " HasChanges");
        }
    }

    [TestMethod]
    public void Sec09_NotConfiguredOnTheOnlyPolicy_LeavesAnEmptyDocument_AndOnNothingReportsNoChange()
    {
        var d = PolDocument.Create();
        AssertNoChange(Apply("Pol_Value_Decimal", d, PolicyEdit.NotConfigured), d);
        Apply("Pol_Value_Decimal", d, PolicyEdit.Enabled());
        Assert.IsTrue(Apply("Pol_Value_Decimal", d, PolicyEdit.NotConfigured).Changed);
        Assert.IsTrue(d.IsEmpty);
    }

    [TestMethod]
    public void Sec09_RoundTripOfEveryElementKind()
    {
        var d = PolDocument.Create();
        void Check(string policy, string id, ElementValue value)
        {
            Apply(policy, d, PolicyEdit.Enabled((id, value)));
            var back = Read(policy, Reparse(d)).Elements[id];
            switch (value)
            {
                case ListValue expected:
                    CollectionAssert.AreEqual(expected.Entries.Select(e => e.Data).ToArray(), ((ListValue)back).Entries.Select(e => e.Data).ToArray());
                    break;
                case MultiTextValue expected:
                    CollectionAssert.AreEqual(expected.Lines.ToArray(), ((MultiTextValue)back).Lines.ToArray());
                    break;
                default:
                    Assert.AreEqual(value, back, policy);
                    break;
            }
        }

        Check("Pol_Bool_TrueFalseValues", "TrueFalseCheck", ElementValue.FromBoolean(true));
        Check("Pol_Decimal_MinMax", "MinMaxNumber", ElementValue.FromNumber(250));
        Check("Pol_LongDecimal_Basic", "LongNumber", ElementValue.FromNumber(8000000000000));
        Check("Pol_Text_Plain", "PlainText", ElementValue.FromText("round trip"));
        Check("Pol_MultiText_Basic", "MultiLines", ElementValue.FromLines("one", "two", "three"));
        Check("Pol_Enum_StringItems", "StringChoice", ElementValue.FromChoice(2));
        Check("Pol_List_Plain", "PlainList", Entries("a", "b", "c"));
    }

    // ---------------------------------------------------------------- 7. List instructions

    private const string PlainKey = K + @"\Lists\Plain";
    private const string AdditiveKey = K + @"\Lists\Additive";

    [TestMethod]
    public void Sec07_1_DelvalsIsFirst_NeverStacks_AndARepeatedApplyChangesNothing()
    {
        var d = PolDocument.Create();
        Apply("Pol_List_Plain", d, EnableList("PlainList", "a", "b"));
        var again = Reparse(d);
        AssertNoChange(Apply("Pol_List_Plain", again, EnableList("PlainList", "a", "b")), again);
        Assert.AreEqual(1, again.GetRecords(PlainKey).Count(r => r.ValueName.Equals("**delvals.", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual("**delvals.", again.GetRecords(PlainKey)[0].ValueName);
        var data = again.GetValue(PlainKey, "**delvals.")!;
        Assert.AreEqual((uint)PolValueType.String, data.Type);
        CollectionAssert.AreEqual(new byte[] { 0x20, 0, 0, 0 }, data.Data.ToArray());
    }

    // Open: the doc contradicts itself. Section 7.1 item 3 says changing one entry moves only that entry to the end
    // (a, c, b). The table row for a non-additive list says "clear the values of the key, `**delvals.` at the front,
    // then the entries in order" (a, b, c). The product does the second.
    [TestMethod]
    [Ignore("open: non-additive list with one changed entry: moves only the changed entry (7.1 item 3, a,c,b) or clears and rewrites all in entry order (7.1 table row 1, a,b,c)?")]
    public void Sec07_1_ChangingOneEntryMovesOnlyThatEntry_DelvalsStaysFirst()
    {
        var d = PolDocument.Create();
        Apply("Pol_List_ExplicitValue", d, PolicyEdit.Enabled(("ExplicitList",
            ElementValue.FromList(new ListEntry("a", "1"), new ListEntry("b", "2"), new ListEntry("c", "3")))));
        Apply("Pol_List_ExplicitValue", d, PolicyEdit.Enabled(("ExplicitList",
            ElementValue.FromList(new ListEntry("a", "1"), new ListEntry("b", "changed"), new ListEntry("c", "3")))));
        var key = K + @"\Lists\Explicit";
        CollectionAssert.AreEqual(new[] { "**delvals.", "a", "c", "b" }, Names(d, key));
        Assert.AreEqual("changed", Sz(d, key, "b"));
    }

    [TestMethod]
    public void Sec07_1_DisabledClearsTheKeyAndWritesDelvals_ListOnlyPolicyReadsDisabled()
    {
        var d = PolDocument.Create();
        Apply("Pol_List_Plain", d, EnableList("PlainList", "a", "b"));
        Apply("Pol_List_Plain", d, PolicyEdit.Disabled);
        CollectionAssert.AreEqual(new[] { "**delvals." }, Names(d, PlainKey));
        Assert.AreEqual(PolicyState.Disabled, Read("Pol_List_Plain", d).State);
    }

    [TestMethod]
    public void Sec07_1_AdditiveDisabled_ClearsTheKeyLikeTheNonAdditiveList()
    {
        var d = PolDocument.Create();
        SetSz(d, AdditiveKey, "foreign", "x");
        Apply("Pol_List_Additive", d, EnableList("AdditiveList", "a"));
        Apply("Pol_List_Additive", d, PolicyEdit.Disabled);
        CollectionAssert.AreEqual(new[] { "**delvals." }, Names(d, AdditiveKey));
    }

    [TestMethod]
    public void Sec07_1_NotConfigured_RemovesValuesAndDelvals_TheKeyIsGone()
    {
        var d = PolDocument.Create();
        Apply("Pol_List_Plain", d, EnableList("PlainList", "a", "b"));
        Apply("Pol_List_Plain", d, PolicyEdit.NotConfigured);
        Assert.AreEqual(0, d.GetRecords(PlainKey).Count);
        Assert.IsTrue(d.IsEmpty);
    }

    [TestMethod]
    public void Sec07_1_NotConfigured_KeepsOtherInstructionsAndASoftRecord()
    {
        var d = Crafted((PlainKey, "**unknown", (uint)PolValueType.String, PolData.FromString("u")));
        Apply("Pol_List_Plain", d, EnableList("PlainList", "a"));
        d.AddSoftInstruction(PlainKey, "other");
        Apply("Pol_List_Plain", d, PolicyEdit.NotConfigured);
        var names = Names(d, PlainKey);
        CollectionAssert.Contains(names, "**soft.other");
        CollectionAssert.Contains(names, "**unknown");
        CollectionAssert.DoesNotContain(names, "**delvals.");
        CollectionAssert.DoesNotContain(names, "a");
    }

    [TestMethod]
    public void Sec07_1_SoftRecordInAListKey_IsKeptByEnabledAndDisabled_AndIgnoredByTheReader()
    {
        var d = PolDocument.Create();
        d.AddSoftInstruction(PlainKey, "keepme");
        Apply("Pol_List_Plain", d, EnableList("PlainList", "a"));
        CollectionAssert.Contains(Names(d, PlainKey), "**soft.keepme");
        var list = (ListValue)ValueOf(Read("Pol_List_Plain", d), "PlainList");
        CollectionAssert.AreEqual(new[] { "a" }, list.Entries.Select(e => e.Data).ToArray());
        Apply("Pol_List_Plain", d, PolicyEdit.Disabled);
        CollectionAssert.Contains(Names(d, PlainKey), "**soft.keepme");
        Assert.IsTrue(Read("Pol_List_Plain", d).OwnedRecords.Any(r => r.ValueName == "**soft.keepme"));
    }

    [TestMethod]
    public void Sec07_1_WritingAnEntryNamedLikeASoftTarget_RemovesItsSoftAndDeleteRecords()
    {
        var d = PolDocument.Create();
        d.AddSoftInstruction(PlainKey, "a");
        d.AddDeleteInstruction(PlainKey, "a");
        Apply("Pol_List_Plain", d, EnableList("PlainList", "a"));
        CollectionAssert.DoesNotContain(Names(d, PlainKey), "**soft.a");
        CollectionAssert.DoesNotContain(Names(d, PlainKey), "**del.a");
    }

    [TestMethod]
    public void Sec07_1_UpperCaseDelvals_IsFound_AndKeptWhenFirst()
    {
        var d = Crafted((PlainKey, "**DELVALS.", (uint)PolValueType.String, Marker));
        Assert.AreEqual(PolicyState.Disabled, Read("Pol_List_Plain", d).State);
        Apply("Pol_List_Plain", d, EnableList("PlainList", "a"));
        var names = Names(d, PlainKey);
        Assert.AreEqual(1, names.Count(n => n.Equals("**delvals.", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual("**DELVALS.", names[0], "the first canonical record keeps its spelling");
    }

    [TestMethod]
    public void Sec07_1_AKeyWithOnlyDelvals_ListOnlyReadsDisabled_ValueNamePolicyTakesItsOwnValue()
    {
        var d = PolDocument.Create();
        d.AddDeleteValuesInstruction(PlainKey);
        var snap = Read("Pol_List_Plain", d);
        Assert.AreEqual(PolicyState.Disabled, snap.State);
        Assert.IsFalse(snap.Elements.ContainsKey("PlainList") && ((ListValue)snap.Elements["PlainList"]).Entries.Count > 0);

        var e = PolDocument.Create();
        e.AddDeleteValuesInstruction(K + @"\Lists\Switched");
        Assert.AreEqual(PolicyState.NotConfigured, Read("Pol_List_PolicyValueName", e).State);
    }

    [TestMethod]
    public void Sec07_1_ListUnderAPolicyValueName_WritesTheSwitchAndTheList()
    {
        var d = PolDocument.Create();
        var switched = K + @"\Lists\Switched";
        Apply("Pol_List_PolicyValueName", d, PolicyEdit.Enabled(("SwitchedList", Entries("a", "b"))));
        Assert.AreEqual(1u, Dw(d, K + @"\Lists", "ListSwitch"));
        CollectionAssert.AreEqual(new[] { "**delvals.", "a", "b" }, Names(d, switched));
        Assert.AreEqual(PolicyState.Enabled, Read("Pol_List_PolicyValueName", d).State);
        Apply("Pol_List_PolicyValueName", d, PolicyEdit.Disabled);
        Assert.AreEqual(0u, Dw(d, K + @"\Lists", "ListSwitch"));
        CollectionAssert.AreEqual(new[] { "**delvals." }, Names(d, switched));
        Assert.AreEqual(PolicyState.Disabled, Read("Pol_List_PolicyValueName", d).State);
    }

    [TestMethod]
    public void Sec07_1_Additive_KeepsForeignValues_RemovesDelvals_AndAddsTheEntries()
    {
        var d = PolDocument.Create();
        SetSz(d, AdditiveKey, "foreign", "x");
        d.AddDeleteValuesInstruction(AdditiveKey);
        Apply("Pol_List_Additive", d, EnableList("AdditiveList", "a", "b"));
        var names = Names(d, AdditiveKey);
        CollectionAssert.DoesNotContain(names, "**delvals.");
        CollectionAssert.AreEqual(new[] { "foreign", "a", "b" }, names);
    }

    [TestMethod]
    public void Sec07_1_AdditiveEmpty_RemovesOnlyTheRemovedEntriesAndDelvals_NothingElse()
    {
        var d = PolDocument.Create();
        SetSz(d, AdditiveKey, "foreign", "x");
        SetSz(d, AdditiveKey, "gone", "gone");
        d.AddDeleteValuesInstruction(AdditiveKey);
        var edit = PolicyEdit.Enabled(("AdditiveList", ElementValue.FromListWithRemoved([], [ListEntry.Of("gone")])));
        Apply("Pol_List_Additive", d, edit);
        CollectionAssert.AreEqual(new[] { "foreign" }, Names(d, AdditiveKey));
    }

    [TestMethod]
    public void Sec07_1_AdditiveRemoval_IsConservative()
    {
        var d = PolDocument.Create();
        SetSz(d, AdditiveKey, "same", "same");
        SetSz(d, AdditiveKey, "other", "changedByOthers");
        SetSz(d, AdditiveKey, "kept", "kept");
        var edit = PolicyEdit.Enabled(("AdditiveList", ElementValue.FromListWithRemoved(
            [ListEntry.Of("kept")], [ListEntry.Of("same"), ListEntry.Of("other"), ListEntry.Of("kept")])));
        Apply("Pol_List_Additive", d, edit);
        var names = Names(d, AdditiveKey);
        CollectionAssert.DoesNotContain(names, "same", "same data: removed");
        CollectionAssert.Contains(names, "other", "different data: stays");
        CollectionAssert.Contains(names, "kept", "also an entry of the new list: stays");
    }

    [TestMethod]
    public void Sec07_1_RemovedOfANonAdditiveList_IsIgnored()
    {
        var d = PolDocument.Create();
        var edit = PolicyEdit.Enabled(("PlainList", ElementValue.FromListWithRemoved([ListEntry.Of("a")], [ListEntry.Of("zzz")])));
        Assert.IsTrue(Apply("Pol_List_Plain", d, edit).IsValid);
        CollectionAssert.AreEqual(new[] { "**delvals.", "a" }, Names(d, PlainKey));
    }

    [TestMethod]
    public void Sec07_1_UnknownInstructionsInAListKey_AreNeverTouched_NorMoved()
    {
        var d = Crafted((PlainKey, "**mystery", (uint)PolValueType.String, PolData.FromString("m")));
        var before = d.GetValue(PlainKey, "**mystery")!.RawBytes.ToArray();
        Apply("Pol_List_Plain", d, EnableList("PlainList", "a"));
        Apply("Pol_List_Plain", d, EnableList("PlainList", "b"));
        Apply("Pol_List_Plain", d, PolicyEdit.Disabled);
        CollectionAssert.AreEqual(before, d.GetValue(PlainKey, "**mystery")!.RawBytes.ToArray());
        var list = Read("Pol_List_Plain", d);
        Assert.IsTrue(list.OwnedRecords.Any(r => r.ValueName == "**mystery"));
        var enabled = Crafted((PlainKey, "**mystery", (uint)PolValueType.String, PolData.FromString("m")));
        SetSz(enabled, PlainKey, "e", "e");
        var entries = (ListValue)ValueOf(Read("Pol_List_Plain", enabled), "PlainList");
        CollectionAssert.AreEqual(new[] { "e" }, entries.Entries.Select(e => e.Data).ToArray());
    }

    [TestMethod]
    public void Sec07_1_DelvalsBehindTheEntries_StillReadsEnabledWithTheEntries()
    {
        var d = Crafted(
            (PlainKey, "a", (uint)PolValueType.String, PolData.FromString("a")),
            (PlainKey, "**delvals.", (uint)PolValueType.String, Marker));
        var snap = Read("Pol_List_Plain", d);
        Assert.AreEqual(PolicyState.Enabled, snap.State);
        var list = (ListValue)ValueOf(snap, "PlainList");
        CollectionAssert.AreEqual(new[] { "a" }, list.Entries.Select(e => e.Data).ToArray());
        Assert.IsTrue(snap.OwnedRecords.Any(r => r.ValueName == "**delvals."));
    }

    [TestMethod]
    public void Sec07_1_SharedKeyWithOneAdditiveAndOneNot_FollowsTheDocumentedCost()
    {
        // Pol_SharedList_One and Two share one non-additive key here; enabling the second replaces the key.
        var d = PolDocument.Create();
        var key = K + @"\Shared\SharedList";
        Apply("Pol_SharedList_One", d, EnableList("SharedListOne", "a", "b"));
        Apply("Pol_SharedList_Two", d, EnableList("SharedListTwo", "c"));
        CollectionAssert.AreEqual(new[] { "**delvals.", "c" }, Names(d, key));
        var one = (ListValue)ValueOf(Read("Pol_SharedList_One", d), "SharedListOne");
        CollectionAssert.AreEqual(new[] { "c" }, one.Entries.Select(e => e.Data).ToArray());
    }

    [TestMethod]
    public void Sec07_1_EmptyEnabledNonAdditiveList_RemovesValuesAndDelvals_AndWritesNothing()
    {
        var d = PolDocument.Create();
        Apply("Pol_List_Plain", d, EnableList("PlainList", "a"));
        Apply("Pol_List_Plain", d, PolicyEdit.Enabled(("PlainList", ElementValue.FromList())));
        Assert.AreEqual(0, d.GetRecords(PlainKey).Count);
    }

    // ---------------------------------------------------------------- Extra adversarial cases

    [TestMethod]
    public void Sec03_AnElementIsOnlyReadWhenThePolicyIsEnabled()
    {
        var d = PolDocument.Create();
        Set1(d, Basics, "BothElementFlag", 0);
        Set1(d, Basics, "BothNumber", 5);
        var disabled = Read("Pol_Both_WithElements", d);
        Assert.AreEqual(PolicyState.Disabled, disabled.State);
        Assert.IsFalse(disabled.Elements.ContainsKey("BothNumber"), "Disabled reads no element");

        var e = PolDocument.Create();
        Set1(e, Basics, "BothNumber", 5);
        var none = Read("Pol_Both_WithElements", e);
        Assert.AreEqual(PolicyState.NotConfigured, none.State);
        Assert.IsFalse(none.Elements.ContainsKey("BothNumber"), "Not Configured reads no element");

        var f = PolDocument.Create();
        Set1(f, Basics, "BothElementFlag", 1);
        Set1(f, Basics, "BothNumber", 5);
        Assert.AreEqual(5UL, ((NumberValue)Read("Pol_Both_WithElements", f).Elements["BothNumber"]).Number);
    }

    [TestMethod]
    public void Sec04_TheValueIsWrittenBeforeTheElements()
    {
        var d = PolDocument.Create();
        Apply("Pol_Both_WithElements", d, PolicyEdit.Enabled(("BothNumber", ElementValue.FromNumber(5))));
        CollectionAssert.AreEqual(new[] { "BothElementFlag", "BothNumber" }, Names(d, Basics));
        Assert.AreEqual(1u, Dw(d, Basics, "BothElementFlag"));
    }

    [TestMethod]
    public void Sec01_NotConfigured_RemovesWhateverRecordIsAtItsName_EvenOfAForeignType()
    {
        var d = PolDocument.Create();
        SetSz(d, Basics, "MachineFlag", "foreign type");
        SetSz(d, Basics, "Neighbour", "stays");
        Apply("Pol_Class_Machine", d, PolicyEdit.NotConfigured);
        Assert.IsNull(d.GetValue(Basics, "MachineFlag"));
        Assert.IsNotNull(d.GetValue(Basics, "Neighbour"));
    }

    [TestMethod]
    public void Sec01_2_SoftIsNeverWrittenByThePolicyEngine()
    {
        var d = PolDocument.Create();
        Apply("Pol_Decimal_Soft", d, PolicyEdit.Enabled(("SoftNumber", ElementValue.FromNumber(50))));
        Apply("Pol_LongDecimal_RequiredSoftText", d, PolicyEdit.Enabled(("LongTextNumber", ElementValue.FromNumber(5))));
        Assert.IsFalse(d.EnumerateRecords().Any(r => r.ValueName.StartsWith("**soft.", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Sec03_5_AnEnumReadsTheFirstItemWhoseValueEquals()
    {
        var policy = Tmp("Pol",
            "<elements><enum id=\"E\" valueName=\"E\">" +
            "<item displayName=\"$(string.Pol)\"><value><decimal value=\"7\" /></value></item>" +
            "<item displayName=\"$(string.Pol)\"><value><decimal value=\"7\" /></value></item>" +
            "</enum></elements>");
        var d = PolDocument.Create();
        Set1(d, Key, "V", 1);
        Set1(d, Key, "E", 7);
        Assert.AreEqual(0, ((ChoiceValue)PolicyStateReader.Read(policy, d).Elements["E"]).ItemIndex);
    }

    [TestMethod]
    public void Sec03_2_NumbersAreReadFromTheirOwnType_Only()
    {
        var d = PolDocument.Create();
        var key = K + @"\Elements\Number";
        d.SetValue(key, "MinMaxNumber", PolValueType.QWord, PolData.FromQWord(20));
        d.SetValue(key, "LongNumber", PolValueType.DWord, PolData.FromDWord(20));
        SetSz(d, key, "TextNumber", "-1");
        Assert.IsFalse(Read("Pol_Decimal_MinMax", d).Elements.ContainsKey("MinMaxNumber"), "a QWORD is not a decimal");
        Assert.IsFalse(Read("Pol_LongDecimal_Basic", d).Elements.ContainsKey("LongNumber"), "a DWORD is not a longDecimal");
        Assert.IsFalse(Read("Pol_Decimal_StoreAsText", d).Elements.ContainsKey("TextNumber"), "digits only");
    }

    [TestMethod]
    public void Sec09_DisabledTwice_ChangesNothingTheSecondTime()
    {
        foreach (var policy in new[] { "Pol_Class_Machine", "Pol_Value_EnabledOnly", "Pol_Value_NoValueName_ElementsOnly", "Pol_List_Plain" })
        {
            var d = PolDocument.Create();
            Apply(policy, d, PolicyEdit.Disabled);
            var parsed = Reparse(d);
            AssertNoChange(Apply(policy, parsed, PolicyEdit.Disabled), parsed);
        }
    }
}

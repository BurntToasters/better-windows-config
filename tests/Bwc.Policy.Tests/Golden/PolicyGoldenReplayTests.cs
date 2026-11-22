namespace Bwc.Policy.Tests.Golden;

/// <summary>The cases on awaiting-recapture.txt: capture folders whose golden is stale until the user
/// recaptures them.</summary>
internal static class AwaitingRecapture
{
    internal static IReadOnlyList<string> Read()
    {
        if (!File.Exists(GoldenRepo.AwaitingList))
        {
            throw new AssertFailedException("The awaiting-recapture list is missing: " + GoldenRepo.AwaitingList);
        }

        var ids = File.ReadAllLines(GoldenRepo.AwaitingList)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();
        var duplicate = ids.GroupBy(i => i, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new AssertFailedException("The awaiting-recapture list names a case twice: " + duplicate.Key);
        }

        return ids;
    }
}

/// <summary>
/// The golden replay (pieces E6a and E6b, docs/failure-modes/policy-save.md section R and
/// docs/failure-modes/policy-defaults.md section B): each case of groups A, B, C and K is copied into a
/// sandbox GPO folder, its structured operations are saved through <see cref="Bwc.Policy.Model.PolicySave"/>
/// and every file of every recorded step must equal the capture byte for byte. A missing case, a damaged
/// golden or a CAP-4 step fails; none skips.
/// </summary>
[TestClass]
public sealed class PolicyGoldenReplayTests
{
    internal static readonly string[] GroupA =
    [
        "Both_Computer_Then_User", "Both_UnderComputer_CommentOnly", "Both_UnderComputer_Enabled", "Both_UnderUser_CommentOnly",
        "Both_UnderUser_Enabled", "Machine_Apply_Cancel", "Machine_Apply_OK", "Machine_Cancel", "Machine_CommentOnly", "Machine_Disabled",
        "Machine_Enabled", "Machine_EnabledWithComment", "Machine_NotConfigured", "Machine_Then_User", "User_CommentOnly", "User_Disabled",
        "User_Enabled", "User_EnabledWithComment", "User_Then_Machine",
    ];

    internal static readonly string[] GroupK =
    [
        "KeyLeadingBackslash_Enable", "KeyLeadingBackslash_ReadAsWritten", "KeyLeadingBackslash_ReadTrimmed", "KeyTrailingBackslash_Enable",
        "KeyTrailingBackslash_ReadAsWritten", "KeyTrailingBackslash_ReadTrimmed",
    ];

    internal static readonly string[] GroupB =
    [
        "Pol_Amp_DisplayName", "Pol_Amp_LabelDouble", "Pol_Amp_LabelSingle",
        "Pol_Bool_NoFalseValue", "Pol_Bool_StringValues", "Pol_Bool_TrueFalseLists",
        "Pol_Bool_TrueFalseValues", "Pol_Bool_TwoChecksReverseOrder", "Pol_Both_WithElements.Computer",
        "Pol_Both_WithElements.User", "Pol_Class_Both.Computer", "Pol_Class_Both.User",
        "Pol_Class_Machine", "Pol_Class_User", "Pol_Decimal_MinMax",
        "Pol_Decimal_Required", "Pol_Decimal_Soft", "Pol_Decimal_StoreAsText",
        "Pol_Element_KeyOverride", "Pol_Enum_DecimalItems", "Pol_Enum_ItemValueList",
        "Pol_Enum_NoSortDefaultItem", "Pol_Enum_Required", "Pol_Enum_StringItems",
        "Pol_Explain_AttributeOmitted", "Pol_Explain_EmptyString", "Pol_Explain_MultiParagraph",
        "Pol_List_Additive", "Pol_List_EmptyPrefix", "Pol_List_Expandable",
        "Pol_List_ExplicitValue", "Pol_List_Plain", "Pol_List_PolicyValueName",
        "Pol_List_ValuePrefix", "Pol_LongDecimal_Basic", "Pol_LongDecimal_RequiredSoftText",
        "Pol_MultiText_Basic", "Pol_MultiText_Limits", "Pol_Nested_MachineDeep",
        "Pol_Nested_MachineMid", "Pol_Nested_MachineTop", "Pol_Nested_UserDeep",
        "Pol_Outside_KeyOutsideRoots", "Pol_SharedList_One", "Pol_SharedList_Two",
        "Pol_Shared_Alpha", "Pol_Shared_ElementsReverseOrder", "Pol_Shared_Mid",
        "Pol_Shared_Zeta", "Pol_Sort_Digit1", "Pol_Sort_Digit10",
        "Pol_Sort_Digit2", "Pol_Sort_LeadingSymbol", "Pol_Sort_LowerAlpha",
        "Pol_Sort_NonAscii", "Pol_Sort_UpperAlpha", "Pol_Sort_Zulu",
        "Pol_Text_ComboBoxNoSortRequired", "Pol_Text_ComboBoxSuggestions", "Pol_Text_Expandable",
        "Pol_Text_MaxLength", "Pol_Text_Plain", "Pol_Text_Required",
        "Pol_Top_DirectUnderRoot", "Pol_Value_BothLists", "Pol_Value_Decimal",
        "Pol_Value_DisabledListOnly", "Pol_Value_DisabledOnly", "Pol_Value_EnabledListOnly",
        "Pol_Value_EnabledOnly", "Pol_Value_NoValueName_ElementsOnly", "Pol_Value_String",
    ];

    internal static readonly string[] GroupC =
    [
        "Apply_Then_OK", "Booleans_ReverseOrder", "Clear_LastSetting_Disabled",
        "Clear_LastSetting_Machine", "Clear_LastSetting_User", "Clear_LastSetting_WithComment",
        "Clear_MachineAndUser", "Clear_OneOfTwo", "Cmtx_FirstSave_ForeignNamespaces",
        "Cmtx_FirstSave_ThenUser", "Cmtx_NoStartFile", "Comment_Enabled_AddEditRemove",
        "Comment_MultiLine", "Comment_NotConfigured_AddEditRemove", "Comment_OnlyAfterSave",
        "Comment_TwoPolicies", "Comment_User", "Elements_ReverseOrder",
        "GptIni_ForeignExtensions", "GptIni_PairRemoval", "List_PolicyValueName",
        "NoChange_Apply", "ReApply_SameValues", "SharedList_DisableAndShrink",
        "SharedList_EmptyList", "SharedList_OneThenTwo", "SharedList_TwoThenOne",
        "Shared_DeleteRecordsOrder", "Shared_MidAlphaZeta", "Shared_ZetaAlphaMid",
        "TwoInKey_ChangeFirstAgain", "TwoInKey_ClassThenDecimal", "TwoInKey_DecimalThenClass",
        "Version_FromHighMachine", "Version_FromHighUser", "Version_FromKnownStart",
        "Version_MachineUserCarry",
    ];

    private static IEnumerable<string> ReplayedCases() =>
        GroupA.Select(c => "A/" + c)
            .Concat(GroupB.Select(c => "B/" + c))
            .Concat(GroupC.Select(c => "C/" + c))
            .Concat(GroupK.Select(c => "K/" + c));

    public static IEnumerable<string[]> Rows() =>
        ReplayedCases().Concat(AwaitingRecapture.Read()).Distinct(StringComparer.Ordinal).Select(c => new[] { c });

    [TestMethod]
    [DynamicData(nameof(Rows))]
    public void Replay_WritesWhatClassicWrote(string caseId)
    {
        if (AwaitingRecapture.Read().Contains(caseId, StringComparer.Ordinal))
        {
            Assert.Inconclusive($"awaiting recapture: {caseId} (tests/Bwc.Policy.Tests/Golden/awaiting-recapture.txt)");
        }

        GoldenReplay.Run(GoldenCase.Load(caseId));
    }

    [TestMethod]
    public void ReplayRowsCoverEveryCaseOfTheGroups()
    {
        var expected = ReplayedCases().Order(StringComparer.Ordinal).ToArray();
        var onDisk = new[] { "A", "B", "C", "K" }
            .SelectMany(g => Directory.GetDirectories(Path.Combine(GoldenRepo.Goldens, g)).Select(d => g + "/" + Path.GetFileName(d)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(expected, onDisk, "Every case folder of groups A, B, C and K needs a replay row, and every row a folder.");
    }

    [TestMethod]
    public void AwaitingRecaptureEntries_AreRealCasesThatStillShowCap4()
    {
        foreach (var id in AwaitingRecapture.Read())
        {
            Assert.IsTrue(GoldenCase.Exists(id), $"The awaiting-recapture list names {id}, which has no capture folder.");
            var golden = GoldenCase.Load(id);
            Assert.IsNotNull(
                UnnoticedEditRule.FindInCase(golden),
                $"{id} no longer shows the CAP-4 pattern (it was recaptured): remove it from the awaiting-recapture list.");
        }
    }

    [TestMethod]
    [TestCategory("ReleaseGate")]
    public void AwaitingRecaptureListIsEmpty()
    {
        var left = AwaitingRecapture.Read();
        Assert.AreEqual(0, left.Count, $"{left.Count} capture case(s) still await a recapture: {string.Join(", ", left)}");
    }
}

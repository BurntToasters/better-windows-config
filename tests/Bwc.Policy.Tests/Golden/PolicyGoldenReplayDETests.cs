using Bwc.Policy.Model;

namespace Bwc.Policy.Tests.Golden;

/// <summary>
/// The golden replay of groups D and E (piece E6c, docs/failure-modes/policy-save.md, sections M, X, V and C). Each case is
/// copied into a sandbox GPO folder, its structured operations are saved through <see cref="PolicySave"/> and every file of
/// every recorded step must equal the capture byte for byte. The refused starts of group D (D9) take the refused-start
/// path of <see cref="GoldenReplay"/>. Group E applies the dialog control rules of <see cref="DialogControlRules"/> to the
/// options first. A missing case, a damaged golden or a CAP-4 step fails; none skips. Cases on awaiting-recapture.txt
/// report Inconclusive.
/// </summary>
[TestClass]
public sealed class PolicyGoldenReplayDETests
{
    internal static readonly string[] GroupD =
    [
        "BothLists_Disabled", "BothLists_Enabled", "Both_BothFiles", "Both_MachineFileOnly", "Both_UserFileOnly",
        "Case_KeyDiffers", "Case_KeyTwoSpellings", "Case_ValueNameDiffers", "Del_DisabledValuePolicy", "Del_Only",
        "Del_OtherName", "Del_ThenValue", "Delvals", "Delvals_After", "DisabledList_AllValues", "Dup_DelBetween",
        "Dup_DelBetweenDifferent", "Dup_DelvalsBetween", "Dup_DifferentFirstWins", "Dup_DifferentLastWins",
        "Dup_Identical", "Dup_Key_TwoBlocks", "Dup_NonAdjacent", "EnabledList_AllValues", "EnabledList_Partial",
        "EnumValueList_ItemA", "EnumValueList_ItemAPartial", "EnumValueList_ItemC", "EnumValueList_ValuesWithoutChoice",
        "EnumValueList_WrongItemValues", "File_BadHeader", "File_Empty", "File_HeaderOnly", "File_LargeValueName",
        "File_MissingCloseBracket", "File_OnlyGarbage", "File_TrailingBytes", "File_TruncatedLastRecord",
        "File_VersionTwo", "Foreign_KeyOutsideRoots", "Foreign_KeyUnderRoot", "Foreign_OnlyUnknownOutside",
        "Foreign_OnlyUnknownUnderPolicies", "Foreign_TopValue", "Foreign_ValueInPolicyKey", "Foreign_ValueOnly",
        "Instruction_KeyLevel", "Instruction_Unknown", "Instruction_UnknownAlone", "List_AdditiveValues",
        "List_ExpandableAsPlain", "List_ExplicitValues", "List_PlainGapNames", "List_PlainOtherNames", "List_PlainValues",
        "List_PolicyValueNameAndEntries", "List_PolicyValueNameOnly", "List_PrefixValues", "List_SharedKey",
        "List_WithDelvals", "List_WrongType", "Mismatch_DecimalAsString", "Mismatch_DecimalDisabled",
        "Mismatch_DecimalEnabled", "Mismatch_DecimalNeither", "Mismatch_DecimalOutOfRange", "Mismatch_DisabledOnly",
        "Mismatch_DisabledOnlyMatch", "Mismatch_EnabledOnly", "Mismatch_EnabledOnlyMatch", "Mismatch_EnumUnknownItem",
        "Mismatch_EnumValidItem", "Mismatch_FlagValue2", "Mismatch_FlagZero", "Mismatch_StringCase",
        "Mismatch_StringNeither", "Mismatch_StringOff", "Mismatch_TextTooLong", "Order_KeysDescending",
        "Order_KeysInterleaved", "Order_ParentAfterChild", "Order_ValuesAscending", "Order_ValuesDescending",
        "Partial_BoolFalseValue", "Partial_BoolNoFalse", "Partial_BoolNoFalse_One", "Partial_BoolNoFalse_Two",
        "Partial_BoolStrings", "Partial_BoolValues", "Partial_BothElements", "Partial_ChecksBothValues",
        "Partial_ElementsWithoutGate", "Partial_GateAndOneElement", "Partial_GateWithoutElements",
        "Partial_OneOfTwoChecks", "Partial_OnlyNumberOfTwo", "Partial_OnlyTextOfTwo", "Soft_Only", "Soft_WithValue",
        "User_DelOnly", "User_FlagEnabled", "User_FlagInMachineFile", "Value_ThenDel", "WrongType_DecimalAsString",
        "WrongType_ExpandableAsPlain", "WrongType_FlagAsBinary", "WrongType_FlagAsQword", "WrongType_FlagAsString",
        "WrongType_FlagEightBytes", "WrongType_FlagShortSize", "WrongType_LongAsDword", "WrongType_MultiAsString",
        "WrongType_StoreAsTextAsDword",
    ];

    internal static readonly string[] GroupE =
    [
        "Combo_Required_Empty", "Decimal_AboveMax", "Decimal_Apply", "Decimal_BelowMin", "Decimal_EdgeValues",
        "Decimal_Empty", "Decimal_NotNumber", "Decimal_Required_Empty", "Decimal_Soft_OutOfRange",
        "Decimal_StoreAsText_OutOfRange", "Decimal_TypedAboveMax", "Decimal_TypedLetters", "Enum_Required_Defaults",
        "List_Explicit_DuplicateNames", "List_Explicit_EmptyName", "List_Explicit_EmptyValue", "List_Explicit_NameOnly",
        "List_Plain_DuplicateEntries", "List_Plain_EmptyEntry", "List_Plain_LongEntry", "List_Plain_SpecialCharacters",
        "LongDecimal_AboveMax", "LongDecimal_Required_Empty", "MultiText_Limits", "Next_WithInvalid",
        "Ok_DefaultsOfRequired", "Previous_WithInvalid", "Text_Required_Empty", "Text_TooLong_SetText",
        "Text_TooLong_Typed",
    ];

    /// <summary>The six D cases classic refused to open (policy-save.md, X).</summary>
    internal static readonly string[] RefusedStarts =
    [
        "File_BadHeader", "File_Empty", "File_MissingCloseBracket", "File_OnlyGarbage", "File_TruncatedLastRecord", "File_VersionTwo",
    ];

    private static IEnumerable<string> Cases() => GroupD.Select(c => "D/" + c).Concat(GroupE.Select(c => "E/" + c));

    public static IEnumerable<string[]> Rows() => Cases().Select(c => new[] { c });

    [TestMethod]
    [DynamicData(nameof(Rows))]
    public void Replay_WritesWhatClassicWrote(string caseId)
    {
        if (AwaitingRecapture.Read().Contains(caseId, StringComparer.Ordinal))
        {
            Assert.Inconclusive($"awaiting recapture: {caseId} (tests/Bwc.Policy.Tests/Golden/awaiting-recapture.txt)");
        }

        var golden = GoldenCase.Load(caseId);
        DropTreeObservations(golden);
        if (caseId.StartsWith("E/", StringComparison.Ordinal))
        {
            DialogControlRules.Apply(golden);
        }

        GoldenReplay.Run(golden);
    }

    /// <summary>
    /// The foreign cases of group D read the children of a tree node (an observation of the console, with no engine
    /// counterpart: inventory row BEH-014 says no Extra Registry Settings node exists). The harness replays no such
    /// operation, so the rows leave them out. E6c keeps them out: which nodes the tree lists is a fact of the window, and the
    /// engine has no tree of records to compare it with (the filter and the tree builder work from templates only).
    /// </summary>
    private static void DropTreeObservations(GoldenCase golden)
    {
        var ops = golden.Ops as List<GoldenOp> ?? throw new AssertFailedException("The operations of a loaded case are no longer a list.");
        ops.RemoveAll(o => o.Kind == "treeChildren");
    }

    [TestMethod]
    public void ReplayRowsCoverEveryCaseOfGroupsDAndE()
    {
        var expected = Cases().Order(StringComparer.Ordinal).ToArray();
        var onDisk = new[] { "D", "E" }
            .SelectMany(g => Directory.GetDirectories(Path.Combine(GoldenRepo.Goldens, g)).Select(d => g + "/" + Path.GetFileName(d)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(expected, onDisk, "Every case folder of groups D and E needs a replay row, and every row a folder.");
        Assert.AreEqual(113, GroupD.Length, "Group D holds 113 cases.");
        Assert.AreEqual(30, GroupE.Length, "Group E holds 30 cases.");
    }

    [TestMethod]
    public void RefusedStartsOfGroupD_AreExactlyTheSixMalformedFiles()
    {
        var refused = GroupD.Where(c => GoldenCase.Load("D/" + c).StartRefused).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(RefusedStarts.Order(StringComparer.Ordinal).ToArray(), refused, "The cases classic refused to open changed.");
    }

    [TestMethod]
    public void GroupD_StartsHoldOnlyRegistryPolFiles()
    {
        foreach (var name in GroupD)
        {
            var golden = GoldenCase.Load("D/" + name);
            var others = golden.StartFiles.Keys.Where(k => !k.EndsWith("Registry.pol", StringComparison.Ordinal)).ToArray();
            Assert.AreEqual(0, others.Length, $"D/{name} starts with a file the replay was not written for: {string.Join(", ", others)}");
        }
    }
}

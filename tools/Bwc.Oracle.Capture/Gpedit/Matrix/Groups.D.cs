using Bwc.Oracle.Capture.Gpedit.Driver;
using static Bwc.Oracle.Capture.Gpedit.Matrix.MatrixOps;
using static Bwc.Oracle.Capture.Gpedit.Matrix.PregWriter;

namespace Bwc.Oracle.Capture.Gpedit.Matrix;

internal static partial class MatrixGroups
{
    // Group D: the state matrix. Each case crafts a Registry.pol (BWC-authored, in the order and with the types the case needs), starts classic
    // on it, reads the State column of the affected nodes and the dialog of every affected policy (Cancel), then saves one unrelated policy
    // and clears it again, so the goldens show what classic keeps, drops, reorders or normalizes.
    internal static IEnumerable<MatrixCase> GroupD(MatrixContext c)
    {
        FixtureModel f = c.Fixture;
        string basics = Crafted.K("Basics");
        string number = Crafted.K(@"Elements\Number");
        string text = Crafted.K(@"Elements\Text");
        string choice = Crafted.K(@"Elements\Choice");
        string bools = Crafted.K(@"Elements\Bool");
        string shared = Crafted.K("Shared");
        string lists = Crafted.K("Lists");

        MatrixCase Machine(string id, string description, string[] affected, params Rec[] records) =>
            BuildCase(f, id, description, Crafted.Machine(description, records), affected, "Computer", "Pol_Sort_Zulu");

        MatrixCase UserCase(string id, string description, string[] affected, params Rec[] records) =>
            BuildCase(f, id, description, Crafted.User(description, records), affected, "User", "Pol_Top_DirectUnderRoot");

        // Duplicates and instruction records on one flag.
        yield return Machine("Dup_Identical", "MachineFlag present twice with the same data", new[] { "Pol_Class_Machine" },
            Dword(basics, "MachineFlag", 1), Dword(basics, "MachineFlag", 1));
        yield return Machine("Dup_DifferentLastWins", "MachineFlag 1 then 0 in the same key", new[] { "Pol_Class_Machine" },
            Dword(basics, "MachineFlag", 1), Dword(basics, "MachineFlag", 0));
        yield return Machine("Dup_DifferentFirstWins", "MachineFlag 0 then 1 in the same key", new[] { "Pol_Class_Machine" },
            Dword(basics, "MachineFlag", 0), Dword(basics, "MachineFlag", 1));
        yield return Machine("Dup_NonAdjacent", "MachineFlag 1, another value, MachineFlag 1 again", new[] { "Pol_Class_Machine", "Pol_Value_Decimal" },
            Dword(basics, "MachineFlag", 1), Dword(basics, "ModeNumber", 5), Dword(basics, "MachineFlag", 1));
        yield return Machine("Dup_Key_TwoBlocks", "the same key in two separate runs of records", new[] { "Pol_Class_Machine", "Pol_Value_Decimal" },
            Dword(basics, "MachineFlag", 1), Dword(number, "MinMaxNumber", 100), Dword(basics, "ModeNumber", 5));
        yield return Machine("Dup_DelBetween", "MachineFlag 1, a **del.MachineFlag record, MachineFlag 1 again (GPO-4: shown state and the next save)", new[] { "Pol_Class_Machine" },
            Dword(basics, "MachineFlag", 1), Del(basics, "MachineFlag"), Dword(basics, "MachineFlag", 1));
        yield return Machine("Dup_DelBetweenDifferent", "MachineFlag 1, a **del.MachineFlag record, MachineFlag 0 (GPO-4)", new[] { "Pol_Class_Machine" },
            Dword(basics, "MachineFlag", 1), Del(basics, "MachineFlag"), Dword(basics, "MachineFlag", 0));
        yield return Machine("Dup_DelvalsBetween", "MachineFlag 1, a **delvals. record, MachineFlag 0 (GPO-4)", new[] { "Pol_Class_Machine" },
            Dword(basics, "MachineFlag", 1), Instruction(basics, "**delvals."), Dword(basics, "MachineFlag", 0));
        yield return Machine("Del_Only", "only a delete record for MachineFlag (the probe lead: Disabled or Not Configured)", new[] { "Pol_Class_Machine" },
            Del(basics, "MachineFlag"));
        yield return Machine("Del_ThenValue", "delete record, then the value", new[] { "Pol_Class_Machine" },
            Del(basics, "MachineFlag"), Dword(basics, "MachineFlag", 1));
        yield return Machine("Value_ThenDel", "the value, then a delete record", new[] { "Pol_Class_Machine" },
            Dword(basics, "MachineFlag", 1), Del(basics, "MachineFlag"));
        yield return Machine("Del_OtherName", "a delete record for a name no policy owns", new[] { "Pol_Class_Machine" },
            Del(basics, "NobodyOwnsThis"), Dword(basics, "MachineFlag", 1));
        yield return Machine("Del_DisabledValuePolicy", "delete records next to a policy with enabled and disabled values", new[] { "Pol_Value_Decimal", "Pol_Value_EnabledOnly", "Pol_Value_DisabledOnly" },
            Del(basics, "ModeNumber"), Del(basics, "EnabledOnly"), Del(basics, "DisabledOnly"));
        yield return Machine("Delvals", "a **delvals. record in the key, then a value", new[] { "Pol_Class_Machine" },
            Instruction(basics, "**delvals."), Dword(basics, "MachineFlag", 1));
        yield return Machine("Delvals_After", "a value, then a **delvals. record in the key", new[] { "Pol_Class_Machine" },
            Dword(basics, "MachineFlag", 1), Instruction(basics, "**delvals."));
        yield return Machine("Soft_Only", "a **soft. record for MachineFlag alone", new[] { "Pol_Class_Machine" },
            Instruction(basics, "**soft.MachineFlag"));
        yield return Machine("Soft_WithValue", "a **soft. record and the value", new[] { "Pol_Class_Machine" },
            Instruction(basics, "**soft.MachineFlag"), Dword(basics, "MachineFlag", 1));
        yield return Machine("Instruction_Unknown", "an unknown ** instruction in the key, with a value", new[] { "Pol_Class_Machine" },
            Instruction(basics, "**bwcunknown.Thing"), Dword(basics, "MachineFlag", 1));
        yield return Machine("Instruction_UnknownAlone", "an unknown ** instruction alone in an otherwise unknown key", Array.Empty<string>(),
            Instruction(Crafted.K("Unknown"), "**bwcunknown.Thing"));
        yield return Machine("Instruction_KeyLevel", "key level instructions (**DeleteValues, **DeleteKeys, **SecureKey) in a policy key", new[] { "Pol_Class_Machine" },
            Sz(basics, "**DeleteValues", "MachineFlag"), Sz(basics, "**DeleteKeys", "Nothing"), Sz(basics, "**SecureKey", "1"), Dword(basics, "MachineFlag", 1));

        // Wrong types and sizes.
        yield return Machine("WrongType_FlagAsString", "MachineFlag stored as REG_SZ \"1\"", new[] { "Pol_Class_Machine" },
            Sz(basics, "MachineFlag", "1"));
        yield return Machine("WrongType_FlagAsQword", "MachineFlag stored as REG_QWORD 1", new[] { "Pol_Class_Machine" },
            Qword(basics, "MachineFlag", 1));
        yield return Machine("WrongType_FlagAsBinary", "MachineFlag stored as REG_BINARY 01", new[] { "Pol_Class_Machine" },
            Binary(basics, "MachineFlag", 0x01));
        yield return Machine("WrongType_FlagShortSize", "MachineFlag as REG_DWORD with a size of 2 and two data bytes", new[] { "Pol_Class_Machine" },
            new Rec(basics, "MachineFlag", RegDword, new byte[] { 1, 0 }));
        yield return Machine("WrongType_FlagEightBytes", "MachineFlag as REG_DWORD with eight data bytes and a size of 8", new[] { "Pol_Class_Machine" },
            new Rec(basics, "MachineFlag", RegDword, new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 }));
        yield return Machine("WrongType_DecimalAsString", "a decimal element stored as REG_SZ and a text element stored as REG_DWORD",
            new[] { "Pol_Decimal_MinMax", "Pol_Text_Plain" },
            Sz(number, "MinMaxNumber", "100"), Dword(text, "PlainText", 7));
        yield return Machine("WrongType_StoreAsTextAsDword", "a storeAsText decimal stored as REG_DWORD", new[] { "Pol_Decimal_StoreAsText" },
            Dword(number, "TextNumber", 8080));
        yield return Machine("WrongType_ExpandableAsPlain", "an expandable text stored as REG_SZ and a plain text stored as REG_EXPAND_SZ",
            new[] { "Pol_Text_Expandable", "Pol_Text_Plain" },
            Sz(text, "ExpandText", @"%SystemRoot%\x"), ExpandSz(text, "PlainText", "plain"));
        yield return Machine("WrongType_MultiAsString", "a multiText stored as REG_SZ", new[] { "Pol_MultiText_Basic" },
            Sz(text, "MultiLines", "one"));
        yield return Machine("WrongType_LongAsDword", "a longDecimal stored as REG_DWORD", new[] { "Pol_LongDecimal_Basic" },
            Dword(number, "LongNumber", 5));

        // Partial element values.
        yield return Machine("Partial_OnlyTextOfTwo", "elements only policy: only the text value present", new[] { "Pol_Value_NoValueName_ElementsOnly" },
            Sz(basics, "OnlyText", "bwc"));
        yield return Machine("Partial_OnlyNumberOfTwo", "elements only policy: only the number value present", new[] { "Pol_Value_NoValueName_ElementsOnly" },
            Dword(basics, "OnlyNumber", 9));
        yield return Machine("Partial_BothElements", "elements only policy: both values present", new[] { "Pol_Value_NoValueName_ElementsOnly" },
            Sz(basics, "OnlyText", "bwc"), Dword(basics, "OnlyNumber", 9));
        yield return Machine("Partial_GateWithoutElements", "a policy with a value name and two elements: only the value name present", new[] { "Pol_Shared_ElementsReverseOrder" },
            Dword(shared, "Gate", 1));
        yield return Machine("Partial_ElementsWithoutGate", "the same policy: both elements but not the value name", new[] { "Pol_Shared_ElementsReverseOrder" },
            Sz(shared, "ZetaElement", "zz"), Dword(shared, "AlphaElement", 5));
        yield return Machine("Partial_GateAndOneElement", "the same policy: the value name and the Alpha element only", new[] { "Pol_Shared_ElementsReverseOrder" },
            Dword(shared, "Gate", 1), Dword(shared, "AlphaElement", 5));
        yield return Machine("Partial_OneOfTwoChecks", "two check boxes: only the Zeta value present", new[] { "Pol_Bool_TwoChecksReverseOrder" },
            Dword(Crafted.K(@"Elements\Bool\Order"), "ZetaCheck", 1));
        yield return Machine("Partial_ChecksBothValues", "two check boxes: both present, Zeta 0 and Alpha 1", new[] { "Pol_Bool_TwoChecksReverseOrder" },
            Dword(Crafted.K(@"Elements\Bool\Order"), "ZetaCheck", 0), Dword(Crafted.K(@"Elements\Bool\Order"), "AlphaCheck", 1));
        yield return Machine("Partial_BoolNoFalse", "a boolean with no falseValue present as 0",
            new[] { "Pol_Bool_NoFalseValue" }, Dword(bools, "NoFalseCheck", 0));
        yield return Machine("Partial_BoolNoFalse_One", "a boolean with no falseValue present as 1", new[] { "Pol_Bool_NoFalseValue" },
            Dword(bools, "NoFalseCheck", 1));
        yield return Machine("Partial_BoolNoFalse_Two", "a boolean with no falseValue present as 2", new[] { "Pol_Bool_NoFalseValue" },
            Dword(bools, "NoFalseCheck", 2));
        yield return Machine("Partial_BoolValues", "booleans with true and false values: 10, 20, and a third value 30",
            new[] { "Pol_Bool_TrueFalseValues" }, Dword(bools, "TrueFalseCheck", 30));
        yield return Machine("Partial_BoolFalseValue", "a boolean with values 10 and 20 holding 20", new[] { "Pol_Bool_TrueFalseValues" },
            Dword(bools, "TrueFalseCheck", 20));
        yield return UserCase("Partial_BoolStrings", "a boolean with string values: yes, no and YES", new[] { "Pol_Bool_StringValues" },
            Sz(bools, "StringCheck", "YES"));

        // The value is there but does not match the enabled or disabled value.
        yield return Machine("Mismatch_DecimalNeither", "enabledValue 5 and disabledValue 9: the value is 6", new[] { "Pol_Value_Decimal" },
            Dword(basics, "ModeNumber", 6));
        yield return Machine("Mismatch_DecimalEnabled", "enabledValue 5 and disabledValue 9: the value is 5", new[] { "Pol_Value_Decimal" },
            Dword(basics, "ModeNumber", 5));
        yield return Machine("Mismatch_DecimalDisabled", "enabledValue 5 and disabledValue 9: the value is 9", new[] { "Pol_Value_Decimal" },
            Dword(basics, "ModeNumber", 9));
        yield return Machine("Mismatch_DecimalAsString", "enabledValue 5: the value is the string \"5\"", new[] { "Pol_Value_Decimal" },
            Sz(basics, "ModeNumber", "5"));
        yield return UserCase("Mismatch_StringNeither", "string values on and off: the value is maybe", new[] { "Pol_Value_String" },
            Sz(basics, "ModeText", "maybe"));
        yield return UserCase("Mismatch_StringCase", "string values on and off: the value is ON", new[] { "Pol_Value_String" },
            Sz(basics, "ModeText", "ON"));
        yield return UserCase("Mismatch_StringOff", "string values on and off: the value is off", new[] { "Pol_Value_String" },
            Sz(basics, "ModeText", "off"));
        yield return Machine("Mismatch_EnabledOnly", "enabledValue 7 only: the value is 0", new[] { "Pol_Value_EnabledOnly" },
            Dword(basics, "EnabledOnly", 0));
        yield return Machine("Mismatch_EnabledOnlyMatch", "enabledValue 7 only: the value is 7", new[] { "Pol_Value_EnabledOnly" },
            Dword(basics, "EnabledOnly", 7));
        yield return Machine("Mismatch_DisabledOnly", "disabledValue 3 only: the value is 4", new[] { "Pol_Value_DisabledOnly" },
            Dword(basics, "DisabledOnly", 4));
        yield return Machine("Mismatch_DisabledOnlyMatch", "disabledValue 3 only: the value is 3", new[] { "Pol_Value_DisabledOnly" },
            Dword(basics, "DisabledOnly", 3));
        yield return Machine("Mismatch_FlagValue2", "a plain flag holding 2", new[] { "Pol_Class_Machine" },
            Dword(basics, "MachineFlag", 2));
        yield return Machine("Mismatch_FlagZero", "a plain flag holding 0", new[] { "Pol_Class_Machine" },
            Dword(basics, "MachineFlag", 0));
        yield return Machine("Mismatch_EnumUnknownItem", "an enum value that matches no item", new[] { "Pol_Enum_DecimalItems" },
            Dword(choice, "DecimalChoice", 99));
        yield return Machine("Mismatch_EnumValidItem", "an enum value that matches the second item", new[] { "Pol_Enum_DecimalItems" },
            Dword(choice, "DecimalChoice", 1));
        yield return Machine("Mismatch_DecimalOutOfRange", "a decimal with range 10 to 500 holding 9999 and a second holding 1",
            new[] { "Pol_Decimal_MinMax", "Pol_Decimal_Soft" },
            Dword(number, "MinMaxNumber", 9999), Dword(number, "SoftNumber", 1000));
        yield return Machine("Mismatch_TextTooLong", "a text with maxLength 12 holding 30 characters", Array.Empty<string>(),
            Sz(text, "ShortText", new string('x', 30)));

        // Enum item value lists and boolean lists.
        yield return Machine("EnumValueList_ItemA", "enum with item value lists: the value 1 and the item A values present",
            new[] { "Pol_Enum_ItemValueList" },
            Dword(choice, "ProfileChoice", 1), Dword(Crafted.K(@"Elements\Choice\ProfileA"), "ProfileAFirst", 100), Sz(Crafted.K(@"Elements\Choice\ProfileAOther"), "ProfileASecond", "second"));
        yield return Machine("EnumValueList_ItemAPartial", "enum with item value lists: the value 1 and only one of the two item A values",
            new[] { "Pol_Enum_ItemValueList" },
            Dword(choice, "ProfileChoice", 1), Dword(Crafted.K(@"Elements\Choice\ProfileA"), "ProfileAFirst", 100));
        yield return Machine("EnumValueList_ValuesWithoutChoice", "enum with item value lists: the item A values without the choice value",
            new[] { "Pol_Enum_ItemValueList" },
            Dword(Crafted.K(@"Elements\Choice\ProfileA"), "ProfileAFirst", 100), Sz(Crafted.K(@"Elements\Choice\ProfileAOther"), "ProfileASecond", "second"));
        yield return Machine("EnumValueList_WrongItemValues", "enum with item value lists: the value 2 (item B has no list) and item A values left over",
            new[] { "Pol_Enum_ItemValueList" },
            Dword(choice, "ProfileChoice", 2), Dword(Crafted.K(@"Elements\Choice\ProfileA"), "ProfileAFirst", 100));
        yield return Machine("EnumValueList_ItemC", "enum with item value lists: the value 3 and the item C value",
            new[] { "Pol_Enum_ItemValueList" },
            Dword(choice, "ProfileChoice", 3), Dword(Crafted.K(@"Elements\Choice\ProfileC"), "ProfileCOnly", 300),
            Dword(basics, "ProfileCOnly", 300));
        yield return Machine("EnabledList_AllValues", "policy with only an enabledList: all three list values present",
            new[] { "Pol_Value_EnabledListOnly" },
            Dword(Crafted.K(@"Basics\EnabledListKey"), "ListItemOne", 1), Sz(Crafted.K(@"Basics\ItemKeyOverride"), "ListItemTwo", "two"), Dword(Crafted.K(@"Basics\EnabledListKey"), "ListItemThree", 3));
        yield return Machine("EnabledList_Partial", "policy with only an enabledList: two of the three values present",
            new[] { "Pol_Value_EnabledListOnly" },
            Dword(Crafted.K(@"Basics\EnabledListKey"), "ListItemOne", 1), Dword(Crafted.K(@"Basics\EnabledListKey"), "ListItemThree", 3));
        yield return Machine("DisabledList_AllValues", "policy with only a disabledList: both values present",
            new[] { "Pol_Value_DisabledListOnly" },
            Dword(Crafted.K(@"Basics\DisabledListKey"), "OffItemOne", 0), Sz(basics + @"\ItemKeyOverride", "OffItemTwo", "zero"));
        yield return Machine("BothLists_Enabled", "policy with a value name and both lists: the value name and the enabled list values",
            new[] { "Pol_Value_BothLists" },
            Dword(basics, "ListMaster", 1), Dword(basics, "BothListOn", 1), Dword(Crafted.K(@"Basics\BothListKey"), "BothListOnOther", 11));
        yield return Machine("BothLists_Disabled", "policy with a value name and both lists: only the disabled list value",
            new[] { "Pol_Value_BothLists" },
            Dword(basics, "BothListOff", 0));

        // Lists.
        yield return Machine("List_PlainValues", "a plain list with value names 1 and 2", new[] { "Pol_List_Plain" },
            Sz(Crafted.K(@"Lists\Plain"), "1", "bwc-1"), Sz(Crafted.K(@"Lists\Plain"), "2", "bwc-2"));
        yield return Machine("List_PlainOtherNames", "a plain list whose value names are words, not numbers", new[] { "Pol_List_Plain" },
            Sz(Crafted.K(@"Lists\Plain"), "alpha", "bwc-1"), Sz(Crafted.K(@"Lists\Plain"), "beta", "bwc-2"));
        yield return Machine("List_PlainGapNames", "a plain list with value names 1 and 3", new[] { "Pol_List_Plain" },
            Sz(Crafted.K(@"Lists\Plain"), "1", "bwc-1"), Sz(Crafted.K(@"Lists\Plain"), "3", "bwc-3"));
        yield return Machine("List_ExplicitValues", "an explicit list: name and value pairs", new[] { "Pol_List_ExplicitValue" },
            Sz(Crafted.K(@"Lists\Explicit"), "name1", "value1"), Sz(Crafted.K(@"Lists\Explicit"), "name2", "value2"));
        yield return Machine("List_PrefixValues", "a prefix list: Item1 and Item2 (user)", Array.Empty<string>(),
            Sz(Crafted.K(@"Lists\Prefix"), "Item1", "bwc-1"), Sz(Crafted.K(@"Lists\Prefix"), "Item2", "bwc-2"));
        yield return Machine("List_AdditiveValues", "an additive list with two entries", new[] { "Pol_List_Additive" },
            Sz(Crafted.K(@"Lists\Additive"), "1", "bwc-1"), Sz(Crafted.K(@"Lists\Additive"), "2", "bwc-2"));
        yield return Machine("List_WithDelvals", "a list key that starts with **delvals. and holds two entries", new[] { "Pol_List_Plain" },
            Instruction(Crafted.K(@"Lists\Plain"), "**delvals."), Sz(Crafted.K(@"Lists\Plain"), "1", "bwc-1"), Sz(Crafted.K(@"Lists\Plain"), "2", "bwc-2"));
        yield return Machine("List_WrongType", "a plain list whose entries are REG_DWORD", new[] { "Pol_List_Plain" },
            Dword(Crafted.K(@"Lists\Plain"), "1", 1), Dword(Crafted.K(@"Lists\Plain"), "2", 2));
        yield return Machine("List_ExpandableAsPlain", "an expandable list whose entries are REG_SZ", new[] { "Pol_List_Expandable" },
            Sz(Crafted.K(@"Lists\Expand"), "1", @"%SystemRoot%\a"));
        yield return Machine("List_PolicyValueNameOnly", "a list policy with a value name: the list entries but not the value name", new[] { "Pol_List_PolicyValueName" },
            Sz(Crafted.K(@"Lists\Switched"), "1", "bwc-1"));
        yield return Machine("List_PolicyValueNameAndEntries", "a list policy with a value name: the value name and the entries", new[] { "Pol_List_PolicyValueName" },
            Dword(lists, "ListSwitch", 1), Sz(Crafted.K(@"Lists\Switched"), "1", "bwc-1"));
        yield return Machine("List_SharedKey", "two policies sharing a list key, the key holds two entries", new[] { "Pol_SharedList_One", "Pol_SharedList_Two" },
            Sz(Crafted.K(@"Shared\SharedList"), "1", "a-1"), Sz(Crafted.K(@"Shared\SharedList"), "2", "b-1"));

        // Foreign values and keys.
        yield return Machine("Foreign_ValueInPolicyKey", "an unknown value in a policy key next to a known one", new[] { "Pol_Class_Machine" },
            Dword(basics, "MachineFlag", 1), Dword(basics, "ForeignValue", 1));
        yield return Machine("Foreign_ValueOnly", "an unknown value alone in a policy key", new[] { "Pol_Class_Machine" },
            Dword(basics, "ForeignValue", 1));
        yield return Machine("Foreign_KeyUnderRoot", "an unknown key below the fixture root", Array.Empty<string>(),
            Dword(Crafted.K("Unknown"), "X", 1));
        yield return Machine("Foreign_KeyOutsideRoots", "the one fixture key outside the policy roots, and an unknown key beside it (the Extra Registry Settings node)",
            new[] { "Pol_Outside_KeyOutsideRoots" },
            Dword(@"Software\BWC-Oracle-Gpedit", "OutsideFlag", 1), Dword(@"Software\BWC-Oracle-Gpedit\Unknown", "Y", 2), Sz(@"Software\BWC-Oracle-Gpedit", "StrayText", "bwc"));
        yield return Machine("Foreign_OnlyUnknownOutside", "only an unknown key outside the policy roots", Array.Empty<string>(),
            Dword(@"Software\BWC-Oracle-Gpedit\Unknown", "Y", 2));
        yield return Machine("Foreign_OnlyUnknownUnderPolicies", "only an unknown value directly under Software\\Policies\\BWC-Oracle", Array.Empty<string>(),
            Dword(Crafted.Root, "Stray", 1));
        yield return BuildCase(f, "Foreign_TopValue", "the value of the policy directly under the top category in the user file", Crafted.User("top flag in the user file", Dword(Crafted.Root, "TopFlag", 1)),
            new[] { "Pol_Top_DirectUnderRoot" }, "User", "Pol_Class_User");

        // Key and value name case, order.
        yield return Machine("Case_KeyDiffers", "the key written in other letter case than the template", new[] { "Pol_Class_Machine" },
            Dword(@"software\policies\bwc-oracle\BASICS", "MachineFlag", 1));
        yield return Machine("Case_ValueNameDiffers", "the value name written in other letter case", new[] { "Pol_Class_Machine" },
            Dword(basics, "MACHINEFLAG", 1));
        yield return Machine("Case_KeyTwoSpellings", "the same key spelled two ways", new[] { "Pol_Class_Machine", "Pol_Value_Decimal" },
            Dword(basics, "MachineFlag", 1), Dword(@"SOFTWARE\POLICIES\BWC-ORACLE\BASICS", "ModeNumber", 5));
        yield return Machine("Order_KeysDescending", "keys written in descending order", new[] { "Pol_Class_Machine", "Pol_Sort_Zulu", "Pol_Shared_Zeta" },
            Dword(Crafted.K("Sort"), "SortZulu", 1), Dword(shared, "Zeta", 1), Dword(basics, "MachineFlag", 1));
        yield return Machine("Order_ValuesDescending", "values of one key written in descending name order", new[] { "Pol_Shared_Zeta", "Pol_Shared_Alpha", "Pol_Shared_Mid" },
            Dword(shared, "Zeta", 1), Dword(shared, "Mid", 1), Dword(shared, "Alpha", 1));
        yield return Machine("Order_ValuesAscending", "values of one key written in ascending name order", new[] { "Pol_Shared_Zeta", "Pol_Shared_Alpha", "Pol_Shared_Mid" },
            Dword(shared, "Alpha", 1), Dword(shared, "Mid", 1), Dword(shared, "Zeta", 1));
        yield return Machine("Order_KeysInterleaved", "records of two keys interleaved", new[] { "Pol_Class_Machine", "Pol_Shared_Zeta" },
            Dword(basics, "MachineFlag", 1), Dword(shared, "Zeta", 1), Dword(basics, "ModeNumber", 5), Dword(shared, "Mid", 1));
        yield return Machine("Order_ParentAfterChild", "a child key written before its parent key", new[] { "Pol_List_Plain" },
            Sz(Crafted.K(@"Lists\Plain"), "1", "bwc-1"), Dword(lists, "ListSwitch", 1));

        // Both-class policies and the user file.
        yield return Machine("Both_MachineFileOnly", "a class Both flag in the machine file only (read under both roots)", new[] { "Pol_Class_Both" },
            Dword(basics, "BothFlag", 1));
        yield return BuildCase(f, "Both_UserFileOnly", "a class Both flag in the user file only (read under both roots)", Crafted.User("class Both flag in the user file", Dword(basics, "BothFlag", 1)),
            new[] { "Pol_Class_Both" }, "User", "Pol_Top_DirectUnderRoot", alsoRoots: new[] { "Computer" });
        yield return BuildCase(f, "Both_BothFiles", "a class Both flag in the machine file as 1 and the user file as 0", Crafted.Both("class Both flag in both files", new[] { Dword(basics, "BothFlag", 1) }, new[] { Dword(basics, "BothFlag", 0) }),
            new[] { "Pol_Class_Both" }, "Computer", "Pol_Sort_Zulu", alsoRoots: new[] { "User" });
        yield return UserCase("User_DelOnly", "a delete record in the user file", new[] { "Pol_Class_User" }, Del(basics, "UserFlag"));
        yield return UserCase("User_FlagEnabled", "the user flag present in the user file", new[] { "Pol_Class_User" }, Dword(basics, "UserFlag", 1));
        yield return BuildCase(f, "User_FlagInMachineFile", "the user flag written to the machine file (wrong file)", Crafted.Machine("user flag in the machine file", Dword(basics, "UserFlag", 1)),
            new[] { "Pol_Class_User" }, "User", "Pol_Top_DirectUnderRoot");

        // The file itself.
        yield return BuildCase(f, "File_Empty", "Registry.pol with zero bytes", Crafted.Raw("zero byte Registry.pol", Crafted.MachinePol, Array.Empty<byte>()),
            new[] { "Pol_Class_Machine" }, "Computer", "Pol_Sort_Zulu");
        yield return BuildCase(f, "File_HeaderOnly", "Registry.pol with the eight byte header and no record", Crafted.Raw("header only Registry.pol", Crafted.MachinePol, HeaderOnly()),
            new[] { "Pol_Class_Machine" }, "Computer", "Pol_Sort_Zulu");
        yield return BuildCase(f, "File_BadHeader", "Registry.pol whose first four bytes are not PReg", Crafted.Raw("bad header Registry.pol", Crafted.MachinePol, new byte[] { 0x50, 0x52, 0x65, 0x68, 1, 0, 0, 0 }.Concat(Build(new[] { Dword(basics, "MachineFlag", 1) })[8..]).ToArray()),
            new[] { "Pol_Class_Machine" }, "Computer", "Pol_Sort_Zulu");
        yield return BuildCase(f, "File_VersionTwo", "Registry.pol with format version 2", Crafted.Raw("version 2 Registry.pol", Crafted.MachinePol, WithVersion(2).Concat(Build(new[] { Dword(basics, "MachineFlag", 1) })[8..]).ToArray()),
            new[] { "Pol_Class_Machine" }, "Computer", "Pol_Sort_Zulu");
        byte[] good = Build(new[] { Dword(basics, "MachineFlag", 1), Dword(basics, "ModeNumber", 5) });
        yield return BuildCase(f, "File_TruncatedLastRecord", "Registry.pol cut in the middle of the second record", Crafted.Raw("truncated Registry.pol", Crafted.MachinePol, Truncate(good, good.Length - 14)),
            new[] { "Pol_Class_Machine", "Pol_Value_Decimal" }, "Computer", "Pol_Sort_Zulu");
        yield return BuildCase(f, "File_TrailingBytes", "Registry.pol with two stray bytes after the last record", Crafted.Raw("trailing bytes Registry.pol", Crafted.MachinePol, good.Concat(new byte[] { 0x41, 0x00 }).ToArray()),
            new[] { "Pol_Class_Machine", "Pol_Value_Decimal" }, "Computer", "Pol_Sort_Zulu");
        yield return BuildCase(f, "File_MissingCloseBracket", "Registry.pol whose first record has no closing bracket", Crafted.Raw("missing bracket Registry.pol", Crafted.MachinePol, good[..^2]),
            new[] { "Pol_Class_Machine", "Pol_Value_Decimal" }, "Computer", "Pol_Sort_Zulu");
        yield return BuildCase(f, "File_OnlyGarbage", "Registry.pol that is eight bytes of text", Crafted.Raw("garbage Registry.pol", Crafted.MachinePol, System.Text.Encoding.ASCII.GetBytes("notapol!")),
            new[] { "Pol_Class_Machine" }, "Computer", "Pol_Sort_Zulu");
        yield return BuildCase(f, "File_LargeValueName", "a record whose value name is 300 characters", Crafted.Machine("long value name", Dword(basics, new string('v', 300), 1), Dword(basics, "MachineFlag", 1)),
            new[] { "Pol_Class_Machine" }, "Computer", "Pol_Sort_Zulu");
    }

    private static MatrixCase BuildCase(
        FixtureModel f,
        string id,
        string description,
        StartState start,
        string[] affected,
        string root,
        string unrelated,
        string[]? alsoRoots = null,
        string group = "D",
        Func<string, PolicyInfo>? affectedPolicy = null)
    {
        // Group K reuses this with its own group letter and a lookup for its edge fixture policy; group D passes neither.
        Func<string, PolicyInfo> lookup = affectedPolicy ?? f.Policy;
        var builder = new CaseBuilder(group, id, description).StartWith(start);
        builder.Mark("start");
        IEnumerable<string> roots = new[] { root }.Concat(alsoRoots ?? Array.Empty<string>());
        foreach (string r in roots)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string name in affected)
            {
                PolicyInfo policy = lookup(name);
                if (!policy.Roots.Contains(r))
                {
                    continue;
                }

                if (seen.Add(string.Join("/", policy.NodePath)))
                {
                    builder.Add(Select(r, policy.NodePath));
                    builder.Add(ReadList(r, policy.NodePath));
                }
            }

            foreach (string name in affected)
            {
                PolicyInfo policy = lookup(name);
                if (policy.Roots.Contains(r))
                {
                    builder.Add(Edit(policy, r, label: "read state of " + name + " under " + r));
                }
            }
        }

        if (id.StartsWith("Foreign_", StringComparison.Ordinal))
        {
            builder.Add(Children(root, new[] { "Administrative Templates" }, "children of Administrative Templates"), isOptional: true);
            builder.Add(Select(root, new[] { "Administrative Templates", "Extra Registry Settings" }, "the Extra Registry Settings node"), isOptional: true);
            builder.Add(ReadList(root, new[] { "Administrative Templates", "Extra Registry Settings" }, label: "rows of the Extra Registry Settings node"), isOptional: true);
        }

        PolicyInfo save = f.Policy(unrelated);
        builder.Step("unrelated-save", Edit(save, root, "Enabled"));
        builder.Step("unrelated-cleared", Edit(save, root, "Not Configured", press: new[] { "OK" }));
        return builder.Build();
    }
}

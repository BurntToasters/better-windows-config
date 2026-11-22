using System.Globalization;
using Bwc.Policy.Admx;

namespace Bwc.Policy.Model;

/// <summary>
/// Every state and write rule of the policy model, in one place (docs/failure-modes/policy-state.md,
/// docs/contracts/policy-engine.md). A comment names the capture case that settles a member; a member that points at a row
/// of the Gaps section of docs/parity/gpedit-audit.md has no capture yet. A capture golden that disagrees changes a member
/// here and nothing else.
/// </summary>
internal static class PolicyRules
{
    /// <summary>Probe-confirmed for a policy with a value name: Enabled writes this DWORD by default.</summary>
    public static readonly PolicyValue DefaultEnabledValue = new(PolicyValueKind.Decimal, 1, null);

    /// <summary>The DWORD that Disabled writes by default, see <see cref="PlainValueNameDisabledWritesZero"/>.</summary>
    public static readonly PolicyValue DefaultDisabledValue = new(PolicyValueKind.Decimal, 0, null);

    /// <summary>
    /// A policy with a value name and neither an enabledValue nor a disabledValue writes <see cref="DefaultDisabledValue"/>
    /// when Disabled (true), or deletes the value and writes <c>**del.</c>, like a policy that has only an enabledValue
    /// (false). Classic does the second: capture A/Machine_Disabled and A/User_Disabled (D1).
    /// </summary>
    public static readonly bool PlainValueNameDisabledWritesZero = false;

    /// <summary>
    /// A DWORD 0 at the value of a plain value-name policy reads as Disabled. Settled by D/Mismatch_FlagZero (classic shows
    /// Disabled for a flag that holds 0).
    /// </summary>
    public static readonly bool PlainValueNameZeroReadsAsDisabled = true;

    /// <summary>A delete literal (the template delete element) removes the value and writes <c>**del.</c>.</summary>
    public static readonly bool DeleteLiteralWritesDeleteInstruction = true;

    /// <summary>
    /// Not Configured also removes the <c>**del.</c> and <c>**soft.</c> records of the values it clears, so the policy cannot
    /// read back as Disabled. Probe-confirmed only for the value record itself.
    /// </summary>
    public static readonly bool NotConfiguredClearsInstructions = true;

    /// <summary>
    /// A policy value record that matches neither the enabled nor the disabled literal (a foreign number, a wrong type)
    /// reads as this state.
    /// </summary>
    public const PolicyState UnmatchedValueState = PolicyState.NotConfigured;

    /// <summary>
    /// A <c>**del.</c> record with no value record reads as Disabled. Open lead from the probe: classic may read it as Not
    /// Configured (Not Configured on it wrote nothing). Flip this and <see cref="NotConfiguredClearsInstructions"/> together.
    /// </summary>
    public static readonly bool DeleteInstructionOnlyIsDisabled = true;

    /// <summary>
    /// Tells how a lone <c>**del.</c> record (no value record) reads for this policy shape. Classic shows Disabled when the
    /// template declares neither an enabledValue nor an enabledList (plain value-name policies, elements-only policies:
    /// A/Machine_Disabled, B/Pol_Value_NoValueName_ElementsOnly) and Not Configured when it declares one
    /// (B/Pol_Value_EnabledOnly, B/Pol_Value_EnabledListOnly). Group B settles it for these four shapes (D7). A policy that
    /// declares a disabled value or list writes that and not a delete when Disabled, so a lone delete is Not Configured for
    /// it (D/Del_DisabledValuePolicy, Pol_Value_DisabledOnly).
    /// </summary>
    /// <param name="policy">The policy definition.</param>
    /// <returns>True when the lone delete reads as Disabled.</returns>
    public static bool LoneDeleteReadsAsDisabled(PolicyDefinition policy) =>
        DeleteInstructionOnlyIsDisabled && policy.EnabledValue is null && policy.EnabledList is null
        && policy.DisabledValue is null && policy.DisabledList is null;

    /// <summary>
    /// Disabled on a policy with elements or an enabled list removes those values and writes a <c>**del.</c> for each. False
    /// removes them without an instruction.
    /// </summary>
    public static readonly bool DisabledClearsElementsWithDelete = true;

    /// <summary>Enabled deletes the disabled list values with <c>**del.</c> instead of only removing them.</summary>
    public static readonly bool EnabledClearsDisabledListWithDelete = false;

    /// <summary>An optional element left empty writes a <c>**del.</c> instead of only removing its value.</summary>
    public static readonly bool EmptyOptionalElementWritesDeleteInstruction = false;

    /// <summary>
    /// An unchecked boolean with nothing to write at its value name removes the value and writes a <c>**del.</c> for it.
    /// Settled by B/Pol_Bool_NoFalseValue, B/Pol_Bool_TrueFalseLists and B/Pol_Amp_LabelSingle.
    /// </summary>
    public static readonly bool FalseBooleanWithoutFalseValueWritesDeleteInstruction = true;

    /// <summary>A boolean with a value name and no true or false value or list writes DWORD 1 and 0.</summary>
    public static readonly bool BooleanDefaultsToOneAndZero = true;

    /// <summary>A list item with no value in the template is skipped (true) or written as the default enabled value.</summary>
    public static readonly bool ValuelessListItemIsSkipped = true;

    /// <summary>The longest text when the element has no maxLength.</summary>
    public const ulong DefaultTextMaxLength = 1023;

    /// <summary>The soft attribute does not change range checking (true). False skips the check for a soft number.</summary>
    public static readonly bool SoftIgnoredByValidation = true;

    /// <summary>
    /// Choosing an enum item removes the values of the value lists of the other items. Classic does not:
    /// B/Pol_Enum_ItemValueList steps 3 and 4 keep the list of the first item while the second and third items are chosen.
    /// </summary>
    public static readonly bool EnumClearsOtherItemLists = false;

    /// <summary>
    /// Enabling a non-additive list (ADMX default) writes <c>**delvals.</c> at the front of its key and then the entries, which
    /// is how Group Policy expresses "replace the values of the key". Settled by B/Pol_List_Plain, B/Pol_List_ValuePrefix,
    /// B/Pol_SharedList_One and C/SharedList_OneThenTwo: replaying them with this false fails the byte comparison.
    /// </summary>
    public static readonly bool ListReplaceWritesDelvals = true;

    /// <summary>
    /// An additive list (additive="true") adds its entries and keeps the values of its key that it does not own. Only the
    /// entries in <see cref="ListValue.Removed"/> (the rows the dialog removed) leave the file. False makes the list behave
    /// like a non-additive one: the key ends up with exactly the new entries. B/Pol_List_Additive shows the entries only (no
    /// <c>**delvals.</c> at the key). No capture starts with a foreign value at an additive key: gap GAP-03.
    /// </summary>
    public static readonly bool AdditiveListKeepsOtherValues = true;

    /// <summary>
    /// Enabling an additive list removes a <c>**delvals.</c> from its key, so the key no longer says "replace". False leaves
    /// the record where it is. No capture starts with a delete-values record at an additive key: gap GAP-04.
    /// </summary>
    public static readonly bool AdditiveListRemovesDelvals = true;

    /// <summary>
    /// Disabled on a list policy removes the values of every list key and writes <c>**delvals.</c> there, so a list-only
    /// policy reads back as Disabled through <see cref="ListDelvalsOnlyIsDisabled"/>. False writes nothing for the list and
    /// a list-only policy then reads as Not Configured. Applies to additive and non-additive lists alike. Settled by
    /// B/Pol_List_Additive step 5 and B/Pol_List_Plain step 5 (Disabled leaves one <c>**delvals.</c> at the key).
    /// </summary>
    public static readonly bool DisabledListWritesDelvals = true;

    /// <summary>
    /// Enabled with an empty list writes <c>**delvals.</c> and no entries. False (default) removes the values and the
    /// <c>**delvals.</c> and writes nothing, because delvals alone reads as Disabled for a list-only policy. Settled by
    /// B/Pol_List_Plain step 4 and C/SharedList_EmptyList: OK on an empty list changes no file.
    /// </summary>
    public static readonly bool EnabledEmptyListWritesDelvals = false;

    /// <summary>A <c>**delvals.</c> record in a list key with no entries reads as Disabled (no value name).</summary>
    public static readonly bool ListDelvalsOnlyIsDisabled = true;

    /// <summary>
    /// Not Configured removes every value and the <c>**delvals.</c> of a list key, also when another policy shares the key.
    /// Other instructions of the key stay.
    /// </summary>
    public static readonly bool NotConfiguredClearsWholeListKey = true;

    /// <summary>
    /// A policy with no value name reads as Enabled when at least one element has a value, whatever the other elements hold.
    /// </summary>
    /// <param name="present">The elements that have a value.</param>
    /// <param name="total">The elements of the policy.</param>
    /// <returns>True when that is evidence of Enabled.</returns>
    public static bool IsEnabledByElementEvidence(int present, int total) => total > 0 && present > 0;

    /// <summary>
    /// A policy with a value name and no values, lists or elements: a plain flag. Its record is read as a number
    /// (D/Mismatch_FlagValue2 and the WrongType_Flag cases).
    /// </summary>
    /// <param name="policy">The policy definition.</param>
    /// <returns>True for a plain flag.</returns>
    public static bool IsPlainFlag(PolicyDefinition policy) =>
        policy.ValueName is not null && policy.EnabledValue is null && policy.DisabledValue is null
        && policy.EnabledList is null && policy.DisabledList is null && policy.Elements.Count == 0;

    /// <summary>
    /// The data of a record as a number, whatever its type: little-endian, nothing when it is longer than the size the reader
    /// has room for (D/WrongType_FlagAsQword, D/WrongType_FlagEightBytes, D/WrongType_DecimalAsString).
    /// </summary>
    /// <param name="record">The record.</param>
    /// <param name="maxBytes">The room: 4 for a DWORD, 8 for a QWORD.</param>
    /// <returns>The number, or null when the data does not fit.</returns>
    public static ulong? LooseNumber(Bwc.Gpo.Pol.PolRecord record, int maxBytes)
    {
        var data = record.Data.Span;
        if (data.Length > maxBytes)
        {
            return null;
        }

        ulong number = 0;
        for (var i = data.Length - 1; i >= 0; i--)
        {
            number = (number << 8) | data[i];
        }

        return number;
    }

    /// <summary>
    /// The data of a record as text, whatever its type: whole UTF-16 characters up to the first NUL
    /// (D/WrongType_DecimalAsString shows the character U+0007 for a DWORD 7).
    /// </summary>
    /// <param name="record">The record.</param>
    /// <returns>The text.</returns>
    public static string LooseText(Bwc.Gpo.Pol.PolRecord record)
    {
        var data = record.Data.Span;
        var text = new System.Text.StringBuilder();
        for (var i = 0; i + 1 < data.Length; i += 2)
        {
            var c = (char)(data[i] | (data[i + 1] << 8));
            if (c == '\0')
            {
                break;
            }

            text.Append(c);
        }

        return text.ToString();
    }

    /// <summary>
    /// Tells whether a record of a list key is an entry of the list. A plain list (no explicit value, no prefix) keeps the
    /// entry as its own name, so a record with other data in its name is none of its entries: D/List_PlainValues,
    /// D/List_PlainGapNames, D/List_PlainOtherNames, D/List_AdditiveValues, D/List_SharedKey and D/Order_ParentAfterChild
    /// read Not Configured, B/Pol_List_Plain reads Enabled for names equal to the data. A prefix or explicit list takes any
    /// name (B/Pol_List_EmptyPrefix, D/List_ExplicitValues).
    /// </summary>
    /// <param name="list">The list element.</param>
    /// <param name="name">The value name of the record.</param>
    /// <param name="data">The text of the record.</param>
    /// <returns>True when the record is an entry.</returns>
    public static bool IsListEntryOf(Bwc.Policy.Admx.ListElement list, string name, string data) =>
        list.ExplicitValue || list.ValuePrefix is not null || string.Equals(name, data, StringComparison.Ordinal);

    /// <summary>
    /// Tells whether a stored record has the type that the template expects. Exact: a REG_SZ never stands for a
    /// REG_EXPAND_SZ.
    /// </summary>
    /// <param name="expected">The expected type number.</param>
    /// <param name="actual">The type number of the record.</param>
    /// <returns>True when the record has a usable type.</returns>
    public static bool StoredTypeMatches(uint expected, uint actual) => expected == actual;

    /// <summary>The order in which the elements of a policy are written: template document order.</summary>
    /// <param name="elements">The elements in document order.</param>
    /// <returns>The elements in write order.</returns>
    public static IEnumerable<PolicyElement> WriteOrder(IReadOnlyList<PolicyElement> elements) => elements;

    /// <summary>The lines of a multiText that are written: empty lines are dropped.</summary>
    /// <param name="lines">The lines as entered.</param>
    /// <returns>The lines to store.</returns>
    public static IReadOnlyList<string> NormalizeMultiTextLines(IEnumerable<string> lines) =>
        lines.Where(l => l.Length > 0).ToArray();

    /// <summary>The length that a multiText maxLength is compared with: all characters plus one separator per break.</summary>
    /// <param name="lines">The normalized lines.</param>
    /// <returns>The length.</returns>
    public static ulong MultiTextLength(IReadOnlyList<string> lines) =>
        lines.Count == 0 ? 0UL : (ulong)lines.Sum(l => l.Length) + (ulong)(lines.Count - 1);

    /// <summary>
    /// The value name of a list entry. explicitValue: the name of the entry. A valuePrefix attribute, empty or not: the prefix
    /// and the one-based position (B/Pol_List_EmptyPrefix writes the names 1 and 2, B/Pol_List_ValuePrefix a prefix and the
    /// position). No attribute: the data itself (B/Pol_List_Plain, OQ-06).
    /// </summary>
    /// <param name="list">The list element.</param>
    /// <param name="index">The zero-based position of the entry.</param>
    /// <param name="entry">The entry.</param>
    /// <returns>The name, or an empty string when an explicitValue entry has no name.</returns>
    public static string ListEntryName(ListElement list, int index, ListEntry entry)
    {
        if (list.ExplicitValue)
        {
            return entry.Name ?? string.Empty;
        }

        if (list.ValuePrefix is not null)
        {
            return list.ValuePrefix + (index + 1).ToString(CultureInfo.InvariantCulture);
        }

        return entry.Data;
    }
}

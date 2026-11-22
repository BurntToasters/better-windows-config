using System.Globalization;
using Bwc.Policy.Admx;

namespace Bwc.Policy.Model;

/// <summary>
/// Every state and write rule of the policy model that classic has not confirmed, in one place. Each member is *verify
/// against oracle* (docs/failure-modes/policy-state.md, docs/contracts/policy-engine.md) unless its comment says
/// probe-confirmed. A capture golden that disagrees changes a member here and nothing else.
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
    /// A DWORD 0 at the value of a plain value-name policy reads as Disabled, as the earlier version of this engine wrote it.
    /// No capture of a crafted file settles it yet; verify against oracle (group D).
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
    /// Disabled on a policy with elements or an enabled list removes those values and writes a <c>**del.</c> for each. False
    /// removes them without an instruction.
    /// </summary>
    public static readonly bool DisabledClearsElementsWithDelete = true;

    /// <summary>Enabled deletes the disabled list values with <c>**del.</c> instead of only removing them.</summary>
    public static readonly bool EnabledClearsDisabledListWithDelete = false;

    /// <summary>An optional element left empty writes a <c>**del.</c> instead of only removing its value.</summary>
    public static readonly bool EmptyOptionalElementWritesDeleteInstruction = false;

    /// <summary>An unchecked boolean that has no false side writes a <c>**del.</c> for its true side value.</summary>
    public static readonly bool FalseBooleanWithoutFalseValueWritesDeleteInstruction = false;

    /// <summary>A boolean with a value name and no true or false value or list writes DWORD 1 and 0.</summary>
    public static readonly bool BooleanDefaultsToOneAndZero = true;

    /// <summary>A list item with no value in the template is skipped (true) or written as the default enabled value.</summary>
    public static readonly bool ValuelessListItemIsSkipped = true;

    /// <summary>The longest text when the element has no maxLength.</summary>
    public const ulong DefaultTextMaxLength = 1023;

    /// <summary>The soft attribute does not change range checking (true). False skips the check for a soft number.</summary>
    public static readonly bool SoftIgnoredByValidation = true;

    /// <summary>Choosing an enum item removes the values of the value lists of the other items.</summary>
    public static readonly bool EnumClearsOtherItemLists = true;

    /// <summary>
    /// Enabling a non-additive list (ADMX default) writes <c>**delvals.</c> at the front of its key and then the entries, which
    /// is how Group Policy expresses "replace the values of the key". Verify against oracle: the exact bytes and position
    /// (capture group B lists, group C OQ-04). False writes the entries alone.
    /// </summary>
    public static readonly bool ListReplaceWritesDelvals = true;

    /// <summary>
    /// An additive list (additive="true") adds its entries and keeps the values of its key that it does not own. Only the
    /// entries in <see cref="ListValue.Removed"/> (the rows the dialog removed) leave the file. False makes the list behave
    /// like a non-additive one: the key ends up with exactly the new entries. Verify against oracle.
    /// </summary>
    public static readonly bool AdditiveListKeepsOtherValues = true;

    /// <summary>
    /// Enabling an additive list removes a <c>**delvals.</c> from its key, so the key no longer says "replace". False leaves
    /// the record where it is. Verify against oracle.
    /// </summary>
    public static readonly bool AdditiveListRemovesDelvals = true;

    /// <summary>
    /// Disabled on a list policy removes the values of every list key and writes <c>**delvals.</c> there, so a list-only
    /// policy reads back as Disabled through <see cref="ListDelvalsOnlyIsDisabled"/>. False writes nothing for the list and
    /// a list-only policy then reads as Not Configured. Applies to additive and non-additive lists alike. Verify against
    /// oracle (matrix B for the write, group D for the read).
    /// </summary>
    public static readonly bool DisabledListWritesDelvals = true;

    /// <summary>
    /// Enabled with an empty list writes <c>**delvals.</c> and no entries. False (default) removes the values and the
    /// <c>**delvals.</c> and writes nothing, because delvals alone reads as Disabled for a list-only policy. Verify against
    /// oracle.
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
    /// The value name of a list entry. explicitValue: the name of the entry. A prefix that is not empty: the prefix and
    /// the one-based position. Otherwise (no attribute or an empty prefix) the data itself (OQ-06).
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

        if (!string.IsNullOrEmpty(list.ValuePrefix))
        {
            return list.ValuePrefix + (index + 1).ToString(CultureInfo.InvariantCulture);
        }

        return entry.Data;
    }
}

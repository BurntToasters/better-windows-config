using Bwc.Gpo.Pol;
using Bwc.Policy.Admx;

namespace Bwc.Policy.Model;

/// <summary>
/// Reads the state of a policy from a Registry.pol model the way classic shows it. Pure: it never changes the document.
/// The rules are in docs/failure-modes/policy-state.md and the unconfirmed ones in <see cref="PolicyRules"/>.
/// </summary>
public static class PolicyStateReader
{
    /// <summary>Reads one policy from one document (one hive; call it once per hive for a class Both policy).</summary>
    /// <param name="policy">The policy definition.</param>
    /// <param name="document">The document of the hive.</param>
    /// <returns>A snapshot.</returns>
    public static PolicyStateSnapshot Read(PolicyDefinition policy, PolDocument document)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(document);

        // Classic matches a key whose file spelling ends in a backslash as if it did not (capture
        // K/KeyTrailingBackslash_ReadAsWritten).
        if (PolKeySpelling.HasTrailingBackslash(document))
        {
            document = PolKeySpelling.TrimmedCopy(document);
        }

        var found = ReadElements(policy, document);
        var state = ReadState(policy, document, found);
        var elements = new Dictionary<string, ElementValue>(StringComparer.OrdinalIgnoreCase);
        if (state == PolicyState.Enabled)
        {
            foreach (var pair in found)
            {
                elements[pair.Key] = pair.Value;
            }

            foreach (var boolean in policy.Elements.OfType<BooleanElement>())
            {
                if (!elements.ContainsKey(boolean.Id) && boolean.ValueName is not null && !PolicyLayout.HasLiteralFor(boolean, isTrue: false)
                    && PolicyLayout.BooleanSide(boolean, isTrue: true).Count > 0)
                {
                    elements[boolean.Id] = new BooleanValue(false);
                }
            }
        }

        return new PolicyStateSnapshot(state, elements, OwnedRecords(policy, document));
    }

    private static PolicyState ReadState(PolicyDefinition policy, PolDocument document, Dictionary<string, ElementValue> found)
    {
        // An unchecked box is no evidence of Enabled: with every check box of an elements-only policy unchecked, classic shows
        // Disabled (B/Pol_Bool_TrueFalseValues, B/Pol_Bool_StringValues and B/Pol_Bool_TwoChecksReverseOrder, "before Disabled"
        // after the unchecked save).
        var elementsWithValue = found.Values.Count(v => v is not BooleanValue { Checked: false });
        var onlyUncheckedBoxes = found.Count > 0 && elementsWithValue == 0;
        if (policy.ValueName is not null)
        {
            return ReadValueNameState(policy, document);
        }

        if (PolicyEncoding.AllMatch(document, PolicyLayout.ListWrites(policy.EnabledList))
            || PolicyRules.IsEnabledByElementEvidence(elementsWithValue, policy.Elements.Count))
        {
            return PolicyState.Enabled;
        }

        if (PolicyEncoding.AllMatch(document, PolicyLayout.ListWrites(policy.DisabledList)) || onlyUncheckedBoxes)
        {
            return PolicyState.Disabled;
        }

        if (PolicyRules.LoneDeleteReadsAsDisabled(policy) && PolicyLayout.EnabledSideSlots(policy).Any(s => PolicyEncoding.HasDelete(document, s)))
        {
            return PolicyState.Disabled;
        }

        if (PolicyRules.ListDelvalsOnlyIsDisabled && policy.Elements.OfType<ListElement>().Any(l =>
                document.GetValue(l.EffectiveKey, PolicyEncoding.DeleteValuesName) is not null))
        {
            return PolicyState.Disabled;
        }

        return PolicyState.NotConfigured;
    }

    private static PolicyState ReadValueNameState(PolicyDefinition policy, PolDocument document)
    {
        var slot = new Slot(policy.Key, policy.ValueName!);
        if (PolicyRules.IsPlainFlag(policy) && document.GetValue(slot.Key, slot.Name) is { } flag
            && PolicyRules.LooseNumber(flag, maxBytes: 4) is { } flagNumber)
        {
            // A policy with no values, lists or elements reads the data as a number whatever its type: a nonzero number is
            // Enabled and zero is Disabled (D/Mismatch_FlagValue2, D/Mismatch_FlagZero, D/WrongType_FlagAsBinary,
            // D/WrongType_FlagAsString, D/WrongType_FlagShortSize). More than four bytes is no number
            // (D/WrongType_FlagAsQword, D/WrongType_FlagEightBytes) and falls through to the unmatched rule.
            return flagNumber != 0 ? PolicyState.Enabled : PolicyState.Disabled;
        }

        if (PolicyLayout.EnabledReadLiteral(policy) is { } enabled && PolicyEncoding.Matches(document, new ValueWrite(slot, enabled)))
        {
            return PolicyState.Enabled;
        }

        var disabled = PolicyLayout.DisabledReadLiteral(policy);
        if (disabled is not null && PolicyEncoding.Matches(document, new ValueWrite(slot, disabled)))
        {
            return PolicyState.Disabled;
        }

        if (document.GetValue(slot.Key, slot.Name) is not null)
        {
            return PolicyRules.UnmatchedValueState;
        }

        // The disabled list shows Disabled when the value itself says nothing, but only with the delete instruction of the
        // policy value: B/Pol_Value_BothLists step 2 holds the instruction and the disabled item and classic shows Disabled for
        // it; D/BothLists_Disabled holds the disabled item alone and classic shows Not Configured.
        if (PolicyEncoding.HasDelete(document, slot) && PolicyEncoding.AllMatch(document, PolicyLayout.ListWrites(policy.DisabledList)))
        {
            return PolicyState.Disabled;
        }

        return PolicyRules.LoneDeleteReadsAsDisabled(policy) && PolicyEncoding.HasDelete(document, slot)
            ? PolicyState.Disabled
            : PolicyState.NotConfigured;
    }

    private static Dictionary<string, ElementValue> ReadElements(PolicyDefinition policy, PolDocument document)
    {
        var found = new Dictionary<string, ElementValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in policy.Elements)
        {
            var value = ReadElement(element, document);
            if (value is not null)
            {
                found[element.Id] = value;
            }
        }

        return found;
    }

    private static ElementValue? ReadElement(PolicyElement element, PolDocument document) =>
        element switch
        {
            BooleanElement boolean => ReadBoolean(boolean, document),
            DecimalElement number => ReadNumber(number, document),
            TextElement text => ReadText(text, document),
            MultiTextElement multi => ReadMultiText(multi, document),
            EnumElement choice => ReadEnum(choice, document),
            ListElement list => ReadList(list, document),
            _ => null,
        };

    private static PolRecord? Record(PolicyElement element, PolDocument document)
    {
        if (element.ValueName is null)
        {
            return null;
        }

        // A soft number lives under the soft name (B/Pol_LongDecimal_RequiredSoftText).
        var name = element is DecimalElement { Soft: true } ? PolicyEncoding.SoftPrefix + element.ValueName : element.ValueName;
        return document.GetValue(element.EffectiveKey, name);
    }

    private static ElementValue? ReadBoolean(BooleanElement element, PolDocument document)
    {
        if (element.ValueName is not null && element.TrueList is not { Items.Count: > 0 } && element.FalseList is not { Items.Count: > 0 })
        {
            return ReadPlainBoolean(element, document);
        }

        if (PolicyEncoding.AllMatch(document, PolicyLayout.BooleanSide(element, isTrue: true)))
        {
            return new BooleanValue(true);
        }

        // An unchecked box with no false literal leaves only a delete instruction (and perhaps the false list entries), and
        // classic reads that policy as Disabled: B/Pol_Bool_TrueFalseLists step 4 shows "Disabled" with the false list in the
        // file. Only a false literal at the value name counts as evidence of an unchecked box.
        if (!PolicyLayout.HasLiteralFor(element, isTrue: false))
        {
            return null;
        }

        return PolicyEncoding.AllMatch(document, PolicyLayout.BooleanSide(element, isTrue: false)) ? new BooleanValue(false) : null;
    }

    // A boolean with a value name and no lists. The record is read whatever its type: it is unchecked when it equals the false
    // value, or zero when the template has none, and checked otherwise (D/Partial_BoolNoFalse reads 0 as unchecked and 2 as
    // checked, D/Partial_BoolValues reads 30 as checked next to 10 and 20, D/Partial_BoolStrings reads YES as checked).
    private static ElementValue? ReadPlainBoolean(BooleanElement element, PolDocument document)
    {
        var record = document.GetValue(element.EffectiveKey, element.ValueName!);
        if (record is null)
        {
            return null;
        }

        var falseValue = element.FalseValue ?? (element.TrueValue is { Kind: PolicyValueKind.String } ? null : PolicyRules.DefaultDisabledValue);
        if (falseValue is null)
        {
            return PolicyEncoding.Matches(document, new ValueWrite(new Slot(element.EffectiveKey, element.ValueName!), element.TrueValue!))
                ? new BooleanValue(true)
                : null;
        }

        if (falseValue.Kind == PolicyValueKind.String)
        {
            return new BooleanValue(!string.Equals(PolicyRules.LooseText(record), falseValue.Text, StringComparison.OrdinalIgnoreCase));
        }

        return PolicyRules.LooseNumber(record, maxBytes: 4) is { } number ? new BooleanValue(number != falseValue.Number) : null;
    }

    // A number element is present when a record has its name and fits its size, whatever the type: a decimal needs at
    // most four bytes, a long decimal at most eight (D/WrongType_DecimalAsString: a string of eight bytes is not read;
    // D/WrongType_LongAsDword: a DWORD is read as a long number). A storeAsText number reads the data as text and takes 0
    // when the text is no number (D/WrongType_StoreAsTextAsDword: Enabled, 0 shown).
    private static ElementValue? ReadNumber(DecimalElement element, PolDocument document)
    {
        var record = Record(element, document);
        if (record is null)
        {
            return null;
        }

        if (element.StoreAsText)
        {
            var digits = PolicyRules.LooseText(record);
            return new NumberValue(ulong.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0);
        }

        return PolicyRules.LooseNumber(record, element.Kind == PolicyElementKind.LongDecimal ? 8 : 4) is { } number ? new NumberValue(number) : null;
    }

    // A text element is present when a record has its name, whatever its type, and shows the data read as text:
    // D/WrongType_DecimalAsString (a DWORD 7 shows the character U+0007), D/WrongType_ExpandableAsPlain (a REG_SZ in an
    // expandable box and the reverse).
    private static ElementValue? ReadText(TextElement element, PolDocument document)
    {
        var record = Record(element, document);
        return record is null ? null : new TextValue(PolicyRules.LooseText(record));
    }

    // A multi-line element is present when a record has its name; only a REG_MULTI_SZ gives lines (D/WrongType_MultiAsString:
    // a REG_SZ is Enabled and shows no text).
    private static ElementValue? ReadMultiText(MultiTextElement element, PolDocument document)
    {
        var record = Record(element, document);
        if (record is null)
        {
            return null;
        }

        var text = (uint)PolValueType.MultiString == record.Type ? PolData.TryGetString(record.Data.Span) : string.Empty;
        if (text is null)
        {
            return new MultiTextValue([]);
        }

        var lines = text.TrimEnd('\0').Split('\0', StringSplitOptions.None).Where(l => l.Length > 0).ToArray();
        // A record with no line is the empty multi-text that the dialog saves by default (B/Pol_MultiText_Basic step 1) and
        // it shows Enabled, so it is a value.
        return new MultiTextValue(lines);
    }

    private static ElementValue? ReadEnum(EnumElement element, PolDocument document)
    {
        if (element.ValueName is null)
        {
            return null;
        }

        // The item is the first whose value matches and whose value list is all there. When a value matches an item and no item
        // has its list complete, the element is still there and the box shows the first item: D/EnumValueList_ItemC (value 3, the
        // list of item C missing) and D/EnumValueList_ItemAPartial (value 1, one of two list values) show Profile A.
        var slot = new Slot(element.EffectiveKey, element.ValueName);
        var valueMatched = false;
        for (var i = 0; i < element.Items.Count; i++)
        {
            var literal = element.Items[i].Value;
            if (literal is null || !PolicyEncoding.Matches(document, new ValueWrite(slot, literal)))
            {
                continue;
            }

            valueMatched = true;
            var list = PolicyLayout.ListWrites(element.Items[i].ValueList);
            if (list.Count == 0 || PolicyEncoding.AllMatch(document, list))
            {
                return new ChoiceValue(i);
            }
        }

        return valueMatched ? new ChoiceValue(0) : null;
    }

    private static ElementValue? ReadList(ListElement element, PolDocument document)
    {
        var type = element.Expandable ? PolValueType.ExpandString : PolValueType.String;
        var entries = new List<ListEntry>();
        foreach (var record in document.GetRecords(element.EffectiveKey))
        {
            if (record.IsInstruction || !PolicyRules.StoredTypeMatches((uint)type, record.Type))
            {
                continue;
            }

            var data = PolData.TryGetString(record.Data.Span);
            if (data is not null && PolicyRules.IsListEntryOf(element, record.ValueName, data))
            {
                entries.Add(new ListEntry(record.ValueName, data));
            }
        }

        return entries.Count == 0 ? null : new ListValue(entries);
    }

    private static List<PolicyOwnedRecord> OwnedRecords(PolicyDefinition policy, PolDocument document)
    {
        var owned = new List<PolicyOwnedRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(PolRecord? record)
        {
            if (record is not null && seen.Add(record.Key + "\u0001" + record.ValueName))
            {
                owned.Add(new PolicyOwnedRecord(record.Key, record.ValueName));
            }
        }

        foreach (var slot in PolicyLayout.AllSlots(policy))
        {
            Add(document.GetValue(slot.Key, slot.Name));
            Add(document.GetValue(slot.Key, PolicyEncoding.DeletePrefix + slot.Name));
            Add(document.GetValue(slot.Key, PolicyEncoding.SoftPrefix + slot.Name));
        }

        foreach (var key in PolicyLayout.ListKeys(policy))
        {
            foreach (var record in document.GetRecords(key))
            {
                Add(record);
            }
        }

        return owned;
    }
}

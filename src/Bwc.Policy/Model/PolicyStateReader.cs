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
        var state = ReadState(policy, document, found.Count);
        var elements = new Dictionary<string, ElementValue>(StringComparer.OrdinalIgnoreCase);
        if (state == PolicyState.Enabled)
        {
            foreach (var pair in found)
            {
                elements[pair.Key] = pair.Value;
            }

            foreach (var boolean in policy.Elements.OfType<BooleanElement>())
            {
                if (!elements.ContainsKey(boolean.Id) && PolicyLayout.BooleanSide(boolean, isTrue: false).Count == 0
                    && PolicyLayout.BooleanSide(boolean, isTrue: true).Count > 0)
                {
                    elements[boolean.Id] = new BooleanValue(false);
                }
            }
        }

        return new PolicyStateSnapshot(state, elements, OwnedRecords(policy, document));
    }

    private static PolicyState ReadState(PolicyDefinition policy, PolDocument document, int elementsWithValue)
    {
        if (policy.ValueName is not null)
        {
            return ReadValueNameState(policy, document);
        }

        if (PolicyEncoding.AllMatch(document, PolicyLayout.ListWrites(policy.EnabledList))
            || PolicyRules.IsEnabledByElementEvidence(elementsWithValue, policy.Elements.Count))
        {
            return PolicyState.Enabled;
        }

        if (PolicyEncoding.AllMatch(document, PolicyLayout.ListWrites(policy.DisabledList)))
        {
            return PolicyState.Disabled;
        }

        if (PolicyRules.DeleteInstructionOnlyIsDisabled && PolicyLayout.EnabledSideSlots(policy).Any(s => PolicyEncoding.HasDelete(document, s)))
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
        if (PolicyEncoding.Matches(document, new ValueWrite(slot, PolicyLayout.EnabledLiteral(policy))))
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

        return PolicyRules.DeleteInstructionOnlyIsDisabled && PolicyEncoding.HasDelete(document, slot)
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

    private static PolRecord? Record(PolicyElement element, PolDocument document, PolValueType type)
    {
        if (element.ValueName is null)
        {
            return null;
        }

        var record = document.GetValue(element.EffectiveKey, element.ValueName);
        return record is not null && PolicyRules.StoredTypeMatches((uint)type, record.Type) ? record : null;
    }

    private static ElementValue? ReadBoolean(BooleanElement element, PolDocument document)
    {
        if (PolicyEncoding.AllMatch(document, PolicyLayout.BooleanSide(element, isTrue: true)))
        {
            return new BooleanValue(true);
        }

        return PolicyEncoding.AllMatch(document, PolicyLayout.BooleanSide(element, isTrue: false)) ? new BooleanValue(false) : null;
    }

    private static ElementValue? ReadNumber(DecimalElement element, PolDocument document)
    {
        if (element.StoreAsText)
        {
            var text = Record(element, document, PolValueType.String);
            var digits = text is null ? null : PolData.TryGetString(text.Data.Span);
            return digits is not null && ulong.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? new NumberValue(parsed)
                : null;
        }

        if (element.Kind == PolicyElementKind.LongDecimal)
        {
            var record = Record(element, document, PolValueType.QWord);
            var number = record is null ? null : PolData.TryGetQWord(record.Data.Span);
            return number is null ? null : new NumberValue(number.Value);
        }

        var dword = Record(element, document, PolValueType.DWord);
        var small = dword is null ? null : PolData.TryGetDWord(dword.Data.Span);
        return small is null ? null : new NumberValue(small.Value);
    }

    private static ElementValue? ReadText(TextElement element, PolDocument document)
    {
        var record = Record(element, document, element.Expandable ? PolValueType.ExpandString : PolValueType.String);
        var text = record is null ? null : PolData.TryGetString(record.Data.Span);
        return text is null ? null : new TextValue(text);
    }

    private static ElementValue? ReadMultiText(MultiTextElement element, PolDocument document)
    {
        var record = Record(element, document, PolValueType.MultiString);
        var text = record is null ? null : PolData.TryGetString(record.Data.Span);
        if (text is null)
        {
            return null;
        }

        var lines = text.TrimEnd('\0').Split('\0', StringSplitOptions.None).Where(l => l.Length > 0).ToArray();
        return lines.Length == 0 ? null : new MultiTextValue(lines);
    }

    private static ElementValue? ReadEnum(EnumElement element, PolDocument document)
    {
        if (element.ValueName is null)
        {
            return null;
        }

        var slot = new Slot(element.EffectiveKey, element.ValueName);
        for (var i = 0; i < element.Items.Count; i++)
        {
            var literal = element.Items[i].Value;
            if (literal is not null && PolicyEncoding.Matches(document, new ValueWrite(slot, literal)))
            {
                return new ChoiceValue(i);
            }
        }

        return null;
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
            if (data is not null)
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

using System.Globalization;
using Bwc.Gpo.Pol;
using Bwc.Policy.Admx;

namespace Bwc.Policy.Model;

/// <summary>A registry key and value name.</summary>
/// <param name="Key">The key path.</param>
/// <param name="Name">The value name.</param>
internal readonly record struct Slot(string Key, string Name)
{
    internal string Id => Key + "\u0001" + Name;
}

/// <summary>A value that a template writes at a slot.</summary>
/// <param name="Slot">Where it goes.</param>
/// <param name="Value">What it is.</param>
internal readonly record struct ValueWrite(Slot Slot, PolicyValue Value);

/// <summary>Encoding of template literals and the instruction names of the Registry.pol format.</summary>
internal static class PolicyEncoding
{
    public const string DeletePrefix = "**del.";
    public const string SoftPrefix = "**soft.";
    public const string DeleteValuesName = "**delvals.";

    public static (PolValueType Type, byte[] Data) Encode(PolicyValue value) =>
        value.Kind switch
        {
            PolicyValueKind.Decimal when value.Number <= uint.MaxValue => (PolValueType.DWord, PolData.FromDWord((uint)value.Number)),
            PolicyValueKind.Decimal or PolicyValueKind.LongDecimal => (PolValueType.QWord, PolData.FromQWord(value.Number)),
            _ => (PolValueType.String, PolData.FromString(value.Text ?? string.Empty)),
        };

    public static bool HasDelete(PolDocument document, Slot slot) => document.GetValue(slot.Key, DeletePrefix + slot.Name) is not null;

    public static bool Matches(PolDocument document, ValueWrite write)
    {
        var record = document.GetValue(write.Slot.Key, write.Slot.Name);
        if (write.Value.Kind == PolicyValueKind.Delete)
        {
            return record is null && HasDelete(document, write.Slot);
        }

        if (record is null)
        {
            return false;
        }

        var (type, data) = Encode(write.Value);
        if (!PolicyRules.StoredTypeMatches((uint)type, record.Type))
        {
            return false;
        }

        // A string value is compared without regard to case: D/Mismatch_StringCase reads ON as the value on.
        return write.Value.Kind == PolicyValueKind.String
            ? string.Equals(PolData.TryGetString(record.Data.Span), write.Value.Text ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            : record.Data.Span.SequenceEqual(data);
    }

    public static bool AllMatch(PolDocument document, IReadOnlyList<ValueWrite> writes) =>
        writes.Count > 0 && writes.All(w => Matches(document, w));

    public static string Digits(ulong number) => number.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Which registry values a policy writes, from the template alone.</summary>
internal static class PolicyLayout
{
    public static PolicyValue EnabledLiteral(PolicyDefinition policy) => policy.EnabledValue ?? PolicyRules.DefaultEnabledValue;

    /// <summary>The value Disabled writes at the policy value, or null when Disabled deletes the value.</summary>
    public static PolicyValue? DisabledLiteral(PolicyDefinition policy) =>
        policy.DisabledValue
        ?? (policy.EnabledValue is null && PolicyRules.PlainValueNameDisabledWritesZero ? PolicyRules.DefaultDisabledValue : null);

    /// <summary>
    /// The value that reads as Enabled at the policy value, or null when none does. A policy that declares a disabledValue and
    /// no enabledValue writes DWORD 1 when Enabled but reads it back as Not Configured: capture B/Pol_Value_DisabledOnly (D7).
    /// </summary>
    public static PolicyValue? EnabledReadLiteral(PolicyDefinition policy) =>
        policy.EnabledValue ?? (policy.DisabledValue is null ? PolicyRules.DefaultEnabledValue : null);

    /// <summary>The value that reads as Disabled at the policy value, or null when none does.</summary>
    public static PolicyValue? DisabledReadLiteral(PolicyDefinition policy) =>
        policy.DisabledValue
        ?? (policy.EnabledValue is null && PolicyRules.PlainValueNameZeroReadsAsDisabled ? PolicyRules.DefaultDisabledValue : null);

    public static IReadOnlyList<ValueWrite> ListWrites(PolicyValueList? list)
    {
        var writes = new List<ValueWrite>();
        foreach (var item in list?.Items ?? [])
        {
            var value = item.Value ?? (PolicyRules.ValuelessListItemIsSkipped ? null : PolicyRules.DefaultEnabledValue);
            if (value is not null)
            {
                writes.Add(new ValueWrite(new Slot(item.EffectiveKey, item.ValueName), value));
            }
        }

        return writes;
    }

    public static IReadOnlyList<Slot> ListSlots(PolicyValueList? list) =>
        (list?.Items ?? []).Select(i => new Slot(i.EffectiveKey, i.ValueName)).ToArray();

    public static IReadOnlyList<ValueWrite> EnabledWrites(PolicyDefinition policy)
    {
        var writes = new List<ValueWrite>();
        if (policy.ValueName is not null)
        {
            writes.Add(new ValueWrite(new Slot(policy.Key, policy.ValueName), EnabledLiteral(policy)));
        }

        writes.AddRange(ListWrites(policy.EnabledList));
        return writes;
    }

    public static IReadOnlyList<ValueWrite> DisabledWrites(PolicyDefinition policy)
    {
        var writes = new List<ValueWrite>();
        var literal = DisabledLiteral(policy);
        if (policy.ValueName is not null && literal is not null)
        {
            writes.Add(new ValueWrite(new Slot(policy.Key, policy.ValueName), literal));
        }

        writes.AddRange(ListWrites(policy.DisabledList));
        return writes;
    }

    /// <summary>The value slots of one element. A list element has none: it owns its whole key.</summary>
    public static IReadOnlyList<Slot> ElementSlots(PolicyElement element)
    {
        var slots = new List<Slot>();
        if (element.Kind != PolicyElementKind.List && element.ValueName is not null)
        {
            slots.Add(new Slot(element.EffectiveKey, element.ValueName));
        }

        switch (element)
        {
            case BooleanElement boolean:
                slots.AddRange(ListSlots(boolean.TrueList));
                slots.AddRange(ListSlots(boolean.FalseList));
                break;
            case EnumElement choice:
                foreach (var item in choice.Items)
                {
                    slots.AddRange(ListSlots(item.ValueList));
                }

                break;
        }

        return slots;
    }

    /// <summary>
    /// The slots Disabled and Not Configured remove for one element: its value name and the value lists of its default
    /// side, which is the false list of a boolean and the first item of an enum. The other lists stay:
    /// B/Pol_Bool_TrueFalseLists step 5 keeps the true list, and B/Pol_Enum_ItemValueList steps 5 and 6 keep the list of
    /// the third item and clear the first.
    /// </summary>
    public static IReadOnlyList<Slot> ElementClearSlots(PolicyElement element)
    {
        var slots = new List<Slot>();
        if (element.Kind != PolicyElementKind.List && element.ValueName is not null)
        {
            slots.Add(new Slot(element.EffectiveKey, element.ValueName));
        }

        switch (element)
        {
            case BooleanElement boolean:
                slots.AddRange(ListSlots(boolean.FalseList));
                break;
            case EnumElement { Items.Count: > 0 } choice:
                slots.AddRange(ListSlots(choice.Items[0].ValueList));
                break;
        }

        return slots;
    }

    /// <summary>
    /// The slots Not Configured removes for the policy value and its lists. With a value name the enabled list stays, as in
    /// B/Pol_Value_BothLists step 3 (the disabled list and the value name leave, the enabled items stay).
    /// </summary>
    public static IReadOnlyList<Slot> NotConfiguredSlots(PolicyDefinition policy)
    {
        var slots = new List<Slot>();
        if (policy.ValueName is not null)
        {
            slots.Add(new Slot(policy.Key, policy.ValueName));
        }
        else
        {
            slots.AddRange(ListSlots(policy.EnabledList));
        }

        slots.AddRange(ListSlots(policy.DisabledList));
        foreach (var element in policy.Elements)
        {
            slots.AddRange(ElementClearSlots(element));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return slots.Where(s => seen.Add(s.Id)).ToArray();
    }

    /// <summary>The check box side of a boolean: the values that make it true or false.</summary>
    public static IReadOnlyList<ValueWrite> BooleanSide(BooleanElement element, bool isTrue)
    {
        var writes = new List<ValueWrite>();
        var literal = isTrue ? element.TrueValue : element.FalseValue;
        var list = isTrue ? element.TrueList : element.FalseList;
        if (element.ValueName is not null)
        {
            var slot = new Slot(element.EffectiveKey, element.ValueName);
            if (literal is not null)
            {
                writes.Add(new ValueWrite(slot, literal));
            }
            else if (isTrue && PolicyRules.BooleanDefaultsToOneAndZero)
            {
                // Checked with no true value writes DWORD 1: B/Pol_Amp_LabelSingle (no values at all) and B/Pol_Bool_TrueFalseLists
                // step 2 (lists only). Unchecked with no false value writes nothing here: classic deletes the value with an
                // instruction (B/Pol_Amp_LabelSingle step 3, B/Pol_Bool_NoFalseValue, B/Pol_Bool_TrueFalseLists step 1).
                writes.Add(new ValueWrite(slot, PolicyRules.DefaultEnabledValue));
            }
        }

        writes.AddRange(ListWrites(list));
        return writes;
    }

    /// <summary>Tells whether one side of a boolean writes something at the boolean value name.</summary>
    public static bool HasLiteralFor(BooleanElement element, bool isTrue)
    {
        if (element.ValueName is null)
        {
            return false;
        }

        var slot = new Slot(element.EffectiveKey, element.ValueName);
        return BooleanSide(element, isTrue).Any(w => string.Equals(w.Slot.Id, slot.Id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every slot the policy can write, in a stable order, without duplicates.</summary>
    public static IReadOnlyList<Slot> AllSlots(PolicyDefinition policy)
    {
        var slots = new List<Slot>();
        if (policy.ValueName is not null)
        {
            slots.Add(new Slot(policy.Key, policy.ValueName));
        }

        slots.AddRange(ListSlots(policy.EnabledList));
        slots.AddRange(ListSlots(policy.DisabledList));
        foreach (var element in policy.Elements)
        {
            slots.AddRange(ElementSlots(element));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return slots.Where(s => seen.Add(s.Id)).ToArray();
    }

    public static IReadOnlyList<string> ListKeys(PolicyDefinition policy) =>
        policy.Elements.OfType<ListElement>().Select(l => l.EffectiveKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>The slots that mean Enabled for a policy with no value name: enabled list items and element values.</summary>
    public static IReadOnlyList<Slot> EnabledSideSlots(PolicyDefinition policy)
    {
        var slots = new List<Slot>(ListSlots(policy.EnabledList));
        foreach (var element in policy.Elements)
        {
            slots.AddRange(ElementSlots(element));
        }

        return slots;
    }
}

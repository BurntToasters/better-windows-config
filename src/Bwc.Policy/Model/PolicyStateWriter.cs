using Bwc.Gpo.Pol;
using Bwc.Policy.Admx;

namespace Bwc.Policy.Model;

/// <summary>
/// Applies a <see cref="PolicyEdit"/> to a Registry.pol model the way classic writes it. It only calls
/// <see cref="PolDocument"/> and never does file I/O. The rules are in docs/failure-modes/policy-state.md and the
/// unconfirmed ones in <see cref="PolicyRules"/>.
/// </summary>
public static class PolicyStateWriter
{
    /// <summary>
    /// Validates the edit and, when it is valid, applies it to one document (one hive; call it once per hive for a class Both
    /// policy). An invalid edit writes nothing.
    /// </summary>
    /// <param name="policy">The policy definition.</param>
    /// <param name="document">The document of the hive. It changes only when the edit is valid.</param>
    /// <param name="edit">The new state and element values.</param>
    /// <returns>The problems, or whether the bytes of the document changed.</returns>
    /// <exception cref="ArgumentException">
    /// The template holds a key or value name that the document refuses. That is a template fault, and the apply may then be
    /// partial.
    /// </exception>
    public static PolicyApplyResult Apply(PolicyDefinition policy, PolDocument document, PolicyEdit edit) =>
        Apply(policy, document, edit, commentChanged: false);

    /// <summary>
    /// <see cref="Apply(PolicyDefinition, PolDocument, PolicyEdit)"/> for a save that also changes the comment. A policy with
    /// a value name and no elements that stays Enabled writes its value again then, which moves it to the end of its key:
    /// capture C/Cmtx_FirstSave_ForeignNamespaces step 4.
    /// </summary>
    internal static PolicyApplyResult Apply(PolicyDefinition policy, PolDocument document, PolicyEdit edit, bool commentChanged)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(edit);

        var problems = PolicyEditValidator.Validate(policy, edit);
        if (problems.Count > 0)
        {
            return new PolicyApplyResult(problems, false);
        }

        var before = document.ToBytes();
        var current = PolicyStateReader.Read(policy, document).State;

        // Disabled over a policy that already shows Disabled saves nothing: B/Pol_Bool_TrueFalseValues, B/Pol_Bool_StringValues,
        // B/Pol_Bool_TwoChecksReverseOrder, B/Pol_Bool_TrueFalseLists (the unchecked box reads Disabled, a second Disabled
        // writes no file) and C/Booleans_ReverseOrder.
        if (edit.State == PolicyState.Disabled && current == PolicyState.Disabled)
        {
            return new PolicyApplyResult(problems, false);
        }

        var rewrite = commentChanged && edit.State == PolicyState.Enabled && current == PolicyState.Enabled && policy.Elements.Count == 0;
        if (PolKeySpelling.HasTrailingBackslash(document))
        {
            // Classic spells every key without the trailing backslash once a save changes something, and a save that changes
            // nothing leaves the file alone (capture K/KeyTrailingBackslash_ReadAsWritten). A copy tells which one this is.
            var probe = PolKeySpelling.TrimmedCopy(document);
            var trimmedBefore = probe.ToBytes();
            ApplyPlan(policy, probe, edit, current, rewrite);
            if (probe.ToBytes().AsSpan().SequenceEqual(trimmedBefore))
            {
                return new PolicyApplyResult(problems, false);
            }

            document.TrimTrailingKeyBackslashes();
        }

        ApplyPlan(policy, document, edit, current, rewrite);
        return new PolicyApplyResult(problems, !document.ToBytes().AsSpan().SequenceEqual(before));
    }

    private static void ApplyPlan(PolicyDefinition policy, PolDocument document, PolicyEdit edit, PolicyState current, bool rewrite)
    {
        var plan = new WritePlan();
        switch (edit.State)
        {
            case PolicyState.Enabled:
                PlanEnabled(plan, policy, document, edit, rewrite);
                break;
            case PolicyState.Disabled:
                PlanDisabled(plan, policy);
                break;
            default:
                PlanNotConfigured(plan, policy, current);
                break;
        }

        plan.Execute(document);
    }

    private static void PlanEnabled(WritePlan plan, PolicyDefinition policy, PolDocument document, PolicyEdit edit, bool rewrite)
    {
        foreach (var write in PolicyLayout.EnabledWrites(policy))
        {
            var isPolicyValue = policy.ValueName is not null && write.Slot.Name == policy.ValueName && write.Slot.Key == policy.Key;
            plan.AddWrite(write, move: rewrite && isPolicyValue);
        }

        foreach (var slot in PolicyLayout.ListSlots(policy.DisabledList))
        {
            plan.Remove(slot, PolicyRules.EnabledClearsDisabledListWithDelete);
        }

        foreach (var element in PolicyRules.WriteOrder(policy.Elements))
        {
            edit.Elements.TryGetValue(element.Id, out var value);
            var keepsRemoved = value is ListValue { Removed.Count: > 0 };

            // An optional text or multi-text left empty is saved as an empty value: B/Pol_Amp_LabelDouble, B/Pol_Text_MaxLength,
            // B/Pol_Element_KeyOverride and B/Pol_Shared_ElementsReverseOrder (an empty string), B/Pol_MultiText_Basic (one NUL).
            var savedWhenEmpty = value is TextValue or MultiTextValue;
            PlanElement(plan, element, PolicyEditValidator.IsEmpty(value) && !keepsRemoved && !savedWhenEmpty ? null : value, document);
        }
    }

    private static void PlanDisabled(WritePlan plan, PolicyDefinition policy)
    {
        // The delete instruction of the policy value goes in before the disabled list: B/Pol_Value_BothLists step 2.
        if (policy.ValueName is not null && PolicyLayout.DisabledLiteral(policy) is null)
        {
            plan.Delete(new Slot(policy.Key, policy.ValueName));
        }

        foreach (var write in PolicyLayout.DisabledWrites(policy))
        {
            plan.AddWrite(write);
        }

        // The enabled list gets its delete instructions only when the policy has no value name to carry the state:
        // B/Pol_Value_EnabledListOnly step 2 against B/Pol_Value_BothLists step 2, where the enabled items stay.
        if (policy.ValueName is null)
        {
            foreach (var slot in PolicyLayout.ListSlots(policy.EnabledList))
            {
                plan.Remove(slot, PolicyRules.DisabledClearsElementsWithDelete);
            }
        }

        foreach (var element in policy.Elements)
        {
            if (element is ListElement list)
            {
                plan.ClearListValues(list.EffectiveKey, includeDeleteValues: false);
                if (PolicyRules.DisabledListWritesDelvals)
                {
                    plan.WriteDeleteValues(list.EffectiveKey);
                }

                continue;
            }

            foreach (var slot in PolicyLayout.ElementClearSlots(element))
            {
                plan.Remove(slot, PolicyRules.DisabledClearsElementsWithDelete);
            }
        }
    }

    private static void PlanNotConfigured(WritePlan plan, PolicyDefinition policy, PolicyState current)
    {
        // A policy that shows Not Configured keeps its delete instructions: B/Pol_Value_EnabledOnly and
        // B/Pol_Value_EnabledListOnly step 3 (the lone instruction reads Not Configured and a second Not Configured writes
        // nothing). A policy that shows Disabled loses them (every other group B step 'not-configured').
        var removeInstructions = PolicyRules.NotConfiguredClearsInstructions && current != PolicyState.NotConfigured;
        foreach (var slot in PolicyLayout.NotConfiguredSlots(policy))
        {
            plan.Clear(slot, removeInstructions);
        }

        if (PolicyRules.NotConfiguredClearsWholeListKey)
        {
            foreach (var key in PolicyLayout.ListKeys(policy))
            {
                plan.ClearListValues(key, includeDeleteValues: true);
            }
        }
    }

    private static void PlanElement(WritePlan plan, PolicyElement element, ElementValue? value, PolDocument document)
    {
        switch (element)
        {
            case BooleanElement boolean:
                PlanBoolean(plan, boolean, value as BooleanValue);
                break;
            case DecimalElement number:
                PlanNumber(plan, number, value as NumberValue);
                break;
            case TextElement text:
                PlanText(plan, text, value as TextValue);
                break;
            case MultiTextElement multi:
                PlanMultiText(plan, multi, value as MultiTextValue);
                break;
            case EnumElement choice:
                PlanEnum(plan, choice, value as ChoiceValue);
                break;
            case ListElement list:
                PlanList(plan, list, value as ListValue, document);
                break;
        }
    }

    private static void ClearEmpty(WritePlan plan, PolicyElement element)
    {
        foreach (var slot in PolicyLayout.ElementSlots(element))
        {
            plan.Remove(slot, PolicyRules.EmptyOptionalElementWritesDeleteInstruction);
        }
    }

    private static void PlanBoolean(WritePlan plan, BooleanElement element, BooleanValue? value)
    {
        if (value is null)
        {
            ClearEmpty(plan, element);
            return;
        }

        var written = PolicyLayout.BooleanSide(element, value.Checked);
        foreach (var write in written)
        {
            plan.AddWrite(write, inPlace: true);
        }

        if (element.ValueName is null || PolicyLayout.HasLiteralFor(element, value.Checked))
        {
            return;
        }

        // The side has nothing to write at the value name. Unchecked deletes it with an instruction, also when the false side
        // is a list: B/Pol_Bool_NoFalseValue steps 1 and 3, B/Pol_Bool_TrueFalseLists steps 1 and 3 and B/Pol_Amp_LabelSingle
        // step 3. The value lists of the other side stay: B/Pol_Bool_TrueFalseLists step 3 keeps the true list entries.
        plan.Remove(
            new Slot(element.EffectiveKey, element.ValueName),
            !value.Checked && PolicyRules.FalseBooleanWithoutFalseValueWritesDeleteInstruction,
            inPlace: true);
    }

    private static void PlanNumber(WritePlan plan, DecimalElement element, NumberValue? value)
    {
        if (value is null)
        {
            ClearEmpty(plan, element);
            return;
        }

        if (element.ValueName is null)
        {
            return;
        }

        var slot = new Slot(element.EffectiveKey, element.ValueName);
        var soft = element.Soft;
        if (element.StoreAsText)
        {
            plan.Set(slot, PolValueType.String, PolData.FromString(PolicyEncoding.Digits(value.Number)), inPlace: true, soft: soft);
        }
        else if (element.Kind == PolicyElementKind.LongDecimal)
        {
            plan.Set(slot, PolValueType.QWord, PolData.FromQWord(value.Number), inPlace: true, soft: soft);
        }
        else
        {
            plan.Set(slot, PolValueType.DWord, PolData.FromDWord((uint)value.Number), inPlace: true, soft: soft);
        }
    }

    private static void PlanText(WritePlan plan, TextElement element, TextValue? value)
    {
        if (value is null)
        {
            ClearEmpty(plan, element);
            return;
        }

        if (element.ValueName is not null)
        {
            plan.Set(
                new Slot(element.EffectiveKey, element.ValueName),
                element.Expandable ? PolValueType.ExpandString : PolValueType.String,
                PolData.FromString(value.Text),
                inPlace: true);
        }
    }

    private static void PlanMultiText(WritePlan plan, MultiTextElement element, MultiTextValue? value)
    {
        if (value is null)
        {
            ClearEmpty(plan, element);
            return;
        }

        if (element.ValueName is not null)
        {
            var lines = PolicyRules.NormalizeMultiTextLines(value.Lines);

            // No lines is a single NUL character (B/Pol_MultiText_Basic step 1, 2 bytes); lines end with one more NUL.
            plan.Set(
                new Slot(element.EffectiveKey, element.ValueName),
                PolValueType.MultiString,
                PolData.FromString(lines.Count == 0 ? string.Empty : string.Join('\0', lines) + "\0"));
        }
    }

    private static void PlanEnum(WritePlan plan, EnumElement element, ChoiceValue? value)
    {
        if (value is null)
        {
            ClearEmpty(plan, element);
            return;
        }

        var selected = element.Items[value.ItemIndex];
        if (element.ValueName is not null)
        {
            var slot = new Slot(element.EffectiveKey, element.ValueName);
            if (selected.Value is not null)
            {
                plan.AddWrite(new ValueWrite(slot, selected.Value), inPlace: true);
            }
            else
            {
                plan.Remove(slot, false);
            }
        }

        foreach (var write in PolicyLayout.ListWrites(selected.ValueList))
        {
            plan.AddWrite(write);
        }

        if (!PolicyRules.EnumClearsOtherItemLists)
        {
            return;
        }

        foreach (var item in element.Items.Where(i => !ReferenceEquals(i, selected)))
        {
            foreach (var slot in PolicyLayout.ListSlots(item.ValueList))
            {
                plan.Remove(slot, false);
            }
        }
    }

    private static void PlanList(WritePlan plan, ListElement element, ListValue? value, PolDocument document)
    {
        var key = element.EffectiveKey;
        var entries = value?.Entries ?? [];
        var type = element.Expandable ? PolValueType.ExpandString : PolValueType.String;
        var desired = new List<(string Name, byte[] Data)>();
        for (var i = 0; i < entries.Count; i++)
        {
            desired.Add((PolicyRules.ListEntryName(element, i, entries[i]), PolData.FromString(entries[i].Data)));
        }

        if (element.Additive && PolicyRules.AdditiveListKeepsOtherValues)
        {
            PlanAdditiveList(plan, key, type, value, desired);
            return;
        }

        var replaceWritesDelvals = PolicyRules.ListReplaceWritesDelvals;
        if (desired.Count == 0)
        {
            var keepDelvals = PolicyRules.EnabledEmptyListWritesDelvals && replaceWritesDelvals;
            plan.ClearListValues(key, includeDeleteValues: !keepDelvals);
            if (keepDelvals)
            {
                plan.WriteDeleteValues(key);
            }

            return;
        }

        var existing = document.GetRecords(key).Where(r => !r.IsInstruction).ToArray();
        var same = existing.Length == desired.Count
            && existing.Select((record, i) => (record, i)).All(p =>
                string.Equals(p.record.ValueName, desired[p.i].Name, StringComparison.OrdinalIgnoreCase)
                && p.record.Type == (uint)type
                && p.record.Data.Span.SequenceEqual(desired[p.i].Data));
        if (!same)
        {
            plan.ClearListValues(key, includeDeleteValues: false);
        }

        if (replaceWritesDelvals)
        {
            plan.WriteDeleteValues(key);
        }

        if (same)
        {
            return;
        }

        foreach (var (name, data) in desired)
        {
            plan.Set(new Slot(key, name), type, data);
        }
    }

    private static void PlanAdditiveList(WritePlan plan, string key, PolValueType type, ListValue? value, List<(string Name, byte[] Data)> desired)
    {
        if (PolicyRules.AdditiveListRemovesDelvals)
        {
            plan.RemoveDeleteValues(key);
        }

        var names = new HashSet<string>(desired.Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var removed in value?.Removed ?? [])
        {
            var name = removed.Name ?? removed.Data;
            if (!names.Contains(name))
            {
                plan.RemoveListValue(key, name, type, removed.Data);
            }
        }

        foreach (var (name, data) in desired)
        {
            plan.Set(new Slot(key, name), type, data);
        }
    }

    private sealed class WritePlan
    {
        private readonly List<(string Key, bool IncludeDeleteValues)> listClears = [];
        private readonly List<string> deleteValuesRemovals = [];
        private readonly List<string> deleteValuesWrites = [];
        private readonly List<(string Key, string Name, PolValueType Type, string Data)> listRemovals = [];
        private readonly List<(Slot Slot, bool RemoveInstructions)> clears = [];
        private readonly List<(Slot Slot, bool IsDelete, PolValueType Type, byte[] Data, bool InPlace, bool Soft, bool Move)> writes = [];
        private readonly HashSet<string> claimed = new(StringComparer.OrdinalIgnoreCase);

        public void ClearListValues(string key, bool includeDeleteValues) => listClears.Add((key, includeDeleteValues));

        public void RemoveDeleteValues(string key) => deleteValuesRemovals.Add(key);

        public void WriteDeleteValues(string key) => deleteValuesWrites.Add(key);

        public void RemoveListValue(string key, string name, PolValueType type, string data) => listRemovals.Add((key, name, type, data));

        public void Clear(Slot slot, bool removeInstructions) => clears.Add((slot, removeInstructions));

        public void Set(Slot slot, PolValueType type, byte[] data, bool inPlace = false, bool soft = false, bool move = false)
        {
            claimed.Add(slot.Id);
            writes.Add((slot, false, type, data, inPlace, soft, move));
        }

        public void Delete(Slot slot, bool inPlace = false)
        {
            if (claimed.Add(slot.Id))
            {
                writes.Add((slot, true, PolValueType.String, [], inPlace, false, false));
            }
        }

        public void Remove(Slot slot, bool withDeleteInstruction, bool inPlace = false)
        {
            if (withDeleteInstruction)
            {
                Delete(slot, inPlace);
            }
            else
            {
                Clear(slot, true);
            }
        }

        public void AddWrite(ValueWrite write, bool inPlace = false, bool move = false)
        {
            if (write.Value.Kind != PolicyValueKind.Delete)
            {
                var (type, data) = PolicyEncoding.Encode(write.Value);
                Set(write.Slot, type, data, inPlace, move: move);
            }
            else if (PolicyRules.DeleteLiteralWritesDeleteInstruction)
            {
                Delete(write.Slot);
            }
            else
            {
                Clear(write.Slot, true);
            }
        }

        public void Execute(PolDocument document)
        {
            foreach (var (key, includeDeleteValues) in listClears)
            {
                foreach (var record in document.GetRecords(key).Where(r => !r.IsInstruction))
                {
                    document.RemoveValue(key, record.ValueName);
                }

                if (includeDeleteValues)
                {
                    document.RemoveDeleteValuesInstruction(key);
                }
            }

            foreach (var key in deleteValuesRemovals)
            {
                document.RemoveDeleteValuesInstruction(key);
            }

            foreach (var (key, name, type, data) in listRemovals)
            {
                var record = document.GetValue(key, name);
                if (record is not null && !record.IsInstruction && PolicyRules.StoredTypeMatches((uint)type, record.Type)
                    && string.Equals(PolData.TryGetString(record.Data.Span), data, StringComparison.Ordinal))
                {
                    document.RemoveValue(key, name);
                }
            }

            foreach (var (slot, removeInstructions) in clears.Where(c => !claimed.Contains(c.Slot.Id)))
            {
                document.RemoveValue(slot.Key, slot.Name);
                if (removeInstructions)
                {
                    // Only the delete instruction goes. A soft value stays through Disabled and Not Configured:
                    // B/Pol_LongDecimal_RequiredSoftText steps 3 and 4.
                    document.RemoveValue(slot.Key, PolicyEncoding.DeletePrefix + slot.Name);
                }
            }

            foreach (var key in deleteValuesWrites.Where(k => !PolKeySpelling.HasLeadingBackslash(k)))
            {
                document.AddDeleteValuesInstruction(key);
            }

            // A key with a leading backslash is never written: classic saves such a policy and adds no record (capture
            // K/KeyLeadingBackslash_Enable).
            foreach (var (slot, isDelete, type, data, inPlace, soft, move) in writes.Where(w => !PolKeySpelling.HasLeadingBackslash(w.Slot.Key)))
            {
                if (isDelete && inPlace)
                {
                    document.AddDeleteInstructionInPlace(slot.Key, slot.Name);
                }
                else if (isDelete)
                {
                    // The value and any older instruction leave and the new instruction goes to the end of the key:
                    // B/Pol_Amp_LabelSingle step 4 (every record becomes an instruction in element order, the instruction that
                    // was there before included) and B/Pol_Enum_ItemValueList step 5 (the instruction lands after a record that
                    // stays).
                    document.RemoveValue(slot.Key, slot.Name);
                    document.RemoveValue(slot.Key, PolicyEncoding.DeletePrefix + slot.Name);
                    document.AddDeleteInstruction(slot.Key, slot.Name);
                }
                else if (soft)
                {
                    document.RemoveValue(slot.Key, PolicyEncoding.DeletePrefix + slot.Name);
                    document.SetSoftValue(slot.Key, slot.Name, type, data);
                }
                else
                {
                    document.RemoveInstructions(slot.Key, slot.Name);
                    if (move)
                    {
                        document.RemoveValue(slot.Key, slot.Name);
                    }

                    if (inPlace)
                    {
                        document.SetValueInPlace(slot.Key, slot.Name, type, data);
                    }
                    else
                    {
                        document.SetValue(slot.Key, slot.Name, type, data);
                    }
                }
            }
        }
    }
}

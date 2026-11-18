using Bwc.Policy.Admx;

namespace Bwc.Policy.Model;

/// <summary>
/// Checks the element values of an edit against the definition. It returns typed problems and no text; the UI chooses the
/// classic message for each kind. Only an Enabled edit has anything to check.
/// </summary>
public static class PolicyEditValidator
{
    /// <summary>Validates an edit.</summary>
    /// <param name="policy">The policy definition.</param>
    /// <param name="edit">The edit.</param>
    /// <returns>The problems in element order, empty when the edit is valid.</returns>
    public static IReadOnlyList<PolicyEditProblem> Validate(PolicyDefinition policy, PolicyEdit edit)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(edit);

        var problems = new List<PolicyEditProblem>();
        if (edit.State != PolicyState.Enabled)
        {
            return problems;
        }

        var ids = new HashSet<string>(policy.Elements.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var id in edit.Elements.Keys.Where(id => !ids.Contains(id)))
        {
            problems.Add(new PolicyEditProblem(id, PolicyProblemKind.UnknownElement));
        }

        foreach (var element in policy.Elements)
        {
            edit.Elements.TryGetValue(element.Id, out var value);
            ValidateElement(element, value, problems);
        }

        return problems;
    }

    /// <summary>Tells whether an element value counts as empty: absent, an empty text, no lines, no entries.</summary>
    /// <param name="value">The value, or null.</param>
    /// <returns>True when the element is empty.</returns>
    internal static bool IsEmpty(ElementValue? value) =>
        value switch
        {
            null => true,
            TextValue text => text.Text.Length == 0,
            MultiTextValue multi => PolicyRules.NormalizeMultiTextLines(multi.Lines).Count == 0,
            ListValue list => list.Entries.Count == 0,
            _ => false,
        };

    private static void ValidateElement(PolicyElement element, ElementValue? value, List<PolicyEditProblem> problems)
    {
        if (value is not null && !ShapeMatches(element, value))
        {
            problems.Add(new PolicyEditProblem(element.Id, PolicyProblemKind.WrongValueKind));
            return;
        }

        if (IsEmpty(value))
        {
            if (element.Required && element.Kind != PolicyElementKind.Boolean)
            {
                problems.Add(new PolicyEditProblem(element.Id, PolicyProblemKind.Required));
            }

            return;
        }

        switch (element)
        {
            case DecimalElement number:
                ValidateNumber(number, ((NumberValue)value!).Number, problems);
                break;
            case TextElement text:
                ValidateText(text, ((TextValue)value!).Text, problems);
                break;
            case MultiTextElement multi:
                ValidateMultiText(multi, ((MultiTextValue)value!).Lines, problems);
                break;
            case EnumElement choice:
                if (((ChoiceValue)value!).ItemIndex < 0 || ((ChoiceValue)value).ItemIndex >= choice.Items.Count)
                {
                    problems.Add(new PolicyEditProblem(choice.Id, PolicyProblemKind.InvalidChoice));
                }

                break;
            case ListElement list:
                ValidateList(list, ((ListValue)value!).Entries, problems);
                break;
        }
    }

    private static bool ShapeMatches(PolicyElement element, ElementValue value) =>
        (element, value) switch
        {
            (BooleanElement, BooleanValue) => true,
            (DecimalElement, NumberValue) => true,
            (TextElement, TextValue) => true,
            (MultiTextElement, MultiTextValue) => true,
            (EnumElement, ChoiceValue) => true,
            (ListElement, ListValue) => true,
            _ => false,
        };

    private static void ValidateNumber(DecimalElement element, ulong number, List<PolicyEditProblem> problems)
    {
        if (element.Soft && !PolicyRules.SoftIgnoredByValidation)
        {
            return;
        }

        var min = element.MinValue ?? 0UL;
        var ceiling = element.Kind == PolicyElementKind.Decimal ? uint.MaxValue : ulong.MaxValue;
        var max = Math.Min(element.MaxValue ?? ceiling, ceiling);
        if (number < min)
        {
            problems.Add(new PolicyEditProblem(element.Id, PolicyProblemKind.BelowMinimum, min));
        }
        else if (number > max)
        {
            problems.Add(new PolicyEditProblem(element.Id, PolicyProblemKind.AboveMaximum, max));
        }
    }

    private static void ValidateText(TextElement element, string text, List<PolicyEditProblem> problems)
    {
        if (text.Contains('\0'))
        {
            problems.Add(new PolicyEditProblem(element.Id, PolicyProblemKind.InvalidCharacter));
            return;
        }

        var limit = element.MaxLength ?? PolicyRules.DefaultTextMaxLength;
        if ((ulong)text.Length > limit)
        {
            problems.Add(new PolicyEditProblem(element.Id, PolicyProblemKind.TooLong, limit));
        }
    }

    private static void ValidateMultiText(MultiTextElement element, IReadOnlyList<string> raw, List<PolicyEditProblem> problems)
    {
        if (raw.Any(l => l.Contains('\0')))
        {
            problems.Add(new PolicyEditProblem(element.Id, PolicyProblemKind.InvalidCharacter));
            return;
        }

        var lines = PolicyRules.NormalizeMultiTextLines(raw);
        if (element.MaxStrings is { } maxStrings && maxStrings > 0 && (ulong)lines.Count > maxStrings)
        {
            problems.Add(new PolicyEditProblem(element.Id, PolicyProblemKind.TooManyLines, maxStrings));
        }

        if (element.MaxLength is { } maxLength && PolicyRules.MultiTextLength(lines) > maxLength)
        {
            problems.Add(new PolicyEditProblem(element.Id, PolicyProblemKind.TooLong, maxLength));
        }
    }

    private static void ValidateList(ListElement element, IReadOnlyList<ListEntry> entries, List<PolicyEditProblem> problems)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (element.ExplicitValue && string.IsNullOrEmpty(entry.Name))
            {
                problems.Add(new PolicyEditProblem(element.Id, PolicyProblemKind.EntryNameMissing, EntryIndex: i));
                continue;
            }

            if (entry.Data.Length == 0)
            {
                problems.Add(new PolicyEditProblem(element.Id, PolicyProblemKind.EntryEmpty, EntryIndex: i));
                continue;
            }

            var name = PolicyRules.ListEntryName(element, i, entry);
            if (entry.Data.Contains('\0') || name.Contains('\0') || name.StartsWith("**", StringComparison.Ordinal))
            {
                problems.Add(new PolicyEditProblem(element.Id, PolicyProblemKind.InvalidCharacter, EntryIndex: i));
            }
            else if (!names.Add(name))
            {
                problems.Add(new PolicyEditProblem(element.Id, PolicyProblemKind.DuplicateEntry, EntryIndex: i));
            }
        }
    }
}

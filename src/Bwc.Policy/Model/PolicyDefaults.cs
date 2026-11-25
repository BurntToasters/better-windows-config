using System.Globalization;
using Bwc.Policy.Admx;

namespace Bwc.Policy.Model;

/// <summary>
/// The values the setting dialog shows for the elements of a policy right after Enabled was chosen and before anything was
/// typed (docs/failure-modes/policy-defaults.md, section F). The rules are the captures of classic: a check box follows
/// <c>defaultChecked</c>, a number its <c>defaultValue</c> (1 when there is none), a text and a combo box their default
/// text (else empty), an enum its <c>defaultItem</c> (nothing selected when there is none), a multi-text and a list
/// nothing.
/// </summary>
public static class PolicyDefaults
{
    /// <summary>The default values of the elements that have a control in the presentation of the policy.</summary>
    /// <param name="policy">The policy definition, with its presentation linked to its elements.</param>
    /// <returns>
    /// A new dictionary by element id (case-insensitive). An element with nothing selected (an enum with no default item), an
    /// element with no control and a static text have no entry. Empty when the policy has no presentation or no element.
    /// </returns>
    public static IReadOnlyDictionary<string, ElementValue> For(PolicyDefinition policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var values = new Dictionary<string, ElementValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var control in policy.Presentation?.Controls ?? [])
        {
            if (control.Element is not { } element)
            {
                continue;
            }

            switch (element)
            {
                case BooleanElement:
                    // F1: B/Pol_Bool_StringValues (checked), B/Pol_Bool_NoFalseValue (unchecked).
                    values[element.Id] = ElementValue.FromBoolean(control.DefaultChecked == true);
                    break;
                case DecimalElement number:
                    values[element.Id] = ElementValue.FromNumber(NumberDefault(number, control));
                    break;
                case TextElement:
                    // F5, F6: B/Pol_Text_Plain, B/Pol_Text_ComboBoxSuggestions (the default text), B/Pol_Text_MaxLength (empty).
                    values[element.Id] = ElementValue.FromText(control.DefaultValue ?? string.Empty);
                    break;
                case MultiTextElement:
                    // F10: B/Pol_MultiText_Basic shows one empty line, which is an empty value.
                    values[element.Id] = ElementValue.FromLines();
                    break;
                case EnumElement choice:
                    // F7, F8: B/Pol_Enum_StringItems shows no selection, B/Pol_Enum_NoSortDefaultItem selects the template item 1.
                    // A defaultItem past the last item has no capture (F9): nothing is selected, the safe choice.
                    if (control.DefaultItem is { } item && item < (ulong)choice.Items.Count)
                    {
                        values[element.Id] = ElementValue.FromChoice((int)item);
                    }

                    break;
                case ListElement:
                    // F11: every B/Pol_List_* case shows a list with no rows.
                    values[element.Id] = ElementValue.FromList();
                    break;
            }
        }

        return values;
    }

    private static ulong NumberDefault(DecimalElement element, PresentationControl control)
    {
        // F2, F3: B/Pol_Decimal_MinMax (25), B/Pol_LongDecimal_Basic (123456789012), B/Pol_Decimal_Required (no default, shows 1).
        var parsed = ulong.TryParse(control.DefaultValue, NumberStyles.None, CultureInfo.InvariantCulture, out var given) ? given : 1UL;

        // F4: no capture has a default outside the minimum and maximum. The engine keeps the number inside the range, as the
        // dialog code does, so the validator never refuses a default the dialog shows.
        var ceiling = element.Kind == PolicyElementKind.Decimal ? uint.MaxValue : ulong.MaxValue;
        var min = element.MinValue ?? 0UL;
        var max = Math.Max(min, Math.Min(element.MaxValue ?? ceiling, ceiling));
        return Math.Clamp(parsed, min, max);
    }
}

using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Bwc.Policy.Admx;
using Bwc.Policy.Model;

namespace Bwc.Policy.Tests.Golden;

/// <summary>
/// What classic's controls do with typed or set text before the dialog saves, as the group E captures show it
/// (docs/failure-modes/policy-save.md, section C). The rules change the options of an operation in place, so the
/// replay
/// hands the engine the value classic's dialog handed its writer. They are applied to group E only. A rule that
/// is not what
/// classic did cannot give the golden bytes, so the byte compare of the replay checks each of them.
/// </summary>
internal static class DialogControlRules
{
    /// <summary>The default longest text of a text box when the element states none (the rule of the engine
    /// validator).</summary>
    private const ulong DefaultTextMaxLength = 1023;

    /// <summary>Applies every rule to every option of every editPolicy operation of the case.</summary>
    /// <param name="golden">The case; its operations are changed.</param>
    internal static void Apply(GoldenCase golden)
    {
        foreach (var op in golden.Ops.Where(o => o.Kind == "editPolicy" && o.Op["options"] is JsonArray))
        {
            var ns = op.Text("policyNamespace");
            var id = op.Text("policyId");
            var policy = ns is null || id is null ? null : GoldenReplay.LoadedTemplates.FindPolicy(ns, id);
            if (policy is null)
            {
                continue;
            }

            var dropped = new List<JsonObject>();
            var all = (JsonArray)op.Op["options"]!;
            foreach (var option in all.OfType<JsonObject>())
            {
                if (option["elementId"]?.GetValue<string>() is not { } elementId
                    || policy.Elements.FirstOrDefault(e => e.Id == elementId) is not { } element)
                {
                    continue;
                }

                var typed = option["typed"] is JsonValue flag && flag.TryGetValue(out bool isTyped) && isTyped;
                switch (element)
                {
                    case DecimalElement number:
                        ApplyNumber(policy, number, option, typed, dropped);
                        break;
                    case TextElement text:
                        ApplyText(text, option, typed);
                        break;
                    case ListElement { ExplicitValue: true }:
                        ApplyExplicitRows(option);
                        break;
                }
            }

            foreach (var option in dropped)
            {
                all.Remove(option);
            }
        }
    }

    private static void ApplyNumber(PolicyDefinition policy, DecimalElement element, JsonObject option, bool typed, List<JsonObject> dropped)
    {
        if (option["text"] is not JsonValue value || !value.TryGetValue(out string? text) || text is null)
        {
            return;
        }

        // C1: typed characters that are not digits are not taken.
        if (typed)
        {
            text = new string(text.Where(char.IsAsciiDigit).ToArray());
        }

        if (text.Length > 0 && text.All(char.IsAsciiDigit))
        {
            // C2: the box returns the number held inside the minimum and maximum.
            var ceiling = element.Kind == PolicyElementKind.Decimal ? uint.MaxValue : ulong.MaxValue;
            var min = element.MinValue ?? 0UL;
            var max = Math.Min(element.MaxValue ?? ceiling, ceiling);
            var parsed = BigInteger.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
            var held = parsed > max ? max : parsed < min ? min : (ulong)parsed;
            option["text"] = held.ToString(CultureInfo.InvariantCulture);
            return;
        }

        // C2b: a minus sign and digits (WM_SETTEXT "-5" on a 10 to 500 box, "-1" on a 0 to 65535 storeAsText box) are held
        // at the minimum. Captures E/Decimal_NotNumber step 'negative' (writes 10) and E/Decimal_StoreAsText_OutOfRange
        // step 'negative' (writes "0").
        if (text.Length > 1 && text[0] == '-' && text[1..].All(char.IsAsciiDigit))
        {
            option["text"] = (element.MinValue ?? 0UL).ToString(CultureInfo.InvariantCulture);
            return;
        }

        // C3: an empty box that is not required reads as the default of the control; an empty required one is refused.
        if (text.Length == 0)
        {
            if (!element.Required && PolicyDefaults.For(policy).TryGetValue(element.Id, out var shown) && shown is NumberValue number)
            {
                option["text"] = number.Number.ToString(CultureInfo.InvariantCulture);
            }

            return;
        }

        // C3: text with a character that is not a digit is not taken: the box keeps the value it showed (the stored
        // number of an
        // Enabled policy, else the default), so the option is dropped and the replay supplies that value.
        dropped.Add(option);
    }

    private static void ApplyText(TextElement element, JsonObject option, bool typed)
    {
        // C4: a text box cuts typed text at the longest length; set text is not cut.
        if (typed && option["text"] is JsonValue value && value.TryGetValue(out string? text) && text is not null)
        {
            var limit = element.MaxLength ?? DefaultTextMaxLength;
            if ((ulong)text.Length > limit)
            {
                option["text"] = text[..(int)limit];
            }
        }
    }

    private static void ApplyExplicitRows(JsonObject option)
    {
        // C5: a row with only a name has an empty value.
        foreach (var row in (option["rows"] as JsonArray ?? new JsonArray()).OfType<JsonArray>().Where(r => r.Count == 1))
        {
            row.Add("");
        }
    }
}

using System.Globalization;
using Bwc.Oracle.Capture.Gpedit.Driver;

namespace Bwc.Oracle.Capture.Gpedit.Matrix;

// The values group B types into each element kind. Every element has an ordered list of variants; step k of a policy uses variant k of every
// element (the last variant when an element has fewer). Values are plain ASCII, inside the template's limits unless a case says otherwise.
internal static class OptionChoices
{
    internal sealed record Variant(string Tag, OptionSpec Spec);

    internal static string ExpectKind(ElementInfo element) => element.Control switch
    {
        "listBox" => "list",
        "multiTextBox" => "multiText",
        _ => element.Control,
    };

    // The spec that finds the element's control: by label when the label is unique in the policy, by position otherwise.
    internal static OptionSpec Base(PolicyInfo policy, ElementInfo element)
    {
        bool uniqueLabel = element.Label is not null
            && policy.Elements.Count(e => string.Equals(e.Label, element.Label, StringComparison.Ordinal)) == 1;
        return new OptionSpec
        {
            ElementId = element.Id,
            Label = uniqueLabel ? element.Label : null,
            Index = uniqueLabel ? null : element.ControlIndex,
            ExpectKind = ExpectKind(element),
        };
    }

    internal static List<Variant> Variants(PolicyInfo policy, ElementInfo e)
    {
        OptionSpec b = Base(policy, e);
        switch (e.Kind)
        {
            case "boolean":
                return new List<Variant> { new("checked", b with { Checked = true }), new("unchecked", b with { Checked = false }) };

            case "decimal":
                return new List<Variant> { new("value", b with { Text = Number(e, 77, 7).ToString(CultureInfo.InvariantCulture) }) };

            case "longDecimal":
                long value = e.Max is { } max ? Math.Min(4000000000L, max) : 4000000000L;
                if (e.Min is { } min && value < min)
                {
                    value = min;
                }

                return new List<Variant> { new("value", b with { Text = value.ToString(CultureInfo.InvariantCulture) }) };

            case "text":
                string text = e.MaxLength is { } limit ? new string('a', Math.Min(3, limit)) : e.Expandable ? @"%SystemRoot%\bwc-alt" : "bwc-alt";
                var variants = new List<Variant> { new("text", b with { Text = text }) };
                if (e.Control == "comboBox" && e.Suggestions.Count > 0)
                {
                    variants.Add(new Variant("suggestion", b with { Item = e.Suggestions[^1] }));
                }

                return variants;

            case "multiText":
                return new List<Variant> { new("lines", b with { Lines = new List<string> { "alpha", "beta" } }) };

            case "enum":
                return e.EnumItems.Select((item, i) => new Variant("item" + i, b with { Item = item })).ToList();

            case "list":
                return new List<Variant>
                {
                    new("rows2", b with { ClearRows = true, Rows = Rows(e, 2) }),
                    new("rows1", b with { ClearRows = true, Rows = Rows(e, 1) }),
                    new("rows0", b with { ClearRows = true }),
                };

            default:
                throw new InvalidOperationException($"element kind '{e.Kind}' of {policy.Name} has no variants");
        }
    }

    internal static List<List<string>> Rows(ElementInfo e, int count)
    {
        var rows = new List<List<string>>();
        for (int i = 1; i <= count; i++)
        {
            string name = e.Expandable ? @"%SystemRoot%\bwc-" + i : "bwc-" + i;
            rows.Add(e.ExplicitValue ? new List<string> { "name" + i, "value" + i } : new List<string> { name });
        }

        return rows;
    }

    // A value inside the range that is not the default: a third of the way up when both limits exist.
    private static long Number(ElementInfo e, long fallback, long offset)
    {
        long alt = e.Min is { } min && e.Max is { } max ? min + ((max - min) / 3)
            : e.Min is { } onlyMin ? onlyMin + offset
            : e.Max is { } onlyMax ? Math.Max(0, onlyMax - offset)
            : fallback;
        if (long.TryParse(e.DefaultText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long def) && def == alt)
        {
            alt++;
        }

        return alt;
    }

    // The option lists for each step after the defaults: step k takes variant k of every element. typeRanges types the value of a number box
    // that has a range: such a step runs on a policy that is already Enabled, and classic does not notice a number box set with WM_SETTEXT then
    // (failure mode 21 of the matrix document). Group B passes it. Group G reuses these steps in the step that changes the state, which was
    // never wrong, so it does not.
    internal static List<(string Tag, List<OptionSpec> Specs)> Steps(PolicyInfo policy, bool typeRanges = false)
    {
        List<List<Variant>> all = policy.Elements.Select(e => Variants(policy, e)).ToList();
        int count = all.Count == 0 ? 0 : all.Max(v => v.Count);
        var steps = new List<(string, List<OptionSpec>)>();
        for (int k = 0; k < count; k++)
        {
            var specs = new List<OptionSpec>();
            var tags = new List<string>();
            foreach (List<Variant> variants in all)
            {
                Variant pick = variants[Math.Min(k, variants.Count - 1)];
                bool keys = typeRanges && policy.Elements.FirstOrDefault(e => e.Id == pick.Spec.ElementId) is { } element && UnnoticedEdit.NeedsKeys(element);
                specs.Add(keys ? pick.Spec with { Typed = true } : pick.Spec);
                tags.Add(pick.Tag);
            }

            steps.Add((string.Join("+", tags.Distinct()), specs));
        }

        return steps;
    }
}

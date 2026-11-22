using System.Globalization;
using System.Text.Json.Nodes;
using Bwc.Policy.Admx;
using Bwc.Policy.Model;

namespace Bwc.Policy.Tests.Golden;

/// <summary>
/// Turns the display values of a structured op (an enum by item label, a number as text, a list as rows, a check box
/// as its state, a multi-text as lines) into element values. A value that classic's dialog would refuse before the
/// engine sees it (a number that is not digits) is reported as a refusal by the dialog, not as an error of the
/// harness. Anything the mapping cannot represent fails the row with a message. The failure modes are in
/// docs/failure-modes/policy-defaults.md, section O.
/// </summary>
internal static class GoldenOptions
{
    private static readonly string[] KnownMembers =
    [
        "label", "expectKind", "elementId", "index", "typed", "text", "checked", "item", "itemIndex", "lines", "rows", "clearRows",
    ];

    internal sealed record Mapped(IReadOnlyDictionary<string, ElementValue> Values, string? DialogRefusal);

    /// <summary>Fails when options come with a state that disables the options (O17).</summary>
    /// <param name="caseId">The case.</param>
    /// <param name="opIndex">The operation.</param>
    /// <param name="state">The state of the operation, or null.</param>
    /// <param name="options">The options of the operation, or null.</param>
    internal static void RequireEnabledForOptions(string caseId, int opIndex, string? state, JsonArray? options)
    {
        if (options is { Count: > 0 } && state is "Disabled" or "Not Configured")
        {
            throw Unmappable(caseId, opIndex, $"options come with the state '{state}', where the dialog disables every option");
        }
    }

    /// <summary>Maps the options of one operation.</summary>
    /// <param name="caseId">The case, for messages.</param>
    /// <param name="opIndex">The operation, for messages.</param>
    /// <param name="policy">The policy with its presentation linked to its elements.</param>
    /// <param name="options">The <c>options</c> member of the operation, or null.</param>
    /// <param name="stored">The stored element values the dialog shows when the policy is already Enabled; empty
    /// otherwise. A list that is not cleared adds its rows after the rows of these values.</param>
    /// <returns>The values by element id, and the first refusal of the dialog, if any.</returns>
    internal static Mapped Map(
        string caseId, int opIndex, PolicyDefinition policy, JsonArray? options, IReadOnlyDictionary<string, ElementValue>? stored = null)
    {
        var values = new Dictionary<string, ElementValue>(StringComparer.OrdinalIgnoreCase);
        string? refusal = null;
        foreach (var node in options ?? new JsonArray())
        {
            var option = node as JsonObject ?? throw Unmappable(caseId, opIndex, "an option is not an object");
            var unknown = option.Select(p => p.Key).Where(k => !KnownMembers.Contains(k, StringComparer.Ordinal)).ToArray();
            if (unknown.Length > 0)
            {
                throw Unmappable(caseId, opIndex, $"the option has the unknown member '{unknown[0]}'");
            }

            var id = Str(option, "elementId") ?? throw Unmappable(caseId, opIndex, "an option has no elementId");
            var element = policy.Elements.FirstOrDefault(e => e.Id == id)
                ?? throw Unmappable(caseId, opIndex, $"policy {policy.Name} has no element '{id}'");
            if (values.ContainsKey(id))
            {
                throw Unmappable(caseId, opIndex, $"two options name the element '{id}'");
            }

            var control = policy.Presentation?.Controls.FirstOrDefault(c => c.Element?.Id == id)
                ?? throw Unmappable(caseId, opIndex, $"element '{id}' has no control in the presentation of {policy.Name}");
            CheckKind(caseId, opIndex, option, element, control);
            CheckMembers(caseId, opIndex, option, element, control);

            switch (element)
            {
                case BooleanElement:
                    values[id] = ElementValue.FromBoolean(Bool(option, "checked") ?? throw Unmappable(caseId, opIndex, $"option '{id}' has no boolean checked"));
                    break;
                case DecimalElement:
                    var digits = Str(option, "text") ?? throw Unmappable(caseId, opIndex, $"option '{id}' has no text");
                    if (ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                    {
                        values[id] = ElementValue.FromNumber(number);
                    }
                    else
                    {
                        refusal ??= $"'{digits}' is not a number (option '{id}')";
                    }

                    break;
                case TextElement:
                    var text = Str(option, "text") ?? Str(option, "item") ?? throw Unmappable(caseId, opIndex, $"option '{id}' has neither text nor item");
                    if (Str(option, "item") is { } suggestion && !control.Suggestions.Contains(suggestion, StringComparer.Ordinal))
                    {
                        throw Unmappable(caseId, opIndex, $"option '{id}': '{suggestion}' is not a suggestion of the combo box");
                    }

                    values[id] = ElementValue.FromText(text);
                    break;
                case MultiTextElement:
                    values[id] = ElementValue.FromLines(Lines(option, caseId, opIndex, id));
                    break;
                case EnumElement choice:
                    var item = Str(option, "item") ?? throw Unmappable(caseId, opIndex, $"option '{id}' has no item");
                    var at = choice.Items.Select((i, n) => (i, n)).Where(p => p.i.DisplayName == item).Select(p => p.n).ToArray();
                    if (at.Length == 0)
                    {
                        throw Unmappable(caseId, opIndex, $"option '{id}': '{item}' is not an item label of the enum ({string.Join(", ", choice.Items.Select(i => "'" + i.DisplayName + "'"))})");
                    }

                    if (at.Length > 1)
                    {
                        throw Unmappable(caseId, opIndex, $"option '{id}': the label '{item}' names {at.Length} items, so the choice is ambiguous");
                    }

                    values[id] = ElementValue.FromChoice(at[0]);
                    break;
                case ListElement list:
                    values[id] = ListOf(option, list, stored, caseId, opIndex, id);
                    break;
                default:
                    throw Unmappable(caseId, opIndex, $"option '{id}' has the unsupported element kind {element.Kind}");
            }
        }

        return new Mapped(values, refusal);
    }

    private static void CheckKind(string caseId, int opIndex, JsonObject option, PolicyElement element, PresentationControl control)
    {
        var expected = Str(option, "expectKind");
        if (expected is null)
        {
            return;
        }

        string[] allowed = element switch
        {
            BooleanElement => ["checkBox"],
            DecimalElement { Kind: PolicyElementKind.LongDecimal } => ["longDecimalTextBox", "spinner"],
            DecimalElement => ["decimalTextBox", "spinner"],
            TextElement => [control.Kind == PresentationControlKind.ComboBox ? "comboBox" : "textBox"],
            MultiTextElement => ["multiText"],
            EnumElement => ["dropdownList"],
            ListElement => ["list"],
            _ => [],
        };
        if (!allowed.Contains(expected, StringComparer.Ordinal))
        {
            throw Unmappable(caseId, opIndex, $"option '{element.Id}' says expectKind '{expected}' but the element is shown as '{string.Join("' or '", allowed)}'");
        }
    }

    private static void CheckMembers(string caseId, int opIndex, JsonObject option, PolicyElement element, PresentationControl control)
    {
        var present = new[] { "text", "checked", "item", "itemIndex", "lines" }.Where(m => option.ContainsKey(m)).ToList();
        if (option.ContainsKey("rows") || option.ContainsKey("clearRows"))
        {
            present.Add("rows");
        }

        if (present.Count != 1)
        {
            throw Unmappable(caseId, opIndex, $"option '{element.Id}' needs exactly one value member and has {present.Count} ({string.Join(", ", present)})");
        }

        if (present[0] == "itemIndex")
        {
            throw Unmappable(caseId, opIndex, $"option '{element.Id}' names itemIndex, which is not supported: the position depends on the sort, name the item label");
        }

        string[] allowed = element switch
        {
            BooleanElement => ["checked"],
            DecimalElement => ["text"],
            TextElement => control.Kind == PresentationControlKind.ComboBox ? ["text", "item"] : ["text"],
            MultiTextElement => ["lines"],
            EnumElement => ["item"],
            ListElement => ["rows"],
            _ => [],
        };
        if (!allowed.Contains(present[0], StringComparer.Ordinal))
        {
            throw Unmappable(caseId, opIndex, $"option '{element.Id}' holds '{present[0]}' but its element takes {string.Join(" or ", allowed)}");
        }
    }

    private static string[] Lines(JsonObject option, string caseId, int opIndex, string id)
    {
        if (option["lines"] is not JsonArray array)
        {
            throw Unmappable(caseId, opIndex, $"option '{id}': lines is not an array");
        }

        return array.Select(l => l is JsonValue v && v.TryGetValue(out string? s) ? s : throw Unmappable(caseId, opIndex, $"option '{id}': a line is not a string")).ToArray();
    }

    private static ElementValue ListOf(
        JsonObject option, ListElement list, IReadOnlyDictionary<string, ElementValue>? stored, string caseId, int opIndex, string id)
    {
        var storedEntries = stored is not null && stored.TryGetValue(id, out var kept) && kept is ListValue storedList ? storedList.Entries : [];
        var rows = new List<ListEntry>();
        if (Bool(option, "clearRows") != true)
        {
            rows.AddRange(storedEntries.Select(e => list.ExplicitValue ? e : ListEntry.Of(e.Data)));
        }

        if (option["rows"] is not null && option["rows"] is not JsonArray)
        {
            throw Unmappable(caseId, opIndex, $"option '{id}': rows is not an array");
        }

        foreach (var row in option["rows"] as JsonArray ?? new JsonArray())
        {
            var cells = row is JsonArray array
                ? array.Select(c => c is JsonValue v && v.TryGetValue(out string? s) ? s : throw Unmappable(caseId, opIndex, $"option '{id}': a cell is not a string")).ToArray()
                : throw Unmappable(caseId, opIndex, $"option '{id}': a row is not an array");
            if (cells.Length != (list.ExplicitValue ? 2 : 1))
            {
                throw Unmappable(
                    caseId,
                    opIndex,
                    $"option '{id}': a row of {(list.ExplicitValue ? "an explicit-value list needs a name and a value" : "a list needs one cell")} and has {cells.Length}");
            }

            rows.Add(list.ExplicitValue ? new ListEntry(cells[0], cells[1]) : ListEntry.Of(cells[0]));
        }

        var removed = storedEntries
            .Where(old => !rows.Any(r => list.ExplicitValue
                ? string.Equals(r.Name, old.Name, StringComparison.OrdinalIgnoreCase)
                : string.Equals(r.Data, old.Data, StringComparison.Ordinal)))
            .ToArray();
        return ElementValue.FromListWithRemoved(rows, removed);
    }

    private static string? Str(JsonObject option, string name) => option[name] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static bool? Bool(JsonObject option, string name) => option[name] is JsonValue value && value.TryGetValue(out bool flag) ? flag : null;

    private static AssertFailedException Unmappable(string caseId, int opIndex, string why) =>
        new($"{caseId} operation {opIndex}: the option cannot be turned into an element value: {why}.");
}

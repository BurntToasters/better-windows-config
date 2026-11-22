using System.Globalization;
using System.Text.Json.Nodes;
using Bwc.Policy.Admx;
using Bwc.Policy.Model;

namespace Bwc.Policy.Tests.Golden;

/// <summary>
/// Turns the display values of a structured op (an enum by item label, a number as text, a list as rows)
/// into element values. A value that classic's dialog would refuse before the engine sees it (a number
/// that is not digits) is reported as a refusal by the dialog, not as an error of the harness.
/// </summary>
internal static class GoldenOptions
{
    internal sealed record Mapped(IReadOnlyDictionary<string, ElementValue> Values, string? DialogRefusal);

    internal static Mapped Map(string caseId, int opIndex, PolicyDefinition policy, JsonArray? options)
    {
        var values = new Dictionary<string, ElementValue>(StringComparer.OrdinalIgnoreCase);
        string? refusal = null;
        foreach (var node in options ?? new JsonArray())
        {
            var option = (JsonObject)node!;
            var id = option["elementId"]?.GetValue<string>()
                ?? throw Unmappable(caseId, opIndex, "an option has no elementId");
            var element = policy.Elements.FirstOrDefault(e => e.Id == id)
                ?? throw Unmappable(caseId, opIndex, $"policy {policy.Name} has no element '{id}'");
            switch (element)
            {
                case BooleanElement:
                    values[id] = ElementValue.FromBoolean(option["checked"]?.GetValue<bool>()
                        ?? throw Unmappable(caseId, opIndex, $"option '{id}' has no checked value"));
                    break;
                case DecimalElement:
                    var digits = Text(option) ?? throw Unmappable(caseId, opIndex, $"option '{id}' has no text");
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
                    values[id] = ElementValue.FromText(Text(option) ?? option["item"]?.GetValue<string>() ?? string.Empty);
                    break;
                case MultiTextElement:
                    values[id] = ElementValue.FromLines((option["lines"] as JsonArray ?? new JsonArray()).Select(l => l!.GetValue<string>()).ToArray());
                    break;
                case EnumElement choice:
                    var item = option["item"]?.GetValue<string>() ?? throw Unmappable(caseId, opIndex, $"option '{id}' has no item");
                    var at = choice.Items.ToList().FindIndex(i => i.DisplayName == item);
                    values[id] = at >= 0
                        ? ElementValue.FromChoice(at)
                        : throw Unmappable(caseId, opIndex, $"option '{id}': '{item}' is not an item label of the enum");
                    break;
                case ListElement list:
                    values[id] = ElementValue.FromList(Rows(option, list, caseId, opIndex, id));
                    break;
                default:
                    throw Unmappable(caseId, opIndex, $"option '{id}' has the unsupported element kind {element.Kind}");
            }
        }

        return new Mapped(values, refusal);
    }

    private static ListEntry[] Rows(JsonObject option, ListElement list, string caseId, int opIndex, string id)
    {
        var entries = new List<ListEntry>();
        foreach (var row in option["rows"] as JsonArray ?? new JsonArray())
        {
            var cells = ((JsonArray)row!).Select(c => c!.GetValue<string>()).ToArray();
            if (list.ExplicitValue)
            {
                if (cells.Length != 2)
                {
                    throw Unmappable(caseId, opIndex, $"option '{id}': a row of an explicit list needs a name and a value");
                }

                entries.Add(new ListEntry(cells[0], cells[1]));
            }
            else
            {
                entries.Add(ListEntry.Of(cells[0]));
            }
        }

        return entries.ToArray();
    }

    private static string? Text(JsonObject option) => option["text"] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static AssertFailedException Unmappable(string caseId, int opIndex, string why) =>
        new($"{caseId} operation {opIndex}: the option cannot be turned into an element value: {why}.");
}

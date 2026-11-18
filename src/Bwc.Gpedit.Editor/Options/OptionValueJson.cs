using System.Text;
using System.Text.Json;
using Bwc.Policy.Model;

namespace Bwc.Gpedit.Editor.Options;

/// <summary>
/// The values of the Options pane as compact JSON, one string per element. The pane compares two of these to tell whether
/// anything changed since it was loaded or applied (so a late TextChanged of a programmatic set cannot make the
/// dialog dirty),
/// and the E2E flavor publishes them as the readout of the pane (docs/failure-modes/gpedit-options.md).
/// </summary>
internal static class OptionValueJson
{
    /// <summary>One value, for example <c>{"k":"num","v":"25"}</c>.</summary>
    internal static string Serialize(ElementValue value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Write(writer, value);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>All values in element order, as one string that two calls can compare.</summary>
    internal static string Serialize(IEnumerable<KeyValuePair<string, ElementValue>> values) =>
        string.Join("\n", values.Select(pair => pair.Key + "=" + Serialize(pair.Value)));

    /// <summary>A list of strings as a JSON array.</summary>
    internal static string SerializeItems(IEnumerable<string> items) => JsonSerializer.Serialize(items);

    private static void Write(Utf8JsonWriter writer, ElementValue value)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case BooleanValue boolean:
                writer.WriteString("k", "bool");
                writer.WriteBoolean("v", boolean.Checked);
                break;
            case NumberValue number:
                writer.WriteString("k", "num");
                writer.WriteString("v", number.Number.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
            case TextValue text:
                writer.WriteString("k", "text");
                writer.WriteString("v", text.Text);
                break;
            case MultiTextValue multi:
                writer.WriteString("k", "lines");
                writer.WriteStartArray("v");
                foreach (string line in multi.Lines)
                {
                    writer.WriteStringValue(line);
                }

                writer.WriteEndArray();
                break;
            case ChoiceValue choice:
                writer.WriteString("k", "choice");
                writer.WriteNumber("v", choice.ItemIndex);
                break;
            case ListValue list:
                writer.WriteString("k", "list");
                WriteEntries(writer, "entries", list.Entries);
                WriteEntries(writer, "removed", list.Removed);
                break;
            default:
                writer.WriteString("k", "unknown");
                break;
        }

        writer.WriteEndObject();
    }

    private static void WriteEntries(Utf8JsonWriter writer, string name, IReadOnlyList<ListEntry> entries)
    {
        writer.WriteStartArray(name);
        foreach (ListEntry entry in entries)
        {
            writer.WriteStartObject();
            if (entry.Name is null)
            {
                writer.WriteNull("n");
            }
            else
            {
                writer.WriteString("n", entry.Name);
            }

            writer.WriteString("d", entry.Data);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}

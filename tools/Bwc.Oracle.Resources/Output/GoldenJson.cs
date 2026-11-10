using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bwc.Oracle.Resources.Model;

namespace Bwc.Oracle.Resources.Output;

internal static class GoldenJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Keep "&File" and "\t" readable instead of & / escaped Unicode. Control characters are still escaped.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// Two-space indent, LF newlines, trailing newline. No timestamps, so output is byte-identical across runs.
    public static string Serialize(GoldenFile file)
    {
        string json = JsonSerializer.Serialize(file, Options);
        // The serializer writes Environment.NewLine. Raw CR never appears inside strings (they are escaped).
        return json.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }
}

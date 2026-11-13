using System.Text.Json;

namespace Bwc.Update;

/// <summary>
/// JSON reading for signed documents. Rejects a byte-order mark, comments, duplicate property names
/// at any depth, trailing content, and nesting deeper than 16 levels. Duplicate names are refused
/// because two readers could pick different values from the same signed bytes.
/// </summary>
internal static class StrictJson
{
    private const int MaxDepth = 16;

    public static Outcome<JsonDocument> Parse(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Outcome<JsonDocument>.Fail(UpdateFailure.ManifestInvalid, "document starts with a UTF-8 byte-order mark");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = MaxDepth,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
        }
        catch (JsonException)
        {
            return Outcome<JsonDocument>.Fail(UpdateFailure.ManifestInvalid, "document is not strict JSON");
        }

        if (HasDuplicateName(document.RootElement))
        {
            document.Dispose();
            return Outcome<JsonDocument>.Fail(UpdateFailure.ManifestInvalid, "document repeats a property name");
        }

        return Outcome<JsonDocument>.Success(document);
    }

    private static bool HasDuplicateName(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name) || HasDuplicateName(property.Value))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (HasDuplicateName(item))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>Reads a canonical base64 string (no whitespace, no alternate padding). Returns false otherwise.</summary>
    public static bool TryDecodeCanonicalBase64(string text, out byte[] bytes)
    {
        bytes = [];
        if (text.Length == 0 || text.Length % 4 != 0)
        {
            return false;
        }

        var buffer = new byte[text.Length / 4 * 3];
        if (!Convert.TryFromBase64String(text, buffer, out var written))
        {
            return false;
        }

        bytes = buffer[..written];
        return string.Equals(Convert.ToBase64String(bytes), text, StringComparison.Ordinal);
    }
}

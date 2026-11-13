namespace Bwc.Update;

/// <summary>
/// Reads the signed trusted comment: space or tab separated key:value fields, such as
/// "timestamp:1794102823	file:installer.exe	version:0.2.0	arch:win-x64". The comment is signed,
/// so its fields can be trusted once the signature verifies. Keys must be unique.
/// </summary>
public static class TrustedComment
{
    private const int MaxLength = 1024;

    public static bool TryParseFields(string comment, out Dictionary<string, string> fields, out string reason)
    {
        fields = new Dictionary<string, string>(StringComparer.Ordinal);
        reason = string.Empty;
        if (comment.Length > MaxLength)
        {
            reason = "trusted comment is too long";
            return false;
        }

        foreach (var token in comment.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = token.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || colon == token.Length - 1)
            {
                reason = "trusted comment field is not key:value";
                return false;
            }

            var key = token[..colon];
            var value = token[(colon + 1)..];
            if (!key.All(char.IsAsciiLetterLower))
            {
                reason = "trusted comment key is not lowercase letters";
                return false;
            }

            if (!fields.TryAdd(key, value))
            {
                reason = $"trusted comment repeats the key '{key}'";
                return false;
            }
        }

        return true;
    }

    /// <summary>True for one file name with no folders, drive letters, control characters, or dot names.</summary>
    public static bool IsPlainFileName(string value)
    {
        if (value.Length is 0 or > 255 || value is "." or "..")
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c < 0x20 || c is '/' or '\\' or ':' or '"' or '<' or '>' or '|' or '*' or '?')
            {
                return false;
            }
        }

        return true;
    }
}

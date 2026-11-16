using System.Text;
using System.Text.RegularExpressions;

namespace Bwc.Hub.Migration;

/// <summary>
/// Reads the requestedExecutionLevel values an executable's embedded manifest declares, from its bytes alone: the file is
/// never loaded or run. It is the C# form of readRequestedLevels in scripts/flavor-metadata.js (the same two encodings and
/// the same span), so the publish audit and the Hub agree on what a manifest asks for.
/// </summary>
internal static partial class UninstallerManifest
{
    private const string ElementName = "requestedExecutionLevel";
    private const string Unreadable = "(unreadable level)";

    [GeneratedRegex("""\blevel\s*=\s*["']([A-Za-z]+)["']""")]
    private static partial Regex LevelAttribute();

    /// <summary>The distinct level values, sorted. Empty when the file has no requestedExecutionLevel element.</summary>
    public static IReadOnlyList<string> RequestedLevels(ReadOnlySpan<byte> bytes)
    {
        var levels = new SortedSet<string>(StringComparer.Ordinal);
        Scan(bytes, Encoding.Latin1, unit: 1, span: 512, levels);
        Scan(bytes, Encoding.Unicode, unit: 2, span: 1024, levels);
        return [.. levels];
    }

    /// <summary>
    /// True only when the manifest declares asInvoker and nothing else. No manifest, a second element with another level,
    /// or a level that cannot be read all fail closed.
    /// </summary>
    public static bool RequestsOnlyAsInvoker(ReadOnlySpan<byte> bytes)
    {
        IReadOnlyList<string> levels = RequestedLevels(bytes);
        return levels.Count == 1 && string.Equals(levels[0], "asInvoker", StringComparison.Ordinal);
    }

    private static void Scan(ReadOnlySpan<byte> bytes, Encoding encoding, int unit, int span, SortedSet<string> levels)
    {
        byte[] needle = encoding.GetBytes(ElementName);
        int from = 0;
        while (from <= bytes.Length - needle.Length)
        {
            int relative = bytes[from..].IndexOf(needle);
            if (relative < 0)
            {
                return;
            }

            int at = from + relative;
            if (at >= unit && encoding.GetString(bytes.Slice(at - unit, unit)) == "<")
            {
                int length = Math.Min(bytes.Length - at, span);
                string text = encoding.GetString(bytes.Slice(at, length));
                int close = text.IndexOf('>', StringComparison.Ordinal);
                string element = close < 0 ? text : text[..close];
                Match match = LevelAttribute().Match(element);
                _ = levels.Add(match.Success ? match.Groups[1].Value : Unreadable);
            }

            from = at + 1;
        }
    }
}

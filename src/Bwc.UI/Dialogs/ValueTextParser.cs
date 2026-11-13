namespace Bwc.UI.Dialogs;

/// <summary>Text helpers shared by the multi-string editor.</summary>
internal static class ValueTextParser
{
    internal static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<string>();
        }

        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }
}

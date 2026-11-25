using Bwc.Gpo.Pol;

namespace Bwc.Policy.Model;

/// <summary>
/// How classic treats a key that starts or ends with a backslash (captures K/KeyLeadingBackslash_* and
/// K/KeyTrailingBackslash_*).
/// </summary>
internal static class PolKeySpelling
{
    public static bool HasLeadingBackslash(string key) => key.StartsWith('\\');

    public static bool HasTrailingBackslash(PolDocument document) => document.GetKeys().Any(k => k.EndsWith('\\'));

    /// <summary>A copy of the document with every trailing key backslash removed. The document itself is not changed.</summary>
    public static PolDocument TrimmedCopy(PolDocument document)
    {
        var copy = PolDocument.Parse(document.ToBytes());
        copy.TrimTrailingKeyBackslashes();
        return copy;
    }
}

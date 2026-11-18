using Microsoft.Windows.ApplicationModel.Resources;

namespace Bwc.Gpedit;

/// <summary>Strings from Bwc.Gpedit/Strings/en-US/Resources.resw and Tree.resw.</summary>
public static class GpeditStrings
{
    // The default loader only sees the head's own map, so the library map must be named explicitly.
    public const string ResourceMap = "Bwc.Gpedit/Resources";

    // Each resw file of a library is a map of its own, named after the file (as Bwc.UI/Console is Console.resw).
    internal const string TreeResourceMap = "Bwc.Gpedit/Tree";

    public static string Get(string key)
    {
        var loader = new ResourceLoader(ResourceLoader.GetDefaultResourceFilePath(), ResourceMap);
        return loader.GetString(key);
    }

    /// <summary>
    /// A string of Tree.resw (the tree, list, status and placeholder texts). A missing or empty entry gives the key
    /// name, so a blank row or label shows up in review and in the tests instead of staying invisible.
    /// </summary>
    internal static string Tree(string key)
    {
        try
        {
            var loader = new ResourceLoader(ResourceLoader.GetDefaultResourceFilePath(), TreeResourceMap);
            string value = loader.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Bwc.Core.Log.Warn($"Editor string '{key}' could not be read: {ex.Message}");
            return key;
        }
    }
}

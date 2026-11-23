using Microsoft.Windows.ApplicationModel.Resources;

namespace Bwc.Gpedit;

/// <summary>
/// Strings from Bwc.Gpedit/Strings/en-US/Resources.resw, Tree.resw, Commands.resw, Filter.resw and Templates.resw.
/// </summary>
public static class GpeditStrings
{
    // The default loader only sees the head's own map, so the library map must be named explicitly.
    public const string ResourceMap = "Bwc.Gpedit/Resources";

    // Each resw file of a library is a map of its own, named after the file (as Bwc.UI/Console is Console.resw).
    internal const string TreeResourceMap = "Bwc.Gpedit/Tree";
    internal const string CommandsResourceMap = "Bwc.Gpedit/Commands";
    internal const string FilterResourceMap = "Bwc.Gpedit/Filter";
    internal const string TemplatesResourceMap = "Bwc.Gpedit/Templates";

    public static string Get(string key)
    {
        var loader = new ResourceLoader(ResourceLoader.GetDefaultResourceFilePath(), ResourceMap);
        return loader.GetString(key);
    }

    /// <summary>A string of Commands.resw (menu entries, the About box). A missing entry gives the key name.</summary>
    internal static string Commands(string key) => Lookup(CommandsResourceMap, key);

    /// <summary>A string of Filter.resw (the Filter Options dialog). A missing entry gives the key name.</summary>
    internal static string Filter(string key) => Lookup(FilterResourceMap, key);

    /// <summary>
    /// A string of Templates.resw (the Add/Remove Templates dialog and the load-error notice). A missing entry gives the
    /// key name.
    /// </summary>
    internal static string Templates(string key) => Lookup(TemplatesResourceMap, key);

    /// <summary>
    /// A string of Tree.resw (the tree, list, status and placeholder texts). A missing or empty entry gives the key
    /// name, so a blank row or label shows up in review and in the tests instead of staying invisible.
    /// </summary>
    internal static string Tree(string key) => Lookup(TreeResourceMap, key);

    private static string Lookup(string map, string key)
    {
        try
        {
            var loader = new ResourceLoader(ResourceLoader.GetDefaultResourceFilePath(), map);
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

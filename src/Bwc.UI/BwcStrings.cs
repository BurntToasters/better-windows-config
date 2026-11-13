using Microsoft.Windows.ApplicationModel.Resources;

namespace Bwc.UI;

/// <summary>Strings from this library's Strings/en-US/Resources.resw.</summary>
public static class BwcStrings
{
    // The default loader only sees the head's own map, so the library map must be named explicitly.
    private const string ResourceMap = "Bwc.UI/Resources";

    public static string ProductName => Get("BwcProductName");

    public static string Get(string key)
    {
        var loader = new ResourceLoader(ResourceLoader.GetDefaultResourceFilePath(), ResourceMap);
        return loader.GetString(key);
    }
}

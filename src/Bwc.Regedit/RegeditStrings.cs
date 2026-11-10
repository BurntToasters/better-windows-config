using Microsoft.Windows.ApplicationModel.Resources;

namespace Bwc.Regedit;

/// <summary>Strings from Bwc.Regedit/Strings/en-US/Resources.resw.</summary>
public static class RegeditStrings
{
    // The default loader only sees the head's own map, so the library map must be named explicitly.
    public const string ResourceMap = "Bwc.Regedit/Resources";

    public static string Get(string key)
    {
        var loader = new ResourceLoader(ResourceLoader.GetDefaultResourceFilePath(), ResourceMap);
        return loader.GetString(key);
    }
}

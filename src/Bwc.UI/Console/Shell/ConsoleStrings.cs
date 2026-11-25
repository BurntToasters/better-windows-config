using Microsoft.Windows.ApplicationModel.Resources;

namespace Bwc.UI.Console.Shell;

/// <summary>Strings from this library's Strings/en-US/Console.resw (resource map Bwc.UI/Console).</summary>
public static class ConsoleStrings
{
    // The default loader only sees the head's own map, so the library map must be named explicitly.
    private const string ResourceMap = "Bwc.UI/Console";

    /// <summary>
    /// The string for a key. A missing or empty entry gives the key name, so a blank menu item or toolbar name
    /// shows up in review and in the tests instead of staying invisible.
    /// </summary>
    public static string Get(string key)
    {
        try
        {
            var loader = new ResourceLoader(ResourceLoader.GetDefaultResourceFilePath(), ResourceMap);
            string value = loader.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UiDiagnostics.Warn?.Invoke($"Console string '{key}' could not be read: {ex.Message}");
            return key;
        }
    }
}

using Microsoft.Windows.ApplicationModel.Resources;

namespace Bwc.Gpedit.Editor.Options;

/// <summary>Strings from this library's Strings/en-US/Options.resw (resource map Bwc.Gpedit.Editor/Options).</summary>
internal static class OptionsStrings
{
    // The default loader only sees the head's own map, so the library map must be named explicitly.
    private const string ResourceMap = "Bwc.Gpedit.Editor/Options";

    /// <summary>
    /// The string for a key. A missing or empty entry gives the key name, so a blank message shows up in review and in the
    /// tests instead of staying invisible.
    /// </summary>
    internal static string Get(string key)
    {
        try
        {
            var loader = new ResourceLoader(ResourceLoader.GetDefaultResourceFilePath(), ResourceMap);
            string value = loader.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Bwc.UI.UiDiagnostics.Warn?.Invoke($"Options string '{key}' could not be read: {ex.Message}");
            return key;
        }
    }

    /// <summary>A string with its placeholders filled in (culture neutral: the numbers are plain).</summary>
    internal static string Format(string key, params object[] arguments) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, Get(key), arguments);
}

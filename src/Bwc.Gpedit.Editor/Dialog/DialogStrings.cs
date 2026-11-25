using Microsoft.Windows.ApplicationModel.Resources;

namespace Bwc.Gpedit.Editor.Dialog;

/// <summary>Strings from this library's Strings/en-US/Dialog.resw (resource map Bwc.Gpedit.Editor/Dialog).</summary>
internal static class DialogStrings
{
    // The default loader only sees the head's own map, so the library map must be named explicitly.
    private const string ResourceMap = "Bwc.Gpedit.Editor/Dialog";

    /// <summary>
    /// The string for a key. A missing or empty entry gives the key name, so a blank control shows up in review and in the
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
            Bwc.UI.UiDiagnostics.Warn?.Invoke($"Dialog string '{key}' could not be read: {ex.Message}");
            return key;
        }
    }
}

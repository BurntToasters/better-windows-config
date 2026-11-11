using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Bwc.Hub;

/// <summary>Strings from the Hub's own Strings/en-US/Resources.resw (the default resource map of the exe).</summary>
internal static class HubStrings
{
    private static ResourceLoader? _loader;

    public static string Get(string key)
    {
        _loader ??= new ResourceLoader();
        return _loader.GetString(key);
    }

    public static string Format(string key, params object[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, Get(key), args);
    }

    /// <summary>The resource text for a key, or null when the key is missing.</summary>
    public static string? TryGet(string key)
    {
        try
        {
            string value = Get(key);
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}

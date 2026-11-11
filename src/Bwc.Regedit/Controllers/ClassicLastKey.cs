namespace Bwc.Regedit.Controllers;

/// <summary>
/// Reads the LastKey value that classic regedit saves under the Applets key. It is read only: BWC keeps
/// its own state in its settings file and never writes classic's key.
/// </summary>
public static class ClassicLastKey
{
    /// <summary>The classic applet key, relative to HKEY_CURRENT_USER.</summary>
    public const string AppletKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit";

    /// <summary>Returns the saved classic key path, or null when it is missing or cannot be read.</summary>
    public static string? Read()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AppletKeyPath, writable: false);
            return key?.GetValue("LastKey") as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}

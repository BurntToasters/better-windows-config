using System.ComponentModel;
using Microsoft.Win32;

namespace Bwc.Oracle.Capture;

// The ONLY class in this tool that writes to the registry. Every write asserts that
// its full path sits under one of the two permitted roots before touching anything.
internal static class RegistryGuard
{
    internal const string OracleRoot = @"HKEY_CURRENT_USER\Software\BWC-Oracle";
    internal const string AppletRoot = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Applets\Regedit";

    private const string OracleSubPath = @"Software\BWC-Oracle";
    internal const string AppletSubPath = @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit";

    internal static void AssertAllowed(string fullPath)
    {
        if (!IsUnder(fullPath, OracleRoot) && !IsUnder(fullPath, AppletRoot))
        {
            throw new InvalidOperationException($"Registry write refused, path outside allowed roots: '{fullPath}'.");
        }
    }

    private static bool IsUnder(string path, string root)
    {
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return path.StartsWith(root + @"\", StringComparison.OrdinalIgnoreCase);
    }

    internal static void DeleteOracleTree()
    {
        AssertAllowed(OracleRoot);
        Registry.CurrentUser.DeleteSubKeyTree(OracleSubPath, throwOnMissingSubKey: false);
    }

    // Creates (or opens for writing) a key under the Oracle root. relative is e.g. "Seed\\Nested".
    internal static RegistryKey CreateOracleKey(string relative)
    {
        string full = relative.Length == 0 ? OracleRoot : OracleRoot + @"\" + relative;
        AssertAllowed(full);
        string sub = relative.Length == 0 ? OracleSubPath : OracleSubPath + @"\" + relative;
        RegistryKey? key = Registry.CurrentUser.CreateSubKey(sub, writable: true);
        return key ?? throw new IOException($"CreateSubKey returned null for '{full}'.");
    }

    // Writes one value with an exact REG_* type and exact bytes via RegSetValueExW.
    // valueName "" addresses the default value.
    internal static void SetRawValue(RegistryKey key, string keyFullPath, string valueName, uint type, byte[] data)
    {
        AssertAllowed(keyFullPath);
        int rc = RegistryNative.RegSetValueExW(key.Handle, valueName, 0, type, data, (uint)data.Length);
        if (rc != RegistryNative.ErrorSuccess)
        {
            throw new Win32Exception(rc, $"RegSetValueExW failed for '{keyFullPath}\\{valueName}' type {type}.");
        }
    }

    // Same as SetRawValue but stores the bytes exactly (no NUL fix-up). Used only for the seeded no-NUL REG_SZ.
    internal static void SetExactValue(RegistryKey key, string keyFullPath, string valueName, uint type, byte[] data)
    {
        AssertAllowed(keyFullPath);
        RegistryNative.SetExact(key.Handle, valueName, type, data);
    }

    // Opens (creating if needed) a key under the Regedit applet key, for the snapshot restore only.
    // relative is "" for the applet key itself or a subkey path such as "Favorites".
    internal static RegistryKey OpenOrCreateAppletKey(string relative)
    {
        string full = relative.Length == 0 ? AppletRoot : AppletRoot + @"\" + relative;
        AssertAllowed(full);
        string sub = relative.Length == 0 ? AppletSubPath : AppletSubPath + @"\" + relative;
        return Registry.CurrentUser.CreateSubKey(sub, writable: true)
            ?? throw new IOException($"Could not open '{full}' for writing.");
    }

    internal static void SetAppletValue(string relative, string valueName, uint type, byte[] data)
    {
        using RegistryKey key = OpenOrCreateAppletKey(relative);
        string full = relative.Length == 0 ? AppletRoot : AppletRoot + @"\" + relative;
        AssertAllowed(full);
        RegistryNative.SetExact(key.Handle, valueName, type, data);
    }

    internal static void DeleteAppletValue(string relative, string valueName)
    {
        string sub = relative.Length == 0 ? AppletSubPath : AppletSubPath + @"\" + relative;
        string full = relative.Length == 0 ? AppletRoot : AppletRoot + @"\" + relative;
        AssertAllowed(full);
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(sub, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    // Removes one subkey of the applet key with everything under it. relative must name a subkey, never the applet key.
    internal static void DeleteAppletSubtree(string relative)
    {
        if (relative.Length == 0)
        {
            throw new InvalidOperationException("Refusing to delete the whole Regedit applet key.");
        }

        AssertAllowed(AppletRoot + @"\" + relative);
        Registry.CurrentUser.DeleteSubKeyTree(AppletSubPath + @"\" + relative, throwOnMissingSubKey: false);
    }

    // Removes the applet key itself. Only used when the snapshot found it absent before the run.
    internal static void DeleteAppletKeyTree()
    {
        AssertAllowed(AppletRoot);
        Registry.CurrentUser.DeleteSubKeyTree(AppletSubPath, throwOnMissingSubKey: false);
    }
}

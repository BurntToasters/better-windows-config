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
    private const string AppletSubPath = @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit";

    // The only applet values this tool may write or remove.
    private static readonly string[] AllowedAppletValues = { "LastKey", "View" };

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

    // Restores one applet value (LastKey or View) to a previously captured raw state.
    internal static void SetAppletValue(string valueName, uint type, byte[] data)
    {
        EnsureAppletValueAllowed(valueName);
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(AppletSubPath, writable: true)
            ?? throw new IOException("Could not open applet key for writing.");
        SetRawValue(key, AppletRoot, valueName, type, data);
    }

    internal static void DeleteAppletValue(string valueName)
    {
        EnsureAppletValueAllowed(valueName);
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(AppletSubPath, writable: true);
        if (key is null)
        {
            return;
        }

        AssertAllowed(AppletRoot);
        key.DeleteValue(valueName, throwOnMissingValue: false);
    }

    // Removes the applet key itself. Only used when it did not exist before this tool ran
    // and is now empty, so that the registry returns to its original shape.
    internal static void DeleteAppletKeyIfEmpty()
    {
        AssertAllowed(AppletRoot);
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(AppletSubPath, writable: false);
        if (key is null || key.ValueCount != 0 || key.SubKeyCount != 0)
        {
            return;
        }

        key.Dispose();
        Registry.CurrentUser.DeleteSubKey(AppletSubPath, throwOnMissingSubKey: false);
    }

    private static void EnsureAppletValueAllowed(string valueName)
    {
        if (!AllowedAppletValues.Contains(valueName, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Applet value '{valueName}' is not on the allow list.");
        }
    }
}

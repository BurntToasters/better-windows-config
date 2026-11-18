using Microsoft.Win32;

namespace Bwc.Oracle.Capture.Gpedit.Session;

// The only code that deletes registry keys for a gpedit session. A key outside the fixed list is refused.
internal static class GpeditRegistry
{
    private static readonly string[] AllowedSubKeys =
    {
        GpeditPaths.PolicyKey,
        GpeditPaths.GpeditKey,
        GpeditPaths.SelftestKey,
    };

    internal static void AssertAllowed(RegistryTarget target)
    {
        if (!AllowedSubKeys.Contains(target.SubKey, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"refused: {target.Display} is not one of the keys a gpedit session may delete.");
        }

        if (target.LocalMachine && string.Equals(target.SubKey, GpeditPaths.SelftestKey, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("refused: the self test key is only allowed under HKCU.");
        }
    }

    private static RegistryKey Base(RegistryTarget target) =>
        RegistryKey.OpenBaseKey(target.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);

    internal static bool Exists(RegistryTarget target)
    {
        AssertAllowed(target);
        using RegistryKey root = Base(target);
        using RegistryKey? key = root.OpenSubKey(target.SubKey, writable: false);
        return key is not null;
    }

    internal static void Delete(RegistryTarget target)
    {
        AssertAllowed(target);
        using RegistryKey root = Base(target);
        root.DeleteSubKeyTree(target.SubKey, throwOnMissingSubKey: false);
    }
}

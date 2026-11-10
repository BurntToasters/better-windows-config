using Microsoft.Win32;

namespace Bwc.Oracle.Capture;

// Captures and restores HKCU\...\Applets\Regedit\{LastKey,View}, the only applet state this
// tool changes. If the Regedit applet key did not exist before, it is removed again when empty.
internal sealed class AppletSnapshot
{
    private static readonly string[] ManagedNames = { "LastKey", "View" };

    private const string AppletSubPath = @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit";

    private readonly bool _keyExisted;
    private readonly Dictionary<string, RegistryNative.RawValue?> _values;

    private AppletSnapshot(bool keyExisted, Dictionary<string, RegistryNative.RawValue?> values)
    {
        _keyExisted = keyExisted;
        _values = values;
    }

    internal static AppletSnapshot Capture()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(AppletSubPath, writable: false);
        var values = new Dictionary<string, RegistryNative.RawValue?>(StringComparer.Ordinal);
        foreach (string name in ManagedNames)
        {
            values[name] = key is null ? null : RegistryNative.QueryRaw(key.Handle, name);
        }

        return new AppletSnapshot(key is not null, values);
    }

    internal string Describe()
    {
        var parts = new List<string>();
        foreach (string name in ManagedNames)
        {
            RegistryNative.RawValue? v = _values[name];
            parts.Add(v is null ? $"{name}=<absent>" : $"{name}=type {v.Value.Type} {Util.Hex(v.Value.Data)}");
        }

        return $"applet key existed={_keyExisted}; " + string.Join("; ", parts);
    }

    internal void Restore()
    {
        foreach (string name in ManagedNames)
        {
            RegistryNative.RawValue? v = _values[name];
            if (v is null)
            {
                RegistryGuard.DeleteAppletValue(name);
            }
            else
            {
                RegistryGuard.SetAppletValue(name, v.Value.Type, v.Value.Data);
            }
        }

        if (!_keyExisted)
        {
            RegistryGuard.DeleteAppletKeyIfEmpty();
        }

        AppletSnapshot after = Capture();
        if (!SameAs(after))
        {
            throw new InvalidOperationException($"Applet restore mismatch. Expected {Describe()}, found {after.Describe()}.");
        }
    }

    // Compares only LastKey and View. Other applet values written by regedit are reported, not reverted.
    private bool SameAs(AppletSnapshot other)
    {
        foreach (string name in ManagedNames)
        {
            RegistryNative.RawValue? a = _values[name];
            RegistryNative.RawValue? b = other._values[name];
            if ((a is null) != (b is null))
            {
                return false;
            }

            if (a is not null && (a.Value.Type != b!.Value.Type || !a.Value.Data.AsSpan().SequenceEqual(b.Value.Data)))
            {
                return false;
            }
        }

        return true;
    }
}

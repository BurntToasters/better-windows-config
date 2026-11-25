using System.Text.Json.Nodes;
using Bwc.Oracle.Capture.Gpedit.Session;
using Microsoft.Win32;

namespace Bwc.Oracle.Capture.Gpedit.Driver;

// Reads, never writes, the live registry below the keys the oracle fixture can reach: Software\Policies\BWC-Oracle and
// Software\BWC-Oracle-Gpedit, in HKLM and HKCU (64-bit view). Result: key path to value name to type and data, empty keys included.
internal static class LiveRegistry
{
    internal static JsonObject Read()
    {
        var result = new JsonObject();
        foreach (bool machine in new[] { true, false })
        {
            foreach (string subKey in new[] { GpeditPaths.PolicyKey, GpeditPaths.GpeditKey })
            {
                string display = (machine ? "HKLM" : "HKCU") + "\\" + subKey;
                using RegistryKey root = RegistryKey.OpenBaseKey(machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);
                using RegistryKey? key = root.OpenSubKey(subKey, writable: false);
                if (key is null)
                {
                    result[display] = null;
                    continue;
                }

                Walk(key, display, result);
            }
        }

        return result;
    }

    internal static string Digest() => Read().ToJsonString();

    private static void Walk(RegistryKey key, string path, JsonObject into)
    {
        var values = new JsonObject();
        foreach (string name in key.GetValueNames().OrderBy(n => n, StringComparer.Ordinal))
        {
            RegistryValueKind kind = key.GetValueKind(name);
            object? data = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            values[name.Length == 0 ? "(Default)" : name] = new JsonObject { ["type"] = kind.ToString(), ["data"] = Format(data) };
        }

        into[path] = values;
        foreach (string sub in key.GetSubKeyNames().OrderBy(n => n, StringComparer.Ordinal))
        {
            using RegistryKey? child = key.OpenSubKey(sub, writable: false);
            if (child is not null)
            {
                Walk(child, path + "\\" + sub, into);
            }
        }
    }

    private static JsonNode? Format(object? data) => data switch
    {
        null => null,
        byte[] bytes => JsonValue.Create(Util.Hex(bytes)),
        string[] lines => new JsonArray(lines.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray()),
        string text => JsonValue.Create(text),
        int number => JsonValue.Create(number),
        long number => JsonValue.Create(number),
        _ => JsonValue.Create(data.ToString()),
    };
}

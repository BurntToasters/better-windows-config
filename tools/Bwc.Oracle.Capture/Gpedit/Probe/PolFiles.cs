using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bwc.Oracle.Capture.Gpedit.Session;
using Microsoft.Win32;

namespace Bwc.Oracle.Capture.Gpedit.Probe;

// Reads the local GPO files and the live probe key: bytes as hex, a parsed record list for Registry.pol, the gpt.ini version, timestamps.
// Reads only. Nothing here writes.
internal static partial class PolFiles
{
    private const int MaxHexBytes = 65536;

    internal static readonly string[] Names =
    {
        @"Machine\Registry.pol",
        @"gpt.ini",
        @"Machine\comment.cmtx",
        @"User\Registry.pol",
        @"User\comment.cmtx",
    };

    [GeneratedRegex(@"^\s*Version\s*=\s*(\d+)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex VersionPattern();

    // File name to SHA-256 (or "-" when absent), used to detect and time a change.
    internal static Dictionary<string, string> Fingerprint(GpeditPaths paths)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in Names)
        {
            string path = Path.Combine(paths.GroupPolicyDir, name);
            map[name] = File.Exists(path) ? SafeHash(path) : "-";
        }

        return map;
    }

    private static string SafeHash(string path)
    {
        try
        {
            return FileOps.Sha256(path);
        }
        catch (IOException)
        {
            return "locked";
        }
    }

    internal static JsonObject Snapshot(GpeditPaths paths, string label)
    {
        var files = new JsonObject();
        foreach (string name in Names)
        {
            files[name] = Describe(Path.Combine(paths.GroupPolicyDir, name), name);
        }

        var listing = new JsonArray();
        if (Directory.Exists(paths.GroupPolicyDir))
        {
            foreach (string file in Directory.EnumerateFiles(paths.GroupPolicyDir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                listing.Add(new JsonObject { ["path"] = Path.GetRelativePath(paths.GroupPolicyDir, file), ["size"] = new FileInfo(file).Length });
            }
        }

        string gptPath = Path.Combine(paths.GroupPolicyDir, "gpt.ini");
        JsonNode? version = null;
        if (File.Exists(gptPath))
        {
            Match match = VersionPattern().Match(File.ReadAllText(gptPath));
            if (match.Success && long.TryParse(match.Groups[1].Value, out long value))
            {
                version = new JsonObject { ["raw"] = value, ["machine"] = value & 0xFFFF, ["user"] = (value >> 16) & 0xFFFF };
            }
        }

        return new JsonObject
        {
            ["label"] = label,
            ["atUtc"] = DateTime.UtcNow.ToString("O"),
            ["groupPolicyExists"] = Directory.Exists(paths.GroupPolicyDir),
            ["files"] = files,
            ["listing"] = listing,
            ["gptVersion"] = version,
            ["liveRegistry"] = ReadLiveProbeKey(),
        };
    }

    private static JsonObject Describe(string path, string name)
    {
        if (!File.Exists(path))
        {
            return new JsonObject { ["exists"] = false };
        }

        var node = new JsonObject { ["exists"] = true };
        try
        {
            var info = new FileInfo(path);
            byte[] bytes = File.ReadAllBytes(path);
            node["size"] = bytes.Length;
            node["sha256"] = FileOps.Sha256(path);
            node["lastWriteUtc"] = info.LastWriteTimeUtc.ToString("O");
            node["attributes"] = info.Attributes.ToString();
            node["hex"] = Util.Hex(bytes.Length <= MaxHexBytes ? bytes : bytes[..MaxHexBytes]);
            if (name.EndsWith(".pol", StringComparison.OrdinalIgnoreCase))
            {
                node["records"] = ParsePol(bytes);
            }
            else
            {
                node["text"] = Encoding.UTF8.GetString(bytes);
            }
        }
        catch (IOException ex)
        {
            node["error"] = ex.Message;
        }

        return node;
    }

    // Registry.pol: header PReg 1, then records [key;value;type;size;data] in UTF-16LE. Stops with an error entry at the first violation.
    internal static JsonArray ParsePol(byte[] bytes)
    {
        var records = new JsonArray();
        if (bytes.Length < 8 || bytes[0] != 0x50 || bytes[1] != 0x52 || bytes[2] != 0x65 || bytes[3] != 0x67)
        {
            records.Add(new JsonObject { ["parseError"] = "bad or missing PReg header", ["length"] = bytes.Length });
            return records;
        }

        int p = 8;
        while (p < bytes.Length)
        {
            int start = p;
            try
            {
                Expect(bytes, ref p, '[');
                string key = ReadString(bytes, ref p);
                Expect(bytes, ref p, ';');
                string value = ReadString(bytes, ref p);
                Expect(bytes, ref p, ';');
                uint type = BitConverter.ToUInt32(bytes, p);
                p += 4;
                Expect(bytes, ref p, ';');
                uint size = BitConverter.ToUInt32(bytes, p);
                p += 4;
                Expect(bytes, ref p, ';');
                if (size > bytes.Length - p)
                {
                    throw new InvalidDataException("size runs past the end of the file");
                }

                byte[] data = bytes[p..(p + (int)size)];
                p += (int)size;
                Expect(bytes, ref p, ']');
                records.Add(new JsonObject
                {
                    ["offset"] = start,
                    ["key"] = key,
                    ["value"] = value,
                    ["type"] = type,
                    ["size"] = size,
                    ["data"] = Util.Hex(data),
                });
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            {
                records.Add(new JsonObject { ["parseError"] = ex.Message, ["offset"] = start });
                break;
            }
        }

        return records;
    }

    private static void Expect(byte[] bytes, ref int p, char ch)
    {
        if (p + 2 > bytes.Length || bytes[p] != (byte)ch || bytes[p + 1] != 0)
        {
            throw new InvalidDataException($"expected '{ch}' at offset {p}");
        }

        p += 2;
    }

    private static string ReadString(byte[] bytes, ref int p)
    {
        int start = p;
        while (p + 2 <= bytes.Length)
        {
            if (bytes[p] == 0 && bytes[p + 1] == 0)
            {
                string text = Encoding.Unicode.GetString(bytes, start, p - start);
                p += 2;
                return text;
            }

            p += 2;
        }

        throw new InvalidDataException($"unterminated string at offset {start}");
    }

    // What changed between two snapshots: files whose hash differs, and the gpt.ini version before and after.
    internal static JsonObject Diff(JsonObject? before, JsonObject after)
    {
        var changed = new JsonArray();
        var times = new JsonObject();
        JsonObject afterFiles = after["files"]!.AsObject();
        foreach (string name in Names)
        {
            string? was = before?["files"]?[name]?["sha256"]?.GetValue<string>();
            string? now = afterFiles[name]?["sha256"]?.GetValue<string>();
            if (!string.Equals(was, now, StringComparison.Ordinal))
            {
                changed.Add(name);
                times[name] = afterFiles[name]?["lastWriteUtc"]?.GetValue<string>();
            }
        }

        long? versionBefore = before?["gptVersion"]?["raw"]?.GetValue<long>();
        long? versionAfter = after["gptVersion"]?["raw"]?.GetValue<long>();
        return new JsonObject
        {
            ["changedFiles"] = changed,
            ["lastWriteUtcOfChanged"] = times,
            ["gptVersionBefore"] = versionBefore,
            ["gptVersionAfter"] = versionAfter,
            ["gptVersionChanged"] = versionBefore != versionAfter,
        };
    }

    // HKLM\Software\Policies\BWC-Oracle\Probe, read only. Null when the key is absent (no policy refresh has applied the probe).
    private static JsonNode? ReadLiveProbeKey()
    {
        var result = new JsonObject();
        foreach (bool machine in new[] { true, false })
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);
            using RegistryKey? key = root.OpenSubKey(GpeditPaths.PolicyKey + @"\Probe", writable: false);
            if (key is null)
            {
                result[machine ? "HKLM" : "HKCU"] = null;
                continue;
            }

            var values = new JsonObject();
            foreach (string name in key.GetValueNames())
            {
                values[name] = key.GetValue(name)?.ToString();
            }

            result[machine ? "HKLM" : "HKCU"] = values;
        }

        return result;
    }
}

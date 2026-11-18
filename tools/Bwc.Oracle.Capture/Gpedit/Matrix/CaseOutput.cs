using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bwc.Oracle.Capture.Gpedit.Driver;
using Bwc.Oracle.Capture.Gpedit.Probe;
using Bwc.Oracle.Capture.Gpedit.Session;

namespace Bwc.Oracle.Capture.Gpedit.Matrix;

internal sealed record CaseOutcome(bool Complete, bool Changed, int OptionalFailures, string? Failure, List<string> ChangedFiles);

// Turns one case's driver result into the tracked golden folder: the five GPO files after each recorded step, the crafted start files, and
// case.json (scrubbed). Written into a temp folder first and swapped in only when complete (failure modes 10, 11, 12, 17).
internal static class CaseOutput
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static string GoldensDir(RepoPaths repo) => Path.Combine(repo.Root, "docs", "parity", "oracle", "gpedit", "goldens");

    internal static string CaseDir(RepoPaths repo, MatrixCase c) => Path.Combine(GoldensDir(repo), c.Group, c.DirName);

    // Machine\Registry.pol becomes Machine.Registry.pol; gpt.ini stays.
    internal static string GoldenName(string polName) => polName.Replace('\\', '.');

    internal static string SafeStep(string label) => Regex.Replace(label, "[^A-Za-z0-9_.-]", "-");

    // A folder counts only with complete.json whose plan hash is the current one.
    internal static bool IsComplete(RepoPaths repo, MatrixCase c)
    {
        string marker = Path.Combine(CaseDir(repo, c), "complete.json");
        if (!File.Exists(marker))
        {
            return false;
        }

        try
        {
            JsonNode? node = JsonNode.Parse(File.ReadAllText(marker));
            return string.Equals(node?["planHash"]?.GetValue<string>(), c.PlanHash(), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return false;
        }
    }

    // Puts back or removes what a crashed swap left behind (<case>.old, <case>.tmp) in one group folder.
    internal static void Recover(RepoPaths repo, string group)
    {
        string dir = Path.Combine(GoldensDir(repo), group);
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (string old in Directory.GetDirectories(dir, "*.old"))
        {
            string real = old[..^4];
            if (Directory.Exists(real))
            {
                FileOps.DeleteTree(old);
            }
            else
            {
                Directory.Move(old, real);
            }
        }

        foreach (string tmp in Directory.GetDirectories(dir, "*.tmp"))
        {
            FileOps.DeleteTree(tmp);
        }
    }

    // Every operation must be ok, except the case's optional ones (their failure is an observation). Skipped operations never count.
    internal static bool Evaluate(MatrixCase c, JsonObject result, out int optionalFailures, out string? why)
    {
        optionalFailures = 0;
        why = null;
        if (result["startError"] is not null)
        {
            why = "classic did not start: " + result["startError"];
            return false;
        }

        if (result["cancelled"]?.GetValue<bool>() == true)
        {
            why = "cancelled";
            return false;
        }

        JsonArray ops = result["ops"]?.AsArray() ?? new JsonArray();
        if (ops.Count != c.Ops.Count)
        {
            why = $"the result holds {ops.Count} operations, the case has {c.Ops.Count}";
            return false;
        }

        if (result["liveRegistryAtStart"] is JsonObject start && start.Any(p => p.Value is not null))
        {
            why = "the live BWC-Oracle registry keys were not empty when classic started";
            return false;
        }

        for (int i = 0; i < ops.Count; i++)
        {
            JsonObject entry = ops[i]!.AsObject();
            if (entry["skipped"]?.GetValue<bool>() == true)
            {
                why = $"operation {i} ({c.Ops[i].Kind}) was skipped: {entry["reason"]}";
                return false;
            }

            if (entry["ok"]?.GetValue<bool>() == true)
            {
                continue;
            }

            if (c.Optional.Contains(i))
            {
                optionalFailures++;
                continue;
            }

            why = $"operation {i} ({c.Ops[i].Kind}{(c.Ops[i].Label is null ? string.Empty : " " + c.Ops[i].Label)}) failed: {entry["error"]}";
            return false;
        }

        return true;
    }

    internal static CaseOutcome Write(RepoPaths repo, MatrixCase c, JsonObject result, Scrubber scrub, int optionalFailures)
    {
        string group = Path.Combine(GoldensDir(repo), c.Group);
        Directory.CreateDirectory(group);
        string tmp = Path.Combine(group, c.DirName + ".tmp");
        string final = Path.Combine(group, c.DirName);
        string old = Path.Combine(group, c.DirName + ".old");
        if (Directory.Exists(tmp))
        {
            FileOps.DeleteTree(tmp);
        }

        try
        {
            Directory.CreateDirectory(tmp);
            foreach (StartFile file in c.Start.Files)
            {
                WriteBytes(Path.Combine(tmp, "start", GoldenName(file.Name)), file.Bytes);
            }

            JsonArray ops = result["ops"]!.AsArray();
            var steps = new JsonArray();
            JsonObject? last = null;
            for (int i = 0; i < c.Steps.Count; i++)
            {
                RecordedStep step = c.Steps[i];
                JsonObject snapshot = ops[step.SnapshotOp]!["data"]!["snapshot"]!.AsObject();
                string folder = c.Steps.Count > 1 ? Path.Combine(tmp, $"step-{i + 1:00}-{SafeStep(step.Label)}") : tmp;
                JsonObject files = WriteSnapshotFiles(folder, snapshot);
                last = snapshot;
                steps.Add(new JsonObject
                {
                    ["number"] = i + 1,
                    ["label"] = step.Label,
                    ["folder"] = c.Steps.Count > 1 ? $"step-{i + 1:00}-{SafeStep(step.Label)}" : ".",
                    ["files"] = files,
                    ["gptVersion"] = snapshot["gptVersion"]?.DeepClone(),
                    ["listing"] = snapshot["listing"]?.DeepClone(),
                });
            }

            if (c.Steps.Count > 1 && last is not null)
            {
                WriteSnapshotFiles(tmp, last);
            }

            JsonObject caseJson = BuildCaseJson(c, result, scrub, steps, optionalFailures);
            List<string> leaks = scrub.Leaks(caseJson["observed"]);
            if (leaks.Count > 0)
            {
                throw new InvalidDataException("text that is not BWC text reached case.json: " + string.Join(" | ", leaks.Take(5)));
            }

            string json = JsonSerializer.Serialize(caseJson, Pretty).Replace("\r\n", "\n") + "\n";
            WriteBytes(Path.Combine(tmp, "case.json"), new UTF8Encoding(false).GetBytes(json));

            List<string> changedFiles = Directory.Exists(final) ? Differences(final, tmp) : new List<string>();
            var marker = new JsonObject
            {
                ["case"] = c.FullId,
                ["planHash"] = c.PlanHash(),
                ["files"] = new JsonArray(Directory.GetFiles(tmp, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(tmp, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal).Select(f => (JsonNode?)f).ToArray()),
            };
            WriteBytes(Path.Combine(tmp, "complete.json"), new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(marker, Pretty).Replace("\r\n", "\n") + "\n"));

            if (Directory.Exists(final))
            {
                if (Directory.Exists(old))
                {
                    FileOps.DeleteTree(old);
                }

                Directory.Move(final, old);
            }

            Directory.Move(tmp, final);
            if (Directory.Exists(old))
            {
                FileOps.DeleteTree(old);
            }

            return new CaseOutcome(true, changedFiles.Count > 0, optionalFailures, null, changedFiles);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or KeyNotFoundException or NullReferenceException or FormatException or InvalidOperationException or JsonException)
        {
            if (Directory.Exists(tmp))
            {
                FileOps.DeleteTree(tmp);
            }

            if (!Directory.Exists(final) && Directory.Exists(old))
            {
                Directory.Move(old, final);
            }

            return new CaseOutcome(false, false, optionalFailures, "golden not written: " + ex.Message, new List<string>());
        }
    }

    private static void WriteBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    // The five files of one snapshot, decoded from hex and proved by their recorded SHA-256. Files that do not exist are not written.
    private static JsonObject WriteSnapshotFiles(string folder, JsonObject snapshot)
    {
        var summary = new JsonObject();
        foreach (string name in PolFiles.Names)
        {
            JsonNode? file = snapshot["files"]?[name];
            if (file?["exists"]?.GetValue<bool>() != true)
            {
                summary[GoldenName(name)] = null;
                continue;
            }

            string sha = file["sha256"]!.GetValue<string>();
            long size = file["size"]!.GetValue<long>();
            byte[] bytes = Convert.FromHexString(file["hex"]?.GetValue<string>() ?? throw new InvalidDataException($"{name} has no bytes in the snapshot ({file["error"]})"));
            if (bytes.Length != size || !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), sha, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"{name}: the recorded bytes do not match the recorded size or hash (a file over 64 KB, or a read during a write)");
            }

            WriteBytes(Path.Combine(folder, GoldenName(name)), bytes);
            summary[GoldenName(name)] = new JsonObject { ["size"] = size, ["sha256"] = sha };
        }

        return summary;
    }

    // Files whose bytes differ between the old and the new folder (complete.json excluded).
    private static List<string> Differences(string oldDir, string newDir)
    {
        static Dictionary<string, string> Hashes(string dir) => Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("complete.json", StringComparison.Ordinal))
            .ToDictionary(f => Path.GetRelativePath(dir, f).Replace('\\', '/'), f => FileOps.Sha256(f), StringComparer.Ordinal);

        Dictionary<string, string> before = Hashes(oldDir);
        Dictionary<string, string> after = Hashes(newDir);
        var changed = new List<string>();
        foreach (string key in before.Keys.Union(after.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!before.TryGetValue(key, out string? a) || !after.TryGetValue(key, out string? b) || a != b)
            {
                changed.Add(key);
            }
        }

        return changed;
    }

    private static JsonObject BuildCaseJson(MatrixCase c, JsonObject result, Scrubber scrub, JsonArray steps, int optionalFailures)
    {
        var start = new JsonArray();
        foreach (StartFile file in c.Start.Files)
        {
            start.Add(new JsonObject
            {
                ["name"] = GoldenName(file.Name),
                ["size"] = file.Bytes.Length,
                ["sha256"] = Convert.ToHexString(SHA256.HashData(file.Bytes)).ToLowerInvariant(),
            });
        }

        var ops = new JsonArray();
        for (int i = 0; i < c.Ops.Count; i++)
        {
            ops.Add(new JsonObject
            {
                ["index"] = i,
                ["kind"] = c.Ops[i].Kind,
                ["label"] = c.Ops[i].Label,
                ["plan"] = c.Ops[i].Describe(),
                ["optional"] = c.Optional.Contains(i) ? true : null,
            });
        }

        var observed = new JsonArray();
        JsonArray entries = result["ops"]!.AsArray();
        for (int i = 0; i < entries.Count; i++)
        {
            observed.Add(Observe(c, i, entries[i]!.AsObject(), scrub));
        }

        return new JsonObject
        {
            ["case"] = c.FullId,
            ["description"] = c.Description,
            ["planVersion"] = MatrixCase.PlanVersion,
            ["planHash"] = c.PlanHash(),
            ["startState"] = new JsonObject { ["description"] = c.Start.Description, ["files"] = start },
            ["optionalFailures"] = optionalFailures,
            ["ops"] = ops,
            ["steps"] = steps,
            ["observed"] = observed,
        };
    }

    private enum Tracking
    {
        Full,
        Partial,
        None,
    }

    // Which nodes' lists, counts and names may be tracked: the fixture's own nodes and the placeholder nodes. Anything that depends on the
    // machine's own templates is not (the All Settings rows, the children of Administrative Templates beyond the BWC ones).
    private static Tracking TrackingOf(GpeditOp op, Scrubber scrub)
    {
        List<string>? path = op.Path;
        if (path is null || path.Count == 0)
        {
            return Tracking.Full;
        }

        if (path[0] is "Software Settings" or "Windows Settings")
        {
            return Tracking.Full;
        }

        if (path[0] != "Administrative Templates")
        {
            return Tracking.None;
        }

        if (path.Count == 1)
        {
            return Tracking.Partial;
        }

        return path[1] != "All Settings" && scrub.IsAllowed(path[1]) ? Tracking.Full : Tracking.None;
    }

    private static JsonObject Observe(MatrixCase c, int index, JsonObject entry, Scrubber scrub)
    {
        GpeditOp op = c.Ops[index];
        bool ok = entry["ok"]?.GetValue<bool>() == true;
        var node = new JsonObject
        {
            ["index"] = index,
            ["kind"] = op.Kind,
            ["ok"] = ok,
        };
        if (!ok)
        {
            string error = entry["error"]?.ToString() ?? "(no message)";
            node["error"] = scrub.S(error);
        }

        if (entry["unexpectedFileChange"] is not null)
        {
            node["unexpectedFileChange"] = true;
        }

        JsonObject? files = entry["files"]?.AsObject();
        if (files is not null && files["changedFiles"] is JsonArray { Count: > 0 } changed)
        {
            node["changedFiles"] = changed.DeepClone();
            node["gptVersionBefore"] = files["gptVersionBefore"]?.DeepClone();
            node["gptVersionAfter"] = files["gptVersionAfter"]?.DeepClone();
        }

        JsonNode? data = entry["data"];
        if (op.Kind == "snapshot" && data?["snapshot"] is JsonObject snapshot)
        {
            var summary = new JsonObject();
            foreach (string name in PolFiles.Names)
            {
                JsonNode? file = snapshot["files"]?[name];
                summary[GoldenName(name)] = file?["exists"]?.GetValue<bool>() == true
                    ? new JsonObject { ["size"] = file["size"]?.DeepClone(), ["sha256"] = file["sha256"]?.DeepClone() }
                    : null;
            }

            node["state"] = summary;
            node["gptVersion"] = snapshot["gptVersion"]?.DeepClone();
            if (c.KeepRegistry)
            {
                node["liveRegistry"] = scrub.Clean(data["liveRegistry"]);
            }

            return node;
        }

        if (data is null || op.Kind is "screenshot" or "wait")
        {
            return node;
        }

        Tracking tracking = op.Kind is "readList" or "readStatus" or "treeChildren" or "sortColumn" or "pressKey" or "readLayout" or "selectNode" or "setFilter"
            or "filterOn" or "clearFilter"
            ? TrackingOf(op, scrub)
            : Tracking.Full;
        if (tracking == Tracking.None)
        {
            node["data"] = new JsonObject { ["untracked"] = "depends on the machine's own templates" };
            return node;
        }

        JsonNode? cleaned = scrub.Clean(data);
        if (cleaned is JsonObject obj)
        {
            AddStatusNumbers(data, obj);
            if (tracking == Tracking.Partial && op.Kind == "treeChildren" && obj["children"] is JsonArray children)
            {
                obj["children"] = new JsonArray(children.Where(n => n is JsonValue v && v.TryGetValue(out string? s) && !s.StartsWith('#')).Select(n => n!.DeepClone()).ToArray());
                obj.Remove("count");
            }

            if (tracking == Tracking.Partial)
            {
                obj.Remove("rows");
                obj.Remove("rowCount");
                obj.Remove("firstRows");
            }

            if (op.Kind == "readLayout" && obj["tree"] is null)
            {
                obj["tree"] = FilterTree(data["tree"], scrub);
            }
        }

        node["data"] = cleaned;
        return node;
    }

    // `tree` is dropped by the scrubber (it holds every node of the machine's templates); the nodes whose name is BWC text or a plan label
    // are kept with their image indexes.
    private static JsonNode? FilterTree(JsonNode? tree, Scrubber scrub)
    {
        if (tree is not JsonArray nodes)
        {
            return null;
        }

        var kept = new JsonArray();
        int hidden = 0;
        foreach (JsonNode? n in nodes)
        {
            string name = n?["name"]?.GetValue<string>() ?? string.Empty;
            if (n is JsonObject o && o["name"] is not null && scrub.IsAllowed(name) && name.Length > 0)
            {
                kept.Add(o.DeepClone());
            }
            else
            {
                hidden++;
            }
        }

        return new JsonObject { ["nodes"] = kept, ["otherNodes"] = hidden > 0 ? "present, untracked" : null };
    }

    // The numbers of a status bar text ("N setting(s)"), so counts are tracked while the sentence is only a hash.
    private static void AddStatusNumbers(JsonNode raw, JsonObject cleaned)
    {
        string? text = raw["text"]?.GetValue<string>() ?? raw["status"]?["text"]?.GetValue<string>();
        if (text is null)
        {
            return;
        }

        var numbers = new JsonArray(Regex.Matches(text, @"\d+").Select(m => (JsonNode?)JsonValue.Create(int.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture))).ToArray());
        cleaned["statusNumbers"] = numbers;
    }
}

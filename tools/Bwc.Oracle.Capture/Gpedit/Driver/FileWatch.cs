using System.Text.Json.Nodes;
using Bwc.Oracle.Capture.Gpedit.Probe;
using Bwc.Oracle.Capture.Gpedit.Session;

namespace Bwc.Oracle.Capture.Gpedit.Driver;

// What classic wrote after one action. Begin takes the file hashes (and the live registry) before the action; Settle polls until the
// files have been stable for 500 ms after the last change, or no change came within the grace time (an OK with no change writes
// nothing), and returns before and after, first-seen times, every distinct hash per file, gpt.ini versions, and the live registry.
// Failure modes 14, 15, 16 and 25 in docs/failure-modes/oracle-gpedit-driver.md.
internal sealed class FileWatch
{
    private const int PollMs = 50;

    private readonly GpeditPaths paths;
    private readonly Dictionary<string, string> before;
    private readonly JsonObject beforeSnapshot;
    private readonly string registryBefore;

    private FileWatch(GpeditPaths paths, JsonObject beforeSnapshot)
    {
        this.paths = paths;
        this.beforeSnapshot = beforeSnapshot;
        before = PolFiles.Fingerprint(paths);
        registryBefore = LiveRegistry.Digest();
    }

    // The snapshot after the settle, set only when a file changed. The caller keeps it as the next "before".
    internal JsonObject? AfterSnapshot { get; private set; }

    internal static FileWatch Begin(GpeditPaths paths, JsonObject? lastSnapshot) =>
        new(paths, lastSnapshot ?? PolFiles.Snapshot(paths, "before"));

    internal JsonObject Settle(CancellationToken token, int graceMs = 1200, bool waitRegistry = true)
    {
        const int stableMs = 500;
        const int timeoutMs = 10000;
        DateTime start = DateTime.UtcNow;
        var current = new Dictionary<string, string>(before, StringComparer.OrdinalIgnoreCase);
        var history = PolFiles.Names.ToDictionary(n => n, n => new List<(string Hash, int Ms)> { (before[n], 0) }, StringComparer.OrdinalIgnoreCase);
        DateTime lastChange = start;
        bool anyChange = false;
        bool timedOut = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Dictionary<string, string> now = PolFiles.Fingerprint(paths);
            bool locked = now.Values.Any(v => v == "locked");
            foreach (string name in PolFiles.Names)
            {
                if (now[name] != current[name] && now[name] != "locked")
                {
                    current[name] = now[name];
                    history[name].Add((now[name], (int)(DateTime.UtcNow - start).TotalMilliseconds));
                    anyChange = true;
                    lastChange = DateTime.UtcNow;
                }
            }

            if (locked)
            {
                lastChange = DateTime.UtcNow;
            }

            double sinceStart = (DateTime.UtcNow - start).TotalMilliseconds;
            if (anyChange && !locked && (DateTime.UtcNow - lastChange).TotalMilliseconds >= stableMs)
            {
                break;
            }

            if (!anyChange && sinceStart >= graceMs)
            {
                break;
            }

            if (sinceStart >= timeoutMs)
            {
                timedOut = true;
                break;
            }

            Thread.Sleep(PollMs);
        }

        int settleMs = (int)(DateTime.UtcNow - start).TotalMilliseconds;
        var changedFiles = PolFiles.Names.Where(n => current[n] != before[n]).ToList();
        JsonObject result = new()
        {
            ["changed"] = changedFiles.Count > 0,
            ["changedFiles"] = new JsonArray(changedFiles.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
            ["groups"] = new JsonObject
            {
                ["machine"] = changedFiles.Any(f => f.StartsWith("Machine\\", StringComparison.OrdinalIgnoreCase)),
                ["user"] = changedFiles.Any(f => f.StartsWith("User\\", StringComparison.OrdinalIgnoreCase)),
                ["gptIni"] = changedFiles.Contains("gpt.ini", StringComparer.OrdinalIgnoreCase),
            },
            ["settleMs"] = settleMs,
            ["settleTimedOut"] = timedOut,
            ["beforeHashes"] = Hashes(before),
            ["afterHashes"] = Hashes(current),
            ["firstSeenMs"] = new JsonObject(changedFiles.Select(n => KeyValuePair.Create(n, (JsonNode?)JsonValue.Create(history[n][1].Ms))).ToArray()),
            ["writes"] = new JsonObject(changedFiles.Select(n => KeyValuePair.Create(n, (JsonNode?)JsonValue.Create(history[n].Count - 1))).ToArray()),
            ["history"] = new JsonObject(changedFiles.Select(n => KeyValuePair.Create(
                n,
                (JsonNode?)new JsonArray(history[n].Select(h => (JsonNode?)new JsonObject { ["hash"] = h.Hash, ["ms"] = h.Ms }).ToArray()))).ToArray()),
        };

        if (changedFiles.Count > 0)
        {
            JsonObject after = PolFiles.Snapshot(paths, "after");
            after.Remove("liveRegistry");
            AfterSnapshot = after;
            result["gptVersionBefore"] = beforeSnapshot["gptVersion"]?.DeepClone();
            result["gptVersionAfter"] = after["gptVersion"]?.DeepClone();
            result["diff"] = PolFiles.Diff(beforeSnapshot, after);
            result["snapshot"] = after.DeepClone();
        }
        else
        {
            result["gptVersionAfter"] = beforeSnapshot["gptVersion"]?.DeepClone();
        }

        result["liveRegistry"] = waitRegistry && changedFiles.Count > 0 ? SettleRegistry(token) : CurrentRegistry();
        return result;
    }

    private JsonObject CurrentRegistry() => new() { ["after"] = LiveRegistry.Read(), ["changed"] = LiveRegistry.Digest() != registryBefore, ["waited"] = false };

    // The live registry is applied by classic about 4 seconds after OK. Wait for it to change and then stay stable for 500 ms.
    private JsonObject SettleRegistry(CancellationToken token)
    {
        const int graceMs = 6000;
        const int stableMs = 500;
        const int timeoutMs = 10000;
        DateTime start = DateTime.UtcNow;
        string last = registryBefore;
        DateTime lastChange = start;
        bool changed = false;
        bool timedOut = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            string digest = LiveRegistry.Digest();
            if (digest != last)
            {
                last = digest;
                lastChange = DateTime.UtcNow;
                changed = digest != registryBefore || changed;
            }

            double elapsed = (DateTime.UtcNow - start).TotalMilliseconds;
            if (changed && (DateTime.UtcNow - lastChange).TotalMilliseconds >= stableMs)
            {
                break;
            }

            if (!changed && elapsed >= graceMs)
            {
                break;
            }

            if (elapsed >= timeoutMs)
            {
                timedOut = true;
                break;
            }

            Thread.Sleep(PollMs * 4);
        }

        return new JsonObject
        {
            ["after"] = LiveRegistry.Read(),
            ["changed"] = changed,
            ["waited"] = true,
            ["waitMs"] = (int)(DateTime.UtcNow - start).TotalMilliseconds,
            ["timedOut"] = timedOut,
        };
    }

    private static JsonObject Hashes(Dictionary<string, string> map) =>
        new(PolFiles.Names.Select(n => KeyValuePair.Create(n, (JsonNode?)JsonValue.Create(map[n]))).ToArray());
}

using System.Diagnostics;
using System.Text.Json;
using Bwc.E2E.Harness;

namespace Bwc.E2E.Installer;

/// <summary>
/// The checks of the elevated machine-mode installer tests. Each check goes to <see cref="E2EResults"/> (the artifact the
/// runner prints) and to this list, which <see cref="Write"/> turns into artifacts/e2e/installer-machine.json together
/// with the run id, the rid, and the evidence text (icacls output, SDDL, helper output).
/// </summary>
internal static class MachineChecks
{
    public const string FileName = "installer-machine.json";

    private static readonly object Sync = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly DateTime StartedUtc = DateTime.UtcNow;
    private static readonly List<Entry> Entries = [];
    private static readonly SortedDictionary<string, string> Evidence = new(StringComparer.Ordinal);
    private static bool _begun;

    public static int Count
    {
        get
        {
            lock (Sync)
            {
                return Entries.Count;
            }
        }
    }

    /// <summary>Called by the first test past its gates. Removes the file of an earlier run, so a crash leaves none.</summary>
    public static void Begin(E2EConfig config)
    {
        lock (Sync)
        {
            if (_begun)
            {
                return;
            }

            _begun = true;
            File.Delete(Path.Combine(config.ArtifactsDir, FileName));
        }
    }

    public static bool Record(string id, bool pass, string notes)
    {
        lock (Sync)
        {
            Entries.Add(new Entry(id, pass, notes, Clock.ElapsedMilliseconds));
        }

        return E2EResults.Record(id, pass, notes);
    }

    public static void AddEvidence(string name, string text)
    {
        lock (Sync)
        {
            Evidence[name] = text;
        }
    }

    public static IReadOnlyList<string> FailuresSince(int startIndex)
    {
        lock (Sync)
        {
            return Entries.Skip(startIndex).Where(entry => !entry.Pass).Select(entry => entry.Id + ": " + entry.Notes).ToList();
        }
    }

    /// <summary>Writes the file when a machine test ran its checks. A run where every test was not run writes none.</summary>
    public static void Write(E2EConfig config, MachineInputs? inputs)
    {
        Entry[] snapshot;
        KeyValuePair<string, string>[] evidence;
        lock (Sync)
        {
            if (Entries.Count == 0)
            {
                return;
            }

            snapshot = [.. Entries];
            evidence = [.. Evidence];
        }

        var document = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schema"] = 1,
            ["product"] = "Better Windows Config",
            ["runId"] = config.RunId,
            ["rid"] = config.Rid,
            ["flavor"] = config.Flavor,
            ["elevated"] = MachineNative.IsCurrentProcessElevated(),
            ["versions"] = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["a"] = inputs?.A.Version,
                ["b"] = inputs?.B.Version,
            },
            ["startedUtc"] = StartedUtc.ToString("o"),
            ["durationMs"] = Clock.ElapsedMilliseconds,
            ["pass"] = snapshot.All(entry => entry.Pass),
            ["checks"] = snapshot.Select(entry => new { id = entry.Id, pass = entry.Pass, notes = entry.Notes, atMs = entry.AtMs }),
            ["evidence"] = evidence.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        };

        Directory.CreateDirectory(config.ArtifactsDir);
        File.WriteAllText(
            Path.Combine(config.ArtifactsDir, FileName),
            JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private sealed record Entry(string Id, bool Pass, string Notes, long AtMs);
}

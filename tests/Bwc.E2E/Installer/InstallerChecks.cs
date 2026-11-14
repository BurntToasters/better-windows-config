using System.Diagnostics;
using System.Text.Json;

namespace Bwc.E2E.Installer;

/// <summary>
/// The checks of the installer E2E run: an id, a pass state, and a note each. The list and the step timings are
/// written to artifacts/e2e/installer-update.json, the evidence scripts/test-installer-e2e.js reads for its verdict.
/// </summary>
internal static class InstallerChecks
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly DateTime StartedUtc = DateTime.UtcNow;
    private static readonly List<Check> Entries = [];
    private static readonly Dictionary<string, long> Durations = new(StringComparer.Ordinal);

    private sealed record Check(string Id, bool Pass, string Notes, long AtMs);

    public static int Count => Entries.Count;

    public static void Record(string id, bool pass, string notes) =>
        Entries.Add(new Check(id, pass, notes, Clock.ElapsedMilliseconds));

    public static void Duration(string label, long milliseconds) => Durations[label] = milliseconds;

    public static IReadOnlyList<string> FailuresSince(int startIndex) =>
        Entries.Skip(startIndex).Where(check => !check.Pass).Select(check => $"{check.Id}: {check.Notes}").ToList();

    public static void Write(string path, string rid, InstallerInputs inputs)
    {
        bool pass = Entries.Count > 0 && Entries.All(check => check.Pass);
        var document = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schema"] = 1,
            ["product"] = "Better Windows Config",
            ["rid"] = rid,
            ["flavor"] = "e2e",
            ["versions"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["from"] = inputs.VersionA,
                ["to"] = inputs.VersionB,
            },
            ["startedUtc"] = StartedUtc.ToString("o"),
            ["durationMs"] = Clock.ElapsedMilliseconds,
            ["pass"] = pass,
            ["checks"] = Entries.Select(check => new { id = check.Id, pass = check.Pass, notes = check.Notes, atMs = check.AtMs }),
            ["durationsMs"] = Durations,
        };

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}

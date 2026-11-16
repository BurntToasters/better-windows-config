using System.Diagnostics;
using System.Text.Json;

namespace Bwc.E2E.Installer;

/// <summary>
/// The checks of the migration E2E: an id, a pass state, and a note each. The list is written to
/// artifacts/e2e/migration-&lt;rid&gt;.json, the repeatable artifact of the legacy per-user cleanup
/// (docs/failure-modes/hub.md,
/// "Legacy per-user cleanup (0.1.2)"). The file says whether the real old installer case ran: only the installer E2E driver
/// (scripts/test-installer-e2e.js) supplies installer A, so a run of the filtered Migration tests alone
/// is marked incomplete.
/// </summary>
internal static class MigrationChecks
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly DateTime StartedUtc = DateTime.UtcNow;
    private static readonly List<Check> Entries = [];
    private static bool _realInstallRan;

    private sealed record Check(string Id, bool Pass, string Notes, long AtMs);

    public static int Count => Entries.Count;

    public static void Record(string id, bool pass, string notes) =>
        Entries.Add(new Check(id, pass, notes, Clock.ElapsedMilliseconds));

    /// <summary>Called by the case that installs the real old per-user install, so the artifact says it ran.</summary>
    public static void MarkRealInstallRan() => _realInstallRan = true;

    public static IReadOnlyList<string> FailuresSince(int startIndex) =>
        Entries.Skip(startIndex).Where(check => !check.Pass).Select(check => $"{check.Id}: {check.Notes}").ToList();

    /// <summary>Writes the artifact when at least one check was recorded. Does nothing for a run that
    /// never reached a case.</summary>
    public static void Write(string path, string rid)
    {
        if (Entries.Count == 0)
        {
            return;
        }

        bool pass = Entries.All(check => check.Pass);
        var document = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schema"] = 1,
            ["product"] = "Better Windows Config",
            ["kind"] = "legacy-per-user-cleanup",
            ["rid"] = rid,
            ["flavor"] = "e2e",
            ["startedUtc"] = StartedUtc.ToString("o"),
            ["durationMs"] = Clock.ElapsedMilliseconds,
            ["pass"] = pass,
            ["realInstall"] = _realInstallRan ? "run" : "skipped",
            ["complete"] = pass && _realInstallRan,
            ["checks"] = Entries.Select(check => new { id = check.Id, pass = check.Pass, notes = check.Notes, atMs = check.AtMs }),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}

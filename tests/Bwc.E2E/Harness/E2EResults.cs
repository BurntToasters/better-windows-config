using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using FlaUI.Core.Capturing;

namespace Bwc.E2E.Harness;

/// <summary>
/// Collects checks, notes, and known gaps from one test assembly and writes
/// artifacts/e2e/result-&lt;rid&gt;-&lt;flavor&gt;.json during cleanup.
/// A failed check captures the current application window when one is available.
/// A check is a pass or fail claim. A note is information only. A known gap is a documented
/// difference from the classic Registry Editor oracle, tied to one parity work package. It never
/// counts as a pass and never fails the run.
/// </summary>
public static class E2EResults
{
    /// <summary>Work package ids a known gap may cite. The parity plan owns this list.</summary>
    public static readonly IReadOnlyList<string> KnownGapWorkPackages = ["E1", "E2", "RF1", "RF4", "RF8", "UF7"];

    private static readonly object Sync = new();
    private static readonly List<Check> Checks = [];
    private static readonly List<NoteEntry> Notes = [];
    private static readonly List<GapEntry> KnownGaps = [];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [ThreadStatic]
    private static AppSession? _currentSession;

    private static E2EConfig? _config;
    private static DateTime _startedUtc;

    public static AppSession? CurrentSession
    {
        get => _currentSession;
        set => _currentSession = value;
    }

    public static void Begin(E2EConfig config)
    {
        lock (Sync)
        {
            _config = config;
            _startedUtc = DateTime.UtcNow;
            Checks.Clear();
            Notes.Clear();
            KnownGaps.Clear();
        }
    }

    public static bool Record(string id, bool pass, string notes)
    {
        var finalNotes = notes;
        if (!pass)
        {
            try
            {
                var screenshot = CaptureScreenshot(id, CurrentSession);
                finalNotes = Append(notes, $"screenshot: {screenshot}");
            }
            catch (Exception ex)
            {
                finalNotes = Append(notes, $"screenshot failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        lock (Sync)
        {
            Checks.Add(new Check(id, pass, finalNotes));
        }

        return pass;
    }

    /// <summary>
    /// Records information that is not a pass or fail claim, such as a value that was read for the
    /// record, a manual step that was not run, or a bookkeeping step that succeeded.
    /// </summary>
    public static void Note(string id, string text)
    {
        lock (Sync)
        {
            Notes.Add(new NoteEntry(id, text ?? string.Empty));
        }
    }

    /// <summary>
    /// Records a difference from the oracle that is known and bounded. The entry does not count as a
    /// pass and does not fail the run. <paramref name="workPackage"/> must be one of
    /// <see cref="KnownGapWorkPackages"/>.
    /// </summary>
    public static void KnownGap(string id, string workPackage, string expectedOracleBehavior, string observed)
    {
        if (!KnownGapWorkPackages.Contains(workPackage, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"Known gap '{id}' cites work package '{workPackage}'. Use one of: {string.Join(", ", KnownGapWorkPackages)}.",
                nameof(workPackage));
        }

        lock (Sync)
        {
            KnownGaps.Add(new GapEntry(id, workPackage, expectedOracleBehavior ?? string.Empty, observed ?? string.Empty));
        }
    }

    /// <summary>
    /// Records a pass when the observed behavior matches the oracle, and a known gap otherwise.
    /// Use this where a known difference is expected, so a fixed product turns the entry into a pass.
    /// </summary>
    public static bool RecordOrKnownGap(
        string id,
        bool matchesOracle,
        string workPackage,
        string expectedOracleBehavior,
        string observed)
    {
        if (matchesOracle)
        {
            return Record(id, true, observed);
        }

        KnownGap(id, workPackage, expectedOracleBehavior, observed);
        return false;
    }

    /// <summary>
    /// Captures the given session window, or the full screen when no session is available.
    /// Throws when the PNG cannot be written so the caller can record a screenshot failure.
    /// </summary>
    public static string CaptureScreenshot(string id, AppSession? session = null)
    {
        var config = _config ?? throw new InvalidOperationException("E2EResults.Begin was not called.");
        Directory.CreateDirectory(config.ScreenshotsDir);

        var safeId = Sanitize(id);
        var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
        var path = Path.Combine(config.ScreenshotsDir, $"{safeId}-{stamp}.png");

        using (var image = session is null ? Capture.Screen() : Capture.Element(session.Window))
        {
            image.ToFile(path);
        }

        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            throw new InvalidOperationException($"Screenshot was not written to '{path}'.");
        }

        return path;
    }

    public static bool AllPassed()
    {
        lock (Sync)
        {
            return Checks.Count > 0 && Checks.All(c => c.Pass);
        }
    }

    public static IReadOnlyList<string> FailedChecks()
    {
        lock (Sync)
        {
            return Checks
                .Where(c => !c.Pass)
                .Select(c => $"{c.Id}: {c.Notes}")
                .ToArray();
        }
    }

    /// <summary>Number of checks recorded so far. Tests use this to scope a failure assertion to their own checks.</summary>
    public static int CheckCount
    {
        get
        {
            lock (Sync)
            {
                return Checks.Count;
            }
        }
    }

    /// <summary>Failed checks recorded after <paramref name="startIndex"/>.</summary>
    public static IReadOnlyList<string> FailedChecksSince(int startIndex)
    {
        lock (Sync)
        {
            if (startIndex < 0 || startIndex > Checks.Count)
            {
                startIndex = 0;
            }

            return Checks
                .Skip(startIndex)
                .Where(c => !c.Pass)
                .Select(c => $"{c.Id}: {c.Notes}")
                .ToArray();
        }
    }

    /// <summary>
    /// Writes the artifact. Safe to call once from assembly cleanup, including after test failures.
    /// </summary>
    public static void Finish()
    {
        E2EConfig config;
        Check[] snapshot;
        NoteEntry[] notes;
        GapEntry[] gaps;
        DateTime startedUtc;

        lock (Sync)
        {
            config = _config ?? throw new InvalidOperationException("E2EResults.Begin was not called.");
            snapshot = Checks.ToArray();
            notes = Notes.ToArray();
            gaps = KnownGaps.ToArray();
            startedUtc = _startedUtc;
        }

        var durationMs = (long)(DateTime.UtcNow - startedUtc).TotalMilliseconds;
        var payload = new
        {
            schema = 2,
            pass = snapshot.Length > 0 && snapshot.All(c => c.Pass),
            runId = config.RunId,
            rid = config.Rid,
            appRid = config.Rid,
            flavor = config.Flavor,
            publishDir = config.PublishDir,
            startedUtc = startedUtc.ToString("O", CultureInfo.InvariantCulture),
            durationMs,
            osDescription = RuntimeInformation.OSDescription,
            osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            checks = snapshot.Select(c => new { id = c.Id, pass = c.Pass, notes = c.Notes }).ToArray(),
            notes = notes.Select(n => new { id = n.Id, text = n.Text }).ToArray(),
            knownGaps = gaps.Select(g => new
            {
                id = g.Id,
                workPackage = g.WorkPackage,
                expectedOracleBehavior = g.ExpectedOracleBehavior,
                observed = g.Observed,
            }).ToArray(),
        };

        Directory.CreateDirectory(config.ArtifactsDir);
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        File.WriteAllText(Path.Combine(config.ArtifactsDir, ResultFileName(config)), json + Environment.NewLine);
    }

    /// <summary>File name of the result artifact for one rid and flavor, read by scripts/test-e2e.js.</summary>
    public static string ResultFileName(E2EConfig config) => $"result-{config.Rid}-{config.Flavor}.json";

    private static string Sanitize(string id)
    {
        var chars = id.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray();
        var safe = new string(chars).Trim('-');
        return safe.Length == 0 ? "check" : safe;
    }

    private static string Append(string notes, string addition) =>
        string.IsNullOrWhiteSpace(notes) ? addition : $"{notes} {addition}";

    private sealed record Check(string Id, bool Pass, string Notes);

    private sealed record NoteEntry(string Id, string Text);

    private sealed record GapEntry(string Id, string WorkPackage, string ExpectedOracleBehavior, string Observed);
}

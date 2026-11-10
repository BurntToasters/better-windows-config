using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using FlaUI.Core.Capturing;

namespace Bwc.E2E.Harness;

/// <summary>
/// Collects checks from one test assembly and writes artifacts/e2e/result.json during cleanup.
/// A failed check captures the current application window when one is available.
/// </summary>
public static class E2EResults
{
    private static readonly object Sync = new();
    private static readonly List<Check> Checks = [];
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
        DateTime startedUtc;

        lock (Sync)
        {
            config = _config ?? throw new InvalidOperationException("E2EResults.Begin was not called.");
            snapshot = Checks.ToArray();
            startedUtc = _startedUtc;
        }

        var durationMs = (long)(DateTime.UtcNow - startedUtc).TotalMilliseconds;
        var payload = new
        {
            schema = 1,
            pass = snapshot.Length > 0 && snapshot.All(c => c.Pass),
            runId = config.RunId,
            rid = config.Rid,
            flavor = config.Flavor,
            publishDir = config.PublishDir,
            startedUtc = startedUtc.ToString("O", CultureInfo.InvariantCulture),
            durationMs,
            osDescription = RuntimeInformation.OSDescription,
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            checks = snapshot.Select(c => new { id = c.Id, pass = c.Pass, notes = c.Notes }).ToArray(),
        };

        Directory.CreateDirectory(config.ArtifactsDir);
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        File.WriteAllText(Path.Combine(config.ArtifactsDir, "result.json"), json + Environment.NewLine);
    }

    private static string Sanitize(string id)
    {
        var chars = id.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray();
        var safe = new string(chars).Trim('-');
        return safe.Length == 0 ? "check" : safe;
    }

    private static string Append(string notes, string addition) =>
        string.IsNullOrWhiteSpace(notes) ? addition : $"{notes} {addition}";

    private sealed record Check(string Id, bool Pass, string Notes);
}

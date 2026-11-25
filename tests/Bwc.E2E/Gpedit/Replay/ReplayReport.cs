using System.Security.Cryptography;
using System.Text.Json;
using Bwc.E2E.Harness;
using Bwc.Policy.Tests.Golden;

namespace Bwc.E2E.Gpedit.Replay;

/// <summary>
/// The artifact of a replay test: one row per file of every step of every case, with the size and SHA-256 that classic's
/// capture holds and the size and SHA-256 that the editor left in the sandbox. The file
/// <c>artifacts/e2e/visual/gpedit/gpedit-replay-&lt;name&gt;.json</c> holds no time and no machine path, so a second run
/// with the same code writes the same bytes, and a reviewer can compare any row with <c>case.json</c> of the capture.
/// </summary>
internal sealed class ReplayReport
{
    private readonly string _name;
    private readonly List<Row> _rows = [];
    private readonly List<string> _cases = [];

    internal ReplayReport(string name) => _name = name;

    internal sealed record Row(
        string Case,
        int Step,
        string Label,
        string File,
        long? ExpectedSize,
        string? ExpectedSha256,
        long? ActualSize,
        string? ActualSha256,
        bool Equal);

    internal void AddCase(string caseId) => _cases.Add(caseId);

    /// <summary>Adds the row of one file of one step.</summary>
    /// <param name="golden">The case.</param>
    /// <param name="step">The recorded step.</param>
    /// <param name="flatName">The file by its flat name.</param>
    /// <param name="actual">The bytes the editor left, or null when the file is absent.</param>
    /// <returns>True when the file equals the golden (an absent file is equal to an absent golden).</returns>
    internal bool AddFile(GoldenCase golden, GoldenStep step, string flatName, byte[]? actual)
    {
        var expected = step.Files[flatName];
        var equal = expected is null
            ? actual is null
            : actual is not null && File.ReadAllBytes(step.PathOf(flatName)).AsSpan().SequenceEqual(actual);
        _rows.Add(new Row(
            golden.Id,
            step.Number,
            step.Label,
            flatName,
            expected?.Size,
            expected?.Sha256,
            actual?.LongLength,
            actual is null ? null : Convert.ToHexString(SHA256.HashData(actual)).ToLowerInvariant(),
            equal));
        return equal;
    }

    /// <summary>Writes the report next to the other pictures and measurements of the Gpedit tests and returns its
    /// path.</summary>
    internal string Write()
    {
        var folder = Path.Combine(GuiGate.Config.ArtifactsDir, "visual", GpeditRun.Area);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"gpedit-replay-{_name}.json");
        var document = new
        {
            report = _name,
            cases = _cases,
            files = _rows.Count,
            equal = _rows.Count(r => r.Equal),
            different = _rows.Count(r => !r.Equal),
            rows = _rows,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
        E2EResults.Note($"gpedit.replay.{_name}.artifact", path);
        return path;
    }
}

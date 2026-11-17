using System.Text.Json.Serialization;

namespace Bwc.Update;

/// <summary> The JSON lines of BwcUpdate.exe on stdout. The Hub and the installer parse them, so the text is a contract
/// (update-cli.md, update-elevation.md HN1 to HN7). The names, their order, and the escaping are the ones the
/// reflection serializer wrote for anonymous types in 0.1.2. NativeAOT cannot do that, so the shapes are named records
/// written through a source-generated context: there is no run-time type lookup.
/// </summary>
public static class StdoutJson
{
    /// <summary>The line of <c>--check</c>.</summary>
    public static string Check(UpdateFailure failure, string detail, string? version, string? notesUrl) =>
        System.Text.Json.JsonSerializer.Serialize(
            new CheckLine(failure == UpdateFailure.None, failure.ToString(), detail, version, notesUrl),
            StdoutJsonContext.Default.CheckLine);

    /// <summary>The line of <c>--download</c>.</summary>
    public static string Download(UpdateFailure failure, string detail, string? version, string? notesUrl, string? stagingFolder) =>
        System.Text.Json.JsonSerializer.Serialize(
            new DownloadLine(failure == UpdateFailure.None, failure.ToString(), detail, version, notesUrl, stagingFolder),
            StdoutJsonContext.Default.DownloadLine);

    /// <summary>The line of <c>--apply</c> and <c>--apply-elevated</c>.</summary>
    public static string Apply(ApplyOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return System.Text.Json.JsonSerializer.Serialize(
            new ApplyLine(
                outcome.Failure == UpdateFailure.None,
                outcome.Failure.ToString(),
                outcome.Detail,
                outcome.InstallerStarted,
                outcome.ElevatedHelperStarted,
                outcome.HubLaunched),
            StdoutJsonContext.Default.ApplyLine);
    }

    /// <summary>The line of <c>--launch-hub</c>, <c>--set-aumid</c>, and an unexpected error in other modes.</summary>
    public static string Simple(UpdateFailure failure, string detail) =>
        System.Text.Json.JsonSerializer.Serialize(
            new SimpleLine(failure == UpdateFailure.None, failure.ToString(), detail),
            StdoutJsonContext.Default.SimpleLine);

    /// <summary>One progress line of <c>--download</c>.</summary>
    public static string Progress(long received, long total) =>
        System.Text.Json.JsonSerializer.Serialize(
            new ProgressLine(new ProgressCounts(received, total)),
            StdoutJsonContext.Default.ProgressLine);
}

// The property order of each record is the order on the wire.
internal sealed record CheckLine(bool Ok, string Failure, string Detail, string? Version, string? NotesUrl);

internal sealed record DownloadLine(bool Ok, string Failure, string Detail, string? Version, string? NotesUrl, string? StagingFolder);

internal sealed record ApplyLine(
    bool Ok,
    string Failure,
    string Detail,
    bool InstallerStarted,
    bool ElevatedHelperStarted,
    bool HubLaunched);

internal sealed record SimpleLine(bool Ok, string Failure, string Detail);

internal sealed record ProgressLine(ProgressCounts Progress);

internal sealed record ProgressCounts(long Received, long Total);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CheckLine))]
[JsonSerializable(typeof(DownloadLine))]
[JsonSerializable(typeof(ApplyLine))]
[JsonSerializable(typeof(SimpleLine))]
[JsonSerializable(typeof(ProgressLine))]
internal sealed partial class StdoutJsonContext : JsonSerializerContext;

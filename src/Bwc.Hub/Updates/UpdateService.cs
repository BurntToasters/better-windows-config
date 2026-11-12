using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Bwc.Core;

namespace Bwc.Hub.Updates;

internal enum UpdateOutcomeKind
{
    NotConfigured,
    UpToDate,
    Available,
    Failed,
    Canceled,
}

/// <summary>What one check produced. Message is display text. Version and NotesUrl are set for an offer.</summary>
internal sealed record UpdateCheckResult(UpdateOutcomeKind Kind, string Message, string? Version = null, string? NotesUrl = null);

/// <summary>A verified installer the helper staged. FolderPath holds installer.exe and receipt.json.</summary>
internal sealed record StagedUpdate(string FolderPath, string Version);

/// <summary>Installer bytes written so far, out of the size the manifest states.</summary>
internal readonly record struct DownloadProgressReport(long Received, long Total);

/// <summary>
/// Runs the update helper (BwcUpdate.exe). The check (manifest only) and the download (installer, with
/// progress) are separate steps, each with its own limit. The helper does all network and signature
/// work; the Hub starts it, reads its JSON lines, and maps the result to display text. Runs off the UI
/// thread and honors cancellation by killing the helper, after which incomplete staging is discarded.
/// </summary>
internal sealed class UpdateService
{
    public const string HelperFileName = "BwcUpdate.exe";

    private const string StagingFolderName = "BWC-Update";
    private const string InstallerFileName = "installer.exe";
    private const string ReceiptFileName = "receipt.json";
    private const string ProgressPrefix = "{\"progress\"";
    private const int MaxOutputCharacters = 64 * 1024;

    /// <summary>How long a killed helper's output may take to reach its end. The pipe closes with the process.</summary>
    private static readonly TimeSpan PipeDrainLimit = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The check: two manifest reads of at most 60 seconds each, plus a margin. The helper's own limits are
    /// the manifest timeout (60 seconds) and the stall limit (60 seconds) per read.
    /// </summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// The download: the manifest pair (2 minutes) plus the helper's installer limit (30 minutes), with a margin.
    /// The helper gives up first, so the Hub only kills a helper that has stopped responding.
    /// </summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(32);

    public static string HelperPath => Path.Combine(AppContext.BaseDirectory, HelperFileName);

    /// <summary>The per-user staging root. The helper writes here, and the Hub prunes here.</summary>
    public static string StagingRoot => Path.Combine(Path.GetTempPath(), StagingFolderName);

    /// <summary>
    /// Checks the signed manifest only. Downloads nothing and stages nothing. Never throws for a helper problem.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(string? manifestUrl, string channel, string currentVersion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            return new UpdateCheckResult(UpdateOutcomeKind.NotConfigured, HubStrings.Get("UpdateNotConfigured"));
        }

        string helper = HelperPath;
        if (!File.Exists(helper))
        {
            return Failed(HubStrings.Format("UpdateHelperMissing", helper));
        }

        HelperRun run;
        try
        {
            run = await RunHelperAsync(HelperArguments("--check", manifestUrl, channel, currentVersion), CheckTimeout, ct, onLine: null);
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult(UpdateOutcomeKind.Canceled, string.Empty);
        }
        catch (Win32Exception ex)
        {
            Log.Exception("Update helper could not start.", ex);
            return Failed(HubStrings.Format("UpdateStartFailed", ex.Message));
        }

        if (run.TimedOut)
        {
            Log.Warn("Update check timed out and the helper was stopped.");
            return Failed(HubStrings.Get("UpdateTimedOut"));
        }

        JsonElement? result = ResultLine(run.Stdout);
        if (result is not { } line)
        {
            Log.Warn("Update helper printed no result. Exit code " + run.ExitCode.ToString(CultureInfo.InvariantCulture) + ".");
            return Failed(HubStrings.Format("UpdateNoResult", run.ExitCode.ToString(CultureInfo.InvariantCulture)));
        }

        return MapCheck(line, currentVersion);
    }

    /// <summary>
    /// Downloads and verifies the installer for the offered version, with progress. The helper reuses a
    /// verified staged installer when one exists. Returns the staged folder on success.
    /// </summary>
    public async Task<(UpdateCheckResult Result, StagedUpdate? Staged)> DownloadAsync(
        string manifestUrl,
        string channel,
        string currentVersion,
        IProgress<DownloadProgressReport> progress,
        CancellationToken ct)
    {
        string helper = HelperPath;
        if (!File.Exists(helper))
        {
            return (Failed(HubStrings.Format("UpdateHelperMissing", helper)), null);
        }

        HelperRun run;
        try
        {
            run = await RunHelperAsync(
                HelperArguments("--download", manifestUrl, channel, currentVersion),
                DownloadTimeout,
                ct,
                line => ReportProgress(line, progress));
        }
        catch (OperationCanceledException)
        {
            DiscardIncompleteStaging();
            return (new UpdateCheckResult(UpdateOutcomeKind.Canceled, string.Empty), null);
        }
        catch (Win32Exception ex)
        {
            Log.Exception("Update helper could not start.", ex);
            return (Failed(HubStrings.Format("UpdateStartFailed", ex.Message)), null);
        }

        if (run.TimedOut)
        {
            Log.Warn("Update download timed out and the helper was stopped.");
            DiscardIncompleteStaging();
            return (Failed(HubStrings.Get("UpdateTimedOut")), null);
        }

        JsonElement? result = ResultLine(run.Stdout);
        if (result is not { } line)
        {
            Log.Warn("Update helper printed no result. Exit code " + run.ExitCode.ToString(CultureInfo.InvariantCulture) + ".");
            return (Failed(HubStrings.Format("UpdateNoResult", run.ExitCode.ToString(CultureInfo.InvariantCulture))), null);
        }

        bool ok = line.TryGetProperty("ok", out JsonElement okElement) && okElement.ValueKind == JsonValueKind.True;
        string? folder = ReadString(line, "stagingFolder");
        string? version = ReadString(line, "version");
        if (ok && !string.IsNullOrEmpty(folder) && !string.IsNullOrEmpty(version))
        {
            return (new UpdateCheckResult(UpdateOutcomeKind.Available, HubStrings.Format("UpdateStatusAvailable", version), version), new StagedUpdate(folder, version));
        }

        return (MapFailure(line, currentVersion), null);
    }

    /// <summary>
    /// Starts the helper's apply mode and returns without waiting. The helper waits for the Hub process to
    /// exit, verifies the installer again, starts it, and returns. The installer relaunches the Hub.
    /// </summary>
    public static void StartInstall(StagedUpdate staged, string currentVersion, string installDir, int hubProcessId)
    {
        var startInfo = new ProcessStartInfo(HelperPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add("--apply");
        startInfo.ArgumentList.Add(Path.Combine(staged.FolderPath, InstallerFileName));
        startInfo.ArgumentList.Add("--receipt");
        startInfo.ArgumentList.Add(Path.Combine(staged.FolderPath, ReceiptFileName));
        startInfo.ArgumentList.Add("--current-version");
        startInfo.ArgumentList.Add(currentVersion);
        startInfo.ArgumentList.Add("--install-dir");
        startInfo.ArgumentList.Add(installDir);
        startInfo.ArgumentList.Add("--wait-pid");
        startInfo.ArgumentList.Add(hubProcessId.ToString(CultureInfo.InvariantCulture));

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The update helper did not start.");
        Log.Info("Update install started. Helper pid " + process.Id.ToString(CultureInfo.InvariantCulture) + ".");
    }

    /// <summary>Names of the tools that run from the install folder. The install is refused while any runs.</summary>
    public static IReadOnlyList<string> ToolsRunningFrom(string installDir)
    {
        var names = new List<string>();
        foreach (ToolCatalog.Tool tool in ToolCatalog.Tools)
        {
            string processName = Path.GetFileNameWithoutExtension(tool.ExeName);
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    if (Bwc.Update.RunningImageRule.IsRunningFrom(process.Id, installDir))
                    {
                        names.Add(tool.DisplayName);
                        break;
                    }
                }
            }
        }

        return names;
    }

    /// <summary>
    /// Removes staging folders without a receipt. A helper that was killed mid-download never wrote its
    /// receipt, so its partial installer is removed here.
    /// </summary>
    public static void DiscardIncompleteStaging()
    {
        try
        {
            Bwc.Update.StagingCleanup.DiscardIncomplete(StagingRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Incomplete update staging could not be removed: " + ex.GetType().Name);
        }
    }

    private static string[] HelperArguments(string mode, string manifestUrl, string channel, string currentVersion)
    {
        string manifest = manifestUrl.Trim();
        return
        [
            mode,
            "--manifest-url", manifest,
            "--signature-url", UpdateSources.SignatureUrl(manifest),
            "--channel", channel,
            "--current-version", currentVersion,
        ];
    }

    private static void ReportProgress(string line, IProgress<DownloadProgressReport> progress)
    {
        if (!line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement body = document.RootElement.GetProperty("progress");
            long received = body.GetProperty("received").GetInt64();
            long total = body.GetProperty("total").GetInt64();
            progress.Report(new DownloadProgressReport(received, total));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // A progress line that does not parse is ignored; the result line still decides the outcome.
        }
    }

    private static UpdateCheckResult MapCheck(JsonElement line, string currentVersion)
    {
        bool ok = line.TryGetProperty("ok", out JsonElement okElement) && okElement.ValueKind == JsonValueKind.True;
        if (ok)
        {
            string? version = ReadString(line, "version");
            if (string.IsNullOrEmpty(version))
            {
                return MapFailure(line, currentVersion);
            }

            string? notes = ReadString(line, "notesUrl");
            Log.Info("Update check offers version " + version + ".");
            return new UpdateCheckResult(
                UpdateOutcomeKind.Available,
                HubStrings.Format("UpdateStatusAvailable", version),
                version,
                notes);
        }

        return MapFailure(line, currentVersion);
    }

    private static UpdateCheckResult MapFailure(JsonElement line, string currentVersion)
    {
        string failure = ReadString(line, "failure") ?? string.Empty;
        string detail = ReadString(line, "detail") ?? string.Empty;

        switch (failure)
        {
            case "TrustedKeysNotConfigured":
                return new UpdateCheckResult(UpdateOutcomeKind.NotConfigured, HubStrings.Get("UpdateNoReleaseKey"));
            case "TrustedKeysInvalid":
                Log.Warn("Update trusted key list is invalid. " + detail);
                return Failed(HubStrings.Get("UpdateTrustedKeysInvalid"));
            case "ChannelMismatch":
                return Failed(HubStrings.Get("UpdateChannelMismatch"));
            case "ManifestExpired":
                return Failed(HubStrings.Get("UpdateManifestExpired"));
            case "SameVersion":
            case "Downgrade":
            case "PrereleaseOnStable":
                return new UpdateCheckResult(
                    UpdateOutcomeKind.UpToDate,
                    HubStrings.Format("UpdateUpToDate", currentVersion));
            default:
                Log.Warn("Update check refused: " + failure + ". " + detail);
                return Failed(HubStrings.Format("UpdateRefused", failure, detail));
        }
    }

    private static UpdateCheckResult Failed(string message) =>
        new(UpdateOutcomeKind.Failed, message);

    private static string? ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>The last JSON line that carries an "ok" field. Progress lines do not, so they are skipped.</summary>
    private static JsonElement? ResultLine(string output)
    {
        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (!lines[i].StartsWith('{'))
            {
                continue;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(lines[i]);
                if (document.RootElement.TryGetProperty("ok", out _))
                {
                    return document.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
                // Not the result line. Keep looking upward.
            }
        }

        return null;
    }

    /// <summary>
    /// Runs the helper and reads its output line by line, so progress arrives while it runs. The
    /// helper is killed (its whole tree) when the timeout passes or the caller cancels.
    /// </summary>
    private static async Task<HelperRun> RunHelperAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct,
        Action<string>? onLine)
    {
        var startInfo = new ProcessStartInfo(HelperPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The update helper did not start.");

        Task<string> stderr = process.StandardError.ReadToEndAsync();
        var output = new StringBuilder();

        // The output is read on its own task. A read on a pipe does not honor cancellation while the
        // helper is silent, so the wait for exit (which does) is the one that decides when to kill it.
        Task reading = ReadOutputAsync(process.StandardOutput, output, onLine);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAny(reading, Task.Delay(PipeDrainLimit));
            await Task.WhenAny(stderr, Task.Delay(PipeDrainLimit));
            ct.ThrowIfCancellationRequested();
            return new HelperRun(-1, string.Empty, string.Empty, TimedOut: true);
        }

        await reading;
        string errors = await stderr;
        if (errors.Length > 0)
        {
            Log.Warn("Update helper wrote to stderr: " + errors.Trim());
        }

        return new HelperRun(process.ExitCode, output.ToString(), errors, TimedOut: false);
    }

    private static async Task ReadOutputAsync(StreamReader reader, StringBuilder output, Action<string>? onLine)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (output.Length < MaxOutputCharacters)
            {
                output.AppendLine(line);
            }

            onLine?.Invoke(line);
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // The helper exited on its own while it was being stopped.
        }
    }

    private sealed record HelperRun(int ExitCode, string Stdout, string Stderr, bool TimedOut);
}

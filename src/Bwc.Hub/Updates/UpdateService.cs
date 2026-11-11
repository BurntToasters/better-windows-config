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

/// <summary>An installer that the helper staged and verified during a check.</summary>
internal sealed record StagedUpdate(string InstallerPath, string ReceiptPath, string Version);

/// <summary>
/// Runs the update helper (BwcUpdate.exe) for the check and the install. The helper does all
/// network and signature work; the Hub only starts it, reads its one JSON line, and maps the
/// result to display text. Runs off the UI thread and honors cancellation by killing the helper.
/// </summary>
internal sealed class UpdateService
{
    public const string HelperFileName = "BwcUpdate.exe";
    public const string SignatureSuffix = ".minisig";

    private const string StagingFolderName = "BWC-Update";
    private const string ReceiptFileName = "receipt.json";
    private const string InstallerFileName = "installer.exe";
    private const int MaxReceiptBytes = 16 * 1024;
    private const int MaxOutputCharacters = 64 * 1024;
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromMinutes(3);

    public static string HelperPath => Path.Combine(AppContext.BaseDirectory, HelperFileName);

    /// <summary>Checks for an update. Never throws for a helper problem; it returns a result instead.</summary>
    public async Task<UpdateCheckResult> CheckAsync(
        string? manifestUrl,
        string channel,
        string currentVersion,
        CancellationToken ct)
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

        var arguments = new List<string>
        {
            "--check",
            "--manifest-url", manifestUrl.Trim(),
            "--signature-url", manifestUrl.Trim() + SignatureSuffix,
            "--channel", channel,
            "--current-version", currentVersion,
        };

        HelperRun run;
        try
        {
            run = await RunHelperAsync(arguments, CheckTimeout, ct);
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

        JsonElement? result = LastJsonLine(run.Stdout);
        if (result is not { } line)
        {
            Log.Warn("Update helper printed no result. Exit code " + run.ExitCode.ToString(CultureInfo.InvariantCulture) + ".");
            return Failed(HubStrings.Format("UpdateNoResult", run.ExitCode.ToString(CultureInfo.InvariantCulture)));
        }

        return Map(line, currentVersion);
    }

    /// <summary>Finds the newest staged installer whose receipt names the offered version.</summary>
    public static bool TryFindStaged(string offeredVersion, out StagedUpdate? staged)
    {
        staged = null;
        string root = Path.Combine(Path.GetTempPath(), StagingFolderName);
        if (!Directory.Exists(root))
        {
            return false;
        }

        DateTime newest = DateTime.MinValue;
        foreach (string folder in Directory.EnumerateDirectories(root))
        {
            string receipt = Path.Combine(folder, ReceiptFileName);
            string installer = Path.Combine(folder, InstallerFileName);
            if (!File.Exists(receipt) || !File.Exists(installer))
            {
                continue;
            }

            string? version = ReceiptVersion(receipt);
            if (!string.Equals(version, offeredVersion, StringComparison.Ordinal))
            {
                continue;
            }

            DateTime written = File.GetLastWriteTimeUtc(receipt);
            if (staged is null || written > newest)
            {
                newest = written;
                staged = new StagedUpdate(installer, receipt, offeredVersion);
            }
        }

        return staged is not null;
    }

    /// <summary>
    /// Starts the helper's install mode and returns without waiting. The helper waits for the Hub
    /// process to exit, verifies the installer again, runs it, and starts the Hub again.
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
        startInfo.ArgumentList.Add(staged.InstallerPath);
        startInfo.ArgumentList.Add("--receipt");
        startInfo.ArgumentList.Add(staged.ReceiptPath);
        startInfo.ArgumentList.Add("--current-version");
        startInfo.ArgumentList.Add(currentVersion);
        startInfo.ArgumentList.Add("--install-dir");
        startInfo.ArgumentList.Add(installDir);
        startInfo.ArgumentList.Add("--wait-pid");
        startInfo.ArgumentList.Add(hubProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.Environment["__COMPAT_LAYER"] = "RunAsInvoker";

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The update helper did not start.");
        Log.Info("Update install started. Helper pid " + process.Id.ToString(CultureInfo.InvariantCulture) + ".");
    }

    /// <summary>Names of the tools that run from the install folder. The helper refuses to install while any runs.</summary>
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
                    if (RunsFrom(process, installDir))
                    {
                        names.Add(tool.DisplayName);
                        break;
                    }
                }
            }
        }

        return names;
    }

    private static bool RunsFrom(Process process, string installDir)
    {
        try
        {
            string? file = process.MainModule?.FileName;
            return file is not null &&
                file.StartsWith(installDir, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static UpdateCheckResult Map(JsonElement line, string currentVersion)
    {
        bool ok = line.TryGetProperty("ok", out JsonElement okElement) && okElement.ValueKind == JsonValueKind.True;
        string failure = ReadString(line, "failure") ?? string.Empty;
        string detail = ReadString(line, "detail") ?? string.Empty;

        if (ok)
        {
            string? version = ReadString(line, "version");
            if (string.IsNullOrEmpty(version))
            {
                return Failed(HubStrings.Format("UpdateRefused", failure, detail));
            }

            string? notes = ReadString(line, "notesUrl");
            Log.Info("Update check offers version " + version + ".");
            return new UpdateCheckResult(
                UpdateOutcomeKind.Available,
                HubStrings.Format("UpdateStatusAvailable", version),
                version,
                notes);
        }

        switch (failure)
        {
            case "TrustedKeysInvalid":
                return new UpdateCheckResult(UpdateOutcomeKind.NotConfigured, HubStrings.Get("UpdateNoReleaseKey"));
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

    private static JsonElement? LastJsonLine(string output)
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
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Not the result line. Keep looking upward.
            }
        }

        return null;
    }

    private static string? ReceiptVersion(string receiptPath)
    {
        try
        {
            var info = new FileInfo(receiptPath);
            if (info.Length > MaxReceiptBytes)
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(receiptPath, Encoding.UTF8));
            return ReadString(document.RootElement, "version");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static async Task<HelperRun> RunHelperAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct)
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

        startInfo.Environment["__COMPAT_LAYER"] = "RunAsInvoker";

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The update helper did not start.");

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();

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
            _ = await stdout;
            _ = await stderr;
            ct.ThrowIfCancellationRequested();
            return new HelperRun(-1, string.Empty, string.Empty, TimedOut: true);
        }

        string output = await stdout;
        string errors = await stderr;
        if (output.Length > MaxOutputCharacters)
        {
            output = output[..MaxOutputCharacters];
        }

        if (errors.Length > 0)
        {
            Log.Warn("Update helper wrote to stderr: " + errors.Trim());
        }

        return new HelperRun(process.ExitCode, output, errors, TimedOut: false);
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

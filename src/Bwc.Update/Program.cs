using System.Globalization;
using System.Text.Json;
using Bwc.Core;

namespace Bwc.Update;

internal static class Program
{
    private const string ToolId = "update";
    private const int DefaultWaitMilliseconds = 60_000;
    private const int DefaultProcessTimeoutSeconds = 30;

    // Progress lines are written at most once per this many bytes, so a 1 GB download prints a few hundred lines.
    private const long ProgressStepBytes = 4L * 1024 * 1024;

    /// <summary>Exit code for an unexpected error in any mode (update-cli.md, "Unexpected errors").</summary>
    private const int UnexpectedExitCode = 7;

    private static async Task<int> Main(string[] args)
    {
        UpdateArguments? parsed = null;
        try
        {
            parsed = UpdateArguments.Parse(args);
            return await RunModeAsync(parsed);
        }
        catch (Exception ex)
        {
            return WriteUnexpected(parsed, ex);
        }
    }

    private static async Task<int> RunModeAsync(UpdateArguments parsed)
    {
        switch (parsed.Mode)
        {
            case UpdateMode.Help:
                Console.WriteLine(UpdateArguments.UsageText);
                return 0;
            case UpdateMode.Invalid:
                Console.Error.WriteLine("BwcUpdate: " + parsed.Error);
                Console.Error.WriteLine(UpdateArguments.UsageText);
                return 2;
            case UpdateMode.Check:
                Log.Initialize(ToolId);
                return await RunCheckAsync(parsed);
            case UpdateMode.Download:
                Log.Initialize(ToolId);
                return await RunDownloadAsync(parsed);
            case UpdateMode.CloseRunning:
                Log.Initialize(ToolId);
                return RunCloseRunning(parsed);
            case UpdateMode.WaitPid:
                Log.Initialize(ToolId);
                return RunWaitPid(parsed);
            default:
                Log.Initialize(ToolId);
                return await RunApplyAsync(parsed);
        }
    }

    private static async Task<int> RunCheckAsync(UpdateArguments parsed)
    {
        var architecture = Architectures.Current();
        if (architecture is null)
        {
            return WriteCheck(UpdateFailure.InvalidOptions, "this build does not support the running architecture", null, null, 2);
        }

        var options = new UpdateOptions();
#if BWC_E2E
        options = UpdateE2ESeam.ApplyCheckOverrides(options);
#endif

        var manifest = UrlPolicy.Validate(parsed.Single("--manifest-url"), requireHttps: options.RequireHttps);
        if (!manifest.Ok)
        {
            return WriteCheck(manifest.Failure, manifest.Detail, null, null, 1);
        }

        var signature = UrlPolicy.Validate(parsed.Single("--signature-url"), requireHttps: options.RequireHttps);
        if (!signature.Ok)
        {
            return WriteCheck(signature.Failure, signature.Detail, null, null, 1);
        }

        UpdateChannelNames.TryParse(parsed.Single("--channel"), out var channel);
        var request = new UpdateRequest(
            manifest.Value!,
            signature.Value!,
            channel,
            parsed.Single("--current-version")!,
            architecture,
            UpdatePaths.DefaultStagingRoot);

        var outcome = await new UpdateChecker(options).CheckAsync(request);
        return WriteCheck(
            outcome.Failure,
            outcome.Detail,
            outcome.Value?.Version,
            outcome.Value?.NotesUrl?.AbsoluteUri,
            outcome.Ok ? 0 : 1);
    }

    private static async Task<int> RunDownloadAsync(UpdateArguments parsed)
    {
        var architecture = Architectures.Current();
        if (architecture is null)
        {
            return WriteDownload(UpdateFailure.InvalidOptions, "this build does not support the running architecture", null, null, null, 2);
        }

        var options = new UpdateOptions();
#if BWC_E2E
        options = UpdateE2ESeam.ApplyCheckOverrides(options);
#endif

        var manifest = UrlPolicy.Validate(parsed.Single("--manifest-url"), requireHttps: options.RequireHttps);
        if (!manifest.Ok)
        {
            return WriteDownload(manifest.Failure, manifest.Detail, null, null, null, 1);
        }

        var signature = UrlPolicy.Validate(parsed.Single("--signature-url"), requireHttps: options.RequireHttps);
        if (!signature.Ok)
        {
            return WriteDownload(signature.Failure, signature.Detail, null, null, null, 1);
        }

        UpdateChannelNames.TryParse(parsed.Single("--channel"), out var channel);
        var request = new UpdateRequest(
            manifest.Value!,
            signature.Value!,
            channel,
            parsed.Single("--current-version")!,
            architecture,
            UpdatePaths.DefaultStagingRoot);

        var outcome = await new UpdateChecker(options).DownloadAsync(request, CancellationToken.None, new ConsoleProgress());
        return WriteDownload(
            outcome.Failure,
            outcome.Detail,
            outcome.Value?.Receipt.Version,
            outcome.Value?.NotesUrl?.AbsoluteUri,
            outcome.Value?.FolderPath,
            outcome.Ok ? 0 : 1);
    }

    private static async Task<int> RunApplyAsync(UpdateArguments parsed)
    {
        var architecture = Architectures.Current();
        if (architecture is null)
        {
            return WriteApply(new ApplyOutcome(UpdateFailure.InvalidOptions, "unsupported architecture", false, false), 2);
        }

        var options = new UpdateOptions();
#if BWC_E2E
        options = UpdateE2ESeam.ApplyCheckOverrides(options);
#endif

        var installDir = parsed.Single("--install-dir")!;
        var waitPids = parsed.Multiple("--wait-pid").Select(pid => int.Parse(pid, CultureInfo.InvariantCulture)).ToList();
        var waitMilliseconds = parsed.Single("--wait-timeout-ms") is { } text
            ? int.Parse(text, CultureInfo.InvariantCulture)
            : DefaultWaitMilliseconds;

        var request = new ApplyRequest(
            parsed.Single("--apply")!,
            parsed.Single("--receipt")!,
            UpdatePaths.DefaultStagingRoot,
            parsed.Single("--current-version")!,
            architecture,
            installDir,
            waitPids,
            TimeSpan.FromMilliseconds(waitMilliseconds),
            parsed.Multiple("--installer-switch").ToList());

        var outcome = await new UpdateApplier(options, new WindowsUpdateProcessHost()).ApplyAsync(request);
        return WriteApply(outcome, ExitCodeFor(outcome));
    }

    private static int RunCloseRunning(UpdateArguments parsed)
    {
        var installDir = parsed.Single("--install-dir")!;
        var excluded = parsed.Multiple("--exclude-pid").Select(pid => int.Parse(pid, CultureInfo.InvariantCulture)).ToList();
        var seconds = parsed.Single("--timeout-seconds") is { } text
            ? int.Parse(text, CultureInfo.InvariantCulture)
            : DefaultProcessTimeoutSeconds;
        return ProcessCommands.CloseRunning(installDir, excluded, seconds, Console.Out);
    }

    private static int RunWaitPid(UpdateArguments parsed)
    {
        var pid = int.Parse(parsed.Single("--wait-pid")!, CultureInfo.InvariantCulture);
        var seconds = parsed.Single("--timeout-seconds") is { } text
            ? int.Parse(text, CultureInfo.InvariantCulture)
            : DefaultProcessTimeoutSeconds;
        return ProcessCommands.WaitForPid(pid, seconds, Console.Out);
    }

    private static int ExitCodeFor(ApplyOutcome outcome) => outcome.Failure switch
    {
        UpdateFailure.None => 0,
        UpdateFailure.ProcessesStillRunning => 4,
        UpdateFailure.InstallerFailed => 5,
        UpdateFailure.HubMissing => 6,
        UpdateFailure.Unexpected => UnexpectedExitCode,
        _ => 3,
    };

    /// <summary>
    /// The last line of a mode that threw. Each mode keeps its own JSON shape. Apply mode also relaunches the Hub
    /// (with <c>--update-result Unexpected</c>), so the user still gets the app. The installer is never started here.
    /// </summary>
    private static int WriteUnexpected(UpdateArguments? parsed, Exception ex)
    {
        Log.Exception("unexpected error in BwcUpdate", ex);
        var detail = "unexpected " + ex.GetType().Name + "; see the log";
        switch (parsed?.Mode)
        {
            case UpdateMode.Check:
                return WriteCheck(UpdateFailure.Unexpected, detail, null, null, UnexpectedExitCode);
            case UpdateMode.Download:
                return WriteDownload(UpdateFailure.Unexpected, detail, null, null, null, UnexpectedExitCode);
            case UpdateMode.Apply:
                var installDir = parsed!.Single("--install-dir");
                var hubLaunched = installDir is not null && StartHubAfterUnexpected(installDir);
                return WriteApply(new ApplyOutcome(UpdateFailure.Unexpected, detail, InstallerStarted: false, HubLaunched: hubLaunched), UnexpectedExitCode);
            default:
                Console.WriteLine(JsonSerializer.Serialize(new { ok = false, failure = UpdateFailure.Unexpected.ToString(), detail }));
                return UnexpectedExitCode;
        }
    }

    private static bool StartHubAfterUnexpected(string installDir)
    {
        try
        {
            return new WindowsUpdateProcessHost().StartDetached(
                Path.Combine(installDir, "BetterWindowsConfig.exe"),
                ["--update-result", UpdateFailure.Unexpected.ToString()],
                installDir);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            Log.Exception("could not start the Hub after an unexpected error", ex);
            return false;
        }
    }

    private static int WriteCheck(UpdateFailure failure, string detail, string? version, string? notesUrl, int exitCode)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = failure == UpdateFailure.None,
            failure = failure.ToString(),
            detail,
            version,
            notesUrl,
        }));
        return exitCode;
    }

    private static int WriteDownload(UpdateFailure failure, string detail, string? version, string? notesUrl, string? stagingFolder, int exitCode)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = failure == UpdateFailure.None,
            failure = failure.ToString(),
            detail,
            version,
            notesUrl,
            stagingFolder,
        }));
        return exitCode;
    }

    private static int WriteApply(ApplyOutcome outcome, int exitCode)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = outcome.Failure == UpdateFailure.None,
            failure = outcome.Failure.ToString(),
            detail = outcome.Detail,
            installerStarted = outcome.InstallerStarted,
            hubLaunched = outcome.HubLaunched,
        }));
        return exitCode;
    }

    /// <summary>
    /// Writes one progress line per <see cref="ProgressStepBytes"/>, and the last one. The Hub reads
    /// them as they arrive.
    /// </summary>
    private sealed class ConsoleProgress : IProgress<DownloadProgress>
    {
        private long _lastWritten = -1;

        public void Report(DownloadProgress value)
        {
            if (value.Received != value.Total && value.Received - _lastWritten < ProgressStepBytes)
            {
                return;
            }

            _lastWritten = value.Received;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                progress = new { received = value.Received, total = value.Total },
            }));
            Console.Out.Flush();
        }
    }
}

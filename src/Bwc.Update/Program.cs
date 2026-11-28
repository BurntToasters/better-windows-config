using System.Globalization;
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
        }

        // An elevated process never opens the file log: its folder comes from BWC_DATA_ROOT, which a caller can aim at
        // a junction.
        if (UpdateLogging.ShouldInitialize(parsed.Mode, ProcessIdentity.IsElevatedOrHighIntegrity()))
        {
            Log.Initialize(ToolId);
        }

        switch (parsed.Mode)
        {
            case UpdateMode.Check:
                return await RunCheckAsync(parsed);
            case UpdateMode.Download:
                return await RunDownloadAsync(parsed);
            case UpdateMode.CloseRunning:
                return RunCloseRunning(parsed);
            case UpdateMode.WaitPid:
                return RunWaitPid(parsed);
            case UpdateMode.ApplyElevated:
                return RunApplyElevated(parsed);
            case UpdateMode.LaunchHub:
                return RunLaunchHub(parsed);
            case UpdateMode.SetAumid:
                return RunSetAumid(parsed);
            default:
                return await RunApplyAsync(parsed);
        }
    }

    private static async Task<int> RunCheckAsync(UpdateArguments parsed)
    {
        // A check reads the network and writes the staging root, a folder that medium processes can write. It never
        // runs elevated (HK1).
        if (ElevationPolicy.RefusesElevated(parsed.Mode) && ProcessIdentity.IsElevatedOrHighIntegrity())
        {
            return WriteCheck(UpdateFailure.RunningElevated, ElevationPolicy.RefusalDetail, null, null, 1);
        }

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
        if (ElevationPolicy.RefusesElevated(parsed.Mode) && ProcessIdentity.IsElevatedOrHighIntegrity())
        {
            return WriteDownload(UpdateFailure.RunningElevated, ElevationPolicy.RefusalDetail, null, null, null, 1);
        }

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

    private static int RunApplyElevated(UpdateArguments parsed)
    {
        var architecture = Architectures.Current();
        if (architecture is null)
        {
            return WriteApply(new ApplyOutcome(UpdateFailure.InvalidOptions, "unsupported architecture", false, false), 2);
        }

        // The elevated helper does not pass on a startup hook, a profiler, or a host trace to what it starts (HE3).
        // This exe is NativeAOT, so none of them ran before Main either (HE5).
        _ = ChildEnvironment.RemoveRuntimeVariables();

        var options = new UpdateOptions();
        var stagingRoot = UpdatePaths.DefaultStagingRoot;
        string? protectedRoot = null;
#if BWC_E2E
        options = UpdateE2ESeam.ApplyCheckOverrides(options);
        protectedRoot = UpdateE2ESeam.ProtectedRoot(stagingRoot);

        // The sandbox root is protected by the same rules, with the current user in place of Administrators (HP).
        if (protectedRoot is not null)
        {
            options = options with { ProtectedAcl = ProtectedAcl.ForCurrentUser() };
        }
#endif

        // Only the staging folder comes from the command line. The install dir and the current version are this
        // helper's own.
        var host = new WindowsUpdateProcessHost();
        var request = new ElevatedApplyRequest(
            parsed.Single("--apply-elevated")!,
            stagingRoot,
            host.HelperDirectory,
            HelperVersion.Read(typeof(Program).Assembly) ?? string.Empty,
            architecture,
            protectedRoot);
        var outcome = new ElevatedApplier(options, host).Apply(request);
        return WriteApply(outcome, ExitCodeFor(outcome));
    }

    private static int RunLaunchHub(UpdateArguments parsed)
    {
        UpdateFailure? updateResult = null;
        if (parsed.Single("--update-result") is { } text && UpdateArguments.TryParseUpdateResult(text, out var failure))
        {
            updateResult = failure;
        }

        var host = new WindowsUpdateProcessHost();
        var launched = HubLauncher.Launch(host, host.HelperDirectory, updateResult);
        Console.WriteLine(StdoutJson.Simple(launched.Failure, launched.Ok ? "Hub started" : launched.Detail));
        return launched.Failure switch
        {
            UpdateFailure.None => 0,
            UpdateFailure.HubMissing => 6,
            UpdateFailure.DeElevationFailed => 8,
            _ => UnexpectedExitCode,
        };
    }

    /// <summary> <c>--set-aumid</c> (update-elevation.md SA): refuses a shortcut that is not one of the two of this
    /// product in the folder the installer wrote them to, or that is a link, then sets and reads back the
    /// AppUserModelID. It starts nothing.
    /// </summary>
    private static int RunSetAumid(UpdateArguments parsed)
    {
        var shortcut = parsed.Single("--set-aumid")!;
        var appId = parsed.Single("--app-id")!;
        var commonPrograms = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        var scope = ShortcutAumidRules.ShipScope(commonPrograms);
#if BWC_E2E
        scope = ShortcutAumidE2EScope.For(
            Path.GetTempPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            commonPrograms,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"));
#endif
        var problem = ShortcutAumidRules.ScopeProblem(shortcut, appId, scope) ?? ShortcutAumidRules.FileProblem(shortcut);
        if (problem is not null)
        {
            return WriteSimple(UpdateFailure.PathRejected, problem, 3);
        }

        var written = ShortcutAumid.Write(shortcut, appId);
        return written.Ok
            ? WriteSimple(UpdateFailure.None, "AppUserModelID set and read back", 0)
            : WriteSimple(written.Failure, written.Detail, 4);
    }

    private static int WriteSimple(UpdateFailure failure, string detail, int exitCode)
    {
        Console.WriteLine(StdoutJson.Simple(failure, detail));
        return exitCode;
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
        Log.Exception("unexpected error in BwcUpdate (" + BuildFlavor.Marker + ")", ex);
        var detail = "unexpected " + ex.GetType().Name + "; see the log";
        switch (parsed?.Mode)
        {
            case UpdateMode.Check:
                return WriteCheck(UpdateFailure.Unexpected, detail, null, null, UnexpectedExitCode);
            case UpdateMode.Download:
                return WriteDownload(UpdateFailure.Unexpected, detail, null, null, null, UnexpectedExitCode);
            case UpdateMode.Apply:
                var hubLaunched = StartHubAfterUnexpected();
                return WriteApply(new ApplyOutcome(UpdateFailure.Unexpected, detail, InstallerStarted: false, HubLaunched: hubLaunched), UnexpectedExitCode);
            default:
                Console.WriteLine(StdoutJson.Simple(UpdateFailure.Unexpected, detail));
                return UnexpectedExitCode;
        }
    }

    // Never from --install-dir (not validated at this point) and never at the helper's own integrity level (see HubLauncher).
    private static bool StartHubAfterUnexpected()
    {
        try
        {
            return HubLauncher.RelaunchAfterUnexpected(new WindowsUpdateProcessHost());
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            Log.Exception("could not start the Hub after an unexpected error", ex);
            return false;
        }
    }

    private static int WriteCheck(UpdateFailure failure, string detail, string? version, string? notesUrl, int exitCode)
    {
        Console.WriteLine(StdoutJson.Check(failure, detail, version, notesUrl));
        return exitCode;
    }

    private static int WriteDownload(UpdateFailure failure, string detail, string? version, string? notesUrl, string? stagingFolder, int exitCode)
    {
        Console.WriteLine(StdoutJson.Download(failure, detail, version, notesUrl, stagingFolder));
        return exitCode;
    }

    private static int WriteApply(ApplyOutcome outcome, int exitCode)
    {
        Console.WriteLine(StdoutJson.Apply(outcome));
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
            Console.WriteLine(StdoutJson.Progress(value.Received, value.Total));
            Console.Out.Flush();
        }
    }
}

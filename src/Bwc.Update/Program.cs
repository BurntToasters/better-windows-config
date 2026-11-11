using System.Globalization;
using System.Text.Json;
using Bwc.Core;

namespace Bwc.Update;

internal static class Program
{
    private const string ToolId = "update";
    private const int DefaultWaitMilliseconds = 60_000;

    private static async Task<int> Main(string[] args)
    {
        var parsed = UpdateArguments.Parse(args);
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
            default:
                Log.Initialize(ToolId);
                return await RunApplyAsync(parsed, args);
        }
    }

    private static async Task<int> RunCheckAsync(UpdateArguments parsed)
    {
        var architecture = Architectures.Current();
        if (architecture is null)
        {
            return WriteCheck(UpdateFailure.InvalidOptions, "this build does not support the running architecture", null, 2);
        }

        var manifest = UrlPolicy.Validate(parsed.Single("--manifest-url"), requireHttps: true);
        if (!manifest.Ok)
        {
            return WriteCheck(manifest.Failure, manifest.Detail, null, 1);
        }

        var signature = UrlPolicy.Validate(parsed.Single("--signature-url"), requireHttps: true);
        if (!signature.Ok)
        {
            return WriteCheck(signature.Failure, signature.Detail, null, 1);
        }

        UpdateChannelNames.TryParse(parsed.Single("--channel"), out var channel);
        var request = new UpdateRequest(
            manifest.Value!,
            signature.Value!,
            channel,
            parsed.Single("--current-version")!,
            architecture,
            UpdatePaths.DefaultStagingRoot);

        var outcome = await new UpdateChecker(new UpdateOptions()).CheckAsync(request);
        return WriteCheck(outcome.Failure, outcome.Detail, outcome.Value?.Receipt.Version, outcome.Ok ? 0 : 1);
    }

    private static async Task<int> RunApplyAsync(UpdateArguments parsed, string[] args)
    {
        var architecture = Architectures.Current();
        if (architecture is null)
        {
            return WriteApply(new ApplyOutcome(UpdateFailure.InvalidOptions, "unsupported architecture", null, false), 2);
        }

        var installDir = parsed.Single("--install-dir")!;
        var waitPids = parsed.Multiple("--wait-pid").Select(pid => int.Parse(pid, CultureInfo.InvariantCulture)).ToList();
        var waitMilliseconds = parsed.Single("--wait-timeout-ms") is { } text
            ? int.Parse(text, CultureInfo.InvariantCulture)
            : DefaultWaitMilliseconds;
        var host = new WindowsUpdateProcessHost();

        // The installer replaces this folder's files, so a helper running from it moves itself out first.
        if (HelperRelocation.ShouldRelocate(AppContext.BaseDirectory, installDir, parsed.Has("--relocated")))
        {
            var copyFolder = Path.Combine(UpdatePaths.DefaultStagingRoot, Guid.NewGuid().ToString("N"), "helper");
            HelperRelocation.CopyTree(AppContext.BaseDirectory, copyFolder);
            var copyArguments = args
                .Concat(["--relocated", "--wait-pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture)])
                .ToList();
            if (!host.StartDetached(Path.Combine(copyFolder, "BwcUpdate.exe"), copyArguments, copyFolder))
            {
                return WriteApply(new ApplyOutcome(UpdateFailure.HubMissing, "could not start the relocated helper", null, false), 3);
            }

            Log.Info("helper relocated out of the install folder");
            return 0;
        }

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

        var outcome = await new UpdateApplier(new UpdateOptions(), host).ApplyAsync(request);
        return WriteApply(outcome, ExitCodeFor(outcome));
    }

    private static int ExitCodeFor(ApplyOutcome outcome) => outcome.Failure switch
    {
        UpdateFailure.None => 0,
        UpdateFailure.ProcessesStillRunning => 4,
        UpdateFailure.InstallerFailed or UpdateFailure.InstallerTimedOut => 5,
        UpdateFailure.HubMissing => 6,
        _ => 3,
    };

    private static int WriteCheck(UpdateFailure failure, string detail, string? version, int exitCode)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = failure == UpdateFailure.None,
            failure = failure.ToString(),
            detail,
            version,
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
            installerExitCode = outcome.InstallerExitCode,
            hubLaunched = outcome.HubLaunched,
        }));
        return exitCode;
    }
}

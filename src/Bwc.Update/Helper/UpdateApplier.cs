using System.ComponentModel;
using System.Diagnostics;
using Bwc.Core;

namespace Bwc.Update;

/// <summary>
/// Helper mode. Order: refuse when elevated, check the request, check the paths and receipt, wait for
/// the Hub and the tools to exit, verify the installer and keep its file open with no write or delete
/// sharing, then start the installer with the verified file still held. The helper then returns: the
/// installer waits for this process to exit (update-cli.md), and it relaunches the Hub itself.
///
/// Every refusal before the installer starts relaunches the Hub with the failure code as the
/// <c>--update-result</c> argument, so the user sees what happened. The Hub is relaunched only after the
/// wait-for pids have exited, because the relaunched Hub would otherwise just activate the old one.
/// </summary>
public sealed class UpdateApplier
{
    private const string HubExecutableName = "BetterWindowsConfig.exe";
    private const string UpdateResultArgument = "--update-result";
    private const int PollMilliseconds = 250;

    private readonly UpdateOptions _options;
    private readonly IUpdateProcessHost _host;
    private bool _installerLaunched;

    public UpdateApplier(UpdateOptions options, IUpdateProcessHost host)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>
    /// Any unexpected exception before the installer starts ends as <see cref="UpdateFailure.Unexpected"/>, and the
    /// Hub is relaunched with that code. After the installer starts, the helper never relaunches the Hub.
    /// </summary>
    public async Task<ApplyOutcome> ApplyAsync(ApplyRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return await ApplyCoreAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Exception("unexpected error while applying the update", ex);
            if (_installerLaunched)
            {
                return new ApplyOutcome(UpdateFailure.Unexpected, "unexpected error after the installer started", InstallerStarted: true, HubLaunched: false);
            }

            // The wait for the pids is skipped here: the failure may have come from the process check itself.
            return await RefuseAsync(request, UpdateFailure.Unexpected, "unexpected error: " + ex.GetType().Name, ct, alreadyWaited: true);
        }
    }

    private async Task<ApplyOutcome> ApplyCoreAsync(ApplyRequest request, CancellationToken ct)
    {
        ChildEnvironment.RemoveCompatibilityLayer();

        // Staging under %TEMP% is writable by medium-integrity processes, so an elevated helper must not load from it.
        if (_host.IsElevated())
        {
            return await RefuseAsync(request, UpdateFailure.RunningElevated, "the update helper runs elevated, so the update was not applied", ct, alreadyWaited: false);
        }

        if (!SemVer.TryParse(request.CurrentVersion, out var current))
        {
            return await RefuseAsync(request, UpdateFailure.InvalidOptions, "installed version is not a SemVer version", ct, alreadyWaited: false);
        }

        if (!Architectures.IsKnown(request.Architecture))
        {
            return await RefuseAsync(request, UpdateFailure.InvalidOptions, "architecture is not supported", ct, alreadyWaited: false);
        }

        if (request.WaitPids.Any(pid => pid <= 0) || request.WaitTimeout <= TimeSpan.Zero)
        {
            return await RefuseAsync(request, UpdateFailure.InvalidOptions, "wait process ids and timeout must be positive", ct, alreadyWaited: false);
        }

        var keyCheck = MinisignVerifier.ValidatePublicKeys(_options.TrustedPublicKeys);
        if (!keyCheck.Ok)
        {
            return await RefuseAsync(request, TrustedKeys.FailureFor(_options.TrustedPublicKeys), keyCheck.Failure, ct, alreadyWaited: false);
        }

        var folder = InstallerPaths.Check(request.InstallerPath, request.ReceiptPath, request.StagingRoot);
        if (!folder.Ok)
        {
            return await RefuseAsync(request, folder.Failure, folder.Detail, ct, alreadyWaited: false);
        }

        // The installer relaunches the Hub on success and waits for this process to exit before it does.
        var arguments = InstallerArguments.Build(request.InstallDir, request.InstallerSwitches, Environment.ProcessId);
        if (!arguments.Ok)
        {
            return await RefuseAsync(request, arguments.Failure, arguments.Detail, ct, alreadyWaited: false);
        }

        var receipt = UpdateReceipt.Read(request.ReceiptPath);
        if (!receipt.Ok)
        {
            return await RefuseAsync(request, receipt.Failure, receipt.Detail, ct, alreadyWaited: false);
        }

        var offered = receipt.Value!;
        if (offered.Architecture != request.Architecture)
        {
            return await RefuseAsync(request, UpdateFailure.ArchitectureMismatch, "the receipt is for another architecture", ct, alreadyWaited: false);
        }

        // The receipt reader already accepted only canonical SemVer, so Parse cannot fail here.
        var order = SemVer.Parse(offered.Version).CompareTo(current);
        if (order == 0)
        {
            return await RefuseAsync(request, UpdateFailure.SameVersion, "the staged version is the installed version", ct, alreadyWaited: false);
        }

        if (order < 0)
        {
            return await RefuseAsync(request, UpdateFailure.Downgrade, "the staged version is older than the installed version", ct, alreadyWaited: false);
        }

        try
        {
            var exited = await WaitForExitAsync(request.WaitPids, request.InstallDir, request.WaitTimeout, ct);
            if (!exited)
            {
                return await RefuseAsync(request, UpdateFailure.ProcessesStillRunning, "apps are still running after the wait limit; nothing was installed", ct, alreadyWaited: true);
            }

            return await VerifyAndStartAsync(request, offered, arguments.Value!, ct);
        }
        catch (OperationCanceledException)
        {
            Log.Warn("apply was canceled");
            return await RefuseAsync(request, UpdateFailure.Canceled, "apply was canceled", ct, alreadyWaited: true);
        }
    }

    private async Task<ApplyOutcome> VerifyAndStartAsync(ApplyRequest request, UpdateReceipt offered, string arguments, CancellationToken ct)
    {
        var keys = _options.TrustedPublicKeys;
        var expected = new InstallerExpectation(offered.Version, offered.Architecture, offered.Size, offered.Sha256, offered.Signature);

        // The layout can change during the wait (for example, a junction swapped in), so it is checked again here.
        var layout = InstallerPaths.Check(request.InstallerPath, request.ReceiptPath, request.StagingRoot);
        if (!layout.Ok)
        {
            return await RefuseAsync(request, layout.Failure, layout.Detail, ct, alreadyWaited: true);
        }

        // The file stays open with FileShare.Read only. No other process can write or delete it from here
        // until this method returns, so the bytes checked below are the bytes started.
        // The folder is held first, then the installer. Its final path must be the held folder's final path plus
        // installer.exe. Short names and junctioned parents resolve the same way on both sides; a swap between the
        // two opens does not. The folder handle stays open until the installer has started.
        using var folder = InstallerPaths.OpenFolder(Path.GetDirectoryName(request.InstallerPath)!);
        if (folder is null)
        {
            return await RefuseAsync(request, UpdateFailure.PathRejected, "staging folder cannot be opened for verification", ct, alreadyWaited: true);
        }

        using var file = OpenInstaller(request.InstallerPath);
        if (file is null)
        {
            return await RefuseAsync(request, UpdateFailure.PathRejected, "installer cannot be opened for verification", ct, alreadyWaited: true);
        }

        var finalPath = InstallerPaths.FinalPathOf(file.SafeFileHandle);
        if (finalPath is null || !InstallerPaths.InFolder(folder, finalPath))
        {
            return await RefuseAsync(request, UpdateFailure.PathRejected, "installer is not in the folder that was checked", ct, alreadyWaited: true);
        }

        var verified = InstallerVerifier.Verify(file, expected, keys);
        if (!verified.Ok)
        {
            Log.Warn($"installer refused before start: {verified.Failure}");
            return await RefuseAsync(request, verified.Failure, verified.Detail, ct, alreadyWaited: true);
        }

        try
        {
            if (!_host.StartInstaller(InstallerPaths.StripPrefix(finalPath), arguments))
            {
                return await RefuseAsync(request, UpdateFailure.InstallerFailed, "installer did not start", ct, alreadyWaited: true);
            }

            _installerLaunched = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception)
        {
            Log.Exception("installer did not start", ex);
            return await RefuseAsync(request, UpdateFailure.InstallerFailed, "installer did not start: " + ex.GetType().Name, ct, alreadyWaited: true);
        }

        Log.Info($"update installer started: version={offered.Version} architecture={offered.Architecture}");
        return new ApplyOutcome(UpdateFailure.None, "installer started", InstallerStarted: true, HubLaunched: false);
    }

    /// <summary>
    /// Ends the apply without starting the installer, and starts the Hub with the failure code. Before the
    /// installer starts, the Hub may still be running, so the wait-for pids are waited for first.
    /// </summary>
    private async Task<ApplyOutcome> RefuseAsync(ApplyRequest request, UpdateFailure failure, string detail, CancellationToken ct, bool alreadyWaited)
    {
        Log.Warn($"update not applied: {failure}. {detail}");
        if (!alreadyWaited && request.WaitPids.Count > 0)
        {
            _ = await WaitForExitAsync(request.WaitPids, request.InstallDir, request.WaitTimeout, CancellationToken.None);
        }

        if (StartHub(request.InstallDir, failure))
        {
            return new ApplyOutcome(failure, detail, InstallerStarted: false, HubLaunched: true);
        }

        return new ApplyOutcome(UpdateFailure.HubMissing, $"{failure}: {detail}; the Hub could not be started", InstallerStarted: false, HubLaunched: false);
    }

    private async Task<bool> WaitForExitAsync(IReadOnlyList<int> pids, string installDir, TimeSpan timeout, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (_host.LiveProcesses(pids, installDir).Count == 0)
            {
                return true;
            }

            if (clock.Elapsed >= timeout)
            {
                return false;
            }

            await Task.Delay(PollMilliseconds, ct);
        }
    }

    private bool StartHub(string installDir, UpdateFailure failure)
    {
        try
        {
            return _host.StartDetached(
                Path.Combine(installDir, HubExecutableName),
                [UpdateResultArgument, failure.ToString()],
                installDir);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception)
        {
            Log.Exception("could not start the Hub", ex);
            return false;
        }
    }

    private static FileStream? OpenInstaller(string path)
    {
        try
        {
            return File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

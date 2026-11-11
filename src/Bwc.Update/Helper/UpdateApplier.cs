using System.ComponentModel;
using System.Diagnostics;
using Bwc.Core;

namespace Bwc.Update;

/// <summary>
/// Helper mode. Order: check the request, check the paths and receipt, wait for the apps to exit,
/// verify the installer and keep its file open with no write or delete sharing, then start the
/// installer with the verified file still held. The Hub is started again unless the wait timed out,
/// because then the Hub may still be running.
/// </summary>
public sealed class UpdateApplier
{
    private const string HubExecutableName = "BetterWindowsConfig.exe";
    private const int PollMilliseconds = 250;

    private readonly UpdateOptions _options;
    private readonly IUpdateProcessHost _host;

    public UpdateApplier(UpdateOptions options, IUpdateProcessHost host)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public async Task<ApplyOutcome> ApplyAsync(ApplyRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!SemVer.TryParse(request.CurrentVersion, out var current))
        {
            return Stop(UpdateFailure.InvalidOptions, "installed version is not a SemVer version");
        }

        if (!Architectures.IsKnown(request.Architecture))
        {
            return Stop(UpdateFailure.InvalidOptions, "architecture is not supported");
        }

        if (request.WaitPids.Any(pid => pid <= 0) || request.WaitTimeout <= TimeSpan.Zero)
        {
            return Stop(UpdateFailure.InvalidOptions, "wait process ids and timeout must be positive");
        }

        var keyCheck = MinisignVerifier.ValidatePublicKeys(_options.TrustedPublicKeys);
        if (!keyCheck.Ok)
        {
            return Stop(UpdateFailure.TrustedKeysInvalid, keyCheck.Failure);
        }

        var folder = InstallerPaths.Check(request.InstallerPath, request.ReceiptPath, request.StagingRoot);
        if (!folder.Ok)
        {
            return Stop(folder.Failure, folder.Detail);
        }

        var arguments = InstallerArguments.Build(request.InstallDir, request.InstallerSwitches);
        if (!arguments.Ok)
        {
            return Stop(arguments.Failure, arguments.Detail);
        }

        var receipt = UpdateReceipt.Read(request.ReceiptPath);
        if (!receipt.Ok)
        {
            return Stop(receipt.Failure, receipt.Detail);
        }

        var offered = receipt.Value!;
        if (offered.Architecture != request.Architecture)
        {
            return Stop(UpdateFailure.ArchitectureMismatch, "the receipt is for another architecture");
        }

        // The receipt reader already accepted only canonical SemVer, so Parse cannot fail here.
        var order = SemVer.Parse(offered.Version).CompareTo(current);
        if (order == 0)
        {
            return Stop(UpdateFailure.SameVersion, "the staged version is the installed version");
        }

        if (order < 0)
        {
            return Stop(UpdateFailure.Downgrade, "the staged version is older than the installed version");
        }

        try
        {
            var exited = await WaitForExitAsync(request.WaitPids, request.InstallDir, request.WaitTimeout, ct);
            if (!exited)
            {
                return Stop(UpdateFailure.ProcessesStillRunning, "apps are still running after the wait limit; nothing was installed");
            }

            return await VerifyAndRunAsync(request, offered, arguments.Value!, ct);
        }
        catch (OperationCanceledException)
        {
            Log.Warn("apply was canceled");
            return Stop(UpdateFailure.Canceled, "apply was canceled");
        }
    }

    private async Task<ApplyOutcome> VerifyAndRunAsync(ApplyRequest request, UpdateReceipt offered, string arguments, CancellationToken ct)
    {
        var keys = _options.TrustedPublicKeys;
        var expected = new InstallerExpectation(offered.Version, offered.Architecture, offered.Size, offered.Sha256, offered.Signature);

        // The layout can change during the wait (for example, a junction swapped in), so it is checked again here.
        var layout = InstallerPaths.Check(request.InstallerPath, request.ReceiptPath, request.StagingRoot);
        if (!layout.Ok)
        {
            return new ApplyOutcome(layout.Failure, layout.Detail, null, StartHub(request.InstallDir));
        }

        // The file stays open with FileShare.Read only. No other process can write or delete it from here
        // until this method returns, so the bytes checked below are the bytes started.
        using var file = OpenInstaller(request.InstallerPath);
        if (file is null)
        {
            return Stop(UpdateFailure.PathRejected, "installer cannot be opened for verification");
        }

        var verified = InstallerVerifier.Verify(file, expected, keys);
        if (!verified.Ok)
        {
            Log.Warn($"installer refused before launch: {verified.Failure}");
            return new ApplyOutcome(verified.Failure, verified.Detail, null, StartHub(request.InstallDir));
        }

        int? exitCode;
        try
        {
            exitCode = await _host.RunInstallerAsync(request.InstallerPath, arguments, _options.InstallerRunTimeout, ct);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception)
        {
            Log.Exception("installer did not start", ex);
            return new ApplyOutcome(UpdateFailure.InstallerFailed, "installer did not start: " + ex.GetType().Name, null, StartHub(request.InstallDir));
        }

        if (exitCode is null)
        {
            Log.Warn("installer passed its time limit; it was left running and the Hub was not started");
            return new ApplyOutcome(UpdateFailure.InstallerTimedOut, "installer did not finish within its time limit", null, false);
        }

        var hubLaunched = StartHub(request.InstallDir);
        if (exitCode != 0)
        {
            Log.Error($"installer exited with code {exitCode.Value}");
            return new ApplyOutcome(UpdateFailure.InstallerFailed, $"installer exited with code {exitCode.Value}", exitCode, hubLaunched);
        }

        if (!hubLaunched)
        {
            return new ApplyOutcome(UpdateFailure.HubMissing, "installed, but the Hub could not be started", 0, false);
        }

        Log.Info($"update installed: version={offered.Version} architecture={offered.Architecture}");
        return new ApplyOutcome(UpdateFailure.None, "installed", 0, true);
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

    private bool StartHub(string installDir)
    {
        try
        {
            return _host.StartDetached(Path.Combine(installDir, HubExecutableName), [], installDir);
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

    private static ApplyOutcome Stop(UpdateFailure failure, string detail) =>
        new(failure, detail, null, false);
}

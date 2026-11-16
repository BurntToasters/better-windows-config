using System.ComponentModel;
using Bwc.Core;

namespace Bwc.Update;

/// <summary>
/// <c>--apply-elevated &lt;staging folder&gt;</c>: the elevated half of an update. The medium helper
/// (<see cref="UpdateApplier"/>)
/// verified the installer and asked for elevation. This process does not trust that: the staging folder is under a folder
/// that medium-integrity processes can write. So it copies the installer into a protected folder next to the install dir
/// (<see cref="ProtectedStore"/>), verifies the copy, and starts the copy. Everything else is its own: the install dir is
/// own folder, the current version is its own version, and no switch comes from the caller.
///
/// Order: elevated token, own folder and version, staging folder shape and links, receipt, version order, protected root,
/// stale folders, bounded copy, verification of the copy, start. A refusal removes the new copy and starts the Hub again at
/// the shell's integrity level with the failure code. It writes no file log (<see cref="UpdateLogging"/>).
/// </summary>
public sealed class ElevatedApplier
{
    private readonly UpdateOptions _options;
    private readonly IUpdateProcessHost _host;
    private string? _copyFolder;
    private bool _installerLaunched;

    public ElevatedApplier(UpdateOptions options, IUpdateProcessHost host)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public ApplyOutcome Apply(ElevatedApplyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return ApplyCore(request);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Exception("unexpected error in the elevated helper", ex);
            if (_installerLaunched)
            {
                return new ApplyOutcome(UpdateFailure.Unexpected, "unexpected error after the installer started", InstallerStarted: true, HubLaunched: false);
            }

            return Refuse(request, UpdateFailure.Unexpected, "unexpected error: " + ex.GetType().Name);
        }
    }

    private ApplyOutcome ApplyCore(ElevatedApplyRequest request)
    {
        ChildEnvironment.RemoveCompatibilityLayer();

        // A root override is the E2E flavor's. A bad one is refused, and a good one stands in for the elevated token.
        string? overrideRoot = null;
        if (request.E2EProtectedRoot is not null)
        {
            var tempRoot = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(request.StagingRoot));
            if (!ProtectedRootRule.AcceptE2ERoot(request.E2EProtectedRoot, tempRoot, request.StagingRoot, out overrideRoot))
            {
                return Plain(UpdateFailure.InvalidOptions, "the E2E protected root is not a folder inside the temp folder");
            }
        }

        if (overrideRoot is null && !_host.IsElevated())
        {
            return Plain(UpdateFailure.NotElevated, "--apply-elevated needs an elevated token; nothing was done");
        }

        // The install dir is this helper's own folder. It must be real, and (ship) inside Program Files.
        var ownDirectory = request.OwnDirectory;
        var directory = InstallDirectory.Validate(ownDirectory);
        if (!directory.Ok)
        {
            return Refuse(request, directory.Failure, directory.Detail);
        }

        if (_options.ProgramFilesRoot is { } programFiles && !ProgramFilesRule.IsUnder(ownDirectory, programFiles))
        {
            return Refuse(request, UpdateFailure.InstallDirUnsafe, "the update helper does not run from Program Files");
        }

        if (!SemVer.TryParse(request.OwnVersion, out var own))
        {
            return Refuse(request, UpdateFailure.InvalidOptions, "the helper's own version is not a SemVer version");
        }

        if (!Architectures.IsKnown(request.Architecture))
        {
            return Refuse(request, UpdateFailure.InvalidOptions, "architecture is not supported");
        }

        var keyCheck = MinisignVerifier.ValidatePublicKeys(_options.TrustedPublicKeys);
        if (!keyCheck.Ok)
        {
            return Refuse(request, TrustedKeys.FailureFor(_options.TrustedPublicKeys), keyCheck.Failure);
        }

        var staged = InstallerPaths.CheckFolder(request.StagingFolder, request.StagingRoot);
        if (!staged.Ok)
        {
            return Refuse(request, staged.Failure, staged.Detail);
        }

        var stagingFolder = staged.Value!;
        if (InstallerPaths.HasLinkInside(stagingFolder))
        {
            return Refuse(request, UpdateFailure.PathRejected, "a link or junction is inside the staging folder");
        }

        var receiptPath = Path.Combine(stagingFolder, UpdateReceipt.FileName);
        var receipt = UpdateReceipt.Read(receiptPath);
        if (!receipt.Ok)
        {
            return Refuse(request, receipt.Failure, receipt.Detail);
        }

        var offered = receipt.Value!;
        if (offered.Architecture != request.Architecture)
        {
            return Refuse(request, UpdateFailure.ArchitectureMismatch, "the receipt is for another architecture");
        }

        // The receipt reader already accepted only canonical SemVer, so Parse cannot fail here.
        var order = SemVer.Parse(offered.Version).CompareTo(own);
        if (order == 0)
        {
            return Refuse(request, UpdateFailure.SameVersion, "the staged version is this helper's version");
        }

        if (order < 0)
        {
            return Refuse(request, UpdateFailure.Downgrade, "the staged version is older than this helper's version");
        }

        if (offered.Size > _options.MaxInstallerBytes)
        {
            return Refuse(request, UpdateFailure.InstallerTooLarge, "the receipt size is over the limit");
        }

        var root = ProtectedStore.EnsureRoot(overrideRoot ?? ProtectedStore.RootFor(ownDirectory));
        if (!root.Ok)
        {
            return Refuse(request, root.Failure, root.Detail);
        }

        _ = ProtectedStore.RemoveStale(root.Value!);
        var started = CopyVerifyAndStart(offered, stagingFolder, ownDirectory, root.Value!);
        return started.Ok
            ? new ApplyOutcome(UpdateFailure.None, "installer started", InstallerStarted: true, HubLaunched: false)
            : Refuse(request, started.Failure, started.Detail);
    }

    /// <summary>
    /// Copies the installer, verifies the copy, and starts it. Success means the installer started. A failure is returned
    /// without cleanup: the caller removes the copy folder once the file handles opened here are closed.
    /// </summary>
    private Outcome<string> CopyVerifyAndStart(UpdateReceipt offered, string stagingFolder, string ownDirectory, string root)
    {
        var expected = new InstallerExpectation(offered.Version, offered.Architecture, offered.Size, offered.Sha256, offered.Signature);
        var sourcePath = Path.Combine(stagingFolder, InstallerPaths.InstallerName);

        // The source stays open with FileShare.Read only while it is copied, so no writer can change it meanwhile.
        // Its final path must be installer.exe in the folder that was checked and is held open.
        _copyFolder = ProtectedStore.NewCopyFolder(root);
        var copyPath = Path.Combine(_copyFolder, InstallerPaths.InstallerName);
        using (var folder = InstallerPaths.OpenFolder(stagingFolder))
        {
            if (folder is null)
            {
                return Outcome<string>.Fail(UpdateFailure.PathRejected, "staging folder cannot be opened");
            }

            using var source = OpenForRead(sourcePath);
            if (source is null)
            {
                return Outcome<string>.Fail(UpdateFailure.PathRejected, "installer cannot be opened for the copy");
            }

            var finalPath = InstallerPaths.FinalPathOf(source.SafeFileHandle);
            if (finalPath is null || !InstallerPaths.InFolder(folder, finalPath))
            {
                return Outcome<string>.Fail(UpdateFailure.PathRejected, "installer is not in the folder that was checked");
            }

            var copied = ProtectedStore.CopyBounded(source, copyPath, offered.Size);
            if (!copied.Ok)
            {
                return Outcome<string>.Fail(copied.Failure, copied.Detail);
            }
        }

        // The copy is what gets verified and started. It stays open with FileShare.Read until the start.
        using var copy = OpenForRead(copyPath);
        if (copy is null)
        {
            return Outcome<string>.Fail(UpdateFailure.PathRejected, "the copy cannot be opened for verification");
        }

        var verified = InstallerVerifier.Verify(copy, expected, _options.TrustedPublicKeys);
        if (!verified.Ok)
        {
            return Outcome<string>.Fail(verified.Failure, verified.Detail);
        }

        var arguments = InstallerArguments.Build(ownDirectory, [], Environment.ProcessId);
        if (!arguments.Ok)
        {
            return Outcome<string>.Fail(arguments.Failure, arguments.Detail);
        }

        try
        {
            if (!_host.StartInstaller(copyPath, arguments.Value!))
            {
                return Outcome<string>.Fail(UpdateFailure.InstallerFailed, "installer did not start");
            }

            _installerLaunched = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception)
        {
            return Outcome<string>.Fail(UpdateFailure.InstallerFailed, "installer did not start: " + ex.GetType().Name);
        }

        return Outcome<string>.Success(copyPath);
    }

    /// <summary>A failure that does nothing else: no copy is removed and the Hub is not started.</summary>
    private static ApplyOutcome Plain(UpdateFailure failure, string detail) =>
        new(failure, detail, InstallerStarted: false, HubLaunched: false);

    /// <summary>
    /// Ends the apply without starting the installer: removes this run's copy folder (no handle of it is open here) and
    /// starts the Hub at the shell's integrity level with the failure code.
    /// </summary>
    private ApplyOutcome Refuse(ElevatedApplyRequest request, UpdateFailure failure, string detail)
    {
        ProtectedStore.RemoveFolder(_copyFolder);
        Log.Warn($"update not applied by the elevated helper: {failure}. {detail}");
        var launched = HubLauncher.Launch(_host, request.OwnDirectory, failure);
        var outcome = launched.Ok
            ? new ApplyOutcome(failure, detail, InstallerStarted: false, HubLaunched: true)
            : new ApplyOutcome(
                UpdateFailure.HubMissing,
                $"{failure}: {detail}; the Hub was not started ({launched.Failure})",
                InstallerStarted: false,
                HubLaunched: false);
        return outcome;
    }

    private static FileStream? OpenForRead(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

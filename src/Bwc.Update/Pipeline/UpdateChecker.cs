using System.Text;
using Bwc.Core;

namespace Bwc.Update;

/// <summary>What one check or download needs: where the manifest is, what is installed, and where to stage files.</summary>
public sealed record UpdateRequest(
    Uri ManifestUrl,
    Uri ManifestSignatureUrl,
    UpdateChannel Channel,
    string CurrentVersion,
    string Architecture,
    string StagingRoot);

/// <summary>What the manifest step found: the verified manifest offers this version, with these notes.</summary>
public sealed record CheckedUpdate(string Version, UpdateChannel Channel, Uri? NotesUrl);

/// <summary>Installer bytes written so far, out of the size the manifest states.</summary>
public sealed record DownloadProgress(long Received, long Total);

/// <summary>A verified installer, staged in its own folder, with the receipt that describes it.</summary>
public sealed record ReadyUpdate(string FolderPath, string InstallerPath, string ReceiptPath, UpdateReceipt Receipt, Uri? NotesUrl = null);

/// <summary>
/// Two steps. <see cref="CheckAsync"/> verifies the manifest only: its signature and trusted comment,
/// its body, its expiry, the channel, and the version policy. It needs no staging folder.
/// <see cref="DownloadAsync"/> runs the same manifest verification, then reuses an already verified
/// staged installer for the same version and SHA-256, or downloads and verifies a new one. A new
/// receipt is written only after every check passes, and then every other staging folder older than
/// 24 hours is pruned (a younger one may be another install's staged update).
/// A failed attempt leaves no staging folder behind.
/// </summary>
public sealed class UpdateChecker
{
    private const string InstallerName = InstallerPaths.InstallerName;

    private readonly UpdateOptions _options;

    public UpdateChecker(UpdateOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Verifies the manifest and reports the version it offers. Downloads nothing.</summary>
    public async Task<Outcome<CheckedUpdate>> CheckAsync(UpdateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var prepared = Prepare(request);
        if (!prepared.Ok)
        {
            return Outcome<CheckedUpdate>.Fail(prepared.Failure, prepared.Detail);
        }

        try
        {
            using var downloader = new UpdateDownloader(_options);
            var verified = await VerifyManifestAsync(downloader, request, prepared.Value!, ct);
            if (!verified.Ok)
            {
                return Outcome<CheckedUpdate>.Fail(verified.Failure, verified.Detail);
            }

            var manifest = verified.Value!.Manifest;
            return Outcome<CheckedUpdate>.Success(new CheckedUpdate(verified.Value.Version, request.Channel, manifest.NotesUrl));
        }
        catch (OperationCanceledException)
        {
            Log.Warn("update check was canceled");
            return Outcome<CheckedUpdate>.Fail(UpdateFailure.Canceled, "update check was canceled");
        }
    }

    /// <summary>
    /// Verifies the manifest, then makes sure a verified installer is staged: an existing one for the same
    /// version, channel, architecture, size, SHA-256, and signature is reused after re-verification.
    /// </summary>
    public async Task<Outcome<ReadyUpdate>> DownloadAsync(
        UpdateRequest request,
        CancellationToken ct = default,
        IProgress<DownloadProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var prepared = Prepare(request);
        if (!prepared.Ok)
        {
            return Outcome<ReadyUpdate>.Fail(prepared.Failure, prepared.Detail);
        }

        try
        {
            using var downloader = new UpdateDownloader(_options);
            var verified = await VerifyManifestAsync(downloader, request, prepared.Value!, ct);
            if (!verified.Ok)
            {
                return Outcome<ReadyUpdate>.Fail(verified.Failure, verified.Detail);
            }

            var reused = TryReuse(request, verified.Value!);
            if (reused is not null)
            {
                StagingCleanup.Prune(request.StagingRoot, reused.FolderPath);
                Log.Info($"update staged: version={verified.Value!.Version} reused a verified installer");
                return Outcome<ReadyUpdate>.Success(reused);
            }

            var staged = await StageAsync(downloader, request, verified.Value!, progress, ct);
            if (!staged.Ok)
            {
                return Outcome<ReadyUpdate>.Fail(staged.Failure, staged.Detail);
            }

            StagingCleanup.Prune(request.StagingRoot, staged.Value!.FolderPath);
            return staged;
        }
        catch (OperationCanceledException)
        {
            Log.Warn("update download was canceled");
            return Outcome<ReadyUpdate>.Fail(UpdateFailure.Canceled, "update download was canceled");
        }
    }

    private sealed record Prepared(SemVer Current, Uri ManifestUrl, Uri SignatureUrl);

    private sealed record Verified(string Version, UpdateManifest Manifest, InstallerEntry Installer);

    private Outcome<Prepared> Prepare(UpdateRequest request)
    {
        if (!SemVer.TryParse(request.CurrentVersion, out var current))
        {
            return Outcome<Prepared>.Fail(UpdateFailure.InvalidOptions, "installed version is not a SemVer version");
        }

        if (!Architectures.IsKnown(request.Architecture))
        {
            return Outcome<Prepared>.Fail(UpdateFailure.InvalidOptions, "architecture is not supported");
        }

        if (!Path.IsPathFullyQualified(request.StagingRoot))
        {
            return Outcome<Prepared>.Fail(UpdateFailure.InvalidOptions, "staging root must be an absolute path");
        }

        var keyCheck = MinisignVerifier.ValidatePublicKeys(_options.TrustedPublicKeys);
        if (!keyCheck.Ok)
        {
            return Outcome<Prepared>.Fail(TrustedKeys.FailureFor(_options.TrustedPublicKeys), keyCheck.Failure);
        }

        var manifestUrl = UrlPolicy.Validate(request.ManifestUrl.OriginalString, _options.RequireHttps);
        if (!manifestUrl.Ok)
        {
            return Outcome<Prepared>.Fail(UpdateFailure.UrlRejected, "manifest URL: " + manifestUrl.Detail);
        }

        var signatureUrl = UrlPolicy.Validate(request.ManifestSignatureUrl.OriginalString, _options.RequireHttps);
        if (!signatureUrl.Ok)
        {
            return Outcome<Prepared>.Fail(UpdateFailure.UrlRejected, "manifest signature URL: " + signatureUrl.Detail);
        }

        return Outcome<Prepared>.Success(new Prepared(current!, manifestUrl.Value!, signatureUrl.Value!));
    }

    // The manifest, its signature, the signed trusted comment, the body, the expiry, the channel, and the version policy.
    private async Task<Outcome<Verified>> VerifyManifestAsync(
        UpdateDownloader downloader,
        UpdateRequest request,
        Prepared prepared,
        CancellationToken ct)
    {
        var keys = _options.TrustedPublicKeys;
        Log.Info($"update check: channel={UpdateChannelNames.ToText(request.Channel)} manifest={Describe(prepared.ManifestUrl)}");

        var manifestBytes = await downloader.ReadAllAsync(prepared.ManifestUrl, DownloadStage.Manifest, _options.MaxManifestBytes, ct);
        if (!manifestBytes.Ok)
        {
            return Outcome<Verified>.Fail(manifestBytes.Failure, manifestBytes.Detail);
        }

        var signatureBytes = await downloader.ReadAllAsync(prepared.SignatureUrl, DownloadStage.ManifestSignature, _options.MaxSignatureBytes, ct);
        if (!signatureBytes.Ok)
        {
            return Outcome<Verified>.Fail(signatureBytes.Failure, signatureBytes.Detail);
        }

        // The manifest is not parsed until its signature verifies.
        var signed = MinisignVerifier.Verify(
            new MemoryStream(manifestBytes.Value!),
            Encoding.UTF8.GetString(signatureBytes.Value!),
            keys);
        if (!signed.Ok)
        {
            return Outcome<Verified>.Fail(UpdateFailure.ManifestSignatureInvalid, signed.Failure);
        }

        var binding = ManifestComment.Parse(signed.TrustedComment!);
        if (!binding.Ok)
        {
            return Outcome<Verified>.Fail(binding.Failure, binding.Detail);
        }

        var manifest = UpdateManifestParser.Parse(manifestBytes.Value!, _options);
        if (!manifest.Ok)
        {
            return Outcome<Verified>.Fail(manifest.Failure, manifest.Detail);
        }

        var offered = manifest.Value!.Version.ToString();
        if (binding.Value!.Version != offered)
        {
            return Outcome<Verified>.Fail(UpdateFailure.ManifestInvalid, "signed version does not match the manifest version");
        }

        if (binding.Value.Channel != manifest.Value.Channel)
        {
            return Outcome<Verified>.Fail(UpdateFailure.ManifestInvalid, "signed channel does not match the manifest channel");
        }

        if (_options.Clock.GetUtcNow() >= binding.Value.Expires)
        {
            return Outcome<Verified>.Fail(UpdateFailure.ManifestExpired, "the signed manifest has expired; publish a new one");
        }

        var entry = UpdatePolicy.SelectInstaller(manifest.Value, request.Channel, prepared.Current, request.Architecture);
        if (!entry.Ok)
        {
            return Outcome<Verified>.Fail(entry.Failure, entry.Detail);
        }

        return Outcome<Verified>.Success(new Verified(offered, manifest.Value, entry.Value!));
    }

    // A staged folder whose receipt names the same release, and whose installer still verifies, is reused.
    // A folder that fails verification is discarded, and so is one that cannot be read right now.
    private ReadyUpdate? TryReuse(UpdateRequest request, Verified verified)
    {
        if (!Directory.Exists(request.StagingRoot))
        {
            return null;
        }

        var installer = verified.Installer;
        List<string> folders;
        try
        {
            folders = Directory.EnumerateDirectories(request.StagingRoot).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"staging root cannot be listed for reuse: {ex.GetType().Name}");
            return null;
        }

        foreach (var folder in folders)
        {
            if (!StagingCleanup.IsStagingFolderName(Path.GetFileName(folder)))
            {
                continue;
            }

            var receiptPath = Path.Combine(folder, UpdateReceipt.FileName);
            var installerPath = Path.Combine(folder, InstallerName);
            if (!File.Exists(receiptPath) || !File.Exists(installerPath))
            {
                continue;
            }

            var read = UpdateReceipt.Read(receiptPath);
            if (!read.Ok)
            {
                continue;
            }

            var receipt = read.Value!;
            if (receipt.Version != verified.Version
                || receipt.Channel != request.Channel
                || receipt.Architecture != request.Architecture
                || receipt.Size != installer.Size
                || receipt.Sha256 != installer.Sha256
                || receipt.Signature != installer.Signature)
            {
                continue;
            }

            var expected = new InstallerExpectation(verified.Version, request.Architecture, installer.Size, installer.Sha256, installer.Signature);
            Outcome<string> check;
            try
            {
                using var file = File.Open(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                check = InstallerVerifier.Verify(file, expected, _options.TrustedPublicKeys);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Held or unreadable right now: not reused, and not discarded, because nothing shows it was changed.
                Log.Warn($"staged installer cannot be read for reuse: {ex.GetType().Name}");
                continue;
            }

            if (check.Failure == UpdateFailure.Io)
            {
                continue;
            }

            if (!check.Ok)
            {
                Log.Warn($"staged installer no longer verifies and is discarded: {check.Failure}");
                Discard(folder);
                continue;
            }

            return new ReadyUpdate(folder, installerPath, receiptPath, receipt, verified.Manifest.NotesUrl);
        }

        return null;
    }

    private async Task<Outcome<ReadyUpdate>> StageAsync(
        UpdateDownloader downloader,
        UpdateRequest request,
        Verified verified,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        var installer = verified.Installer;
        var folder = Path.Combine(request.StagingRoot, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Outcome<ReadyUpdate>.Fail(UpdateFailure.Io, $"cannot create the staging folder: {ex.GetType().Name}");
        }

        var installerPath = Path.Combine(folder, InstallerName);
        var receiptPath = Path.Combine(folder, UpdateReceipt.FileName);

        Outcome<long> download;
        try
        {
            download = await downloader.DownloadToFileAsync(installer.Url, installer.Size, installerPath, ct, progress);
        }
        catch (OperationCanceledException)
        {
            Discard(folder);
            throw;
        }

        if (!download.Ok)
        {
            Discard(folder);
            return Outcome<ReadyUpdate>.Fail(download.Failure, download.Detail);
        }

        Outcome<string> check;
        try
        {
            using var file = File.Open(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            check = InstallerVerifier.Verify(
                file,
                new InstallerExpectation(verified.Version, request.Architecture, installer.Size, installer.Sha256, installer.Signature),
                _options.TrustedPublicKeys);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Discard(folder);
            return Outcome<ReadyUpdate>.Fail(UpdateFailure.Io, $"cannot open the downloaded installer: {ex.GetType().Name}");
        }

        if (!check.Ok)
        {
            Discard(folder);
            return Outcome<ReadyUpdate>.Fail(check.Failure, check.Detail);
        }

        var receipt = new UpdateReceipt(
            verified.Version,
            request.Channel,
            request.Architecture,
            installer.Size,
            installer.Sha256,
            installer.Signature,
            check.Value!);
        try
        {
            UpdateReceipt.Write(receiptPath, receipt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Discard(folder);
            return Outcome<ReadyUpdate>.Fail(UpdateFailure.Io, $"cannot write the receipt: {ex.GetType().Name}");
        }

        Log.Info($"update ready: version={verified.Version} architecture={request.Architecture} size={installer.Size}");
        return Outcome<ReadyUpdate>.Success(new ReadyUpdate(folder, installerPath, receiptPath, receipt, verified.Manifest.NotesUrl));
    }

    private static void Discard(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"could not remove staging folder: {ex.GetType().Name}");
        }
    }

    // Host and path only. Query strings can carry tokens, so they never reach the log.
    private static string Describe(Uri uri) => uri.GetLeftPart(UriPartial.Path);
}

using System.Text;
using Bwc.Core;

namespace Bwc.Update;

/// <summary>What one check needs: where the manifest is, what is installed, and where to stage files.</summary>
public sealed record UpdateRequest(
    Uri ManifestUrl,
    Uri ManifestSignatureUrl,
    UpdateChannel Channel,
    string CurrentVersion,
    string Architecture,
    string StagingRoot);

/// <summary>A verified installer, staged in its own folder, with the receipt that describes it.</summary>
public sealed record ReadyUpdate(string FolderPath, string InstallerPath, string ReceiptPath, UpdateReceipt Receipt);

/// <summary>
/// The check pipeline: manifest and signature, manifest signature, parse, the signed version
/// binding, channel and version policy, installer download, installer verification, receipt.
/// Nothing is reported ready until the installer's own signature has verified. A failed check
/// leaves no staged files.
/// </summary>
public sealed class UpdateChecker
{
    private readonly UpdateOptions _options;

    public UpdateChecker(UpdateOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<Outcome<ReadyUpdate>> CheckAsync(UpdateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!SemVer.TryParse(request.CurrentVersion, out var current))
        {
            return Outcome<ReadyUpdate>.Fail(UpdateFailure.InvalidOptions, "installed version is not a SemVer version");
        }

        if (!Architectures.IsKnown(request.Architecture))
        {
            return Outcome<ReadyUpdate>.Fail(UpdateFailure.InvalidOptions, "architecture is not supported");
        }

        if (!Path.IsPathFullyQualified(request.StagingRoot))
        {
            return Outcome<ReadyUpdate>.Fail(UpdateFailure.InvalidOptions, "staging root must be an absolute path");
        }

        var keyCheck = MinisignVerifier.ValidatePublicKeys(_options.TrustedPublicKeys);
        if (!keyCheck.Ok)
        {
            return Outcome<ReadyUpdate>.Fail(UpdateFailure.TrustedKeysInvalid, keyCheck.Failure);
        }

        var manifestUrl = UrlPolicy.Validate(request.ManifestUrl.OriginalString, _options.RequireHttps);
        if (!manifestUrl.Ok)
        {
            return Outcome<ReadyUpdate>.Fail(UpdateFailure.UrlRejected, "manifest URL: " + manifestUrl.Detail);
        }

        var signatureUrl = UrlPolicy.Validate(request.ManifestSignatureUrl.OriginalString, _options.RequireHttps);
        if (!signatureUrl.Ok)
        {
            return Outcome<ReadyUpdate>.Fail(UpdateFailure.UrlRejected, "manifest signature URL: " + signatureUrl.Detail);
        }

        Log.Info($"update check: channel={UpdateChannelNames.ToText(request.Channel)} manifest={Describe(manifestUrl.Value!)}");
        try
        {
            return await CheckCoreAsync(request, current!, manifestUrl.Value!, signatureUrl.Value!, ct);
        }
        catch (OperationCanceledException)
        {
            Log.Warn("update check was canceled");
            return Outcome<ReadyUpdate>.Fail(UpdateFailure.Canceled, "update check was canceled");
        }
    }

    private async Task<Outcome<ReadyUpdate>> CheckCoreAsync(
        UpdateRequest request,
        SemVer current,
        Uri manifestUrl,
        Uri signatureUrl,
        CancellationToken ct)
    {
        var keys = _options.TrustedPublicKeys;
        using var downloader = new UpdateDownloader(_options);

        var manifestBytes = await downloader.ReadAllAsync(manifestUrl, DownloadStage.Manifest, _options.MaxManifestBytes, ct);
        if (!manifestBytes.Ok)
        {
            return Outcome<ReadyUpdate>.Fail(manifestBytes.Failure, manifestBytes.Detail);
        }

        var signatureBytes = await downloader.ReadAllAsync(signatureUrl, DownloadStage.ManifestSignature, _options.MaxSignatureBytes, ct);
        if (!signatureBytes.Ok)
        {
            return Outcome<ReadyUpdate>.Fail(signatureBytes.Failure, signatureBytes.Detail);
        }

        // The manifest is not parsed until its signature verifies.
        var signed = MinisignVerifier.Verify(
            new MemoryStream(manifestBytes.Value!),
            Encoding.UTF8.GetString(signatureBytes.Value!),
            keys);
        if (!signed.Ok)
        {
            return Outcome<ReadyUpdate>.Fail(UpdateFailure.ManifestSignatureInvalid, signed.Failure);
        }

        if (!TrustedComment.TryParseFields(signed.TrustedComment!, out var fields, out var reason))
        {
            return Outcome<ReadyUpdate>.Fail(UpdateFailure.ManifestInvalid, reason);
        }

        var manifest = UpdateManifestParser.Parse(manifestBytes.Value!, _options);
        if (!manifest.Ok)
        {
            return Outcome<ReadyUpdate>.Fail(manifest.Failure, manifest.Detail);
        }

        var offered = manifest.Value!.Version.ToString();
        if (!fields.TryGetValue("version", out var signedVersion) || signedVersion != offered)
        {
            return Outcome<ReadyUpdate>.Fail(UpdateFailure.ManifestInvalid, "signed version does not match the manifest version");
        }

        var entry = UpdatePolicy.SelectInstaller(manifest.Value, request.Channel, current, request.Architecture);
        if (!entry.Ok)
        {
            return Outcome<ReadyUpdate>.Fail(entry.Failure, entry.Detail);
        }

        var folder = Path.Combine(request.StagingRoot, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Outcome<ReadyUpdate>.Fail(UpdateFailure.Io, $"cannot create the staging folder: {ex.GetType().Name}");
        }

        var installerPath = Path.Combine(folder, "installer.exe");
        var receiptPath = Path.Combine(folder, UpdateReceipt.FileName);
        var installer = entry.Value!;

        Outcome<long> download;
        try
        {
            download = await downloader.DownloadToFileAsync(installer.Url, installer.Size, installerPath, ct);
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

        Outcome<string> verified;
        using (var file = File.Open(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            verified = InstallerVerifier.Verify(
                file,
                new InstallerExpectation(offered, request.Architecture, installer.Size, installer.Sha256, installer.Signature),
                keys);
        }

        if (!verified.Ok)
        {
            Discard(folder);
            return Outcome<ReadyUpdate>.Fail(verified.Failure, verified.Detail);
        }

        var receipt = new UpdateReceipt(
            offered,
            request.Channel,
            request.Architecture,
            installer.Size,
            installer.Sha256,
            installer.Signature,
            verified.Value!);
        try
        {
            UpdateReceipt.Write(receiptPath, receipt);
        }
        catch (IOException ex)
        {
            Discard(folder);
            return Outcome<ReadyUpdate>.Fail(UpdateFailure.Io, $"cannot write the receipt: {ex.GetType().Name}");
        }

        Log.Info($"update ready: version={offered} architecture={request.Architecture} size={installer.Size}");
        return Outcome<ReadyUpdate>.Success(new ReadyUpdate(folder, installerPath, receiptPath, receipt));
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

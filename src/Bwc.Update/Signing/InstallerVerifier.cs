using System.Security.Cryptography;

namespace Bwc.Update;

/// <summary>What a receipt or manifest entry says an installer must be.</summary>
public sealed record InstallerExpectation(string Version, string Architecture, long Size, string Sha256, string Signature);

/// <summary>
/// Checks an installer file against its manifest entry, in this order: size, SHA-256, minisign
/// signature, then the signed trusted comment (version, architecture, and a plain file name).
/// Used by the check (after download) and by the helper (right before launch).
/// </summary>
public static class InstallerVerifier
{
    /// <summary>
    /// Verifies the bytes from the current position of <paramref name="file"/> to its end. The
    /// caller keeps the stream open for as long as the verified bytes must stay unchanged.
    /// </summary>
    public static Outcome<string> Verify(Stream file, InstallerExpectation expected, IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(expected);

        if (file.Length != expected.Size)
        {
            return Outcome<string>.Fail(
                UpdateFailure.InstallerSizeMismatch,
                $"installer is {file.Length} bytes, expected {expected.Size}");
        }

        file.Position = 0;
        var digest = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
        if (!string.Equals(digest, expected.Sha256, StringComparison.Ordinal))
        {
            return Outcome<string>.Fail(UpdateFailure.InstallerHashMismatch, "installer SHA-256 does not match the manifest");
        }

        file.Position = 0;
        var signed = MinisignVerifier.Verify(file, expected.Signature, keys);
        if (!signed.Ok)
        {
            return Outcome<string>.Fail(UpdateFailure.InstallerSignatureInvalid, signed.Failure);
        }

        var comment = signed.TrustedComment!;
        if (!TrustedComment.TryParseFields(comment, out var fields, out var reason))
        {
            return Outcome<string>.Fail(UpdateFailure.InstallerCommentMismatch, reason);
        }

        if (!fields.TryGetValue("version", out var version) || version != expected.Version)
        {
            return Outcome<string>.Fail(UpdateFailure.InstallerCommentMismatch, "signed version does not match the manifest");
        }

        if (!fields.TryGetValue("arch", out var architecture) || architecture != expected.Architecture)
        {
            return Outcome<string>.Fail(UpdateFailure.InstallerCommentMismatch, "signed architecture does not match the manifest");
        }

        if (fields.TryGetValue("file", out var fileName) && !TrustedComment.IsPlainFileName(fileName))
        {
            return Outcome<string>.Fail(UpdateFailure.InstallerCommentMismatch, "signed file name is not a plain file name");
        }

        return Outcome<string>.Success(comment);
    }
}

using System.Text;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Bwc.Spike.Minisign;

/// <summary>Outcome of a verification. Ok is false with a non-empty Failure reason when rejected.</summary>
public sealed record VerificationResult(bool Ok, string? TrustedComment, string Failure);

/// <summary>
/// Verify-only minisign for the Tauri updater envelope (see README.md and FAILURE-MODES.md).
/// Signatures are prehashed ("ED": Ed25519 over BLAKE2b-512 of the file). Legacy "Ed" signatures are
/// refused unless allowLegacy is true. Bad signature text, keys or file contents never throw: they
/// return Ok = false with a reason. Null arguments throw ArgumentNullException (programmer error).
/// </summary>
public static class MinisignVerifier
{
    private const int KeyIdLength = 8;
    private const int PublicKeyLength = 32;
    private const int SignatureLength = 64;
    private const int PublicKeyPacketLength = 2 + KeyIdLength + PublicKeyLength;   // 42
    private const int SignaturePacketLength = 2 + KeyIdLength + SignatureLength;   // 74
    private const int GlobalSignatureLength = SignatureLength;                     // 64
    private const int ReadBufferSize = 64 * 1024;

    private const string UntrustedPrefix = "untrusted comment:";
    private const string TrustedPrefix = "trusted comment: ";

    private static readonly byte[] UntrustedPrefixBytes = Encoding.ASCII.GetBytes(UntrustedPrefix);
    private static readonly byte[] TrustedPrefixBytes = Encoding.ASCII.GetBytes(TrustedPrefix);
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Verifies a file read from <paramref name="file"/>, from its current position to end of stream.
    /// The stream is read once and is not seeked or disposed.
    /// </summary>
    /// <param name="signature">Raw minisign signature text, or the base64 envelope of it (Tauri form).</param>
    /// <param name="trustedPublicKeys">Minisign public key text, or its base64 (Tauri form). Any one may match.</param>
    /// <param name="allowLegacy">Accept legacy "Ed" (non-prehashed) signatures. Buffers the whole file in memory.</param>
    public static VerificationResult Verify(
        Stream file,
        string signature,
        IReadOnlyList<string> trustedPublicKeys,
        bool allowLegacy = false)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(trustedPublicKeys);

        try
        {
            return VerifyCore(file, signature, trustedPublicKeys, allowLegacy);
        }
        catch (MinisignRefusedException e)
        {
            return new VerificationResult(false, null, e.Message);
        }
        catch (Exception e) when (e is IOException or NotSupportedException or ObjectDisposedException)
        {
            return new VerificationResult(false, null, $"cannot read file: {e.GetType().Name}");
        }
    }

    /// <summary>Opens <paramref name="path"/> read-only and calls <see cref="Verify"/>.</summary>
    public static VerificationResult VerifyFile(
        string path,
        string signature,
        IReadOnlyList<string> trustedPublicKeys,
        bool allowLegacy = false)
    {
        ArgumentNullException.ThrowIfNull(path);

        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ReadBufferSize, FileOptions.SequentialScan);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new VerificationResult(false, null, $"cannot read file: {e.GetType().Name}");
        }

        using (stream)
        {
            return Verify(stream, signature, trustedPublicKeys, allowLegacy);
        }
    }

    private static VerificationResult VerifyCore(
        Stream file,
        string signature,
        IReadOnlyList<string> trustedPublicKeys,
        bool allowLegacy)
    {
        if (trustedPublicKeys.Count == 0)
        {
            throw new MinisignRefusedException("no trusted public keys configured");
        }

        // Fail closed: one malformed trusted key rejects the whole call, even if another key is valid.
        var keys = new List<TrustedKey>(trustedPublicKeys.Count);
        for (var i = 0; i < trustedPublicKeys.Count; i++)
        {
            try
            {
                keys.Add(ParsePublicKey(trustedPublicKeys[i]));
            }
            catch (MinisignRefusedException e)
            {
                throw new MinisignRefusedException($"trusted key {i} is malformed: {e.Message}");
            }
        }

        var parsed = ParseSignature(signature, allowLegacy);

        // The key id only selects candidates. The signature itself must verify under a trusted key.
        var candidates = keys.Where(k => k.KeyId.AsSpan().SequenceEqual(parsed.KeyId)).ToList();
        if (candidates.Count == 0)
        {
            throw new MinisignRefusedException(
                $"signature key id {Convert.ToHexString(parsed.KeyId)} does not match any trusted key");
        }

        // Global signature first: it covers the signature bytes and the trusted comment, and needs no file read.
        var globalSigners = candidates
            .Where(k => VerifyEd25519(k.Key, Concat(parsed.Signature, parsed.TrustedCommentBytes), parsed.GlobalSignature))
            .ToList();
        if (globalSigners.Count == 0)
        {
            throw new MinisignRefusedException(
                "global signature does not verify: the trusted comment or signature was altered, or the key is not trusted");
        }

        byte[]? message = null;
        foreach (var key in globalSigners)
        {
            message ??= ReadSignedMessage(file, parsed.Legacy);
            if (VerifyEd25519(key.Key, message, parsed.Signature))
            {
                return new VerificationResult(true, parsed.TrustedComment, string.Empty);
            }
        }

        throw new MinisignRefusedException(
            "the file does not match the signature (altered file, or a signature for a different file)");
    }

    private static byte[] ReadSignedMessage(Stream file, bool legacy)
    {
        if (!file.CanRead)
        {
            throw new MinisignRefusedException("file stream is not readable");
        }

        if (!legacy)
        {
            return Blake2b512(file);
        }

        // Legacy Ed25519 signs the raw file. Verification needs the whole message, so it is buffered.
        using var copy = new MemoryStream();
        file.CopyTo(copy);
        return copy.ToArray();
    }

    private static byte[] Blake2b512(Stream file)
    {
        var digest = new Blake2bDigest(512);
        var buffer = new byte[ReadBufferSize];
        int read;
        while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
        {
            digest.BlockUpdate(buffer, 0, read);
        }

        var output = new byte[digest.GetDigestSize()];
        digest.DoFinal(output, 0);
        return output;
    }

    private static bool VerifyEd25519(Ed25519PublicKeyParameters key, byte[] message, byte[] signature)
    {
        var signer = new Ed25519Signer();
        signer.Init(false, key);
        signer.BlockUpdate(message, 0, message.Length);
        return signer.VerifySignature(signature);
    }

    private static TrustedKey ParsePublicKey(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = DocumentLines(Document(text, "public key"));
        if (lines.Count != 2)
        {
            throw new MinisignRefusedException($"public key must have 2 lines, found {lines.Count}");
        }

        var packet = DecodeCanonicalBase64(Latin1(lines[1]), "public key packet");
        if (packet.Length != PublicKeyPacketLength)
        {
            throw new MinisignRefusedException($"public key packet must be {PublicKeyPacketLength} bytes, got {packet.Length}");
        }

        if (packet[0] != (byte)'E' || packet[1] != (byte)'d')
        {
            throw new MinisignRefusedException("public key algorithm must be Ed");
        }

        var keyId = packet[2..10];
        var publicKey = packet[10..42];
        if (!Ed25519.ValidatePublicKeyFull(publicKey, 0))
        {
            throw new MinisignRefusedException("public key is not a valid Ed25519 point");
        }

        return new TrustedKey(keyId, new Ed25519PublicKeyParameters(publicKey, 0));
    }

    private static ParsedSignature ParseSignature(string signature, bool allowLegacy)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            throw new MinisignRefusedException("signature is empty");
        }

        var lines = DocumentLines(Document(signature, "signature"));
        if (lines.Count != 4)
        {
            throw new MinisignRefusedException($"signature must have 4 lines, found {lines.Count}");
        }

        var packet = DecodeCanonicalBase64(Latin1(lines[1]), "signature packet");
        if (packet.Length != SignaturePacketLength)
        {
            throw new MinisignRefusedException($"signature packet must be {SignaturePacketLength} bytes, got {packet.Length}");
        }

        bool legacy;
        if (packet[0] == (byte)'E' && packet[1] == (byte)'D')
        {
            legacy = false;
        }
        else if (packet[0] == (byte)'E' && packet[1] == (byte)'d')
        {
            legacy = true;
            if (!allowLegacy)
            {
                throw new MinisignRefusedException("legacy Ed signatures are refused; allowLegacy must be set to accept them");
            }
        }
        else
        {
            throw new MinisignRefusedException("unsupported signature algorithm (expected ED or Ed)");
        }

        if (!lines[2].AsSpan().StartsWith(TrustedPrefixBytes))
        {
            throw new MinisignRefusedException("signature line 3 must start with 'trusted comment: '");
        }

        var commentBytes = lines[2].AsSpan(TrustedPrefixBytes.Length).ToArray();
        var global = DecodeCanonicalBase64(Latin1(lines[3]), "global signature");
        if (global.Length != GlobalSignatureLength)
        {
            throw new MinisignRefusedException($"global signature must be {GlobalSignatureLength} bytes, got {global.Length}");
        }

        return new ParsedSignature(
            Legacy: legacy,
            KeyId: packet[2..10],
            Signature: packet[10..74],
            TrustedCommentBytes: commentBytes,
            TrustedComment: DecodeTrustedComment(commentBytes),
            GlobalSignature: global);
    }

    // Accepts raw minisign text, or base64 of it. Exactly one trailing newline is allowed in the base64 form.
    private static byte[] Document(string input, string what)
    {
        var bytes = input.StartsWith(UntrustedPrefix, StringComparison.Ordinal)
            ? Encoding.UTF8.GetBytes(input)
            : DecodeCanonicalBase64(TrimOneTrailingNewline(input), what);

        if (!bytes.AsSpan().StartsWith(UntrustedPrefixBytes))
        {
            throw new MinisignRefusedException($"{what} is not a minisign document (expected 'untrusted comment:')");
        }

        return bytes;
    }

    // Splits on LF. One CR directly before an LF is removed, so CRLF text verifies. A CR anywhere else is refused.
    // Exactly one final newline is optional. Any other trailing line counts as a line.
    private static List<byte[]> DocumentLines(byte[] document)
    {
        var end = document.Length;
        if (end >= 2 && document[end - 2] == '\r' && document[end - 1] == '\n')
        {
            end -= 2;
        }
        else if (end >= 1 && document[end - 1] == '\n')
        {
            end -= 1;
        }

        var lines = new List<byte[]>();
        var start = 0;
        for (var i = 0; i < end; i++)
        {
            if (document[i] == '\n')
            {
                lines.Add(Segment(document, start, i, terminated: true));
                start = i + 1;
            }
        }

        lines.Add(Segment(document, start, end, terminated: false));
        return lines;
    }

    private static byte[] Segment(byte[] document, int start, int end, bool terminated)
    {
        var length = end - start;
        if (terminated && length > 0 && document[end - 1] == '\r')
        {
            length--;
        }

        var segment = document.AsSpan(start, length).ToArray();
        if (Array.IndexOf(segment, (byte)'\r') >= 0)
        {
            throw new MinisignRefusedException("stray carriage return inside a line");
        }

        return segment;
    }

    // Strict base64: standard alphabet, length multiple of 4, no whitespace, and the canonical form only
    // (unused padding bits must be zero). Convert.FromBase64String alone is too lenient for this.
    private static byte[] DecodeCanonicalBase64(string text, string what)
    {
        if (text.Length == 0)
        {
            throw new MinisignRefusedException($"{what} is empty");
        }

        if (text.Length % 4 != 0)
        {
            throw new MinisignRefusedException($"{what} is not base64 (length is not a multiple of 4)");
        }

        foreach (var c in text)
        {
            var valid = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '+' or '/' or '=';
            if (!valid)
            {
                throw new MinisignRefusedException($"{what} contains a character outside base64");
            }
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            throw new MinisignRefusedException($"{what} is not valid base64");
        }

        if (!string.Equals(Convert.ToBase64String(bytes), text, StringComparison.Ordinal))
        {
            throw new MinisignRefusedException($"{what} is not canonical base64");
        }

        return bytes;
    }

    private static string DecodeTrustedComment(byte[] bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new MinisignRefusedException("trusted comment is not valid UTF-8");
        }
    }

    private static string TrimOneTrailingNewline(string text)
    {
        if (text.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return text[..^2];
        }

        return text.EndsWith('\n') ? text[..^1] : text;
    }

    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static byte[] Concat(byte[] first, byte[] second)
    {
        var result = new byte[first.Length + second.Length];
        first.CopyTo(result, 0);
        second.CopyTo(result, first.Length);
        return result;
    }

    private sealed record TrustedKey(byte[] KeyId, Ed25519PublicKeyParameters Key);

    private sealed record ParsedSignature(
        bool Legacy,
        byte[] KeyId,
        byte[] Signature,
        byte[] TrustedCommentBytes,
        string TrustedComment,
        byte[] GlobalSignature);

    // Internal control flow: every refusal reason becomes VerificationResult.Failure at the public boundary.
    private sealed class MinisignRefusedException(string reason) : Exception(reason);
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Bwc.Update;

/// <summary>
/// What the check reports as ready, written as receipt.json beside the installer. The helper
/// reads it and verifies the installer again; the receipt itself is not trusted.
/// </summary>
public sealed partial record UpdateReceipt(
    string Version,
    UpdateChannel Channel,
    string Architecture,
    long Size,
    string Sha256,
    string Signature,
    string TrustedComment)
{
    public const int Schema = 1;
    public const long MaxBytes = 16 * 1024;
    public const string FileName = "receipt.json";

    private const int MaxCommentLength = 1024;

    [GeneratedRegex("^[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    public static void Write(string path, UpdateReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var json = new JsonObject
        {
            ["schema"] = Schema,
            ["version"] = receipt.Version,
            ["channel"] = UpdateChannelNames.ToText(receipt.Channel),
            ["architecture"] = receipt.Architecture,
            ["size"] = receipt.Size,
            ["sha256"] = receipt.Sha256,
            ["signature"] = receipt.Signature,
            ["trustedComment"] = receipt.TrustedComment,
        };
        // Written beside the target, then moved over it: a reader sees the old receipt or the new one, never a cut one.
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, json.ToJsonString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    public static Outcome<UpdateReceipt> Read(string path)
    {
        if (!File.Exists(path))
        {
            return Refuse("receipt is missing");
        }

        byte[] bytes;
        try
        {
            if (new FileInfo(path).Length > MaxBytes)
            {
                return Refuse("receipt is larger than the limit");
            }

            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refuse("receipt cannot be read: " + ex.GetType().Name);
        }

        var parsed = StrictJson.Parse(bytes);
        if (!parsed.Ok)
        {
            return Refuse("receipt is not strict JSON");
        }

        using var document = parsed.Value!;
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("schema", out var schema)
            || schema.ValueKind != JsonValueKind.Number
            || !schema.TryGetInt32(out var schemaValue)
            || schemaValue != Schema)
        {
            return Refuse("receipt schema is not supported");
        }

        var version = Text(root, "version");
        if (version is null || !SemVer.TryParse(version, out var parsedVersion) || parsedVersion!.ToString() != version)
        {
            return Refuse("receipt version is not canonical SemVer");
        }

        var channelText = Text(root, "channel");
        if (channelText is null || !UpdateChannelNames.TryParse(channelText, out var channel))
        {
            return Refuse("receipt channel is not stable or beta");
        }

        var architecture = Text(root, "architecture");
        if (!Architectures.IsKnown(architecture))
        {
            return Refuse("receipt architecture is not supported");
        }

        if (!root.TryGetProperty("size", out var sizeElement) || sizeElement.ValueKind != JsonValueKind.Number || !sizeElement.TryGetInt64(out var size) || size < 1 || size > (1L << 30))
        {
            return Refuse("receipt size is out of range");
        }

        var sha = Text(root, "sha256");
        if (sha is null || !Sha256Pattern().IsMatch(sha))
        {
            return Refuse("receipt sha256 is not 64 lowercase hex characters");
        }

        var signature = Text(root, "signature");
        if (signature is null || !StrictJson.TryDecodeCanonicalBase64(signature, out var signatureBytes)
            || !Encoding.ASCII.GetString(signatureBytes).StartsWith("untrusted comment:", StringComparison.Ordinal))
        {
            return Refuse("receipt signature is not a minisign envelope");
        }

        var comment = Text(root, "trustedComment");
        if (comment is null || comment.Length > MaxCommentLength)
        {
            return Refuse("receipt trusted comment is missing or too long");
        }

        return Outcome<UpdateReceipt>.Success(new UpdateReceipt(version, channel, architecture!, size, sha, signature, comment));
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static Outcome<UpdateReceipt> Refuse(string detail) =>
        Outcome<UpdateReceipt>.Fail(UpdateFailure.ReceiptInvalid, detail);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The temp file is harmless to a reader, which only looks at receipt.json.
        }
    }
}

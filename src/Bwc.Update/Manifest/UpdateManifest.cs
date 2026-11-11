using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bwc.Update;

/// <summary>One installer for one architecture, as the manifest states it.</summary>
public sealed record InstallerEntry(Uri Url, long Size, string Sha256, string Signature);

/// <summary>A parsed, structurally valid manifest. Its signature has already been verified.</summary>
public sealed record UpdateManifest(
    SemVer Version,
    UpdateChannel Channel,
    DateTimeOffset PubDate,
    Uri? NotesUrl,
    IReadOnlyDictionary<string, InstallerEntry> Installers);

/// <summary>
/// Parses the manifest body (schema 1). Call only after the signature has verified: parsing
/// untrusted bytes is never the first step. Unknown properties and unknown architecture keys are
/// ignored. Known fields must have the exact JSON type and range.
/// </summary>
public static partial class UpdateManifestParser
{
    private const int CurrentSchema = 1;
    private const string SignatureLead = "untrusted comment:";

    [GeneratedRegex("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\\.[0-9]{3})?Z\\z", RegexOptions.CultureInvariant)]
    private static partial Regex PubDatePattern();

    [GeneratedRegex("^[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    public static Outcome<UpdateManifest> Parse(byte[] bytes, UpdateOptions options)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(options);

        if (bytes.Length > options.MaxManifestBytes)
        {
            return Outcome<UpdateManifest>.Fail(UpdateFailure.ManifestTooLarge, "manifest is larger than the limit");
        }

        var parsed = StrictJson.Parse(bytes);
        if (!parsed.Ok)
        {
            return Outcome<UpdateManifest>.Fail(parsed.Failure, parsed.Detail);
        }

        using var document = parsed.Value!;
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Invalid("manifest root must be an object");
        }

        if (!root.TryGetProperty("schema", out var schemaElement) || schemaElement.ValueKind != JsonValueKind.Number || !schemaElement.TryGetInt64(out var schema))
        {
            return Invalid("schema must be an integer");
        }

        if (schema != CurrentSchema)
        {
            return Outcome<UpdateManifest>.Fail(UpdateFailure.UnsupportedSchema, $"schema {schema} is not supported");
        }

        if (!TryString(root, "version", out var versionText) || !SemVer.TryParse(versionText, out var version))
        {
            return Invalid("version must be a SemVer string");
        }

        if (!TryString(root, "channel", out var channelText) || !UpdateChannelNames.TryParse(channelText, out var channel))
        {
            return Invalid("channel must be stable or beta");
        }

        if (!TryString(root, "pubDate", out var pubDateText) || !TryParseUtc(pubDateText!, out var pubDate))
        {
            return Invalid("pubDate must be a UTC ISO-8601 timestamp ending in Z");
        }

        if (pubDate > options.Clock.GetUtcNow() + options.MaxFutureSkew)
        {
            return Outcome<UpdateManifest>.Fail(UpdateFailure.ManifestDatedInFuture, "pubDate is further in the future than allowed; check the local clock");
        }

        Uri? notesUrl = null;
        if (root.TryGetProperty("notesUrl", out var notesElement))
        {
            if (notesElement.ValueKind != JsonValueKind.String)
            {
                return Invalid("notesUrl must be a string");
            }

            var notes = UrlPolicy.Validate(notesElement.GetString(), options.RequireHttps);
            if (!notes.Ok)
            {
                return Outcome<UpdateManifest>.Fail(UpdateFailure.UrlRejected, "notesUrl: " + notes.Detail);
            }

            notesUrl = notes.Value;
        }

        if (!root.TryGetProperty("installers", out var installersElement) || installersElement.ValueKind != JsonValueKind.Object)
        {
            return Invalid("installers must be an object");
        }

        var installers = new Dictionary<string, InstallerEntry>(StringComparer.Ordinal);
        foreach (var architecture in new[] { Architectures.X64, Architectures.Arm64 })
        {
            if (!installersElement.TryGetProperty(architecture, out var entryElement))
            {
                continue;
            }

            var entry = ParseEntry(entryElement, options);
            if (!entry.Ok)
            {
                return Outcome<UpdateManifest>.Fail(entry.Failure, $"{architecture}: {entry.Detail}");
            }

            installers[architecture] = entry.Value!;
        }

        return Outcome<UpdateManifest>.Success(new UpdateManifest(version!, channel, pubDate, notesUrl, installers));
    }

    private static Outcome<InstallerEntry> ParseEntry(JsonElement entry, UpdateOptions options)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return Outcome<InstallerEntry>.Fail(UpdateFailure.ManifestInvalid, "entry must be an object");
        }

        if (!TryString(entry, "url", out var urlText))
        {
            return Outcome<InstallerEntry>.Fail(UpdateFailure.ManifestInvalid, "url must be a string");
        }

        var url = UrlPolicy.Validate(urlText, options.RequireHttps);
        if (!url.Ok)
        {
            return Outcome<InstallerEntry>.Fail(UpdateFailure.UrlRejected, url.Detail);
        }

        if (!entry.TryGetProperty("size", out var sizeElement) || sizeElement.ValueKind != JsonValueKind.Number || !sizeElement.TryGetInt64(out var size))
        {
            return Outcome<InstallerEntry>.Fail(UpdateFailure.ManifestInvalid, "size must be an integer");
        }

        if (size < 1 || size > options.MaxInstallerBytes)
        {
            return Outcome<InstallerEntry>.Fail(UpdateFailure.ManifestInvalid, "size is outside 1 to the installer limit");
        }

        if (!TryString(entry, "sha256", out var sha) || !Sha256Pattern().IsMatch(sha!))
        {
            return Outcome<InstallerEntry>.Fail(UpdateFailure.ManifestInvalid, "sha256 must be 64 lowercase hex characters");
        }

        if (!TryString(entry, "signature", out var signatureText)
            || !StrictJson.TryDecodeCanonicalBase64(signatureText!, out var signatureBytes)
            || !Encoding.ASCII.GetString(signatureBytes).StartsWith(SignatureLead, StringComparison.Ordinal))
        {
            return Outcome<InstallerEntry>.Fail(UpdateFailure.ManifestInvalid, "signature must be a base64 minisign envelope");
        }

        return Outcome<InstallerEntry>.Success(new InstallerEntry(url.Value!, size, sha!, signatureText!));
    }

    private static bool TryString(JsonElement parent, string name, out string? value)
    {
        value = null;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return value is not null;
    }

    private static bool TryParseUtc(string text, out DateTimeOffset value)
    {
        value = default;
        if (!PubDatePattern().IsMatch(text))
        {
            return false;
        }

        return DateTimeOffset.TryParseExact(
            text,
            ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.fff'Z'"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out value);
    }

    private static Outcome<UpdateManifest> Invalid(string detail) =>
        Outcome<UpdateManifest>.Fail(UpdateFailure.ManifestInvalid, detail);
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace Bwc.Update;

/// <summary>What the signed trusted comment of a manifest binds: its version, its channel, and when it expires.</summary>
public sealed record ManifestBinding(string Version, UpdateChannel Channel, DateTimeOffset Expires);

/// <summary>
/// Reads the trusted comment of a manifest signature. The comment must be exactly
/// <c>version:&lt;semver&gt; channel:&lt;stable|beta&gt; expires:&lt;yyyy-MM-ddTHH:mm:ssZ&gt;</c>, one space between
/// fields, in that order, with nothing else. The signature covers it, so its fields are authentic once it verifies.
/// </summary>
public static partial class ManifestComment
{
    private const string ExpiresFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    [GeneratedRegex(
        "^version:(?<version>[^ ]+) channel:(?<channel>stable|beta) expires:(?<expires>[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z)\\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static Outcome<ManifestBinding> Parse(string comment)
    {
        ArgumentNullException.ThrowIfNull(comment);

        var match = Pattern().Match(comment);
        if (!match.Success)
        {
            return Invalid("trusted comment is not version:<semver> channel:<stable|beta> expires:<yyyy-MM-ddTHH:mm:ssZ>");
        }

        var version = match.Groups["version"].Value;
        if (!SemVer.TryParse(version, out var parsed) || parsed!.ToString() != version)
        {
            return Invalid("trusted comment version is not canonical SemVer");
        }

        if (!UpdateChannelNames.TryParse(match.Groups["channel"].Value, out var channel))
        {
            return Invalid("trusted comment channel is not stable or beta");
        }

        if (!DateTimeOffset.TryParseExact(
                match.Groups["expires"].Value,
                ExpiresFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var expires))
        {
            return Invalid("trusted comment expires is not a real UTC time");
        }

        return Outcome<ManifestBinding>.Success(new ManifestBinding(version, channel, expires));
    }

    private static Outcome<ManifestBinding> Invalid(string detail) =>
        Outcome<ManifestBinding>.Fail(UpdateFailure.ManifestInvalid, detail);
}

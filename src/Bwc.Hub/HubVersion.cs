using System.Globalization;
using System.Reflection;

namespace Bwc.Hub;

/// <summary>
/// The version the Hub shows. The SemVer core and prerelease label come from the informational
/// version; the commit is the first seven hex digits of the build metadata, when there is one.
/// </summary>
internal sealed record HubVersion(string SemVer, string? Commit)
{
    public const int ShortCommitLength = 7;

    /// <summary>"0.1.0", "0.1.0-beta.1", with " (commit abc1234)" when the commit is known.</summary>
    public string Display => Commit is null ? SemVer : SemVer + " (commit " + Commit + ")";

    public static HubVersion Resolve(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return Parse(informational);
        }

        Version? version = assembly.GetName().Version;
        if (version is null)
        {
            return new HubVersion("unknown", null);
        }

        return new HubVersion(version.Major.ToString(CultureInfo.InvariantCulture) + "." +
            version.Minor.ToString(CultureInfo.InvariantCulture) + "." +
            version.Build.ToString(CultureInfo.InvariantCulture), null);
    }

    /// <summary>Splits "0.1.0-beta.1+&lt;commit&gt;" into the core and a short commit.</summary>
    public static HubVersion Parse(string informational)
    {
        int plus = informational.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0)
        {
            return new HubVersion(informational.Trim(), null);
        }

        string core = informational[..plus].Trim();
        string metadata = informational[(plus + 1)..];
        int dot = metadata.IndexOf('.', StringComparison.Ordinal);
        string first = dot < 0 ? metadata : metadata[..dot];
        string? commit = IsHex(first) && first.Length >= ShortCommitLength
            ? first[..ShortCommitLength].ToLowerInvariant()
            : null;
        return new HubVersion(core, commit);
    }

    private static bool IsHex(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}

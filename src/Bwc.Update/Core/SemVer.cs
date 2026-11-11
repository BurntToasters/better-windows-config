using System.Text.RegularExpressions;

namespace Bwc.Update;

/// <summary>
/// A strict SemVer 2.0.0 version. Precedence ignores build metadata. Numeric parts are kept as
/// text, so arbitrarily large identifiers compare without overflow.
/// </summary>
public sealed class SemVer : IComparable<SemVer>
{
    private const int MaxLength = 256;
    private const string Identifier = @"(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)";

    private static readonly Regex Pattern = new(
        @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
        + @"(?:-(" + Identifier + @"(?:\." + Identifier + @")*))?"
        + @"(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private SemVer(string major, string minor, string patch, string[] prerelease, string? build)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
        Build = build;
    }

    public string Major { get; }

    public string Minor { get; }

    public string Patch { get; }

    public IReadOnlyList<string> Prerelease { get; }

    public string? Build { get; }

    public bool IsPrerelease => Prerelease.Count > 0;

    /// <summary>Parses a version that is known to be valid. Throws FormatException otherwise.</summary>
    public static SemVer Parse(string text) =>
        TryParse(text, out var version) ? version! : throw new FormatException("not a SemVer version");

    public static bool TryParse(string? text, out SemVer? version)
    {
        version = null;
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength)
        {
            return false;
        }

        var match = Pattern.Match(text);
        if (!match.Success)
        {
            return false;
        }

        var prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        var build = match.Groups[5].Success ? match.Groups[5].Value : null;
        version = new SemVer(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value, prerelease, build);
        return true;
    }

    public int CompareTo(SemVer? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = CompareNumeric(Major, other.Major);
        if (core != 0)
        {
            return core;
        }

        core = CompareNumeric(Minor, other.Minor);
        if (core != 0)
        {
            return core;
        }

        core = CompareNumeric(Patch, other.Patch);
        if (core != 0)
        {
            return core;
        }

        // A release ranks above any prerelease with the same core.
        if (!IsPrerelease && !other.IsPrerelease)
        {
            return 0;
        }

        if (!IsPrerelease)
        {
            return 1;
        }

        if (!other.IsPrerelease)
        {
            return -1;
        }

        var shared = Math.Min(Prerelease.Count, other.Prerelease.Count);
        for (var i = 0; i < shared; i++)
        {
            var diff = ComparePrereleaseIdentifier(Prerelease[i], other.Prerelease[i]);
            if (diff != 0)
            {
                return diff;
            }
        }

        return Prerelease.Count.CompareTo(other.Prerelease.Count);
    }

    public override string ToString()
    {
        var text = Major + "." + Minor + "." + Patch;
        if (IsPrerelease)
        {
            text += "-" + string.Join('.', Prerelease);
        }

        return Build is null ? text : text + "+" + Build;
    }

    private static int CompareNumeric(string left, string right)
    {
        if (left.Length != right.Length)
        {
            return left.Length < right.Length ? -1 : 1;
        }

        return Math.Sign(string.CompareOrdinal(left, right));
    }

    private static int ComparePrereleaseIdentifier(string left, string right)
    {
        var leftNumeric = IsNumeric(left);
        var rightNumeric = IsNumeric(right);
        if (leftNumeric && rightNumeric)
        {
            return CompareNumeric(left, right);
        }

        // Numeric identifiers rank below alphanumeric ones.
        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }

        return Math.Sign(string.CompareOrdinal(left, right));
    }

    private static bool IsNumeric(string identifier) => identifier.All(char.IsAsciiDigit);
}

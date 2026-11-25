using System.Globalization;

namespace Bwc.Policy.Admx;

/// <summary>
/// The language folders an ADML is taken from, in the order they are tried (docs/failure-modes/policy-admx.md, section 52).
/// The loader and the store import check both use this list, so they pick the same ADML.
/// </summary>
internal static class AdmlLanguageChain
{
    /// <summary>The last language of every chain.</summary>
    internal const string Fallback = "en-US";

    /// <summary>
    /// The folder names to try for a UI culture: the culture, each parent culture up to the invariant culture, then en-US.
    /// A null, empty or invalid culture name gives en-US alone. The names are the culture's own names.
    /// </summary>
    internal static IReadOnlyList<string> Names(string? uiCulture)
    {
        var names = new List<string>();
        for (var culture = Resolve(uiCulture); culture is not null && culture.Name.Length > 0; culture = culture.Parent)
        {
            names.Add(culture.Name);
        }

        if (!names.Contains(Fallback, StringComparer.OrdinalIgnoreCase))
        {
            names.Add(Fallback);
        }

        return names;
    }

    private static CultureInfo? Resolve(string? uiCulture)
    {
        var name = uiCulture?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        try
        {
            return CultureInfo.GetCultureInfo(name);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

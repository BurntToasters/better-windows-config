using Bwc.Policy.Admx;

namespace Bwc.Policy.Filter;

/// <summary>A platform the requirements filter can select: one supported-on definition of the loaded set.</summary>
/// <param name="Key">The key (<see cref="PlatformList.KeyOf"/>).</param>
/// <param name="DisplayName">The text of the definition from the ADML.</param>
public sealed record FilterPlatform(string Key, string DisplayName);

/// <summary>
/// The platform list of the requirements filter. The loader does not parse the products part of a supported-on
/// definition, so the list is flat: one entry per definition, in source order.
/// </summary>
public static class PlatformList
{
    /// <summary>The key of a definition: namespace and name joined by a colon.</summary>
    public static string KeyOf(SupportedOnDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Namespace + ":" + definition.Name;
    }

    /// <summary>Every definition of the set as a platform, in source order.</summary>
    public static IReadOnlyList<FilterPlatform> From(TemplateSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return set.SupportedOn.Select(d => new FilterPlatform(KeyOf(d), d.DisplayName)).ToArray();
    }

    /// <summary>Select All: every key of the list.</summary>
    public static IReadOnlyList<string> All(IReadOnlyList<FilterPlatform> platforms)
    {
        ArgumentNullException.ThrowIfNull(platforms);
        return platforms.Select(p => p.Key).ToArray();
    }

    /// <summary>Drops the selected keys that the set no longer defines.</summary>
    public static IReadOnlyList<string> Prune(IEnumerable<string> selected, TemplateSet set)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(set);
        var known = new HashSet<string>(set.SupportedOn.Select(KeyOf), StringComparer.OrdinalIgnoreCase);
        return selected.Where(known.Contains).ToArray();
    }
}

using Bwc.Policy.Admx;

namespace Bwc.Policy.Model;

/// <summary>Computes which snapshot rows are no longer represented by an edited list.</summary>
public static class PolicyListEntryRemoval
{
    /// <summary>Returns the old entries that are not represented in the final list.</summary>
    /// <param name="list">The list definition.</param>
    /// <param name="oldEntries">The entries from the loaded policy snapshot, including their stored names.</param>
    /// <param name="finalEntries">The rows after editing.</param>
    /// <returns>The old entries to put in <see cref="ListValue.Removed"/>.</returns>
    public static IReadOnlyList<ListEntry> FindRemoved(
        ListElement list,
        IReadOnlyList<ListEntry> oldEntries,
        IReadOnlyList<ListEntry> finalEntries)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(oldEntries);
        ArgumentNullException.ThrowIfNull(finalEntries);

        var removed = new List<ListEntry>();
        for (var oldIndex = 0; oldIndex < oldEntries.Count; oldIndex++)
        {
            var old = oldEntries[oldIndex];
            var kept = list.ExplicitValue
                ? finalEntries.Any(entry => string.Equals(entry.Name, old.Name, StringComparison.OrdinalIgnoreCase))
                : list.ValuePrefix is not null
                    ? IsPrefixedEntryKept(list, old, oldIndex, finalEntries)
                    : finalEntries.Any(entry => string.Equals(entry.Data, old.Data, StringComparison.Ordinal));

            if (!kept)
            {
                removed.Add(old);
            }
        }

        return removed;
    }

    private static bool IsPrefixedEntryKept(
        ListElement list,
        ListEntry old,
        int oldIndex,
        IReadOnlyList<ListEntry> finalEntries)
    {
        var oldName = old.Name ?? PolicyRules.ListEntryName(list, oldIndex, old);
        for (var finalIndex = 0; finalIndex < finalEntries.Count; finalIndex++)
        {
            var final = finalEntries[finalIndex];
            var finalName = PolicyRules.ListEntryName(list, finalIndex, final);
            if (string.Equals(finalName, oldName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(final.Data, old.Data, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

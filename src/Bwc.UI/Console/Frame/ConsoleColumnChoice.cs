using Bwc.UI.Console.List;

namespace Bwc.UI.Console.Frame;

/// <summary>
/// The user's column choice for a result list: the column ids in display order (the hidden ones included) and the ids
/// that are hidden. It is one choice for the list, not one per node: a node whose layout has other columns applies the
/// named ones in the saved order and the unnamed ones follow, shown. The first column of a list (the label column) is
/// fixed: it is first and shown whatever the choice says (docs/failure-modes/console-frame.md, section 3 and 5).
/// </summary>
/// <param name="Order">The column ids in display order. Unknown and repeated ids are ignored.</param>
/// <param name="Hidden">The ids of the hidden columns. Unknown ids and the first column are ignored.</param>
public sealed record ConsoleColumnChoice(IReadOnlyList<string> Order, IReadOnlyList<string> Hidden)
{
    /// <summary>The definition order with every column shown.</summary>
    public static ConsoleColumnChoice Default { get; } = new([], []);

    /// <summary>True when the choice names nothing, so a list keeps its definition order and shows every column.</summary>
    public bool IsDefault => Order.Count == 0 && Hidden.Count == 0;

    /// <summary>
    /// Makes a choice from stored lists. A null list (a file that holds null) is empty, and a null or empty entry is dropped.
    /// </summary>
    public static ConsoleColumnChoice From(IEnumerable<string?>? order, IEnumerable<string?>? hidden) =>
        new(
            [.. (order ?? []).Where(id => !string.IsNullOrEmpty(id)).Select(id => id!)],
            [.. (hidden ?? []).Where(id => !string.IsNullOrEmpty(id)).Select(id => id!)]);

    /// <summary>
    /// Applies the choice to the list: the first column first and shown, the named columns after it in the saved order, the
    /// unnamed ones after those in their definition order, and the named hidden columns hidden. Raises the list's column
    /// events (the list clears a sort by a column that goes).
    /// </summary>
    public void ApplyTo(ConsoleList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        List<ConsoleListColumn> definition = [.. list.Columns.OrderBy(column => column.Index)];
        if (definition.Count == 0)
        {
            return;
        }

        string first = definition[0].Id;
        var known = new HashSet<string>(definition.Select(column => column.Id), StringComparer.Ordinal);
        var order = new List<string> { first };
        var seen = new HashSet<string>(StringComparer.Ordinal) { first };
        foreach (string id in Order)
        {
            if (known.Contains(id) && seen.Add(id))
            {
                order.Add(id);
            }
        }

        foreach (ConsoleListColumn column in definition)
        {
            if (seen.Add(column.Id))
            {
                order.Add(column.Id);
            }
        }

        var hidden = new HashSet<string>(Hidden.Where(id => known.Contains(id) && id != first), StringComparer.Ordinal);
        list.SetColumnOrder(order);
        foreach (string id in order)
        {
            list.SetColumnVisible(id, !hidden.Contains(id));
        }
    }

    /// <summary>
    /// The choice after the chooser closed with OK on a list that has the columns <paramref name="layoutIds"/>: the
    /// chooser's result for those columns first, then what this choice said about columns that list does not have (another
    /// node's layout), so a choice made at one node is not forgotten at another.
    /// </summary>
    public ConsoleColumnChoice Replace(ConsoleColumnChoice result, IReadOnlyCollection<string> layoutIds)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(layoutIds);
        var inLayout = new HashSet<string>(layoutIds, StringComparer.Ordinal);
        var seen = new HashSet<string>(result.Order, StringComparer.Ordinal);
        List<string> order = [.. result.Order];
        order.AddRange(Order.Where(id => !inLayout.Contains(id) && seen.Add(id)));
        List<string> hidden = [.. result.Hidden];
        hidden.AddRange(Hidden.Where(id => !inLayout.Contains(id) && !hidden.Contains(id)));
        return new ConsoleColumnChoice(order, hidden);
    }
}

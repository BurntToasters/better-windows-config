namespace Bwc.Policy.Model;

/// <summary>A new state for a policy and, for Enabled, the values of its elements.</summary>
public sealed class PolicyEdit
{
    private static readonly IReadOnlyDictionary<string, ElementValue> NoElements =
        new Dictionary<string, ElementValue>(StringComparer.OrdinalIgnoreCase);

    private PolicyEdit(PolicyState state, IReadOnlyDictionary<string, ElementValue> elements)
    {
        State = state;
        Elements = elements;
    }

    /// <summary>The state to apply.</summary>
    public PolicyState State { get; }

    /// <summary>
    /// The element values by element id (case-insensitive). An element that is not here is empty. Only an Enabled edit uses
    /// them.
    /// </summary>
    public IReadOnlyDictionary<string, ElementValue> Elements { get; }

    /// <summary>The Not Configured edit.</summary>
    public static PolicyEdit NotConfigured { get; } = new(PolicyState.NotConfigured, NoElements);

    /// <summary>The Disabled edit.</summary>
    public static PolicyEdit Disabled { get; } = new(PolicyState.Disabled, NoElements);

    /// <summary>An Enabled edit.</summary>
    /// <param name="elements">The element values by element id. Later pairs with the same id replace earlier ones.</param>
    /// <returns>The edit.</returns>
    public static PolicyEdit Enabled(params (string Id, ElementValue Value)[] elements)
    {
        var map = new Dictionary<string, ElementValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, value) in elements)
        {
            map[id] = value;
        }

        return new PolicyEdit(PolicyState.Enabled, map);
    }

    /// <summary>An Enabled edit built from an existing set of values, for example a snapshot's.</summary>
    /// <param name="elements">The element values by element id.</param>
    /// <returns>The edit.</returns>
    public static PolicyEdit EnabledWith(IReadOnlyDictionary<string, ElementValue> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        return new PolicyEdit(PolicyState.Enabled, new Dictionary<string, ElementValue>(elements, StringComparer.OrdinalIgnoreCase));
    }
}

/// <summary>What is wrong with an element value. The UI maps each kind to a classic message.</summary>
public enum PolicyProblemKind
{
    /// <summary>The edit names an element the policy does not have.</summary>
    UnknownElement,

    /// <summary>The value has the shape of another element kind.</summary>
    WrongValueKind,

    /// <summary>A required element is empty.</summary>
    Required,

    /// <summary>A number is below the minimum. The limit is the minimum.</summary>
    BelowMinimum,

    /// <summary>A number is above the maximum. The limit is the maximum.</summary>
    AboveMaximum,

    /// <summary>A text is longer than allowed. The limit is the longest length.</summary>
    TooLong,

    /// <summary>A multiText has more lines than allowed. The limit is the most lines.</summary>
    TooManyLines,

    /// <summary>An enum index is not an item.</summary>
    InvalidChoice,

    /// <summary>A string holds a character the file cannot hold (NUL), or a list name starts with two asterisks.</summary>
    InvalidCharacter,

    /// <summary>An explicitValue list entry has no name.</summary>
    EntryNameMissing,

    /// <summary>A list entry has no data.</summary>
    EntryEmpty,

    /// <summary>Two list entries get the same value name.</summary>
    DuplicateEntry,
}

/// <summary>One problem with an edit.</summary>
/// <param name="ElementId">The element id (as given, for an unknown element).</param>
/// <param name="Kind">What is wrong.</param>
/// <param name="Limit">The limit that was broken, for the kinds that have one.</param>
/// <param name="EntryIndex">The zero-based list entry, for list problems.</param>
public sealed record PolicyEditProblem(string ElementId, PolicyProblemKind Kind, ulong? Limit = null, int? EntryIndex = null);

/// <summary>The outcome of <see cref="PolicyStateWriter.Apply"/>.</summary>
/// <param name="Problems">The problems found. When there are any, nothing was written.</param>
/// <param name="Changed">True when the document bytes changed.</param>
public sealed record PolicyApplyResult(IReadOnlyList<PolicyEditProblem> Problems, bool Changed)
{
    /// <summary>True when the edit was valid and applied.</summary>
    public bool IsValid => Problems.Count == 0;
}

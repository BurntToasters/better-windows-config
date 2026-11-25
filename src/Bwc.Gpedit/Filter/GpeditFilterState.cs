using Bwc.Gpedit.Policy;
using Bwc.Gpo.Store;
using Bwc.Policy.Admx;
using Bwc.Policy.Filter;

namespace Bwc.Gpedit.Filter;

/// <summary>
/// The filter of the editor window: the options of the last OK, whether Filter On is on, and what the filter let through.
/// What it lets through is worked out from the policy files as they were when the filter was applied (OK, Filter On or
/// Re-Apply Filter), so a policy that is edited afterwards keeps its place in the list until Re-Apply Filter. The work is
/// done once per side, on first need, against that model. The options are one set for the whole editor, both sides.
/// Docs: docs/failure-modes/gpedit-filter.md.
/// </summary>
internal sealed class GpeditFilterState
{
    private readonly Dictionary<GpoSide, HashSet<PolicyDefinition>> _matches = [];
    private readonly Dictionary<GpoSide, HashSet<PolicyCategory>> _visible = [];
    private PolicyFilter? _filter;
    private GpeditModel? _basis;

    /// <summary>The options of the last OK. Kept while Filter On is off.</summary>
    internal FilterOptions Options { get; private set; } = FilterOptions.None;

    /// <summary>True while Filter On is on.</summary>
    internal bool IsOn { get; private set; }

    /// <summary>
    /// Takes new options and turns Filter On on. False, with the reasons, when the options are refused; nothing changes then.
    /// </summary>
    internal bool TryAccept(FilterOptions options, GpeditModel? model, out IReadOnlyList<FilterProblem> problems)
    {
        if (!PolicyFilter.TryCreate(options, out PolicyFilter? filter, out problems) || filter is null)
        {
            return false;
        }

        Options = options;
        _filter = filter;
        IsOn = true;
        Rebase(model);
        return true;
    }

    /// <summary>Turns Filter On on with the options kept. Returns false when they cannot make a filter.</summary>
    internal bool TurnOn(GpeditModel? model)
    {
        if (!PolicyFilter.TryCreate(Options, out PolicyFilter? filter, out _) || filter is null)
        {
            return false;
        }

        _filter = filter;
        IsOn = true;
        Rebase(model);
        return true;
    }

    /// <summary>Turns Filter On off. The options stay.</summary>
    internal void TurnOff()
    {
        IsOn = false;
        Rebase(null);
    }

    /// <summary>Applies the options again, to the policy files of this model (Re-Apply Filter, and a reload).</summary>
    internal void Rebase(GpeditModel? model)
    {
        _basis = model;
        _matches.Clear();
        _visible.Clear();
    }

    /// <summary>True when the policy shows in a list of this side: always while Filter On is off.</summary>
    internal bool Shows(PolicyDefinition policy, GpoSide side)
    {
        if (!IsOn || _filter is null || _basis is null)
        {
            return true;
        }

        return MatchesOf(side).Contains(policy);
    }

    /// <summary>
    /// True when the category has a matching policy of the side in itself or below it: always while Filter On is off. A
    /// category with none is hidden in the tree and in the list of its parent.
    /// </summary>
    internal bool Shows(PolicyCategory category, GpoSide side)
    {
        if (!IsOn || _filter is null || _basis is null)
        {
            return true;
        }

        if (!_visible.TryGetValue(side, out HashSet<PolicyCategory>? known))
        {
            known = [];
            foreach (PolicyDefinition policy in MatchesOf(side))
            {
                for (PolicyCategory? at = policy.Category; at is not null; at = at.Parent)
                {
                    if (!known.Add(at))
                    {
                        break;
                    }
                }
            }

            _visible[side] = known;
        }

        return known.Contains(category);
    }

    private HashSet<PolicyDefinition> MatchesOf(GpoSide side)
    {
        if (_matches.TryGetValue(side, out HashSet<PolicyDefinition>? known))
        {
            return known;
        }

        var matching = new HashSet<PolicyDefinition>();
        if (_filter is not null && _basis is not null)
        {
            foreach (PolicyDefinition policy in _basis.AllPolicies(side))
            {
                if (_filter.Matches(_basis.SubjectOf(policy, side)))
                {
                    _ = matching.Add(policy);
                }
            }
        }

        _matches[side] = matching;
        return matching;
    }
}

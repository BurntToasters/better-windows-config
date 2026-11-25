using Bwc.Gpo.Store;
using Bwc.Policy.Admx;

namespace Bwc.Policy.Filter;

/// <summary>The flat All Settings list of one side (Oracle admtmpl.dll.mui:3118).</summary>
public static class AllSettings
{
    /// <summary>
    /// Every policy that applies to the side, once each, in source order. A class Both policy is in the list of each
    /// side. Sorting is the UI's job. A policy with no category is included
    /// (<see cref="FilterRules.AllSettingsIncludesUncategorized"/>).
    /// </summary>
    public static IReadOnlyList<PolicyDefinition> List(TemplateSet set, GpoSide side, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(set);
        var result = new List<PolicyDefinition>();
        foreach (var policy in set.Policies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var applies = side == GpoSide.Machine ? policy.AppliesToMachine : policy.AppliesToUser;
            if (applies && (policy.Category is not null || FilterRules.AllSettingsIncludesUncategorized))
            {
                result.Add(policy);
            }
        }

        return result;
    }
}

using Bwc.Policy.Admx;

namespace Bwc.Policy.Model;

/// <summary>
/// The values the setting dialog shows for the elements of a policy right after Enabled was chosen and before anything was
/// typed (docs/failure-modes/policy-defaults.md, section F). The rules are the captures of classic: a check box follows
/// <c>defaultChecked</c>, a number its <c>defaultValue</c> (1 when there is none), a text and a combo box their default
/// text (else empty), an enum its <c>defaultItem</c> (nothing selected when there is none), a multi-text and a list
/// nothing.
/// </summary>
public static class PolicyDefaults
{
    /// <summary>The default values of the elements that have a control in the presentation of the policy.</summary>
    /// <param name="policy">The policy definition, with its presentation linked to its elements.</param>
    /// <returns>
    /// A new dictionary by element id (case-insensitive). An element with nothing selected (an enum with no default item), an
    /// element with no control and a static text have no entry. Empty when the policy has no presentation or no element.
    /// </returns>
    public static IReadOnlyDictionary<string, ElementValue> For(PolicyDefinition policy) =>
        throw new NotImplementedException("PolicyDefaults.For is written by dispatch C of piece E6b.");
}

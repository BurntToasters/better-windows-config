using Bwc.Gpo.Store;
using Bwc.Policy.Admx;

namespace Bwc.Gpedit.List;

/// <summary>
/// A request to edit a policy: the Edit policy setting link of the description pane, Enter or a double click on a policy
/// row. Piece G3a opens the setting dialog for it. Nothing handles it in G1, so raising it does nothing.
/// </summary>
public sealed class GpeditPolicyEditEventArgs : EventArgs
{
    internal GpeditPolicyEditEventArgs(PolicyDefinition policy, GpoSide side)
    {
        Policy = policy;
        Side = side;
    }

    /// <summary>The policy to edit.</summary>
    public PolicyDefinition Policy { get; }

    /// <summary>The side whose file the policy is edited in (a class Both policy is edited once per side).</summary>
    public GpoSide Side { get; }
}

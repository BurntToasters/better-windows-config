using Bwc.Gpo.Store;
using Bwc.Policy.Admx;

namespace Bwc.Policy.Model;

/// <summary>The outcome of <see cref="PolicySave.Save"/>.</summary>
/// <param name="Problems">The problems found before anything was read or written. Empty when the edit was
/// valid.</param>
/// <param name="Changed">True when the save changed a model and so wrote at least one file. False with
/// problems.</param>
/// <param name="Result">What the store wrote and refreshed, or null when there were problems.</param>
public sealed record PolicySaveOutcome(IReadOnlyList<PolicyEditProblem> Problems, bool Changed, GpoSaveResult? Result)
{
    /// <summary>True when the edit was valid and the save ran.</summary>
    public bool IsValid => Problems.Count == 0;
}

/// <summary>
/// The one save every store uses for a policy edit (docs/failure-modes/policy-save.md). The contract:
/// <list type="number">
/// <item>An edit that has problems under <see cref="PolicyEditValidator"/> returns them. Nothing is read,
/// locked or written.</item>
/// <item>Otherwise one <see cref="GpoStore.Save"/> of the side runs. Inside it the edit is applied with
/// <see cref="PolicyStateWriter.Apply"/> to the models read under the lock, then the comment is set.</item>
/// <item>When the state or the comment changed, <c>RestrictNamespacesTo</c> of the policy namespace runs,
/// as classic does on a save, so the first save writes comment.cmtx with one using line even with no
/// comment. When nothing changed it does not run, so a save that changes nothing writes nothing.</item>
/// <item>An exception inside the edit (a template fault, a malformed file) leaves the files as they were,
/// because the store writes only after the edit returns.</item>
/// <item>The files written, their order, the gpt.ini rules and the empty Registry.pol of a first save
/// follow the store.</item>
/// </list>
/// </summary>
public static class PolicySave
{
    /// <summary>Validates an edit and saves it with its comment into one side of the GPO.</summary>
    /// <param name="store">The store of the GPO folder.</param>
    /// <param name="side">The side being saved; a class Both policy is saved once per side.</param>
    /// <param name="policy">The policy definition.</param>
    /// <param name="edit">The new state and element values; null saves the comment alone.</param>
    /// <param name="comment">The comment text; empty removes the comment.</param>
    /// <param name="cancellationToken">Passed to the store; checked before its first write.</param>
    /// <returns>The problems, or whether the save changed anything and what it wrote.</returns>
    /// <exception cref="GpoStoreException">The store refused or failed (read-only, lock, malformed file,
    /// write).</exception>
    public static PolicySaveOutcome Save(
        GpoStore store,
        GpoSide side,
        PolicyDefinition policy,
        PolicyEdit? edit,
        string comment,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("PolicySave.Save is written by piece E6a, dispatch C.");
}

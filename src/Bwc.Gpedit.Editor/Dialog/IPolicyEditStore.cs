using Bwc.Gpo.Comments;
using Bwc.Gpo.Pol;
using Bwc.Gpo.Store;
using Bwc.Policy.Model;

namespace Bwc.Gpedit.Editor.Dialog;

/// <summary>What <see cref="IPolicyEditStore.Apply"/> did.</summary>
/// <param name="Problems">The problems found. When there are any, nothing was changed.</param>
/// <param name="Changed">True when the state or the comment changed.</param>
/// <param name="RefreshFailure">
/// Why the policy refresh after the write failed, or null. The files are on disk and the change is applied.
/// </param>
public sealed record PolicyApplyOutcome(IReadOnlyList<PolicyEditProblem> Problems, bool Changed, GpoStoreException? RefreshFailure = null)
{
    /// <summary>True when the edit was valid and applied.</summary>
    public bool IsApplied => Problems.Count == 0;
}

/// <summary>
/// The one seam between the setting dialog and the policy object: what the dialog reads at open and what Apply does. The
/// store of the editor saves the edit into the policy object and reads the files back, so the models it hands out are what
/// is on disk (docs/failure-modes/gpedit-dialog.md, section 15). The dialog never names a file.
/// </summary>
public interface IPolicyEditStore
{
    /// <summary>True when the policy object cannot be changed (not elevated, or an edition that cannot be written).</summary>
    bool IsReadOnly { get; }

    /// <summary>The policy file model of a side, or null when it could not be read.</summary>
    PolDocument? PolFor(GpoSide side);

    /// <summary>The comments of a side, or null when they could not be read.</summary>
    CmtxDocument? CommentsFor(GpoSide side);

    /// <summary>
    /// Validates the edit and saves it, with the comment, into the side of the target. An invalid edit changes nothing. It
    /// blocks while it waits for the write lock, so the dialog calls it off the UI thread. After it returns,
    /// <see cref="PolFor"/> and <see cref="CommentsFor"/> give the saved result.
    /// </summary>
    /// <param name="target">The policy and side.</param>
    /// <param name="edit">The new state and element values, or null when only the comment changed.</param>
    /// <param name="comment">The comment text; empty removes the comment.</param>
    /// <returns>The problems, or whether anything changed.</returns>
    /// <exception cref="GpoStoreException">The store refused or failed (read-only file, lock, malformed, write).</exception>
    PolicyApplyOutcome Apply(PolicyRef target, PolicyEdit? edit, string comment);
}

using Bwc.Gpedit.Editor.Dialog;
using Bwc.Gpo.Comments;
using Bwc.Gpo.Pol;
using Bwc.Gpo.Store;
using Bwc.Policy.Model;

namespace Bwc.Gpedit.Save;

/// <summary>
/// The store of the setting dialog that saves (piece G4): an edit goes through <see cref="PolicySave.Save"/> into the side
/// of the policy it was opened from, and the files are read again afterwards, so <see cref="PolFor"/> and
/// <see cref="CommentsFor"/> always give what is on disk. A class Both policy is saved into the side of the node it was
/// opened from, because the side comes from the request and never from the class of the policy. The failure modes are in
/// docs/failure-modes/gpedit-dialog.md, section 15.
/// </summary>
internal sealed class PolicySaveStore : IPolicyEditStore
{
    private readonly GpoStore? _store;
    private readonly bool _readOnly;
    private GpoSnapshot? _snapshot;

    /// <summary>Creates the store over the policy object that the editor loaded.</summary>
    /// <param name="store">The one store of the window (it keeps the session backup), or null when none was made.</param>
    /// <param name="snapshot">The files as the editor read them, or null when the policy object could not be opened.</param>
    /// <param name="readOnly">True when the policy object cannot be changed.</param>
    internal PolicySaveStore(GpoStore? store, GpoSnapshot? snapshot, bool readOnly)
    {
        _store = store;
        _snapshot = snapshot;
        _readOnly = readOnly || store is null;
    }

    /// <summary>The files as last read: at the start, or after the last save that wrote something.</summary>
    internal GpoSnapshot? Snapshot => Volatile.Read(ref _snapshot);

    /// <inheritdoc />
    public bool IsReadOnly => _readOnly;

    /// <inheritdoc />
    public PolDocument? PolFor(GpoSide side) => Snapshot?.Side(side).Pol;

    /// <inheritdoc />
    public CmtxDocument? CommentsFor(GpoSide side) => Snapshot?.Side(side).Comments;

    /// <inheritdoc />
    public PolicyApplyOutcome Apply(PolicyRef target, PolicyEdit? edit, string comment)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(comment);
        if (_readOnly || _store is null)
        {
            throw new InvalidOperationException("The policy object is read-only.");
        }

        PolicySaveOutcome outcome = PolicySave.Save(_store, target.Side, target.Policy, edit, comment);
        if (!outcome.IsValid)
        {
            return new PolicyApplyOutcome(outcome.Problems, false);
        }

        if (outcome.Result is { Wrote: true } result)
        {
            // The files were written: the models of the dialog and of the list become what is on disk now.
            Volatile.Write(ref _snapshot, _store.Open());
            return new PolicyApplyOutcome([], true, result.RefreshFailure);
        }

        return new PolicyApplyOutcome([], false);
    }
}

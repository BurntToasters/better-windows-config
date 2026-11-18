using Bwc.Gpo.Comments;
using Bwc.Gpo.Pol;
using Bwc.Gpo.Store;
using Bwc.Policy.Model;

namespace Bwc.Gpedit.Editor.Dialog;

/// <summary>
/// The G3a apply seam: changes the in-memory models that the editor loaded and writes nothing. It has no file API, so the
/// writer guard accepts it, and it is the one class piece G4 replaces with a saving store.
/// </summary>
public sealed class InMemoryPolicyEditStore : IPolicyEditStore
{
    private readonly Func<GpoSide, PolDocument?> _pol;
    private readonly Func<GpoSide, CmtxDocument?> _comments;

    /// <summary>Creates the store over the models of the loaded policy object.</summary>
    /// <param name="pol">The policy file model of a side, or null when it could not be read.</param>
    /// <param name="comments">The comments of a side, or null when they could not be read.</param>
    /// <param name="isReadOnly">True when the dialog must not change anything.</param>
    public InMemoryPolicyEditStore(Func<GpoSide, PolDocument?> pol, Func<GpoSide, CmtxDocument?> comments, bool isReadOnly = false)
    {
        _pol = pol ?? throw new ArgumentNullException(nameof(pol));
        _comments = comments ?? throw new ArgumentNullException(nameof(comments));
        IsReadOnly = isReadOnly;
    }

    /// <inheritdoc />
    public bool IsReadOnly { get; }

    /// <inheritdoc />
    public PolDocument? PolFor(GpoSide side) => _pol(side);

    /// <inheritdoc />
    public CmtxDocument? CommentsFor(GpoSide side) => _comments(side);

    /// <inheritdoc />
    public PolicyApplyOutcome Apply(PolicyRef target, PolicyEdit? edit, string comment)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(comment);
        if (IsReadOnly)
        {
            throw new InvalidOperationException("The policy object is read-only.");
        }

        bool stateChanged = false;
        if (edit is not null)
        {
            PolDocument document = _pol(target.Side)
                ?? throw new InvalidOperationException("The policy file of the side could not be read.");

            // The engine validates again, but nothing is changed (not even the comment) when the edit has a problem.
            IReadOnlyList<PolicyEditProblem> problems = PolicyEditValidator.Validate(target.Policy, edit);
            if (problems.Count > 0)
            {
                return new PolicyApplyOutcome(problems, false);
            }

            stateChanged = PolicyStateWriter.Apply(target.Policy, document, edit).Changed;
        }

        bool commentChanged = false;
        CmtxDocument? comments = _comments(target.Side);
        if (comments is not null)
        {
            string before = comments.GetComment(target.Policy.Namespace, target.Policy.Name) ?? string.Empty;
            if (!string.Equals(before, comment, StringComparison.Ordinal))
            {
                comments.SetComment(target.Policy.Namespace, target.Policy.Name, comment.Length == 0 ? null : comment);
                commentChanged = true;
            }
        }

        return new PolicyApplyOutcome([], stateChanged || commentChanged);
    }
}

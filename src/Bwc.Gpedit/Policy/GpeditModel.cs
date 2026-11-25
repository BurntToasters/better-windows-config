using System.Diagnostics;
using System.Globalization;
using Bwc.Core;
using Bwc.Gpo.Comments;
using Bwc.Gpo.Pol;
using Bwc.Gpo.Store;
using Bwc.Policy.Admx;
using Bwc.Policy.Filter;
using Bwc.Policy.Model;
using Bwc.Policy.Store;

namespace Bwc.Gpedit.Policy;

/// <summary>What kind of read problem a <see cref="GpeditProblem"/> records.</summary>
internal enum GpeditProblemKind
{
    /// <summary>A template (or part of one, or a folder) did not load. The detail is the engine's failure code.</summary>
    TemplateFailure,

    /// <summary>The import store is not usable (missing is normal and not recorded).</summary>
    TemplateStore,

    /// <summary>The E2E sandbox policy object was refused.</summary>
    StorePolicy,

    /// <summary>A policy file could not be read; the states of that side are unknown.</summary>
    PolicyFile,

    /// <summary>A comment file could not be read; the Comment column shows No.</summary>
    CommentFile,

    /// <summary>The version file could not be read.</summary>
    VersionFile,

    /// <summary>An unexpected exception ended part of the load.</summary>
    LoadException,
}

/// <summary>
/// One recorded read problem. The template ones are counted in the status bar and listed in the Add/Remove Templates
/// dialog.
/// </summary>
/// <param name="Kind">The kind.</param>
/// <param name="Path">The file or folder, or an empty string.</param>
/// <param name="Detail">The reason code or message, without user-facing wording.</param>
/// <param name="Message">The engine's own detail of a template failure (a namespace, a name, an HRESULT), or empty.</param>
internal sealed record GpeditProblem(GpeditProblemKind Kind, string Path, string Detail, string Message = "");

/// <summary>
/// Everything the tree and the list read: the linked templates, one snapshot of the policy object and the problems found
/// on the way. Built on the thread pool by <see cref="Load"/>, which never throws except for cancellation, and immutable
/// afterwards, so the UI thread reads it without a lock (docs/failure-modes/gpedit-tree-list.md, sections 3 and 7).
/// </summary>
internal sealed class GpeditModel
{
    private readonly Dictionary<GpoSide, HashSet<PolicyCategory>> _visible = [];

    private GpeditModel(
        TemplateSet templates,
        GpoSnapshot? snapshot,
        IReadOnlyList<GpeditProblem> problems,
        TimeSpan loadTime,
        string storeDescription,
        IGpoStorePolicy? storePolicy = null,
        GpoAccessResult? access = null)
    {
        StorePolicy = storePolicy;
        Access = access ?? new GpoAccessResult([GpoReadOnlyReason.NoWriteAccess]);
        Templates = templates;
        Snapshot = snapshot;
        Problems = problems;
        LoadTime = loadTime;
        StoreDescription = storeDescription;
    }

    /// <summary>The linked templates (empty when nothing loaded).</summary>
    public TemplateSet Templates { get; }

    /// <summary>The policy object as read, or null when it could not be opened at all.</summary>
    public GpoSnapshot? Snapshot { get; }

    /// <summary>Every read problem in the order found.</summary>
    public IReadOnlyList<GpeditProblem> Problems { get; }

    /// <summary>How long the load took.</summary>
    public TimeSpan LoadTime { get; }

    /// <summary>The policy choice, for the log.</summary>
    public string StoreDescription { get; }

    /// <summary>The policy the store opened through, or null when none was chosen (a model that did not load).</summary>
    public IGpoStorePolicy? StorePolicy { get; }

    /// <summary>
    /// Why the policy object cannot be saved. A model that did not load has a reason, so nothing is saved through it.
    /// </summary>
    public GpoAccessResult Access { get; }

    /// <summary>True when OK and Apply must not write: the access check found a reason, or no policy was chosen.</summary>
    public bool IsReadOnly => Access.IsReadOnly || StorePolicy is null;

    /// <summary>
    /// The same model over the files as a save left them. The templates, the problems of the load and the access stay; the
    /// states and comments the list shows come from the new snapshot.
    /// </summary>
    /// <param name="snapshot">The policy object as read after the save.</param>
    public GpeditModel WithSnapshot(GpoSnapshot snapshot) =>
        new(Templates, snapshot, Problems, LoadTime, StoreDescription, StorePolicy, Access);

    /// <summary>An empty model with no problem, for a view that has not loaded yet.</summary>
    public static GpeditModel Empty { get; } = new(TemplateSet.Empty, null, [], TimeSpan.Zero, string.Empty);

    /// <summary>Loads the templates and opens the policy object. Only cancellation is thrown.</summary>
    public static GpeditModel Load(CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        var problems = new List<GpeditProblem>();
        var templates = TemplateSet.Empty;
        GpoSnapshot? snapshot = null;
        string description = string.Empty;
        GpeditStoreChoice? chosen = null;

        try
        {
            GpeditStoreChoice choice = GpeditStorePolicy.Choose();
            chosen = choice;
            description = choice.Description;
            Log.Info("Local Group Policy Editor policy object: " + description);
            if (choice.Problem is not null)
            {
                problems.Add(new GpeditProblem(GpeditProblemKind.StorePolicy, string.Empty, choice.Problem));
            }

            templates = LoadTemplates(problems, cancellationToken);
            snapshot = OpenStore(choice, problems, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            problems.Add(new GpeditProblem(GpeditProblemKind.LoadException, string.Empty, ex.GetType().Name));
            Log.Warn("Local Group Policy Editor load failed: " + ex.Message);
        }

        clock.Stop();
        return new GpeditModel(templates, snapshot, problems, clock.Elapsed, description, chosen?.Policy, chosen?.Access);
    }

    /// <summary>The policy file model of a side, or null when it could not be read (or nothing was opened).</summary>
    public PolDocument? PolFor(GpoSide side) => Snapshot?.Side(side).Pol;

    /// <summary>The comments of a side, or null when they could not be read.</summary>
    public CmtxDocument? CommentsFor(GpoSide side) => Snapshot?.Side(side).Comments;

    /// <summary>
    /// The state of a policy on a side, or null when the side's policy file is unknown. A class Both policy is read from
    /// the document of the asked side.
    /// </summary>
    public PolicyState? StateOf(PolicyDefinition policy, GpoSide side)
    {
        PolDocument? document = PolFor(side);
        if (document is null)
        {
            return null;
        }

        try
        {
            return PolicyStateReader.Read(policy, document).State;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"The state of '{policy.FullName}' could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>True when the policy has a non-empty comment on the side.</summary>
    public bool HasComment(PolicyDefinition policy, GpoSide side)
    {
        try
        {
            return !string.IsNullOrEmpty(CommentsFor(side)?.GetComment(policy.Namespace, policy.Name));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"The comment of '{policy.FullName}' could not be read: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The categories shown on a side: each one that holds a policy applying to the side, directly or in a descendant.
    /// Computed once per side.
    /// </summary>
    public IReadOnlySet<PolicyCategory> VisibleCategories(GpoSide side)
    {
        lock (_visible)
        {
            if (_visible.TryGetValue(side, out HashSet<PolicyCategory>? known))
            {
                return known;
            }

            var set = new HashSet<PolicyCategory>();
            foreach (PolicyDefinition policy in Templates.Policies)
            {
                bool applies = side == GpoSide.Machine ? policy.AppliesToMachine : policy.AppliesToUser;
                if (!applies)
                {
                    continue;
                }

                int guard = 0;
                for (PolicyCategory? category = policy.Category; category is not null && guard < 256; category = category.Parent, guard++)
                {
                    if (!set.Add(category))
                    {
                        break;
                    }
                }
            }

            _visible[side] = set;
            return set;
        }
    }

    /// <summary>The visible child categories of a category (null for the roots) on a side, in display order.</summary>
    public IReadOnlyList<PolicyCategory> ChildCategories(PolicyCategory? parent, GpoSide side)
    {
        IReadOnlySet<PolicyCategory> visible = VisibleCategories(side);
        IEnumerable<PolicyCategory> source = parent is null ? Templates.RootCategories : parent.Children;
        return source.Where(visible.Contains).OrderBy(c => c, CategoryOrder.Instance).ToList();
    }

    /// <summary>
    /// The policies of a category that apply to a side, in the default order of the list: the policy ids, compared ordinally
    /// (F/Sort_Columns op 5 and 17, F/Nodes_Computer; docs/failure-modes/gpedit-filter.md, section 18).
    /// </summary>
    public IReadOnlyList<PolicyDefinition> PoliciesOf(PolicyCategory category, GpoSide side) =>
        category.Policies
            .Where(p => side == GpoSide.Machine ? p.AppliesToMachine : p.AppliesToUser)
            .OrderBy(p => p, IdOrder.Instance)
            .ToList();

    /// <summary>
    /// A policy as the filter engine sees it: the definition, its state in the side's policy file (null when that file is
    /// unknown or the state cannot be read) and its comment on the side (null when it has none or cannot be read).
    /// </summary>
    public FilterSubject SubjectOf(PolicyDefinition policy, GpoSide side)
    {
        PolicyStateSnapshot? state = null;
        try
        {
            PolDocument? document = PolFor(side);
            state = document is null ? null : PolicyStateReader.Read(policy, document);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"The state of '{policy.FullName}' could not be read for the filter: {ex.Message}");
        }

        string? comment = null;
        try
        {
            comment = CommentsFor(side)?.GetComment(policy.Namespace, policy.Name);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"The comment of '{policy.FullName}' could not be read for the filter: {ex.Message}");
        }

        return new FilterSubject(policy, state, comment);
    }

    /// <summary>Every policy of a side (All Settings), in display order.</summary>
    public IReadOnlyList<PolicyDefinition> AllPolicies(GpoSide side) =>
        Bwc.Policy.Filter.AllSettings.List(Templates, side).OrderBy(p => p, PolicyOrder.Instance).ToList();

    private static TemplateSet LoadTemplates(List<GpeditProblem> problems, CancellationToken cancellationToken)
    {
        try
        {
            TemplateSources sources = GpeditStorePolicy.CreateTemplateSources();
            TemplateSourcesResult result = sources.Load(CultureInfo.CurrentUICulture.Name, cancellationToken);
            if (result.Store is not (TemplateStoreStatus.Ready or TemplateStoreStatus.Missing))
            {
                string detail = result.Store + (result.StoreDetail.Length > 0 ? ": " + result.StoreDetail : string.Empty);
                problems.Add(new GpeditProblem(GpeditProblemKind.TemplateStore, sources.StoreFolder, detail));
            }

            foreach (TemplateFailure failure in result.Set.Failures)
            {
                problems.Add(new GpeditProblem(GpeditProblemKind.TemplateFailure, failure.FilePath, failure.Code.ToString(), failure.Detail));
            }

            return result.Set;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            problems.Add(new GpeditProblem(GpeditProblemKind.LoadException, string.Empty, "Templates: " + ex.GetType().Name));
            Log.Warn("Templates could not be loaded: " + ex.Message);
            return TemplateSet.Empty;
        }
    }

    private static GpoSnapshot? OpenStore(GpeditStoreChoice choice, List<GpeditProblem> problems, CancellationToken cancellationToken)
    {
        try
        {
            GpoSnapshot snapshot = new GpoStore(choice.Policy).Open(cancellationToken);
            foreach (GpoSide side in new[] { GpoSide.Machine, GpoSide.User })
            {
                GpoSideSnapshot one = snapshot.Side(side);
                if (one.Pol is null)
                {
                    string path = GpoFileNames.PolPath(choice.Policy.GpoRoot, side);
                    problems.Add(new GpeditProblem(GpeditProblemKind.PolicyFile, path, one.PolFailure?.GetType().Name ?? "Unreadable"));
                }

                if (one.Comments is null)
                {
                    string path = GpoFileNames.CmtxPath(choice.Policy.GpoRoot, side);
                    problems.Add(new GpeditProblem(GpeditProblemKind.CommentFile, path, one.CommentsFailure?.GetType().Name ?? "Unreadable"));
                }
            }

            if (snapshot.GptIni is null)
            {
                string path = GpoFileNames.GptIniPath(choice.Policy.GpoRoot);
                problems.Add(new GpeditProblem(GpeditProblemKind.VersionFile, path, snapshot.GptIniFailure?.GetType().Name ?? "Unreadable"));
            }

            return snapshot;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            problems.Add(new GpeditProblem(GpeditProblemKind.LoadException, choice.Policy.GpoRoot, "Open: " + ex.GetType().Name));
            Log.Warn("The policy object could not be opened: " + ex.Message);
            return null;
        }
    }

    /// <summary>Display order of categories: display name in the user locale, ignoring case, then source order.</summary>
    private sealed class CategoryOrder : IComparer<PolicyCategory>
    {
        public static CategoryOrder Instance { get; } = new();

        public int Compare(PolicyCategory? x, PolicyCategory? y)
        {
            int byName = string.Compare(x!.DisplayName, y!.DisplayName, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
            return byName != 0 ? byName : x.SourceOrder.CompareTo(y.SourceOrder);
        }
    }

    /// <summary>
    /// The default order of the policies of a category: the policy id (the name attribute) compared ordinally, then the
    /// namespace, then the source order. 'L' is below '_' in an ordinal compare, which the captures settle.
    /// </summary>
    private sealed class IdOrder : IComparer<PolicyDefinition>
    {
        public static IdOrder Instance { get; } = new();

        public int Compare(PolicyDefinition? x, PolicyDefinition? y)
        {
            int byId = string.CompareOrdinal(x!.Name, y!.Name);
            if (byId != 0)
            {
                return byId;
            }

            int byNamespace = string.CompareOrdinal(x.Namespace, y.Namespace);
            return byNamespace != 0 ? byNamespace : x.SourceOrder.CompareTo(y.SourceOrder);
        }
    }

    /// <summary>Display order of policies: the same rule as <see cref="CategoryOrder"/>.</summary>
    private sealed class PolicyOrder : IComparer<PolicyDefinition>
    {
        public static PolicyOrder Instance { get; } = new();

        public int Compare(PolicyDefinition? x, PolicyDefinition? y)
        {
            int byName = string.Compare(x!.DisplayName, y!.DisplayName, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
            return byName != 0 ? byName : x.SourceOrder.CompareTo(y.SourceOrder);
        }
    }
}

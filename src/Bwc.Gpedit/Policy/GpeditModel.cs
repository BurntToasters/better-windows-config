using System.Diagnostics;
using System.Globalization;
using Bwc.Core;
using Bwc.Gpo.Comments;
using Bwc.Gpo.Pol;
using Bwc.Gpo.Store;
using Bwc.Policy.Admx;
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

/// <summary>One recorded read problem. G6 shows the list; this piece only records and counts it.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Path">The file or folder, or an empty string.</param>
/// <param name="Detail">The reason code or message, without user-facing wording.</param>
internal sealed record GpeditProblem(GpeditProblemKind Kind, string Path, string Detail);

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

    /// <summary>The policies of a category that apply to a side, in display order.</summary>
    public IReadOnlyList<PolicyDefinition> PoliciesOf(PolicyCategory category, GpoSide side) =>
        category.Policies
            .Where(p => side == GpoSide.Machine ? p.AppliesToMachine : p.AppliesToUser)
            .OrderBy(p => p, PolicyOrder.Instance)
            .ToList();

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
                problems.Add(new GpeditProblem(GpeditProblemKind.TemplateFailure, failure.FilePath, failure.Code.ToString()));
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

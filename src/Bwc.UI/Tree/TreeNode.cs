namespace Bwc.UI.Tree;

/// <summary>
/// A replaceable source of child nodes. The demo data implements this interface; a later registry
/// source can replace it without changing <see cref="DenseTree"/>.
/// </summary>
public interface ITreeNodeSource
{
    Task<IReadOnlyList<TreeNode>> LoadChildrenAsync(TreeNode node, CancellationToken cancellationToken);
}

/// <summary>Delegate form of <see cref="ITreeNodeSource"/> for small hand-built trees.</summary>
public delegate Task<IReadOnlyList<TreeNode>> TreeNodeLoader(TreeNode node, CancellationToken cancellationToken);

/// <summary>
/// One registry-shaped tree node. Children are loaded once on first expand and cached. A node that
/// is expanded before its children are loaded (for example the demo's Computer root) can also have
/// children added directly.
/// </summary>
public sealed class TreeNode
{
    private readonly List<TreeNode> _children = new();
    private bool _childrenLoaded;

    public TreeNode(
        string name,
        string? registryPath = null,
        bool hasChildren = false,
        TreeNodeLoader? loader = null,
        ITreeNodeSource? source = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
        RegistryPath = registryPath;
        HasChildren = hasChildren;
        Source = source;
        Loader = loader ?? (source is null ? null : source.LoadChildrenAsync);
    }

    /// <summary>Display name. Registry key names are case-insensitive; the spelling here is preserved.</summary>
    public string Name { get; set; }

    /// <summary>The registry path when this node came from a registry source, otherwise null.</summary>
    public string? RegistryPath { get; set; }

    /// <summary>Zero for a root node. Children are one deeper than their parent.</summary>
    public int Depth { get; internal set; }

    /// <summary>True when the node can show children. It may be true before children are loaded.</summary>
    public bool HasChildren { get; set; }

    /// <summary>True while the node is showing its children in the flattened list.</summary>
    public bool IsExpanded { get; set; }

    /// <summary>The parent node, or null for a root.</summary>
    public TreeNode? Parent { get; private set; }

    /// <summary>The loaded children. Empty until <see cref="LoadChildrenAsync"/> completes.</summary>
    public IReadOnlyList<TreeNode> Children => _children;

    /// <summary>The optional source that produced the lazy loader.</summary>
    public ITreeNodeSource? Source { get; }

    /// <summary>The optional lazy loader. It is called once.</summary>
    public TreeNodeLoader? Loader { get; }

    /// <summary>Adds a child, assigns its parent, and fixes its depth.</summary>
    public TreeNode AddChild(TreeNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        child.Parent = this;
        child.Depth = Depth + 1;
        _children.Add(child);
        HasChildren = true;
        return child;
    }

    /// <summary>Adds every child in order.</summary>
    public void AddChildren(IEnumerable<TreeNode> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        foreach (var child in children)
        {
            AddChild(child);
        }
    }

    /// <summary>
    /// Returns the children, invoking the loader at most once. Cancellation is passed through so a
    /// collapse during loading does not leave a late result installed.
    /// </summary>
    public async Task<IReadOnlyList<TreeNode>> LoadChildrenAsync(CancellationToken cancellationToken)
    {
        if (_childrenLoaded || _children.Count > 0)
        {
            _childrenLoaded = true;
            return _children;
        }

        if (Loader is null)
        {
            _childrenLoaded = true;
            return _children;
        }

        var loaded = await Loader(this, cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();

        if (_children.Count == 0)
        {
            AddChildren(loaded);
        }

        _childrenLoaded = true;
        return _children;
    }
}

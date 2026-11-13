using Bwc.Core;
using Bwc.Registry;
using Bwc.UI.Tree;

namespace Bwc.Regedit.Registry;

/// <summary>
/// Lazy read-only tree source over the live registry. Construction is free: the virtual
/// <c>Computer</c> node and its five roots are created without touching the registry, and every
/// child list is enumerated on the thread pool when a node is expanded or restored. Connected
/// remote computers are appended after the five local roots.
/// </summary>
public sealed class RegistryTreeSource : ITreeNodeSource
{
    /// <summary>Win32 ERROR_ACCESS_DENIED and ERROR_PRIVILEGE_NOT_HELD: a key the user cannot enumerate.</summary>
    private const int AccessDeniedError = 5;
    private const int PrivilegeNotHeldError = 1314;

    /// <summary>Longest key name component the registry accepts, in characters.</summary>
    private const int MaxKeyNameLength = 255;

    private static readonly StringComparer NameComparer = StringComparer.CurrentCultureIgnoreCase;
    private static readonly IReadOnlyList<TreeNode> NoChildren = Array.Empty<TreeNode>();

    private static readonly RegistryRoot[] RootOrder =
    [
        RegistryRoot.ClassesRoot,
        RegistryRoot.CurrentUser,
        RegistryRoot.LocalMachine,
        RegistryRoot.Users,
        RegistryRoot.CurrentConfig,
    ];

    private readonly RegistryReader _reader;

    public RegistryTreeSource(
        RegistryReader? reader = null,
        IReadOnlyList<RemoteRegistryConnection>? remoteConnections = null)
    {
        _reader = reader ?? new RegistryReader();
        Computer = new TreeNode("Computer", registryPath: null, hasChildren: true, source: this)
        {
            IsExpanded = true,
        };

        foreach (RegistryRoot root in RootOrder)
        {
            string displayName = root.DisplayName();
            string displayPath = RegistryPath.Parse(displayName).ToDisplayString();
            Computer.AddChild(new TreeNode(displayName, displayPath, hasChildren: true, source: this));
        }

        Roots = [Computer];

        if (remoteConnections is not null)
        {
            foreach (RemoteRegistryConnection connection in remoteConnections)
            {
                AddRemoteComputer(connection);
            }
        }
    }

    /// <summary>The always-expanded virtual root that owns the five predefined keys and any remote computers.</summary>
    public TreeNode Computer { get; }

    /// <summary>The roots passed to <c>DenseTree.SetRoots</c>. Only the virtual Computer node.</summary>
    public IReadOnlyList<TreeNode> Roots { get; }

    /// <summary>
    /// Appends one connected computer as a child of the virtual Computer node. A disposed
    /// connection or a name that is already present is ignored.
    /// </summary>
    public void AddRemoteComputer(RemoteRegistryConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        string nodeName = RemoteRegistryTreeSource.DisplayPrefix(connection.ComputerName);
        if (FindChild(Computer, nodeName) is not null)
        {
            Log.Warn($"Registry tree already shows the remote computer '{connection.ComputerName}'.");
            return;
        }

        RemoteRegistryTreeSource source;
        try
        {
            source = new RemoteRegistryTreeSource(connection);
        }
        catch (ObjectDisposedException ex)
        {
            Log.Warn(
                $"Registry tree ignored the disposed remote computer '{connection.ComputerName}': {ex.Message}");
            return;
        }

        Computer.AddChild(new TreeNode(
            nodeName,
            registryPath: nodeName,
            hasChildren: true,
            source: source));
    }

    public Task<IReadOnlyList<TreeNode>> LoadChildrenAsync(TreeNode node, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node.RegistryPath is not { Length: > 0 } displayPath)
        {
            return Task.FromResult(node.Children);
        }

        if (!RegistryPath.TryParse(displayPath, out RegistryPath? path))
        {
            Log.Warn($"Registry tree node '{node.Name}' has an invalid path '{displayPath}'.");
            return Task.FromResult(NoChildren);
        }

        return Task.Run(() => LoadChildren(path, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Walks or rebuilds the path from the virtual Computer node. Ancestors are marked expanded so
    /// a following <c>SetRoots</c> shows the restored key. Returns null when the path is invalid,
    /// the key disappeared, or a read failed.
    /// </summary>
    public async Task<TreeNode?> FindPathAsync(string displayPath)
    {
        if (string.IsNullOrWhiteSpace(displayPath))
        {
            return null;
        }

        // Names are not trimmed: a key may end in spaces, and the path must match it exactly.
        if (string.Equals(displayPath, Computer.Name, StringComparison.OrdinalIgnoreCase))
        {
            return Computer;
        }

        if (!RegistryPath.TryParse(displayPath, out RegistryPath? path))
        {
            Log.Warn($"Registry tree could not restore '{displayPath}': the path is not valid.");
            return null;
        }

        try
        {
            TreeNode? current = FindChild(Computer, path.Root.DisplayName());
            if (current is null)
            {
                return null;
            }

            foreach (string component in path.Components)
            {
                await current.LoadChildrenAsync(CancellationToken.None).ConfigureAwait(true);
                TreeNode? next = FindChild(current, component);
                if (next is null)
                {
                    return null;
                }

                current.IsExpanded = true;
                current = next;
            }

            for (TreeNode? ancestor = current.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                ancestor.IsExpanded = true;
            }

            return current;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Registry tree could not restore '{displayPath}': {ex.Message}");
            return null;
        }
    }

    private IReadOnlyList<TreeNode> LoadChildren(RegistryPath path, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> names;
        try
        {
            names = _reader.EnumerateSubKeyNames(path, cancellationToken);
        }
        catch (RegistryOperationException ex) when (ex.Win32Error is AccessDeniedError or PrivilegeNotHeldError)
        {
            // The expand fails with the classic message. The tree raises ExpandFailed for it, so the
            // error is not turned into an empty list here.
            Log.Warn(
                $"Registry tree could not open '{path.ToDisplayString()}': Win32 {ex.Win32Error} ({ex.Message}).");
            throw;
        }
        catch (RegistryOperationException ex)
        {
            Log.Warn(
                $"Registry tree could not enumerate '{path.ToDisplayString()}': Win32 {ex.Win32Error} ({ex.Message}).");
            return NoChildren;
        }

        // The contract asks for the native linguistic order. The managed culture comparer is
        // the closest equivalent; punctuation and digit runs can differ in edge cases.
        List<string> sorted = names.OrderBy(name => name, NameComparer).ToList();
        var children = new List<TreeNode>(sorted.Count);

        foreach (string name in sorted)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsRepresentable(name))
            {
                // RegistryPath cannot name '.' or '..'. The key is still listed, under a display
                // path built from its parent. It has no children and no values to read.
                children.Add(new TreeNode(
                    name,
                    path.ToDisplayString() + @"\" + name,
                    hasChildren: false,
                    source: this));
                continue;
            }

            RegistryPath childKey = path.Append(name);
            children.Add(new TreeNode(
                name,
                childKey.ToDisplayString(),
                hasChildren: HasSubKeys(childKey, cancellationToken),
                source: this));
        }

        return children;
    }

    /// <summary>
    /// True when <see cref="RegistryPath.Append"/> accepts the name. The path type rejects '.' and '..',
    /// which a real key can still have.
    /// </summary>
    private static bool IsRepresentable(string name) =>
        name.Length is > 0 and <= MaxKeyNameLength &&
        name is not ("." or "..") &&
        name.IndexOf('\0') < 0;

    /// <summary>
    /// True when the key has at least one subkey, from RegQueryInfoKey. A key that cannot be opened
    /// keeps its expander, as classic regedit does: expanding it shows nothing.
    /// </summary>
    private bool HasSubKeys(RegistryPath key, CancellationToken cancellationToken)
    {
        try
        {
            return _reader.GetKeyInfo(key, cancellationToken).SubKeyCount > 0;
        }
        catch (RegistryOperationException)
        {
            return true;
        }
    }

    private static TreeNode? FindChild(TreeNode parent, string name)
    {
        foreach (TreeNode child in parent.Children)
        {
            if (string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }
        }

        return null;
    }
}

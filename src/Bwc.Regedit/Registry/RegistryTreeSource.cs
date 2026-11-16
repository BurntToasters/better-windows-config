using System.Diagnostics.CodeAnalysis;
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

        if (!TryParseNodePath(displayPath, out RegistryPath? path))
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

        if (displayPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return await FindRemotePathAsync(displayPath);
        }

        if (!TryParseNodePath(displayPath, out RegistryPath? path))
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

    /// <summary>
    /// Walks a remote display path, <c>\\COMPUTER\HKEY...\name</c>, from the connected computer's node. Null when
    /// the computer is not connected or a name is missing. Each level is loaded on the way down.
    /// </summary>
    private async Task<TreeNode?> FindRemotePathAsync(string displayPath)
    {
        int separator = displayPath.IndexOf('\\', 2);
        string computerName = separator < 0 ? displayPath : displayPath[..separator];
        TreeNode? current = FindChild(Computer, computerName);
        if (current is null)
        {
            Log.Warn($"Registry tree has no computer node '{computerName}' for '{displayPath}'.");
            return null;
        }

        if (separator < 0)
        {
            return current;
        }

        foreach (string component in displayPath[(separator + 1)..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            _ = await current.LoadChildrenAsync(CancellationToken.None).ConfigureAwait(true);
            TreeNode? next = FindChild(current, component);
            if (next is null)
            {
                Log.Warn($"Registry tree found no '{component}' under '{current.Name}' on the way to '{displayPath}'.");
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

    private IReadOnlyList<TreeNode> LoadChildren(RegistryPath path, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> names;
        try
        {
            names = _reader.EnumerateSubKeyNames(path, cancellationToken);
        }
        catch (RegistryOperationException ex)
        {
            // Every enumeration error propagates, as on the remote tree. The tree raises ExpandFailed, which shows
            // the classic "cannot be opened" message and collapses the node. An empty list would hide the error.
            Log.Warn(
                $"Registry tree could not enumerate '{path.ToDisplayString()}': Win32 {ex.Win32Error} ({ex.Message}).");
            throw;
        }

        // The contract asks for the native linguistic order. The managed culture comparer is
        // the closest equivalent; punctuation and digit runs can differ in edge cases.
        List<string> sorted = names.OrderBy(name => name, NameComparer).ToList();
        var children = new List<TreeNode>(sorted.Count);

        foreach (string name in sorted)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // AppendEnumeratedName accepts '.' and '..', which the registry lists as keys. The other names it refuses
            // (empty, overlong, NUL, separator) cannot come from the registry, so they are logged and skipped.
            RegistryPath childKey;
            try
            {
                childKey = path.AppendEnumeratedName(name);
            }
            catch (ArgumentException ex)
            {
                Log.Warn($"Registry tree skipped the child '{name}' of '{path.ToDisplayString()}': {ex.Message}");
                continue;
            }

            children.Add(new TreeNode(
                name,
                childKey.ToDisplayString(),
                hasChildren: HasSubKeys(childKey, cancellationToken),
                source: this));
        }

        return children;
    }

    /// <summary>
    /// Parses the display path of a tree node into a key path. Unlike <see cref="RegistryPath.TryParse"/>, a
    /// component may be "." or "..", because the registry lists keys with those names. The root and the optional
    /// "Computer" prefix parse as before. A component that is empty or holds a separator or NUL still fails.
    /// </summary>
    public static bool TryParseNodePath(string? displayPath, [NotNullWhen(true)] out RegistryPath? path)
    {
        path = null;
        if (string.IsNullOrEmpty(displayPath))
        {
            return false;
        }

        string[] segments = displayPath.Split('\\');
        int index = segments[0].Equals("Computer", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (index >= segments.Length || !RegistryPath.TryParse(segments[index], out RegistryPath? current))
        {
            return false;
        }

        for (int componentIndex = index + 1; componentIndex < segments.Length; componentIndex++)
        {
            try
            {
                current = current.AppendEnumeratedName(segments[componentIndex]);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        path = current;
        return true;
    }

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

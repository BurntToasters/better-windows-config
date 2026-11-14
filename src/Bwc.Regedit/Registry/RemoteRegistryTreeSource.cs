using Bwc.Core;
using Bwc.Registry;
using Bwc.UI.Tree;

namespace Bwc.Regedit.Registry;

/// <summary>
/// Lazy read-only tree source for one connected remote computer. Remote nodes use the display
/// prefix <c>\\COMPUTER</c> and expose only HKEY_LOCAL_MACHINE and HKEY_USERS, matching classic
/// regedit. Every child list is enumerated on the thread pool when a node is expanded.
/// </summary>
public sealed class RemoteRegistryTreeSource : ITreeNodeSource
{
    private static readonly StringComparer NameComparer = StringComparer.CurrentCultureIgnoreCase;
    private static readonly IReadOnlyList<TreeNode> NoChildren = Array.Empty<TreeNode>();

    private readonly RegistryReader _reader;
    private readonly string _prefix;

    public RemoteRegistryTreeSource(
        RemoteRegistryConnection connection,
        string? computerName = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _reader = connection.CreateReader();
        _prefix = DisplayPrefix(computerName ?? connection.ComputerName);
    }

    /// <summary>The two-backslash display prefix for one computer name.</summary>
    public static string DisplayPrefix(string computerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(computerName);
        return @"\\" + computerName.Trim();
    }

    /// <summary>
    /// Parses a remote display path such as <c>\\COMPUTER\HKEY_LOCAL_MACHINE\Software</c>. Only
    /// the two remote roots are accepted; a local root or a missing computer name is rejected.
    /// </summary>
    public static bool TryParseDisplayPath(string displayPath, out RegistryPath path)
    {
        path = null!;

        if (string.IsNullOrEmpty(displayPath) ||
            !displayPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        int separator = displayPath.IndexOf('\\', 2);
        if (separator <= 2)
        {
            return false;
        }

        string remainder = displayPath[(separator + 1)..];
        string rootToken = remainder.Split('\\', 2)[0];
        if (!rootToken.Equals(RegistryRoot.LocalMachine.DisplayName(), StringComparison.OrdinalIgnoreCase) &&
            !rootToken.Equals(RegistryRoot.Users.DisplayName(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            path = RegistryPath.Parse(remainder);
        }
        catch (FormatException)
        {
            return false;
        }

        return path.Root is RegistryRoot.LocalMachine or RegistryRoot.Users;
    }

    public Task<IReadOnlyList<TreeNode>> LoadChildrenAsync(
        TreeNode node,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node.RegistryPath is not { Length: > 0 } displayPath)
        {
            return Task.FromResult(node.Children);
        }

        if (string.Equals(displayPath, _prefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(CreateRootNodes());
        }

        if (!TryParseDisplayPath(displayPath, out RegistryPath path))
        {
            Log.Warn(
                $"Remote registry tree node '{node.Name}' has an invalid path '{displayPath}'.");
            return Task.FromResult(NoChildren);
        }

        return Task.Run(() => LoadChildren(path, cancellationToken), cancellationToken);
    }

    private IReadOnlyList<TreeNode> CreateRootNodes()
    {
        string localMachine = RegistryRoot.LocalMachine.DisplayName();
        string users = RegistryRoot.Users.DisplayName();

        return new[]
        {
            new TreeNode(
                localMachine,
                _prefix + "\\" + localMachine,
                hasChildren: true,
                source: this),
            new TreeNode(
                users,
                _prefix + "\\" + users,
                hasChildren: true,
                source: this),
        };
    }

    private IReadOnlyList<TreeNode> LoadChildren(RegistryPath path, CancellationToken cancellationToken)
    {
        // A registry error propagates: the tree collapses the node and shows the classic Cannot open key message.
        try
        {
            IReadOnlyList<string> names = _reader.EnumerateSubKeyNames(path, cancellationToken);
            List<string> sorted = names.OrderBy(name => name, NameComparer).ToList();
            var children = new List<TreeNode>(sorted.Count);

            foreach (string name in sorted)
            {
                cancellationToken.ThrowIfCancellationRequested();

                RegistryPath childPath;
                try
                {
                    childPath = path.Append(name);
                }
                catch (ArgumentException ex)
                {
                    // A real registry key can be named '.' or '..', which RegistryPath rejects
                    // because the same string is ambiguous in a display path.
                    Log.Warn(
                        $"Remote registry tree skipped the child '{name}' of '{path.ToDisplayString()}': {ex.Message}");
                    continue;
                }

                children.Add(new TreeNode(
                    name,
                    ToRemoteDisplayPath(childPath),
                    hasChildren: true,
                    source: this));
            }

            return children;
        }
        catch (ObjectDisposedException ex)
        {
            Log.Warn(
                $"Remote registry tree stopped reading '{path.ToDisplayString()}' because the connection was closed: {ex.Message}");
            return NoChildren;
        }
    }

    private string ToRemoteDisplayPath(RegistryPath path)
    {
        string root = path.Root.DisplayName();
        return path.Components.Count == 0
            ? _prefix + "\\" + root
            : _prefix + "\\" + root + "\\" + string.Join('\\', path.Components);
    }
}

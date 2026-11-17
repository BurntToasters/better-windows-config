using Bwc.Regedit.Registry;
using Bwc.UI.Tree;

namespace Bwc.Regedit.Controllers;

/// <summary>
/// The address bar auto-suggest list. The parent is the text up to the last backslash, and the entries are the
/// parent's subkeys that start with the rest of the text, as full paths. A remote path gets no entries. The walk
/// loads children without expanding the tree, so opening the list does not change what the tree shows.
/// </summary>
internal static class AddressSuggestions
{
    /// <summary>The most entries the list shows. A longer list is cut, in name order.</summary>
    public const int MaxEntries = 64;

    private const string ComputerName = "Computer";

    public static async Task<IReadOnlyList<string>> ComputeAsync(
        RegistryTreeSource source,
        string text,
        CancellationToken cancellationToken)
    {
        string typed = (text ?? string.Empty).Trim();
        if (typed.Length == 0 || typed.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return Array.Empty<string>();
        }

        string parentPath;
        string prefix;
        if (string.Equals(typed, ComputerName, StringComparison.OrdinalIgnoreCase))
        {
            parentPath = ComputerName;
            prefix = string.Empty;
        }
        else
        {
            int slash = typed.LastIndexOf('\\');
            parentPath = slash <= 0 ? ComputerName : typed[..slash];
            prefix = slash < 0 ? typed : typed[(slash + 1)..];
        }

        TreeNode? parent = await FindWithoutExpandingAsync(source, parentPath, cancellationToken);
        if (parent is null)
        {
            return Array.Empty<string>();
        }

        // The Computer node holds the five hives as children, and its own children load without a registry read.
        IReadOnlyList<TreeNode> children = await parent.LoadChildrenAsync(cancellationToken);

        return children
            .Where(child => child.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxEntries)
            .Select(child => parentPath + "\\" + child.Name)
            .ToArray();
    }

    /// <summary>Walks the tree by name from Computer, loading children on the way and expanding nothing.</summary>
    private static async Task<TreeNode?> FindWithoutExpandingAsync(
        RegistryTreeSource source,
        string displayPath,
        CancellationToken cancellationToken)
    {
        string[] parts = displayPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && string.Equals(parts[0], ComputerName, StringComparison.OrdinalIgnoreCase))
        {
            parts = parts[1..];
        }

        TreeNode current = source.Computer;
        foreach (string part in parts)
        {
            IReadOnlyList<TreeNode> children = await current.LoadChildrenAsync(cancellationToken);
            TreeNode? next = children.FirstOrDefault(child => string.Equals(child.Name, part, StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                return null;
            }

            current = next;
        }

        return current;
    }
}

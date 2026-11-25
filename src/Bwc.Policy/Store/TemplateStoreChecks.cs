using Bwc.Gpo.Store;
using Bwc.Update;

namespace Bwc.Policy.Store;

/// <summary>What a read-only look at the store folder found.</summary>
public enum TemplateStoreStatus
{
    /// <summary>The folder does not exist. Normal before the first import.</summary>
    Missing,

    /// <summary>The folder exists, is not a link and passes the ACL rules.</summary>
    Ready,

    /// <summary>The path is a file.</summary>
    NotADirectory,

    /// <summary>The folder is a reparse point.</summary>
    ReparsePoint,

    /// <summary>The owner or the access list of the folder is not acceptable. The detail says why.</summary>
    AclRejected,

    /// <summary>The folder cannot be inspected.</summary>
    Unreadable,
}

/// <summary>The read-only checks that the sources, the import and the writer share. Nothing here writes.</summary>
internal static class TemplateStoreChecks
{
    /// <summary>The full path with no trailing separator, or null for empty, invalid or relative text.</summary>
    internal static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathRooted(path))
        {
            return null;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }

        var rootLength = Path.GetPathRoot(full)?.Length ?? 0;
        while (full.Length > rootLength && (full[^1] == Path.DirectorySeparatorChar || full[^1] == Path.AltDirectorySeparatorChar))
        {
            full = full[..^1];
        }

        return full;
    }

    /// <summary>True when <paramref name="path"/> is strictly below <paramref name="root"/> on a folder boundary.</summary>
    internal static bool IsUnder(string root, string path) =>
        path.Length > root.Length
        && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
        && (root[^1] == Path.DirectorySeparatorChar || path[root.Length] == Path.DirectorySeparatorChar);

    internal static bool IsSame(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the path is a reparse point. A missing path is not one; an unreadable path is.</summary>
    internal static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    /// <summary>True for a UNC path, a device path or a drive of the network type.</summary>
    internal static bool IsNotLocal(string full)
    {
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            var root = Path.GetPathRoot(full);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The folder that holds the store (the BWC data root): its parent. Null for a path that has none, which the writer
    /// refuses as a drive root before it gets here.
    /// </summary>
    internal static string? DataRootOf(string storeFolder) => Path.GetDirectoryName(storeFolder);

    /// <summary>
    /// Looks at a store or language folder and every folder from the data root down to it: existence, reparse point, owner
    /// and access list (`ProtectedDataRoot.Verify`). A missing data root or store is <see cref="TemplateStoreStatus.Missing"/>.
    /// When a folder above the one asked about fails, the detail names that folder.
    /// </summary>
    /// <param name="dataRoot">The data root, the parent of the store folder.</param>
    /// <param name="folder">The store folder or a language folder under it.</param>
    /// <param name="acl">The owner and access rules.</param>
    internal static (TemplateStoreStatus Status, string Detail) Inspect(string dataRoot, string folder, ProtectedAcl acl)
    {
        ProtectedFolderProblem? problem;
        try
        {
            problem = ProtectedDataRoot.Verify(dataRoot, folder, acl);
        }
        catch (ArgumentException)
        {
            return (TemplateStoreStatus.Unreadable, folder);
        }

        return problem is null ? (TemplateStoreStatus.Ready, string.Empty) : Map(problem, folder);
    }

    /// <summary>Looks at a store folder and the data root above it.</summary>
    internal static (TemplateStoreStatus Status, string Detail) Inspect(string folder, ProtectedAcl acl)
    {
        var root = DataRootOf(folder);
        return root is null ? (TemplateStoreStatus.Unreadable, folder) : Inspect(root, folder, acl);
    }

    private static (TemplateStoreStatus Status, string Detail) Map(ProtectedFolderProblem problem, string asked)
    {
        var detail = problem.Status switch
        {
            ProtectedFolderStatus.OwnerRejected or ProtectedFolderStatus.AclRejected or ProtectedFolderStatus.Unreadable =>
                IsSame(problem.Folder, asked) ? problem.Detail : problem.Folder + ": " + problem.Detail,
            ProtectedFolderStatus.Missing => string.Empty,
            _ => problem.Folder,
        };
        var status = problem.Status switch
        {
            ProtectedFolderStatus.Missing => TemplateStoreStatus.Missing,
            ProtectedFolderStatus.NotADirectory => TemplateStoreStatus.NotADirectory,
            ProtectedFolderStatus.ReparsePoint => TemplateStoreStatus.ReparsePoint,
            ProtectedFolderStatus.OwnerRejected or ProtectedFolderStatus.AclRejected => TemplateStoreStatus.AclRejected,
            _ => TemplateStoreStatus.Unreadable,
        };
        return (status, detail);
    }
}

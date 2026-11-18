using System.Security.AccessControl;
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

    /// <summary>Looks at a store or language folder: existence, reparse point, owner and access list.</summary>
    internal static (TemplateStoreStatus Status, string Detail) Inspect(string folder, ProtectedAcl acl)
    {
        try
        {
            if (File.Exists(folder))
            {
                return (TemplateStoreStatus.NotADirectory, folder);
            }

            if (!Directory.Exists(folder))
            {
                return (TemplateStoreStatus.Missing, string.Empty);
            }

            if (IsReparsePoint(folder))
            {
                return (TemplateStoreStatus.ReparsePoint, folder);
            }

            var why = acl.Inspect(new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
            return why is null ? (TemplateStoreStatus.Ready, string.Empty) : (TemplateStoreStatus.AclRejected, why);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or SystemException)
        {
            return (TemplateStoreStatus.Unreadable, "0x" + e.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}

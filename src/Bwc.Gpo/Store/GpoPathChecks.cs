namespace Bwc.Gpo.Store;

/// <summary>
/// The path checks both write policies share. They read attributes and never write. Paths are compared as full
/// normalized strings, ignoring case, on a folder boundary, so <c>..</c>, doubled separators, other casing and a
/// sibling that shares a name prefix cannot leave a root.
/// </summary>
internal static class GpoPathChecks
{
    /// <summary>Full path without a trailing separator (except a drive root); false for empty or invalid text.</summary>
    internal static bool TryNormalize(string? path, out string full)
    {
        full = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return false;
        }

        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return false;
        }

        var rootLength = Path.GetPathRoot(full)?.Length ?? 0;
        while (full.Length > rootLength && (full[^1] == Path.DirectorySeparatorChar || full[^1] == Path.AltDirectorySeparatorChar))
        {
            full = full[..^1];
        }

        return true;
    }

    /// <summary>True when <paramref name="path"/> is strictly below <paramref name="root"/> on a folder boundary.</summary>
    internal static bool IsUnder(string root, string path)
    {
        if (path.Length <= root.Length || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return root[^1] == Path.DirectorySeparatorChar || path[root.Length] == Path.DirectorySeparatorChar;
    }

    internal static bool IsSameOrUnder(string root, string path) =>
        string.Equals(root, path, StringComparison.OrdinalIgnoreCase) || IsUnder(root, path);

    /// <summary>
    /// True when an existing component of <paramref name="path"/> is a reparse point, walking up to
    /// <paramref name="boundary"/>. A component that cannot be inspected counts as a reparse point (the safe side).
    /// </summary>
    /// <param name="boundary">The folder where the walk stops.</param>
    /// <param name="path">The path to inspect.</param>
    /// <param name="includePath">Whether the final component itself is inspected.</param>
    /// <param name="includeBoundary">Whether the boundary folder itself is inspected.</param>
    internal static bool HasReparsePoint(string boundary, string path, bool includePath, bool includeBoundary)
    {
        var current = includePath ? path : Path.GetDirectoryName(path);
        while (current is not null && IsSameOrUnder(boundary, current))
        {
            var atBoundary = string.Equals(current, boundary, StringComparison.OrdinalIgnoreCase);
            if ((!atBoundary || includeBoundary) && IsReparsePoint(current))
            {
                return true;
            }

            if (atBoundary)
            {
                break;
            }

            current = Path.GetDirectoryName(current);
        }

        return false;
    }

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

    /// <summary>The shared <c>EnsureWriteAllowed</c> and <c>EnsureRemoveAllowed</c> body.</summary>
    internal static void Check(string path, IReadOnlyList<string> roots, bool removing)
    {
        var operation = removing ? "Remove" : "Write";
        if (!TryNormalize(path, out var full))
        {
            throw Denied(operation, path ?? string.Empty, "the path is empty or not valid");
        }

        string? root = null;
        foreach (var candidate in roots)
        {
            if (IsUnder(candidate, full))
            {
                root = candidate;
                break;
            }
        }

        if (root is null)
        {
            throw Denied(operation, full, "the path is outside the GPO folder and the backup folder");
        }

        if (full.IndexOf(':', root.Length) >= 0)
        {
            throw Denied(operation, full, "the path names an alternate data stream");
        }

        if (!removing && Directory.Exists(full))
        {
            throw Denied(operation, full, "the path is a folder");
        }

        if (HasReparsePoint(root, full, includePath: !removing, includeBoundary: true))
        {
            throw Denied(operation, full, "the path passes through a reparse point");
        }
    }

    internal static GpoStoreException Denied(string operation, string path, string why) =>
        new(GpoStoreFailure.PolicyDenied, operation, path, 0, $"{operation} of '{path}' refused by the write policy: {why}.");
}

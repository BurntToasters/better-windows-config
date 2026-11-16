using System.Text.RegularExpressions;

namespace Bwc.Update;

/// <summary>
/// The folder where the elevated helper puts its copy of the installer: <c>&lt;install dir&gt;.upd</c>, a sibling of the
/// install folder, created under Program Files so it inherits admin-only access. Each run uses a new 32-hex folder name.
/// Older folders of that shape are removed at the start of the next run, and never through a link.
/// </summary>
public static partial class ProtectedStore
{
    public const string FolderSuffix = ".upd";

    private const int CopyBufferBytes = 1024 * 1024;

    [GeneratedRegex("^[0-9a-f]{32}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex CopyFolderPattern();

    /// <summary><c>&lt;install dir&gt;.upd</c>, next to the install dir (a trailing separator is ignored).</summary>
    public static string RootFor(string installDir) => Path.TrimEndingDirectorySeparator(installDir) + FolderSuffix;

    /// <summary>
    /// Makes sure the root is a real folder: it is created when missing (its parent must exist), and refused when it is a
    /// file, a link, or a junction, or cannot be inspected.
    /// </summary>
    public static Outcome<string> EnsureRoot(string root)
    {
        try
        {
            var parent = Path.GetDirectoryName(root);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            {
                return Outcome<string>.Fail(UpdateFailure.InstallDirUnsafe, "the folder that holds the install folder does not exist");
            }

            if (File.Exists(root) || Directory.Exists(root))
            {
                var attributes = File.GetAttributes(root);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                {
                    return Outcome<string>.Fail(UpdateFailure.PathRejected, "the protected folder is a link or a file");
                }

                return Outcome<string>.Success(root);
            }

            _ = Directory.CreateDirectory(root);
            return Outcome<string>.Success(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Outcome<string>.Fail(UpdateFailure.Io, "the protected folder cannot be prepared: " + ex.GetType().Name);
        }
    }

    /// <summary>
    /// Removes the earlier copy folders under the root: only folders whose name is 32 lowercase hex digits, and never one
    /// that is a link or junction itself (it is left in place). A link inside a folder is removed as a link, without
    /// touching its target. A folder that cannot be removed (a file is in use) is skipped. Returns how many were removed.
    /// </summary>
    public static int RemoveStale(string root)
    {
        var removed = 0;
        string[] folders;
        try
        {
            folders = Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        foreach (var folder in folders)
        {
            if (!CopyFolderPattern().IsMatch(Path.GetFileName(folder)) || !IsRealFolder(folder))
            {
                continue;
            }

            if (TryRemoveTree(folder))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>Creates a new copy folder under the root and returns its path. The name is a new 32-hex id.</summary>
    public static string NewCopyFolder(string root)
    {
        var folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// Copies up to <paramref name="expectedSize"/> bytes of <paramref name="source"/> (from its start) into
    /// <paramref name="destination"/>, which must not exist. A source with more bytes than expected stops the copy at one
    /// byte over and fails <see cref="UpdateFailure.InstallerTooLarge"/>. The caller verifies the copy afterwards.
    /// </summary>
    public static Outcome<long> CopyBounded(Stream source, string destination, long expectedSize)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            source.Position = 0;
            using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[CopyBufferBytes];
            long total = 0;
            while (true)
            {
                var wanted = (int)Math.Min(buffer.Length, expectedSize + 1 - total);
                var read = source.Read(buffer, 0, wanted);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > expectedSize)
                {
                    return Outcome<long>.Fail(UpdateFailure.InstallerTooLarge, "installer is larger than the receipt says");
                }

                target.Write(buffer, 0, read);
            }

            target.Flush(flushToDisk: true);
            return Outcome<long>.Success(total);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Outcome<long>.Fail(UpdateFailure.Io, "installer cannot be copied: " + ex.GetType().Name);
        }
    }

    /// <summary>Removes a folder this run made. Best effort: a file in use leaves the folder for the next run.</summary>
    public static void RemoveFolder(string? folder)
    {
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder) && IsRealFolder(folder))
        {
            _ = TryRemoveTree(folder);
        }
    }

    private static bool IsRealFolder(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.ReparsePoint) == 0 && (attributes & FileAttributes.Directory) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryRemoveTree(string folder)
    {
        try
        {
            DeleteTree(folder);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // A link entry is removed as a link. Its target is never entered, so a planted junction cannot reach other folders.
    private static void DeleteTree(string folder)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
        {
            var attributes = File.GetAttributes(entry);
            var isFolder = (attributes & FileAttributes.Directory) != 0;
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (isFolder)
                {
                    Directory.Delete(entry, recursive: false);
                }
                else
                {
                    File.Delete(entry);
                }

                continue;
            }

            if (isFolder)
            {
                DeleteTree(entry);
            }
            else
            {
                File.SetAttributes(entry, FileAttributes.Normal);
                File.Delete(entry);
            }
        }

        Directory.Delete(folder, recursive: false);
    }
}

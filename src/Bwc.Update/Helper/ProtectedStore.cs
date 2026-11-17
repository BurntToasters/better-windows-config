using System.Security.AccessControl;
using System.Text.RegularExpressions;

namespace Bwc.Update;

/// <summary>
/// The folder where the elevated helper puts its copy of the installer: <c>&lt;install dir&gt;.upd</c>, a sibling of the
/// install folder. It is created with an explicit protected DACL (<see cref="ProtectedAcl"/>: SYSTEM and
/// Administrators only,
/// no inheritance), and an existing one is used only when its owner and DACL pass the same rule. Each run uses a new 32-hex
/// folder name. Older folders of that shape are removed at the start of the next run, and never through a link.
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
    /// Makes sure the root is a real, protected folder. It is created when missing (its parent must exist) with the explicit
    /// DACL of <paramref name="acl"/>. An existing root is refused when it is a file, a link, or a junction, when its owner is
    /// not an owner of <paramref name="acl"/>, or when its DACL grants write to anyone else (HP2, HP3, HP5). The new or
    /// existing folder is inspected after the create, so a folder made by someone else in the gap is refused too.
    /// </summary>
    public static Outcome<string> EnsureRoot(string root, ProtectedAcl acl)
    {
        ArgumentNullException.ThrowIfNull(acl);
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

                return Inspected(root, acl);
            }

            new DirectoryInfo(root).Create(acl.NewSecurity());
            return Inspected(root, acl);
        }
        catch (Exception ex) when (IsAclFailure(ex))
        {
            return AclFailure(ex, "the protected folder cannot be prepared");
        }
    }

    /// <summary>
    /// Creates one new folder with the explicit protected DACL of <paramref name="acl"/> and checks the result: not a link,
    /// owner and DACL as the rule says. The folder must not exist. Used for the copy folder and its temp folder.
    /// </summary>
    public static Outcome<string> NewProtectedFolder(string path, ProtectedAcl acl)
    {
        ArgumentNullException.ThrowIfNull(acl);
        try
        {
            if (Directory.Exists(path) || File.Exists(path))
            {
                return Outcome<string>.Fail(UpdateFailure.PathRejected, "the folder to create already exists");
            }

            new DirectoryInfo(path).Create(acl.NewSecurity());
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
            {
                return Outcome<string>.Fail(UpdateFailure.PathRejected, "the new folder is a link or a file");
            }

            return Inspected(path, acl);
        }
        catch (Exception ex) when (IsAclFailure(ex))
        {
            return AclFailure(ex, "the folder cannot be created with a protected access list");
        }
    }

    private static Outcome<string> Inspected(string folder, ProtectedAcl acl)
    {
        var reason = acl.Inspect(new DirectoryInfo(folder).GetAccessControl());
        return reason is null
            ? Outcome<string>.Success(folder)
            : Outcome<string>.Fail(UpdateFailure.PathRejected, "the protected folder is not trusted: " + reason);
    }

    private static bool IsAclFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException
            or PrivilegeNotHeldException or ArgumentException;

    // An IO error is Io. A refusal by the access control (no right, no privilege, an access list that cannot be set) is
    // PathRejected: there is no fallback to an unprotected folder.
    private static Outcome<string> AclFailure(Exception ex, string text) =>
        ex is IOException
            ? Outcome<string>.Fail(UpdateFailure.Io, text + ": " + ex.GetType().Name)
            : Outcome<string>.Fail(UpdateFailure.PathRejected, text + ": " + ex.GetType().Name);

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

    /// <summary>Name of the temp folder inside a copy folder. The installer's TEMP and TMP name it (HT1).</summary>
    public const string TempFolderName = "tmp";

    /// <summary>Creates a new protected copy folder under the root. The name is a new 32-hex id.</summary>
    public static Outcome<string> NewCopyFolder(string root, ProtectedAcl acl) =>
        NewProtectedFolder(Path.Combine(root, Guid.NewGuid().ToString("N")), acl);

    /// <summary>Creates the protected temp folder inside a copy folder, for the installer's TEMP and TMP.</summary>
    public static Outcome<string> NewTempFolder(string copyFolder, ProtectedAcl acl) =>
        NewProtectedFolder(Path.Combine(copyFolder, TempFolderName), acl);

    /// <summary>
    /// Copies up to <paramref name="expectedSize"/> bytes of <paramref name="source"/> (from its start) into
    /// <paramref name="destination"/>, which must not exist. A source with more bytes than expected stops the copy at one
    /// byte over and fails <see cref="UpdateFailure.InstallerTooLarge"/>. The writer (FileShare.Read) writes and flushes
    /// to disk, and a reader is opened while the writer is still open, so there is no moment when nothing of ours holds the
    /// file (HP7). The writer is then closed and the reader returned: the caller verifies through it and opens the next
    /// reader before closing this one. The reader shares read and write, because the writer was open when it was opened.
    /// </summary>
    public static Outcome<FileStream> CopyBounded(Stream source, string destination, long expectedSize)
    {
        ArgumentNullException.ThrowIfNull(source);
        FileStream? reader = null;
        try
        {
            source.Position = 0;
            using (var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
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
                        return Outcome<FileStream>.Fail(UpdateFailure.InstallerTooLarge, "installer is larger than the receipt says");
                    }

                    target.Write(buffer, 0, read);
                }

                target.Flush(flushToDisk: true);
                reader = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }

            return Outcome<FileStream>.Success(reader);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reader?.Dispose();
            return Outcome<FileStream>.Fail(UpdateFailure.Io, "installer cannot be copied: " + ex.GetType().Name);
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

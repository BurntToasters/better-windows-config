using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Bwc.Core;

internal enum FolderAccess
{
    Ok,
    Missing,
    Unsafe,
}

internal enum FileOpenResult
{
    Opened,
    Missing,
    Unsafe,
    Failed,
}

internal readonly record struct FileFacts(long Length, DateTime LastWriteUtc);

/// <summary>
/// A data folder that an elevated process holds open and has checked. Each folder from the data root down is opened
/// once with <c>FILE_FLAG_OPEN_REPARSE_POINT</c>, refused when it is a reparse point or when its final path is not the
/// expected one, and then kept open for the process lifetime without delete sharing, so it cannot be renamed or
/// replaced while held. Every file operation opens the file the same way, checks it (reparse point, hard link count,
/// final path), and renames or deletes through that handle, never through a path.
/// </summary>
internal sealed class SafeDataFolder
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint ReadAttributes = 0x80;
    private const uint ShareRead = 1;
    private const uint ShareReadWrite = 3;
    private const uint ShareAll = 7;
    private const uint CreateNew = 1;
    private const uint OpenAlways = 4;
    private const uint OpenExisting = 3;
    private const uint FlagBackupSemantics = 0x02000000;
    private const uint FlagOpenReparsePoint = 0x00200000;
    private const uint AttributeDirectory = 0x10;
    private const uint AttributeReparsePoint = 0x400;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int FileRenameInfoEx = 22;
    private const int FileDispositionInfoClass = 4;
    private const int FileDispositionInfoExClass = 21;
    private const int MaxFinalPathLength = 32768;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, SafeDataFolder> Held = new(StringComparer.OrdinalIgnoreCase);

    private readonly SafeFileHandle _handle;

    private SafeDataFolder(string path, string finalPath, SafeFileHandle handle)
    {
        Path = path;
        FinalPath = finalPath;
        _handle = handle;
    }

    /// <summary>The folder path as it was requested.</summary>
    public string Path { get; }

    /// <summary>The path Windows resolved for the held handle, without the device prefix.</summary>
    public string FinalPath { get; }

    /// <summary>Closes every held folder. The process normally keeps them until it exits; tests call this.</summary>
    public static void ReleaseAll()
    {
        lock (Gate)
        {
            foreach (SafeDataFolder folder in Held.Values)
            {
                folder._handle.Dispose();
            }

            Held.Clear();
        }
    }

    /// <summary>
    /// Opens and checks <paramref name="folder"/> and every folder between <paramref name="root"/> and it. A folder
    /// that is already held is reused. With <paramref name="create"/>, missing folders are created under a held,
    /// checked parent; without it, a missing folder reports <see cref="FolderAccess.Missing"/>.
    /// </summary>
    public static SafeDataFolder? Acquire(string root, string folder, bool create, out FolderAccess result)
    {
        result = FolderAccess.Unsafe;
        try
        {
            string rootFull = TrimSeparators(System.IO.Path.GetFullPath(root));
            string folderFull = TrimSeparators(System.IO.Path.GetFullPath(folder));
            string relative = System.IO.Path.GetRelativePath(rootFull, folderFull);
            if (System.IO.Path.IsPathRooted(relative) ||
                relative == ".." ||
                relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return null;
            }

            string[] parts = relative == "."
                ? []
                : relative.Split(System.IO.Path.DirectorySeparatorChar);

            lock (Gate)
            {
                SafeDataFolder? current = OpenRoot(rootFull, create, out result);
                string path = rootFull;
                foreach (string part in parts)
                {
                    if (current is null)
                    {
                        return null;
                    }

                    path = System.IO.Path.Combine(path, part);
                    current = OpenChild(current, path, create, out result);
                }

                return current;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
            or IOException or UnauthorizedAccessException)
        {
            SafeTrace.WriteLine("Data folder check failed: " + ex.Message);
            result = FolderAccess.Unsafe;
            return null;
        }
    }

    /// <summary>Opens a file in this folder with the checks described on the type. The handle is for the caller.</summary>
    public FileOpenResult OpenFile(
        string name,
        uint access,
        uint share,
        uint disposition,
        out SafeFileHandle? handle,
        out FileFacts facts)
    {
        handle = null;
        facts = default;
        if (string.IsNullOrEmpty(name) || name != System.IO.Path.GetFileName(name))
        {
            return FileOpenResult.Unsafe;
        }

        SafeFileHandle opened = CreateFileW(
            System.IO.Path.Combine(FinalPath, name),
            access,
            share,
            IntPtr.Zero,
            disposition,
            FlagOpenReparsePoint,
            IntPtr.Zero);
        if (opened.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            opened.Dispose();
            return error is ErrorFileNotFound or ErrorPathNotFound ? FileOpenResult.Missing : FileOpenResult.Failed;
        }

        if (!TryQuery(opened, out uint attributes, out uint links, out facts) ||
            (attributes & AttributeDirectory) != 0 ||
            !DataPathPolicy.AllowFile(
                elevated: true,
                (attributes & AttributeReparsePoint) != 0,
                FinalPathMatches(opened, System.IO.Path.Combine(FinalPath, name)),
                links))
        {
            opened.Dispose();
            facts = default;
            return FileOpenResult.Unsafe;
        }

        handle = opened;
        return FileOpenResult.Opened;
    }

    /// <summary>Appends bytes, creating the file when absent. False when the file is refused or not writable.</summary>
    public bool Append(string name, byte[] bytes)
    {
        if (OpenFile(name, GenericWrite, ShareAll, OpenAlways, out SafeFileHandle? handle, out _) != FileOpenResult.Opened)
        {
            return false;
        }

        // The stream owns the handle from here and closes it.
        using var stream = new FileStream(handle!, FileAccess.Write, 1);
        stream.Seek(0, SeekOrigin.End);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
        return true;
    }

    /// <summary>Creates the file when absent. False when the file is refused or cannot be opened.</summary>
    public bool EnsureFile(string name)
    {
        if (OpenFile(name, GenericWrite, ShareAll, OpenAlways, out SafeFileHandle? handle, out _) != FileOpenResult.Opened)
        {
            return false;
        }

        handle!.Dispose();
        return true;
    }

    /// <summary>The length of a usable file, or 0 when it is missing or refused.</summary>
    public long Length(string name)
    {
        if (OpenFile(name, ReadAttributes, ShareAll, OpenExisting, out SafeFileHandle? handle, out FileFacts facts) !=
            FileOpenResult.Opened)
        {
            return 0;
        }

        handle!.Dispose();
        return facts.Length;
    }

    /// <summary>True unless the name is certainly absent. A refused or unreadable entry counts as present.</summary>
    public bool Exists(string name)
    {
        FileOpenResult result = OpenFile(name, ReadAttributes, ShareAll, OpenExisting, out SafeFileHandle? handle, out _);
        handle?.Dispose();
        return result != FileOpenResult.Missing;
    }

    /// <summary>The names of the files in this folder that match the pattern.</summary>
    public List<string> Names(string pattern)
    {
        var names = new List<string>();
        foreach (string full in System.IO.Directory.EnumerateFiles(FinalPath, pattern, SearchOption.TopDirectoryOnly))
        {
            names.Add(System.IO.Path.GetFileName(full));
        }

        return names;
    }

    /// <summary>
    /// Deletes a file through a checked handle. With <paramref name="olderThanUtc"/>, only when its last write is older.
    /// False when the file is missing, refused, newer, or the delete failed.
    /// </summary>
    public bool Delete(string name, DateTime? olderThanUtc = null)
    {
        if (OpenFile(name, DeleteAccess | ReadAttributes, ShareAll, OpenExisting, out SafeFileHandle? handle, out FileFacts facts) !=
            FileOpenResult.Opened)
        {
            return false;
        }

        using (handle)
        {
            if (olderThanUtc is not null && facts.LastWriteUtc >= olderThanUtc.Value)
            {
                return false;
            }

            return MarkForDeletion(handle!);
        }
    }

    /// <summary>Marks an open handle (opened with delete access) for deletion on close.</summary>
    public static bool MarkForDeletion(SafeFileHandle handle)
    {
        uint flags = 0x3; // FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS
        if (SetFileInformationByHandle(handle, FileDispositionInfoExClass, ref flags, sizeof(uint)))
        {
            return true;
        }

        byte legacy = 1;
        return SetFileInformationByHandle(handle, FileDispositionInfoClass, ref legacy, sizeof(byte));
    }

    /// <summary>
    /// Renames an open handle (opened with delete access) to a name in this folder. The rename is applied to the
    /// handle, and the target is the held folder's final path (an NT path, since the Win32 call does not accept a
    /// root directory handle), which cannot be swapped while the folder is held. Throws <see cref="IOException"/>.
    /// </summary>
    public void Rename(SafeFileHandle file, string newName, bool replace)
    {
        string target = (FinalPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\??\UNC\" + FinalPath[2..]
            : @"\??\" + FinalPath) + @"\" + newName;
        int nameBytes = target.Length * sizeof(char);
        int size = 20 + nameBytes + sizeof(char);
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            // FILE_RENAME_INFO on a 64-bit process: Flags at 0, RootDirectory (null) at 8, FileNameLength at 16, name at 20.
            for (int i = 0; i < size; i++)
            {
                Marshal.WriteByte(buffer, i, 0);
            }

            Marshal.WriteInt32(buffer, 0, replace ? 1 : 0);
            Marshal.WriteInt32(buffer, 16, nameBytes);
            Marshal.Copy(target.ToCharArray(), 0, buffer + 20, target.Length);
            if (!SetFileInformationByHandle(file, FileRenameInfoEx, buffer, (uint)size))
            {
                throw new IOException(
                    "Could not rename to '" + newName + "': " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Flushes a written file to disk.</summary>
    public static void Flush(SafeFileHandle file)
    {
        if (!FlushFileBuffers(file))
        {
            throw new IOException("Could not flush: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }
    }

    private static SafeDataFolder? OpenRoot(string rootFull, bool create, out FolderAccess result)
    {
        if (Held.TryGetValue(rootFull, out SafeDataFolder? cached))
        {
            result = FolderAccess.Ok;
            return cached;
        }

        string? parent = System.IO.Path.GetDirectoryName(rootFull);
        if (parent is null)
        {
            result = FolderAccess.Unsafe;
            return null;
        }

        if (create)
        {
            // A folder that does not exist cannot be a link; the final path check below covers a swap.
            System.IO.Directory.CreateDirectory(parent);
        }

        // The profile may legitimately be relocated by a junction above the data root, so the expectation is built
        // from the parent's final path rather than from the requested path.
        using SafeFileHandle parentHandle = CreateFileW(
            parent,
            ReadAttributes,
            ShareAll,
            IntPtr.Zero,
            OpenExisting,
            FlagBackupSemantics,
            IntPtr.Zero);
        if (parentHandle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            result = error is ErrorFileNotFound or ErrorPathNotFound ? FolderAccess.Missing : FolderAccess.Unsafe;
            return null;
        }

        string? parentFinal = FinalPathOf(parentHandle);
        if (parentFinal is null)
        {
            result = FolderAccess.Unsafe;
            return null;
        }

        string expected = System.IO.Path.Combine(StripPrefix(parentFinal), System.IO.Path.GetFileName(rootFull));
        return OpenHeld(rootFull, expected, create, out result);
    }

    private static SafeDataFolder? OpenChild(SafeDataFolder parent, string path, bool create, out FolderAccess result)
    {
        if (Held.TryGetValue(path, out SafeDataFolder? cached))
        {
            result = FolderAccess.Ok;
            return cached;
        }

        string expected = System.IO.Path.Combine(parent.FinalPath, System.IO.Path.GetFileName(path));
        return OpenHeld(path, expected, create, out result);
    }

    private static SafeDataFolder? OpenHeld(string path, string expectedFinal, bool create, out FolderAccess result)
    {
        SafeFileHandle handle = OpenFolderHandle(expectedFinal);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is not (ErrorFileNotFound or ErrorPathNotFound))
            {
                result = FolderAccess.Unsafe;
                return null;
            }

            if (!create)
            {
                result = FolderAccess.Missing;
                return null;
            }

            // The parent is held and checked, so the new folder is created inside it.
            System.IO.Directory.CreateDirectory(expectedFinal);
            handle = OpenFolderHandle(expectedFinal);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                result = FolderAccess.Unsafe;
                return null;
            }
        }

        string? final = FinalPathOf(handle);
        if (!TryQuery(handle, out uint attributes, out _, out _) ||
            (attributes & AttributeDirectory) == 0 ||
            final is null ||
            !DataPathPolicy.AllowFile(
                elevated: true,
                (attributes & AttributeReparsePoint) != 0,
                SamePath(expectedFinal, final),
                linkCount: 1))
        {
            handle.Dispose();
            result = FolderAccess.Unsafe;
            return null;
        }

        var held = new SafeDataFolder(path, StripPrefix(final), handle);
        Held[path] = held;
        result = FolderAccess.Ok;
        return held;
    }

    private static SafeFileHandle OpenFolderHandle(string path)
    {
        // Read and write sharing, but no delete sharing: the folder cannot be renamed or replaced while held.
        return CreateFileW(
            path,
            GenericRead | GenericWrite,
            ShareReadWrite,
            IntPtr.Zero,
            OpenExisting,
            FlagBackupSemantics | FlagOpenReparsePoint,
            IntPtr.Zero);
    }

    private static bool TryQuery(SafeFileHandle handle, out uint attributes, out uint links, out FileFacts facts)
    {
        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation info))
        {
            attributes = 0;
            links = 0;
            facts = default;
            return false;
        }

        attributes = info.FileAttributes;
        links = info.NumberOfLinks;
        long write = ((long)info.LastWriteTime.High << 32) | info.LastWriteTime.Low;
        facts = new FileFacts(((long)info.FileSizeHigh << 32) | info.FileSizeLow, DateTime.FromFileTimeUtc(write));
        return true;
    }

    private static bool FinalPathMatches(SafeFileHandle handle, string expected)
    {
        string? final = FinalPathOf(handle);
        return final is not null && SamePath(expected, final);
    }

    private static string? FinalPathOf(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(MaxFinalPathLength);
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity)
        {
            return null;
        }

        return buffer.ToString(0, (int)length);
    }

    private static bool SamePath(string expected, string actual)
    {
        try
        {
            return string.Equals(
                System.IO.Path.GetFullPath(StripPrefix(expected)),
                System.IO.Path.GetFullPath(StripPrefix(actual)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string StripPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    private static string TrimSeparators(string path)
    {
        string root = System.IO.Path.GetPathRoot(path) ?? string.Empty;
        return path.Length > root.Length ? path.TrimEnd(System.IO.Path.DirectorySeparatorChar) : path;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

#pragma warning disable SYSLIB1054 // DllImport keeps these signatures plain; the buffers are marshalled directly.
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileW(
        string name,
        uint access,
        uint share,
        IntPtr security,
        uint disposition,
        uint flags,
        IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, IntPtr information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref uint information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref byte information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle file);
#pragma warning restore SYSLIB1054
}

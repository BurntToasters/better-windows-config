using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Win32.SafeHandles;

namespace Bwc.Core;

/// <summary>
/// Loads and saves a settings object through a source-generated <see cref="JsonTypeInfo{T}"/>.
/// Saves replace the destination atomically; corrupt files are preserved beside the original.
/// </summary>
public sealed class SettingsStore<T>
    where T : class, new()
{
    /// <summary>
    /// Gets the largest settings file in bytes that <see cref="Load"/> will read. A larger file is
    /// treated as corrupt: it is moved beside the original and a fresh instance is returned. The
    /// length is checked before the read buffer is allocated, so a hostile file cannot force an
    /// unbounded allocation.
    /// </summary>
    public const long MaxFileBytes = 1L * 1024 * 1024;

    /// <summary>How many <c>.bad-*</c> copies of a corrupt settings file are kept.</summary>
    private const int BadFilesKept = 5;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint ReadAttributes = 0x80;
    private const uint ShareRead = 1;
    private const uint ShareAll = 7;
    private const uint CreateNew = 1;
    private const uint OpenExisting = 3;

    private readonly JsonTypeInfo<T> _jsonTypeInfo;
    private readonly bool _elevated;
    private readonly string? _root;
    private bool _memoryOnly;
    private T? _memory;

    public SettingsStore(string filePath, JsonTypeInfo<T> jsonTypeInfo)
        : this(filePath, jsonTypeInfo, ElevationProbe.IsElevatedOrHighIntegrity(), null)
    {
    }

    /// <summary>
    /// Creates a store that behaves as if the process were (or were not) elevated. When elevated, the file is used
    /// only through the checked, held folders of <see cref="SafeDataFolder"/> under <paramref name="root"/> (null means
    /// <see cref="AppPaths.BaseDirectory"/>); when that is not safe the values live in memory only.
    /// </summary>
    internal SettingsStore(string filePath, JsonTypeInfo<T> jsonTypeInfo, bool elevated, string? root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        Path = System.IO.Path.GetFullPath(filePath);
        _jsonTypeInfo = jsonTypeInfo;
        _elevated = elevated;
        _root = root;
    }

    public string Path { get; }

    public T Load()
    {
        if (_elevated)
        {
            return LoadElevated();
        }

        try
        {
            if (!File.Exists(Path))
            {
                return new T();
            }

            byte[]? json = ReadCappedFile();
            if (json is null)
            {
                return RecoverFromCorruptFile();
            }

            T? value;
            try
            {
                value = JsonSerializer.Deserialize(new ReadOnlySpan<byte>(json), _jsonTypeInfo);
            }
            catch (JsonException)
            {
                return RecoverFromCorruptFile();
            }
            catch (NotSupportedException)
            {
                return RecoverFromCorruptFile();
            }

            return value ?? RecoverFromCorruptFile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException("Failed to load settings from '" + Path + "'.", ex);
        }
    }

    public void Save(T value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (_elevated)
        {
            SaveElevated(value);
            return;
        }

        string? directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            System.IO.Directory.CreateDirectory(directory);
        }

        string temporaryPath = Path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, _jsonTypeInfo);
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, Path, overwrite: true);
        }
        catch
        {
            TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
    }

    private byte[]? ReadCappedFile()
    {
        using var stream = new FileStream(
            Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        long length = stream.Length;
        if (length > MaxFileBytes)
        {
            return null;
        }

        if (length == 0)
        {
            return Array.Empty<byte>();
        }

        byte[] bytes = new byte[(int)length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private T RecoverFromCorruptFile()
    {
        string stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string badPath = Path + ".bad-" + stamp;
        int suffix = 0;
        while (File.Exists(badPath))
        {
            suffix++;
            badPath = Path + ".bad-" + stamp + "-" + suffix.ToString(CultureInfo.InvariantCulture);
        }

        File.Move(Path, badPath);
        PruneBadFiles();
        return new T();
    }

    private void PruneBadFiles()
    {
        try
        {
            string directory = System.IO.Path.GetDirectoryName(Path)!;
            string name = System.IO.Path.GetFileName(Path);
            var names = new List<string>();
            foreach (string full in System.IO.Directory.EnumerateFiles(directory, name + ".bad-*"))
            {
                names.Add(System.IO.Path.GetFileName(full));
            }

            foreach (string old in BadFilesToDelete(name, names))
            {
                try
                {
                    File.Delete(System.IO.Path.Combine(directory, old));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    SafeTrace.WriteLine("Could not delete old corrupt settings copy '" + old + "': " + ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SafeTrace.WriteLine("Could not prune corrupt settings copies for '" + Path + "': " + ex.Message);
        }
    }

    /// <summary>
    /// The oldest names to delete so that only the newest <see cref="BadFilesKept"/> remain. Only the exact names this
    /// store creates count: the file name, <c>.bad-</c>, a <c>yyyyMMddTHHmmssZ</c> stamp, and an optional <c>-n</c> suffix.
    /// </summary>
    private static List<string> BadFilesToDelete(string fileName, IEnumerable<string> names)
    {
        string prefix = fileName + ".bad-";
        var matches = new List<(string Name, string Stamp, int Suffix)>();
        foreach (string name in names)
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string rest = name[prefix.Length..];
            if (rest.Length < 16 || !IsStamp(rest.AsSpan(0, 16)))
            {
                continue;
            }

            int suffix = 0;
            if (rest.Length > 16 &&
                (rest[16] != '-' ||
                 !int.TryParse(rest.AsSpan(17), NumberStyles.None, CultureInfo.InvariantCulture, out suffix)))
            {
                continue;
            }

            matches.Add((name, rest[..16], suffix));
        }

        matches.Sort((a, b) =>
        {
            int stamp = string.CompareOrdinal(a.Stamp, b.Stamp);
            return stamp != 0 ? stamp : a.Suffix.CompareTo(b.Suffix);
        });
        var oldest = new List<string>();
        for (int i = 0; i < matches.Count - BadFilesKept; i++)
        {
            oldest.Add(matches[i].Name);
        }

        return oldest;
    }

    private static bool IsStamp(ReadOnlySpan<char> text)
    {
        for (int i = 0; i < 16; i++)
        {
            bool ok = i == 8 ? text[i] == 'T' : i == 15 ? text[i] == 'Z' : char.IsAsciiDigit(text[i]);
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SafeTrace.WriteLine("Could not delete temporary settings file '" + path + "': " + ex.Message);
        }
    }

    private SafeDataFolder? AcquireFolder(bool create, out FolderAccess access)
    {
        access = FolderAccess.Unsafe;
        string? directory = System.IO.Path.GetDirectoryName(Path);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        string root;
        try
        {
            root = _root ?? AppPaths.BaseDirectory;
        }
        catch (InvalidOperationException ex)
        {
            SafeTrace.WriteLine("No data root for settings: " + ex.Message);
            return null;
        }

        return SafeDataFolder.Acquire(root, directory, create, out access);
    }

    private T GoMemoryOnly()
    {
        _memoryOnly = true;
        SafeTrace.WriteLine("Settings '" + Path + "' is not a safe elevated data path; keeping values in memory only.");
        return _memory ?? new T();
    }

    private T LoadElevated()
    {
        if (_memoryOnly)
        {
            return _memory ?? new T();
        }

        try
        {
            SafeDataFolder? folder = AcquireFolder(create: false, out FolderAccess access);
            if (folder is null)
            {
                return access == FolderAccess.Missing ? new T() : GoMemoryOnly();
            }

            string name = System.IO.Path.GetFileName(Path);
            FileOpenResult opened = folder.OpenFile(
                name,
                GenericRead | DeleteAccess,
                ShareRead,
                OpenExisting,
                out SafeFileHandle? handle,
                out FileFacts facts);
            switch (opened)
            {
                case FileOpenResult.Missing:
                    return new T();
                case FileOpenResult.Unsafe:
                    return GoMemoryOnly();
                case FileOpenResult.Failed:
                    throw new IOException("Could not open the settings file.");
            }

            using (handle)
            {
                if (facts.Length > MaxFileBytes)
                {
                    return RecoverElevated(folder, name, handle!);
                }

                byte[] json = new byte[(int)facts.Length];
                int read = 0;
                while (read < json.Length)
                {
                    int count = RandomAccess.Read(handle!, json.AsSpan(read), read);
                    if (count == 0)
                    {
                        break;
                    }

                    read += count;
                }

                T? value;
                try
                {
                    value = JsonSerializer.Deserialize(new ReadOnlySpan<byte>(json, 0, read), _jsonTypeInfo);
                }
                catch (JsonException)
                {
                    return RecoverElevated(folder, name, handle!);
                }
                catch (NotSupportedException)
                {
                    return RecoverElevated(folder, name, handle!);
                }

                return value ?? RecoverElevated(folder, name, handle!);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException("Failed to load settings from '" + Path + "'.", ex);
        }
    }

    private T RecoverElevated(SafeDataFolder folder, string name, SafeFileHandle handle)
    {
        string stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string badName = name + ".bad-" + stamp;
        int suffix = 0;
        while (folder.Exists(badName))
        {
            suffix++;
            badName = name + ".bad-" + stamp + "-" + suffix.ToString(CultureInfo.InvariantCulture);
        }

        folder.Rename(handle, badName, replace: false);
        try
        {
            foreach (string old in BadFilesToDelete(name, folder.Names(name + ".bad-*")))
            {
                _ = folder.Delete(old);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SafeTrace.WriteLine("Could not prune corrupt settings copies for '" + Path + "': " + ex.Message);
        }

        return new T();
    }

    private void SaveElevated(T value)
    {
        if (_memoryOnly)
        {
            _memory = value;
            return;
        }

        SafeDataFolder? folder = AcquireFolder(create: true, out _);
        if (folder is null)
        {
            _memory = value;
            _ = GoMemoryOnly();
            return;
        }

        string name = System.IO.Path.GetFileName(Path);
        FileOpenResult existing = folder.OpenFile(
            name,
            ReadAttributes,
            ShareAll,
            OpenExisting,
            out SafeFileHandle? probe,
            out _);
        probe?.Dispose();
        if (existing == FileOpenResult.Unsafe)
        {
            _memory = value;
            _ = GoMemoryOnly();
            return;
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, _jsonTypeInfo);
        string temporaryName = name + ".tmp-" + Guid.NewGuid().ToString("N");
        if (folder.OpenFile(
                temporaryName,
                GenericWrite | DeleteAccess,
                0,
                CreateNew,
                out SafeFileHandle? temporary,
                out _) != FileOpenResult.Opened)
        {
            throw new IOException("Could not create a temporary settings file in the data folder.");
        }

        using (temporary)
        {
            try
            {
                RandomAccess.Write(temporary!, bytes, 0);
                SafeDataFolder.Flush(temporary!);
                folder.Rename(temporary!, name, replace: true);
            }
            catch
            {
                _ = SafeDataFolder.MarkForDeletion(temporary!);
                throw;
            }
        }
    }
}

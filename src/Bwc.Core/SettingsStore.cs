using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

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

    private readonly JsonTypeInfo<T> _jsonTypeInfo;

    public SettingsStore(string filePath, JsonTypeInfo<T> jsonTypeInfo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        Path = System.IO.Path.GetFullPath(filePath);
        _jsonTypeInfo = jsonTypeInfo;
    }

    public string Path { get; }

    public T Load()
    {
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
        return new T();
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
}

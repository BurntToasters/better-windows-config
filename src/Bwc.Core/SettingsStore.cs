using System.Diagnostics;
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

            string json = File.ReadAllText(Path);
            T? value;
            try
            {
                value = JsonSerializer.Deserialize(json, _jsonTypeInfo);
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
            Trace.WriteLine("Could not delete temporary settings file '" + path + "': " + ex.Message);
        }
    }
}

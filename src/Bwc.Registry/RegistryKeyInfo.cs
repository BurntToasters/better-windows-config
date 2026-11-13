namespace Bwc.Registry;

/// <summary>
/// A snapshot of one key's metadata. Counts and lengths come directly from RegQueryInfoKey.
/// </summary>
public sealed class RegistryKeyInfo
{
    public RegistryKeyInfo(
        string name,
        RegistryPath path,
        int subKeyCount,
        int valueCount,
        DateTime lastWriteTimeUtc,
        int maxSubKeyNameLength,
        int maxValueNameLength,
        int maxValueDataLength,
        string className)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(className);
        ArgumentOutOfRangeException.ThrowIfNegative(subKeyCount);
        ArgumentOutOfRangeException.ThrowIfNegative(valueCount);
        ArgumentOutOfRangeException.ThrowIfNegative(maxSubKeyNameLength);
        ArgumentOutOfRangeException.ThrowIfNegative(maxValueNameLength);
        ArgumentOutOfRangeException.ThrowIfNegative(maxValueDataLength);

        Name = name;
        Path = path;
        SubKeyCount = subKeyCount;
        ValueCount = valueCount;
        LastWriteTimeUtc = lastWriteTimeUtc;
        MaxSubKeyNameLength = maxSubKeyNameLength;
        MaxValueNameLength = maxValueNameLength;
        MaxValueDataLength = maxValueDataLength;
        ClassName = className;
    }

    /// <summary>The last path component, or the root display name for a predefined root.</summary>
    public string Name { get; }

    public RegistryPath Path { get; }

    public int SubKeyCount { get; }

    public int ValueCount { get; }

    public DateTime LastWriteTimeUtc { get; }

    /// <summary>The longest subkey name in characters, excluding the terminating NUL.</summary>
    public int MaxSubKeyNameLength { get; }

    /// <summary>The longest value name in characters, excluding the terminating NUL.</summary>
    public int MaxValueNameLength { get; }

    /// <summary>The largest value data size in bytes.</summary>
    public int MaxValueDataLength { get; }

    /// <summary>The key class name. Usually empty, never null.</summary>
    public string ClassName { get; }
}

using System.Diagnostics.CodeAnalysis;

namespace Bwc.Registry;

/// <summary>
/// The `.reg` file format. The v5 format is UTF-16LE with a BOM; REGEDIT4 is the system ANSI
/// code page without a BOM.
/// </summary>
public enum RegFileFormat
{
    V5 = 0,
    Regedit4 = 1,
}

/// <summary>
/// One complete `.reg` file: a format and an ordered list of key sections. Duplicate key paths
/// are allowed because an import may intentionally apply several changes in sequence.
/// </summary>
public sealed class RegFileDocument
{
    public RegFileDocument(RegFileFormat format, IReadOnlyList<RegFileKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        Format = format;
        Keys = Array.AsReadOnly(keys.ToArray());
    }

    public RegFileFormat Format { get; }

    public IReadOnlyList<RegFileKey> Keys { get; }
}

/// <summary>
/// One key section. A section either creates or opens the key, or deletes it when
/// <see cref="Delete"/> is true. Values keep their file order.
/// </summary>
public sealed class RegFileKey
{
    public RegFileKey(string path, IReadOnlyList<RegFileValue> values, bool delete = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(values);

        Path = path;
        Values = Array.AsReadOnly(values.ToArray());
        Delete = delete;
    }

    /// <summary>The registry-style path, for example <c>HKEY_CURRENT_USER\Software\BWC-Oracle</c>.</summary>
    public string Path { get; }

    public bool Delete { get; }

    public IReadOnlyList<RegFileValue> Values { get; }
}

/// <summary>
/// One value line. <see cref="Name"/> is empty for the default value. <see cref="Data"/> is in
/// registry form: UTF-16LE bytes for string-like types, raw bytes for every other type.
/// </summary>
public sealed class RegFileValue
{
    public RegFileValue(string name, uint type, byte[] data, bool delete = false)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(data);

        Name = name;
        Type = type;
        Data = data.Length == 0 ? Array.Empty<byte>() : (byte[])data.Clone();
        Delete = delete;
    }

    public string Name { get; }

    public uint Type { get; }

    [SuppressMessage(
        "Performance",
        "CA1819:Properties should not return arrays",
        Justification = "The raw registry bytes are the contract for a lossless value.")]
    public byte[] Data { get; }

    public bool Delete { get; }
}

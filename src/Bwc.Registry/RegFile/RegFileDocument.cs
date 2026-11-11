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
/// <see cref="Problems"/> lists the lines a lenient parse skipped. A strict parse never has any.
/// </summary>
public sealed class RegFileDocument
{
    public RegFileDocument(
        RegFileFormat format,
        IReadOnlyList<RegFileKey> keys,
        IReadOnlyList<RegFileProblem>? problems = null)
    {
        ArgumentNullException.ThrowIfNull(keys);

        Format = format;
        Keys = Array.AsReadOnly(keys.ToArray());
        Problems = Array.AsReadOnly(problems is null ? Array.Empty<RegFileProblem>() : problems.ToArray());
    }

    public RegFileFormat Format { get; }

    public IReadOnlyList<RegFileKey> Keys { get; }

    /// <summary>Lines the lenient parser skipped, in file order. Empty for a strict parse.</summary>
    public IReadOnlyList<RegFileProblem> Problems { get; }
}

/// <summary>
/// A line the lenient parser skipped. <see cref="Line"/> and <see cref="Column"/> are one-based
/// positions in the decoded text, and <see cref="Message"/> names the problem.
/// </summary>
public sealed record RegFileProblem(int Line, int Column, string Message);

/// <summary>
/// One key section. A section either creates or opens the key, or deletes it when
/// <see cref="Delete"/> is true. Values keep their file order. <see cref="Line"/> is the one-based
/// line of the section header, or zero when the section was built in code.
/// </summary>
public sealed class RegFileKey
{
    public RegFileKey(
        string path,
        IReadOnlyList<RegFileValue> values,
        bool delete = false,
        int line = 0)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(values);

        Path = path;
        Values = Array.AsReadOnly(values.ToArray());
        Delete = delete;
        Line = line;
    }

    /// <summary>The registry-style path, for example <c>HKEY_CURRENT_USER\Software\BWC-Oracle</c>.</summary>
    public string Path { get; }

    public bool Delete { get; }

    public IReadOnlyList<RegFileValue> Values { get; }

    public int Line { get; }
}

/// <summary>
/// One value line. <see cref="Name"/> is empty for the default value. <see cref="Data"/> is in
/// registry form: UTF-16LE bytes for string-like types, raw bytes for every other type.
/// <see cref="Line"/> is the one-based line of the value, or zero when it was built in code.
/// </summary>
public sealed class RegFileValue
{
    public RegFileValue(string name, uint type, byte[] data, bool delete = false, int line = 0)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(data);

        Name = name;
        Type = type;
        Data = data.Length == 0 ? Array.Empty<byte>() : (byte[])data.Clone();
        Delete = delete;
        Line = line;
    }

    public string Name { get; }

    public uint Type { get; }

    [SuppressMessage(
        "Performance",
        "CA1819:Properties should not return arrays",
        Justification = "The raw registry bytes are the contract for a lossless value.")]
    public byte[] Data { get; }

    public bool Delete { get; }

    public int Line { get; }
}

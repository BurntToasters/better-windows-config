namespace Bwc.Registry;

/// <summary>
/// Why a `.reg` file was not applied at all. Each value is a code, not text. The UI maps it to a
/// message. <see cref="NotRegistryFile"/> is regedit string 133, <see cref="ReadFailed"/> is 129 or
/// 130, and <see cref="InvalidKeyPath"/> is 125.
/// </summary>
public enum RegFileRefusal
{
    /// <summary>The file was applied; individual lines may still have been skipped or rejected.</summary>
    None = 0,

    /// <summary>
    /// The header is missing or not exactly <c>Windows Registry Editor Version 5.00</c> or
    /// <c>REGEDIT4</c>, or the bytes are not decodable as one of those formats.
    /// </summary>
    NotRegistryFile = 1,

    /// <summary>
    /// A key path has an unknown root, an empty, <c>.</c>, or <c>..</c> segment, a segment over 255
    /// characters, or a <c>]</c> inside a segment.
    /// </summary>
    InvalidKeyPath = 2,

    /// <summary>The file could not be opened or read.</summary>
    ReadFailed = 3,
}

/// <summary>
/// The import position reported after each key section finishes. <see cref="CompletedSections"/>
/// counts the sections finished so far, <see cref="TotalSections"/> counts every section in the
/// document, and <see cref="KeyPath"/> is the section that just finished.
/// </summary>
public readonly record struct ImportProgress(int CompletedSections, int TotalSections, string KeyPath);

/// <summary>Whether a failure came from a skipped file line or from a rejected registry write.</summary>
public enum RegFileImportFailureKind
{
    /// <summary>The line was skipped and the rest of the file was imported.</summary>
    MalformedLine = 0,

    /// <summary>A registry write was rejected. The rest of the file was still attempted.</summary>
    WriteFailed = 1,
}

/// <summary>
/// One failure with its file line. <see cref="KeyPath"/> and <see cref="ValueName"/> are set for
/// write failures. <see cref="Win32Error"/> is zero when the failure has no Win32 code.
/// </summary>
public sealed record RegFileImportFailure(
    RegFileImportFailureKind Kind,
    int Line,
    string? KeyPath,
    string? ValueName,
    int Win32Error,
    string Message);

/// <summary>
/// The outcome of one import. A refused file has no counts and no failures, because nothing was
/// applied. Otherwise the counts say what was written and the failures list every skipped line
/// and every rejected write, in file order.
/// </summary>
public sealed class RegFileImportResult
{
    internal RegFileImportResult(
        RegFileRefusal refusal,
        int keysWritten,
        int keysDeleted,
        int valuesWritten,
        int valuesDeleted,
        IReadOnlyList<RegFileImportFailure> failures)
    {
        Refusal = refusal;
        KeysWritten = keysWritten;
        KeysDeleted = keysDeleted;
        ValuesWritten = valuesWritten;
        ValuesDeleted = valuesDeleted;
        Failures = Array.AsReadOnly(failures.ToArray());
    }

    public RegFileRefusal Refusal { get; }

    public bool IsRefused => Refusal != RegFileRefusal.None;

    /// <summary>Key sections that were created, opened, or reached (predefined roots always count).</summary>
    public int KeysWritten { get; }

    /// <summary>Key sections whose key was deleted. A key that was already absent is not counted.</summary>
    public int KeysDeleted { get; }

    /// <summary>Values written.</summary>
    public int ValuesWritten { get; }

    /// <summary>Values deleted. A value that was already absent is not counted.</summary>
    public int ValuesDeleted { get; }

    public IReadOnlyList<RegFileImportFailure> Failures { get; }

    /// <summary>True when at least one registry write was rejected. Classic regedit shows string 132 for this case.</summary>
    public bool HasWriteFailures => Failures.Any(failure => failure.Kind == RegFileImportFailureKind.WriteFailed);
}

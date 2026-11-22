namespace Bwc.Gpo.Pol;

/// <summary>Why a Registry.pol file was refused.</summary>
public enum PolFormatReason
{
    /// <summary>The input has zero bytes.</summary>
    Empty,

    /// <summary>The input is shorter than the 8-byte header.</summary>
    HeaderTruncated,

    /// <summary>The first four bytes are not <c>PReg</c>.</summary>
    BadSignature,

    /// <summary>The signature is right and the version is not 1.</summary>
    BadVersion,

    /// <summary>Bytes after a record, or after the header, do not start a record with <c>[</c>.</summary>
    ExpectedRecordStart,

    /// <summary>The input ends inside a record.</summary>
    Truncated,

    /// <summary>A key or value name has no NUL terminator before the end of the input.</summary>
    UnterminatedString,

    /// <summary>A <c>;</c> separator is missing between two fields.</summary>
    MissingDelimiter,

    /// <summary>The size field claims more data than the input holds.</summary>
    SizePastEnd,

    /// <summary>The data is not followed by <c>]</c>.</summary>
    MissingRecordEnd,

    /// <summary>A stream is longer than the reader's size cap.</summary>
    FileTooLarge,

    /// <summary>
    /// A record's key starts with a backslash. Classic refuses to open such a file (capture
    /// K/KeyLeadingBackslash_ReadAsWritten).
    /// </summary>
    KeyLeadingBackslash,
}

/// <summary>The one failure type of the Registry.pol reader: a reason and the byte offset where it was found.</summary>
public sealed class PolFormatException : Exception
{
    /// <summary>Creates a failure.</summary>
    /// <param name="offset">Byte offset in the input where the problem was found.</param>
    /// <param name="reason">The reason code.</param>
    public PolFormatException(int offset, PolFormatReason reason)
        : base($"Registry.pol is not valid at byte offset {offset}: {reason}.")
    {
        Offset = offset;
        Reason = reason;
    }

    /// <summary>Byte offset in the input where the problem was found.</summary>
    public int Offset { get; }

    /// <summary>The reason code.</summary>
    public PolFormatReason Reason { get; }
}

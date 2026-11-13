namespace Bwc.Registry;

/// <summary>
/// A malformed `.reg` file. <see cref="Line"/> and <see cref="Column"/> are one-based positions
/// in the decoded text. Nothing was partially applied.
/// </summary>
public sealed class RegFileParseException : Exception
{
    public RegFileParseException(string message, int line, int column)
        : this(message, line, column, RegFileRefusal.None)
    {
    }

    internal RegFileParseException(string message, int line, int column, RegFileRefusal refusal)
        : base($"{message} (line {line}, column {column})")
    {
        Detail = message;
        Line = line;
        Column = column;
        Refusal = refusal;
    }

    /// <summary>The problem without the position suffix that <see cref="Exception.Message"/> adds.</summary>
    public string Detail { get; }

    public int Line { get; }

    public int Column { get; }

    /// <summary>
    /// The reason the whole file is refused, or <see cref="RegFileRefusal.None"/> for a line-level
    /// problem. The lenient parser refuses only when this is not <see cref="RegFileRefusal.None"/>.
    /// </summary>
    public RegFileRefusal Refusal { get; }
}

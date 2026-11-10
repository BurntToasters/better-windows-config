namespace Bwc.Registry;

/// <summary>
/// A malformed `.reg` file. <see cref="Line"/> and <see cref="Column"/> are one-based positions
/// in the decoded text. Nothing was partially applied.
/// </summary>
public sealed class RegFileParseException : Exception
{
    public RegFileParseException(string message, int line, int column)
        : base($"{message} (line {line}, column {column})")
    {
        Line = line;
        Column = column;
    }

    public int Line { get; }

    public int Column { get; }
}

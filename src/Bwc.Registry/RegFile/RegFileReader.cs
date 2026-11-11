using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Bwc.Registry;

/// <summary>
/// Parses classic regedit `.reg` text into an immutable document. The format is detected from
/// the BOM and header; the parser never writes to the registry and never returns a partial
/// document.
/// </summary>
/// <remarks>
/// <see cref="Parse(Stream)"/> and <see cref="ParseFile(string)"/> are strict: the first malformed
/// line throws. <see cref="ParseLenient(Stream)"/> and <see cref="ParseFileLenient(string)"/> match
/// classic regedit import: a malformed line is skipped and listed in
/// <see cref="RegFileDocument.Problems"/>, and only a file-level problem throws, with a
/// <see cref="RegFileParseException.Refusal"/> other than <see cref="RegFileRefusal.None"/>.
/// </remarks>
public static class RegFileReader
{
    /// <summary>Reads the stream from its current position to the end and parses it strictly.</summary>
    public static RegFileDocument Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return ParseBytes(buffer.ToArray(), strict: true);
    }

    /// <summary>Reads and parses a `.reg` file strictly.</summary>
    public static RegFileDocument ParseFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        return ParseBytes(File.ReadAllBytes(path), strict: true);
    }

    /// <summary>Reads the stream from its current position to the end and parses it the way classic regedit imports.</summary>
    public static RegFileDocument ParseLenient(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return ParseBytes(buffer.ToArray(), strict: false);
    }

    /// <summary>Reads and parses a `.reg` file the way classic regedit imports it.</summary>
    public static RegFileDocument ParseFileLenient(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        return ParseBytes(File.ReadAllBytes(path), strict: false);
    }

    private static RegFileDocument ParseBytes(byte[] bytes, bool strict)
    {
        (string text, RegFileFormat format) = Decode(bytes);

        return new Parser(text, format, strict).Parse();
    }

    private static (string Text, RegFileFormat Format) Decode(byte[] bytes)
    {
        string text;

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            int length = bytes.Length - 2;

            if ((length & 1) != 0)
            {
                throw new RegFileParseException(
                    "The UTF-16LE data has an odd number of bytes.",
                    1,
                    1,
                    RegFileRefusal.NotRegistryFile);
            }

            text = Encoding.Unicode.GetString(bytes, 2, length);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            throw new RegFileParseException(
                "UTF-16 big-endian .reg files are not supported.",
                1,
                1,
                RegFileRefusal.NotRegistryFile);
        }
        else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        else
        {
            text = RegFileEncoding.AnsiEncoding.GetString(bytes);
        }

        string firstLine = text;
        int firstLineEnd = firstLine.IndexOfAny('\r', '\n');

        if (firstLineEnd >= 0)
        {
            firstLine = firstLine[..firstLineEnd];
        }

        if (firstLine == RegFileEncoding.V5Header)
        {
            return (text, RegFileFormat.V5);
        }

        if (firstLine == RegFileEncoding.Regedit4Header)
        {
            return (text, RegFileFormat.Regedit4);
        }

        throw new RegFileParseException(
            "The .reg header is missing or not recognized.",
            1,
            1,
            RegFileRefusal.NotRegistryFile);
    }

    private sealed class Parser
    {
        private readonly string _text;
        private readonly RegFileFormat _format;
        private readonly bool _strict;
        private readonly int[] _lineStarts;
        private int _index;

        internal Parser(string text, RegFileFormat format, bool strict)
        {
            _text = text;
            _format = format;
            _strict = strict;

            var lineStarts = new List<int> { 0 };

            for (int position = 0; position < text.Length; position += 1)
            {
                if (text[position] == '\n')
                {
                    lineStarts.Add(position + 1);
                }
            }

            _lineStarts = lineStarts.ToArray();
        }

        private bool AtEnd => _index >= _text.Length;

        internal RegFileDocument Parse()
        {
            ConsumeLine();

            var keys = new List<RegFileKey>();
            var problems = new List<RegFileProblem>();
            string? currentPath = null;
            bool currentDelete = false;
            int currentLine = 0;
            List<RegFileValue>? currentValues = null;

            while (true)
            {
                SkipBlankLinesAndComments();

                if (AtEnd)
                {
                    break;
                }

                if (Peek() == '[')
                {
                    FlushKey();

                    try
                    {
                        int headerLine = LineOf(_index);
                        (currentPath, currentDelete) = ParseKeyHeader();
                        currentLine = headerLine;
                        currentValues = new List<RegFileValue>();
                    }
                    catch (RegFileParseException error)
                        when (!_strict && error.Refusal == RegFileRefusal.None)
                    {
                        // The section is not started, so its value lines become orphans below.
                        problems.Add(ToProblem(error));
                        SkipPhysicalLine();
                    }

                    continue;
                }

                if (currentPath is null || currentValues is null)
                {
                    RegFileParseException orphan = Error(_index, "A value line must follow a key section.");

                    if (_strict)
                    {
                        throw orphan;
                    }

                    problems.Add(ToProblem(orphan));
                    SkipLogicalValue(_index);
                    continue;
                }

                int valueStart = _index;

                try
                {
                    currentValues.Add(ParseValue());
                }
                catch (RegFileParseException error) when (!_strict && error.Refusal == RegFileRefusal.None)
                {
                    problems.Add(ToProblem(error));
                    SkipLogicalValue(valueStart);
                }
            }

            FlushKey();

            return new RegFileDocument(_format, keys, problems);

            void FlushKey()
            {
                if (currentPath is null)
                {
                    return;
                }

                keys.Add(new RegFileKey(currentPath, currentValues!, currentDelete, currentLine));
                currentPath = null;
                currentDelete = false;
                currentLine = 0;
                currentValues = null;
            }
        }

        private (string Path, bool Delete) ParseKeyHeader()
        {
            int start = _index;
            Advance();

            int lineEnd = FindLineEnd();
            int close = LastCloseBracketBefore(lineEnd);

            if (close < 0)
            {
                throw Error(lineEnd, "A key section is missing the closing ']'.");
            }

            string content = _text[_index..close];
            _index = close + 1;
            bool delete = content.Length > 0 && content[0] == '-';
            string path = delete ? content[1..] : content;
            int pathStart = start + 1 + (delete ? 1 : 0);

            if (path.Length == 0)
            {
                throw Error(start, "A key section must contain a registry path.");
            }

            if (path.Contains(']'))
            {
                throw Error(
                    pathStart,
                    $"The key path '{path}' has a ']' inside a segment, which is not allowed.",
                    RegFileRefusal.InvalidKeyPath);
            }

            if (!RegistryPath.TryParse(path, out _))
            {
                throw Error(pathStart, InvalidKeyPathReason(path), RegFileRefusal.InvalidKeyPath);
            }

            SkipHorizontalWhitespace();

            if (!AtEnd && !IsLineEnd(Peek()))
            {
                throw Error(_index, "Unexpected characters after the key section's closing ']'.");
            }

            ExpectEndOfLine();

            return (path, delete);
        }

        private RegFileValue ParseValue()
        {
            int line = LineOf(_index);
            string name;

            if (Peek() == '@')
            {
                Advance();
                name = string.Empty;
            }
            else if (Peek() == '"')
            {
                name = ParseQuotedString("value name");
            }
            else
            {
                throw Error(
                    _index,
                    "A value line must begin with @ or a quoted value name.");
            }

            SkipHorizontalWhitespace();

            if (Peek() != '=')
            {
                throw Error(_index, "A value line is missing '='.");
            }

            Advance();
            SkipHorizontalWhitespace();
            int dataStart = _index;

            if (Peek() == '-')
            {
                Advance();
                ExpectEndOfLine();
                return new RegFileValue(name, 0, Array.Empty<byte>(), delete: true, line: line);
            }

            if (Peek() == '"')
            {
                string text = ParseQuotedString("string value");
                ExpectEndOfLine();
                return new RegFileValue(name, 1, EncodeString(text), line: line);
            }

            if (Matches("dword:"))
            {
                _index += "dword:".Length;
                return new RegFileValue(name, 4, ParseDword(), line: line);
            }

            if (Matches("hex"))
            {
                _index += "hex".Length;
                return ParseHexValue(name, line);
            }

            throw Error(
                dataStart,
                "The value data is not a string, dword, hex, or delete marker.");
        }

        private RegFileValue ParseHexValue(string name, int line)
        {
            uint type = 3;

            if (Peek() == '(')
            {
                Advance();
                int typeStart = _index;

                while (IsHexDigit(Peek()))
                {
                    Advance();
                }

                if (_index == typeStart)
                {
                    throw Error(typeStart, "A hex type number is missing.");
                }

                string token = _text[typeStart.._index];

                if (!uint.TryParse(
                        token,
                        NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture,
                        out type))
                {
                    throw Error(typeStart, "The hex type number is too large.");
                }

                if (Peek() != ')')
                {
                    throw Error(_index, "A hex type number is missing the closing ')'.");
                }

                Advance();
            }

            if (Peek() != ':')
            {
                throw Error(_index, "A hex value is missing ':'.");
            }

            Advance();
            byte[] data = ParseHexData();

            if (_format == RegFileFormat.Regedit4 && type is 1 or 2 or 7)
            {
                data = RegFileEncoding.ConvertAnsiToUnicodeBytes(data);
            }

            return new RegFileValue(name, type, data, line: line);
        }

        private byte[] ParseDword()
        {
            uint value = 0;
            int digits = 0;

            while (IsHexDigit(Peek()))
            {
                if (digits == 8)
                {
                    throw Error(
                        _index,
                        "A dword value must have at most eight hexadecimal digits.");
                }

                value = (value << 4) | (uint)HexValue(Peek());
                digits += 1;
                Advance();
            }

            if (digits == 0)
            {
                throw Error(
                    _index,
                    "A dword value must have one to eight hexadecimal digits.");
            }

            ExpectEndOfLine();

            byte[] data = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(data, value);
            return data;
        }

        private byte[] ParseHexData()
        {
            var bytes = new List<byte>();
            SkipHorizontalWhitespace();

            if (AtEnd)
            {
                return bytes.ToArray();
            }

            if (IsLineEnd(Peek()))
            {
                ConsumeLineEnding();
                return bytes.ToArray();
            }

            while (true)
            {
                char high = Peek();

                if (!IsHexDigit(high))
                {
                    throw Error(
                        _index,
                        "A hex value contains an invalid hexadecimal digit.");
                }

                int byteStart = _index;
                Advance();
                char low = Peek();

                if (!IsHexDigit(low))
                {
                    throw Error(
                        byteStart,
                        "A hex byte must have exactly two hexadecimal digits.");
                }

                Advance();
                bytes.Add((byte)((HexValue(high) << 4) | HexValue(low)));
                SkipHorizontalWhitespace();

                if (AtEnd)
                {
                    return bytes.ToArray();
                }

                char next = Peek();

                if (IsLineEnd(next))
                {
                    ConsumeLineEnding();
                    return bytes.ToArray();
                }

                if (next == '\\')
                {
                    ConsumeContinuation();
                    continue;
                }

                if (next == ',')
                {
                    Advance();
                    SkipHorizontalWhitespace();

                    // A comma that ends the value is accepted, as classic regedit does
                    // (oracle: hex-trailing-comma).
                    if (AtEnd)
                    {
                        return bytes.ToArray();
                    }

                    if (IsLineEnd(Peek()))
                    {
                        ConsumeLineEnding();
                        return bytes.ToArray();
                    }

                    if (Peek() == '\\')
                    {
                        ConsumeContinuation();
                    }

                    continue;
                }

                throw Error(_index, "Unexpected character in hex data.");
            }
        }

        private void ConsumeContinuation()
        {
            int start = _index;
            Advance();
            SkipHorizontalWhitespace();

            if (AtEnd)
            {
                throw Error(
                    start,
                    "A hex line continuation reaches the end of the file.");
            }

            if (!IsLineEnd(Peek()))
            {
                throw Error(
                    start,
                    "A hex line continuation must be the last character on its line.");
            }

            ConsumeLineEnding();
            SkipHorizontalWhitespace();
        }

        private string ParseQuotedString(string what)
        {
            int start = _index;
            Advance();
            var builder = new StringBuilder();

            while (!AtEnd)
            {
                char character = Peek();

                if (character == '"')
                {
                    Advance();
                    return builder.ToString();
                }

                if (IsLineEnd(character))
                {
                    throw Error(start, $"The {what} is missing its closing quote.");
                }

                if (character == '\\')
                {
                    int escapeStart = _index;
                    Advance();

                    if (AtEnd)
                    {
                        throw Error(
                            escapeStart,
                            "A quoted string ends with an incomplete escape sequence.");
                    }

                    char escape = Peek();
                    Advance();
                    builder.Append(escape switch
                    {
                        '\\' => '\\',
                        '"' => '"',
                        'n' => '\n',
                        'r' => '\r',
                        '0' => '\0',
                        _ => throw Error(
                            escapeStart,
                            $"The escape sequence '\\{escape}' is not recognized."),
                    });
                    continue;
                }

                builder.Append(character);
                Advance();
            }

            throw Error(start, $"The {what} is missing its closing quote.");
        }

        private byte[] EncodeString(string value)
        {
            if (_format == RegFileFormat.V5)
            {
                return Encoding.Unicode.GetBytes(value + "\0");
            }

            string ansiText = RegFileEncoding.AnsiEncoding.GetString(
                RegFileEncoding.AnsiEncoding.GetBytes(value));

            return Encoding.Unicode.GetBytes(ansiText + "\0");
        }

        private void SkipBlankLinesAndComments()
        {
            while (true)
            {
                SkipHorizontalWhitespace();

                if (AtEnd)
                {
                    return;
                }

                if (IsLineEnd(Peek()))
                {
                    ConsumeLineEnding();
                    continue;
                }

                if (Peek() == ';')
                {
                    while (!AtEnd && !IsLineEnd(Peek()))
                    {
                        Advance();
                    }

                    ConsumeLineEnding();
                    continue;
                }

                return;
            }
        }

        private void ConsumeLine()
        {
            while (!AtEnd && !IsLineEnd(Peek()))
            {
                Advance();
            }

            ConsumeLineEnding();
        }

        // Skips the physical line that holds a bad key header. Headers never continue.
        private void SkipPhysicalLine()
        {
            _index = FindLineEnd();
            ConsumeLineEnding();
        }

        // Skips one value line that failed to parse, together with the continuation lines of a
        // hex value. Only a hex value continues, so a backslash on any other line is not a join.
        private void SkipLogicalValue(int valueStart)
        {
            bool hex = IsHexValue(valueStart);
            _index = valueStart;

            while (true)
            {
                int end = FindLineEnd();
                int last = end - 1;

                while (last >= _index && _text[last] is ' ' or '\t')
                {
                    last -= 1;
                }

                bool continued = hex && last >= _index && _text[last] == '\\';
                _index = end;
                ConsumeLineEnding();

                if (!continued || AtEnd)
                {
                    return;
                }
            }
        }

        // Reports whether the value that starts at position is hex data, by looking past the
        // name and the '=' on the same line.
        private bool IsHexValue(int start)
        {
            int position = start;

            if (position < _text.Length && _text[position] == '"')
            {
                position += 1;

                while (position < _text.Length && !IsLineEnd(_text[position]))
                {
                    if (_text[position] == '\\')
                    {
                        position += 2;
                        continue;
                    }

                    if (_text[position] == '"')
                    {
                        position += 1;
                        break;
                    }

                    position += 1;
                }
            }
            else if (position < _text.Length && _text[position] == '@')
            {
                position += 1;
            }
            else
            {
                return false;
            }

            position = SkipSpacesFrom(position);

            if (position >= _text.Length || _text[position] != '=')
            {
                return false;
            }

            position = SkipSpacesFrom(position + 1);

            return position + 3 <= _text.Length
                && string.Compare(_text, position, "hex", 0, 3, StringComparison.OrdinalIgnoreCase) == 0;
        }

        private int SkipSpacesFrom(int position)
        {
            while (position < _text.Length && _text[position] is ' ' or '\t')
            {
                position += 1;
            }

            return position;
        }

        private void ExpectEndOfLine()
        {
            SkipHorizontalWhitespace();

            if (AtEnd)
            {
                return;
            }

            if (!IsLineEnd(Peek()))
            {
                throw Error(
                    _index,
                    "Unexpected characters at the end of a value line.");
            }

            ConsumeLineEnding();
        }

        private void ConsumeLineEnding()
        {
            if (!AtEnd && Peek() == '\r')
            {
                Advance();
            }

            if (!AtEnd && Peek() == '\n')
            {
                Advance();
            }
        }

        private void SkipHorizontalWhitespace()
        {
            while (!AtEnd && Peek() is ' ' or '\t')
            {
                Advance();
            }
        }

        private int FindLineEnd()
        {
            int position = _index;

            while (position < _text.Length && !IsLineEnd(_text[position]))
            {
                position += 1;
            }

            return position;
        }

        // The last ']' on the header line, searching from the current position. Taking the last one
        // keeps an extra ']' inside the path, where the path check refuses it.
        private int LastCloseBracketBefore(int lineEnd)
        {
            for (int position = lineEnd - 1; position >= _index; position -= 1)
            {
                if (_text[position] == ']')
                {
                    return position;
                }
            }

            return -1;
        }

        private static string InvalidKeyPathReason(string path)
        {
            string[] segments = path.Split('\\');

            for (int index = 1; index < segments.Length; index += 1)
            {
                if (!RegistryPath.IsValidComponent(segments[index]))
                {
                    return $"The key path has a segment '{segments[index]}' that is not allowed.";
                }
            }

            return $"The key path '{path}' does not start with a known registry root.";
        }

        private bool Matches(string token) =>
            _index + token.Length <= _text.Length
            && string.Compare(
                _text,
                _index,
                token,
                0,
                token.Length,
                StringComparison.OrdinalIgnoreCase) == 0;

        private char Peek() => AtEnd ? '\0' : _text[_index];

        private void Advance() => _index += 1;

        private static bool IsLineEnd(char character) => character is '\r' or '\n';

        private static bool IsHexDigit(char character) =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F';

        private static int HexValue(char character) => character switch
        {
            >= '0' and <= '9' => character - '0',
            >= 'a' and <= 'f' => character - 'a' + 10,
            _ => character - 'A' + 10,
        };

        private static RegFileProblem ToProblem(RegFileParseException error) =>
            new(error.Line, error.Column, error.Detail);

        private int LineOf(int position) => LineIndexOf(Math.Clamp(position, 0, _text.Length)) + 1;

        private int LineIndexOf(int clamped)
        {
            int found = Array.BinarySearch(_lineStarts, clamped);

            if (found < 0)
            {
                found = ~found - 1;
            }

            return found;
        }

        private RegFileParseException Error(
            int position,
            string message,
            RegFileRefusal refusal = RegFileRefusal.None)
        {
            int clamped = Math.Clamp(position, 0, _text.Length);
            int found = LineIndexOf(clamped);

            int lineStart = _lineStarts[found];
            int line = found + 1;
            int column = clamped - lineStart + 1;

            return new RegFileParseException(message, line, column, refusal);
        }
    }
}

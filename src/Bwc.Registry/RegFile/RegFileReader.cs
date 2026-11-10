using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Bwc.Registry;

/// <summary>
/// Parses classic regedit `.reg` text into an immutable document. The format is detected from
/// the BOM and header; the parser never writes to the registry and never returns a partial
/// document.
/// </summary>
public static class RegFileReader
{
    /// <summary>Reads the stream from its current position to the end and parses it.</summary>
    public static RegFileDocument Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return ParseBytes(buffer.ToArray());
    }

    /// <summary>Reads and parses a `.reg` file.</summary>
    public static RegFileDocument ParseFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        return ParseBytes(File.ReadAllBytes(path));
    }

    private static RegFileDocument ParseBytes(byte[] bytes)
    {
        (string text, RegFileFormat format) = Decode(bytes);

        return new Parser(text, format).Parse();
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
                    1);
            }

            text = Encoding.Unicode.GetString(bytes, 2, length);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            throw new RegFileParseException(
                "UTF-16 big-endian .reg files are not supported.",
                1,
                1);
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
            1);
    }

    private sealed class Parser
    {
        private readonly string _text;
        private readonly RegFileFormat _format;
        private readonly int[] _lineStarts;
        private int _index;

        internal Parser(string text, RegFileFormat format)
        {
            _text = text;
            _format = format;

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
            string? currentPath = null;
            bool currentDelete = false;
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
                    (currentPath, currentDelete) = ParseKeyHeader();
                    currentValues = new List<RegFileValue>();
                    continue;
                }

                if (currentPath is null || currentValues is null)
                {
                    throw Error(_index, "A value line must follow a key section.");
                }

                currentValues.Add(ParseValue());
            }

            FlushKey();

            return new RegFileDocument(_format, keys);

            void FlushKey()
            {
                if (currentPath is null)
                {
                    return;
                }

                keys.Add(new RegFileKey(currentPath, currentValues!, currentDelete));
                currentPath = null;
                currentDelete = false;
                currentValues = null;
            }
        }

        private (string Path, bool Delete) ParseKeyHeader()
        {
            int start = _index;
            Advance();

            int lineEnd = FindLineEnd();
            int close = _text.IndexOf(']', _index);

            if (close < 0 || close > lineEnd)
            {
                throw Error(lineEnd, "A key section is missing the closing ']'.");
            }

            string content = _text[_index..close];
            _index = close + 1;
            bool delete = content.Length > 0 && content[0] == '-';
            string path = delete ? content[1..] : content;

            if (path.Length == 0)
            {
                throw Error(start, "A key section must contain a registry path.");
            }

            if (!RegistryPath.TryParse(path, out _))
            {
                throw Error(
                    start + 1 + (delete ? 1 : 0),
                    "The key path is not a valid absolute registry path.");
            }

            ExpectEndOfLine();

            return (path, delete);
        }

        private RegFileValue ParseValue()
        {
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
                return new RegFileValue(name, 0, Array.Empty<byte>(), delete: true);
            }

            if (Peek() == '"')
            {
                string text = ParseQuotedString("string value");
                ExpectEndOfLine();
                return new RegFileValue(name, 1, EncodeString(text));
            }

            if (Matches("dword:"))
            {
                _index += "dword:".Length;
                return new RegFileValue(name, 4, ParseDword());
            }

            if (Matches("hex"))
            {
                _index += "hex".Length;
                return ParseHexValue(name);
            }

            throw Error(
                dataStart,
                "The value data is not a string, dword, hex, or delete marker.");
        }

        private RegFileValue ParseHexValue(string name)
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

            return new RegFileValue(name, type, data);
        }

        private byte[] ParseDword()
        {
            uint value = 0;

            for (int digit = 0; digit < 8; digit += 1)
            {
                if (!IsHexDigit(Peek()))
                {
                    throw Error(
                        _index,
                        "A dword value must have exactly eight hexadecimal digits.");
                }

                value = (value << 4) | (uint)HexValue(Peek());
                Advance();
            }

            if (IsHexDigit(Peek()))
            {
                throw Error(
                    _index,
                    "A dword value must have exactly eight hexadecimal digits.");
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
                    int comma = _index;
                    Advance();
                    SkipHorizontalWhitespace();

                    if (AtEnd || IsLineEnd(Peek()))
                    {
                        throw Error(
                            comma,
                            "A hex line ends with a comma but has no continuation.");
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

        private RegFileParseException Error(int position, string message)
        {
            int clamped = Math.Clamp(position, 0, _text.Length);
            int found = Array.BinarySearch(_lineStarts, clamped);

            if (found < 0)
            {
                found = ~found - 1;
            }

            int lineStart = _lineStarts[found];
            int line = found + 1;
            int column = clamped - lineStart + 1;

            return new RegFileParseException(message, line, column);
        }
    }
}

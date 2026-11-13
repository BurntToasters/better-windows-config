using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Bwc.Registry;

/// <summary>
/// Serializes a <see cref="RegFileDocument"/> in the classic regedit text format. The three
/// files in <c>docs/parity/oracle/exports</c> are the byte-level specification.
/// </summary>
public static class RegFileWriter
{
    private const int MaxLineLength = 80;
    private const int ContinuationIndent = 2;
    private const int ContinuationCapacity = (MaxLineLength - ContinuationIndent - 1) / 3;
    private const string ComputerPrefix = "Computer\\";
    private const string HexDigits = "0123456789abcdef";

    /// <summary>Writes the document using the format recorded on it.</summary>
    public static void Write(RegFileDocument document, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(stream);

        switch (document.Format)
        {
            case RegFileFormat.V5:
                WriteCore(document, RegFileFormat.V5, stream);
                return;
            case RegFileFormat.Regedit4:
                WriteCore(document, RegFileFormat.Regedit4, stream);
                return;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(document),
                    document.Format,
                    "The document has an unknown .reg format.");
        }
    }

    /// <summary>Writes UTF-16LE with a BOM and the v5 header.</summary>
    public static void WriteV5(RegFileDocument document, Stream stream) =>
        WriteCore(document, RegFileFormat.V5, stream);

    /// <summary>Writes the system ANSI code page without a BOM and the REGEDIT4 header.</summary>
    public static void WriteRegedit4(RegFileDocument document, Stream stream) =>
        WriteCore(document, RegFileFormat.Regedit4, stream);

    private static void WriteCore(RegFileDocument document, RegFileFormat format, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(stream);

        // The whole document is built before the first byte is written, so a refused name or key
        // leaves the stream untouched. The streaming sink writes key by key instead.
        var builder = new StringBuilder();
        RegFileStreamWriter.AppendHeader(builder, format);

        foreach (RegFileKey key in document.Keys)
        {
            AppendKeySection(builder, key, format);
        }

        RegFileStreamWriter.WriteText(stream, format, builder.ToString());
    }

    /// <summary>
    /// Writes one key section, with its values, to a builder. The streaming sink and the document
    /// writer both use this, so the two paths produce the same bytes.
    /// </summary>
    internal static void AppendKeySection(StringBuilder builder, RegFileKey key, RegFileFormat format)
    {
        AppendKey(builder, key);

        foreach (RegFileValue value in key.Values)
        {
            AppendValue(builder, value, format);
        }

        builder.Append(RegFileEncoding.Crlf);
    }

    private static void AppendKey(StringBuilder builder, RegFileKey key)
    {
        builder.Append('[');

        if (key.Delete)
        {
            builder.Append('-');
        }

        string path = key.Path.StartsWith(ComputerPrefix, StringComparison.OrdinalIgnoreCase)
            ? key.Path[ComputerPrefix.Length..]
            : key.Path;

        if (path.IndexOfAny(UnwritableNameCharacters) >= 0)
        {
            throw new ArgumentException(
                $"The key path '{path}' contains a NUL, line feed, or carriage return, which a .reg file cannot hold.",
                nameof(key));
        }

        builder.Append(path);
        builder.Append(']');
        builder.Append(RegFileEncoding.Crlf);
    }

    private static void AppendValue(StringBuilder builder, RegFileValue value, RegFileFormat format)
    {
        int lineStart = builder.Length;
        AppendValueName(builder, value.Name);
        builder.Append('=');

        if (value.Delete)
        {
            builder.Append('-');
            builder.Append(RegFileEncoding.Crlf);
            return;
        }

        switch (value.Type)
        {
            case 1:
                string text = RegistryValueCodec.DecodeString(value.Data);
                if (text.IndexOfAny(LineBreakCharacters) >= 0)
                {
                    // Quoted form cannot hold a line feed or carriage return, and regedit has no escape
                    // for them. The raw bytes go out as hex(1) so the value round-trips.
                    AppendHexData(builder, lineStart, value, format);
                    return;
                }

                AppendQuoted(builder, text);
                builder.Append(RegFileEncoding.Crlf);
                return;

            case 4 when value.Data.Length == 4:
                builder.Append("dword:");
                builder.Append(
                    BinaryPrimitives.ReadUInt32LittleEndian(value.Data)
                        .ToString("x8", CultureInfo.InvariantCulture));
                builder.Append(RegFileEncoding.Crlf);
                return;

            default:
                AppendHexData(builder, lineStart, value, format);
                return;
        }
    }

    private static void AppendValueName(StringBuilder builder, string name)
    {
        if (name.Length == 0)
        {
            builder.Append('@');
            return;
        }

        if (name.IndexOfAny(UnwritableNameCharacters) >= 0)
        {
            throw new ArgumentException(
                "A value name contains a NUL, line feed, or carriage return, which a .reg file cannot hold.",
                nameof(name));
        }

        AppendQuoted(builder, name);
    }

    private static void AppendQuoted(StringBuilder builder, string text)
    {
        builder.Append('"');

        foreach (char character in text)
        {
            switch (character)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        builder.Append('"');
    }

    private static void AppendHexData(
        StringBuilder builder,
        int lineStart,
        RegFileValue value,
        RegFileFormat format)
    {
        string marker = value.Type == 3
            ? "hex:"
            : $"hex({value.Type.ToString("x", CultureInfo.InvariantCulture)}):";

        byte[] data = format == RegFileFormat.Regedit4 && value.Type is 1 or 2 or 7
            ? RegFileEncoding.ConvertUnicodeToAnsiBytes(value.Data)
            : value.Data;

        int prefixLength = builder.Length - lineStart + marker.Length;
        builder.Append(marker);

        if (data.Length == 0)
        {
            builder.Append(RegFileEncoding.Crlf);
            return;
        }

        int firstCapacity = Math.Max(1, (MaxLineLength - prefixLength - 1) / 3);
        int index = 0;
        bool firstLine = true;

        while (index < data.Length)
        {
            int capacity = firstLine ? firstCapacity : ContinuationCapacity;
            int count = Math.Min(capacity, data.Length - index);
            bool more = index + count < data.Length;

            if (!firstLine)
            {
                builder.Append(' ', ContinuationIndent);
            }

            for (int offset = 0; offset < count; offset += 1)
            {
                byte current = data[index + offset];
                builder.Append(HexDigits[current >> 4]);
                builder.Append(HexDigits[current & 0x0F]);

                if (offset < count - 1 || more)
                {
                    builder.Append(',');
                }
            }

            if (more)
            {
                builder.Append('\\');
            }

            builder.Append(RegFileEncoding.Crlf);
            index += count;
            firstLine = false;
        }
    }

    internal static readonly char[] UnwritableNameCharacters = { '\0', '\r', '\n' };

    private static readonly char[] LineBreakCharacters = { '\r', '\n' };
}

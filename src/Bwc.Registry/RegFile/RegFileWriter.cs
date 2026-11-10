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

    private static readonly byte[] V5Bom = { 0xFF, 0xFE };

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

        var builder = new StringBuilder();
        builder.Append(
            format == RegFileFormat.V5
                ? RegFileEncoding.V5Header
                : RegFileEncoding.Regedit4Header);
        builder.Append(RegFileEncoding.Crlf);
        builder.Append(RegFileEncoding.Crlf);

        foreach (RegFileKey key in document.Keys)
        {
            AppendKey(builder, key);

            foreach (RegFileValue value in key.Values)
            {
                AppendValue(builder, value, format);
            }

            builder.Append(RegFileEncoding.Crlf);
        }

        string text = builder.ToString();

        if (format == RegFileFormat.V5)
        {
            stream.Write(V5Bom);
            stream.Write(Encoding.Unicode.GetBytes(text));
        }
        else
        {
            stream.Write(RegFileEncoding.AnsiEncoding.GetBytes(text));
        }
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
                AppendQuoted(builder, RegistryValueCodec.DecodeString(value.Data));
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
                case '\0':
                    builder.Append("\\0");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
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
}

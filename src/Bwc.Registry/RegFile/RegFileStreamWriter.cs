using System.Text;

namespace Bwc.Registry;

/// <summary>
/// Writes a .reg file one key section at a time. The header is written with the first section, so a
/// export that fails before its first key (an unreadable start key, for example) leaves the stream
/// empty. <see cref="WriteKey"/> appends one complete section, and <see cref="Finish"/> writes the
/// header if no section was written. The bytes are the same as
/// <see cref="RegFileWriter.Write(RegFileDocument, Stream)"/> produces for the same keys. The stream
/// stays open. A failure after the first section leaves the earlier sections in the stream.
/// </summary>
internal sealed class RegFileStreamWriter
{
    private static readonly byte[] V5Bom = { 0xFF, 0xFE };

    private readonly Stream _stream;
    private readonly RegFileFormat _format;
    private bool _headerWritten;

    internal RegFileStreamWriter(Stream stream, RegFileFormat format)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateFormat(format);

        _stream = stream;
        _format = format;
    }

    internal void WriteKey(RegFileKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        EnsureHeader();

        var section = new StringBuilder();
        RegFileWriter.AppendKeySection(section, key, _format);
        _stream.Write(Encode(section.ToString(), _format));
    }

    /// <summary>Writes the header if no section has been written yet, so an empty export is still a valid file.</summary>
    internal void Finish() => EnsureHeader();

    /// <summary>Appends the header line, a blank line, and the format's header for <paramref name="format"/>.</summary>
    internal static void AppendHeader(StringBuilder builder, RegFileFormat format)
    {
        ValidateFormat(format);

        builder.Append(
            format == RegFileFormat.V5
                ? RegFileEncoding.V5Header
                : RegFileEncoding.Regedit4Header);
        builder.Append(RegFileEncoding.Crlf);
        builder.Append(RegFileEncoding.Crlf);
    }

    /// <summary>Writes a complete text with the BOM and encoding of <paramref name="format"/>.</summary>
    internal static void WriteText(Stream stream, RegFileFormat format, string text)
    {
        if (format == RegFileFormat.V5)
        {
            stream.Write(V5Bom);
        }

        stream.Write(Encode(text, format));
    }

    internal static void ValidateFormat(RegFileFormat format)
    {
        if (format is not (RegFileFormat.V5 or RegFileFormat.Regedit4))
        {
            throw new ArgumentOutOfRangeException(
                nameof(format),
                format,
                "The document has an unknown .reg format.");
        }
    }

    private void EnsureHeader()
    {
        if (_headerWritten)
        {
            return;
        }

        _headerWritten = true;

        if (_format == RegFileFormat.V5)
        {
            _stream.Write(V5Bom);
        }

        var header = new StringBuilder();
        AppendHeader(header, _format);
        _stream.Write(Encode(header.ToString(), _format));
    }

    private static byte[] Encode(string text, RegFileFormat format) =>
        format == RegFileFormat.V5
            ? Encoding.Unicode.GetBytes(text)
            : RegFileEncoding.AnsiEncoding.GetBytes(text);
}

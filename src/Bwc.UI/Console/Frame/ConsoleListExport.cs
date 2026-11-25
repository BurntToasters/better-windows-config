using System.Text;
using Bwc.UI.Console.List;
using Bwc.UI.Console.Shell;
using Bwc.UI.Dialogs;

namespace Bwc.UI.Console.Frame;

/// <summary>Why an export did not write the file. The three causes each have a message of their own.</summary>
public enum ConsoleListExportFailure
{
    /// <summary>The export wrote the file.</summary>
    None,

    /// <summary>The file cannot be created (a missing folder, no right to create in it).</summary>
    CannotCreate,

    /// <summary>The file was created and cannot be written (a full disk, a lost share).</summary>
    CannotWrite,

    /// <summary>The chosen file exists and is read-only.</summary>
    ReadOnly,
}

/// <summary>
/// Writes the list as text for Action > Export List (docs/failure-modes/console-frame.md, section 3). The text is the
/// list as shown now: the displayed columns in display order, every row in the order shown. The first line holds the
/// column headers. Every line, the last too, ends with CR LF. A tab type joins cells with a tab and turns a tab, CR or
/// LF inside a cell into one space. A comma type joins cells with a comma and wraps a cell that holds a comma or a
/// double quote in double quotes, each quote doubled. An ANSI type writes the system ANSI code page; a Unicode type
/// writes UTF-16LE with a byte order mark. Nothing here changes the list.
/// </summary>
public static class ConsoleListExport
{
    private const string LineEnd = "\r\n";

    /// <summary>Builds the text of the list in the chosen separator.</summary>
    public static string BuildText(ConsoleList list, bool comma)
    {
        ArgumentNullException.ThrowIfNull(list);
        IReadOnlyList<ConsoleListColumn> columns = list.VisibleColumns;
        var text = new StringBuilder();
        AppendLine(text, columns.Select(column => column.Header), comma);
        foreach (ConsoleListRow row in list.Rows)
        {
            AppendLine(text, columns.Select(column => row.CellText(column.Index)), comma);
        }

        return text.ToString();
    }

    /// <summary>
    /// Writes the list to <paramref name="path"/> in the format of the save dialog. The text goes to a file beside the target
    /// first and replaces the target in one move, so a failure leaves no partial file under the chosen name.
    /// </summary>
    public static ConsoleListExportFailure Write(string path, ConsoleList list, ExportListFileFormat format)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(list);

        bool comma = format is ExportListFileFormat.AnsiComma or ExportListFileFormat.UnicodeComma;
        bool unicode = format is ExportListFileFormat.UnicodeTab or ExportListFileFormat.UnicodeComma;
        byte[] bytes = Encode(BuildText(list, comma), unicode);

        try
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            {
                return ConsoleListExportFailure.ReadOnly;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ConsoleListExportFailure.CannotCreate;
        }

        string temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        FileStream stream;
        try
        {
            stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ConsoleListExportFailure.CannotCreate;
        }

        try
        {
            using (stream)
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
            return ConsoleListExportFailure.None;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            return ex is UnauthorizedAccessException ? ConsoleListExportFailure.ReadOnly : ConsoleListExportFailure.CannotWrite;
        }
    }

    /// <summary>The message of a failure, in the words of the frame.</summary>
    public static string MessageFor(ConsoleListExportFailure failure) =>
        failure switch
        {
            ConsoleListExportFailure.ReadOnly => ConsoleStrings.Get("ConsoleExportErrorReadOnly"),
            ConsoleListExportFailure.CannotWrite => ConsoleStrings.Get("ConsoleExportErrorWrite"),
            _ => ConsoleStrings.Get("ConsoleExportErrorCreate"),
        };

    private static void AppendLine(StringBuilder text, IEnumerable<string> cells, bool comma)
    {
        bool first = true;
        foreach (string cell in cells)
        {
            if (!first)
            {
                _ = text.Append(comma ? ',' : '\t');
            }

            first = false;
            _ = text.Append(comma ? CommaCell(cell) : TabCell(cell));
        }

        _ = text.Append(LineEnd);
    }

    private static string TabCell(string cell) =>
        cell.Any(c => c is '\t' or '\r' or '\n')
            ? string.Concat(cell.Select(c => c is '\t' or '\r' or '\n' ? ' ' : c))
            : cell;

    private static string CommaCell(string cell) =>
        cell.Contains(',') || cell.Contains('"') ? "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : cell;

    private static byte[] Encode(string text, bool unicode)
    {
        if (unicode)
        {
            byte[] body = Encoding.Unicode.GetBytes(text);
            var bytes = new byte[body.Length + 2];
            bytes[0] = 0xFF;
            bytes[1] = 0xFE;
            Buffer.BlockCopy(body, 0, bytes, 2, body.Length);
            return bytes;
        }

        return AnsiEncoding().GetBytes(text);
    }

    /// <summary>The system ANSI code page; a character it cannot hold becomes '?'. ASCII if it is unknown.</summary>
    private static Encoding AnsiEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            // Code page 0 is the active ANSI code page of the system.
            return Encoding.GetEncoding(0, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.GetEncoding("us-ascii", EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The temporary file stays; the target is untouched.
        }
    }
}

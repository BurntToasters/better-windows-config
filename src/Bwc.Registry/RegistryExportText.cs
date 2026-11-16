using System.Globalization;
using System.Text;
using Bwc.Registry.Resources;

namespace Bwc.Registry;

/// <summary>
/// Streams the *Text Files* export in the classic regedit layout. The golden is
/// <c>docs/parity/oracle/exports/bwc-oracle-text.txt</c>, and <c>docs/failure-modes/export.md</c>, section 2, lists
/// the rules and the open points. It is also the print report, so File > Export and Print write the same text. Only
/// the current key's values and subkey names are held in memory, and no value is cut or capped. The format is
/// display-only and lossy; use a .reg file or a hive for data that must be applied again.
/// </summary>
public static class RegistryTextExporter
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorFileNotFound = 2;
    private const int ErrorKeyDeleted = 1018;
    private const string Crlf = "\r\n";
    private const string ComputerPrefix = "Computer\\";
    private const string NoClass = "<NO CLASS>";
    private const string NoName = "<NO NAME>";
    private const string ListContinuation = "                   ";
    private const int HexRowBytes = 16;
    private const int HexFieldEnd = 61;

    public static void Export(
        RegistryReader reader,
        RegistryPath startKey,
        TextWriter writer,
        CancellationToken cancellationToken = default,
        IProgress<ExportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        Export(new RegistryReaderExportSource(reader), startKey, writer, cancellationToken, progress);
    }

    /// <summary>
    /// Writes the text layout for one branch. <paramref name="keyDisplay"/> names each key in its Key Name line. Null
    /// is the local form (<see cref="RegistryPath.ToDisplayString"/>); a remote key passes its classic
    /// \\computer\HKEY_... form.
    /// </summary>
    public static void Export(
        IRegistryExportSource source,
        RegistryPath startKey,
        TextWriter writer,
        CancellationToken cancellationToken = default,
        IProgress<ExportProgress>? progress = null,
        Func<RegistryPath, string>? keyDisplay = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(startKey);
        ArgumentNullException.ThrowIfNull(writer);
        cancellationToken.ThrowIfCancellationRequested();

        var state = new TextExportState { KeyDisplay = keyDisplay };
        var gate = new ExportProgressGate(progress);
        gate.Always(Starting(startKey, state));
        Walk(source, startKey, writer, isStart: true, cancellationToken, gate, state);
        gate.Always(Completed(startKey, state));
    }

    public static void ExportWholeRegistry(
        RegistryReader reader,
        TextWriter writer,
        CancellationToken cancellationToken = default,
        IProgress<ExportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ExportWholeRegistry(new RegistryReaderExportSource(reader), writer, cancellationToken, progress);
    }

    public static void ExportWholeRegistry(
        IRegistryExportSource source,
        TextWriter writer,
        CancellationToken cancellationToken = default,
        IProgress<ExportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(writer);
        cancellationToken.ThrowIfCancellationRequested();

        var state = new TextExportState();
        IReadOnlyList<RegistryPath> roots = RegistryExportRoots.Classic;
        var gate = new ExportProgressGate(progress);
        gate.Always(Starting(roots[0], state));

        foreach (RegistryPath root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Walk(source, root, writer, isStart: false, cancellationToken, gate, state);
        }

        gate.Always(Completed(roots[^1], state));
    }

    private static void Walk(
        IRegistryExportSource source,
        RegistryPath path,
        TextWriter writer,
        bool isStart,
        CancellationToken cancellationToken,
        ExportProgressGate gate,
        TextExportState state)
    {
        cancellationToken.ThrowIfCancellationRequested();

        RegistryKeyInfo info;
        IReadOnlyList<RegistryValueInfo> values;
        IReadOnlyList<string> subKeyNames;

        try
        {
            info = source.GetKeyInfo(path, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            values = source.EnumerateValues(path, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            subKeyNames = source.EnumerateSubKeyNames(path, cancellationToken);
        }
        catch (RegistryOperationException ex) when (!isStart && IsSkippableKeyError(ex.Win32Error))
        {
            state.KeysSkipped += 1;
            gate.Always(new ExportProgress(
                ExportPhase.KeySkipped,
                path,
                null,
                state.KeysExported,
                state.ValuesExported,
                state.KeysSkipped));
            return;
        }

        string keyName = state.KeyDisplay?.Invoke(path) ?? path.ToDisplayString();
        WriteLine(writer, "Key Name:          " + WithoutComputerPrefix(keyName));
        WriteLine(writer, "Class Name:        " + (info.ClassName.Length == 0 ? NoClass : info.ClassName));
        WriteLine(writer, "Last Write Time:   " + FormatLastWrite(info.LastWriteTimeUtc));

        for (int index = 0; index < values.Count; index += 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RegistryValueInfo value = values[index];
            WriteLine(writer, "Value " + index.ToString(CultureInfo.InvariantCulture));
            WriteLine(writer, "  Name:            " + (value.Name.Length == 0 ? NoName : value.Name));
            WriteLine(writer, "  Type:            " + RegistryValueTypeExtensions.ToTypeName(value.Type));

            // The first line of the data follows the label. FormatDataLines always yields at least one line.
            bool first = true;
            foreach (string line in FormatDataLines(value))
            {
                WriteLine(writer, first ? "  Data:            " + line : line);
                first = false;
            }

            WriteLine(writer, string.Empty);

            state.ValuesExported += 1;
            gate.Throttled(new ExportProgress(
                ExportPhase.ValueExported,
                path,
                value.Name,
                state.KeysExported,
                state.ValuesExported,
                state.KeysSkipped));
        }

        WriteLine(writer, string.Empty);

        state.KeysExported += 1;
        gate.Throttled(new ExportProgress(
            ExportPhase.KeyExported,
            path,
            null,
            state.KeysExported,
            state.ValuesExported,
            state.KeysSkipped));

        foreach (string subKeyName in subKeyNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Walk(source, path.AppendEnumeratedName(subKeyName), writer, isStart: false, cancellationToken, gate, state);
        }
    }

    // The same skip set as the .reg exporter: a key that vanished or was deleted mid-walk is skipped.
    private static bool IsSkippableKeyError(int win32Error) =>
        win32Error is ErrorAccessDenied or ErrorFileNotFound or ErrorKeyDeleted;

    private static ExportProgress Starting(RegistryPath path, TextExportState state) =>
        new(ExportPhase.Starting, path, null, state.KeysExported, state.ValuesExported, state.KeysSkipped);

    private static ExportProgress Completed(RegistryPath path, TextExportState state) =>
        new(ExportPhase.Completed, path, null, state.KeysExported, state.ValuesExported, state.KeysSkipped);

    private static string WithoutComputerPrefix(string path) =>
        path.StartsWith(ComputerPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[ComputerPrefix.Length..]
            : path;

    // The local time as the current culture's short date, " - ", and short time: en-US gives "11/14/2026 - 7:25 PM".
    private static string FormatLastWrite(DateTime utc)
    {
        DateTime local = utc.ToLocalTime();
        return local.ToString("d", CultureInfo.CurrentCulture) + " - " + local.ToString("t", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// The data lines of one value. The first line goes after the Data label and is empty when the data is on the
    /// following lines. Every later line is a whole line.
    /// </summary>
    private static IEnumerable<string> FormatDataLines(RegistryValueInfo value)
    {
        switch (value.Type)
        {
            case 1:
            case 2:
                yield return RegistryValueCodec.DecodeString(value.Data);
                yield break;

            case 4:
                yield return FormatDword(value.Data);
                yield break;

            case 7:
                foreach (string line in MultiStringLines(value.Data))
                {
                    yield return line;
                }

                yield break;

            case 8:
            case 9:
            case 10:
            {
                List<string>? resourceLines = ResourceTextFormatter.TryFormat(value.Type, value.Data);
                if (resourceLines is not null)
                {
                    yield return string.Empty;
                    foreach (string line in resourceLines)
                    {
                        yield return line;
                    }

                    yield break;
                }

                // A structure that does not parse is shown as a hex dump of the whole value (unmeasured).
                break;
            }
        }

        yield return string.Empty;
        foreach (string row in HexDumpRows(value.Data))
        {
            yield return row;
        }
    }

    // C's %#x: zero prints as 0. Data shorter than four bytes is zero-extended; bytes after the fourth are ignored.
    private static string FormatDword(byte[] data)
    {
        uint value = 0;
        for (int index = 0; index < Math.Min(4, data.Length); index += 1)
        {
            value |= (uint)data[index] << (8 * index);
        }

        return value == 0 ? "0" : "0x" + value.ToString("x", CultureInfo.InvariantCulture);
    }

    // Trailing empty strings are the terminators and are dropped. An empty middle string is a line of 19 spaces.
    private static IEnumerable<string> MultiStringLines(byte[] data)
    {
        var items = new List<string>(RegistryValueCodec.DecodeStringRaw(data).Split('\0'));
        while (items.Count > 0 && items[^1].Length == 0)
        {
            items.RemoveAt(items.Count - 1);
        }

        if (items.Count == 0)
        {
            yield return string.Empty;
            yield break;
        }

        yield return items[0];
        for (int index = 1; index < items.Count; index += 1)
        {
            yield return ListContinuation + items[index];
        }
    }

    // Each row: an 8-digit lowercase offset, three spaces, the bytes as "xx " with "- " after the 8th, padded to
    // column 61, one space, then the ASCII column at column 62.
    private static IEnumerable<string> HexDumpRows(byte[] data)
    {
        for (int offset = 0; offset < data.Length; offset += HexRowBytes)
        {
            int count = Math.Min(HexRowBytes, data.Length - offset);
            var row = new StringBuilder(96);
            row.Append(offset.ToString("x8", CultureInfo.InvariantCulture)).Append("   ");

            for (int index = 0; index < count; index += 1)
            {
                row.Append(data[offset + index].ToString("x2", CultureInfo.InvariantCulture)).Append(' ');
                if (index == 7)
                {
                    row.Append("- ");
                }
            }

            while (row.Length < HexFieldEnd)
            {
                row.Append(' ');
            }

            row.Append(' ');
            for (int index = 0; index < count; index += 1)
            {
                row.Append(AsciiOf(data[offset + index]));
            }

            yield return row.ToString();
        }
    }

    private static char AsciiOf(byte value)
    {
        if (value == 0x09)
        {
            return '\t';
        }

        return value < 0x20 || (value >= 0x7F && value <= 0x9F) ? '.' : (char)value;
    }

    private static void WriteLine(TextWriter writer, string text)
    {
        writer.Write(text);
        writer.Write(Crlf);
    }

    private sealed class TextExportState
    {
        /// <summary>Names a key in its Key Name line. Null for the local form.</summary>
        public Func<RegistryPath, string>? KeyDisplay { get; set; }

        public int KeysExported { get; set; }

        public int ValuesExported { get; set; }

        public int KeysSkipped { get; set; }
    }
}

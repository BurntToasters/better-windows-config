using System.Globalization;
using System.Text;

namespace Bwc.Registry;

/// <summary>
/// Streams the stable classic-style *Text Files* export described in
/// <c>docs/failure-modes/export.md</c>. Only the current key's values and subkey names are held in
/// memory. The format is display-only and lossy; use a .reg file or a hive for data that must be
/// applied again.
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
    private const string ValueNotSet = "(value not set)";
    private const string ZeroLengthBinary = "(zero-length binary value)";
    private const string HexDigits = "0123456789ABCDEF";

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

    public static void Export(
        IRegistryExportSource source,
        RegistryPath startKey,
        TextWriter writer,
        CancellationToken cancellationToken = default,
        IProgress<ExportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(startKey);
        ArgumentNullException.ThrowIfNull(writer);
        cancellationToken.ThrowIfCancellationRequested();

        var state = new TextExportState();
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

        WriteLine(writer, "Key Name:          " + WithoutComputerPrefix(path.ToDisplayString()));
        WriteLine(writer, "Class Name:        " + (info.ClassName.Length == 0 ? NoClass : Escape(info.ClassName)));
        WriteLine(
            writer,
            "Last Write Time:   "
            + info.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            + " UTC");
        WriteLine(writer, string.Empty);

        for (int index = 0; index < values.Count; index += 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RegistryValueInfo value = values[index];
            WriteLine(writer, "Value " + index.ToString(CultureInfo.InvariantCulture));
            WriteLine(writer, "  Name:            " + FormatName(value.Name));
            WriteLine(writer, "  Type:            " + RegistryValueTypeExtensions.ToTypeName(value.Type));
            WriteLine(writer, "  Data:            " + FormatData(value));
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

    private static string FormatName(string name) =>
        name.Length == 0 ? NoName : Escape(name);

    private static string FormatData(RegistryValueInfo value)
    {
        switch (value.Type)
        {
            case 1:
            case 2:
            case 6:
                return value.DataLength == 0
                    ? ValueNotSet
                    : Escape(RegistryValueCodec.DecodeString(value.Data));

            case 7:
                if (value.DataLength == 0)
                {
                    return ValueNotSet;
                }

                IReadOnlyList<string> items = RegistryValueCodec.DecodeMultiString(value.Data).Strings;
                if (items.Count == 0)
                {
                    return ValueNotSet;
                }

                return string.Join(", ", items.Select(item => "\"" + Escape(item) + "\""));

            case 4 when RegistryValueCodec.IsValidDword(value.Data):
            case 11 when RegistryValueCodec.IsValidQword(value.Data):
                return RegistryValueDisplay.FormatData(value, int.MaxValue);

            default:
                return value.DataLength == 0 ? ZeroLengthBinary : FormatHex(value.Data);
        }
    }

    private static string FormatHex(byte[] data)
    {
        var builder = new StringBuilder(data.Length * 3);

        for (int index = 0; index < data.Length; index += 1)
        {
            if (index > 0)
            {
                builder.Append(' ');
            }

            builder.Append(HexDigits[data[index] >> 4]);
            builder.Append(HexDigits[data[index] & 0x0F]);
        }

        return builder.ToString();
    }

    private static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (char character in text)
        {
            switch (character)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\0':
                    builder.Append("\\0");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }

    private static void WriteLine(TextWriter writer, string text)
    {
        writer.Write(text);
        writer.Write(Crlf);
    }

    private sealed class TextExportState
    {
        public int KeysExported { get; set; }

        public int ValuesExported { get; set; }

        public int KeysSkipped { get; set; }
    }
}

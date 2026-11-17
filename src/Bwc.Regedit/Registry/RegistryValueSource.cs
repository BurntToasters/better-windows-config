using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Bwc.Core;
using Bwc.Regedit.Controllers;
using Bwc.Registry;
using Bwc.UI.List;

namespace Bwc.Regedit.Registry;

/// <summary>
/// The rows of one key. Built on the thread pool and never stored there: the caller applies it on the UI thread,
/// and only when it is still the current load. <paramref name="Failure"/> is the enumeration error when the rows
/// could not be read.
/// </summary>
public sealed record RegistryValueListing(
    IReadOnlyList<DetailsRow> Rows,
    RegistryOperationException? Failure = null);

/// <summary>
/// Loads the details rows for one key. Enumeration and formatting run on the thread pool and return a
/// <see cref="RegistryValueListing"/>; the caller applies it on the UI thread. The rows are not sorted here: the
/// list sorts them once, in <c>DetailsList</c>. Registry failures produce an empty list with the error in Failure,
/// never an exception to the window.
/// </summary>
public sealed class RegistryValueSource
{
    /// <summary>Type column text of the synthetic default row (classic regedit shows REG_SZ).</summary>
    private static readonly string SyntheticDefaultType = RegistryValueTypeExtensions.ToTypeName((uint)RegistryValueType.String);
    private static readonly string SyntheticDefaultData = MessageCatalog.ValueNotSetData;
    private static readonly IReadOnlyList<DetailsRow> NoRows = Array.Empty<DetailsRow>();

    // Classic regedit shows at most this many characters of Data, then "...".
    private const int ClassicDataCharacters = 192;
    private const string Ellipsis = "...";
    private const string DwordBigEndianType = "REG_DWORD_BIG_ENDIAN";
    private const uint DwordBigEndianNumber = 5;
    private const string UnknownTypeName = "REG_UNKNOWN";

    private readonly RegistryReader _reader;

    public RegistryValueSource(RegistryReader? reader = null)
    {
        _reader = reader ?? new RegistryReader();
    }

    /// <summary>Reads the rows of a key on the thread pool. The result is not stored in this source.</summary>
    public Task<RegistryValueListing> ReadAsync(RegistryPath path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Task.Run(() => Read(path, cancellationToken), cancellationToken);
    }

    private RegistryValueListing Read(RegistryPath path, CancellationToken cancellationToken)
    {
        try
        {
            ThrowIfInjectedEnumerationFailure(path);
            WaitForInjectedLoadDelay(path);
            IReadOnlyList<RegistryValueInfo> values = _reader.EnumerateValues(path, cancellationToken);

            var rows = new List<DetailsRow>(values.Count + 1);

            // Classic regedit lists (Default) for every key. A key without an unnamed value gets a synthetic row with
            // no registry value behind it. A value literally named (Default) is a separate row with its own raw name.
            if (!values.Any(value => value.Name.Length == 0))
            {
                rows.Add(new DetailsRow(
                    MessageCatalog.DefaultValueLabel,
                    string.Empty,
                    SyntheticDefaultType,
                    SyntheticDefaultData,
                    renameable: false,
                    isSynthetic: true));
            }

            foreach (RegistryValueInfo value in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add(new DetailsRow(
                    RegistryValueDisplay.FormatName(value),
                    value.Name,
                    ListTypeText(value),
                    ListDataText(value),
                    renameable: value.Name.Length > 0));
            }

            return new RegistryValueListing(rows);
        }
        catch (RegistryOperationException ex)
        {
            Log.Warn(
                $"Registry values for '{path.ToDisplayString()}' could not be read: Win32 {ex.Win32Error} ({ex.Message}).");
            return new RegistryValueListing(NoRows, ex);
        }
    }

    /// <summary>
    /// The Type cell. Classic names REG_DWORD_BIG_ENDIAN (type 5) and shows any other unnamed type as the number in
    /// lowercase hex with a 0x prefix (0x1234).
    /// </summary>
    internal static string ListTypeText(RegistryValueInfo value)
    {
        if (value.Type == DwordBigEndianNumber)
        {
            return DwordBigEndianType;
        }

        string name = RegistryValueDisplay.FormatType(value);
        return string.Equals(name, UnknownTypeName, StringComparison.Ordinal)
            ? "0x" + value.Type.ToString("x", CultureInfo.InvariantCulture)
            : name;
    }

    /// <summary>
    /// The Data cell, as classic regedit's list shows it. Strings stop at the first NUL, zero-length strings are empty,
    /// and a multi-string is each string followed by a space, up to the first empty string. Numbers use the oracle
    /// strings. Other types are lowercase hex bytes. Text longer than 192 characters is cut and ends with "...".
    /// </summary>
    internal static string ListDataText(RegistryValueInfo value)
    {
        string text = value.Type switch
        {
            (uint)RegistryValueType.String or (uint)RegistryValueType.ExpandString =>
                RegistryValueCodec.DecodeString(value.Data),
            (uint)RegistryValueType.MultiString => MultiStringText(value.Data),
            (uint)RegistryValueType.Dword when RegistryValueCodec.IsValidDword(value.Data) =>
                NumberText(RegistryValueCodec.DecodeDword(value.Data)),
            (uint)RegistryValueType.Dword => RegistryValueDisplay.FormatData(value),
            DwordBigEndianNumber when value.Data.Length == 4 =>
                NumberText(BinaryPrimitives.ReadUInt32BigEndian(value.Data)),
            DwordBigEndianNumber => RegistryValueDisplay.FormatData(AsDword(value)),
            (uint)RegistryValueType.Qword when RegistryValueCodec.IsValidQword(value.Data) =>
                NumberText(RegistryValueCodec.DecodeQword(value.Data)),
            (uint)RegistryValueType.Qword => RegistryValueDisplay.FormatData(value),
            _ => value.DataLength == 0 ? RegistryValueDisplay.FormatData(value) : HexText(value.Data),
        };

        // The cut drops trailing spaces before the ellipsis: a hex cut ends in "3f ", shown as "3f...".
        return text.Length > ClassicDataCharacters
            ? text[..ClassicDataCharacters].TrimEnd() + Ellipsis
            : text;
    }

    /// <summary>
    /// The same value read as REG_DWORD. Its invalid-length text (oracle string 30) comes from the shared formatter.
    /// </summary>
    private static RegistryValueInfo AsDword(RegistryValueInfo value) =>
        new(value.Name, (uint)RegistryValueType.Dword, value.Data);

    /// <summary>Oracle strings 29 and 32: 0x%08x (%u). Lowercase hex, eight digits minimum, unsigned.</summary>
    private static string NumberText(ulong value) =>
        "0x" + value.ToString("x8", CultureInfo.InvariantCulture) + " (" + value.ToString(CultureInfo.InvariantCulture) + ")";

    private static string MultiStringText(byte[] data)
    {
        // Classic walks the strings with a pointer and stops at the first empty one. It adds whole strings, each with a
        // space, while they fit in the budget, and then stops with no ellipsis (measured: "s00 ... s47 " at 192).
        string raw = RegistryValueCodec.DecodeStringRaw(data);
        var builder = new StringBuilder();
        var start = 0;
        while (start < raw.Length && raw[start] != '\0')
        {
            int end = raw.IndexOf('\0', start);
            if (end < 0)
            {
                end = raw.Length;
            }

            if (builder.Length + (end - start) + 1 > ClassicDataCharacters)
            {
                break;
            }

            _ = builder.Append(raw, start, end - start).Append(' ');
            start = end + 1;
        }

        return builder.ToString();
    }

    private static string HexText(byte[] data)
    {
        var builder = new StringBuilder(data.Length * 3);
        for (var index = 0; index < data.Length; index += 1)
        {
            if (index > 0)
            {
                _ = builder.Append(' ');
            }

            _ = builder.Append(data[index].ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

#if BWC_E2E
    /// <summary>
    /// E2E only: BWC_E2E_FAIL_VALUE_ENUMERATION names one display path whose value enumeration fails with
    /// ERROR_NOT_ENOUGH_MEMORY. The ship build has no such check.
    /// </summary>
    private static readonly string? InjectedEnumerationFailure =
        Environment.GetEnvironmentVariable("BWC_E2E_FAIL_VALUE_ENUMERATION");

    private static void ThrowIfInjectedEnumerationFailure(RegistryPath path)
    {
        if (!string.IsNullOrEmpty(InjectedEnumerationFailure) &&
            string.Equals(StripComputer(path.ToDisplayString()), StripComputer(InjectedEnumerationFailure), StringComparison.OrdinalIgnoreCase))
        {
            throw new RegistryOperationException("EnumerateValues", path, 8, "injected by BWC_E2E_FAIL_VALUE_ENUMERATION");
        }
    }

    /// <summary>
    /// E2E only: BWC_E2E_DELAY_VALUE_LOAD is "display path|milliseconds". The value load of that key
    /// waits on the thread pool before it reads, so a test can hold its rows back. The ship build has no delay.
    /// </summary>
    private static readonly string? InjectedLoadDelay = Environment.GetEnvironmentVariable("BWC_E2E_DELAY_VALUE_LOAD");

    private static void WaitForInjectedLoadDelay(RegistryPath path)
    {
        if (string.IsNullOrEmpty(InjectedLoadDelay))
        {
            return;
        }

        int bar = InjectedLoadDelay.LastIndexOf('|');
        if (bar <= 0 ||
            !int.TryParse(InjectedLoadDelay[(bar + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int delayMs) ||
            !string.Equals(StripComputer(path.ToDisplayString()), StripComputer(InjectedLoadDelay[..bar]), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        System.Threading.Thread.Sleep(delayMs);
    }

    /// <summary>Drops a leading "Computer\" so the address form and the tree form of a path compare equal.</summary>
    private static string StripComputer(string displayPath) =>
        displayPath.StartsWith("Computer\\", StringComparison.OrdinalIgnoreCase) ? displayPath["Computer\\".Length..] : displayPath;
#else
    private static void ThrowIfInjectedEnumerationFailure(RegistryPath path)
    {
        _ = path;
    }

    private static void WaitForInjectedLoadDelay(RegistryPath path)
    {
        _ = path;
    }
#endif
}

using System.Globalization;
using System.Text;

namespace Bwc.Registry;

/// <summary>
/// The classic regedit Data-column rendering. This type has no UI or resource dependency; the
/// exact strings are fixed so callers can compare them with the oracle.
/// </summary>
public static class RegistryValueDisplay
{
    private const string ValueNotSet = "(value not set)";
    private const string ZeroLengthBinary = "(zero-length binary value)";
    private const string InvalidDword = "(invalid DWORD (32-bit) value)";
    private const string InvalidQword = "(invalid QWORD (64-bit) value)";
    private const string DefaultValueName = "(Default)";
    private const string Ellipsis = "...";

    /// <summary>
    /// Formats the raw value for the Data column. The result is kept within
    /// <paramref name="maxChars"/>; when truncation is needed the ellipsis counts toward the
    /// budget.
    /// </summary>
    public static string FormatData(RegistryValueInfo value, int maxChars = 1024)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(maxChars);

        string text;

        switch (value.Type)
        {
            case (uint)RegistryValueType.String:
            case (uint)RegistryValueType.ExpandString:
            case (uint)RegistryValueType.Link:
                text = value.DataLength == 0
                    ? ValueNotSet
                    : RegistryValueCodec.DecodeString(value.Data);
                break;

            case (uint)RegistryValueType.MultiString:
                text = value.DataLength == 0
                    ? ValueNotSet
                    : FormatMultiString(value.Data);
                break;

            case (uint)RegistryValueType.Dword:
                text = RegistryValueCodec.IsValidDword(value.Data)
                    ? FormatNumber(RegistryValueCodec.DecodeDword(value.Data))
                    : InvalidDword;
                break;

            case (uint)RegistryValueType.Qword:
                text = RegistryValueCodec.IsValidQword(value.Data)
                    ? FormatNumber(RegistryValueCodec.DecodeQword(value.Data))
                    : InvalidQword;
                break;

            case (uint)RegistryValueType.Binary:
                text = value.DataLength == 0 ? ZeroLengthBinary : FormatHex(value.Data);
                break;

            default:
                text = value.DataLength == 0 ? ZeroLengthBinary : FormatHex(value.Data);
                break;
        }

        return Truncate(text, maxChars);
    }

    public static string FormatType(RegistryValueInfo value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return RegistryValueTypeExtensions.ToTypeName(value.Type);
    }

    public static string FormatName(RegistryValueInfo value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return FormatName(value.Name);
    }

    public static string FormatName(string valueName)
    {
        ArgumentNullException.ThrowIfNull(valueName);
        return valueName.Length == 0 ? DefaultValueName : valueName;
    }

    /// <summary>
    /// Oracle strings 29 and 32: <c>0x%1!08x! (%1!u!)</c> for DWORD and
    /// <c>0x%1!08I64x! (%1!I64u!)</c> for QWORD. The hex part is lowercase with a minimum of eight
    /// digits, and the decimal part is unsigned. The width is a minimum, so a QWORD above 32 bits
    /// prints all of its digits.
    /// </summary>
    private static string FormatNumber(ulong value)
    {
        string hex = value.ToString("x8", CultureInfo.InvariantCulture);
        string decimalText = value.ToString(CultureInfo.InvariantCulture);
        return "0x" + hex + " (" + decimalText + ")";
    }

    private static string FormatMultiString(byte[] data)
    {
        MultiStringDecodeResult decoded = RegistryValueCodec.DecodeMultiString(data);
        return string.Join(' ', decoded.Strings);
    }

    private static string FormatHex(byte[] data)
    {
        StringBuilder builder = new(data.Length * 3);

        for (int index = 0; index < data.Length; index += 1)
        {
            if (index > 0)
            {
                builder.Append(' ');
            }

            builder.Append(data[index].ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string Truncate(string text, int maxChars)
    {
        if (maxChars == 0)
        {
            return string.Empty;
        }

        if (text.Length <= maxChars)
        {
            return text;
        }

        if (maxChars <= Ellipsis.Length)
        {
            return text[..maxChars];
        }

        return text[..(maxChars - Ellipsis.Length)] + Ellipsis;
    }
}

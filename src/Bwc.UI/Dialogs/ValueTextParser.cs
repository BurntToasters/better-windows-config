using System.Globalization;
using System.Text;

namespace Bwc.UI.Dialogs;

/// <summary>Parsers shared by the numeric, multi-string, and binary editors.</summary>
internal static class ValueTextParser
{
    internal static bool TryParseDword(string? text, out uint value, out string? error)
    {
        bool parsed = TryParseUnsigned(text, 32, out ulong wide, out error);
        value = (uint)wide;
        return parsed;
    }

    internal static bool TryParseQword(string? text, out ulong value, out string? error) =>
        TryParseUnsigned(text, 64, out value, out error);

    internal static bool TryParseBinary(string? text, out byte[] value, out string? error)
    {
        value = Array.Empty<byte>();
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var digits = new StringBuilder(text.Length);
        foreach (char character in text)
        {
            if (char.IsWhiteSpace(character) || character == ',')
            {
                continue;
            }

            if (!Uri.IsHexDigit(character))
            {
                error = $"'{character}' is not a hexadecimal digit.";
                return false;
            }

            digits.Append(character);
        }

        if (digits.Length % 2 != 0)
        {
            error = "The binary value must contain an even number of hexadecimal digits.";
            return false;
        }

        try
        {
            value = Convert.FromHexString(digits.ToString());
            return true;
        }
        catch (FormatException)
        {
            error = "The binary value is not valid hexadecimal data.";
            return false;
        }
    }

    internal static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<string>();
        }

        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }

    private static bool TryParseUnsigned(string? text, int bits, out ulong value, out string? error)
    {
        value = 0;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Enter a value.";
            return false;
        }

        string trimmed = text.Trim();
        string digits = trimmed;
        bool hex = false;

        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hex = true;
            digits = trimmed[2..];
            if (digits.Length == 0)
            {
                error = "The 0x prefix must be followed by hexadecimal digits.";
                return false;
            }
        }

        NumberStyles style = hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None;
        if (!ulong.TryParse(digits, style, CultureInfo.InvariantCulture, out ulong parsed))
        {
            error = hex
                ? "Enter a valid hexadecimal number such as 0x1A2B."
                : "Enter a decimal number or a hexadecimal number prefixed with 0x.";
            return false;
        }

        ulong maximum = bits == 32 ? uint.MaxValue : ulong.MaxValue;
        if (parsed > maximum)
        {
            error = $"The value is too large for a {bits}-bit value.";
            return false;
        }

        value = parsed;
        return true;
    }
}

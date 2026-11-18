using System.Buffers.Binary;

namespace Bwc.Gpo.Pol;

/// <summary>Encoders and decoders for the data bytes of common value types.</summary>
public static class PolData
{
    /// <summary>Encodes text as UTF-16LE followed by one NUL terminator, the data of a REG_SZ or REG_EXPAND_SZ.</summary>
    /// <param name="text">The text. It must not be null.</param>
    /// <returns>A new array.</returns>
    public static byte[] FromString(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = new byte[(text.Length + 1) * 2];
        for (var i = 0; i < text.Length; i++)
        {
            bytes[i * 2] = (byte)(text[i] & 0xFF);
            bytes[(i * 2) + 1] = (byte)(text[i] >> 8);
        }

        return bytes;
    }

    /// <summary>Encodes a REG_DWORD as four little-endian bytes.</summary>
    /// <param name="value">The value.</param>
    /// <returns>A new array.</returns>
    public static byte[] FromDWord(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    /// <summary>Encodes a REG_QWORD as eight little-endian bytes.</summary>
    /// <param name="value">The value.</param>
    /// <returns>A new array.</returns>
    public static byte[] FromQWord(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return bytes;
    }

    /// <summary>
    /// Decodes string data: UTF-16LE text with one trailing NUL removed when present. Data without a terminator is
    /// decoded as is.
    /// </summary>
    /// <param name="data">The record data.</param>
    /// <returns>The text, or null when the length is odd.</returns>
    public static string? TryGetString(ReadOnlySpan<byte> data)
    {
        if ((data.Length & 1) != 0)
        {
            return null;
        }

        var count = data.Length / 2;
        if (count > 0 && data[^1] == 0 && data[^2] == 0)
        {
            count--;
        }

        return string.Create(count, data.ToArray(), static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = (char)(source[i * 2] | (source[(i * 2) + 1] << 8));
            }
        });
    }

    /// <summary>Decodes a REG_DWORD.</summary>
    /// <param name="data">The record data.</param>
    /// <returns>The value, or null when the data is not exactly four bytes.</returns>
    public static uint? TryGetDWord(ReadOnlySpan<byte> data) =>
        data.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data) : null;

    /// <summary>Decodes a REG_QWORD.</summary>
    /// <param name="data">The record data.</param>
    /// <returns>The value, or null when the data is not exactly eight bytes.</returns>
    public static ulong? TryGetQWord(ReadOnlySpan<byte> data) =>
        data.Length == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(data) : null;
}

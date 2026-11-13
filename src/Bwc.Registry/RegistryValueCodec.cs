using System.Buffers.Binary;
using System.Text;

namespace Bwc.Registry;

/// <summary>
/// Lossless encode and decode helpers for the common registry value types. Decoders are
/// tolerant of odd byte lengths so data read from the registry never fails merely because it
/// was written by an unusual producer.
/// </summary>
public static class RegistryValueCodec
{
    /// <summary>
    /// Decodes UTF-16LE text for display and stops at the first NUL. An odd trailing byte is
    /// ignored because it cannot form a complete UTF-16 code unit.
    /// </summary>
    public static string DecodeString(ReadOnlySpan<byte> data)
    {
        string raw = DecodeStringRaw(data);
        int nul = raw.IndexOf('\0');
        return nul >= 0 ? raw[..nul] : raw;
    }

    /// <summary>
    /// Decodes every complete UTF-16LE code unit, including NUL characters and any stored
    /// terminator. An odd trailing byte is ignored.
    /// </summary>
    public static string DecodeStringRaw(ReadOnlySpan<byte> data)
    {
        int length = data.Length - (data.Length % 2);
        return Encoding.Unicode.GetString(data[..length]);
    }

    /// <summary>Encodes text as UTF-16LE with one terminating NUL.</summary>
    public static byte[] EncodeString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        byte[] text = Encoding.Unicode.GetBytes(value);
        byte[] result = new byte[text.Length + 2];
        text.CopyTo(result, 0);
        return result;
    }

    public static bool IsValidDword(ReadOnlySpan<byte> data) => data.Length == 4;

    public static bool IsValidQword(ReadOnlySpan<byte> data) => data.Length == 8;

    public static uint DecodeDword(ReadOnlySpan<byte> data)
    {
        if (!IsValidDword(data))
        {
            throw new ArgumentException("A REG_DWORD value must be exactly 4 bytes.", nameof(data));
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(data);
    }

    public static uint DecodeDwordBigEndian(ReadOnlySpan<byte> data)
    {
        if (!IsValidDword(data))
        {
            throw new ArgumentException("A REG_DWORD value must be exactly 4 bytes.", nameof(data));
        }

        return BinaryPrimitives.ReadUInt32BigEndian(data);
    }

    public static byte[] EncodeDword(uint value)
    {
        byte[] result = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(result, value);
        return result;
    }

    public static byte[] EncodeDwordBigEndian(uint value)
    {
        byte[] result = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(result, value);
        return result;
    }

    public static ulong DecodeQword(ReadOnlySpan<byte> data)
    {
        if (!IsValidQword(data))
        {
            throw new ArgumentException("A REG_QWORD value must be exactly 8 bytes.", nameof(data));
        }

        return BinaryPrimitives.ReadUInt64LittleEndian(data);
    }

    public static byte[] EncodeQword(ulong value)
    {
        byte[] result = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(result, value);
        return result;
    }

    /// <summary>
    /// Splits UTF-16LE multi-string data on NUL. Interior empty strings are preserved. A
    /// trailing single NUL is treated as one terminator and a trailing double NUL as the
    /// classic list terminator. The encoding cannot distinguish an empty list from one empty
    /// string when both use the same two NUL bytes.
    /// </summary>
    public static MultiStringDecodeResult DecodeMultiString(ReadOnlySpan<byte> data)
    {
        bool hasDoubleNullTerminator = HasDoubleNullTerminator(data);
        string text = DecodeStringRaw(data);

        if (text.Length == 0)
        {
            return new MultiStringDecodeResult(Array.Empty<string>(), hasDoubleNullTerminator);
        }

        int contentLength = text.Length;
        if (hasDoubleNullTerminator && contentLength >= 2)
        {
            contentLength -= 2;
        }
        else if (text[contentLength - 1] == '\0')
        {
            contentLength -= 1;
        }

        if (contentLength == 0)
        {
            return new MultiStringDecodeResult(Array.Empty<string>(), hasDoubleNullTerminator);
        }

        return new MultiStringDecodeResult(
            text[..contentLength].Split('\0'),
            hasDoubleNullTerminator);
    }

    /// <summary>Joins strings with NUL and appends the classic double-NUL terminator.</summary>
    public static byte[] EncodeMultiString(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        StringBuilder builder = new();
        foreach (string value in values)
        {
            builder.Append(value);
            builder.Append('\0');
        }

        builder.Append('\0');

        if (values.Count == 0)
        {
            builder.Append('\0');
        }

        return Encoding.Unicode.GetBytes(builder.ToString());
    }

    private static bool HasDoubleNullTerminator(ReadOnlySpan<byte> data) =>
        data.Length >= 4
        && data[^1] == 0
        && data[^2] == 0
        && data[^3] == 0
        && data[^4] == 0;
}

/// <summary>The strings and terminator observation from <see cref="RegistryValueCodec.DecodeMultiString"/>.</summary>
public sealed class MultiStringDecodeResult
{
    public MultiStringDecodeResult(IReadOnlyList<string> strings, bool hasDoubleNullTerminator)
    {
        ArgumentNullException.ThrowIfNull(strings);

        Strings = strings;
        HasDoubleNullTerminator = hasDoubleNullTerminator;
    }

    public IReadOnlyList<string> Strings { get; }

    public bool HasDoubleNullTerminator { get; }
}

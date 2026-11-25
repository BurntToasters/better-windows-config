using System.Buffers.Binary;

namespace Bwc.Gpo.Pol;

/// <summary>
/// One Registry.pol record. Immutable. The record holds its complete serialized bytes, so a record read from a file is
/// written back exactly as it was read, whatever its type or name.
/// </summary>
public sealed class PolRecord
{
    private readonly byte[] _raw;
    private readonly int _dataOffset;
    private readonly int _dataLength;

    internal PolRecord(string key, string valueName, uint type, byte[] raw, int dataOffset, int dataLength)
    {
        Key = key;
        ValueName = valueName;
        Type = type;
        _raw = raw;
        _dataOffset = dataOffset;
        _dataLength = dataLength;
    }

    /// <summary>The key path as written in the record, without normalization.</summary>
    public string Key { get; }

    /// <summary>The value name as written in the record. <c>**</c> names are instructions.</summary>
    public string ValueName { get; }

    /// <summary>The raw registry type number.</summary>
    public uint Type { get; }

    /// <summary>The record's data bytes, including string terminators.</summary>
    public ReadOnlyMemory<byte> Data => new(_raw, _dataOffset, _dataLength);

    /// <summary>The complete serialized record, from <c>[</c> to <c>]</c>.</summary>
    public ReadOnlyMemory<byte> RawBytes => _raw;

    /// <summary>True when the value name starts with <c>**</c>.</summary>
    public bool IsInstruction => ValueName.StartsWith(PolFormat.InstructionPrefix, StringComparison.Ordinal);

    internal static PolRecord Create(string key, string valueName, uint type, ReadOnlySpan<byte> data)
    {
        var dataOffset = 2 + (2 * (key.Length + 1)) + 2 + (2 * (valueName.Length + 1)) + 2 + 4 + 2 + 4 + 2;
        var raw = new byte[checked(dataOffset + data.Length + 2)];
        var pos = 0;
        PutChar(raw, ref pos, PolFormat.RecordStart);
        PutText(raw, ref pos, key);
        PutChar(raw, ref pos, PolFormat.FieldSeparator);
        PutText(raw, ref pos, valueName);
        PutChar(raw, ref pos, PolFormat.FieldSeparator);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(pos), type);
        pos += 4;
        PutChar(raw, ref pos, PolFormat.FieldSeparator);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(pos), (uint)data.Length);
        pos += 4;
        PutChar(raw, ref pos, PolFormat.FieldSeparator);
        data.CopyTo(raw.AsSpan(pos));
        pos += data.Length;
        PutChar(raw, ref pos, PolFormat.RecordEnd);
        return new PolRecord(key, valueName, type, raw, dataOffset, data.Length);
    }

    private static void PutChar(byte[] buffer, ref int pos, char c)
    {
        buffer[pos++] = (byte)(c & 0xFF);
        buffer[pos++] = (byte)(c >> 8);
    }

    private static void PutText(byte[] buffer, ref int pos, string text)
    {
        foreach (var c in text)
        {
            PutChar(buffer, ref pos, c);
        }

        PutChar(buffer, ref pos, '\0');
    }
}

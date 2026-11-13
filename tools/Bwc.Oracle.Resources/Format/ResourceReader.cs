using System.Buffers.Binary;
using System.Text;
using Bwc.Oracle.Resources.Model;

namespace Bwc.Oracle.Resources.Format;

/// Thrown when a resource blob does not match its documented layout (truncated, out of range, unterminated).
internal sealed class ResourceFormatException : Exception
{
    public ResourceFormatException(string message) : base(message)
    {
    }
}

/// Bounds-checked little-endian reader over an in-memory copy of one resource.
/// Every read either lies fully inside the buffer or throws ResourceFormatException.
internal ref struct ResourceReader
{
    private readonly ReadOnlySpan<byte> _data;

    public ResourceReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        Position = 0;
    }

    public int Position { get; private set; }

    public int Remaining => _data.Length - Position;

    public ResourceFormatException Fail(string message) =>
        new($"{message} (offset 0x{Position:X}, resource size 0x{_data.Length:X})");

    public byte ReadByte()
    {
        Require(1);
        return _data[Position++];
    }

    public ushort ReadUInt16()
    {
        Require(2);
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(Position, 2));
        Position += 2;
        return value;
    }

    public short ReadInt16() => unchecked((short)ReadUInt16());

    public uint ReadUInt32()
    {
        Require(4);
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(Position, 4));
        Position += 4;
        return value;
    }

    /// Reads a WORD that is `ahead` bytes after the current position without consuming anything.
    public ushort PeekUInt16(int ahead)
    {
        if (ahead < 0 || Remaining - ahead < 2)
        {
            throw Fail($"truncated: cannot read a WORD {ahead} byte(s) ahead");
        }
        return BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(Position + ahead, 2));
    }

    public void Skip(int count)
    {
        Require(count);
        Position += count;
    }

    /// Moves to an absolute offset from the start of the resource (may equal the length).
    public void Seek(int absoluteOffset)
    {
        if (absoluteOffset < 0 || absoluteOffset > _data.Length)
        {
            throw Fail($"seek target 0x{absoluteOffset:X} is outside the resource");
        }
        Position = absoluteOffset;
    }

    /// Rounds up to a DWORD boundary relative to the resource start. A missing final pad is tolerated.
    public void AlignToDword()
    {
        Position = Math.Min((Position + 3) & ~3, _data.Length);
    }

    /// Reads a NUL-terminated UTF-16 string. The terminator is consumed and not returned.
    public string ReadWideString(int maxChars = 4096)
    {
        int start = Position;
        int chars = 0;
        while (true)
        {
            if (Remaining < 2)
            {
                throw Fail("unterminated UTF-16 string");
            }
            if (chars >= maxChars)
            {
                throw Fail($"string is longer than {maxChars} characters");
            }
            ushort unit = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(Position, 2));
            Position += 2;
            if (unit == 0)
            {
                break;
            }
            chars++;
        }
        return Encoding.Unicode.GetString(_data.Slice(start, chars * 2));
    }

    /// Reads exactly `count` UTF-16 code units (no terminator).
    public string ReadWideChars(int count)
    {
        int bytes = count * 2;
        Require(bytes);
        string text = Encoding.Unicode.GetString(_data.Slice(Position, bytes));
        Position += bytes;
        return text;
    }

    /// Reads the DLGTEMPLATE/MENU style "sz_Or_Ord": 0 means none, 0xFFFF means an ordinal WORD follows, else a string.
    public object? ReadSzOrOrd()
    {
        ushort first = PeekUInt16(0);
        if (first == 0x0000)
        {
            Position += 2;
            return null;
        }
        if (first == 0xFFFF)
        {
            Skip(2);
            return new OrdinalRef(ReadUInt16(), null);
        }
        return ReadWideString();
    }

    private void Require(int count)
    {
        if (count < 0 || count > Remaining)
        {
            throw Fail($"truncated: needs {count} byte(s) but {Remaining} remain");
        }
    }
}

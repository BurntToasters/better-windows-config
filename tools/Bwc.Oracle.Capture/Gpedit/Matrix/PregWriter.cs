using System.Text;

namespace Bwc.Oracle.Capture.Gpedit.Matrix;

// A tiny Registry.pol writer for the crafted start files of matrix groups D, C and F. It writes exactly what it is told, in the order it is
// told, so a case can build duplicates, out-of-order records and wrong types that classic itself would never write. BWC-authored data only.
internal static class PregWriter
{
    internal const uint RegSz = 1;
    internal const uint RegExpandSz = 2;
    internal const uint RegBinary = 3;
    internal const uint RegDword = 4;
    internal const uint RegMultiSz = 7;
    internal const uint RegQword = 11;

    internal static readonly byte[] Header = { 0x50, 0x52, 0x65, 0x67, 0x01, 0x00, 0x00, 0x00 };

    // The size field is written as the data length unless SizeOverride is given (a record with a wrong size).
    internal sealed record Rec(string Key, string Value, uint Type, byte[] Data, uint? SizeOverride = null);

    internal static byte[] Build(IEnumerable<Rec> records)
    {
        using var stream = new MemoryStream();
        stream.Write(Header);
        foreach (Rec record in records)
        {
            stream.Write(Record(record));
        }

        return stream.ToArray();
    }

    internal static byte[] Record(Rec record)
    {
        using var stream = new MemoryStream();
        void Char(char c) => stream.Write(Encoding.Unicode.GetBytes(c.ToString()));
        void Text(string text)
        {
            stream.Write(Encoding.Unicode.GetBytes(text));
            stream.Write(new byte[] { 0, 0 });
        }

        void Number(uint value) => stream.Write(BitConverter.GetBytes(value));
        Char('[');
        Text(record.Key);
        Char(';');
        Text(record.Value);
        Char(';');
        Number(record.Type);
        Char(';');
        Number(record.SizeOverride ?? (uint)record.Data.Length);
        Char(';');
        stream.Write(record.Data);
        Char(']');
        return stream.ToArray();
    }

    internal static Rec Dword(string key, string value, uint number) => new(key, value, RegDword, BitConverter.GetBytes(number));

    internal static Rec Qword(string key, string value, ulong number) => new(key, value, RegQword, BitConverter.GetBytes(number));

    internal static Rec Sz(string key, string value, string text) => new(key, value, RegSz, StringData(text));

    internal static Rec ExpandSz(string key, string value, string text) => new(key, value, RegExpandSz, StringData(text));

    internal static Rec Binary(string key, string value, params byte[] data) => new(key, value, RegBinary, data);

    internal static Rec MultiSz(string key, string value, params string[] lines)
    {
        using var stream = new MemoryStream();
        foreach (string line in lines)
        {
            stream.Write(StringData(line));
        }

        stream.Write(new byte[] { 0, 0 });
        return new Rec(key, value, RegMultiSz, stream.ToArray());
    }

    // The delete record classic writes: value name `**del.<name>`, REG_SZ, size 4, data 20 00 00 00.
    internal static Rec Del(string key, string name) => new(key, "**del." + name, RegSz, new byte[] { 0x20, 0x00, 0x00, 0x00 });

    // Any other instruction record with the same data shape as Del (for `**delvals.`, `**soft.<name>`, unknown instructions).
    internal static Rec Instruction(string key, string valueName) => new(key, valueName, RegSz, new byte[] { 0x20, 0x00, 0x00, 0x00 });

    internal static byte[] StringData(string text)
    {
        byte[] chars = Encoding.Unicode.GetBytes(text);
        byte[] data = new byte[chars.Length + 2];
        chars.CopyTo(data, 0);
        return data;
    }

    internal static byte[] HeaderOnly() => (byte[])Header.Clone();

    // The first n bytes of a good file: a truncated record.
    internal static byte[] Truncate(byte[] bytes, int length) => bytes[..Math.Min(length, bytes.Length)];

    internal static byte[] WithVersion(uint version)
    {
        byte[] bytes = HeaderOnly();
        BitConverter.GetBytes(version).CopyTo(bytes, 4);
        return bytes;
    }
}

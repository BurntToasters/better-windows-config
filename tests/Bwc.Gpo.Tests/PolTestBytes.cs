namespace Bwc.Gpo.Tests;

/// <summary>Hand-built Registry.pol bytes for the Pol tests. BWC-authored, no captured data.</summary>
internal static class PolTestBytes
{
    public static readonly byte[] Header = [0x50, 0x52, 0x65, 0x67, 0x01, 0x00, 0x00, 0x00];

    public static byte[] Utf16(string text)
    {
        var bytes = new byte[text.Length * 2];
        for (var i = 0; i < text.Length; i++)
        {
            bytes[i * 2] = (byte)(text[i] & 0xFF);
            bytes[(i * 2) + 1] = (byte)(text[i] >> 8);
        }

        return bytes;
    }

    public static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    public static byte[] U32(uint value) => BitConverter.GetBytes(value);

    public static byte[] Ch(char c) => Utf16(c.ToString());

    public static byte[] Nul() => [0, 0];

    /// <summary>A string value's data: UTF-16LE text plus a terminating NUL.</summary>
    public static byte[] SzData(string text) => Cat(Utf16(text), Nul());

    /// <summary>One record built from its parts with explicit size, for corrupting individual fields.</summary>
    public static byte[] RecordWithSize(string key, string name, uint type, uint size, byte[] data) =>
        Cat(
            Ch('['), Utf16(key), Nul(), Ch(';'), Utf16(name), Nul(), Ch(';'),
            U32(type), Ch(';'), U32(size), Ch(';'), data, Ch(']'));

    public static byte[] Record(string key, string name, uint type, byte[] data) =>
        RecordWithSize(key, name, type, (uint)data.Length, data);

    public static byte[] SzRecord(string key, string name, string value) => Record(key, name, 1, SzData(value));

    public static byte[] DwordRecord(string key, string name, uint value) => Record(key, name, 4, U32(value));

    public static byte[] DeleteRecord(string key, string name) =>
        Record(key, "**del." + name, 1, [0x20, 0x00, 0x00, 0x00]);

    public static byte[] File(params byte[][] records) => Cat([Header, .. records]);

    public static Bwc.Gpo.Pol.PolFormatException Fail(byte[] bytes)
    {
        try
        {
            Bwc.Gpo.Pol.PolDocument.Parse(bytes);
        }
        catch (Bwc.Gpo.Pol.PolFormatException ex)
        {
            return ex;
        }

        throw new Microsoft.VisualStudio.TestTools.UnitTesting.AssertFailedException("Parse succeeded but a failure was expected.");
    }
}

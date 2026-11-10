using System.Buffers.Binary;
using System.Text;

namespace Bwc.Oracle.Capture;

internal static class RegType
{
    internal const uint None = 0;
    internal const uint Sz = 1;
    internal const uint ExpandSz = 2;
    internal const uint Binary = 3;
    internal const uint Dword = 4;
    internal const uint DwordBigEndian = 5;
    internal const uint Link = 6;
    internal const uint MultiSz = 7;
    internal const uint ResourceList = 8;
    internal const uint FullResourceDescriptor = 9;
    internal const uint ResourceRequirementsList = 10;
    internal const uint Qword = 11;

    internal static string Name(uint type) => type switch
    {
        None => "REG_NONE",
        Sz => "REG_SZ",
        ExpandSz => "REG_EXPAND_SZ",
        Binary => "REG_BINARY",
        Dword => "REG_DWORD",
        DwordBigEndian => "REG_DWORD_BIG_ENDIAN",
        Link => "REG_LINK",
        MultiSz => "REG_MULTI_SZ",
        ResourceList => "REG_RESOURCE_LIST",
        FullResourceDescriptor => "REG_FULL_RESOURCE_DESCRIPTOR",
        ResourceRequirementsList => "REG_RESOURCE_REQUIREMENTS_LIST",
        Qword => "REG_QWORD",
        _ => $"0x{type:X}",
    };
}

// Exact: write with NtSetValueKey so the stored bytes are exactly Data (RegSetValueExW would add a NUL).
internal sealed record SeedValue(string Name, uint Type, byte[] Data, bool Exact = false);

// relative is the key path under HKCU\Software\BWC-Oracle. "" is the root itself.
internal sealed record SeedKey(string Relative, IReadOnlyList<SeedValue> Values);

// Deterministic seed content. Every byte is spelled out here so that the golden
// exports can be regenerated and compared bit-for-bit.
internal static class SeedSpec
{
    internal const string DefaultValueName = "";

    internal static IReadOnlyList<SeedKey> Keys { get; } = Build();

    internal static byte[] Sz(string s) => Encoding.Unicode.GetBytes(s + "\0");

    internal static byte[] SzNoNul(string s) => Encoding.Unicode.GetBytes(s);

    internal static byte[] MultiSz(params string[] items)
    {
        if (items.Length == 0)
        {
            return Encoding.Unicode.GetBytes("\0");
        }

        var sb = new StringBuilder();
        foreach (string item in items)
        {
            sb.Append(item).Append('\0');
        }

        sb.Append('\0');
        return Encoding.Unicode.GetBytes(sb.ToString());
    }

    internal static byte[] Dword(uint v)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    internal static byte[] Qword(ulong v)
    {
        byte[] b = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, v);
        return b;
    }

    internal static byte[] Pattern(int length, int seed)
    {
        byte[] b = new byte[length];
        for (int i = 0; i < length; i++)
        {
            b[i] = (byte)(i * 7 + seed);
        }

        return b;
    }

    private static IReadOnlyList<SeedKey> Build()
    {
        var keys = new List<SeedKey>();

        keys.Add(new SeedKey("Default", new[]
        {
            new SeedValue(DefaultValueName, RegType.Sz, Sz("Default value set")),
            new SeedValue("Other", RegType.Sz, Sz("non-default sibling")),
        }));

        keys.Add(new SeedKey("NoDefault", new[]
        {
            new SeedValue("Other", RegType.Sz, Sz("no default value on this key")),
        }));

        keys.Add(new SeedKey("Strings", new[]
        {
            new SeedValue("Empty", RegType.Sz, Sz("")),
            new SeedValue("EmptyNoNul", RegType.Sz, Array.Empty<byte>()),
            new SeedValue("Ascii", RegType.Sz, Sz("Hello World 123")),
            new SeedValue("Quotes", RegType.Sz, Sz("He said \"hi\" and left")),
            new SeedValue("Backslashes", RegType.Sz, Sz("C:\\Path\\To\\File")),
            new SeedValue("Unicode", RegType.Sz, Sz("Ünïcødé ✓ 日本")),
            new SeedValue("ExpandSz", RegType.ExpandSz, Sz("%SystemRoot%\\x")),
            new SeedValue("NoNul", RegType.Sz, SzNoNul("no terminator"), Exact: true),
            new SeedValue("EmbeddedNul", RegType.Sz, Encoding.Unicode.GetBytes("abc\0def\0")),
            new SeedValue("Long5000", RegType.Sz, Sz(LongText(5000))),
        }));

        keys.Add(new SeedKey("Numbers", new[]
        {
            new SeedValue("Dword0", RegType.Dword, Dword(0)),
            new SeedValue("Dword1", RegType.Dword, Dword(1)),
            new SeedValue("DwordMax", RegType.Dword, Dword(0xFFFFFFFF)),
            new SeedValue("DwordBigEndian", RegType.DwordBigEndian, new byte[] { 0x12, 0x34, 0x56, 0x78 }),
            new SeedValue("DwordWrongLen3", RegType.Dword, new byte[] { 0x01, 0x02, 0x03 }),
            new SeedValue("Qword0", RegType.Qword, Qword(0)),
            new SeedValue("QwordMax", RegType.Qword, Qword(0xFFFFFFFFFFFFFFFF)),
        }));

        keys.Add(new SeedKey("Binary", new[]
        {
            new SeedValue("Empty", RegType.Binary, Array.Empty<byte>()),
            new SeedValue("OneByte", RegType.Binary, new byte[] { 0xAB }),
            new SeedValue("Wrap300", RegType.Binary, Pattern(300, 13)),
            new SeedValue("NoneEmpty", RegType.None, Array.Empty<byte>()),
            new SeedValue("NoneData", RegType.None, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }),
        }));

        keys.Add(new SeedKey("MultiSz", new[]
        {
            new SeedValue("Several", RegType.MultiSz, MultiSz("alpha", "beta", "gamma")),
            new SeedValue("EmptyList", RegType.MultiSz, MultiSz()),
            new SeedValue("WithEmpty", RegType.MultiSz, MultiSz("one", "", "three")),
        }));

        keys.Add(new SeedKey("OddTypes", new[]
        {
            new SeedValue("Link6", RegType.Link, Encoding.Unicode.GetBytes("\\Registry\\Machine\\Software\0")),
            new SeedValue("Resource8", RegType.ResourceList, Pattern(16, 1)),
            new SeedValue("Full9", RegType.FullResourceDescriptor, Pattern(24, 2)),
            new SeedValue("Req10", RegType.ResourceRequirementsList, Pattern(16, 3)),
            new SeedValue("Custom12345", 0x12345, new byte[] { 0xCA, 0xFE, 0xBA, 0xBE }),
        }));

        keys.Add(new SeedKey("ValueNames", new[]
        {
            new SeedValue("a=b", RegType.Sz, Sz("equals in name")),
            new SeedValue("=", RegType.Sz, Sz("bare equals")),
            new SeedValue("quote\"name", RegType.Sz, Sz("quoted name")),
            new SeedValue("back\\slash", RegType.Sz, Sz("backslash name")),
            new SeedValue("@", RegType.Sz, Sz("at sign name")),
            new SeedValue("[bracket]", RegType.Sz, Sz("bracket name")),
            new SeedValue("Space name", RegType.Sz, Sz("with space")),
            new SeedValue("dots.in.name", RegType.Sz, Sz("dots")),
            new SeedValue("Ünïcødé name ✓", RegType.Dword, Dword(42)),
            new SeedValue(LongName(200), RegType.Sz, Sz("name is 200 chars")),
        }));

        keys.Add(new SeedKey("Key With Spaces", new[]
        {
            new SeedValue("Name", RegType.Sz, Sz("key name has spaces")),
        }));

        keys.Add(new SeedKey("Ünïcødé Key ✓", new[]
        {
            new SeedValue("Name", RegType.Sz, Sz("key name is unicode")),
        }));

        keys.Add(new SeedKey("dotted.key.name", new[]
        {
            new SeedValue("Name", RegType.Sz, Sz("key name has dots")),
        }));

        keys.Add(new SeedKey("Nested", new[]
        {
            new SeedValue("Depth", RegType.Dword, Dword(0)),
        }));
        keys.Add(new SeedKey("Nested\\Level1", new[]
        {
            new SeedValue("Depth", RegType.Dword, Dword(1)),
        }));
        keys.Add(new SeedKey("Nested\\Level1\\Level2", new[]
        {
            new SeedValue("Depth", RegType.Dword, Dword(2)),
        }));
        keys.Add(new SeedKey("Nested\\Level1\\Level2\\Level3", new[]
        {
            new SeedValue("Depth", RegType.Dword, Dword(3)),
            new SeedValue("Path", RegType.Sz, Sz("Nested\\Level1\\Level2\\Level3")),
        }));

        var many = new List<SeedValue>(300);
        for (int i = 1; i <= 300; i++)
        {
            many.Add(new SeedValue($"V{i:D3}", RegType.Dword, Dword((uint)i)));
        }

        keys.Add(new SeedKey("Many300", many));

        keys.Add(new SeedKey("Mixed", new[]
        {
            new SeedValue("Text", RegType.Sz, Sz("mixed text")),
            new SeedValue("Expand", RegType.ExpandSz, Sz("%SystemRoot%\\mixed")),
            new SeedValue("Number", RegType.Dword, Dword(0x1234)),
            new SeedValue("Big", RegType.Qword, Qword(0x0102030405060708)),
            new SeedValue("Blob", RegType.Binary, new byte[] { 0x01, 0x02, 0x03 }),
            new SeedValue("List", RegType.MultiSz, MultiSz("a", "b")),
            new SeedValue("Raw", RegType.None, Array.Empty<byte>()),
        }));

        return keys;
    }

    private static string LongText(int chars)
    {
        var sb = new StringBuilder(chars + 16);
        for (int i = 0; sb.Length < chars; i++)
        {
            sb.Append($"chunk{i:D4}-");
        }

        return sb.ToString(0, chars);
    }

    private static string LongName(int chars)
    {
        var sb = new StringBuilder(chars + 10);
        while (sb.Length < chars)
        {
            sb.Append("0123456789");
        }

        return sb.ToString(0, chars);
    }
}

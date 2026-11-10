using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Registry.Tests;

[TestClass]
public sealed class RegFileWriterTests
{
    [TestMethod]
    public void WriteV5EmitsBomHeaderCrlfAndValues()
    {
        var document = new RegFileDocument(
            RegFileFormat.V5,
            new[]
            {
                new RegFileKey(
                    @"HKEY_CURRENT_USER\Software\Bwc\Test",
                    new[]
                    {
                        new RegFileValue(string.Empty, 1, Encoding.Unicode.GetBytes("hello\0")),
                        new RegFileValue("Number", 4, new byte[] { 0x01, 0x00, 0x00, 0x00 }),
                    }),
            });

        using var stream = new MemoryStream();
        RegFileWriter.WriteV5(document, stream);
        byte[] bytes = stream.ToArray();

        Assert.IsTrue(bytes.Length > 2);
        Assert.AreEqual(0xFF, bytes[0]);
        Assert.AreEqual(0xFE, bytes[1]);

        string text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        Assert.AreEqual(
            "Windows Registry Editor Version 5.00\r\n\r\n"
            + "[HKEY_CURRENT_USER\\Software\\Bwc\\Test]\r\n"
            + "@=\"hello\"\r\n"
            + "\"Number\"=dword:00000001\r\n\r\n",
            text);
    }

    [TestMethod]
    public void WriteRegedit4UsesAnsiWithoutBom()
    {
        var document = new RegFileDocument(
            RegFileFormat.Regedit4,
            new[]
            {
                new RegFileKey(
                    @"HKEY_CURRENT_USER\Software\Bwc\Ünïcødé Key ✓",
                    new[]
                    {
                        new RegFileValue("Unicode", 1, Encoding.Unicode.GetBytes("Ünïcødé ✓ 日本\0")),
                        new RegFileValue("Expand", 2, Encoding.Unicode.GetBytes("%SystemRoot%\\x\0")),
                    }),
            });

        using var stream = new MemoryStream();
        RegFileWriter.WriteRegedit4(document, stream);
        byte[] bytes = stream.ToArray();

        Assert.AreNotEqual(0xFF, bytes[0]);
        Assert.AreEqual((byte)'R', bytes[0]);

        string text = AnsiEncoding.GetString(bytes);
        Assert.AreEqual(
            "REGEDIT4\r\n\r\n"
            + "[HKEY_CURRENT_USER\\Software\\Bwc\\Ünïcødé Key ?]\r\n"
            + "\"Unicode\"=\"Ünïcødé ? ??\"\r\n"
            + "\"Expand\"=hex(2):25,53,79,73,74,65,6d,52,6f,6f,74,25,5c,78,00\r\n\r\n",
            text);
    }

    [TestMethod]
    public void WriteEmitsDeleteLinesEmptyBinaryAndHexTypes()
    {
        var document = new RegFileDocument(
            RegFileFormat.V5,
            new[]
            {
                new RegFileKey(
                    @"HKEY_CURRENT_USER\Software\Bwc\Gone",
                    Array.Empty<RegFileValue>(),
                    delete: true),
                new RegFileKey(
                    @"HKEY_CURRENT_USER\Software\Bwc\Test",
                    new[]
                    {
                        new RegFileValue(string.Empty, 0, Array.Empty<byte>(), delete: true),
                        new RegFileValue("Gone", 0, Array.Empty<byte>(), delete: true),
                        new RegFileValue("Empty", 3, Array.Empty<byte>()),
                        new RegFileValue("Three", 4, new byte[] { 0x01, 0x02, 0x03 }),
                        new RegFileValue("Big", 11, new byte[] { 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01 }),
                        new RegFileValue("Custom", 0x12345, new byte[] { 0xCA, 0xFE, 0xBA, 0xBE }),
                    }),
            });

        string text = WriteV5Text(document);

        Assert.AreEqual(
            "Windows Registry Editor Version 5.00\r\n\r\n"
            + "[-HKEY_CURRENT_USER\\Software\\Bwc\\Gone]\r\n\r\n"
            + "[HKEY_CURRENT_USER\\Software\\Bwc\\Test]\r\n"
            + "@=-\r\n"
            + "\"Gone\"=-\r\n"
            + "\"Empty\"=hex:\r\n"
            + "\"Three\"=hex(4):01,02,03\r\n"
            + "\"Big\"=hex(b):08,07,06,05,04,03,02,01\r\n"
            + "\"Custom\"=hex(12345):ca,fe,ba,be\r\n\r\n",
            text);
    }

    [TestMethod]
    public void WriteWrapsHexDataAtEightyColumns()
    {
        byte[] data = Enumerable.Range(0, 100).Select(value => (byte)value).ToArray();
        var document = new RegFileDocument(
            RegFileFormat.V5,
            new[]
            {
                new RegFileKey(
                    @"HKEY_CURRENT_USER\Software\Bwc\Test",
                    new[] { new RegFileValue("Data", 3, data) }),
            });

        using var stream = new MemoryStream();
        RegFileWriter.Write(document, stream);
        byte[] bytes = stream.ToArray();

        string text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        string[] lines = text.Split("\r\n");
        string[] dataLines = lines
            .SkipWhile(line => !line.StartsWith("\"Data\"=hex:", StringComparison.Ordinal))
            .TakeWhile(line => line.Length > 0)
            .ToArray();

        Assert.IsTrue(dataLines.Length > 1);
        Assert.IsTrue(dataLines[0].StartsWith("\"Data\"=hex:", StringComparison.Ordinal));
        Assert.IsTrue(dataLines[0].EndsWith(",\\", StringComparison.Ordinal));
        Assert.IsFalse(dataLines[^1].EndsWith("\\", StringComparison.Ordinal));

        foreach (string line in dataLines)
        {
            Assert.IsTrue(line.Length <= 80, line);
        }

        for (int index = 1; index < dataLines.Length; index += 1)
        {
            Assert.IsTrue(dataLines[index].StartsWith("  ", StringComparison.Ordinal), dataLines[index]);
        }

        using var input = new MemoryStream(bytes);
        RegFileDocument parsed = RegFileReader.Parse(input);
        CollectionAssert.AreEqual(data, parsed.Keys[0].Values[0].Data);
    }

    [TestMethod]
    public void WritePreservesDocumentOrderAndCase()
    {
        var document = new RegFileDocument(
            RegFileFormat.V5,
            new[]
            {
                new RegFileKey(
                    @"hkey_current_user\software\Bwc\MiXeD",
                    new[]
                    {
                        new RegFileValue("Name", 1, Encoding.Unicode.GetBytes("value\0")),
                    }),
                new RegFileKey(
                    @"HKEY_CURRENT_USER\Software\Bwc\First",
                    Array.Empty<RegFileValue>()),
            });

        string text = WriteV5Text(document);

        Assert.IsTrue(
            text.IndexOf(@"hkey_current_user\software\Bwc\MiXeD", StringComparison.Ordinal)
            < text.IndexOf(@"HKEY_CURRENT_USER\Software\Bwc\First", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("\"Name\"=\"value\"", StringComparison.Ordinal));
    }

    private static string WriteV5Text(RegFileDocument document)
    {
        using var stream = new MemoryStream();
        RegFileWriter.Write(document, stream);
        byte[] bytes = stream.ToArray();

        return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
    }

    private static Encoding AnsiEncoding
    {
        get
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(0);
        }
    }
}

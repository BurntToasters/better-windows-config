using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Registry.Tests;

[TestClass]
public sealed class RegFileReaderTests
{
    private const string V5Header = "Windows Registry Editor Version 5.00\r\n\r\n";
    private const string Regedit4Header = "REGEDIT4\r\n\r\n";

    [TestMethod]
    public void ParseV5GoldenPreservesKeyStructure()
    {
        RegFileDocument document = ParseGolden("bwc-oracle-v5.reg");

        Assert.AreEqual(RegFileFormat.V5, document.Format);
        Assert.AreEqual(18, document.Keys.Count);
        Assert.AreEqual(358, document.Keys.Sum(key => key.Values.Count));
        Assert.AreEqual(@"HKEY_CURRENT_USER\Software\BWC-Oracle", document.Keys[0].Path);
        Assert.AreEqual(0, document.Keys[0].Values.Count);
        Assert.AreEqual(
            @"HKEY_CURRENT_USER\Software\BWC-Oracle\Ünïcødé Key ✓",
            document.Keys[^1].Path);
    }

    [TestMethod]
    public void ParseV5GoldenPreservesRawTypesAndBytes()
    {
        RegFileDocument document = ParseGolden("bwc-oracle-v5.reg");

        RegFileKey binary = FindKey(document, @"HKEY_CURRENT_USER\Software\BWC-Oracle\Binary");
        RegFileValue emptyBinary = FindValue(binary, "Empty");
        Assert.AreEqual(3u, emptyBinary.Type);
        Assert.AreEqual(0, emptyBinary.Data.Length);

        RegFileValue wrap300 = FindValue(binary, "Wrap300");
        Assert.AreEqual(3u, wrap300.Type);
        Assert.AreEqual(300, wrap300.Data.Length);
        Assert.AreEqual(0x0D, wrap300.Data[0]);
        Assert.AreEqual(0x3A, wrap300.Data[^1]);

        RegFileValue noneData = FindValue(binary, "NoneData");
        Assert.AreEqual(0u, noneData.Type);
        CollectionAssert.AreEqual(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, noneData.Data);

        RegFileKey numbers = FindKey(document, @"HKEY_CURRENT_USER\Software\BWC-Oracle\Numbers");
        RegFileValue dwordMax = FindValue(numbers, "DwordMax");
        Assert.AreEqual(4u, dwordMax.Type);
        CollectionAssert.AreEqual(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, dwordMax.Data);

        RegFileValue dwordWrongLength = FindValue(numbers, "DwordWrongLen3");
        Assert.AreEqual(4u, dwordWrongLength.Type);
        CollectionAssert.AreEqual(new byte[] { 0x01, 0x02, 0x03 }, dwordWrongLength.Data);

        RegFileValue dwordBigEndian = FindValue(numbers, "DwordBigEndian");
        Assert.AreEqual(5u, dwordBigEndian.Type);
        CollectionAssert.AreEqual(new byte[] { 0x12, 0x34, 0x56, 0x78 }, dwordBigEndian.Data);

        RegFileValue qwordMax = FindValue(numbers, "QwordMax");
        Assert.AreEqual(11u, qwordMax.Type);
        CollectionAssert.AreEqual(new byte[8] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, qwordMax.Data);

        RegFileKey multiString = FindKey(document, @"HKEY_CURRENT_USER\Software\BWC-Oracle\MultiSz");
        RegFileValue several = FindValue(multiString, "Several");
        Assert.AreEqual(7u, several.Type);
        CollectionAssert.AreEqual(
            Encoding.Unicode.GetBytes("alpha\0beta\0gamma\0\0"),
            several.Data);

        RegFileKey strings = FindKey(document, @"HKEY_CURRENT_USER\Software\BWC-Oracle\Strings");
        RegFileValue quotes = FindValue(strings, "Quotes");
        Assert.AreEqual(1u, quotes.Type);
        CollectionAssert.AreEqual(
            Encoding.Unicode.GetBytes("He said \"hi\" and left\0"),
            quotes.Data);

        RegFileValue backslashes = FindValue(strings, "Backslashes");
        CollectionAssert.AreEqual(
            Encoding.Unicode.GetBytes("C:\\Path\\To\\File\0"),
            backslashes.Data);

        RegFileValue embeddedNul = FindValue(strings, "EmbeddedNul");
        CollectionAssert.AreEqual(Encoding.Unicode.GetBytes("abc\0"), embeddedNul.Data);

        RegFileValue emptyNoNul = FindValue(strings, "EmptyNoNul");
        CollectionAssert.AreEqual(Encoding.Unicode.GetBytes("\0"), emptyNoNul.Data);

        RegFileKey defaultKey = FindKey(document, @"HKEY_CURRENT_USER\Software\BWC-Oracle\Default");
        RegFileValue defaultValue = FindValue(defaultKey, string.Empty);
        Assert.AreEqual(1u, defaultValue.Type);
        CollectionAssert.AreEqual(
            Encoding.Unicode.GetBytes("Default value set\0"),
            defaultValue.Data);

        RegFileKey valueNames = FindKey(document, @"HKEY_CURRENT_USER\Software\BWC-Oracle\ValueNames");
        CollectionAssert.AreEqual(
            new[]
            {
                "a=b",
                "=",
                "quote\"name",
                "back\\slash",
                "@",
                "[bracket]",
                "Space name",
                "dots.in.name",
                "Ünïcødé name ✓",
                string.Concat(Enumerable.Repeat("0123456789", 20)),
            },
            valueNames.Values.Select(value => value.Name).ToArray());

        RegFileValue unicodeName = FindValue(valueNames, "Ünïcødé name ✓");
        Assert.AreEqual(4u, unicodeName.Type);
        CollectionAssert.AreEqual(RegistryValueCodec.EncodeDword(0x2A), unicodeName.Data);
    }

    [TestMethod]
    public void ParseMany300GoldenPreservesEveryDword()
    {
        RegFileDocument document = ParseGolden("bwc-oracle-many300-v5.reg");

        Assert.AreEqual(RegFileFormat.V5, document.Format);
        Assert.AreEqual(1, document.Keys.Count);
        Assert.AreEqual(@"HKEY_CURRENT_USER\Software\BWC-Oracle\Many300", document.Keys[0].Path);
        Assert.AreEqual(300, document.Keys[0].Values.Count);

        RegFileValue first = document.Keys[0].Values[0];
        Assert.AreEqual("V001", first.Name);
        Assert.AreEqual(4u, first.Type);
        CollectionAssert.AreEqual(new byte[] { 0x01, 0x00, 0x00, 0x00 }, first.Data);

        RegFileValue last = document.Keys[0].Values[^1];
        Assert.AreEqual("V300", last.Name);
        CollectionAssert.AreEqual(new byte[] { 0x2C, 0x01, 0x00, 0x00 }, last.Data);
    }

    [TestMethod]
    public void ParseRegedit4GoldenConvertsAnsiStringData()
    {
        RegFileDocument document = ParseGolden("bwc-oracle-regedit4.reg");

        Assert.AreEqual(RegFileFormat.Regedit4, document.Format);
        Assert.AreEqual(18, document.Keys.Count);
        Assert.AreEqual(358, document.Keys.Sum(key => key.Values.Count));
        Assert.AreEqual(
            @"HKEY_CURRENT_USER\Software\BWC-Oracle\Ünïcødé Key ?",
            document.Keys[^1].Path);

        RegFileKey strings = FindKey(document, @"HKEY_CURRENT_USER\Software\BWC-Oracle\Strings");
        RegFileValue unicode = FindValue(strings, "Unicode");
        Assert.AreEqual(1u, unicode.Type);
        CollectionAssert.AreEqual(
            Encoding.Unicode.GetBytes("Ünïcødé ? ??\0"),
            unicode.Data);

        RegFileValue expand = FindValue(strings, "ExpandSz");
        Assert.AreEqual(2u, expand.Type);
        CollectionAssert.AreEqual(
            Encoding.Unicode.GetBytes("%SystemRoot%\\x\0"),
            expand.Data);

        RegFileKey multiString = FindKey(document, @"HKEY_CURRENT_USER\Software\BWC-Oracle\MultiSz");
        RegFileValue several = FindValue(multiString, "Several");
        CollectionAssert.AreEqual(
            Encoding.Unicode.GetBytes("alpha\0beta\0gamma\0\0"),
            several.Data);

        RegFileValue emptyList = FindValue(multiString, "EmptyList");
        CollectionAssert.AreEqual(new byte[] { 0x00, 0x00 }, emptyList.Data);

        RegFileKey mixed = FindKey(document, @"HKEY_CURRENT_USER\Software\BWC-Oracle\Mixed");
        RegFileValue list = FindValue(mixed, "List");
        CollectionAssert.AreEqual(Encoding.Unicode.GetBytes("a\0b\0\0"), list.Data);

        RegFileKey numbers = FindKey(document, @"HKEY_CURRENT_USER\Software\BWC-Oracle\Numbers");
        RegFileValue bigEndian = FindValue(numbers, "DwordBigEndian");
        Assert.AreEqual(5u, bigEndian.Type);
        CollectionAssert.AreEqual(new byte[] { 0x12, 0x34, 0x56, 0x78 }, bigEndian.Data);

        RegFileKey oddTypes = FindKey(document, @"HKEY_CURRENT_USER\Software\BWC-Oracle\OddTypes");
        RegFileValue custom = FindValue(oddTypes, "Custom12345");
        Assert.AreEqual(0x12345u, custom.Type);
        CollectionAssert.AreEqual(new byte[] { 0xCA, 0xFE, 0xBA, 0xBE }, custom.Data);
    }

    [TestMethod]
    public void V5GoldenRoundTripsByteForByte()
    {
        AssertGoldenRoundTrip("bwc-oracle-v5.reg");
    }

    [TestMethod]
    public void Many300GoldenRoundTripsByteForByte()
    {
        AssertGoldenRoundTrip("bwc-oracle-many300-v5.reg");
    }

    [TestMethod]
    public void Regedit4GoldenRoundTripsByteForByte()
    {
        AssertGoldenRoundTrip("bwc-oracle-regedit4.reg");
    }

    [TestMethod]
    public void ParseDeleteKeyAndValueLines()
    {
        RegFileDocument document = ParseV5(
            "[HKEY_CURRENT_USER\\Software\\Test]\r\n"
            + "\"Gone\"=-\r\n"
            + "@=-\r\n"
            + "\"Keep\"=dword:0000002a\r\n"
            + "\r\n"
            + "[-HKEY_CURRENT_USER\\Software\\Test\\Old]\r\n");

        Assert.AreEqual(2, document.Keys.Count);

        RegFileKey test = document.Keys[0];
        Assert.IsFalse(test.Delete);
        Assert.AreEqual(3, test.Values.Count);
        Assert.IsTrue(test.Values[0].Delete);
        Assert.AreEqual("Gone", test.Values[0].Name);
        Assert.IsTrue(test.Values[1].Delete);
        Assert.AreEqual(string.Empty, test.Values[1].Name);
        Assert.IsFalse(test.Values[2].Delete);
        CollectionAssert.AreEqual(new byte[] { 0x2A, 0x00, 0x00, 0x00 }, test.Values[2].Data);

        RegFileKey old = document.Keys[1];
        Assert.IsTrue(old.Delete);
        Assert.AreEqual(@"HKEY_CURRENT_USER\Software\Test\Old", old.Path);
        Assert.AreEqual(0, old.Values.Count);
    }

    [TestMethod]
    public void ParseAcceptsWholeLineCommentsAndBlankLines()
    {
        RegFileDocument document = ParseV5(
            "; leading comment\r\n"
            + "\r\n"
            + "[HKEY_CURRENT_USER\\Software\\Test]\r\n"
            + "   ; indented comment\r\n"
            + "\"Name\"=\"value\"\r\n");

        Assert.AreEqual(1, document.Keys.Count);
        Assert.AreEqual(1, document.Keys[0].Values.Count);
        Assert.AreEqual("Name", document.Keys[0].Values[0].Name);
    }

    [TestMethod]
    public void ParseAcceptsLfOnlyInput()
    {
        string text = "Windows Registry Editor Version 5.00\n\n"
            + "[HKEY_CURRENT_USER\\Software\\Test]\n"
            + "\"Name\"=\"value\"\n";

        RegFileDocument document = ParseBytes(WithBom(Encoding.Unicode.GetBytes(text)));

        Assert.AreEqual(1, document.Keys.Count);
        Assert.AreEqual("value", RegistryValueCodec.DecodeString(document.Keys[0].Values[0].Data));
    }

    [TestMethod]
    public void ParseFileMatchesParseFromBytes()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "bwc-regfile-" + Guid.NewGuid().ToString("N") + ".reg");

        try
        {
            File.WriteAllBytes(path, ReadGolden("bwc-oracle-many300-v5.reg"));
            RegFileDocument fromFile = RegFileReader.ParseFile(path);
            RegFileDocument fromBytes = ParseGolden("bwc-oracle-many300-v5.reg");

            Assert.AreEqual(fromBytes.Format, fromFile.Format);
            Assert.AreEqual(fromBytes.Keys.Count, fromFile.Keys.Count);
            Assert.AreEqual(fromBytes.Keys[0].Values.Count, fromFile.Keys[0].Values.Count);
            CollectionAssert.AreEqual(
                fromBytes.Keys[0].Values[^1].Data,
                fromFile.Keys[0].Values[^1].Data);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void MissingHeaderReportsLineOne()
    {
        RegFileParseException error = Assert.ThrowsExactly<RegFileParseException>(
            () => ParseBytes(Encoding.Unicode.GetBytes("Windows Registry Editor Version 4.00\r\n")));

        Assert.AreEqual(1, error.Line);
        Assert.AreEqual(1, error.Column);
        Assert.IsTrue(error.Message.Contains("header", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void BadHexDigitReportsTheValueLine()
    {
        RegFileParseException error = ParseV5ValueError("\"Value\"=hex:0g\r\n");

        Assert.AreEqual(4, error.Line);
        Assert.IsTrue(error.Column > 1);
        Assert.IsTrue(error.Message.Length > 0);
    }

    [TestMethod]
    public void OddHexDigitReportsTheValueLine()
    {
        RegFileParseException error = ParseV5ValueError("\"Value\"=hex:1\r\n");

        Assert.AreEqual(4, error.Line);
        Assert.IsTrue(error.Column > 1);
    }

    [TestMethod]
    public void MissingClosingQuoteReportsTheValueLine()
    {
        RegFileParseException error = ParseV5ValueError("\"Name\"=\"unterminated\r\n");

        Assert.AreEqual(4, error.Line);
        Assert.IsTrue(error.Column > 1);
    }

    [TestMethod]
    public void UnknownEscapeReportsTheValueLine()
    {
        RegFileParseException error = ParseV5ValueError("\"Name\"=\"bad\\q\"\r\n");

        Assert.AreEqual(4, error.Line);
        Assert.IsTrue(error.Column > 1);
    }

    [TestMethod]
    public void ContinuationAtEndOfFileReportsTheBackslash()
    {
        RegFileParseException error = ParseV5ValueError("\"Value\"=hex:00,\\");

        Assert.AreEqual(4, error.Line);
        Assert.IsTrue(error.Column > 1);
        Assert.IsTrue(error.Message.Length > 0);
    }

    [TestMethod]
    public void TrailingCommaWithoutContinuationIsRejected()
    {
        RegFileParseException error = ParseV5ValueError("\"Value\"=hex:00,\r\n");

        Assert.AreEqual(4, error.Line);
        Assert.IsTrue(error.Message.Length > 0);
    }

    [TestMethod]
    public void ValueBeforeKeySectionIsRejected()
    {
        RegFileParseException error = ParseV5Error("\"Name\"=\"value\"\r\n");

        Assert.AreEqual(3, error.Line);
        Assert.IsTrue(error.Message.Length > 0);
    }

    [TestMethod]
    public void MissingKeyBracketIsRejected()
    {
        RegFileParseException error = ParseV5Error("[HKEY_CURRENT_USER\\Software\\Test\r\n");

        Assert.AreEqual(3, error.Line);
        Assert.IsTrue(error.Column > 1);
    }

    [TestMethod]
    public void UnknownRootIsRejected()
    {
        RegFileParseException error = ParseV5Error("[NOT_A_ROOT\\Software\\Test]\r\n");

        Assert.AreEqual(3, error.Line);
        Assert.IsTrue(error.Column > 1);
    }

    [TestMethod]
    public void DwordWithWrongNumberOfDigitsIsRejected()
    {
        RegFileParseException error = ParseV5ValueError("\"Value\"=dword:123\r\n");

        Assert.AreEqual(4, error.Line);
        Assert.IsTrue(error.Column > 1);
    }

    private static void AssertGoldenRoundTrip(string fileName)
    {
        byte[] source = ReadGolden(fileName);
        RegFileDocument document;

        using (var input = new MemoryStream(source))
        {
            document = RegFileReader.Parse(input);
        }

        using var output = new MemoryStream();
        RegFileWriter.Write(document, output);
        byte[] actual = output.ToArray();

        Assert.AreEqual(
            Convert.ToHexString(SHA256.HashData(source)),
            Convert.ToHexString(SHA256.HashData(actual)),
            fileName);
        CollectionAssert.AreEqual(source, actual, fileName);
    }

    private static byte[] ReadGolden(string fileName) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "oracle", fileName));

    private static RegFileDocument ParseGolden(string fileName) => ParseBytes(ReadGolden(fileName));

    private static RegFileDocument ParseBytes(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return RegFileReader.Parse(stream);
    }

    private static RegFileDocument ParseV5(string body) =>
        ParseBytes(WithBom(Encoding.Unicode.GetBytes(V5Header + body)));

    private static RegFileParseException ParseV5Error(string body) =>
        Assert.ThrowsExactly<RegFileParseException>(() => ParseV5(body));

    private static RegFileParseException ParseV5ValueError(string valueLine) =>
        ParseV5Error("[HKEY_CURRENT_USER\\Software\\Test]\r\n" + valueLine);

    private static byte[] WithBom(byte[] text)
    {
        byte[] bytes = new byte[text.Length + 2];
        bytes[0] = 0xFF;
        bytes[1] = 0xFE;
        text.CopyTo(bytes, 2);
        return bytes;
    }

    private static RegFileKey FindKey(RegFileDocument document, string path) =>
        document.Keys.Single(key => key.Path == path);

    private static RegFileValue FindValue(RegFileKey key, string name) =>
        key.Values.Single(value => value.Name == name);
}

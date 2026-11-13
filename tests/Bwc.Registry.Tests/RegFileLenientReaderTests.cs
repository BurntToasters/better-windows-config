using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Registry.Tests;

/// <summary>
/// Tests for <see cref="RegFileReader.ParseLenient(Stream)"/>, the classic-compatible reader
/// that skips malformed lines and reports them, and refuses only whole-file problems.
/// </summary>
[TestClass]
public sealed class RegFileLenientReaderTests
{
    private const string V5Header = "Windows Registry Editor Version 5.00";
    private const string Section = "[HKEY_CURRENT_USER\\Software\\Test]";

    [TestMethod]
    public void ValueBeforeAnySectionIsSkippedAndReported()
    {
        RegFileDocument document = LenientV5(
            "\"Orphan\"=\"before\"\r\n"
            + "[HKEY_CURRENT_USER\\Software\\Test\\After]\r\n"
            + "\"Text\"=\"after\"\r\n");

        RegFileProblem problem = Assert.ContainsSingle(document.Problems);
        Assert.AreEqual(3, problem.Line);
        Assert.AreEqual(1, document.Keys.Count);
        Assert.AreEqual("HKEY_CURRENT_USER\\Software\\Test\\After", document.Keys[0].Path);
        Assert.AreEqual(4, document.Keys[0].Line);
    }

    [TestMethod]
    public void UnrecognizedLineBetweenSectionsIsSkipped()
    {
        RegFileDocument document = LenientV5(
            Section + "\r\n"
            + "\"First\"=\"ok\"\r\n"
            + "this line is not a value\r\n"
            + "[HKEY_CURRENT_USER\\Software\\Test\\After]\r\n"
            + "\"Second\"=\"still here\"\r\n");

        Assert.AreEqual(5, Assert.ContainsSingle(document.Problems).Line);
        Assert.AreEqual(2, document.Keys.Count);
        Assert.AreEqual("First", document.Keys[0].Values[0].Name);
        Assert.AreEqual("Second", document.Keys[1].Values[0].Name);
    }

    [TestMethod]
    public void KeyLineWithoutCloseBracketStartsNoSectionAndItsValuesAreOrphans()
    {
        RegFileDocument document = LenientV5(
            "[HKEY_CURRENT_USER\\Software\\Test\r\n"
            + "\"Text\"=\"noclose\"\r\n");

        Assert.AreEqual(2, document.Problems.Count);
        Assert.AreEqual(3, document.Problems[0].Line);
        Assert.AreEqual(4, document.Problems[1].Line);
        Assert.AreEqual(0, document.Keys.Count);
    }

    [TestMethod]
    public void KeyLineWithoutCloseBracketDoesNotAttachValuesToThePreviousSection()
    {
        RegFileDocument document = LenientV5(
            Section + "\r\n"
            + "\"Kept\"=\"yes\"\r\n"
            + "[HKEY_CURRENT_USER\\Software\\Test\\Broken\r\n"
            + "\"Lost\"=\"no\"\r\n");

        Assert.AreEqual(1, document.Keys.Count);
        Assert.AreEqual(1, document.Keys[0].Values.Count);
        Assert.AreEqual("Kept", document.Keys[0].Values[0].Name);
        Assert.AreEqual(2, document.Problems.Count);
    }

    [TestMethod]
    public void HexValueWithBadDigitIsDroppedAndTheNextValueIsKept()
    {
        RegFileDocument document = LenientV5(
            Section + "\r\n"
            + "\"Bin\"=hex:zz\r\n"
            + "\"After\"=\"still here\"\r\n");

        RegFileKey key = Assert.ContainsSingle(document.Keys);
        RegFileValue value = Assert.ContainsSingle(key.Values);
        Assert.AreEqual("After", value.Name);
        Assert.AreEqual(4, Assert.ContainsSingle(document.Problems).Line);
    }

    [TestMethod]
    public void HexValueWithBadDigitDropsItsContinuationLinesWithoutReportingThem()
    {
        RegFileDocument document = LenientV5(
            Section + "\r\n"
            + "\"Bin\"=hex:01,zz,\\\r\n"
            + "  03,04\r\n"
            + "\"After\"=\"kept\"\r\n");

        RegFileKey key = Assert.ContainsSingle(document.Keys);
        Assert.AreEqual("After", Assert.ContainsSingle(key.Values).Name);
        Assert.AreEqual(4, Assert.ContainsSingle(document.Problems).Line);
    }

    [TestMethod]
    public void UnterminatedStringIsSkippedAndReported()
    {
        RegFileDocument document = LenientV5(
            Section + "\r\n"
            + "\"Name\"=\"unterminated\r\n"
            + "\"Good\"=\"yes\"\r\n");

        Assert.AreEqual(4, Assert.ContainsSingle(document.Problems).Line);
        Assert.AreEqual("Good", Assert.ContainsSingle(document.Keys[0].Values).Name);
    }

    [TestMethod]
    public void UnknownEscapeIsSkippedAndReported()
    {
        RegFileDocument document = LenientV5(
            Section + "\r\n"
            + "\"Name\"=\"bad\\q\"\r\n");

        Assert.AreEqual(4, Assert.ContainsSingle(document.Problems).Line);
        Assert.AreEqual(0, document.Keys[0].Values.Count);
    }

    [TestMethod]
    public void HexTrailingCommaAtEndOfLineIsAccepted()
    {
        RegFileDocument document = LenientV5(
            Section + "\r\n"
            + "\"Bin\"=hex:01,02,\r\n");

        RegFileValue value = Assert.ContainsSingle(document.Keys[0].Values);
        CollectionAssert.AreEqual(new byte[] { 0x01, 0x02 }, value.Data);
        Assert.AreEqual(0, document.Problems.Count);
    }

    [TestMethod]
    public void HexWithNoBytesIsAnEmptyBinary()
    {
        RegFileDocument document = LenientV5(
            Section + "\r\n"
            + "\"Bin\"=hex:\r\n");

        RegFileValue value = Assert.ContainsSingle(document.Keys[0].Values);
        Assert.AreEqual(3u, value.Type);
        Assert.AreEqual(0, value.Data.Length);
        Assert.AreEqual(0, document.Problems.Count);
    }

    [TestMethod]
    public void DwordWithOneDigitIsAcceptedAsHex()
    {
        RegFileDocument document = LenientV5(
            Section + "\r\n"
            + "\"N\"=dword:1\r\n");

        RegFileValue value = Assert.ContainsSingle(document.Keys[0].Values);
        Assert.AreEqual(4u, value.Type);
        CollectionAssert.AreEqual(new byte[] { 1, 0, 0, 0 }, value.Data);
        Assert.AreEqual(0, document.Problems.Count);
    }

    [TestMethod]
    public void DwordWithNoDigitsOrMoreThanEightIsSkipped()
    {
        RegFileDocument document = LenientV5(
            Section + "\r\n"
            + "\"Empty\"=dword:\r\n"
            + "\"Long\"=dword:123456789\r\n"
            + "\"Good\"=dword:0000002a\r\n");

        Assert.AreEqual(2, document.Problems.Count);
        Assert.AreEqual(4, document.Problems[0].Line);
        Assert.AreEqual(5, document.Problems[1].Line);
        Assert.AreEqual("Good", Assert.ContainsSingle(document.Keys[0].Values).Name);
    }

    [TestMethod]
    public void CommentAndBlankLinesAreNotProblems()
    {
        RegFileDocument document = LenientV5(
            "\r\n; a comment\r\n" + Section + "\r\n\"A\"=\"1\"\r\n");

        Assert.AreEqual(0, document.Problems.Count);
        Assert.AreEqual(1, document.Keys.Count);
    }

    [TestMethod]
    public void HeaderWithDotDotSegmentRefusesTheWholeFile()
    {
        RegFileParseException error = AssertRefused(
            V5Header + "\r\n\r\n"
            + "[HKEY_CURRENT_USER\\Software\\BWC-Oracle\\..\\..\\Microsoft]\r\n"
            + "\"Bad\"=\"x\"\r\n");

        Assert.AreEqual(RegFileRefusal.InvalidKeyPath, error.Refusal);
        Assert.AreEqual(3, error.Line);
    }

    [TestMethod]
    public void HeaderWithUnknownRootRefusesTheWholeFile()
    {
        RegFileParseException error = AssertRefused(
            V5Header + "\r\n\r\n"
            + "[NOT_A_ROOT\\Software\\Test]\r\n"
            + "\"A\"=\"1\"\r\n");

        Assert.AreEqual(RegFileRefusal.InvalidKeyPath, error.Refusal);
    }

    [TestMethod]
    public void LastCloseBracketOnTheLineEndsTheHeaderSoAnInnerBracketIsAPathCharacter()
    {
        // The first ']' would end the header and leave "B]" as junk. Taking the last one puts
        // ']' inside a path segment, which is refused.
        RegFileParseException error = AssertRefused(
            V5Header + "\r\n\r\n"
            + "[HKEY_CURRENT_USER\\Software\\Test\\A]B]\r\n");

        Assert.AreEqual(RegFileRefusal.InvalidKeyPath, error.Refusal);
    }

    [TestMethod]
    public void DoubleCloseBracketRefusesTheWholeFile()
    {
        RegFileParseException error = AssertRefused(
            V5Header + "\r\n\r\n"
            + "[HKEY_CURRENT_USER\\Software\\BWC-Oracle\\Import\\C12]]\r\n"
            + "\"Text\"=\"double\"\r\n");

        Assert.AreEqual(RegFileRefusal.InvalidKeyPath, error.Refusal);
    }

    [TestMethod]
    public void HeaderlessFileIsRefusedAsNotARegistryFile()
    {
        RegFileParseException error = AssertRefused(
            "[HKEY_CURRENT_USER\\Software\\Test]\r\n\"A\"=\"1\"\r\n");

        Assert.AreEqual(RegFileRefusal.NotRegistryFile, error.Refusal);
    }

    [TestMethod]
    public void EmptyInputIsRefusedAsNotARegistryFile()
    {
        RegFileParseException error = AssertRefused(string.Empty);

        Assert.AreEqual(RegFileRefusal.NotRegistryFile, error.Refusal);
    }

    [TestMethod]
    public void Utf16WithoutBomIsRefusedAsNotARegistryFile()
    {
        byte[] bytes = Encoding.Unicode.GetBytes(V5Header + "\r\n\r\n" + Section + "\r\n");

        RegFileParseException error = Assert.ThrowsExactly<RegFileParseException>(
            () => ParseBytesLenient(bytes));

        Assert.AreEqual(RegFileRefusal.NotRegistryFile, error.Refusal);
    }

    [TestMethod]
    public void Utf8ByteOrderMarkIsRemovedBeforeTheHeaderCheck()
    {
        byte[] body = new UTF8Encoding(false).GetBytes(
            V5Header + "\r\n\r\n" + Section + "\r\n\"Text\"=\"utf8 bom ü\"\r\n");
        byte[] bytes = new byte[body.Length + 3];
        bytes[0] = 0xEF;
        bytes[1] = 0xBB;
        bytes[2] = 0xBF;
        body.CopyTo(bytes, 3);

        RegFileDocument document = ParseBytesLenient(bytes);

        Assert.AreEqual(RegFileFormat.V5, document.Format);
        RegFileValue value = Assert.ContainsSingle(document.Keys[0].Values);
        Assert.AreEqual("utf8 bom ü", Encoding.Unicode.GetString(value.Data).TrimEnd('\0'));
    }

    [TestMethod]
    public void StrictParseStillStopsAtTheFirstProblem()
    {
        RegFileParseException error = Assert.ThrowsExactly<RegFileParseException>(
            () => ParseBytesStrict(V5Header + "\r\n\r\n\"Orphan\"=\"1\"\r\n"));

        Assert.AreEqual(3, error.Line);
        Assert.AreEqual(RegFileRefusal.None, error.Refusal);
    }

    [TestMethod]
    public void TextAfterTheCloseBracketSkipsTheSectionAndItsValues()
    {
        RegFileDocument document = LenientV5(
            Section + " junk\r\n"
            + "\"A\"=\"1\"\r\n");

        Assert.AreEqual(2, document.Problems.Count);
        Assert.AreEqual(3, document.Problems[0].Line);
        Assert.AreEqual(4, document.Problems[1].Line);
        Assert.AreEqual(0, document.Keys.Count);
    }

    [TestMethod]
    public void EmptyKeyPathsAreSkipped()
    {
        RegFileDocument document = LenientV5("[]\r\n[-]\r\n");

        Assert.AreEqual(2, document.Problems.Count);
        Assert.AreEqual(3, document.Problems[0].Line);
        Assert.AreEqual(4, document.Problems[1].Line);
        Assert.AreEqual(0, document.Keys.Count);
    }

    [TestMethod]
    public void EmptyPathSegmentRefusesTheWholeFile()
    {
        RegFileParseException error = AssertRefused(
            V5Header + "\r\n\r\n[HKEY_CURRENT_USER\\\\Software]\r\n\"A\"=\"1\"\r\n");

        Assert.AreEqual(RegFileRefusal.InvalidKeyPath, error.Refusal);
    }

    [TestMethod]
    public void ValueNameThatIsNotQuotedIsSkipped()
    {
        RegFileDocument document = LenientV5(Section + "\r\nName=\"x\"\r\n");

        Assert.AreEqual(4, Assert.ContainsSingle(document.Problems).Line);
        Assert.AreEqual(0, document.Keys[0].Values.Count);
    }

    [TestMethod]
    public void MissingEqualsOrJunkAfterAValueIsSkipped()
    {
        RegFileDocument document = LenientV5(
            Section + "\r\n"
            + "\"Name\" \"x\"\r\n"
            + "\"Other\"=\"x\" junk\r\n");

        Assert.AreEqual(2, document.Problems.Count);
        Assert.AreEqual(4, document.Problems[0].Line);
        Assert.AreEqual(5, document.Problems[1].Line);
        Assert.AreEqual(0, document.Keys[0].Values.Count);
    }

    [TestMethod]
    public void HexWithAnOddDigitBadTypeOrMissingColonIsSkipped()
    {
        RegFileDocument document = LenientV5(
            Section + "\r\n"
            + "\"Odd\"=hex:0\r\n"
            + "\"Type\"=hex(zz):01\r\n"
            + "\"Colon\"=hex 01\r\n");

        Assert.AreEqual(3, document.Problems.Count);
        Assert.AreEqual(4, document.Problems[0].Line);
        Assert.AreEqual(5, document.Problems[1].Line);
        Assert.AreEqual(6, document.Problems[2].Line);
        Assert.AreEqual(0, document.Keys[0].Values.Count);
    }

    [TestMethod]
    public void HexContinuationNotLastOnItsLineIsSkipped()
    {
        RegFileDocument document = LenientV5(
            Section + "\r\n"
            + "\"Bin\"=hex:01,\\ 02\r\n"
            + "\"After\"=\"kept\"\r\n");

        Assert.AreEqual(4, Assert.ContainsSingle(document.Problems).Line);
        Assert.AreEqual("After", Assert.ContainsSingle(document.Keys[0].Values).Name);
    }

    [TestMethod]
    public void HexContinuationAtEndOfFileIsSkipped()
    {
        RegFileDocument document = LenientV5(Section + "\r\n\"Bin\"=hex:01,\\");

        Assert.AreEqual(4, Assert.ContainsSingle(document.Problems).Line);
        Assert.AreEqual(0, document.Keys[0].Values.Count);
    }

    [TestMethod]
    public void BigEndianUtf16IsRefusedAsNotARegistryFile()
    {
        byte[] body = Encoding.BigEndianUnicode.GetBytes(V5Header + "\r\n\r\n" + Section + "\r\n");
        byte[] bytes = new byte[body.Length + 2];
        bytes[0] = 0xFE;
        bytes[1] = 0xFF;
        body.CopyTo(bytes, 2);

        RegFileParseException error = Assert.ThrowsExactly<RegFileParseException>(
            () => ParseBytesLenient(bytes));

        Assert.AreEqual(RegFileRefusal.NotRegistryFile, error.Refusal);
    }

    [TestMethod]
    public void UnicodeWithOddByteCountIsRefusedAsNotARegistryFile()
    {
        byte[] bytes = WithBom(Encoding.Unicode.GetBytes(V5Header + "\r\n")).Append((byte)0x41).ToArray();

        RegFileParseException error = Assert.ThrowsExactly<RegFileParseException>(
            () => ParseBytesLenient(bytes));

        Assert.AreEqual(RegFileRefusal.NotRegistryFile, error.Refusal);
    }

    private static RegFileDocument LenientV5(string body) =>
        ParseBytesLenient(WithBom(Encoding.Unicode.GetBytes(V5Header + "\r\n\r\n" + body)));

    private static RegFileParseException AssertRefused(string text)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(text);
        return Assert.ThrowsExactly<RegFileParseException>(
            () => ParseBytesLenient(WithBom(bytes)));
    }

    private static RegFileDocument ParseBytesLenient(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return RegFileReader.ParseLenient(stream);
    }

    private static RegFileDocument ParseBytesStrict(string text)
    {
        using var stream = new MemoryStream(WithBom(Encoding.Unicode.GetBytes(text)));
        return RegFileReader.Parse(stream);
    }

    private static byte[] WithBom(byte[] text)
    {
        byte[] bytes = new byte[text.Length + 2];
        bytes[0] = 0xFF;
        bytes[1] = 0xFE;
        text.CopyTo(bytes, 2);
        return bytes;
    }
}

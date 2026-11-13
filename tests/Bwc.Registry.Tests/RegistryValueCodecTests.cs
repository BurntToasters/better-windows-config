using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Registry.Tests;

[TestClass]
public sealed class RegistryValueCodecTests
{
    [TestMethod]
    public void EncodeStringWritesUtf16LittleEndianWithATerminator()
    {
        byte[] encoded = RegistryValueCodec.EncodeString("hello");

        CollectionAssert.AreEqual(Encoding.Unicode.GetBytes("hello\0"), encoded);
        Assert.AreEqual("hello", RegistryValueCodec.DecodeString(encoded));
        Assert.AreEqual("hello\0", RegistryValueCodec.DecodeStringRaw(encoded));
    }

    [TestMethod]
    public void EncodeStringOfEmptyTextIsJustTheTerminator()
    {
        byte[] encoded = RegistryValueCodec.EncodeString(string.Empty);

        CollectionAssert.AreEqual(new byte[] { 0, 0 }, encoded);
        Assert.AreEqual(string.Empty, RegistryValueCodec.DecodeString(encoded));
        Assert.AreEqual("\0", RegistryValueCodec.DecodeStringRaw(encoded));
    }

    [TestMethod]
    public void DecodeStringStopsAtTheFirstNulButRawKeepsEverything()
    {
        byte[] embedded = Encoding.Unicode.GetBytes("ab\0cd\0");
        byte[] noTerminator = Encoding.Unicode.GetBytes("abc");

        Assert.AreEqual("ab", RegistryValueCodec.DecodeString(embedded));
        Assert.AreEqual("ab\0cd\0", RegistryValueCodec.DecodeStringRaw(embedded));
        Assert.AreEqual("abc", RegistryValueCodec.DecodeString(noTerminator));
        Assert.AreEqual("abc", RegistryValueCodec.DecodeStringRaw(noTerminator));
    }

    [TestMethod]
    public void DecodeStringIgnoresAnOddTrailingByte()
    {
        byte[] data = { 0x61, 0x00, 0xFF };

        Assert.AreEqual("a", RegistryValueCodec.DecodeString(data));
        Assert.AreEqual("a", RegistryValueCodec.DecodeStringRaw(data));
    }

    [TestMethod]
    public void DwordRoundTripsLittleEndian()
    {
        byte[] encoded = RegistryValueCodec.EncodeDword(0x11223344u);

        CollectionAssert.AreEqual(new byte[] { 0x44, 0x33, 0x22, 0x11 }, encoded);
        Assert.AreEqual(0x11223344u, RegistryValueCodec.DecodeDword(encoded));
        Assert.AreEqual(0x44332211u, RegistryValueCodec.DecodeDwordBigEndian(encoded));
        Assert.IsTrue(RegistryValueCodec.IsValidDword(encoded));
    }

    [TestMethod]
    public void DwordBigEndianReadsTheOppositeByteOrder()
    {
        byte[] encoded = { 0x11, 0x22, 0x33, 0x44 };

        Assert.AreEqual(0x11223344u, RegistryValueCodec.DecodeDwordBigEndian(encoded));
    }

    [TestMethod]
    public void QwordRoundTripsLittleEndian()
    {
        const ulong Value = 0x1122334455667788UL;
        byte[] encoded = RegistryValueCodec.EncodeQword(Value);

        CollectionAssert.AreEqual(
            new byte[] { 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11 },
            encoded);
        Assert.AreEqual(Value, RegistryValueCodec.DecodeQword(encoded));
        Assert.IsTrue(RegistryValueCodec.IsValidQword(encoded));
    }

    [TestMethod]
    public void InvalidDwordAndQwordLengthsThrow()
    {
        Assert.ThrowsExactly<ArgumentException>(() => RegistryValueCodec.DecodeDword(new byte[3]));
        Assert.ThrowsExactly<ArgumentException>(() => RegistryValueCodec.DecodeDword(new byte[5]));
        Assert.ThrowsExactly<ArgumentException>(() => RegistryValueCodec.DecodeDwordBigEndian(new byte[3]));
        Assert.ThrowsExactly<ArgumentException>(() => RegistryValueCodec.DecodeQword(new byte[7]));
        Assert.ThrowsExactly<ArgumentException>(() => RegistryValueCodec.DecodeQword(new byte[9]));

        Assert.IsFalse(RegistryValueCodec.IsValidDword(new byte[3]));
        Assert.IsFalse(RegistryValueCodec.IsValidDword(new byte[5]));
        Assert.IsFalse(RegistryValueCodec.IsValidQword(new byte[7]));
        Assert.IsFalse(RegistryValueCodec.IsValidQword(new byte[9]));
    }

    [TestMethod]
    public void MultiStringRoundTripsAndPreservesEmptyStrings()
    {
        string[] values = { "one", string.Empty, "two" };

        byte[] encoded = RegistryValueCodec.EncodeMultiString(values);
        MultiStringDecodeResult decoded = RegistryValueCodec.DecodeMultiString(encoded);

        CollectionAssert.AreEqual(Encoding.Unicode.GetBytes("one\0\0two\0\0"), encoded);
        CollectionAssert.AreEqual(values, decoded.Strings.ToArray());
        Assert.IsTrue(decoded.HasDoubleNullTerminator);
    }

    [TestMethod]
    public void MultiStringWithOneTerminatorReportsNoDoubleNul()
    {
        byte[] data = Encoding.Unicode.GetBytes("one\0two\0");
        MultiStringDecodeResult decoded = RegistryValueCodec.DecodeMultiString(data);

        CollectionAssert.AreEqual(new[] { "one", "two" }, decoded.Strings.ToArray());
        Assert.IsFalse(decoded.HasDoubleNullTerminator);
    }

    [TestMethod]
    public void MultiStringWithoutTerminatorStillSplitsOnNul()
    {
        byte[] data = Encoding.Unicode.GetBytes("one\0two");
        MultiStringDecodeResult decoded = RegistryValueCodec.DecodeMultiString(data);

        CollectionAssert.AreEqual(new[] { "one", "two" }, decoded.Strings.ToArray());
        Assert.IsFalse(decoded.HasDoubleNullTerminator);
    }

    [TestMethod]
    public void MultiStringEmptyDataAndTerminatorOnlyData()
    {
        MultiStringDecodeResult empty = RegistryValueCodec.DecodeMultiString(Array.Empty<byte>());
        MultiStringDecodeResult terminatorOnly =
            RegistryValueCodec.DecodeMultiString(Encoding.Unicode.GetBytes("\0\0"));

        Assert.AreEqual(0, empty.Strings.Count);
        Assert.IsFalse(empty.HasDoubleNullTerminator);
        Assert.AreEqual(0, terminatorOnly.Strings.Count);
        Assert.IsTrue(terminatorOnly.HasDoubleNullTerminator);
    }

    [TestMethod]
    public void EncodeMultiStringOfEmptyListWritesDoubleNul()
    {
        byte[] encoded = RegistryValueCodec.EncodeMultiString(Array.Empty<string>());
        MultiStringDecodeResult decoded = RegistryValueCodec.DecodeMultiString(encoded);

        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0 }, encoded);
        Assert.AreEqual(0, decoded.Strings.Count);
        Assert.IsTrue(decoded.HasDoubleNullTerminator);
    }

    [TestMethod]
    public void MultiStringUnicodeRoundTrips()
    {
        string[] values = { "Ünïcødé ✓", string.Empty };

        byte[] encoded = RegistryValueCodec.EncodeMultiString(values);
        MultiStringDecodeResult decoded = RegistryValueCodec.DecodeMultiString(encoded);

        CollectionAssert.AreEqual(values, decoded.Strings.ToArray());
        Assert.IsTrue(decoded.HasDoubleNullTerminator);
    }
}

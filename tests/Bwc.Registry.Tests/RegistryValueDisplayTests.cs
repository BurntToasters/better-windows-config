using System.Globalization;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Registry.Tests;

[TestClass]
public sealed class RegistryValueDisplayTests
{
    [TestMethod]
    public void ZeroLengthStringValuesShowValueNotSet()
    {
        foreach (uint type in new uint[] { 1, 2, 7 })
        {
            var value = new RegistryValueInfo("Text", type, Array.Empty<byte>());

            Assert.AreEqual("(value not set)", RegistryValueDisplay.FormatData(value));
        }
    }

    [TestMethod]
    public void ZeroLengthBinaryShowsTheBinaryMessage()
    {
        var value = new RegistryValueInfo("Blob", 3, Array.Empty<byte>());

        Assert.AreEqual("(zero-length binary value)", RegistryValueDisplay.FormatData(value));
    }

    [TestMethod]
    public void InvalidDwordAndQwordLengthsShowTheExactMessages()
    {
        var oddDword = new RegistryValueInfo("OddDword", 4, new byte[] { 1, 2, 3 });
        var emptyDword = new RegistryValueInfo("EmptyDword", 4, Array.Empty<byte>());
        var oddQword = new RegistryValueInfo("OddQword", 11, new byte[7]);
        var emptyQword = new RegistryValueInfo("EmptyQword", 11, Array.Empty<byte>());

        Assert.AreEqual("(invalid DWORD (32-bit) value)", RegistryValueDisplay.FormatData(oddDword));
        Assert.AreEqual("(invalid DWORD (32-bit) value)", RegistryValueDisplay.FormatData(emptyDword));
        Assert.AreEqual("(invalid QWORD (64-bit) value)", RegistryValueDisplay.FormatData(oddQword));
        Assert.AreEqual("(invalid QWORD (64-bit) value)", RegistryValueDisplay.FormatData(emptyQword));
    }

    [TestMethod]
    public void ValidDwordAndQwordShowDecimal()
    {
        var dword = new RegistryValueInfo("Dword", 4, BitConverter.GetBytes(42u));
        const ulong QwordValue = 0x1122334455667788UL;
        var qword = new RegistryValueInfo("Qword", 11, BitConverter.GetBytes(QwordValue));

        Assert.AreEqual("42", RegistryValueDisplay.FormatData(dword));
        Assert.AreEqual(
            QwordValue.ToString(CultureInfo.InvariantCulture),
            RegistryValueDisplay.FormatData(qword));
    }

    [TestMethod]
    public void StringValuesShowTextWithoutTheTerminator()
    {
        var terminated = new RegistryValueInfo("Text", 1, Encoding.Unicode.GetBytes("hello\0"));
        var noTerminator = new RegistryValueInfo("Text", 1, Encoding.Unicode.GetBytes("hello"));
        var embedded = new RegistryValueInfo("Text", 1, Encoding.Unicode.GetBytes("ab\0cd\0"));
        var expand = new RegistryValueInfo("Expand", 2, Encoding.Unicode.GetBytes("%TEMP%\0"));

        Assert.AreEqual("hello", RegistryValueDisplay.FormatData(terminated));
        Assert.AreEqual("hello", RegistryValueDisplay.FormatData(noTerminator));
        Assert.AreEqual("ab", RegistryValueDisplay.FormatData(embedded));
        Assert.AreEqual("%TEMP%", RegistryValueDisplay.FormatData(expand));
    }

    [TestMethod]
    public void BinaryAndUnknownTypesShowHexBytes()
    {
        var binary = new RegistryValueInfo("Blob", 3, new byte[] { 0x01, 0x02, 0x03 });
        var none = new RegistryValueInfo("None", 0, new byte[] { 0x0A });
        var unknown = new RegistryValueInfo("Unknown", 0x1234, new byte[] { 0xDE, 0xAD });

        Assert.AreEqual("01 02 03", RegistryValueDisplay.FormatData(binary));
        Assert.AreEqual("0A", RegistryValueDisplay.FormatData(none));
        Assert.AreEqual("DE AD", RegistryValueDisplay.FormatData(unknown));
    }

    [TestMethod]
    public void TypeFiveHasNoOracleNameAndShowsHex()
    {
        var value = new RegistryValueInfo("BigEndian", 5, new byte[] { 0x11, 0x22, 0x33, 0x44 });

        Assert.AreEqual("REG_UNKNOWN", RegistryValueDisplay.FormatType(value));
        Assert.AreEqual("11 22 33 44", RegistryValueDisplay.FormatData(value));
    }

    [TestMethod]
    public void TypeNamesFollowTheOracleTable()
    {
        var cases = new (uint Type, string Name)[]
        {
            (0, "REG_NONE"),
            (1, "REG_SZ"),
            (2, "REG_EXPAND_SZ"),
            (3, "REG_BINARY"),
            (4, "REG_DWORD"),
            (5, "REG_UNKNOWN"),
            (6, "REG_LINK"),
            (7, "REG_MULTI_SZ"),
            (8, "REG_RESOURCE_LIST"),
            (9, "REG_FULL_RESOURCE_DESCRIPTOR"),
            (10, "REG_RESOURCE_REQUIREMENTS_LIST"),
            (11, "REG_QWORD"),
            (12, "REG_UNKNOWN"),
            (0x1234, "REG_UNKNOWN"),
        };

        foreach (var item in cases)
        {
            Assert.AreEqual(
                item.Name,
                RegistryValueTypeExtensions.ToTypeName(item.Type),
                item.Type.ToString(CultureInfo.InvariantCulture));

            var value = new RegistryValueInfo("Value", item.Type, Array.Empty<byte>());
            Assert.AreEqual(item.Name, RegistryValueDisplay.FormatType(value));
        }
    }

    [TestMethod]
    public void FormatNameUsesDefaultForTheEmptyName()
    {
        var defaultValue = new RegistryValueInfo(string.Empty, 4, BitConverter.GetBytes(1u));
        var namedValue = new RegistryValueInfo("Text", 1, Array.Empty<byte>());

        Assert.AreEqual("(Default)", RegistryValueDisplay.FormatName(defaultValue));
        Assert.AreEqual("Text", RegistryValueDisplay.FormatName(namedValue));
        Assert.AreEqual("(Default)", RegistryValueDisplay.FormatName(string.Empty));
        Assert.AreEqual("Text", RegistryValueDisplay.FormatName("Text"));
    }

    [TestMethod]
    public void LongValuesAreTruncatedWithinTheBudget()
    {
        var value = new RegistryValueInfo(
            "Text",
            1,
            Encoding.Unicode.GetBytes("abcdefghij\0"));

        Assert.AreEqual("abcde...", RegistryValueDisplay.FormatData(value, 8));
        Assert.AreEqual("abcdefghij", RegistryValueDisplay.FormatData(value, 10));
        Assert.AreEqual(string.Empty, RegistryValueDisplay.FormatData(value, 0));
        Assert.AreEqual("ab", RegistryValueDisplay.FormatData(value, 2));
        Assert.AreEqual("a...", RegistryValueDisplay.FormatData(value, 4));

        string longText = new('x', 2000);
        var longValue = new RegistryValueInfo("Long", 1, Encoding.Unicode.GetBytes(longText + "\0"));
        string formatted = RegistryValueDisplay.FormatData(longValue);

        Assert.AreEqual(1024, formatted.Length);
        StringAssert.EndsWith(formatted, "...");
    }

    [TestMethod]
    public void MultiStringDisplayIsProvisional()
    {
        // Provisional: classic regedit's exact REG_MULTI_SZ Data-column rendering is not in
        // docs/parity/oracle yet. This test pins the current join-with-spaces choice so a later
        // oracle capture changes it deliberately.
        var value = new RegistryValueInfo(
            "Multi",
            7,
            Encoding.Unicode.GetBytes("one\0\0two\0\0"));

        Assert.AreEqual("one  two", RegistryValueDisplay.FormatData(value));
    }

    [TestMethod]
    public void ValueInfoKeepsTheRawTypeAndLength()
    {
        var value = new RegistryValueInfo("Unknown", 0x1234, new byte[] { 1, 2, 3 });

        Assert.AreEqual("Unknown", value.Name);
        Assert.AreEqual(0x1234u, value.Type);
        Assert.AreEqual(0x1234u, (uint)value.TypeEnum);
        Assert.AreEqual(3, value.DataLength);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, value.Data);
    }
}

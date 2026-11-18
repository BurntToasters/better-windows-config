using Bwc.Gpo.Pol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Bwc.Gpo.Tests.PolTestBytes;

namespace Bwc.Gpo.Tests;

[TestClass]
public sealed class PolRoundTripTests
{
    [TestMethod]
    public void CanonicalFile_RoundTripsByteForByte()
    {
        var bytes = File(
            DwordRecord("Software\\Policies\\A", "One", 1),
            SzRecord("Software\\Policies\\A", "Two", "text"),
            DeleteRecord("Software\\Policies\\A", "Gone"),
            Record("Software\\Policies\\A", "Expand", 2, SzData("%SystemRoot%")),
            Record("Software\\Policies\\A", "Bin", 3, [0, 1, 2, 255]),
            Record("Software\\Policies\\A", "Multi", 7, Cat(SzData("x"), SzData("y"), Nul())),
            Record("Software\\Policies\\A", "Q", 11, BitConverter.GetBytes(0x0102030405060708ul)),
            Record("Software\\Policies\\A", "**mystery", 9, [1, 2, 3]),
            DwordRecord("Software\\Policies\\A\\Child", "V", 5),
            DwordRecord("Software\\Policies\\B", "V", 6));
        CollectionAssert.AreEqual(bytes, PolDocument.Parse(bytes).ToBytes());
    }

    [TestMethod]
    public void HeaderOnlyFile_RoundTrips()
    {
        CollectionAssert.AreEqual(Header, PolDocument.Parse(Header).ToBytes());
    }

    [TestMethod]
    public void OutOfOrderKeys_AreNormalizedOnceAndThenStable()
    {
        var bytes = File(DwordRecord("B", "V", 1), DwordRecord("a", "V", 2), DwordRecord("B", "W", 3));
        var once = PolDocument.Parse(bytes).ToBytes();
        var expected = File(DwordRecord("a", "V", 2), DwordRecord("B", "V", 1), DwordRecord("B", "W", 3));
        CollectionAssert.AreEqual(expected, once);
        CollectionAssert.AreEqual(once, PolDocument.Parse(once).ToBytes());
    }

    [TestMethod]
    public void DuplicateRecords_AreCollapsedOnceAndThenStable()
    {
        var bytes = File(DwordRecord("K", "A", 1), DwordRecord("K", "B", 2), DwordRecord("K", "A", 3));
        var once = PolDocument.Parse(bytes).ToBytes();
        CollectionAssert.AreEqual(File(DwordRecord("K", "A", 3), DwordRecord("K", "B", 2)), once);
        CollectionAssert.AreEqual(once, PolDocument.Parse(once).ToBytes());
    }

    [TestMethod]
    public void EditThenReparse_PreservesUntouchedRecordsByteForByte()
    {
        var bytes = File(
            DwordRecord("K", "A", 1),
            Record("K", "**mystery", 99, [7, 7]),
            Record("K", "Odd", 1, Utf16("unterminated")),
            DwordRecord("Z", "V", 1));
        var doc = PolDocument.Parse(bytes);
        doc.SetValue("K", "A", PolValueType.DWord, PolData.FromDWord(2));
        var expected = File(
            DwordRecord("K", "A", 2),
            Record("K", "**mystery", 99, [7, 7]),
            Record("K", "Odd", 1, Utf16("unterminated")),
            DwordRecord("Z", "V", 1));
        CollectionAssert.AreEqual(expected, doc.ToBytes());
    }

    [TestMethod]
    public void RealMachineAndUserRegistryPol_LoadThenSaveIsByteIdentical()
    {
        var root = Path.Combine(Environment.SystemDirectory, "GroupPolicy");
        var checkedAny = false;
        foreach (var scope in new[] { "Machine", "User" })
        {
            var path = Path.Combine(root, scope, "Registry.pol");
            byte[] bytes;
            try
            {
                if (!System.IO.File.Exists(path))
                {
                    continue;
                }

                bytes = System.IO.File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            checkedAny = true;
            var doc = PolDocument.Parse(bytes);
            CollectionAssert.AreEqual(bytes, doc.ToBytes(), $"{scope} Registry.pol is not reproduced byte for byte");
        }

        if (!checkedAny)
        {
            Assert.Inconclusive("No readable Registry.pol under the system GroupPolicy folder.");
        }
    }
}

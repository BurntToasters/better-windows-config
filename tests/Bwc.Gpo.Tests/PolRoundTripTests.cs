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
        CollectionAssert.AreEqual(File(DwordRecord("K", "B", 2), DwordRecord("K", "A", 3)), once);
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
            Record("K", "**mystery", 99, [7, 7]),
            Record("K", "Odd", 1, Utf16("unterminated")),
            DwordRecord("K", "A", 2),
            DwordRecord("Z", "V", 1));
        CollectionAssert.AreEqual(expected, doc.ToBytes());
    }

    private const string ProbeKey = @"Software\Policies\BWC-Oracle\Probe";
    private const string CloudContentKey = @"Software\Policies\Microsoft\Windows\CloudContent";

    private static string[] ProbeOrder(PolDocument doc) =>
        doc.GetRecords(ProbeKey)
            .Select(r => r.ValueName + "=" + (r.IsInstruction ? "del" : PolData.TryGetDWord(r.Data.Span)?.ToString()))
            .ToArray();

    [TestMethod]
    public void ClassicProbeSequence_ReplaysTheFourObservedSteps()
    {
        var doc = PolDocument.Create();
        doc.SetValue(CloudContentKey, "Other", PolValueType.DWord, PolData.FromDWord(1));

        doc.SetValue(ProbeKey, "Zeta", PolValueType.DWord, PolData.FromDWord(1));
        CollectionAssert.AreEqual(new[] { "Zeta=1" }, ProbeOrder(doc));
        doc.SetValue(ProbeKey, "Alpha", PolValueType.DWord, PolData.FromDWord(1));
        CollectionAssert.AreEqual(new[] { "Zeta=1", "Alpha=1" }, ProbeOrder(doc));

        doc.SetValue(ProbeKey, "Zeta", PolValueType.DWord, PolData.FromDWord(0));
        CollectionAssert.AreEqual(new[] { "Alpha=1", "Zeta=0" }, ProbeOrder(doc));

        doc.RemoveValue(ProbeKey, "Alpha");
        doc.AddDeleteInstruction(ProbeKey, "Alpha");
        CollectionAssert.AreEqual(new[] { "Zeta=0", "**del.Alpha=del" }, ProbeOrder(doc));
        var del = doc.GetValue(ProbeKey, "**del.Alpha")!;
        Assert.AreEqual(1u, del.Type);
        CollectionAssert.AreEqual(new byte[] { 0x20, 0, 0, 0 }, del.Data.ToArray());

        doc.RemoveValue(ProbeKey, "Zeta");
        CollectionAssert.AreEqual(new[] { "**del.Alpha=del" }, ProbeOrder(doc));

        CollectionAssert.AreEqual(new[] { ProbeKey, CloudContentKey }, doc.GetKeys().ToArray());
    }

    [TestMethod]
    public void ClassicProbeSequence_SavedBytesReloadToTheSameOrderAfterEveryStep()
    {
        var doc = PolDocument.Create();
        Action[] steps =
        [
            () => doc.SetValue(ProbeKey, "Zeta", PolValueType.DWord, PolData.FromDWord(1)),
            () => doc.SetValue(ProbeKey, "Alpha", PolValueType.DWord, PolData.FromDWord(1)),
            () => doc.SetValue(ProbeKey, "Zeta", PolValueType.DWord, PolData.FromDWord(0)),
            () =>
            {
                doc.RemoveValue(ProbeKey, "Alpha");
                doc.AddDeleteInstruction(ProbeKey, "Alpha");
            },
            () => doc.RemoveValue(ProbeKey, "Zeta"),
        ];
        var expected = new[]
        {
            File(DwordRecord(ProbeKey, "Zeta", 1)),
            File(DwordRecord(ProbeKey, "Zeta", 1), DwordRecord(ProbeKey, "Alpha", 1)),
            File(DwordRecord(ProbeKey, "Alpha", 1), DwordRecord(ProbeKey, "Zeta", 0)),
            File(DwordRecord(ProbeKey, "Zeta", 0), DeleteRecord(ProbeKey, "Alpha")),
            File(DeleteRecord(ProbeKey, "Alpha")),
        };
        for (var i = 0; i < steps.Length; i++)
        {
            steps[i]();
            var bytes = doc.ToBytes();
            CollectionAssert.AreEqual(expected[i], bytes, $"step {i + 1}");
            CollectionAssert.AreEqual(bytes, PolDocument.Parse(bytes).ToBytes(), $"reload step {i + 1}");
        }
    }

    [TestMethod]
    public void ClassicProbe_AnOkThatChangesNothingReportsNoChange()
    {
        var bytes = File(DwordRecord(ProbeKey, "Zeta", 0), DeleteRecord(ProbeKey, "Alpha"));
        var doc = PolDocument.Parse(bytes);
        doc.SetValue(ProbeKey, "Zeta", PolValueType.DWord, PolData.FromDWord(0));
        doc.AddDeleteInstruction(ProbeKey, "Alpha");
        Assert.IsFalse(doc.HasChanges);
        Assert.AreEqual(0, doc.Version);
        CollectionAssert.AreEqual(bytes, doc.ToBytes());
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

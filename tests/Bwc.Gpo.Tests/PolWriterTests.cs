using Bwc.Gpo.Pol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Bwc.Gpo.Tests.PolTestBytes;

namespace Bwc.Gpo.Tests;

[TestClass]
public sealed class PolWriterTests
{
    private static string[] Names(PolDocument doc) => doc.EnumerateRecords().Select(r => r.Key + "|" + r.ValueName).ToArray();

    [TestMethod]
    public void NewDocument_IsEmptyAndWritesTheEightByteHeader()
    {
        var doc = PolDocument.Create();
        Assert.IsTrue(doc.IsEmpty);
        CollectionAssert.AreEqual(Header, doc.ToBytes());
        CollectionAssert.AreEqual(Header, PolOrderRules.EmptyDocumentBytes());
    }

    [TestMethod]
    public void SetValue_WritesExactBytesForADword()
    {
        var doc = PolDocument.Create();
        doc.SetValue("A", "V", PolValueType.DWord, PolData.FromDWord(1));
        byte[] expected =
        [
            0x50, 0x52, 0x65, 0x67, 0x01, 0x00, 0x00, 0x00,
            0x5B, 0x00, 0x41, 0x00, 0x00, 0x00, 0x3B, 0x00,
            0x56, 0x00, 0x00, 0x00, 0x3B, 0x00,
            0x04, 0x00, 0x00, 0x00, 0x3B, 0x00,
            0x04, 0x00, 0x00, 0x00, 0x3B, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x5D, 0x00,
        ];
        CollectionAssert.AreEqual(expected, doc.ToBytes());
        Assert.IsFalse(doc.IsEmpty);
    }

    [TestMethod]
    public void AllSixTypes_AreWrittenWithTheirTypeNumbers()
    {
        var doc = PolDocument.Create();
        doc.SetValue("K", "sz", PolValueType.String, PolData.FromString("a"));
        doc.SetValue("K", "exp", PolValueType.ExpandString, PolData.FromString("%X%"));
        doc.SetValue("K", "bin", PolValueType.Binary, [1, 2, 3]);
        doc.SetValue("K", "dw", PolValueType.DWord, PolData.FromDWord(0xFFFFFFFF));
        doc.SetValue("K", "multi", PolValueType.MultiString, Cat(SzData("a"), Nul()));
        doc.SetValue("K", "qw", PolValueType.QWord, PolData.FromQWord(0x1122334455667788));
        var types = doc.GetRecords("K").Select(r => r.Type).ToArray();
        CollectionAssert.AreEqual(new uint[] { 1, 2, 3, 4, 7, 11 }, types);
        var reparsed = PolDocument.Parse(doc.ToBytes());
        Assert.AreEqual(0x1122334455667788ul, PolData.TryGetQWord(reparsed.GetValue("K", "qw")!.Data.Span));
        Assert.AreEqual("%X%", PolData.TryGetString(reparsed.GetValue("K", "exp")!.Data.Span));
    }

    [TestMethod]
    public void Keys_AreWrittenInCaseInsensitiveRegistryOrder()
    {
        var doc = PolDocument.Create();
        foreach (var key in new[] { "b", "A", "C", "a2", "Zeta" })
        {
            doc.SetValue(key, "V", PolValueType.DWord, PolData.FromDWord(1));
        }

        CollectionAssert.AreEqual(new[] { "A", "a2", "b", "C", "Zeta" }, doc.GetKeys().ToArray());
    }

    [TestMethod]
    public void ChildKey_SortsBeforeASiblingWhoseNameExtendsTheParent()
    {
        var doc = PolDocument.Create();
        foreach (var key in new[] { "A1", @"A\B", "A", "A B" })
        {
            doc.SetValue(key, "V", PolValueType.DWord, PolData.FromDWord(1));
        }

        CollectionAssert.AreEqual(new[] { "A", @"A\B", "A B", "A1" }, doc.GetKeys().ToArray());
    }

    [TestMethod]
    public void KeyOrder_IsStableForAlreadyEqualNamesAndNonAsciiLetters()
    {
        var doc = PolDocument.Create();
        foreach (var key in new[] { "éa", "Zed", "À", "ab", "à" })
        {
            doc.SetValue(key, "V", PolValueType.DWord, PolData.FromDWord(1));
        }

        Assert.AreEqual(4, doc.GetKeys().Count);
        CollectionAssert.AreEqual(new[] { "ab", "Zed", "À", "éa" }, doc.GetKeys().ToArray());
    }

    [TestMethod]
    public void ValuesInAKey_KeepCreationOrderNotAlphabeticalOrder()
    {
        var doc = PolDocument.Create();
        foreach (var name in new[] { "z", "a", "m" })
        {
            doc.SetValue("K", name, PolValueType.DWord, PolData.FromDWord(1));
        }

        CollectionAssert.AreEqual(new[] { "K|z", "K|a", "K|m" }, Names(doc));
    }

    [TestMethod]
    public void Overwrite_KeepsPositionAndExistingSpelling()
    {
        var doc = PolDocument.Create();
        doc.SetValue("Key", "First", PolValueType.DWord, PolData.FromDWord(1));
        doc.SetValue("Key", "Second", PolValueType.DWord, PolData.FromDWord(2));
        doc.SetValue("KEY", "FIRST", PolValueType.DWord, PolData.FromDWord(9));
        CollectionAssert.AreEqual(new[] { "Key|First", "Key|Second" }, Names(doc));
        Assert.AreEqual(9u, PolData.TryGetDWord(doc.GetValue("key", "first")!.Data.Span));
    }

    [TestMethod]
    public void OverwriteWithIdenticalContent_LeavesBytesUnchanged()
    {
        var original = File(DwordRecord("K", "A", 1), DwordRecord("K", "B", 2));
        var doc = PolDocument.Parse(original);
        doc.SetValue("K", "A", PolValueType.DWord, PolData.FromDWord(1));
        CollectionAssert.AreEqual(original, doc.ToBytes());
    }

    [TestMethod]
    public void RemoveThenRecreate_AppendsAtTheEndOfItsKey()
    {
        var doc = PolDocument.Create();
        doc.SetValue("K", "A", PolValueType.DWord, PolData.FromDWord(1));
        doc.SetValue("K", "B", PolValueType.DWord, PolData.FromDWord(2));
        doc.SetValue("K", "C", PolValueType.DWord, PolData.FromDWord(3));
        Assert.IsTrue(doc.RemoveValue("K", "a"));
        doc.SetValue("K", "A", PolValueType.DWord, PolData.FromDWord(4));
        CollectionAssert.AreEqual(new[] { "K|B", "K|C", "K|A" }, Names(doc));
    }

    [TestMethod]
    public void RemoveValue_OfMissingRecordReturnsFalseAndChangesNothing()
    {
        var original = File(DwordRecord("K", "A", 1));
        var doc = PolDocument.Parse(original);
        Assert.IsFalse(doc.RemoveValue("K", "nope"));
        Assert.IsFalse(doc.RemoveValue("nokey", "A"));
        CollectionAssert.AreEqual(original, doc.ToBytes());
    }

    [TestMethod]
    public void RemovingTheLastRecordOfAKey_RemovesTheKeyAndEmptiesTheDocument()
    {
        var doc = PolDocument.Parse(File(DwordRecord("K", "A", 1)));
        Assert.IsTrue(doc.RemoveValue("K", "A"));
        Assert.IsTrue(doc.IsEmpty);
        Assert.AreEqual(0, doc.GetKeys().Count);
        CollectionAssert.AreEqual(Header, doc.ToBytes());
    }

    [TestMethod]
    public void AddDeleteInstruction_WritesCanonicalBytes()
    {
        var doc = PolDocument.Create();
        var record = doc.AddDeleteInstruction("K", "Name");
        Assert.IsTrue(record.IsInstruction);
        Assert.AreEqual("**del.Name", record.ValueName);
        Assert.AreEqual(1u, record.Type);
        CollectionAssert.AreEqual(new byte[] { 0x20, 0, 0, 0 }, record.Data.ToArray());
        CollectionAssert.AreEqual(File(DeleteRecord("K", "Name")), doc.ToBytes());
    }

    [TestMethod]
    public void AddDeleteInstruction_DoesNotRemoveTheValueRecordAndAppends()
    {
        var doc = PolDocument.Create();
        doc.SetValue("K", "Name", PolValueType.DWord, PolData.FromDWord(1));
        doc.SetValue("K", "Other", PolValueType.DWord, PolData.FromDWord(1));
        doc.AddDeleteInstruction("K", "Name");
        CollectionAssert.AreEqual(new[] { "K|Name", "K|Other", "K|**del.Name" }, Names(doc));
    }

    [TestMethod]
    public void AddDeleteInstruction_Twice_ReplacesInPlace()
    {
        var doc = PolDocument.Create();
        doc.AddDeleteInstruction("K", "A");
        doc.SetValue("K", "B", PolValueType.DWord, PolData.FromDWord(1));
        doc.AddDeleteInstruction("K", "a");
        CollectionAssert.AreEqual(new[] { "K|**del.A", "K|B" }, Names(doc));
    }

    [TestMethod]
    public void RemoveInstructions_RemovesDelAndSoftForThatValueOnly()
    {
        var doc = PolDocument.Parse(File(
            DwordRecord("K", "X", 1),
            DeleteRecord("K", "X"),
            Record("K", "**soft.x", 4, U32(1)),
            DeleteRecord("K", "Y"),
            Record("K", "**SecureKey", 4, U32(1))));
        Assert.AreEqual(2, doc.RemoveInstructions("K", "X"));
        CollectionAssert.AreEqual(new[] { "K|X", "K|**del.Y", "K|**SecureKey" }, Names(doc));
        Assert.AreEqual(0, doc.RemoveInstructions("K", "X"));
    }

    [TestMethod]
    public void RemoveKey_RemovesEveryRecordIncludingInstructionsButNotChildren()
    {
        var doc = PolDocument.Parse(File(
            DwordRecord("A", "V", 1),
            DeleteRecord("A", "V"),
            Record("a", "**mystery", 3, [1]),
            DwordRecord(@"A\B", "V", 1)));
        Assert.AreEqual(3, doc.RemoveKey("A"));
        CollectionAssert.AreEqual(new[] { @"A\B|V" }, Names(doc));
        Assert.AreEqual(0, doc.RemoveKey("A"));
    }

    [TestMethod]
    public void GetRecords_OfMissingKeyIsEmpty()
    {
        Assert.AreEqual(0, PolDocument.Create().GetRecords("nope").Count);
    }

    [TestMethod]
    public void Edits_RefuseInvalidArguments()
    {
        var doc = PolDocument.Create();
        byte[] dword = PolData.FromDWord(1);
        foreach (var key in new[] { "", @"\A", @"A\", @"A\\B", "A\0B" })
        {
            Assert.ThrowsExactly<ArgumentException>(() => doc.SetValue(key, "V", PolValueType.DWord, dword), key);
        }

        Assert.ThrowsExactly<ArgumentNullException>(() => doc.SetValue(null!, "V", PolValueType.DWord, dword));
        Assert.ThrowsExactly<ArgumentNullException>(() => doc.SetValue("K", null!, PolValueType.DWord, dword));
        Assert.ThrowsExactly<ArgumentException>(() => doc.SetValue("K", "a\0b", PolValueType.DWord, dword));
        Assert.ThrowsExactly<ArgumentException>(() => doc.SetValue("K", "**del.X", PolValueType.DWord, dword));
        Assert.ThrowsExactly<ArgumentException>(() => doc.SetValue("K", "V", (PolValueType)99, dword));
        Assert.ThrowsExactly<ArgumentException>(() => doc.SetValue("K", "V", PolValueType.DWord, [1, 2, 3]));
        Assert.ThrowsExactly<ArgumentException>(() => doc.SetValue("K", "V", PolValueType.QWord, dword));
        Assert.ThrowsExactly<ArgumentException>(() => doc.SetValue("K", "V", PolValueType.String, [0x61, 0x00]));
        Assert.ThrowsExactly<ArgumentException>(() => doc.SetValue("K", "V", PolValueType.String, [0x61, 0x00, 0x00]));
        Assert.ThrowsExactly<ArgumentException>(() => doc.SetValue("K", "V", PolValueType.ExpandString, []));
        Assert.ThrowsExactly<ArgumentException>(() => doc.AddDeleteInstruction("K", "a\0b"));
        Assert.IsTrue(doc.IsEmpty);
        doc.SetValue("K", "", PolValueType.Binary, []);
        Assert.IsFalse(doc.IsEmpty);
    }

    [TestMethod]
    public void GetKeys_IsASnapshot()
    {
        var doc = PolDocument.Create();
        doc.SetValue("A", "V", PolValueType.DWord, PolData.FromDWord(1));
        var keys = doc.GetKeys();
        doc.SetValue("B", "V", PolValueType.DWord, PolData.FromDWord(1));
        Assert.AreEqual(1, keys.Count);
    }

    [TestMethod]
    public void Data_IsCopiedAtSetTime()
    {
        var doc = PolDocument.Create();
        var data = PolData.FromDWord(1);
        doc.SetValue("K", "V", PolValueType.DWord, data);
        data[0] = 0xFF;
        Assert.AreEqual(1u, PolData.TryGetDWord(doc.GetValue("K", "V")!.Data.Span));
    }

    [TestMethod]
    public void PolData_FromStringAddsTheTerminatorAndRejectsOddInput()
    {
        CollectionAssert.AreEqual(new byte[] { 0x61, 0x00, 0x00, 0x00 }, PolData.FromString("a"));
        CollectionAssert.AreEqual(new byte[] { 0x00, 0x00 }, PolData.FromString(""));
        Assert.AreEqual("", PolData.TryGetString([0x00, 0x00]));
        Assert.AreEqual("", PolData.TryGetString([]));
        Assert.IsNull(PolData.TryGetDWord([1, 2, 3]));
        Assert.IsNull(PolData.TryGetQWord([1, 2, 3, 4]));
    }
}

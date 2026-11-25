using Bwc.Gpo.Pol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Bwc.Gpo.Tests.PolTestBytes;

namespace Bwc.Gpo.Tests;

/// <summary>
/// The list instructions <c>**delvals.</c> and <c>**soft.&lt;name&gt;</c> (docs/failure-modes/gpo-pol.md 2.5a). Each
/// test is a failure mode of that section: position, repeat, case, unknown instructions, removal and validation. The
/// tests that also set a value are in PolWriterTests.
/// </summary>
[TestClass]
public sealed class PolInstructionTests
{
    private const string Key = @"Software\Policies\BWC-Oracle\Lists\Plain";
    private static readonly byte[] InstructionData = [0x20, 0x00, 0x00, 0x00];

    private static byte[] Delvals(string key = Key) => Record(key, "**delvals.", 1, InstructionData);

    private static byte[] Soft(string name, string key = Key) => Record(key, "**soft." + name, 1, InstructionData);

    private static void AssertFile(PolDocument document, params byte[][] records) =>
        CollectionAssert.AreEqual(File(records), document.ToBytes());

    [TestMethod]
    public void AddDeleteValues_OnAnEmptyDocument_WritesTheCanonicalRecord()
    {
        var document = PolDocument.Create();

        var record = document.AddDeleteValuesInstruction(Key);

        Assert.AreEqual("**delvals.", record.ValueName);
        Assert.AreEqual(1u, record.Type);
        CollectionAssert.AreEqual(InstructionData, record.Data.ToArray());
        AssertFile(document, Delvals());
        Assert.AreEqual(1, document.RecordCount);
    }

    [TestMethod]
    public void AddDeleteValues_GoesBeforeEveryOtherRecordOfTheKey()
    {
        var document = PolDocument.Parse(File(SzRecord(Key, "a", "a"), DeleteRecord(Key, "gone"), SzRecord(Key, "b", "b")));

        document.AddDeleteValuesInstruction(Key);

        AssertFile(document, Delvals(), SzRecord(Key, "a", "a"), DeleteRecord(Key, "gone"), SzRecord(Key, "b", "b"));
    }

    [TestMethod]
    public void AddDeleteValues_TwiceDoesNotStackAndChangesNothing()
    {
        var document = PolDocument.Create();
        document.AddDeleteValuesInstruction(Key);
        var reloaded = PolDocument.Parse(document.ToBytes());
        var version = reloaded.Version;

        reloaded.AddDeleteValuesInstruction(Key);
        reloaded.AddDeleteValuesInstruction(Key);

        Assert.AreEqual(1, reloaded.RecordCount);
        Assert.AreEqual(version, reloaded.Version);
        Assert.IsFalse(reloaded.HasChanges);
    }

    [TestMethod]
    public void AddDeleteValues_WhenItIsNotFirst_MovesItToTheFront()
    {
        var document = PolDocument.Parse(File(SzRecord(Key, "a", "a"), Delvals(), SzRecord(Key, "b", "b")));

        document.AddDeleteValuesInstruction(Key);

        AssertFile(document, Delvals(), SzRecord(Key, "a", "a"), SzRecord(Key, "b", "b"));
        Assert.IsTrue(document.HasChanges);
        Assert.AreEqual(3, document.RecordCount);
    }

    [TestMethod]
    public void AddDeleteValues_WithOtherData_ReplacesItAtTheFrontKeepingTheSpelling()
    {
        var odd = Record(Key, "**DelVals.", 4, U32(7));
        var document = PolDocument.Parse(File(SzRecord(Key, "a", "a"), odd));

        var record = document.AddDeleteValuesInstruction(Key);

        Assert.AreEqual("**DelVals.", record.ValueName);
        AssertFile(document, Record(Key, "**DelVals.", 1, InstructionData), SzRecord(Key, "a", "a"));
    }

    [TestMethod]
    public void AddDeleteValues_MatchesTheNameCaseInsensitively()
    {
        var upper = Record(Key, "**DELVALS.", 1, InstructionData);
        var document = PolDocument.Parse(File(upper, SzRecord(Key, "a", "a")));
        var version = document.Version;

        document.AddDeleteValuesInstruction(Key.ToUpperInvariant());

        Assert.AreEqual(2, document.RecordCount);
        Assert.AreEqual(version, document.Version);
        AssertFile(document, upper, SzRecord(Key, "a", "a"));
    }

    [TestMethod]
    public void DeleteValues_AsTheOnlyRecord_KeepsTheKeyAndRoundTrips()
    {
        var bytes = File(Delvals());
        var document = PolDocument.Parse(bytes);

        CollectionAssert.AreEqual(new[] { Key }, document.GetKeys().ToArray());
        Assert.AreEqual(1, document.RecordCount);
        CollectionAssert.AreEqual(bytes, document.ToBytes());

        Assert.IsTrue(document.RemoveDeleteValuesInstruction(Key));

        Assert.IsTrue(document.IsEmpty);
        Assert.AreEqual(0, document.GetKeys().Count);
    }

    [TestMethod]
    public void Loading_DoesNotInterpretDelvals()
    {
        var document = PolDocument.Parse(File(SzRecord(Key, "a", "a"), Delvals(), SzRecord(Key, "b", "b")));

        Assert.AreEqual(3, document.RecordCount);
        Assert.IsNotNull(document.GetValue(Key, "a"));
    }

    [TestMethod]
    public void UnknownInstructions_StayByteForByteAndInOrder()
    {
        var mystery = Record(Key, "**Mystery", 9, [1, 2, 3]);
        var other = Record(Key, "**DeleteValues", 1, InstructionData);
        var document = PolDocument.Parse(File(mystery, SzRecord(Key, "a", "a"), other));

        document.AddDeleteValuesInstruction(Key);
        document.AddSoftInstruction(Key, "a");

        AssertFile(document, Delvals(), mystery, SzRecord(Key, "a", "a"), other, Soft("a"));
        Assert.IsTrue(document.RemoveDeleteValuesInstruction(Key));
        Assert.IsTrue(document.RemoveSoftInstruction(Key, "a"));
        AssertFile(document, mystery, SzRecord(Key, "a", "a"), other);
    }

    [TestMethod]
    public void AddSoft_OnAKeyThatHoldsTheValue_AppendsAndLeavesTheValueInPlace()
    {
        var document = PolDocument.Parse(File(SzRecord(Key, "a", "a"), SzRecord(Key, "b", "b")));

        var record = document.AddSoftInstruction(Key, "a");

        Assert.AreEqual("**soft.a", record.ValueName);
        Assert.AreEqual(1u, record.Type);
        CollectionAssert.AreEqual(InstructionData, record.Data.ToArray());
        AssertFile(document, SzRecord(Key, "a", "a"), SzRecord(Key, "b", "b"), Soft("a"));
    }

    [TestMethod]
    public void AddSoft_Repeated_ChangesNothing()
    {
        var document = PolDocument.Parse(File(Soft("a"), SzRecord(Key, "b", "b")));
        var version = document.Version;

        document.AddSoftInstruction(Key, "A");

        Assert.AreEqual(version, document.Version);
        Assert.IsFalse(document.HasChanges);
        Assert.AreEqual(2, document.RecordCount);
    }

    [TestMethod]
    public void AddSoft_WithOtherData_ReplacesByAppend()
    {
        var odd = Record(Key, "**Soft.a", 4, U32(1));
        var document = PolDocument.Parse(File(odd, SzRecord(Key, "b", "b")));

        document.AddSoftInstruction(Key, "a");

        AssertFile(document, SzRecord(Key, "b", "b"), Record(Key, "**Soft.a", 1, InstructionData));
    }

    [TestMethod]
    public void RemoveInstructions_RemovesTheSoftRecordOfAValueButNeverDelvals()
    {
        var document = PolDocument.Parse(File(Delvals(), Soft("a"), DeleteRecord(Key, "a"), SzRecord(Key, "b", "b")));

        var removed = document.RemoveInstructions(Key, "a");

        Assert.AreEqual(2, removed);
        AssertFile(document, Delvals(), SzRecord(Key, "b", "b"));
    }

    [TestMethod]
    public void Remove_OfARecordThatIsNotThere_ReturnsFalseAndChangesNothing()
    {
        var document = PolDocument.Parse(File(SzRecord(Key, "a", "a")));
        var version = document.Version;

        Assert.IsFalse(document.RemoveDeleteValuesInstruction(Key));
        Assert.IsFalse(document.RemoveDeleteValuesInstruction("Other"));
        Assert.IsFalse(document.RemoveSoftInstruction(Key, "a"));

        Assert.AreEqual(version, document.Version);
        Assert.IsFalse(document.HasChanges);
    }

    [TestMethod]
    public void RemoveKey_RemovesDelvalsWithTheRest()
    {
        var document = PolDocument.Parse(File(Delvals(), SzRecord(Key, "a", "a")));

        Assert.AreEqual(2, document.RemoveKey(Key));

        Assert.IsTrue(document.IsEmpty);
    }

    [TestMethod]
    public void InvalidArguments_ThrowAndWriteNothing()
    {
        var document = PolDocument.Create();

        Assert.ThrowsExactly<ArgumentException>(() => document.AddDeleteValuesInstruction(string.Empty));
        Assert.ThrowsExactly<ArgumentException>(() => document.AddDeleteValuesInstruction("A\\\\B"));
        Assert.ThrowsExactly<ArgumentException>(() => document.AddDeleteValuesInstruction("\\A"));
        Assert.ThrowsExactly<ArgumentException>(() => document.AddSoftInstruction("A\0B", "x"));
        Assert.ThrowsExactly<ArgumentException>(() => document.AddSoftInstruction(Key, "x\0y"));
        Assert.ThrowsExactly<ArgumentNullException>(() => document.AddSoftInstruction(Key, null!));

        Assert.IsTrue(document.IsEmpty);
        Assert.AreEqual(0, document.Version);
    }

    [TestMethod]
    public void Keys_SortAroundTheInstructionsAsBefore()
    {
        var document = PolDocument.Create();
        document.AddDeleteValuesInstruction(@"B\Child");
        document.AddDeleteValuesInstruction("B");
        document.AddSoftInstruction("A", "x");

        CollectionAssert.AreEqual(new[] { "A", "B", @"B\Child" }, document.GetKeys().ToArray());
    }
}

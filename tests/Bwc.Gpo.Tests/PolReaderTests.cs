using Bwc.Gpo.Pol;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Bwc.Gpo.Tests.PolTestBytes;

namespace Bwc.Gpo.Tests;

[TestClass]
public sealed class PolReaderTests
{
    [TestMethod]
    public void EmptyFile_IsEmptyReason()
    {
        var ex = Fail([]);
        Assert.AreEqual(PolFormatReason.Empty, ex.Reason);
        Assert.AreEqual(0, ex.Offset);
    }

    [TestMethod]
    public void ShortHeader_IsHeaderTruncated()
    {
        var ex = Fail([0x50, 0x52, 0x65]);
        Assert.AreEqual(PolFormatReason.HeaderTruncated, ex.Reason);
        Assert.AreEqual(0, ex.Offset);
    }

    [TestMethod]
    public void WrongSignature_IsBadSignature()
    {
        var ex = Fail([0x50, 0x52, 0x65, 0x68, 1, 0, 0, 0]);
        Assert.AreEqual(PolFormatReason.BadSignature, ex.Reason);
    }

    [TestMethod]
    public void ByteOrderMarkBeforeHeader_IsRefused()
    {
        Assert.AreEqual(PolFormatReason.BadSignature, Fail(Cat([0xFF, 0xFE], Header)).Reason);
        Assert.AreEqual(PolFormatReason.BadSignature, Fail(Cat([0xEF, 0xBB, 0xBF], Header)).Reason);
    }

    [TestMethod]
    public void WrongVersion_IsBadVersionAtOffset4()
    {
        var ex = Fail([0x50, 0x52, 0x65, 0x67, 2, 0, 0, 0]);
        Assert.AreEqual(PolFormatReason.BadVersion, ex.Reason);
        Assert.AreEqual(4, ex.Offset);
    }

    [TestMethod]
    public void HeaderOnly_IsEmptyDocument()
    {
        var doc = PolDocument.Parse(Header);
        Assert.IsTrue(doc.IsEmpty);
        Assert.AreEqual(0, doc.RecordCount);
    }

    [TestMethod]
    public void OneRecord_ParsesAllFields()
    {
        var doc = PolDocument.Parse(File(DwordRecord(@"Software\Policies\X", "Flag", 7)));
        var record = doc.GetValue(@"software\policies\x", "FLAG");
        Assert.IsNotNull(record);
        Assert.AreEqual(@"Software\Policies\X", record.Key);
        Assert.AreEqual("Flag", record.ValueName);
        Assert.AreEqual(4u, record.Type);
        Assert.AreEqual(7u, PolData.TryGetDWord(record.Data.Span));
        Assert.IsFalse(record.IsInstruction);
    }

    [TestMethod]
    public void HandComputedRecordBytes_ParseAndWriteBack()
    {
        byte[] file =
        [
            0x50, 0x52, 0x65, 0x67, 0x01, 0x00, 0x00, 0x00,
            0x5B, 0x00, 0x41, 0x00, 0x00, 0x00, 0x3B, 0x00,
            0x56, 0x00, 0x00, 0x00, 0x3B, 0x00,
            0x04, 0x00, 0x00, 0x00, 0x3B, 0x00,
            0x04, 0x00, 0x00, 0x00, 0x3B, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x5D, 0x00,
        ];
        var doc = PolDocument.Parse(file);
        Assert.AreEqual(1u, PolData.TryGetDWord(doc.GetValue("A", "V")!.Data.Span));
        CollectionAssert.AreEqual(file, doc.ToBytes());
    }

    [TestMethod]
    public void EmptyValueName_IsDefaultValue()
    {
        var doc = PolDocument.Parse(File(SzRecord("K", "", "x")));
        Assert.IsNotNull(doc.GetValue("K", ""));
    }

    [TestMethod]
    public void EveryPrefixOfAValidFile_FailsTypedAndInBounds()
    {
        var file = File(DwordRecord("Key", "Val", 3), SzRecord("Key2", "S", "hello"));
        var firstEnd = Header.Length + DwordRecord("Key", "Val", 3).Length;
        for (var length = 1; length < file.Length; length++)
        {
            var prefix = file[..length];
            if (length == Header.Length || length == firstEnd)
            {
                PolDocument.Parse(prefix);
                continue;
            }

            var ex = Fail(prefix);
            Assert.IsTrue(ex.Offset >= 0 && ex.Offset <= length, $"offset {ex.Offset} for length {length}");
        }
    }

    [TestMethod]
    public void UnterminatedKey_IsUnterminatedString()
    {
        var file = Cat(Header, Ch('['), Utf16("Key"));
        var ex = Fail(file);
        Assert.AreEqual(PolFormatReason.UnterminatedString, ex.Reason);
        Assert.AreEqual(Header.Length + 2, ex.Offset);
    }

    [TestMethod]
    public void MissingDelimiterAfterKey_Fails()
    {
        var good = File(SzRecord("A", "V", "x"));
        var bad = (byte[])good.Clone();
        bad[14] = 0x2C;
        var ex = Fail(bad);
        Assert.AreEqual(PolFormatReason.MissingDelimiter, ex.Reason);
        Assert.AreEqual(14, ex.Offset);
    }

    [TestMethod]
    public void MissingDelimiterAfterNameTypeAndSize_Fail()
    {
        var good = File(DwordRecord("A", "V", 1));
        foreach (var offset in new[] { 20, 26, 32 })
        {
            var bad = (byte[])good.Clone();
            bad[offset] = 0x2C;
            var ex = Fail(bad);
            Assert.AreEqual(PolFormatReason.MissingDelimiter, ex.Reason, $"offset {offset}");
            Assert.AreEqual(offset, ex.Offset);
        }
    }

    [TestMethod]
    public void RecordWithoutOpeningBracket_FailsAtRecordStart()
    {
        var file = Cat(Header, Ch('x'), Nul());
        var ex = Fail(file);
        Assert.AreEqual(PolFormatReason.ExpectedRecordStart, ex.Reason);
        Assert.AreEqual(8, ex.Offset);
    }

    [TestMethod]
    public void MissingClosingBracket_Fails()
    {
        var file = File(DwordRecord("A", "V", 1));
        file[^2] = 0x2C;
        var ex = Fail(file);
        Assert.AreEqual(PolFormatReason.MissingRecordEnd, ex.Reason);
        Assert.AreEqual(file.Length - 2, ex.Offset);
    }

    [TestMethod]
    public void SizeLargerThanRemaining_IsSizePastEnd()
    {
        var file = File(RecordWithSize("A", "V", 3, 100, [1, 2, 3, 4]));
        var ex = Fail(file);
        Assert.AreEqual(PolFormatReason.SizePastEnd, ex.Reason);
        Assert.AreEqual(28, ex.Offset);
    }

    [TestMethod]
    public void SizeLeavesNoRoomForClosingBracket_IsSizePastEnd()
    {
        var file = File(RecordWithSize("A", "V", 3, 5, [1, 2, 3, 4]));
        Assert.AreEqual(PolFormatReason.SizePastEnd, Fail(file).Reason);
    }

    [TestMethod]
    public void HugeSizeField_FailsWithoutAllocating()
    {
        foreach (var size in new[] { 0xFFFFFFFFu, 0x80000000u, 0x7FFFFFFFu, 0xFFFFFFFEu })
        {
            var file = File(RecordWithSize("A", "V", 3, size, [1, 2, 3, 4]));
            Assert.AreEqual(PolFormatReason.SizePastEnd, Fail(file).Reason);
        }
    }

    [TestMethod]
    public void SizeSmallerThanData_FailsAtClosingBracket()
    {
        var file = File(RecordWithSize("A", "V", 3, 2, [1, 2, 3, 4]));
        Assert.AreEqual(PolFormatReason.MissingRecordEnd, Fail(file).Reason);
    }

    [TestMethod]
    public void TrailingStrayByteOrZeroPadding_IsRefused()
    {
        var good = File(DwordRecord("A", "V", 1));
        Assert.AreEqual(PolFormatReason.ExpectedRecordStart, Fail(Cat(good, [0x00])).Reason);
        Assert.AreEqual(PolFormatReason.ExpectedRecordStart, Fail(Cat(good, [0x00, 0x00])).Reason);
    }

    [TestMethod]
    public void EmbeddedNulInKey_FailsAsMissingDelimiter()
    {
        var file = Cat(Header, Ch('['), Utf16("A"), Nul(), Utf16("B"), Nul(), Ch(';'), Utf16("V"), Nul(), Ch(';'), U32(4), Ch(';'), U32(0), Ch(';'), Ch(']'));
        Assert.AreEqual(PolFormatReason.MissingDelimiter, Fail(file).Reason);
    }

    [TestMethod]
    public void MisalignedRecord_FailsTyped()
    {
        var file = Cat(Header, [0x00], DwordRecord("A", "V", 1));
        var ex = Fail(file);
        Assert.IsTrue(ex.Offset >= 8);
    }

    [TestMethod]
    public void UnknownTypeNumber_IsKeptByteForByte()
    {
        var bytes = File(Record("A", "V", 0x1234, [9, 8, 7]));
        var doc = PolDocument.Parse(bytes);
        Assert.AreEqual(0x1234u, doc.GetValue("A", "V")!.Type);
        CollectionAssert.AreEqual(bytes, doc.ToBytes());
    }

    [TestMethod]
    public void StringDataWithoutTerminator_IsKept()
    {
        var bytes = File(Record("A", "V", 1, Utf16("abc")));
        var doc = PolDocument.Parse(bytes);
        Assert.AreEqual("abc", PolData.TryGetString(doc.GetValue("A", "V")!.Data.Span));
        CollectionAssert.AreEqual(bytes, doc.ToBytes());
    }

    [TestMethod]
    public void StringDataOfOddLength_IsKeptAndNotDecoded()
    {
        var bytes = File(Record("A", "V", 1, [0x61, 0x00, 0x62]));
        var doc = PolDocument.Parse(bytes);
        Assert.IsNull(PolData.TryGetString(doc.GetValue("A", "V")!.Data.Span));
        CollectionAssert.AreEqual(bytes, doc.ToBytes());
    }

    [TestMethod]
    public void ZeroSizeData_IsValid()
    {
        var bytes = File(Record("A", "V", 3, []));
        var doc = PolDocument.Parse(bytes);
        Assert.AreEqual(0, doc.GetValue("A", "V")!.Data.Length);
    }

    [TestMethod]
    public void DataContainingDelimiters_IsFramedBySizeOnly()
    {
        var data = Cat(Ch(']'), Ch(';'), Ch('['));
        var bytes = File(Record("A", "V", 3, data), DwordRecord("A", "W", 2));
        var doc = PolDocument.Parse(bytes);
        CollectionAssert.AreEqual(data, doc.GetValue("A", "V")!.Data.ToArray());
        Assert.AreEqual(2, doc.RecordCount);
        CollectionAssert.AreEqual(bytes, doc.ToBytes());
    }

    [TestMethod]
    public void UnpairedSurrogateInKey_RoundTripsLosslessly()
    {
        var key = "A\uD800B";
        var bytes = File(DwordRecord(key, "V", 1));
        var doc = PolDocument.Parse(bytes);
        Assert.IsNotNull(doc.GetValue(key, "V"));
        CollectionAssert.AreEqual(bytes, doc.ToBytes());
    }

    [TestMethod]
    public void KeyWithTrailingBackslashOrEmptySegments_IsAcceptedAndNotNormalized()
    {
        var bytes = File(DwordRecord(@"A\B\", "V", 1), DwordRecord(@"A\\B", "V", 2), DwordRecord("", "V", 3));
        var doc = PolDocument.Parse(bytes);
        Assert.AreEqual(3, doc.GetKeys().Count);
        Assert.IsNotNull(doc.GetValue(@"A\B\", "V"));
        Assert.IsNull(doc.GetValue(@"A\B", "V"));
    }

    [TestMethod]
    public void ValueNamesAndKeysAreCaseInsensitive()
    {
        var doc = PolDocument.Parse(File(DwordRecord("Key", "Name", 1)));
        Assert.IsNotNull(doc.GetValue("KEY", "nAME"));
    }

    [TestMethod]
    public void DuplicateRecordWithDifferentData_KeepsTheFirstPlaceAndTakesTheLastData()
    {
        // Changed from "moves to the end of the key": captures D/Dup_DifferentLastWins and D/Dup_NonAdjacent show classic
        // keeping the first position of a duplicate name with the data of the last record.
        var doc = PolDocument.Parse(File(
            DwordRecord("K", "A", 1),
            DwordRecord("K", "B", 2),
            DwordRecord("K", "a", 3)));
        var records = doc.GetRecords("K");
        Assert.AreEqual(2, records.Count);
        Assert.AreEqual("A", records[0].ValueName);
        Assert.AreEqual("B", records[1].ValueName);
        Assert.AreEqual(3u, PolData.TryGetDWord(records[0].Data.Span));
    }

    [TestMethod]
    public void DuplicateRecordWithIdenticalData_KeepsTheFirstRecordAndPosition()
    {
        var doc = PolDocument.Parse(File(
            DwordRecord("K", "A", 1),
            DwordRecord("K", "B", 2),
            DwordRecord("K", "a", 1)));
        var records = doc.GetRecords("K");
        Assert.AreEqual(2, records.Count);
        Assert.AreEqual("A", records[0].ValueName);
        Assert.AreEqual("B", records[1].ValueName);
    }

    [TestMethod]
    public void ParsedDocument_StartsWithVersionZeroAndNoChanges()
    {
        var doc = PolDocument.Parse(File(DwordRecord("K", "A", 1), DwordRecord("K", "A", 2)));
        Assert.AreEqual(0, doc.Version);
        Assert.IsFalse(doc.HasChanges);
    }

    [TestMethod]
    public void InstructionRecords_AreKeptInPositionAndNotInterpreted()
    {
        var bytes = File(
            DwordRecord("K", "X", 1),
            DeleteRecord("K", "X"),
            Record("K", "**delvals.", 1, [0x20, 0, 0, 0]),
            Record("K", "**DeleteValues", 1, SzData("a;b")),
            Record("K", "**DeleteKeys", 1, SzData("c")),
            Record("K", "**SecureKey", 4, U32(1)),
            Record("K", "**soft.Y", 4, U32(1)),
            Record("K", "**mystery", 3, [1, 2, 3]));
        var doc = PolDocument.Parse(bytes);
        var records = doc.GetRecords("K");
        Assert.AreEqual(8, records.Count);
        Assert.IsNotNull(doc.GetValue("K", "X"));
        Assert.AreEqual(7, records.Count(r => r.IsInstruction));
        CollectionAssert.AreEqual(bytes, doc.ToBytes());
    }

    [TestMethod]
    public void TryParse_ReportsFailureWithoutThrowing()
    {
        Assert.IsFalse(PolDocument.TryParse([], out var doc, out var failure));
        Assert.IsNull(doc);
        Assert.AreEqual(PolFormatReason.Empty, failure!.Reason);
        Assert.IsTrue(PolDocument.TryParse(Header, out doc, out failure));
        Assert.IsNotNull(doc);
        Assert.IsNull(failure);
    }

    [TestMethod]
    public void StreamOverload_ParsesTheSameBytes()
    {
        var bytes = File(DwordRecord("A", "V", 1));
        using var stream = new MemoryStream(bytes);
        CollectionAssert.AreEqual(bytes, PolDocument.Parse(stream).ToBytes());
    }

    [TestMethod]
    public void StreamLongerThanTheCap_IsFileTooLarge()
    {
        using var stream = new LengthOnlyStream(PolReader.MaxStreamBytes + 1);
        var ex = Assert.ThrowsExactly<PolFormatException>(() => PolDocument.Parse(stream));
        Assert.AreEqual(PolFormatReason.FileTooLarge, ex.Reason);
    }

    [TestMethod]
    public void NonSeekableStreamLongerThanTheCap_IsFileTooLarge()
    {
        using var stream = new EndlessStream();
        var ex = Assert.ThrowsExactly<PolFormatException>(() => PolDocument.Parse(stream));
        Assert.AreEqual(PolFormatReason.FileTooLarge, ex.Reason);
    }

    [TestMethod]
    public void RandomAndMutatedInput_OnlyEverFailsWithPolFormatException()
    {
        var random = new Random(20260617);
        var seed = File(
            DwordRecord("Software\\P", "A", 1),
            SzRecord("Software\\P", "B", "text"),
            DeleteRecord("Software\\P", "C"),
            Record("Other", "Bin", 3, [1, 2, 3, 4, 5]));
        for (var i = 0; i < 20000; i++)
        {
            byte[] input;
            if (i % 3 == 0)
            {
                input = new byte[random.Next(0, 80)];
                random.NextBytes(input);
                if (random.Next(2) == 0 && input.Length >= 8)
                {
                    Header.CopyTo(input, 0);
                }
            }
            else
            {
                input = (byte[])seed.Clone();
                var edits = random.Next(1, 4);
                for (var e = 0; e < edits; e++)
                {
                    input[random.Next(input.Length)] = (byte)random.Next(256);
                }

                if (random.Next(3) == 0)
                {
                    input = input[..random.Next(input.Length)];
                }
            }

            try
            {
                if (PolDocument.TryParse(input, out var doc, out var failure))
                {
                    var written = doc.ToBytes();
                    PolDocument.Parse(written);
                }
                else
                {
                    Assert.IsTrue(failure.Offset >= 0 && failure.Offset <= input.Length);
                }
            }
            catch (Exception ex) when (ex is not AssertFailedException)
            {
                Assert.Fail($"iteration {i}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private sealed class LengthOnlyStream(long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => count;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

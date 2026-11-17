using Bwc.Registry.Resources;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Registry.Tests;

// Failure modes are documented in docs/failure-modes/resource-values.md. Each test name matches the row there.
[TestClass]
public sealed class ResourceParserTests
{
    // HKLM\HARDWARE\RESOURCEMAP\System Resources\Physical Memory, .Translated (REG_RESOURCE_LIST), read only.
    private const string PhysicalMemoryHex =
        "01000000000000000000000000000000050000000301000000000040000000000000FFBF0000000007010002000000000100000000E03B0300000000030100000000003C040000000000020000000000030100000000083C0400000000004D0300000000030100000000933F0400000000006D0000000000";

    // Bytes of HKLM\HARDWARE\DESCRIPTION\System, value Configuration Data (REG_FULL_RESOURCE_DESCRIPTOR, read only).
    private const string ConfigurationDataHex =
        "FFFFFFFFFFFFFFFF000000000200000005000000180000000000000000000000000000000000FFFFFFFF0000FFFF02000000FFFFFFFF0000FFFF02000500000000000000000000000000000000000000";

    [TestMethod]
    public void Resource_EmptyValue_IsTruncated()
    {
        Assert.AreEqual(ResourceParseError.Truncated, CmResourceParser.ParseList(Array.Empty<byte>()).Error);
        Assert.AreEqual(ResourceParseError.Truncated, CmResourceParser.ParseFull(Array.Empty<byte>()).Error);
        Assert.AreEqual(ResourceParseError.Truncated, IoRequirementsParser.Parse(Array.Empty<byte>()).Error);
    }

    [TestMethod]
    public void Resource_ZeroCount_IsEmptyList()
    {
        ResourceParseResult<CmResourceList> result = CmResourceParser.ParseList(new ResourceBytes().U32(0).ToArray());

        Assert.AreEqual(ResourceParseError.None, result.Error);
        Assert.AreEqual(0, result.Value!.Full.Count);
        Assert.AreEqual(0, result.TrailingBytes);
    }

    [TestMethod]
    public void Resource_ForgedCount_RejectedBeforeAllocation()
    {
        byte[] data = new ResourceBytes().U32(0xFFFFFFFF).Zeros(16).ToArray();
        long before = GC.GetAllocatedBytesForCurrentThread();

        ResourceParseResult<CmResourceList> result = CmResourceParser.ParseList(data);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(ResourceParseError.CountExceedsData, result.Error);
        Assert.IsTrue(allocated < 64 * 1024, $"a forged count allocated {allocated} bytes");
    }

    [TestMethod]
    public void Resource_CountTimesStride_DoesNotOverflow()
    {
        // 0x0CCCCCCD * 20 wraps to 4 in 32-bit arithmetic, so a 32-bit bounds check would accept 4 bytes.
        byte[] fullWithForgedPartials = new ResourceBytes().U32(1).U32(0).U32(0).U16(0).U16(0).U32(0x0CCCCCCD).Zeros(4).ToArray();

        Assert.AreEqual(ResourceParseError.CountExceedsData, CmResourceParser.ParseList(fullWithForgedPartials).Error);
        Assert.AreEqual(ResourceParseError.CountExceedsData, CmResourceParser.ParseFull(fullWithForgedPartials.AsSpan(4).ToArray()).Error);
    }

    [TestMethod]
    public void Resource_PartialCountOverrun_IsRejected()
    {
        byte[] data = new ResourceBytes().U32(0).U32(0).U16(0).U16(0).U32(3).Partial(3, 1, 0, Memory(0x1000, 0x100)).ToArray();

        Assert.AreEqual(ResourceParseError.CountExceedsData, CmResourceParser.ParseFull(data).Error);
    }

    [TestMethod]
    public void Resource_TrailingBytes_AreReportedNotShown()
    {
        byte[] data = new ResourceBytes().U32(1).U32(0).U32(0).U16(0).U16(0).U32(0).Zeros(5).ToArray();

        ResourceParseResult<CmResourceList> result = CmResourceParser.ParseList(data);

        Assert.AreEqual(ResourceParseError.None, result.Error);
        Assert.AreEqual(1, result.Value!.Full.Count);
        Assert.AreEqual(5, result.TrailingBytes);
    }

    [TestMethod]
    public void Resource_UnknownPartialType_StopsWithOffset()
    {
        byte[] data = new ResourceBytes().U32(1).U32(0).U32(0).U16(0).U16(0).U32(1).Partial(0x42, 0, 0, new byte[16]).ToArray();

        ResourceParseResult<CmResourceList> result = CmResourceParser.ParseList(data);

        Assert.AreEqual(ResourceParseError.UnknownDescriptorType, result.Error);
        Assert.AreEqual(4 + 16, result.Offset);
    }

    [TestMethod]
    public void Resource_DeviceDataOverrun_IsRejected()
    {
        byte[] data = new ResourceBytes().U32(0).U32(0).U16(0).U16(0).U32(1).Partial(5, 0, 0, U32Union(100)).Zeros(4).ToArray();

        Assert.AreEqual(ResourceParseError.Truncated, CmResourceParser.ParseFull(data).Error);
    }

    [TestMethod]
    public void Resource_DeviceDataSizeWraps_IsRejected()
    {
        byte[] data = new ResourceBytes().U32(0).U32(0).U16(0).U16(0).U32(1).Partial(5, 0, 0, U32Union(0xFFFFFFFF)).Zeros(8).ToArray();

        Assert.AreEqual(ResourceParseError.Truncated, CmResourceParser.ParseFull(data).Error);
    }

    [TestMethod]
    public void Resource_UnknownInterfaceType_KeepsRawNumber()
    {
        byte[] data = new ResourceBytes().U32(1).U32(0x77).U32(0).U16(0).U16(0).U32(0).ToArray();

        ResourceParseResult<CmResourceList> result = CmResourceParser.ParseList(data);

        Assert.AreEqual(ResourceParseError.None, result.Error);
        Assert.AreEqual(0x77u, result.Value!.Full[0].InterfaceType);
    }

    [TestMethod]
    public void Resource_UnknownFlagBits_AreKept()
    {
        byte[] data = new ResourceBytes().U32(0).U32(0).U16(0).U16(0).U32(1).Partial(3, 0, 0x8001, Memory(0x1000, 0x100)).ToArray();

        ResourceParseResult<CmFullResourceDescriptor> result = CmResourceParser.ParseFull(data);

        Assert.AreEqual(ResourceParseError.None, result.Error);
        Assert.AreEqual((ushort)0x8001, result.Value!.Partials[0].Flags);
    }

    [TestMethod]
    public void Resource_LargeMemoryUnknownLength_IsRejected()
    {
        foreach (ushort flags in new ushort[] { 0x0000, 0x0600 })
        {
            byte[] data = new ResourceBytes().U32(0).U32(0).U16(0).U16(0).U32(1).Partial(7, 0, flags, new byte[16]).ToArray();

            Assert.AreEqual(ResourceParseError.UnknownDescriptorType, CmResourceParser.ParseFull(data).Error, $"flags 0x{flags:X4}");
        }
    }

    [TestMethod]
    public void Requirements_ListSizeTooLarge_IsRejected()
    {
        byte[] data = RequirementsHeader(listSize: 0x100, alternatives: 0).ToArray();

        Assert.AreEqual(ResourceParseError.SizeMismatch, IoRequirementsParser.Parse(data).Error);
    }

    [TestMethod]
    public void Requirements_ForgedAlternativeCount_IsRejected()
    {
        byte[] data = RequirementsHeader(listSize: 32, alternatives: 0xFFFFFFF0).ToArray();

        Assert.AreEqual(ResourceParseError.CountExceedsData, IoRequirementsParser.Parse(data).Error);
    }

    [TestMethod]
    public void Requirements_ListCountOverrun_IsRejected()
    {
        byte[] list = new ResourceBytes().U16(1).U16(0).U32(5).Zeros(32).ToArray();
        byte[] data = RequirementsHeader(listSize: (uint)(32 + list.Length), alternatives: 1).Raw(list).ToArray();

        Assert.AreEqual(ResourceParseError.CountExceedsData, IoRequirementsParser.Parse(data).Error);
    }

    [TestMethod]
    public void Requirements_UnknownOption_IsKept()
    {
        byte[] descriptor = new ResourceBytes().U8(0x7F).U8(3).U8(0).U8(0).U16(0).U16(0).U32(0x10).U32(0x4).U64(0x1000).U64(0x1FFF).ToArray();
        byte[] list = new ResourceBytes().U16(1).U16(0).U32(1).Raw(descriptor).ToArray();
        byte[] data = RequirementsHeader((uint)(32 + list.Length), 1).Raw(list).ToArray();

        ResourceParseResult<IoResourceRequirementsList> result = IoRequirementsParser.Parse(data);

        Assert.AreEqual(ResourceParseError.None, result.Error);
        Assert.AreEqual((byte)0x7F, result.Value!.Lists[0].Descriptors[0].Option);
        Assert.AreEqual(0x1000UL, result.Value.Lists[0].Descriptors[0].Minimum);
    }

    [TestMethod]
    public void Resource_RealHardwareBytes_MatchRecordedRows()
    {
        ResourceParseResult<CmResourceList> list = CmResourceParser.ParseList(FromHex(PhysicalMemoryHex));

        Assert.AreEqual(ResourceParseError.None, list.Error);
        Assert.AreEqual(0, list.TrailingBytes);
        CmFullResourceDescriptor full = list.Value!.Full[0];
        CollectionAssert.AreEqual(new byte[] { 3, 7, 3, 3, 3 }, full.Partials.Select(p => p.Type).ToArray());
        Assert.AreEqual(0x40000000UL, full.Partials[0].Start);
        Assert.AreEqual(0xBFFF0000UL, full.Partials[0].Length);
        Assert.AreEqual(0x100000000UL, full.Partials[1].Start);
        Assert.AreEqual(0x33BE00000UL, full.Partials[1].Length);
        Assert.AreEqual(0x43C000000UL, full.Partials[2].Start);
        Assert.AreEqual(0x20000UL, full.Partials[2].Length);
        Assert.AreEqual(0x43F930000UL, full.Partials[4].Start);

        ResourceParseResult<CmFullResourceDescriptor> config = CmResourceParser.ParseFull(FromHex(ConfigurationDataHex));

        Assert.AreEqual(ResourceParseError.None, config.Error);
        Assert.AreEqual(0, config.TrailingBytes);
        Assert.AreEqual(0xFFFFFFFFu, config.Value!.InterfaceType);
        CollectionAssert.AreEqual(new byte[] { 5, 0 }, config.Value.Partials.Select(p => p.Type).ToArray());
        Assert.AreEqual(24, config.Value.Partials[0].DeviceData!.Length);
    }

    [TestMethod]
    public void Resource_RandomBytes_NeverThrow()
    {
        var random = new Random(20260601);
        for (int i = 0; i < 20000; i++)
        {
            byte[] data = new byte[random.Next(0, 257)];
            random.NextBytes(data);

            _ = CmResourceParser.ParseList(data);
            _ = CmResourceParser.ParseFull(data);
            _ = IoRequirementsParser.Parse(data);
        }
    }

    private static byte[] FromHex(string hex)
    {
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        return bytes;
    }

    private static byte[] Memory(ulong start, uint length) => new ResourceBytes().U64(start).U32(length).Zeros(4).ToArray();

    private static byte[] U32Union(uint value) => new ResourceBytes().U32(value).Zeros(12).ToArray();

    private static ResourceBytes RequirementsHeader(uint listSize, uint alternatives) =>
        new ResourceBytes().U32(listSize).U32(0).U32(0).U32(0).Zeros(12).U32(alternatives);

    // Little-endian byte writer for crafted layouts. Each method appends; ToArray returns the result.
    private sealed class ResourceBytes
    {
        private readonly List<byte> _data = new();

        public ResourceBytes U8(byte value) { _data.Add(value); return this; }

        public ResourceBytes U16(ushort value) { _data.AddRange(BitConverter.GetBytes(value)); return this; }

        public ResourceBytes U32(uint value) { _data.AddRange(BitConverter.GetBytes(value)); return this; }

        public ResourceBytes U64(ulong value) { _data.AddRange(BitConverter.GetBytes(value)); return this; }

        public ResourceBytes Zeros(int count) { _data.AddRange(new byte[count]); return this; }

        public ResourceBytes Raw(byte[] data) { _data.AddRange(data); return this; }

        // One CM_PARTIAL_RESOURCE_DESCRIPTOR: type, share disposition, flags, then the 16-byte union.
        public ResourceBytes Partial(byte type, byte share, ushort flags, byte[] union)
        {
            U8(type).U8(share).U16(flags);
            return Raw(union.Length == 16 ? union : union.Concat(new byte[16 - union.Length]).ToArray());
        }

        public byte[] ToArray() => _data.ToArray();
    }
}

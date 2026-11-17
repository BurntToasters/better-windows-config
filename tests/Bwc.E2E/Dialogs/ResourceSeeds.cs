namespace Bwc.E2E.Dialogs;

/// <summary>
/// Little-endian bytes for the seeded hardware resource values, in the 64-bit kernel layouts that
/// src/Bwc.Registry/Resources parses: CM_PARTIAL_RESOURCE_DESCRIPTOR (20 bytes), the full descriptor
/// header, and IO_RESOURCE_DESCRIPTOR (32 bytes). The viewer and the export checks share these builders.
/// </summary>
internal static class ResourceSeeds
{
    internal static Builder Bytes() => new();

    // Memory and port: a 64-bit start and a 32-bit length, then four spare bytes.
    internal static byte[] Memory(ulong start, uint length) => Bytes().U64(start).U32(length).Zeros(4).ToArray();

    // Interrupt: level, vector, and the 64-bit affinity.
    internal static byte[] Interrupt(uint level, uint vector, ulong affinity) =>
        Bytes().U32(level).U32(vector).U64(affinity).ToArray();

    // Device specific: the data size, two reserved words, then four spare bytes.
    internal static byte[] Union(uint dataSize, uint reserved1, uint reserved2) =>
        Bytes().U32(dataSize).U32(reserved1).U32(reserved2).Zeros(4).ToArray();

    // DMA: the channel, the port, and the reserved word, then four spare bytes.
    internal static byte[] Dma(uint channel, uint port, uint reserved1) =>
        Bytes().U32(channel).U32(port).U32(reserved1).Zeros(4).ToArray();

    // An IO_RESOURCE_DESCRIPTOR: option, type, share, a spare byte, flags, a spare word, then the 24-byte union.
    internal static byte[] Requirement(byte option, byte type, byte share, ushort flags, Builder union) =>
        Bytes().U8(option).U8(type).U8(share).U8(0).U16(flags).U16(0).Raw(union.ToArray()).ToArray();

    // Little-endian builder for the seeded layouts. The partial descriptor is 20 bytes: type, share,
    // flags, 16-byte union.
    internal sealed class Builder
    {
        private readonly List<byte> _data = [];

        public Builder U8(byte value) { _data.Add(value); return this; }

        public Builder U16(ushort value) { _data.AddRange(BitConverter.GetBytes(value)); return this; }

        public Builder U32(uint value) { _data.AddRange(BitConverter.GetBytes(value)); return this; }

        public Builder U64(ulong value) { _data.AddRange(BitConverter.GetBytes(value)); return this; }

        public Builder Zeros(int count) { _data.AddRange(new byte[count]); return this; }

        public Builder Raw(byte[] data) { _data.AddRange(data); return this; }

        public Builder Partial(byte type, byte share, ushort flags, byte[] union)
        {
            U8(type).U8(share).U16(flags);
            return Raw(union.Length == 16 ? union : union.Concat(new byte[16 - union.Length]).ToArray());
        }

        public byte[] ToArray() => _data.ToArray();
    }
}

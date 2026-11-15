using System.Buffers.Binary;

namespace Bwc.Registry.Resources;

/// <summary>
/// Parses REG_RESOURCE_LIST (8) and REG_FULL_RESOURCE_DESCRIPTOR (9) bytes in the 64-bit kernel layout. Never throws
/// for input data: every count and size is checked against the bytes that remain before anything is allocated.
/// </summary>
public static class CmResourceParser
{
    private const int ListCountSize = 4;
    private const int FullHeaderSize = 16;
    private const int PartialSize = 20;
    private const ushort LargeMemory40 = 0x0200;
    private const ushort LargeMemory48 = 0x0400;
    private const ushort LargeMemory64 = 0x0800;
    private const ushort LargeMemoryMask = LargeMemory40 | LargeMemory48 | LargeMemory64;

    public static ResourceParseResult<CmResourceList> ParseList(ReadOnlySpan<byte> data)
    {
        if (data.Length < ListCountSize)
        {
            return ResourceParseResult<CmResourceList>.Fail(ResourceParseError.Truncated, 0);
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (count > (data.Length - ListCountSize) / FullHeaderSize)
        {
            return ResourceParseResult<CmResourceList>.Fail(ResourceParseError.CountExceedsData, 0);
        }

        var full = new List<CmFullResourceDescriptor>((int)count);
        int position = ListCountSize;
        for (uint i = 0; i < count; i++)
        {
            ResourceParseError error = ReadFull(data, position, out CmFullResourceDescriptor? descriptor, out int end, out int errorOffset);
            if (error != ResourceParseError.None)
            {
                return ResourceParseResult<CmResourceList>.FailWithPartial(
                    error,
                    errorOffset,
                    new CmResourceList(full));
            }

            full.Add(descriptor!);
            position = end;
        }

        return ResourceParseResult<CmResourceList>.Ok(new CmResourceList(full), data.Length - position);
    }

    public static ResourceParseResult<CmFullResourceDescriptor> ParseFull(ReadOnlySpan<byte> data)
    {
        ResourceParseError error = ReadFull(data, 0, out CmFullResourceDescriptor? descriptor, out int end, out int errorOffset);
        return error != ResourceParseError.None
            ? ResourceParseResult<CmFullResourceDescriptor>.Fail(error, errorOffset)
            : ResourceParseResult<CmFullResourceDescriptor>.Ok(descriptor!, data.Length - end);
    }

    private static ResourceParseError ReadFull(
        ReadOnlySpan<byte> data,
        int start,
        out CmFullResourceDescriptor? descriptor,
        out int end,
        out int errorOffset)
    {
        descriptor = null;
        end = start;
        errorOffset = start;
        if (data.Length - start < FullHeaderSize)
        {
            return ResourceParseError.Truncated;
        }

        uint interfaceType = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(start));
        uint busNumber = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(start + 4));
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(start + 8));
        ushort revision = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(start + 10));
        uint partialCount = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(start + 12));

        int partialStart = start + FullHeaderSize;
        if (partialCount > (data.Length - partialStart) / PartialSize)
        {
            errorOffset = start + 12;
            return ResourceParseError.CountExceedsData;
        }

        var partials = new List<CmPartialResourceDescriptor>((int)partialCount);
        int position = partialStart;
        for (uint i = 0; i < partialCount; i++)
        {
            ResourceParseError error = ReadPartial(data, position, out CmPartialResourceDescriptor? partial);
            if (error != ResourceParseError.None)
            {
                errorOffset = position;
                return error;
            }

            partials.Add(partial!);
            position += PartialSize;
        }

        // Device specific data follows the whole partial list, in descriptor order.
        for (int i = 0; i < partials.Count; i++)
        {
            if (partials[i].Type != CmResourceTypes.DeviceSpecific)
            {
                continue;
            }

            long size = partials[i].DataSize;
            if (size > data.Length - position)
            {
                errorOffset = position;
                return ResourceParseError.Truncated;
            }

            byte[] bytes = data.Slice(position, (int)size).ToArray();
            partials[i] = partials[i] with { DeviceData = bytes };
            position += (int)size;
        }

        descriptor = new CmFullResourceDescriptor(interfaceType, busNumber, version, revision, partials);
        end = position;
        return ResourceParseError.None;
    }

    private static ResourceParseError ReadPartial(ReadOnlySpan<byte> data, int start, out CmPartialResourceDescriptor? partial)
    {
        partial = null;
        if (data.Length - start < PartialSize)
        {
            return ResourceParseError.Truncated;
        }

        ReadOnlySpan<byte> union = data.Slice(start + 4, 16);
        byte type = data[start];
        byte share = data[start + 1];
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(start + 2));
        ulong startAddress = 0;
        ulong length = 0;
        uint level = 0;
        uint vector = 0;
        ulong affinity = 0;
        uint channel = 0;
        uint port = 0;
        uint reserved1 = 0;
        uint reserved2 = 0;
        uint dataSize = 0;

        switch (type)
        {
            case CmResourceTypes.Null:
                break;
            case CmResourceTypes.Port:
            case CmResourceTypes.Memory:
                startAddress = BinaryPrimitives.ReadUInt64LittleEndian(union);
                length = BinaryPrimitives.ReadUInt32LittleEndian(union.Slice(8));
                break;
            case CmResourceTypes.Interrupt:
                level = BinaryPrimitives.ReadUInt32LittleEndian(union);
                vector = BinaryPrimitives.ReadUInt32LittleEndian(union.Slice(4));
                affinity = BinaryPrimitives.ReadUInt64LittleEndian(union.Slice(8));
                break;
            case CmResourceTypes.Dma:
                channel = BinaryPrimitives.ReadUInt32LittleEndian(union);
                port = BinaryPrimitives.ReadUInt32LittleEndian(union.Slice(4));
                reserved1 = BinaryPrimitives.ReadUInt32LittleEndian(union.Slice(8));
                break;
            case CmResourceTypes.DeviceSpecific:
                dataSize = BinaryPrimitives.ReadUInt32LittleEndian(union);
                reserved1 = BinaryPrimitives.ReadUInt32LittleEndian(union.Slice(4));
                reserved2 = BinaryPrimitives.ReadUInt32LittleEndian(union.Slice(8));
                break;
            case CmResourceTypes.BusNumber:
                startAddress = BinaryPrimitives.ReadUInt32LittleEndian(union);
                length = BinaryPrimitives.ReadUInt32LittleEndian(union.Slice(4));
                break;
            case CmResourceTypes.MemoryLarge:
            {
                // The length field is in 256, 64 KiB, or 4 GiB units, selected by the one large-memory flag bit.
                ushort size = (ushort)(flags & LargeMemoryMask);
                uint field = BinaryPrimitives.ReadUInt32LittleEndian(union.Slice(8));
                startAddress = BinaryPrimitives.ReadUInt64LittleEndian(union);
                length = size switch
                {
                    LargeMemory40 => (ulong)field << 8,
                    LargeMemory48 => (ulong)field << 16,
                    LargeMemory64 => (ulong)field << 32,
                    _ => ulong.MaxValue,
                };
                if (length == ulong.MaxValue)
                {
                    return ResourceParseError.UnknownDescriptorType;
                }

                break;
            }
            default:
                return ResourceParseError.UnknownDescriptorType;
        }

        partial = new CmPartialResourceDescriptor(
            type,
            share,
            flags,
            startAddress,
            length,
            level,
            vector,
            affinity,
            channel,
            port,
            reserved1,
            reserved2,
            dataSize,
            null);
        return ResourceParseError.None;
    }
}

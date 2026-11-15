using System.Buffers.Binary;

namespace Bwc.Registry.Resources;

/// <summary>
/// Parses REG_RESOURCE_REQUIREMENTS_LIST (10): an IO_RESOURCE_REQUIREMENTS_LIST header, then its alternative lists.
/// The header is 32 bytes, each IO_RESOURCE_LIST header is 8 bytes, and each IO_RESOURCE_DESCRIPTOR is 32 bytes.
/// ListSize must describe the whole structure exactly. Never throws for input data.
/// </summary>
public static class IoRequirementsParser
{
    private const int HeaderSize = 32;
    private const int ListHeaderSize = 8;
    private const int DescriptorSize = 32;
    private const int AlternativeCountOffset = 28;

    public static ResourceParseResult<IoResourceRequirementsList> Parse(ReadOnlySpan<byte> data)
    {
        var parsed = new List<IoResourceList>();
        ResourceParseResult<IoResourceRequirementsList> result = ParseCore(data, parsed);
        if (!result.Succeeded && data.Length >= HeaderSize)
        {
            return ResourceParseResult<IoResourceRequirementsList>.FailWithPartial(
                result.Error,
                result.Offset,
                new IoResourceRequirementsList(
                    data.Length >= HeaderSize ? BinaryPrimitives.ReadUInt32LittleEndian(data) : 0,
                    data.Length >= HeaderSize ? BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4)) : 0,
                    data.Length >= HeaderSize ? BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8)) : 0,
                    data.Length >= HeaderSize ? BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12)) : 0,
                    parsed));
        }

        return result;
    }

    private static ResourceParseResult<IoResourceRequirementsList> ParseCore(ReadOnlySpan<byte> data, List<IoResourceList> parsed)
    {
        if (data.Length < HeaderSize)
        {
            return ResourceParseResult<IoResourceRequirementsList>.Fail(ResourceParseError.Truncated, 0);
        }

        uint listSize = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (listSize < HeaderSize || listSize > data.Length)
        {
            return ResourceParseResult<IoResourceRequirementsList>.Fail(ResourceParseError.SizeMismatch, 0);
        }

        uint interfaceType = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4));
        uint busNumber = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8));
        uint slotNumber = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12));
        uint alternativeLists = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(AlternativeCountOffset));

        // Each alternative list takes at least its 8-byte header, so the count is checked against the bytes after it.
        if (alternativeLists > (listSize - HeaderSize) / ListHeaderSize)
        {
            return ResourceParseResult<IoResourceRequirementsList>.Fail(ResourceParseError.CountExceedsData, AlternativeCountOffset);
        }

        var lists = new List<IoResourceList>((int)alternativeLists);
        int position = HeaderSize;
        for (uint i = 0; i < alternativeLists; i++)
        {
            if (listSize - position < ListHeaderSize)
            {
                return ResourceParseResult<IoResourceRequirementsList>.Fail(ResourceParseError.Truncated, position);
            }

            ushort version = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(position));
            ushort revision = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(position + 2));
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(position + 4));
            int first = position + ListHeaderSize;
            if (count > (listSize - first) / DescriptorSize)
            {
                return ResourceParseResult<IoResourceRequirementsList>.Fail(ResourceParseError.CountExceedsData, position + 4);
            }

            var descriptors = new List<IoResourceDescriptor>((int)count);
            for (uint j = 0; j < count; j++)
            {
                int at = first + (int)(j * DescriptorSize);
                descriptors.Add(new IoResourceDescriptor(
                    data[at],
                    data[at + 1],
                    data[at + 2],
                    BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(at + 4)),
                    data.Slice(at + 8, 24).ToArray()));
            }

            lists.Add(new IoResourceList(version, revision, descriptors));
            parsed.Add(lists[^1]);
            position = first + (int)count * DescriptorSize;
        }

        // The declared size must end exactly where the last alternative list ends.
        if (position != listSize)
        {
            return ResourceParseResult<IoResourceRequirementsList>.Fail(ResourceParseError.SizeMismatch, position);
        }

        return ResourceParseResult<IoResourceRequirementsList>.Ok(
            new IoResourceRequirementsList(listSize, interfaceType, busNumber, slotNumber, lists),
            data.Length - (int)listSize);
    }
}

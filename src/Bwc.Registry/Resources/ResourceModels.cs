using System.Buffers.Binary;

namespace Bwc.Registry.Resources;

/// <summary>Why a hardware resource value could not be parsed. Each kind is listed in resource-values.md.</summary>
public enum ResourceParseError
{
    None = 0,
    Truncated = 1,
    CountExceedsData = 2,
    UnknownDescriptorType = 3,
    SizeMismatch = 4,
}

/// <summary>
/// The outcome of a parse. A failed parse has no value; <see cref="Offset"/> is where the parser stopped.
/// <see cref="TrailingBytes"/> counts the bytes after the last declared structure.
/// </summary>
public sealed class ResourceParseResult<T> where T : class
{
    private ResourceParseResult(ResourceParseError error, int offset, int trailingBytes, T? value, T? partialValue)
    {
        Error = error;
        Offset = offset;
        TrailingBytes = trailingBytes;
        Value = value;
        PartialValue = partialValue;
    }

    public ResourceParseError Error { get; }

    public int Offset { get; }

    public int TrailingBytes { get; }

    public T? Value { get; }

    /// <summary>On a failed parse, the structures that parsed before the failure. Null when nothing parsed.</summary>
    public T? PartialValue { get; }

    public bool Succeeded => Error == ResourceParseError.None;

    internal static ResourceParseResult<T> Ok(T value, int trailingBytes) =>
        new(ResourceParseError.None, 0, trailingBytes, value, null);

    internal static ResourceParseResult<T> Fail(ResourceParseError error, int offset) =>
        new(error, offset, 0, null, null);

    internal static ResourceParseResult<T> FailWithPartial(ResourceParseError error, int offset, T partial) =>
        new(error, offset, 0, null, partial);
}

/// <summary>REG_RESOURCE_LIST: a count followed by full resource descriptors.</summary>
public sealed record CmResourceList(IReadOnlyList<CmFullResourceDescriptor> Full);

/// <summary>CM_FULL_RESOURCE_DESCRIPTOR. The interface type and bus number are kept as raw numbers.</summary>
public sealed record CmFullResourceDescriptor(
    uint InterfaceType,
    uint BusNumber,
    ushort Version,
    ushort Revision,
    IReadOnlyList<CmPartialResourceDescriptor> Partials);

/// <summary>
/// CM_PARTIAL_RESOURCE_DESCRIPTOR with the union decoded for its type. Fields that the type does not use are
/// zero. <see cref="DeviceData"/> is set only for device specific descriptors and holds the bytes that follow the list.
/// </summary>
public sealed record CmPartialResourceDescriptor(
    byte Type,
    byte ShareDisposition,
    ushort Flags,
    ulong Start,
    ulong Length,
    uint Level,
    uint Vector,
    ulong Affinity,
    uint Channel,
    uint Port,
    uint Reserved1,
    uint Reserved2,
    uint DataSize,
    byte[]? DeviceData);

/// <summary>REG_RESOURCE_REQUIREMENTS_LIST: IO_RESOURCE_REQUIREMENTS_LIST with its alternative lists.</summary>
public sealed record IoResourceRequirementsList(
    uint ListSize,
    uint InterfaceType,
    uint BusNumber,
    uint SlotNumber,
    IReadOnlyList<IoResourceList> Lists);

/// <summary>IO_RESOURCE_LIST: one alternative set of requirement descriptors.</summary>
public sealed record IoResourceList(ushort Version, ushort Revision, IReadOnlyList<IoResourceDescriptor> Descriptors);

/// <summary>
/// IO_RESOURCE_DESCRIPTOR. The 24-byte union is kept as bytes and read through the accessors for each type, so an
/// unknown type keeps its bytes and the 32-byte stride stays fixed.
/// </summary>
public sealed record IoResourceDescriptor(byte Option, byte Type, byte ShareDisposition, ushort Flags, byte[] Union)
{
    /// <summary>Length of a port, memory, or bus number range.</summary>
    public uint Length => BinaryPrimitives.ReadUInt32LittleEndian(Union.AsSpan(0));

    public uint Alignment => BinaryPrimitives.ReadUInt32LittleEndian(Union.AsSpan(4));

    public ulong Minimum => BinaryPrimitives.ReadUInt64LittleEndian(Union.AsSpan(8));

    public ulong Maximum => BinaryPrimitives.ReadUInt64LittleEndian(Union.AsSpan(16));

    public uint MinimumVector => BinaryPrimitives.ReadUInt32LittleEndian(Union.AsSpan(0));

    public uint MaximumVector => BinaryPrimitives.ReadUInt32LittleEndian(Union.AsSpan(4));

    public uint MinimumBus => BinaryPrimitives.ReadUInt32LittleEndian(Union.AsSpan(4));

    public uint MaximumBus => BinaryPrimitives.ReadUInt32LittleEndian(Union.AsSpan(8));

    /// <summary>DMA: the first channel of the range (union offset 0).</summary>
    public uint MinimumChannel => BinaryPrimitives.ReadUInt32LittleEndian(Union.AsSpan(0));

    /// <summary>DMA: the last channel of the range (union offset 4).</summary>
    public uint MaximumChannel => BinaryPrimitives.ReadUInt32LittleEndian(Union.AsSpan(4));
}

/// <summary>Partial descriptor type numbers (CmResourceType*) that the parser understands.</summary>
public static class CmResourceTypes
{
    public const byte Null = 0;
    public const byte Port = 1;
    public const byte Interrupt = 2;
    public const byte Memory = 3;
    public const byte Dma = 4;
    public const byte DeviceSpecific = 5;
    public const byte BusNumber = 6;
    public const byte MemoryLarge = 7;
}

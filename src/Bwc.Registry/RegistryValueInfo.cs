using System.Diagnostics.CodeAnalysis;

namespace Bwc.Registry;

/// <summary>
/// A registry value read as raw bytes plus the raw type. Nothing is normalized.
/// </summary>
public sealed class RegistryValueInfo
{
    public RegistryValueInfo(string name, uint type, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(data);

        Name = name;
        Type = type;
        Data = data;
        DataLength = data.Length;
    }

    public string Name { get; }

    public uint Type { get; }

    [SuppressMessage(
        "Performance",
        "CA1819:Properties should not return arrays",
        Justification = "The raw bytes are the contract for a lossless registry read.")]
    public byte[] Data { get; }

    public int DataLength { get; }

    /// <summary>
    /// The raw type cast to the enum. Unknown types keep their raw numeric value.
    /// </summary>
    public RegistryValueType TypeEnum => (RegistryValueType)Type;
}

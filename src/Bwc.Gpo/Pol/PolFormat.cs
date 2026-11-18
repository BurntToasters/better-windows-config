namespace Bwc.Gpo.Pol;

/// <summary>The fixed byte-level constants of the Registry.pol format (docs/contracts/policy-engine.md).</summary>
internal static class PolFormat
{
    /// <summary>Signature <c>PReg</c> followed by version 1, little-endian.</summary>
    public static ReadOnlySpan<byte> Header => [0x50, 0x52, 0x65, 0x67, 0x01, 0x00, 0x00, 0x00];

    public const int HeaderLength = 8;
    public const int SignatureLength = 4;
    public const char RecordStart = '[';
    public const char RecordEnd = ']';
    public const char FieldSeparator = ';';

    /// <summary>Prefix of a value name that makes a record an instruction instead of a value.</summary>
    public const string InstructionPrefix = "**";

    /// <summary>Value-name prefix of a delete instruction.</summary>
    public const string DeletePrefix = "**del.";

    /// <summary>The data of a delete instruction: the UTF-16LE string " " with its terminator (REG_SZ, size 4).</summary>
    public static ReadOnlySpan<byte> DeleteInstructionData => [0x20, 0x00, 0x00, 0x00];
}

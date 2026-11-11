using System.Text;

namespace Bwc.Registry.Tests;

/// <summary>
/// Builds minimal registry hive files for the restore and replace guard tests. The files are not
/// meant for RegRestoreKey to accept: the restore tests need only the sandbox's structural check to
/// see the file, and the non-elevated test process never reaches the native restore. Layout follows
/// the regf format: a 4096-byte base block, one hbin with a root key nk, a subkey list lf, and one
/// child nk. Offsets are relative to the start of the hive bins, as in the format.
/// </summary>
internal static class HiveFileFixture
{
    internal const int BaseBlockSize = 0x1000;
    internal const int BinSize = 0x1000;
    internal const uint KeySymbolicLinkFlag = 0x0010;
    internal const uint KeyCompressedNameFlag = 0x0020;

    private const int RootCellOffset = 0x20;
    private const int RootCellSize = 0x58;
    private const int ListCellOffset = RootCellOffset + RootCellSize;
    private const int ListCellSize = 0x10;
    private const int ChildCellOffset = ListCellOffset + ListCellSize;
    private const int ChildCellSize = 0x58;
    private const uint NoOffset = 0xFFFFFFFF;

    /// <summary>
    /// Builds a hive whose child key is a symbolic link when <paramref name="includeLinkChild"/> is
    /// true and an ordinary key otherwise.
    /// </summary>
    internal static byte[] BuildHive(bool includeLinkChild)
    {
        byte[] bytes = new byte[BaseBlockSize + BinSize];

        WriteAscii(bytes, 0, "regf");
        WriteUInt32(bytes, 0x24, RootCellOffset);
        WriteUInt32(bytes, 0x28, BinSize);

        WriteAscii(bytes, BaseBlockSize, "hbin");
        WriteUInt32(bytes, BaseBlockSize + 4, 0);
        WriteUInt32(bytes, BaseBlockSize + 8, BinSize);

        WriteNamedKey(bytes, RootCellOffset, RootCellSize, "Root", KeyCompressedNameFlag, 1, ListCellOffset);

        WriteCellHeader(bytes, ListCellOffset, ListCellSize);
        int list = BaseBlockSize + ListCellOffset + 4;
        WriteAscii(bytes, list, "lf");
        WriteUInt16(bytes, list + 2, 1);
        WriteUInt32(bytes, list + 4, ChildCellOffset);
        WriteUInt32(bytes, list + 8, 0);

        uint childFlags = KeyCompressedNameFlag | (includeLinkChild ? KeySymbolicLinkFlag : 0);
        WriteNamedKey(bytes, ChildCellOffset, ChildCellSize, "Child", childFlags, subkeyCount: 0, NoOffset);

        return bytes;
    }

    /// <summary>Builds a hive whose header claims more hive bin data than the file contains.</summary>
    internal static byte[] BuildTruncatedHive()
    {
        byte[] bytes = BuildHive(includeLinkChild: false);
        WriteUInt32(bytes, 0x28, BinSize * 3);
        return bytes;
    }

    private static void WriteNamedKey(
        byte[] bytes,
        int cellOffset,
        int cellSize,
        string name,
        uint flags,
        int subkeyCount,
        uint subkeyList)
    {
        WriteCellHeader(bytes, cellOffset, cellSize);
        int data = BaseBlockSize + cellOffset + 4;

        WriteAscii(bytes, data, "nk");
        WriteUInt16(bytes, data + 2, (ushort)flags);
        WriteUInt32(bytes, data + 0x10, NoOffset);
        WriteUInt32(bytes, data + 0x14, (uint)subkeyCount);
        WriteUInt32(bytes, data + 0x18, 0);
        WriteUInt32(bytes, data + 0x1C, subkeyList);
        WriteUInt32(bytes, data + 0x20, NoOffset);
        WriteUInt32(bytes, data + 0x24, 0);
        WriteUInt32(bytes, data + 0x28, NoOffset);
        WriteUInt32(bytes, data + 0x2C, NoOffset);
        WriteUInt32(bytes, data + 0x30, NoOffset);
        WriteUInt16(bytes, data + 0x48, (ushort)name.Length);
        WriteAscii(bytes, data + 0x4C, name);
    }

    private static void WriteCellHeader(byte[] bytes, int cellOffset, int allocatedSize)
    {
        WriteInt32(bytes, BaseBlockSize + cellOffset, -allocatedSize);
    }

    private static void WriteAscii(byte[] bytes, int offset, string text)
    {
        Encoding.ASCII.GetBytes(text, 0, text.Length, bytes, offset);
    }

    private static void WriteUInt16(byte[] bytes, int offset, ushort value)
    {
        BitConverter.TryWriteBytes(bytes.AsSpan(offset, 2), value);
    }

    private static void WriteUInt32(byte[] bytes, int offset, uint value)
    {
        BitConverter.TryWriteBytes(bytes.AsSpan(offset, 4), value);
    }

    private static void WriteInt32(byte[] bytes, int offset, int value)
    {
        BitConverter.TryWriteBytes(bytes.AsSpan(offset, 4), value);
    }
}

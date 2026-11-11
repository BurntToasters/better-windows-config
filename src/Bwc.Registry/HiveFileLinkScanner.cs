using System.Buffers.Binary;
using System.Text;

namespace Bwc.Registry;

/// <summary>
/// Walks the key tree of a regf hive file without loading it and reports whether any reachable key
/// node has the KEY_SYM_LINK flag. The walk follows subkey lists from the root key only. Every cell
/// is checked against its hive bin before it is read. Any structure that cannot be validated is
/// reported as unverifiable, so the caller fails closed.
/// </summary>
internal static class HiveFileLinkScanner
{
    private const int BaseBlockSize = 0x1000;
    private const int BinAlignment = 0x1000;
    private const int BinHeaderSize = 0x20;
    private const int MinimumCellSize = 8;
    private const int MaxKeyDepth = 512;
    private const int KeyNameOffset = 0x4C;
    private const ushort KeySymbolicLinkFlag = 0x0010;
    private const ushort KeyCompressedNameFlag = 0x0020;

    /// <summary>Scans <paramref name="fullPath"/>. Never throws for a malformed file.</summary>
    internal static HiveScanResult Scan(string fullPath)
    {
        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new Walker(stream).Run();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or ArgumentException or NotSupportedException)
        {
            return HiveScanResult.Unverifiable(ex.Message);
        }
    }

    private sealed class Walker
    {
        private readonly FileStream _stream;
        private readonly long _length;
        private readonly uint _binsSize;
        private readonly uint _rootOffset;
        private readonly HashSet<uint> _visited = new();
        private string _linkedKeyPath = string.Empty;

        public Walker(FileStream stream)
        {
            _stream = stream;
            _length = stream.Length;

            byte[] header = ReadBytes(0, BaseBlockSize);
            if (!HasSignature(header, 0, "regf"))
            {
                throw new InvalidDataException("the file does not start with a regf header");
            }

            _binsSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x28));
            if (_binsSize == 0 || _binsSize % BinAlignment != 0)
            {
                throw new InvalidDataException("the hive bin data size is not a positive multiple of 4096");
            }

            if (BaseBlockSize + (long)_binsSize > _length)
            {
                throw new InvalidDataException("the header claims more hive bin data than the file contains");
            }

            _rootOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x24));
        }

        public HiveScanResult Run()
        {
            return VisitKey(_rootOffset, string.Empty, depth: 0)
                ? HiveScanResult.Linked(_linkedKeyPath)
                : HiveScanResult.Clean;
        }

        /// <returns>True when a link node was found; its path is in <see cref="_linkedKeyPath"/>.</returns>
        private bool VisitKey(uint relativeOffset, string parentPath, int depth)
        {
            if (depth > MaxKeyDepth)
            {
                throw new InvalidDataException("the key tree is nested more than 512 levels deep");
            }

            if (!_visited.Add(relativeOffset))
            {
                throw new InvalidDataException("a key node is reachable twice");
            }

            (long dataOffset, int dataLength) = ResolveCell(relativeOffset);
            if (dataLength < KeyNameOffset)
            {
                throw new InvalidDataException("a key node is smaller than its fixed fields");
            }

            byte[] node = ReadBytes(dataOffset, dataLength);
            if (!HasSignature(node, 0, "nk"))
            {
                throw new InvalidDataException("a key offset does not point at a key node");
            }

            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(2));
            ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(0x48));
            if (KeyNameOffset + nameLength > dataLength)
            {
                throw new InvalidDataException("a key name extends past its cell");
            }

            string name = (flags & KeyCompressedNameFlag) != 0
                ? Encoding.Latin1.GetString(node, KeyNameOffset, nameLength)
                : Encoding.Unicode.GetString(node, KeyNameOffset, nameLength);
            string path = parentPath.Length == 0 ? name : parentPath + @"\" + name;

            if ((flags & KeySymbolicLinkFlag) != 0)
            {
                _linkedKeyPath = path;
                return true;
            }

            uint subkeyCount = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(0x14));
            if (subkeyCount == 0)
            {
                return false;
            }

            uint subkeyList = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(0x1C));
            return VisitList(subkeyList, path, depth, allowIndex: true);
        }

        private bool VisitList(uint relativeOffset, string parentPath, int depth, bool allowIndex)
        {
            (long dataOffset, int dataLength) = ResolveCell(relativeOffset);
            if (dataLength < 4)
            {
                throw new InvalidDataException("a subkey list is smaller than its header");
            }

            byte[] list = ReadBytes(dataOffset, dataLength);
            bool isIndex = HasSignature(list, 0, "ri");
            if (isIndex && !allowIndex)
            {
                throw new InvalidDataException("an index list points at another index list");
            }

            int entrySize = HasSignature(list, 0, "lf") || HasSignature(list, 0, "lh")
                ? 8
                : HasSignature(list, 0, "li") || isIndex
                    ? 4
                    : throw new InvalidDataException("a subkey list has an unknown signature");

            ushort count = BinaryPrimitives.ReadUInt16LittleEndian(list.AsSpan(2));
            if (4 + (long)count * entrySize > dataLength)
            {
                throw new InvalidDataException("a subkey list extends past its cell");
            }

            for (int index = 0; index < count; index += 1)
            {
                uint entry = BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(4 + (index * entrySize)));
                bool found = isIndex
                    ? VisitList(entry, parentPath, depth, allowIndex: false)
                    : VisitKey(entry, parentPath, depth + 1);

                if (found)
                {
                    return true;
                }
            }

            return false;
        }

        private (long DataOffset, int DataLength) ResolveCell(uint relativeOffset)
        {
            if (relativeOffset >= _binsSize || relativeOffset % MinimumCellSize != 0)
            {
                throw new InvalidDataException("a cell offset is outside the hive bins or is misaligned");
            }

            long binStart = relativeOffset & ~(long)(BinAlignment - 1);
            byte[] bin = ReadBytes(BaseBlockSize + binStart, BinHeaderSize);
            if (!HasSignature(bin, 0, "hbin"))
            {
                throw new InvalidDataException("a cell points into a block without an hbin header");
            }

            uint binSize = BinaryPrimitives.ReadUInt32LittleEndian(bin.AsSpan(8));
            if (binSize < BinAlignment || binSize % BinAlignment != 0 || binStart + binSize > _binsSize)
            {
                throw new InvalidDataException("an hbin size is invalid");
            }

            if (relativeOffset < binStart + BinHeaderSize)
            {
                throw new InvalidDataException("a cell starts inside an hbin header");
            }

            byte[] sizeField = ReadBytes(BaseBlockSize + relativeOffset, 4);
            int cellSize = BinaryPrimitives.ReadInt32LittleEndian(sizeField);
            if (cellSize >= 0)
            {
                throw new InvalidDataException("a key or list points at a free cell");
            }

            long allocated = -(long)cellSize;
            if (allocated < MinimumCellSize || allocated % MinimumCellSize != 0
                || relativeOffset + allocated > binStart + binSize)
            {
                throw new InvalidDataException("a cell size is invalid");
            }

            return (BaseBlockSize + relativeOffset + 4, (int)(allocated - 4));
        }

        private byte[] ReadBytes(long offset, int count)
        {
            if (offset < 0 || count < 0 || offset + count > _length)
            {
                throw new InvalidDataException("a structure extends past the end of the file");
            }

            var buffer = new byte[count];
            _stream.Seek(offset, SeekOrigin.Begin);
            _stream.ReadExactly(buffer);
            return buffer;
        }

        private static bool HasSignature(byte[] bytes, int offset, string signature)
        {
            if (bytes.Length < offset + signature.Length)
            {
                return false;
            }

            return string.Equals(
                Encoding.ASCII.GetString(bytes, offset, signature.Length),
                signature,
                StringComparison.Ordinal);
        }
    }
}

/// <summary>The outcome of scanning a hive file for symbolic link key nodes.</summary>
internal readonly record struct HiveScanResult(HiveScanOutcome Outcome, string KeyPath, string Reason)
{
    internal static HiveScanResult Clean { get; } = new(HiveScanOutcome.Clean, string.Empty, string.Empty);

    internal static HiveScanResult Linked(string keyPath) =>
        new(HiveScanOutcome.Linked, keyPath, string.Empty);

    internal static HiveScanResult Unverifiable(string reason) =>
        new(HiveScanOutcome.Unverifiable, string.Empty, reason);
}

internal enum HiveScanOutcome
{
    Clean,
    Linked,
    Unverifiable,
}

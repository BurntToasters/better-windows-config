using System.Buffers.Binary;
using System.Text;

namespace Bwc.Registry;

/// <summary>
/// Walks the key tree of a regf hive file without loading it and reports whether any reachable key
/// node has the KEY_SYM_LINK flag. The scanner first walks the hive bin chain from offset zero, so
/// every bin is a validated range; a bin may span several 4 KiB pages. The walk then follows subkey
/// lists from the root key only, and every cell is mapped to its bin before it is read. Any structure
/// that cannot be validated is reported as unverifiable, so the caller fails closed.
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

    /// <summary>Scans <paramref name="fullPath"/> and releases the file. Never throws for a malformed file.</summary>
    internal static HiveScanResult Scan(string fullPath)
    {
        using HeldHiveScan held = OpenAndScan(fullPath);
        return held.Result;
    }

    /// <summary>
    /// Scans <paramref name="fullPath"/> and keeps the file open with <see cref="FileShare.Read"/> until
    /// the returned object is disposed. A writer that opens the file for write, deletes it, or renames it
    /// is refused for that whole window, so the scanned bytes are the bytes a native call then loads.
    /// Never throws for a malformed file; an unverifiable file is returned with no file held.
    /// </summary>
    internal static HeldHiveScan OpenAndScan(string fullPath)
    {
        FileStream? stream = null;

        try
        {
            stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            HiveScanResult result = new Walker(stream).Run();

            var held = new HeldHiveScan(stream, result);
            stream = null;
            return held;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or ArgumentException or NotSupportedException)
        {
            return new HeldHiveScan(null, HiveScanResult.Unverifiable(ex.Message));
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private readonly record struct BinRange(long Start, long Size);

    private sealed class Walker
    {
        private readonly FileStream _stream;
        private readonly long _length;
        private readonly uint _binsSize;
        private readonly uint _rootOffset;
        private readonly List<BinRange> _bins = new();
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
            WalkBinChain();
        }

        public HiveScanResult Run()
        {
            return VisitKey(_rootOffset, string.Empty, depth: 0)
                ? HiveScanResult.Linked(_linkedKeyPath)
                : HiveScanResult.Clean;
        }

        /// <summary>
        /// Reads every bin header from offset zero. Each bin must start with an "hbin" header whose offset
        /// field matches its position, and each bin size must be a positive multiple of 4 KiB. The bins must
        /// end exactly at the declared bin data size. Anything else is unverifiable.
        /// </summary>
        private void WalkBinChain()
        {
            long offset = 0;

            while (offset < _binsSize)
            {
                byte[] header = ReadBytes(BaseBlockSize + offset, BinHeaderSize);
                if (!HasSignature(header, 0, "hbin"))
                {
                    throw new InvalidDataException("the hive bin chain has no hbin header where a bin should start");
                }

                if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != offset)
                {
                    throw new InvalidDataException("an hbin header records the wrong offset");
                }

                long size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
                if (size < BinAlignment || size % BinAlignment != 0 || offset + size > _binsSize)
                {
                    throw new InvalidDataException("an hbin size is invalid");
                }

                _bins.Add(new BinRange(offset, size));
                offset += size;
            }

            if (offset != _binsSize)
            {
                throw new InvalidDataException("the hive bin chain does not end at the declared bin data size");
            }
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

        /// <summary>
        /// Maps a cell offset to the validated bin that contains it, then checks the cell's size field.
        /// Returns the data offset and data length of the cell.
        /// </summary>
        private (long DataOffset, int DataLength) ResolveCell(uint relativeOffset)
        {
            if (relativeOffset >= _binsSize || relativeOffset % MinimumCellSize != 0)
            {
                throw new InvalidDataException("a cell offset is outside the hive bins or is misaligned");
            }

            BinRange bin = FindBin(relativeOffset);
            if (relativeOffset < bin.Start + BinHeaderSize)
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
                || relativeOffset + allocated > bin.Start + bin.Size)
            {
                throw new InvalidDataException("a cell size is invalid");
            }

            return (BaseBlockSize + relativeOffset + 4, (int)(allocated - 4));
        }

        /// <summary>
        /// Returns the bin whose range contains <paramref name="relativeOffset"/>. The bins cover all of
        /// the bin data, so a miss means the offset is outside the hive.
        /// </summary>
        private BinRange FindBin(uint relativeOffset)
        {
            int low = 0;
            int high = _bins.Count - 1;

            while (low <= high)
            {
                int middle = low + ((high - low) / 2);
                BinRange candidate = _bins[middle];

                if (relativeOffset < candidate.Start)
                {
                    high = middle - 1;
                }
                else if (relativeOffset >= candidate.Start + candidate.Size)
                {
                    low = middle + 1;
                }
                else
                {
                    return candidate;
                }
            }

            throw new InvalidDataException("a cell offset is outside every hive bin");
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

/// <summary>
/// A scanned hive file that stays open, with the scan result. Dispose releases the file. When the scan
/// was unverifiable, no file is held and dispose does nothing.
/// </summary>
internal sealed class HeldHiveScan : IDisposable
{
    private FileStream? _stream;

    internal HeldHiveScan(FileStream? stream, HiveScanResult result)
    {
        _stream = stream;
        Result = result;
    }

    internal HiveScanResult Result { get; }

    public void Dispose()
    {
        FileStream? stream = Interlocked.Exchange(ref _stream, null);
        stream?.Dispose();
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

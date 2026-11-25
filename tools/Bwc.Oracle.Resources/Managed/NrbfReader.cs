using System.Text;
using Bwc.Oracle.Resources.Model;

namespace Bwc.Oracle.Resources.Managed;

internal sealed class NrbfUnsupportedException : Exception
{
    public NrbfUnsupportedException(string message) : base(message)
    {
    }
}

/// A decoded serialized class instance: its name and its members in serialized order.
internal sealed record NrbfObject(string ClassName, List<DecodedField> Members);

/// A small reader for the .NET remoting binary serialization format (MS-NRBF), enough for the value types that WinForms
/// designers write into .resources files. It never instantiates a type. Any record it does not handle throws
/// NrbfUnsupportedException with the record type, and the caller keeps the raw bytes instead.
internal sealed class NrbfReader
{
    private enum BinaryType : byte
    {
        Primitive = 0,
        String = 1,
        Object = 2,
        SystemClass = 3,
        Class = 4,
        ObjectArray = 5,
        StringArray = 6,
        PrimitiveArray = 7,
    }

    private sealed record ClassShape(string Name, string[] MemberNames, BinaryType[] Types, object?[] Extra);

    private readonly BinaryReader _reader;
    private readonly Dictionary<int, ClassShape> _shapes = [];
    private readonly Dictionary<int, object?> _objects = [];
    private int _rootId;

    private NrbfReader(Stream stream)
    {
        _reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
    }

    public static NrbfObject Read(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        var reader = new NrbfReader(stream);
        return reader.ReadStream();
    }

    private NrbfObject ReadStream()
    {
        byte first = _reader.ReadByte();
        if (first != 0)
        {
            throw new NrbfUnsupportedException($"stream does not start with a serialization header (record {first})");
        }
        _rootId = _reader.ReadInt32();
        _reader.ReadInt32(); // header id
        int major = _reader.ReadInt32();
        _reader.ReadInt32(); // minor
        if (major != 1)
        {
            throw new NrbfUnsupportedException($"serialization major version {major}");
        }

        while (true)
        {
            byte record = _reader.ReadByte();
            if (record == 11)
            {
                break;
            }
            if (record == 12)
            {
                _reader.ReadInt32();
                ReadString();
                continue;
            }
            ReadRecordBody(record);
        }

        if (_objects.TryGetValue(_rootId, out object? root) && root is NrbfObject obj)
        {
            return obj;
        }
        throw new NrbfUnsupportedException($"root object {_rootId} is not a class instance");
    }

    private object? ReadRecordBody(byte record)
    {
        switch (record)
        {
            case 1:
                return ReadClassWithId();
            case 4:
                return ReadClass(withLibrary: false);
            case 5:
                return ReadClass(withLibrary: true);
            case 6:
            {
                int id = _reader.ReadInt32();
                string text = ReadString();
                _objects[id] = text;
                return text;
            }
            case 8:
            {
                byte primitive = _reader.ReadByte();
                return ReadPrimitive(primitive);
            }
            case 9:
            {
                int idRef = _reader.ReadInt32();
                return _objects.TryGetValue(idRef, out object? target) ? target : $"(reference {idRef})";
            }
            case 10:
                return null;
            default:
                throw new NrbfUnsupportedException($"record type {record} is not supported");
        }
    }

    private NrbfObject ReadClassWithId()
    {
        int objectId = _reader.ReadInt32();
        int metadataId = _reader.ReadInt32();
        if (!_shapes.TryGetValue(metadataId, out ClassShape? shape))
        {
            throw new NrbfUnsupportedException($"class with id refers to unknown metadata {metadataId}");
        }
        _shapes[objectId] = shape;
        return ReadMembers(objectId, shape);
    }

    private NrbfObject ReadClass(bool withLibrary)
    {
        int objectId = _reader.ReadInt32();
        string name = ReadString();
        int count = _reader.ReadInt32();
        if (count < 0 || count > 4096)
        {
            throw new NrbfUnsupportedException($"class {name} claims {count} members");
        }

        var names = new string[count];
        for (int i = 0; i < count; i++)
        {
            names[i] = ReadString();
        }

        var types = new BinaryType[count];
        for (int i = 0; i < count; i++)
        {
            types[i] = (BinaryType)_reader.ReadByte();
        }

        var extra = new object?[count];
        for (int i = 0; i < count; i++)
        {
            switch (types[i])
            {
                case BinaryType.Primitive:
                case BinaryType.PrimitiveArray:
                    extra[i] = _reader.ReadByte();
                    break;
                case BinaryType.SystemClass:
                    extra[i] = ReadString();
                    break;
                case BinaryType.Class:
                    extra[i] = ReadString();
                    _reader.ReadInt32();
                    break;
                case BinaryType.String:
                case BinaryType.Object:
                case BinaryType.ObjectArray:
                case BinaryType.StringArray:
                    break;
                default:
                    throw new NrbfUnsupportedException($"member binary type {(byte)types[i]} is not supported");
            }
        }

        if (withLibrary)
        {
            _reader.ReadInt32();
        }

        var shape = new ClassShape(name, names, types, extra);
        _shapes[objectId] = shape;
        return ReadMembers(objectId, shape);
    }

    private NrbfObject ReadMembers(int objectId, ClassShape shape)
    {
        var members = new List<DecodedField>(shape.MemberNames.Length);
        for (int i = 0; i < shape.MemberNames.Length; i++)
        {
            object? value = shape.Types[i] switch
            {
                BinaryType.Primitive => ReadPrimitive((byte)shape.Extra[i]!),
                BinaryType.String or BinaryType.Object or BinaryType.SystemClass or BinaryType.Class => ReadValueRecord(),
                _ => throw new NrbfUnsupportedException($"member '{shape.MemberNames[i]}' is an array ({shape.Types[i]})"),
            };
            members.Add(new DecodedField(shape.MemberNames[i], Flatten(value)));
        }

        var result = new NrbfObject(shape.Name, members);
        _objects[objectId] = result;
        return result;
    }

    private object? ReadValueRecord()
    {
        byte record = _reader.ReadByte();
        return ReadRecordBody(record);
    }

    private static object? Flatten(object? value) => value switch
    {
        NrbfObject nested => nested.ClassName + "{" + string.Join(", ", nested.Members.Select(m => $"{m.Name}={m.Value}")) + "}",
        _ => value,
    };

    private string ReadString()
    {
        int length = Read7BitLength();
        if (length < 0 || length > 16 * 1024 * 1024)
        {
            throw new NrbfUnsupportedException($"string length {length} is out of range");
        }
        byte[] bytes = _reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException("string is truncated");
        }
        return Encoding.UTF8.GetString(bytes);
    }

    private int Read7BitLength()
    {
        int result = 0;
        int shift = 0;
        while (true)
        {
            byte b = _reader.ReadByte();
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
            shift += 7;
            if (shift > 28)
            {
                throw new NrbfUnsupportedException("string length prefix is too long");
            }
        }
    }

    private object? ReadPrimitive(byte primitive) => primitive switch
    {
        1 => _reader.ReadBoolean(),
        2 => (long)_reader.ReadByte(),
        3 => _reader.ReadChar().ToString(),
        6 => _reader.ReadDouble(),
        7 => (long)_reader.ReadInt16(),
        8 => (long)_reader.ReadInt32(),
        9 => _reader.ReadInt64(),
        10 => (long)_reader.ReadSByte(),
        11 => (double)_reader.ReadSingle(),
        14 => (long)_reader.ReadUInt16(),
        15 => (long)_reader.ReadUInt32(),
        16 => (long)_reader.ReadUInt64(),
        18 => ReadString(),
        _ => throw new NrbfUnsupportedException($"primitive type {primitive} is not supported"),
    };
}

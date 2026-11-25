using System.Globalization;
using System.Text;
using Bwc.Oracle.Resources.Model;

namespace Bwc.Oracle.Resources.Managed;

/// Fills a ManagedResourceEntry from the raw data that ResourceReader.GetResourceData returned.
/// Strings and primitives are decoded from the .resources data format. Serialized objects are read with NrbfReader and
/// the known WinForms structs are given a readable form. Anything else keeps its type name and raw bytes.
internal static class WinFormsValueDecoder
{
    public static void Decode(ManagedResourceEntry entry, byte[] data)
    {
        string type = entry.Type;
        if (type.StartsWith("ResourceTypeCode.", StringComparison.Ordinal))
        {
            DecodeTypeCode(entry, type["ResourceTypeCode.".Length..], data);
            return;
        }

        try
        {
            NrbfObject obj = NrbfReader.Read(data);
            entry.Kind = "object";
            entry.Fields = obj.Members;
            entry.Value = Readable(obj);
        }
        catch (Exception ex) when (ex is NrbfUnsupportedException or EndOfStreamException or InvalidDataException)
        {
            entry.Kind = "bytes";
            entry.Base64 = Convert.ToBase64String(data);
            entry.Error = $"not decoded: {ex.Message}";
        }
    }

    private static void DecodeTypeCode(ManagedResourceEntry entry, string code, byte[] data)
    {
        switch (code)
        {
            case "Null":
                entry.Kind = "primitive";
                return;
            case "String":
                entry.Kind = "string";
                entry.Value = ReadLengthPrefixedString(data);
                return;
            case "Boolean":
                Primitive(entry, data, 1, d => d[0] != 0);
                return;
            case "Char":
                Primitive(entry, data, 2, d => BitConverter.ToChar(d).ToString());
                return;
            case "Byte":
                Primitive(entry, data, 1, d => (long)d[0]);
                return;
            case "SByte":
                Primitive(entry, data, 1, d => (long)(sbyte)d[0]);
                return;
            case "Int16":
                Primitive(entry, data, 2, d => (long)BitConverter.ToInt16(d));
                return;
            case "UInt16":
                Primitive(entry, data, 2, d => (long)BitConverter.ToUInt16(d));
                return;
            case "Int32":
                Primitive(entry, data, 4, d => (long)BitConverter.ToInt32(d));
                return;
            case "UInt32":
                Primitive(entry, data, 4, d => (long)BitConverter.ToUInt32(d));
                return;
            case "Int64":
                Primitive(entry, data, 8, d => BitConverter.ToInt64(d));
                return;
            case "UInt64":
                Primitive(entry, data, 8, d => BitConverter.ToUInt64(d).ToString(CultureInfo.InvariantCulture));
                return;
            case "Single":
                Primitive(entry, data, 4, d => (double)BitConverter.ToSingle(d));
                return;
            case "Double":
                Primitive(entry, data, 8, d => BitConverter.ToDouble(d));
                return;
            default:
                // ByteArray, Stream, Decimal, DateTime, TimeSpan: kept raw.
                entry.Kind = "bytes";
                entry.Base64 = Convert.ToBase64String(data);
                return;
        }
    }

    private static void Primitive(ManagedResourceEntry entry, byte[] data, int size, Func<byte[], object?> read)
    {
        if (data.Length != size)
        {
            entry.Kind = "bytes";
            entry.Base64 = Convert.ToBase64String(data);
            entry.Error = $"not decoded: expected {size} bytes, got {data.Length}";
            return;
        }
        entry.Kind = "primitive";
        entry.Value = read(data);
    }

    /// The .resources string form: a 7 bit encoded byte length, then UTF-8. GetResourceData may or may not keep the prefix,
    /// so the prefix is accepted only when it matches the remaining length exactly.
    private static string ReadLengthPrefixedString(byte[] data)
    {
        int length = 0;
        int shift = 0;
        int index = 0;
        while (index < data.Length && index < 5)
        {
            byte b = data[index++];
            length |= (b & 0x7F) << shift;
            shift += 7;
            if ((b & 0x80) == 0)
            {
                if (length == data.Length - index)
                {
                    return Encoding.UTF8.GetString(data, index, length);
                }
                break;
            }
        }
        return Encoding.UTF8.GetString(data);
    }

    private static object? Readable(NrbfObject obj)
    {
        object? Field(string name) => obj.Members.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

        switch (obj.ClassName)
        {
            case "System.Drawing.Point":
                return $"{Field("x")}, {Field("y")}";
            case "System.Drawing.Size":
                return $"{Field("width")}, {Field("height")}";
            case "System.Windows.Forms.Padding":
                return $"{Field("_left") ?? Field("left")}, {Field("_top") ?? Field("top")}, {Field("_right") ?? Field("right")}, {Field("_bottom") ?? Field("bottom")}";
            case "System.Windows.Forms.AnchorStyles":
                return Field("value__") is long a ? AnchorText(a) : null;
            case "System.Windows.Forms.DockStyle":
                return Field("value__") is long d ? DockText(d) : null;
            case "System.Windows.Forms.TableLayoutSettings":
                return Field("SerializedString");
            default:
                return null;
        }
    }

    private static string AnchorText(long value)
    {
        if (value == 0)
        {
            return "None";
        }
        var parts = new List<string>();
        if ((value & 1) != 0) parts.Add("Top");
        if ((value & 2) != 0) parts.Add("Bottom");
        if ((value & 4) != 0) parts.Add("Left");
        if ((value & 8) != 0) parts.Add("Right");
        long rest = value & ~15L;
        if (rest != 0) parts.Add($"0x{rest:X}");
        return string.Join(", ", parts);
    }

    private static string DockText(long value) => value switch
    {
        0 => "None",
        1 => "Top",
        2 => "Bottom",
        3 => "Left",
        4 => "Right",
        5 => "Fill",
        _ => $"0x{value:X}",
    };
}

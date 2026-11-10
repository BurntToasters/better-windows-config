using Bwc.Oracle.Resources.Model;
using Bwc.Oracle.Resources.Native;

namespace Bwc.Oracle.Resources.Format;

/// RT_DIALOG: DLGTEMPLATE (classic) and DLGTEMPLATEEX (signature word 0xFFFF after dlgVer 1).
internal static class DialogParser
{
    private const uint DS_SETFONT = 0x00000040;

    public static DialogRecord Parse(ResourceEntry entry)
    {
        var record = new DialogRecord();
        RecordFactory.Fill(record, entry);
        if (entry.LoadError is not null)
        {
            return record;
        }

        try
        {
            var reader = new ResourceReader(entry.Data);
            bool extended = reader.PeekUInt16(0) == 1 && reader.PeekUInt16(2) == 0xFFFF;
            if (extended)
            {
                ParseExtended(ref reader, record);
            }
            else
            {
                ParseClassic(ref reader, record);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            record.Error = ParseGuard.Describe(ex);
            record.Controls = null;
        }

        return record;
    }

    private static void ParseClassic(ref ResourceReader reader, DialogRecord record)
    {
        record.Template = "DIALOG";
        uint style = reader.ReadUInt32();
        uint exStyle = reader.ReadUInt32();
        ushort itemCount = reader.ReadUInt16();
        short x = reader.ReadInt16();
        short y = reader.ReadInt16();
        short cx = reader.ReadInt16();
        short cy = reader.ReadInt16();

        record.Style = Decode.Hex32(style);
        record.ExStyle = Decode.Hex32(exStyle);
        record.DeclaredControlCount = itemCount;
        record.X = x;
        record.Y = y;
        record.Cx = cx;
        record.Cy = cy;
        record.Menu = reader.ReadSzOrOrd();
        record.WindowClass = ResolveWindowClass(reader.ReadSzOrOrd());
        record.Title = reader.ReadWideString();

        if ((style & DS_SETFONT) != 0)
        {
            ushort pointSize = reader.ReadUInt16();
            string typeface = reader.ReadWideString();
            record.Font = new FontRecord { PointSize = pointSize, Typeface = typeface };
        }

        var controls = new List<ControlRecord>();
        for (int i = 0; i < itemCount; i++)
        {
            reader.AlignToDword();
            uint itemStyle = reader.ReadUInt32();
            uint itemExStyle = reader.ReadUInt32();
            short ix = reader.ReadInt16();
            short iy = reader.ReadInt16();
            short icx = reader.ReadInt16();
            short icy = reader.ReadInt16();
            ushort id = reader.ReadUInt16();
            object? windowClass = reader.ReadSzOrOrd();
            object? title = reader.ReadSzOrOrd();
            ushort creationLength = reader.ReadUInt16();
            reader.Skip(creationLength);

            controls.Add(new ControlRecord
            {
                Id = id,
                ExStyle = Decode.Hex32(itemExStyle),
                Style = Decode.Hex32(itemStyle),
                X = ix,
                Y = iy,
                Cx = icx,
                Cy = icy,
                WindowClass = ResolveWindowClass(windowClass),
                Title = title,
                CreationDataLength = creationLength,
            });
        }

        record.Controls = controls;
    }

    private static void ParseExtended(ref ResourceReader reader, DialogRecord record)
    {
        record.Template = "DIALOGEX";
        reader.Skip(4); // dlgVer (1) and signature (0xFFFF), already checked by the caller.
        uint helpId = reader.ReadUInt32();
        uint exStyle = reader.ReadUInt32();
        uint style = reader.ReadUInt32();
        ushort itemCount = reader.ReadUInt16();
        short x = reader.ReadInt16();
        short y = reader.ReadInt16();
        short cx = reader.ReadInt16();
        short cy = reader.ReadInt16();

        record.HelpId = helpId;
        record.Style = Decode.Hex32(style);
        record.ExStyle = Decode.Hex32(exStyle);
        record.DeclaredControlCount = itemCount;
        record.X = x;
        record.Y = y;
        record.Cx = cx;
        record.Cy = cy;
        record.Menu = reader.ReadSzOrOrd();
        record.WindowClass = ResolveWindowClass(reader.ReadSzOrOrd());
        record.Title = reader.ReadWideString();

        if ((style & DS_SETFONT) != 0)
        {
            ushort pointSize = reader.ReadUInt16();
            ushort weight = reader.ReadUInt16();
            byte italic = reader.ReadByte();
            byte charset = reader.ReadByte();
            string typeface = reader.ReadWideString();
            record.Font = new FontRecord
            {
                PointSize = pointSize,
                Weight = weight,
                Italic = italic != 0,
                Charset = charset,
                Typeface = typeface,
            };
        }

        var controls = new List<ControlRecord>();
        for (int i = 0; i < itemCount; i++)
        {
            reader.AlignToDword();
            uint itemHelpId = reader.ReadUInt32();
            uint itemExStyle = reader.ReadUInt32();
            uint itemStyle = reader.ReadUInt32();
            short ix = reader.ReadInt16();
            short iy = reader.ReadInt16();
            short icx = reader.ReadInt16();
            short icy = reader.ReadInt16();
            uint id = reader.ReadUInt32();
            object? windowClass = reader.ReadSzOrOrd();
            object? title = reader.ReadSzOrOrd();
            ushort extraLength = reader.ReadUInt16();
            reader.Skip(extraLength);

            controls.Add(new ControlRecord
            {
                Id = id,
                HelpId = itemHelpId,
                ExStyle = Decode.Hex32(itemExStyle),
                Style = Decode.Hex32(itemStyle),
                X = ix,
                Y = iy,
                Cx = icx,
                Cy = icy,
                WindowClass = ResolveWindowClass(windowClass),
                Title = title,
                CreationDataLength = extraLength,
            });
        }

        record.Controls = controls;
    }

    private static object? ResolveWindowClass(object? value) =>
        value is OrdinalRef ordinal ? ordinal with { Name = ControlClass.NameOf(ordinal.Ordinal) } : value;
}

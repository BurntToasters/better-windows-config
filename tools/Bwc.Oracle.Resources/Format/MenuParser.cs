using Bwc.Oracle.Resources.Model;
using Bwc.Oracle.Resources.Native;

namespace Bwc.Oracle.Resources.Format;

/// RT_MENU: classic MENU (version word 0) and MENUEX (version word 1).
internal static class MenuParser
{
    private const int MaxDepth = 32;
    private const int MaxItems = 8192;

    private const ushort MF_POPUP = 0x0010;
    private const ushort MF_SEPARATOR = 0x0800;
    private const ushort MF_END = 0x0080;

    private const uint MFT_SEPARATOR = 0x00000800;
    private const ushort MFR_POPUP = 0x0001;
    private const ushort MFR_END = 0x0080;

    public static MenuRecord Parse(ResourceEntry entry)
    {
        var record = new MenuRecord();
        RecordFactory.Fill(record, entry);
        if (entry.LoadError is not null)
        {
            return record;
        }

        try
        {
            var reader = new ResourceReader(entry.Data);
            ushort version = reader.ReadUInt16();
            int count = 0;
            switch (version)
            {
                case 0:
                {
                    // MENUHEADER: versionNumber, offset. Items start `offset` bytes after the 4-byte header.
                    record.Template = "MENU";
                    ushort offset = reader.ReadUInt16();
                    reader.Skip(offset);
                    record.Items = ReadClassicItems(ref reader, 0, ref count);
                    break;
                }
                case 1:
                {
                    // MENUEX_TEMPLATE_HEADER: wVersion, wOffset, dwHelpId. Items start at 4 + wOffset.
                    record.Template = "MENUEX";
                    ushort offset = reader.ReadUInt16();
                    record.HelpId = reader.ReadUInt32();
                    reader.Seek(4 + offset);
                    record.Items = ReadExItems(ref reader, 0, ref count);
                    break;
                }
                default:
                    throw reader.Fail($"unknown menu template version {version}");
            }
            record.TemplateVersion = version;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            record.Error = ParseGuard.Describe(ex);
            record.Items = null;
        }

        return record;
    }

    private static List<MenuItemRecord> ReadClassicItems(ref ResourceReader reader, int depth, ref int count)
    {
        if (depth > MaxDepth)
        {
            throw reader.Fail($"menu nesting is deeper than {MaxDepth} levels");
        }

        var items = new List<MenuItemRecord>();
        while (true)
        {
            if (++count > MaxItems)
            {
                throw reader.Fail($"menu has more than {MaxItems} items");
            }

            ushort option = reader.ReadUInt16();
            bool popup = (option & MF_POPUP) != 0;
            ushort id = popup ? (ushort)0 : reader.ReadUInt16();
            string text = reader.ReadWideString();
            List<MenuItemRecord>? children = popup ? ReadClassicItems(ref reader, depth + 1, ref count) : null;

            // Classic separators are either flagged MF_SEPARATOR or written as an empty, id-0, non-popup item.
            bool separator = (option & MF_SEPARATOR) != 0 || (!popup && id == 0 && text.Length == 0);
            items.Add(new MenuItemRecord
            {
                Id = id,
                Text = text,
                Popup = popup,
                Separator = separator,
                Flags = Decode.Hex16(option),
                FlagNames = Decode.Names(option, Decode.ClassicMenuOptions),
                Children = children,
            });

            if ((option & MF_END) != 0)
            {
                return items;
            }
        }
    }

    private static List<MenuItemRecord> ReadExItems(ref ResourceReader reader, int depth, ref int count)
    {
        if (depth > MaxDepth)
        {
            throw reader.Fail($"menu nesting is deeper than {MaxDepth} levels");
        }

        var items = new List<MenuItemRecord>();
        while (true)
        {
            if (++count > MaxItems)
            {
                throw reader.Fail($"menu has more than {MaxItems} items");
            }

            uint type = reader.ReadUInt32();
            uint state = reader.ReadUInt32();
            uint id = reader.ReadUInt32();
            ushort resInfo = reader.ReadUInt16();
            string text = reader.ReadWideString();
            reader.AlignToDword();

            bool popup = (resInfo & MFR_POPUP) != 0;
            uint? helpId = null;
            List<MenuItemRecord>? children = null;
            if (popup)
            {
                helpId = reader.ReadUInt32();
                children = ReadExItems(ref reader, depth + 1, ref count);
            }

            items.Add(new MenuItemRecord
            {
                Id = id,
                Text = text,
                Popup = popup,
                Separator = (type & MFT_SEPARATOR) != 0,
                Type = Decode.Hex32(type),
                TypeNames = Decode.Names(type, Decode.MenuExType),
                State = Decode.Hex32(state),
                StateNames = Decode.Names(state, Decode.MenuExState),
                ResInfo = Decode.Hex16(resInfo),
                HelpId = helpId,
                Children = children,
            });

            if ((resInfo & MFR_END) != 0)
            {
                return items;
            }
        }
    }
}

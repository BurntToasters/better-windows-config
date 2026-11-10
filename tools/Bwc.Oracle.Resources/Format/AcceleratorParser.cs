using Bwc.Oracle.Resources.Model;
using Bwc.Oracle.Resources.Native;

namespace Bwc.Oracle.Resources.Format;

/// RT_ACCELERATOR: 8-byte ACCELTABLEENTRY records (fFlags, key, cmd, pad). The entry with FEND set is last.
internal static class AcceleratorParser
{
    private const ushort FVIRTKEY = 0x01;
    private const ushort FEND = 0x80;
    private const int EntrySize = 8;
    private const int MaxEntries = 4096;

    public static AcceleratorRecord Parse(ResourceEntry entry)
    {
        var record = new AcceleratorRecord();
        RecordFactory.Fill(record, entry);
        if (entry.LoadError is not null)
        {
            return record;
        }

        try
        {
            var reader = new ResourceReader(entry.Data);
            var entries = new List<AcceleratorEntry>();
            while (true)
            {
                if (entries.Count >= MaxEntries)
                {
                    throw reader.Fail($"accelerator table has more than {MaxEntries} entries");
                }
                if (reader.Remaining < EntrySize)
                {
                    throw reader.Fail("accelerator table ends before an entry with the FEND flag");
                }

                ushort flags = reader.ReadUInt16();
                ushort key = reader.ReadUInt16();
                ushort command = reader.ReadUInt16();
                reader.ReadUInt16(); // padding

                bool virtualKey = (flags & FVIRTKEY) != 0;
                entries.Add(new AcceleratorEntry
                {
                    Key = key,
                    KeyName = KeyNames.Describe(key, virtualKey),
                    Flags = Decode.Hex16(flags),
                    FlagNames = Decode.Names(flags, Decode.AcceleratorFlags),
                    Command = command,
                });

                if ((flags & FEND) != 0)
                {
                    break;
                }
            }
            record.Entries = entries;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            record.Error = ParseGuard.Describe(ex);
            record.Entries = null;
        }

        return record;
    }
}

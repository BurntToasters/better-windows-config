using Bwc.Oracle.Resources.Model;
using Bwc.Oracle.Resources.Native;

namespace Bwc.Oracle.Resources.Format;

/// RT_STRING: one block per resource name (block id >= 1). Each block holds 16 slots, each a WORD length
/// followed by that many UTF-16 code units with no terminator. Real id = (blockId - 1) * 16 + slot.
internal static class StringTableParser
{
    private const int SlotsPerBlock = 16;

    public static StringBlockRecord Parse(ResourceEntry entry)
    {
        var record = new StringBlockRecord();
        RecordFactory.Fill(record, entry);
        if (entry.LoadError is not null)
        {
            return record;
        }

        try
        {
            if (entry.Name.Number is not int blockId)
            {
                throw new ResourceFormatException($"string block name '{entry.Name.Name}' is not numeric");
            }
            if (blockId < 1)
            {
                throw new ResourceFormatException($"string block id {blockId} is invalid (block ids start at 1)");
            }

            var reader = new ResourceReader(entry.Data);
            var strings = new List<StringEntry>();
            for (int slot = 0; slot < SlotsPerBlock; slot++)
            {
                if (reader.Remaining < 2)
                {
                    throw reader.Fail($"string block {blockId} is truncated at slot {slot}");
                }

                ushort length = reader.ReadUInt16();
                if (reader.Remaining < length * 2)
                {
                    throw reader.Fail($"string block {blockId} slot {slot} claims {length} characters but the block is truncated");
                }
                if (length == 0)
                {
                    continue;
                }

                strings.Add(new StringEntry
                {
                    Id = (blockId - 1) * SlotsPerBlock + slot,
                    Text = reader.ReadWideChars(length),
                });
            }
            record.Strings = strings;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            record.Error = ParseGuard.Describe(ex);
            record.Strings = null;
        }

        return record;
    }
}

using Bwc.Oracle.Resources.Model;
using Bwc.Oracle.Resources.Native;

namespace Bwc.Oracle.Resources.Format;

internal static class RecordFactory
{
    /// Copies the identity of a resource into a record. A load failure is recorded here so parsers can skip the bytes.
    public static void Fill(ResourceRecord record, ResourceEntry entry)
    {
        record.Type = ResourceTypeNames.Display(entry.Type);
        record.TypeId = entry.Type.Number;
        record.Id = entry.Name.Number;
        record.Name = entry.Name.Name;
        record.Language = entry.Language;
        record.LanguageTag = LanguageTags.Of(entry.Language);
        record.ByteLength = entry.ByteLength;
        record.Error = entry.LoadError;
    }
}

internal static class ParseGuard
{
    /// Format errors keep their message. Anything else is a parser bug, reported as an error entry instead of a crash.
    public static string Describe(Exception ex) =>
        ex is ResourceFormatException
            ? ex.Message
            : $"parser fault ({ex.GetType().Name}): {ex.Message}";
}

using Bwc.Oracle.Resources.Format;
using Bwc.Oracle.Resources.Native;

namespace Bwc.Oracle.Resources.Model;

/// Turns the raw resource entries of one module into the sorted golden model.
internal static class GoldenBuilder
{
    private static readonly Comparer<ResourceId> IdOrder = Comparer<ResourceId>.Create(ResourceId.Compare);

    public static ModuleDump Build(string module, string path, IReadOnlyList<ResourceEntry> entries)
    {
        var dump = new ModuleDump { Module = module, Path = path };
        dump.ResourceTypes = BuildInventory(entries);

        foreach (ResourceEntry entry in entries)
        {
            switch (entry.Type.Number ?? -1)
            {
                case ResourceTypeNames.Menu:
                    dump.Menus.Add(MenuParser.Parse(entry));
                    break;
                case ResourceTypeNames.Dialog:
                    dump.Dialogs.Add(DialogParser.Parse(entry));
                    break;
                case ResourceTypeNames.Accelerator:
                    dump.Accelerators.Add(AcceleratorParser.Parse(entry));
                    break;
                case ResourceTypeNames.StringTable:
                    dump.StringTables.Add(StringTableParser.Parse(entry));
                    break;
            }
        }

        dump.Menus.Sort((a, b) => CompareRecords(a, b));
        dump.Dialogs.Sort((a, b) => CompareRecords(a, b));
        dump.Accelerators.Sort((a, b) => CompareRecords(a, b));
        dump.StringTables.Sort((a, b) => CompareRecords(a, b));

        dump.ErrorCount = CountErrors(dump);
        return dump;
    }

    private static List<TypeInventory> BuildInventory(IReadOnlyList<ResourceEntry> entries) =>
        entries
            .GroupBy(e => e.Type)
            .OrderBy(g => g.Key, IdOrder)
            .Select(g => new TypeInventory
            {
                Type = ResourceTypeNames.Display(g.Key),
                TypeId = g.Key.Number,
                Resources = g
                    .OrderBy(e => e.Name, IdOrder)
                    .ThenBy(e => e.Language)
                    .Select(e => new InventoryEntry
                    {
                        Id = e.Name.Number,
                        Name = e.Name.Name,
                        Language = e.Language,
                        LanguageTag = LanguageTags.Of(e.Language),
                        ByteLength = e.ByteLength,
                        Error = e.LoadError,
                    })
                    .ToList(),
            })
            .ToList();

    private static int CompareRecords(ResourceRecord a, ResourceRecord b)
    {
        int byName = ResourceId.Compare(new ResourceId(a.Id, a.Name), new ResourceId(b.Id, b.Name));
        return byName != 0 ? byName : a.Language.CompareTo(b.Language);
    }

    /// Load failures of parsed types are already carried by their records, so count them once.
    private static int CountErrors(ModuleDump dump)
    {
        int errors = dump.ResourceTypes
            .Where(t => t.TypeId is not (ResourceTypeNames.Menu or ResourceTypeNames.Dialog or ResourceTypeNames.Accelerator or ResourceTypeNames.StringTable))
            .Sum(t => t.Resources.Count(r => r.Error is not null));
        errors += dump.Menus.Count(r => r.Error is not null);
        errors += dump.Dialogs.Count(r => r.Error is not null);
        errors += dump.Accelerators.Count(r => r.Error is not null);
        errors += dump.StringTables.Count(r => r.Error is not null);
        return errors;
    }
}

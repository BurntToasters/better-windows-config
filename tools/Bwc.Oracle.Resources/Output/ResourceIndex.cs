using System.Text.Json;
using System.Text.RegularExpressions;
using Bwc.Oracle.Resources.Model;
using Bwc.Oracle.Resources.Native;

namespace Bwc.Oracle.Resources.Output;

// The tracked gpedit-resource-index.json. It is built only from ids, counts, resource type names and identifier-like
// entry names. No field of the golden model that can hold Microsoft text (titles, menu text, strings, values) is read.

internal sealed class IndexFile
{
    public int SchemaVersion { get; set; } = 1;
    public string Generator { get; set; } = "Bwc.Oracle.Resources";
    public string Tool { get; set; } = "gpedit";
    public IndexDefinitions Definitions { get; set; } = new();
    public List<IndexModule> Modules { get; set; } = [];
    public List<IndexWinFormsFile> WinForms { get; set; } = [];
}

internal sealed class IndexDefinitions
{
    public string StringId { get; set; } = "Qualified as <module>:<id>, for example admtmpl.dll.mui:123.";
    public string ItemCount { get; set; } = "Menu items at every depth, popups included, separators excluded.";
    public string ControlIds { get; set; } = "Dialog control ids in template order. The static id (0xFFFF or 0xFFFFFFFF) is written as -1.";
}

internal sealed class IndexModule
{
    public string Module { get; set; } = "";
    public List<IndexType> ResourceTypes { get; set; } = [];
    public List<IndexDialog> Dialogs { get; set; } = [];
    public List<IndexMenu> Menus { get; set; } = [];
    public List<IndexAccelerator> Accelerators { get; set; } = [];
    public List<int> StringIds { get; set; } = [];
}

internal sealed class IndexType
{
    public string Type { get; set; } = "";
    public int? TypeId { get; set; }
    public List<int> Ids { get; set; } = [];
    public List<string> Names { get; set; } = [];
}

internal sealed class IndexDialog
{
    public int? Id { get; set; }
    public string? Name { get; set; }
    public int ControlCount { get; set; }
    public List<int> ControlIds { get; set; } = [];
}

internal sealed class IndexMenu
{
    public int? Id { get; set; }
    public string? Name { get; set; }
    public int ItemCount { get; set; }
    public List<uint> CommandIds { get; set; } = [];
}

internal sealed class IndexAccelerator
{
    public int? Id { get; set; }
    public string? Name { get; set; }
    public int EntryCount { get; set; }
}

internal sealed class IndexWinFormsFile
{
    public string Assembly { get; set; } = "";
    public string Resource { get; set; } = "";
    public List<IndexWinFormsEntry> Entries { get; set; } = [];
}

internal sealed class IndexWinFormsEntry
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
}

internal static partial class ResourceIndex
{
    /// Same options as the other dumps, then every array of integers is put on one line so the file stays reviewable.
    /// The result is checked before it is returned: it must parse as JSON and equal in content the parse of the plain
    /// text, so the one-line rewrite can never change data. A failed check throws and nothing is written.
    public static string Serialize(IndexFile index)
    {
        string plain = GoldenJson.Serialize(index);
        string formatted = NumberArray().Replace(plain, m => "[" + Whitespace().Replace(m.Groups[1].Value, " ") + "]");

        JsonDocument formattedDoc;
        try
        {
            formattedDoc = JsonDocument.Parse(formatted);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"the formatted resource index is not valid JSON ({ex.Message}); the index was not written");
        }

        using (formattedDoc)
        using (JsonDocument plainDoc = JsonDocument.Parse(plain))
        {
            string? difference = FirstDifference(plainDoc.RootElement, formattedDoc.RootElement, "$");
            if (difference is not null)
            {
                throw new InvalidDataException($"the one-line rewrite of the resource index changed its content ({difference}); the index was not written");
            }
        }
        return formatted;
    }

    /// Names a JSON path and the kind of difference only. No value is ever put in the message.
    private static string? FirstDifference(JsonElement a, JsonElement b, string path)
    {
        if (a.ValueKind != b.ValueKind)
        {
            return $"{path}: value kind differs";
        }

        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var left = a.EnumerateObject().ToList();
                var right = b.EnumerateObject().ToList();
                if (left.Count != right.Count)
                {
                    return $"{path}: property count differs";
                }
                for (int i = 0; i < left.Count; i++)
                {
                    if (!string.Equals(left[i].Name, right[i].Name, StringComparison.Ordinal))
                    {
                        return $"{path}: property {i} name differs";
                    }
                    string? inner = FirstDifference(left[i].Value, right[i].Value, path + "." + left[i].Name);
                    if (inner is not null)
                    {
                        return inner;
                    }
                }
                return null;
            }
            case JsonValueKind.Array:
            {
                int length = a.GetArrayLength();
                if (length != b.GetArrayLength())
                {
                    return $"{path}: array length differs";
                }
                for (int i = 0; i < length; i++)
                {
                    string? inner = FirstDifference(a[i], b[i], $"{path}[{i}]");
                    if (inner is not null)
                    {
                        return inner;
                    }
                }
                return null;
            }
            case JsonValueKind.String:
                return string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal) ? null : $"{path}: string differs";
            case JsonValueKind.Number:
                return string.Equals(a.GetRawText(), b.GetRawText(), StringComparison.Ordinal) ? null : $"{path}: number differs";
            default:
                return null;
        }
    }

    [GeneratedRegex(@"\[\s*(-?\d+(?:,\s*-?\d+)*)\s*\]")]
    private static partial Regex NumberArray();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public static IndexFile Build(GoldenFile golden, FormsFile forms)
    {
        var index = new IndexFile();
        foreach (ModuleDump module in golden.Modules)
        {
            index.Modules.Add(BuildModule(module));
        }

        foreach (ManagedResourceFile file in forms.ResourceFiles
                     .OrderBy(f => f.Assembly, StringComparer.Ordinal)
                     .ThenBy(f => f.Name, StringComparer.Ordinal))
        {
            index.WinForms.Add(new IndexWinFormsFile
            {
                Assembly = file.Assembly,
                Resource = file.Name,
                Entries = file.Entries
                    .OrderBy(e => e.Name, StringComparer.Ordinal)
                    .Select(e => new IndexWinFormsEntry { Name = e.Name, Type = e.Type })
                    .ToList(),
            });
        }
        return index;
    }

    private static IndexModule BuildModule(ModuleDump module)
    {
        var result = new IndexModule { Module = module.Module };

        foreach (TypeInventory type in module.ResourceTypes)
        {
            result.ResourceTypes.Add(new IndexType
            {
                Type = type.Type,
                TypeId = type.TypeId,
                Ids = type.Resources.Where(r => r.Id is not null).Select(r => r.Id!.Value).Distinct().Order().ToList(),
                Names = type.Resources.Where(r => r.Name is not null).Select(r => r.Name!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            });
        }

        foreach (DialogRecord dialog in Distinct(module.Dialogs))
        {
            result.Dialogs.Add(new IndexDialog
            {
                Id = dialog.Id,
                Name = dialog.Name,
                ControlCount = dialog.Controls?.Count ?? 0,
                ControlIds = (dialog.Controls ?? []).Select(c => c.Id is 0xFFFF or 0xFFFFFFFF ? -1 : unchecked((int)c.Id)).ToList(),
            });
        }

        foreach (MenuRecord menu in Distinct(module.Menus))
        {
            var commands = new SortedSet<uint>();
            int count = CountItems(menu.Items, commands);
            result.Menus.Add(new IndexMenu
            {
                Id = menu.Id,
                Name = menu.Name,
                ItemCount = count,
                CommandIds = commands.ToList(),
            });
        }

        foreach (AcceleratorRecord table in Distinct(module.Accelerators))
        {
            result.Accelerators.Add(new IndexAccelerator { Id = table.Id, Name = table.Name, EntryCount = table.Entries?.Count ?? 0 });
        }

        result.StringIds = module.StringTables
            .SelectMany(t => t.Strings ?? [])
            .Select(s => s.Id)
            .Distinct()
            .Order()
            .ToList();
        return result;
    }

    /// The same resource in several languages appears once. The golden lists are already sorted by name, then language.
    private static IEnumerable<T> Distinct<T>(IEnumerable<T> records) where T : ResourceRecord
    {
        var seen = new HashSet<(int?, string?)>();
        foreach (T record in records)
        {
            if (seen.Add((record.Id, record.Name)))
            {
                yield return record;
            }
        }
    }

    private static int CountItems(List<MenuItemRecord>? items, SortedSet<uint> commands)
    {
        int count = 0;
        foreach (MenuItemRecord item in items ?? [])
        {
            if (!item.Separator)
            {
                count++;
                if (item.Id != 0)
                {
                    commands.Add(item.Id);
                }
            }
            count += CountItems(item.Children, commands);
        }
        return count;
    }
}

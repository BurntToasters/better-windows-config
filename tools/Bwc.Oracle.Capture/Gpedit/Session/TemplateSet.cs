namespace Bwc.Oracle.Capture.Gpedit.Session;

// ADMX and ADML files a capture needs in PolicyDefinitions. Only these names are ever created or deleted there.
internal sealed class TemplateSet
{
    internal sealed record Item(string SourcePath, string RelativePath);

    internal TemplateSet(IReadOnlyList<Item> items)
    {
        Items = items;
        foreach (Item item in items)
        {
            ValidateRelative(item.RelativePath);
        }
    }

    internal IReadOnlyList<Item> Items { get; }

    internal static TemplateSet Empty { get; } = new(Array.Empty<Item>());

    // A folder holding Name.admx files and en-US\Name.adml files.
    internal static TemplateSet FromDirectory(string dir)
    {
        var items = new List<Item>();
        foreach (string admx in Directory.GetFiles(dir, "*.admx").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            items.Add(new Item(admx, Path.GetFileName(admx)));
        }

        string enUs = Path.Combine(dir, "en-US");
        if (Directory.Exists(enUs))
        {
            foreach (string adml in Directory.GetFiles(enUs, "*.adml").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                items.Add(new Item(adml, "en-US\\" + Path.GetFileName(adml)));
            }
        }

        return new TemplateSet(items);
    }

    // Only "Name.admx" or "en-US\Name.adml", the name a plain file name with no separators or dots-only parts.
    internal static void ValidateRelative(string relative)
    {
        string[] parts = relative.Split('\\');
        bool shapeOk = parts.Length switch
        {
            1 => parts[0].EndsWith(".admx", StringComparison.OrdinalIgnoreCase),
            2 => string.Equals(parts[0], "en-US", StringComparison.Ordinal) && parts[1].EndsWith(".adml", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
        string name = parts[^1];
        if (!shapeOk || name.Length < 6 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"template path '{relative}' is not Name.admx or en-US\\Name.adml.");
        }
    }
}

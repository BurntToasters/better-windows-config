using System.Text.Json.Nodes;

namespace Bwc.Oracle.Capture;

// Read-only view of docs/parity/oracle/regedit-resources.json (written by `resources`). The captures name what they saw
// by resource id: the leaf command ids of a menu template, the control ids of a dialog template, the accelerator table.
internal sealed class ResourceDump
{
    private readonly JsonObject _module;

    private ResourceDump(JsonObject module)
    {
        _module = module;
    }

    internal static ResourceDump Load(RepoPaths paths)
    {
        if (!File.Exists(paths.ResourcesJson))
        {
            throw new FileNotFoundException("regedit-resources.json is missing. Run `npm run oracle -- --only resources` first.", paths.ResourcesJson);
        }

        JsonObject root = JsonNode.Parse(File.ReadAllText(paths.ResourcesJson))?.AsObject()
            ?? throw new InvalidDataException("regedit-resources.json is empty.");
        JsonObject module = root["modules"]?.AsArray().FirstOrDefault()?.AsObject()
            ?? throw new InvalidDataException("regedit-resources.json has no modules[0].");
        return new ResourceDump(module);
    }

    // Sorted leaf command ids of a menu template (popups and separators excluded), at every depth.
    internal List<int> MenuLeafIds(int templateId)
    {
        JsonObject menu = FindById("menus", templateId);
        var ids = new List<int>();
        CollectLeaves(menu["items"]!.AsArray(), ids);
        ids.Sort();
        return ids;
    }

    private static void CollectLeaves(JsonArray items, List<int> ids)
    {
        foreach (JsonNode? node in items)
        {
            JsonObject item = node!.AsObject();
            if (item["separator"]!.GetValue<bool>())
            {
                continue;
            }

            if (item["popup"]!.GetValue<bool>() && item["children"] is JsonArray children)
            {
                CollectLeaves(children, ids);
                continue;
            }

            int id = (int)item["id"]!.GetValue<long>();
            if (id > 0)
            {
                ids.Add(id);
            }
        }
    }

    // Sorted control ids of a dialog template, duplicates kept (several controls share 65535).
    internal List<int> DialogControlIds(int templateId)
    {
        JsonObject dialog = FindById("dialogs", templateId);
        var ids = dialog["controls"]!.AsArray().Select(c => (int)c!["id"]!.GetValue<long>()).ToList();
        ids.Sort();
        return ids;
    }

    // The dialog template whose control ids equal the given sorted ids, or null. Templates 102 (Edit String) and 103 (Edit
    // Binary Value) have the same ids, so caption breaks the tie against the template's title. An unresolved tie is null.
    internal int? MatchDialog(IReadOnlyList<int> sortedControlIds, string? caption = null)
    {
        var matches = new List<(int Id, string Title)>();
        foreach (JsonNode? node in _module["dialogs"]!.AsArray())
        {
            int id = (int)node!["id"]!.GetValue<long>();
            if (DialogControlIds(id).SequenceEqual(sortedControlIds))
            {
                matches.Add((id, node["title"]?.GetValue<string>() ?? string.Empty));
            }
        }

        if (matches.Count <= 1)
        {
            return matches.Count == 1 ? matches[0].Id : null;
        }

        if (caption is null)
        {
            return null;
        }

        var titled = matches.Where(m => m.Title == caption).ToList();
        return titled.Count == 1 ? titled[0].Id : null;
    }

    // The menu template whose leaf ids equal the given sorted ids, or null.
    internal int? MatchMenu(IReadOnlyList<int> sortedLeafIds)
    {
        foreach (JsonNode? node in _module["menus"]!.AsArray())
        {
            int id = (int)node!["id"]!.GetValue<long>();
            if (MenuLeafIds(id).SequenceEqual(sortedLeafIds))
            {
                return id;
            }
        }

        return null;
    }

    // Accelerator entries of the first accelerator table: virtual key, flags and the command id they post.
    internal List<(int Key, int Flags, int Command)> Accelerators()
    {
        var list = new List<(int Key, int Flags, int Command)>();
        JsonArray table = _module["accelerators"]!.AsArray();
        foreach (JsonNode? entry in table[0]!["entries"]!.AsArray())
        {
            int flags = Convert.ToInt32(entry!["flags"]!.GetValue<string>(), 16);
            list.Add(((int)entry["key"]!.GetValue<long>(), flags, (int)entry["command"]!.GetValue<long>()));
        }

        return list;
    }

    // Flag bits of an accelerator entry (winuser.h): FVIRTKEY 1, FSHIFT 4, FCONTROL 8, FALT 16.
    internal static int AccelShift => 4;

    internal static int AccelAlt => 16;

    internal static int AccelControl => 8;

    private JsonObject FindById(string collection, int templateId)
    {
        foreach (JsonNode? node in _module[collection]!.AsArray())
        {
            if ((int)node!["id"]!.GetValue<long>() == templateId)
            {
                return node.AsObject();
            }
        }

        throw new InvalidDataException($"regedit-resources.json has no {collection} template {templateId}.");
    }
}

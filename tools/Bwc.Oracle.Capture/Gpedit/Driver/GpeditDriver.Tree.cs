using System.Text.Json.Nodes;
using System.Windows.Automation;
using Bwc.Oracle.Capture.Gpedit.Probe;

namespace Bwc.Oracle.Capture.Gpedit.Driver;

// Console tree: select a node by path under Computer Configuration or User Configuration, or list a node's children.
// Failure modes 1 and 2 in docs/failure-modes/oracle-gpedit-driver.md.
internal sealed partial class GpeditDriver
{
    internal static string RootName(string root) => root == "User" ? "User Configuration" : "Computer Configuration";

    internal AutomationElement FindTree() =>
        ProbeUia.OfType(Main(), ControlType.Tree).FirstOrDefault()
            ?? ProbeUia.OfClass(Main(), "SysTreeView32").FirstOrDefault()
            ?? throw new DriverException("no console tree control found");

    private static List<AutomationElement> ChildItems(AutomationElement parent) =>
        ProbeUia.OfType(parent, ControlType.TreeItem, TreeScope.Children);

    // The selected node as its names from the first item (Local Computer Policy) down, or null when nothing is selected.
    internal List<string>? SelectedPath()
    {
        AutomationElement tree = FindTree();
        AutomationElement? selected = tree.FindFirst(
            TreeScope.Descendants,
            new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem),
                new PropertyCondition(SelectionItemPattern.IsSelectedProperty, true)));
        if (selected is null)
        {
            return null;
        }

        var names = new List<string>();
        AutomationElement? current = selected;
        for (int i = 0; i < 40 && current is not null && current.Current.ControlType == ControlType.TreeItem; i++)
        {
            names.Insert(0, current.Current.Name ?? string.Empty);
            current = TreeWalker.ControlViewWalker.GetParent(current);
        }

        return names;
    }

    private static bool PathMatches(List<string>? selected, string root, IReadOnlyList<string> path)
    {
        if (selected is null || selected.Count != path.Count + 2)
        {
            return false;
        }

        if (!string.Equals(selected[1], RootName(root), StringComparison.Ordinal))
        {
            return false;
        }

        for (int i = 0; i < path.Count; i++)
        {
            if (!string.Equals(selected[i + 2], path[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    // The direct child tree item with exactly this name. A node fills lazily after it is expanded: names are polled until the child
    // appears; the wait ends early when the list of names stopped changing (3 s, or 8 s while the node still shows no child).
    private AutomationElement WaitChild(AutomationElement parent, string name, string where)
    {
        DateTime start = DateTime.UtcNow;
        DateTime lastChange = start;
        List<string>? previous = null;
        while (true)
        {
            ctx.ThrowIfCancelled();
            ThrowIfDead();
            List<AutomationElement> items = ChildItems(parent);
            var names = items.Select(ProbeUia.NameOf).ToList();
            for (int i = 0; i < names.Count; i++)
            {
                if (string.Equals(names[i], name, StringComparison.Ordinal))
                {
                    return items[i];
                }
            }

            if (previous is null || !previous.SequenceEqual(names))
            {
                previous = names;
                lastChange = DateTime.UtcNow;
            }

            double stable = (DateTime.UtcNow - lastChange).TotalMilliseconds;
            double limit = names.Count == 0 ? 8000 : 3000;
            if (stable >= limit || (DateTime.UtcNow - start).TotalSeconds >= 20)
            {
                throw new DriverException(
                    $"no tree node '{name}' under '{where}'. Children: [{string.Join(" | ", names)}]",
                    evidence: new JsonArray(names.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()));
            }

            Thread.Sleep(300);
        }
    }

    // The tree item of the node, with each parent expanded on the way. Expansion uses ExpandCollapse only.
    private AutomationElement ResolveNode(string root, IReadOnlyList<string> path, JsonArray? hops)
    {
        AutomationElement tree = FindTree();
        AutomationElement current = ChildItems(tree).FirstOrDefault() ?? throw new DriverException("the console tree has no root item");
        string where = ProbeUia.NameOf(current);
        hops?.Add(ProbeUia.NameOf(current));
        foreach (string segment in new[] { RootName(root) }.Concat(path))
        {
            ProbeUia.Expand(current);
            current = WaitChild(current, segment, where);
            where += "/" + segment;
            hops?.Add(segment);
        }

        return current;
    }

    // Selects the node, checks the selection by reading it back, and waits for the result list to settle.
    internal JsonObject SelectNode(string root, IReadOnlyList<string> path)
    {
        var hops = new JsonArray();
        AutomationElement item = ResolveNode(root, path, hops);
        string via = "SelectionItem";
        if (!ProbeUia.Select(item))
        {
            ProbeUia.Click(item, doubleClick: false);
            via = "mouse click";
        }

        List<string>? selected = ProbeUia.Wait(
            () => SelectedPath() is { } p && PathMatches(p, root, path) ? p : null,
            TimeSpan.FromSeconds(6));
        if (selected is null)
        {
            List<string>? actual = SelectedPath();
            throw new DriverException(
                $"the node was not selected (selected: '{string.Join("/", actual ?? new List<string>())}', wanted '{RootName(root)}/{string.Join("/", path)}')");
        }

        Thread.Sleep(400);
        bool stable = WaitListStable(TimeSpan.FromSeconds(8));
        if (SelectedPath() is not { } after || !PathMatches(after, root, path))
        {
            throw new DriverException("the tree selection moved away while the list was loading");
        }

        return new JsonObject
        {
            ["node"] = RootName(root) + "/" + string.Join("/", path),
            ["hops"] = hops,
            ["selectedVia"] = via,
            ["listStable"] = stable,
        };
    }

    // The selected node must be this one; used by operations that name a node, after SelectNode or instead of it when already there.
    internal bool IsSelected(string root, IReadOnlyList<string> path) => PathMatches(SelectedPath(), root, path);

    // The children of a node by name, without selecting it. The node is expanded; names are read until they stop changing.
    internal JsonObject TreeChildren(string root, IReadOnlyList<string> path)
    {
        AutomationElement item = ResolveNode(root, path, null);
        ProbeUia.Expand(item);
        DateTime start = DateTime.UtcNow;
        DateTime lastChange = start;
        List<string>? previous = null;
        while (true)
        {
            ctx.ThrowIfCancelled();
            var names = ChildItems(item).Select(ProbeUia.NameOf).ToList();
            if (previous is null || !previous.SequenceEqual(names))
            {
                previous = names;
                lastChange = DateTime.UtcNow;
            }

            double stable = (DateTime.UtcNow - lastChange).TotalMilliseconds;
            if (stable >= (names.Count == 0 ? 5000 : 1500) || (DateTime.UtcNow - start).TotalSeconds >= 20)
            {
                return new JsonObject
                {
                    ["node"] = RootName(root) + "/" + string.Join("/", path),
                    ["children"] = new JsonArray(names.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
                    ["count"] = names.Count,
                };
            }

            Thread.Sleep(300);
        }
    }
}

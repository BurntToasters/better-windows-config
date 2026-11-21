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

    // Classic can crash when an expand arrives while it still handles a save, so each expand waits for its input queue to be idle first.
    // Failure mode 27 in docs/failure-modes/oracle-gpedit-driver.md.
    private void WaitIdle()
    {
        ThrowIfDead();
        bool idle;
        try
        {
            idle = mmc.WaitForInputIdle(30000);
        }
        catch (InvalidOperationException)
        {
            ThrowIfDead();
            throw;
        }

        if (!idle)
        {
            Note("classic was not idle 30 s before a tree expand; the expand ran anyway");
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
            WaitIdle();
            ProbeUia.Expand(current);
            current = WaitChild(current, segment, where);
            where += "/" + segment;
            hops?.Add(segment);
        }

        return current;
    }

    // Expands Computer Configuration and then User Configuration once, right after classic started and before any save, so no root is
    // expanded for the first time after a save (classic crashed that way). Only the two root items are expanded, never Administrative
    // Templates or anything deeper, and the selection is not changed. A dialog of classic that is open is neither dismissed nor worked
    // around: that root is skipped and the reason recorded. A failure throws (a start error of the case). Failure mode 31 of the driver document.
    internal JsonObject WarmUpRoots()
    {
        var roots = new JsonArray();
        bool skipped = false;
        foreach (string root in GpeditOp.Roots)
        {
            ctx.ThrowIfCancelled();
            ThrowIfDead();
            List<WinInfo> open = Dialogs();
            if (open.Count > 0)
            {
                skipped = true;
                roots.Add(new JsonObject
                {
                    ["root"] = RootName(root),
                    ["skipped"] = true,
                    ["reason"] = "a dialog of classic is open (" + string.Join("; ", open.Select(w => $"{Classify(w)}, class {w.Class}")) + "); the warm-up neither expands nor dismisses it",
                });
                continue;
            }

            roots.Add(WarmUpRoot(root));
        }

        return new JsonObject { ["skipped"] = skipped, ["roots"] = roots };
    }

    private JsonObject WarmUpRoot(string root)
    {
        DateTime start = DateTime.UtcNow;
        string name = RootName(root);
        AutomationElement item = ResolveNode(root, Array.Empty<string>(), null);
        if (!item.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object? pattern))
        {
            throw new DriverException($"the tree item '{name}' has no ExpandCollapse pattern, so it was not expanded at the start");
        }

        var expandCollapse = (ExpandCollapsePattern)pattern;
        string before = expandCollapse.Current.ExpandCollapseState.ToString();

        // A root that is already Expanded and already shows a child needs no expand and no stability wait. Expanded alone is not enough:
        // classic can report it before the children are loaded, so a root without a child takes the full path below (failure mode 32).
        if (before == nameof(ExpandCollapseState.Expanded))
        {
            int existing = ChildItems(item).Count;
            if (existing > 0)
            {
                return new JsonObject
                {
                    ["root"] = name,
                    ["stateBefore"] = before,
                    ["stateAfter"] = expandCollapse.Current.ExpandCollapseState.ToString(),
                    ["children"] = existing,
                    ["ms"] = (int)(DateTime.UtcNow - start).TotalMilliseconds,
                    ["skippedWait"] = true,
                    ["skippedWaitReason"] = "the root was already Expanded and showed " + existing + " child item(s) before the warm-up",
                };
            }
        }

        WaitIdle();
        ProbeUia.Expand(item);

        // The root is expanded when it shows children and the list of names stopped changing.
        DateTime lastChange = DateTime.UtcNow;
        List<string>? previous = null;
        while (true)
        {
            ctx.ThrowIfCancelled();
            ThrowIfDead();
            var names = ChildItems(item).Select(ProbeUia.NameOf).ToList();
            if (previous is null || !previous.SequenceEqual(names))
            {
                previous = names;
                lastChange = DateTime.UtcNow;
            }

            if (names.Count > 0 && (DateTime.UtcNow - lastChange).TotalMilliseconds >= 1500)
            {
                return new JsonObject
                {
                    ["root"] = name,
                    ["stateBefore"] = before,
                    ["stateAfter"] = expandCollapse.Current.ExpandCollapseState.ToString(),
                    ["children"] = names.Count,
                    ["ms"] = (int)(DateTime.UtcNow - start).TotalMilliseconds,
                };
            }

            if ((DateTime.UtcNow - start).TotalSeconds >= 60)
            {
                throw new DriverException(
                    $"'{name}' showed {(names.Count == 0 ? "no children" : "a child list that kept changing")} 60 s after it was expanded at the start (state before {before}, now {expandCollapse.Current.ExpandCollapseState})");
            }

            Thread.Sleep(300);
        }
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
        WaitIdle();
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

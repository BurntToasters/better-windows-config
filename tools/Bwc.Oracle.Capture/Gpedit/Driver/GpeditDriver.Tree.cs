using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using Bwc.Oracle.Capture.Gpedit.Probe;

namespace Bwc.Oracle.Capture.Gpedit.Driver;

// Console tree: select a node by path under Computer Configuration or User Configuration, or list a node's children.
// Failure modes 1 and 2 in docs/failure-modes/oracle-gpedit-driver.md.
internal sealed partial class GpeditDriver
{
    internal static string RootName(string root) => root == "User" ? "User Configuration" : "Computer Configuration";

    internal AutomationElement FindTree() => FindTree(null);

    // The same two lookups in the same order as ever; times, when given, only has a stopwatch around them (failure mode 33).
    private AutomationElement FindTree(HopTimes? times)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            AutomationElement? tree = ProbeUia.OfType(Main(), ControlType.Tree).FirstOrDefault();
            if (tree is null)
            {
                if (times is not null)
                {
                    times.FindTreeViaClass = true;
                }

                tree = ProbeUia.OfClass(Main(), "SysTreeView32").FirstOrDefault();
            }

            return tree ?? throw new DriverException("no console tree control found");
        }
        finally
        {
            if (times is not null)
            {
                times.FindTreeMs = HopTimes.Ms(start);
            }
        }
    }

    private static List<AutomationElement> ChildItems(AutomationElement parent) =>
        ProbeUia.OfType(parent, ControlType.TreeItem, TreeScope.Children);

    private static List<AutomationElement> ChildItems(AutomationElement parent, HopStep? step)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            return ChildItems(parent);
        }
        finally
        {
            step?.AddChildItems(HopTimes.Ms(start));
        }
    }

    // The names of the items, read one by one as always; the time of the reads goes to the step.
    private static List<string> NamesOf(List<AutomationElement> items, HopStep? step)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            return items.Select(ProbeUia.NameOf).ToList();
        }
        finally
        {
            if (step is not null)
            {
                step.NameOfMs += HopTimes.Ms(start);
            }
        }
    }

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
    private AutomationElement WaitChild(AutomationElement parent, string name, string where, HopStep? step = null)
    {
        DateTime start = DateTime.UtcNow;
        DateTime lastChange = start;
        List<string>? previous = null;
        while (true)
        {
            ctx.ThrowIfCancelled();
            ThrowIfDead();
            if (step is not null)
            {
                step.Polls++;
            }

            List<AutomationElement> items = ChildItems(parent, step);
            List<string> names = NamesOf(items, step);
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

            long slept = Stopwatch.GetTimestamp();
            Thread.Sleep(300);
            if (step is not null)
            {
                step.SleptMs += HopTimes.Ms(slept);
            }
        }
    }

    // Classic can crash when an expand arrives while it still handles a save, so each expand waits for its input queue to be idle first.
    // Failure mode 27 in docs/failure-modes/oracle-gpedit-driver.md.
    private bool WaitIdle()
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

        return idle;
    }

    // The tree item of the node, with each parent expanded on the way. Expansion uses ExpandCollapse only.
    // times, when given, collects where each step of each hop spent its time (failure mode 33); it changes no call and no wait.
    private AutomationElement ResolveNode(string root, IReadOnlyList<string> path, JsonArray? hops, HopTimes? times = null)
    {
        AutomationElement tree = FindTree(times);
        HopStep? rootStep = times?.NextHop("(root item)");
        AutomationElement current;
        string where;
        try
        {
            current = ChildItems(tree, rootStep).FirstOrDefault() ?? throw new DriverException("the console tree has no root item");
            long named = Stopwatch.GetTimestamp();
            where = ProbeUia.NameOf(current);
            hops?.Add(ProbeUia.NameOf(current));
            if (rootStep is not null)
            {
                rootStep.NameOfMs += HopTimes.Ms(named);
            }
        }
        finally
        {
            rootStep?.Finish();
        }

        foreach (string segment in new[] { RootName(root) }.Concat(path))
        {
            HopStep? step = times?.NextHop(segment);
            try
            {
                long idleStart = Stopwatch.GetTimestamp();
                try
                {
                    bool idle = WaitIdle();
                    if (step is not null)
                    {
                        step.WaitIdleReturnedIdle = idle;
                    }
                }
                finally
                {
                    if (step is not null)
                    {
                        step.WaitIdleMs = HopTimes.Ms(idleStart);
                    }
                }

                ProbeUia.Expand(current, 4000, step?.Expand);
                current = WaitChild(current, segment, where, step);
                where += "/" + segment;
                hops?.Add(segment);
            }
            finally
            {
                step?.Finish();
            }
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
        var times = new HopTimes("warm-up " + name);
        HopTimings.Add(times);
        AutomationElement item = ResolveNode(root, Array.Empty<string>(), null, times);
        ExpandCollapsePattern expandCollapse = times.Time("rootPattern", () =>
            item.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object? pattern)
                ? (ExpandCollapsePattern)pattern!
                : throw new DriverException($"the tree item '{name}' has no ExpandCollapse pattern, so it was not expanded at the start"));
        string before = times.Time("rootState", () => expandCollapse.Current.ExpandCollapseState.ToString());

        // A root that is already Expanded and already shows a child needs no expand and no stability wait. Expanded alone is not enough:
        // classic can report it before the children are loaded, so a root without a child takes the full path below (failure mode 32).
        if (before == nameof(ExpandCollapseState.Expanded))
        {
            int existing = times.Time("existingChildItems", () => ChildItems(item).Count);
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
                    ["resolve"] = times.ToJson(),
                };
            }
        }

        times.Time("waitIdle", () => WaitIdle());
        var expandTiming = new ExpandTiming();
        times.Time("expand", () => ProbeUia.Expand(item, 4000, expandTiming));

        // The root is expanded when it shows children and the list of names stopped changing.
        DateTime lastChange = DateTime.UtcNow;
        List<string>? previous = null;
        while (true)
        {
            ctx.ThrowIfCancelled();
            ThrowIfDead();
            List<string> names = times.Time("pollChildItemsAndNames", () => ChildItems(item).Select(ProbeUia.NameOf).ToList());
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
                    ["resolve"] = times.ToJson(),
                    ["expandTiming"] = expandTiming.ToJson(),
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
        var times = new HopTimes("selectNode " + RootName(root) + "/" + string.Join("/", path));
        HopTimings.Add(times);
        AutomationElement item = ResolveNode(root, path, hops, times);
        string via = "SelectionItem";
        if (!times.Time("select", () => ProbeUia.Select(item)))
        {
            times.Time("clickFallback", () => ProbeUia.Click(item, doubleClick: false));
            via = "mouse click";
        }

        long waitStart = Stopwatch.GetTimestamp();
        List<string>? selected = ProbeUia.Wait(
            () => times.Time("selectedPathWhileWaiting", () => SelectedPath()) is { } p && PathMatches(p, root, path) ? p : null,
            TimeSpan.FromSeconds(6));
        times.Record("waitForSelection", HopTimes.Ms(waitStart));
        if (selected is null)
        {
            List<string>? actual = SelectedPath();
            throw new DriverException(
                $"the node was not selected (selected: '{string.Join("/", actual ?? new List<string>())}', wanted '{RootName(root)}/{string.Join("/", path)}')");
        }

        times.Time("pause400", () => Thread.Sleep(400));
        bool stable = times.Time("waitListStable", () => WaitListStable(TimeSpan.FromSeconds(8), times));
        if (times.Time("selectedPathFinal", () => SelectedPath()) is not { } after || !PathMatches(after, root, path))
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
        var times = new HopTimes("treeChildren " + RootName(root) + "/" + string.Join("/", path));
        HopTimings.Add(times);
        AutomationElement item = ResolveNode(root, path, null, times);
        times.Time("waitIdle", () => WaitIdle());
        times.Time("expand", () => ProbeUia.Expand(item));
        DateTime start = DateTime.UtcNow;
        DateTime lastChange = start;
        List<string>? previous = null;
        while (true)
        {
            ctx.ThrowIfCancelled();
            List<string> names = times.Time("pollChildItemsAndNames", () => ChildItems(item).Select(ProbeUia.NameOf).ToList());
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

using System.Diagnostics;
using System.Text.Json.Nodes;
using Bwc.Oracle.Capture.Gpedit.Probe;

namespace Bwc.Oracle.Capture.Gpedit.Driver;

// Where one tree resolution (and, for SelectNode, the steps after it) spends its time. Only measures: a stopwatch around calls that
// were made anyway, no new UI Automation call, no wait. The detail goes to the untracked result file next to `phases`, never into an
// operation's `data`, so no timing can reach a golden. Failure mode 33 in docs/failure-modes/oracle-gpedit-driver.md.
internal sealed class HopTimes
{
    private const int MaxSteps = 60;
    private const int MaxRoutes = 40;

    private readonly List<HopStep> hops = new();
    private readonly List<KeyValuePair<string, long>> steps = new();
    private readonly Dictionary<string, long[]> totals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> routes = new(StringComparer.Ordinal);
    private int droppedSteps;

    internal HopTimes(string node)
    {
        Node = node;
    }

    internal string Node { get; }

    internal long FindTreeMs { get; set; }

    internal bool FindTreeViaClass { get; set; }

    // Which lookup found the tree for the resolution: "hwnd", "uiaType" or "uiaClass" (failure mode 34). Null until a lookup ran.
    internal string? FindTreeVia { get; set; }

    // Counts a route that a fast lookup took or fell back with (failure mode 34). The distinct names are capped, the counts are not.
    internal void CountRoute(string name)
    {
        if (!routes.TryGetValue(name, out int count) && routes.Count >= MaxRoutes)
        {
            return;
        }

        routes[name] = count + 1;
    }

    internal HopStep NextHop(string segment)
    {
        var step = new HopStep(segment);
        hops.Add(step);
        return step;
    }

    internal static long Ms(long startTicks) => (long)Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;

    // Runs the call and records how long it took under the name, also when it throws.
    internal T Time<T>(string name, Func<T> call)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            return call();
        }
        finally
        {
            Record(name, Ms(start));
        }
    }

    internal void Time(string name, Action call) => Time(name, () =>
    {
        call();
        return true;
    });

    // A step outside the hops (the select call, a read of the selection, the pause, the wait for the list). The list is capped; the
    // totals per name are not, so a long poll still adds up.
    internal void Record(string name, long ms)
    {
        if (steps.Count < MaxSteps)
        {
            steps.Add(new KeyValuePair<string, long>(name, ms));
        }
        else
        {
            droppedSteps++;
        }

        if (!totals.TryGetValue(name, out long[]? total))
        {
            total = new long[2];
            totals[name] = total;
        }

        total[0]++;
        total[1] += ms;
    }

    // Never throws: a failure here drops the detail and must not cost an operation its result.
    internal JsonObject? ToJson()
    {
        try
        {
            var json = new JsonObject
            {
                ["node"] = Node,
                ["findTreeMs"] = FindTreeMs,
                ["findTreeViaClass"] = FindTreeViaClass,
                ["findTreeVia"] = FindTreeVia,
                ["routes"] = new JsonObject(routes.Select(r => new KeyValuePair<string, JsonNode?>(r.Key, r.Value))),
                ["hops"] = new JsonArray(hops.Select(h => (JsonNode?)h.ToJson()).ToArray()),
                ["afterHops"] = new JsonArray(steps.Select(s => (JsonNode?)new JsonObject { ["name"] = s.Key, ["ms"] = s.Value }).ToArray()),
            };
            if (droppedSteps > 0)
            {
                json["afterHopsDropped"] = droppedSteps;
            }

            json["afterHopsTotals"] = new JsonObject(totals.Select(t => new KeyValuePair<string, JsonNode?>(t.Key, new JsonObject { ["count"] = t.Value[0], ["ms"] = t.Value[1] })));
            return json;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}

// One hop of ResolveNode: wait for idle, expand the parent, poll for the child.
internal sealed class HopStep
{
    private const int MaxListed = 20;

    private readonly long startTicks = Stopwatch.GetTimestamp();
    private readonly List<long> childItemsMs = new();
    private long childItemsTotal;
    private int childItemsCalls;
    private long finishedMs = -1;

    internal HopStep(string segment)
    {
        Segment = segment;
    }

    internal string Segment { get; }

    internal long WaitIdleMs { get; set; }

    internal bool? WaitIdleReturnedIdle { get; set; }

    internal ExpandTiming Expand { get; } = new();

    internal long NameOfMs { get; set; }

    internal int Polls { get; set; }

    internal long SleptMs { get; set; }

    // The Expand call was made (the item was Collapsed).
    internal bool? ExpandCalled => Expand.HasPattern ? Expand.Called : null;

    internal void AddChildItems(long ms)
    {
        childItemsCalls++;
        childItemsTotal += ms;
        if (childItemsMs.Count < MaxListed)
        {
            childItemsMs.Add(ms);
        }
    }

    internal void Finish() => finishedMs = HopTimes.Ms(startTicks);

    internal JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["segment"] = Segment,
            ["totalMs"] = finishedMs >= 0 ? finishedMs : HopTimes.Ms(startTicks),
            ["waitIdleMs"] = WaitIdleMs,
            ["waitIdleReturnedIdle"] = WaitIdleReturnedIdle,
            ["expandCalled"] = ExpandCalled,
            ["expand"] = Expand.ToJson(),
            ["childItemsCalls"] = childItemsCalls,
            ["childItemsMs"] = new JsonArray(childItemsMs.Select(m => (JsonNode?)m).ToArray()),
            ["childItemsTotalMs"] = childItemsTotal,
            ["nameOfMs"] = NameOfMs,
            ["pollCount"] = Polls,
            ["sleptMs"] = SleptMs,
        };
        return json;
    }
}

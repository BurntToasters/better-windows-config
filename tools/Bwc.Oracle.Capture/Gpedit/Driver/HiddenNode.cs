using System.Text.Json.Nodes;

namespace Bwc.Oracle.Capture.Gpedit.Driver;

// What the tree knew when a segment of a node path was missing: the segment, the path (below the root configuration node) of the parent that was
// found, and the children that parent had. Carried by the DriverException of the selection.
internal sealed record TreeGap(string Segment, IReadOnlyList<string> ParentPath, IReadOnlyList<string> Children);

// A node that classic hides under a filter is a result and not a failure (failure mode 42 in docs/failure-modes/oracle-gpedit-driver.md). These are
// the decisions and the shape of that result, on plain values.
internal static class HiddenNode
{
    // A selection that failed because a segment was missing under a parent is a hidden node only when Filter On is known to be checked.
    internal static bool IsHidden(TreeGap? gap, bool? filterOn) => gap is not null && filterOn == true;

    // The result of an operation that names a node that is hidden. selected is the path of the selected node below the root, or null.
    internal static JsonObject Describe(TreeGap gap, IReadOnlyList<string>? selected) => new()
    {
        ["nodeHidden"] = true,
        ["filterOn"] = true,
        ["segment"] = gap.Segment,
        ["parentPath"] = Names(gap.ParentPath),
        ["parentChildren"] = Names(gap.Children),
        ["parentChildCount"] = gap.Children.Count,
        ["selectedPath"] = selected is null ? null : Names(selected),
    };

    // The paths to try when the node is hidden, nearest first: the parent, its parent, and so on up to the root configuration node (the empty path).
    internal static IEnumerable<IReadOnlyList<string>> AncestorsNearestFirst(TreeGap gap)
    {
        for (int count = gap.ParentPath.Count; count >= 0; count--)
        {
            yield return gap.ParentPath.Take(count).ToList();
        }
    }

    internal static JsonArray Names(IEnumerable<string> names) => new(names.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
}

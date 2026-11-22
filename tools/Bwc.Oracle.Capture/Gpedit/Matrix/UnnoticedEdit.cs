using System.Text.Json.Nodes;
using Bwc.Oracle.Capture.Gpedit.Driver;

namespace Bwc.Oracle.Capture.Gpedit.Matrix;

// An edit the setting dialog never noticed (CAP-4): a number box set without keys on a policy that is already Enabled. Classic then keeps Apply
// disabled and OK writes nothing, where a person who types the number gets a write. Two checks on plain values, so the self test covers them
// without classic. Failure mode 21 in docs/failure-modes/oracle-gpedit-matrix.md.
internal static class UnnoticedEdit
{
    // The decision on its four facts. A set changed the text of a control, no state set changed the radio, Apply was disabled after the sets,
    // and no message box refused the edit.
    internal static bool IsUnnoticed(bool textChanged, bool stateChanged, bool applyDisabledAfterSet, bool boxSeen) =>
        textChanged && !stateChanged && applyDisabledAfterSet && !boxSeen;

    // The result of an editPolicy operation (the data of its entry). Null when the edit was noticed or when the facts are not all known: an
    // operation that failed before its presses has no boxes to look at, one with no afterSet had no set. Otherwise the text of the failure.
    internal static string? Find(JsonObject data)
    {
        if (data["sets"] is not JsonArray sets || data["presses"] is not JsonArray presses || data["afterSet"] is not JsonObject afterSet)
        {
            return null;
        }

        bool applyDisabled = afterSet["buttons"]?["Apply"]?["enabled"] is JsonValue apply && apply.TryGetValue(out bool enabled) && !enabled;
        bool stateChanged = false;
        string? firstChange = null;
        foreach (JsonNode? node in sets)
        {
            if (node is not JsonObject set)
            {
                continue;
            }

            string kind = Text(set["set"]);
            if (kind == "state")
            {
                stateChanged |= Text(set["before"]) != Text(set["observed"]);
            }
            else if (kind == "option" && set["before"] is JsonValue before && set["observed"] is JsonValue observed
                && before.TryGetValue(out string? from) && observed.TryGetValue(out string? to) && from != to)
            {
                firstChange ??= $"'{Text(set["label"])}' went from '{from}' to '{to}'";
            }
        }

        int boxes = 0;
        foreach (JsonNode? node in presses)
        {
            boxes += (node?["messageBoxes"] as JsonArray)?.Count ?? 0;
            boxes += (node?["boxesBeforePress"] as JsonArray)?.Count ?? 0;
        }

        return IsUnnoticed(firstChange is not null, stateChanged, applyDisabled, boxes > 0)
            ? $"the dialog did not notice the edit: {firstChange}, the state did not change, Apply stayed disabled and no message box refused it, so a press writes nothing where a person typing gets a write (a number box set without keys on a policy that is already Enabled, failure mode 21 of the matrix document)"
            : null;
    }

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text ?? string.Empty : string.Empty;

    // Number boxes that have a range are Windows Forms number controls that take no notice of WM_SETTEXT. Both limits make the range.
    internal static bool NeedsKeys(ElementInfo element) => element.Kind is "decimal" or "longDecimal" && element.Min is not null && element.Max is not null;

    // The plan problems of one case: a step that only sets number boxes with a range, without keys, on a policy that the same case already Enabled
    // in the same root. A policy is judged only after an operation of the case set its state, so a crafted start is never guessed at.
    internal static List<string> PlanProblems(MatrixCase c, FixtureModel fixture)
    {
        var problems = new List<string>();
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < c.Ops.Count; i++)
        {
            GpeditOp op = c.Ops[i];
            if (op.Kind != "editPolicy")
            {
                continue;
            }

            string key = op.Root + "|" + (op.PolicyId ?? op.Policy);
            states.TryGetValue(key, out string? known);
            bool staysEnabled = known == "Enabled" && (op.State is null or "Enabled");
            if (staysEnabled && op.Comment is null && OnlyUnkeyedRanges(op, fixture))
            {
                problems.Add($"{c.FullId} op[{i}] editPolicy '{op.Policy}': only a number box with a range is set, without keys, on a policy that is already Enabled; classic does not notice it (failure mode 21 of the matrix document). Type it, or start the step from Not Configured");
            }

            if (op.State is not null)
            {
                states[key] = op.State;
            }
        }

        return problems;
    }

    private static bool OnlyUnkeyedRanges(GpeditOp op, FixtureModel fixture)
    {
        if (op.Options is not { Count: > 0 } options)
        {
            return false;
        }

        PolicyInfo? policy = fixture.Policies.FirstOrDefault(p => p.Name == op.PolicyId);
        return policy is not null && options.All(o =>
            o.Typed is not true && policy.Elements.FirstOrDefault(e => e.Id == o.ElementId) is { } element && NeedsKeys(element));
    }
}

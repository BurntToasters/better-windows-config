using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Bwc.Policy.Tests.Golden;

/// <summary>
/// The CAP-4 rule of the capture tool (tools/Bwc.Oracle.Capture/Gpedit/Matrix/UnnoticedEdit.cs,
/// <c>Find</c>) ported to the golden replay: a set changed an option's text, no state set changed the
/// radio, Apply was disabled after the sets and no message box refused the edit. Classic's dialog then
/// never noticed the edit, so the golden shows no write where a person who types the value gets one, and
/// the case must be recaptured. A golden stores the kind of a set as a hash of its name (the scrubber
/// hashes text that is not in the fixture), so the kinds are compared by their hashes.
/// </summary>
internal static class UnnoticedEditRule
{
    private static readonly string StateKind = Hash("state");
    private static readonly string OptionKind = Hash("option");

    internal static string Hash(string text) =>
        "#" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();

    /// <summary>The decision on its four facts.</summary>
    internal static bool IsUnnoticed(bool textChanged, bool stateChanged, bool applyDisabledAfterSet, bool boxSeen) =>
        textChanged && !stateChanged && applyDisabledAfterSet && !boxSeen;

    /// <summary>Applies the rule to the observed data of one editPolicy operation.</summary>
    /// <param name="data">The <c>data</c> member of an observed operation; null when there is none.</param>
    /// <returns>The failure text, or null when the edit was noticed or the facts are not all known.</returns>
    internal static string? Find(JsonObject? data)
    {
        if (data is null || data["sets"] is not JsonArray sets || data["presses"] is not JsonArray presses || data["afterSet"] is not JsonObject afterSet)
        {
            return null;
        }

        var applyDisabled = afterSet["buttons"]?["Apply"]?["enabled"] is JsonValue apply && apply.TryGetValue(out bool enabled) && !enabled;
        var stateChanged = false;
        string? firstChange = null;
        foreach (var node in sets)
        {
            if (node is not JsonObject set)
            {
                continue;
            }

            var kind = Text(set["set"]);
            if (kind == StateKind)
            {
                stateChanged |= Text(set["before"]) != Text(set["observed"]);
            }
            else if (kind == OptionKind && set["before"] is JsonValue before && set["observed"] is JsonValue observed
                && before.TryGetValue(out string? from) && observed.TryGetValue(out string? to) && from != to)
            {
                firstChange ??= $"an option went from '{from}' to '{to}'";
            }
        }

        var boxes = 0;
        foreach (var node in presses)
        {
            boxes += (node?["messageBoxes"] as JsonArray)?.Count ?? 0;
            boxes += (node?["boxesBeforePress"] as JsonArray)?.Count ?? 0;
        }

        return IsUnnoticed(firstChange is not null, stateChanged, applyDisabled, boxes > 0)
            ? $"CAP-4: the dialog did not notice the edit ({firstChange}), the state did not change, Apply stayed disabled and no message box refused it, so the golden shows no write where a person typing gets one; the case waits for a recapture"
            : null;
    }

    /// <summary>The first CAP-4 finding of any editPolicy operation of a case, or null.</summary>
    /// <param name="golden">The case.</param>
    /// <returns>The text with the operation index, or null.</returns>
    internal static string? FindInCase(GoldenCase golden)
    {
        foreach (var op in golden.Ops.Where(o => o.Kind == "editPolicy"))
        {
            if (Find(op.Data) is { } finding)
            {
                return $"{golden.Id} operation {op.Index}: {finding}";
            }
        }

        return null;
    }

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text ?? string.Empty : string.Empty;
}

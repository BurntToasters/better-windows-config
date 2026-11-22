using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Bwc.Oracle.Capture.Gpedit.Matrix;

// The only way text read from classic reaches a tracked file. A string is kept when it is a BWC fixture string, a string the case itself
// typed or named, a short label quoted in the plan, a driver token, a number, a hash or a key below the BWC-Oracle registry roots. Every other
// string becomes "#" plus the first 12 hex digits of its SHA-256, so the same text always gives the same value and nothing else is learned.
internal sealed partial class Scrubber
{
    // Short labels quoted in docs and the plan, and words the driver itself produces (no Microsoft sentence among them).
    private static readonly string[] Labels =
    {
        "Enabled", "Disabled", "Not Configured", "Not configured", "Computer Configuration", "User Configuration", "Local Computer Policy",
        "Administrative Templates", "All Settings", "Extra Registry Settings", "Software Settings", "Windows Settings", "Name Resolution Policy",
        "Scripts", "Deployed Printers", "Security Settings", "Policy-based QoS", "Setting", "State", "Comment", "OK", "Cancel", "Apply",
        "Previous", "Next", "Previous Setting", "Next Setting", "Filter Options", "Show Contents", "File", "Action", "View", "Help", "Extended", "Standard",
        "Any", "Yes", "No", "All", "Exact", "Close", "Retry", "Abort", "Ignore", "Try Again", "Continue",
        "#32770", "Button", "Static", "MessageBox", "ShowContents", "FilterOptions", "Setting", "Other", "(window closed)", "BM_CLICK", "Invoke",
        "WM_CLOSE", "exact", "ignoringAmpersand", "windowTitleExact", "normalized", "index", "unchecked", "Machine", "User", "Both",
    };

    private static readonly HashSet<string> DroppedKeys = new(StringComparer.Ordinal)
    {
        "ms", "settleMs", "waitMs", "waitedMs", "startedUtc", "finishedUtc", "atUtc", "lastWriteUtc", "lastWriteUtcOfChanged", "firstSeenMs", "history",
        "snapshot", "hex", "screenshot", "screenshotDialog", "notes", "evidence", "exception", "hwnd", "listHwnd", "tree", "uiaTexts", "uiaButtons",
        "beforeHashes", "afterHashes", "diff", "via", "selectedVia", "hops", "opened", "pid", "plan", "dpiAwareness", "className", "node",
        "gapPixels", "closedBy", "attributes", "epilogue", "epilogueError", "epilogueEvidence", "fileCheckError", "parts",
        // Timings and refusal evidence live beside the data in the untracked result; if one ever ends up inside data it is still dropped.
        "phases", "hopPhases", "timing", "startRefusal", "filesAfterRefusal",
        // Which reader gave a check box state, and what the test for a check box saw (failure mode 35 of the driver document).
        "checkStateVia", "checkEvidence",
        // What the test for the Show button of a list saw on each inner BUTTON (failure mode 36 of the driver document).
        "pushEvidence",
        // How the Show Contents grid was classified, and what each delete pass of a clear saw (failure mode 37 of the driver document).
        "gridRead", "clearPasses",
        // The route that closed the Show Contents dialog after a refused list (failure mode 38 of the driver document).
        "cancelClosedBy",
        // Which lookup route read the status bar, the tabs or the layout, and how a column header was brought into view (failure modes 40 and 41).
        "uiaRoute", "scrolledBy", "scrollSteps",
    };

    private readonly HashSet<string> allowed;
    private readonly bool keepRegistry;

    internal Scrubber(IEnumerable<string> fixtureStrings, IEnumerable<string> authored, bool keepRegistry)
    {
        allowed = new HashSet<string>(fixtureStrings.Concat(authored).Concat(Labels).Concat(Own()), StringComparer.Ordinal);
        this.keepRegistry = keepRegistry;
    }

    // The words this tool itself writes into case.json: operation kinds, file names, and its two fixed notes.
    private static IEnumerable<string> Own() => Driver.GpeditOp.KindNames
        .Concat(Probe.PolFiles.Names)
        .Concat(Probe.PolFiles.Names.Select(n => n.Replace('\\', '.')))
        .Concat(Enum.GetNames<Microsoft.Win32.RegistryValueKind>())
        .Concat(new[] { "depends on the machine's own templates", "present, untracked" });

    internal bool IsAllowed(string text) => text.Length == 0 || allowed.Contains(text) || Safe().IsMatch(text) || text.StartsWith('#') && HashForm().IsMatch(text);

    [GeneratedRegex(@"^(-?\d+|([0-9a-f]{2})+|0x[0-9A-Fa-f]+|(HKLM|HKCU)\\Software\\(Policies\\BWC-Oracle|BWC-Oracle-Gpedit)(\\.*)?)$")]
    private static partial Regex Safe();

    [GeneratedRegex(@"^#[0-9a-f]{12}$")]
    private static partial Regex HashForm();

    internal string S(string text)
    {
        if (IsAllowed(text))
        {
            return text;
        }

        return Hash(text);
    }

    internal static string Hash(string text) => "#" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();

    internal JsonNode? Clean(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                var result = new JsonObject();
                foreach (KeyValuePair<string, JsonNode?> pair in obj)
                {
                    if (DroppedKeys.Contains(pair.Key) || (!keepRegistry && pair.Key == "liveRegistry"))
                    {
                        continue;
                    }

                    result[pair.Key] = Clean(pair.Value);
                }

                return result;
            case JsonArray array:
                return new JsonArray(array.Select(Clean).ToArray());
            default:
                if (node is JsonValue value && value.TryGetValue(out string? text))
                {
                    return JsonValue.Create(S(text));
                }

                return node.DeepClone();
        }
    }

    // Every string below the node that would not survive Clean unchanged. An empty list is the check that nothing slipped through.
    internal List<string> Leaks(JsonNode? node, string path = "$")
    {
        var found = new List<string>();
        switch (node)
        {
            case JsonObject obj:
                foreach (KeyValuePair<string, JsonNode?> pair in obj)
                {
                    found.AddRange(Leaks(pair.Value, path + "." + pair.Key));
                }

                break;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    found.AddRange(Leaks(array[i], $"{path}[{i}]"));
                }

                break;
            case JsonValue value when value.TryGetValue(out string? text):
                if (!IsAllowed(text))
                {
                    found.Add($"{path}: {text}");
                }

                break;
        }

        return found;
    }
}

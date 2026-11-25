using System.Text.Json.Nodes;
using Bwc.Oracle.Capture.Gpedit.Driver;
using Bwc.Oracle.Capture.Gpedit.Matrix;

namespace Bwc.Oracle.Capture.Gpedit.Session;

// The decision "did classic refuse the list on OK of Show Contents", and the shape of that set entry in a golden. Failure mode 38 of the driver
// document. These cases were written before the code they check. The message box is built like the ones DismissMessageBox returns, with markers
// in place of what classic says.
internal static partial class SelfTest
{
    private static JsonObject FakeBox() => new()
    {
        ["title"] = "ZZ-MARKER-box-title",
        ["class"] = "#32770",
        ["kind"] = "MessageBox",
        ["texts"] = new JsonArray("ZZ-MARKER-box-text"),
        ["buttons"] = new JsonArray(new JsonObject { ["text"] = "OK", ["id"] = 2 }),
        ["uiaTexts"] = new JsonArray("ZZ-MARKER-uia-text"),
        ["uiaButtons"] = new JsonArray("OK"),
        ["screenshot"] = "ZZ-MARKER-shot.png",
        ["answeredWith"] = "OK",
        ["answeredVia"] = "BM_CLICK",
        ["answerRequested"] = null,
        ["answerFallback"] = false,
    };

    // The set entry of a list option as EditList returns it, with the close of OK and the fields of the refusal.
    private static JsonObject FakeListSet(bool openAfterBox, bool? cancelled, string? cancelRoute)
    {
        var close = new JsonObject
        {
            ["button"] = "OK",
            ["messageBoxes"] = new JsonArray(FakeBox()),
            ["blockedByMessageBox"] = true,
        };
        var entry = new JsonObject
        {
            ["before"] = new JsonObject { ["rowCount"] = 0 },
            ["clearPasses"] = new JsonArray(),
            ["cells"] = new JsonArray(),
            ["close"] = close,
        };
        ListRefusal.Apply(entry, new JsonArray(FakeBox()), openAfterBox, cancelled, cancelRoute, new JsonArray());
        entry["set"] = "option";
        entry["label"] = "Plain entries:";
        return entry;
    }

    private static void ListRefusalCases(RepoPaths repo, Action<string, Action> Case)
    {
        Case("list refusal: refused is a message box on OK with the dialog still open, and nothing else", () =>
        {
            Expect(ListRefusal.IsRefused(1, true), "a box with the dialog open was not a refusal");
            Expect(ListRefusal.IsRefused(2, true), "two boxes with the dialog open were not a refusal");
            Expect(!ListRefusal.IsRefused(1, false), "a box with the dialog closed was taken as a refusal");
            Expect(!ListRefusal.IsRefused(0, true), "no box with the dialog open was taken as a refusal");
            Expect(!ListRefusal.IsRefused(0, false), "no box and a closed dialog was taken as a refusal");
        });

        Case("list refusal: a refused set entry says refused, the box and the cancel, and nothing the scrubber must drop", () =>
        {
            JsonObject entry = FakeListSet(openAfterBox: true, cancelled: true, cancelRoute: "Cancel button");
            Expect(entry["okMessageBox"]?.GetValue<bool>() == true, "okMessageBox is missing");
            Expect(entry["refused"]?.GetValue<bool>() == true, "refused is not true");
            Expect(entry["dialogOpenAfterBox"]?.GetValue<bool>() == true, "dialogOpenAfterBox is not true");
            Expect(entry["cancelled"]?.GetValue<bool>() == true, "cancelled is not true");
            Expect(entry["cancelClosedBy"]?.GetValue<string>() == "Cancel button", "the cancel route is missing");
            Expect(entry["messageBoxesAfterCancel"] is null, "an empty list of boxes after Cancel was written");
        });

        Case("list refusal: the golden keeps the fact and the identity of the box, never its text or the diagnostics", () =>
        {
            var scrub = new Scrubber(MatrixPlan.Build(repo).Fixture.AllowedStrings, Array.Empty<string>(), false);
            JsonNode cleaned = scrub.Clean(new JsonObject { ["sets"] = new JsonArray(FakeListSet(true, true, "Cancel button")) })!;
            string text = cleaned.ToJsonString();
            Expect(!text.Contains("ZZ-MARKER", StringComparison.Ordinal), "text of the box or a diagnostic reached the golden data: " + text);
            Expect(!text.Contains("cancelClosedBy", StringComparison.Ordinal) && !text.Contains("Cancel button", StringComparison.Ordinal), "the cancel route reached the golden data");
            Expect(!text.Contains("uiaTexts", StringComparison.Ordinal) && !text.Contains("screenshot", StringComparison.Ordinal), "an untracked box field reached the golden data");
            JsonObject set = cleaned["sets"]![0]!.AsObject();
            Expect(set["refused"]?.GetValue<bool>() == true && set["okMessageBox"]?.GetValue<bool>() == true, "the refusal was lost from the golden data");
            Expect(set["dialogOpenAfterBox"]?.GetValue<bool>() == true && set["cancelled"]?.GetValue<bool>() == true, "the state of the dialog was lost from the golden data");
            JsonObject box = set["close"]!["messageBoxes"]![0]!.AsObject();
            Expect(box["title"]!.GetValue<string>().StartsWith('#') && box["title"]!.GetValue<string>().Length == 13, "the title of the box is not in the hash form");
            Expect(box["texts"]![0]!.GetValue<string>().StartsWith('#') && box["texts"]![0]!.GetValue<string>().Length == 13, "the text of the box is not in the hash form");
            Expect(box["class"]!.GetValue<string>() == "#32770" && box["kind"]!.GetValue<string>() == "MessageBox", "the class or kind of the box was changed");
            Expect(box["answeredWith"]!.GetValue<string>() == "OK" && box["answeredVia"]!.GetValue<string>() == "BM_CLICK", "the answer to the box was changed");
            Expect(scrub.Leaks(cleaned).Count == 0, "the cleaned set entry has text that is not BWC text: " + string.Join(" | ", scrub.Leaks(cleaned)));
            Expect(
                box["title"]!.GetValue<string>() == Scrubber.Hash("ZZ-MARKER-box-title"),
                "the same title does not give the same hash as a press of the policy dialog would");
        });

        Case("list refusal: a box with the dialog closed afterwards is recorded as not refused, and nothing is cancelled", () =>
        {
            JsonObject entry = FakeListSet(openAfterBox: false, cancelled: null, cancelRoute: null);
            Expect(entry["okMessageBox"]?.GetValue<bool>() == true, "okMessageBox is missing");
            Expect(entry["refused"]?.GetValue<bool>() == false, "a box with the dialog closed is not refused");
            Expect(entry["dialogOpenAfterBox"]?.GetValue<bool>() == false, "dialogOpenAfterBox is not false");
            Expect(entry["cancelled"] is null && entry["cancelClosedBy"] is null, "a cancel was recorded for a dialog that closed by itself");
        });

        Case("list refusal: a Cancel that did not close the dialog is recorded as cancelled false, and boxes after Cancel are kept", () =>
        {
            var entry = new JsonObject();
            ListRefusal.Apply(entry, new JsonArray(FakeBox()), true, false, "NOT CLOSED", new JsonArray(FakeBox()));
            Expect(entry["refused"]?.GetValue<bool>() == true && entry["cancelled"]?.GetValue<bool>() == false, "a failed Cancel was recorded as a closed dialog");
            Expect(entry["messageBoxesAfterCancel"] is JsonArray { Count: 1 }, "the box after Cancel was not kept");
        });
    }
}

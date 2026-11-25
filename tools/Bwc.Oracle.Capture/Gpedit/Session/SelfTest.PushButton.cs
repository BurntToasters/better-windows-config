using System.Text.Json.Nodes;
using Bwc.Oracle.Capture.Gpedit.Driver;
using Bwc.Oracle.Capture.Gpedit.Matrix;

namespace Bwc.Oracle.Capture.Gpedit.Session;

// The decision "is this inner BUTTON of an option container the Show button of a list". Failure mode 36 of the driver document. These
// cases were written before the code they check.
internal static partial class SelfTest
{
    private static void PushButtonCases(RepoPaths repo, Action<string, Action> Case)
    {
        Case("push button: a native push style is a push button and needs no UI Automation", () =>
        {
            foreach (uint type in new[] { BsPush, BsDefPush })
            {
                uint style = 0x50010000 | type;
                Expect(!PushButtonRead.NeedsUia(FormsButton, style), $"a native push style {type} asks for UI Automation");
                Expect(PushButtonRead.IsPush(FormsButton, style, null), $"a native push style {type} is not a push button");
                Expect(PushButtonRead.IsPush(FormsButton, style, new PushUia(false, false, false, false, "Pane")), $"UI Automation changed the answer for native style {type}");
            }
        });

        Case("push button: an owner-drawn BUTTON is a push button only with UI Automation evidence of Button or Invoke", () =>
        {
            uint style = 0x50010000 | BsOwner;
            Expect(PushButtonRead.NeedsUia(FormsButton, style), "an owner-drawn BUTTON does not ask for UI Automation");
            Expect(PushButtonRead.IsPush(FormsButton, style, new PushUia(true, true, false, false, "Button")), "an owner-drawn Button with Invoke is not a push button");
            Expect(PushButtonRead.IsPush(FormsButton, style, new PushUia(true, false, false, false, "Button")), "an owner-drawn element of type Button is not a push button");
            Expect(PushButtonRead.IsPush(FormsButton, style, new PushUia(false, true, false, false, "Pane")), "an owner-drawn element with Invoke is not a push button");
            Expect(!PushButtonRead.IsPush(FormsButton, style, new PushUia(false, false, false, false, "Pane")), "an owner-drawn element with neither Button nor Invoke was taken");
            Expect(!PushButtonRead.IsPush(FormsButton, style, null), "an owner-drawn BUTTON with no UI Automation answer was taken as a push button");
        });

        Case("push button: an owner-drawn check box or radio button is never a push button", () =>
        {
            uint owner = 0x50010000 | BsOwner;
            Expect(!PushButtonRead.IsPush(FormsButton, owner, new PushUia(false, false, true, true, "CheckBox")), "an owner-drawn check box was taken as a push button");
            Expect(!PushButtonRead.IsPush(FormsButton, owner, new PushUia(false, true, true, false, "CheckBox")), "a CheckBox type with Invoke was taken as a push button");
            Expect(!PushButtonRead.IsPush(FormsButton, owner, new PushUia(true, true, false, true, "Button")), "an element with a toggle pattern was taken as a push button");
            Expect(!PushButtonRead.IsPush(FormsButton, owner, new PushUia(false, false, false, false, "RadioButton")), "an owner-drawn radio button was taken as a push button");
            foreach (uint type in new[] { 2u, 3u, 5u, 6u, BsRadio, BsAutoRadio })
            {
                uint style = 0x50010000 | type;
                Expect(!PushButtonRead.NeedsUia(FormsButton, style), $"BS type {type} asks for UI Automation");
                Expect(!PushButtonRead.IsPush(FormsButton, style, new PushUia(true, true, false, false, "Button")), $"BS type {type} was taken as a push button on UI Automation evidence alone");
            }
        });

        Case("push button: the class decides first, case does not matter", () =>
        {
            uint owner = 0x50010000 | BsOwner;
            string edit = "WindowsForms10.EDIT.app.0.297b065_r40_ad1";
            Expect(!PushButtonRead.NeedsUia(edit, owner), "an EDIT class asks for UI Automation");
            Expect(!PushButtonRead.IsPush(edit, owner, new PushUia(true, true, false, false, "Button")), "an EDIT class was taken as a push button");
            Expect(!PushButtonRead.IsPush("WindowsForms10.STATIC.app.0.297b065_r40_ad1", BsPush, null), "a STATIC class with a push style was taken as a push button");
            Expect(PushButtonRead.IsPush("windowsforms10.button.app.0.297b065_r40_ad1", BsPush, null), "the BUTTON class was matched in a case sensitive way");
        });

        Case("push button: the evidence never reaches a golden", () =>
        {
            var scrub = new Scrubber(MatrixPlan.Build(repo).Fixture.AllowedStrings, Array.Empty<string>(), false);
            var option = new JsonObject
            {
                ["index"] = 0,
                ["kind"] = "list",
                ["label"] = "Plain entries:",
                ["enabled"] = true,
                ["pushEvidence"] = new JsonArray(new JsonObject { ["buttonStyle"] = "0x5001000B", ["uiaControlType"] = "ZZ-MARKER-push" }),
            };
            JsonNode cleaned = scrub.Clean(new JsonObject { ["options"] = new JsonArray(option) })!;
            string text = cleaned.ToJsonString();
            Expect(!text.Contains("ZZ-MARKER", StringComparison.Ordinal), "a diagnostic field reached the golden data: " + text);
            Expect(!text.Contains("pushEvidence", StringComparison.Ordinal), "a diagnostic key reached the golden data");
            JsonObject kept = cleaned["options"]![0]!.AsObject();
            Expect(kept["kind"]!.GetValue<string>() == scrub.S("list"), "the kind was lost from the golden data");
        });
    }
}

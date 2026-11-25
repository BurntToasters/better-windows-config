using System.Text.Json.Nodes;
using System.Windows.Automation;
using Bwc.Oracle.Capture.Gpedit.Driver;
using Bwc.Oracle.Capture.Gpedit.Matrix;

namespace Bwc.Oracle.Capture.Gpedit.Session;

// The decision "is this child of the options panel a check box, and how is its state read", on plain values. Failure mode 35 of the driver
// document. These cases were written before the code they check.
internal static partial class SelfTest
{
    private const string FormsButton = "WindowsForms10.BUTTON.app.0.297b065_r40_ad1";
    private const uint BsPush = 0;
    private const uint BsDefPush = 1;
    private const uint BsCheck = 2;
    private const uint BsAutoCheck = 3;
    private const uint BsRadio = 4;
    private const uint Bs3State = 5;
    private const uint BsAuto3State = 6;
    private const uint BsAutoRadio = 9;
    private const uint BsOwner = 0xB;

    private static void CheckBoxCases(RepoPaths repo, Action<string, Action> Case)
    {
        Case("check box: a native check style is a check box read with BM_GETCHECK and needs no UI Automation", () =>
        {
            foreach (uint type in new[] { BsCheck, BsAutoCheck, Bs3State, BsAuto3State })
            {
                uint style = 0x50010000 | type;
                Expect(!CheckBoxRead.NeedsUia(FormsButton, style), $"a native check style {type} asks for UI Automation");
                Expect(CheckBoxRead.Judge(FormsButton, style, null) == CheckReader.BmGetCheck, $"a native check style {type} is not read with BM_GETCHECK");
                Expect(CheckBoxRead.Judge(FormsButton, style, new CheckUia(false, false, "Button")) == CheckReader.BmGetCheck, $"UI Automation changed the reader of native style {type}");
            }
        });

        Case("check box: an owner-drawn BUTTON is a check box only with UI Automation evidence, and is read with the toggle pattern", () =>
        {
            uint style = 0x50010000 | BsOwner;
            Expect(CheckBoxRead.NeedsUia(FormsButton, style), "an owner-drawn BUTTON does not ask for UI Automation");
            Expect(CheckBoxRead.Judge(FormsButton, style, new CheckUia(true, true, "CheckBox")) == CheckReader.TogglePattern, "an owner-drawn CheckBox with a toggle pattern is not a check box");
            Expect(CheckBoxRead.Judge(FormsButton, style, new CheckUia(true, false, "CheckBox")) == CheckReader.TogglePattern, "an owner-drawn element of type CheckBox is not a check box");
            Expect(CheckBoxRead.Judge(FormsButton, style, new CheckUia(false, true, "Button")) == CheckReader.TogglePattern, "an owner-drawn element with a toggle pattern is not a check box");
        });

        Case("check box: a push button, a radio button and an unreadable element are not check boxes", () =>
        {
            uint owner = 0x50010000 | BsOwner;
            Expect(CheckBoxRead.Judge(FormsButton, owner, new CheckUia(false, false, "Button")) is null, "an owner-drawn push button was taken as a check box");
            Expect(CheckBoxRead.Judge(FormsButton, owner, new CheckUia(false, false, "RadioButton")) is null, "an owner-drawn radio button was taken as a check box");
            Expect(CheckBoxRead.Judge(FormsButton, owner, null) is null, "an owner-drawn BUTTON with no UI Automation answer was taken as a check box");
            foreach (uint type in new[] { BsPush, BsDefPush, BsRadio, BsAutoRadio })
            {
                uint style = 0x50010000 | type;
                Expect(!CheckBoxRead.NeedsUia(FormsButton, style), $"BS type {type} asks for UI Automation");
                Expect(CheckBoxRead.Judge(FormsButton, style, new CheckUia(true, true, "CheckBox")) is null, $"BS type {type} was taken as a check box on UI Automation evidence alone");
            }
        });

        Case("check box: the class decides first, and the panel id text never decides", () =>
        {
            uint owner = 0x50010000 | BsOwner;
            Expect(!CheckBoxRead.NeedsUia("WindowsForms10.EDIT.app.0.297b065_r40_ad1", owner), "an EDIT class asks for UI Automation");
            Expect(CheckBoxRead.Judge("WindowsForms10.EDIT.app.0.297b065_r40_ad1", owner, new CheckUia(true, true, "CheckBox")) is null, "an EDIT class was taken as a check box");
            Expect(CheckBoxRead.Judge("WindowsForms10.STATIC.app.0.297b065_r40_ad1", 0x50010000 | BsCheck, null) is null, "a STATIC class with a check style was taken as a check box");
            Expect(CheckBoxRead.Judge("windowsforms10.button.app.0.297b065_r40_ad1", 0x50010000 | BsAutoCheck, null) == CheckReader.BmGetCheck, "the BUTTON class was matched in a case sensitive way");
            Expect(CheckBoxRead.Judge(FormsButton, owner, new CheckUia(false, false, "Button")) is null, "evidence without UI Automation proof was accepted");
            Expect(CheckBoxRead.PanelHint("checkBoxNumber check"), "the panel id prefix is not read as a hint");
            Expect(!CheckBoxRead.PanelHint("buttonShow"), "a push button panel id is read as a check box hint");
        });

        Case("check box: toggle states map to the BM_GETCHECK numbers", () =>
        {
            Expect(CheckBoxRead.ToCheckState(ToggleState.Off) == 0, "Off is not 0");
            Expect(CheckBoxRead.ToCheckState(ToggleState.On) == 1, "On is not 1");
            Expect(CheckBoxRead.ToCheckState(ToggleState.Indeterminate) == 2, "Indeterminate is not 2");
            Expect(CheckBoxRead.ViaName(CheckReader.BmGetCheck) == "BM_GETCHECK", "the BM_GETCHECK reader name changed");
            Expect(CheckBoxRead.ViaName(CheckReader.TogglePattern) == "TogglePattern", "the TogglePattern reader name changed");
        });

        Case("check box: the reader name and the evidence never reach a golden", () =>
        {
            var scrub = new Scrubber(MatrixPlan.Build(repo).Fixture.AllowedStrings, Array.Empty<string>(), false);
            var option = new JsonObject
            {
                ["index"] = 0,
                ["kind"] = "checkBox",
                ["label"] = "Number check",
                ["enabled"] = true,
                ["checkState"] = 1,
                ["checkStateVia"] = "TogglePattern ZZ-MARKER-via",
                ["checkEvidence"] = new JsonObject { ["buttonStyle"] = "0x5001000B", ["uiaControlType"] = "ZZ-MARKER-evidence" },
            };
            JsonNode cleaned = scrub.Clean(new JsonObject { ["options"] = new JsonArray(option) })!;
            string text = cleaned.ToJsonString();
            Expect(!text.Contains("ZZ-MARKER", StringComparison.Ordinal), "a diagnostic field reached the golden data: " + text);
            Expect(!text.Contains("checkStateVia", StringComparison.Ordinal) && !text.Contains("checkEvidence", StringComparison.Ordinal), "a diagnostic key reached the golden data");
            JsonObject kept = cleaned["options"]![0]!.AsObject();
            Expect(kept["kind"]!.GetValue<string>() == scrub.S("checkBox") && kept["checkState"]!.GetValue<int>() == 1, "kind or check state was lost from the golden data");
        });
    }
}

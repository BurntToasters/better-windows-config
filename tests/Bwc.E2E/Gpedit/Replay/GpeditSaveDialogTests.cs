using System.Text.Json;
using System.Text.Json.Nodes;
using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using Bwc.Policy.Tests.Golden;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Gpedit.Replay;

/// <summary>
/// Two behaviours of the setting dialog that the save piece (G4) settles and that the engine alone cannot show: an
/// Enabled
/// list with no rows is refused with a message box and writes nothing, and the values the dialog shows right after
/// Enabled is
/// chosen are the defaults of classic (docs/failure-modes/gpedit-dialog.md, section 15). The expected defaults are
/// the rules
/// of <c>PolicyDefaults.For</c> (docs/failure-modes/policy-defaults.md, section F), stated here as literals so the
/// test does
/// not reference the engine it checks.
/// </summary>
[TestClass]
public sealed class GpeditSaveDialogTests
{
    private const string Root = GpeditRun.RootName;
    private const string Computer = "Computer Configuration";
    private const string Admin = "Administrative Templates";
    private const string Oracle = "BWC Oracle";
    private const string PlainButton = "buttonPlain entries:";

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    // ---- an empty list is refused -----------------------------------------------------------------

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Options_List_EmptyRefused()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("options", "listemptyrefused");
        var sandbox = ReplaySandbox.Create("gpedit-list-empty");
        sandbox.Sandbox.SeedStandard();
        SortedDictionary<string, string> seeded = sandbox.Sandbox.PolicyFileHashes();
        using GpeditRun run = GpeditRun.Open("gpedit-list-empty", sandbox.Sandbox);
        run.GoTo(Root, Computer, Admin, Oracle, "Elements", "Lists");

        // Classic, B/Pol_List_Plain operation 0: Enabled with the empty list and OK gives a message box, the dialog stays open,
        // Apply stays enabled and no file changes.
        using GpeditDialog? dialog = GpeditDialog.Open(run, "List with default naming setting");
        checks.Record("opens", dialog is not null, "the plain list policy opens.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        _ = dialog.Select(GpeditDialog.Enabled);
        _ = dialog.Invoke(GpeditDialog.Ok);
        (string Caption, string Text)? ok = dialog.WaitMessage();
        checks.Record("okRefused", ok is not null && ok.Value.Text.Trim().Length > 0, $"OK with the empty list shows a message: '{ok?.Text}'.");
        if (ok is not null)
        {
            _ = dialog.DismissMessage();
        }

        checks.Record("okStaysOpen", dialog.Find(GpeditDialog.Cancel, 1) is not null && dialog.IsEnabled(GpeditDialog.Apply), "the dialog stays open and Apply stays enabled.");
        _ = dialog.Invoke(GpeditDialog.Apply);
        (string Caption, string Text)? apply = dialog.WaitMessage();
        checks.Record("applyRefused", apply is not null, "Apply with the empty list is refused the same way.");
        if (apply is not null)
        {
            _ = dialog.DismissMessage();
        }

        checks.Record("nothingWritten", seeded.SequenceEqual(sandbox.Sandbox.PolicyFileHashes()), "no file changed after the two refusals.");

        // The Show Contents grid refuses OK with no row and stays open (B/Pol_List_Plain operation 6, C/SharedList_EmptyList).
        _ = dialog.Invoke(PlainButton);
        GpeditContents? contents = GpeditContents.Wait(dialog);
        checks.Record("contentsOpens", contents is not null, "Show... opens the grid.");
        if (contents is null)
        {
            checks.AssertAllPassed();
            return;
        }

        _ = contents.Ok();
        (string Caption, string Text)? grid = dialog.WaitMessage();
        checks.Record("gridRefused", grid is not null, "OK of the empty grid is refused with a message box.");
        if (grid is not null)
        {
            _ = dialog.DismissMessage();
        }

        checks.Record("gridStaysOpen", contents.IsOpen, "the grid stays open after the message.");
        if (!contents.IsOpen)
        {
            // The product let the empty grid close; open it again so the checks below judge their own behavior.
            _ = dialog.Invoke(PlainButton);
            contents = GpeditContents.Wait(dialog);
            if (contents is null)
            {
                checks.AssertAllPassed();
                return;
            }
        }

        // A row makes the same list valid: the refusal is about the empty list, not about the policy.
        contents.Edit(0, 0, "one");
        _ = contents.Ok();
        checks.Record("gridCloses", contents.WaitClosed(), "OK of a grid with a row closes it.");
        _ = dialog.Invoke(GpeditDialog.Ok);
        checks.Record("okSaves", dialog.WaitClosed(), "OK with one row saves and closes.");
        checks.Record("rowWritten", !seeded.SequenceEqual(sandbox.Sandbox.PolicyFileHashes()), "the row was written.");

        // The saved row stays when the grid is emptied: classic refuses the empty grid and the list keeps its rows.
        using GpeditDialog? again = GpeditDialog.Open(run, "List with default naming setting");
        checks.Record("reopens", again is not null, "the policy opens again.");
        if (again is not null)
        {
            _ = again.Invoke(PlainButton);
            GpeditContents? second = GpeditContents.Wait(again);
            if (second is not null)
            {
                second.DeleteRow(0);
                _ = second.Ok();
                (string Caption, string Text)? message = again.WaitMessage();
                checks.Record("clearRefused", message is not null, "OK after the only row was deleted is refused.");
                if (message is not null)
                {
                    _ = again.DismissMessage();
                }

                _ = second.Cancel();
                _ = second.WaitClosed();
            }

            checks.Record("rowKept", ListData(again, "PlainList").SequenceEqual(["one"]), $"the list still holds [{string.Join("|", ListData(again, "PlainList"))}].");
            checks.Record("cleanAfterClear", !again.IsEnabled(GpeditDialog.Apply), "nothing is pending after the refused clear.");
            _ = again.Invoke(GpeditDialog.Cancel);
            _ = again.WaitClosed();
        }

        checks.AssertAllPassed();
    }

    private static string[] ListData(GpeditDialog dialog, string id) =>
        dialog.ValueOf(id) is { } v && v.GetProperty("k").GetString() == "list"
            ? v.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("d").GetString() ?? string.Empty).ToArray()
            : ["<none>"];

    // ---- the defaults of classic ------------------------------------------------------------------

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Dialog_Defaults()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("dialog", "defaults");
        var sandbox = ReplaySandbox.Create("gpedit-dialog-defaults");
        sandbox.Sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-dialog-defaults", sandbox.Sandbox);

        // A number with no default shows 1 (F3, B/Pol_Decimal_Required), clamped into its range.
        Check(run, checks, "number", "B/Pol_Decimal_Required", dialog =>
        {
            string shown = NumberOf(dialog, "RequiredNumber");
            checks.Record("number.noDefaultShowsOne", shown == "1", $"the number box shows '{shown}'; classic shows 1 when the presentation has no defaultValue.");
        });

        // An enum with no defaultItem selects nothing (F7, B/Pol_Enum_StringItems), so OK is refused until one is chosen.
        Check(run, checks, "enum", "B/Pol_Enum_StringItems", dialog =>
        {
            int index = ChoiceOf(dialog, "StringChoice");
            checks.Record("enum.noDefaultSelectsNothing", index < 0, $"the drop-down has item {index} selected; classic selects nothing without a defaultItem.");
        });

        // Explicit defaults are shown as they are (F5, B/Pol_Value_NoValueName_ElementsOnly: text 'start', number 4).
        Check(run, checks, "explicit", "B/Pol_Value_NoValueName_ElementsOnly", dialog =>
        {
            string text = TextOf(dialog, "OnlyText");
            string number = NumberOf(dialog, "OnlyNumber");
            checks.Record("explicit.text", text == "start", $"the text box shows '{text}', expected the defaultValue 'start'.");
            checks.Record("explicit.number", number == "4", $"the number box shows '{number}', expected the defaultValue 4.");
        });

        checks.AssertAllPassed();
    }

    private static void Check(GpeditRun run, GpeditChecks checks, string name, string caseId, Action<GpeditDialog> inspect)
    {
        GoldenOp op = GoldenCase.Load(caseId).Ops.First(o => o.Kind == "editPolicy");
        var path = new List<string> { GpeditRun.RootName, op.Text("root") == "Computer" ? "Computer Configuration" : "User Configuration" };
        path.AddRange((op.Op["path"] as JsonArray ?? []).Select(n => n!.GetValue<string>()));
        run.GoTo([.. path]);
        using GpeditDialog? dialog = GpeditDialog.Open(run, op.Text("policy") + " setting");
        checks.Record($"{name}.opens", dialog is not null, $"the dialog of '{op.Text("policy")}' opens.");
        if (dialog is null)
        {
            return;
        }

        checks.Record($"{name}.notConfigured", dialog.IsSelected(GpeditDialog.NotConfigured), "the policy starts as Not Configured.");
        _ = dialog.Select(GpeditDialog.Enabled);
        checks.Record($"{name}.optionsEnabled", ShellRun.WaitUntil(dialog.OptionsEnabled, TimeSpan.FromSeconds(3)), "the options are enabled after Enabled is chosen.");
        inspect(dialog);
        _ = dialog.Invoke(GpeditDialog.Cancel);
        _ = dialog.WaitClosed();
    }

    private static string NumberOf(GpeditDialog dialog, string id) =>
        dialog.ValueOf(id) is { } v && v.GetProperty("k").GetString() == "num" ? v.GetProperty("v").GetString() ?? string.Empty : "<none>";

    private static string TextOf(GpeditDialog dialog, string id) =>
        dialog.ValueOf(id) is { } v && v.GetProperty("k").GetString() == "text" ? v.GetProperty("v").GetString() ?? string.Empty : "<none>";

    private static int ChoiceOf(GpeditDialog dialog, string id) =>
        dialog.ValueOf(id) is { } v && v.GetProperty("k").GetString() == "choice" && v.GetProperty("v").ValueKind == JsonValueKind.Number
            ? v.GetProperty("v").GetInt32()
            : -1;
}

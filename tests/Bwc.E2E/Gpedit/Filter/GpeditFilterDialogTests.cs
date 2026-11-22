using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.Controls;
using Bwc.E2E.Gpedit.Commands;
using Bwc.E2E.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Bwc.E2E.Gpedit.Commands.GpeditNames;

namespace Bwc.E2E.Gpedit.Filter;

/// <summary>
/// Filter Options dialog tests of the Local Group Policy Editor: the layout and parts (DLG-035, DLG-113), the three
/// refusals (DLG-114), Cancel, and the values the dialog remembers. The failure modes are in
/// docs/failure-modes/gpedit-filter.md, sections 1 to 3. Every test writes
/// artifacts/e2e/visual/gpedit/gpedit-&lt;name&gt;.json and a picture, and its checks to the E2E result artifact.
/// </summary>
[TestClass]
public sealed class GpeditFilterDialogTests
{
    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Filter_Dialog_Layout()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("filter", "dialog.layout");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-filter-dialog-layout");
        sandbox.SeedStandard();
        using GpeditRun run = GpeditRun.Open("gpedit-filter-dialog-layout", sandbox);
        run.GoTo(BasicsPath);

        using GpeditFilterDialog? dialog = GpeditFilterDialog.Open(run);
        checks.Record("opens", dialog is not null, "Filter Options from the Action menu opens a window with the id GpeditFilterOptions.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        // Every part of DLG-113 exists.
        foreach (string id in FilterIds.TabOrder)
        {
            checks.Record("part." + id["GpeditFilterOptions.".Length..], dialog.Has(id), $"the control {id} exists.");
        }

        // The three combo boxes list Any, Yes and No; the match box lists Any, All and Exact.
        string[] tri = ["Any", "Yes", "No"];
        checks.Record("managedItems", dialog.ComboItems(FilterIds.Managed).SequenceEqual(tri), $"Managed items: {string.Join("|", dialog.ComboItems(FilterIds.Managed))}.");
        checks.Record("configuredItems", dialog.ComboItems(FilterIds.Configured).SequenceEqual(tri), $"Configured items: {string.Join("|", dialog.ComboItems(FilterIds.Configured))}.");
        checks.Record("commentedItems", dialog.ComboItems(FilterIds.Commented).SequenceEqual(tri), $"Commented items: {string.Join("|", dialog.ComboItems(FilterIds.Commented))}.");

        // F/Filter_Options, the dialog read before each OK: the keyword and requirement details are disabled until their
        // check box is ticked, the three combo boxes and the two check boxes are always enabled.
        checks.Record("alwaysEnabled", new[] { FilterIds.Managed, FilterIds.Configured, FilterIds.Commented, FilterIds.KeywordsEnabled, FilterIds.RequirementsEnabled, FilterIds.Ok, FilterIds.Cancel }.All(dialog.IsEnabled), "combos, both part check boxes, OK and Cancel are enabled.");
        string[] keywordDetails = [FilterIds.KeywordText, FilterIds.KeywordMatch, FilterIds.InTitle, FilterIds.InHelp, FilterIds.InComment];
        string[] platformDetails = [FilterIds.PlatformMatch, FilterIds.PlatformList, FilterIds.SelectAll, FilterIds.ClearAll];
        checks.Record("detailsOffAtStart", keywordDetails.Concat(platformDetails).All(id => !dialog.IsEnabled(id)), "keyword and requirement details start disabled.");
        _ = dialog.SetChecked(FilterIds.KeywordsEnabled, true);
        checks.Record("keywordDetailsOn", keywordDetails.All(dialog.IsEnabled) && platformDetails.All(id => !dialog.IsEnabled(id)), "ticking Enable Keyword Filters enables the keyword details only.");
        _ = dialog.SetChecked(FilterIds.RequirementsEnabled, true);
        checks.Record("platformDetailsOn", platformDetails.All(dialog.IsEnabled), "ticking Enable Requirements Filters enables the platform details.");
        _ = dialog.SetChecked(FilterIds.KeywordsEnabled, false);
        checks.Record("keywordDetailsOffAgain", keywordDetails.All(id => !dialog.IsEnabled(id)), "unticking the keyword filter disables its details again.");
        _ = dialog.SetChecked(FilterIds.KeywordsEnabled, true);
        // The two match boxes are disabled until their check boxes are ticked, and a disabled box cannot be opened.
        checks.Record("matchItems", dialog.ComboItems(FilterIds.KeywordMatch).SequenceEqual(["Any", "All", "Exact"]), $"Match items: {string.Join("|", dialog.ComboItems(FilterIds.KeywordMatch))}.");
        checks.Record("platformMatchItems", dialog.ComboItems(FilterIds.PlatformMatch).SequenceEqual(["Any", "All"]), $"Requirement match items: {string.Join("|", dialog.ComboItems(FilterIds.PlatformMatch))}.");

        // The platform list holds the four supported-on definitions of the fixture, none ticked.
        List<(string Name, bool Ticked)> platforms = dialog.Platforms();
        string[] expectedPlatforms = [FilterIds.PlatformAny, FilterIds.PlatformNewer, FilterIds.PlatformOlder, FilterIds.PlatformLocal];
        checks.Record("platformNames", platforms.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(expectedPlatforms.OrderBy(n => n, StringComparer.Ordinal)), $"platforms: {string.Join("|", platforms.Select(p => p.Name))}.");
        checks.Record("noneTicked", platforms.All(p => !p.Ticked), "no platform is ticked at start.");
        _ = dialog.Press(FilterIds.SelectAll);
        checks.Record("selectAll", dialog.Platforms().Count == 4 && dialog.Platforms().All(p => p.Ticked), "Select All ticks every platform.");
        _ = dialog.Press(FilterIds.ClearAll);
        checks.Record("clearAll", dialog.Platforms().All(p => !p.Ticked), "Clear All unticks every platform.");

        // The dialog is about 330 x 351 dialog units (DLG-035): 495 x 570 DIP at the classic font, with a tolerance.
        System.Drawing.Rectangle? root = dialog.RectOf(FilterIds.Dialog);
        double width = root is { } r ? r.Width / run.Scale : -1;
        double height = root is { } q ? q.Height / run.Scale : -1;
        checks.Record("size", width is >= 430 and <= 570 && height is >= 500 and <= 660, $"dialog size {width:0} x {height:0} DIP; expected about 495 x 570.");
        run.Measure("dialogSizeDip", $"{width:0} x {height:0}");

        // Reading order: the three combos, the keyword group, the requirement group, then the buttons, OK left of Cancel.
        System.Drawing.Rectangle?[] top = FilterIds.TabOrder.Select(dialog.RectOf).ToArray();
        int Index(string id) => Array.IndexOf(FilterIds.TabOrder, id);
        double Top(string id) => top[Index(id)]?.Top ?? double.NaN;
        double Left(string id) => top[Index(id)]?.Left ?? double.NaN;
        checks.Record("keywordsBelowCombos", Top(FilterIds.KeywordsEnabled) > Math.Max(Top(FilterIds.Commented), Math.Max(Top(FilterIds.Managed), Top(FilterIds.Configured))), "Enable Keyword Filters is below the three combos.");
        checks.Record("keywordRows", Top(FilterIds.KeywordText) >= Top(FilterIds.KeywordsEnabled) && Top(FilterIds.InTitle) > Top(FilterIds.KeywordText) - 1 && Top(FilterIds.InComment) >= Top(FilterIds.InTitle), "keyword text, match and the Within boxes follow the keyword check box.");
        checks.Record("requirementsBelowKeywords", Top(FilterIds.RequirementsEnabled) > Top(FilterIds.InComment), "Enable Requirements Filters is below the Within boxes.");
        checks.Record("platformBelowRequirements", Top(FilterIds.PlatformList) > Top(FilterIds.RequirementsEnabled), "the platform list is below its check box.");
        checks.Record("buttonsLast", Top(FilterIds.Ok) > Top(FilterIds.PlatformList) && Math.Abs(Top(FilterIds.Ok) - Top(FilterIds.Cancel)) < 4 && Left(FilterIds.Ok) < Left(FilterIds.Cancel), "OK and Cancel sit at the bottom, OK left of Cancel.");

        // DLG-035: the tab order of the controls.
        _ = dialog.SetChecked(FilterIds.KeywordsEnabled, true);
        _ = dialog.SetChecked(FilterIds.RequirementsEnabled, true);
        checks.Record("focusFirst", dialog.Focus(FilterIds.Managed), $"focus on Managed; focused part={dialog.FocusedPart()}.");
        string[] sequence = dialog.TabSequence(FilterIds.TabOrder.Length - 1);
        checks.Record("tabOrder", sequence.SequenceEqual(FilterIds.TabOrder.Skip(1)), $"Tab visits {string.Join(" > ", sequence)}; expected {string.Join(" > ", FilterIds.TabOrder.Skip(1))}.");

        _ = dialog.Screenshot("gpedit-filter-dialog-layout.png");
        _ = dialog.PressCancel();
        run.WriteArtifacts("gpedit-filter-dialog-layout.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Filter_Dialog_Refusals()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("filter", "dialog.refusals");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-filter-dialog-refusals");
        FilterSeed.SortCases(sandbox);
        using GpeditRun run = GpeditRun.Open("gpedit-filter-dialog-refusals", sandbox);
        run.GoTo(FilterNames.SortCasesPath);
        _ = run.WaitRowsAsSet(FilterNames.Rows("Policy 1", "Policy 10", "Policy 2", "#Symbol policy", "alpha policy", "Éclair policy", "Alpha Policy", "Zulu sort case"));

        // DLG-114: three options that OK refuses with a message box. The dialog stays open, the filter stays off, and the
        // list stays as it was (F/Filter_Options op 62, the requirements filter with no platform: all eight rows).
        (string Id, FilterValues Values)[] refused =
        [
            ("keywordNoWord", new FilterValues { Keywords = true, Text = string.Empty, InTitle = true }),
            ("keywordNoWithin", new FilterValues { Keywords = true, Text = "policy", InTitle = false, InHelp = false, InComment = false }),
            ("requirementsNoPlatform", new FilterValues { Requirements = true }),
        ];
        var messages = new List<string>();
        foreach ((string id, FilterValues values) in refused)
        {
            using GpeditFilterDialog? dialog = GpeditFilterDialog.Open(run);
            checks.Record(id + ".opens", dialog is not null, "Filter Options opens.");
            if (dialog is null)
            {
                continue;
            }

            checks.Record(id + ".set", dialog.Set(values), "the options were set.");
            checks.Record(id + ".okPressed", dialog.PressOk(), "OK was pressed.");
            string? message = dialog.WaitMessage();
            checks.Record(id + ".message", !string.IsNullOrWhiteSpace(message), $"a message box named {FilterIds.Message} shows; text='{message}'.");
            messages.Add(message ?? string.Empty);
            checks.Record(id + ".dismissed", dialog.DismissMessage(), "the message box closes with Enter.");
            checks.Record(id + ".dialogStaysOpen", dialog.IsOpen, "the dialog stays open after a refusal.");
            checks.Record(id + ".filterStaysOff", run.Count("FilterOn") == 0, $"FilterOn={run.Str("FilterOn")} while the dialog is open after a refusal.");
            checks.Record(id + ".cancelCloses", dialog.PressCancel(), "Cancel closes the dialog.");
            checks.Record(id + ".listUnfiltered", run.RowNames().Length == 8 && run.Count("FilterOn") == 0, $"rows={run.RowNames().Length}, FilterOn={run.Str("FilterOn")}; the list stays unfiltered.");
            checks.Record(id + ".flagOff", !run.StatusFlagShown(), "the status bar shows no filter flag.");
        }

        checks.Record("messagesDiffer", messages.Count == 3 && messages.Distinct(StringComparer.Ordinal).Count() == 3, $"each refusal has its own text: {string.Join(" || ", messages)}.");

        // After the cause is fixed the same dialog accepts the options.
        using (GpeditFilterDialog? dialog = GpeditFilterDialog.Open(run))
        {
            checks.Record("fixed.opens", dialog is not null, "Filter Options opens again.");
            if (dialog is not null)
            {
                _ = dialog.Set(new FilterValues { Requirements = true, Platforms = [FilterIds.PlatformAny] });
                checks.Record("fixed.accepted", dialog.PressOk() && dialog.WaitClosed(), "OK is accepted with a platform ticked.");
            }
        }

        checks.Record("fixed.filterOn", run.WaitFilterOn(true), $"FilterOn={run.Str("FilterOn")}; an accepted OK turns the filter on.");
        run.WriteArtifacts("gpedit-filter-dialog-refusals.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Filter_Dialog_RemembersAndCancels()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("filter", "dialog.remember");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-filter-dialog-remember");
        FilterSeed.SortCases(sandbox);
        using GpeditRun run = GpeditRun.Open("gpedit-filter-dialog-remember", sandbox);
        run.GoTo(FilterNames.SortCasesPath);

        // F/Filter_Options ops 7 and 37: OK turns the filter on by itself, and the dialog read again shows what OK accepted.
        var accepted = new FilterValues { Configured = "Yes", Keywords = true, Text = "zulu", Match = "Exact", InTitle = true, InHelp = false, InComment = false };
        checks.Record("applied", run.ApplyFilter(accepted), "the options were set and OK closed the dialog.");
        checks.Record("onAfterOk", run.WaitFilterOn(true), $"FilterOn={run.Str("FilterOn")} after OK.");
        checks.Record("rows", run.WaitRowsAsSet(FilterNames.Rows("Zulu sort case")).SequenceEqual(FilterNames.Rows("Zulu sort case")), $"rows: {string.Join("|", run.RowNames())}.");

        using (GpeditFilterDialog? dialog = GpeditFilterDialog.Open(run))
        {
            checks.Record("reopen", dialog is not null, "Filter Options opens again.");
            if (dialog is not null)
            {
                checks.Record("rememberedConfigured", dialog.Combo(FilterIds.Configured) == "Yes", $"Configured='{dialog.Combo(FilterIds.Configured)}'.");
                checks.Record("rememberedManaged", dialog.Combo(FilterIds.Managed) == "Any" && dialog.Combo(FilterIds.Commented) == "Any", "Managed and Commented stay Any.");
                checks.Record("rememberedKeywords", dialog.Checked(FilterIds.KeywordsEnabled) && dialog.Text(FilterIds.KeywordText) == "zulu" && dialog.Combo(FilterIds.KeywordMatch) == "Exact", $"keywords on={dialog.Checked(FilterIds.KeywordsEnabled)}, text='{dialog.Text(FilterIds.KeywordText)}', match='{dialog.Combo(FilterIds.KeywordMatch)}'.");
                checks.Record("rememberedWithin", dialog.Checked(FilterIds.InTitle) && !dialog.Checked(FilterIds.InHelp) && !dialog.Checked(FilterIds.InComment), "Within: title only.");

                // Cancel keeps the filter: a changed dialog that is cancelled changes nothing.
                _ = dialog.Choose(FilterIds.Configured, "No");
                _ = dialog.SetChecked(FilterIds.KeywordsEnabled, false);
                checks.Record("cancel", dialog.PressCancel(), "Cancel closes the dialog.");
            }
        }

        checks.Record("cancelKeepsRows", run.WaitRowsAsSet(FilterNames.Rows("Zulu sort case")).Length == 1 && run.Count("FilterOn") == 1, $"rows after Cancel: {string.Join("|", run.RowNames())}; FilterOn={run.Str("FilterOn")}.");

        // Filter On off keeps the options: the dialog still holds them.
        checks.Record("toggleOff", run.ToggleFilterByMenu() && run.WaitFilterOn(false), $"FilterOn={run.Str("FilterOn")} after the toggle.");
        checks.Record("allRowsBack", run.WaitRowsAsSet(FilterNames.Rows("Policy 1", "Policy 10", "Policy 2", "#Symbol policy", "alpha policy", "Éclair policy", "Alpha Policy", "Zulu sort case")).Length == 8, $"rows: {run.RowNames().Length}.");
        using (GpeditFilterDialog? dialog = GpeditFilterDialog.Open(run))
        {
            checks.Record("optionsKeptWhileOff", dialog is not null && dialog.Combo(FilterIds.Configured) == "Yes" && dialog.Text(FilterIds.KeywordText) == "zulu", "the dialog still holds the options after Filter On was switched off.");
            _ = dialog?.PressCancel();
        }

        checks.Record("cancelDidNotTurnOn", run.Count("FilterOn") == 0, $"FilterOn={run.Str("FilterOn")}; Cancel does not turn the filter on.");
        run.WriteArtifacts("gpedit-filter-dialog-remember.png");
        checks.AssertAllPassed();
    }
}

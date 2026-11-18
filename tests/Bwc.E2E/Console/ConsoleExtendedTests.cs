using System.Drawing;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using Bwc.E2E.List;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.ConsoleFrame;

/// <summary>
/// Console Extended view tests: the description pane beside the list, the Extended and Standard tabs and their UI
/// Automation shape, the pane width, long and odd help text, the link by keyboard, and high contrast. The failure modes
/// are in docs/failure-modes/console-extended.md. Every test writes
/// artifacts/e2e/visual/console-extended/console-extended-&lt;name&gt;.json and a picture, and its checks to the E2E result
/// artifact. The host is the Local Group Policy Editor with the placeholder descriptions (see GpeditRootView).
/// </summary>
[TestClass]
public sealed class ConsoleExtendedTests
{
    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    // ---- tabs in UI Automation (failure mode 11) -------------------------------------------------

    [TestMethod]
    [TestCategory("Console")]
    public void Console_Extended_Tabs()
    {
        var checks = new ShellChecks("tabs", "extended");
        using ExtendedRun run = ExtendedRun.Open("tabs", 8);
        AutomationElement? strip = run.Find(ExtendedRun.TabsId);
        checks.Record("stripType", strip?.ControlType == ControlType.Tab, $"the strip is {strip?.ControlType}; classic's is a Tab.");
        AutomationElement[] items = strip?.FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem)) ?? [];
        string[] names = items.Select(ShellRun.SafeName).ToArray();
        checks.Record("itemNames", names.SequenceEqual(["Extended", "Standard"]), $"TabItems={string.Join("|", names)}; expected Extended|Standard.");
        checks.Record(
            "itemPatterns",
            items.Length == 2 && items.All(item => item.Patterns.SelectionItem.IsSupported && item.Patterns.Invoke.IsSupported),
            "each TabItem supports SelectionItem and Invoke.");
        checks.Record("startsExtended", run.Tab == "Extended" && IsSelected(run, ExtendedRun.ExtendedTabId) && !IsSelected(run, ExtendedRun.StandardTabId), $"readout Tab={run.Tab}.");
        string[] selection = strip?.Patterns.Selection.PatternOrDefault?.Selection.ValueOrDefault?.Select(ShellRun.SafeName).ToArray() ?? [];
        checks.Record("stripSelection", selection.SequenceEqual(["Extended"]), $"strip Selection pattern gives [{string.Join("|", selection)}].");

        Rectangle? stripRect = run.Rect(ExtendedRun.TabsId);
        Rectangle? viewRect = run.Rect(ExtendedRun.ViewId);
        Rectangle? extendedRect = run.Rect(ExtendedRun.ExtendedTabId);
        Rectangle? standardRect = run.Rect(ExtendedRun.StandardTabId);
        if (stripRect is not null && viewRect is not null && extendedRect is not null && standardRect is not null)
        {
            checks.Record("stripHeight", Near(run.Logical(stripRect.Value.Height), 19, 1.5), $"strip height={run.Logical(stripRect.Value.Height):0.##} DIP; classic 19.");
            checks.Record("stripAtBottom", Math.Abs(stripRect.Value.Bottom - viewRect.Value.Bottom) <= 2, $"strip bottom={stripRect.Value.Bottom}, view bottom={viewRect.Value.Bottom}.");
            checks.Record(
                "tabWidths",
                Near(run.Logical(extendedRect.Value.Width), 69, 1.5) && Near(run.Logical(standardRect.Value.Width), 68, 1.5),
                $"tab widths={run.Logical(extendedRect.Value.Width):0.##} and {run.Logical(standardRect.Value.Width):0.##} DIP; classic 69 and 68.");
        }
        else
        {
            checks.Record("geometry", false, "the strip, the view or a tab has no rectangle.");
        }

        // Invoke, Select, a repeated Select and a click each change the tab once (the host counts changes in the status bar).
        AutomationElement? standard = run.Find(ExtendedRun.StandardTabId);
        AutomationElement? extended = run.Find(ExtendedRun.ExtendedTabId);
        standard?.Patterns.Invoke.PatternOrDefault?.Invoke();
        checks.Record("invokeStandard", ShellRun.WaitUntil(() => run.Tab == "Standard" && run.Shell.StatusText() == "Tab: Standard n=1"), $"after Invoke on Standard: Tab={run.Tab}, status='{run.Shell.StatusText()}'.");
        checks.Record("standardSelected", IsSelected(run, ExtendedRun.StandardTabId) && !IsSelected(run, ExtendedRun.ExtendedTabId), "IsSelected moved to Standard.");
        extended?.Patterns.SelectionItem.PatternOrDefault?.Select();
        checks.Record("selectExtended", ShellRun.WaitUntil(() => run.Tab == "Extended" && run.Shell.StatusText() == "Tab: Extended n=2"), $"after Select on Extended: Tab={run.Tab}, status='{run.Shell.StatusText()}'.");
        extended?.Patterns.SelectionItem.PatternOrDefault?.Select();
        Thread.Sleep(700);
        checks.Record("selectSameRaisesNothing", run.Shell.StatusText() == "Tab: Extended n=2", $"after selecting the selected tab again: status='{run.Shell.StatusText()}'; no change event.");
        standard?.Click();
        checks.Record("clickStandard", ShellRun.WaitUntil(() => run.Tab == "Standard" && run.Shell.StatusText() == "Tab: Standard n=3"), $"after a click on Standard: Tab={run.Tab}, status='{run.Shell.StatusText()}'.");

        run.Measure("names", string.Join("|", names));
        run.WriteArtifacts("console-extended-tabs.png");
        checks.AssertAllPassed();
    }

    // ---- description pane: category, row, clear (failure modes 4 and 9) ----------------------------

    [TestMethod]
    [TestCategory("Console")]
    public void Console_Extended_DescriptionPane()
    {
        var checks = new ShellChecks("pane", "extended");
        using ExtendedRun run = ExtendedRun.Open("pane", 8);

        // No row selected: the selected tree node is the category, with the hint and no link.
        checks.Record("categoryTitle", ShellRun.WaitUntil(() => run.Title == "Computer Configuration"), $"title='{run.Title}'; the selected tree node.");
        checks.Record("categoryHint", run.NameOf("ConsoleDescriptionText_hint") == ExtendedRun.HintText, $"hint='{run.NameOf("ConsoleDescriptionText_hint")}'.");
        checks.Record("categoryNoLink", run.Find(ExtendedRun.LinkId) is null, "a category has no edit link.");
        checks.Record("categoryNoUnnamedText", run.PaneTexts().All(text => text.Name.Length > 0), $"Text elements: {string.Join("|", run.PaneTexts().Select(t => t.Id + "=" + t.Name.Length))}.");

        // Geometry: the pane is left of the list, the tabs are under both.
        Rectangle? pane = run.Rect(ExtendedRun.PaneId);
        Rectangle? list = run.Rect(ListRun.ListId);
        Rectangle? strip = run.Rect(ExtendedRun.TabsId);
        if (pane is not null && list is not null && strip is not null)
        {
            checks.Record("paneLeftOfList", pane.Value.Right <= list.Value.Left + 1 && pane.Value.Left < list.Value.Left, $"pane {pane}, list {list}.");
            checks.Record("sameTop", Math.Abs(pane.Value.Top - list.Value.Top) <= 2, $"pane top={pane.Value.Top}, list top={list.Value.Top}.");
            checks.Record("tabsBelow", strip.Value.Top >= pane.Value.Bottom - 2 && strip.Value.Top >= list.Value.Bottom - 2, $"strip top={strip.Value.Top}, pane bottom={pane.Value.Bottom}, list bottom={list.Value.Bottom}.");
            checks.Record("paneRatio", Near(run.Logical(pane.Value.Width) / (run.Logical(pane.Value.Width) + run.Logical(list.Value.Width)), 212.0 / 589, 0.01), $"pane {run.Logical(pane.Value.Width):0.#} DIP, list {run.Logical(list.Value.Width):0.#} DIP; classic 212 of 589.");
        }
        else
        {
            checks.Record("geometry", false, $"missing rectangle: pane={pane}, list={list}, strip={strip}.");
        }

        // One selected row: its title, the link, both labeled sections.
        run.SelectFirstRow();
        string title = run.RowTitle(0);
        checks.Record("rowTitle", run.Title == title, $"title='{run.Title}'; expected '{title}'.");
        AutomationElement? link = run.Find(ExtendedRun.LinkId);
        checks.Record("linkType", link?.ControlType == ControlType.Hyperlink && ShellRun.SafeName(link) == "Edit policy setting", $"link type={link?.ControlType}, name='{ShellRun.SafeName(link)}'.");
        checks.Record("requirementsLabel", run.NameOf("ConsoleDescriptionLabel_requirements") == "Requirements" && run.Find("ConsoleDescriptionLabel_requirements")?.ControlType == ControlType.Text, "Requirements label is a Text with its name.");
        checks.Record("descriptionLabel", run.NameOf("ConsoleDescriptionLabel_description") == "Description", "Description label is a Text with its name.");
        string body = run.NameOf("ConsoleDescriptionText_description");
        checks.Record("descriptionBody", body.StartsWith("Placeholder help text of " + title, StringComparison.Ordinal), $"body='{body}'.");
        checks.Record("requirementsBody", run.NameOf("ConsoleDescriptionText_requirements").Length > 0, "the Requirements text has its name.");
        checks.Record("noUnnamedText", run.PaneTexts().All(text => text.Name.Length > 0), $"Text elements: {string.Join("|", run.PaneTexts().Select(t => t.Id + "=" + t.Name.Length))}.");

        // Ctrl+Space clears the selection: back to the category.
        run.Shell.Chord(RawKeyboard.Control, ListKeys.Space);
        checks.Record("clearedToCategory", ShellRun.WaitUntil(() => run.ShowsCategory() && run.Title == "Computer Configuration"), $"after Ctrl+Space: title='{run.Title}', selected={run.List.SelectedCount}.");

        run.Measure("scale", run.Scale);
        run.WriteArtifacts("console-extended-pane.png");
        checks.AssertAllPassed();
    }

    // ---- pane width at several window widths and after the tree splitter moved (failure modes 5 and 9) ---------

    [TestMethod]
    [TestCategory("Console")]
    public void Console_Extended_PaneWidth()
    {
        var checks = new ShellChecks("width", "extended");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["BWC_E2E_CONSOLE_HELP"] = "wide" };
        using ExtendedRun run = ExtendedRun.Open("width", 8, environment);
        run.SelectFirstRow();

        int[] widths = [Math.Min(1900, ShellRun.ScreenWidth - 60), 1100, 700];
        foreach (int width in widths)
        {
            run.Shell.PlaceWindow(20, 20, width, 1000);
            checks.Record($"w{width}", ShellRun.WaitUntil(() => PaneMatchesFormula(run), TimeSpan.FromSeconds(8)), Describe(run));
            CheckWordInside(checks, run, $"w{width}.word");
        }

        // The tree splitter dragged far right leaves the view almost nothing: the pane keeps its minimum and the word stays in.
        Rectangle? splitter = run.Rect(ShellRun.SplitterId);
        if (splitter is not null)
        {
            var start = new Point(splitter.Value.Left + (splitter.Value.Width / 2), splitter.Value.Top + (splitter.Value.Height / 2));
            ShellRun.DragStart(start, 3000);
            ShellRun.DragEnd();
            checks.Record("afterSplitterDrag", ShellRun.WaitUntil(() => PaneMatchesFormula(run), TimeSpan.FromSeconds(8)), Describe(run));
            CheckWordInside(checks, run, "afterSplitterDrag.word");
            checks.Record("minimumKept", run.Number("PaneWidth") >= 48 - 0.01, $"pane width={run.Number("PaneWidth")} DIP; minimum 48.");
            checks.Record("viewNarrow", run.Number("ViewWidth") < 200, $"view width after the drag={run.Number("ViewWidth")} DIP.");
        }
        else
        {
            checks.Record("splitter", false, "the splitter has no rectangle.");
        }

        run.Measure("viewWidth", run.Number("ViewWidth"));
        run.Measure("paneWidth", run.Number("PaneWidth"));
        run.WriteArtifacts("console-extended-width.png");
        checks.AssertAllPassed();
    }

    // ---- help shapes: empty, long, markup (failure modes 1, 2, 3) --------------------------------

    [TestMethod]
    [TestCategory("Console")]
    public void Console_Extended_HelpShapes()
    {
        var checks = new ShellChecks("help", "extended");

        // Empty help: the label stays, no body element, no unnamed Text.
        using (ExtendedRun run = ExtendedRun.Open("help-empty", 8, Help("empty")))
        {
            run.SelectFirstRow();
            checks.Record("empty.label", run.NameOf("ConsoleDescriptionLabel_description") == "Description", "the Description label shows.");
            checks.Record("empty.noBody", run.Find("ConsoleDescriptionText_description") is null, "an empty help has no body element.");
            checks.Record("empty.noUnnamed", run.PaneTexts().All(text => text.Name.Length > 0), $"Text elements: {string.Join("|", run.PaneTexts().Select(t => t.Id + "=" + t.Name.Length))}.");
            checks.Record("empty.responsive", run.Find(ExtendedRun.PaneId) is not null && run.Shell.FocusedPane() == "result", $"the app answers; pane={run.Shell.FocusedPane()}.");
            run.WriteArtifacts("console-extended-help-empty.png");
        }

        // Ampersand, markup text and three kinds of line break.
        using (ExtendedRun run = ExtendedRun.Open("help-markup", 8, Help("markup")))
        {
            run.SelectFirstRow();
            string body = run.NameOf("ConsoleDescriptionText_description");
            const string expected = "Tom & Jerry <b>x</b>\nline two\nline three\nline four";
            checks.Record("markup.text", body == expected, $"name='{body.Replace("\n", "\\n", StringComparison.Ordinal)}'; the ampersand and the brackets are literal and every break is one line break.");
            Rectangle? bodyRect = run.Rect("ConsoleDescriptionText_description");
            Rectangle? titleRect = run.Rect(ExtendedRun.TitleId);
            checks.Record(
                "markup.lines",
                bodyRect is not null && titleRect is not null && bodyRect.Value.Height >= titleRect.Value.Height * 3,
                $"body height={bodyRect?.Height}, one line (the title)={titleRect?.Height}; four lines are at least three line heights.");
            run.Measure("markupBody", body);
            run.WriteArtifacts("console-extended-help-markup.png");
        }

        // 60,000 characters: split over blocks, a scroller, the keyboard reaches it, End scrolls, Ctrl+C copies the pane.
        using (ExtendedRun run = ExtendedRun.Open("help-long", 8, Help("long")))
        {
            run.SelectFirstRow();
            checks.Record("long.blocks", run.Number("Paragraphs") >= 15 && run.Number("ParagraphChars") == 60_025, $"paragraphs={run.Number("Paragraphs")}, characters={run.Number("ParagraphChars")}; 60,000 of help plus the 25 of the Requirements text, in blocks of at most 4,000.");
            AutomationElement? scroller = run.Find(ExtendedRun.ScrollId);
            var scroll = scroller?.Patterns.Scroll.PatternOrDefault;
            checks.Record("long.scrollable", scroll?.VerticallyScrollable.ValueOrDefault == true, $"vertically scrollable={scroll?.VerticallyScrollable.ValueOrDefault}.");
            checks.Record("long.stops", run.Number("Stops") == 2 && run.Number("Links") == 1, $"keyboard stops in the pane={run.Number("Stops")} (link and scroller), links={run.Number("Links")}.");

            run.Shell.Tap(ShellRun.VkTab);
            checks.Record("long.tabLink", ShellRun.WaitUntil(() => run.Shell.FocusedId() == ExtendedRun.LinkId), $"after Tab: focused id='{run.Shell.FocusedId()}'.");
            run.Shell.Tap(ShellRun.VkTab);
            checks.Record("long.tabScroller", ShellRun.WaitUntil(() => run.Shell.FocusedId() == ExtendedRun.ScrollId), $"after a second Tab: focused id='{run.Shell.FocusedId()}'.");
            double before = scroll?.VerticalScrollPercent.ValueOrDefault ?? -1;
            run.Shell.Tap(ListKeys.End);
            bool atEnd = ShellRun.WaitUntil(() => (run.Find(ExtendedRun.ScrollId)?.Patterns.Scroll.PatternOrDefault?.VerticalScrollPercent.ValueOrDefault ?? -1) > 99);
            checks.Record("long.end", atEnd, $"vertical scroll percent before End={before}, after={run.Find(ExtendedRun.ScrollId)?.Patterns.Scroll.PatternOrDefault?.VerticalScrollPercent.ValueOrDefault}.");

            _ = ClipboardProbe.Clear();
            run.Shell.Chord(RawKeyboard.Control, 0x43);
            ClipboardProbe.Reading reading = ClipboardProbe.Read();
            bool copied = ShellRun.WaitUntil(
                () =>
                {
                    reading = ClipboardProbe.Read();
                    return reading.State == ClipboardProbe.ClipboardState.Text && reading.Text.Contains("Requirements", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(5));
            checks.Record(
                "long.copy",
                copied && reading.Text.StartsWith(run.RowTitle(0), StringComparison.Ordinal) && reading.Text.Length > 60_000,
                $"clipboard state={reading.State}, length={reading.Text.Length}, prefix='{ClipboardProbe.Prefix(reading.Text)}'; Ctrl+C on the scroller copies the title, labels and text.");

            run.Shell.Tap(ShellRun.VkTab);
            checks.Record("long.tabOut", ShellRun.WaitUntil(() => run.Shell.FocusedPane() == "tree"), $"after Tab from the last stop: focused pane={run.Shell.FocusedPane()}; the shell cycles on.");
            run.Measure("longScrollPercent", run.Find(ExtendedRun.ScrollId)?.Patterns.Scroll.PatternOrDefault?.VerticalScrollPercent.ValueOrDefault);
            run.WriteArtifacts("console-extended-help-long.png");
        }

        checks.AssertAllPassed();
    }

    // ---- tab switch keeps list selection and focus (failure modes 6 and 7) ------------------------------

    [TestMethod]
    [TestCategory("Console")]
    public void Console_Extended_TabSwitch()
    {
        var checks = new ShellChecks("switch", "extended");
        using ExtendedRun run = ExtendedRun.Open("switch", 60);
        run.List.FocusList();
        run.Shell.Tap(ListKeys.Home);
        for (int i = 0; i < 3; i++)
        {
            run.Shell.Tap(ListKeys.Down);
        }

        checks.Record("start", ShellRun.WaitUntil(() => run.List.Focus == 3 && run.ShowsRow(3)), $"focus={run.List.Focus}, title='{run.Title}'.");
        string state = ListState(run);

        // 1. Ctrl+Tab.
        run.CtrlTab();
        checks.Record("ctrlTab.standard", ShellRun.WaitUntil(() => run.Tab == "Standard"), $"after Ctrl+Tab: Tab={run.Tab}.");
        checks.Record("ctrlTab.noPane", run.Rect(ExtendedRun.PaneId) is null && run.Number("PaneWidth") == 0, $"pane rectangle={run.Rect(ExtendedRun.PaneId)}, width={run.Number("PaneWidth")} in Standard.");
        CheckKept(checks, run, "ctrlTab", state);

        // 2. A selection change while Standard shows no pane; the model is kept.
        run.Shell.Tap(ListKeys.Down);
        run.Shell.Tap(ListKeys.Down);
        checks.Record("whileStandard.moved", ShellRun.WaitUntil(() => run.List.Focus == 5), $"focus={run.List.Focus}.");
        state = ListState(run);

        // 3. A click on the Extended tab.
        run.Find(ExtendedRun.ExtendedTabId)?.Click();
        checks.Record("click.extended", ShellRun.WaitUntil(() => run.Tab == "Extended"), $"after a click on Extended: Tab={run.Tab}.");
        checks.Record("click.titleIsNewRow", ShellRun.WaitUntil(() => run.ShowsRow(5)), $"title='{run.Title}'; expected the row selected while Standard showed ('{run.RowTitle(5)}').");
        CheckKept(checks, run, "click", state);

        // 4. UI Automation Select on Standard.
        run.Find(ExtendedRun.StandardTabId)?.Patterns.SelectionItem.PatternOrDefault?.Select();
        checks.Record("uia.standard", ShellRun.WaitUntil(() => run.Tab == "Standard"), $"after Select on Standard: Tab={run.Tab}.");
        CheckKept(checks, run, "uia", state);

        // 5. Ctrl+Shift+Tab goes back; then the focus is in the pane (the link) and the switch to Standard moves it to the list.
        run.CtrlTab(shift: true);
        checks.Record("ctrlShiftTab.extended", ShellRun.WaitUntil(() => run.Tab == "Extended"), $"after Ctrl+Shift+Tab: Tab={run.Tab}.");
        run.Shell.Tap(ShellRun.VkTab);
        checks.Record("link.focused", ShellRun.WaitUntil(() => run.Shell.FocusedId() == ExtendedRun.LinkId), $"after Tab: focused id='{run.Shell.FocusedId()}'.");
        run.CtrlTab();
        checks.Record("fromLink.standard", ShellRun.WaitUntil(() => run.Tab == "Standard"), $"Tab={run.Tab}.");
        checks.Record("fromLink.focusOnList", ShellRun.WaitUntil(() => run.Shell.FocusedId() == ListRun.ListId), $"after the switch with the focus on the link: focused id='{run.Shell.FocusedId()}'; the list must have it.");
        run.Shell.Tap(ListKeys.Up);
        checks.Record("fromLink.arrowWorks", ShellRun.WaitUntil(() => run.List.Focus == 4), $"Up from the list moved the focus row to {run.List.Focus}; expected 4.");

        run.Measure("finalState", ListState(run));
        run.WriteArtifacts("console-extended-switch.png");
        checks.AssertAllPassed();
    }

    // ---- link by keyboard (failure mode 8) --------------------------------------------------------

    [TestMethod]
    [TestCategory("Console")]
    public void Console_Extended_Link()
    {
        var checks = new ShellChecks("link", "extended");
        using ExtendedRun run = ExtendedRun.Open("link", 8);
        run.SelectFirstRow();
        checks.Record("startOnList", run.Shell.FocusedId() == ListRun.ListId, $"focused id='{run.Shell.FocusedId()}'.");

        run.Shell.Tap(ShellRun.VkTab);
        checks.Record("tabReachesLink", ShellRun.WaitUntil(() => run.Shell.FocusedId() == ExtendedRun.LinkId), $"after Tab: focused id='{run.Shell.FocusedId()}'.");
        AutomationElement? link = run.Find(ExtendedRun.LinkId);
        checks.Record("linkIsHyperlink", link?.ControlType == ControlType.Hyperlink && ShellRun.SafeName(link) == "Edit policy setting", $"type={link?.ControlType}, name='{ShellRun.SafeName(link)}'.");
        checks.Record("paneStillResult", run.Shell.FocusedPane() == "result", $"focused pane={run.Shell.FocusedPane()}.");

        // Enter raises one event, Space one more; neither raises two.
        run.Shell.Tap(ListKeys.Return);
        checks.Record("enter", ShellRun.WaitUntil(() => run.Shell.StatusText().StartsWith("Link: edit", StringComparison.Ordinal)), $"status after Enter='{run.Shell.StatusText()}'.");
        Thread.Sleep(600);
        checks.Record("enterOnce", run.Shell.StatusText() == "Link: edit n=1", $"status after Enter='{run.Shell.StatusText()}'; expected n=1.");
        run.Shell.Tap(ListKeys.Space);
        checks.Record("space", ShellRun.WaitUntil(() => run.Shell.StatusText() == "Link: edit n=2"), $"status after Space='{run.Shell.StatusText()}'; expected n=2.");
        Thread.Sleep(600);
        checks.Record("spaceOnce", run.Shell.StatusText() == "Link: edit n=2", $"status after Space='{run.Shell.StatusText()}'.");

        // Shift+Tab goes back to the list; Tab twice leaves the result pane (link, then the next pane).
        run.Shell.Chord(RawKeyboard.Shift, ShellRun.VkTab);
        checks.Record("shiftTabToList", ShellRun.WaitUntil(() => run.Shell.FocusedId() == ListRun.ListId), $"after Shift+Tab: focused id='{run.Shell.FocusedId()}'.");
        run.Shell.Tap(ShellRun.VkTab);
        run.Shell.Tap(ShellRun.VkTab);
        checks.Record("tabLeaves", ShellRun.WaitUntil(() => run.Shell.FocusedPane() == "tree"), $"after Tab, Tab: focused pane={run.Shell.FocusedPane()}; the cycle goes on to the tree.");
        run.Shell.Chord(RawKeyboard.Shift, ShellRun.VkTab);
        checks.Record("shiftTabEntersList", ShellRun.WaitUntil(() => run.Shell.FocusedId() == ListRun.ListId), $"Shift+Tab from the tree: focused id='{run.Shell.FocusedId()}'; the result pane is entered at the list.");

        // Standard hides the pane: Tab no longer finds the link.
        run.CtrlTab();
        checks.Record("standard", ShellRun.WaitUntil(() => run.Tab == "Standard"), $"Tab={run.Tab}.");
        run.Shell.Tap(ShellRun.VkTab);
        checks.Record("noLinkInStandard", ShellRun.WaitUntil(() => run.Shell.FocusedPane() == "tree"), $"Tab from the list in Standard: focused pane={run.Shell.FocusedPane()}; there is no link stop.");

        run.WriteArtifacts("console-extended-link.png");
        checks.AssertAllPassed();
    }

    // ---- high contrast (failure mode 10) ---------------------------------------------------------

    [TestMethod]
    [TestCategory("Console")]
    public void Console_Extended_HighContrast()
    {
        var checks = new ShellChecks("contrast", "extended");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["BWC_E2E_HIGH_CONTRAST"] = "1" };
        using ExtendedRun run = ExtendedRun.Open("contrast", 8, environment);
        run.SelectFirstRow();
        Thread.Sleep(500);

        using Bitmap? picture = run.Shell.CaptureClient(out Point origin);
        if (picture is null)
        {
            checks.Record("picture", false, "the window picture could not be judged.");
            checks.AssertAllPassed();
            return;
        }

        // The fixed high contrast palette of the E2E flavor: window 0C0C0C, window text FFFF00, face 202020.
        Color window = Color.FromArgb(0x0C, 0x0C, 0x0C);
        Color face = Color.FromArgb(0x20, 0x20, 0x20);
        Rectangle? pane = run.Rect(ExtendedRun.PaneId);
        Rectangle? extended = run.Rect(ExtendedRun.ExtendedTabId);
        Rectangle? standard = run.Rect(ExtendedRun.StandardTabId);
        Rectangle? title = run.Rect(ExtendedRun.TitleId);
        Rectangle? link = run.Rect(ExtendedRun.LinkId);
        if (pane is null || extended is null || standard is null || title is null || link is null)
        {
            checks.Record("rectangles", false, $"pane={pane}, tabs={extended},{standard}, title={title}, link={link}.");
            checks.AssertAllPassed();
            return;
        }

        Color paneColor = PixelAt(picture, origin, pane.Value.Right - 6, pane.Value.Bottom - 6);
        checks.Record("paneBackground", Close(paneColor, window), $"pane background={Hex(paneColor)}; expected the palette window {Hex(window)}.");
        Color selectedTab = PixelAt(picture, origin, extended.Value.Left + 4, extended.Value.Top + 4);
        Color otherTab = PixelAt(picture, origin, standard.Value.Left + 4, standard.Value.Top + 4);
        checks.Record("selectedTab", Close(selectedTab, window), $"selected tab background={Hex(selectedTab)}; expected the window color.");
        checks.Record("otherTab", Close(otherTab, face), $"unselected tab background={Hex(otherTab)}; expected the face color {Hex(face)}.");
        checks.Record("titleInk", HasPixel(picture, origin, title.Value, c => c.R > 200 && c.G > 200 && c.B < 80), "the title has window text colored (yellow) pixels.");
        checks.Record("linkNotLightBlue", !HasPixel(picture, origin, link.Value, c => c.R < 40 && c.G < 40 && c.B > 200), "the link has no pure blue pixels (the light theme link color).");
        checks.Record("linkHasInk", HasPixel(picture, origin, link.Value, c => Math.Abs(c.R - window.R) + Math.Abs(c.G - window.G) + Math.Abs(c.B - window.B) > 150), "the link has text pixels that differ from the background.");

        run.Measure("paneBackground", Hex(paneColor));
        run.Measure("selectedTab", Hex(selectedTab));
        run.Measure("otherTab", Hex(otherTab));
        run.WriteArtifacts("console-extended-contrast.png");
        checks.AssertAllPassed();
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static Dictionary<string, string> Help(string kind) =>
        new(StringComparer.Ordinal) { ["BWC_E2E_CONSOLE_HELP"] = kind };

    private static bool IsSelected(ExtendedRun run, string id) =>
        run.Find(id)?.Patterns.SelectionItem.PatternOrDefault?.IsSelected.ValueOrDefault == true;

    private static string ListState(ExtendedRun run) =>
        $"selected={run.List.SelectedCount};tags={run.List.Str("SelectedTags")};focus={run.List.Focus};mode={run.List.Mode};offset={run.List.Str("Offset")}";

    private static void CheckKept(ShellChecks checks, ExtendedRun run, string id, string expectedState)
    {
        string now = ListState(run);
        checks.Record(id + ".listKept", now == expectedState, $"list state now '{now}'; before the switch '{expectedState}'.");
        checks.Record(id + ".focusPane", run.Shell.FocusedPane() == "result", $"focused pane={run.Shell.FocusedPane()}.");
    }

    private static double ExpectedPaneWidth(double viewWidth) =>
        Math.Clamp(viewWidth * 212.0 / 589, 48, Math.Max(48, viewWidth - 48));

    private static bool PaneMatchesFormula(ExtendedRun run)
    {
        double view = run.Number("ViewWidth");
        Rectangle? pane = run.Rect(ExtendedRun.PaneId);
        return double.IsFinite(view) && pane is not null &&
            Near(run.Number("PaneWidth"), ExpectedPaneWidth(view), 0.05) &&
            Near(run.Logical(pane.Value.Width), ExpectedPaneWidth(view), 1.2);
    }

    private static string Describe(ExtendedRun run)
    {
        double view = run.Number("ViewWidth");
        Rectangle? pane = run.Rect(ExtendedRun.PaneId);
        return $"view width={view:0.##} DIP, readout pane={run.Number("PaneWidth"):0.##}, UIA pane={(pane is null ? double.NaN : run.Logical(pane.Value.Width)):0.##}, formula={ExpectedPaneWidth(view):0.##}.";
    }

    private static void CheckWordInside(ShellChecks checks, ExtendedRun run, string id)
    {
        Rectangle? pane = run.Rect(ExtendedRun.PaneId);
        Rectangle word = run.Find("ConsoleDescriptionText_description")?.BoundingRectangle ?? Rectangle.Empty;
        bool horizontal = run.Find(ExtendedRun.ScrollId)?.Patterns.Scroll.PatternOrDefault?.HorizontallyScrollable.ValueOrDefault == true;
        if (word.Width <= 0)
        {
            // In a very narrow pane the wrapped word starts below the visible part and UI Automation gives it no rectangle:
            // the pane must still have no horizontal scroll, which is what an overflowing word would cause.
            checks.Record(id, pane is not null && !horizontal, $"the help text is below the visible part; horizontally scrollable={horizontal}, pane {pane}.");
            return;
        }

        checks.Record(
            id,
            pane is not null && !horizontal && word.Left >= pane.Value.Left - 1 && word.Right <= pane.Value.Right + 1,
            $"help text rectangle {word} against the pane {pane}, horizontally scrollable={horizontal}; the 300 character word must wrap inside the pane.");
    }

    private static Color PixelAt(Bitmap picture, Point origin, int screenX, int screenY) =>
        picture.GetPixel(Math.Clamp(screenX - origin.X, 0, picture.Width - 1), Math.Clamp(screenY - origin.Y, 0, picture.Height - 1));

    private static bool HasPixel(Bitmap picture, Point origin, Rectangle screenRect, Func<Color, bool> match)
    {
        int left = Math.Max(0, screenRect.Left - origin.X);
        int top = Math.Max(0, screenRect.Top - origin.Y);
        int right = Math.Min(picture.Width, screenRect.Right - origin.X);
        int bottom = Math.Min(picture.Height, screenRect.Bottom - origin.Y);
        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                if (match(picture.GetPixel(x, y)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool Close(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) <= 12;

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static bool Near(double actual, double expected, double tolerance) => Math.Abs(actual - expected) <= tolerance;
}

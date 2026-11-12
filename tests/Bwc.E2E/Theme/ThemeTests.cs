using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Theme;

/// <summary>
/// Read-only palette checks on the Registry Editor. Each case opens the editor, selects
/// HKEY_CURRENT_USER\Console with the keyboard, moves focus to the list with Tab, selects one
/// value with Down, and samples pixels from a verified, settled screen copy of the main window (WindowCapture). The tree
/// selection must match the Explorer visual style, the list selection must match the classic list
/// view, the pane backgrounds must equal the window color, and only the focused selected row carries
/// a dotted focus rectangle.
/// </summary>
/// <remarks>
/// Expected tree colors are derived in this test by drawing Explorer::TreeView with
/// DrawThemeBackground and sampling the bitmap, which is the method the app uses. The derived values
/// are also compared with docs/parity/oracle/regedit-selection-colors.json. The list colors come
/// from GetSysColor for Light and from the computed dark palette for Dark. Captures and sampled
/// values are written under artifacts/e2e/visual/theme/. The Light and Dark cases force the palette
/// with BWC_E2E_THEME, which the app reads only in E2E builds.
/// </remarks>
[TestClass]
public sealed class ThemeTests
{
    private const string ThemeEnvironmentVariable = "BWC_E2E_THEME";
    private const string ConsoleKey = "Console";
    private const string HkcuKey = "HKEY_CURRENT_USER";
    private const string TreeRowPrefix = "TreeRow_";
    private const string ListRowPrefix = "ListRow_";

    private const int ColorWindow = 5;
    private const int ColorWindowText = 8;
    private const int ColorHighlight = 13;
    private const int ColorHighlightText = 14;
    private const int ColorBtnFace = 15;

    private const int StrokeTolerance = 24;
    private const int DottedMinPercent = 30;
    private const int DottedMaxPercent = 70;
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SettleInterval = TimeSpan.FromMilliseconds(150);
    private const int TreeBaseWidthDip = 64;
    private const int TreeBaseHeightDip = 32;

    private static readonly Color DarkWindowColor = Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E);
    private static readonly Color DarkInactiveColor = Color.FromArgb(0xFF, 0x3F, 0x3F, 0x46);

    /// <summary>Oracle values from docs/parity/oracle/regedit-selection-colors.json (this OS build, 200 percent).</summary>
    private static readonly Color OracleTreeFocusFill = Color.FromArgb(0xFF, 0xCC, 0xE8, 0xFF);
    private static readonly Color OracleTreeFocusBorder = Color.FromArgb(0xFF, 0x00, 0x78, 0xD4);
    private static readonly Color OracleTreeInactiveFill = Color.FromArgb(0xFF, 0xD9, 0xD9, 0xD9);
    private static readonly Color OracleTreeInactiveBorder = Color.FromArgb(0xFF, 0x94, 0x94, 0x94);

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Theme")]
    public void Theme_SystemPalette_FollowsWindowsAppMode()
    {
        RunPaletteCase(
            caseId: "system",
            mode: SystemIsDark() ? "Dark" : "Light",
            environment: null);
    }

    [TestMethod]
    [TestCategory("Theme")]
    [TestCategory("E2EOnly")]
    public void Theme_E2EOnly_LightOverride_UsesSystemColorTable()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        RunPaletteCase(
            caseId: "light",
            mode: "Light",
            environment: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ThemeEnvironmentVariable] = "Light",
            });
    }

    [TestMethod]
    [TestCategory("Theme")]
    [TestCategory("E2EOnly")]
    public void Theme_E2EOnly_DarkOverride_UsesDarkPalette()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        RunPaletteCase(
            caseId: "dark",
            mode: "Dark",
            environment: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ThemeEnvironmentVariable] = "Dark",
            });
    }

    private static void RunPaletteCase(string caseId, string mode, IReadOnlyDictionary<string, string>? environment)
    {
        var config = GuiGate.Config;
        int start = E2EResults.CheckCount;
        config.EnsureExecutablesExist();

        ExpectedPalette expected = ExpectedFor(mode);
        var samples = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mode"] = mode,
            ["expectedWindow"] = Hex(expected.Window),
            ["expectedTreeFocusFill"] = Hex(expected.TreeFocusFill),
            ["expectedTreeFocusBorder"] = expected.TreeFocusBorder is { } tfb ? Hex(tfb) : "none",
            ["expectedTreeInactiveFill"] = Hex(expected.TreeInactiveFill),
            ["expectedTreeInactiveBorder"] = expected.TreeInactiveBorder is { } tib ? Hex(tib) : "none",
            ["expectedListFocusFill"] = Hex(expected.ListFocusFill),
            ["expectedListFocusText"] = Hex(expected.ListFocusText),
            ["expectedListInactiveFill"] = Hex(expected.ListInactiveFill),
            ["explorerSource"] = expected.ExplorerSource,
        };
        Directory.CreateDirectory(VisualDir(config));

        if (string.Equals(mode, "Light", StringComparison.Ordinal))
        {
            CheckOracleMatch(caseId, expected, samples);
        }

        // The editor saves its last visited key under the data root. This case gets its own folder so
        // selecting Console here cannot change the launch state that the shell tests read.
        var sessionEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in config.DataRootEnvironment("theme-" + caseId))
        {
            sessionEnvironment[pair.Key] = pair.Value;
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                sessionEnvironment[pair.Key] = pair.Value;
            }
        }

        using var session = new AppSession(config, config.RegeditExePath, environment: sessionEnvironment);
        session.WaitForElement(ControlTestSupport.TreeControl);
        session.Find(ControlTestSupport.TreeControl)?.Focus();

        bool navigated = NavigateToConsole(session);
        Record(caseId, "navigate.console", navigated, "Keyboard navigation reached HKEY_CURRENT_USER\\Console.");
        if (!navigated)
        {
            AssertNoFailures(start, caseId);
            return;
        }

        bool listRows = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.Rows(session, ControlTestSupport.ListControl, ControlType.DataItem).Length > 0,
            ControlTestSupport.UiTimeout);
        Record(caseId, "list.hasRows", listRows, "The list shows values for Console.");
        if (!listRows)
        {
            AssertNoFailures(start, caseId);
            return;
        }

        // Phase one: the tree has keyboard focus and its selected row is the focused selection.
        // Every pixel probe below is settled against fresh captures (see Settle), so the rectangles
        // stay in screen coordinates and each probe converts them to its own capture.
        AutomationElement? treeRow = SelectedTreeRow(session);
        if (treeRow is null)
        {
            Record(caseId, "tree.selectedRow", false, "No tree row reports IsSelected.");
            AssertNoFailures(start, caseId);
            return;
        }

        Rectangle treeRowRect = treeRow.BoundingRectangle;
        Rectangle treePaneRect = ElementRect(session, ControlTestSupport.TreeControl);
        Rectangle listPaneRect = ElementRect(session, ControlTestSupport.ListControl);
        bool treeHasFocus = HasKeyboardFocus(session.Find(ControlTestSupport.TreeControl)) || HasKeyboardFocus(treeRow);
        Record(caseId, "focus.tree.start", treeHasFocus, "Keyboard focus is in the tree before Tab.");

        IntPtr hwnd = session.MainHwnd;
        int fillX = treeRowRect.Right - 8;
        int fillY = treeRowRect.Top + treeRowRect.Height / 2;
        CheckPixel(caseId, "tree.focusedSelection", hwnd, fillX, fillY, expected.TreeFocusFill, "The focused tree selection is the Explorer selected fill.", samples, "treeFocusedSelection");
        CheckBorder(caseId, "tree.focusedBorder", hwnd, fillX, treeRowRect.Top, expected.TreeFocusBorder, samples, "treeFocusedBorder");

        bool dottedTree = SettledDotted(hwnd, treeRowRect, expected.TreeText, expectedDotted: true);
        Record(caseId, "tree.focusRect.onFocusedRow", dottedTree, $"A one pixel dotted rectangle is on the focused tree row. Observed {(dottedTree ? "dotted" : "not dotted")}.");
        samples["treeFocusRectDotted"] = dottedTree ? "yes" : "no";

        int aboveTree = treeRowRect.Top - treeRowRect.Height;
        var rowAbove = new Rectangle(treeRowRect.Left, aboveTree, treeRowRect.Width, treeRowRect.Height);
        bool aboveHasFill = SettledBand(hwnd, rowAbove, expected.TreeFocusFill, expectedFill: false);
        Record(caseId, "tree.focusRect.notOnOtherRows", !aboveHasFill, $"The row above the focused row has no selection or focus pixels. Observed {(aboveHasFill ? "fill" : "none")}.");

        CheckPixel(caseId, "tree.background", hwnd, treePaneRect.Left + 380, treePaneRect.Bottom - 20, expected.Window, "Tree background is COLOR_WINDOW.", samples, "treeBackground");
        CheckPixel(caseId, "list.background", hwnd, listPaneRect.Left + 1200, listPaneRect.Bottom - 60, expected.Window, "List background is COLOR_WINDOW.", samples, "listBackground");
        CheckPixel(caseId, "tree.edge", hwnd, treePaneRect.Left + 1, treePaneRect.Top + 200, expected.Window, "The tree pane edge has no system focus rectangle.", samples, "treeEdge");

        using (Bitmap treeCapture = CaptureWindow(hwnd, out _))
        {
            string treeCapturePath = SaveCapture(config, $"main-{mode.ToLowerInvariant()}-{caseId}-tree-focus", treeCapture);
            E2EResults.Note($"theme.{caseId}.capture.tree", treeCapturePath);
        }

        // Phase two: move focus to the list and select one value with the keyboard.
        bool listFocused = FocusList(session);
        Record(caseId, "focus.list.tab", listFocused, "Tab moves keyboard focus to the list.");
        if (!listFocused)
        {
            WriteSamples(config, caseId, samples);
            AssertNoFailures(start, caseId);
            return;
        }

        session.SendKeys("Down");
        AutomationElement? selectedList = null;
        _ = ControlTestSupport.WaitUntil(
            () =>
            {
                selectedList = SelectedListRow(session);
                return selectedList is not null;
            },
            TimeSpan.FromSeconds(5));
        Record(caseId, "list.selectedRow", selectedList is not null, "One list row is selected after Down.");
        if (selectedList is null)
        {
            WriteSamples(config, caseId, samples);
            AssertNoFailures(start, caseId);
            return;
        }

        // UI Automation reports the selection before the list repaints it. Each probe below settles
        // against fresh captures, so it waits for the paint instead of sleeping for a fixed time.
        Rectangle listRowRect = selectedList.BoundingRectangle;
        Rectangle listPaneRectAgain = ElementRect(session, ControlTestSupport.ListControl);
        Rectangle treeRowAgain = treeRow.BoundingRectangle;
        int listFillRight = Math.Min(listRowRect.Right, listPaneRectAgain.Right - 60) - 8;

        CheckPixel(caseId, "list.focusedSelection", hwnd, listFillRight, listRowRect.Top + listRowRect.Height / 2, expected.ListFocusFill, "The focused list selection is the classic list highlight.", samples, "listFocusedSelection");
        bool dottedList = SettledDotted(hwnd, listRowRect, expected.ListFocusText, expectedDotted: true);
        Record(caseId, "list.focusRect.onFocusedRow", dottedList, $"A one pixel dotted rectangle is on the focused list row. Observed {(dottedList ? "dotted" : "not dotted")}.");
        samples["listFocusRectDotted"] = dottedList ? "yes" : "no";

        CheckPixel(caseId, "tree.unfocusedSelection", hwnd, treeRowAgain.Right - 8, treeRowAgain.Top + treeRowAgain.Height / 2, expected.TreeInactiveFill, "The unfocused tree selection is the Explorer not-focused fill (dark: muted).", samples, "treeUnfocusedSelection");
        CheckBorder(caseId, "tree.unfocusedBorder", hwnd, treeRowAgain.Right - 8, treeRowAgain.Top, expected.TreeInactiveBorder, samples, "treeUnfocusedBorder");
        bool dottedTreeUnfocused = SettledDotted(hwnd, treeRowAgain, expected.TreeText, expectedDotted: false);
        Record(caseId, "tree.focusRect.absentWhenUnfocused", !dottedTreeUnfocused, $"The unfocused tree row has no focus rectangle. Observed {(dottedTreeUnfocused ? "dotted" : "none")}.");
        samples["treeUnfocusedHasStroke"] = dottedTreeUnfocused ? "yes" : "no";

        using (Bitmap listCapture = CaptureWindow(hwnd, out _))
        {
            string listCapturePath = SaveCapture(config, $"main-{mode.ToLowerInvariant()}-{caseId}-list-focus", listCapture);
            E2EResults.Note($"theme.{caseId}.capture.list", listCapturePath);
        }

        // Phase three: Tab back to the tree. The list keeps its selection but loses focus, so the
        // classic list draws no selection (the row stays the window color, dark: muted).
        bool treeFocused = FocusTree(session);
        Record(caseId, "focus.tree.return", treeFocused, "Tab moves keyboard focus back to the tree.");
        if (treeFocused)
        {
            Rectangle listRowReturn = selectedList.BoundingRectangle;
            int listReturnRight = Math.Min(listRowReturn.Right, ElementRect(session, ControlTestSupport.ListControl).Right - 60) - 8;
            CheckPixel(caseId, "list.unfocusedSelection", hwnd, listReturnRight, listRowReturn.Top + listRowReturn.Height / 2, expected.ListInactiveFill, "The unfocused list selection is not drawn (light: window color).", samples, "listUnfocusedSelection");
            using Bitmap returnCapture = CaptureWindow(hwnd, out _);
            string returnPath = SaveCapture(config, $"main-{mode.ToLowerInvariant()}-{caseId}-tree-refocus", returnCapture);
            E2EResults.Note($"theme.{caseId}.capture.refocus", returnPath);
        }

        WriteSamples(config, caseId, samples);
        AssertNoFailures(start, caseId);
    }

    /// <summary>
    /// Checks the Explorer tree colors that this test derives against the committed oracle. The
    /// oracle was sampled from classic regedit on this OS build, so a mismatch means the Explorer
    /// style or the derivation changed, and it is reported as a failed check.
    /// </summary>
    private static void CheckOracleMatch(string caseId, ExpectedPalette expected, Dictionary<string, string> samples)
    {
        bool fill = Near(expected.TreeFocusFill, OracleTreeFocusFill) && Near(expected.TreeInactiveFill, OracleTreeInactiveFill);
        bool border = expected.TreeFocusBorder is { } fb && Near(fb, OracleTreeFocusBorder)
            && expected.TreeInactiveBorder is { } ib && Near(ib, OracleTreeInactiveBorder);
        samples["oracleTreeMatch"] = fill && border ? "yes" : "no";
        Record(caseId, "oracle.tree.matchesClassic", fill && border, $"Explorer tree colors match the classic oracle. Derived {Hex(expected.TreeFocusFill)}/{Hex(expected.TreeFocusBorder ?? Color.Empty)}, oracle {Hex(OracleTreeFocusFill)}/{Hex(OracleTreeFocusBorder)}.");
    }

    /// <summary>
    /// Moves the tree selection down with the keyboard until HKEY_CURRENT_USER\Console is selected.
    /// Right expands HKEY_CURRENT_USER only when Console is not yet visible.
    /// </summary>
    private static bool NavigateToConsole(AppSession session)
    {
        for (int step = 0; step < 400; step++)
        {
            AutomationElement[] rows = ControlTestSupport.Rows(session, ControlTestSupport.TreeControl, ControlType.TreeItem);
            AutomationElement? selected = rows.FirstOrDefault(ControlTestSupport.IsSelected);
            string selectedName = ControlTestSupport.SafeName(selected);
            if (string.Equals(selectedName, ConsoleKey, StringComparison.Ordinal))
            {
                return true;
            }

            bool consoleVisible = rows.Any(row =>
                string.Equals(ControlTestSupport.SafeName(row), ConsoleKey, StringComparison.Ordinal));
            if (string.Equals(selectedName, HkcuKey, StringComparison.Ordinal) && !consoleVisible)
            {
                session.SendKeys("Right");
            }
            else
            {
                session.SendKeys("Down");
            }

            Thread.Sleep(80);
        }

        return false;
    }

    private static bool FocusList(AppSession session) =>
        MoveFocusTo(session, ControlTestSupport.ListControl);

    /// <summary>Presses Tab until the tree has keyboard focus again. The address bar can take one stop.</summary>
    private static bool FocusTree(AppSession session) =>
        MoveFocusTo(session, ControlTestSupport.TreeControl);

    /// <summary>
    /// Brings the editor to the foreground, then presses Tab until the named control reports keyboard
    /// focus. Each press waits up to two seconds for the focus change to show in UI Automation, so a
    /// slow focus move is not counted as a failed Tab.
    /// </summary>
    private static bool MoveFocusTo(AppSession session, string automationId)
    {
        _ = ControlTestSupport.EnsureForeground(session);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (HasKeyboardFocus(session.Find(automationId)))
            {
                return true;
            }

            session.SendKeys("Tab");
            _ = ControlTestSupport.WaitUntil(
                () => HasKeyboardFocus(session.Find(automationId)),
                TimeSpan.FromSeconds(2));
        }

        return HasKeyboardFocus(session.Find(automationId));
    }

    private static AutomationElement? SelectedTreeRow(AppSession session)
    {
        return ControlTestSupport.Rows(session, ControlTestSupport.TreeControl, ControlType.TreeItem)
            .FirstOrDefault(row => ControlTestSupport.IsSelected(row) && IsRowElement(row, TreeRowPrefix));
    }

    private static AutomationElement? SelectedListRow(AppSession session)
    {
        return ControlTestSupport.Rows(session, ControlTestSupport.ListControl, ControlType.DataItem)
            .FirstOrDefault(row => ControlTestSupport.IsSelected(row) && IsRowElement(row, ListRowPrefix));
    }

    private static bool IsRowElement(AutomationElement element, string prefix)
    {
        string id = ControlTestSupport.SafeId(element);
        return id.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static bool HasKeyboardFocus(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            return element.Properties.HasKeyboardFocus.Value;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static System.Drawing.Rectangle ElementRect(AppSession session, string automationId)
    {
        AutomationElement? element = session.Find(automationId);
        return element is null
            ? System.Drawing.Rectangle.Empty
            : element.BoundingRectangle;
    }

    /// <summary>
    /// Dotted means alternating pixels along the top edge of the row. The band starts one device
    /// pixel below the row top, where the one pixel stroke sits. A stroke pixel is one that matches
    /// the focus rectangle color (the selection text color), so the solid selection border, which
    /// sits on the same line, does not count. The stroke must cover 30 to 70 percent of the band.
    /// </summary>
    private static bool HasDottedFocusRect(Bitmap capture, Rectangle row, Color stroke)
    {
        if (row.IsEmpty || row.Width <= 16)
        {
            return false;
        }

        int left = row.Left + 4;
        int right = row.Right - 4;
        int width = right - left;
        if (width <= 0)
        {
            return false;
        }

        // The stroke is one device pixel high. Scan the first three rows below the top edge and
        // use the row with the most stroke pixels, so a one pixel placement difference does not matter.
        for (int y = row.Top + 1; y <= row.Top + 3; y++)
        {
            int strokeCount = 0;
            for (int x = left; x < right; x++)
            {
                if (Near(PixelAt(capture, x, y), stroke))
                {
                    strokeCount++;
                }
            }

            int strokePercent = strokeCount * 100 / width;
            if (strokePercent >= DottedMinPercent && strokePercent <= DottedMaxPercent)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasColorInTopBand(Bitmap capture, Rectangle row, Color color)
    {
        if (row.IsEmpty || row.Width <= 16)
        {
            return false;
        }

        int hits = 0;
        for (int x = row.Left + 4; x < row.Right - 4; x++)
        {
            if (Near(PixelAt(capture, x, row.Top + 1), color))
            {
                hits++;
            }
        }

        return hits > 2;
    }

    private static void CheckColor(
        string caseId,
        string check,
        Color observed,
        Color expected,
        string notes,
        Dictionary<string, string> samples,
        string sampleKey)
    {
        samples[sampleKey] = Hex(observed);
        bool pass = Near(observed, expected);
        Record(caseId, check, pass, $"{notes} Expected {Hex(expected)}, observed {Hex(observed)}.");
    }

    /// <summary>
    /// Checks the selection border on the top edge of a row. The border sits on the first or second
    /// device pixel, so the three rows from the top are scanned. A null expected border means the
    /// palette draws none, and the check is skipped.
    /// </summary>
    private static void CheckBorder(
        string caseId,
        string check,
        IntPtr hwnd,
        int screenX,
        int screenTop,
        Color? expected,
        Dictionary<string, string> samples,
        string sampleKey)
    {
        if (expected is not { } border)
        {
            samples[sampleKey] = "none";
            return;
        }

        (Color First, Color Second, Color Third) rows = Settle(
            hwnd,
            (bitmap, window) => (
                PixelAt(bitmap, screenX - window.X, screenTop - window.Y),
                PixelAt(bitmap, screenX - window.X, screenTop + 1 - window.Y),
                PixelAt(bitmap, screenX - window.X, screenTop + 2 - window.Y)),
            v => Near(v.Item1, border) || Near(v.Item2, border) || Near(v.Item3, border));
        string text = $"{Hex(rows.First)},{Hex(rows.Second)},{Hex(rows.Third)}";
        samples[sampleKey] = text;
        bool pass = Near(rows.First, border) || Near(rows.Second, border) || Near(rows.Third, border);
        Record(caseId, check, pass, $"The selection border is one device pixel {Hex(border)} on the row top edge. Rows: {text}.");
    }

    /// <summary>Settles one pixel at a screen point and checks it against the expected color.</summary>
    private static void CheckPixel(
        string caseId,
        string check,
        IntPtr hwnd,
        int screenX,
        int screenY,
        Color expected,
        string notes,
        Dictionary<string, string> samples,
        string sampleKey)
    {
        Color observed = Settle(
            hwnd,
            (bitmap, window) => PixelAt(bitmap, screenX - window.X, screenY - window.Y),
            c => Near(c, expected));
        CheckColor(caseId, check, observed, expected, notes, samples, sampleKey);
    }

    /// <summary>Settles the dotted focus rectangle check on a screen row. True means the pattern is present.</summary>
    private static bool SettledDotted(IntPtr hwnd, Rectangle screenRow, Color stroke, bool expectedDotted) =>
        Settle(
            hwnd,
            (bitmap, window) => HasDottedFocusRect(bitmap, ToBitmap(screenRow, window), stroke),
            v => v == expectedDotted);

    /// <summary>Settles the check for a fill color along the top edge of a screen row.</summary>
    private static bool SettledBand(IntPtr hwnd, Rectangle screenRow, Color color, bool expectedFill) =>
        Settle(
            hwnd,
            (bitmap, window) => HasColorInTopBand(bitmap, ToBitmap(screenRow, window), color),
            v => v == expectedFill);

    /// <summary>
    /// Samples a probe from fresh window captures until its value matches the expectation, or until
    /// two consecutive samples SettleInterval apart are equal. Gives up after SettleTimeout and
    /// returns the last observed value, so a failed check reports what the window showed.
    /// </summary>
    private static T Settle<T>(IntPtr hwnd, Func<Bitmap, Rectangle, T> probe, Func<T, bool> matches)
    {
        DateTime deadline = DateTime.UtcNow + SettleTimeout;
        T previous = default!;
        bool havePrevious = false;
        while (true)
        {
            T value;
            using (Bitmap bitmap = CaptureWindow(hwnd, out Rectangle window))
            {
                value = probe(bitmap, window);
            }

            if (matches(value))
            {
                return value;
            }

            if (havePrevious && EqualityComparer<T>.Default.Equals(value, previous))
            {
                return value;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return value;
            }

            previous = value;
            havePrevious = true;
            Thread.Sleep(SettleInterval);
        }
    }

    private static void Record(string caseId, string check, bool pass, string notes)
    {
        _ = E2EResults.Record($"theme.{caseId}.{check}", pass, notes);
    }

    private static void AssertNoFailures(int start, string caseId)
    {
        var failures = E2EResults.FailedChecksSince(start);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Theme checks failed for '{caseId}':{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private static string VisualDir(E2EConfig config) =>
        Path.Combine(config.ArtifactsDir, "visual", "theme");

    private static string SaveCapture(E2EConfig config, string name, Bitmap capture)
    {
        Directory.CreateDirectory(VisualDir(config));
        string path = Path.Combine(VisualDir(config), name + ".png");
        capture.Save(path, ImageFormat.Png);
        return path;
    }

    private static void WriteSamples(E2EConfig config, string caseId, Dictionary<string, string> samples)
    {
        Directory.CreateDirectory(VisualDir(config));
        string path = Path.Combine(VisualDir(config), $"samples-{caseId}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
        E2EResults.Note($"theme.{caseId}.samples", path);
    }

    /// <summary>
    /// The main window as the screen shows it, verified unobscured by the app (see <see cref="WindowCapture"/>).
    /// A covered window throws <see cref="WindowCapture.NotJudgedException"/>, so the test is inconclusive
    /// rather than sampling another window's pixels.
    /// </summary>
    private static Bitmap CaptureWindow(IntPtr hwnd, out Rectangle windowRect)
    {
        if (!GetWindowRect(hwnd, out NativeRect rect))
        {
            throw new InvalidOperationException("GetWindowRect failed for the Registry Editor window.");
        }

        windowRect = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return WindowCapture.CaptureWindow(hwnd);
    }

    private static Rectangle ToBitmap(Rectangle screen, Rectangle window) =>
        new(screen.X - window.X, screen.Y - window.Y, screen.Width, screen.Height);

    private static Color PixelAt(Bitmap bitmap, int x, int y)
    {
        if (x < 0 || y < 0 || x >= bitmap.Width || y >= bitmap.Height)
        {
            return Color.Empty;
        }

        return bitmap.GetPixel(x, y);
    }

    private static bool Near(Color a, Color b)
    {
        if (a.IsEmpty || b.IsEmpty)
        {
            return false;
        }

        int distance = Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
        return distance <= StrokeTolerance;
    }

    private static string Hex(Color color) => color.IsEmpty ? "none" : $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>
    /// The system apps mode, read from the current user hive (read only). The app reads the same
    /// setting through UISettings. A missing value means light, which is the Windows default.
    /// </summary>
    private static bool SystemIsDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
        object? value = key?.GetValue("AppsUseLightTheme");
        return value is int lightFlag && lightFlag == 0;
    }

    /// <summary>
    /// The Windows accent from the DWM AccentColor value (read only), which is ABGR. The app reads
    /// the same accent through UISettings. The test project does not reference the WinRT projection,
    /// so this registry value is the one assumption behind the dark focused selection check. A
    /// missing value falls back to the Windows default accent.
    /// </summary>
    private static Color SystemAccent()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM", writable: false);
        if (key?.GetValue("AccentColor") is int abgr)
        {
            uint value = unchecked((uint)abgr);
            return Color.FromArgb(0xFF, (int)(value & 0xFF), (int)((value >> 8) & 0xFF), (int)((value >> 16) & 0xFF));
        }

        return Color.FromArgb(0xFF, 0x00, 0x78, 0xD4);
    }

    private static ExpectedPalette ExpectedFor(string mode)
    {
        if (string.Equals(mode, "Dark", StringComparison.Ordinal))
        {
            Color accent = SystemAccent();
            bool lightAccent = Luminance(accent.R, accent.G, accent.B) > 0.6;
            Color darkText = lightAccent ? Color.Black : Color.White;
            return new ExpectedPalette(
                Window: DarkWindowColor,
                WindowText: Color.White,
                TreeFocusFill: accent,
                TreeFocusBorder: null,
                TreeInactiveFill: DarkInactiveColor,
                TreeInactiveBorder: null,
                TreeText: Color.White,
                ListFocusFill: accent,
                ListFocusText: darkText,
                ListInactiveFill: DarkInactiveColor,
                ExplorerSource: "dark palette (computed, not Explorer)");
        }

        // Light: the tree comes from Explorer::TreeView, derived here, and the list from the classic list view.
        ExplorerTreeColors tree = DeriveExplorerTreeColors();
        return new ExpectedPalette(
            Window: SystemColor(ColorWindow),
            WindowText: SystemColor(ColorWindowText),
            TreeFocusFill: tree.FocusFill,
            TreeFocusBorder: tree.FocusBorder,
            TreeInactiveFill: tree.InactiveFill,
            TreeInactiveBorder: tree.InactiveBorder,
            TreeText: tree.Text ?? SystemColor(ColorWindowText),
            ListFocusFill: SystemColor(ColorHighlight),
            ListFocusText: SystemColor(ColorHighlightText),
            ListInactiveFill: SystemColor(ColorWindow),
            ExplorerSource: tree.Source);
    }

    /// <summary>
    /// Derives the Explorer tree selection the same way the app does: open Explorer::TreeView,
    /// draw the selected and not-focused item into a 32 bit bitmap at the system DPI, and sample the
    /// center pixel for the fill and the top edge for the border. Falls back to the classic system
    /// colors when the visual style is off, and says so in the source field.
    /// </summary>
    private static ExplorerTreeColors DeriveExplorerTreeColors()
    {
        if (!IsThemeActive())
        {
            return ExplorerTreeColors.Fallback(SystemColor(ColorHighlight), SystemColor(ColorBtnFace), SystemColor(ColorHighlightText), "fallback: visual style off");
        }

        IntPtr theme = OpenThemeData(GetDesktopWindow(), "Explorer::TreeView");
        if (theme == IntPtr.Zero)
        {
            return ExplorerTreeColors.Fallback(SystemColor(ColorHighlight), SystemColor(ColorBtnFace), SystemColor(ColorHighlightText), "fallback: OpenThemeData failed");
        }

        try
        {
            int dpi = (int)GetDpiForSystem();
            int width = Math.Max(8, TreeBaseWidthDip * dpi / 96);
            int height = Math.Max(8, TreeBaseHeightDip * dpi / 96);
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr dc = CreateCompatibleDC(screen);
            _ = ReleaseDC(IntPtr.Zero, screen);
            Color focusFill;
            Color focusEdge;
            Color inactiveFill;
            Color inactiveEdge;
            try
            {
                var info = new BitmapInfo
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfo>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,
                };
                IntPtr dib = CreateDIBSection(dc, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
                IntPtr previous = SelectObject(dc, dib);
                try
                {
                    (focusFill, focusEdge) = DrawAndSample(theme, dc, bits, width, height, TreisSelected);
                    (inactiveFill, inactiveEdge) = DrawAndSample(theme, dc, bits, width, height, TreisSelectedNotFocus);
                }
                finally
                {
                    _ = SelectObject(dc, previous);
                    _ = DeleteObject(dib);
                }
            }
            finally
            {
                _ = DeleteDC(dc);
            }

            return new ExplorerTreeColors(
                FocusFill: focusFill,
                FocusBorder: Near(focusEdge, focusFill) ? null : focusEdge,
                InactiveFill: inactiveFill,
                InactiveBorder: Near(inactiveEdge, inactiveFill) ? null : inactiveEdge,
                Text: null,
                Source: $"Explorer::TreeView derived at {dpi} dpi ({width}x{height})");
        }
        finally
        {
            CloseThemeData(theme);
        }
    }

    /// <summary>TVP_TREEITEM is part 1. Part 2 is TVP_GLYPH, the expand chevron.</summary>
    private const int TvpTreeItem = 1;
    private const int TreisSelected = 3;
    private const int TreisSelectedNotFocus = 5;

    /// <summary>
    /// Clears the DIB to white, draws one state of the tree item, and reads the pixels back. The
    /// fill is the center pixel and the border is the top edge pixel.
    /// </summary>
    private static (Color Fill, Color Edge) DrawAndSample(IntPtr theme, IntPtr dc, IntPtr bits, int width, int height, int state)
    {
        var white = new int[width * height];
        Array.Fill(white, 0x00FFFFFF);
        Marshal.Copy(white, 0, bits, white.Length);

        var rect = new NativeRect { Left = 0, Top = 0, Right = width, Bottom = height };
        _ = DrawThemeBackground(theme, dc, TvpTreeItem, state, ref rect, IntPtr.Zero);

        var pixels = new int[width * height];
        Marshal.Copy(bits, pixels, 0, pixels.Length);
        return (FromPixel(pixels[(height / 2) * width + width / 2]), FromPixel(pixels[width / 2]));
    }

    /// <summary>GDI pixels are 0xAARRGGBB as a 32 bit value on a little endian machine.</summary>
    private static Color FromPixel(int pixel) =>
        Color.FromArgb(0xFF, (pixel >> 16) & 0xFF, (pixel >> 8) & 0xFF, pixel & 0xFF);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    /// <summary>GetSysColor returns a COLORREF: red in the low byte, then green, then blue.</summary>
    private static Color SystemColor(int index)
    {
        uint colorRef = GetSysColor(index);
        return Color.FromArgb(
            0xFF,
            (int)(colorRef & 0xFF),
            (int)((colorRef >> 8) & 0xFF),
            (int)((colorRef >> 16) & 0xFF));
    }

    private static double Luminance(byte r, byte g, byte b) =>
        (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;

    private sealed record ExplorerTreeColors(
        Color FocusFill,
        Color? FocusBorder,
        Color InactiveFill,
        Color? InactiveBorder,
        Color? Text,
        string Source)
    {
        public static ExplorerTreeColors Fallback(Color highlight, Color btnFace, Color highlightText, string source) =>
            new(highlight, null, btnFace, null, highlightText, source);
    }

    private sealed record ExpectedPalette(
        Color Window,
        Color WindowText,
        Color TreeFocusFill,
        Color? TreeFocusBorder,
        Color TreeInactiveFill,
        Color? TreeInactiveBorder,
        Color TreeText,
        Color ListFocusFill,
        Color ListFocusText,
        Color ListInactiveFill,
        string ExplorerSource);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

#pragma warning disable SYSLIB1054 // The small user32 and uxtheme surface does not justify LibraryImport plumbing.
    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);


    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("uxtheme.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsThemeActive();

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenThemeData(IntPtr hwnd, string classList);

    [DllImport("uxtheme.dll")]
    private static extern int CloseThemeData(IntPtr theme);

    [DllImport("uxtheme.dll")]
    private static extern int DrawThemeBackground(IntPtr theme, IntPtr hdc, int part, int state, ref NativeRect rect, IntPtr clipRect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
#pragma warning restore SYSLIB1054
}

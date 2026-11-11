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
/// value with Down, and samples pixels from a PrintWindow capture of the main window. The focused
/// pane selection must equal the highlight color, the unfocused pane selection must equal the
/// inactive color, the pane backgrounds must equal the window color, and only the focused
/// selected row carries a dotted focus rectangle.
/// </summary>
/// <remarks>
/// Expected colors come from GetSysColor for Light, and from the computed dark palette with the
/// Windows accent for Dark. The system case reads the same UISettings background the app reads to
/// choose its palette. Captures and sampled values are written under artifacts/e2e/visual/theme/.
/// The Light and Dark cases force the palette with BWC_E2E_THEME, which the app reads only in
/// E2E builds.
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
    private const int ColorHighlight = 13;
    private const int ColorHighlightText = 14;
    private const int ColorBtnFace = 15;

    private const uint PwRenderFullContent = 2;
    private const int StrokeTolerance = 24;
    private const int DottedMinPercent = 30;
    private const int DottedMaxPercent = 70;
    private const int StrokeBrightnessStep = 90;

    private static readonly Color DarkWindowColor = Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E);
    private static readonly Color DarkInactiveColor = Color.FromArgb(0xFF, 0x3F, 0x3F, 0x46);

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
            ["expectedHighlight"] = Hex(expected.Highlight),
            ["expectedHighlightText"] = Hex(expected.HighlightText),
            ["expectedInactive"] = Hex(expected.Inactive),
        };
        Directory.CreateDirectory(VisualDir(config));

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
        Bitmap treeCapture = CaptureWindow(session.MainHwnd, out Rectangle windowRect);
        AutomationElement? selectedTree = SelectedTreeRow(session);
        AutomationElement? treeRow = selectedTree;
        if (treeRow is null)
        {
            Record(caseId, "tree.selectedRow", false, "No tree row reports IsSelected.");
            treeCapture.Dispose();
            AssertNoFailures(start, caseId);
            return;
        }

        Rectangle treeRowRect = ToBitmap(treeRow.BoundingRectangle, windowRect);
        Rectangle treePaneRect = ToBitmap(ElementRect(session, ControlTestSupport.TreeControl), windowRect);
        Rectangle listPaneRect = ToBitmap(ElementRect(session, ControlTestSupport.ListControl), windowRect);
        bool treeHasFocus = HasKeyboardFocus(session.Find(ControlTestSupport.TreeControl)) || HasKeyboardFocus(treeRow);
        Record(caseId, "focus.tree.start", treeHasFocus, "Keyboard focus is in the tree before Tab.");

        Color focusedFill = PixelAt(treeCapture, treeRowRect.Right - 8, treeRowRect.Top + treeRowRect.Height / 2);
        CheckColor(caseId, "tree.focusedSelection", focusedFill, expected.Highlight, "COLOR_HIGHLIGHT on the focused tree row.", samples, "treeFocusedSelection");

        bool dottedTree = HasDottedFocusRect(treeCapture, treeRowRect, expected.Highlight);
        Record(caseId, "tree.focusRect.onFocusedRow", dottedTree, "A one pixel dotted rectangle is on the focused tree row.");
        samples["treeFocusRectDotted"] = dottedTree ? "yes" : "no";

        int aboveTree = treeRowRect.Top - treeRowRect.Height;
        var rowAbove = new Rectangle(treeRowRect.Left, aboveTree, treeRowRect.Width, treeRowRect.Height);
        bool aboveHasFill = HasColorInTopBand(treeCapture, rowAbove, expected.Highlight);
        Record(caseId, "tree.focusRect.notOnOtherRows", !aboveHasFill, "The row above the focused row has no selection or focus pixels.");

        CheckColor(caseId, "tree.background", PixelAt(treeCapture, treePaneRect.Left + 380, treePaneRect.Bottom - 20), expected.Window, "Tree background is COLOR_WINDOW.", samples, "treeBackground");
        CheckColor(caseId, "list.background", PixelAt(treeCapture, listPaneRect.Left + 1200, listPaneRect.Bottom - 60), expected.Window, "List background is COLOR_WINDOW.", samples, "listBackground");
        CheckColor(caseId, "tree.edge", PixelAt(treeCapture, treePaneRect.Left + 1, treePaneRect.Top + 200), expected.Window, "The tree pane edge has no system focus rectangle.", samples, "treeEdge");

        string treeCapturePath = SaveCapture(config, $"main-{mode.ToLowerInvariant()}-{caseId}-tree-focus", treeCapture);
        treeCapture.Dispose();
        E2EResults.Note($"theme.{caseId}.capture.tree", treeCapturePath);

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

        Bitmap listCapture = CaptureWindow(session.MainHwnd, out Rectangle listWindowRect);
        Rectangle listRowRect = ToBitmap(selectedList.BoundingRectangle, listWindowRect);
        Rectangle listPaneRectAgain = ToBitmap(ElementRect(session, ControlTestSupport.ListControl), listWindowRect);
        Rectangle treeRowAgain = ToBitmap(treeRow.BoundingRectangle, listWindowRect);
        int listFillRight = Math.Min(listRowRect.Right, listPaneRectAgain.Right - 60) - 8;

        CheckColor(caseId, "list.focusedSelection", PixelAt(listCapture, listFillRight, listRowRect.Top + listRowRect.Height / 2), expected.Highlight, "COLOR_HIGHLIGHT on the focused list row.", samples, "listFocusedSelection");
        bool dottedList = HasDottedFocusRect(listCapture, listRowRect, expected.Highlight);
        Record(caseId, "list.focusRect.onFocusedRow", dottedList, "A one pixel dotted rectangle is on the focused list row.");
        samples["listFocusRectDotted"] = dottedList ? "yes" : "no";

        CheckColor(caseId, "tree.unfocusedSelection", PixelAt(listCapture, treeRowAgain.Right - 8, treeRowAgain.Top + treeRowAgain.Height / 2), expected.Inactive, "The unfocused tree selection is COLOR_BTNFACE (dark: muted).", samples, "treeUnfocusedSelection");
        bool dottedTreeUnfocused = HasColorInTopBand(listCapture, treeRowAgain, expected.HighlightText);
        Record(caseId, "tree.focusRect.absentWhenUnfocused", !dottedTreeUnfocused, "The unfocused tree row has no focus rectangle.");
        samples["treeUnfocusedHasStroke"] = dottedTreeUnfocused ? "yes" : "no";

        string listCapturePath = SaveCapture(config, $"main-{mode.ToLowerInvariant()}-{caseId}-list-focus", listCapture);
        listCapture.Dispose();
        E2EResults.Note($"theme.{caseId}.capture.list", listCapturePath);

        WriteSamples(config, caseId, samples);
        AssertNoFailures(start, caseId);
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

    private static bool FocusList(AppSession session)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (HasKeyboardFocus(session.Find(ControlTestSupport.ListControl)))
            {
                return true;
            }

            session.SendKeys("Tab");
            Thread.Sleep(150);
        }

        return HasKeyboardFocus(session.Find(ControlTestSupport.ListControl));
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
    /// pixel below the row top, where the one pixel stroke sits. A stroke pixel is any pixel that is
    /// clearly brighter than the fill, so a half-tone blend counts and a solid anti-aliased line of
    /// half-tone pixels does not pass the 30 to 70 percent window. The rest must be the fill.
    /// </summary>
    private static bool HasDottedFocusRect(Bitmap capture, Rectangle row, Color fill)
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
            int fillCount = 0;
            for (int x = left; x < right; x++)
            {
                Color pixel = PixelAt(capture, x, y);
                if (Brightness(pixel) - Brightness(fill) >= StrokeBrightnessStep)
                {
                    strokeCount++;
                }
                else if (Near(pixel, fill))
                {
                    fillCount++;
                }
            }

            int strokePercent = strokeCount * 100 / width;
            if (strokePercent >= DottedMinPercent && strokePercent <= DottedMaxPercent && fillCount > 0)
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

    private static Bitmap CaptureWindow(IntPtr hwnd, out Rectangle windowRect)
    {
        if (!GetWindowRect(hwnd, out NativeRect rect))
        {
            throw new InvalidOperationException("GetWindowRect failed for the Registry Editor window.");
        }

        windowRect = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        var bitmap = new Bitmap(windowRect.Width, windowRect.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            IntPtr hdc = graphics.GetHdc();
            try
            {
                _ = PrintWindow(hwnd, hdc, PwRenderFullContent);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }

        return bitmap;
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

    private static int Brightness(Color color) => color.R + color.G + color.B;

    private static bool Near(Color a, Color b)
    {
        if (a.IsEmpty || b.IsEmpty)
        {
            return false;
        }

        int distance = Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
        return distance <= StrokeTolerance;
    }

    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

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
            Color highlight = accent;
            bool lightAccent = Luminance(accent.R, accent.G, accent.B) > 0.6;
            return new ExpectedPalette(
                DarkWindowColor,
                highlight,
                lightAccent ? Color.Black : Color.White,
                DarkInactiveColor);
        }

        return new ExpectedPalette(
            SystemColor(ColorWindow),
            SystemColor(ColorHighlight),
            SystemColor(ColorHighlightText),
            SystemColor(ColorBtnFace));
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

    private sealed record ExpectedPalette(
        Color Window,
        Color Highlight,
        Color HighlightText,
        Color Inactive);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

#pragma warning disable SYSLIB1054 // The small user32 surface does not justify LibraryImport plumbing.
    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
#pragma warning restore SYSLIB1054
}

using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Menus;

/// <summary>
/// Checks of the menu bar and its popups, run against the BWC Registry Editor and the classic
/// C:\Windows\regedit.exe. The bar rectangles come from docs/parity/oracle/regedit-metrics.json. The
/// popup item heights, separator lines, and check mark come from
/// docs/parity/oracle/regedit-menu-metrics.json, which was measured from the classic popups.
/// </summary>
/// <remarks>
/// Classic popups are opened with the keyboard, because that is how they are measured. BWC popups
/// are opened through the bar item's UI Automation expand pattern, the same path the shell tests use;
/// one test opens the BWC File menu with Alt+F to check the keyboard path. Pixel checks read the
/// screen for the popup rectangle, so the popup must be visible. Every popup is closed with Escape
/// and, for BWC, collapsed through UI Automation. The classic process gets __COMPAT_LAYER=RunAsInvoker
/// from AppSession, so no UAC prompt can appear. The captures and a JSON summary are written to
/// artifacts/e2e/visual/menus/.
/// </remarks>
[TestClass]
public sealed class MenuTests
{
    private const string ClassicRegeditPath = @"C:\Windows\regedit.exe";
    private const string MenuBarRootId = "MenuBarRoot";
    private const string AddressBarId = "AddressBar";
    private const string MenuBarItemClass = "Microsoft.UI.Xaml.Controls.MenuBarItem";
    private const string FlyoutItemClass = "MenuFlyoutItem";
    private const string FlyoutSubItemClass = "MenuFlyoutSubItem";
    private const string ToggleFlyoutItemClass = "ToggleMenuFlyoutItem";
    private const string ClassicPopupClass = "#32768";
    private const string MenuMetricsRelativePath = @"docs\parity\oracle\regedit-menu-metrics.json";
    /// <summary>The classic View menu has 5 items (oracle). BWC View adds View > Theme, so it has 6.</summary>
    private const int BwcViewItems = 6;

    private const string SummaryFileName = "measurements.json";

    // Pixel thresholds. Contrast is the sum of the three channel differences. A separator row has most of
    // its sampled pixels different from the band background; the fraction is that share.
    private const int LineContrast = 6;
    private const double LineRowFraction = 0.6;
    private const int InkContrast = 60;
    private const int CheckInkMinimum = 6;
    private const int UnderlineMinimumRun = 6;
    private const int EdgeDarkThreshold = 30;

    private static readonly TimeSpan MenuTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PopupSettle = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan PopupFadeSample = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan PopupFadeTimeout = TimeSpan.FromSeconds(2);
    private static readonly List<KeyValuePair<string, string>> Summary = [];
    private static readonly object SummaryLock = new();
    private static UIA3Automation? _automation;

    /// <summary>
    /// One UI Automation instance for the class. A popup is a separate top-level window, so its items are
    /// read from the desktop, not from the main window.
    /// </summary>
    private static UIA3Automation Automation => _automation ??= new UIA3Automation();

    /// <summary>
    /// Sends keys to whichever window is foreground. A BWC popup takes the foreground while it is open, so
    /// this does not require the main window to be foreground, unlike AppSession.SendKeys.
    /// </summary>
    private static void SendRaw(string key)
    {
        if (key.Length == 1)
        {
            Keyboard.Type(key);
            return;
        }

        string[] parts = key.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1)
        {
            Keyboard.Press(Enum.Parse<VirtualKeyShort>(parts[0], ignoreCase: true));
            return;
        }

        Keyboard.TypeSimultaneously(parts.Select(p => Enum.Parse<VirtualKeyShort>(p, ignoreCase: true)).ToArray());
    }

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        WriteSummary();
        _automation?.Dispose();
        _automation = null;
    }

    [TestMethod]
    [TestCategory("Menus")]
    public void Menus_BarRectsMatchOracleAtWindowScale()
    {
        var config = GuiGate.Config;
        var checks = new Checks("menus.bar");
        var oracle = OracleMetrics.Load(config);
        using var session = OpenBwc(config, "menus-bar");
        uint dpi = ControlTestSupport.WindowDpi(session);
        double dipPx = dpi / 96.0;

        var bar = WaitForElement(session, MenuBarRootId);
        double barHeight = HeightOf(bar);
        double expectedBar = oracle.DevicePixels(oracle.MenuBarHeight, dpi);
        checks.Record(
            "height",
            Within(barHeight, expectedBar, dipPx),
            $"menu bar expected {expectedBar:0.#} px (oracle menuBar.height {oracle.MenuBarHeight} at {oracle.CaptureDpi} DPI), got {barHeight:0.#} px; tolerance one DIP ({dipPx:0.#} px).");

        var items = TopLevelItems(session);
        var oracleItems = OracleBarItems(config);
        checks.Record(
            "count",
            items.Length == oracleItems.Count,
            $"expected {oracleItems.Count} top-level items, got {items.Length}.");
        if (items.Length != oracleItems.Count)
        {
            checks.AssertAllPassed();
            return;
        }

        // Offsets are measured from the first item, which sits at the left edge of the bar in both captures.
        int firstLeft = items[0].BoundingRectangle.Left;
        int oracleFirstX = oracleItems[0].X;
        for (var i = 0; i < items.Length; i++)
        {
            var oracleItem = oracleItems[i];
            var rect = items[i].BoundingRectangle;
            double expectedWidth = oracle.DevicePixels(oracleItem.Width, dpi);
            double expectedHeight = oracle.DevicePixels(oracleItem.Height, dpi);
            double expectedOffset = oracle.DevicePixels(oracleItem.X - oracleFirstX, dpi);
            double actualOffset = rect.Left - firstLeft;
            checks.Record(
                $"item{i}.{oracleItem.Text.Replace("&", string.Empty)}",
                Within(rect.Width, expectedWidth, dipPx) &&
                    Within(rect.Height, expectedHeight, dipPx) &&
                    Within(actualOffset, expectedOffset, dipPx),
                $"'{oracleItem.Text}': expected x offset {expectedOffset:0.#}, width {expectedWidth:0.#}, height {expectedHeight:0.#} px; got offset {actualOffset:0.#}, width {rect.Width:0.#}, height {rect.Height:0.#} px.");
        }

        Remember("bar.bwc", $"height {barHeight:0.#} px at {dpi} DPI; widths {string.Join(",", items.Select(i => i.BoundingRectangle.Width.ToString("0.#")))} px.");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Menus")]
    public void Menus_AltFOpensBwcFileMenuFromKeyboard()
    {
        var config = GuiGate.Config;
        var checks = new Checks("menus.keyboard");
        using var session = OpenBwc(config, "menus-keyboard");

        // A click puts the keyboard in the window first, as the shell tests do; Alt alone is not delivered otherwise.
        session.Window.Click();
        SendRaw("ALT+KEY_F");
        var file = TopLevelItems(session).FirstOrDefault(e => string.Equals(SafeName(e), "File", StringComparison.Ordinal));
        bool expanded = WaitUntil(() => StateOf(file) == ExpandCollapseState.Expanded, MenuTimeout);
        checks.Record("fileExpanded", expanded, $"Alt+F File ExpandCollapseState={StateOf(file)?.ToString() ?? "(missing)"}.");
        int count = FlyoutItems(session).Length;
        checks.Record("fileItems", count >= 8, $"Alt+F showed {count} flyout items; the File menu has 8.");
        Remember("keyboard.alt-f", $"expanded={expanded}, flyout items={count}.");
        CloseBwcMenus(session);
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Menus")]
    public void Menus_PopupItemHeightsAndSeparatorLinesMatchMeasurement()
    {
        var config = GuiGate.Config;
        var checks = new Checks("menus.popup");
        var metrics = MenuMetrics.Load(config);

        using (var bwc = OpenBwc(config, "menus-popup-bwc"))
        {
            var popup = OpenBwcPopup(bwc, "File", expectedItems: 8);
            CheckPopup(checks, "bwc.file", popup, metrics, bwc, waitForFadeIn: false);
            CloseBwcMenus(bwc);
        }

        using (var classic = OpenClassic(config, "menus-popup-classic"))
        {
            var popup = OpenClassicPopup(classic, "ALT+KEY_F", expectedItems: 8);
            CheckPopup(checks, "classic.file", popup, metrics, classic, waitForFadeIn: true);
            CloseClassicMenus(classic);
        }

        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Menus")]
    public void Menus_ViewAddressBarCheckMarkMatchesVisibility()
    {
        var config = GuiGate.Config;
        var checks = new Checks("menus.check");
        var metrics = MenuMetrics.Load(config);

        using (var bwc = OpenBwc(config, "menus-check-bwc"))
        {
            bool visible = AddressBarVisible(bwc);
            var popup = OpenBwcPopup(bwc, "View", expectedItems: BwcViewItems);
            bool ink = CheckMarkInk(popup, metrics, ControlTestSupport.WindowDpi(bwc) / 96.0);
            checks.Record(
                "bwc.addressBar",
                ink == visible,
                $"View > Address Bar check ink={ink} while the address bar visible={visible}.");
            Remember("check.bwc", $"address bar visible={visible}, check ink={ink}.");
            CloseBwcMenus(bwc);
        }

        using (var classic = OpenClassic(config, "menus-check-classic"))
        {
            var popup = OpenClassicPopup(classic, "ALT+KEY_V", expectedItems: 5);
            bool ink = CheckMarkInk(popup, metrics, ControlTestSupport.WindowDpi(classic) / 96.0);
            checks.Note("classic.addressBar", $"classic View > Address Bar check ink={ink}; the classic address bar setting is not read.");
            Remember("check.classic", $"check ink={ink}.");
            CloseClassicMenus(classic);
        }

        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Menus")]
    public void Menus_LetterInOpenPopupRunsCommandAndOpensSubmenu()
    {
        var config = GuiGate.Config;
        var checks = new Checks("menus.letter");
        using var session = OpenBwc(config, "menus-letter");

        // Edit is open; N is the access key of New, which is a submenu. The submenu opens.
        OpenBwcPopup(session, "Edit", expectedItems: 7);
        SendRaw("n");
        var newItem = FindMenuItem(session, "New");
        bool expanded = WaitUntil(() => StateOf(newItem) == ExpandCollapseState.Expanded, MenuTimeout);
        checks.Record("submenu", expanded, $"With Edit open, N expanded 'New': state={StateOf(newItem)?.ToString() ?? "(missing)"}.");
        CloseBwcMenus(session);

        // View is open; B is the access key of "Address &Bar", a command. The letter runs it and closes the menu.
        bool before = AddressBarVisible(session);
        OpenBwcPopup(session, "View", expectedItems: BwcViewItems);
        SendRaw("b");
        bool toggled = WaitUntil(() => AddressBarVisible(session) != before, MenuTimeout);
        bool closed = WaitUntil(() => FlyoutItems(session).Length == 0, MenuTimeout);
        checks.Record("command", toggled, $"With View open, B changed the address bar visibility from {before}.");
        checks.Record("commandClosesMenu", closed, $"After View > B the popup items remaining={FlyoutItems(session).Length}.");
        CloseBwcMenus(session);

        // A second run of the same command restores the address bar.
        OpenBwcPopup(session, "View", expectedItems: BwcViewItems);
        SendRaw("b");
        bool restored = WaitUntil(() => AddressBarVisible(session) == before, MenuTimeout);
        checks.Record("restored", restored, $"A second View > B restored the address bar to visible={before}.");
        CloseBwcMenus(session);

        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Menus")]
    public void Menus_AltShowsMnemonicUnderlineOnBar()
    {
        var config = GuiGate.Config;
        var checks = new Checks("menus.underline");
        using var session = OpenBwc(config, "menus-underline");
        var bar = WaitForElement(session, MenuBarRootId);
        var file = TopLevelItems(session).FirstOrDefault(e => string.Equals(SafeName(e), "File", StringComparison.Ordinal));
        if (bar is null || file is null)
        {
            checks.Record("bar", false, "The menu bar or the File item was not found.");
            checks.AssertAllPassed();
            return;
        }

        Rectangle label = file.BoundingRectangle;
        using Bitmap before = CaptureScreen(label);
        SendRaw("ALT");
        Thread.Sleep(400);
        using Bitmap after = CaptureScreen(label);
        int run = LongestNewDarkRun(before, after);
        checks.Record(
            "bar",
            run >= UnderlineMinimumRun,
            $"After Alt the File label gained a horizontal run of {run} new dark pixels (need {UnderlineMinimumRun}); the underline is under the F.");
        Remember("underline.bar", $"longest new dark horizontal run after Alt={run} px.");

        SendRaw("ESCAPE");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Menus")]
    public void Menus_WindowEdgeHasNoBlackStrip()
    {
        var config = GuiGate.Config;
        var checks = new Checks("menus.edge");
        using var session = OpenBwc(config, "menus-edge");
        Thread.Sleep(500);

        if (!GetClientRect(session.MainHwnd, out var client))
        {
            checks.Record("client", false, "GetClientRect failed.");
            checks.AssertAllPassed();
            return;
        }

        var origin = new POINT { X = 0, Y = 0 };
        _ = ClientToScreen(session.MainHwnd, ref origin);
        int width = client.Right - client.Left;
        int height = client.Bottom - client.Top;
        var clientScreen = new Rectangle(origin.X, origin.Y, width, height);
        using Bitmap capture = CaptureScreen(clientScreen);

        // Three pixels in from the right and bottom client edges, clear of the anti-aliased corner.
        Color right = capture.GetPixel(width - 3, height / 2);
        Color bottom = capture.GetPixel(width / 2, height - 3);
        bool rightOk = right.R + right.G + right.B > EdgeDarkThreshold;
        bool bottomOk = bottom.R + bottom.G + bottom.B > EdgeDarkThreshold;
        checks.Record(
            "right",
            rightOk,
            $"Right client edge pixel is #{right.R:X2}{right.G:X2}{right.B:X2}; a black strip is #000000.");
        checks.Record(
            "bottom",
            bottomOk,
            $"Bottom client edge pixel is #{bottom.R:X2}{bottom.G:X2}{bottom.B:X2}; a black strip is #000000.");
        Remember("edge", $"client {width}x{height} px; right #{right.R:X2}{right.G:X2}{right.B:X2}, bottom #{bottom.R:X2}{bottom.G:X2}{bottom.B:X2}.");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Menus")]
    public void Menus_FileAndEditPopupWidthMatchesClassic()
    {
        var config = GuiGate.Config;
        var checks = new Checks("menus.width");
        double dipPx;
        Rectangle[] bwcFile;
        Rectangle[] bwcEdit;
        using (var bwc = OpenBwc(config, "menus-width-bwc"))
        {
            dipPx = ControlTestSupport.WindowDpi(bwc) / 96.0;
            bwcFile = OpenBwcPopup(bwc, "File", expectedItems: 8).Items.ToArray();
            CloseBwcMenus(bwc);
            bwcEdit = OpenBwcPopup(bwc, "Edit", expectedItems: 7).Items.ToArray();
            CloseBwcMenus(bwc);
        }

        Rectangle[] classicFile;
        Rectangle[] classicEdit;
        using (var classic = OpenClassic(config, "menus-width-classic"))
        {
            classicFile = OpenClassicPopup(classic, "ALT+KEY_F", expectedItems: 8).Items.ToArray();
            CloseClassicMenus(classic);
            classicEdit = OpenClassicPopup(classic, "ALT+KEY_E", expectedItems: 7).Items.ToArray();
            CloseClassicMenus(classic);
        }

        // Item rectangles are compared, not the popup windows, so the border and padding do not count. Two DIP is the tolerance.
        int bwcFileWidth = Union(bwcFile).Width;
        int classicFileWidth = Union(classicFile).Width;
        int bwcEditWidth = Union(bwcEdit).Width;
        int classicEditWidth = Union(classicEdit).Width;
        checks.Record(
            "file",
            Within(bwcFileWidth, classicFileWidth, 2 * dipPx),
            $"File item width BWC {bwcFileWidth} px, classic {classicFileWidth} px; tolerance 2 DIP ({2 * dipPx:0.#} px).");
        checks.Record(
            "edit",
            Within(bwcEditWidth, classicEditWidth, 2 * dipPx),
            $"Edit item width BWC {bwcEditWidth} px, classic {classicEditWidth} px; tolerance 2 DIP ({2 * dipPx:0.#} px).");
        Remember("width", $"file bwc={bwcFileWidth} classic={classicFileWidth}; edit bwc={bwcEditWidth} classic={classicEditWidth} px.");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Menus")]
    public void Menus_KeyboardFocusedItemHasFrameAndNoFill()
    {
        var config = GuiGate.Config;
        var checks = new Checks("menus.focus");
        using var session = OpenBwc(config, "menus-focus");

        session.Window.Click();
        SendRaw("ALT+KEY_F");
        var items = WaitForFlyoutItems(session, 8);
        checks.Record("open", items.Length >= 8, $"Alt+F showed {items.Length} File items; expected 8.");
        SendRaw("DOWN");
        Thread.Sleep(PopupSettle);

        var rects = ProcessMenuItems(session)
            .Where(e => !SafeOffscreen(e) && string.Equals(SafeClassName(e), FlyoutItemClass, StringComparison.Ordinal))
            .Select(e => new { Rect = e.BoundingRectangle, Focused = HasKeyboardFocus(e) })
            .OrderBy(e => e.Rect.Top)
            .ToArray();
        var focused = rects.FirstOrDefault(e => e.Focused);
        var other = rects.FirstOrDefault(e => !e.Focused);
        checks.Record("focused", focused is not null, $"{rects.Length} leaf items; keyboard focus on one of them={focused is not null}.");
        if (focused is null || other is null)
        {
            CloseBwcMenus(session);
            checks.AssertAllPassed();
            return;
        }

        // A pointer over an item paints the hover fill, which would hide the keyboard state.
        // The pointer goes to the desktop first.
        Mouse.MoveTo(new System.Drawing.Point(0, 0));
        Thread.Sleep(300);
        Rectangle r = focused.Rect;
        using Bitmap shot = CaptureScreen(Rectangle.Inflate(r, 4, 4));
        // The shot starts 4 px left of and above the item. The fill is sampled in the gutter, clear of the frame and the text.
        const int margin = 4;
        int fillY = (r.Height / 2) - 8;
        Color fill = shot.GetPixel(margin + 20, margin + fillY);
        Color background = CaptureScreen(new Rectangle(other.Rect.Left + 20, other.Rect.Top + (other.Rect.Height / 2) - 8, 1, 1)).GetPixel(0, 0);
        int fillContrast = Contrast(fill, background);

        // The focus frame sits 3 DIP (6 px) in from the item edge. A dark pixel in that column is the frame.
        int frameX = margin + 6;
        int darkFrame = 0;
        for (int y = margin + 2; y < shot.Height - margin - 2; y++)
        {
            if (Contrast(shot.GetPixel(frameX, y), fill) >= InkContrast)
            {
                darkFrame++;
            }
        }

        checks.Record(
            "fillUnchanged",
            fillContrast < 4,
            $"Focused item fill #{fill.R:X2}{fill.G:X2}{fill.B:X2} against the unfocused item #{background.R:X2}{background.G:X2}{background.B:X2}: contrast {fillContrast}; classic keeps the fill (need under 4).");
        checks.Record(
            "frame",
            darkFrame > 0,
            $"Frame pixels in the focus column: {darkFrame}; classic draws a 1 px frame on the keyboard-focused item (need at least 1).");
        Remember("focus", $"fill=#{fill.R:X2}{fill.G:X2}{fill.B:X2} contrast={fillContrast} darkFrame={darkFrame}.");
        CloseBwcMenus(session);
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Menus")]
    public void Menus_PopupCornersAreRounded()
    {
        var config = GuiGate.Config;
        var checks = new Checks("menus.corner");
        using var session = OpenBwc(config, "menus-corner");
        var popup = OpenBwcPopup(session, "File", expectedItems: 8);
        double dipPx = ControlTestSupport.WindowDpi(session) / 96.0;

        // The presenter edge is 1.5 DIP outside the first item (1 DIP border plus 0.5 DIP padding). Its top
        // border is about 1 DIP thick. A square corner shows the border color at the corner pixel; a rounded
        // one shows the window behind.
        Rectangle items = Union(popup.Items);
        int edge = (int)Math.Round(1.5 * dipPx);
        int x0 = items.Left - edge;
        int y0 = items.Top - edge;
        using Bitmap shot = CaptureScreen(Rectangle.Inflate(new Rectangle(x0, y0, items.Width + (2 * edge), items.Height + (2 * edge)), 4, 4));
        int ox = 4;
        int oy = 4;
        Color corner = shot.GetPixel(ox, oy);
        Color topBorder = shot.GetPixel(ox + ((items.Width + (2 * edge)) / 2), oy + 1);
        int contrast = Contrast(corner, topBorder);
        checks.Record(
            "rounded",
            contrast >= 12,
            $"Corner pixel #{corner.R:X2}{corner.G:X2}{corner.B:X2} against the top border #{topBorder.R:X2}{topBorder.G:X2}{topBorder.B:X2}: contrast {contrast}, need 12 for a rounded corner.");
        Remember("corner", $"corner contrast={contrast} vs top border.");
        CloseBwcMenus(session);
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Menus")]
    public void Menus_SideBySideCapturesAreWritten()
    {
        var config = GuiGate.Config;
        var checks = new Checks("menus.captures");
        string dir = VisualDir(config);
        Directory.CreateDirectory(dir);

        using (var bwc = OpenBwc(config, "menus-capture-bwc"))
        {
            CaptureBwcPopup(bwc, "File", 8, Path.Combine(dir, "bwc-file-menu.png"), checks, "bwc.file");
            CloseBwcMenus(bwc);
            CaptureBwcPopup(bwc, "View", BwcViewItems, Path.Combine(dir, "bwc-view-menu.png"), checks, "bwc.view");
            CloseBwcMenus(bwc);
            CaptureBwcPopup(bwc, "Edit", 7, Path.Combine(dir, "bwc-edit-menu.png"), checks, "bwc.edit");
            CloseBwcMenus(bwc);
        }

        using (var classic = OpenClassic(config, "menus-capture-classic"))
        {
            CaptureClassicPopup(classic, "ALT+KEY_F", 8, Path.Combine(dir, "classic-file-menu.png"), checks, "classic.file");
            CloseClassicMenus(classic);
            CaptureClassicPopup(classic, "ALT+KEY_V", 5, Path.Combine(dir, "classic-view-menu.png"), checks, "classic.view");
            CloseClassicMenus(classic);
            CaptureClassicPopup(classic, "ALT+KEY_E", 7, Path.Combine(dir, "classic-edit-menu.png"), checks, "classic.edit");
            CloseClassicMenus(classic);
        }

        checks.AssertAllPassed();
    }

    // ---- popups -----------------------------------------------------------------------------------

    /// <summary>One visible popup: its item rectangles in screen pixels and the separator bands between them.</summary>
    private sealed record Popup(
        Rectangle Bounds,
        IReadOnlyList<Rectangle> Items,
        IReadOnlyList<string> Names,
        IReadOnlyList<Rectangle> SeparatorBands);

    private static void CheckPopup(
        Checks checks,
        string id,
        Popup popup,
        MenuMetrics metrics,
        AppSession session,
        bool waitForFadeIn)
    {
        uint dpi = ControlTestSupport.WindowDpi(session);
        double dipPx = dpi / 96.0;
        double expectedItem = metrics.ItemHeightDip * dipPx;

        var heights = popup.Items.Select(r => r.Height).ToArray();
        var tops = popup.Items.Select(r => r.Top - popup.Bounds.Top).ToArray();
        checks.Record(
            $"{id}.itemCount",
            popup.Items.Count == metrics.ItemCount,
            $"expected {metrics.ItemCount} items, found {popup.Items.Count}.");
        checks.Record(
            $"{id}.itemHeight",
            heights.Length > 0 && heights.All(h => Within(h, expectedItem, dipPx)),
            $"expected every item {expectedItem:0.#} px ({metrics.ItemHeightDip} DIP at {dpi} DPI), got [{string.Join(",", heights.Select(h => h.ToString("0.#")))}] px for {string.Join(", ", popup.Names)}; item tops [{string.Join(",", tops)}] px.");

        checks.Record(
            $"{id}.separatorCount",
            popup.SeparatorBands.Count == metrics.SeparatorCount,
            $"expected {metrics.SeparatorCount} separators in the popup, found {popup.SeparatorBands.Count} gaps between items.");

        // The classic popup fades in, so its separator rows are scanned only after the pixels settle.
        using Bitmap shot = waitForFadeIn ? SettledPopupCapture(popup.Bounds) : CaptureScreen(popup.Bounds);
        int visible = 0;
        var offsets = new List<string>();
        foreach (Rectangle band in popup.SeparatorBands)
        {
            int line = SeparatorLineRow(shot, band, popup.Bounds);
            if (line >= 0)
            {
                visible++;
                offsets.Add(line.ToString());
            }
        }

        checks.Record(
            $"{id}.separatorVisible",
            popup.SeparatorBands.Count > 0 && visible == popup.SeparatorBands.Count,
            $"{visible} of {popup.SeparatorBands.Count} separator bands have a 1 px line; expected a {metrics.SeparatorHeightDip} DIP band with its line at about 3 DIP; line rows at [{string.Join(",", offsets)}] px.");

        Remember(
            id,
            $"items={popup.Items.Count} heights=[{string.Join(",", heights.Select(h => h.ToString("0.#")))}] px; separator bands={popup.SeparatorBands.Count} visible={visible}; item tops=[{string.Join(",", tops)}] px.");
    }

    /// <summary>
    /// Returns the row offset (within the band) of the 1 px separator line, or -1 when the band has no
    /// line. A line row has most of its sampled pixels different from the band background.
    /// </summary>
    private static int SeparatorLineRow(Bitmap shot, Rectangle band, Rectangle bounds)
    {
        int left = band.Left + 4;
        int right = band.Right - 4;
        if (right <= left || band.Height < 1)
        {
            return -1;
        }

        var colors = new Dictionary<int, int>();
        for (int y = band.Top; y < band.Bottom; y++)
        {
            for (int x = left; x < right; x += 2)
            {
                int key = shot.GetPixel(x - bounds.Left, y - bounds.Top).ToArgb();
                colors[key] = colors.TryGetValue(key, out int n) ? n + 1 : 1;
            }
        }

        Color background = Color.FromArgb(colors.OrderByDescending(p => p.Value).First().Key);
        int samples = Math.Max(1, (right - left + 1) / 2);
        for (int y = band.Top; y < band.Bottom; y++)
        {
            int differing = 0;
            for (int x = left; x < right; x += 2)
            {
                if (Contrast(shot.GetPixel(x - bounds.Left, y - bounds.Top), background) >= LineContrast)
                {
                    differing++;
                }
            }

            if (differing >= samples * LineRowFraction)
            {
                return y - band.Top;
            }
        }

        return -1;
    }

    /// <summary>True when the check gutter of the first item of a popup holds ink (the check glyph).</summary>
    private static bool CheckMarkInk(Popup popup, MenuMetrics metrics, double dipPx)
    {
        if (popup.Items.Count == 0)
        {
            return false;
        }

        Rectangle item = popup.Items[0];
        int checkLeft = (int)Math.Round(metrics.CheckLeftDip * dipPx);
        int gutterRight = (int)Math.Round(metrics.GutterWidthDip * dipPx);
        int inset = (int)Math.Round(4 * dipPx);
        var gutter = new Rectangle(
            item.Left + checkLeft,
            item.Top + inset,
            gutterRight - checkLeft,
            item.Height - (2 * inset));
        using Bitmap shot = CaptureScreen(gutter);
        Color background = shot.GetPixel(0, 0);
        int ink = 0;
        for (int y = 0; y < shot.Height; y++)
        {
            for (int x = 0; x < shot.Width; x++)
            {
                if (Contrast(shot.GetPixel(x, y), background) >= InkContrast)
                {
                    ink++;
                }
            }
        }

        return ink >= CheckInkMinimum;
    }

    private static void CaptureBwcPopup(AppSession session, string topName, int expectedItems, string path, Checks checks, string id)
    {
        Popup popup = OpenBwcPopup(session, topName, expectedItems);
        SavePopupCapture(popup, path);
        checks.Record(id + ".capture", File.Exists(path) && new FileInfo(path).Length > 0, $"Wrote {path}.");
    }

    private static void CaptureClassicPopup(AppSession session, string keys, int expectedItems, string path, Checks checks, string id)
    {
        Popup popup = OpenClassicPopup(session, keys, expectedItems);
        SavePopupCapture(popup, path);
        checks.Record(id + ".capture", File.Exists(path) && new FileInfo(path).Length > 0, $"Wrote {path}.");
    }

    private static void SavePopupCapture(Popup popup, string path)
    {
        Rectangle area = Rectangle.Inflate(popup.Bounds, 6, 6);
        using Bitmap shot = CaptureScreen(area);
        shot.Save(path, ImageFormat.Png);
    }

    /// <summary>
    /// Opens a BWC top-level menu through its UI Automation expand pattern and returns its items. The
    /// separator bands are the gaps between the item rectangles, since separators have no UIA peer.
    /// </summary>
    private static Popup OpenBwcPopup(AppSession session, string topName, int expectedItems)
    {
        AutomationElement? top = TopLevelItems(session).FirstOrDefault(e => string.Equals(SafeName(e), topName, StringComparison.Ordinal));
        if (top is null)
        {
            throw new InvalidOperationException($"The BWC menu bar item '{topName}' was not found.");
        }

        try
        {
            if (top.Patterns.ExpandCollapse.IsSupported)
            {
                top.Patterns.ExpandCollapse.Pattern.Expand();
            }
        }
        catch (Exception)
        {
            // The count check below reports a flyout that did not open.
        }

        var items = WaitForFlyoutItems(session, expectedItems);
        if (items.Length != expectedItems)
        {
            throw new InvalidOperationException(
                $"The BWC '{topName}' menu showed {items.Length} items, expected {expectedItems}. Its state is {StateOf(top)?.ToString() ?? "(unknown)"}.");
        }

        // WinUI draws a popup after its items exist in UI Automation. The pixels are read after a short settle.
        Thread.Sleep(PopupSettle);
        var sorted = items.OrderBy(e => e.BoundingRectangle.Top).ToArray();
        var rects = sorted.Select(e => e.BoundingRectangle).ToArray();
        return new Popup(Union(rects), rects, sorted.Select(SafeName).ToArray(), GapBands(rects));
    }

    /// <summary>
    /// The separator bands of a popup: the gap between each pair of consecutive item rectangles, widened by
    /// two pixels above and below so a line that sits on a gap edge is still inside the band.
    /// </summary>
    private static List<Rectangle> GapBands(IReadOnlyList<Rectangle> rects)
    {
        var bands = new List<Rectangle>();
        for (int i = 1; i < rects.Count; i++)
        {
            int gapTop = rects[i - 1].Bottom;
            int gapBottom = rects[i].Top;
            if (gapBottom - gapTop >= 2)
            {
                bands.Add(new Rectangle(rects[i].Left, gapTop - 2, rects[i].Width, gapBottom - gapTop + 4));
            }
        }

        return bands;
    }

    /// <summary>Opens a classic popup with the keys, then reads its window and its UIA items and separators.</summary>
    private static Popup OpenClassicPopup(AppSession session, string keys, int expectedItems)
    {
        SendRaw(keys);
        IntPtr hwnd = WaitForClassicPopup(session.ProcessId);
        if (hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("The classic popup window did not appear.");
        }

        _ = GetWindowRect(hwnd, out var r);
        var bounds = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        using var automation = new UIA3Automation();
        AutomationElement element = automation.FromHandle(hwnd);
        // Classic separators are not reliably exposed to UI Automation, so only the items are read. The
        // separators are found from the gaps between the item rectangles, the same way as for BWC.
        var all = element.FindAllDescendants().Where(e => e.ControlType == ControlType.MenuItem).ToArray();
        var rects = new List<Rectangle>();
        var names = new List<string>();
        foreach (AutomationElement entry in all.OrderBy(e => e.BoundingRectangle.Top))
        {
            rects.Add(entry.BoundingRectangle);
            names.Add(SafeName(entry));
        }

        if (rects.Count != expectedItems)
        {
            throw new InvalidOperationException($"The classic popup showed {rects.Count} items, expected {expectedItems}.");
        }

        return new Popup(bounds, rects, names, GapBands(rects));
    }

    private static AutomationElement[] WaitForFlyoutItems(AppSession session, int expected)
    {
        AutomationElement[] found = [];
        WaitUntil(() =>
        {
            found = FlyoutItems(session);
            return found.Length >= expected;
        }, MenuTimeout);
        return found;
    }

    private static AutomationElement[] FlyoutItems(AppSession session)
    {
        return ProcessMenuItems(session)
            .Where(e =>
            {
                string className = SafeClassName(e);
                return !SafeOffscreen(e) &&
                    (string.Equals(className, FlyoutItemClass, StringComparison.Ordinal) ||
                     string.Equals(className, ToggleFlyoutItemClass, StringComparison.Ordinal) ||
                     string.Equals(className, FlyoutSubItemClass, StringComparison.Ordinal));
            })
            .ToArray();
    }

    /// <summary>
    /// Every MenuItem in the top-level windows of the session's process: the main window, and the popup
    /// windows that WinUI creates for flyouts. The desktop children are filtered by process id.
    /// </summary>
    private static AutomationElement[] ProcessMenuItems(AppSession session)
    {
        try
        {
            var found = new List<AutomationElement>();
            foreach (AutomationElement top in Automation.GetDesktop().FindAllChildren())
            {
                int processId;
                try
                {
                    processId = top.Properties.ProcessId.Value;
                }
                catch (Exception)
                {
                    continue;
                }

                if (processId != session.ProcessId)
                {
                    continue;
                }

                found.AddRange(top.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem)));
            }

            return found.ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static IntPtr WaitForClassicPopup(int processId)
    {
        IntPtr found = IntPtr.Zero;
        WaitUntil(() =>
        {
            found = VisiblePopupOf(processId);
            return found != IntPtr.Zero;
        }, MenuTimeout);
        return found;
    }

    /// <summary>The topmost visible #32768 popup owned by the given process.</summary>
    private static IntPtr VisiblePopupOf(int processId)
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows((hwnd, lParam) =>
        {
            _ = lParam;
            if (result != IntPtr.Zero)
            {
                return false;
            }

            var sb = new StringBuilder(64);
            _ = GetClassName(hwnd, sb, sb.Capacity);
            if (!string.Equals(sb.ToString(), ClassicPopupClass, StringComparison.Ordinal) || !IsWindowVisible(hwnd))
            {
                return true;
            }

            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == unchecked((uint)processId))
            {
                result = hwnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>Escape leaves a submenu, then the popup, then the bar. Then the top-level items are collapsed.</summary>
    private static void CloseBwcMenus(AppSession session)
    {
        for (int i = 0; i < 3; i++)
        {
            SendRaw("ESCAPE");
            Thread.Sleep(150);
        }

        foreach (AutomationElement top in TopLevelItems(session))
        {
            try
            {
                if (top.Patterns.ExpandCollapse.IsSupported && top.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value != ExpandCollapseState.Collapsed)
                {
                    top.Patterns.ExpandCollapse.Pattern.Collapse();
                }
            }
            catch (Exception)
            {
                // The next open replaces an open popup anyway.
            }
        }

        _ = WaitUntil(() => FlyoutItems(session).Length == 0, MenuTimeout);
    }

    private static void CloseClassicMenus(AppSession session)
    {
        for (int i = 0; i < 3; i++)
        {
            SendRaw("ESCAPE");
            Thread.Sleep(150);
        }
    }

    // ---- sessions and elements ---------------------------------------------------------------------

    private static AppSession OpenBwc(E2EConfig config, string scope)
    {
        config.EnsureExecutablesExist();
        var environment = config.DataRootEnvironment(scope);
        var session = new AppSession(config, config.RegeditExePath, SessionTimeout, environment);
        _ = ControlTestSupport.EnsureForeground(session);
        return session;
    }

    private static AppSession OpenClassic(E2EConfig config, string scope)
    {
        _ = scope;
        if (!File.Exists(ClassicRegeditPath))
        {
            throw new FileNotFoundException("The classic Registry Editor is missing.", ClassicRegeditPath);
        }

        var session = new AppSession(config, ClassicRegeditPath, SessionTimeout);
        _ = ControlTestSupport.EnsureForeground(session);
        return session;
    }

    private static AutomationElement? WaitForElement(AppSession session, string automationId)
    {
        AutomationElement? found = null;
        WaitUntil(() =>
        {
            found = session.Find(automationId);
            return found is not null;
        }, MenuTimeout);
        return found;
    }

    private static AutomationElement[] TopLevelItems(AppSession session)
    {
        try
        {
            return session.Window
                .FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
                .Where(e => string.Equals(SafeClassName(e), MenuBarItemClass, StringComparison.Ordinal))
                .OrderBy(e => e.BoundingRectangle.Left)
                .ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static AutomationElement? FindMenuItem(AppSession session, string name)
    {
        return ProcessMenuItems(session)
            .FirstOrDefault(e => string.Equals(SafeName(e), name, StringComparison.Ordinal));
    }

    private static bool AddressBarVisible(AppSession session)
    {
        var element = session.Find(AddressBarId);
        if (element is null)
        {
            return false;
        }

        try
        {
            return !element.IsOffscreen && element.BoundingRectangle.Height > 1;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static ExpandCollapseState? StateOf(AutomationElement? element)
    {
        if (element is null)
        {
            return null;
        }

        try
        {
            return element.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string SafeName(AutomationElement? element)
    {
        try
        {
            return element?.Name ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string SafeClassName(AutomationElement? element)
    {
        try
        {
            return element?.ClassName ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static bool HasKeyboardFocus(AutomationElement? element)
    {
        try
        {
            return element?.Properties.HasKeyboardFocus.Value ?? false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool SafeOffscreen(AutomationElement? element)
    {
        try
        {
            return element?.IsOffscreen ?? true;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static double HeightOf(AutomationElement? element) => element is null ? 0 : element.BoundingRectangle.Height;

    private static bool Within(double actual, double expected, double tolerance) => Math.Abs(actual - expected) <= tolerance;

    private static Rectangle Union(IEnumerable<Rectangle> rects)
    {
        Rectangle[] array = rects.ToArray();
        if (array.Length == 0)
        {
            return Rectangle.Empty;
        }

        int left = array.Min(r => r.Left);
        int top = array.Min(r => r.Top);
        int right = array.Max(r => r.Right);
        int bottom = array.Max(r => r.Bottom);
        return new Rectangle(left, top, right - left, bottom - top);
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            try
            {
                if (condition())
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // The next poll retries.
            }

            Thread.Sleep(100);
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }

    // ---- pixels -------------------------------------------------------------------------------------

    private static Bitmap CaptureScreen(Rectangle area)
    {
        var bitmap = new Bitmap(Math.Max(1, area.Width), Math.Max(1, area.Height));
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(area.Left, area.Top, 0, 0, bitmap.Size);
        }

        return bitmap;
    }

    /// <summary>
    /// Captures a popup after its fade-in has finished. Two captures PopupFadeSample apart must match before the
    /// capture is used. The wait is bounded by PopupFadeTimeout, and the last capture is returned when the bound
    /// passes, so a popup that never settles is still scanned and fails on its own pixels.
    /// </summary>
    private static Bitmap SettledPopupCapture(Rectangle area)
    {
        DateTime deadline = DateTime.UtcNow + PopupFadeTimeout;
        Bitmap previous = CaptureScreen(area);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(PopupFadeSample);
            Bitmap next = CaptureScreen(area);
            bool same = SameBitmap(previous, next);
            if (same)
            {
                previous.Dispose();
                return next;
            }

            previous.Dispose();
            previous = next;
        }

        return previous;
    }

    private static bool SameBitmap(Bitmap a, Bitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            return false;
        }

        for (int y = 0; y < a.Height; y++)
        {
            for (int x = 0; x < a.Width; x++)
            {
                if (a.GetPixel(x, y).ToArgb() != b.GetPixel(x, y).ToArgb())
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static int Contrast(Color a, Color b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    /// <summary>
    /// The longest horizontal run of pixels that were not dark before and are dark after. This is the
    /// underline when the bar gains mnemonics; text that already existed does not count.
    /// </summary>
    private static int LongestNewDarkRun(Bitmap before, Bitmap after)
    {
        int longest = 0;
        int width = Math.Min(before.Width, after.Width);
        int height = Math.Min(before.Height, after.Height);
        for (int y = 0; y < height; y++)
        {
            int run = 0;
            for (int x = 0; x < width; x++)
            {
                // Contrast, not darkness: the E2E palette is dark, so the underline is light text.
                bool changed = Contrast(after.GetPixel(x, y), before.GetPixel(x, y)) >= InkContrast;
                run = changed ? run + 1 : 0;
                longest = Math.Max(longest, run);
            }
        }

        return longest;
    }

    private static string VisualDir(E2EConfig config) => Path.Combine(config.ArtifactsDir, "visual", "menus");

    // ---- measurements -----------------------------------------------------------------------------

    private static void Remember(string key, string text)
    {
        lock (SummaryLock)
        {
            Summary.Add(new KeyValuePair<string, string>(key, text));
        }
    }

    private static void WriteSummary()
    {
        try
        {
            var config = GuiGate.Config;
            string dir = VisualDir(config);
            Directory.CreateDirectory(dir);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            lock (SummaryLock)
            {
                foreach (var pair in Summary)
                {
                    values[pair.Key] = pair.Value;
                }
            }

            File.WriteAllText(
                Path.Combine(dir, SummaryFileName),
                JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            E2EResults.Note("menus.summary", "The measurement summary could not be written: " + ex.Message);
        }
    }

    // ---- oracle -----------------------------------------------------------------------------------

    private sealed record OracleBarItem(string Text, int X, int Width, int Height);

    private static List<OracleBarItem> OracleBarItems(E2EConfig config)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(config.RepoRoot, OracleMetrics.RelativePath)));
        var list = new List<OracleBarItem>();
        foreach (JsonElement item in document.RootElement.GetProperty("menuBar").GetProperty("items").EnumerateArray())
        {
            JsonElement rect = item.GetProperty("rect");
            list.Add(new OracleBarItem(
                item.GetProperty("text").GetString() ?? string.Empty,
                rect.GetProperty("x").GetInt32(),
                rect.GetProperty("width").GetInt32(),
                rect.GetProperty("height").GetInt32()));
        }

        return list;
    }

    /// <summary>The popup measurements from docs/parity/oracle/regedit-menu-metrics.json.</summary>
    private sealed class MenuMetrics
    {
        public double ItemHeightDip { get; init; }
        public double SeparatorHeightDip { get; init; }
        public int ItemCount { get; init; }
        public int SeparatorCount { get; init; }
        public double GutterWidthDip { get; init; }
        public double CheckLeftDip { get; init; }

        public static MenuMetrics Load(E2EConfig config)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(config.RepoRoot, MenuMetricsRelativePath)));
            JsonElement root = document.RootElement;
            JsonElement popup = root.GetProperty("popup");
            JsonElement file = root.GetProperty("popupsMeasured").GetProperty("file");
            return new MenuMetrics
            {
                ItemHeightDip = popup.GetProperty("itemHeightDip").GetDouble(),
                SeparatorHeightDip = popup.GetProperty("separatorHeightDip").GetDouble(),
                ItemCount = file.GetProperty("items").GetInt32(),
                SeparatorCount = file.GetProperty("separators").GetInt32(),
                GutterWidthDip = popup.GetProperty("textLeftFromItemEdgeDip").GetDouble(),
                CheckLeftDip = popup.GetProperty("checkMark").GetProperty("glyphLeftFromItemEdgeDip").GetDouble(),
            };
        }
    }

    // ---- native -----------------------------------------------------------------------------------

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

#pragma warning disable SYSLIB1054 // The helpers are small and need no LibraryImport plumbing.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Win32.NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out Win32.NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
#pragma warning restore SYSLIB1054

    private sealed class Checks
    {
        private readonly string _prefix;
        private readonly List<string> _failures = [];

        public Checks(string prefix)
        {
            _prefix = prefix;
        }

        public void Record(string id, bool pass, string notes)
        {
            string fullId = $"{_prefix}.{id}";
            _ = E2EResults.Record(fullId, pass, notes);
            if (!pass)
            {
                _failures.Add($"{fullId}: {notes}");
            }
        }

        public void Note(string id, string text) => E2EResults.Note($"{_prefix}.{id}", text);

        public void AssertAllPassed()
        {
            Assert.AreEqual(
                0,
                _failures.Count,
                $"Menu checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, _failures)}");
        }
    }
}

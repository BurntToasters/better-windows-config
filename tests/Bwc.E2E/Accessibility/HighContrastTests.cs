using System.Drawing;
using System.Runtime.InteropServices;
using Bwc.E2E.Controls;
using Bwc.E2E.Dialogs;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Accessibility;

/// <summary>
/// High contrast rendering with the E2E-only launch variable BWC_E2E_HIGH_CONTRAST=1. That variable makes BWC use a
/// fixed high contrast palette (the values below) and never touches the system setting. The check reads
/// SPI_GETHIGHCONTRAST before and after, and it must not change. The tree and list selection fills and the tree
/// background use the fixed COLOR_HIGHLIGHT and COLOR_WINDOW, the dialog face uses COLOR_BTNFACE, and no pixel of the
/// Light or Dark palette shows in the tree or list. Failure modes: docs/failure-modes/accessibility.md section 5.
/// </summary>
[TestClass]
public sealed class HighContrastTests
{
    /// <summary>The E2E launch variable that turns on the fixed high contrast palette.</summary>
    private const string HighContrastVariable = "BWC_E2E_HIGH_CONTRAST";

    // The fixed palette. These are COLOR_WINDOW, COLOR_WINDOWTEXT, COLOR_HIGHLIGHT, COLOR_HIGHLIGHTTEXT and COLOR_BTNFACE.
    private static readonly Color HcWindow = Color.FromArgb(0x0C, 0x0C, 0x0C);
    private static readonly Color HcWindowText = Color.FromArgb(0xFF, 0xFF, 0x00);
    private static readonly Color HcHighlight = Color.FromArgb(0x00, 0x80, 0xFF);
    private static readonly Color HcFace = Color.FromArgb(0x20, 0x20, 0x20);
    private static readonly Color HcHighlightText = Color.FromArgb(0x00, 0x00, 0x00);

    /// <summary>Colors of the Light and Dark palettes. None may appear in the tree or the list under high contrast.</summary>
    private static readonly Color[] HardCodedColors =
    [
        Color.FromArgb(0x1E, 0x1E, 0x1E),
        Color.FromArgb(0x2B, 0x2B, 0x2B),
        Color.FromArgb(0x3F, 0x3F, 0x46),
        Color.FromArgb(0x00, 0x78, 0xD4),
        Color.FromArgb(0xFF, 0xFF, 0xFF),
    ];

    private const int Tolerance = 6;
    private const int HardCodedTolerance = 2;
    private const int MinimumPaintPixels = 8;
    private const int EdgeBandPixels = 6;

    /// <summary>The list's value icon column: the icon starts at 2 DIP and the name at 18 DIP.</summary>
    private const int IconColumnDip = 20;
    private const string ListRowName = "AlphaString";
    private const string EditStringName = "AccHcString";

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Accessibility")]
    [TestCategory("Theme")]
    [TestCategory("Mutating")]
    [TestCategory("E2EOnly")]
    public void HighContrast_UsesTheFixedPaletteAndNeverChangesTheSystemSetting()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("a11y.hc.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        bool systemBefore = SystemHighContrastOn();
        SandboxRegistry? sandbox = null;
        try
        {
            sandbox = ControlTestSupport.SeedRegistry(config, 0);
            sandbox.SetString(string.Empty, EditStringName, "hc");
            using AppSession session = AccessibilitySupport.Launch(
                config,
                sandbox,
                "a11y-hc",
                new Dictionary<string, string>(StringComparer.Ordinal) { [HighContrastVariable] = "1" });
            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "a11y.hc.navigate");
            using var driver = new EditingDriver(session);
            CheckTreeAndList(session, config);
            CheckDialog(session, driver, sandbox);
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }

        bool systemAfter = SystemHighContrastOn();
        E2EResults.Record(
            "a11y.hc.system-setting-unchanged",
            systemBefore == systemAfter,
            $"SPI_GETHIGHCONTRAST before={systemBefore}, after={systemAfter}.");

        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(0, failures.Count, $"High contrast checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    /// <summary>
    /// The client area of the main window in screen pixels, less a 6 pixel edge band. The window frame lies outside the
    /// client area, and the edge band holds a 1 pixel window edge line (about 8 pixels near the bottom, measured in the
    /// first run). Neither is pane paint, so the samples come from inside the band.
    /// </summary>
    private static Rectangle ContentArea(AppSession session)
    {
        if (!GetClientRect(session.MainHwnd, out Win32.NativeRect client))
        {
            return session.Window.BoundingRectangle;
        }

        var origin = new Point(0, 0);
        _ = ClientToScreen(session.MainHwnd, ref origin);
        var area = new Rectangle(origin.X, origin.Y, client.Right, client.Bottom);
        area.Inflate(-EdgeBandPixels, -EdgeBandPixels);
        return area;
    }

#pragma warning disable SYSLIB1054 // The small user32 surface does not justify LibraryImport plumbing.
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out Win32.NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref Point point);
#pragma warning restore SYSLIB1054

    private static void CheckTreeAndList(AppSession session, E2EConfig config)
    {
        AutomationElement? tree = session.Find(ControlTestSupport.TreeControl);
        AutomationElement? selectedKey = ControlTestSupport.FindRowByName(session, ControlTestSupport.TreeControl, ControlType.TreeItem, config.RunId);
        if (tree is null || selectedKey is null)
        {
            E2EResults.Record("a11y.hc.tree-found", false, "the tree or the selected key was not found.");
            return;
        }

        // The control rectangle can reach past the window (the taskbar is behind it), so the capture is clipped to the window.
        Rectangle treeRect = Rectangle.Intersect(tree.BoundingRectangle, ContentArea(session));
        using Bitmap treeShot = WindowCapture.CaptureArea(treeRect, (uint)session.ProcessId, session.MainHwnd);
        // The selected row is filled across its right part. The text sits on the left, so the right half is sampled.
        Rectangle keyRect = selectedKey.BoundingRectangle;
        var keyArea = new Rectangle(
            keyRect.Left - treeRect.Left + (keyRect.Width / 2),
            keyRect.Top - treeRect.Top + 2,
            Math.Max(1, keyRect.Width / 2 - 4),
            Math.Max(1, keyRect.Height - 4));
        double keyFraction = FractionNear(treeShot, keyArea, HcHighlight, Tolerance);
        E2EResults.Record(
            "a11y.hc.tree-selection-fill",
            keyFraction >= 0.5,
            $"selected tree row: {keyFraction:0.00} of its right half is COLOR_HIGHLIGHT {Hex(HcHighlight)} (majority needed).");

        Color background = SafeGet(treeShot, new Point(treeShot.Width - 4, 2));
        E2EResults.Record(
            "a11y.hc.tree-background",
            Near(background, HcWindow, Tolerance),
            $"tree background={Hex(background)}, expected COLOR_WINDOW {Hex(HcWindow)}.");

        string hardCoded = DescribeHardCoded(treeShot, treeRect, treeRect.Left);
        E2EResults.Record("a11y.hc.tree-no-hard-coded", hardCoded.Length == 0, hardCoded.Length == 0 ? "no Light or Dark color in the tree." : hardCoded);

        int textPixels = CountNear(treeShot, HcWindowText, Tolerance);
        E2EResults.Record("a11y.hc.tree-text", textPixels > 0, $"tree text pixels in COLOR_WINDOWTEXT={textPixels}.");

        CheckTreeFocusRectangle(session, selectedKey, treeRect);

        CheckList(session);
    }

    /// <summary>
    /// With the tree focused, the selected row draws a dotted one pixel focus rectangle on its top edge, in the selection
    /// text color (COLOR_HIGHLIGHTTEXT, black on this palette). The top three device rows of the row are sampled across
    /// its middle half, where the dashes are, and a dash is at least a quarter of the samples.
    /// </summary>
    private static void CheckTreeFocusRectangle(AppSession session, AutomationElement selectedKey, Rectangle treeRect)
    {
        ControlTestSupport.Select(selectedKey);
        _ = ControlTestSupport.WaitUntil(() => ControlTestSupport.IsSelected(selectedKey), TimeSpan.FromSeconds(3));
        Thread.Sleep(300);

        Rectangle keyRect = selectedKey.BoundingRectangle;
        using Bitmap shot = WindowCapture.CaptureArea(treeRect, (uint)session.ProcessId, session.MainHwnd);
        // The stroke is one device row, so each of the three rows is sampled alone and the densest row is the dash line.
        double dashFraction = 0;
        for (int row = 0; row < 3; row++)
        {
            var band = new Rectangle(
                keyRect.Left - treeRect.Left + (keyRect.Width / 4),
                keyRect.Top - treeRect.Top + row,
                Math.Max(1, keyRect.Width / 2),
                1);
            dashFraction = Math.Max(dashFraction, FractionNear(shot, band, HcHighlightText, Tolerance));
        }

        E2EResults.Record(
            "a11y.hc.tree-focus-rect",
            dashFraction >= 0.3,
            $"focused tree row top edge: densest row {dashFraction:0.00} COLOR_HIGHLIGHTTEXT {Hex(HcHighlightText)} (dashes are about half).");
    }

    private static void CheckList(AppSession session)
    {
        AutomationElement? list = session.Find(ControlTestSupport.ListControl);
        AutomationElement? row = ControlTestSupport.FindRowByName(session, ControlTestSupport.ListControl, ControlType.DataItem, ListRowName);
        if (list is null || row is null)
        {
            E2EResults.Record("a11y.hc.list-found", false, $"list found={list is not null}, row '{ListRowName}' found={row is not null}.");
            return;
        }

        ControlTestSupport.Select(row);
        _ = ControlTestSupport.WaitUntil(() => ControlTestSupport.IsSelected(row), TimeSpan.FromSeconds(3));
        Rectangle listRect = Rectangle.Intersect(list.BoundingRectangle, ContentArea(session));
        using Bitmap listShot = WindowCapture.CaptureArea(listRect, (uint)session.ProcessId, session.MainHwnd);
        Rectangle rowRect = row.BoundingRectangle;
        Point fill = new(rowRect.Left - listRect.Left + rowRect.Width - 6, rowRect.Top - listRect.Top + (rowRect.Height / 2));
        Color selectionPixel = SafeGet(listShot, fill);
        E2EResults.Record(
            "a11y.hc.list-selection-fill",
            Near(selectionPixel, HcHighlight, Tolerance),
            $"selected list row fill={Hex(selectionPixel)}, expected COLOR_HIGHLIGHT {Hex(HcHighlight)}.");

        // The value icons are artwork with a white page, so the icon column (the first 20 DIP of the list) is not checked.
        double scale = Math.Max(1, ControlTestSupport.WindowDpi(session)) / 96.0;
        int iconColumnEnd = listRect.Left + (int)Math.Ceiling(IconColumnDip * scale);
        string hardCoded = DescribeHardCoded(listShot, listRect, iconColumnEnd);
        E2EResults.Record("a11y.hc.list-no-hard-coded", hardCoded.Length == 0, hardCoded.Length == 0 ? "no Light or Dark color in the list." : hardCoded);
    }

    /// <summary>The classic Edit String dialog uses COLOR_BTNFACE for its face and passes the contrast checks.</summary>
    private static void CheckDialog(AppSession session, EditingDriver driver, SandboxRegistry sandbox)
    {
        AutomationElement? row = ClassicDialogTests.OpenValueRow(session, sandbox, EditStringName, "a11y.hc.dialog");
        bool opened = row is not null && ClassicDialogTests.OpenEditor(session, driver, row, "a11y.hc.dialog.open");
        if (!opened)
        {
            E2EResults.Record("a11y.hc.dialog.open", false, "Modify did not open Edit String.");
            return;
        }

        AutomationElement dialog = driver.WaitForElement("DialogRoot")!;
        IntPtr hwnd = ClassicDialogTests.DialogHandle(dialog);
        _ = ClassicDialogTests.EnsureForeground(hwnd);
        DialogContrast.Check("a11y.hc.dialog.contrast", ClassicDialogTests.WindowOf(dialog), hwnd, HcFace, HcWindow);
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        E2EResults.Record("a11y.hc.dialog.closed", driver.WaitForNoDialog(), "Escape closed the high contrast dialog.");
    }

    /// <summary>
    /// Lists each Light or Dark color that appears in the capture, with its count and the first screen position, or
    /// returns an empty string when none appears. The position tells which control paints the color.
    /// </summary>
    private static string DescribeHardCoded(Bitmap bitmap, Rectangle screenArea, int skipBelowX)
    {
        var parts = new List<string>();
        foreach (Color color in HardCodedColors)
        {
            int count = 0;
            Point first = Point.Empty;
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (screenArea.Left + x < skipBelowX || !Near(bitmap.GetPixel(x, y), color, HardCodedTolerance))
                    {
                        continue;
                    }

                    if (count == 0)
                    {
                        first = new Point(screenArea.Left + x, screenArea.Top + y);
                    }

                    count++;
                }
            }

            // Antialiased text edges give a few pixels that match a color by chance. A paint of a color covers many.
            if (count >= MinimumPaintPixels)
            {
                parts.Add($"{Hex(color)} x{count} first at screen ({first.X},{first.Y})");
            }
        }

        return parts.Count == 0 ? string.Empty : "Light or Dark colors present: " + string.Join("; ", parts) + ".";
    }

    /// <summary>The fraction of pixels in the area that are within the tolerance of the color.</summary>
    private static double FractionNear(Bitmap bitmap, Rectangle area, Color expected, int tolerance)
    {
        int total = 0;
        int near = 0;
        for (int y = Math.Max(0, area.Top); y < Math.Min(bitmap.Height, area.Bottom); y++)
        {
            for (int x = Math.Max(0, area.Left); x < Math.Min(bitmap.Width, area.Right); x++)
            {
                total++;
                if (Near(bitmap.GetPixel(x, y), expected, tolerance))
                {
                    near++;
                }
            }
        }

        return total == 0 ? 0 : (double)near / total;
    }

    private static int CountNear(Bitmap bitmap, Color expected, int tolerance)
    {
        int count = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (Near(bitmap.GetPixel(x, y), expected, tolerance))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static Color SafeGet(Bitmap bitmap, Point point)
    {
        int x = Math.Clamp(point.X, 0, bitmap.Width - 1);
        int y = Math.Clamp(point.Y, 0, bitmap.Height - 1);
        return bitmap.GetPixel(x, y);
    }

    private static bool Near(Color actual, Color expected, int tolerance) =>
        Math.Abs(actual.R - expected.R) <= tolerance &&
        Math.Abs(actual.G - expected.G) <= tolerance &&
        Math.Abs(actual.B - expected.B) <= tolerance;

    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

#pragma warning disable SYSLIB1054 // The small user32 surface does not justify LibraryImport plumbing.
    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrastInfo
    {
        public uint CbSize;
        public uint Flags;
        public IntPtr DefaultScheme;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint param, ref HighContrastInfo info, uint winIni);
#pragma warning restore SYSLIB1054

    private const uint SpiGetHighContrast = 0x0042;
    private const uint HcfHighContrastOn = 0x00000001;

    /// <summary>Reads the system high contrast flag. Read only: the check never sets it.</summary>
    private static bool SystemHighContrastOn()
    {
        var info = new HighContrastInfo { CbSize = (uint)Marshal.SizeOf<HighContrastInfo>() };
        return SystemParametersInfoW(SpiGetHighContrast, info.CbSize, ref info, 0) && (info.Flags & HcfHighContrastOn) != 0;
    }
}

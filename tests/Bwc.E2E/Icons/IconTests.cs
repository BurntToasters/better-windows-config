using System.Drawing;
using System.Text.Json;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Icons;

/// <summary>
/// Drives the shipped Registry Editor against a seeded HKCU sandbox and checks the classic tree and
/// list rows: the expander only on keys with subkeys, the computer and folder icons, the value icons
/// (string and binary differ, REG_DWORD matches binary), and the text offsets measured on classic
/// regedit in docs/parity/oracle/regedit-row-metrics.json. The window capture and a metrics summary
/// are written to artifacts/e2e/visual/icons. The classic captures beside them come from
/// Icons_ClassicRegeditCaptures in this run, not from files left by an earlier run.
/// </summary>
[TestClass]
public sealed class IconTests
{
    // Measured on classic regedit (docs/parity/oracle/regedit-row-metrics.json), in DIPs. The text
    // value is the first ink column of the label on classic, which is 37.5 at depth 0 and 54 at depth 1.
    private const double TreeIndentDip = 16;
    private const double ExpanderCenterDip = 10.5;
    private const double TreeIconDip = 18;
    private const double TreeTextDip = 37.5;
    private const double ListIconDip = 2;
    private const double ListTextDip = 18;
    private const double IconDip = 16;
    private const double TextToleranceDip = 1;

    // A pixel is ink when its color differs from the row background by more than this sum of RGB deltas.
    private const int InkDistance = 90;

    // Depth of the sandbox children: Computer 0, HKEY_CURRENT_USER 1, Software 2, BWC-E2E 3, run key 4.
    private const int ChildDepth = 5;

    private static int _checkStart;
    private static int _classicCheckStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Icons")]
    [TestCategory("Mutating")]
    public void Icons_TreeAndListRowsMatchClassicLayout()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("icons.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        try
        {
            var sandbox = ControlTestSupport.SeedRegistry(config, 0);
            using (sandbox.CreateSubKey("Parent"))
            {
            }

            using (sandbox.CreateSubKey(@"Parent\Child"))
            {
            }

            using (sandbox.CreateSubKey("Leaf"))
            {
            }

            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = config.DataRootFor("icons"),
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "icons.sandbox.navigate");

            // Expand the run key so its children (Leaf, Parent) are rows. The selection stays on the run key.
            ControlTestSupport.SendKeys(session, "Right");
            bool childrenShown = ControlTestSupport.WaitForTreeRow(session, "Leaf", ControlTestSupport.ExpandTimeout) &&
                ControlTestSupport.WaitForTreeRow(session, "Parent", ControlTestSupport.ExpandTimeout);
            E2EResults.Record("icons.tree.children", childrenShown, $"Leaf and Parent rows visible={childrenShown}.");
            if (!childrenShown)
            {
                FailIfNeeded();
                return;
            }

            AutomationElement? computer = ControlTestSupport.FindRowByName(session, ControlTestSupport.TreeControl, ControlType.TreeItem, "Computer");
            AutomationElement? hkcu = ControlTestSupport.FindRowByName(session, ControlTestSupport.TreeControl, ControlType.TreeItem, "HKEY_CURRENT_USER");
            AutomationElement? parent = ControlTestSupport.FindRowByName(session, ControlTestSupport.TreeControl, ControlType.TreeItem, "Parent");
            AutomationElement? leaf = ControlTestSupport.FindRowByName(session, ControlTestSupport.TreeControl, ControlType.TreeItem, "Leaf");
            AutomationElement? alpha = ControlTestSupport.FindRowByName(session, ControlTestSupport.ListControl, ControlType.DataItem, "AlphaString");
            AutomationElement? binary = ControlTestSupport.FindRowByName(session, ControlTestSupport.ListControl, ControlType.DataItem, "SampleBinary");
            AutomationElement? dword = ControlTestSupport.FindRowByName(session, ControlTestSupport.ListControl, ControlType.DataItem, "SampleDword");
            bool rowsFound = computer is not null && hkcu is not null && parent is not null && leaf is not null &&
                alpha is not null && binary is not null && dword is not null;
            E2EResults.Record("icons.rows.found", rowsFound, $"all tree and list rows found={rowsFound}.");
            if (!rowsFound)
            {
                FailIfNeeded();
                return;
            }

            Rectangle window = session.Window.BoundingRectangle;
            double scale = Math.Max(1, ControlTestSupport.WindowDpi(session)) / 96.0;
            using var capture = Capture.Element(session.Window);
            Bitmap bitmap = capture.Bitmap;
            string captureDir = Path.Combine(config.ArtifactsDir, "visual", "icons");
            Directory.CreateDirectory(captureDir);
            capture.ToFile(Path.Combine(captureDir, $"bwc-{config.Rid}-{config.Flavor}-tree-list.png"));

            var measured = new Dictionary<string, double>(StringComparer.Ordinal);

            // Expanders: UIA state from the row peer plus pixels in the expander box.
            Rectangle computerRow = ToLocal(computer!.BoundingRectangle, window);
            Rectangle hkcuRow = ToLocal(hkcu!.BoundingRectangle, window);
            Rectangle parentRow = ToLocal(parent!.BoundingRectangle, window);
            Rectangle leafRow = ToLocal(leaf!.BoundingRectangle, window);
            Rectangle alphaRow = ToLocal(alpha!.BoundingRectangle, window);
            Rectangle binaryRow = ToLocal(binary!.BoundingRectangle, window);
            Rectangle dwordRow = ToLocal(dword!.BoundingRectangle, window);

            Color computerBg = Background(bitmap, computerRow);
            Color parentBg = Background(bitmap, parentRow);
            Color leafBg = Background(bitmap, leafRow);
            Color hkcuBg = Background(bitmap, hkcuRow);

            string leafState = ControlTestSupport.StateOf(leaf);
            int leafInk = Ink(bitmap, ExpanderBox(leafRow, ChildDepth, scale), leafBg);
            E2EResults.Record(
                "icons.tree.expander.leaf",
                leafState == "LeafNode" && leafInk == 0,
                $"Leaf: state={leafState}, expander ink={leafInk} (required LeafNode and 0).");

            string parentState = ControlTestSupport.StateOf(parent);
            int parentInk = Ink(bitmap, ExpanderBox(parentRow, ChildDepth, scale), parentBg);
            E2EResults.Record(
                "icons.tree.expander.parent",
                parentState == "Collapsed" && parentInk >= 4,
                $"Parent: state={parentState}, expander ink={parentInk} (required Collapsed and at least 4).");

            string computerState = ControlTestSupport.StateOf(computer);
            int computerInk = Ink(bitmap, ExpanderBox(computerRow, 0, scale), computerBg);
            E2EResults.Record(
                "icons.tree.expander.computer",
                computerState == "Expanded" && computerInk >= 4,
                $"Computer: state={computerState}, expander ink={computerInk} (required Expanded and at least 4).");

            // Icons: non-empty at the measured position.
            Rectangle computerIcon = TreeIconArea(computerRow, 0, scale);
            int computerIconInk = Ink(bitmap, computerIcon, computerBg);
            E2EResults.Record(
                "icons.tree.icon.computer",
                HasIcon(computerIconInk, scale),
                $"Computer icon ink={computerIconInk} of {Area(computerIcon)} px at {Describe(computerIcon)}.");

            Rectangle parentIcon = TreeIconArea(parentRow, ChildDepth, scale);
            int parentIconInk = Ink(bitmap, parentIcon, parentBg);
            E2EResults.Record(
                "icons.tree.icon.folder",
                HasIcon(parentIconInk, scale) && HasIcon(Ink(bitmap, TreeIconArea(leafRow, ChildDepth, scale), leafBg), scale),
                $"Parent folder icon ink={parentIconInk} of {Area(parentIcon)} px.");

            // Text offsets, measured as the first ink column after the icon.
            CheckText(bitmap, "computer", computerRow, computerBg, 0, scale, measured, "icons.tree.text.computer");
            CheckText(bitmap, "hkcu", hkcuRow, hkcuBg, 1, scale, measured, "icons.tree.text.hkcu");
            CheckText(bitmap, "leaf", leafRow, leafBg, ChildDepth, scale, measured, "icons.tree.text.leaf");

            // List value icons: string and binary differ, REG_DWORD uses the binary icon.
            Color alphaBg = Background(bitmap, alphaRow);
            Color binaryBg = Background(bitmap, binaryRow);
            Color dwordBg = Background(bitmap, dwordRow);
            Rectangle alphaIcon = ListIconArea(alphaRow, scale);
            Rectangle binaryIcon = ListIconArea(binaryRow, scale);
            Rectangle dwordIcon = ListIconArea(dwordRow, scale);
            int alphaInk = Ink(bitmap, alphaIcon, alphaBg);
            int binaryInk = Ink(bitmap, binaryIcon, binaryBg);
            int dwordInk = Ink(bitmap, dwordIcon, dwordBg);
            int stringVsBinary = DiffCount(bitmap, alphaIcon, binaryIcon);
            int dwordVsBinary = DiffCount(bitmap, dwordIcon, binaryIcon);
            E2EResults.Record(
                "icons.list.icon.string",
                HasIcon(alphaInk, scale),
                $"AlphaString (REG_SZ) icon ink={alphaInk}.");
            E2EResults.Record(
                "icons.list.icon.binary",
                HasIcon(binaryInk, scale) && HasIcon(dwordInk, scale),
                $"SampleBinary icon ink={binaryInk}; SampleDword icon ink={dwordInk}.");
            E2EResults.Record(
                "icons.list.icon.distinct",
                stringVsBinary >= 10 && dwordVsBinary <= 2,
                $"string vs binary differing px={stringVsBinary} (required at least 10); dword vs binary={dwordVsBinary} (required at most 2).");

            CheckListText(bitmap, "alpha", alphaRow, alphaBg, scale, measured);

            WriteSummary(captureDir, config, scale, measured);

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    /// <summary>
    /// Takes the two classic regedit captures beside the BWC capture, inside this run. It is a separate
    /// test so the classic window never overlaps the BWC window under test. Classic regedit's applet
    /// values are restored afterwards, and that restore is a check of its own.
    /// </summary>
    [TestMethod]
    [TestCategory("Icons")]
    [TestCategory("Mutating")]
    public void Icons_ClassicRegeditCaptures()
    {
        _classicCheckStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);

        string captureDir = Path.Combine(config.ArtifactsDir, "visual", "icons");
        ClassicRegeditCapture.Run(config, captureDir);

        var failures = E2EResults.FailedChecksSince(_classicCheckStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Classic capture checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private static void CheckText(
        Bitmap bitmap,
        string label,
        Rectangle row,
        Color background,
        int depth,
        double scale,
        Dictionary<string, double> measured,
        string checkId)
    {
        double expectedDip = TreeIndentDip * depth + TreeTextDip;
        double? startDip = FirstInkDip(
            bitmap,
            row,
            background,
            TreeIndentDip * depth + TreeIconDip + IconDip + 1,
            TreeIndentDip * depth + TreeTextDip + 16,
            scale);
        measured[$"tree.{label}.textLeftDip"] = startDip ?? double.NaN;
        bool ok = startDip is { } start && Math.Abs(start - expectedDip) <= TextToleranceDip;
        E2EResults.Record(
            checkId,
            ok,
            $"{label} (depth {depth}): text starts at {startDip?.ToString("F1") ?? "none"} DIP, expected {expectedDip:F1} DIP +/- {TextToleranceDip}.");
    }

    private static void CheckListText(Bitmap bitmap, string label, Rectangle row, Color background, double scale, Dictionary<string, double> measured)
    {
        // Start at the text origin. The icon ends at 18 DIP, so an earlier start would read the icon.
        double? startDip = FirstInkDip(bitmap, row, background, ListTextDip, ListTextDip + 16, scale);
        measured[$"list.{label}.textLeftDip"] = startDip ?? double.NaN;
        bool ok = startDip is { } start && Math.Abs(start - ListTextDip) <= TextToleranceDip;
        E2EResults.Record(
            "icons.list.text",
            ok,
            $"AlphaString name starts at {startDip?.ToString("F1") ?? "none"} DIP, expected {ListTextDip} DIP +/- {TextToleranceDip}.");
    }

    private static void WriteSummary(string captureDir, E2EConfig config, double scale, Dictionary<string, double> measured)
    {
        var summary = new Dictionary<string, object?>
        {
            ["rid"] = config.Rid,
            ["flavor"] = config.Flavor,
            ["dpiScale"] = scale,
            ["measuredDip"] = measured,
            ["expectedDip"] = new Dictionary<string, double>
            {
                ["tree.indentPerLevel"] = TreeIndentDip,
                ["tree.expanderCenterDepth0"] = ExpanderCenterDip,
                ["tree.iconLeftDepth0"] = TreeIconDip,
                ["tree.textLeftDepth0"] = TreeTextDip,
                ["list.iconLeft"] = ListIconDip,
                ["list.textLeft"] = ListTextDip,
            },
        };

        string path = Path.Combine(captureDir, $"bwc-{config.Rid}-{config.Flavor}-row-metrics.json");
        File.WriteAllText(path, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Rectangle ToLocal(Rectangle screen, Rectangle window) =>
        new(screen.X - window.X, screen.Y - window.Y, screen.Width, screen.Height);

    private static Color Background(Bitmap bitmap, Rectangle row)
    {
        int x = Math.Clamp(row.Right - 6, 0, bitmap.Width - 1);
        int y = Math.Clamp(row.Y + row.Height / 2, 0, bitmap.Height - 1);
        return bitmap.GetPixel(x, y);
    }

    private static Rectangle ExpanderBox(Rectangle row, int depth, double scale)
    {
        double centerX = row.X + (TreeIndentDip * depth + ExpanderCenterDip) * scale;
        int midY = row.Y + row.Height / 2;
        int half = (int)Math.Round(4 * scale);
        return new Rectangle(
            (int)Math.Round(centerX - 3 * scale),
            midY - half,
            (int)Math.Round(6 * scale),
            half * 2);
    }

    private static Rectangle TreeIconArea(Rectangle row, int depth, double scale)
    {
        int size = (int)Math.Round(IconDip * scale);
        int left = row.X + (int)Math.Round((TreeIndentDip * depth + TreeIconDip) * scale);
        return new Rectangle(left, row.Y + (row.Height - size) / 2, size, size);
    }

    private static Rectangle ListIconArea(Rectangle row, double scale)
    {
        int size = (int)Math.Round(IconDip * scale);
        int left = row.X + (int)Math.Round(ListIconDip * scale);
        return new Rectangle(left, row.Y + (row.Height - size) / 2, size, size);
    }

    /// <summary>
    /// The first column, from fromDip to toDip after the row start, that holds ink in the row's middle band.
    /// </summary>
    private static double? FirstInkDip(Bitmap bitmap, Rectangle row, Color background, double fromDip, double toDip, double scale)
    {
        int midY = row.Y + row.Height / 2;
        int top = Math.Max(0, midY - (int)Math.Round(4 * scale));
        int bottom = Math.Min(bitmap.Height - 1, midY + (int)Math.Round(4 * scale));
        int from = row.X + (int)Math.Round(fromDip * scale);
        int to = Math.Min(bitmap.Width - 1, row.X + (int)Math.Round(toDip * scale));
        for (int x = Math.Max(0, from); x <= to; x++)
        {
            for (int y = top; y <= bottom; y++)
            {
                if (IsInk(bitmap.GetPixel(x, y), background))
                {
                    return (x - row.X) / scale;
                }
            }
        }

        return null;
    }

    private static int Ink(Bitmap bitmap, Rectangle area, Color background)
    {
        int count = 0;
        Rectangle clipped = Rectangle.Intersect(area, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        for (int x = clipped.Left; x < clipped.Right; x++)
        {
            for (int y = clipped.Top; y < clipped.Bottom; y++)
            {
                if (IsInk(bitmap.GetPixel(x, y), background))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static int DiffCount(Bitmap bitmap, Rectangle a, Rectangle b)
    {
        int count = 0;
        for (int dx = 0; dx < a.Width; dx++)
        {
            for (int dy = 0; dy < a.Height; dy++)
            {
                int ax = a.X + dx;
                int ay = a.Y + dy;
                int bx = b.X + dx;
                int by = b.Y + dy;
                if (ax >= bitmap.Width || ay >= bitmap.Height || bx >= bitmap.Width || by >= bitmap.Height)
                {
                    continue;
                }

                if (IsInk(bitmap.GetPixel(ax, ay), bitmap.GetPixel(bx, by)))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static bool IsInk(Color pixel, Color background) =>
        Math.Abs(pixel.R - background.R) + Math.Abs(pixel.G - background.G) + Math.Abs(pixel.B - background.B) > InkDistance;

    /// <summary>An icon is present when at least a tenth of its box is ink.</summary>
    private static bool HasIcon(int ink, double scale)
    {
        double box = IconDip * scale * IconDip * scale;
        return ink >= box * 0.1;
    }

    private static int Area(Rectangle rectangle) => rectangle.Width * rectangle.Height;

    private static string Describe(Rectangle rectangle) => $"x={rectangle.X},y={rectangle.Y},{rectangle.Width}x{rectangle.Height}";

    private static void FailIfNeeded()
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Icon checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}

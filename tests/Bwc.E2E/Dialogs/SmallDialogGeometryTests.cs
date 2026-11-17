using System.Runtime.InteropServices;
using System.Text.Json;
using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Dialogs;

/// <summary>
/// VIEW-019: the Add to Favorites, Remove Favorites, and Find dialogs are opened, and each control's UIA
/// rectangle is compared with the measured classic rectangle in docs/parity/oracle/regedit-dialog-rects.json.
/// The classic numbers are device pixels at 192 DPI relative to the dialog client origin. They are scaled by
/// the BWC dialog DPI divided by 192, and the tolerance is 4 device pixels per edge at 192 DPI, scaled the same
/// way. The result artifact records the measured and expected rectangles for every control.
/// </summary>
[TestClass]
public sealed class SmallDialogGeometryTests
{
    private const int MenuFavorites = 517;
    private const int MenuFavoritesAdd = 1281;
    private const int MenuFavoritesRemove = 1280;
    private const int MenuEdit = 513;
    private const int MenuEditFind = 662;
    private const string TargetKeyName = "FavoriteTarget";
    private const string FavoriteOne = "Existing";
    private const string FavoriteTwo = "Second";
    private const int ClassicDpi = 192;
    private const int ToleranceAtClassicDpi = 4;
    private const string RectsDialogAdd = "add-favorite";
    private const string RectsDialogRemove = "remove-favorite";
    private const string RectsDialogFind = "find";

    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);

    private static readonly IReadOnlyDictionary<int, string> AddFavoriteIds = new Dictionary<int, string>
    {
        [1] = "DialogPrimary",
        [2] = "DialogSecondary",
        [1190] = "DialogTextInput",
        [65535] = "FavoriteNameLabel",
    };

    private static readonly IReadOnlyDictionary<int, string> RemoveFavoriteIds = new Dictionary<int, string>
    {
        [1] = "DialogPrimary",
        [2] = "DialogSecondary",
        [1191] = "FavoritesList",
        [65535] = "FavoritesLabel",
    };

    private static readonly IReadOnlyDictionary<int, string> FindIds = new Dictionary<int, string>
    {
        [1] = "DialogPrimary",
        [2] = "DialogSecondary",
        [1150] = "FindText",
        [1151] = "FindWholeString",
        [1152] = "FindKeys",
        [1153] = "FindValues",
        [1154] = "FindData",
        [1160] = "FindLookAt",
        [1161] = "FindSubtree",
        [65535] = "FindWhatLabel",
    };

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Dialogs")]
    [TestCategory("Favorites")]
    [TestCategory("Mutating")]
    public void SmallDialogs_RectsMatchClassicMeasurements()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        config.EnsureExecutablesExist();

        string rectsPath = Path.Combine(config.RepoRoot, "docs", "parity", "oracle", "regedit-dialog-rects.json");
        using JsonDocument classic = JsonDocument.Parse(File.ReadAllText(rectsPath));

        var sandbox = new SandboxRegistry(config);
        var measured = new List<DialogMeasure>();
        try
        {
            sandbox.Cleanup();
            Seed(sandbox);

            string dataRoot = config.DataRootFor("small-dialog-rects");
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }

            Directory.CreateDirectory(dataRoot);
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
                ["BWC_E2E_FAVORITES_ROOT"] = sandbox.RunRootDisplayPath + @"\Favorites",
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);
            using var automation = new UIA3Automation();

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config, childKey: TargetKeyName);
            bool selected = ControlTestSupport.WaitForTreeSelection(session, TargetKeyName, UiTimeout);
            E2EResults.Record("rects.navigate", navigated && selected, $"navigated={navigated}, selected={selected}.");
            E2EGuards.RequireSandboxSelection(session, sandbox, "rects.sandbox.select");

            measured.Add(OpenAndMeasure(driver, RectsDialogAdd, AddFavoriteIds, classic, () => driver.OpenMenu(MenuFavorites, MenuFavoritesAdd)));
            CloseDialog(driver);

            measured.Add(OpenAndMeasure(driver, RectsDialogRemove, RemoveFavoriteIds, classic, () => driver.OpenMenu(MenuFavorites, MenuFavoritesRemove)));
            CloseDialog(driver);

            measured.Add(OpenAndMeasure(driver, RectsDialogFind, FindIds, classic, () => driver.OpenMenu(MenuEdit, MenuEditFind)));
            CheckFindNextEnabledState(driver, classic);
            CloseDialog(driver);

            WriteArtifact(config, measured);

            var failures = E2EResults.FailedChecksSince(checkStart);
            Assert.AreEqual(
                0,
                failures.Count,
                $"Small dialog rectangle checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    private static void Seed(SandboxRegistry sandbox)
    {
        sandbox.SetString(string.Empty, "RootMarker", "root");
        sandbox.CreateKey(TargetKeyName);
        sandbox.SetString(TargetKeyName, "Marker", "favorite-target");

        sandbox.SetString("Favorites", FavoriteOne, $@"Computer\HKEY_CURRENT_USER\{sandbox.RunRootPath}\{TargetKeyName}");
        sandbox.SetString("Favorites", FavoriteTwo, $@"Computer\HKEY_CURRENT_USER\{sandbox.RunRootPath}");
    }

    private static DialogMeasure OpenAndMeasure(
        EditingDriver driver,
        string name,
        IReadOnlyDictionary<int, string> ids,
        JsonDocument classic,
        Action open)
    {
        open();
        AutomationElement? root = driver.WaitForElement("DialogRoot", UiTimeout);
        if (root is null)
        {
            E2EResults.Record("rects." + name + ".open", false, "The dialog did not open.");
            return new DialogMeasure(name, 0, 0, 0, Array.Empty<ControlMeasure>());
        }

        JsonElement expected = ClassicDialog(classic, name);
        IntPtr hwnd = ClassicDialogTests.DialogHandle(root);
        uint dpi = GetDpiForWindow(hwnd);
        double scale = dpi / (double)ClassicDpi;
        double tolerance = ToleranceAtClassicDpi * scale;
        System.Drawing.Rectangle client = ClientScreen(hwnd);

        var controls = new List<ControlMeasure>();
        bool allWithin = true;
        var deviations = new List<string>();

        double expectedWidth = expected.GetProperty("clientWidth").GetInt32() * scale;
        double expectedHeight = expected.GetProperty("clientHeight").GetInt32() * scale;
        bool sizeOk = Math.Abs(client.Width - expectedWidth) <= tolerance && Math.Abs(client.Height - expectedHeight) <= tolerance;
        allWithin &= sizeOk;
        if (!sizeOk)
        {
            deviations.Add($"client {client.Width}x{client.Height} px, classic {expectedWidth:0.0}x{expectedHeight:0.0} px");
        }

        foreach (JsonElement control in expected.GetProperty("controls").EnumerateArray())
        {
            int id = control.GetProperty("id").GetInt32();
            if (!ids.TryGetValue(id, out string? automationId))
            {
                continue;
            }

            int x = control.GetProperty("x").GetInt32();
            int y = control.GetProperty("y").GetInt32();
            int width = control.GetProperty("width").GetInt32();
            int height = control.GetProperty("height").GetInt32();
            var want = new double[] { x * scale, y * scale, (x + width) * scale, (y + height) * scale };

            AutomationElement? element = root.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            if (element is null)
            {
                allWithin = false;
                deviations.Add($"{automationId} missing");
                controls.Add(new ControlMeasure(id, automationId, null, want, tolerance, false));
                continue;
            }

            var bounds = element.BoundingRectangle;
            double left = bounds.Left - client.Left;
            double top = bounds.Top - client.Top;
            var got = new double[] { left, top, left + bounds.Width, top + bounds.Height };
            bool within = true;
            for (int edge = 0; edge < 4; edge++)
            {
                within &= Math.Abs(got[edge] - want[edge]) <= tolerance;
            }

            allWithin &= within;
            if (!within)
            {
                deviations.Add(
                    $"{automationId} [{Format(got)}] px, classic [{Format(want)}] px");
            }

            controls.Add(new ControlMeasure(id, automationId, got, want, tolerance, within));
        }

        E2EResults.Record(
            "rects." + name,
            allWithin,
            $"dpi={dpi}, scale={scale:0.###}, tolerance={tolerance:0.0} px, controls={controls.Count}. " +
            (deviations.Count == 0 ? "All within tolerance." : string.Join("; ", deviations)));

        return new DialogMeasure(name, dpi, client.Width, client.Height, controls);
    }

    private static void CheckFindNextEnabledState(EditingDriver driver, JsonDocument classic)
    {
        JsonElement expected = ClassicDialog(classic, RectsDialogFind);
        bool classicEnabled = false;
        foreach (JsonElement control in expected.GetProperty("controls").EnumerateArray())
        {
            if (control.GetProperty("id").GetInt32() == 1)
            {
                classicEnabled = control.GetProperty("enabled").GetBoolean();
            }
        }

        AutomationElement? primary = driver.WaitForElement("DialogPrimary", TimeSpan.FromSeconds(2));
        bool bwcEnabled = primary is not null && primary.IsEnabled;
        E2EResults.Record(
            "rects.find.next-enabled",
            bwcEnabled == classicEnabled,
            $"classic Find Next enabled={classicEnabled} with an empty search, BWC enabled={bwcEnabled}.");

        // Live: typing enables Find Next, clearing the text disables it (classic-dialog.md 12.4).
        bool typed = driver.SetText("FindText", "needle");
        bool enabledWithText = typed && ControlTestSupport.WaitUntil(
            () => driver.WaitForElement("DialogPrimary", TimeSpan.FromSeconds(1))?.IsEnabled == true,
            TimeSpan.FromSeconds(3));
        bool cleared = driver.SetText("FindText", string.Empty);
        bool disabledAgain = cleared && ControlTestSupport.WaitUntil(
            () => driver.WaitForElement("DialogPrimary", TimeSpan.FromSeconds(1))?.IsEnabled == false,
            TimeSpan.FromSeconds(3));
        E2EResults.Record(
            "rects.find.next-live",
            enabledWithText && disabledAgain,
            $"typed={typed}, enabled with text={enabledWithText}, cleared={cleared}, disabled again={disabledAgain}.");
    }

    private static void CloseDialog(EditingDriver driver)
    {
        _ = driver.Invoke("DialogSecondary");
        _ = driver.WaitForNoDialog(TimeSpan.FromSeconds(10));
    }

    private static JsonElement ClassicDialog(JsonDocument classic, string name)
    {
        foreach (JsonElement dialog in classic.RootElement.GetProperty("dialogs").EnumerateArray())
        {
            if (string.Equals(dialog.GetProperty("name").GetString(), name, StringComparison.Ordinal))
            {
                return dialog;
            }
        }

        throw new InvalidOperationException($"The classic rectangles file has no dialog '{name}'.");
    }

    private static string Format(double[] edges) => string.Join(",", edges.Select(edge => edge.ToString("0.0")));

    private static void WriteArtifact(E2EConfig config, IReadOnlyList<DialogMeasure> dialogs)
    {
        string folder = Path.Combine(config.ArtifactsDir, "visual", "dialogs");
        Directory.CreateDirectory(folder);
        var document = new
        {
            schema = 1,
            run = config.RunId,
            classicDpi = ClassicDpi,
            dialogs = dialogs.Select(dialog => new
            {
                name = dialog.Name,
                dpi = dialog.Dpi,
                clientPx = new { width = dialog.ClientWidth, height = dialog.ClientHeight },
                controls = dialog.Controls.Select(control => new
                {
                    id = control.Id,
                    automationId = control.AutomationId,
                    measuredPx = control.Measured is null ? null : Edges(control.Measured),
                    expectedPx = Edges(control.Expected),
                    tolerancePx = Math.Round(control.Tolerance, 2),
                    withinTolerance = control.Within,
                }),
            }),
        };

        string path = Path.Combine(folder, "small-dialog-rects.json");
        File.WriteAllText(path, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
        E2EResults.Note("rects.artifact", $"wrote {path}.");
    }

    private static object Edges(double[] edges) => new
    {
        left = Math.Round(edges[0], 2),
        top = Math.Round(edges[1], 2),
        right = Math.Round(edges[2], 2),
        bottom = Math.Round(edges[3], 2),
    };

    private static System.Drawing.Rectangle ClientScreen(IntPtr hwnd)
    {
        var origin = new ScreenPoint { X = 0, Y = 0 };
        if (!ClientToScreen(hwnd, ref origin) || !GetClientRect(hwnd, out Win32.NativeRect client))
        {
            throw new InvalidOperationException("The dialog client area could not be read.");
        }

        return new System.Drawing.Rectangle(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top);
    }

    private sealed record ControlMeasure(int Id, string AutomationId, double[]? Measured, double[] Expected, double Tolerance, bool Within);

    private sealed record DialogMeasure(string Name, uint Dpi, int ClientWidth, int ClientHeight, IReadOnlyList<ControlMeasure> Controls);

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint
    {
        public int X;
        public int Y;
    }

#pragma warning disable SYSLIB1054 // This test project does not use LibraryImport for these small user32 calls.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref ScreenPoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out Win32.NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
#pragma warning restore SYSLIB1054
}

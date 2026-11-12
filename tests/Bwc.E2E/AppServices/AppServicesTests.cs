using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.AppServices;

/// <summary>
/// Checks the Registry Editor app services through real input and real processes: the log file,
/// the font dialog, window placement and splitter and column persistence across restarts, off-screen
/// placement, single instance activation and /m, the regedit file.reg confirmation (answered No and
/// Yes), the About box, the View &gt; Theme palette (sampled from pixels), and the Load Hive and
/// Unload Hive enable rules. Registry writes stay under the run sandbox. No hive is loaded.
/// </summary>
[TestClass]
public sealed class AppServicesTests
{
    private const int MenuFile = 512;
    private const int MenuView = 514;
    private const int MenuHelp = 515;
    private const int CommandFont = 699;
    private const int CommandAbout = 667;
    private const int CommandTheme = 9100;
    private const int CommandThemeLight = 9102;
    private const int CommandThemeDark = 9103;
    private const int CommandLoadHive = 680;
    private const int CommandUnloadHive = 681;
    private const string NativeDialogClass = "#32770";
    private const string Caption = "Registry Editor";
    private const string FontDialogTitle = "Font";
    private const string ListRow0 = "ListRow_0";
    private const string SplitterId = "Splitter";
    private const string KnownKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const int SizeComboControlId = 1138;
    private const int DialogOkId = 1;
    private const int ClassicTreePaneWidthDip = 216;
    private const double DefaultListRowDip = 18;
    private const double DefaultTreeRowDip = 17;
    private const double RestoredNameDip = 150;
    private const string TreeSplitDeltaPixels = "60";
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DialogTimeout = TimeSpan.FromSeconds(10);

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("AppServices")]
    public void AppServices_StartupCreatesLogFile()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        if (!RequirePublish(config))
        {
            return;
        }

        string dataRoot = AppServicesSupport.FreshDataRoot(config, "log");
        string logDir = AppServicesPaths.LogDirectory(dataRoot);
        using (RegeditProcess app = RegeditProcess.Start(config, dataRoot, Array.Empty<string>()))
        {
            bool created = AppServicesSupport.WaitUntil(
                () => Directory.Exists(logDir) && Directory.EnumerateFiles(logDir, "log-*.txt").Any(),
                UiTimeout);
            string logText = created ? ReadFirstLog(logDir) : string.Empty;
            E2EResults.Record(
                "appsvc.log.created",
                created,
                $"log directory '{logDir}' exists with a log-*.txt file={created}.");
            E2EResults.Record(
                "appsvc.log.startupEntry",
                logText.Contains("Registry Editor", StringComparison.Ordinal) && logText.Contains("starting", StringComparison.Ordinal),
                $"startup entry present={logText.Contains("starting", StringComparison.Ordinal)}; {logText.Length} characters in the log.");
            app.Close();
        }

        FailIfNeeded(checkStart);
    }

    [TestMethod]
    [TestCategory("AppServices")]
    public void AppServices_FontDialogChangesRowHeightAndPersists()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        if (!RequirePublish(config))
        {
            return;
        }

        string dataRoot = AppServicesSupport.FreshDataRoot(config, "font");
        double expectedBefore;
        double expectedAfter;
        using (RegeditProcess app = RegeditProcess.Start(config, dataRoot, Array.Empty<string>()))
        {
            Navigate(app, KnownKey);
            uint dpi = AppServicesSupport.DpiOf(app.MainHwnd);
            expectedBefore = ExpectedRowPixels(DefaultListRowDip, 12, dpi);
            expectedAfter = ExpectedRowPixels(DefaultListRowDip, 16 * 96.0 / 72.0, dpi);
            double tolerance = Tolerance(dpi);

            double before = WaitForRowHeight(app);
            E2EResults.Record(
                "appsvc.font.rowBefore",
                Math.Abs(before - expectedBefore) <= tolerance,
                $"list row height before={before:0.#} px, expected {expectedBefore:0.#} px at 12 DIP font (dpi {dpi}).");

            app.InvokeMenu(MenuView, CommandFont);
            IntPtr dialog = AppServicesSupport.WaitForOwnedWindow(app.MainHwnd, NativeDialogClass, FontDialogTitle, DialogTimeout);
            E2EResults.Record("appsvc.font.dialog.opened", dialog != IntPtr.Zero, $"Font dialog found={dialog != IntPtr.Zero}.");
            if (dialog == IntPtr.Zero)
            {
                Assert.Fail("View > Font did not open the font dialog.");
            }

            IntPtr sizeBox = AppServicesSupport.ChildOf(dialog, SizeComboControlId);
            string initial = AppServicesSupport.TitleOf(sizeBox);
            E2EResults.Note(
                "appsvc.font.dialog.initialSize",
                $"size box text read as '{initial}' before the change; the row height check covers the applied size.");

            // Real input: click the size box, select its text, type 16, then click OK.
            var editCenter = AppServicesSupport.CenterOf(sizeBox);
            AppServicesSupport.Click(editCenter.X, editCenter.Y);
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            Keyboard.Type("16");
            var okCenter = AppServicesSupport.CenterOf(AppServicesSupport.ChildOf(dialog, DialogOkId));
            AppServicesSupport.Click(okCenter.X, okCenter.Y);

            bool closed = AppServicesSupport.WaitUntil(() => !AppServicesSupport.IsWindow(dialog), DialogTimeout);
            E2EResults.Record("appsvc.font.dialog.closed", closed, $"Font dialog closed after OK={closed}.");

            // The common dialog hands the window back. Wait until the window is enabled and foreground, then the
            // rows are read again through UI Automation from the handle, so no element from before the dialog is used.
            bool enabledAfterDialog = AppServicesSupport.WaitUntil(() => AppServicesSupport.IsEnabled(app.MainHwnd), DialogTimeout);
            bool foregroundAfterDialog = enabledAfterDialog && AppServicesSupport.ForceForeground(app.MainHwnd);
            E2EResults.Record(
                "appsvc.font.window.ready",
                enabledAfterDialog && foregroundAfterDialog,
                $"main window enabled={enabledAfterDialog}, foreground={foregroundAfterDialog} after the font dialog closed.");

            bool changed = AppServicesSupport.WaitUntil(
                () => Math.Abs(WaitForRowHeight(app) - expectedAfter) <= tolerance,
                DialogTimeout);
            double after = WaitForRowHeight(app);
            try
            {
                var dataItems = app.Window.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem));
                string firstItems = string.Join(";", dataItems.Take(4).Select(item => $"{ControlTestSupport.SafeId(item)}:{item.BoundingRectangle.Height}"));
                E2EResults.Note(
                    "appsvc.font.rowHeight.diagnostic",
                    $"after the font dialog: dataItems={dataItems.Length} [{firstItems}], selected address='{AddressText(app)}'.");
            }
            catch (Exception ex)
            {
                E2EResults.Note("appsvc.font.rowHeight.diagnostic", $"the list could not be read after the font dialog: {ex.GetType().Name}: {ex.Message}");
            }
            E2EResults.Record(
                "appsvc.font.rowHeight.changed",
                changed,
                $"list row height after 16 DIP font={after:0.#} px, expected {expectedAfter:0.#} px.");

            // The whole window must stay walkable through UI Automation after the metrics change: every
            // descendant is enumerated and read, and the list rows and tree items must both be present.
            int listRows = 0;
            int treeItems = 0;
            int unnamed = 0;
            string walkError = string.Empty;
            try
            {
                foreach (AutomationElement element in app.Window.FindAllDescendants())
                {
                    ControlType type = element.ControlType;
                    if (type == ControlType.DataItem)
                    {
                        listRows++;
                    }
                    else if (type == ControlType.TreeItem)
                    {
                        treeItems++;
                    }

                    try
                    {
                        _ = element.Name;
                    }
                    catch (Exception)
                    {
                        // Some framework elements do not expose a name. Counted, not treated as a broken walk.
                        unnamed++;
                    }
                }
            }
            catch (Exception ex)
            {
                walkError = $"{ex.GetType().Name}: {ex.Message}";
            }

            E2EResults.Record(
                "appsvc.font.uiaWalk",
                walkError.Length == 0 && listRows > 0 && treeItems > 0,
                $"window UI Automation walk after the font change: list rows={listRows}, tree items={treeItems}, elements without a name={unnamed}, error='{walkError}'.");

            // The window closes the way a user closes it, so the saved font is the one the restart must read.
            bool closedByUser = app.CloseWithAltF4(TimeSpan.FromSeconds(15));
            E2EResults.Record(
                "appsvc.font.altF4.exits",
                closedByUser,
                $"Alt+F4 with the editor in the foreground closed it={closedByUser}.");
        }

        using (RegeditProcess app = RegeditProcess.Start(config, dataRoot, Array.Empty<string>()))
        {
            double restored = WaitForRowHeight(app);
            E2EResults.Record(
                "appsvc.font.persisted",
                Math.Abs(restored - expectedAfter) <= Tolerance(AppServicesSupport.DpiOf(app.MainHwnd)),
                $"after restart the list row height is {restored:0.#} px; expected {expectedAfter:0.#} px.");
            app.Close();
        }

        FailIfNeeded(checkStart);
    }

    [TestMethod]
    [TestCategory("AppServices")]
    public void AppServices_PlacementSplitAndColumnsPersistAcrossRestart()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        if (!RequirePublish(config))
        {
            return;
        }

        string dataRoot = AppServicesSupport.FreshDataRoot(config, "place");
        string settingsPath = AppServicesPaths.SettingsFile(dataRoot);
        const int x = 150;
        const int y = 120;
        const int width = 900;
        const int height = 640;
        double expectedTree;
        using (RegeditProcess app = RegeditProcess.Start(config, dataRoot, Array.Empty<string>()))
        {
            Navigate(app, KnownKey);
            uint dpi = AppServicesSupport.DpiOf(app.MainHwnd);
            AppServicesSupport.MoveAndResize(app.MainHwnd, x, y, width, height);
            bool placed = AppServicesSupport.WaitUntil(() => NearRect(app.MainHwnd, x, y, width, height, 3), DialogTimeout);
            E2EResults.Record("appsvc.place.moved", placed, $"window rectangle after MoveAndResize={AppServicesSupport.RectOf(app.MainHwnd)}.");

            // Real drag of the splitter to the right. The tree pane grows by the same number of DIPs.
            // The window must finish its first render before the splitter has real bounds to drag.
            _ = app.WaitFor(ListRow0, UiTimeout);
            _ = app.WaitFor("TreeRow_0", UiTimeout);
            Thread.Sleep(800);
            AutomationElement? splitter = app.WaitFor(SplitterId, UiTimeout);
            if (splitter is null)
            {
                E2EResults.Record("appsvc.split.found", false, "The splitter was not found.");
                Assert.Fail("The splitter was not found.");
            }

            var center = CenterOf(splitter.BoundingRectangle);
            app.Focus();
            int delta = int.Parse(TreeSplitDeltaPixels, System.Globalization.CultureInfo.InvariantCulture);
            AppServicesSupport.Drag(center.X, center.Y, center.X + delta, center.Y);
            expectedTree = ClassicTreePaneWidthDip + (delta * 96.0 / AppServicesSupport.DpiOf(app.MainHwnd));
            Thread.Sleep(400);

            app.Close();
            JsonObject saved = ReadSettings(settingsPath);
            bool windowSaved = NearValue(saved, "WindowX", x, 3) && NearValue(saved, "WindowY", y, 3)
                && NearValue(saved, "WindowWidth", width, 3) && NearValue(saved, "WindowHeight", height, 3);
            E2EResults.Record(
                "appsvc.place.saved",
                windowSaved,
                $"saved placement x={Get(saved, "WindowX")} y={Get(saved, "WindowY")} w={Get(saved, "WindowWidth")} h={Get(saved, "WindowHeight")}.");

            double savedTree = GetDouble(saved, "TreePaneWidth");
            E2EResults.Record(
                "appsvc.split.saved",
                Math.Abs(savedTree - expectedTree) <= 3,
                $"saved tree pane width={savedTree:0.#} DIP, expected about {expectedTree:0.#} DIP after the drag.");

            JsonObject columns = saved["ListColumnWidths"] as JsonObject ?? new JsonObject();
            bool columnsSaved = columns.ContainsKey("Name") && columns.ContainsKey("Type") && columns.ContainsKey("Data")
                && GetDouble(columns, "Name") > 0 && GetDouble(columns, "Type") > 0 && GetDouble(columns, "Data") > 0;
            E2EResults.Record(
                "appsvc.columns.saved",
                columnsSaved,
                $"saved column widths Name={Get(columns, "Name")} Type={Get(columns, "Type")} Data={Get(columns, "Data")}.");

            // Edit the saved Name width, then restart: the header must take the saved width. The value is 150 DIP, which
            // differs from the 120 DIP default and fits beside the tree pane at the test's window size. A header wider
            // than the list pane is clipped, so the check reads the visible width.
            columns["Name"] = RestoredNameDip;
            File.WriteAllText(settingsPath, saved.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }

        using (RegeditProcess app = RegeditProcess.Start(config, dataRoot, Array.Empty<string>()))
        {
            uint dpi = AppServicesSupport.DpiOf(app.MainHwnd);
            bool placed = NearRect(app.MainHwnd, x, y, width, height, 3);
            E2EResults.Record(
                "appsvc.place.restored",
                placed,
                $"window rectangle after restart={AppServicesSupport.RectOf(app.MainHwnd)}; expected {x},{y},{width},{height} physical pixels.");

            // The Name width is the distance from the Name title to the Type title. A title's own bounds are clipped
            // at the list pane edge when the column is wider than the pane, but the Type title keeps its layout position.
            AutomationElement? header = app.WaitFor("HeaderName", UiTimeout);
            AutomationElement? typeHeader = app.Find("HeaderType");
            double nameWidth = header is not null && typeHeader is not null
                ? typeHeader.BoundingRectangle.Left - header.BoundingRectangle.Left
                : 0;
            double expectedHeader = RestoredNameDip * dpi / 96.0;
            E2EResults.Record(
                "appsvc.columns.restored",
                header is not null && typeHeader is not null && Math.Abs(nameWidth - expectedHeader) <= Math.Max(4, dpi / 96.0 * 2),
                $"Name column width={nameWidth:0.#} px (Type title minus Name title), expected about {expectedHeader:0.#} px from the saved {RestoredNameDip} DIP.");
            app.Close();
        }

        FailIfNeeded(checkStart);
    }

    [TestMethod]
    [TestCategory("AppServices")]
    public void AppServices_OffScreenSavedPlacementIsFitted()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        if (!RequirePublish(config))
        {
            return;
        }

        string dataRoot = AppServicesSupport.FreshDataRoot(config, "offscreen");
        Directory.CreateDirectory(Path.GetDirectoryName(AppServicesPaths.SettingsFile(dataRoot))!);
        var seeded = new JsonObject
        {
            ["WindowX"] = 40000,
            ["WindowY"] = 40000,
            ["WindowWidth"] = 800,
            ["WindowHeight"] = 600,
        };
        File.WriteAllText(AppServicesPaths.SettingsFile(dataRoot), seeded.ToJsonString());

        using RegeditProcess app = RegeditProcess.Start(config, dataRoot, Array.Empty<string>());
        var work = AppServicesSupport.WorkArea();
        var rect = AppServicesSupport.RectOf(app.MainHwnd);
        const int slack = 2;
        bool inside = rect.Left >= work.Left - slack && rect.Top >= work.Top - slack
            && rect.Right <= work.Right + slack && rect.Bottom <= work.Bottom + slack;
        E2EResults.Record(
            "appsvc.offscreen.fitted",
            inside,
            $"window {rect.Left},{rect.Top},{rect.Right},{rect.Bottom} against work area {work.Left},{work.Top},{work.Right},{work.Bottom}.");
        E2EResults.Record(
            "appsvc.offscreen.size",
            Math.Abs((rect.Right - rect.Left) - 800) <= 2 && Math.Abs((rect.Bottom - rect.Top) - 600) <= 2,
            $"fitted size {rect.Right - rect.Left} by {rect.Bottom - rect.Top} physical pixels; saved size 800 by 600.");
        app.Close();

        FailIfNeeded(checkStart);
    }

    [TestMethod]
    [TestCategory("AppServices")]
    public void AppServices_SecondLaunchActivatesFirstWindowAndMultipleSwitchOpensAnother()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        if (!RequirePublish(config))
        {
            return;
        }

        string dataRoot = AppServicesSupport.FreshDataRoot(config, "single");
        int countBefore = Process.GetProcessesByName("BwcRegedit").Length;
        using RegeditProcess first = RegeditProcess.Start(config, dataRoot, Array.Empty<string>());
        AppServicesSupport.Minimize(first.MainHwnd);
        bool minimized = AppServicesSupport.WaitUntil(() => AppServicesSupport.IsMinimized(first.MainHwnd), UiTimeout);
        E2EResults.Record("appsvc.single.minimized", minimized, "The first window was minimized before the second launch.");

        using Process second = RegeditProcess.StartProcess(config, dataRoot, Array.Empty<string>());
        bool sawWindow = false;
        DateTime deadline = DateTime.UtcNow + UiTimeout;
        while (!second.HasExited && DateTime.UtcNow < deadline)
        {
            second.Refresh();
            if (second.MainWindowHandle != IntPtr.Zero)
            {
                sawWindow = true;
            }

            Thread.Sleep(50);
        }

        bool exited = second.HasExited;
        int exitCode = exited ? second.ExitCode : -1;
        E2EResults.Record(
            "appsvc.single.secondExit",
            exited && exitCode == 0,
            $"second launch exited={exited} with code {exitCode}.");
        E2EResults.Record(
            "appsvc.single.noSecondWindow",
            exited && !sawWindow,
            $"second launch showed its own window={sawWindow}.");

        bool restored = AppServicesSupport.WaitUntil(() => !AppServicesSupport.IsMinimized(first.MainHwnd), TimeSpan.FromSeconds(5));
        E2EResults.Record(
            "appsvc.single.activated",
            restored && AppServicesSupport.IsWindow(first.MainHwnd),
            $"first window restored from minimized by the second launch={restored}.");
        // The second process must be gone and the first must still be running.
        // HasExited is the authority for the second process. The process list can still show a process for a
        // moment after it exits, so the list is read until it shows the first instance alone, or the timeout passes.
        bool secondGone = second.HasExited;
        bool listSettled = AppServicesSupport.WaitUntil(
            () => Process.GetProcessesByName("BwcRegedit").Length == countBefore + 1,
            TimeSpan.FromSeconds(5));
        Process[] running = Process.GetProcessesByName("BwcRegedit");
        bool firstAlive = running.Any(p => p.Id == first.ProcessId);
        E2EResults.Record(
            "appsvc.single.oneProcess",
            firstAlive && secondGone && listSettled,
            $"first pid {first.ProcessId} running={firstAlive}; second pid {second.Id} exited={secondGone}; BwcRegedit processes={running.Length} (expected {countBefore + 1}: started with {countBefore} before the first launch).");

        using RegeditProcess multiple = RegeditProcess.Start(config, dataRoot, new[] { "/m" });
        bool separate = multiple.ProcessId != first.ProcessId && multiple.MainHwnd != first.MainHwnd
            && AppServicesSupport.IsWindow(first.MainHwnd) && AppServicesSupport.IsWindow(multiple.MainHwnd);
        E2EResults.Record(
            "appsvc.multi.window",
            separate,
            $"/m launch pid={multiple.ProcessId} hwnd=0x{multiple.MainHwnd:X}, first pid={first.ProcessId} hwnd=0x{first.MainHwnd:X}.");

        multiple.Close();
        first.Close();
        FailIfNeeded(checkStart);
    }

    [TestMethod]
    [TestCategory("AppServices")]
    [TestCategory("Mutating")]
    public void AppServices_RegFileArgumentAsksBeforeImport_AnsweredNo()
    {
        int checkStart = E2EResults.CheckCount;
        RegFileImport(answerYes: false, checkPrefix: "appsvc.reg.no");
    }

    [TestMethod]
    [TestCategory("AppServices")]
    [TestCategory("Mutating")]
    public void AppServices_RegFileArgumentAsksBeforeImport_AnsweredYes()
    {
        int checkStart = E2EResults.CheckCount;
        RegFileImport(answerYes: true, checkPrefix: "appsvc.reg.yes");
    }

    [TestMethod]
    [TestCategory("AppServices")]
    public void AppServices_AboutShowsProductVersionAndCopyright()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        if (!RequirePublish(config))
        {
            return;
        }

        string dataRoot = AppServicesSupport.FreshDataRoot(config, "about");
        using RegeditProcess app = RegeditProcess.Start(config, dataRoot, Array.Empty<string>());
        app.InvokeMenu(MenuHelp, CommandAbout);
        // ShellAbout titles its window "About " plus the application name.
        IntPtr dialog = AppServicesSupport.WaitForOwnedWindow(app.MainHwnd, NativeDialogClass, "About " + Caption, DialogTimeout);
        E2EResults.Record("appsvc.about.opened", dialog != IntPtr.Zero, $"About box found={dialog != IntPtr.Zero}.");
        if (dialog == IntPtr.Zero)
        {
            Assert.Fail("Help > About did not open the About box.");
        }

        string text = string.Join("\n", AppServicesSupport.ChildTexts(dialog));
        string version = ExpectedVersion(config);
        E2EResults.Record(
            "appsvc.about.version",
            version.Length > 0 && text.Contains("Version " + version, StringComparison.Ordinal),
            $"About text names version '{version}': {text.Replace("\n", " | ", StringComparison.Ordinal)}.");
        E2EResults.Record(
            "appsvc.about.product",
            text.Contains("Better Windows Config", StringComparison.Ordinal),
            "About text names the product Better Windows Config.");
        E2EResults.Record(
            "appsvc.about.copyright",
            text.Contains("Copyright", StringComparison.Ordinal),
            "About text carries the copyright line.");

        AppServicesSupport.ForceForeground(dialog);
        Keyboard.Press(VirtualKeyShort.ENTER);
        bool closed = AppServicesSupport.WaitUntil(() => !AppServicesSupport.IsWindow(dialog), DialogTimeout);
        E2EResults.Record("appsvc.about.closed", closed, "The About box closed on Enter.");
        app.Close();

        FailIfNeeded(checkStart);
    }

    [TestMethod]
    [TestCategory("AppServices")]
    public void AppServices_ThemeSubmenuSwitchesPaletteAndPersists()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        if (!RequirePublish(config))
        {
            return;
        }

        string dataRoot = AppServicesSupport.FreshDataRoot(config, "theme");
        using (RegeditProcess app = RegeditProcess.Start(config, dataRoot, Array.Empty<string>()))
        {
            app.InvokeMenu(MenuView, CommandTheme, CommandThemeLight);
            Thread.Sleep(500);
            (int lr, int lg, int lb) = SampleBackground(app);
            bool light = lr > 200 && lg > 200 && lb > 200;
            E2EResults.Record("appsvc.theme.light", light, $"window background sampled as ({lr},{lg},{lb}) with View > Theme > Light.");

            app.InvokeMenu(MenuView, CommandTheme, CommandThemeDark);
            Thread.Sleep(500);
            (int dr, int dg, int db) = SampleBackground(app);
            bool dark = dr < 60 && dg < 60 && db < 60;
            E2EResults.Record("appsvc.theme.dark", dark, $"window background sampled as ({dr},{dg},{db}) with View > Theme > Dark.");
            app.Close();
        }

        using (RegeditProcess app = RegeditProcess.Start(config, dataRoot, Array.Empty<string>()))
        {
            Thread.Sleep(500);
            (int r, int g, int b) = SampleBackground(app);
            bool persisted = r < 60 && g < 60 && b < 60;
            E2EResults.Record("appsvc.theme.persisted", persisted, $"after restart the background is ({r},{g},{b}); expected the Dark palette.");
            app.Close();
        }

        FailIfNeeded(checkStart);
    }

    [TestMethod]
    [TestCategory("AppServices")]
    public void AppServices_HiveRulesEnableLoadAndUnloadByRoot()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        if (!RequirePublish(config))
        {
            return;
        }

        string dataRoot = AppServicesSupport.FreshDataRoot(config, "hiverules");
        using RegeditProcess app = RegeditProcess.Start(config, dataRoot, Array.Empty<string>());

        // Load Hive opens only at the HKEY_LOCAL_MACHINE or HKEY_USERS root. Unload Hive never opens at a
        // root or at a boot hive. No hive is loaded here, so no privilege is needed.
        var cases = new (string Path, string Label, bool Load, bool Unload)[]
        {
            (@"HKEY_LOCAL_MACHINE", "HKLM root", true, false),
            (@"HKEY_USERS", "HKU root", true, false),
            (@"HKEY_CURRENT_USER", "HKCU root", false, false),
            (@"HKEY_CURRENT_USER\Software", "HKCU subkey", false, false),
            (@"HKEY_LOCAL_MACHINE\SOFTWARE", "HKLM boot hive", false, false),
        };

        foreach (var item in cases)
        {
            Navigate(app, item.Path);
            app.InvokeMenu(MenuFile);
            AutomationElement? load = app.WaitFor("MenuCommand_" + CommandLoadHive, UiTimeout);
            AutomationElement? unload = app.WaitFor("MenuCommand_" + CommandUnloadHive, UiTimeout);
            bool loadEnabled = load?.IsEnabled ?? false;
            bool unloadEnabled = unload?.IsEnabled ?? false;
            CloseMenus();

            E2EResults.Record(
                "appsvc.hive.load." + Slug(item.Label),
                loadEnabled == item.Load,
                $"{item.Label} ({item.Path}): Load Hive enabled={loadEnabled}, expected {item.Load}.");
            E2EResults.Record(
                "appsvc.hive.unload." + Slug(item.Label),
                unloadEnabled == item.Unload,
                $"{item.Label} ({item.Path}): Unload Hive enabled={unloadEnabled}, expected {item.Unload}.");
        }

        app.Close();
        FailIfNeeded(checkStart);
    }

    private static bool RequirePublish(E2EConfig config)
    {
        try
        {
            config.EnsureExecutablesExist();
            return true;
        }
        catch (Exception ex)
        {
            E2EResults.Record("appsvc.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return false;
        }
    }

    /// <summary>Fails the test when a check recorded since <paramref name="startIndex"/> did not pass.</summary>
    private static void FailIfNeeded(int startIndex)
    {
        IReadOnlyList<string> failures = E2EResults.FailedChecksSince(startIndex);
        if (failures.Count > 0)
        {
            Assert.Fail($"Checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
        }
    }

    /// <summary>Types a key path into the address bar and presses Enter, then waits for the address to settle.</summary>
    private static void Navigate(RegeditProcess app, string path)
    {
        app.Focus();
        AutomationElement? address = app.WaitFor("AddressBar", UiTimeout);
        if (address is null)
        {
            throw new InvalidOperationException("The address bar was not found.");
        }

        address.Focus();
        address.AsTextBox().Text = path;
        Keyboard.Press(VirtualKeyShort.ENTER);
        _ = AppServicesSupport.WaitUntil(() => AddressText(app) == path, UiTimeout);
        Thread.Sleep(300);
    }

    private static string AddressText(RegeditProcess app)
    {
        AutomationElement? address = app.Find("AddressBar");
        if (address is null)
        {
            return string.Empty;
        }

        try
        {
            return address.Patterns.Value.IsSupported ? address.Patterns.Value.Pattern.Value.Value ?? string.Empty : address.Name ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static void CloseMenus()
    {
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Keyboard.Press(VirtualKeyShort.ESCAPE);
    }

    private static double WaitForRowHeight(RegeditProcess app)
    {
        AutomationElement? row = app.WaitFor(ListRow0, UiTimeout);
        return row is null ? 0 : ControlTestSupport.HeightOf(row);
    }

    /// <summary>Physical row height at a font size in DIPs, the same rounding DenseMetrics applies.</summary>
    private static double ExpectedRowPixels(double baseRowDip, double fontDip, uint dpi)
    {
        double dip = Math.Max(1, Math.Round(baseRowDip * fontDip / 12.0, MidpointRounding.AwayFromZero));
        return dip * dpi / 96.0;
    }

    private static (int X, int Y) CenterOf(System.Drawing.Rectangle rectangle) =>
        ((int)(rectangle.Left + (rectangle.Width / 2)), (int)(rectangle.Top + (rectangle.Height / 2)));

    private static double Tolerance(uint dpi) => Math.Max(2, dpi / 96.0 * 1.5);

    private static bool NearRect(IntPtr hwnd, int x, int y, int width, int height, int slack)
    {
        var rect = AppServicesSupport.RectOf(hwnd);
        return Math.Abs(rect.Left - x) <= slack && Math.Abs(rect.Top - y) <= slack
            && Math.Abs((rect.Right - rect.Left) - width) <= slack && Math.Abs((rect.Bottom - rect.Top) - height) <= slack;
    }

    private static JsonObject ReadSettings(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonObject();
        }

        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
    }

    private static bool NearValue(JsonObject obj, string key, double expected, double slack) =>
        obj[key] is not null && Math.Abs(GetDouble(obj, key) - expected) <= slack;

    private static double GetDouble(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue(out double number) ? number : double.NaN;

    private static string Get(JsonObject obj, string key) => obj[key]?.ToJsonString() ?? "(missing)";

    /// <summary>
    /// Reads the first log file. The running app can hold the file for a moment while it writes, which raises a
    /// sharing violation, so the read is retried for a short bounded time. The content checked is unchanged.
    /// </summary>
    private static string ReadFirstLog(string logDir)
    {
        string? file = Directory.EnumerateFiles(logDir, "log-*.txt").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
        if (file is null)
        {
            return string.Empty;
        }

        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            try
            {
                return File.ReadAllText(file);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>The product version from the built executable, without build metadata.</summary>
    private static string ExpectedVersion(E2EConfig config)
    {
        string? product = FileVersionInfo.GetVersionInfo(config.RegeditExePath).ProductVersion;
        if (string.IsNullOrWhiteSpace(product))
        {
            return string.Empty;
        }

        int plus = product.IndexOf('+', StringComparison.Ordinal);
        return (plus < 0 ? product : product[..plus]).Trim();
    }

    /// <summary>The window background, sampled from a blank part of the list pane.</summary>
    private static (int R, int G, int B) SampleBackground(RegeditProcess app)
    {
        app.Focus();
        var rect = app.Window.BoundingRectangle;
        using CaptureImage image = Capture.Element(app.Window);
        int x = (int)(rect.Width * 0.75);
        int y = (int)(rect.Height - 40);
        System.Drawing.Color color = image.Bitmap.GetPixel(Math.Clamp(x, 0, image.Bitmap.Width - 1), Math.Clamp(y, 0, image.Bitmap.Height - 1));
        return (color.R, color.G, color.B);
    }

    private static string Slug(string label) =>
        string.Concat(label.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant));

    private static void RegFileImport(bool answerYes, string checkPrefix)
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        if (!RequirePublish(config))
        {
            return;
        }

        E2EGuards.RequireMutatingFlavor(config);
        var sandbox = new SandboxRegistry(config);
        string folder = Path.Combine(config.ArtifactsDir, "appservices", config.RunId, answerYes ? "yes" : "no");
        string file = Path.Combine(folder, "import.reg");
        string dataRoot = AppServicesSupport.FreshDataRoot(config, answerYes ? "regyes" : "regno");
        const string target = @"Import\Target";

        try
        {
            sandbox.Cleanup();
            Directory.CreateDirectory(folder);
            string text = "Windows Registry Editor Version 5.00\r\n\r\n"
                + "[HKEY_CURRENT_USER\\" + sandbox.RunRootPath + "\\Import\\Target]\r\n"
                + "\"Value\"=\"imported\"\r\n";
            byte[] body = Encoding.Unicode.GetBytes(text);
            File.WriteAllBytes(file, Encoding.Unicode.GetPreamble().Concat(body).ToArray());
            E2EGuards.RequireImportFileInsideSandbox(file, sandbox, checkPrefix + ".target");

            // The parent of the target is created first. The importer's handling of missing ancestors is
            // checked separately, so this test is about the confirmation and the import result.
            sandbox.CreateKey("Import");
            bool absentBefore = !sandbox.Exists(target);
            E2EResults.Record(checkPrefix + ".absentBefore", absentBefore, "The import target does not exist before the launch.");

            // The import writes only under the sandbox root, which the app reads from this variable.
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };
            using RegeditProcess app = RegeditProcess.Start(config, dataRoot, new[] { file }, environment: environment);
            IntPtr confirm = AppServicesSupport.WaitForOwnedWindow(app.MainHwnd, null, Caption, DialogTimeout);
            E2EResults.Record(checkPrefix + ".confirm.shown", confirm != IntPtr.Zero, $"confirmation window found={confirm != IntPtr.Zero}.");
            if (confirm == IntPtr.Zero)
            {
                Assert.Fail("regedit file.reg did not ask for confirmation.");
            }

            string confirmText = AppServicesSupport.WaitForDialogText(
                confirm,
                text => text.Contains("Adding information", StringComparison.Ordinal) && text.Contains(file, StringComparison.Ordinal),
                DialogTimeout);
            bool textOk = confirmText.Contains("Adding information", StringComparison.Ordinal) && confirmText.Contains(file, StringComparison.Ordinal);
            E2EResults.Record(checkPrefix + ".confirm.text", textOk, "The confirmation carries the RT_STRING 304 text and the file path.");

            AppServicesSupport.ClickButton(app, confirm, answerYes ? "Yes" : "No");
            bool closed = AppServicesSupport.WaitUntil(() => !AppServicesSupport.IsWindow(confirm), DialogTimeout);
            E2EResults.Record(checkPrefix + ".confirm.closed", closed, "The confirmation closed after the answer.");

            if (answerYes)
            {
                IntPtr result = AppServicesSupport.WaitForOwnedWindow(app.MainHwnd, null, Caption, DialogTimeout, confirm);
                string resultText = result == IntPtr.Zero
                    ? string.Empty
                    : AppServicesSupport.WaitForDialogText(
                        result,
                        text => text.Contains("successfully added", StringComparison.Ordinal),
                        DialogTimeout);
                E2EResults.Record(
                    checkPrefix + ".result.shown",
                    resultText.Contains("successfully added", StringComparison.Ordinal),
                    "The success message (RT_STRING 228) appeared after Yes.");
                if (result != IntPtr.Zero)
                {
                    AppServicesSupport.ClickButton(app, result, "OK");
                }
            }
            else
            {
                Thread.Sleep(1500);
                IntPtr result = AppServicesSupport.FindOwnedWindow(app.MainHwnd, null, Caption);
                E2EResults.Record(checkPrefix + ".result.none", result == IntPtr.Zero, "No success message appeared after No.");
            }

            bool present = sandbox.Exists(target);
            string? value = present ? sandbox.ReadString(target, "Value") : null;
            bool imported = present && value == "imported";
            E2EResults.Record(
                checkPrefix + ".registry",
                answerYes ? imported : !present,
                answerYes
                    ? $"import target inside the sandbox: exists={present}, Value='{value}'."
                    : $"import target inside the sandbox after No: exists={present}.");

            app.Close();
        }
        finally
        {
            sandbox.Cleanup();
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                E2EResults.Record(checkPrefix + ".cleanup", false, ex.Message);
            }
        }

        FailIfNeeded(checkStart);
    }

}

using System.Globalization;
using System.Text;
using Bwc.E2E.AppServices;
using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.ImportExport;

/// <summary>
/// Drives the real Export save dialog and the import progress dialog. The Export range panel is a
/// child of the system IFileSaveDialog and is reached with real mouse clicks. The export branches
/// and formats are written under artifacts/e2e/import-export/&lt;runId&gt;, and the progress tests
/// cancel through the dialog button with a real click.
/// </summary>
[TestClass]
public sealed class ExportRangeTests
{
    private const int MenuFile = 512;
    private const int MenuFileImport = 658;
    private const int MenuFileExport = 659;
    private const string ExportValueName = "ExportString";
    private const string ExportValueData = "exported";

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("ImportExport")]
    [TestCategory("Mutating")]
    public void ExportDialogs_RangePanelWritesAllBranchTextAndRegedit4()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        RequireExecutables(config);

        var sandbox = new SandboxRegistry(config);
        string tempDir = Path.Combine(config.ArtifactsDir, "import-export", config.RunId, "range");
        string visualDir = Path.Combine(config.ArtifactsDir, "visual", "importexport");
        string v5File = Path.Combine(tempDir, "branch-v5.reg");
        string textFile = Path.Combine(tempDir, "branch.txt");
        string regedit4File = Path.Combine(tempDir, "branch-regedit4.reg");
        string allFile = Path.Combine(tempDir, "all.reg");

        try
        {
            sandbox.Cleanup();
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(visualDir);
            Seed(sandbox);

            string dataRoot = CreateFreshDataRoot(config, "importexport-range");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);
            using var dialogs = new SystemFileDialogDriver(session);
            using var automation = new UIA3Automation();

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            bool seeded = ControlTestSupport.WaitUntil(
                () => ControlTestSupport.ListRowNames(session).Contains("AlphaString", StringComparer.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(15));
            E2EResults.Record(
                "rangerange.navigate",
                navigated && seeded,
                $"navigated={navigated}, rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}].");
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated && seeded, "rangerange.sandbox.navigate");

            CheckRangePanelAndBranchExport(
                session,
                driver,
                dialogs,
                automation,
                sandbox,
                v5File,
                visualDir);
            CheckTextExport(session, driver, dialogs, automation, sandbox, textFile);
            CheckRegedit4Export(session, driver, dialogs, automation, sandbox, regedit4File);
            CheckWholeRegistryExport(session, driver, dialogs, automation, sandbox, allFile, visualDir);

            FailIfNeeded(checkStart);
        }
        finally
        {
            sandbox.Cleanup();
        }
    }

    [TestMethod]
    [TestCategory("ImportExport")]
    [TestCategory("Mutating")]
    public void ImportProgress_CancelKeepsCompletedWritesAndShowsStringOneThirtyTwo()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        RequireExecutables(config);

        var sandbox = new SandboxRegistry(config);
        string tempDir = Path.Combine(config.ArtifactsDir, "import-export", config.RunId, "import-cancel");
        string visualDir = Path.Combine(config.ArtifactsDir, "visual", "importexport");

        try
        {
            sandbox.Cleanup();
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(visualDir);
            Seed(sandbox);
            string importFile = WriteLargeImportFile(sandbox, tempDir, sections: 20000);

            string dataRoot = CreateFreshDataRoot(config, "importexport-import-cancel");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);
            using var dialogs = new SystemFileDialogDriver(session);
            using var automation = new UIA3Automation();

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "importcancel.sandbox.navigate");
            E2EGuards.RequireImportFileInsideSandbox(importFile, sandbox, "importcancel.file-inside");

            if (!OpenDialog(driver, dialogs, MenuFileImport, "importcancel.import", out IntPtr dialog))
            {
                FailIfNeeded(checkStart);
                return;
            }

            bool typed = dialogs.TrySetFileName(dialog, importFile, out string typeNotes);
            string acceptNotes = "not attempted";
            bool accepted = typed && dialogs.TryAccept(dialog, "Open", out acceptNotes);
            bool closed = accepted && dialogs.WaitGone(dialog, TimeSpan.FromSeconds(10));
            E2EResults.Record(
                "importcancel.open",
                typed && accepted && closed,
                $"{typeNotes} {acceptNotes} closed={closed}.");

            IntPtr progress = ImportExportWindows.WaitForProgress(automation, session.MainHwnd, TimeSpan.FromSeconds(15));
            bool progressShown = E2EResults.Record(
                "importcancel.progress",
                progress != IntPtr.Zero,
                $"progress hwnd=0x{progress.ToInt64():X}.");

            bool advanced = progressShown && WaitForImportProgress(automation, progress, TimeSpan.FromSeconds(10));
            E2EResults.Record(
                "importcancel.progress-advanced",
                advanced,
                advanced ? "at least one key section finished before Cancel." : "no section finished before Cancel.");

            if (progressShown)
            {
                string shot = ImportExportWindows.CaptureWindow(
                    progress,
                    Path.Combine(visualDir, "import-progress.png"));
                E2EResults.Note("importcancel.screenshot", $"saved {shot}, written={File.Exists(shot)}.");
            }

            bool cancelClicked = progressShown && ImportExportWindows.ClickCancel(automation, progress);
            bool progressClosed = cancelClicked &&
                ControlTestSupport.WaitUntil(() => !Win32.IsWindow(progress), TimeSpan.FromSeconds(10));
            E2EResults.Record(
                "importcancel.cancel",
                cancelClicked && progressClosed,
                $"cancelClicked={cancelClicked}, progressClosed={progressClosed}.");

            IntPtr result = ImportExportWindows.WaitForResult(
                session.MainHwnd,
                text => text.Contains("Not all data was successfully written", StringComparison.Ordinal),
                TimeSpan.FromSeconds(15));
            string resultText = result == IntPtr.Zero ? string.Empty : AppServicesSupport.DialogText(result);
            E2EResults.Record(
                "importcancel.partial-message",
                result != IntPtr.Zero,
                result == IntPtr.Zero ? "no partial result message." : resultText);
            if (result != IntPtr.Zero)
            {
                _ = AppServicesSupport.DismissMessage(result, TimeSpan.FromSeconds(10));
            }

            bool firstPresent = sandbox.Exists(@"ImportCancel\Key00001");
            bool lastAbsent = !sandbox.Exists(@"ImportCancel\Key19999");
            E2EResults.Record(
                "importcancel.partial-writes",
                firstPresent && lastAbsent,
                $"Key00001 exists={firstPresent}, Key19999 exists={!lastAbsent}.");

            bool responsive = ControlTestSupport.WaitUntil(
                () => Win32.IsWindowEnabled(session.MainHwnd),
                TimeSpan.FromSeconds(10));
            E2EResults.Record("importcancel.responsive", responsive, $"main enabled={responsive}.");

            FailIfNeeded(checkStart);
        }
        finally
        {
            sandbox.Cleanup();
        }
    }

    [TestMethod]
    [TestCategory("ImportExport")]
    [TestCategory("Mutating")]
    public void ExportProgress_CancelDeletesThePartialFileAndWritesNothing()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        RequireExecutables(config);

        var sandbox = new SandboxRegistry(config);
        string tempDir = Path.Combine(config.ArtifactsDir, "import-export", config.RunId, "export-cancel");
        string visualDir = Path.Combine(config.ArtifactsDir, "visual", "importexport");
        string destination = Path.Combine(tempDir, "cancel-export.reg");

        try
        {
            sandbox.Cleanup();
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(visualDir);
            SeedLargeBranch(sandbox, keyCount: 20000);

            string dataRoot = CreateFreshDataRoot(config, "importexport-export-cancel");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);
            using var dialogs = new SystemFileDialogDriver(session);
            using var automation = new UIA3Automation();

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config, childKey: "CancelExport");
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "exportcancel.sandbox.navigate");

            if (!OpenDialog(driver, dialogs, MenuFileExport, "exportcancel.export", out IntPtr dialog))
            {
                FailIfNeeded(checkStart);
                return;
            }

            bool typed = dialogs.TrySetFileName(dialog, destination, out string typeNotes);
            string acceptNotes = "not attempted";
            bool accepted = typed && dialogs.TryAccept(dialog, "Save", out acceptNotes);
            bool closed = accepted && dialogs.WaitGone(dialog, TimeSpan.FromSeconds(10));
            E2EResults.Record(
                "exportcancel.save-dialog",
                typed && accepted && closed,
                $"{typeNotes} {acceptNotes} closed={closed}.");

            IntPtr progress = ImportExportWindows.WaitForProgress(automation, session.MainHwnd, TimeSpan.FromSeconds(15));
            bool progressShown = E2EResults.Record(
                "exportcancel.progress",
                progress != IntPtr.Zero,
                $"progress hwnd=0x{progress.ToInt64():X}.");

            bool advanced = progressShown && WaitForExportProgress(automation, progress, TimeSpan.FromSeconds(15));
            E2EResults.Record(
                "exportcancel.progress-advanced",
                advanced,
                advanced ? "at least one key was exported before Cancel." : "no key was exported before Cancel.");

            if (progressShown)
            {
                string shot = ImportExportWindows.CaptureWindow(
                    progress,
                    Path.Combine(visualDir, "export-progress.png"));
                E2EResults.Note("exportcancel.screenshot", $"saved {shot}, written={File.Exists(shot)}.");
            }

            bool cancelClicked = progressShown && ImportExportWindows.ClickCancel(automation, progress);
            bool progressClosed = cancelClicked &&
                ControlTestSupport.WaitUntil(() => !Win32.IsWindow(progress), TimeSpan.FromSeconds(10));
            E2EResults.Record(
                "exportcancel.cancel",
                cancelClicked && progressClosed,
                $"cancelClicked={cancelClicked}, progressClosed={progressClosed}.");

            // The worker observes the token before the move. The partial file was in the destination
            // folder, so a small grace period lets the worker delete it and exit.
            bool settled = ControlTestSupport.WaitUntil(
                () => !File.Exists(destination) &&
                    Directory.GetFiles(tempDir, "*.tmp", SearchOption.TopDirectoryOnly).Length == 0,
                TimeSpan.FromSeconds(15));
            bool destinationAbsent = !File.Exists(destination);
            string[] leftovers = Directory.GetFiles(tempDir, "*.tmp", SearchOption.TopDirectoryOnly);
            E2EResults.Record(
                "exportcancel.no-file",
                settled && destinationAbsent && leftovers.Length == 0,
                $"destination exists={!destinationAbsent}, temp files=[{string.Join(",", leftovers)}], settled={settled}.");

            bool responsive = ControlTestSupport.WaitUntil(
                () => Win32.IsWindowEnabled(session.MainHwnd),
                TimeSpan.FromSeconds(10));
            E2EResults.Record("exportcancel.responsive", responsive, $"main enabled={responsive}.");

            FailIfNeeded(checkStart);
        }
        finally
        {
            sandbox.Cleanup();
        }
    }

    private static void CheckRangePanelAndBranchExport(
        AppSession session,
        EditingDriver driver,
        SystemFileDialogDriver dialogs,
        UIA3Automation automation,
        SandboxRegistry sandbox,
        string exportFile,
        string visualDir)
    {
        E2EGuards.RequireSandboxSelection(session, sandbox, "range.sandbox.export");
        if (!OpenDialog(driver, dialogs, MenuFileExport, "range.export", out IntPtr dialog))
        {
            return;
        }

        bool owned = Win32.GetWindow(dialog, Win32.GvOwner) == session.MainHwnd;
        bool disabled = !Win32.IsWindowEnabled(session.MainHwnd);
        E2EResults.Record(
            "range.dialog",
            owned && disabled,
            $"owner=0x{Win32.GetWindow(dialog, Win32.GvOwner).ToInt64():X}, main=0x{session.MainHwnd.ToInt64():X}, disabled={disabled}.");

        AutomationElement? group = dialogs.FindNamed(dialog, "Export range", ControlType.Group)
            ?? dialogs.FindNamed(dialog, "Export range");
        AutomationElement? allRadio = dialogs.FindRadio(dialog, "All");
        AutomationElement? branchRadio = dialogs.FindRadio(dialog, "Selected branch");
        bool radiosPresent = ControlTestSupport.WaitUntil(
            () => dialogs.ReadRadio(dialogs.FindRadio(dialog, "All")).Present &&
                dialogs.ReadRadio(dialogs.FindRadio(dialog, "Selected branch")).Present,
            TimeSpan.FromSeconds(5));
        E2EResults.Record(
            "range.panel",
            radiosPresent,
            group is null
                ? "Export range radios present; the visual group label is drawn by the shell and is not a separate UIA element."
                : $"Export range group found as {group.ControlType}; radios present={radiosPresent}.");

        allRadio = dialogs.FindRadio(dialog, "All");
        branchRadio = dialogs.FindRadio(dialog, "Selected branch");
        RadioState allDefault = dialogs.ReadRadio(allRadio);
        RadioState branchDefault = dialogs.ReadRadio(branchRadio);
        E2EResults.Record(
            "range.defaults",
            radiosPresent && allDefault.Present && allDefault.Enabled && !allDefault.Selected &&
                branchDefault.Present && branchDefault.Enabled && branchDefault.Selected,
            $"All present={allDefault.Present} enabled={allDefault.Enabled} selected={allDefault.Selected}; " +
            $"Selected branch present={branchDefault.Present} enabled={branchDefault.Enabled} selected={branchDefault.Selected}.");

        string shot = dialogs.SaveScreenshot(dialog, Path.Combine(visualDir, "save-dialog-range-panel.png"));
        E2EResults.Note("range.screenshot", $"saved {shot}, written={File.Exists(shot)}.");

        bool allClicked = dialogs.ClickRadio(dialog, allRadio);
        bool allSelected = allClicked && WaitForRadio(dialogs, dialog, "All", selected: true);
        RadioState branchAfterAll = dialogs.ReadRadio(dialogs.FindRadio(dialog, "Selected branch"));
        E2EResults.Record(
            "range.all-selectable",
            allSelected && !branchAfterAll.Selected,
            $"clicked={allClicked}, All selected={allSelected}, branch selected={branchAfterAll.Selected}.");

        bool branchClicked = dialogs.ClickRadio(dialog, branchRadio);
        bool branchSelected = branchClicked && WaitForRadio(dialogs, dialog, "Selected branch", selected: true);
        RadioState allAfterBranch = dialogs.ReadRadio(dialogs.FindRadio(dialog, "All"));
        E2EResults.Record(
            "range.branch-selectable",
            branchSelected && !allAfterBranch.Selected,
            $"clicked={branchClicked}, branch selected={branchSelected}, All selected={allAfterBranch.Selected}.");

        string fileType = dialogs.ReadFileType(dialog);
        E2EResults.Record(
            "range.file-type-default",
            fileType.Contains("Registration Files", StringComparison.OrdinalIgnoreCase),
            $"file type='{fileType}'.");

        bool typed = dialogs.TrySetFileName(dialog, exportFile, out string typeNotes);
        string acceptNotes = "not attempted";
        bool accepted = typed && dialogs.TryAccept(dialog, "Save", out acceptNotes);
        bool closed = accepted && dialogs.WaitGone(dialog, TimeSpan.FromSeconds(10));
        E2EResults.Record(
            "range.save",
            typed && accepted && closed,
            $"{typeNotes} {acceptNotes} closed={closed}.");
        if (!closed)
        {
            _ = dialogs.TryCancel(dialog);
        }

        byte[]? bytes = WaitForStableFile(exportFile, TimeSpan.FromSeconds(20));
        string text = DecodeV5(bytes);
        bool fileOk = bytes is not null &&
            bytes.Length > 2 &&
            bytes[0] == 0xFF &&
            bytes[1] == 0xFE &&
            text.StartsWith("Windows Registry Editor Version 5.00\r\n\r\n", StringComparison.Ordinal) &&
            text.Contains("[HKEY_CURRENT_USER\\" + sandbox.RunRootPath + "]\r\n", StringComparison.OrdinalIgnoreCase) &&
            text.Contains("\"" + ExportValueName + "\"=\"" + ExportValueData + "\"\r\n", StringComparison.Ordinal);
        E2EResults.Record(
            "range.v5",
            fileOk,
            $"bytes={bytes?.Length ?? 0}, file='{exportFile}'.");

        bool idle = WaitForExportIdle(session, automation, TimeSpan.FromSeconds(15));
        bool noSuccessMessage = ImportExportWindows.WaitForResult(
            session.MainHwnd,
            message => message.Contains("successfully", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(1)) == IntPtr.Zero;
        E2EResults.Record(
            "range.success-message",
            idle && noSuccessMessage,
            $"idle={idle}, noSuccessMessage={noSuccessMessage} (classic regedit has no export success text).");
    }

    private static void CheckTextExport(
        AppSession session,
        EditingDriver driver,
        SystemFileDialogDriver dialogs,
        UIA3Automation automation,
        SandboxRegistry sandbox,
        string exportFile)
    {
        E2EGuards.RequireSandboxSelection(session, sandbox, "text.sandbox.export");
        if (!OpenDialog(driver, dialogs, MenuFileExport, "text.export", out IntPtr dialog))
        {
            return;
        }

        bool selected = dialogs.SelectFileType(dialog, index: 3, "Text Files", out string typeNotes);
        E2EResults.Record("text.file-type", selected, typeNotes);

        bool typed = dialogs.TrySetFileName(dialog, exportFile, out string notes);
        string acceptNotes = "not attempted";
        bool accepted = typed && dialogs.TryAccept(dialog, "Save", out acceptNotes);
        bool closed = accepted && dialogs.WaitGone(dialog, TimeSpan.FromSeconds(10));
        E2EResults.Record(
            "text.save",
            typed && accepted && closed,
            $"{notes} {acceptNotes} closed={closed}.");
        if (!closed)
        {
            _ = dialogs.TryCancel(dialog);
        }

        byte[]? bytes = WaitForStableFile(exportFile, TimeSpan.FromSeconds(20));
        string text = DecodeV5(bytes);
        bool bom = bytes is { Length: > 2 } && bytes[0] == 0xFF && bytes[1] == 0xFE;
        bool key = text.Contains("Key Name:          HKEY_CURRENT_USER\\" + sandbox.RunRootPath, StringComparison.OrdinalIgnoreCase);
        bool value = text.Contains("Name:", StringComparison.Ordinal) &&
            text.Contains(ExportValueName, StringComparison.Ordinal) &&
            text.Contains("Data:", StringComparison.Ordinal) &&
            text.Contains(ExportValueData, StringComparison.Ordinal);
        bool notRegHeader = !text.StartsWith("Windows Registry Editor Version 5.00", StringComparison.Ordinal);
        E2EResults.Record(
            "text.content",
            bom && key && value && notRegHeader,
            $"bom={bom}, key={key}, value={value}, notRegHeader={notRegHeader}, bytes={bytes?.Length ?? 0}.");

        _ = WaitForExportIdle(session, automation, TimeSpan.FromSeconds(15));
    }

    private static void CheckRegedit4Export(
        AppSession session,
        EditingDriver driver,
        SystemFileDialogDriver dialogs,
        UIA3Automation automation,
        SandboxRegistry sandbox,
        string exportFile)
    {
        E2EGuards.RequireSandboxSelection(session, sandbox, "regedit4.sandbox.export");
        if (!OpenDialog(driver, dialogs, MenuFileExport, "regedit4.export", out IntPtr dialog))
        {
            return;
        }

        bool selected = dialogs.SelectFileType(dialog, index: 4, "Win9x/NT4", out string typeNotes);
        E2EResults.Record("regedit4.file-type", selected, typeNotes);

        bool typed = dialogs.TrySetFileName(dialog, exportFile, out string notes);
        string acceptNotes = "not attempted";
        bool accepted = typed && dialogs.TryAccept(dialog, "Save", out acceptNotes);
        bool closed = accepted && dialogs.WaitGone(dialog, TimeSpan.FromSeconds(10));
        E2EResults.Record(
            "regedit4.save",
            typed && accepted && closed,
            $"{notes} {acceptNotes} closed={closed}.");
        if (!closed)
        {
            _ = dialogs.TryCancel(dialog);
        }

        byte[]? bytes = WaitForStableFile(exportFile, TimeSpan.FromSeconds(20));
        bool noBom = bytes is { Length: > 2 } && !(bytes[0] == 0xFF && bytes[1] == 0xFE);
        string text = bytes is null ? string.Empty : Encoding.ASCII.GetString(bytes);
        bool header = text.StartsWith("REGEDIT4\r\n", StringComparison.Ordinal);
        bool key = text.Contains(
            "[HKEY_CURRENT_USER\\" + sandbox.RunRootPath + "]\r\n",
            StringComparison.OrdinalIgnoreCase);
        bool value = text.Contains("\"" + ExportValueName + "\"=\"" + ExportValueData + "\"\r\n", StringComparison.Ordinal);
        E2EResults.Record(
            "regedit4.content",
            noBom && header && key && value,
            $"noBom={noBom}, header={header}, key={key}, value={value}, bytes={bytes?.Length ?? 0}.");

        _ = WaitForExportIdle(session, automation, TimeSpan.FromSeconds(15));
    }

    private static void CheckWholeRegistryExport(
        AppSession session,
        EditingDriver driver,
        SystemFileDialogDriver dialogs,
        UIA3Automation automation,
        SandboxRegistry sandbox,
        string exportFile,
        string visualDir)
    {
        AutomationElement? computer = ControlTestSupport.FindRowByName(
            session,
            ControlTestSupport.TreeControl,
            ControlType.TreeItem,
            "Computer");
        ControlTestSupport.Select(computer);
        bool computerSelected = ControlTestSupport.WaitForTreeSelection(session, "Computer", TimeSpan.FromSeconds(10));
        E2EResults.Record("all.computer-selected", computerSelected, $"Computer selected={computerSelected}.");

        if (!OpenDialog(driver, dialogs, MenuFileExport, "all.export", out IntPtr dialog))
        {
            return;
        }

        bool radiosPresent = ControlTestSupport.WaitUntil(
            () => dialogs.ReadRadio(dialogs.FindRadio(dialog, "All")).Present &&
                dialogs.ReadRadio(dialogs.FindRadio(dialog, "Selected branch")).Present,
            TimeSpan.FromSeconds(5));
        RadioState all = dialogs.ReadRadio(dialogs.FindRadio(dialog, "All"));
        RadioState branch = dialogs.ReadRadio(dialogs.FindRadio(dialog, "Selected branch"));
        E2EResults.Record(
            "all.computer-range",
            radiosPresent && all.Present && all.Enabled && all.Selected &&
                branch.Present && !branch.Enabled && !branch.Selected,
            $"radiosPresent={radiosPresent}; All present={all.Present} enabled={all.Enabled} selected={all.Selected}; " +
            $"Selected branch present={branch.Present} enabled={branch.Enabled} selected={branch.Selected}.");

        string screenshot = dialogs.SaveScreenshot(
            dialog,
            Path.Combine(visualDir, "save-dialog-computer-range.png"));
        E2EResults.Note("all.computer-screenshot", $"saved {screenshot}, written={File.Exists(screenshot)}.");

        // Classic regedit draws Selected branch greyed on Computer, and a disabled radio cannot be selected.
        // A real click is sent, and the radio must still be unselected afterwards. A selected disabled radio
        // is a product bug, recorded as a failed check. The saved range is still read by the all.content check.
        bool branchClicked = dialogs.ClickRadio(dialog, dialogs.FindRadio(dialog, "Selected branch"));
        Thread.Sleep(400);
        RadioState branchAfterClick = dialogs.ReadRadio(dialogs.FindRadio(dialog, "Selected branch"));
        E2EResults.Record(
            "all.computer-branch",
            !branchAfterClick.Selected,
            $"Selected branch is disabled on Computer. After a real click: clicked={branchClicked}, " +
            $"enabled={branchAfterClick.Enabled}, selected={branchAfterClick.Selected}; classic regedit keeps it unselected.");

        bool typed = dialogs.TrySetFileName(dialog, exportFile, out string notes);
        string acceptNotes = "not attempted";
        bool accepted = typed && dialogs.TryAccept(dialog, "Save", out acceptNotes);
        bool closed = accepted && dialogs.WaitGone(dialog, TimeSpan.FromSeconds(10));
        E2EResults.Record(
            "all.save",
            typed && accepted && closed,
            $"{notes} {acceptNotes} closed={closed}.");
        if (!closed)
        {
            _ = dialogs.TryCancel(dialog);
        }

        byte[]? bytes = WaitForStableFile(exportFile, TimeSpan.FromSeconds(30));
        string text = DecodeV5(bytes);
        bool header = text.StartsWith("Windows Registry Editor Version 5.00\r\n\r\n", StringComparison.Ordinal);
        string[] roots =
        {
            "[HKEY_CLASSES_ROOT]",
            "[HKEY_CURRENT_USER]",
            "[HKEY_LOCAL_MACHINE]",
            "[HKEY_USERS]",
            "[HKEY_CURRENT_CONFIG]",
        };
        int previous = -1;
        bool ordered = true;
        foreach (string root in roots)
        {
            int index = text.IndexOf(root + "\r\n", StringComparison.OrdinalIgnoreCase);
            if (index < 0 || index <= previous)
            {
                ordered = false;
                break;
            }

            previous = index;
        }

        bool sandboxValue = text.Contains("\"" + ExportValueName + "\"=\"" + ExportValueData + "\"\r\n", StringComparison.Ordinal);
        E2EResults.Record(
            "all.content",
            header && ordered && sandboxValue,
            $"header={header}, rootsInOrder={ordered}, sandboxValue={sandboxValue}, bytes={bytes?.Length ?? 0}.");

        _ = WaitForExportIdle(session, automation, TimeSpan.FromSeconds(20));
    }

    private static bool OpenDialog(
        EditingDriver driver,
        SystemFileDialogDriver dialogs,
        int commandId,
        string checkPrefix,
        out IntPtr dialog)
    {
        if (!InvokeFileCommand(driver, dialogs, commandId, checkPrefix))
        {
            dialog = IntPtr.Zero;
            return false;
        }

        dialog = dialogs.WaitForDialog(TimeSpan.FromSeconds(10));
        return E2EResults.Record(
            checkPrefix + ".dialog",
            dialog != IntPtr.Zero,
            $"#32770 hwnd=0x{dialog.ToInt64():X}.");
    }

    private static bool InvokeFileCommand(
        EditingDriver driver,
        SystemFileDialogDriver dialogs,
        int commandId,
        string checkPrefix)
    {
        try
        {
            driver.OpenMenu(MenuFile);
        }
        catch (Exception ex)
        {
            E2EResults.Record(
                checkPrefix + ".menu",
                false,
                $"File menu did not open: {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        AutomationElement? command = null;
        _ = ControlTestSupport.WaitUntil(
            () => (command = sessionElement(dialogs, commandId)) is not null,
            TimeSpan.FromSeconds(5));

        bool found = command is not null;
        bool enabled = found && SafeIsEnabled(command!);
        bool activated = false;
        string notes = "not found";

        if (found && enabled)
        {
            try
            {
                command!.Focus();
                Thread.Sleep(150);
                Keyboard.Type(VirtualKeyShort.RETURN);
                activated = true;
                notes = $"found, enabled={enabled}, focused then Enter";
            }
            catch (Exception ex)
            {
                notes = $"found, enabled={enabled}, activation failed: {ex.GetType().Name}: {ex.Message}";
            }
        }

        return E2EResults.Record(
            checkPrefix + ".menu",
            found && enabled && activated,
            $"command={commandId}, {notes}.");
    }

    private static AutomationElement? sessionElement(SystemFileDialogDriver dialogs, int commandId) =>
        dialogs.FindProcessElement("MenuCommand_" + commandId);

    private static bool SafeIsEnabled(AutomationElement element)
    {
        try
        {
            return element.IsEnabled;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool WaitForRadio(
        SystemFileDialogDriver dialogs,
        IntPtr dialog,
        string name,
        bool selected)
    {
        return ControlTestSupport.WaitUntil(
            () =>
            {
                RadioState state = dialogs.ReadRadio(dialogs.FindRadio(dialog, name));
                return state.Present && state.Selected == selected;
            },
            TimeSpan.FromSeconds(4));
    }

    /// <summary>
    /// The classic Import progress window (DLG-019) has a bar and the file name only, so the wait ends
    /// when the bar reaches its maximum.
    /// </summary>
    private static bool WaitForImportProgress(UIA3Automation automation, IntPtr progress, TimeSpan timeout)
    {
        return ControlTestSupport.WaitUntil(
            () => ImportExportWindows.ProgressValue(automation, progress) >= 1,
            timeout);
    }

    private static bool WaitForExportProgress(UIA3Automation automation, IntPtr progress, TimeSpan timeout)
    {
        return ControlTestSupport.WaitUntil(
            () =>
            {
                string status = ImportExportWindows.ProgressStatus(automation, progress);
                return status.Contains("Exported ", StringComparison.Ordinal) &&
                    !status.Contains("Exported 0 keys and 0 values", StringComparison.Ordinal);
            },
            timeout);
    }

    private static bool WaitForExportIdle(
        AppSession session,
        UIA3Automation automation,
        TimeSpan timeout)
    {
        return ControlTestSupport.WaitUntil(
            () =>
                ImportExportWindows.FindProgress(automation, session.MainHwnd) == IntPtr.Zero &&
                Win32.IsWindowEnabled(session.MainHwnd),
            timeout);
    }

    private static byte[]? WaitForStableFile(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        long lastLength = -1;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                long length = new FileInfo(path).Length;
                if (length > 0 && length == lastLength)
                {
                    try
                    {
                        return File.ReadAllBytes(path);
                    }
                    catch (IOException)
                    {
                        // The writer still holds the file. The next pass retries.
                    }
                }

                lastLength = length;
            }

            Thread.Sleep(250);
        }

        return null;
    }

    private static string DecodeV5(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 2)
        {
            return string.Empty;
        }

        return bytes[0] == 0xFF && bytes[1] == 0xFE
            ? Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2)
            : Encoding.ASCII.GetString(bytes);
    }

    private static string WriteLargeImportFile(SandboxRegistry sandbox, string folder, int sections)
    {
        string rootPath = @"HKEY_CURRENT_USER\" + sandbox.RunRootPath;
        var builder = new StringBuilder(64 + (sections * 96));
        builder.Append("Windows Registry Editor Version 5.00\r\n\r\n");
        for (var index = 1; index <= sections; index++)
        {
            builder.Append('[').Append(rootPath).Append(@"\ImportCancel\Key")
                .Append(index.ToString("D5", CultureInfo.InvariantCulture)).Append("]\r\n");
            builder.Append("\"Value\"=\"data\"\r\n\r\n");
        }

        string file = Path.Combine(folder, "cancel-import.reg");
        byte[] body = Encoding.Unicode.GetBytes(builder.ToString());
        byte[] preamble = Encoding.Unicode.GetPreamble();
        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        File.WriteAllBytes(file, bytes);
        return file;
    }

    private static void Seed(SandboxRegistry sandbox)
    {
        sandbox.SetString(string.Empty, "AlphaString", "alpha");
        sandbox.SetString(string.Empty, ExportValueName, ExportValueData);
        sandbox.SetDword(string.Empty, "SampleDword", 7);
        sandbox.SetString("Child", "ChildValue", "child");
    }

    private static void SeedLargeBranch(SandboxRegistry sandbox, int keyCount)
    {
        for (var index = 0; index < keyCount; index++)
        {
            sandbox.SetString(
                @"CancelExport\Key" + index.ToString("D5", CultureInfo.InvariantCulture),
                "Value",
                "data");
        }
    }

    private static string CreateFreshDataRoot(E2EConfig config, string scope)
    {
        string path = config.DataRootFor(scope);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
        return path;
    }

    private static void RequireExecutables(E2EConfig config)
    {
        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("importexport.publish", false, ex.Message);
            Assert.Fail(ex.Message);
        }
    }

    private static void FailIfNeeded(int startIndex)
    {
        IReadOnlyList<string> failures = E2EResults.FailedChecksSince(startIndex);
        if (failures.Count > 0)
        {
            Assert.Fail($"Export range checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
        }
    }
}

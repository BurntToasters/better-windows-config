using Bwc.E2E.Controls;
using Bwc.E2E.Dialogs;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.ImportExport;

/// <summary>
/// The Export save dialog's branch path edit (classic control 1282, DLG-001, DLG-002, BEH-021). The edit is
/// prefilled with the selected key in the classic form, it stays enabled whichever range is chosen, a missing
/// key is refused with RT_STRING 148 while the dialog stays open, and a typed key that exists is the
/// key exported, not the tree selection. The test is mutating and runs only on the e2e flavor.
/// </summary>
[TestClass]
public sealed class ExportBranchPathTests
{
    private const int MenuFile = 512;
    private const int MenuFileExport = 659;
    private const string AlphaName = "AlphaString";
    private const string AlphaData = "alpha";
    private const string BranchChildName = "BranchTarget";
    private const string BranchMarkerName = "Marker";
    private const string BranchMarkerData = "branch-target";
    private const string ClassicMissingBranch =
        "The selected branch does not exist. Make sure that the correct path is given.";

    // RT_STRING 301 is the caption used until the oracle measures the caption of the RT_STRING 148 box (OQ-15).
    private const string ClassicExportTitle = "Export Registry File";

    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("ImportExport")]
    [TestCategory("Mutating")]
    public void ExportDialogs_BranchPathEditRefusesMissingKey()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        config.EnsureExecutablesExist();

        var sandbox = new SandboxRegistry(config);
        string tempDir = Path.Combine(config.ArtifactsDir, "import-export", config.RunId, "branch-path");
        try
        {
            sandbox.Cleanup();
            Directory.CreateDirectory(tempDir);
            Seed(sandbox);

            using var session = StartApp(config, sandbox, "export-branch-refuse");
            using var driver = new EditingDriver(session);
            using var dialogs = new SystemFileDialogDriver(session);
            using var automation = new UIA3Automation();

            NavigateToRunRoot(session, config, sandbox, "branch.refuse");
            IntPtr dialog = OpenFileCommand(driver, dialogs, MenuFileExport, "branch.refuse");
            if (dialog == IntPtr.Zero)
            {
                FailIfNeeded(checkStart);
                return;
            }

            CheckPrefillAndRadios(dialogs, dialog, sandbox, "branch.refuse");
            AutomationElement? edit = dialogs.FindBranchEdit(dialog);
            string missing = RunRootClassicPath(sandbox) + @"\DoesNotExist";
            string typedBack = "(not typed)";
            bool typed = edit is not null && dialogs.TrySetEditText(dialog, edit, missing, out typedBack);
            E2EResults.Record(
                "branch.refuse.typed",
                typed,
                $"typed '{missing}', edit reads '{(edit is null ? "(no edit)" : typedBack)}'.");

            string missingFile = Path.Combine(tempDir, "missing.reg");
            string nameNotes = "not attempted";
            string acceptNotes = "not attempted";
            bool named = typed && dialogs.TrySetFileName(dialog, missingFile, out nameNotes);
            bool accepted = named && dialogs.TryAccept(dialog, "Save", out acceptNotes);
            E2EResults.Record("branch.refuse.save", accepted, $"{nameNotes} {acceptNotes}");

            AutomationElement? error = null;
            _ = ControlTestSupport.WaitUntil(
                () => (error = dialogs.FindProcessElement("DialogErrorText")) is not null,
                TimeSpan.FromSeconds(10));
            string errorText = error is null ? string.Empty : ReadText(error);
            string errorCaption = error is null ? string.Empty : ControlTestSupport.SafeName(ClassicDialogTests.WindowOf(error));
            E2EResults.Record(
                "branch.refuse.message",
                error is not null &&
                    string.Equals(errorText, ClassicMissingBranch, StringComparison.Ordinal) &&
                    string.Equals(errorCaption, ClassicExportTitle, StringComparison.Ordinal),
                $"text='{errorText}' (expected '{ClassicMissingBranch}'), caption='{errorCaption}' " +
                $"(expected '{ClassicExportTitle}' until OQ-15 is measured).");

            bool stillOpen = Win32.IsWindow(dialog);
            E2EResults.Record(
                "branch.refuse.dialog-open",
                stillOpen && !File.Exists(missingFile),
                $"file dialog still open={stillOpen}, nothing written={!File.Exists(missingFile)}.");

            bool dismissed = error is not null && driver.DismissErrorBox();
            bool openAfterDismiss = Win32.IsWindow(dialog);
            E2EResults.Record(
                "branch.refuse.after-dismiss",
                dismissed && openAfterDismiss,
                $"error dismissed={dismissed}, file dialog still open={openAfterDismiss}.");

            bool cancelled = dialogs.TryCancel(dialog);
            E2EResults.Record(
                "branch.refuse.cancel",
                cancelled && !File.Exists(missingFile),
                $"cancelled={cancelled}, nothing written={!File.Exists(missingFile)}.");

            FailIfNeeded(checkStart);
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    [TestMethod]
    [TestCategory("ImportExport")]
    [TestCategory("Mutating")]
    public void ExportDialogs_BranchPathEditExportsTheTypedKey()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        config.EnsureExecutablesExist();

        var sandbox = new SandboxRegistry(config);
        string tempDir = Path.Combine(config.ArtifactsDir, "import-export", config.RunId, "branch-path");
        try
        {
            sandbox.Cleanup();
            Directory.CreateDirectory(tempDir);
            Seed(sandbox);

            using var session = StartApp(config, sandbox, "export-branch-typed");
            using var driver = new EditingDriver(session);
            using var dialogs = new SystemFileDialogDriver(session);

            NavigateToRunRoot(session, config, sandbox, "branch.typed");
            IntPtr dialog = OpenFileCommand(driver, dialogs, MenuFileExport, "branch.typed");
            if (dialog == IntPtr.Zero)
            {
                FailIfNeeded(checkStart);
                return;
            }

            // The tree selection is the run root. The typed key is the child, so only the child may be exported.
            string typedKey = RunRootClassicPath(sandbox) + @"\" + BranchChildName;
            AutomationElement? edit = dialogs.FindBranchEdit(dialog);
            string typedBack = "(not typed)";
            bool typed = edit is not null && dialogs.TrySetEditText(dialog, edit, typedKey, out typedBack);
            E2EResults.Record("branch.typed.edit", typed, $"typed '{typedKey}', edit reads '{typedBack}'.");

            string exportFile = Path.Combine(tempDir, "typed-branch.reg");
            string nameNotes = "not attempted";
            string acceptNotes = "not attempted";
            bool named = typed && dialogs.TrySetFileName(dialog, exportFile, out nameNotes);
            bool accepted = named && dialogs.TryAccept(dialog, "Save", out acceptNotes);
            bool closed = accepted && dialogs.WaitGone(dialog, TimeSpan.FromSeconds(15));
            E2EResults.Record("branch.typed.save", closed, $"{nameNotes} {acceptNotes} closed={closed}.");
            if (!closed)
            {
                _ = dialogs.TryCancel(dialog);
            }

            string text = ControlTestSupport.WaitUntil(() => File.Exists(exportFile), TimeSpan.FromSeconds(20))
                ? File.ReadAllText(exportFile)
                : string.Empty;
            bool exportedChild = text.Contains("[" + typedKey + "]", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("\"" + BranchMarkerName + "\"=\"" + BranchMarkerData + "\"", StringComparison.Ordinal);
            bool notRootTree = !text.Contains("\"" + AlphaName + "\"=\"" + AlphaData + "\"", StringComparison.Ordinal);
            E2EResults.Record(
                "branch.typed.exports-typed-key",
                exportedChild && notRootTree,
                $"file has the typed key={exportedChild}, file has no tree-selection value={notRootTree}, bytes={text.Length}.");

            FailIfNeeded(checkStart);
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    private static AppSession StartApp(E2EConfig config, SandboxRegistry sandbox, string scope)
    {
        string dataRoot = config.DataRootFor(scope);
        if (Directory.Exists(dataRoot))
        {
            Directory.Delete(dataRoot, recursive: true);
        }

        Directory.CreateDirectory(dataRoot);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
            ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
        };
        return new AppSession(config, config.RegeditExePath, environment: environment);
    }

    private static void NavigateToRunRoot(AppSession session, E2EConfig config, SandboxRegistry sandbox, string checkId)
    {
        bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
        bool seeded = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.ListRowNames(session).Contains(AlphaName, StringComparer.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(15));
        E2EResults.Record(
            checkId + ".navigate",
            navigated && seeded,
            $"navigated={navigated}, seeded={seeded}, rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}].");
        E2EGuards.RequireSandboxSelection(session, sandbox, checkId + ".sandbox.selection");
    }

    private static void CheckPrefillAndRadios(SystemFileDialogDriver dialogs, IntPtr dialog, SandboxRegistry sandbox, string checkId)
    {
        string expectedPrefill = RunRootClassicPath(sandbox);
        AutomationElement? edit = dialogs.FindBranchEdit(dialog);
        string prefill = edit is null ? "(no edit)" : ReadEditValue(edit);
        E2EResults.Record(
            checkId + ".prefill",
            string.Equals(prefill, expectedPrefill, StringComparison.Ordinal),
            $"branch edit='{prefill}', expected the classic form '{expectedPrefill}'.");

        AutomationElement? allRadio = dialogs.FindRadio(dialog, "All");
        AutomationElement? branchRadio = dialogs.FindRadio(dialog, "Selected branch");
        bool branchSelected = dialogs.ReadRadio(branchRadio).Selected;
        E2EResults.Record(checkId + ".branch-default", branchSelected, $"Selected branch selected by default={branchSelected}.");
        bool editEnabledByDefault = SystemFileDialogDriver.IsEnabledElement(edit);
        E2EResults.Record(checkId + ".edit-enabled-default", editEnabledByDefault, $"edit enabled with Selected branch={editEnabledByDefault}.");

        bool allClicked = dialogs.ClickRadio(dialog, allRadio);
        // Classic keeps the branch edit enabled under All too (oracle export-range, after-all enabled=true).
        bool editEnabledWithAll = allClicked && ControlTestSupport.WaitUntil(
            () => SystemFileDialogDriver.IsEnabledElement(dialogs.FindBranchEdit(dialog)),
            TimeSpan.FromSeconds(3));
        E2EResults.Record(
            checkId + ".edit-enabled-with-all",
            editEnabledWithAll,
            $"clicked All={allClicked}, branch edit enabled={editEnabledWithAll} (classic: enabled).");

        bool branchClicked = dialogs.ClickRadio(dialog, dialogs.FindRadio(dialog, "Selected branch"));
        bool editEnabled = branchClicked && ControlTestSupport.WaitUntil(
            () => SystemFileDialogDriver.IsEnabledElement(dialogs.FindBranchEdit(dialog)),
            TimeSpan.FromSeconds(3));
        E2EResults.Record(checkId + ".edit-enabled-with-branch", editEnabled, $"clicked Selected branch={branchClicked}, edit enabled={editEnabled}.");
    }

    private static IntPtr OpenFileCommand(EditingDriver driver, SystemFileDialogDriver dialogs, int commandId, string checkId)
    {
        try
        {
            driver.OpenMenu(MenuFile);
        }
        catch (Exception ex)
        {
            E2EResults.Record(checkId + ".menu", false, $"File menu did not open: {ex.GetType().Name}: {ex.Message}");
            return IntPtr.Zero;
        }

        AutomationElement? command = null;
        _ = ControlTestSupport.WaitUntil(
            () => (command = dialogs.FindProcessElement("MenuCommand_" + commandId)) is not null,
            TimeSpan.FromSeconds(5));
        if (command is null)
        {
            E2EResults.Record(checkId + ".menu", false, $"command {commandId} not found.");
            return IntPtr.Zero;
        }

        command.Focus();
        Thread.Sleep(150);
        Keyboard.Type(VirtualKeyShort.RETURN);
        IntPtr dialog = dialogs.WaitForDialog(TimeSpan.FromSeconds(10));
        E2EResults.Record(checkId + ".dialog", dialog != IntPtr.Zero, $"command={commandId}, file dialog hwnd=0x{dialog.ToInt64():X}.");
        return dialog;
    }

    private static void Seed(SandboxRegistry sandbox)
    {
        sandbox.SetString(string.Empty, AlphaName, AlphaData);
        sandbox.CreateKey(BranchChildName);
        sandbox.SetString(BranchChildName, BranchMarkerName, BranchMarkerData);
    }

    /// <summary>The run root as the branch edit shows it: HKEY_CURRENT_USER and the path.</summary>
    private static string RunRootClassicPath(SandboxRegistry sandbox) => "HKEY_CURRENT_USER\\" + sandbox.RunRootPath;

    private static string ReadEditValue(AutomationElement edit)
    {
        try
        {
            return edit.Patterns.Value.IsSupported ? edit.Patterns.Value.Pattern.Value.Value ?? string.Empty : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string ReadText(AutomationElement element)
    {
        try
        {
            return element.Patterns.Value.IsSupported
                ? element.Patterns.Value.Pattern.Value.Value ?? string.Empty
                : element.Name ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static void FailIfNeeded(int startIndex)
    {
        var failures = E2EResults.FailedChecksSince(startIndex);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Export branch path checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}

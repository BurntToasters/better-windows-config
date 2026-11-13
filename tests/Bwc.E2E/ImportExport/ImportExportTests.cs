using System.Text;
using Bwc.E2E.AppServices;
using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.ImportExport;

/// <summary>
/// Drives File > Export and File > Import through the real COM common item dialogs. Every check is
/// scoped to the run sandbox under HKCU and to a temporary folder under artifacts/e2e. The test
/// verifies dialog ownership, the disabled owner, the returned file, cancellation, the success message
/// after an import, and the tree and list refresh after an import.
/// </summary>
[TestClass]
public sealed class ImportExportTests
{
    private const int MenuFile = 512;
    private const int MenuFileImport = 658;
    private const int MenuFileExport = 659;
    private const string ImportedKeyName = "Imported";
    private const string ImportedRootValue = "ImportedAtRoot";
    private const string ImportedChildValue = "ImportedValue";
    private const string ExportValueName = "ExportString";

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("ImportExport")]
    [TestCategory("Mutating")]
    public void ImportExport_UsesOwnedDialogsAndRefreshesTheSandbox()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);

        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("importexport.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        string tempDir = Path.Combine(config.ArtifactsDir, "import-export", config.RunId, "basic");
        string exportFile = Path.Combine(tempDir, "exported key with spaces.reg");
        string importFile = Path.Combine(tempDir, "import source.reg");
        string cancelFile = Path.Combine(tempDir, "cancel me.reg");

        try
        {
            sandbox.Cleanup();
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }

            Directory.CreateDirectory(tempDir);
            Seed(sandbox);
            WriteImportFile(sandbox, importFile);

            string dataRoot = CreateFreshDataRoot(config, "import-export");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);
            using var dialogs = new SystemFileDialogDriver(session);

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            bool seeded = ControlTestSupport.WaitUntil(
                () => ControlTestSupport.ListRowNames(session).Contains("AlphaString", StringComparer.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(15));
            E2EResults.Record(
                "importexport.navigate",
                navigated && seeded,
                $"navigated={navigated}, seededRows={seeded}, rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}].");
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated && seeded, "importexport.sandbox.navigate");

            CheckExport(session, driver, dialogs, sandbox, exportFile);
            CheckImport(session, driver, dialogs, sandbox, importFile);
            CheckCancel(session, driver, dialogs, sandbox, cancelFile);

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            DeleteTempDirectory(tempDir);
        }
    }

    private static void CheckExport(
        AppSession session,
        EditingDriver driver,
        SystemFileDialogDriver dialogs,
        SandboxRegistry sandbox,
        string exportFile)
    {
        E2EGuards.RequireSandboxSelection(session, sandbox, "importexport.sandbox.export");
        if (!InvokeFileCommand(driver, dialogs, MenuFileExport, "importexport.export"))
        {
            return;
        }

        IntPtr dialog = dialogs.WaitForDialog(TimeSpan.FromSeconds(8));

        bool appears = E2EResults.Record(
            "importexport.export.dialog",
            dialog != IntPtr.Zero,
            $"#32770 hwnd=0x{dialog.ToInt64():X}.");
        if (!appears)
        {
            return;
        }

        bool owned = E2EResults.Record(
            "importexport.export.owner",
            Win32.GetWindow(dialog, Win32.GvOwner) == session.MainHwnd,
            $"owner=0x{Win32.GetWindow(dialog, Win32.GvOwner).ToInt64():X}, main=0x{session.MainHwnd.ToInt64():X}.");
        bool disabled = E2EResults.Record(
            "importexport.export.main-disabled",
            !Win32.IsWindowEnabled(session.MainHwnd),
            $"main enabled while dialog open={Win32.IsWindowEnabled(session.MainHwnd)}.");

        bool set = dialogs.TrySetFileName(dialog, exportFile, out string setName);
        string acceptName = string.Empty;
        bool accepted = set && dialogs.TryAccept(dialog, "Save", out acceptName);
        bool closed = accepted && dialogs.WaitGone(dialog, TimeSpan.FromSeconds(10));
        bool cancelFallback = false;
        if (!closed)
        {
            cancelFallback = dialogs.TryCancel(dialog);
        }

        E2EResults.Record(
            "importexport.export.automate",
            owned && disabled && set && accepted && closed,
            $"{setName} {acceptName} closed={closed}, cancelFallback={cancelFallback}.");

        // The application writes the file on a background task, so it can appear after the dialog closes.
        byte[]? stable = WaitForStableFile(exportFile, TimeSpan.FromSeconds(10));
        bool fileExists = stable is not null;
        byte[] bytes = stable ?? Array.Empty<byte>();
        bool bom = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE;
        string text = bom ? Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2) : string.Empty;
        bool header = text.StartsWith(
            "Windows Registry Editor Version 5.00\r\n\r\n",
            StringComparison.Ordinal);
        // Registry key names are case-insensitive, so the key header matches in any case.
        bool key = text.Contains(
            "[HKEY_CURRENT_USER\\" + sandbox.RunRootPath + "]\r\n",
            StringComparison.OrdinalIgnoreCase);
        bool value = text.Contains(
            "\"" + ExportValueName + "\"=\"exported\"\r\n",
            StringComparison.Ordinal);
        E2EResults.Record(
            "importexport.export.file",
            fileExists && bom && header && key && value,
            $"exists={fileExists}, presentNow={File.Exists(exportFile)}, bom={bom}, header={header}, key={key}, value={value}, bytes={bytes.Length}, file='{exportFile}'.");

        bool enabledAgain = ControlTestSupport.WaitUntil(
            () => Win32.IsWindowEnabled(session.MainHwnd),
            TimeSpan.FromSeconds(8));
        E2EResults.Record(
            "importexport.export.enabled-after",
            enabledAgain,
            $"main enabled after close={enabledAgain}.");
    }

    private static void CheckImport(
        AppSession session,
        EditingDriver driver,
        SystemFileDialogDriver dialogs,
        SandboxRegistry sandbox,
        string importFile)
    {
        E2EGuards.RequireSandboxSelection(session, sandbox, "importexport.sandbox.import");
        if (!InvokeFileCommand(driver, dialogs, MenuFileImport, "importexport.import"))
        {
            return;
        }

        IntPtr dialog = dialogs.WaitForDialog(TimeSpan.FromSeconds(8));
        bool appears = E2EResults.Record(
            "importexport.import.dialog",
            dialog != IntPtr.Zero,
            $"#32770 hwnd=0x{dialog.ToInt64():X}.");
        if (!appears)
        {
            return;
        }

        bool owned = Win32.GetWindow(dialog, Win32.GvOwner) == session.MainHwnd;
        bool disabled = !Win32.IsWindowEnabled(session.MainHwnd);
        bool set = dialogs.TrySetFileName(dialog, importFile, out string setName);
        string acceptName = string.Empty;
        bool accepted = set && dialogs.TryAccept(dialog, "Open", out acceptName);
        bool closed = accepted && dialogs.WaitGone(dialog, TimeSpan.FromSeconds(10));
        bool cancelFallback = false;
        if (!closed)
        {
            cancelFallback = dialogs.TryCancel(dialog);
        }

        E2EResults.Record(
            "importexport.import.automate",
            owned && disabled && set && accepted && closed,
            $"{setName} {acceptName} closed={closed}, cancelFallback={cancelFallback}.");

        bool rootValue = ControlTestSupport.WaitUntil(
            () => sandbox.ReadString(string.Empty, ImportedRootValue) == "root value",
            TimeSpan.FromSeconds(10));
        bool childValue = ControlTestSupport.WaitUntil(
            () => sandbox.ReadString(ImportedKeyName, ImportedChildValue) == "child value",
            TimeSpan.FromSeconds(10));
        E2EResults.Record(
            "importexport.import.registry",
            rootValue && childValue,
            $"rootValue={rootValue}, childValue={childValue}.");

        // The result message (RT_STRING 228) is modal. It is read and dismissed here, before the tree and list checks.
        // The import progress dialog has the same title and closes first, so the text picks the result.
        IntPtr result = ImportExportWindows.WaitForResult(
            session.MainHwnd,
            text => text.Contains("Cannot import", StringComparison.Ordinal) ||
                text.Contains("successfully added to the registry", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10),
            dialog);
        bool resultText = result != IntPtr.Zero
            && AppServicesSupport.WaitForDialogText(
                result,
                text => text.Contains("successfully added to the registry", StringComparison.Ordinal),
                TimeSpan.FromSeconds(10))
                .Contains("successfully added to the registry", StringComparison.Ordinal);
        E2EResults.Record(
            "importexport.import.result",
            resultText,
            $"success message shown={result != IntPtr.Zero}, text names the import={resultText}.");
        if (result != IntPtr.Zero)
        {
            // The message must close before the next command, or its modal state blocks the Export menu.
            bool dismissed = AppServicesSupport.DismissMessage(result, TimeSpan.FromSeconds(10));
            E2EResults.Record(
                "importexport.import.dismissed",
                dismissed,
                $"the success message closed with Enter={dismissed}.");
        }

        bool rootSelected = ControlTestSupport.WaitForTreeSelection(
            session,
            SessionKeyName(sandbox),
            TimeSpan.FromSeconds(10));
        bool rootRow = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.ListRowNames(session).Contains(ImportedRootValue, StringComparer.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(10));

        AutomationElement? runRow = ControlTestSupport.FindRowByName(
            session,
            ControlTestSupport.TreeControl,
            ControlType.TreeItem,
            SessionKeyName(sandbox));
        _ = ControlTestSupport.Expand(runRow);
        bool childRow = ControlTestSupport.WaitForTreeRow(
            session,
            ImportedKeyName,
            TimeSpan.FromSeconds(10));
        AutomationElement? importedRow = ControlTestSupport.FindRowByName(
            session,
            ControlTestSupport.TreeControl,
            ControlType.TreeItem,
            ImportedKeyName);
        ControlTestSupport.Select(importedRow);
        bool childSelected = ControlTestSupport.WaitForTreeSelection(
            session,
            ImportedKeyName,
            TimeSpan.FromSeconds(10));
        bool childListRow = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.ListRowNames(session).Contains(ImportedChildValue, StringComparer.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(10));

        E2EResults.Record(
            "importexport.import.refresh",
            rootSelected && rootRow && childRow && childSelected && childListRow,
            $"rootSelected={rootSelected}, rootRow={rootRow}, childRow={childRow}, childSelected={childSelected}, childListRow={childListRow}.");
    }

    private static void CheckCancel(
        AppSession session,
        EditingDriver driver,
        SystemFileDialogDriver dialogs,
        SandboxRegistry sandbox,
        string cancelFile)
    {
        E2EGuards.RequireSandboxSelection(session, sandbox, "importexport.sandbox.cancel");
        if (!InvokeFileCommand(driver, dialogs, MenuFileExport, "importexport.cancel"))
        {
            return;
        }

        IntPtr dialog = dialogs.WaitForDialog(TimeSpan.FromSeconds(8));
        bool appears = E2EResults.Record(
            "importexport.cancel.dialog",
            dialog != IntPtr.Zero,
            $"#32770 hwnd=0x{dialog.ToInt64():X}.");
        if (!appears)
        {
            return;
        }

        bool set = dialogs.TrySetFileName(dialog, cancelFile, out _);
        bool cancelled = dialogs.TryCancel(dialog);
        bool closed = cancelled && dialogs.WaitGone(dialog, TimeSpan.FromSeconds(8));
        bool enabledAgain = ControlTestSupport.WaitUntil(
            () => Win32.IsWindowEnabled(session.MainHwnd),
            TimeSpan.FromSeconds(8));
        bool noFile = !File.Exists(cancelFile);

        E2EResults.Record(
            "importexport.cancel",
            set && cancelled && closed && enabledAgain && noFile,
            $"set={set}, cancelled={cancelled}, closed={closed}, enabledAgain={enabledAgain}, noFile={noFile}.");
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
            () => (command = dialogs.FindProcessElement("MenuCommand_" + commandId)) is not null,
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
                try
                {
                    command!.Click();
                    activated = true;
                    notes = $"found, enabled={enabled}, clicked after focus error {ex.GetType().Name}";
                }
                catch (Exception clickError)
                {
                    notes = $"found, enabled={enabled}, activation failed: {clickError.GetType().Name}: {clickError.Message}";
                }
            }
        }

        return E2EResults.Record(
            checkPrefix + ".menu",
            found && enabled && activated,
            $"command={commandId}, {notes}.");
    }

    /// <summary>
    /// Waits until the file exists, has content, and keeps the same size across two reads 250 ms apart.
    /// Returns its bytes, or null when the file never settles within the timeout.
    /// </summary>
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

    private static string SessionKeyName(SandboxRegistry sandbox)
    {
        string path = sandbox.RunRootPath;
        int separator = path.LastIndexOf('\\');
        return separator >= 0 ? path[(separator + 1)..] : path;
    }

    private static void Seed(SandboxRegistry sandbox)
    {
        using (RegistryKey root = sandbox.CreateSubKey(string.Empty))
        {
            root.SetValue("AlphaString", "alpha", RegistryValueKind.String);
            root.SetValue(ExportValueName, "exported", RegistryValueKind.String);
            root.SetValue("SampleDword", 7, RegistryValueKind.DWord);
        }
    }

    private static void WriteImportFile(SandboxRegistry sandbox, string importFile)
    {
        string rootPath = @"HKEY_CURRENT_USER\" + sandbox.RunRootPath;
        string childPath = rootPath + @"\" + ImportedKeyName;
        string text =
            "Windows Registry Editor Version 5.00\r\n\r\n"
            + "[" + rootPath + "]\r\n"
            + "\"" + ImportedRootValue + "\"=\"root value\"\r\n\r\n"
            + "[" + childPath + "]\r\n"
            + "\"" + ImportedChildValue + "\"=\"child value\"\r\n";

        File.WriteAllBytes(importFile, EncodeV5(text));
    }

    private static byte[] EncodeV5(string text)
    {
        byte[] body = Encoding.Unicode.GetBytes(text);
        var bytes = new byte[body.Length + 2];
        bytes[0] = 0xFF;
        bytes[1] = 0xFE;
        body.CopyTo(bytes, 2);
        return bytes;
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

    private static void DeleteTempDirectory(string tempDir)
    {
        try
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            E2EResults.Record("importexport.cleanup.temp", false, ex.Message);
        }
    }

    private static void FailIfNeeded()
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Import and export checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

}

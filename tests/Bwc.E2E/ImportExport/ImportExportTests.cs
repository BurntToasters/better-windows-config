using System.Runtime.InteropServices;
using System.Text;
using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.ImportExport;

/// <summary>
/// Drives File > Export and File > Import through the real COM common item dialogs. Every check is
/// scoped to the run sandbox under HKCU and to a temporary folder under artifacts/e2e. The test
/// verifies dialog ownership, the disabled owner, the returned file, cancellation, and the tree and
/// list refresh after an import.
/// </summary>
[TestClass]
public sealed class ImportExportTests
{
    private const int MenuFile = 512;
    private const int MenuFileImport = 658;
    private const int MenuFileExport = 659;
    private const string CommonDialogClass = "#32770";
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
        string tempDir = Path.Combine(config.ArtifactsDir, "import-export", config.RunId);
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
            using var dialogs = new CommonDialogDriver(session);

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
        CommonDialogDriver dialogs,
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
        CommonDialogDriver dialogs,
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
        CommonDialogDriver dialogs,
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
        CommonDialogDriver dialogs,
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

    /// <summary>
    /// Finds the COM dialog window by process and class, then drives its file name field and accept
    /// button. UIA can see the shell view; raw Win32 confirms ownership and the disabled owner.
    /// </summary>
    private sealed class CommonDialogDriver : IDisposable
    {
        private readonly AppSession _session;
        private readonly UIA3Automation _automation;

        internal CommonDialogDriver(AppSession session)
        {
            _session = session;
            _automation = new UIA3Automation();
        }

        internal IntPtr WaitForDialog(TimeSpan timeout)
        {
            IntPtr found = IntPtr.Zero;
            _ = ControlTestSupport.WaitUntil(
                () => (found = FindDialog()) != IntPtr.Zero,
                timeout);
            return found;
        }

        internal AutomationElement? FindProcessElement(string automationId)
        {
            try
            {
                AutomationElement desktop = _automation.GetDesktop();

                foreach (AutomationElement window in desktop.FindAllChildren(cf => cf.ByControlType(ControlType.Window)))
                {
                    int processId;
                    try
                    {
                        processId = window.Properties.ProcessId.Value;
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (processId != _session.ProcessId)
                    {
                        continue;
                    }

                    try
                    {
                        AutomationElement? found = window.FindFirstDescendant(
                            cf => cf.ByAutomationId(automationId));
                        if (found is not null)
                        {
                            return found;
                        }
                    }
                    catch (Exception)
                    {
                        // A window that disappeared during enumeration is skipped.
                    }
                }

                AutomationElement? global = desktop.FindFirstDescendant(
                    cf => cf.ByAutomationId(automationId));
                if (global is not null && global.Properties.ProcessId.Value == _session.ProcessId)
                {
                    return global;
                }
            }
            catch (Exception)
            {
                // UIA can throw while a flyout is opening or closing.
            }

            return null;
        }

        internal bool WaitGone(IntPtr dialog, TimeSpan timeout) =>
            ControlTestSupport.WaitUntil(() => !Win32.IsWindow(dialog), timeout);

        internal bool TrySetFileName(IntPtr dialog, string path, out string notes)
        {
            AutomationElement? edit = null;
            _ = ControlTestSupport.WaitUntil(
                () => (edit = FindFileNameEdit(dialog)) is not null,
                TimeSpan.FromSeconds(8));

            if (edit is not null && TrySetValue(edit, path))
            {
                notes = $"fileNameEdit id='{SafeId(edit)}' name='{SafeName(edit)}' set through ValuePattern and read back.";
                return true;
            }

            // Keyboard fallback. Typing goes to whichever control has focus, so it counts only when the
            // file name box reads back the full path. Accepting the dialog with any other text would save
            // under the dialog's default name in its default folder, which is how a stray <run-id>.reg
            // file ended up in Documents.
            if (edit is not null && FocusEdit(edit) && EnsureForeground(dialog))
            {
                Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
                Keyboard.Type(path);
                Thread.Sleep(200);
                AutomationElement? after = FindFileNameEdit(dialog);
                string readBack = after is null ? "(no file name box)" : ReadValue(after);
                if (string.Equals(readBack, path, StringComparison.Ordinal))
                {
                    notes = "fileNameEdit not usable through ValuePattern; typed the path and read it back.";
                    return true;
                }

                notes = $"typed the path, but the file name box reads '{readBack}'. Save is not pressed.";
                return false;
            }

            notes = "fileNameEdit not found or could not be focused. Save is not pressed.";
            return false;
        }

        private static bool FocusEdit(AutomationElement edit)
        {
            try
            {
                edit.Focus();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal bool TryAccept(IntPtr dialog, string acceptText, out string notes)
        {
            AutomationElement? window = WindowElement(dialog);
            AutomationElement? button = window is null ? null : FindAcceptButton(window, acceptText);
            if (button is not null)
            {
                try
                {
                    if (button.Patterns.Invoke.IsSupported)
                    {
                        button.Patterns.Invoke.Pattern.Invoke();
                    }
                    else
                    {
                        button.Click();
                    }

                    notes = $"acceptButton id='{SafeId(button)}' name='{SafeName(button)}' invoked.";
                    return true;
                }
                catch (Exception ex)
                {
                    notes = "acceptButton invoke failed: " + ex.Message;
                }
            }

            if (EnsureForeground(dialog))
            {
                Keyboard.Type(VirtualKeyShort.RETURN);
                notes = "acceptButton not found; pressed Enter.";
                return true;
            }

            notes = "acceptButton not found and the dialog could not be focused.";
            return false;
        }

        internal bool TryCancel(IntPtr dialog)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (!EnsureForeground(dialog))
                {
                    return false;
                }

                Thread.Sleep(300);
                Keyboard.Type(VirtualKeyShort.ESCAPE);
                if (WaitGone(dialog, TimeSpan.FromSeconds(3)))
                {
                    return true;
                }
            }

            return !Win32.IsWindow(dialog);
        }

        public void Dispose() => _automation.Dispose();

        private IntPtr FindDialog()
        {
            IntPtr found = IntPtr.Zero;
            _ = EnumWindows(
                (hwnd, _) =>
                {
                    GetWindowThreadProcessId(hwnd, out uint processId);
                    if (processId != (uint)_session.ProcessId)
                    {
                        return true;
                    }

                    var className = new StringBuilder(64);
                    _ = GetClassName(hwnd, className, className.Capacity);
                    if (!string.Equals(className.ToString(), CommonDialogClass, StringComparison.Ordinal))
                    {
                        return true;
                    }

                    found = hwnd;
                    return false;
                },
                IntPtr.Zero);

            return found;
        }

        private AutomationElement? WindowElement(IntPtr dialog)
        {
            try
            {
                return _automation.FromHandle(dialog);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private AutomationElement? FindFileNameEdit(IntPtr dialog)
        {
            try
            {
                AutomationElement window = _automation.FromHandle(dialog);
                AutomationElement[] edits = FindAll(window, ControlType.Edit);
                return ChooseFileNameEdit(edits);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static AutomationElement? ChooseFileNameEdit(AutomationElement[] edits)
        {
            AutomationElement? bottomMost = null;
            double bottomMostTop = double.MinValue;

            foreach (AutomationElement edit in edits)
            {
                if (!IsUsableEdit(edit))
                {
                    continue;
                }

                if (LooksLikeFileNameEdit(edit))
                {
                    return edit;
                }

                double top = TopOf(edit);
                if (top > bottomMostTop)
                {
                    bottomMost = edit;
                    bottomMostTop = top;
                }
            }

            return bottomMost;
        }

        private static bool IsUsableEdit(AutomationElement edit)
        {
            try
            {
                return edit.IsEnabled
                    && !edit.Properties.IsOffscreen.ValueOrDefault
                    && edit.Patterns.Value.IsSupported;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool LooksLikeFileNameEdit(AutomationElement edit)
        {
            string id = SafeId(edit);
            if (id is "1001" or "1148" or "1152" or "FileNameControl" or "FileNameControlHost")
            {
                return true;
            }

            if (SafeName(edit).Contains("file name", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                AutomationElement? parent = edit.Parent;
                for (var depth = 0; parent is not null && depth < 3; depth++)
                {
                    if (parent.ControlType == ControlType.ComboBox &&
                        SafeName(parent).Contains("file name", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    parent = parent.Parent;
                }
            }
            catch (Exception)
            {
                // Ancestor navigation is best effort; the bottom-most fallback still applies.
            }

            return false;
        }

        private static double TopOf(AutomationElement element)
        {
            try
            {
                return element.BoundingRectangle.Top;
            }
            catch (Exception)
            {
                return double.MinValue;
            }
        }

        private static AutomationElement? FindAcceptButton(AutomationElement window, string acceptText)
        {
            AutomationElement[] buttons = FindAll(window, ControlType.Button);
            foreach (AutomationElement button in buttons)
            {
                if (SafeId(button) == "1" && IsUsableButton(button))
                {
                    return button;
                }
            }

            foreach (AutomationElement button in buttons)
            {
                string name = SafeName(button).Replace("&", string.Empty, StringComparison.Ordinal).Trim();
                if (string.Equals(name, acceptText, StringComparison.OrdinalIgnoreCase) &&
                    IsUsableButton(button))
                {
                    return button;
                }
            }

            return null;
        }

        private static bool IsUsableButton(AutomationElement button)
        {
            try
            {
                return button.IsEnabled && !button.Properties.IsOffscreen.ValueOrDefault;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static AutomationElement[] FindAll(AutomationElement root, ControlType controlType)
        {
            try
            {
                return root.FindAllDescendants(cf => cf.ByControlType(controlType));
            }
            catch (Exception)
            {
                return Array.Empty<AutomationElement>();
            }
        }

        private static bool TrySetValue(AutomationElement edit, string value)
        {
            try
            {
                edit.Patterns.Value.Pattern.SetValue(value);
                return string.Equals(ReadValue(edit), value, StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string ReadValue(AutomationElement edit)
        {
            try
            {
                return edit.Patterns.Value.Pattern.Value.Value ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string SafeId(AutomationElement element)
        {
            try
            {
                return element.AutomationId ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string SafeName(AutomationElement element)
        {
            try
            {
                return element.Name ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static bool EnsureForeground(IntPtr hwnd)
        {
            if (Win32.IsForeground(hwnd))
            {
                return true;
            }

            Win32.ShowWindow(hwnd, Win32.SwRestore);
            _ = Win32.SetForegroundWindow(hwnd);
            if (ControlTestSupport.WaitUntil(
                    () => Win32.IsForeground(hwnd),
                    TimeSpan.FromMilliseconds(500)))
            {
                return true;
            }

            IntPtr foreground = Win32.GetForegroundWindow();
            uint foregroundThread = GetWindowThreadProcessId(foreground, out _);
            uint currentThread = GetCurrentThreadId();
            bool attached = foregroundThread != 0 &&
                foregroundThread != currentThread &&
                AttachThreadInput(currentThread, foregroundThread, true);

            try
            {
                _ = Win32.SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached)
                {
                    _ = AttachThreadInput(currentThread, foregroundThread, false);
                }
            }

            return ControlTestSupport.WaitUntil(
                () => Win32.IsForeground(hwnd),
                TimeSpan.FromSeconds(2));
        }
    }

#pragma warning disable SYSLIB1054 // The small test-only P/Invoke surface does not need LibraryImport plumbing.
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
#pragma warning restore SYSLIB1054
}

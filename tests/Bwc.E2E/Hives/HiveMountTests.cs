using System.Text;
using Bwc.E2E.Controls;
using Bwc.E2E.Dialogs;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using Bwc.E2E.ImportExport;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Hives;

/// <summary>
/// The elevated File > Export (hive format), Load Hive and Unload Hive round trip (hives.md section 14). The mount is
/// HKEY_USERS\BWC-E2E-&lt;run-id&gt;, the only registry location outside HKCU\Software\BWC-E2E that the test touches.
/// It reports Inconclusive unless the process is elevated and holds both hive privileges, so it runs only through the
/// elevated runner. The finally block unloads a mount that is still loaded and deletes the hive file.
/// </summary>
[TestClass]
public sealed class HiveMountTests
{
    private const int MenuFile = 512;
    private const int CommandLoadHive = 680;
    private const int CommandUnloadHive = 681;
    private const int HiveFilterIndex = 2;
    private const string MountPrefix = "BWC-E2E-";
    private const string HkuRootName = "HKEY_USERS";
    private const string PromptRootId = "DialogRoot";
    private const string PromptLabelId = "PromptLabel";
    private const string NameInputId = "DialogTextInput";
    private const string PrimaryId = "DialogPrimary";
    private const string SecondaryId = "DialogSecondary";
    private const string LoadHiveCaption = "Load Hive";
    private const string SeedTextName = "HiveSeedText";
    private const string SeedTextValue = "mounted";
    private const string SeedDwordName = "HiveSeedDword";
    private const int SeedDwordValue = 7;
    private const string SeedBinaryName = "HiveSeedBinary";
    private const string AddressBarId = "AddressBar";
    private const string NotElevated =
        "Not elevated: the hive mount test runs only through the elevated runner (SeBackupPrivilege and SeRestorePrivilege).";

    /// <summary>The prompt as RT_DIALOG 104 lays it out: caption Load Hive, 252 x 53 DLU (TextPrompt template).</summary>
    private static readonly ClassicDialogTests.ControlRect[] PromptControls =
    [
        new(PromptLabelId, 65535, 6, 6, 134, 8),
        new(NameInputId, 1001, 6, 17, 240, 12),
        new(PrimaryId, 1, 142, 35, 50, 14),
        new(SecondaryId, 2, 196, 35, 50, 14),
    ];

    private static readonly string[] SeedValueNames = [SeedTextName, SeedDwordName, SeedBinaryName];
    private static readonly byte[] SeedBinaryValue = [0x01, 0x02, 0xAB];
    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Hives")]
    [TestCategory("Elevated")]
    public void Hives_ElevatedLoadAndUnloadRoundTripThroughTheMenu()
    {
        if (!HiveMountNative.HasHivePrivileges())
        {
            Assert.Inconclusive(NotElevated);
        }

        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);

        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("hives.elevated.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        string mount = MountPrefix + config.RunId;
        string hiveDir = Path.Combine(config.ArtifactsDir, "hive-mount", config.RunId);
        string hiveFile = Path.Combine(hiveDir, mount + ".dat");
        var sandbox = new SandboxRegistry(config);
        string? dataRoot = null;

        try
        {
            sandbox.Cleanup();
            Seed(sandbox);
            Directory.CreateDirectory(hiveDir);

            bool absentBefore = !MountExists(mount);
            E2EResults.Record(
                "hives.elevated.mount-absent",
                absentBefore,
                $"HKEY_USERS\\{mount} is absent before the load={absentBefore}.");
            FailIfNeeded();

            dataRoot = CreateFreshDataRoot(config, "hives-elevated");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);
            using var dialogs = new SystemFileDialogDriver(session);
            using var automation = new UIA3Automation();

            if (ExportHiveFile(session, driver, dialogs, config, sandbox, hiveFile) &&
                LoadMountThroughMenu(session, driver, dialogs, automation, mount, hiveFile))
            {
                CheckMountedValues(session, mount);
                _ = UnloadMountThroughMenu(session, driver, mount);
            }

            FailIfNeeded();
        }
        finally
        {
            RemoveMountAndFile(mount, hiveFile, hiveDir);
            ControlTestSupport.CleanupRegistry(config);
            DeleteDataRoot(dataRoot);
        }
    }

    private static void Seed(SandboxRegistry sandbox)
    {
        using RegistryKey root = sandbox.CreateSubKey(string.Empty);
        root.SetValue(SeedTextName, SeedTextValue, RegistryValueKind.String);
        root.SetValue(SeedDwordName, SeedDwordValue, RegistryValueKind.DWord);
        root.SetValue(SeedBinaryName, SeedBinaryValue, RegistryValueKind.Binary);
    }

    /// <summary>Exports the sandbox key as a hive file through File > Export and checks the file starts with "regf".</summary>
    private static bool ExportHiveFile(
        AppSession session,
        EditingDriver driver,
        SystemFileDialogDriver dialogs,
        E2EConfig config,
        SandboxRegistry sandbox,
        string hiveFile)
    {
        bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
        bool seeded = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.ListRowNames(session).Contains(SeedTextName, StringComparer.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(15));
        E2EGuards.RequireSandboxNavigation(session, sandbox, navigated && seeded, "hives.elevated.sandbox.navigate");
        _ = ControlTestSupport.EnsureForeground(session);
        E2EGuards.RequireSandboxSelection(session, sandbox, "hives.elevated.sandbox.export");

        if (!ExportRangeTests.OpenDialog(driver, dialogs, ExportRangeTests.MenuFileExport, "hives.elevated.export", out IntPtr dialog))
        {
            return false;
        }

        bool typed = dialogs.SelectFileType(dialog, HiveFilterIndex, "Hive", out string typeNotes);
        string nameNotes = "not attempted";
        if (typed)
        {
            typed = dialogs.TrySetFileName(dialog, hiveFile, out nameNotes);
        }

        string acceptNotes = "not attempted";
        bool accepted = typed && dialogs.TryAccept(dialog, "Save", out acceptNotes);
        bool closed = accepted && dialogs.WaitGone(dialog, TimeSpan.FromSeconds(10));
        E2EResults.Record(
            "hives.elevated.export.save-dialog",
            typed && accepted && closed,
            $"{typeNotes} {nameNotes} {acceptNotes} closed={closed}.");
        if (!closed)
        {
            return false;
        }

        bool written = ControlTestSupport.WaitUntil(
            () => File.Exists(hiveFile) && new FileInfo(hiveFile).Length > 0,
            TimeSpan.FromSeconds(20));
        string signature = written ? ReadSignature(hiveFile) : string.Empty;
        bool isHive = signature == "regf";
        E2EResults.Record(
            "hives.elevated.export.file",
            written && isHive,
            $"hive file written={written}, first four bytes='{signature}', expected 'regf'.");
        if (!(written && isHive))
        {
            return false;
        }

        // The file exists before the export finishes: the progress window can still be open and take the foreground
        // from the main window. The next step types into the address bar, so it waits for the app's other windows.
        IReadOnlyList<string> others = [];
        bool finished = ControlTestSupport.WaitUntil(
            () => (others = HiveMountNative.OtherVisibleWindows(session.ProcessId, session.MainHwnd)).Count == 0,
            TimeSpan.FromSeconds(60));
        E2EResults.Record(
            "hives.elevated.export.finished",
            finished,
            finished ? "No other window of the app is open after the export." : "Still open: " + string.Join(", ", others));
        return finished;
    }

    /// <summary>Selects HKEY_USERS, runs File > Load Hive, picks the file and names the mount in the Key Name prompt.</summary>
    private static bool LoadMountThroughMenu(
        AppSession session,
        EditingDriver driver,
        SystemFileDialogDriver dialogs,
        UIA3Automation automation,
        string mount,
        string hiveFile)
    {
        bool atRoot = SelectRootByAddressBar(session, HkuRootName);
        E2EResults.Record(
            "hives.elevated.root.selected",
            atRoot,
            $"tree selection '{HkuRootName}' after the address bar={atRoot}.");
        if (!atRoot)
        {
            return false;
        }

        driver.OpenMenu(MenuFile);
        AutomationElement? loadItem = driver.WaitForElement("MenuCommand_" + CommandLoadHive);
        bool enabled = IsEnabled(loadItem);
        E2EResults.Record("hives.elevated.load.enabled", enabled, $"Load Hive enabled at HKEY_USERS={enabled}.");
        _ = ControlTestSupport.CloseOpenMenus(session);
        if (!enabled)
        {
            return false;
        }

        driver.OpenMenu(MenuFile, CommandLoadHive);
        IntPtr fileDialog = dialogs.WaitForDialog(TimeSpan.FromSeconds(10));
        E2EResults.Record(
            "hives.elevated.load.file-dialog",
            fileDialog != IntPtr.Zero,
            $"file dialog hwnd=0x{fileDialog.ToInt64():X}.");
        if (fileDialog == IntPtr.Zero)
        {
            return false;
        }

        bool typed = dialogs.TrySetFileName(fileDialog, hiveFile, out string nameNotes);
        string acceptNotes = "not attempted";
        bool accepted = typed && dialogs.TryAccept(fileDialog, "Open", out acceptNotes);
        bool fileClosed = accepted && dialogs.WaitGone(fileDialog, TimeSpan.FromSeconds(10));
        E2EResults.Record(
            "hives.elevated.load.file",
            typed && accepted && fileClosed,
            $"{nameNotes} {acceptNotes} closed={fileClosed}.");
        if (!fileClosed)
        {
            return false;
        }

        AutomationElement? prompt = driver.WaitForElement(PromptRootId, TimeSpan.FromSeconds(10));
        E2EResults.Record("hives.elevated.prompt.open", prompt is not null, "the Key Name prompt opened after the file.");
        if (prompt is null)
        {
            return false;
        }

        CheckPromptLayout(driver, automation, prompt);
        return EnterNameAndAccept(session, driver, mount);
    }

    /// <summary>DLG-007: caption, label, tab order and geometry of RT_DIALOG 104, measured like ClassicDialogTests.</summary>
    private static void CheckPromptLayout(EditingDriver driver, UIA3Automation automation, AutomationElement prompt)
    {
        IntPtr hwnd = ClassicDialogTests.DialogHandle(prompt);
        bool foreground = ClassicDialogTests.EnsureForeground(hwnd);
        string caption = ClassicDialogTests.CaptionOf(automation, hwnd);
        E2EResults.Record(
            "hives.elevated.prompt.caption",
            caption == LoadHiveCaption,
            $"window caption='{caption}', expected '{LoadHiveCaption}', foreground={foreground}.");

        string label = driver.TextOf(PromptLabelId);
        E2EResults.Record(
            "hives.elevated.prompt.label",
            label.Contains("Key Name:", StringComparison.Ordinal),
            $"label='{label}', classic template label '&Key Name:'.");

        bool focusedOnInput = ControlTestSupport.WaitUntil(
            () => ClassicDialogTests.FocusedId(automation) == NameInputId,
            TimeSpan.FromSeconds(3));
        string tabs = ClassicDialogTests.TabSequence(automation, 3);
        string expectedTabs = $"{PrimaryId},{SecondaryId},{NameInputId}";
        E2EResults.Record(
            "hives.elevated.prompt.tab-order",
            focusedOnInput && tabs == expectedTabs,
            $"focus on the name box at open={focusedOnInput}; tab sequence='{tabs}', expected '{expectedTabs}'.");

        // The focused Edit draws a focus visual that changes its rectangle, so the default button is focused first.
        Keyboard.Type(VirtualKeyShort.TAB);
        ClassicDialogTests.CheckGeometry("hives.elevated.prompt.layout", prompt, hwnd, PromptControls, 252, 53);
    }

    /// <summary>DLG-008: control 1001 takes the key name, which is read back before OK; OK loads the hive.</summary>
    private static bool EnterNameAndAccept(AppSession session, EditingDriver driver, string mount)
    {
        bool wrote = driver.SetText(NameInputId, mount);
        string readBack = driver.TextOf(NameInputId);
        E2EResults.Record(
            "hives.elevated.prompt.name",
            wrote && readBack == mount,
            $"control 1001 reads '{readBack}' after typing '{mount}'.");
        if (!wrote || readBack != mount)
        {
            return false;
        }

        bool ok = driver.Invoke(PrimaryId);
        bool promptGone = driver.WaitForNoDialog(TimeSpan.FromSeconds(30));
        bool selected = ControlTestSupport.WaitForTreeSelection(session, mount, TimeSpan.FromSeconds(20));
        bool mounted = MountExists(mount);
        E2EResults.Record(
            "hives.elevated.load.mounted",
            ok && promptGone && selected && mounted,
            $"OK invoked={ok}, prompt closed={promptGone}, mount selected={selected}, HKEY_USERS\\{mount} exists={mounted}.");
        return ok && promptGone && selected && mounted;
    }

    /// <summary>The mounted key shows the seeded values in the list, and the managed API reads the same data.</summary>
    private static void CheckMountedValues(AppSession session, string mount)
    {
        bool inList = ControlTestSupport.WaitUntil(
            () => SeedValueNames.All(name => ControlTestSupport.ListRowNames(session).Contains(name, StringComparer.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(10));
        E2EResults.Record(
            "hives.elevated.load.values-listed",
            inList,
            $"list rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}], seeded=[{string.Join(",", SeedValueNames)}].");

        bool managed = MountHoldsSeed(mount, out string detail);
        E2EResults.Record("hives.elevated.load.values-read", managed, detail);
    }

    /// <summary>File > Unload Hive on the selected mount, confirmed with Yes. Returns true when the mount is gone.</summary>
    private static bool UnloadMountThroughMenu(AppSession session, EditingDriver driver, string mount)
    {
        bool selected = ControlTestSupport.WaitForTreeSelection(session, mount, TimeSpan.FromSeconds(10));
        E2EResults.Record("hives.elevated.unload.selected", selected, $"tree selection '{mount}'={selected}.");
        if (!selected)
        {
            return false;
        }

        driver.OpenMenu(MenuFile);
        AutomationElement? unloadItem = driver.WaitForElement("MenuCommand_" + CommandUnloadHive);
        bool enabled = IsEnabled(unloadItem);
        E2EResults.Record("hives.elevated.unload.enabled", enabled, $"Unload Hive enabled on the mount={enabled}.");
        _ = ControlTestSupport.CloseOpenMenus(session);
        if (!enabled)
        {
            return false;
        }

        driver.OpenMenu(MenuFile, CommandUnloadHive);
        AutomationElement? confirm = driver.WaitForElement(PromptRootId, TimeSpan.FromSeconds(10));
        string message = confirm is null ? string.Empty : driver.TextOf("DialogMessage");
        bool asked = confirm is not null && message.Contains(mount, StringComparison.Ordinal);
        E2EResults.Record("hives.elevated.unload.confirm", asked, $"confirmation names the mount={asked}: '{message}'.");
        if (!asked)
        {
            return false;
        }

        bool yes = driver.Invoke(PrimaryId);
        bool dialogClosed = driver.WaitForNoDialog(TimeSpan.FromSeconds(15));
        bool gone = ControlTestSupport.WaitUntil(() => !MountExists(mount), TimeSpan.FromSeconds(20));
        E2EResults.Record(
            "hives.elevated.unload.gone",
            yes && dialogClosed && gone,
            $"Yes invoked={yes}, dialog closed={dialogClosed}, HKEY_USERS\\{mount} gone={gone}.");
        return gone;
    }

    /// <summary>
    /// The finally block. A mount still loaded is unloaded with RegUnLoadKey. The hive file is deleted only once the mount
    /// is gone, because a loaded hive holds the file open.
    /// </summary>
    private static void RemoveMountAndFile(string mount, string hiveFile, string hiveDir)
    {
        bool mountLeft;
        try
        {
            mountLeft = MountExists(mount);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            E2EResults.Record("hives.elevated.finally.mount-check", false, ex.Message);
            return;
        }

        if (mountLeft)
        {
            int error = HiveMountNative.UnloadMount(mount);
            mountLeft = MountExists(mount);
            E2EResults.Record(
                "hives.elevated.finally.unload",
                !mountLeft,
                $"RegUnLoadKey on HKEY_USERS\\{mount} returned {error}; still loaded={mountLeft}.");
        }

        if (mountLeft)
        {
            E2EResults.Note("hives.elevated.finally.file", $"the mount is still loaded, so '{hiveFile}' was kept.");
            return;
        }

        try
        {
            if (File.Exists(hiveFile))
            {
                File.Delete(hiveFile);
            }

            if (Directory.Exists(hiveDir))
            {
                Directory.Delete(hiveDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            E2EResults.Record("hives.elevated.finally.file", false, ex.Message);
        }
    }

    private static bool MountExists(string mount)
    {
        using RegistryKey users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
        using RegistryKey? key = users.OpenSubKey(mount, writable: false);
        return key is not null;
    }

    private static bool MountHoldsSeed(string mount, out string detail)
    {
        using RegistryKey users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
        using RegistryKey? key = users.OpenSubKey(mount, writable: false);
        if (key is null)
        {
            detail = $"HKEY_USERS\\{mount} is not loaded.";
            return false;
        }

        string? text = key.GetValue(SeedTextName) as string;
        bool dwordOk = key.GetValue(SeedDwordName) is int dword && dword == SeedDwordValue;
        bool binaryOk = key.GetValue(SeedBinaryName) is byte[] binary && binary.SequenceEqual(SeedBinaryValue);
        bool ok = text == SeedTextValue && dwordOk && binaryOk;
        detail = $"string='{text}', dword ok={dwordOk}, binary ok={binaryOk}.";
        return ok;
    }

    private static bool SelectRootByAddressBar(AppSession session, string rootName)
    {
        if (!ControlTestSupport.FocusAddressBar(session))
        {
            return false;
        }

        AutomationElement? address = session.Find(AddressBarId);
        if (address is null)
        {
            return false;
        }

        try
        {
            address.AsTextBox().Text = rootName;
            Keyboard.Type(VirtualKeyShort.ENTER);
        }
        catch (Exception)
        {
            return false;
        }

        return ControlTestSupport.WaitForTreeSelection(session, rootName, TimeSpan.FromSeconds(10));
    }

    private static bool IsEnabled(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            return element.Properties.IsEnabled.Value;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string ReadSignature(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] head = new byte[4];
        int read = stream.Read(head, 0, head.Length);
        return read == head.Length ? Encoding.ASCII.GetString(head) : string.Empty;
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

    private static void DeleteDataRoot(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The exit path may still hold a settings file for a moment.
        }
    }

    private static void FailIfNeeded()
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Elevated hive checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}

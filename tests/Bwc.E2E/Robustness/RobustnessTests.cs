using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Bwc.E2E.ContextMenus;
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

namespace Bwc.E2E.Robustness;

/// <summary>
/// Checks the robustness fixes of the Registry Editor against the real application. Every write lands
/// under the run's sandbox key (HKCU\Software\BWC-E2E\run-id). The policy checks use the E2E-only
/// override variable and never read or write the real policy values. The rules are in
/// docs/failure-modes/basics.md and docs/failure-modes/app-services.md.
/// </summary>
[TestClass]
public sealed class RobustnessTests
{
    private const int MenuEdit = 513;
    private const int MenuEditModify = 912;
    private const int MenuEditDelete = 913;

    private const uint WmClose = 0x0010;


    private const string ListRowZebra = "ZebraString";
    private const string ListRowAlpha = "AlphaString";
    private const string ContextDeleteName = "Delete";
    private const string PolicyCaption = "Registry Editor";
    private const string PolicyMessage = "Registry editing has been disabled by your administrator.";

    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(30);

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    /// <summary>
    /// Delete from the value context menu deletes the value. The menu used to go through the focused pane,
    /// and with the tree focused it deleted the selected key with all its values.
    /// </summary>
    [TestMethod]
    [TestCategory("Robustness")]
    [TestCategory("Mutating")]
    public void Robustness_ValueMenuDeleteRemovesOnlyTheValue()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        PrepareExecutables(config);

        var sandbox = new SandboxRegistry(config);
        try
        {
            sandbox.Cleanup();
            SeedStrings(sandbox, ListRowAlpha, ListRowZebra);

            using AppSession session = LaunchSandboxed(config, sandbox, "robust-valuedelete");
            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "robust.valuedelete.navigate");
            bool rows = WaitForListRows(session, ListRowAlpha, ListRowZebra);

            using var menus = new ContextMenuDriver(session);
            using var driver = new EditingDriver(session);
            E2EGuards.RequireSandboxSelection(session, sandbox, "robust.valuedelete.sandbox");
            MenuSnapshot? snapshot = menus.RightClickForMenu(
                () => ControlTestSupport.FindRowByName(session, ControlTestSupport.ListControl, ControlType.DataItem, ListRowZebra),
                new[] { ContextDeleteName });
            bool opened = rows && snapshot is not null;
            bool invoked = opened && menus.Invoke(MenuEditDelete);
            bool dialog = invoked && driver.WaitForDialog();
            string message = dialog ? driver.TextOf("DialogMessage") : string.Empty;
            bool valueText = message.Contains("this value?", StringComparison.Ordinal);
            bool confirmed = valueText && driver.Invoke("DialogPrimary") && driver.WaitForNoDialog();
            bool removed = ControlTestSupport.WaitUntil(
                () => sandbox.ReadString(string.Empty, ListRowZebra) is null,
                UiTimeout);
            bool keyKept = sandbox.Exists(string.Empty) && sandbox.ReadString(string.Empty, ListRowAlpha) == ListRowAlpha;
            E2EResults.Record(
                "robust.valuedelete.dialog",
                opened && dialog && valueText,
                $"menu opened={opened}, dialog={dialog}, message='{message}'.");
            E2EResults.Record(
                "robust.valuedelete.removed",
                confirmed && removed && keyKept,
                $"confirmed={confirmed}, value removed={removed}, key and sibling kept={keyKept}.");

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    /// <summary>
    /// While a recursive delete runs on the registry thread, the owner window is disabled and takes no input.
    /// A close request sent during the delete is held, and the window closes after the delete finishes.
    /// </summary>
    [TestMethod]
    [TestCategory("Robustness")]
    [TestCategory("Mutating")]
    public void Robustness_BusyGateDisablesWindowDuringRecursiveDelete()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        PrepareExecutables(config);

        var sandbox = new SandboxRegistry(config);
        try
        {
            sandbox.Cleanup();
            SeedWideKey(sandbox, childCount: 4000);

            AppSession session = LaunchSandboxed(config, sandbox, "robust-busygate");
            using (session)
            {
                bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
                E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "robust.busygate.navigate");
                using var driver = new EditingDriver(session);

                bool selected = ControlTestSupport.NavigateToSandboxKey(session, config, "Wide");
                E2EGuards.RequireSandboxSelection(session, sandbox, "robust.busygate.sandbox");
                driver.OpenMenu(MenuEdit, MenuEditDelete);
                bool dialog = driver.WaitForDialog();
                bool confirmed = selected && dialog && driver.Invoke("DialogPrimary") && driver.WaitForNoDialog();

                bool sawDisabled = false;
                bool closeSent = false;
                DateTime deadline = DateTime.UtcNow + ProcessTimeout;
                while (confirmed && sandbox.Exists("Wide") && DateTime.UtcNow < deadline)
                {
                    if (!session.IsEnabled)
                    {
                        sawDisabled = true;
                        if (!closeSent)
                        {
                            // Posted, not sent: the window must still take the close while the write runs.
                            closeSent = PostMessage(session.MainHwnd, WmClose, nint.Zero, nint.Zero);
                        }
                    }

                    Thread.Sleep(2);
                }

                bool deleted = !sandbox.Exists("Wide");
                E2EResults.Record(
                    "robust.busygate.disabled",
                    confirmed && sawDisabled,
                    $"confirmed={confirmed}, owner disabled during the delete={sawDisabled}, close posted={closeSent}.");

                bool exited = WaitForProcessExit(session.ProcessId, ProcessTimeout);
                E2EResults.Record(
                    "robust.busygate.heldclose",
                    deleted && exited,
                    $"delete finished={deleted}, window closed after the write={exited}.");
            }

            FailIfNeeded();
        }
        finally
        {
            sandbox.Cleanup();
        }
    }

    /// <summary>
    /// A REG_DWORD whose data is not four bytes has no typed editor. Modify opens the binary editor.
    /// </summary>
    [TestMethod]
    [TestCategory("Robustness")]
    [TestCategory("Mutating")]
    public void Robustness_InvalidLengthDwordOpensBinaryEditor()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        PrepareExecutables(config);

        var sandbox = new SandboxRegistry(config);
        try
        {
            sandbox.Cleanup();
            SeedByImport(config, sandbox, "bad-dword.reg", "\"BadDword\"=hex(4):01,02");

            using AppSession session = LaunchSandboxed(config, sandbox, "robust-dword");
            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "robust.dword.navigate");
            bool rows = WaitForListRows(session, "BadDword");

            using var driver = new EditingDriver(session);
            _ = SelectListRow(session, "BadDword");
            E2EGuards.RequireListRowSelected(session, "BadDword", "robust.dword.row");
            driver.OpenMenu(MenuEdit, MenuEditModify);
            bool editor = driver.WaitForElement("HexEditor") is not null;
            bool cancelled = editor && driver.Invoke("DialogSecondary") && driver.WaitForNoDialog();
            E2EResults.Record(
                "robust.dword.binaryeditor",
                rows && editor && cancelled,
                $"rows={rows}, binary editor open={editor}, cancelled={cancelled}.");

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    /// <summary>
    /// A REG_RESOURCE_LIST value opens read-only: Modify shows the viewer and never the writable editor.
    /// </summary>
    [TestMethod]
    [TestCategory("Robustness")]
    [TestCategory("Mutating")]
    public void Robustness_ResourceListOpensReadOnly()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        PrepareExecutables(config);

        var sandbox = new SandboxRegistry(config);
        try
        {
            sandbox.Cleanup();
            SeedByImport(config, sandbox, "resource-list.reg", "\"Resources\"=hex(8):00,00,00,00,00,00,00,00,00,00,00,00");

            using AppSession session = LaunchSandboxed(config, sandbox, "robust-resource");
            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "robust.resource.navigate");
            bool rows = WaitForListRows(session, "Resources");

            using var driver = new EditingDriver(session);
            _ = SelectListRow(session, "Resources");
            E2EGuards.RequireListRowSelected(session, "Resources", "robust.resource.row");
            driver.OpenMenu(MenuEdit, MenuEditModify);
            // The viewer is the classic Binary Data dialog, identified by its read-only list (HexViewerList). The
            // writable editor has the HexEditor control and no list, so it must be absent for the value to be read-only.
            bool viewer = driver.WaitForElement("HexViewerList") is not null;
            bool writable = driver.WaitForElement("HexEditor", TimeSpan.FromSeconds(1)) is not null;
            Keyboard.Press(VirtualKeyShort.ESCAPE);
            bool closed = viewer && driver.WaitForNoDialog();
            E2EResults.Record(
                "robust.resource.readonly",
                rows && viewer && !writable && closed,
                $"rows={rows}, read-only viewer={viewer}, writable editor={writable}, closed={closed}.");

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    /// <summary>
    /// Expanding a key the current user cannot enumerate shows the classic "cannot be opened" error and
    /// collapses the node. HKEY_LOCAL_MACHINESECURITY is read-denied to a standard user, and expanding it
    /// writes nothing, so the check needs no sandbox writes.
    /// </summary>
    [TestMethod]
    [TestCategory("Robustness")]
    public void Robustness_ExpandOfDeniedKeyShowsClassicError()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        PrepareExecutables(config);

        var sandbox = new SandboxRegistry(config);
        using AppSession session = LaunchSandboxed(config, sandbox, "robust-expand");
        using var driver = new EditingDriver(session);

        bool machineOpen = ControlTestSupport.Expand(ControlTestSupport.FindRowByName(
            session, ControlTestSupport.TreeControl, ControlType.TreeItem, "HKEY_LOCAL_MACHINE"));
        AutomationElement? row = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.FindRowByName(session, ControlTestSupport.TreeControl, ControlType.TreeItem, "SECURITY") is not null,
            UiTimeout)
            ? ControlTestSupport.FindRowByName(session, ControlTestSupport.TreeControl, ControlType.TreeItem, "SECURITY")
            : null;
        bool expanded = machineOpen && row is not null && ControlTestSupport.Expand(row);
        bool dialog = expanded && driver.WaitForDialog();
        string text = dialog ? driver.TextOf("DialogErrorText") : string.Empty;
        bool classic = text.Contains("cannot be opened", StringComparison.Ordinal);
        bool dismissed = dialog && driver.DismissErrorBox();
        E2EResults.Record(
            "robust.expand.classicerror",
            machineOpen && row is not null && dialog && classic && dismissed,
            $"HKLM expanded={machineOpen}, SECURITY row={row is not null}, expanded={expanded}, dialog={dialog}, classic text={classic}, text={text}.");

        FailIfNeeded();
    }

    /// <summary>
    /// The tree context menu of an expanded key shows Collapse in place of Expand (RT_STRING 25).
    /// </summary>
    [TestMethod]
    [TestCategory("Robustness")]
    [TestCategory("Mutating")]
    public void Robustness_ExpandedKeyMenuShowsCollapse()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        PrepareExecutables(config);

        var sandbox = new SandboxRegistry(config);
        try
        {
            sandbox.Cleanup();
            SeedWideKey(sandbox, childCount: 2);

            using AppSession session = LaunchSandboxed(config, sandbox, "robust-collapse");
            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "robust.collapse.navigate");

            bool selected = ControlTestSupport.NavigateToSandboxKey(session, config, "Wide");
            AutomationElement? row = ControlTestSupport.FindRowByName(session, ControlTestSupport.TreeControl, ControlType.TreeItem, "Wide");
            bool expanded = selected && ControlTestSupport.Expand(row);
            using var menus = new ContextMenuDriver(session);
            MenuSnapshot? snapshot = expanded
                ? menus.RightClickForMenu(
                    () => ControlTestSupport.FindRowByName(session, ControlTestSupport.TreeControl, ControlType.TreeItem, "Wide"),
                    new[] { "Collapse", "New" })
                : null;
            string label = snapshot?.Find(915)?.Name ?? string.Empty;
            bool collapse = label == "Collapse";
            _ = ControlTestSupport.CloseOpenMenus(session);
            E2EResults.Record(
                "robust.collapse.label",
                expanded && snapshot is not null && collapse,
                $"expanded={expanded}, menu opened={snapshot is not null}, item 915 label='{label}'.");

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    /// <summary>
    /// <c>/e file</c> with no key exports every classic root, not HKEY_CURRENT_USER alone. The E2E build maps
    /// HKEY_CURRENT_USER to the sandbox and leaves the other roots empty, so the file keeps every root header.
    /// </summary>
    [TestMethod]
    [TestCategory("Robustness")]
    [TestCategory("Mutating")]
    public void Robustness_WholeRegistryExportWritesEveryRoot()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        PrepareExecutables(config);

        var sandbox = new SandboxRegistry(config);
        string tempDir = Path.Combine(config.ArtifactsDir, "robustness", config.RunId);
        string file = Path.Combine(tempDir, "whole-registry.reg");
        try
        {
            sandbox.Cleanup();
            SeedStrings(sandbox, "Export");
            ResetDirectory(tempDir);

            (int exit, bool exited, string stderr) = RunRegedit(config, sandbox, environment: null, "/e", file);
            byte[] bytes = File.Exists(file) ? File.ReadAllBytes(file) : Array.Empty<byte>();
            string text = DecodeRegFile(bytes);
            bool local = text.Contains("[HKEY_LOCAL_MACHINE", StringComparison.Ordinal);
            bool user = text.Contains("[HKEY_CURRENT_USER", StringComparison.Ordinal);
            E2EResults.Record(
                "robust.export.whole",
                exited && exit == 0 && local && user,
                $"exited={exited}, exit={exit}, HKLM header={local}, HKCU header={user}, bytes={bytes.Length}, stderr='{stderr.Trim()}'.");

            FailIfNeeded();
        }
        finally
        {
            sandbox.Cleanup();
            ResetDirectory(tempDir, remove: true);
        }
    }

    /// <summary>
    /// With DisableRegistryTools set to 1, the window is not created. The classic policy message is shown in a
    /// message box, and the process exits with code 1. The policy value comes from the E2E override only.
    /// </summary>
    [TestMethod]
    [TestCategory("Robustness")]
    [TestCategory("E2EOnly")]
    public void Robustness_PolicyOverrideShowsRegistryEditingDisabled()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        PrepareExecutables(config);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BWC_E2E_DISABLE_REGISTRY_TOOLS"] = "1",
        };
        using Process process = StartRegedit(config, "robust-policy", environment, Array.Empty<string>());
        using var automation = new UIA3Automation();
        AutomationElement? box = null;
        bool shown = ControlTestSupport.WaitUntil(
            () => (box = FindOwnedMessageBox(automation, process.Id)) is not null,
            ProcessTimeout);
        bool text = shown && box is not null && ContainsText(box, PolicyMessage);
        if (box is not null)
        {
            box.FindFirstDescendant(cf => cf.ByName("OK"))?.AsButton().Invoke();
        }

        bool exited = process.WaitForExit((int)ProcessTimeout.TotalMilliseconds);
        E2EResults.Record(
            "robust.policy.message",
            shown && text,
            $"message box shown={shown}, text matches={text}.");
        E2EResults.Record(
            "robust.policy.exit",
            exited && process.ExitCode == 1,
            $"exited={exited}, exit={(exited ? process.ExitCode : -1)}.");

        FailIfNeeded();
    }

    /// <summary>
    /// With DisableRegistryTools set to 2, a silent import is refused: the message goes to standard error, the exit
    /// code is 1, and nothing is written.
    /// </summary>
    [TestMethod]
    [TestCategory("Robustness")]
    [TestCategory("E2EOnly")]
    public void Robustness_PolicyLevelTwoRefusesSilentImport()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        PrepareExecutables(config);

        var sandbox = new SandboxRegistry(config);
        string tempDir = Path.Combine(config.ArtifactsDir, "robustness", config.RunId);
        try
        {
            sandbox.Cleanup();
            ResetDirectory(tempDir);
            string file = Path.Combine(tempDir, "policy-import.reg");
            Directory.CreateDirectory(tempDir);
            File.WriteAllBytes(file, EncodeV5(
                "Windows Registry Editor Version 5.00\r\n\r\n"
                + "[HKEY_CURRENT_USER\\" + sandbox.RunRootPath + "\\PolicyBlocked]\r\n"
                + "\"Value\"=\"blocked\"\r\n"));

            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["BWC_E2E_DISABLE_REGISTRY_TOOLS"] = "2",
            };
            (int exit, bool exited, string stderr) = RunRegedit(config, sandbox, environment, "/s", file);
            bool written = sandbox.Exists("PolicyBlocked");
            E2EResults.Record(
                "robust.policy.silentimport",
                exited && exit == 1 && !written && stderr.Contains(PolicyMessage, StringComparison.Ordinal),
                $"exited={exited}, exit={exit}, written={written}, stderr='{stderr.Trim()}'.");

            FailIfNeeded();
        }
        finally
        {
            sandbox.Cleanup();
            ResetDirectory(tempDir, remove: true);
        }
    }

    private static void PrepareExecutables(E2EConfig config)
    {
        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("robust.publish", false, ex.Message);
            Assert.Fail(ex.Message);
        }
    }

    private static AppSession LaunchSandboxed(E2EConfig config, SandboxRegistry sandbox, string scope)
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

    private static void SeedStrings(SandboxRegistry sandbox, params string[] names)
    {
        foreach (string name in names)
        {
            sandbox.SetString(string.Empty, name, name);
        }

        sandbox.SetString(string.Empty, string.Empty, "default text");
    }

    private static void SeedWideKey(SandboxRegistry sandbox, int childCount)
    {
        sandbox.CreateKey("Wide");
        for (var index = 1; index <= childCount; index++)
        {
            string child = @"Wide\Wide" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
            sandbox.CreateKey(child);
            sandbox.SetDword(child, "Value", index);
        }
    }

    /// <summary>
    /// Writes values of an exact type through the product's own silent import, the registry writer that the
    /// E2E sandbox allows. The managed API cannot write a two-byte REG_DWORD or a REG_RESOURCE_LIST.
    /// </summary>
    private static void SeedByImport(E2EConfig config, SandboxRegistry sandbox, string fileName, string valueLine)
    {
        string dir = Path.Combine(config.ArtifactsDir, "robustness", config.RunId);
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, fileName);
        File.WriteAllBytes(file, EncodeV5(
            "Windows Registry Editor Version 5.00\r\n\r\n"
            + "[HKEY_CURRENT_USER\\" + sandbox.RunRootPath + "]\r\n"
            + valueLine + "\r\n"));
        E2EGuards.RequireImportFileInsideSandbox(file, sandbox, "robust.seed.target");

        // The import may create keys only inside the run root, so it cannot create HKCU\Software\BWC-E2E when
        // Cleanup removed it. The run root is created here first, as every other seed path does.
        sandbox.CreateKey(string.Empty);
        (int exit, bool exited, string stderr) = RunRegedit(config, sandbox, environment: null, "/s", file);
        E2EResults.Record(
            "robust.seed." + fileName,
            exited && exit == 0,
            $"seed import exited={exited}, exit={exit}, stderr='{stderr.Trim()}'.");
    }

    /// <summary>Selects one list row, re-finding it on each attempt because the list can rebuild its rows.</summary>
    private static bool SelectListRow(AppSession session, string rowName) =>
        ControlTestSupport.WaitUntil(
            () =>
            {
                ControlTestSupport.Select(ControlTestSupport.FindRowByName(
                    session,
                    ControlTestSupport.ListControl,
                    ControlType.DataItem,
                    rowName));
                return ControlTestSupport.IsSelected(ControlTestSupport.FindRowByName(
                    session,
                    ControlTestSupport.ListControl,
                    ControlType.DataItem,
                    rowName));
            },
            UiTimeout);

    /// <summary>True when the process has exited within the timeout. A process that is already gone counts as exited.</summary>
    private static bool WaitForProcessExit(int processId, TimeSpan timeout)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static bool WaitForListRows(AppSession session, params string[] names) =>
        ControlTestSupport.WaitUntil(
            () =>
            {
                string[] listed = ControlTestSupport.ListRowNames(session);
                return names.All(name => listed.Contains(name, StringComparer.OrdinalIgnoreCase));
            },
            TimeSpan.FromSeconds(15));

    private static bool ContainsText(AutomationElement box, string expected) =>
        box.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .Any(element => element.Name.Contains(expected, StringComparison.Ordinal));

    private static AutomationElement? FindOwnedMessageBox(UIA3Automation automation, int processId) =>
        automation.GetDesktop().FindAllChildren(cf => cf.ByName(PolicyCaption))
            .FirstOrDefault(window => window.Properties.ProcessId.IsSupported &&
                window.Properties.ProcessId.Value == processId);

    private static Process StartRegedit(
        E2EConfig config,
        string scope,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(config.RegeditExePath)
        {
            UseShellExecute = false,
            WorkingDirectory = config.PublishDir,
            CreateNoWindow = false,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["__COMPAT_LAYER"] = "RunAsInvoker";
        startInfo.Environment[E2EConfig.DataRootEnvironmentVariable] = config.DataRootFor(scope);
        foreach (KeyValuePair<string, string> pair in environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Process.Start returned no process for '{config.RegeditExePath}'.");
    }

    /// <summary>Runs the console path to completion. A process that does not exit in time is killed and reported.</summary>
    private static (int Exit, bool Exited, string StandardError) RunRegedit(
        E2EConfig config,
        SandboxRegistry sandbox,
        IReadOnlyDictionary<string, string>? environment,
        params string[] arguments)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
        };
        if (environment is not null)
        {
            foreach (KeyValuePair<string, string> pair in environment)
            {
                merged[pair.Key] = pair.Value;
            }
        }

        using Process process = StartRegedit(config, "robust-cli", merged, arguments);
        bool exited = process.WaitForExit((int)ProcessTimeout.TotalMilliseconds);
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            _ = process.WaitForExit(5000);
        }

        string stderr = process.StandardError.ReadToEnd();
        return (exited ? process.ExitCode : -1, exited, stderr);
    }

    private static string DecodeRegFile(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        return Encoding.ASCII.GetString(bytes);
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

    private static void ResetDirectory(string path, bool remove = false)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        if (!remove)
        {
            Directory.CreateDirectory(path);
        }
    }

    private static void FailIfNeeded()
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Robustness checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

}

using System.ComponentModel;
using System.Runtime.InteropServices;
using Bwc.E2E.Controls;
using Bwc.E2E.Dialogs;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.ContextMenus;

/// <summary>
/// Drives the real Registry Editor context menus against a seeded HKCU sandbox: the tree key menu,
/// the value row menu, the empty list background menu, and the keyboard Delete, Ctrl+C, F5, and
/// Shift+F10 paths. Modal dialogs are cancelled so the test never needs elevation.
/// </summary>
[TestClass]
public sealed class ContextMenuTests
{
    private const string StringValueName = "CtxString";
    private const string BinaryValueName = "CtxBinary";
    private const string DwordValueName = "CtxDword";
    private const string ChildKeyName = "CtxChild";
    private const string NewKeyName = "CtxNewKey";
    private const uint CfUnicodeText = 13;

    private static readonly string[] TreeMenuNames =
    [
        "Expand",
        "New",
        "Find...",
        "Delete",
        "Rename",
        "Export",
        "Permissions...",
        "Copy Key Name",
        "Go to HKEY_LOCAL_MACHINE",
    ];

    private static readonly string[] ValueMenuNames =
    [
        "Modify...",
        "Modify Binary Data...",
        "Delete",
        "Rename",
    ];

    private static readonly string[] BackgroundMenuNames = ["New"];

    private static readonly int[] NewSubmenuIds = [663, 664, 665, 674, 688, 676, 677];

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("ContextMenu")]
    [TestCategory("Mutating")]
    public void ContextMenus_OpenAndInvokeCommands()
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
            E2EResults.Record("contextmenu.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        string? dataRoot = null;

        try
        {
            sandbox.Cleanup();
            Seed(sandbox);
            dataRoot = CreateFreshDataRoot(config, "contextmenu");

            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);
            using var menus = new ContextMenuDriver(session);

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            bool seededRows = ControlTestSupport.WaitUntil(
                () => ContainsAll(
                    ControlTestSupport.ListRowNames(session),
                    StringValueName,
                    BinaryValueName,
                    DwordValueName),
                TimeSpan.FromSeconds(15));
            E2EResults.Record(
                "contextmenu.navigate",
                navigated && seededRows,
                $"navigated={navigated}, seededRows={seededRows}, rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}].");

            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated && seededRows, "contextmenu.sandbox.navigate");
            CheckTreeMenu(session, driver, menus, sandbox, config);
            CheckValueMenu(session, driver, menus, sandbox);
            CheckBackgroundMenu(session, menus);
            CheckKeyboard(session, driver, menus, sandbox, config);

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            DeleteDataRoot(dataRoot);
        }
    }

    private static void CheckTreeMenu(
        AppSession session,
        EditingDriver driver,
        ContextMenuDriver menus,
        SandboxRegistry sandbox,
        E2EConfig config)
    {
        AutomationElement? row = FindTreeRow(session, config.RunId);
        bool clicked = menus.RightClick(row);
        MenuSnapshot? snapshot = menus.WaitForMenu(TreeMenuNames);
        string actual = NamesOf(snapshot);

        E2EResults.Record(
            "contextmenu.tree.open",
            clicked && snapshot is not null,
            $"rightClick={clicked}, names=[{actual}].");
        E2EResults.Record(
            "contextmenu.tree.order",
            snapshot is not null && NamesEqual(snapshot.Names, TreeMenuNames),
            $"expected=[{string.Join(",", TreeMenuNames)}], actual=[{actual}].");

        MenuEntry? permissions = snapshot?.Find(678);
        E2EResults.Record(
            "contextmenu.tree.permissions.enabled",
            permissions is { Enabled: true },
            $"permissionsPresent={permissions is not null}, enabled={permissions?.Enabled}.");

        bool copyInvoked = menus.Invoke(675);
        string expectedKeyPath = ComputerPath(sandbox);
        bool copied = copyInvoked && WaitForClipboardText(expectedKeyPath, TimeSpan.FromSeconds(5));
        E2EResults.Record(
            "contextmenu.tree.copykey",
            copyInvoked && copied,
            $"invoked={copyInvoked}, expected='{expectedKeyPath}', copied={copied}.");

        row = FindTreeRow(session, config.RunId);
        bool expandMenu = menus.RightClick(row) && menus.WaitForMenu(TreeMenuNames) is not null;
        bool expandInvoked = expandMenu && menus.Invoke(915);
        bool childVisible = expandInvoked &&
            ControlTestSupport.WaitForTreeRow(session, ChildKeyName, TimeSpan.FromSeconds(10));
        E2EResults.Record(
            "contextmenu.tree.expand",
            expandInvoked && childVisible,
            $"menu={expandMenu}, invoked={expandInvoked}, childVisible={childVisible}.");

        row = FindTreeRow(session, config.RunId);
        bool newMenu = menus.RightClick(row) && menus.WaitForMenu(TreeMenuNames) is not null;
        bool submenu = newMenu && menus.OpenSubmenu(516) && menus.WaitForItem(663) is not null;
        E2EGuards.RequireSandboxSelection(session, sandbox, "contextmenu.sandbox.newkey");
        bool newInvoked = submenu && menus.Invoke(663);
        bool dialog = newInvoked && driver.WaitForDialog();
        bool input = dialog && driver.WaitForElement("DialogTextInput") is not null;
        // The classic prompt has no heading. Its window title is the New Key caption.
        AutomationElement? promptRoot = dialog ? driver.WaitForElement("DialogRoot") : null;
        string heading = promptRoot is null ? string.Empty : ControlTestSupport.SafeName(ClassicDialogTests.WindowOf(promptRoot));
        bool cancelled = input && driver.Invoke("DialogSecondary") && driver.WaitForNoDialog();
        bool keyAbsent = !sandbox.Exists(NewKeyName);
        E2EResults.Record(
            "contextmenu.tree.newkey",
            submenu && newInvoked && dialog && heading.StartsWith("New Key", StringComparison.Ordinal) &&
                cancelled && keyAbsent,
            $"submenu={submenu}, invoked={newInvoked}, dialog={dialog}, title='{heading}', cancelled={cancelled}, keyAbsent={keyAbsent}.");
    }

    private static void CheckValueMenu(
        AppSession session,
        EditingDriver driver,
        ContextMenuDriver menus,
        SandboxRegistry sandbox)
    {
        AutomationElement? row = FindListRow(session, BinaryValueName);
        bool clicked = menus.RightClick(row);
        MenuSnapshot? snapshot = menus.WaitForMenu(ValueMenuNames);
        string actual = NamesOf(snapshot);

        E2EResults.Record(
            "contextmenu.value.open",
            clicked && snapshot is not null,
            $"rightClick={clicked}, names=[{actual}].");
        E2EResults.Record(
            "contextmenu.value.order",
            snapshot is not null && NamesEqual(snapshot.Names, ValueMenuNames),
            $"expected=[{string.Join(",", ValueMenuNames)}], actual=[{actual}].");

        MenuEntry? modify = snapshot?.Find(912);
        MenuEntry? modifyBinary = snapshot?.Find(917);
        E2EResults.Record(
            "contextmenu.value.enabled",
            modifyBinary is { Enabled: true },
            $"modifyBinaryEnabled={modifyBinary?.Enabled}.");
        if (modify is null)
        {
            E2EResults.Record("contextmenu.value.modify", false, "Modify... was not found in the value menu.");
        }
        else
        {
            E2EResults.Record(
                "contextmenu.value.modify",
                modify.Enabled,
                $"Modify... enabled={modify.Enabled} on the REG_BINARY value; the oracle enables it for every value type.");
        }

        E2EGuards.RequireSandboxSelection(session, sandbox, "contextmenu.sandbox.modifybinary");
        bool binaryInvoked = menus.Invoke(917);
        bool dialog = binaryInvoked && driver.WaitForDialog();
        // The hex editor is a classic dialog, so its root is DialogRoot. Its window title tells it apart from the other dialogs.
        AutomationElement? hexRoot = dialog ? driver.WaitForElement("DialogRoot") : null;
        bool hexDialog = hexRoot is not null &&
            string.Equals(ControlTestSupport.SafeName(ClassicDialogTests.WindowOf(hexRoot)), "Edit Binary Value", StringComparison.Ordinal);
        bool hexEditor = dialog && driver.WaitForElement("HexEditor") is not null;
        bool cancelled = hexEditor && driver.Invoke("DialogSecondary") && driver.WaitForNoDialog();
        bool unchanged = sandbox.ReadBinary(string.Empty, BinaryValueName) is { Length: 5 };
        E2EResults.Record(
            "contextmenu.value.modifybinary",
            binaryInvoked && hexDialog && hexEditor && cancelled && unchanged,
            $"invoked={binaryInvoked}, hexDialog={hexDialog}, hexEditor={hexEditor}, cancelled={cancelled}, unchanged={unchanged}.");

        row = FindListRow(session, BinaryValueName);
        bool renameMenu = menus.RightClick(row) && menus.WaitForMenu(ValueMenuNames) is not null;
        E2EGuards.RequireSandboxSelection(session, sandbox, "contextmenu.sandbox.rename");
        bool renameInvoked = renameMenu && menus.Invoke(914);
        bool renameBox = renameInvoked && ControlTestSupport.WaitUntil(
            () => session.Find("ListRenameBox") is not null,
            TimeSpan.FromSeconds(5));
        bool escaped = renameBox && SendKeys(session, "ESCAPE");
        bool renameClosed = escaped && ControlTestSupport.WaitUntil(
            () => session.Find("ListRenameBox") is null,
            TimeSpan.FromSeconds(5));
        E2EResults.Record(
            "contextmenu.value.rename",
            renameInvoked && renameBox && renameClosed,
            $"menu={renameMenu}, invoked={renameInvoked}, renameBox={renameBox}, escaped={escaped}, closed={renameClosed}.");

        row = FindListRow(session, BinaryValueName);
        bool deleteMenu = menus.RightClick(row) && menus.WaitForMenu(ValueMenuNames) is not null;
        E2EGuards.RequireSandboxSelection(session, sandbox, "contextmenu.sandbox.delete");
        bool deleteInvoked = deleteMenu && menus.Invoke(913);
        bool deleteDialog = deleteInvoked && driver.WaitForDialog();
        bool deleteCancelled = deleteDialog &&
            driver.Invoke("DialogSecondary") &&
            driver.WaitForNoDialog();
        bool stillPresent = sandbox.ReadBinary(string.Empty, BinaryValueName) is { Length: 5 };
        E2EResults.Record(
            "contextmenu.value.delete",
            deleteInvoked && deleteDialog && deleteCancelled && stillPresent,
            $"menu={deleteMenu}, invoked={deleteInvoked}, dialog={deleteDialog}, cancelled={deleteCancelled}, stillPresent={stillPresent}.");
    }

    private static void CheckBackgroundMenu(AppSession session, ContextMenuDriver menus)
    {
        AutomationElement? list = session.Find(ControlTestSupport.ListControl);
        if (list is null)
        {
            E2EResults.Record("contextmenu.background.open", false, "ListControl was not found.");
            return;
        }

        System.Drawing.Rectangle bounds = list.BoundingRectangle;
        int x = bounds.Left + 120;
        int y = Math.Min(bounds.Bottom - 40, bounds.Top + 220);
        bool clicked = menus.RightClick(x, y);
        MenuSnapshot? snapshot = menus.WaitForMenu(BackgroundMenuNames);
        string actual = NamesOf(snapshot);

        E2EResults.Record(
            "contextmenu.background.open",
            clicked && snapshot is not null,
            $"rightClick=({x},{y}), clicked={clicked}, names=[{actual}].");

        bool onlyNew = snapshot is not null &&
            NamesEqual(snapshot.Names, BackgroundMenuNames) &&
            snapshot.Find(663) is null &&
            snapshot.Find(912) is null &&
            snapshot.Find(913) is null &&
            snapshot.Find(914) is null;
        E2EResults.Record(
            "contextmenu.background.onlynew",
            onlyNew,
            $"expected=[New], actual=[{actual}].");

        bool submenu = clicked && menus.OpenSubmenu(516);
        var present = new List<int>();
        var missing = new List<int>();
        if (submenu)
        {
            foreach (int commandId in NewSubmenuIds)
            {
                if (menus.WaitForItem(commandId, TimeSpan.FromSeconds(5)) is not null)
                {
                    present.Add(commandId);
                }
                else
                {
                    missing.Add(commandId);
                }
            }
        }

        E2EResults.Record(
            "contextmenu.background.submenu",
            submenu && missing.Count == 0,
            $"submenu={submenu}, present=[{string.Join(",", present)}], missing=[{string.Join(",", missing)}].");

        _ = SendKeys(session, "ESCAPE");
        _ = menus.WaitForNoMenu(TimeSpan.FromSeconds(3));
    }

    private static void CheckKeyboard(
        AppSession session,
        EditingDriver driver,
        ContextMenuDriver menus,
        SandboxRegistry sandbox,
        E2EConfig config)
    {
        AutomationElement? row = FindListRow(session, BinaryValueName);
        ControlTestSupport.Select(row);
        _ = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.IsSelected(FindListRow(session, BinaryValueName)),
            TimeSpan.FromSeconds(5));

        E2EGuards.RequireListRowSelected(session, BinaryValueName, "contextmenu.sandbox.keyboard.delete.row");
        E2EGuards.RequireSandboxSelection(session, sandbox, "contextmenu.sandbox.keyboard.delete");
        bool deleteSent = SendKeys(session, "DELETE");
        bool deleteDialog = deleteSent && driver.WaitForDialog();
        bool deleteCancelled = deleteDialog &&
            driver.Invoke("DialogSecondary") &&
            driver.WaitForNoDialog();
        bool stillPresent = sandbox.ReadBinary(string.Empty, BinaryValueName) is { Length: 5 };
        E2EResults.Record(
            "contextmenu.keyboard.delete",
            deleteSent && deleteDialog && deleteCancelled && stillPresent,
            $"sent={deleteSent}, dialog={deleteDialog}, cancelled={deleteCancelled}, stillPresent={stillPresent}.");

        AutomationElement? treeRow = FindTreeRow(session, config.RunId);
        ControlTestSupport.Select(treeRow);
        _ = ControlTestSupport.WaitForTreeSelection(session, config.RunId);
        bool copySent = SendKeys(session, "CONTROL+KEY_C");
        string expectedKeyPath = ComputerPath(sandbox);
        bool copied = copySent && WaitForClipboardText(expectedKeyPath, TimeSpan.FromSeconds(5));
        E2EResults.Record(
            "contextmenu.keyboard.copykey",
            copySent && copied,
            $"sent={copySent}, expected='{expectedKeyPath}', copied={copied}.");

        bool f5Sent = SendKeys(session, "F5");
        bool refreshed = f5Sent &&
            ControlTestSupport.WaitForTreeSelection(session, config.RunId, TimeSpan.FromSeconds(15)) &&
            ControlTestSupport.WaitUntil(
                () => ControlTestSupport.ListRowNames(session).Contains(BinaryValueName, StringComparer.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(15));
        E2EResults.Record(
            "contextmenu.keyboard.f5",
            f5Sent && refreshed,
            $"sent={f5Sent}, refreshed={refreshed}, rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}].");

        row = FindListRow(session, BinaryValueName);
        ControlTestSupport.Select(row);
        _ = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.IsSelected(FindListRow(session, BinaryValueName)),
            TimeSpan.FromSeconds(5));
        bool shiftF10 = SendKeys(session, "SHIFT+F10");
        MenuSnapshot? snapshot = shiftF10 ? menus.WaitForMenu(ValueMenuNames) : null;
        bool keyboardMenu = snapshot is not null && NamesEqual(snapshot.Names, ValueMenuNames);
        E2EResults.Record(
            "contextmenu.keyboard.shiftf10",
            keyboardMenu,
            $"sent={shiftF10}, names=[{NamesOf(snapshot)}].");
        _ = SendKeys(session, "ESCAPE");
        _ = menus.WaitForNoMenu(TimeSpan.FromSeconds(3));
    }

    private static void Seed(SandboxRegistry sandbox)
    {
        using (var root = sandbox.CreateSubKey(string.Empty))
        {
            root.SetValue(StringValueName, "context", RegistryValueKind.String);
            root.SetValue(BinaryValueName, new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50 }, RegistryValueKind.Binary);
            root.SetValue(DwordValueName, 7, RegistryValueKind.DWord);
        }

        using var child = sandbox.CreateSubKey(ChildKeyName);
        child.SetValue("Inside", "child", RegistryValueKind.String);
    }

    private static AutomationElement? FindTreeRow(AppSession session, string name) =>
        ControlTestSupport.FindRowByName(
            session,
            ControlTestSupport.TreeControl,
            ControlType.TreeItem,
            name);

    private static AutomationElement? FindListRow(AppSession session, string name) =>
        ControlTestSupport.FindRowByName(
            session,
            ControlTestSupport.ListControl,
            ControlType.DataItem,
            name);

    private static bool SendKeys(AppSession session, string key)
    {
        try
        {
            ControlTestSupport.SendKeys(session, key);
            return true;
        }
        catch (Exception ex)
        {
            E2EResults.Record("contextmenu.keys", false, $"'{key}' failed: {ex.Message}");
            return false;
        }
    }

    private static string ComputerPath(SandboxRegistry sandbox) =>
        "Computer\\HKEY_CURRENT_USER\\" + sandbox.RunRootPath;

    private static string NamesOf(MenuSnapshot? snapshot) =>
        snapshot is null ? "<none>" : string.Join(",", snapshot.Names);

    private static bool NamesEqual(IReadOnlyList<string> actual, IReadOnlyList<string> expected)
    {
        if (actual.Count != expected.Count)
        {
            return false;
        }

        for (var index = 0; index < actual.Count; index++)
        {
            if (!string.Equals(actual[index], expected[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsAll(IReadOnlyList<string> names, params string[] expected)
    {
        foreach (string name in expected)
        {
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The copied text is a key path, and registry key paths are case-insensitive.</summary>
    private static bool WaitForClipboardText(string expected, TimeSpan timeout)
    {
        string text = string.Empty;
        return ControlTestSupport.WaitUntil(
            () => TryReadClipboardText(out text, out _) &&
                string.Equals(text, expected, StringComparison.OrdinalIgnoreCase),
            timeout);
    }

    private static bool TryReadClipboardText(out string text, out string? error)
    {
        text = string.Empty;
        error = null;

        if (!OpenClipboard(IntPtr.Zero))
        {
            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        try
        {
            IntPtr handle = GetClipboardData(CfUnicodeText);
            if (handle == IntPtr.Zero)
            {
                error = "The clipboard has no Unicode text.";
                return false;
            }

            IntPtr pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            try
            {
                text = Marshal.PtrToStringUni(pointer) ?? string.Empty;
                return true;
            }
            finally
            {
                _ = GlobalUnlock(handle);
            }
        }
        finally
        {
            _ = CloseClipboard();
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
            // The scoped settings folder is left for the next run if the exiting application
            // still holds a file for a moment.
        }
    }

    private static void FailIfNeeded()
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Context menu checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr memory);
}

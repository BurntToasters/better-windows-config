using System.Globalization;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Editing;

/// <summary>
/// Drives the real Registry Editor menus and modal dialogs against a seeded HKCU sandbox: new key,
/// new DWORD, rename value, delete value, and recursive delete of a key with children.
/// </summary>
[TestClass]
public sealed class EditingTests
{
    private const int MenuEdit = 513;
    private const int MenuEditNew = 516;
    private const int MenuEditNewKey = 663;
    private const int MenuEditNewDword = 674;
    private const int MenuEditDelete = 913;

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Editing")]
    public void Editing_CreatesRenamesAndDeletesRegistryObjects()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("editing.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        try
        {
            sandbox.Cleanup();
            Seed(sandbox);

            string dataRoot = CreateFreshDataRoot(config, "editing");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EResults.Record(
                "editing.navigate",
                navigated,
                $"navigated to '{sandbox.RunRootDisplayPath}'={navigated}.");

            bool seededRows = ControlTestSupport.WaitUntil(
                () => ContainsAll(
                    ControlTestSupport.ListRowNames(session),
                    "AlphaString",
                    "SampleBinary",
                    "ZebraString"),
                TimeSpan.FromSeconds(15));
            E2EResults.Record(
                "editing.seed.values",
                seededRows,
                $"list rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}].");

            CheckNewKey(session, sandbox, driver);
            bool reselected = SelectRunKey(session, sandbox);
            E2EResults.Record(
                "editing.reselect.root",
                reselected,
                $"reselect={reselected}.");
            CheckNewDword(session, sandbox, driver);
            CheckRenameValue(session, driver, sandbox);
            CheckDeleteValue(session, driver, sandbox);
            CheckDeleteKey(session, driver, sandbox);

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    private static void CheckNewKey(AppSession session, SandboxRegistry sandbox, EditingDriver driver)
    {
        driver.OpenMenu(MenuEdit, MenuEditNew, MenuEditNewKey);
        bool dialog = driver.WaitForDialog();
        bool input = driver.WaitForElement("DialogTextInput") is not null;
        E2EResults.Record(
            "editing.newkey.dialog",
            dialog && input,
            $"DialogRoot={dialog}, DialogTextInput={input}.");

        bool typed = driver.SetText("DialogTextInput", "E2ENewKey");
        bool invoked = typed && driver.Invoke("DialogPrimary");
        bool closed = driver.WaitForNoDialog();
        E2EResults.Record(
            "editing.newkey.submit",
            typed && invoked && closed,
            $"typed={typed}, invoked={invoked}, closed={closed}.");

        bool exists = ControlTestSupport.WaitUntil(
            () => KeyExists(sandbox, "E2ENewKey"),
            TimeSpan.FromSeconds(10));
        bool selected = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.IsSelected(
                ControlTestSupport.FindRowByName(
                    session,
                    ControlTestSupport.TreeControl,
                    ControlType.TreeItem,
                    "E2ENewKey")),
            TimeSpan.FromSeconds(10));
        E2EResults.Record(
            "editing.newkey.exists",
            exists && selected,
            $"HKCU\\{sandbox.RunRootPath}\\E2ENewKey exists={exists}, selected={selected}.");
    }

    private static void CheckNewDword(AppSession session, SandboxRegistry sandbox, EditingDriver driver)
    {
        driver.OpenMenu(MenuEdit, MenuEditNew, MenuEditNewDword);
        bool nameDialog = driver.WaitForDialog() && driver.WaitForElement("DialogTextInput") is not null;
        bool named = nameDialog && driver.SetText("DialogTextInput", "E2ENewDword");
        bool nameAccepted = named && driver.Invoke("DialogPrimary");

        bool valueDialog = nameAccepted && driver.WaitForElement("ValueEditorInput") is not null;
        bool typed = valueDialog && driver.SetText("ValueEditorInput", "0x1234ABCD");
        bool saved = typed && driver.Invoke("DialogPrimary");
        bool closed = driver.WaitForNoDialog();
        E2EResults.Record(
            "editing.newdword.dialog",
            nameDialog && named && nameAccepted && valueDialog && typed && saved && closed,
            $"nameDialog={nameDialog}, nameAccepted={nameAccepted}, valueDialog={valueDialog}, saved={saved}, closed={closed}.");

        bool exists = ControlTestSupport.WaitUntil(
            () => KeyExists(sandbox, string.Empty) && ValueKindOf(sandbox, "E2ENewDword") == RegistryValueKind.DWord,
            TimeSpan.FromSeconds(10));
        uint? value = exists ? ReadDword(sandbox, "E2ENewDword") : null;
        byte[] bytes = value is null ? Array.Empty<byte>() : BitConverter.GetBytes(value.Value);
        string hex = Convert.ToHexString(bytes);
        bool rowSelected = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.IsSelected(
                ControlTestSupport.FindRowByName(
                    session,
                    ControlTestSupport.ListControl,
                    ControlType.DataItem,
                    "E2ENewDword")),
            TimeSpan.FromSeconds(10));
        E2EResults.Record(
            "editing.newdword.value",
            exists && value == 0x1234ABCDu && hex == "CDAB3412" && rowSelected,
            $"kind={ValueKindOf(sandbox, "E2ENewDword")}, value=0x{value.GetValueOrDefault():X8}, bytes={hex}, rowSelected={rowSelected}.");
    }

    private static void CheckRenameValue(AppSession session, EditingDriver driver, SandboxRegistry sandbox)
    {
        ControlTestSupport.WaitUntil(
            () => ControlTestSupport.FindRowByName(
                session,
                ControlTestSupport.ListControl,
                ControlType.DataItem,
                "AlphaString") is not null,
            TimeSpan.FromSeconds(10));

        AutomationElement? row = ControlTestSupport.FindRowByName(
            session,
            ControlTestSupport.ListControl,
            ControlType.DataItem,
            "AlphaString");
        ControlTestSupport.Select(row);
        _ = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.IsSelected(
                ControlTestSupport.FindRowByName(
                    session,
                    ControlTestSupport.ListControl,
                    ControlType.DataItem,
                    "AlphaString")),
            TimeSpan.FromSeconds(5));

        bool f2 = driver.TypeKeys("F2");
        bool renameOpen = f2 && ControlTestSupport.WaitUntil(
            () => session.Find("ListRenameBox") is not null,
            TimeSpan.FromSeconds(5));
        bool typed = renameOpen && driver.TypeKeys("AlphaRenamed");
        bool committed = typed && driver.TypeKeys("Enter");
        bool renamed = ControlTestSupport.WaitUntil(
            () => sandbox.ReadString(string.Empty, "AlphaRenamed") == "alpha" &&
                sandbox.ReadString(string.Empty, "AlphaString") is null,
            TimeSpan.FromSeconds(10));
        E2EResults.Record(
            "editing.renamevalue.commit",
            renameOpen && typed && committed && renamed,
            $"renameOpen={renameOpen}, typed={typed}, committed={committed}, renamed={renamed}.");

        AutomationElement? cancelRow = ControlTestSupport.FindRowByName(
            session,
            ControlTestSupport.ListControl,
            ControlType.DataItem,
            "ZebraString");
        ControlTestSupport.Select(cancelRow);
        _ = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.IsSelected(
                ControlTestSupport.FindRowByName(
                    session,
                    ControlTestSupport.ListControl,
                    ControlType.DataItem,
                    "ZebraString")),
            TimeSpan.FromSeconds(5));

        bool cancelF2 = driver.TypeKeys("F2");
        bool cancelOpen = cancelF2 && ControlTestSupport.WaitUntil(
            () => session.Find("ListRenameBox") is not null,
            TimeSpan.FromSeconds(5));
        bool cancelTyped = cancelOpen && driver.TypeKeys("ZebraCancelled");
        bool escaped = cancelTyped && driver.TypeKeys("Escape");
        bool cancelClosed = escaped && ControlTestSupport.WaitUntil(
            () => session.Find("ListRenameBox") is null,
            TimeSpan.FromSeconds(5));
        bool unchanged = sandbox.ReadString(string.Empty, "ZebraString") == "zebra" &&
            sandbox.ReadString(string.Empty, "ZebraCancelled") is null;
        E2EResults.Record(
            "editing.renamevalue.cancel",
            cancelOpen && cancelTyped && escaped && cancelClosed && unchanged,
            $"cancelOpen={cancelOpen}, escaped={escaped}, cancelClosed={cancelClosed}, unchanged={unchanged}.");
    }

    private static void CheckDeleteValue(AppSession session, EditingDriver driver, SandboxRegistry sandbox)
    {
        AutomationElement? row = ControlTestSupport.FindRowByName(
            session,
            ControlTestSupport.ListControl,
            ControlType.DataItem,
            "SampleBinary");
        ControlTestSupport.Select(row);
        _ = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.IsSelected(
                ControlTestSupport.FindRowByName(
                    session,
                    ControlTestSupport.ListControl,
                    ControlType.DataItem,
                    "SampleBinary")),
            TimeSpan.FromSeconds(5));

        driver.OpenMenu(MenuEdit, MenuEditDelete);
        bool dialog = driver.WaitForDialog();
        bool invoked = dialog && driver.Invoke("DialogPrimary");
        bool closed = invoked && driver.WaitForNoDialog();
        bool removed = ControlTestSupport.WaitUntil(
            () => sandbox.ReadBinary(string.Empty, "SampleBinary") is null,
            TimeSpan.FromSeconds(10));
        E2EResults.Record(
            "editing.deletevalue",
            dialog && invoked && closed && removed,
            $"dialog={dialog}, invoked={invoked}, closed={closed}, removed={removed}.");

        bool rowGone = ControlTestSupport.WaitUntil(
            () => !ControlTestSupport.ListRowNames(session).Contains("SampleBinary", StringComparer.Ordinal),
            TimeSpan.FromSeconds(10));
        E2EResults.Record(
            "editing.deletevalue.refresh",
            rowGone,
            $"list rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}].");
    }

    private static void CheckDeleteKey(AppSession session, EditingDriver driver, SandboxRegistry sandbox)
    {
        bool selected = SelectRunKey(session, sandbox);
        E2EResults.Record(
            "editing.deletekey.select",
            selected,
            $"run key selected={selected}.");

        driver.OpenMenu(MenuEdit, MenuEditDelete);
        bool dialog = driver.WaitForDialog();
        string message = driver.TextOf("DialogMessage");
        bool warned = message.Contains(
            "Deleting this key will delete all of its subkeys.",
            StringComparison.Ordinal);
        E2EResults.Record(
            "editing.deletekey.warning",
            dialog && warned,
            $"dialog={dialog}, message='{message}'.");

        bool invoked = dialog && driver.Invoke("DialogPrimary");
        bool closed = invoked && driver.WaitForNoDialog();
        bool removed = ControlTestSupport.WaitUntil(
            () => !KeyExists(sandbox, string.Empty),
            TimeSpan.FromSeconds(10));
        bool parentExists = ParentExists();
        E2EResults.Record(
            "editing.deletekey",
            invoked && closed && removed && parentExists,
            $"invoked={invoked}, closed={closed}, removed={removed}, parentExists={parentExists}.");

        bool rowGone = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.FindRowByName(
                session,
                ControlTestSupport.TreeControl,
                ControlType.TreeItem,
                SandboxRunId(sandbox)) is null,
            TimeSpan.FromSeconds(10));
        E2EResults.Record("editing.deletekey.refresh", rowGone, $"tree row gone={rowGone}.");
    }

    private static bool SelectRunKey(AppSession session, SandboxRegistry sandbox)
    {
        string runId = SandboxRunId(sandbox);
        AutomationElement? row = ControlTestSupport.FindRowByName(
            session,
            ControlTestSupport.TreeControl,
            ControlType.TreeItem,
            runId);
        if (row is null)
        {
            return false;
        }

        ControlTestSupport.Select(row);
        return ControlTestSupport.WaitForTreeSelection(session, runId);
    }

    private static void Seed(SandboxRegistry sandbox)
    {
        using (var root = sandbox.CreateSubKey(string.Empty))
        {
            root.SetValue("AlphaString", "alpha", RegistryValueKind.String);
            root.SetValue("ZebraString", "zebra", RegistryValueKind.String);
            root.SetValue("SampleBinary", new byte[] { 0x01, 0x02, 0xAB }, RegistryValueKind.Binary);
            root.SetValue("SeedDword", 7, RegistryValueKind.DWord);
        }

        using var child = sandbox.CreateSubKey("Child");
        child.SetValue("Marker", "child", RegistryValueKind.String);
    }

    private static string SandboxRunId(SandboxRegistry sandbox)
    {
        string path = sandbox.RunRootPath;
        int separator = path.LastIndexOf('\\');
        return separator >= 0 ? path[(separator + 1)..] : path;
    }

    private static bool KeyExists(SandboxRegistry sandbox, string relativePath) =>
        sandbox.Exists(relativePath);

    private static RegistryValueKind ValueKindOf(SandboxRegistry sandbox, string name)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(sandbox.RunRootPath, writable: false);
            return key?.GetValueKind(name) ?? RegistryValueKind.Unknown;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return RegistryValueKind.Unknown;
        }
    }

    private static uint? ReadDword(SandboxRegistry sandbox, string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(sandbox.RunRootPath, writable: false);
        object? value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is null ? null : Convert.ToUInt32(value, CultureInfo.InvariantCulture);
    }

    private static bool ParentExists()
    {
        using RegistryKey? parent = Registry.CurrentUser.OpenSubKey(@"Software\BWC-E2E", writable: false);
        return parent is not null;
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

    private static bool ContainsAll(IReadOnlyList<string> names, params string[] expected)
    {
        foreach (string name in expected)
        {
            if (!names.Contains(name, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void FailIfNeeded()
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Editing checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}

using System.Security.Principal;
using Bwc.E2E.ContextMenus;
using Bwc.E2E.Controls;
using Bwc.E2E.Dialogs;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using Bwc.E2E.ImportExport;
using Bwc.E2E.Print;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Remote;

/// <summary>
/// Remote commands on a loopback connection to 127.0.0.1. Every write goes to
/// HKEY_USERS\SID\Software\BWC-E2E\run-id, the local sandbox key with the root swapped. Section 16 of
/// docs/failure-modes/remote-registry.md is the spec. A connect that the remote registry refuses is a note
/// and the test is Inconclusive only while RemoteRegistry is not Running; otherwise it is a failed check. The test
/// never starts or configures the service.
/// </summary>
[TestClass]
public sealed class RemoteCommandTests
{
    private const string LoopbackComputer = "127.0.0.1";
    private const string LoopbackNode = @"\\127.0.0.1";
    private const string UnconnectedNode = @"\\192.168.250.1";
    private const string AddressBarId = "AddressBar";
    private const string DialogRootId = "DialogRoot";
    private const string DialogPrimaryId = "DialogPrimary";
    private const string DialogErrorTextId = "DialogErrorText";
    private const string ConnectTargetVariable = "BWC_E2E_CONNECT_TARGET";
    private const string RemoteListId = "RemoteComputerList";
    private const string RemoteLabelId = "RemoteComputerLabel";
    private const string TreeRenameBoxId = "TreeRenameBox";
    private const string ListRenameBoxId = "ListRenameBox";
    private const string ValueInputId = "ValueEditorInput";
    private const string DecimalBaseId = "BaseDec";
    private const string FindTextId = "FindText";
    private const string FindKeysId = "FindKeys";
    private const string FindValuesId = "FindValues";
    private const string FindDataId = "FindData";

    private const int MenuFile = 512;
    private const int MenuFileConnect = 657;
    private const int MenuFileExport = 659;
    private const int MenuFileDisconnect = 768;
    private const int CommandNew = 516;
    private const int CommandExpandCollapse = 915;
    private const int CommandExport = 659;
    private const int CommandDisconnect = 768;
    private const int CommandDelete = 913;
    private const int CommandRename = 914;
    private const int CommandPermissions = 678;
    private const int CommandCopyKeyName = 675;
    private const int CommandNewKey = 663;
    private const int CommandNewString = 664;
    private const int CommandNewBinary = 665;
    private const int CommandNewDword = 674;
    private const int CommandNewQword = 688;
    private const int CommandNewMulti = 676;
    private const int CommandNewExpand = 677;

    private const string SeedValueName = "RemoteSeed";
    private const string SeedValueData = "remote";
    private const string ChildKeyName = "RemoteChild";
    private const string RenamedKeyName = "RemoteRenamed";
    private const string ClassicNewKeyName = "New Key #1";
    private const string StringValueName = "RemoteString";
    private const string DwordValueName = "RemoteDword";
    private const string QwordValueName = "RemoteQword";
    private const string BinaryValueName = "RemoteBinaryTest";
    private const string MultiValueName = "RemoteMulti";
    private const string ExpandValueName = "RemoteExpand";
    private const string RenamedValueName = "RenamedValue";
    private const string EditedString = "remote-edit";
    private const int EditedDword = 42;

    private const string FindValueName = "RemoteFindTarget";
    private const string FindData = "remote-find-hit";

    private const string ExportText = "Text";
    private const string ExportCount = "Count";
    private const string ExportChildKey = "Child";
    private const string ExportAlpha = "alpha";
    private const string ExportBeta = "beta";
    private const int ExportCountRoot = 42;
    private const int ExportCountChild = 7;

    private const string AddressNotFoundText =
        "The selected branch does not exist. Make sure that the correct path is given.";
    private const string DisconnectCaption = "Disconnect Network Registry";
    private const string DisconnectLabel = "Computer name:";
    private const string DisconnectNoSelectionText = "Select a connected computer to disconnect.";

    private static readonly string[] RemoteRootNames = ["HKEY_LOCAL_MACHINE", "HKEY_USERS"];

    private static readonly NewValueSpec[] NewValues =
    [
        new(CommandNewString, StringValueName, RegistryValueKind.String),
        new(CommandNewDword, DwordValueName, RegistryValueKind.DWord),
        new(CommandNewQword, QwordValueName, RegistryValueKind.QWord),
        new(CommandNewBinary, BinaryValueName, RegistryValueKind.Binary),
        new(CommandNewMulti, MultiValueName, RegistryValueKind.MultiString),
        new(CommandNewExpand, ExpandValueName, RegistryValueKind.ExpandString),
    ];

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Remote")]
    [TestCategory("Mutating")]
    [TestCategory("ContextMenus")]
    public void Remote_LoopbackConnectShowsRemoteRootsAndMenus()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        EnsurePublished(config);

        var sandbox = new SandboxRegistry(config);
        var remote = new RemoteRun(CurrentSid(), config);
        try
        {
            sandbox.Cleanup();
            remote.Cleanup();
            sandbox.SetString(string.Empty, SeedValueName, SeedValueData);
            remote.Seed(string.Empty, SeedValueName, SeedValueData, RegistryValueKind.String);

            using AppSession session = StartApp(config, sandbox, "remote-roots");
            using var driver = new EditingDriver(session);
            using var menus = new ContextMenuDriver(session);
            RequireConnected(session, driver);

            AutomationElement? node = FindTreeRow(session, LoopbackNode);
            E2EResults.Record("remote.node", node is not null, $"tree node '{LoopbackNode}' present={node is not null}.");

            bool expanded = node is not null && ControlTestSupport.Expand(node);
            string[] roots = Array.Empty<string>();
            bool rootsShown = expanded && ControlTestSupport.WaitUntil(
                () => (roots = RemoteChildNames(session)).Length >= RemoteRootNames.Length,
                TimeSpan.FromSeconds(10));
            bool rootsExact = rootsShown && roots.Length == RemoteRootNames.Length &&
                RemoteRootNames.All(name => roots.Contains(name, StringComparer.OrdinalIgnoreCase));
            E2EResults.Record(
                "remote.roots",
                rootsExact,
                $"expanded={expanded}, children=[{string.Join(",", roots)}], expected only HKEY_LOCAL_MACHINE and HKEY_USERS.");

            // The computer node is the classic RT_MENU 107. Export and Disconnect are enabled on it (section 16).
            MenuSnapshot? nodeMenu = OpenContextMenu(session, menus, () => FindTreeRow(session, LoopbackNode));
            MenuEntry? expandItem = nodeMenu?.Find(CommandExpandCollapse);
            MenuEntry? exportItem = nodeMenu?.Find(CommandExport);
            MenuEntry? disconnectItem = nodeMenu?.Find(CommandDisconnect);
            Screenshot(session, "remote.menu.computer");
            E2EResults.Record(
                "remote.menu.computer",
                expandItem is not null && exportItem is { Enabled: true } && disconnectItem is { Enabled: true },
                $"menu={nodeMenu is not null}, expand present={expandItem is not null}, " +
                $"export enabled={exportItem?.Enabled}, disconnect enabled={disconnectItem?.Enabled}.");
            CloseMenus(session);

            // The hive root under the remote computer is locked: Delete and Rename are disabled.
            bool hkuNavigated = NavigateAddress(session, RemotePath("HKEY_USERS"), "HKEY_USERS");
            MenuSnapshot? hkuMenu = hkuNavigated ? OpenContextMenu(session, menus, () => SelectedTreeRow(session)) : null;
            MenuEntry? hkuDelete = hkuMenu?.Find(CommandDelete);
            MenuEntry? hkuRename = hkuMenu?.Find(CommandRename);
            bool hkuItems = hkuMenu is not null &&
                hkuMenu.Find(CommandNew) is not null &&
                hkuMenu.Find(CommandPermissions) is not null &&
                hkuMenu.Find(CommandCopyKeyName) is not null;
            Screenshot(session, "remote.menu.hku-root");
            E2EResults.Record(
                "remote.menu.hku-root",
                hkuNavigated && hkuItems && hkuDelete is { Enabled: false } && hkuRename is { Enabled: false },
                $"navigated={hkuNavigated}, New/Permissions/Copy Key Name present={hkuItems}, " +
                $"delete enabled={hkuDelete?.Enabled}, rename enabled={hkuRename?.Enabled}.");
            CloseMenus(session);

            // A remote key below HKEY_USERS\SID\Software\BWC-E2E gets the ordinary key rule: Delete and Rename enabled.
            bool runNavigated = NavigateAddress(session, remote.DisplayRoot, remote.RunId);
            MenuSnapshot? keyMenu = runNavigated ? OpenContextMenu(session, menus, () => SelectedTreeRow(session)) : null;
            MenuEntry? keyDelete = keyMenu?.Find(CommandDelete);
            MenuEntry? keyRename = keyMenu?.Find(CommandRename);
            E2EResults.Record(
                "remote.menu.remote-key",
                runNavigated &&
                    keyDelete is { Enabled: true } &&
                    keyRename is { Enabled: true } &&
                    keyMenu?.Find(CommandNew) is not null,
                $"navigated={runNavigated}, delete enabled={keyDelete?.Enabled}, rename enabled={keyRename?.Enabled}.");
            CloseMenus(session);

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            remote.Cleanup();
            DeleteDataRoot(config, "remote-roots");
        }
    }

    [TestMethod]
    [TestCategory("Remote")]
    [TestCategory("Mutating")]
    public void Remote_KeyAndValueCommandsWriteOnlyUnderTheSandbox()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        EnsurePublished(config);

        var sandbox = new SandboxRegistry(config);
        var remote = new RemoteRun(CurrentSid(), config);
        try
        {
            sandbox.Cleanup();
            remote.Cleanup();
            remote.Seed(string.Empty, SeedValueName, SeedValueData, RegistryValueKind.String);

            using AppSession session = StartApp(config, sandbox, "remote-commands");
            using var driver = new EditingDriver(session);
            using var menus = new ContextMenuDriver(session);
            RequireConnected(session, driver);

            bool navigated = NavigateAddress(session, remote.DisplayRoot, remote.RunId);
            bool seeded = navigated && ControlTestSupport.WaitUntil(
                () => ControlTestSupport.ListRowNames(session).Contains(SeedValueName, StringComparer.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(10));
            E2EResults.Record("remote.keys.navigate", navigated && seeded, $"navigated={navigated}, seeded={seeded}.");
            RequireAddressInside(session, remote, "remote.keys.selection");

            // New > Key on the remote run key. The first unused classic name is offered, then renamed.
            MenuSnapshot? keyMenu = OpenContextMenu(session, menus, () => SelectedTreeRow(session));
            bool keyInvoked = keyMenu is not null && menus.OpenSubmenu(CommandNew) && menus.Invoke(CommandNewKey);
            bool keyEditor = keyInvoked && ControlTestSupport.WaitUntil(
                () => session.Find(TreeRenameBoxId) is not null,
                TimeSpan.FromSeconds(10));
            AutomationElement? keyBox = keyEditor ? session.Find(TreeRenameBoxId) : null;
            string defaultName = keyBox is null ? string.Empty : session.TextOf(keyBox);
            bool named = keyBox is not null &&
                string.Equals(defaultName, ClassicNewKeyName, StringComparison.Ordinal) &&
                driver.TypeKeys(ChildKeyName) &&
                driver.TypeKeys("Enter");
            bool keyCreated = named && ControlTestSupport.WaitUntil(
                () => remote.KeyExists(ChildKeyName),
                TimeSpan.FromSeconds(10));
            E2EResults.Record(
                "remote.key.new",
                keyCreated,
                $"menu={keyMenu is not null}, invoked={keyInvoked}, editor={keyEditor}, default='{defaultName}', " +
                $"created under HKU={keyCreated}.");

            // One value of each type under RemoteChild, through New in the key's menu.
            foreach (NewValueSpec spec in NewValues)
            {
                bool created = NewValueUnder(session, driver, menus, ChildKeyName, spec.Command, spec.Name) &&
                    ControlTestSupport.WaitUntil(
                        () => remote.KindOf(ChildKeyName, spec.Name) == spec.Kind,
                        TimeSpan.FromSeconds(10));
                E2EResults.Record(
                    "remote.value.new." + spec.Name,
                    created,
                    $"kind={spec.Kind}, present under HKU with that kind={created}.");
            }

            // Modify the String and the DWORD. The DWORD editor is switched to decimal before 42 is typed.
            bool stringSelected = SelectKey(session, ChildKeyName) && SelectListRow(session, StringValueName);
            AutomationElement? stringRow = FindListRow(session, StringValueName);
            bool stringEdited = stringSelected && stringRow is not null &&
                ClassicDialogTests.OpenEditor(session, driver, stringRow, "remote.modify.string.open") &&
                driver.SetText(ValueInputId, EditedString) &&
                driver.Invoke(DialogPrimaryId) &&
                driver.WaitForNoDialog();
            bool stringWritten = stringEdited && ControlTestSupport.WaitUntil(
                () => (remote.ValueOf(ChildKeyName, StringValueName) as string) == EditedString,
                TimeSpan.FromSeconds(10));
            E2EResults.Record("remote.modify.string", stringWritten, $"edited={stringEdited}, written={stringWritten}.");

            bool dwordSelected = SelectListRow(session, DwordValueName);
            AutomationElement? dwordRow = FindListRow(session, DwordValueName);
            bool dwordEdited = dwordSelected && dwordRow is not null &&
                ClassicDialogTests.OpenEditor(session, driver, dwordRow, "remote.modify.dword.open") &&
                driver.Invoke(DecimalBaseId) &&
                driver.SetText(ValueInputId, EditedDword.ToString(System.Globalization.CultureInfo.InvariantCulture)) &&
                driver.Invoke(DialogPrimaryId) &&
                driver.WaitForNoDialog();
            bool dwordWritten = dwordEdited && ControlTestSupport.WaitUntil(
                () => remote.ValueOf(ChildKeyName, DwordValueName) is int stored && stored == EditedDword,
                TimeSpan.FromSeconds(10));
            E2EResults.Record("remote.modify.dword", dwordWritten, $"edited={dwordEdited}, written={dwordWritten}.");

            // Rename the key, then one value under it.
            bool keyRenamed = RenameThrough(
                session,
                driver,
                menus,
                () => FindTreeRow(session, ChildKeyName),
                TreeRenameBoxId,
                RenamedKeyName) &&
                ControlTestSupport.WaitUntil(
                    () => remote.KeyExists(RenamedKeyName) && !remote.KeyExists(ChildKeyName),
                    TimeSpan.FromSeconds(10));
            E2EResults.Record("remote.key.rename", keyRenamed, $"RemoteChild renamed to RemoteRenamed={keyRenamed}.");

            bool renamedSelected = SelectKey(session, RenamedKeyName) && SelectListRow(session, MultiValueName);
            bool valueRenamed = renamedSelected && RenameThrough(
                session,
                driver,
                menus,
                () => FindListRow(session, MultiValueName),
                ListRenameBoxId,
                RenamedValueName) &&
                ControlTestSupport.WaitUntil(
                    () => remote.KindOf(RenamedKeyName, RenamedValueName) == RegistryValueKind.MultiString &&
                        remote.KindOf(RenamedKeyName, MultiValueName) is null,
                    TimeSpan.FromSeconds(10));
            E2EResults.Record(
                "remote.value.rename",
                valueRenamed,
                $"RemoteMulti renamed to RenamedValue={valueRenamed}.");

            // Read the written state back through the registry API, before any delete.
            bool readBack = remote.ValueOf(RenamedKeyName, StringValueName) is string edited && edited == EditedString &&
                remote.ValueOf(RenamedKeyName, DwordValueName) is int dword && dword == EditedDword &&
                remote.KindOf(RenamedKeyName, QwordValueName) == RegistryValueKind.QWord &&
                remote.KindOf(RenamedKeyName, BinaryValueName) == RegistryValueKind.Binary &&
                remote.KindOf(RenamedKeyName, RenamedValueName) == RegistryValueKind.MultiString &&
                remote.KindOf(RenamedKeyName, ExpandValueName) == RegistryValueKind.ExpandString &&
                !remote.KeyExists(ChildKeyName);
            E2EResults.Record("remote.artifact.readback", readBack, $"HKU {RenamedKeyName}: {remote.Describe(RenamedKeyName)}.");

            // Delete one value, then the key. Both confirm with Yes.
            bool binarySelected = SelectKey(session, RenamedKeyName) && SelectListRow(session, BinaryValueName);
            bool valueDeleted = binarySelected &&
                OpenContextMenu(session, menus, () => FindListRow(session, BinaryValueName)) is not null &&
                menus.Invoke(CommandDelete) &&
                driver.WaitForDialog() &&
                driver.Invoke(DialogPrimaryId) &&
                driver.WaitForNoDialog();
            bool valueGone = valueDeleted && ControlTestSupport.WaitUntil(
                () => remote.KindOf(RenamedKeyName, BinaryValueName) is null,
                TimeSpan.FromSeconds(10));
            E2EResults.Record("remote.value.delete", valueGone, $"RemoteBinaryTest deleted={valueGone}.");

            bool keyDeleted = OpenContextMenu(session, menus, () => FindTreeRow(session, RenamedKeyName)) is not null &&
                menus.Invoke(CommandDelete) &&
                driver.WaitForDialog() &&
                driver.Invoke(DialogPrimaryId) &&
                driver.WaitForNoDialog();
            bool keyGone = keyDeleted && ControlTestSupport.WaitUntil(
                () => !remote.KeyExists(RenamedKeyName),
                TimeSpan.FromSeconds(10));
            E2EResults.Record("remote.key.delete", keyGone, $"RemoteRenamed deleted under HKU={keyGone}.");

            bool localClean = !sandbox.Exists(ChildKeyName) && !sandbox.Exists(RenamedKeyName);
            E2EResults.Record(
                "remote.local-untouched",
                localClean,
                $"local {ChildKeyName} exists={sandbox.Exists(ChildKeyName)}, local {RenamedKeyName} exists={sandbox.Exists(RenamedKeyName)}.");

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            remote.Cleanup();
            DeleteDataRoot(config, "remote-commands");
        }
    }

    [TestMethod]
    [TestCategory("Remote")]
    [TestCategory("Mutating")]
    [TestCategory("Find")]
    public void Remote_FindLocatesRemoteValue()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        EnsurePublished(config);

        var sandbox = new SandboxRegistry(config);
        var remote = new RemoteRun(CurrentSid(), config);
        try
        {
            sandbox.Cleanup();
            remote.Cleanup();
            remote.Seed(string.Empty, FindValueName, FindData, RegistryValueKind.String);

            using AppSession session = StartApp(config, sandbox, "remote-find");
            using var driver = new EditingDriver(session);
            RequireConnected(session, driver);

            bool navigated = NavigateAddress(session, remote.DisplayRoot, remote.RunId);
            RequireAddressInside(session, remote, "remote.find.selection");

            // Ctrl+F from the remote run key. Only Data is searched, with the default subtree scope.
            bool accelerator = TrySendKeys(session, "CONTROL+KEY_F");
            AutomationElement? findText = accelerator ? driver.WaitForElement(FindTextId, TimeSpan.FromSeconds(10)) : null;
            bool opened = navigated && findText is not null;
            bool prepared = opened &&
                SetToggle(driver, FindKeysId, on: false) &&
                SetToggle(driver, FindValuesId, on: false) &&
                SetToggle(driver, FindDataId, on: true) &&
                driver.SetText(FindTextId, FindData);
            bool started = prepared && driver.Invoke(DialogPrimaryId);
            bool closed = started && ControlTestSupport.WaitUntil(
                () => driver.WaitForElement(FindTextId, TimeSpan.FromMilliseconds(200)) is null,
                TimeSpan.FromSeconds(20));
            bool selected = closed && ControlTestSupport.WaitUntil(
                () => ControlTestSupport.IsSelected(FindListRow(session, FindValueName)),
                TimeSpan.FromSeconds(30));
            E2EResults.Record(
                "remote.find.hit",
                selected,
                $"opened={opened}, prepared={prepared}, started={started}, closed={closed}, " +
                $"'{FindValueName}' selected={selected}.");

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            remote.Cleanup();
            DeleteDataRoot(config, "remote-find");
        }
    }

    [TestMethod]
    [TestCategory("Remote")]
    [TestCategory("Mutating")]
    public void Remote_ExportBranchMatchesLocalExport()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        EnsurePublished(config);

        var sandbox = new SandboxRegistry(config);
        var remote = new RemoteRun(CurrentSid(), config);
        string exportDir = Path.Combine(config.ArtifactsDir, "remote-commands", config.RunId, "export");
        string localFile = Path.Combine(exportDir, "local.reg");
        string remoteFile = Path.Combine(exportDir, "remote.reg");
        try
        {
            sandbox.Cleanup();
            remote.Cleanup();
            SeedLocalTree(sandbox);
            SeedRemoteTree(remote);
            if (Directory.Exists(exportDir))
            {
                Directory.Delete(exportDir, recursive: true);
            }

            Directory.CreateDirectory(exportDir);

            using AppSession session = StartApp(config, sandbox, "remote-export");
            using var driver = new EditingDriver(session);
            using var dialogs = new SystemFileDialogDriver(session);
            RequireConnected(session, driver);

            bool localNavigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EGuards.RequireSandboxSelection(session, sandbox, "remote.export.local.selection");
            bool localExported = localNavigated &&
                ExportSelectedBranch(session, driver, dialogs, localFile, "remote.export.local");

            bool remoteNavigated = NavigateAddress(session, remote.DisplayRoot, remote.RunId);
            RequireAddressInside(session, remote, "remote.export.remote.selection");
            bool remoteExported = remoteNavigated &&
                ExportSelectedBranch(session, driver, dialogs, remoteFile, "remote.export.remote");

            string[] localLines = ReadRegLines(localFile);
            string[] remoteLines = WithLocalHeaders(ReadRegLines(remoteFile), remote.Sid);
            int difference = FirstDifference(localLines, remoteLines);
            bool matches = localExported && remoteExported && localLines.Length > 0 && difference < 0;
            E2EResults.Record(
                "remote.export.matches-local",
                matches,
                $"local lines={localLines.Length}, remote lines={remoteLines.Length}, " +
                $"first difference at line={(difference < 0 ? "none" : (difference + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))}.");

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            remote.Cleanup();
            DeleteDataRoot(config, "remote-export");
        }
    }

    [TestMethod]
    [TestCategory("Remote")]
    [TestCategory("Mutating")]
    [TestCategory("Navigation")]
    public void Remote_AddressBarNavigatesToRemotePath()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        EnsurePublished(config);

        var sandbox = new SandboxRegistry(config);
        var remote = new RemoteRun(CurrentSid(), config);
        try
        {
            sandbox.Cleanup();
            remote.Cleanup();
            remote.Seed(string.Empty, SeedValueName, SeedValueData, RegistryValueKind.String);

            using AppSession session = StartApp(config, sandbox, "remote-address");
            using var driver = new EditingDriver(session);
            RequireConnected(session, driver);

            bool navigated = NavigateAddress(session, remote.DisplayRoot, remote.RunId);
            bool seeded = navigated && ControlTestSupport.WaitUntil(
                () => ControlTestSupport.ListRowNames(session).Contains(SeedValueName, StringComparer.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(10));
            E2EResults.Record(
                "remote.address.connected",
                navigated && seeded,
                $"navigated={navigated}, seeded value listed={seeded}, address='{AddressText(session)}'.");

            // The text the bar shows for the selected remote key, typed back and entered, lands on the same key.
            string shownText = AddressText(session);
            bool retyped = navigated && NavigateAddress(session, shownText, remote.RunId);
            E2EResults.Record(
                "remote.address.roundtrip",
                retyped && string.Equals(AddressText(session), shownText, StringComparison.OrdinalIgnoreCase),
                $"shown='{shownText}', retyped={retyped}, address after='{AddressText(session)}'.");

            // A missing key under a connected computer gets the classic not-found text.
            string missingPath = RemotePath($@"HKEY_USERS\{remote.Sid}\NoSuchKey-{remote.RunId}");
            bool missingRefused = ExpectAddressError(session, driver, missingPath, "remote.address.missing");

            // A computer that is not connected is refused the same way, and no node is added.
            bool unconnectedRefused = ExpectAddressError(
                session,
                driver,
                UnconnectedNode + @"\HKEY_LOCAL_MACHINE\Software",
                "remote.address.unconnected");
            bool noNewNode = FindTreeRow(session, UnconnectedNode) is null && FindTreeRow(session, LoopbackNode) is not null;
            E2EResults.Record(
                "remote.address.no-connection",
                noNewNode,
                $"'{UnconnectedNode}' node present={!noNewNode}, '{LoopbackNode}' still present={FindTreeRow(session, LoopbackNode) is not null}.");

            E2EResults.Note(
                "remote.address.refusals",
                $"missing key refused={missingRefused}, unconnected refused={unconnectedRefused}.");

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            remote.Cleanup();
            DeleteDataRoot(config, "remote-address");
        }
    }

    [TestMethod]
    [TestCategory("Remote")]
    [TestCategory("Mutating")]
    public void Remote_DisconnectDialogRemovesComputer()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        EnsurePublished(config);

        var sandbox = new SandboxRegistry(config);
        try
        {
            sandbox.Cleanup();

            using AppSession session = StartApp(config, sandbox, "remote-disconnect");
            using var driver = new EditingDriver(session);
            RequireConnected(session, driver);

            // File > Disconnect Network Registry opens dialog 107 with the connected computer in its list.
            bool opened = OpenFileCommand(driver, MenuFileDisconnect);
            bool dialog = opened && driver.WaitForDialog(TimeSpan.FromSeconds(10));
            AutomationElement? root = dialog ? driver.WaitForElement(DialogRootId) : null;
            string caption = root is null ? string.Empty : ControlTestSupport.SafeName(ClassicDialogTests.WindowOf(root));
            string label = dialog ? driver.TextOf(RemoteLabelId) : string.Empty;
            bool listed = dialog && RemoteComputerItem(driver) is not null;
            E2EResults.Record(
                "remote.disconnect.dialog",
                dialog &&
                    string.Equals(caption, DisconnectCaption, StringComparison.Ordinal) &&
                    string.Equals(label, DisconnectLabel, StringComparison.Ordinal) &&
                    listed,
                $"dialog={dialog}, caption='{caption}', label='{label}', '{LoopbackComputer}' listed={listed}.");
            Screenshot(session, "remote.disconnect.dialog");

            // OK with nothing selected shows the classic message and the dialog stays open.
            bool emptyPrompted = dialog && driver.Invoke(DialogPrimaryId) && ControlTestSupport.WaitUntil(
                () => string.Equals(driver.TextOf(DialogErrorTextId), DisconnectNoSelectionText, StringComparison.Ordinal),
                TimeSpan.FromSeconds(10));
            bool emptyDismissed = emptyPrompted && driver.DismissErrorBox();
            bool stillOpen = emptyDismissed && driver.WaitForDialog(TimeSpan.FromSeconds(2));
            E2EResults.Record(
                "remote.disconnect.empty-selection",
                emptyPrompted && emptyDismissed && stillOpen,
                $"message shown={emptyPrompted}, dismissed={emptyDismissed}, dialog still open={stillOpen}.");

            // Select the computer and press OK. The node leaves the tree.
            ControlTestSupport.Select(RemoteComputerItem(driver));
            bool selected = stillOpen && ControlTestSupport.WaitUntil(
                () => ControlTestSupport.IsSelected(RemoteComputerItem(driver)),
                TimeSpan.FromSeconds(5));
            bool confirmed = selected && driver.Invoke(DialogPrimaryId) && driver.WaitForNoDialog();
            bool removed = confirmed && ControlTestSupport.WaitUntil(
                () => FindTreeRow(session, LoopbackNode) is null,
                TimeSpan.FromSeconds(10));
            E2EResults.Record(
                "remote.disconnect.removed",
                removed,
                $"selected={selected}, confirmed={confirmed}, node '{LoopbackNode}' gone={removed}.");

            // With no connection left, the File menu item is disabled again.
            bool menuOpened = OpenFileMenu(driver);
            AutomationElement? disconnect = menuOpened
                ? driver.WaitForElement("MenuCommand_" + MenuFileDisconnect, TimeSpan.FromSeconds(5))
                : null;
            bool disabled = disconnect is not null && !IsEnabled(disconnect);
            CloseMenus(session);
            E2EResults.Record(
                "remote.disconnect.menu-disabled",
                removed && disabled,
                $"menu opened={menuOpened}, Disconnect present={disconnect is not null}, disabled={disabled}.");

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            DeleteDataRoot(config, "remote-disconnect");
        }
    }

    /// <summary>
    /// Print on a remote key reads the connection's store and writes the classic remote Key Name form,
    /// \\127.0.0.1\HKEY_USERS\SID\... The report seam writes the file, and no print dialog opens. When the loopback
    /// connection is refused while RemoteRegistry is not Running, the test records a note and is Inconclusive.
    /// A refusal with the service Running is a failed check. The service is not started or stopped here.
    /// </summary>
    [TestMethod]
    [TestCategory("Print")]
    [TestCategory("Remote")]
    [TestCategory("Mutating")]
    public void Print_RemoteReportUsesClassicRemoteKeyName()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        EnsurePublished(config);

        const string remoteChild = "PrintRemoteChild";
        var sandbox = new SandboxRegistry(config);
        var remote = new RemoteRun(CurrentSid(), config);
        string reportPath = Path.Combine(PrintTestSupport.ArtifactDir(config), $"remote-{config.RunId}.txt");
        string? dataRoot = null;
        try
        {
            sandbox.Cleanup();
            remote.Cleanup();
            PrintTestSupport.DeleteIfExists(reportPath);
            remote.Seed(string.Empty, "PrintRemote", "remote", RegistryValueKind.String);
            remote.Seed(remoteChild, "ChildValue", "child", RegistryValueKind.String);
            dataRoot = CreateFreshDataRoot(config, "remote-print");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
                [ConnectTargetVariable] = LoopbackComputer,
                [PrintTestSupport.ReportPathVariable] = reportPath,
            };

            using AppSession session = new(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);
            ConnectResult connection = ConnectLoopback(session, driver);
            if (!connection.Connected && connection.RemoteError is not null)
            {
                RemoteRegistryService.RefusedConnect("print.remote.connect", "print.remote.not-run", connection.RemoteError);
            }

            E2EResults.Record("print.remote.connect", connection.Connected, connection.Detail);
            if (!connection.Connected)
            {
                FailIfNeeded();
                return;
            }

            bool navigated = NavigateAddress(session, remote.DisplayRoot, remote.RunId);
            ControlTestSupport.SendKeys(session, "CONTROL+KEY_P");
            bool written = ControlTestSupport.WaitUntil(() => File.Exists(reportPath), TimeSpan.FromSeconds(30));
            string report = written ? File.ReadAllText(reportPath) : string.Empty;

            string keyLabel = "Key Name:          ";
            string[] keyNames = report.Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.StartsWith("Key Name:", StringComparison.Ordinal))
                .ToArray();
            string rootName = keyLabel + $@"\\{LoopbackComputer}\HKEY_USERS\{remote.Sid}\Software\BWC-E2E\{remote.RunId}";
            bool rootLine = keyNames.Contains(rootName, StringComparer.Ordinal);
            bool childLine = keyNames.Contains(rootName + @"\" + remoteChild, StringComparer.Ordinal);
            bool allRemote = keyNames.Length >= 2 &&
                keyNames.All(line => line.StartsWith(keyLabel + $@"\\{LoopbackComputer}\HKEY_USERS\", StringComparison.Ordinal));
            E2EResults.Record(
                "print.remote.key-name",
                navigated && written && rootLine && childLine && allRemote,
                $"navigated={navigated}, report written={written}, root line={rootLine}, child line={childLine}, " +
                $"every Key Name is the remote form={allRemote}, names={keyNames.Length}.");

            bool dataFromStore = report.Contains("PrintRemote", StringComparison.Ordinal) &&
                report.Contains("child", StringComparison.Ordinal);
            E2EResults.Record(
                "print.remote.data",
                dataFromStore,
                $"the report holds the remote values from the loopback store: {dataFromStore}.");

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            DeleteDataRoot(config, "remote-print");
        }
    }

    private static void EnsurePublished(E2EConfig config)
    {
        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("remote.publish", false, ex.Message);
            Assert.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Connects to 127.0.0.1 through File > Connect Network Registry. A refused connect is "not run" (a note, and the
    /// test is Inconclusive) only when RemoteRegistry is not Running. Otherwise it is a failed check. A UI failure is a
    /// failed check.
    /// </summary>
    private static void RequireConnected(AppSession session, EditingDriver driver)
    {
        ConnectResult result = ConnectLoopback(session, driver);
        if (result.Connected)
        {
            E2EResults.Record("remote.connect", true, $"connected to '{LoopbackComputer}', node '{LoopbackNode}' present.");
            return;
        }

        if (result.RemoteError is not null)
        {
            RemoteRegistryService.RefusedConnect("remote.connect", "remote.connect.not-run", result.RemoteError);
        }

        E2EResults.Record("remote.connect", false, result.Detail);
        Assert.Fail(result.Detail);
    }

    private static ConnectResult ConnectLoopback(AppSession session, EditingDriver driver)
    {
        if (!OpenFileCommand(driver, MenuFileConnect))
        {
            return new ConnectResult(false, null, "File > Connect Network Registry did not open.");
        }

        // StartApp sets BWC_E2E_CONNECT_TARGET, so Connect skips the picker and connects to the loopback name.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (FindTreeRow(session, LoopbackNode) is not null)
            {
                return new ConnectResult(true, null, string.Empty);
            }

            string error = driver.TextOf(DialogErrorTextId);
            if (error.Length > 0)
            {
                _ = driver.DismissErrorBox();
                if (driver.WaitForElement(DialogRootId, TimeSpan.FromMilliseconds(300)) is not null)
                {
                    _ = driver.Invoke("DialogSecondary");
                }

                return new ConnectResult(false, error, error);
            }

            Thread.Sleep(100);
        }

        return new ConnectResult(false, null, $"Neither the '{LoopbackNode}' node nor an error appeared within 30 s.");
    }

    /// <summary>
    /// Selects the target row, then opens its menu with Shift+F10 (the keyboard path the context-menu tests use).
    /// A right-click after an address-bar move can miss the row, so the keyboard path is used. Retries with the menu closed.
    /// </summary>
    private static MenuSnapshot? OpenContextMenu(AppSession session, ContextMenuDriver menus, Func<AutomationElement?> target)
    {
        for (int attempt = 0; attempt < 3; attempt += 1)
        {
            AutomationElement? element = target();
            if (element is not null)
            {
                ControlTestSupport.Select(element);
                ControlTestSupport.SendKeys(session, "SHIFT+F10");
                MenuSnapshot? menu = menus.WaitForMenu();
                if (menu is not null)
                {
                    return menu;
                }
            }

            CloseMenus(session);
            _ = ControlTestSupport.WaitUntil(() => false, TimeSpan.FromMilliseconds(400));
        }

        return null;
    }

    private static void CloseMenus(AppSession session) => _ = ControlTestSupport.CloseOpenMenus(session);

    private static bool OpenFileCommand(EditingDriver driver, int commandId)
    {
        try
        {
            driver.OpenMenu(MenuFile, commandId);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool OpenFileMenu(EditingDriver driver)
    {
        try
        {
            driver.OpenMenu(MenuFile);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static AppSession StartApp(E2EConfig config, SandboxRegistry sandbox, string scope)
    {
        string dataRoot = CreateFreshDataRoot(config, scope);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
            ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            [ConnectTargetVariable] = LoopbackComputer,
        };
        return new AppSession(config, config.RegeditExePath, environment: environment);
    }

    private static string RemotePath(string rest) => LoopbackNode + "\\" + rest;

    private static string CurrentSid() =>
        WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("The current user SID is not available.");

    private static AutomationElement? FindTreeRow(AppSession session, string name) =>
        ControlTestSupport.FindRowByName(session, ControlTestSupport.TreeControl, ControlType.TreeItem, name);

    private static AutomationElement? FindListRow(AppSession session, string name) =>
        ControlTestSupport.FindRowByName(session, ControlTestSupport.ListControl, ControlType.DataItem, name);

    private static AutomationElement? SelectedTreeRow(AppSession session) =>
        ControlTestSupport.Rows(session, ControlTestSupport.TreeControl, ControlType.TreeItem)
            .FirstOrDefault(row => ControlTestSupport.IsSelected(row));

    private static string SelectedRowName(AppSession session) => ControlTestSupport.SafeName(SelectedTreeRow(session));

    private static bool SelectKey(AppSession session, string name) =>
        ControlTestSupport.WaitUntil(
            () =>
            {
                ControlTestSupport.Select(FindTreeRow(session, name));
                return ControlTestSupport.IsSelected(FindTreeRow(session, name));
            },
            TimeSpan.FromSeconds(10));

    private static bool SelectListRow(AppSession session, string name) =>
        ControlTestSupport.WaitUntil(
            () =>
            {
                ControlTestSupport.Select(FindListRow(session, name));
                return ControlTestSupport.IsSelected(FindListRow(session, name));
            },
            TimeSpan.FromSeconds(10));

    /// <summary>The children of the remote computer node, read in tree order until the next computer node.</summary>
    private static string[] RemoteChildNames(AppSession session)
    {
        AutomationElement[] rows = ControlTestSupport.Rows(session, ControlTestSupport.TreeControl, ControlType.TreeItem);
        int start = Array.FindIndex(
            rows,
            row => string.Equals(ControlTestSupport.SafeName(row), LoopbackNode, StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            return Array.Empty<string>();
        }

        var names = new List<string>();
        for (int index = start + 1; index < rows.Length; index++)
        {
            string name = ControlTestSupport.SafeName(rows[index]);
            if (name.StartsWith(@"\\", StringComparison.Ordinal))
            {
                break;
            }

            names.Add(name);
        }

        return names.ToArray();
    }

    /// <summary>
    /// Types a path into the address bar and presses Enter. Succeeds when the address bar shows the path and the
    /// named row is the selected tree row.
    /// </summary>
    private static bool NavigateAddress(AppSession session, string typedPath, string selectedRow)
    {
        _ = ControlTestSupport.ReleaseStuckModifiers();
        if (!ControlTestSupport.CloseOpenMenus(session) || !EditAddress(session, typedPath))
        {
            return false;
        }

        if (!TrySendKeys(session, "Enter"))
        {
            return false;
        }

        return ControlTestSupport.WaitUntil(
            () => string.Equals(SelectedRowName(session), selectedRow, StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(10));
    }

    private static bool EditAddress(AppSession session, string text)
    {
        if (!FocusAddressBar(session))
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
            address.AsTextBox().Text = string.Empty;
        }
        catch (Exception)
        {
            return false;
        }

        if (!TrySendKeys(session, text))
        {
            return false;
        }

        return ControlTestSupport.WaitUntil(
            () => string.Equals(AddressText(session), text, StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(5));
    }

    private static bool FocusAddressBar(AppSession session)
    {
        if (!ControlTestSupport.EnsureForeground(session))
        {
            return false;
        }

        return ControlTestSupport.WaitUntil(
            () =>
            {
                AutomationElement? address = session.Find(AddressBarId);
                if (address is null)
                {
                    return false;
                }

                try
                {
                    address.Focus();
                }
                catch (Exception)
                {
                    // The next pass reacquires the element.
                }

                return HasKeyboardFocus(session.Find(AddressBarId));
            },
            TimeSpan.FromSeconds(3));
    }

    private static bool ExpectAddressError(AppSession session, EditingDriver driver, string typedPath, string checkId)
    {
        bool typed = EditAddress(session, typedPath);
        bool submitted = typed && TrySendKeys(session, "Enter");
        bool dialog = submitted && driver.WaitForDialog(TimeSpan.FromSeconds(15));
        string text = dialog ? driver.TextOf(DialogErrorTextId) : string.Empty;
        bool dismissed = dialog && driver.DismissErrorBox();
        // The classic box names the typed path on its own line, then a blank line, then the not-found text.
        string expected = "Path: " + typedPath + Environment.NewLine + Environment.NewLine + AddressNotFoundText;
        bool matches = string.Equals(text, expected, StringComparison.Ordinal);
        E2EResults.Record(
            checkId,
            matches && dismissed,
            $"typed='{typedPath}', dialog={dialog}, message='{text}', dismissed={dismissed}.");
        return matches && dismissed;
    }

    private static string AddressText(AppSession session)
    {
        AutomationElement? address = session.Find(AddressBarId);
        return address is null ? string.Empty : session.TextOf(address);
    }

    /// <summary>Requires the address bar to show the remote run root or a key below it.</summary>
    private static void RequireAddressInside(AppSession session, RemoteRun remote, string checkId)
    {
        string address = AddressText(session);
        bool inside = E2EGuards.IsWithin(address, remote.DisplayRoot);
        E2EResults.Record(
            checkId,
            inside,
            $"address bar='{address}', required '{remote.DisplayRoot}' or a key below it, inside={inside}.");
        if (!inside)
        {
            Assert.Fail($"Refusing input: the address bar shows '{address}', not '{remote.DisplayRoot}'.");
        }
    }

    private static bool NewValueUnder(
        AppSession session,
        EditingDriver driver,
        ContextMenuDriver menus,
        string keyName,
        int command,
        string name)
    {
        MenuSnapshot? menu = OpenContextMenu(session, menus, () => FindTreeRow(session, keyName));
        bool invoked = menu is not null && menus.OpenSubmenu(CommandNew) && menus.Invoke(command);
        bool editor = invoked && ControlTestSupport.WaitUntil(
            () => session.Find(ListRenameBoxId) is not null,
            TimeSpan.FromSeconds(10));
        bool typed = editor && driver.TypeKeys(name) && driver.TypeKeys("Enter");
        E2EResults.Note(
            "remote.value.new.steps." + name,
            $"menu={menu is not null}, invoked={invoked}, editor={editor}, typed={typed}.");
        return typed;
    }

    /// <summary>Renames through the target menu. The editor text is replaced, then Enter commits it.</summary>
    private static bool RenameThrough(
        AppSession session,
        EditingDriver driver,
        ContextMenuDriver menus,
        Func<AutomationElement?> target,
        string editorId,
        string newName)
    {
        MenuSnapshot? menu = OpenContextMenu(session, menus, target);
        bool invoked = menu is not null && menus.Invoke(CommandRename);
        bool editor = invoked && ControlTestSupport.WaitUntil(
            () => session.Find(editorId) is not null,
            TimeSpan.FromSeconds(10));
        bool replaced = editor && TrySendKeys(session, "CONTROL+KEY_A") && driver.TypeKeys(newName);
        return replaced && driver.TypeKeys("Enter");
    }

    private static bool SetToggle(EditingDriver driver, string automationId, bool on)
    {
        AutomationElement? element = driver.WaitForElement(automationId, TimeSpan.FromSeconds(5));
        if (element is null)
        {
            return false;
        }

        try
        {
            if (!element.Patterns.Toggle.IsSupported)
            {
                return false;
            }

            bool current = element.Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.On;
            if (current != on)
            {
                element.Patterns.Toggle.Pattern.Toggle();
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static AutomationElement? RemoteComputerItem(EditingDriver driver)
    {
        AutomationElement? list = driver.WaitForElement(RemoteListId, TimeSpan.FromSeconds(5));
        return list?.FindFirstDescendant(cf => cf.ByName(LoopbackComputer));
    }

    /// <summary>Exports the selected key's branch through File > Export and saves it to a local file.</summary>

    private static bool ExportSelectedBranch(
        AppSession session,
        EditingDriver driver,
        SystemFileDialogDriver dialogs,
        string file,
        string checkId)
    {
        if (!OpenFileMenu(driver))
        {
            E2EResults.Record(checkId, false, "The File menu did not open.");
            return false;
        }

        AutomationElement? command = null;
        _ = ControlTestSupport.WaitUntil(
            () => (command = dialogs.FindProcessElement("MenuCommand_" + MenuFileExport)) is not null,
            TimeSpan.FromSeconds(5));
        if (command is null)
        {
            E2EResults.Record(checkId, false, $"File > Export (command {MenuFileExport}) was not found.");
            return false;
        }

        command.Focus();
        Thread.Sleep(150);
        Keyboard.Type(VirtualKeyShort.RETURN);
        IntPtr dialog = dialogs.WaitForDialog(TimeSpan.FromSeconds(10));
        if (dialog == IntPtr.Zero)
        {
            E2EResults.Record(checkId, false, "The Export save dialog did not open.");
            return false;
        }

        string nameNotes = "not attempted";
        string acceptNotes = "not attempted";
        bool named = dialogs.TrySetFileName(dialog, file, out nameNotes);
        bool accepted = named && dialogs.TryAccept(dialog, "Save", out acceptNotes);
        bool closed = accepted && dialogs.WaitGone(dialog, TimeSpan.FromSeconds(15));
        if (!closed)
        {
            _ = dialogs.TryCancel(dialog);
        }

        bool written = closed && ControlTestSupport.WaitUntil(() => File.Exists(file), TimeSpan.FromSeconds(20));

        // The progress dialog stays up for its minimum time after the file is written. Input waits until it is gone.
        bool progressGone = written && driver.WaitForNoDialog();
        E2EResults.Record(
            checkId,
            written && progressGone,
            $"{nameNotes} {acceptNotes} closed={closed}, file written={written}, progress dialog gone={progressGone}.");
        return written && progressGone;
    }

    private static void SeedLocalTree(SandboxRegistry sandbox)
    {
        sandbox.SetString(string.Empty, ExportText, ExportAlpha);
        sandbox.SetDword(string.Empty, ExportCount, ExportCountRoot);
        sandbox.SetString(ExportChildKey, ExportText, ExportBeta);
        sandbox.SetDword(ExportChildKey, ExportCount, ExportCountChild);
    }

    private static void SeedRemoteTree(RemoteRun remote)
    {
        remote.Seed(string.Empty, ExportText, ExportAlpha, RegistryValueKind.String);
        remote.Seed(string.Empty, ExportCount, ExportCountRoot, RegistryValueKind.DWord);
        remote.Seed(ExportChildKey, ExportText, ExportBeta, RegistryValueKind.String);
        remote.Seed(ExportChildKey, ExportCount, ExportCountChild, RegistryValueKind.DWord);
    }

    private static string[] ReadRegLines(string path) =>
        File.Exists(path)
            ? File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            : Array.Empty<string>();

    /// <summary>Rewrites key headers under HKEY_USERS\SID to HKEY_CURRENT_USER, the local form of the keys.</summary>
    private static string[] WithLocalHeaders(string[] lines, string sid)
    {
        string prefix = @"[HKEY_USERS\" + sid + @"\";
        return lines
            .Select(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? @"[HKEY_CURRENT_USER\" + line[prefix.Length..]
                : line)
            .ToArray();
    }

    private static int FirstDifference(string[] left, string[] right)
    {
        int count = Math.Max(left.Length, right.Length);
        for (int index = 0; index < count; index++)
        {
            string a = index < left.Length ? left[index] : "<missing>";
            string b = index < right.Length ? right[index] : "<missing>";
            if (!string.Equals(a, b, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TrySendKeys(AppSession session, string key)
    {
        try
        {
            ControlTestSupport.SendKeys(session, key);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool HasKeyboardFocus(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            return element.Properties.HasKeyboardFocus.Value;
        }
        catch (Exception)
        {
            return false;
        }
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

    private static void Screenshot(AppSession session, string checkId)
    {
        try
        {
            string path = E2EResults.CaptureScreenshot(checkId, session);
            E2EResults.Note(checkId + ".screenshot", $"saved '{path}'.");
        }
        catch (Exception ex)
        {
            E2EResults.Note(checkId + ".screenshot", $"not saved: {ex.GetType().Name}: {ex.Message}");
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

    private static void DeleteDataRoot(E2EConfig config, string scope)
    {
        string path = config.DataRootFor(scope);
        // BWC_E2E_KEEP_DATA=1 keeps the data folder, as the harness does for a failed run, so the app log can be read.
        if (string.Equals(Environment.GetEnvironmentVariable("BWC_E2E_KEEP_DATA"), "1", StringComparison.Ordinal))
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
            $"Remote command checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private sealed record NewValueSpec(int Command, string Name, RegistryValueKind Kind);

    private sealed record ConnectResult(bool Connected, string? RemoteError, string Detail);

    /// <summary>
    /// Reads and writes under HKEY_USERS\SID\Software\BWC-E2E\run-id only. The root is fixed at construction, so a
    /// relative path cannot leave the run subtree.
    /// </summary>
    private sealed class RemoteRun
    {
        private readonly string _root;
        private readonly SandboxRegistry _sandbox;

        /// <summary>
        /// HKEY_USERS\SID\Software\BWC-E2E\run is the same key as the local sandbox, so every write is seeded through
        /// the local sandbox helper. The remote side only reads it back, through the loopback connection and the UI.
        /// </summary>
        public RemoteRun(string sid, E2EConfig config)
        {
            Sid = sid;
            RunId = config.RunId;
            _root = $@"{sid}\Software\BWC-E2E\{RunId}";
            _sandbox = new SandboxRegistry(config);
        }

        public string Sid { get; }

        public string RunId { get; }

        public string DisplayRoot => $@"\\{LoopbackComputer}\HKEY_USERS\{Sid}\Software\BWC-E2E\{RunId}";

        public void Seed(string relativePath, string name, object value, RegistryValueKind kind)
        {
            _sandbox.CreateKey(relativePath);
            _sandbox.SetValueOfKind(relativePath, name, value, kind);
        }

        public bool KeyExists(string relativePath)
        {
            using RegistryKey? key = Registry.Users.OpenSubKey(FullPath(relativePath), writable: false);
            return key is not null;
        }

        public RegistryValueKind? KindOf(string relativePath, string name)
        {
            using RegistryKey? key = Registry.Users.OpenSubKey(FullPath(relativePath), writable: false);
            if (key is null)
            {
                return null;
            }

            return key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)
                ? key.GetValueKind(name)
                : null;
        }

        public object? ValueOf(string relativePath, string name)
        {
            using RegistryKey? key = Registry.Users.OpenSubKey(FullPath(relativePath), writable: false);
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }

        /// <summary>Value names and kinds under the key, for the artifact note.</summary>
        public string Describe(string relativePath)
        {
            using RegistryKey? key = Registry.Users.OpenSubKey(FullPath(relativePath), writable: false);
            if (key is null)
            {
                return "key missing";
            }

            return string.Join(
                "; ",
                key.GetValueNames()
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .Select(name => $"{name}:{key.GetValueKind(name)}"));
        }

        /// <summary>Removes the run subtree only. Missing keys are ignored.</summary>
        public void Cleanup() => _sandbox.Cleanup();

        private string FullPath(string relativePath) =>
            string.IsNullOrEmpty(relativePath) ? _root : $@"{_root}\{relativePath}";
    }
}

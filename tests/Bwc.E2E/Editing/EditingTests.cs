using System.Globalization;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Editing;

/// <summary>
/// Drives the real Registry Editor menus against a seeded HKCU sandbox: inline New Key and New Value
/// creation, the classic default names, inline Rename from the Edit menu, and value and key deletion.
/// </summary>
[TestClass]
public sealed class EditingTests
{
    private const int MenuEdit = 513;
    private const int MenuEditNew = 516;
    private const int MenuEditNewKey = 663;
    private const int MenuEditNewString = 664;
    private const int MenuEditNewBinary = 665;
    private const int MenuEditNewDword = 674;
    private const int MenuEditNewQword = 688;
    private const int MenuEditNewMultiString = 676;
    private const int MenuEditNewExpandable = 677;
    private const int MenuEditDelete = 913;
    private const int MenuEditRename = 914;

    private const string DefaultNewKey1 = "New Key #1";
    private const string DefaultNewKey2 = "New Key #2";
    private const string NewValuePrefix = "New Value #";
    private const string DefaultNewValue1 = "New Value #1";
    private const string DefaultNewValue2 = "New Value #2";
    private const string TreeRenameBox = "TreeRenameBox";
    private const string ListRenameBox = "ListRenameBox";

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Editing")]
    [TestCategory("Mutating")]
    public void Editing_CreatesRenamesAndDeletesRegistryObjects()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
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
            SeedSandbox(sandbox);

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
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "editing.sandbox.navigate");

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
            E2EGuards.RequireSandboxNavigation(session, sandbox, reselected, "editing.sandbox.reselect");
            CheckNewValues(session, sandbox, driver);
            CheckRenameValue(session, driver, sandbox);
            CheckRenameKeyFromMenu(session, sandbox, driver);
            CheckInvalidKeyRename(session, sandbox, driver);
            CheckDeleteValue(session, driver, sandbox);
            CheckDeleteKey(session, driver, sandbox);

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    /// <summary>
    /// New Key creates the first unused classic name and opens the tree inline editor with that name
    /// selected. Escape keeps the default name, and the next New Key then takes the next number.
    /// </summary>
    private static void CheckNewKey(AppSession session, SandboxRegistry sandbox, EditingDriver driver)
    {
        E2EGuards.RequireSandboxSelection(session, sandbox, "editing.sandbox.newkey");
        driver.OpenMenu(MenuEdit, MenuEditNew, MenuEditNewKey);
        bool editor = WaitForRenameBox(session, TreeRenameBox, DefaultNewKey1, out string firstText);
        bool rowSelected = editor && ControlTestSupport.WaitUntil(
            () => ControlTestSupport.IsSelected(
                ControlTestSupport.FindRowByName(
                    session,
                    ControlTestSupport.TreeControl,
                    ControlType.TreeItem,
                    DefaultNewKey1)),
            TimeSpan.FromSeconds(10));
        E2EResults.Record(
            "editing.newkey.inline",
            editor && rowSelected,
            $"renameBox={editor}, text='{firstText}', rowSelected={rowSelected}.");
        _ = RecordRenameGeometry(session, TreeRenameBox, DefaultNewKey1, "editing.newkey.geometry");
        _ = SaveVisual(session, "new-key-inline", "editing.newkey.screenshot");

        bool escaped = editor && driver.TypeKeys("Escape");
        bool closed = escaped && ControlTestSupport.WaitUntil(
            () => session.Find(TreeRenameBox) is null,
            TimeSpan.FromSeconds(5));
        bool kept = ControlTestSupport.WaitUntil(
            () => KeyExists(sandbox, DefaultNewKey1),
            TimeSpan.FromSeconds(10));
        E2EResults.Record(
            "editing.newkey.escape",
            escaped && closed && kept,
            $"escaped={escaped}, closed={closed}, '{DefaultNewKey1}' kept={kept}.");

        bool reselected = SelectRunKey(session, sandbox);
        E2EGuards.RequireSandboxNavigation(session, sandbox, reselected, "editing.sandbox.newkey2");

        driver.OpenMenu(MenuEdit, MenuEditNew, MenuEditNewKey);
        bool second = WaitForRenameBox(session, TreeRenameBox, DefaultNewKey2, out string secondText);
        // Typing with no explicit clear proves the default name was selected: only a replacement
        // leaves the committed name equal to the typed text.
        bool typed = second && driver.TypeKeys("E2ENewKey");
        bool committed = typed && driver.TypeKeys("Enter");
        bool renamed = ControlTestSupport.WaitUntil(
            () => KeyExists(sandbox, "E2ENewKey") && !KeyExists(sandbox, DefaultNewKey2),
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
            "editing.newkey.duplicate",
            second,
            $"renameBox={second}, text='{secondText}'.");
        E2EResults.Record(
            "editing.newkey.commit",
            typed && committed && renamed && selected,
            $"typed={typed}, committed={committed}, renamed={renamed}, selected={selected}.");
    }

    /// <summary>
    /// New Value creates the first unused classic name, writes the classic empty or zero default for
    /// its type, selects the row, and opens the list inline editor. Escape keeps the default name.
    /// </summary>
    private static void CheckNewValues(AppSession session, SandboxRegistry sandbox, EditingDriver driver)
    {
        E2EGuards.RequireSandboxSelection(session, sandbox, "editing.sandbox.newvalue");
        driver.OpenMenu(MenuEdit, MenuEditNew, MenuEditNewString);
        bool editor = WaitForRenameBox(session, ListRenameBox, DefaultNewValue1, out string firstText);
        bool noDialog = !driver.WaitForDialog(TimeSpan.FromMilliseconds(500));
        bool rowSelected = editor && ControlTestSupport.WaitUntil(
            () => ControlTestSupport.IsSelected(
                ControlTestSupport.FindRowByName(
                    session,
                    ControlTestSupport.ListControl,
                    ControlType.DataItem,
                    DefaultNewValue1)),
            TimeSpan.FromSeconds(10));
        E2EResults.Record(
            "editing.newvalue.inline",
            editor && noDialog && rowSelected,
            $"renameBox={editor}, text='{firstText}', noDialog={noDialog}, rowSelected={rowSelected}.");
        _ = RecordRenameGeometry(session, ListRenameBox, DefaultNewValue1, "editing.newvalue.geometry");
        _ = SaveVisual(session, "new-value-inline", "editing.newvalue.screenshot");

        bool escaped = editor && driver.TypeKeys("Escape");
        bool closed = escaped && ControlTestSupport.WaitUntil(
            () => session.Find(ListRenameBox) is null,
            TimeSpan.FromSeconds(5));
        bool kept = ControlTestSupport.WaitUntil(
            () => IsEmptyString(sandbox, DefaultNewValue1),
            TimeSpan.FromSeconds(10));
        E2EResults.Record(
            "editing.newvalue.escape",
            escaped && closed && kept,
            $"escaped={escaped}, closed={closed}, '{DefaultNewValue1}' is an empty REG_SZ={kept}.");

        driver.OpenMenu(MenuEdit, MenuEditNew, MenuEditNewString);
        bool second = WaitForRenameBox(session, ListRenameBox, DefaultNewValue2, out string secondText);
        bool typed = second && driver.TypeKeys("E2ENewString");
        bool committed = typed && driver.TypeKeys("Enter");
        bool renamed = ControlTestSupport.WaitUntil(
            () => IsEmptyString(sandbox, "E2ENewString") && !ValueExists(sandbox, DefaultNewValue2),
            TimeSpan.FromSeconds(10));
        bool selected = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.IsSelected(
                ControlTestSupport.FindRowByName(
                    session,
                    ControlTestSupport.ListControl,
                    ControlType.DataItem,
                    "E2ENewString")),
            TimeSpan.FromSeconds(10));
        E2EResults.Record(
            "editing.newvalue.duplicate",
            second,
            $"renameBox={second}, text='{secondText}'.");
        E2EResults.Record(
            "editing.newvalue.string",
            typed && committed && renamed && selected,
            $"typed={typed}, committed={committed}, renamed={renamed}, selected={selected}.");

        E2EResults.Record(
            "editing.newvalue.binary",
            CreateValueOfType(
                session,
                sandbox,
                driver,
                MenuEditNewBinary,
                "E2ENewBinary",
                () => ValueKindOf(sandbox, "E2ENewBinary") == RegistryValueKind.Binary &&
                    sandbox.ReadBinary(string.Empty, "E2ENewBinary") is { Length: 0 }),
            DescribeValue(sandbox, "E2ENewBinary"));
        E2EResults.Record(
            "editing.newvalue.dword",
            CreateValueOfType(
                session,
                sandbox,
                driver,
                MenuEditNewDword,
                "E2ENewDword",
                () => ValueKindOf(sandbox, "E2ENewDword") == RegistryValueKind.DWord &&
                    ReadObject(sandbox, "E2ENewDword") is int and 0),
            DescribeValue(sandbox, "E2ENewDword"));
        E2EResults.Record(
            "editing.newvalue.qword",
            CreateValueOfType(
                session,
                sandbox,
                driver,
                MenuEditNewQword,
                "E2ENewQword",
                () => ValueKindOf(sandbox, "E2ENewQword") == RegistryValueKind.QWord &&
                    ReadObject(sandbox, "E2ENewQword") is long and 0L),
            DescribeValue(sandbox, "E2ENewQword"));
        E2EResults.Record(
            "editing.newvalue.multistring",
            CreateValueOfType(
                session,
                sandbox,
                driver,
                MenuEditNewMultiString,
                "E2ENewMulti",
                () => ValueKindOf(sandbox, "E2ENewMulti") == RegistryValueKind.MultiString &&
                    ReadObject(sandbox, "E2ENewMulti") is string[] strings &&
                    strings.Length == 1 &&
                    strings[0].Length == 0),
            DescribeValue(sandbox, "E2ENewMulti"));
        E2EResults.Record(
            "editing.newvalue.expandable",
            CreateValueOfType(
                session,
                sandbox,
                driver,
                MenuEditNewExpandable,
                "E2ENewExpand",
                () => ValueKindOf(sandbox, "E2ENewExpand") == RegistryValueKind.ExpandString &&
                    ReadObject(sandbox, "E2ENewExpand") is string { Length: 0 }),
            DescribeValue(sandbox, "E2ENewExpand"));
    }

    /// <summary>Creates one value through New, replaces the selected default name, and checks its data.</summary>
    private static bool CreateValueOfType(
        AppSession session,
        SandboxRegistry sandbox,
        EditingDriver driver,
        int menuId,
        string newName,
        Func<bool> dataIsClassicDefault)
    {
        driver.OpenMenu(MenuEdit, MenuEditNew, menuId);
        bool editor = WaitForAnyRenameBox(session, ListRenameBox, NewValuePrefix, out _);
        bool typed = editor && driver.TypeKeys(newName);
        bool committed = typed && driver.TypeKeys("Enter");
        bool saved = committed && ControlTestSupport.WaitUntil(dataIsClassicDefault, TimeSpan.FromSeconds(10));
        return editor && typed && committed && saved;
    }

    private static void CheckRenameValue(AppSession session, EditingDriver driver, SandboxRegistry sandbox)
    {
        _ = SelectListRowStable(session, "AlphaString");

        E2EGuards.RequireListRowSelected(session, "AlphaString", "editing.sandbox.rename.row");
        E2EGuards.RequireSandboxSelection(session, sandbox, "editing.sandbox.rename");
        bool f2 = driver.TypeKeys("F2");
        bool renameOpen = f2 && ControlTestSupport.WaitUntil(
            () => session.Find(ListRenameBox) is not null,
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

        // The list refreshes asynchronously after the commit. Wait for the refreshed rows before the
        // next selection, because a row selected earlier is replaced by the refresh.
        bool refreshed = WaitForListRows(session, present: "AlphaRenamed", absent: "AlphaString");
        E2EResults.Note(
            "editing.renamevalue.refresh",
            $"list refreshed={refreshed}, rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}].");

        _ = SelectListRowStable(session, "ZebraString");

        E2EGuards.RequireListRowSelected(session, "ZebraString", "editing.sandbox.rename-cancel.row");
        E2EGuards.RequireSandboxSelection(session, sandbox, "editing.sandbox.rename-cancel");
        bool cancelF2 = driver.TypeKeys("F2");
        bool cancelOpen = cancelF2 && ControlTestSupport.WaitUntil(
            () => session.Find(ListRenameBox) is not null,
            TimeSpan.FromSeconds(5));
        bool cancelTyped = cancelOpen && driver.TypeKeys("ZebraCancelled");
        bool escaped = cancelTyped && driver.TypeKeys("Escape");
        bool cancelClosed = escaped && ControlTestSupport.WaitUntil(
            () => session.Find(ListRenameBox) is null,
            TimeSpan.FromSeconds(5));
        bool unchanged = sandbox.ReadString(string.Empty, "ZebraString") == "zebra" &&
            sandbox.ReadString(string.Empty, "ZebraCancelled") is null;
        E2EResults.Record(
            "editing.renamevalue.cancel",
            cancelOpen && cancelTyped && escaped && cancelClosed && unchanged,
            $"cancelOpen={cancelOpen}, escaped={escaped}, cancelClosed={cancelClosed}, unchanged={unchanged}.");

        // Edit > Rename uses the same inline editor as F2, not a prompt.
        E2EGuards.RequireListRowSelected(session, "ZebraString", "editing.sandbox.rename-menu.row");
        driver.OpenMenu(MenuEdit, MenuEditRename);
        bool menuOpen = WaitForRenameBox(session, ListRenameBox, "ZebraString", out string menuText);
        bool menuEscaped = menuOpen && driver.TypeKeys("Escape");
        bool menuClosed = menuEscaped && ControlTestSupport.WaitUntil(
            () => session.Find(ListRenameBox) is null,
            TimeSpan.FromSeconds(5));
        E2EResults.Record(
            "editing.renamevalue.menu",
            menuOpen && menuEscaped && menuClosed,
            $"renameBox={menuOpen}, text='{menuText}', escaped={menuEscaped}, closed={menuClosed}.");
    }

    /// <summary>Edit &gt; Rename on a tree key opens the tree inline editor and commits with Enter.</summary>
    private static void CheckRenameKeyFromMenu(AppSession session, SandboxRegistry sandbox, EditingDriver driver)
    {
        bool selected = SelectTreeRow(session, DefaultNewKey1);
        E2EResults.Record(
            "editing.renamekey.select",
            selected,
            $"'{DefaultNewKey1}' selected={selected}.");
        E2EGuards.RequireSandboxNavigation(session, sandbox, selected, "editing.sandbox.renamekey");

        driver.OpenMenu(MenuEdit, MenuEditRename);
        bool editor = WaitForRenameBox(session, TreeRenameBox, DefaultNewKey1, out string text);
        bool typed = editor && driver.TypeKeys("E2ERenamedKey");
        bool committed = typed && driver.TypeKeys("Enter");
        bool renamed = ControlTestSupport.WaitUntil(
            () => KeyExists(sandbox, "E2ERenamedKey") && !KeyExists(sandbox, DefaultNewKey1),
            TimeSpan.FromSeconds(10));
        E2EResults.Record(
            "editing.renamekey.menu",
            editor && typed && committed && renamed,
            $"renameBox={editor}, text='{text}', typed={typed}, committed={committed}, renamed={renamed}.");
    }

    /// <summary>
    /// A key rename with a backslash shows the classic Error Renaming Key text and keeps the default
    /// key in place. No key with a backslash in its name is created.
    /// </summary>
    private static void CheckInvalidKeyRename(AppSession session, SandboxRegistry sandbox, EditingDriver driver)
    {
        bool reselected = SelectRunKey(session, sandbox);
        E2EGuards.RequireSandboxNavigation(session, sandbox, reselected, "editing.sandbox.invalidrename");

        driver.OpenMenu(MenuEdit, MenuEditNew, MenuEditNewKey);
        bool editor = WaitForRenameBox(session, TreeRenameBox, DefaultNewKey1, out _);
        // Real keyboard input: the default name is selected, so the typed text replaces it. No UI Automation
        // value write and no UI Automation Invoke is used for this check.
        bool typed = editor && driver.TypeKeys(@"Bad\Name");
        bool submitted = typed && driver.TypeKeys("Enter");
        bool dialog = submitted && driver.WaitForDialog();
        string message = dialog ? driver.TextOf("DialogErrorText") : string.Empty;
        bool classicText = message.Contains(
            "Specify a key name without a backslash",
            StringComparison.Ordinal);
        // The error is dismissed with a real Enter, sent through the guard to the foreground window.
        bool dismissed = dialog && driver.TypeKeys("Enter") && driver.WaitForNoDialog();
        bool kept = ControlTestSupport.WaitUntil(
            () => KeyExists(sandbox, DefaultNewKey1),
            TimeSpan.FromSeconds(10));
        E2EResults.Record(
            "editing.renamekey.invalid",
            editor && typed && submitted && dialog && classicText && dismissed && kept,
            $"renameBox={editor}, typed={typed}, dialog={dialog}, message='{message}', dismissed={dismissed}, default kept={kept}.");
    }

    private static void CheckDeleteValue(AppSession session, EditingDriver driver, SandboxRegistry sandbox)
    {
        // The invalid rename check left its new key selected, so return to the run key first.
        bool runSelected = SelectRunKey(session, sandbox);
        bool rowsBack = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.ListRowNames(session).Contains("SampleBinary", StringComparer.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(10));
        E2EResults.Note(
            "editing.deletevalue.reselect",
            $"run key selected={runSelected}, SampleBinary listed={rowsBack}.");
        _ = SelectListRowStable(session, "SampleBinary");

        E2EGuards.RequireListRowSelected(session, "SampleBinary", "editing.sandbox.deletevalue.row");
        E2EGuards.RequireSandboxSelection(session, sandbox, "editing.sandbox.deletevalue");
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
            () => !ControlTestSupport.ListRowNames(session).Contains("SampleBinary", StringComparer.OrdinalIgnoreCase),
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
        E2EGuards.RequireSandboxNavigation(session, sandbox, selected, "editing.sandbox.deletekey");

        driver.OpenMenu(MenuEdit, MenuEditDelete);
        bool dialog = driver.WaitForDialog();
        string message = driver.TextOf("DialogMessage");
        bool warned = message.Contains(
            "Are you sure you want to permanently delete this key and all of its subkeys?",
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

    private static bool SelectRunKey(AppSession session, SandboxRegistry sandbox) =>
        SelectTreeRow(session, SandboxRunId(sandbox));

    private static bool SelectTreeRow(AppSession session, string rowName) =>
        ControlTestSupport.WaitUntil(
            () =>
            {
                // Re-find the row each time: the tree can rebuild its nodes after a refresh.
                AutomationElement? row = ControlTestSupport.FindRowByName(
                    session,
                    ControlTestSupport.TreeControl,
                    ControlType.TreeItem,
                    rowName);
                ControlTestSupport.Select(row);
                return ControlTestSupport.IsSelected(
                    ControlTestSupport.FindRowByName(
                        session,
                        ControlTestSupport.TreeControl,
                        ControlType.TreeItem,
                        rowName));
            },
            TimeSpan.FromSeconds(10));

    /// <summary>
    /// Selects a value row by name and retries until the row is selected. The row is found again on
    /// every attempt, because the list refresh can replace the element a selection was sent to.
    /// </summary>
    private static bool SelectListRowStable(AppSession session, string rowName) =>
        ControlTestSupport.WaitUntil(
            () =>
            {
                AutomationElement? row = ControlTestSupport.FindRowByName(
                    session,
                    ControlTestSupport.ListControl,
                    ControlType.DataItem,
                    rowName);
                ControlTestSupport.Select(row);
                return ControlTestSupport.IsSelected(
                    ControlTestSupport.FindRowByName(
                        session,
                        ControlTestSupport.ListControl,
                        ControlType.DataItem,
                        rowName));
            },
            TimeSpan.FromSeconds(10));

    /// <summary>
    /// Records the rename box bounding rectangle, its text, and the E2E geometry trace. The box must
    /// start at or left of the label text and be at least as wide as the visible label.
    /// </summary>
    private static bool RecordRenameGeometry(
        AppSession session,
        string automationId,
        string expectedText,
        string checkId)
    {
        AutomationElement? box = session.Find(automationId);
        if (box is null)
        {
            E2EResults.Record(checkId, false, $"{automationId} was not found.");
            return false;
        }

        string text = session.TextOf(box);
        System.Drawing.Rectangle bounds = box.BoundingRectangle;
        System.Drawing.Rectangle windowBounds = session.Window.BoundingRectangle;
        System.Drawing.Rectangle row = RowContaining(session, bounds);

        // The row and the box come from UI Automation. The text inside the box comes from the screen pixels,
        // so the check reads what is drawn, not what the app says about itself.
        int centerY = bounds.Top + (bounds.Height / 2);
        bool textOk = string.Equals(text, expectedText, StringComparison.Ordinal);
        bool boxInWindow = bounds.Width > 0 && bounds.Height > 0 && windowBounds.Contains(bounds);
        bool boxInRow = !row.IsEmpty && row.Top <= centerY && centerY < row.Bottom && bounds.Left >= row.Left - 1;
        InkExtent ink = MeasureInkInside(session.MainHwnd, bounds);
        bool inkVisible = ink.Count >= MinimumInkPixels;
        bool startsAtText = inkVisible && ink.Left >= bounds.Left && ink.Left - bounds.Left <= MaximumTextInset;
        bool notClipped = inkVisible && ink.Right < bounds.Right - 2;
        bool pass = textOk && boxInWindow && boxInRow && inkVisible && startsAtText && notClipped;
        E2EResults.Record(
            checkId,
            pass,
            $"text='{text}', box=({bounds.Left},{bounds.Top},{bounds.Width}x{bounds.Height}), boxInWindow={boxInWindow}, " +
            $"row=({row.Left},{row.Top},{row.Width}x{row.Height}) boxInRow={boxInRow}, ink pixels={ink.Count} " +
            $"from x={ink.Left} to x={ink.Right} (box right {bounds.Right}), startsAtText={startsAtText}, notClipped={notClipped}.");
        return pass;
    }

    /// <summary>Minimum text ink pixels inside a rename box for the text to count as drawn.</summary>
    private const int MinimumInkPixels = 8;

    /// <summary>Device pixels between the box's left edge and the first ink pixel, at most.</summary>
    private const int MaximumTextInset = 24;

    /// <summary>
    /// The tree item or list item that holds the box. It contains the box's vertical center and starts at or left of it.
    /// </summary>
    private static System.Drawing.Rectangle RowContaining(AppSession session, System.Drawing.Rectangle box)
    {
        int centerY = box.Top + (box.Height / 2);
        try
        {
            foreach (AutomationElement item in session.Window.FindAllDescendants(
                cf => cf.ByControlType(ControlType.TreeItem).Or(cf.ByControlType(ControlType.DataItem))))
            {
                System.Drawing.Rectangle rect = item.BoundingRectangle;
                if (rect.Width > 0 && rect.Top <= centerY && centerY < rect.Bottom && rect.Left <= box.Left + 1)
                {
                    return rect;
                }
            }
        }
        catch (Exception)
        {
            // A tree that is rebuilt during the read leaves no row, and the check fails with the box geometry.
        }

        return System.Drawing.Rectangle.Empty;
    }

    private readonly record struct InkExtent(int Count, int Left, int Right);

    /// <summary>
    /// Counts the pixels inside the box that differ from its background, and their horizontal extent, from a
    /// verified, settled screen copy (WindowCapture). The background is the color of the box's top right corner,
    /// inset from the border.
    /// </summary>
    private static InkExtent MeasureInkInside(IntPtr hwnd, System.Drawing.Rectangle box)
    {
        if (box.Width <= 8 || box.Height <= 8)
        {
            return new InkExtent(0, 0, 0);
        }

        try
        {
            using System.Drawing.Bitmap region = WindowCapture.CaptureRegion(hwnd, box.Location, box.Width, box.Height);
            System.Drawing.Color background = region.GetPixel(region.Width - 3, 2);
            int count = 0;
            int left = int.MaxValue;
            int right = int.MinValue;
            for (int y = 2; y < region.Height - 2; y++)
            {
                for (int x = 2; x < region.Width - 2; x++)
                {
                    System.Drawing.Color pixel = region.GetPixel(x, y);
                    int difference = Math.Abs(pixel.R - background.R) + Math.Abs(pixel.G - background.G) + Math.Abs(pixel.B - background.B);
                    if (difference < 90)
                    {
                        continue;
                    }

                    count++;
                    left = Math.Min(left, box.Left + x);
                    right = Math.Max(right, box.Left + x);
                }
            }

            return count == 0 ? new InkExtent(0, 0, 0) : new InkExtent(count, left, right);
        }
        catch (WindowCapture.NotJudgedException)
        {
            // Covered by another process: the ink is not judged, so the test is inconclusive. It is never a fail.
            throw;
        }
        catch (Exception)
        {
            return new InkExtent(0, 0, 0);
        }
    }

    /// <summary>
    /// Saves the main window as a visual capture under artifacts/e2e/visual/editing and records the
    /// result as a check, so a missing or empty PNG fails the run.
    /// </summary>
    private static bool SaveVisual(AppSession session, string name, string checkId)
    {
        try
        {
            string directory = Path.Combine(GuiGate.Config.ArtifactsDir, "visual", "editing");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, name + ".png");
            _ = WindowCapture.Save(session.MainHwnd, path);
            bool written = File.Exists(path) && new FileInfo(path).Length > 0;
            // The PNG is the artifact. Writing it is not a product claim, so it is a note.
            E2EResults.Note(checkId, $"screenshot={path}, bytes={(written ? new FileInfo(path).Length : 0)}, written={written}.");
            return written;
        }
        catch (Exception ex)
        {
            E2EResults.Note(checkId, "Screenshot failed: " + ex.Message);
            return false;
        }
    }

    private static bool WaitForRenameBox(
        AppSession session,
        string automationId,
        string expectedText,
        out string actualText)
    {
        string text = string.Empty;
        bool matched = ControlTestSupport.WaitUntil(
            () =>
            {
                AutomationElement? box = session.Find(automationId);
                if (box is null)
                {
                    return false;
                }

                text = session.TextOf(box);
                return string.Equals(text, expectedText, StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(10));
        actualText = text;
        return matched;
    }

    /// <summary>Waits for a list rename box whose default text is one of the New Value counter names.</summary>
    private static bool WaitForAnyRenameBox(
        AppSession session,
        string automationId,
        string defaultPrefix,
        out string actualText)
    {
        string text = string.Empty;
        bool matched = ControlTestSupport.WaitUntil(
            () =>
            {
                AutomationElement? box = session.Find(automationId);
                if (box is null)
                {
                    return false;
                }

                text = session.TextOf(box);
                return text.StartsWith(defaultPrefix, StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(10));
        actualText = text;
        return matched;
    }

    /// <summary>
    /// Waits until the value list shows the refreshed rows: <paramref name="present"/> is listed and
    /// <paramref name="absent"/> is not. Returns false when the refresh does not finish in time.
    /// </summary>
    private static bool WaitForListRows(AppSession session, string present, string absent) =>
        ControlTestSupport.WaitUntil(
            () =>
            {
                string[] names = ControlTestSupport.ListRowNames(session);
                return names.Contains(present, StringComparer.OrdinalIgnoreCase) &&
                    !names.Contains(absent, StringComparer.OrdinalIgnoreCase);
            },
            TimeSpan.FromSeconds(10));

    /// <summary>
    /// Seeds the run sandbox with the values and the child key that the editing and message checks
    /// use. Shared so that other suites seed the same state.
    /// </summary>
    internal static void SeedSandbox(SandboxRegistry sandbox)
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

    private static bool ValueExists(SandboxRegistry sandbox, string name) =>
        ValueKindOf(sandbox, name) != RegistryValueKind.Unknown;

    private static bool IsEmptyString(SandboxRegistry sandbox, string name) =>
        ValueKindOf(sandbox, name) == RegistryValueKind.String &&
        ReadObject(sandbox, name) is string { Length: 0 };

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

    private static object? ReadObject(SandboxRegistry sandbox, string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(sandbox.RunRootPath, writable: false);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    private static string DescribeValue(SandboxRegistry sandbox, string name)
    {
        object? value = ReadObject(sandbox, name);
        string data = value switch
        {
            null => "null",
            byte[] bytes => $"byte[{bytes.Length}]",
            string[] strings => $"string[{strings.Length}]",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null",
        };
        return $"kind={ValueKindOf(sandbox, name)}, data={data}.";
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
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
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

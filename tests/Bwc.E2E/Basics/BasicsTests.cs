using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Basics;

/// <summary>
/// Value and key basics against a seeded HKCU sandbox: Enter and double-click open the editor for each
/// value type, the Edit menu shows the Modify items only with a value selected, Copy Key Name copies the
/// classic path, F5 keeps expansion and selection and shows a value added behind the app, Delete acts on
/// the pane with focus, a multi-value delete uses the classic text, and the (Default) row is listed for a
/// key without a default value. Every mutating step is cancelled, except the key delete on the sandbox
/// key. The failure modes are in docs/failure-modes/basics.md.
/// </summary>
[TestClass]
public sealed class BasicsTests
{
    private const string StringName = "BasicsString";
    private const string ExpandName = "BasicsExpand";
    private const string MultiName = "BasicsMulti";
    private const string DwordName = "BasicsDword";
    private const string QwordName = "BasicsQword";
    private const string BinaryName = "BasicsBinary";
    private const string ChildKey = "BasicsChild";
    private const string GrandKey = "BasicsGrand";
    private const string DoomedKey = "BasicsDoomed";
    private const string NoDefaultKey = "BasicsNoDefault";
    private const string InsideValue = "Inside";
    private const string KeptValue = "Keep";
    private const string OnlyValue = "Only";
    private const string AddedValue = "AddedBehind";
    private const string DefaultLabel = "(Default)";
    private const string ValueNotSetText = "(value not set)";
    private const string StringText = "basic text";
    private const int DwordData = 4660;

    private const int MenuEdit = 513;
    private const int MenuCopyKeyName = 675;
    private const int MenuModify = 912;
    private const int MenuModifyBinary = 917;

    private const string EditStringCaption = "Edit String";
    private const string EditExpandCaption = "Edit Expandable String Value";
    private const string EditMultiCaption = "Edit Multi-String";
    private const string EditDwordCaption = "Edit DWORD (32-bit) Value";
    private const string EditQwordCaption = "Edit QWORD (64-bit) Value";
    private const string EditBinaryCaption = "Edit Binary Data";
    private const string ConfirmKeyCaption = "Confirm Key Delete";
    private const string ConfirmValueCaption = "Confirm Value Delete";

    /// <summary>Oracle string 50, the text for two or more values (MsgDeleteValuesConfirm).</summary>
    private const string DeleteValuesText =
        "Deleting certain registry values could cause system instability. Are you sure you want to permanently delete these values?";

    private static readonly TimeSpan ShortWait = TimeSpan.FromSeconds(2);

    /// <summary>Each typed value, the editor caption it must open, and the check-name slug.</summary>
    private static readonly (string Name, string Caption, string Slug)[] TypedValues =
    [
        (StringName, EditStringCaption, "sz"),
        (ExpandName, EditExpandCaption, "expand-sz"),
        (MultiName, EditMultiCaption, "multi-sz"),
        (DwordName, EditDwordCaption, "dword"),
        (QwordName, EditQwordCaption, "qword"),
        (BinaryName, EditBinaryCaption, "binary"),
    ];

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Basics")]
    [TestCategory("Mutating")]
    public void Basics_EnterAndDoubleClickOpenTheEditorForEachType()
    {
        RunScenario("basics-activate", childKey: null, CheckActivation);
    }

    [TestMethod]
    [TestCategory("Basics")]
    [TestCategory("Mutating")]
    public void Basics_EditMenuModifyItemsFollowTheValueSelection()
    {
        RunScenario("basics-editmenu", childKey: null, CheckEditMenu);
    }

    [TestMethod]
    [TestCategory("Basics")]
    [TestCategory("Mutating")]
    public void Basics_CopyKeyNameAndF5KeepTheView()
    {
        RunScenario("basics-refresh", childKey: null, CheckCopyKeyNameAndRefresh);
    }

    [TestMethod]
    [TestCategory("Basics")]
    [TestCategory("Mutating")]
    public void Basics_DeleteWithTreeFocusDeletesTheKey()
    {
        RunScenario("basics-delete", childKey: null, CheckDeleteRouting);
    }

    [TestMethod]
    [TestCategory("Basics")]
    [TestCategory("Mutating")]
    public void Basics_MultiDeleteTextAndDefaultRow()
    {
        RunScenario("basics-multi", childKey: null, CheckMultiDeleteAndDefaultRow);
    }

    /// <summary>The app, the driver, and the sandbox for one scenario. Built by <see cref="RunScenario"/>.</summary>
    private sealed record Scenario(AppSession Session, EditingDriver Driver, SandboxRegistry Sandbox, E2EConfig Config);

    /// <summary>
    /// Seeds the sandbox, launches the app with the sandbox root, navigates to the run key (or the child key
    /// when given), runs the scenario, and removes the run key and the scoped data root.
    /// </summary>
    private static void RunScenario(string scope, string? childKey, Action<Scenario> body)
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
            E2EResults.Record("basics.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        string? dataRoot = null;

        try
        {
            sandbox.Cleanup();
            Seed(sandbox);
            dataRoot = CreateFreshDataRoot(config, scope);

            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config, childKey);
            E2EResults.Record(
                "basics.navigate",
                navigated,
                $"navigated to '{sandbox.RunRootDisplayPath}' ({childKey ?? "run key"})={navigated}.");
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "basics.sandbox.navigate");

            body(new Scenario(session, driver, sandbox, config));
            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            DeleteDataRoot(dataRoot);
        }
    }

    private static void CheckActivation(Scenario scenario)
    {
        foreach ((string name, string caption, string slug) in TypedValues)
        {
            CheckEditorOpens(scenario, name, caption, "basics.enter." + slug, doubleClick: false);
            CheckEditorOpens(scenario, name, caption, "basics.doubleclick." + slug, doubleClick: true);
        }
    }

    /// <summary>
    /// Selects one value, activates it with Enter or a double-click, and checks the editor caption. The
    /// dialog is cancelled afterwards.
    /// </summary>
    private static void CheckEditorOpens(Scenario scenario, string name, string caption, string checkId, bool doubleClick)
    {
        SelectListRow(scenario.Session, name);
        E2EGuards.RequireListRowSelected(scenario.Session, name, checkId + ".row");
        E2EGuards.RequireSandboxSelection(scenario.Session, scenario.Sandbox, checkId + ".sandbox");

        bool activated = doubleClick
            ? DoubleClick(FindListRow(scenario.Session, name))
            : SendKeys(scenario.Session, "ENTER");
        bool dialog = activated && scenario.Driver.WaitForDialog();
        string actual = dialog ? DialogCaption(scenario.Driver) : string.Empty;
        bool cancelled = dialog && CancelDialog(scenario);

        // The hex editor title adds the value name after the classic caption, so the caption is a prefix.
        E2EResults.Record(
            checkId,
            activated && dialog && actual.StartsWith(caption, StringComparison.Ordinal) && cancelled,
            $"activated={activated}, dialog={dialog}, caption='{actual}', expected '{caption}', cancelled={cancelled}.");
    }

    private static void CheckEditMenu(Scenario scenario)
    {
        // No value selected: the child key is selected and the list has no selection.
        _ = ControlTestSupport.NavigateToSandboxKey(scenario.Session, scenario.Config, ChildKey);
        E2EGuards.RequireSandboxSelection(scenario.Session, scenario.Sandbox, "basics.editmenu.sandbox.key");

        scenario.Driver.OpenMenu(MenuEdit);
        bool hiddenModify = scenario.Driver.WaitForElement(ModifyCommandId(MenuModify), ShortWait) is null;
        bool hiddenBinary = scenario.Driver.WaitForElement(ModifyCommandId(MenuModifyBinary), ShortWait) is null;
        E2EResults.Record(
            "basics.editmenu.hidden-without-value",
            hiddenModify && hiddenBinary,
            $"Modify hidden={hiddenModify}, Modify Binary Data hidden={hiddenBinary} with no value selected.");
        _ = SendKeys(scenario.Session, "ESCAPE");

        // A value is selected: both items appear at the top of Edit, and both are enabled for a real value.
        _ = ControlTestSupport.NavigateToSandboxKey(scenario.Session, scenario.Config);
        E2EGuards.RequireSandboxSelection(scenario.Session, scenario.Sandbox, "basics.editmenu.sandbox.root");
        SelectListRow(scenario.Session, StringName);
        E2EGuards.RequireListRowSelected(scenario.Session, StringName, "basics.editmenu.row");

        scenario.Driver.OpenMenu(MenuEdit);
        AutomationElement? modify = scenario.Driver.WaitForElement(ModifyCommandId(MenuModify), ControlTestSupport.UiTimeout);
        AutomationElement? modifyBinary = scenario.Driver.WaitForElement(ModifyCommandId(MenuModifyBinary), ControlTestSupport.UiTimeout);
        bool shown = modify is not null && modifyBinary is not null;
        bool enabled = shown && IsEnabled(modify) && IsEnabled(modifyBinary);
        bool named = shown &&
            ControlTestSupport.SafeName(modify) == "Modify..." &&
            ControlTestSupport.SafeName(modifyBinary) == "Modify Binary Data...";
        E2EResults.Record(
            "basics.editmenu.shown-with-value",
            shown && enabled && named,
            $"shown={shown}, enabled={enabled}, named={named}, names=[{ControlTestSupport.SafeName(modify)}, {ControlTestSupport.SafeName(modifyBinary)}].");

        // Edit > Modify on the selected string opens Edit String, the same as Enter.
        bool modifyInvoked = InvokeElement(modify);
        bool stringDialog = modifyInvoked && scenario.Driver.WaitForDialog();
        string stringCaption = stringDialog ? DialogCaption(scenario.Driver) : string.Empty;
        bool stringCancelled = stringDialog && CancelDialog(scenario);
        E2EResults.Record(
            "basics.editmenu.modify-opens-string",
            modifyInvoked && stringDialog && stringCaption == EditStringCaption && stringCancelled,
            $"invoked={modifyInvoked}, dialog={stringDialog}, caption='{stringCaption}', cancelled={stringCancelled}.");

        // Edit > Modify Binary Data on the selected binary value opens the hex editor.
        SelectListRow(scenario.Session, BinaryName);
        E2EGuards.RequireListRowSelected(scenario.Session, BinaryName, "basics.editmenu.binary.row");
        scenario.Driver.OpenMenu(MenuEdit);
        AutomationElement? binaryItem = scenario.Driver.WaitForElement(ModifyCommandId(MenuModifyBinary), ControlTestSupport.UiTimeout);
        bool binaryInvoked = InvokeElement(binaryItem);
        bool binaryDialog = binaryInvoked && scenario.Driver.WaitForDialog();
        string binaryCaption = binaryDialog ? DialogCaption(scenario.Driver) : string.Empty;
        bool binaryCancelled = binaryDialog && CancelDialog(scenario);
        E2EResults.Record(
            "basics.editmenu.modify-binary-opens-hex",
            binaryInvoked && binaryDialog && binaryCaption.StartsWith(EditBinaryCaption, StringComparison.Ordinal) && binaryCancelled,
            $"invoked={binaryInvoked}, dialog={binaryDialog}, caption='{binaryCaption}', cancelled={binaryCancelled}.");
    }

    private static void CheckCopyKeyNameAndRefresh(Scenario scenario)
    {
        _ = ControlTestSupport.NavigateToSandboxKey(scenario.Session, scenario.Config, ChildKey);
        E2EGuards.RequireSandboxSelection(scenario.Session, scenario.Sandbox, "basics.refresh.sandbox.key");

        // The child key is expanded, so its own child is visible before the refresh.
        bool expanded = ControlTestSupport.Expand(FindTreeRow(scenario.Session, ChildKey)) &&
            ControlTestSupport.WaitUntil(
                () => ControlTestSupport.StateOf(FindTreeRow(scenario.Session, ChildKey)) == "Expanded",
                ControlTestSupport.ExpandTimeout);
        bool grandVisible = ControlTestSupport.WaitUntil(
            () => FindTreeRow(scenario.Session, GrandKey) is not null,
            ControlTestSupport.ExpandTimeout);
        E2EResults.Record(
            "basics.refresh.expanded-before",
            expanded && grandVisible,
            $"expanded={expanded}, grandchild visible={grandVisible}.");

        // Edit > Copy Key Name puts the classic display path on the clipboard.
        ClipboardProbe.Clear();
        scenario.Driver.OpenMenu(MenuEdit, MenuCopyKeyName);
        string expected = "Computer\\HKEY_CURRENT_USER\\" + scenario.Sandbox.RunRootPath + "\\" + ChildKey;
        ClipboardProbe.Reading reading = ClipboardProbe.WaitForText(expected, TimeSpan.FromSeconds(5));
        bool copied = reading.State == ClipboardProbe.ClipboardState.Text &&
            ClipboardProbe.Normalize(reading.Text) == expected;
        E2EResults.Record(
            "basics.copykeyname.menu",
            copied,
            $"expected '{expected}', clipboard state={reading.State}, text='{ClipboardProbe.Prefix(reading.Text)}'.");

        // F5 re-reads the tree and the list. The selected value stays selected, a value added behind the app
        // appears, and the expansion and the tree selection stay as they were.
        SelectListRow(scenario.Session, InsideValue);
        E2EGuards.RequireListRowSelected(scenario.Session, InsideValue, "basics.refresh.row");
        E2EGuards.RequireSandboxSelection(scenario.Session, scenario.Sandbox, "basics.refresh.sandbox.value");
        scenario.Sandbox.SetString(ChildKey, AddedValue, "added behind the app");

        bool sent = SendKeys(scenario.Session, "F5");
        bool listed = sent && ControlTestSupport.WaitUntil(
            () => ControlTestSupport.ListRowNames(scenario.Session).Contains(AddedValue, StringComparer.OrdinalIgnoreCase),
            ControlTestSupport.UiTimeout);
        E2EResults.Record(
            "basics.refresh.added-value",
            sent && listed,
            $"sent={sent}, listed={listed}, rows=[{string.Join(",", ControlTestSupport.ListRowNames(scenario.Session))}].");

        bool expandedKept = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.StateOf(FindTreeRow(scenario.Session, ChildKey)) == "Expanded" &&
                FindTreeRow(scenario.Session, GrandKey) is not null,
            ControlTestSupport.ExpandTimeout);
        bool selectedKept = ControlTestSupport.WaitForTreeSelection(scenario.Session, ChildKey, ControlTestSupport.ExpandTimeout);
        bool rowKept = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.IsSelected(FindListRow(scenario.Session, InsideValue)),
            ControlTestSupport.UiTimeout);
        E2EResults.Record(
            "basics.refresh.expansion",
            expandedKept,
            $"child expanded and grandchild visible after F5={expandedKept}.");
        E2EResults.Record(
            "basics.refresh.selection",
            selectedKept && rowKept,
            $"tree key selected={selectedKept}, value '{InsideValue}' selected={rowKept}.");
    }

    private static void CheckDeleteRouting(Scenario scenario)
    {
        _ = ControlTestSupport.NavigateToSandboxKey(scenario.Session, scenario.Config, DoomedKey);
        E2EGuards.RequireSandboxSelection(scenario.Session, scenario.Sandbox, "basics.delete.sandbox.key");

        // A value of the key is selected in the list. The tree is then focused by a click on its selected key,
        // which keeps the list selection. Delete must act on the tree, so it asks for the key, not the value.
        SelectListRow(scenario.Session, KeptValue);
        E2EGuards.RequireListRowSelected(scenario.Session, KeptValue, "basics.delete.row");
        bool treeClicked = Click(FindTreeRow(scenario.Session, DoomedKey));
        E2EGuards.RequireSandboxSelection(scenario.Session, scenario.Sandbox, "basics.delete.sandbox.value");

        bool sent = SendKeys(scenario.Session, "DELETE");
        bool dialog = sent && scenario.Driver.WaitForDialog();
        string caption = dialog ? DialogCaption(scenario.Driver) : string.Empty;
        bool keyConfirm = dialog && caption == ConfirmKeyCaption;
        E2EResults.Record(
            "basics.delete.tree-focus.caption",
            treeClicked && sent && keyConfirm,
            $"tree clicked={treeClicked}, sent={sent}, dialog={dialog}, caption='{caption}', expected '{ConfirmKeyCaption}'.");

        if (!dialog)
        {
            return;
        }

        if (!keyConfirm)
        {
            // Not the key confirmation: cancel, so no value is deleted by the check.
            _ = CancelDialog(scenario);
            return;
        }

        bool confirmed = scenario.Driver.Invoke("DialogPrimary") && scenario.Driver.WaitForNoDialog();
        bool gone = ControlTestSupport.WaitUntil(
            () => !scenario.Sandbox.Exists(DoomedKey),
            ControlTestSupport.UiTimeout);
        E2EResults.Record(
            "basics.delete.tree-focus.key-gone",
            confirmed && gone,
            $"confirmed={confirmed}, key removed={gone}.");
    }

    private static void CheckMultiDeleteAndDefaultRow(Scenario scenario)
    {
        // Two values of the run key are selected: a string and a DWORD.
        SelectListRow(scenario.Session, StringName);
        bool added = AddToSelection(FindListRow(scenario.Session, DwordName));
        bool bothSelected = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.IsSelected(FindListRow(scenario.Session, StringName)) &&
                ControlTestSupport.IsSelected(FindListRow(scenario.Session, DwordName)),
            TimeSpan.FromSeconds(5));
        E2EGuards.RequireSandboxSelection(scenario.Session, scenario.Sandbox, "basics.multi.sandbox");

        bool sent = SendKeys(scenario.Session, "DELETE");
        bool dialog = sent && scenario.Driver.WaitForDialog();
        string caption = dialog ? DialogCaption(scenario.Driver) : string.Empty;
        string text = dialog ? scenario.Driver.TextOf("DialogMessage") : string.Empty;
        bool classicText = dialog && caption == ConfirmValueCaption && text == DeleteValuesText;
        E2EResults.Record(
            "basics.delete.multi.text",
            added && bothSelected && sent && classicText,
            $"added={added}, bothSelected={bothSelected}, sent={sent}, caption='{caption}', text matches string 50={text == DeleteValuesText}.");

        bool cancelled = dialog && CancelDialog(scenario);
        bool kept = scenario.Sandbox.ReadString(string.Empty, StringName) == StringText &&
            scenario.Sandbox.ReadDword(string.Empty, DwordName) == DwordData;
        E2EResults.Record(
            "basics.delete.multi.cancelled",
            cancelled && kept,
            $"cancelled={cancelled}, both values kept={kept}.");

        // A key with no default value still lists (Default) first, with the classic data text.
        _ = ControlTestSupport.NavigateToSandboxKey(scenario.Session, scenario.Config, NoDefaultKey);
        E2EGuards.RequireSandboxSelection(scenario.Session, scenario.Sandbox, "basics.default.sandbox");
        bool first = ControlTestSupport.WaitUntil(
            () =>
            {
                string[] names = ControlTestSupport.ListRowNames(scenario.Session);
                return names.Length > 0 && names[0] == DefaultLabel;
            },
            ControlTestSupport.UiTimeout);
        string[] rows = ControlTestSupport.ListRowNames(scenario.Session);
        E2EResults.Record(
            "basics.default.first",
            first,
            $"rows=[{string.Join(",", rows)}], expected '{DefaultLabel}' first.");

        IReadOnlyList<string> texts = TextsUnder(FindListRow(scenario.Session, DefaultLabel));
        bool notSet = texts.Contains(ValueNotSetText, StringComparer.Ordinal);
        E2EResults.Record(
            "basics.default.value-not-set",
            notSet,
            $"(Default) row texts=[{string.Join(", ", texts)}], expected '{ValueNotSetText}'.");
    }

    /// <summary>
    /// Seeds one value of each type in the run key, with no default value, so the run key also shows the
    /// synthetic (Default) row. Keys and values go through the harness helpers only.
    /// </summary>
    private static void Seed(SandboxRegistry sandbox)
    {
        sandbox.CreateKey(string.Empty);
        sandbox.SetString(string.Empty, StringName, StringText);
        sandbox.SetValueOfKind(string.Empty, ExpandName, "%SystemRoot%", RegistryValueKind.ExpandString);
        sandbox.SetValueOfKind(string.Empty, MultiName, new[] { "first", "second" }, RegistryValueKind.MultiString);
        sandbox.SetDword(string.Empty, DwordName, DwordData);
        sandbox.SetValueOfKind(string.Empty, QwordName, 81985529216486895L, RegistryValueKind.QWord);
        sandbox.SetBinary(string.Empty, BinaryName, new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 });

        sandbox.CreateKey(ChildKey);
        sandbox.SetString(ChildKey, InsideValue, "child");
        sandbox.CreateKey(ChildKey + @"\" + GrandKey);
        sandbox.SetString(ChildKey + @"\" + GrandKey, "Leaf", "grand");

        sandbox.CreateKey(DoomedKey);
        sandbox.SetString(DoomedKey, KeptValue, "kept");

        sandbox.CreateKey(NoDefaultKey);
        sandbox.SetString(NoDefaultKey, OnlyValue, "only");
    }

    private static string ModifyCommandId(int commandId) => "MenuCommand_" + commandId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static AutomationElement? FindListRow(AppSession session, string name) =>
        ControlTestSupport.FindRowByName(
            session,
            ControlTestSupport.ListControl,
            ControlType.DataItem,
            name);

    private static AutomationElement? FindTreeRow(AppSession session, string name) =>
        ControlTestSupport.FindRowByName(
            session,
            ControlTestSupport.TreeControl,
            ControlType.TreeItem,
            name);

    /// <summary>Selects a list row and waits until it reports selected. Selection gives the list keyboard focus.</summary>
    private static void SelectListRow(AppSession session, string name)
    {
        _ = ControlTestSupport.EnsureForeground(session);

        // The list fills after a navigation, so the row is waited for before it is selected.
        _ = ControlTestSupport.WaitUntil(() => FindListRow(session, name) is not null, ControlTestSupport.UiTimeout);
        AutomationElement? row = FindListRow(session, name);
        ControlTestSupport.Select(row);
        _ = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.IsSelected(FindListRow(session, name)),
            TimeSpan.FromSeconds(5));
    }

    private static bool AddToSelection(AutomationElement? row)
    {
        if (row is null)
        {
            return false;
        }

        try
        {
            if (!row.Patterns.SelectionItem.IsSupported)
            {
                return false;
            }

            row.Patterns.SelectionItem.Pattern.AddToSelection();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool DoubleClick(AutomationElement? row)
    {
        if (row is null)
        {
            return false;
        }

        try
        {
            row.DoubleClick();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool Click(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            element.Click();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool InvokeElement(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            if (element.Patterns.Invoke.IsSupported)
            {
                element.Patterns.Invoke.Pattern.Invoke();
                return true;
            }

            element.Click();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsEnabled(AutomationElement? element)
    {
        try
        {
            return element is not null && element.IsEnabled;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Clicks Cancel on the open dialog. Escape is the fallback when the button is not found.</summary>
    private static bool CancelDialog(Scenario scenario)
    {
        if (scenario.Driver.Invoke("DialogSecondary") && scenario.Driver.WaitForNoDialog())
        {
            return true;
        }

        _ = SendKeys(scenario.Session, "ESCAPE");
        return scenario.Driver.WaitForNoDialog();
    }

    /// <summary>
    /// The window caption of the open dialog. The dialog root has no handle, so the Window ancestor gives the
    /// caption, as the classic dialog checks do.
    /// </summary>
    private static string DialogCaption(EditingDriver driver)
    {
        AutomationElement? current = driver.WaitForElement("DialogRoot", TimeSpan.FromSeconds(2));
        for (int depth = 0; current is not null && depth < 32; depth++)
        {
            try
            {
                if (current.ControlType == ControlType.Window)
                {
                    return current.Name ?? string.Empty;
                }

                current = current.Parent;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        return string.Empty;
    }

    private static IReadOnlyList<string> TextsUnder(AutomationElement? row)
    {
        var texts = new List<string>();
        if (row is null)
        {
            return texts;
        }

        foreach (AutomationElement element in row.FindAllDescendants())
        {
            string name = ControlTestSupport.SafeName(element);
            if (name.Length > 0)
            {
                texts.Add(name);
            }
        }

        return texts;
    }

    private static bool SendKeys(AppSession session, string key)
    {
        try
        {
            ControlTestSupport.SendKeys(session, key);
            return true;
        }
        catch (Exception ex)
        {
            E2EResults.Record("basics.keys", false, $"'{key}' failed: {ex.Message}");
            return false;
        }
    }

    private static void FailIfNeeded()
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Basics checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
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
}

using Bwc.E2E.Controls;
using Bwc.E2E.Dialogs;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using Bwc.E2E.ImportExport;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Accessibility;

/// <summary>
/// UI Automation checks for the classic dialogs: Edit String, Edit DWORD, Edit Multi-String, Edit Binary Value, Find,
/// Add to Favorites, and Remove Favorites (the ids are the template ids in docs/parity/regedit-inventory.md). Every
/// control that is an edit, button, check, radio, combo, list, or table has a non-empty Name. The Tab sequence from
/// the initial focus follows the template order. The Export range panel (a native common file dialog) is checked for
/// the names of its radios and its branch edit. Failure modes: docs/failure-modes/accessibility.md section 4.
/// </summary>
[TestClass]
public sealed class DialogAccessibilityTests
{
    private const string StringName = "AccString";
    private const string DwordName = "AccDword";
    private const string MultiName = "AccMulti";
    private const string BinaryName = "AccBinary";
    private const string FavoriteName = "A11yFavorite";
    private const int MenuFavorites = 517;
    private const int MenuFavoritesAdd = 1281;
    private const int MenuFavoritesRemove = 1280;
    private const int MenuEdit = 513;
    private const int MenuEditFind = 662;
    private const string DialogRootId = "DialogRoot";

    private static readonly string[] EditStringTabs = ["ValueEditorInput", "DialogPrimary", "DialogSecondary", "ValueNameInput"];
    private static readonly string[] EditDwordTabs = ["ValueEditorInput", "BaseHex", "DialogPrimary", "DialogSecondary", "ValueNameInput"];
    private static readonly string[] EditBinaryTabs = ["HexEditor", "DialogPrimary", "DialogSecondary", "ValueNameInput"];
    private static readonly string[] FindTabs =
    [
        "FindText",
        "FindKeys",
        "FindValues",
        "FindData",
        "FindWholeString",
        "FindSubtree",
        "DialogPrimary",
        "DialogSecondary",
    ];

    private static readonly string[] AddFavoriteTabs = ["DialogTextInput", "DialogPrimary", "DialogSecondary"];
    private static readonly string[] RemoveFavoritesTabs = ["DialogPrimary", "DialogSecondary", "FavoritesList"];

    /// <summary>The control types that must have a Name. Statics and group frames are exempt by design.</summary>
    private static readonly HashSet<string> NamedTypeNames = new(StringComparer.Ordinal)
    {
        "Edit", "Button", "CheckBox", "RadioButton", "ComboBox", "List", "Table", "DataItem",
    };

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Accessibility")]
    [TestCategory("Dialogs")]
    [TestCategory("Mutating")]
    public void ClassicDialogs_NamedControlsAndTemplateTabOrder()
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
            E2EResults.Record("a11y.dialogs.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        SandboxRegistry? sandbox = null;
        try
        {
            sandbox = ControlTestSupport.SeedRegistry(config, 0);
            sandbox.SetString(string.Empty, StringName, "text");
            sandbox.SetDword(string.Empty, DwordName, 42);
            sandbox.SetValueOfKind(string.Empty, MultiName, new[] { "one", "two" }, RegistryValueKind.MultiString);
            sandbox.SetBinary(string.Empty, BinaryName, Enumerable.Range(0, 0x30).Select(index => (byte)(0x41 + index)).ToArray());

            // Favorites are written under the sandbox only, as the favorites checks do.
            var favorites = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["BWC_E2E_FAVORITES_ROOT"] = sandbox.RunRootDisplayPath + @"\Favorites",
            };
            using AppSession session = AccessibilitySupport.Launch(config, sandbox, "a11y-dialogs", favorites);
            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "a11y.dialogs.navigate");
            using var driver = new EditingDriver(session);
            using var automation = new UIA3Automation();

            CheckValueEditor(session, driver, automation, sandbox, StringName, "a11y.edit-string", EditStringTabs);
            CheckValueEditor(session, driver, automation, sandbox, DwordName, "a11y.edit-dword", EditDwordTabs);
            CheckValueEditor(session, driver, automation, sandbox, MultiName, "a11y.edit-multi", EditStringTabs);
            CheckValueEditor(session, driver, automation, sandbox, BinaryName, "a11y.edit-binary", EditBinaryTabs);
            CheckExportRange(session, driver, automation);
            CheckFind(session, driver, automation);
            CheckAddFavorite(session, driver, automation);
            CheckRemoveFavorites(session, driver, automation);
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }

        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(0, failures.Count, $"Dialog accessibility checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private static void CheckValueEditor(
        AppSession session,
        EditingDriver driver,
        UIA3Automation automation,
        SandboxRegistry sandbox,
        string valueName,
        string checkId,
        string[] tabs)
    {
        AutomationElement? row = ClassicDialogTests.OpenValueRow(session, sandbox, valueName, checkId);
        bool opened = row is not null && ClassicDialogTests.OpenEditor(session, driver, row, checkId + ".open");
        if (!opened)
        {
            E2EResults.Record(checkId + ".open", false, $"the editor for '{valueName}' did not open.");
            return;
        }

        CheckDialog(checkId, driver, automation, tabs);
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        E2EResults.Record(checkId + ".closed", driver.WaitForNoDialog(), "Escape closed the editor.");
    }

    private static void CheckFind(AppSession session, EditingDriver driver, UIA3Automation automation)
    {
        _ = ControlTestSupport.CloseOpenMenus(session);
        driver.OpenMenu(MenuEdit, MenuEditFind);
        bool opened = driver.WaitForDialog();
        E2EResults.Record("a11y.find.open", opened, "Edit > Find opened the Find dialog.");
        if (!opened)
        {
            return;
        }

        // Find Next is disabled while the search text is empty, so Tab skips it. The text is typed first.
        _ = driver.SetText("FindText", "a11y");
        CheckDialog("a11y.find", driver, automation, FindTabs);
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        E2EResults.Record("a11y.find.closed", driver.WaitForNoDialog(), "Escape closed Find.");
    }

    private static void CheckAddFavorite(AppSession session, EditingDriver driver, UIA3Automation automation)
    {
        _ = ControlTestSupport.CloseOpenMenus(session);
        driver.OpenMenu(MenuFavorites, MenuFavoritesAdd);
        bool opened = driver.WaitForDialog();
        E2EResults.Record("a11y.add-favorite.open", opened, "Favorites > Add opened Add to Favorites.");
        if (!opened)
        {
            return;
        }

        CheckDialog("a11y.add-favorite", driver, automation, AddFavoriteTabs);
        _ = driver.SetText("DialogTextInput", FavoriteName);
        bool accepted = driver.Invoke("DialogPrimary") && driver.WaitForNoDialog(TimeSpan.FromSeconds(10));
        E2EResults.Record("a11y.add-favorite.saved", accepted, "OK saved the favorite so Remove Favorites has an entry.");
    }

    private static void CheckRemoveFavorites(AppSession session, EditingDriver driver, UIA3Automation automation)
    {
        _ = ControlTestSupport.CloseOpenMenus(session);
        driver.OpenMenu(MenuFavorites, MenuFavoritesRemove);
        bool opened = driver.WaitForDialog();
        E2EResults.Record("a11y.remove-favorites.open", opened, "Favorites > Remove opened Remove Favorites.");
        if (!opened)
        {
            return;
        }

        CheckDialog("a11y.remove-favorites", driver, automation, RemoveFavoritesTabs);
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        E2EResults.Record("a11y.remove-favorites.closed", driver.WaitForNoDialog(), "Escape closed Remove Favorites.");
    }

    /// <summary>
    /// The Export range panel is part of the native Export file dialog. Its radios and branch edit must have names.
    /// </summary>
    private static void CheckExportRange(AppSession session, EditingDriver driver, UIA3Automation automation)
    {
        using var dialogs = new SystemFileDialogDriver(session);
        _ = ControlTestSupport.CloseOpenMenus(session);
        bool opened = ExportRangeTests.OpenDialog(driver, dialogs, ExportRangeTests.MenuFileExport, "a11y.export", out IntPtr dialog);
        if (!opened)
        {
            return;
        }

        // The shell builds the Export range panel after the dialog appears, and UI Automation reports it when the provider
        // has populated. The panel is read only once the radios and the named branch edit are present.
        bool panelReady = ControlTestSupport.WaitUntil(() => ExportPanelReady(automation, dialog), ExportPanelTimeout);
        E2EResults.Record(
            "a11y.export.panel-ready",
            panelReady,
            panelReady ? "the Export range panel and its named branch edit are present."
                : "the Export range panel did not show its radios and named branch edit in time.");

        AutomationElement root = automation.FromHandle(dialog);
        AutomationElement[] controls = root.FindAllDescendants()
            .Where(element => AccessibilitySupport.ControlTypeOf(element) is "Edit" or "RadioButton")
            .ToArray();
        var unnamed = controls.Where(element => AccessibilitySupport.NameOf(element).Length == 0)
            .Select(AccessibilitySupport.ControlTypeOf)
            .ToList();
        bool branchEditNamed = controls.Any(element =>
            AccessibilitySupport.ControlTypeOf(element) == "Edit" && AccessibilitySupport.NameOf(element) == BranchEditName);
        bool radiosNamed = dialogs.FindRadio(dialog, "All") is not null && dialogs.FindRadio(dialog, "Selected branch") is not null;
        E2EResults.Record(
            "a11y.export.names",
            unnamed.Count == 0 && controls.Length > 0 && radiosNamed && branchEditNamed,
            $"export range controls={controls.Length}, radios named={radiosNamed}, branch edit named '{BranchEditName}'={branchEditNamed}, " +
            $"unnamed={string.Join(",", unnamed)}.");

        // The shell keeps the range radios as one tab stop, so the Tab cycle must reach the branch edit right after the
        // Selected branch radio. The branch edit is named by Dynamic Annotation, so its name is part of this check.
        _ = ClassicDialogTests.EnsureForeground(dialog);
        var cycle = new List<string>();
        for (int press = 0; press < ExportTabPresses; press++)
        {
            Keyboard.Type(VirtualKeyShort.TAB);
            Thread.Sleep(150);
            cycle.Add(FocusedDescription(automation));
        }

        string radioText = "RadioButton:" + BranchEditName;
        int radio = cycle.FindIndex(entry => string.Equals(entry, radioText, StringComparison.Ordinal));
        bool ordered = radio >= 0 && radio + 1 < cycle.Count && cycle[radio + 1] == "Edit:" + BranchEditName;
        E2EResults.Record(
            "a11y.export.tab-order",
            ordered,
            $"Tab cycle over {ExportTabPresses} presses: [{string.Join(", ", cycle.Distinct(StringComparer.Ordinal))}]; " +
            $"branch edit directly after the Selected branch radio={ordered}.");
        CloseExportDialog(automation, dialog);
    }

    /// <summary>
    /// Closes the Export dialog through its Cancel button. The Tab cycle can leave the focus on a control that consumes
    /// Escape (the search box), so Escape is only the fallback. The dialog must be gone before the next check starts.
    /// </summary>
    private static void CloseExportDialog(UIA3Automation automation, IntPtr dialog)
    {
        try
        {
            AutomationElement? cancel = automation.FromHandle(dialog)
                .FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Cancel")));
            if (cancel is not null)
            {
                cancel.Patterns.Invoke.Pattern.Invoke();
            }
        }
        catch (Exception)
        {
            // The dialog may already be closing. The wait below reports the outcome.
        }

        bool closed = ControlTestSupport.WaitUntil(() => !IsWindow(dialog), TimeSpan.FromSeconds(10));
        if (!closed)
        {
            Keyboard.Type(VirtualKeyShort.ESCAPE);
            closed = ControlTestSupport.WaitUntil(() => !IsWindow(dialog), TimeSpan.FromSeconds(5));
        }

        E2EResults.Record("a11y.export.closed", closed, "the Export dialog closed before the next check.");
    }

#pragma warning disable SYSLIB1054 // The small user32 surface does not justify LibraryImport plumbing.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);
#pragma warning restore SYSLIB1054

    private const string BranchEditName = "Selected branch";
    private const int ExportTabPresses = 20;
    private static readonly TimeSpan ExportPanelTimeout = TimeSpan.FromSeconds(20);

    /// <summary>True when the Export dialog shows the Selected branch radio and the branch edit has its name.</summary>
    private static bool ExportPanelReady(UIA3Automation automation, IntPtr dialog)
    {
        AutomationElement[] descendants = automation.FromHandle(dialog).FindAllDescendants();
        bool radio = descendants.Any(element =>
            AccessibilitySupport.ControlTypeOf(element) == "RadioButton" && AccessibilitySupport.NameOf(element) == BranchEditName);
        bool edit = descendants.Any(element =>
            AccessibilitySupport.ControlTypeOf(element) == "Edit" && AccessibilitySupport.NameOf(element) == BranchEditName);
        return radio && edit;
    }

    /// <summary>The control type and name of the focused element, or an empty text when it cannot be read.</summary>
    private static string FocusedDescription(UIA3Automation automation)
    {
        try
        {
            AutomationElement? focused = automation.FocusedElement();
            return focused is null ? string.Empty : AccessibilitySupport.ControlTypeOf(focused) + ":" + AccessibilitySupport.NameOf(focused);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// The automation id of the focused control. A list gives the focus to one of its items, which has no id, so the id
    /// comes from the nearest ancestor that has one, and stops at the dialog root.
    /// </summary>
    private static string FocusedControlId(UIA3Automation automation)
    {
        try
        {
            AutomationElement? element = automation.FocusedElement();
            for (int depth = 0; element is not null && depth < 6; depth++)
            {
                string id = AccessibilitySupport.IdOf(element);
                if (id == DialogRootId)
                {
                    return string.Empty;
                }

                if (id.Length > 0)
                {
                    return id;
                }

                element = element.Parent;
            }
        }
        catch (Exception)
        {
            return string.Empty;
        }

        return string.Empty;
    }

    /// <summary>
    /// Every named-type control inside the dialog has a non-empty Name, and the Tab sequence from the initial focus
    /// follows <paramref name="templateTabs"/> (the template order, starting after the initial focus).
    /// </summary>
    private static void CheckDialog(string checkId, EditingDriver driver, UIA3Automation automation, string[] templateTabs)
    {
        AutomationElement? root = driver.WaitForElement(DialogRootId, AccessibilitySupport.UiTimeout);
        if (root is null)
        {
            E2EResults.Record(checkId + ".root", false, "the dialog root was not found.");
            return;
        }

        AutomationElement[] controls = root.FindAllDescendants()
            .Where(element => NamedTypeNames.Contains(AccessibilitySupport.ControlTypeOf(element)))
            .ToArray();
        var unnamed = controls
            .Where(element => AccessibilitySupport.NameOf(element).Length == 0)
            .Select(element => AccessibilitySupport.IdOf(element) is { Length: > 0 } id ? id : AccessibilitySupport.ControlTypeOf(element))
            .ToList();
        E2EResults.Record(
            checkId + ".names",
            unnamed.Count == 0,
            unnamed.Count == 0
                ? $"all {controls.Length} named-type controls have a Name."
                : $"controls without a Name: {string.Join(", ", unnamed)}.");

        CheckTabOrder(checkId, automation, templateTabs);
    }

    private static void CheckTabOrder(string checkId, UIA3Automation automation, string[] templateTabs)
    {
        // The dialog may open with the focus on a control that is not a template tab stop (a list with no focus
        // ring, for example). Tab moves the focus onto the template order first, so the cycle starts there.
        for (int press = 0; press < templateTabs.Length && Array.IndexOf(templateTabs, FocusedControlId(automation)) < 0; press++)
        {
            Keyboard.Type(VirtualKeyShort.TAB);
            _ = ControlTestSupport.WaitUntil(() => FocusedControlId(automation).Length > 0, TimeSpan.FromSeconds(1));
        }

        string start = FocusedControlId(automation);
        int index = Array.IndexOf(templateTabs, start);
        if (index < 0)
        {
            E2EResults.Record(checkId + ".tab-start", false, $"initial focus '{start}' is not a template tab stop.");
            return;
        }

        int count = templateTabs.Length;
        var expected = new string[count];
        for (int step = 0; step < count; step++)
        {
            expected[step] = templateTabs[(index + 1 + step) % count];
        }

        var observed = new string[count];
        for (int step = 0; step < count; step++)
        {
            string before = FocusedControlId(automation);
            Keyboard.Type(VirtualKeyShort.TAB);
            _ = ControlTestSupport.WaitUntil(() => FocusedControlId(automation) != before, TimeSpan.FromSeconds(1));
            observed[step] = FocusedControlId(automation);
        }

        bool same = observed.SequenceEqual(expected, StringComparer.Ordinal);
        E2EResults.Record(
            checkId + ".tab-order",
            same,
            $"Tab sequence from '{start}': observed [{string.Join(",", observed)}], expected [{string.Join(",", expected)}].");
    }
}

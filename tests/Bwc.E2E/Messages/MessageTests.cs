using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Messages;

/// <summary>
/// Checks the classic message boxes the Registry Editor shows, against a seeded HKCU sandbox. Each
/// check reads the caption, the text, the buttons, and the default button of a real dialog, then
/// presses a button that leaves the sandbox as it was. The texts are the classic strings from the
/// oracle, so a change to the resource file that drifts from the classic text fails here.
/// </summary>
[TestClass]
public sealed class MessageTests
{
    private const int MenuEdit = 513;
    private const int MenuEditNew = 516;
    private const int MenuEditNewKey = 663;
    private const int MenuEditDelete = 913;
    private const int MenuEditRename = 914;

    private const string CaptionConfirmKeyDelete = "Confirm Key Delete";
    private const string CaptionConfirmValueDelete = "Confirm Value Delete";
    private const string CaptionRenameValueError = "Error Renaming Value";
    private const string CaptionRenameKeyError = "Error Renaming Key";

    private const string TextConfirmKeyDelete =
        "Are you sure you want to permanently delete this key and all of its subkeys?";
    private const string TextConfirmValuesDelete =
        "Deleting certain registry values could cause system instability. Are you sure you want to permanently delete these values?";
    private const string TextRenameValueExists =
        "The Registry Editor cannot rename AlphaString. The specified value name already exists. Type another name and try again.";
    private const string TextRenameKeyFailed = "The Registry Editor cannot rename {0}. Error while renaming key.";
    private const string TextNewKeyExists = "A registry object with this name already exists.";

    /// <summary>The text of every message box the checks saw. Scanned for developer text at the end.</summary>
    private static readonly List<string> SeenMessages = new();

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Messages")]
    [TestCategory("Mutating")]
    public void Messages_ClassicCaptionsTextsAndButtonsAreShown()
    {
        _checkStart = E2EResults.CheckCount;
        SeenMessages.Clear();
        var config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("messages.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        try
        {
            sandbox.Cleanup();
            EditingTests.SeedSandbox(sandbox);

            string dataRoot = CreateFreshDataRoot(config, "messages");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);
            using var automation = new UIA3Automation();

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EResults.Record(
                "messages.navigate",
                navigated,
                $"navigated to '{sandbox.RunRootDisplayPath}'={navigated}.");
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "messages.sandbox.navigate");

            bool seeded = ControlTestSupport.WaitUntil(
                () => ContainsAll(ControlTestSupport.ListRowNames(session), "AlphaString", "SampleBinary", "SeedDword"),
                TimeSpan.FromSeconds(15));
            E2EResults.Record(
                "messages.seed.values",
                seeded,
                $"list rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}].");

            CheckNewKeyWhereNameExists(session, driver, automation, sandbox);
            CheckDeleteSeveralValues(session, driver, automation, sandbox);
            CheckRenameToExistingValue(session, driver, automation, sandbox);
            CheckDeleteKeyConfirmation(session, driver, automation, sandbox, config);
            CheckRenameRunKeyRefused(session, driver, automation, sandbox, config);
            CheckNoDeveloperText();

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    /// <summary>Two values selected, Delete: the plural text and caption, then No. Nothing is deleted.</summary>
    private static void CheckDeleteSeveralValues(
        AppSession session,
        EditingDriver driver,
        UIA3Automation automation,
        SandboxRegistry sandbox)
    {
        bool first = SelectListRowStable(session, "AlphaString");
        bool second = AddListRowToSelection(session, "SeedDword");
        E2EGuards.RequireListRowSelected(session, "AlphaString", "messages.deletevalues.row");
        E2EGuards.RequireSandboxSelection(session, sandbox, "messages.deletevalues.sandbox");

        driver.OpenMenu(MenuEdit, MenuEditDelete);
        AutomationElement? root = WaitForInProcess(session, automation, "DialogRoot");
        string caption = root is null ? string.Empty : CaptionOf(root);
        string text = driver.TextOf("DialogMessage");
        SeenMessages.Add(text);
        string primary = NameOf(session, automation, "DialogPrimary");
        string secondary = NameOf(session, automation, "DialogSecondary");
        E2EResults.Record(
            "messages.deletevalues.dialog",
            first && second && root is not null,
            $"selected both={first && second}, dialog={root is not null}, caption='{caption}', text='{text}'.");
        E2EResults.Record(
            "messages.deletevalues.caption",
            string.Equals(caption, CaptionConfirmValueDelete, StringComparison.Ordinal),
            $"caption='{caption}', expected '{CaptionConfirmValueDelete}'.");
        E2EResults.Record(
            "messages.deletevalues.text",
            string.Equals(text, TextConfirmValuesDelete, StringComparison.Ordinal),
            $"text='{text}'.");
        E2EResults.Record(
            "messages.deletevalues.buttons",
            primary == "Yes" && secondary == "No",
            $"primary='{primary}', secondary='{secondary}'.");

        bool no = driver.Invoke("DialogSecondary");
        bool closed = no && driver.WaitForNoDialog();
        bool kept = ControlTestSupport.WaitUntil(
            () => sandbox.ReadString(string.Empty, "AlphaString") == "alpha" && ValueExists(sandbox, "SeedDword"),
            TimeSpan.FromSeconds(5));
        E2EResults.Record(
            "messages.deletevalues.no",
            no && closed && kept,
            $"no={no}, closed={closed}, values kept={kept}.");
    }

    /// <summary>Rename a value to the name of another value: caption 72 and text 74. The value is not changed.</summary>
    private static void CheckRenameToExistingValue(
        AppSession session,
        EditingDriver driver,
        UIA3Automation automation,
        SandboxRegistry sandbox)
    {
        bool selected = SelectListRowStable(session, "AlphaString");
        E2EGuards.RequireListRowSelected(session, "AlphaString", "messages.renamevalue.row");
        E2EGuards.RequireSandboxSelection(session, sandbox, "messages.renamevalue.sandbox");

        bool f2 = driver.TypeKeys("F2");
        bool editorOpen = f2 && ControlTestSupport.WaitUntil(
            () => session.Find("ListRenameBox") is not null,
            TimeSpan.FromSeconds(5));
        bool typed = editorOpen && driver.TypeKeys("SeedDword");
        bool committed = typed && driver.TypeKeys("Enter");

        AutomationElement? root = committed ? WaitForInProcess(session, automation, "DialogRoot") : null;
        string caption = root is null ? string.Empty : CaptionOf(root);
        string text = driver.TextOf("DialogErrorText");
        SeenMessages.Add(text);
        string primary = NameOf(session, automation, "DialogPrimary");
        E2EResults.Record(
            "messages.renamevalue.caption",
            string.Equals(caption, CaptionRenameValueError, StringComparison.Ordinal),
            $"selected={selected}, dialog={root is not null}, caption='{caption}'.");
        E2EResults.Record(
            "messages.renamevalue.text",
            string.Equals(text, TextRenameValueExists, StringComparison.Ordinal),
            $"text='{text}'.");
        E2EResults.Record(
            "messages.renamevalue.button",
            primary == "OK",
            $"primary='{primary}'.");

        bool ok = root is not null && driver.Invoke("DialogPrimary");
        bool closed = ok && driver.WaitForNoDialog();
        bool unchanged = ControlTestSupport.WaitUntil(
            () => sandbox.ReadString(string.Empty, "AlphaString") == "alpha",
            TimeSpan.FromSeconds(5));
        E2EResults.Record(
            "messages.renamevalue.unchanged",
            ok && closed && unchanged,
            $"ok={ok}, closed={closed}, AlphaString unchanged={unchanged}.");
    }

    /// <summary>
    /// New Key under the run key, where a key named Child already exists: the prompt shows the
    /// duplicate text and does not create a key. Escape cancels.
    /// </summary>
    private static void CheckNewKeyWhereNameExists(
        AppSession session,
        EditingDriver driver,
        UIA3Automation automation,
        SandboxRegistry sandbox)
    {
        E2EGuards.RequireSandboxSelection(session, sandbox, "messages.newkey.sandbox");
        driver.OpenMenu(MenuEdit, MenuEditNew, MenuEditNewKey);
        bool prompt = driver.WaitForDialog() && driver.WaitForElement("DialogTextInput") is not null;
        bool typed = prompt && driver.SetText("DialogTextInput", "Child");
        // The duplicate check runs when the prompt is submitted, and the prompt stays open with the text.
        bool submitted = typed && driver.Invoke("DialogPrimary");

        string validation = string.Empty;
        bool shown = submitted && ControlTestSupport.WaitUntil(
            () =>
            {
                validation = driver.TextOf("DialogValidationText");
                return string.Equals(validation, TextNewKeyExists, StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(5));
        if (validation.Length > 0)
        {
            SeenMessages.Add(validation);
        }

        bool cancelled = shown && driver.Invoke("DialogSecondary");
        bool closed = cancelled && driver.WaitForNoDialog();
        bool childKept = ControlTestSupport.WaitUntil(
            () => sandbox.ReadString("Child", "Marker") == "child",
            TimeSpan.FromSeconds(5));
        E2EResults.Record(
            "messages.newkey.exists",
            prompt && typed && shown && closed && childKept,
            $"prompt={prompt}, validation='{validation}', shown={shown}, closed={closed}, Child kept={childKept}.");
    }

    /// <summary>Delete on a key: the confirmation text, caption, and buttons, with the default on Yes. Then No.</summary>
    private static void CheckDeleteKeyConfirmation(
        AppSession session,
        EditingDriver driver,
        UIA3Automation automation,
        SandboxRegistry sandbox,
        E2EConfig config)
    {
        bool selected = ControlTestSupport.NavigateToSandboxKey(session, config, "Child");
        E2EGuards.RequireSandboxNavigation(session, sandbox, selected, "messages.deletekey.navigate");

        driver.OpenMenu(MenuEdit, MenuEditDelete);
        AutomationElement? root = WaitForInProcess(session, automation, "DialogRoot");
        string caption = root is null ? string.Empty : CaptionOf(root);
        string text = driver.TextOf("DialogMessage");
        SeenMessages.Add(text);
        string primary = NameOf(session, automation, "DialogPrimary");
        string secondary = NameOf(session, automation, "DialogSecondary");
        bool primaryHasFocus = root is not null && HasFocus(session, automation, "DialogPrimary");
        E2EResults.Record(
            "messages.deletekey.dialog",
            root is not null,
            $"dialog={root is not null}, caption='{caption}'.");
        E2EResults.Record(
            "messages.deletekey.caption",
            string.Equals(caption, CaptionConfirmKeyDelete, StringComparison.Ordinal),
            $"caption='{caption}', expected '{CaptionConfirmKeyDelete}'.");
        E2EResults.Record(
            "messages.deletekey.text",
            string.Equals(text, TextConfirmKeyDelete, StringComparison.Ordinal),
            $"text='{text}'.");
        E2EResults.Record(
            "messages.deletekey.buttons",
            primary == "Yes" && secondary == "No",
            $"primary='{primary}', secondary='{secondary}'.");
        // Classic regedit focuses Yes when the confirmation opens, so Yes is the default button.
        E2EResults.Record(
            "messages.deletekey.default",
            primaryHasFocus,
            $"Yes has focus on open={primaryHasFocus}.");

        bool no = driver.Invoke("DialogSecondary");
        bool closed = no && driver.WaitForNoDialog();
        bool kept = ControlTestSupport.WaitUntil(
            () => sandbox.Exists("Child"),
            TimeSpan.FromSeconds(5));
        E2EResults.Record(
            "messages.deletekey.no",
            no && closed && kept,
            $"no={no}, closed={closed}, Child kept={kept}.");
    }

    /// <summary>
    /// Rename the run key itself. The sandbox write policy refuses renaming its root, so the write
    /// fails with access denied before any native call. The message is the classic rename text, and
    /// the run key is still there afterwards.
    /// </summary>
    private static void CheckRenameRunKeyRefused(
        AppSession session,
        EditingDriver driver,
        UIA3Automation automation,
        SandboxRegistry sandbox,
        E2EConfig config)
    {
        bool selected = ControlTestSupport.NavigateToSandboxKey(session, config);
        E2EGuards.RequireSandboxNavigation(session, sandbox, selected, "messages.renamekey.navigate");
        string runId = SandboxRunId(sandbox);

        driver.OpenMenu(MenuEdit, MenuEditRename);
        bool prompt = driver.WaitForDialog() && driver.WaitForElement("DialogTextInput") is not null;
        bool typed = prompt && driver.SetText("DialogTextInput", runId + "Renamed");
        bool submitted = typed && driver.Invoke("DialogPrimary");

        AutomationElement? root = submitted ? WaitForInProcess(session, automation, "DialogRoot") : null;
        string caption = root is null ? string.Empty : CaptionOf(root);
        string text = driver.TextOf("DialogErrorText");
        SeenMessages.Add(text);
        string expectedText = string.Format(System.Globalization.CultureInfo.InvariantCulture, TextRenameKeyFailed, runId);
        E2EResults.Record(
            "messages.renamekey.caption",
            string.Equals(caption, CaptionRenameKeyError, StringComparison.Ordinal),
            $"prompt={prompt}, submitted={submitted}, dialog={root is not null}, caption='{caption}'.");
        E2EResults.Record(
            "messages.renamekey.text",
            string.Equals(text, expectedText, StringComparison.Ordinal),
            $"text='{text}', expected '{expectedText}'.");

        bool ok = root is not null && driver.Invoke("DialogPrimary");
        bool closed = ok && driver.WaitForNoDialog();
        bool stillThere = ControlTestSupport.WaitUntil(() => sandbox.Exists(string.Empty), TimeSpan.FromSeconds(5));
        E2EResults.Record(
            "messages.renamekey.kept",
            closed && stillThere,
            $"ok={ok}, closed={closed}, run key still exists={stillThere}.");
    }

    /// <summary>Every message box text the checks saw must be classic text, not developer text.</summary>
    private static void CheckNoDeveloperText()
    {
        string[] forbidden = { "Operation", "Win32", "HRESULT", "Exception", "   at " };
        var hits = new List<string>();
        foreach (string message in SeenMessages)
        {
            foreach (string word in forbidden)
            {
                if (message.Contains(word, StringComparison.Ordinal))
                {
                    hits.Add($"'{word}' in '{message}'");
                }
            }
        }

        E2EResults.Record(
            "messages.no-developer-text",
            hits.Count == 0 && SeenMessages.Count > 0,
            $"messages checked={SeenMessages.Count}; developer-text hits=[{string.Join("; ", hits)}].");
    }

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

    private static bool AddListRowToSelection(AppSession session, string rowName) =>
        ControlTestSupport.WaitUntil(
            () =>
            {
                AutomationElement? row = ControlTestSupport.FindRowByName(
                    session,
                    ControlTestSupport.ListControl,
                    ControlType.DataItem,
                    rowName);
                if (row is not null)
                {
                    try
                    {
                        if (row.Patterns.SelectionItem.IsSupported)
                        {
                            row.Patterns.SelectionItem.Pattern.AddToSelection();
                        }
                    }
                    catch (Exception)
                    {
                        // The check below records the selection state.
                    }
                }

                return ControlTestSupport.IsSelected(
                    ControlTestSupport.FindRowByName(
                        session,
                        ControlTestSupport.ListControl,
                        ControlType.DataItem,
                        rowName));
            },
            TimeSpan.FromSeconds(10));

    /// <summary>Finds an element with this automation id in a window of the Registry Editor process.</summary>
    private static AutomationElement? WaitForInProcess(AppSession session, UIA3Automation automation, string automationId)
    {
        AutomationElement? found = null;
        _ = ControlTestSupport.WaitUntil(
            () => (found = FindInProcess(session, automation, automationId)) is not null,
            TimeSpan.FromSeconds(10));
        return found;
    }

    private static AutomationElement? FindInProcess(AppSession session, UIA3Automation automation, string automationId)
    {
        try
        {
            AutomationElement desktop = automation.GetDesktop();
            foreach (AutomationElement window in desktop.FindAllChildren(cf => cf.ByControlType(ControlType.Window)))
            {
                try
                {
                    if (window.Properties.ProcessId.Value != session.ProcessId)
                    {
                        continue;
                    }

                    AutomationElement? found = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
                    if (found is not null)
                    {
                        return found;
                    }
                }
                catch (Exception)
                {
                    // A window that closed during enumeration is skipped.
                }
            }
        }
        catch (Exception)
        {
            // UIA can throw while a window is being created or destroyed.
        }

        return null;
    }

    private static string NameOf(AppSession session, UIA3Automation automation, string automationId)
    {
        AutomationElement? element = FindInProcess(session, automation, automationId);
        try
        {
            return element?.Name ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static bool HasFocus(AppSession session, UIA3Automation automation, string automationId)
    {
        AutomationElement? element = FindInProcess(session, automation, automationId);
        try
        {
            return element is not null && element.Properties.HasKeyboardFocus.Value;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The caption of a dialog: the name of its top-level window.</summary>
    private static string CaptionOf(AutomationElement element)
    {
        AutomationElement? current = element;
        for (int depth = 0; current is not null && depth < 32; depth++)
        {
            try
            {
                if (current.ControlType == ControlType.Window)
                {
                    return current.Name ?? string.Empty;
                }
            }
            catch (Exception)
            {
                // This ancestor has no name. The next one is tried.
            }

            try
            {
                current = current.Parent;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        return string.Empty;
    }

    private static bool ValueExists(SandboxRegistry sandbox, string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(sandbox.RunRootPath, writable: false);
        return key?.GetValue(name) is not null;
    }

    private static string SandboxRunId(SandboxRegistry sandbox)
    {
        string path = sandbox.RunRootPath;
        int separator = path.LastIndexOf('\\');
        return separator >= 0 ? path[(separator + 1)..] : path;
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
            $"Message checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}

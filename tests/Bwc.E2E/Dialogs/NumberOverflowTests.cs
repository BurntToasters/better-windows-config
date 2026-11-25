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

namespace Bwc.E2E.Dialogs;

/// <summary>
/// Decimal input above the DWORD or QWORD maximum (DLG-023, DLG-036, MSG-052, MSG-060, MSG-061). The classic truncation
/// prompt has the caption Overflow (RT_STRING 110) and the text of RT_STRING 119 or 120. No keeps the editor open with
/// the typed text and the stored value unchanged. Yes stores the largest value of the width. Hexadecimal input above the
/// width keeps the input error. The QWORD editor's layout is checked against RT_DIALOG 117 (DLG-035). The oracle dump
/// records the prompt text but not its buttons or caption, so the answers follow the text (classic-dialog.md section 13).
/// </summary>
[TestClass]
public sealed class NumberOverflowTests
{
    private const string DwordName = "E2EOverflowDword";
    private const string QwordName = "E2EOverflowQword";
    private const string DwordEditorCaption = "Edit DWORD (32-bit) Value";
    private const string QwordEditorCaption = "Edit QWORD (64-bit) Value";
    private const string OverflowCaption = "Overflow";
    private const string OverflowMarker = "is greater than the maximum value of a";

    // Classic RT_STRING 119 and 120 as the oracle dump records them (CRLF between the two sentences).
    private const string DwordPrompt =
        "The decimal value entered is greater than the maximum value of a DWORD (32-bit number).\r\nShould the value be truncated in order to continue?";
    private const string QwordPrompt =
        "The decimal value entered is greater than the maximum value of a QWORD (64-bit number).\r\nShould the value be truncated in order to continue?";

    private const string DwordHexError = "Enter a hexadecimal number from 0 to FFFFFFFF.";
    private const string QwordHexError = "Enter a hexadecimal number from 0 to FFFFFFFFFFFFFFFF.";

    private const string ValueInput = "ValueEditorInput";
    private const string NameInputId = "ValueNameInput";
    private const string HexId = "BaseHex";
    private const string DecId = "BaseDec";
    private const string PrimaryId = "DialogPrimary";
    private const string SecondaryId = "DialogSecondary";
    private const string MessageId = "DialogMessage";
    private const string ErrorTextId = "DialogErrorText";
    private const string DialogRootId = "DialogRoot";

    private const string DwordTypedOver = "4294967296";
    private const string QwordTypedOver = "18446744073709551616";
    private const string QwordTypedMax = "18446744073709551615";

    private const ulong OriginalValue = 5000;

    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(10);

    // RT_DIALOG 117 (Edit QWORD (64-bit) Value): 219 x 103 DLU, controls in template order. The numbers are the template's
    // own; each control is compared with MapDialogRect the same way the DWORD and string editors are checked.
    private static readonly ClassicDialogTests.ControlRect[] QwordControls =
    [
        new("ValueDataLabel", 65535, 6, 35, 61, 8),
        new(ValueInput, 1001, 6, 46, 92, 12),
        new("BaseGroup", 65535, 105, 35, 108, 39),
        new(HexId, 1002, 115, 46, 60, 10),
        new(DecId, 1003, 115, 59, 60, 10),
        new(PrimaryId, 1, 108, 81, 50, 14),
        new(SecondaryId, 2, 163, 81, 50, 14),
        new("ValueNameLabel", 65535, 6, 6, 69, 8),
        new(NameInputId, 1000, 6, 17, 207, 12),
    ];

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Dialogs")]
    [TestCategory("Mutating")]
    public void NumberOverflow_DwordDecimalAnswers()
    {
        RunDecimalOverflow(
            scope: "overflow-dword",
            valueName: DwordName,
            prompt: DwordPrompt,
            typed: DwordTypedOver,
            bits: 32,
            checkId: "overflow.dword");
    }

    [TestMethod]
    [TestCategory("Dialogs")]
    [TestCategory("Mutating")]
    public void NumberOverflow_QwordDecimalAnswers()
    {
        RunDecimalOverflow(
            scope: "overflow-qword",
            valueName: QwordName,
            prompt: QwordPrompt,
            typed: QwordTypedOver,
            bits: 64,
            checkId: "overflow.qword");
    }

    [TestMethod]
    [TestCategory("Dialogs")]
    [TestCategory("Mutating")]
    public void NumberOverflow_HexadecimalKeepsInputError()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        config.EnsureExecutablesExist();

        var sandbox = new SandboxRegistry(config);
        try
        {
            sandbox.Cleanup();
            Seed(sandbox);

            using var session = StartApp(config, sandbox, "overflow-hex");
            using var driver = new EditingDriver(session);
            using var automation = new UIA3Automation();
            NavigateToRun(session, config, sandbox, "overflow.hex");

            CheckHexError(session, driver, automation, sandbox, DwordName, "100000000", DwordHexError, "overflow.hex.dword");
            CheckHexError(session, driver, automation, sandbox, QwordName, "10000000000000000", QwordHexError, "overflow.hex.qword");

            FailIfNeeded(checkStart);
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    [TestMethod]
    [TestCategory("Dialogs")]
    [TestCategory("Mutating")]
    public void NumberOverflow_QwordLayout()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        config.EnsureExecutablesExist();

        var sandbox = new SandboxRegistry(config);
        try
        {
            sandbox.Cleanup();
            Seed(sandbox);

            using var session = StartApp(config, sandbox, "overflow-layout");
            using var driver = new EditingDriver(session);
            using var automation = new UIA3Automation();
            NavigateToRun(session, config, sandbox, "overflow.layout");

            AutomationElement? row = ClassicDialogTests.OpenValueRow(session, sandbox, QwordName, "overflow.layout");
            bool opened = row is not null && ClassicDialogTests.OpenEditor(session, driver, row, "overflow.layout.open");
            if (!opened)
            {
                E2EResults.Record("overflow.layout.open", false, "Modify did not open the QWORD editor.");
                FailIfNeeded(checkStart);
                return;
            }

            AutomationElement dialog = driver.WaitForElement(DialogRootId)!;
            IntPtr hwnd = ClassicDialogTests.DialogHandle(dialog);
            OwnedWindowChecks.Record(hwnd);
            _ = ClassicDialogTests.EnsureForeground(hwnd);

            string caption = ClassicDialogTests.CaptionOf(automation, hwnd);
            E2EResults.Record(
                "overflow.layout.caption",
                caption == QwordEditorCaption,
                $"window caption='{caption}', expected '{QwordEditorCaption}'.");

            string shown = driver.TextOf(ValueInput);
            AutomationElement? hex = driver.WaitForElement(HexId, TimeSpan.FromSeconds(2));
            bool hexSelected = ControlTestSupport.IsSelected(hex);
            E2EResults.Record(
                "overflow.layout.hex-default",
                hexSelected && shown == "1388",
                $"Hexadecimal selected={hexSelected}, shown value='{shown}', expected '1388' for 5000.");

            string tabs = ClassicDialogTests.TabSequence(automation, 5);
            string expectedTabs = $"{HexId},{PrimaryId},{SecondaryId},{NameInputId},{ValueInput}";
            E2EResults.Record(
                "overflow.layout.tab-order",
                tabs == expectedTabs,
                $"tab sequence='{tabs}', expected '{expectedTabs}'.");

            bool onPrimary = ClassicDialogTests.FocusAutomationId(automation, PrimaryId);
            ClassicDialogTests.CheckGeometry("overflow.layout.geometry", dialog, hwnd, QwordControls, 219, 103);
            E2EResults.Record("overflow.layout.geometry-focus", onPrimary, $"default button focused for the measurement={onPrimary}.");
            ClassicDialogTests.CheckValueName(driver, QwordName, "overflow.layout.value-name");

            // The base switch converts the QWORD maximum both ways, with no prompt and no lost digits.
            bool decimalSelected = ClassicDialogTests.SendAltLetter('D') && ControlTestSupport.WaitUntil(
                () => ControlTestSupport.IsSelected(driver.WaitForElement(DecId, TimeSpan.FromSeconds(1))),
                TimeSpan.FromSeconds(2));
            _ = ClassicDialogTests.SendAltLetter('V');
            _ = ControlTestSupport.WaitUntil(() => ClassicDialogTests.FocusedId(automation) == ValueInput, TimeSpan.FromSeconds(2));
            ClassicDialogTests.SelectAllAndType(QwordTypedMax);
            bool toHex = ClassicDialogTests.SendAltLetter('H') && ControlTestSupport.WaitUntil(
                () => driver.TextOf(ValueInput) == "FFFFFFFFFFFFFFFF",
                TimeSpan.FromSeconds(3));
            string hexText = driver.TextOf(ValueInput);
            bool backToDecimal = ClassicDialogTests.SendAltLetter('D') && ControlTestSupport.WaitUntil(
                () => driver.TextOf(ValueInput) == QwordTypedMax,
                TimeSpan.FromSeconds(3));
            E2EResults.Record(
                "overflow.layout.base-convert",
                decimalSelected && toHex && backToDecimal,
                $"decimal selected={decimalSelected}, shown in hexadecimal='{hexText}', back to decimal={backToDecimal}.");

            _ = ClassicDialogTests.SendAltLetter('V');
            _ = ControlTestSupport.WaitUntil(() => ClassicDialogTests.FocusedId(automation) == ValueInput, TimeSpan.FromSeconds(2));
            Keyboard.Type(VirtualKeyShort.RETURN);
            bool closed = ControlTestSupport.WaitUntil(() => !Win32.IsWindow(hwnd), TimeSpan.FromSeconds(10));
            bool noPrompt = FindPrompt(session, automation, null) is null;
            ulong? stored = ReadStored(sandbox, QwordName);
            E2EResults.Record(
                "overflow.layout.max-accepted",
                closed && noPrompt && stored == ulong.MaxValue,
                $"closed={closed}, no prompt for the maximum={noPrompt}, stored={stored?.ToString() ?? "(none)"}, expected {ulong.MaxValue}.");

            FailIfNeeded(checkStart);
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    /// <summary>
    /// Opens the editor on the decimal base, types a value above the width and presses OK. The prompt must appear with the
    /// Overflow caption and the classic text. No keeps the value; Yes stores the largest value of the width.
    /// </summary>
    private static void RunDecimalOverflow(string scope, string valueName, string prompt, string typed, int bits, string checkId)
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        config.EnsureExecutablesExist();

        var sandbox = new SandboxRegistry(config);
        try
        {
            sandbox.Cleanup();
            Seed(sandbox);

            using var session = StartApp(config, sandbox, scope);
            using var driver = new EditingDriver(session);
            using var automation = new UIA3Automation();
            NavigateToRun(session, config, sandbox, checkId);

            AutomationElement? row = ClassicDialogTests.OpenValueRow(session, sandbox, valueName, checkId);
            bool opened = row is not null && ClassicDialogTests.OpenEditor(session, driver, row, checkId + ".open");
            if (!opened)
            {
                E2EResults.Record(checkId + ".open", false, $"Modify did not open the editor for '{valueName}'.");
                FailIfNeeded(checkStart);
                return;
            }

            AutomationElement dialog = driver.WaitForElement(DialogRootId)!;
            IntPtr editor = ClassicDialogTests.DialogHandle(dialog);
            _ = ClassicDialogTests.EnsureForeground(editor);

            bool decimalSelected = ClassicDialogTests.SendAltLetter('D') && ControlTestSupport.WaitUntil(
                () => ControlTestSupport.IsSelected(driver.WaitForElement(DecId, TimeSpan.FromSeconds(1))),
                TimeSpan.FromSeconds(2));
            E2EResults.Record(checkId + ".decimal", decimalSelected, $"Decimal selected={decimalSelected}.");

            // First answer: No. The stored value stays, and the editor keeps the typed text.
            SubmitTyped(editor, automation, typed);
            AutomationElement? first = WaitForPrompt(session, automation, prompt, PromptTimeout);
            CheckPrompt(first, prompt, checkId + ".no", editor);
            bool no = first is not null && InvokeIn(first, SecondaryId);
            bool promptGone = ControlTestSupport.WaitUntil(
                () => FindPrompt(session, automation, null) is null,
                TimeSpan.FromSeconds(10));
            bool editorOpen = Win32.IsWindow(editor);
            string afterNo = editorOpen ? driver.TextOf(ValueInput) : "(editor closed)";
            ulong? storedAfterNo = ReadStored(sandbox, valueName);
            E2EResults.Record(
                checkId + ".no",
                no && promptGone && editorOpen && afterNo == typed && storedAfterNo == OriginalValue,
                $"No pressed={no}, prompt gone={promptGone}, editor open={editorOpen}, shown='{afterNo}' " +
                    $"(expected '{typed}'), stored={storedAfterNo?.ToString() ?? "(none)"} (expected {OriginalValue}).");

            // Second answer: Yes. The editor closes and the largest value of the width is stored.
            SubmitTyped(editor, automation, typed);
            AutomationElement? second = WaitForPrompt(session, automation, prompt, PromptTimeout);
            bool promptAgain = second is not null;
            bool yes = second is not null && InvokeIn(second, PrimaryId);
            bool editorClosed = ControlTestSupport.WaitUntil(() => !Win32.IsWindow(editor), TimeSpan.FromSeconds(10));
            ulong expected = bits == 32 ? uint.MaxValue : ulong.MaxValue;
            bool stored = ControlTestSupport.WaitUntil(() => ReadStored(sandbox, valueName) == expected, TimeSpan.FromSeconds(10));
            E2EResults.Record(
                checkId + ".yes",
                promptAgain && yes && editorClosed && stored,
                $"prompt again={promptAgain}, Yes pressed={yes}, editor closed={editorClosed}, " +
                    $"stored={ReadStored(sandbox, valueName)?.ToString() ?? "(none)"} (expected {expected}).");

            FailIfNeeded(checkStart);
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    private static void CheckHexError(
        AppSession session,
        EditingDriver driver,
        UIA3Automation automation,
        SandboxRegistry sandbox,
        string valueName,
        string typedHex,
        string expectedError,
        string checkId)
    {
        AutomationElement? row = ClassicDialogTests.OpenValueRow(session, sandbox, valueName, checkId);
        bool opened = row is not null && ClassicDialogTests.OpenEditor(session, driver, row, checkId + ".open");
        if (!opened)
        {
            E2EResults.Record(checkId + ".open", false, $"Modify did not open the editor for '{valueName}'.");
            return;
        }

        AutomationElement dialog = driver.WaitForElement(DialogRootId)!;
        IntPtr editor = ClassicDialogTests.DialogHandle(dialog);
        _ = ClassicDialogTests.EnsureForeground(editor);
        bool hexDefault = ControlTestSupport.IsSelected(driver.WaitForElement(HexId, TimeSpan.FromSeconds(2)));
        E2EResults.Record(checkId + ".hex-default", hexDefault, $"Hexadecimal selected={hexDefault}.");

        SubmitTyped(editor, automation, typedHex);
        AutomationElement? error = null;
        _ = ControlTestSupport.WaitUntil(
            () => (error = driver.WaitForElement(ErrorTextId, TimeSpan.FromMilliseconds(300))) is not null,
            TimeSpan.FromSeconds(5));
        string text = NormalizeLines(ControlTestSupport.SafeName(error));
        bool noPrompt = FindPrompt(session, automation, null) is null;
        E2EResults.Record(
            checkId + ".error",
            error is not null && text == expectedError && noPrompt,
            $"input error='{text}' (expected '{expectedError}'), no overflow prompt={noPrompt}.");

        bool dismissed = error is not null && driver.DismissErrorBox();
        _ = ClassicDialogTests.EnsureForeground(editor);
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        bool closed = ControlTestSupport.WaitUntil(() => !Win32.IsWindow(editor), TimeSpan.FromSeconds(10));
        ulong? stored = ReadStored(sandbox, valueName);
        E2EResults.Record(
            checkId + ".unchanged",
            dismissed && closed && stored == OriginalValue,
            $"error dismissed={dismissed}, editor closed by Escape={closed}, stored={stored?.ToString() ?? "(none)"} (expected {OriginalValue}).");
    }

    private static void CheckPrompt(AutomationElement? prompt, string expectedText, string checkId, IntPtr editor)
    {
        bool shown = prompt is not null;
        string text = NormalizeLines(ControlTestSupport.SafeName(prompt?.FindFirstDescendant(cf => cf.ByAutomationId(MessageId))));
        string caption = ControlTestSupport.SafeName(prompt);
        E2EResults.Record(
            checkId + ".prompt",
            shown && caption == OverflowCaption && text == NormalizeLines(expectedText),
            $"prompt shown={shown}, caption='{caption}' (expected '{OverflowCaption}'), text='{text}', " +
                $"text matches classic={text == NormalizeLines(expectedText)}.");

        bool ownerDisabled = editor != IntPtr.Zero && !Win32.IsWindowEnabled(editor);
        E2EResults.Record(checkId + ".owner-disabled", ownerDisabled, $"editor disabled while the prompt is open={ownerDisabled}.");
    }

    private static void SubmitTyped(IntPtr editor, UIA3Automation automation, string text)
    {
        _ = ClassicDialogTests.EnsureForeground(editor);
        _ = ClassicDialogTests.SendAltLetter('V');
        _ = ControlTestSupport.WaitUntil(() => ClassicDialogTests.FocusedId(automation) == ValueInput, TimeSpan.FromSeconds(2));
        ClassicDialogTests.SelectAllAndType(text);
        Keyboard.Type(VirtualKeyShort.RETURN);
    }

    /// <summary>
    /// Finds the overflow prompt: a top-level window of this app whose message text is the classic prompt. A null
    /// <paramref name="exactText"/> matches any prompt of the overflow family. The caption is read from the window.
    /// </summary>
    private static AutomationElement? FindPrompt(AppSession session, UIA3Automation automation, string? exactText)
    {
        foreach (AutomationElement window in ProcessWindows.Of(automation, session.ProcessId))
        {
            try
            {
                AutomationElement? message = window.FindFirstDescendant(cf => cf.ByAutomationId(MessageId));
                if (message is null)
                {
                    continue;
                }

                string text = NormalizeLines(ControlTestSupport.SafeName(message));
                bool match = exactText is null
                    ? text.Contains(OverflowMarker, StringComparison.Ordinal)
                    : text == NormalizeLines(exactText);
                if (match)
                {
                    // The message can sit under another window in UI Automation; the prompt is its own Window ancestor.
                    return ClassicDialogTests.WindowOf(message);
                }
            }
            catch (Exception)
            {
                // A window that closed during enumeration is skipped.
            }
        }

        return null;
    }

    private static AutomationElement? WaitForPrompt(AppSession session, UIA3Automation automation, string exactText, TimeSpan timeout)
    {
        AutomationElement? found = null;
        _ = ControlTestSupport.WaitUntil(() => (found = FindPrompt(session, automation, exactText)) is not null, timeout);
        return found;
    }

    /// <summary>Invokes a button inside one window, so the editor's OK is never the one pressed.</summary>
    private static bool InvokeIn(AutomationElement window, string automationId)
    {
        AutomationElement? button = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        if (button is null)
        {
            return false;
        }

        try
        {
            if (button.Patterns.Invoke.IsSupported)
            {
                button.Patterns.Invoke.Pattern.Invoke();
                return true;
            }

            button.Click();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The stored value as an unsigned number. A DWORD comes back as a signed int32; the sign is undone.</summary>
    private static ulong? ReadStored(SandboxRegistry sandbox, string name)
    {
        object? raw = Registry.GetValue("HKEY_CURRENT_USER\\" + sandbox.RunRootPath, name, null);
        return raw switch
        {
            int dword => unchecked((uint)dword),
            long qword => unchecked((ulong)qword),
            _ => null,
        };
    }

    private static string NormalizeLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static void Seed(SandboxRegistry sandbox)
    {
        sandbox.SetDword(string.Empty, DwordName, 5000);
        sandbox.SetValueOfKind(string.Empty, QwordName, 5000L, RegistryValueKind.QWord);
    }

    private static void NavigateToRun(AppSession session, E2EConfig config, SandboxRegistry sandbox, string checkId)
    {
        bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
        E2EResults.Record(checkId + ".navigate", navigated, $"navigated to '{sandbox.RunRootDisplayPath}'={navigated}.");
        E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, checkId + ".sandbox");
        bool seeded = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.ListRowNames(session).Contains(DwordName, StringComparer.OrdinalIgnoreCase) &&
                ControlTestSupport.ListRowNames(session).Contains(QwordName, StringComparer.OrdinalIgnoreCase),
            UiTimeout);
        E2EResults.Record(checkId + ".seed", seeded, $"list rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}].");
    }

    private static AppSession StartApp(E2EConfig config, SandboxRegistry sandbox, string scope)
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

    private static void FailIfNeeded(int checkStart)
    {
        var failures = E2EResults.FailedChecksSince(checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Number overflow checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}

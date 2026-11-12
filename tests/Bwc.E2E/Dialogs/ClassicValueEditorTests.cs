using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Dialogs;

/// <summary>
/// Drives the classic Edit Multi-String and Edit Binary Value dialogs (RT_DIALOG 114 and 103) through
/// real input and checks them against the templates: caption, control rectangles within 1 DIP of the
/// template at the window DPI, tab order, the read-only value name, the empty-string warning and its
/// cleanup, and the 8-byte row of the HEX control. Each dialog is saved to artifacts/e2e/visual/dialogs
/// and its client area is checked for contrast.
/// </summary>
[TestClass]
public sealed class ClassicValueEditorTests
{
    private const string MultiName = "E2EMultiValue";
    private const string BinaryName = "E2EBinaryValue";
    private const string DialogRootId = "DialogRoot";
    private const string PrimaryId = "DialogPrimary";
    private const string SecondaryId = "DialogSecondary";
    private const string NameInputId = "ValueNameInput";
    private const string MultiInputId = "ValueEditorInput";
    private const string HexId = "HexEditor";
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The template of Edit Multi-String, 232 x 178 DLU.</summary>
    private static readonly ClassicDialogTests.ControlRect[] MultiControls =
    [
        new("ValueDataLabel", 65535, 6, 33, 220, 8),
        new(MultiInputId, 1001, 6, 44, 220, 110),
        new(PrimaryId, 1, 120, 158, 50, 14),
        new(SecondaryId, 2, 176, 158, 50, 14),
        new("ValueNameLabel", 65535, 6, 6, 220, 8),
        new(NameInputId, 1000, 6, 17, 220, 12),
    ];

    /// <summary>The template of Edit Binary Value, 232 x 178 DLU, with the HEX control at 220 x 110 DLU.</summary>
    private static readonly ClassicDialogTests.ControlRect[] BinaryControls =
    [
        new("ValueDataLabel", 65535, 6, 33, 220, 8),
        new(HexId, 1001, 6, 44, 220, 110),
        new(PrimaryId, 1, 120, 158, 50, 14),
        new(SecondaryId, 2, 176, 158, 50, 14),
        new("ValueNameLabel", 65535, 6, 6, 220, 8),
        new(NameInputId, 1000, 6, 17, 220, 12),
    ];

    private static readonly string[] MultiStrings = ["one", string.Empty, string.Empty, "two"];

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Dialogs")]
    [TestCategory("Mutating")]
    public void ClassicValueEditors_MultiStringWarningAndBinaryFollowTheTemplates()
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
            E2EResults.Record("valueeditors.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        string? dataRoot = null;
        try
        {
            sandbox.Cleanup();
            Seed(sandbox);
            dataRoot = ClassicDialogTests.FreshDataRoot(config, "valueeditors");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);
            using var automation = new UIA3Automation();

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            bool seeded = ControlTestSupport.WaitUntil(
                () => ClassicDialogTests.RowExists(session, MultiName) && ClassicDialogTests.RowExists(session, BinaryName),
                UiTimeout);
            E2EResults.Record(
                "valueeditors.seed.rows",
                navigated && seeded,
                $"navigated={navigated}, rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}].");
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated && seeded, "valueeditors.sandbox.navigate");

            string artifacts = Path.Combine(config.RepoRoot, "artifacts", "e2e", "visual", "dialogs");
            Directory.CreateDirectory(artifacts);

            CheckMultiString(session, driver, automation, sandbox, artifacts);
            CheckBinary(session, driver, automation, sandbox, artifacts);

            var failures = E2EResults.FailedChecksSince(_checkStart);
            Assert.AreEqual(
                0,
                failures.Count,
                $"Classic value editor checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            if (dataRoot is not null && Directory.Exists(dataRoot))
            {
                try
                {
                    Directory.Delete(dataRoot, recursive: true);
                }
                catch (IOException)
                {
                    // The data root is scoped to this run and is removed best effort.
                }
            }
        }
    }

    private static void CheckMultiString(
        AppSession session,
        EditingDriver driver,
        UIA3Automation automation,
        SandboxRegistry sandbox,
        string artifacts)
    {
        AutomationElement? row = ClassicDialogTests.OpenValueRow(session, sandbox, MultiName, "valueeditors.multi");
        bool opened = row is not null && ClassicDialogTests.OpenEditor(session, driver, row, "valueeditors.multi.open");
        if (!opened)
        {
            E2EResults.Record("valueeditors.multi.open", false, "Modify did not open Edit Multi-String.");
            return;
        }

        AutomationElement dialog = driver.WaitForElement(DialogRootId)!;
        IntPtr hwnd = ClassicDialogTests.DialogHandle(dialog);
        OwnedWindowChecks.Record(hwnd);
        _ = ClassicDialogTests.EnsureForeground(hwnd);

        string caption = ClassicDialogTests.CaptionOf(automation, hwnd);
        E2EResults.Record(
            "valueeditors.multi.caption",
            caption == "Edit Multi-String",
            $"window caption='{caption}', expected 'Edit Multi-String'.");

        bool focused = ControlTestSupport.WaitUntil(() => ClassicDialogTests.FocusedId(automation) == MultiInputId, TimeSpan.FromSeconds(3));
        string tabs = TabSequence(automation, 4);
        string expectedTabs = $"{PrimaryId},{SecondaryId},{NameInputId},{MultiInputId}";
        E2EResults.Record(
            "valueeditors.multi.focus-and-tab-order",
            focused && tabs == expectedTabs,
            $"initial focus ok={focused}; tab sequence='{tabs}', expected '{expectedTabs}'.");

        bool onPrimary = ClassicDialogTests.FocusAutomationId(automation, PrimaryId);
        ClassicDialogTests.CheckGeometry("valueeditors.multi.geometry", dialog, hwnd, MultiControls, 232, 178);
        E2EResults.Record("valueeditors.multi.geometry-focus", onPrimary, $"default button focused for the measurement={onPrimary}.");
        // The Regedit caller passes the value name, so the name box must show it.
        ClassicDialogTests.CheckValueName(driver, MultiName, "valueeditors.multi.value-name");

        string screenshot = ClassicDialogTests.SaveScreenshot(dialog, Path.Combine(artifacts, "edit-multi-string.png"));
        E2EResults.Note("valueeditors.multi.screenshot", $"saved '{screenshot}', written={File.Exists(screenshot)}.");
        DialogContrast.Check("valueeditors.multi.contrast", ClassicDialogTests.WindowOf(dialog), hwnd, DialogContrast.FaceForSystem());

        // OK with two empty lines shows the classic 117 warning. OK on the warning removes the empties and saves.
        bool invoked = driver.Invoke(PrimaryId);
        bool warned = ControlTestSupport.WaitUntil(
            () => driver.TextOf("DialogMessage").Contains("remove all empty strings found", StringComparison.Ordinal),
            UiTimeout);
        E2EResults.Record(
            "valueeditors.multi.warning",
            invoked && warned,
            $"OK invoked={invoked}, warning shown={warned}, text='{driver.TextOf("DialogMessage")}'.");

        Keyboard.Type(VirtualKeyShort.RETURN);
        bool closed = driver.WaitForNoDialog();
        bool cleaned = ControlTestSupport.WaitUntil(
            () => MultiValueOf(sandbox) is { } values && values.SequenceEqual(new[] { "one", "two" }),
            UiTimeout);
        E2EResults.Record(
            "valueeditors.multi.cleanup",
            closed && cleaned,
            $"closed={closed}, saved without empty strings={cleaned}, value=[{string.Join("|", MultiValueOf(sandbox) ?? Array.Empty<string>())}].");
    }

    private static void CheckBinary(
        AppSession session,
        EditingDriver driver,
        UIA3Automation automation,
        SandboxRegistry sandbox,
        string artifacts)
    {
        AutomationElement? row = ClassicDialogTests.OpenValueRow(session, sandbox, BinaryName, "valueeditors.binary");
        bool opened = row is not null && OpenByDoubleClick(row) && driver.WaitForDialog();
        if (!opened)
        {
            E2EResults.Record("valueeditors.binary.open", false, "Double click did not open Edit Binary Value.");
            return;
        }

        AutomationElement dialog = driver.WaitForElement(DialogRootId)!;
        IntPtr hwnd = ClassicDialogTests.DialogHandle(dialog);
        OwnedWindowChecks.Record(hwnd);
        _ = ClassicDialogTests.EnsureForeground(hwnd);

        string caption = ClassicDialogTests.CaptionOf(automation, hwnd);
        E2EResults.Record(
            "valueeditors.binary.caption",
            caption == "Edit Binary Value",
            $"window caption='{caption}', expected 'Edit Binary Value'.");

        AutomationElement? firstRow = driver.WaitForElement("HexRow_0", UiTimeout);
        string rowText = ControlTestSupport.SafeName(firstRow);
        E2EResults.Record(
            "valueeditors.binary.eight-bytes",
            rowText.StartsWith("0000  41 42 43 44 45 46 47 48", StringComparison.Ordinal),
            $"row 0='{rowText}', expected offset 0000 and eight bytes.");

        bool onPrimary = ClassicDialogTests.FocusAutomationId(automation, PrimaryId);
        ClassicDialogTests.CheckGeometry("valueeditors.binary.geometry", dialog, hwnd, BinaryControls, 232, 178);
        E2EResults.Record("valueeditors.binary.geometry-focus", onPrimary, $"default button focused for the measurement={onPrimary}.");
        ClassicDialogTests.CheckValueName(driver, BinaryName, "valueeditors.binary.value-name");

        string screenshot = ClassicDialogTests.SaveScreenshot(dialog, Path.Combine(artifacts, "edit-binary.png"));
        E2EResults.Note("valueeditors.binary.screenshot", $"saved '{screenshot}', written={File.Exists(screenshot)}.");
        DialogContrast.Check("valueeditors.binary.contrast", ClassicDialogTests.WindowOf(dialog), hwnd, DialogContrast.FaceForSystem());

        bool hexFocused = ClassicDialogTests.FocusAutomationId(automation, HexId);
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        bool closed = driver.WaitForNoDialog();
        byte[]? unchanged = sandbox.ReadBinary(string.Empty, BinaryName);
        E2EResults.Record(
            "valueeditors.binary.escape-cancels",
            hexFocused && closed && unchanged is { Length: 0x30 } && unchanged[0] == 0x41,
            $"hex focused={hexFocused}, closed={closed}, value length={unchanged?.Length ?? -1}.");
    }

    /// <summary>Double-clicks a value row, which is the Modify Binary Data action for a binary value.</summary>
    private static bool OpenByDoubleClick(AutomationElement row)
    {
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

    private static string[]? MultiValueOf(SandboxRegistry sandbox)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(sandbox.RunRootPath);
        return key?.GetValue(MultiName) as string[];
    }

    private static string TabSequence(UIA3Automation automation, int count) =>
        ClassicDialogTests.TabSequence(automation, count);

    private static void Seed(SandboxRegistry sandbox)
    {
        sandbox.SetValueOfKind(string.Empty, MultiName, MultiStrings, RegistryValueKind.MultiString);
        // Printable ASCII, so the ASCII column has glyphs whose text rectangles carry strong pixels for the contrast check.
        sandbox.SetBinary(string.Empty, BinaryName, Enumerable.Range(0, 0x30).Select(index => (byte)(0x41 + index)).ToArray());
    }
}

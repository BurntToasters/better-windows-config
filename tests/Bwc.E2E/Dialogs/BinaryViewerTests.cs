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
/// Drives View > Display Binary Data on a seeded 20 byte REG_BINARY value and checks the classic
/// RT_DIALOG 2021 layout: the caption, the template rectangles, the read-only list rows for each of the
/// Byte, Word, Dword, and Qword formats (switched with the mouse-free Alt+letter mnemonics), the value
/// type, Escape, and the contrast of the client pixels. Each format is saved as a PNG under
/// artifacts/e2e/visual/dialogs.
/// </summary>
[TestClass]
public sealed class BinaryViewerTests
{
    private const string BinaryName = "E2EBinary";
    private const string ListId = "HexViewerList";
    private const string DialogRootId = "DialogRoot";
    private const string ByteId = "FormatByte";
    private const string WordId = "FormatWord";
    private const string DwordId = "FormatDword";
    private const string QwordId = "FormatQword";
    private const string TypeTextId = "ValueTypeText";
    private const string Caption = "Binary Data";
    private const string ExpectedType = "REG_BINARY";
    private const int MenuView = 514;
    private const int MenuViewDisplayBinaryData = 679;
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The template rectangles of RT_DIALOG 2021 in DLU, in template order.</summary>
    private static readonly ClassicDialogTests.ControlRect[] Controls =
    [
        new("DataLabel", 502, 4, 6, 20, 8),
        new(ListId, 2022, 4, 15, 433, 71),
        new("FormatGroup", 503, 4, 92, 212, 35),
        new(ByteId, 2023, 24, 105, 30, 10),
        new(WordId, 2024, 64, 105, 35, 10),
        new(DwordId, 2025, 109, 105, 39, 10),
        new(QwordId, 2027, 158, 105, 39, 10),
        new("DialogPrimary", 1, 161, 137, 40, 14),
        new("ValueTypeLabel", 65535, 255, 105, 44, 8),
        new(TypeTextId, 2026, 303, 105, 58, 8),
    ];

    private static readonly byte[] Pattern = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();

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
    public void BinaryViewer_FollowsTheClassicLayoutAndFormats()
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
            E2EResults.Record("binary.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        try
        {
            sandbox.Cleanup();
            using (var root = sandbox.CreateSubKey(string.Empty))
            {
                root.SetValue(BinaryName, Pattern, RegistryValueKind.Binary);
            }

            string dataRoot = ClassicDialogTests.FreshDataRoot(config, "binary-viewer");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);
            using var automation = new UIA3Automation();

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "binary.sandbox.navigate");
            bool seeded = ControlTestSupport.WaitUntil(() => ClassicDialogTests.RowExists(session, BinaryName), UiTimeout);
            E2EResults.Record("binary.seed.row", seeded, $"list rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}].");
            if (!seeded)
            {
                Assert.Fail("The binary value was not listed.");
            }

            var artifacts = Path.Combine(config.RepoRoot, "artifacts", "e2e", "visual", "dialogs");
            Directory.CreateDirectory(artifacts);

            AutomationElement? row = ClassicDialogTests.OpenValueRow(session, sandbox, BinaryName, "binary.row");
            if (row is not null)
            {
                driver.OpenMenu(MenuView, MenuViewDisplayBinaryData);
                CheckDialog(session, driver, automation, artifacts);
            }

            var failures = E2EResults.FailedChecksSince(_checkStart);
            Assert.AreEqual(
                0,
                failures.Count,
                $"Binary viewer checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    private static void CheckDialog(AppSession session, EditingDriver driver, UIA3Automation automation, string artifacts)
    {
        bool opened = driver.WaitForDialog();
        AutomationElement? dialog = driver.WaitForElement(DialogRootId, UiTimeout);
        E2EResults.Record("binary.dialog", opened && dialog is not null, $"DialogRoot present={dialog is not null}.");
        if (dialog is null)
        {
            return;
        }

        IntPtr hwnd = ClassicDialogTests.DialogHandle(dialog);
        _ = ClassicDialogTests.EnsureForeground(hwnd);

        string caption = ClassicDialogTests.CaptionOf(automation, hwnd);
        E2EResults.Record("binary.caption", caption == Caption, $"window caption='{caption}'.");

        ClassicDialogTests.CheckGeometry("binary.geometry", dialog, hwnd, Controls, 440, 161);
        DialogContrast.Check("binary.contrast", ClassicDialogTests.WindowOf(dialog), hwnd, DialogContrast.FaceForSystem());

        AutomationElement? list = dialog.FindFirstDescendant(cf => cf.ByAutomationId(ListId));
        string type = driver.TextOf(TypeTextId);
        E2EResults.Record("binary.type", type == ExpectedType, $"Value Type='{type}'.");

        CheckRows(list, "binary.byte", "0000  00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F", "0010  10 11 12 13");
        Save(dialog, artifacts, "binary-byte.png", "binary.byte.shot");

        ClassicDialogTests.SendAltLetter('W');
        bool word = ControlTestSupport.WaitUntil(() => ControlTestSupport.IsSelected(driver.WaitForElement(WordId, TimeSpan.FromMilliseconds(300))), TimeSpan.FromSeconds(3));
        CheckRows(list, "binary.word", "0000  0100 0302 0504 0706 0908 0B0A 0D0C 0F0E", "0010  1110 1312", word);
        Save(dialog, artifacts, "binary-word.png", "binary.word.shot");

        ClassicDialogTests.SendAltLetter('D');
        bool dword = ControlTestSupport.WaitUntil(() => ControlTestSupport.IsSelected(driver.WaitForElement(DwordId, TimeSpan.FromMilliseconds(300))), TimeSpan.FromSeconds(3));
        CheckRows(list, "binary.dword", "0000  03020100 07060504 0B0A0908 0F0E0D0C", "0010  13121110", dword);
        Save(dialog, artifacts, "binary-dword.png", "binary.dword.shot");

        ClassicDialogTests.SendAltLetter('Q');
        bool qword = ControlTestSupport.WaitUntil(() => ControlTestSupport.IsSelected(driver.WaitForElement(QwordId, TimeSpan.FromMilliseconds(300))), TimeSpan.FromSeconds(3));
        CheckRows(list, "binary.qword", "0000  0706050403020100 0F0E0D0C0B0A0908", "0010  10111213", qword);
        Save(dialog, artifacts, "binary-qword.png", "binary.qword.shot");

        Keyboard.Type(VirtualKeyShort.ESCAPE);
        bool closed = driver.WaitForNoDialog();
        bool restored = ControlTestSupport.WaitUntil(() => session.IsEnabled, TimeSpan.FromSeconds(5));
        E2EResults.Record("binary.escape", closed && restored, $"Escape closed the viewer={closed}, owner enabled={restored}.");
    }

    /// <summary>
    /// Checks the first two list rows against the expected text. The format's check is recorded only when
    /// <paramref name="selected"/> is true (the radio took the mnemonic); otherwise the check fails.
    /// </summary>
    private static void CheckRows(AutomationElement? list, string checkId, string first, string second, bool selected = true)
    {
        IReadOnlyList<string> rows = list is null
            ? Array.Empty<string>()
            : list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).Select(item => item.Name).ToArray();
        bool ok = selected && rows.Count == 2 && rows[0] == first && rows[1] == second;
        E2EResults.Record(
            checkId,
            ok,
            $"selected={selected}; rows=[{string.Join(" | ", rows)}]; expected=[{first} | {second}].");
    }

    private static void Save(AutomationElement dialog, string artifacts, string fileName, string checkId)
    {
        string path = ClassicDialogTests.SaveScreenshot(dialog, Path.Combine(artifacts, fileName));
        E2EResults.Record(checkId, File.Exists(path), $"saved '{path}'.");
    }
}

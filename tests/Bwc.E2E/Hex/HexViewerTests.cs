using System.Globalization;
using System.Text;
using Bwc.E2E.Controls;
using Bwc.E2E.Dialogs;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Hex;

/// <summary>
/// Drives View > Display Binary Data on a seeded 300 byte REG_BINARY value and a REG_SZ value through the
/// real menu and modal dialog. The classic RT_DIALOG 2021 viewer is checked for its caption, Value Type, the
/// read-only list rows in each of the Byte, Word, Dword, and Qword formats (switched with Alt+letter), OK and
/// Escape closing it without changing the value, and the contrast of the client pixels. Each view is saved
/// as a PNG under artifacts/e2e/visual/dialogs.
/// </summary>
[TestClass]
public sealed class HexViewerTests
{
    private const int MenuView = 514;
    private const int MenuViewDisplayBinaryData = 679;
    private const string PatternValueName = "Pattern300";
    private const string StringValueName = "HexString";
    private const string StringText = "HelloHex";
    private const int BytesPerRow = 16;
    private const string ListId = "HexViewerList";
    private const string TypeTextId = "ValueTypeText";
    private const string DialogRootId = "DialogRoot";
    private const string PrimaryId = "DialogPrimary";
    private const string Caption = "Binary Data";
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Hex")]
    [TestCategory("E2EOnly")]
    public void HexViewer_ClassicBinaryDataFollowsTheFormats()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);

        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("hex.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        byte[] pattern = CreatePattern();
        var sandbox = new SandboxRegistry(config);
        string? dataRoot = null;

        try
        {
            sandbox.Cleanup();
            Seed(sandbox, pattern);
            dataRoot = CreateFreshDataRoot(config, "hex");

            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);
            using var automation = new UIA3Automation();

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            bool seededRows = ControlTestSupport.WaitUntil(
                () => ContainsAll(
                    ControlTestSupport.ListRowNames(session),
                    PatternValueName,
                    StringValueName),
                TimeSpan.FromSeconds(15));
            E2EResults.Record(
                "hex.navigate",
                navigated && seededRows,
                $"navigated={navigated}, seededRows={seededRows}, rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}].");

            CheckBinaryValue(session, driver, automation, pattern, sandbox);
            CheckStringValue(session, driver, automation, sandbox);

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            DeleteDataRoot(dataRoot);
        }
    }

    private static void CheckBinaryValue(
        AppSession session,
        EditingDriver driver,
        UIA3Automation automation,
        byte[] pattern,
        SandboxRegistry sandbox)
    {
        bool selected = SelectRow(session, PatternValueName);
        E2EResults.Record("hex.binary.select", selected, $"binary row selected={selected}.");

        driver.OpenMenu(MenuView, MenuViewDisplayBinaryData);
        bool dialog = driver.WaitForDialog();
        AutomationElement? root = driver.WaitForElement(DialogRootId, UiTimeout);
        E2EResults.Record("hex.binary.dialog", dialog && root is not null, $"DialogRoot present={root is not null}.");
        if (root is null)
        {
            return;
        }

        IntPtr hwnd = ClassicDialogTests.DialogHandle(root);
        _ = ClassicDialogTests.EnsureForeground(hwnd);

        string caption = ClassicDialogTests.CaptionOf(automation, hwnd);
        E2EResults.Record("hex.binary.caption", caption == Caption, $"window caption='{caption}'.");

        string type = driver.TextOf(TypeTextId);
        E2EResults.Record("hex.binary.type", type == "REG_BINARY", $"Value Type='{type}'.");

        AutomationElement? list = root.FindFirstDescendant(cf => cf.ByAutomationId(ListId));
        CheckRows(list, "hex.binary.byte", ExpectedRows(pattern, 1), selected: true);

        // The format radios switch the view. Alt+letter is the mnemonic, as in the classic dialog.
        SwitchFormat(driver, "FormatWord", 'W', 2, "hex.binary.word", list, pattern);
        SwitchFormat(driver, "FormatDword", 'D', 4, "hex.binary.dword", list, pattern);
        SwitchFormat(driver, "FormatQword", 'Q', 8, "hex.binary.qword", list, pattern);

        // Byte is the last step back, so the data view returns to the first format.
        SwitchFormat(driver, "FormatByte", 'B', 1, "hex.binary.byte-again", list, pattern);

        DialogContrast.Check("hex.binary.contrast", ClassicDialogTests.WindowOf(root), hwnd, DialogContrast.FaceForSystem());
        Save(root, "hex-viewer-byte.png", "hex.binary.shot");

        AutomationElement? ok = root.FindFirstDescendant(cf => cf.ByAutomationId(PrimaryId));
        bool clicked = ok is not null && TryClick(ok);
        bool closed = driver.WaitForNoDialog();
        bool unchanged = sandbox.ReadBinary(string.Empty, PatternValueName)?.SequenceEqual(pattern) == true;
        E2EResults.Record(
            "hex.binary.ok",
            clicked && closed && unchanged,
            $"OK clicked={clicked}, closed={closed}, value unchanged={unchanged}.");
    }

    private static void SwitchFormat(
        EditingDriver driver,
        string radioId,
        char mnemonic,
        int elementSize,
        string checkId,
        AutomationElement? list,
        byte[] pattern)
    {
        ClassicDialogTests.SendAltLetter(mnemonic);
        bool chosen = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.IsSelected(driver.WaitForElement(radioId, TimeSpan.FromMilliseconds(300))),
            TimeSpan.FromSeconds(3));
        CheckRows(list, checkId, ExpectedRows(pattern, elementSize), chosen);
    }

    private static void CheckStringValue(
        AppSession session,
        EditingDriver driver,
        UIA3Automation automation,
        SandboxRegistry sandbox)
    {
        bool selected = SelectRow(session, StringValueName);
        E2EResults.Record("hex.string.select", selected, $"string row selected={selected}.");

        driver.OpenMenu(MenuView);
        AutomationElement? command = driver.WaitForElement("MenuCommand_" + MenuViewDisplayBinaryData);
        bool enabled = command is not null && SafeIsEnabled(command);
        E2EResults.Record("hex.string.enabled", selected && enabled, $"selected={selected}, DisplayBinaryData enabled={enabled}.");
        if (!enabled)
        {
            return;
        }

        driver.Invoke("MenuCommand_" + MenuViewDisplayBinaryData);
        bool dialog = driver.WaitForDialog();
        AutomationElement? root = driver.WaitForElement(DialogRootId, UiTimeout);
        if (root is null)
        {
            E2EResults.Record("hex.string.viewer", false, $"dialog={dialog}, DialogRoot present=False.");
            return;
        }

        IntPtr hwnd = ClassicDialogTests.DialogHandle(root);
        _ = ClassicDialogTests.EnsureForeground(hwnd);
        string type = driver.TextOf(TypeTextId);

        // REG_SZ is shown as its UTF-16 bytes with the terminating null, so the view has 18 bytes.
        byte[] stringBytes = Encoding.Unicode.GetBytes(StringText + "\0");
        AutomationElement? list = root.FindFirstDescendant(cf => cf.ByAutomationId(ListId));
        CheckRows(list, "hex.string.viewer", ExpectedRows(stringBytes, 1), selected: true);
        E2EResults.Record("hex.string.type", type == "REG_SZ", $"Value Type='{type}'.");

        Keyboard.Press(VirtualKeyShort.ESCAPE);
        bool closed = driver.WaitForNoDialog();
        bool valueUnchanged = sandbox.ReadString(string.Empty, StringValueName) == StringText;
        E2EResults.Record("hex.string.close", closed && valueUnchanged, $"Escape closed={closed}, valueUnchanged={valueUnchanged}.");
    }

    private static IReadOnlyList<string> RealizedRowNames(AutomationElement? list) =>
        list is null
            ? Array.Empty<string>()
            : list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).Select(item => item.Name).ToArray();

    /// <summary>
    /// Compares the list rows with the expected rows exactly. The list must hold one row per 16 bytes, so a
    /// truncated or extra row fails the check.
    /// </summary>
    private static void CheckRows(AutomationElement? list, string checkId, string[] expected, bool selected)
    {
        // The rows are realized lazily, so UI Automation can show only the first row right after the dialog
        // opens. The check waits for the realized rows to match, then compares them exactly.
        IReadOnlyList<string> rows = RealizedRowNames(list);
        _ = ControlTestSupport.WaitUntil(
            () => (rows = RealizedRowNames(list)).Count == expected.Length,
            TimeSpan.FromSeconds(5));
        bool ok = selected && rows.Count == expected.Length && rows.Zip(expected).All(pair => pair.First == pair.Second);
        E2EResults.Record(
            checkId,
            ok,
            $"selected={selected}; rows={rows.Count} expected={expected.Length}; first=[{string.Join(" | ", rows.Take(2))}]; " +
            $"last=[{string.Join(" | ", rows.TakeLast(1))}]; expectedFirst=[{string.Join(" | ", expected.Take(2))}]; expectedLast=[{expected.LastOrDefault()}].");
    }

    /// <summary>
    /// The rows the classic viewer must show: an offset in hex, then the values of each element (little endian,
    /// zero padded to its width). A value cut off at the end of the data shows its bytes one by one.
    /// </summary>
    private static string[] ExpectedRows(byte[] data, int elementSize)
    {
        var rows = new List<string>();
        for (int rowStart = 0; rowStart < data.Length; rowStart += BytesPerRow)
        {
            int end = Math.Min(rowStart + BytesPerRow, data.Length);
            var row = new StringBuilder();
            row.Append(rowStart.ToString("X4", CultureInfo.InvariantCulture)).Append("  ");
            for (int at = rowStart; at < end; at += elementSize)
            {
                if (at > rowStart)
                {
                    row.Append(' ');
                }

                if (end - at >= elementSize)
                {
                    for (int b = elementSize - 1; b >= 0; b--)
                    {
                        row.Append(data[at + b].ToString("X2", CultureInfo.InvariantCulture));
                    }
                }
                else
                {
                    for (int b = at; b < end; b++)
                    {
                        row.Append(data[b].ToString("X2", CultureInfo.InvariantCulture));
                    }
                }
            }

            rows.Add(row.ToString());
        }

        return rows.ToArray();
    }

    private static void Save(AutomationElement root, string fileName, string checkId)
    {
        string artifacts = Path.Combine(GuiGate.Config.RepoRoot, "artifacts", "e2e", "visual", "dialogs");
        Directory.CreateDirectory(artifacts);
        string path = ClassicDialogTests.SaveScreenshot(root, Path.Combine(artifacts, fileName));
        E2EResults.Record(checkId, File.Exists(path), $"saved '{path}'.");
    }

    private static bool SelectRow(AppSession session, string name)
    {
        AutomationElement? row = ControlTestSupport.FindRowByName(
            session,
            ControlTestSupport.ListControl,
            ControlType.DataItem,
            name);
        ControlTestSupport.Select(row);
        return ControlTestSupport.WaitUntil(
            () => ControlTestSupport.IsSelected(ControlTestSupport.FindRowByName(
                session,
                ControlTestSupport.ListControl,
                ControlType.DataItem,
                name)),
            TimeSpan.FromSeconds(5));
    }

    private static byte[] CreatePattern()
    {
        byte[] pattern = new byte[300];
        for (var index = 0; index < pattern.Length; index++)
        {
            pattern[index] = (byte)(index & 0xFF);
        }

        return pattern;
    }

    private static void Seed(SandboxRegistry sandbox, byte[] pattern)
    {
        using var root = sandbox.CreateSubKey(string.Empty);
        root.SetValue(PatternValueName, pattern, RegistryValueKind.Binary);
        root.SetValue(StringValueName, StringText, RegistryValueKind.String);
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

    private static bool TryClick(AutomationElement element)
    {
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

    private static bool SafeIsEnabled(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            return element.IsEnabled;
        }
        catch (Exception)
        {
            return false;
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
            // The run subtree is already gone. A settings file held briefly by the exiting
            // application is left for the next scoped run.
        }
    }

    private static void FailIfNeeded()
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Hex checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}

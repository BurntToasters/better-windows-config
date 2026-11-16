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

namespace Bwc.E2E.Accessibility;

/// <summary>
/// UI Automation checks for the hex editor in Edit Binary Value. The editor exposes Grid (one row per eight bytes, the
/// offset, eight byte cells, and the ASCII cell) and Value (the bytes as hex text). A byte cell reads its hex value
/// through GridPattern.GetItem and ValuePattern. Failure modes: docs/failure-modes/accessibility.md section 3.
/// </summary>
[TestClass]
public sealed class HexAccessibilityTests
{
    private const string BinaryName = "AccHexBinary";
    private const string HexEditorId = "HexEditor";
    private const string PrimaryId = "DialogPrimary";
    private const string Caption = "Edit Binary Value";
    private const int ByteCount = 0x30;
    private const int RowCount = ByteCount / 8;
    private const int GridColumns = 10;

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Accessibility")]
    [TestCategory("Mutating")]
    public void HexEditor_ExposesGridTextAndByteCells()
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
            E2EResults.Record("a11y.hex.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        SandboxRegistry? sandbox = null;
        try
        {
            sandbox = ControlTestSupport.SeedRegistry(config, 0);
            sandbox.SetBinary(string.Empty, BinaryName, Enumerable.Range(0, ByteCount).Select(index => (byte)(0x41 + index)).ToArray());
            using AppSession session = AccessibilitySupport.Launch(config, sandbox, "a11y-hex");
            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "a11y.hex.navigate");
            using var driver = new EditingDriver(session);
            using var automation = new UIA3Automation();
            CheckEditor(session, driver, automation, sandbox);
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }

        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(0, failures.Count, $"Hex accessibility checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private static void CheckEditor(AppSession session, EditingDriver driver, UIA3Automation automation, SandboxRegistry sandbox)
    {
        AutomationElement? row = ClassicDialogTests.OpenValueRow(session, sandbox, BinaryName, "a11y.hex");
        bool opened = row is not null && ClassicDialogTests.OpenEditor(session, driver, row, "a11y.hex.open");
        if (!opened)
        {
            E2EResults.Record("a11y.hex.open-binary", false, "Modify did not open Edit Binary Value.");
            return;
        }

        AutomationElement dialog = driver.WaitForElement("DialogRoot")!;
        IntPtr hwnd = ClassicDialogTests.DialogHandle(dialog);
        _ = ClassicDialogTests.EnsureForeground(hwnd);
        string caption = ClassicDialogTests.CaptionOf(automation, hwnd);
        E2EResults.Record("a11y.hex.caption", caption == Caption, $"window caption='{caption}'.");

        AutomationElement? editor = driver.WaitForElement(HexEditorId, AccessibilitySupport.UiTimeout);
        string editorName = AccessibilitySupport.NameOf(editor);
        E2EResults.Record("a11y.hex.name", editorName.Length > 0, $"hex editor control type={AccessibilitySupport.ControlTypeOf(editor)}, name='{editorName}'.");
        if (editor is null)
        {
            return;
        }

        bool gridSupported = AccessibilitySupport.HasPattern(editor, e => e.Patterns.Grid.IsSupported);
        int rows = gridSupported ? editor.Patterns.Grid.Pattern.RowCount.Value : -1;
        int columns = gridSupported ? editor.Patterns.Grid.Pattern.ColumnCount.Value : -1;
        E2EResults.Record(
            "a11y.hex.grid",
            gridSupported && rows == RowCount && columns == GridColumns,
            $"Grid supported={gridSupported}, RowCount={rows} (expected {RowCount}), ColumnCount={columns} (expected {GridColumns}).");

        bool valueSupported = AccessibilitySupport.HasPattern(editor, e => e.Patterns.Value.IsSupported);
        string value = valueSupported ? TextOfValue(editor) : string.Empty;
        E2EResults.Record(
            "a11y.hex.value",
            valueSupported && value.StartsWith("41 42 43 44 45 46 47 48", StringComparison.Ordinal),
            $"Value supported={valueSupported}, starts with the first eight bytes={value.StartsWith("41 42 43 44 45 46 47 48", StringComparison.Ordinal)}.");

        CheckCells(driver, editor);
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        E2EResults.Record("a11y.hex.escape-closes", driver.WaitForNoDialog(), "Escape closed Edit Binary Value.");
    }

    /// <summary>Row 0 holds the offset, eight bytes, and the ASCII text. Each byte cell reads its hex value.</summary>
    private static void CheckCells(EditingDriver driver, AutomationElement editor)
    {
        AutomationElement? row0 = driver.WaitForElement("HexRow_0", AccessibilitySupport.UiTimeout);
        string rowName = AccessibilitySupport.NameOf(row0);
        E2EResults.Record(
            "a11y.hex.row-name",
            rowName.StartsWith("0000  41 42 43 44 45 46 47 48", StringComparison.Ordinal),
            $"row 0 name='{rowName}'.");

        AutomationElement? offsetCell = CellAt(editor, 0, 0);
        AutomationElement? byteCell = CellAt(editor, 0, 1);
        AutomationElement? lastByteCell = CellAt(editor, 0, 8);
        AutomationElement? asciiCell = CellAt(editor, 0, 9);

        string byteValue = byteCell is null ? string.Empty : TextOfValue(byteCell);
        bool byteOk = AccessibilitySupport.NameOf(byteCell) == "41" && byteValue == "41";
        E2EResults.Record(
            "a11y.hex.byte-cell",
            byteOk,
            $"GetItem(0,1) name='{AccessibilitySupport.NameOf(byteCell)}', value='{byteValue}', expected 41.");
        E2EResults.Record(
            "a11y.hex.offset-cell",
            AccessibilitySupport.NameOf(offsetCell) == "0000",
            $"GetItem(0,0) name='{AccessibilitySupport.NameOf(offsetCell)}', expected 0000.");
        E2EResults.Record(
            "a11y.hex.last-byte-cell",
            AccessibilitySupport.NameOf(lastByteCell) == "48",
            $"GetItem(0,8) name='{AccessibilitySupport.NameOf(lastByteCell)}', expected 48.");
        E2EResults.Record(
            "a11y.hex.ascii-cell",
            AccessibilitySupport.NameOf(asciiCell) == "ABCDEFGH",
            $"GetItem(0,9) name='{AccessibilitySupport.NameOf(asciiCell)}', expected ABCDEFGH.");
    }

    private static AutomationElement? CellAt(AutomationElement editor, int row, int column)
    {
        try
        {
            return editor.Patterns.Grid.Pattern.GetItem(row, column);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string TextOfValue(AutomationElement element)
    {
        try
        {
            return element.Patterns.Value.Pattern.Value.Value ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}

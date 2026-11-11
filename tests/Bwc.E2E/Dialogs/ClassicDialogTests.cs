using System.Runtime.InteropServices;
using Bwc.E2E.ContextMenus;
using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Dialogs;

/// <summary>
/// Drives the classic Edit String, Edit DWORD dialogs through real input (Enter on a selected value
/// row) and checks them against the classic templates: no heading, control rectangles within 1 DIP
/// of the template converted with MapDialogRect at the dialog DPI, tab order, Alt+mnemonic focus,
/// Enter and Escape, owner disabled while open, and the Base radio conversion. Each dialog is saved
/// to artifacts/e2e/visual/dialogs. Each dialog's client area is also read as pixels (<see cref="DialogContrast"/>),
/// so text drawn in the face color fails even when UI Automation reports the text.
/// </summary>
[TestClass]
public sealed class ClassicDialogTests
{
    private const string StringName = "E2EString";
    private const string DwordName = "E2EDword";
    private const string ListControl = "ListControl";
    private const string DialogRootId = "DialogRoot";
    private const string ValueInput = "ValueEditorInput";
    private const string PrimaryId = "DialogPrimary";
    private const string SecondaryId = "DialogSecondary";
    private const string NameInputId = "ValueNameInput";
    private const string HexId = "BaseHex";
    private const string DecId = "BaseDec";
    private const string HeadingId = "DialogHeading";
    private const string DefaultName = "(Default)";
    private const string ZebraName = "ZebraString";
    private const int MenuEdit = 513;
    private const int MenuEditDelete = 913;
    private const double GlyphDip = 13;
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);

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
    public void ClassicDialogs_EditStringAndDwordFollowTheTemplate()
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
            E2EResults.Record("dialogs.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        try
        {
            sandbox.Cleanup();
            Seed(sandbox);

            string dataRoot = FreshDataRoot(config, "dialogs");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);
            using var automation = new UIA3Automation();

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EResults.Record("dialogs.navigate", navigated, $"navigated to '{sandbox.RunRootDisplayPath}'={navigated}.");
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "dialogs.sandbox.navigate");

            bool seeded = ControlTestSupport.WaitUntil(
                () => RowExists(session, StringName) && RowExists(session, DwordName),
                UiTimeout);
            E2EResults.Record(
                "dialogs.seed.rows",
                seeded,
                $"list rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}].");
            if (!seeded)
            {
                Assert.Fail("The sandbox values were not listed.");
            }

            var artifacts = Path.Combine(config.RepoRoot, "artifacts", "e2e", "visual", "dialogs");
            Directory.CreateDirectory(artifacts);

            CheckEditString(session, driver, automation, sandbox, artifacts);
            CheckEditDword(session, driver, automation, sandbox, artifacts);
            CheckDefaultValue(session, driver, sandbox);
            CheckConfirmMessageBox(session, driver, sandbox, artifacts);

            var failures = E2EResults.FailedChecksSince(_checkStart);
            Assert.AreEqual(
                0,
                failures.Count,
                $"Classic dialog checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    private static void CheckEditString(
        AppSession session,
        EditingDriver driver,
        UIA3Automation automation,
        SandboxRegistry sandbox,
        string artifacts)
    {
        AutomationElement? row = OpenValueRow(session, sandbox, StringName, "dialogs.string");
        bool opened = row is not null && OpenEditor(session, driver, row, "dialogs.string.open");
        if (!opened)
        {
            E2EResults.Record("dialogs.string.open", false, "Enter on the selected row did not open the editor.");
            return;
        }

        AutomationElement dialog = driver.WaitForElement(DialogRootId)!;
        IntPtr hwnd = DialogHandle(dialog);
        bool foreground = EnsureForeground(hwnd);

        string caption = CaptionOf(automation, hwnd);
        E2EResults.Record(
            "dialogs.string.caption",
            caption == "Edit String",
            $"window caption='{caption}', expected 'Edit String'.");

        bool ownerDisabled = !session.IsEnabled;
        E2EResults.Record(
            "dialogs.string.owner-disabled",
            ownerDisabled,
            $"main window enabled while the dialog is open={session.IsEnabled}.");

        bool noHeading = driver.WaitForElement(HeadingId, TimeSpan.FromMilliseconds(300)) is null;
        E2EResults.Record(
            "dialogs.string.no-heading",
            noHeading,
            $"DialogHeading present={!noHeading}.");

        bool focused = ControlTestSupport.WaitUntil(() => FocusedId(automation) == ValueInput, TimeSpan.FromSeconds(3));
        string tabs = TabSequence(automation, 4);
        string expectedTabs = $"{PrimaryId},{SecondaryId},{NameInputId},{ValueInput}";
        E2EResults.Record(
            "dialogs.string.focus-and-tab-order",
            focused && tabs == expectedTabs,
            $"initial focus ok={focused}; tab sequence='{tabs}', expected '{expectedTabs}', foreground={foreground}.");

        // Geometry is measured with the default button focused. A focused text box is drawn with its focus visual,
        // which WinUI adds to the control's reported rectangle, so the value box is measured unfocused.
        bool onPrimary = FocusAutomationId(automation, PrimaryId);
        CheckGeometry("dialogs.string.geometry", dialog, hwnd, EditStringControls, 252, 84);
        E2EResults.Record("dialogs.string.geometry-focus", onPrimary, $"default button focused for the measurement={onPrimary}.");
        CheckValueName(driver, StringName, "dialogs.string.value-name");

        bool altName = SendAltLetter('N') && ControlTestSupport.WaitUntil(
            () => FocusedId(automation) == NameInputId,
            TimeSpan.FromSeconds(2));
        bool altValue = SendAltLetter('V') && ControlTestSupport.WaitUntil(
            () => FocusedId(automation) == ValueInput,
            TimeSpan.FromSeconds(2));
        E2EResults.Record(
            "dialogs.string.mnemonics",
            altName && altValue,
            $"Alt+N focused the value name={altName}; Alt+V focused the value data={altValue}.");

        string screenshot = SaveScreenshot(dialog, Path.Combine(artifacts, "edit-string.png"));
        E2EResults.Record("dialogs.string.screenshot", File.Exists(screenshot), $"saved '{screenshot}'.");
        DialogContrast.Check("dialogs.string.contrast", WindowOf(dialog), hwnd, DialogContrast.FaceForSystem());

        _ = SendAltLetter('V');
        bool typed = ControlTestSupport.WaitUntil(() => FocusedId(automation) == ValueInput, TimeSpan.FromSeconds(2));
        SelectAllAndType("E2E-CANCELLED");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        bool escClosed = driver.WaitForNoDialog();
        bool escRestored = ControlTestSupport.WaitUntil(() => session.IsEnabled, TimeSpan.FromSeconds(5));
        bool unchangedByEscape = sandbox.ReadString(string.Empty, StringName) == "alpha";
        E2EResults.Record(
            "dialogs.string.escape-cancels",
            typed && escClosed && unchangedByEscape && escRestored,
            $"typed={typed}, closed={escClosed}, value unchanged={unchangedByEscape}, owner enabled after close={escRestored}.");

        AutomationElement? reopened = OpenValueRow(session, sandbox, StringName, "dialogs.string.reopen");
        bool reopenedDialog = reopened is not null && OpenEditor(session, driver, reopened, "dialogs.string.reopen.open");
        if (!reopenedDialog)
        {
            E2EResults.Record("dialogs.string.reopen.open", false, "Enter did not reopen Edit String.");
            return;
        }

        AutomationElement reopenedRoot = driver.WaitForElement(DialogRootId)!;
        _ = EnsureForeground(DialogHandle(reopenedRoot));
        _ = SendAltLetter('V');
        ControlTestSupport.WaitUntil(() => FocusedId(automation) == ValueInput, TimeSpan.FromSeconds(2));
        SelectAllAndType("E2E-SAVED");
        Keyboard.Type(VirtualKeyShort.RETURN);
        bool enterClosed = driver.WaitForNoDialog();
        bool saved = ControlTestSupport.WaitUntil(
            () => sandbox.ReadString(string.Empty, StringName) == "E2E-SAVED",
            TimeSpan.FromSeconds(10));
        bool ownerBack = ControlTestSupport.WaitUntil(() => session.IsEnabled, TimeSpan.FromSeconds(5));
        E2EResults.Record(
            "dialogs.string.enter-saves",
            enterClosed && saved && ownerBack,
            $"closed={enterClosed}, saved={saved}, owner enabled={ownerBack}.");
    }

    private static void CheckEditDword(
        AppSession session,
        EditingDriver driver,
        UIA3Automation automation,
        SandboxRegistry sandbox,
        string artifacts)
    {
        AutomationElement? row = OpenValueRow(session, sandbox, DwordName, "dialogs.dword");
        bool opened = row is not null && OpenEditor(session, driver, row, "dialogs.dword.open");
        if (!opened)
        {
            E2EResults.Record("dialogs.dword.open", false, "Enter on the selected DWORD row did not open the editor.");
            return;
        }

        AutomationElement dialog = driver.WaitForElement(DialogRootId)!;
        IntPtr hwnd = DialogHandle(dialog);
        _ = EnsureForeground(hwnd);

        string caption = CaptionOf(automation, hwnd);
        E2EResults.Record(
            "dialogs.dword.caption",
            caption == "Edit DWORD (32-bit) Value",
            $"window caption='{caption}'.");

        bool noHeading = driver.WaitForElement(HeadingId, TimeSpan.FromMilliseconds(300)) is null;
        E2EResults.Record("dialogs.dword.no-heading", noHeading, $"DialogHeading present={!noHeading}.");

        string hexText = driver.TextOf(ValueInput);
        AutomationElement? hex = driver.WaitForElement(HexId, TimeSpan.FromSeconds(2));
        bool hexSelected = ControlTestSupport.IsSelected(hex);
        E2EResults.Record(
            "dialogs.dword.hex-default",
            hexSelected && hexText == "1388",
            $"Hexadecimal selected={hexSelected}, shown value='{hexText}', expected '1388' for 5000.");

        string tabs = TabSequence(automation, 5);
        string expectedTabs = $"{HexId},{PrimaryId},{SecondaryId},{NameInputId},{ValueInput}";
        E2EResults.Record(
            "dialogs.dword.tab-order",
            tabs == expectedTabs,
            $"tab sequence='{tabs}', expected '{expectedTabs}'.");

        // Measured with the default button focused, for the same reason as the Edit String check.
        bool onPrimary = FocusAutomationId(automation, PrimaryId);
        CheckGeometry("dialogs.dword.geometry", dialog, hwnd, DwordControls, 219, 103);
        E2EResults.Record("dialogs.dword.geometry-focus", onPrimary, $"default button focused for the measurement={onPrimary}.");
        CheckValueName(driver, DwordName, "dialogs.dword.value-name");
        CheckGlyphs(driver, hwnd, "dialogs.dword.glyph-inside");

        string screenshot = SaveScreenshot(dialog, Path.Combine(artifacts, "edit-dword.png"));
        E2EResults.Record("dialogs.dword.screenshot", File.Exists(screenshot), $"saved '{screenshot}'.");
        DialogContrast.Check("dialogs.dword.contrast", WindowOf(dialog), hwnd, DialogContrast.FaceForSystem());

        _ = SendAltLetter('D');
        bool decSelected = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.IsSelected(driver.WaitForElement(DecId, TimeSpan.FromSeconds(1))),
            TimeSpan.FromSeconds(2));
        string decText = ControlTestSupport.WaitUntil(() => driver.TextOf(ValueInput) == "5000", TimeSpan.FromSeconds(2))
            ? "5000"
            : driver.TextOf(ValueInput);
        E2EResults.Record(
            "dialogs.dword.base-decimal",
            decSelected && decText == "5000",
            $"Decimal selected={decSelected}, shown value='{decText}', expected '5000' for 0x1388.");

        string decimalShot = SaveScreenshot(dialog, Path.Combine(artifacts, "edit-dword-decimal.png"));
        E2EResults.Record("dialogs.dword.screenshot-decimal", File.Exists(decimalShot), $"saved '{decimalShot}'.");

        _ = SendAltLetter('H');
        bool backHex = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.IsSelected(driver.WaitForElement(HexId, TimeSpan.FromSeconds(1))) &&
                driver.TextOf(ValueInput) == "1388",
            TimeSpan.FromSeconds(3));
        E2EResults.Record(
            "dialogs.dword.base-hexadecimal",
            backHex,
            $"Hexadecimal restored shows '{driver.TextOf(ValueInput)}', expected '1388'.");

        _ = SendAltLetter('D');
        ControlTestSupport.WaitUntil(() => driver.TextOf(ValueInput) == "5000", TimeSpan.FromSeconds(2));
        _ = SendAltLetter('V');
        ControlTestSupport.WaitUntil(() => FocusedId(automation) == ValueInput, TimeSpan.FromSeconds(2));
        SelectAllAndType("42");
        Keyboard.Type(VirtualKeyShort.RETURN);
        bool closed = driver.WaitForNoDialog();
        bool saved = ControlTestSupport.WaitUntil(
            () => sandbox.ReadDword(string.Empty, DwordName) == 42,
            TimeSpan.FromSeconds(10));
        E2EResults.Record(
            "dialogs.dword.enter-saves-decimal",
            closed && saved,
            $"closed={closed}, saved 42 in decimal={saved}, stored={sandbox.ReadDword(string.Empty, DwordName)}.");
    }

    /// <summary>The template rows in template order, with the numbers copied from the oracle (DLU).</summary>
    private static readonly ControlRect[] EditStringControls =
    [
        new("ValueDataLabel", 65535, 6, 35, 161, 8),
        new(ValueInput, 1001, 6, 46, 240, 12),
        new(PrimaryId, 1, 142, 64, 50, 14),
        new(SecondaryId, 2, 196, 64, 50, 14),
        new("ValueNameLabel", 65535, 6, 6, 134, 8),
        new(NameInputId, 1000, 6, 17, 240, 12),
    ];

    private static readonly ControlRect[] DwordControls =
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

    /// <summary>
    /// Compares every control rectangle with the template converted by MapDialogRect on a hidden
    /// dialog built from the same font, at the dialog's DPI. The tolerance is one DIP.
    /// </summary>
    private static void CheckGeometry(
        string checkId,
        AutomationElement dialog,
        IntPtr hwnd,
        ControlRect[] controls,
        int widthDlu,
        int heightDlu)
    {
        uint dpi = TemplateGeometry.DpiOf(hwnd);
        double tolerance = Math.Max(1.0, dpi / 96.0);
        TemplateGeometry.Rect client = TemplateGeometry.Map(0, 0, widthDlu, heightDlu, out TemplateGeometry.Rect[] mapped, controls);

        // The DialogRoot element's rectangle is inset from the window client area, so the client
        // rectangle of the window is the reference for the template origin and size.
        System.Drawing.Rectangle root = TemplateGeometry.ClientScreen(hwnd);
        var deviations = new List<string>();
        double worst = 0;
        bool allWithin = true;

        int clientWidth = client.Right - client.Left;
        int clientHeight = client.Bottom - client.Top;
        bool sizeOk = Math.Abs(root.Width - clientWidth) <= tolerance && Math.Abs(root.Height - clientHeight) <= tolerance;
        if (!sizeOk)
        {
            allWithin = false;
            deviations.Add($"client {root.Width}x{root.Height} px, template {clientWidth}x{clientHeight} px");
        }

        for (int i = 0; i < controls.Length; i++)
        {
            ControlRect control = controls[i];
            AutomationElement? element = dialog.FindFirstDescendant(cf => cf.ByAutomationId(control.AutomationId));
            if (element is null)
            {
                allWithin = false;
                deviations.Add($"{control.AutomationId} missing");
                continue;
            }

            var actual = element.BoundingRectangle;
            TemplateGeometry.Rect want = mapped[i];
            double dl = Math.Abs(actual.Left - (root.Left + want.Left));
            double dt = Math.Abs(actual.Top - (root.Top + want.Top));
            double dr = Math.Abs(actual.Right - (root.Left + want.Right));
            double db = Math.Abs(actual.Bottom - (root.Top + want.Bottom));
            double largest = Math.Max(Math.Max(dl, dt), Math.Max(dr, db));
            worst = Math.Max(worst, largest);
            if (largest > tolerance)
            {
                allWithin = false;
                deviations.Add(
                    $"{control.AutomationId} actual [{actual.Left - root.Left},{actual.Top - root.Top},{actual.Right - root.Left},{actual.Bottom - root.Top}] " +
                    $"template [{want.Left},{want.Top},{want.Right},{want.Bottom}] px");
            }
        }

        E2EResults.Record(
            checkId,
            allWithin,
            $"dpi={dpi}, tolerance={tolerance:0.0} px, worst deviation={worst:0.0} px, controls={controls.Length}. " +
            (deviations.Count == 0 ? "All within tolerance." : string.Join("; ", deviations)));
    }

    private static AutomationElement? OpenValueRow(AppSession session, SandboxRegistry sandbox, string name, string checkId)
    {
        AutomationElement? row = ControlTestSupport.FindRowByName(
            session,
            ListControl,
            FlaUI.Core.Definitions.ControlType.DataItem,
            name);
        if (row is null)
        {
            E2EResults.Record($"{checkId}.row", false, $"list row '{name}' was not found.");
            return null;
        }

        row.Click();
        E2EGuards.RequireListRowSelected(session, name, $"{checkId}.row-selected");
        E2EGuards.RequireSandboxSelection(session, sandbox, $"{checkId}.sandbox");
        return row;
    }

    /// <summary>Enter on the selected row activates the value editor, the same path a double click takes.</summary>
    /// <summary>
    /// Opens the value editor with Edit > Modify from the value's context menu (command 912), the same
    /// real-input path the classic Modify command uses. The row must already be selected.
    /// </summary>
    private static bool OpenEditor(AppSession session, EditingDriver driver, AutomationElement row, string checkId)
    {
        _ = ControlTestSupport.EnsureForeground(session);
        using var menus = new ContextMenuDriver(session);
        bool menu = menus.RightClick(row) && menus.WaitForMenu() is not null;
        bool invoked = menu && menus.Invoke(912);
        bool open = invoked && driver.WaitForDialog();
        E2EResults.Record(checkId, open, $"context menu={menu}, Modify invoked={invoked}, dialog root present={open}.");
        return open;
    }

    /// <summary>Sends Alt+letter to the foreground dialog. The letter is the mnemonic under test.</summary>
    private static bool SendAltLetter(char letter)
    {
        try
        {
            Keyboard.TypeSimultaneously(VirtualKeyShort.ALT, Enum.Parse<VirtualKeyShort>("KEY_" + char.ToUpperInvariant(letter)));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void SelectAllAndType(string text)
    {
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(text);
    }

    /// <summary>Presses Tab until the focused element has the automation id, at most five presses.</summary>
    private static bool FocusAutomationId(UIA3Automation automation, string automationId)
    {
        for (int i = 0; i < 5; i++)
        {
            if (FocusedId(automation) == automationId)
            {
                return true;
            }

            Keyboard.Type(VirtualKeyShort.TAB);
            _ = ControlTestSupport.WaitUntil(() => FocusedId(automation) != string.Empty, TimeSpan.FromMilliseconds(300));
        }

        return FocusedId(automation) == automationId;
    }

    /// <summary>Presses Tab <paramref name="count"/> times and returns the focused automation id after each press.</summary>
    private static string TabSequence(UIA3Automation automation, int count)
    {
        var names = new List<string>();
        for (int i = 0; i < count; i++)
        {
            string before = FocusedId(automation);
            Keyboard.Type(VirtualKeyShort.TAB);
            _ = ControlTestSupport.WaitUntil(() => FocusedId(automation) != before, TimeSpan.FromSeconds(1));
            names.Add(FocusedId(automation));
        }

        return string.Join(",", names);
    }

    private static string FocusedId(UIA3Automation automation)
    {
        try
        {
            return automation.FocusedElement()?.AutomationId ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string CaptionOf(UIA3Automation automation, IntPtr hwnd)
    {
        try
        {
            return automation.FromHandle(hwnd).Name ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>The HWND of the top-level dialog window. DialogRoot has no handle, so the Window ancestor is used.</summary>
    private static IntPtr DialogHandle(AutomationElement dialog)
    {
        AutomationElement? current = dialog;
        for (int depth = 0; current is not null && depth < 32; depth++)
        {
            try
            {
                if (current.ControlType == ControlType.Window)
                {
                    return current.Properties.NativeWindowHandle.Value;
                }
            }
            catch (Exception)
            {
                // This ancestor has no handle. The next one is tried.
            }

            current = current.Parent;
        }

        return IntPtr.Zero;
    }

    private static bool EnsureForeground(IntPtr hwnd)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (Win32.IsForeground(hwnd))
            {
                return true;
            }

            _ = Win32.SetForegroundWindow(hwnd);
            if (ControlTestSupport.WaitUntil(() => Win32.IsForeground(hwnd), TimeSpan.FromMilliseconds(500)))
            {
                return true;
            }
        }

        return Win32.IsForeground(hwnd);
    }

    /// <summary>Saves the whole dialog window, including its caption and frame, as a PNG.</summary>
    private static string SaveScreenshot(AutomationElement dialog, string path)
    {
        AutomationElement window = WindowOf(dialog);
        using (var image = Capture.Element(window))
        {
            image.ToFile(path);
        }

        return path;
    }

    private static AutomationElement WindowOf(AutomationElement dialog)
    {
        AutomationElement? current = dialog;
        for (int depth = 0; current is not null && depth < 32; depth++)
        {
            try
            {
                if (current.ControlType == ControlType.Window)
                {
                    return current;
                }
            }
            catch (Exception)
            {
                // The ancestor is gone or has no control type. The next one is tried.
            }

            current = current.Parent;
        }

        return dialog;
    }

    private static bool RowExists(AppSession session, string name) =>
        ControlTestSupport.ListRowNames(session).Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Checks the value name box: it shows the expected name and is read-only, as in classic regedit.</summary>
    private static void CheckValueName(EditingDriver driver, string expected, string checkId)
    {
        AutomationElement? box = driver.WaitForElement(NameInputId, TimeSpan.FromSeconds(2));
        string shown = box is null ? string.Empty : driver.TextOf(NameInputId);
        bool readOnly = box is not null && IsReadOnly(box);
        E2EResults.Record(
            checkId,
            shown == expected && readOnly,
            $"value name box='{shown}', expected '{expected}', read-only={readOnly}.");
    }

    private static bool IsReadOnly(AutomationElement element)
    {
        try
        {
            return element.Patterns.Value.IsSupported && element.Patterns.Value.Pattern.IsReadOnly.Value;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the glyph report that each radio button publishes and checks that the glyph lies inside the control
    /// box, is 13 DIP at the window DPI, and is centered vertically.
    /// </summary>
    private static void CheckGlyphs(EditingDriver driver, IntPtr hwnd, string checkId)
    {
        double scale = TemplateGeometry.DpiOf(hwnd) / 96.0;
        double expected = GlyphDip * scale;
        var problems = new List<string>();
        int inspected = 0;
        foreach (string id in new[] { HexId, DecId })
        {
            AutomationElement? element = driver.WaitForElement(id, TimeSpan.FromSeconds(2));
            string help = element is null ? string.Empty : element.Properties.HelpText.Value ?? string.Empty;
            GlyphReport? report = GlyphReport.Parse(help);
            if (report is null)
            {
                problems.Add($"{id} has no glyph report ('{help}')");
                continue;
            }

            inspected++;
            GlyphReport glyph = report.Value;
            bool inside = glyph.Left >= 0 && glyph.Top >= 0 &&
                glyph.Right <= glyph.Width && glyph.Bottom <= glyph.Height &&
                glyph.Left < glyph.Right && glyph.Top < glyph.Bottom;
            bool sized = Math.Abs((glyph.Right - glyph.Left) - expected) <= 1.5 &&
                Math.Abs((glyph.Bottom - glyph.Top) - expected) <= 1.5;
            bool centered = Math.Abs((glyph.Top + glyph.Bottom) / 2.0 - glyph.Height / 2.0) <= 1.5;
            if (!inside || !sized || !centered)
            {
                problems.Add(
                    $"{id} glyph-px=[{glyph.Left},{glyph.Top},{glyph.Right},{glyph.Bottom}] " +
                    $"control-px={glyph.Width}x{glyph.Height} inside={inside} sized={sized} centered={centered}");
            }
        }

        E2EResults.Record(
            checkId,
            inspected == 2 && problems.Count == 0,
            $"scale={scale:0.00}, expected glyph {expected:0.0} px. " +
                (problems.Count == 0 ? "Both glyphs lie inside their control boxes." : string.Join("; ", problems)));
    }

    /// <summary>The glyph rectangle and control size in pixels, relative to the control.</summary>
    private readonly record struct GlyphReport(int Left, int Top, int Right, int Bottom, int Width, int Height)
    {
        /// <summary>Parses "glyph-px=L,T,R,B;control-px=W,H". Returns null for any other text.</summary>
        internal static GlyphReport? Parse(string help)
        {
            string[] parts = help.Split(';');
            if (parts.Length != 2 ||
                !parts[0].StartsWith("glyph-px=", StringComparison.Ordinal) ||
                !parts[1].StartsWith("control-px=", StringComparison.Ordinal))
            {
                return null;
            }

            int[] glyph = ParseInts(parts[0]["glyph-px=".Length..]);
            int[] control = ParseInts(parts[1]["control-px=".Length..]);
            if (glyph.Length != 4 || control.Length != 2)
            {
                return null;
            }

            return new GlyphReport(glyph[0], glyph[1], glyph[2], glyph[3], control[0], control[1]);
        }

        private static int[] ParseInts(string text) =>
            text.Split(',')
                .Select(part => int.TryParse(part, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value) ? value : int.MinValue)
                .Where(value => value != int.MinValue)
                .ToArray();
    }

    /// <summary>The unnamed value opens with "(Default)" as its value name, the classic name for the default value.</summary>
    private static void CheckDefaultValue(AppSession session, EditingDriver driver, SandboxRegistry sandbox)
    {
        AutomationElement? row = OpenValueRow(session, sandbox, DefaultName, "dialogs.default");
        bool opened = row is not null && OpenEditor(session, driver, row, "dialogs.default.open");
        if (!opened)
        {
            return;
        }

        CheckValueName(driver, DefaultName, "dialogs.default.value-name");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        bool closed = driver.WaitForNoDialog();
        bool kept = sandbox.ReadString(string.Empty, string.Empty) == "Default value text";
        E2EResults.Record("dialogs.default.escape", closed && kept, $"closed={closed}, default value unchanged={kept}.");
    }

    /// <summary>
    /// Opens the Delete confirmation (a question message box with Yes and No), saves it as a screenshot, and
    /// chooses No. The value must remain.
    /// </summary>
    private static void CheckConfirmMessageBox(
        AppSession session,
        EditingDriver driver,
        SandboxRegistry sandbox,
        string artifacts)
    {
        AutomationElement? row = OpenValueRow(session, sandbox, ZebraName, "dialogs.messagebox");
        bool opened = false;
        if (row is not null)
        {
            _ = ControlTestSupport.EnsureForeground(session);
            driver.OpenMenu(MenuEdit, MenuEditDelete);
            opened = driver.WaitForDialog();
        }

        E2EResults.Record("dialogs.messagebox.open", opened, $"Edit > Delete opened the confirmation={opened}.");
        if (!opened)
        {
            return;
        }

        AutomationElement dialog = driver.WaitForElement(DialogRootId)!;
        Thread.Sleep(400);
        string message = driver.TextOf("DialogMessage");
        string screenshot = SaveScreenshot(dialog, Path.Combine(artifacts, "message-box.png"));
        E2EResults.Record(
            "dialogs.messagebox.screenshot",
            File.Exists(screenshot) && message.Length > 0,
            $"message text='{message}', saved '{screenshot}'.");
        IntPtr hwnd = DialogHandle(dialog);
        _ = EnsureForeground(hwnd);
        DialogContrast.Check("dialogs.messagebox.contrast", WindowOf(dialog), hwnd, DialogContrast.FaceForSystem());

        bool chose = driver.Invoke("DialogSecondary");
        bool closed = driver.WaitForNoDialog();
        bool kept = sandbox.ReadString(string.Empty, ZebraName) == "zebra";
        E2EResults.Record(
            "dialogs.messagebox.no-keeps-value",
            chose && closed && kept,
            $"No invoked={chose}, closed={closed}, value kept={kept}.");
    }

    private static void Seed(SandboxRegistry sandbox)
    {
        using (var root = sandbox.CreateSubKey(string.Empty))
        {
            root.SetValue(StringName, "alpha", RegistryValueKind.String);
            root.SetValue(DwordName, 5000, RegistryValueKind.DWord);
            root.SetValue("ZebraString", "zebra", RegistryValueKind.String);
            root.SetValue(string.Empty, "Default value text", RegistryValueKind.String);
        }
    }

    private static string FreshDataRoot(E2EConfig config, string scope)
    {
        string path = config.DataRootFor(scope);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>One template control: its id, its automation id, and its rectangle in DLU.</summary>
    private sealed record ControlRect(string AutomationId, int Id, int X, int Y, int Cx, int Cy);

    /// <summary>
    /// Converts DLU rectangles with MapDialogRect. A hidden dialog is created from an in-memory
    /// template that uses the same face and size as the classic templates, so the base units are the
    /// ones Windows uses for the dialog font at this process DPI.
    /// </summary>
    private static class TemplateGeometry
    {
        private const string TemplateFace = "MS Shell Dlg";
        private const int TemplatePointSize = 8;
        private const uint WsPopupDsSetFont = 0x80000040;

        private delegate IntPtr DialogProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        private static readonly DialogProc NoOpProc = (_, _, _, _) => IntPtr.Zero;

        internal struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        internal static uint DpiOf(IntPtr hwnd) => GetDpiForWindow(hwnd);

        /// <summary>The window client area in screen pixels: its origin and its size.</summary>
        internal static System.Drawing.Rectangle ClientScreen(IntPtr hwnd)
        {
            var origin = new ScreenPoint { X = 0, Y = 0 };
            if (!ClientToScreen(hwnd, ref origin) || !GetClientRect(hwnd, out Win32.NativeRect client))
            {
                throw new InvalidOperationException("The dialog client area could not be read.");
            }

            return new System.Drawing.Rectangle(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ScreenPoint
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ClientToScreen(IntPtr hwnd, ref ScreenPoint point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr hwnd, out Win32.NativeRect rect);

        internal static Rect Map(int left, int top, int right, int bottom, out Rect[] mapped, ControlRect[] controls)
        {
            IntPtr dialog = CreateHiddenDialog();
            try
            {
                Rect client = MapRect(dialog, 0, 0, right, bottom);
                mapped = controls
                    .Select(control => MapRect(dialog, control.X, control.Y, control.X + control.Cx, control.Y + control.Cy))
                    .ToArray();
                return client;
            }
            finally
            {
                _ = DestroyWindow(dialog);
            }
        }

        private static Rect MapRect(IntPtr dialog, int left, int top, int right, int bottom)
        {
            var rect = new Rect { Left = left, Top = top, Right = right, Bottom = bottom };
            if (!MapDialogRect(dialog, ref rect))
            {
                throw new InvalidOperationException("MapDialogRect failed.");
            }

            return rect;
        }

        private static IntPtr CreateHiddenDialog()
        {
            byte[] template = BuildTemplate();
            IntPtr buffer = Marshal.AllocHGlobal(template.Length);
            try
            {
                Marshal.Copy(template, 0, buffer, template.Length);
                IntPtr proc = Marshal.GetFunctionPointerForDelegate(NoOpProc);
                IntPtr dialog = CreateDialogIndirectParamW(IntPtr.Zero, buffer, IntPtr.Zero, proc, IntPtr.Zero);
                if (dialog == IntPtr.Zero)
                {
                    throw new InvalidOperationException("CreateDialogIndirectParamW failed for the measurement template.");
                }

                return dialog;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static byte[] BuildTemplate()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(WsPopupDsSetFont);
            writer.Write(0u);
            writer.Write((ushort)0);
            writer.Write((short)0);
            writer.Write((short)0);
            writer.Write((short)0);
            writer.Write((short)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)TemplatePointSize);
            foreach (char letter in TemplateFace)
            {
                writer.Write((ushort)letter);
            }

            writer.Write((ushort)0);
            writer.Flush();
            return stream.ToArray();
        }

#pragma warning disable SYSLIB1054 // This test project does not allow unsafe code, so LibraryImport is not available.
        [DllImport("user32.dll", EntryPoint = "CreateDialogIndirectParamW", SetLastError = true)]
        private static extern IntPtr CreateDialogIndirectParamW(
            IntPtr instance,
            IntPtr template,
            IntPtr parent,
            IntPtr dialogProc,
            IntPtr initParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MapDialogRect(IntPtr dialog, ref Rect rect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);
#pragma warning restore SYSLIB1054
    }
}

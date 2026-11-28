using System.Runtime.InteropServices;
using System.Text.Json;
using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Bwc.E2E.Dialogs.ClassicDialogTests;

namespace Bwc.E2E.Dialogs;

/// <summary>
/// The 0.2.0 classic dialog additions (docs/failure-modes/classic-dialog.md, section 14): the opt-in resizable frame with a
/// minimum size, literal text, the numeric spin box and the editable grid. Each runs in a host dialog that exists only in
/// the E2E flavor (<c>E2EDialogHost</c>), opened over the Local Group Policy Editor window by the environment variable
/// <c>BWC_E2E_DIALOG_HOST</c>. The tests drive it with real keys and the mouse, read it through UI Automation, and read the
/// JSON report the dialog writes when it closes (artifacts/e2e/data/...; the run artifact holds every check).
/// </summary>
[TestClass]
public sealed class ClassicDialogAdditionsTests
{
    private const int GwlStyle = -16;
    private const long WsThickFrame = 0x00040000;
    private const long WsMaximizeBox = 0x00010000;
    private const long WsMinimizeBox = 0x00020000;
    private const uint WmNcHitTest = 0x0084;
    private const int HtBottomRight = 17;
    private const string DialogRootId = "DialogRoot";
    private const string PrimaryId = "DialogPrimary";
    private const string SecondaryId = "DialogSecondary";

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Dialogs")]
    [TestCategory("E2EOnly")]
    public void Additions_FixedDialogHasNoSizingBorder()
    {
        int start = E2EResults.CheckCount;
        using Host host = Host.Start("fixed");
        long style = StyleOf(host.Hwnd);
        E2EResults.Record(
            "additions.fixed.no-thickframe",
            (style & WsThickFrame) == 0,
            $"window style=0x{style:X}; WS_THICKFRAME must be absent for a dialog without the flag.");
        E2EResults.Record(
            "additions.fixed.no-sizing-corner",
            FindSizingCorner(host.Hwnd) is null,
            "No point near the bottom right corner hit-tests as a sizing corner.");

        Press(host, VirtualKeyShort.ESCAPE);
        E2EResults.Record("additions.fixed.closed", host.Driver.WaitForNoDialog(), "Escape closed the fixed dialog.");
        JsonElement report = host.Report();
        E2EResults.Record(
            "additions.fixed.report",
            report.GetProperty("state").GetString() == "closed" && !report.GetProperty("accepted").GetBoolean(),
            $"report={report.GetRawText().Replace(Environment.NewLine, " ")}");
        Finish(start, "fixed dialog");
    }

    [TestMethod]
    [TestCategory("Dialogs")]
    [TestCategory("E2EOnly")]
    public void Additions_ResizableDialogHonorsMinimumAndGrows()
    {
        int start = E2EResults.CheckCount;
        // The dialog is opened twice, once closed with OK and once with Escape, so the subclass is installed and removed twice.
        foreach (bool useOk in new[] { true, false })
        {
            string tag = useOk ? "ok" : "escape";
            using Host host = Host.Start("resize");
            long style = StyleOf(host.Hwnd);
            E2EResults.Record(
                $"additions.resize.{tag}.frame",
                (style & WsThickFrame) != 0 && (style & WsMaximizeBox) == 0 && (style & WsMinimizeBox) == 0,
                $"window style=0x{style:X}; WS_THICKFRAME present, no maximize or minimize box.");

            CheckGeometry(
                $"additions.resize.{tag}.geometry-open",
                host.Dialog,
                host.Hwnd,
                [
                    new("HostEdit", 1000, 7, 7, 186, 56),
                    new(PrimaryId, 1, 88, 69, 50, 14),
                    new(SecondaryId, 2, 143, 69, 50, 14),
                ],
                200,
                90);

            (int width, int height) = ClientSizeOf(host.Hwnd);
            int expectedMinWidth = (int)Math.Round(width * 160 / 200.0);
            int expectedMinHeight = (int)Math.Round(height * 70 / 90.0);
            AutomationElement? edit = host.Driver.WaitForElement("HostEdit");
            double editWidthBefore = edit?.BoundingRectangle.Width ?? 0;
            double cancelRightGap = Gap(host, SecondaryId);

            if (!EnsureForeground(host.Hwnd))
            {
                Assert.Fail("The resizable dialog is not the foreground window.");
            }

            System.Drawing.Point? corner = FindSizingCorner(host.Hwnd);
            E2EResults.Record($"additions.resize.{tag}.corner", corner is not null, $"sizing corner found={corner is not null}.");
            if (corner is null)
            {
                Assert.Fail("No sizing corner.");
            }

            // Shrink far past the minimum: the window must stop at the minimum client size.
            DragBy(corner.Value, -(width + 200), -(height + 200));
            (int shrunkWidth, int shrunkHeight) = ClientSizeOf(host.Hwnd);
            E2EResults.Record(
                $"additions.resize.{tag}.minimum",
                Math.Abs(shrunkWidth - expectedMinWidth) <= 2 && Math.Abs(shrunkHeight - expectedMinHeight) <= 2,
                $"client after shrinking {shrunkWidth}x{shrunkHeight} px, minimum expected {expectedMinWidth}x{expectedMinHeight} px (160x70 DLU), opened at {width}x{height} px.");

            // Grow: the client area and the anchored controls follow.
            System.Drawing.Point? grownCorner = FindSizingCorner(host.Hwnd);
            if (grownCorner is null)
            {
                E2EResults.Record($"additions.resize.{tag}.corner-again", false, "No sizing corner after shrinking.");
                Assert.Fail("No sizing corner after shrinking.");
            }

            DragBy(grownCorner.Value, 260, 160);
            (int grownWidth, int grownHeight) = ClientSizeOf(host.Hwnd);
            E2EResults.Record(
                $"additions.resize.{tag}.grows",
                grownWidth >= shrunkWidth + 200 && grownHeight >= shrunkHeight + 120,
                $"client after growing {grownWidth}x{grownHeight} px, before {shrunkWidth}x{shrunkHeight} px.");

            bool followed = ControlTestSupport.WaitUntil(
                () => Math.Abs(Gap(host, SecondaryId) - cancelRightGap) <= 3,
                TimeSpan.FromSeconds(3));
            E2EResults.Record(
                $"additions.resize.{tag}.anchored",
                followed,
                $"Cancel keeps its distance to the client right edge: before {cancelRightGap:0.0} px, after {Gap(host, SecondaryId):0.0} px.");
            AutomationElement? grownEdit = host.Driver.WaitForElement("HostEdit");
            double editWidthAfter = grownEdit?.BoundingRectangle.Width ?? 0;
            E2EResults.Record(
                $"additions.resize.{tag}.stretched",
                editWidthAfter > editWidthBefore - 1 && editWidthAfter >= grownWidth - 2 * 40,
                $"edit width {editWidthBefore:0} px at open, {editWidthAfter:0} px after growing (client {grownWidth} px).");

            if (useOk)
            {
                E2EResults.Record("additions.resize.ok.invoked", host.Driver.Invoke(PrimaryId), "OK invoked.");
            }
            else
            {
                Press(host, VirtualKeyShort.ESCAPE);
            }

            E2EResults.Record($"additions.resize.{tag}.closed", host.Driver.WaitForNoDialog(), "The dialog closed.");
            JsonElement report = host.Report();
            E2EResults.Record(
                $"additions.resize.{tag}.report",
                report.GetProperty("accepted").GetBoolean() == useOk,
                $"accepted={report.GetProperty("accepted").GetBoolean()}, expected {useOk}.");
            E2EResults.Record(
                $"additions.resize.{tag}.owner-enabled",
                ControlTestSupport.WaitUntil(() => host.Session.IsEnabled, TimeSpan.FromSeconds(3)),
                "The owner window is enabled and the process still answers after the resizable dialog closed.");
        }

        Finish(start, "resizable dialog");
    }

    [TestMethod]
    [TestCategory("Dialogs")]
    [TestCategory("E2EOnly")]
    public void Additions_LiteralTextKeepsAmpersand()
    {
        int start = E2EResults.CheckCount;
        using (Host host = Host.Start("literal"))
        {
            Names(host, ("LiteralLabel", "Fish && Chips &"), ("LiteralButton", "A&B"), ("LiteralCheckA", "R&D"), ("LiteralCheckB", "Q&D"));
            Names(host, ("MnemonicLabel", "Name:"), ("MnemonicCheck", "Dup"));

            if (!EnsureForeground(host.Hwnd))
            {
                Assert.Fail("The literal dialog is not the foreground window.");
            }

            // Alt+B would press the literal button "A&B" if its ampersand were an access key. Alt+D reaches only the real one.
            _ = SendAltLetter('B');
            Thread.Sleep(400);
            _ = SendAltLetter('D');
            bool toggled = ControlTestSupport.WaitUntil(() => ToggleOf(host, "MnemonicCheck") == "On", TimeSpan.FromSeconds(3));
            E2EResults.Record(
                "additions.literal.mnemonic-still-works",
                toggled,
                $"Alt+D toggled the non-literal '&Dup' check box={toggled}, state={ToggleOf(host, "MnemonicCheck")}.");
            E2EResults.Record(
                "additions.literal.literal-check-untouched",
                ToggleOf(host, "LiteralCheckA") == "Off" && ToggleOf(host, "LiteralCheckB") == "Off",
                $"Literal check boxes stay Off: A={ToggleOf(host, "LiteralCheckA")}, B={ToggleOf(host, "LiteralCheckB")}.");
            _ = SendAltLetter('N');
            bool onName = ControlTestSupport.WaitUntil(() => FocusedId(host.Automation) == "NameBox", TimeSpan.FromSeconds(3));
            E2EResults.Record("additions.literal.alt-n", onName, $"Alt+N focused the edit after '&Name:'={onName}.");

            Press(host, VirtualKeyShort.ESCAPE);
            E2EResults.Record("additions.literal.closed", host.Driver.WaitForNoDialog(), "Escape closed the literal dialog.");
            JsonElement report = host.Report();
            int[] commands = report.GetProperty("commands").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            E2EResults.Record(
                "additions.literal.no-command",
                commands.SequenceEqual([2]),
                $"commands=[{string.Join(",", commands)}]; only Cancel (2) may run, the literal button (100) must not.");
            E2EResults.Record(
                "additions.literal.report",
                report.GetProperty("mnemonicChecked").GetBoolean() && !report.GetProperty("literalCheckAChecked").GetBoolean(),
                $"mnemonicChecked={report.GetProperty("mnemonicChecked").GetBoolean()}, literalCheckAChecked={report.GetProperty("literalCheckAChecked").GetBoolean()}.");
        }

        // Two non-literal controls with one access key still throw.
        using (Host host = Host.Start("duplicate", expectDialog: false))
        {
            JsonElement report = host.Report();
            string error = report.GetProperty("error").GetString() ?? string.Empty;
            E2EResults.Record(
                "additions.literal.duplicate-throws",
                report.GetProperty("state").GetString() == "constructor-failed" &&
                report.GetProperty("exception").GetString() == nameof(InvalidOperationException) &&
                error.Contains("mnemonic 'A'", StringComparison.Ordinal),
                $"report state={report.GetProperty("state").GetString()}, exception={report.GetProperty("exception").GetString()}, error='{error}'.");
        }

        Finish(start, "literal text");
    }

    [TestMethod]
    [TestCategory("Dialogs")]
    [TestCategory("E2EOnly")]
    public void Additions_SpinnerRangeKeysAndText()
    {
        int start = E2EResults.CheckCount;
        using Host host = Host.Start("spinner");
        if (!EnsureForeground(host.Hwnd))
        {
            Assert.Fail("The spinner dialog is not the foreground window.");
        }

        CheckGeometry(
            "additions.spinner.geometry",
            host.Dialog,
            host.Hwnd,
            [
                new("MainLabel", 65535, 7, 9, 55, 8),
                new("SpinMain", 1100, 65, 7, 60, 12),
                new("BigLabel", 65535, 7, 27, 55, 8),
                new("SpinBig", 1101, 65, 25, 80, 12),
                new("OtherLabel", 65535, 7, 45, 55, 8),
                new("OtherEdit", 1000, 65, 43, 80, 12),
                new(PrimaryId, 1, 88, 69, 50, 14),
                new(SecondaryId, 2, 143, 69, 50, 14),
            ],
            200,
            90);

        AutomationElement main = host.Driver.WaitForElement("SpinMain")!;
        E2EResults.Record(
            "additions.spinner.automation",
            main.ControlType == ControlType.Spinner && main.Name == "Amount" &&
                main.Patterns.RangeValue.IsSupported &&
                main.Patterns.RangeValue.Pattern.Minimum.Value == 0 &&
                main.Patterns.RangeValue.Pattern.Maximum.Value == 100 &&
                main.Patterns.RangeValue.Pattern.SmallChange.Value == 5 &&
                main.Patterns.Value.IsSupported,
            $"type={main.ControlType}, name='{main.Name}', range support={main.Patterns.RangeValue.IsSupported}.");
        E2EResults.Record(
            "additions.spinner.parts",
            host.Driver.WaitForElement("SpinMain.Up", TimeSpan.FromSeconds(2))?.Name == "Increase" &&
                host.Driver.WaitForElement("SpinMain.Down", TimeSpan.FromSeconds(2))?.Name == "Decrease" &&
                host.Driver.WaitForElement("SpinMain.Text", TimeSpan.FromSeconds(2))?.Name == "Amount",
            "The buttons are named Increase and Decrease, and the text box carries the control name.");

        bool focused = ControlTestSupport.WaitUntil(() => FocusedId(host.Automation) == "SpinMain.Text", TimeSpan.FromSeconds(3));
        string tabs = TabSequence(host.Automation, 5);
        E2EResults.Record(
            "additions.spinner.tab-order",
            focused && tabs == "SpinBig.Text,OtherEdit,DialogPrimary,DialogSecondary,SpinMain.Text",
            $"initial focus on the first spin box={focused}; tab sequence='{tabs}'. Each spin box is one stop and the buttons are none.");

        // A label's access key moves the focus to the next tab stop, which for a spin box is its text box.
        _ = SendAltLetter('B');
        bool altBig = ControlTestSupport.WaitUntil(() => FocusedId(host.Automation) == "SpinBig.Text", TimeSpan.FromSeconds(2));
        _ = SendAltLetter('A');
        bool altMain = ControlTestSupport.WaitUntil(() => FocusedId(host.Automation) == "SpinMain.Text", TimeSpan.FromSeconds(2));
        E2EResults.Record(
            "additions.spinner.mnemonics",
            altBig && altMain,
            $"Alt+B focused the second spin box={altBig}; Alt+A focused the first={altMain}.");

        // The text of the focused spin box is selected after a key step, so typing replaces it. 10, step 5, range 0..100.
        Press(host, VirtualKeyShort.UP);
        Expect(host, "SpinMain", "15", "Up from 10");
        Press(host, VirtualKeyShort.DOWN);
        Press(host, VirtualKeyShort.DOWN);
        Expect(host, "SpinMain", "5", "Down twice");
        Press(host, VirtualKeyShort.DOWN);
        Press(host, VirtualKeyShort.DOWN);
        Expect(host, "SpinMain", "0", "Down at the minimum stays 0");
        SelectAllAndType("98");
        Press(host, VirtualKeyShort.UP);
        Expect(host, "SpinMain", "100", "Up from 98 clamps to the maximum");
        Press(host, VirtualKeyShort.UP);
        Expect(host, "SpinMain", "100", "Up at the maximum stays 100");
        Press(host, VirtualKeyShort.DOWN);
        Expect(host, "SpinMain", "95", "Down from 100");
        SelectAllAndType("43");
        Press(host, VirtualKeyShort.UP);
        Expect(host, "SpinMain", "48", "A step of 5 from 43 is 48, not a multiple of 5");

        // Typed text is kept as typed. A key on text that is not a number starts from the last valid value (48).
        SelectAllAndType("abc");
        Expect(host, "SpinMain", "abc", "Typed non-numeric text is kept");
        Press(host, VirtualKeyShort.UP);
        Expect(host, "SpinMain", "53", "Up from 'abc' starts from the last valid value 48");
        SelectAllAndType("5000");
        Expect(host, "SpinMain", "5000", "Typed out-of-range text is kept");

        // The second spin box has a range near the 32-bit limit. Its buttons work through UI Automation and keep the focus.
        _ = FocusAutomationId(host.Automation, "SpinBig.Text");
        bool invoked = host.Driver.Invoke("SpinBig.Up");
        Expect(host, "SpinBig", "4294967291", "The Up button from 4294967290");
        E2EResults.Record(
            "additions.spinner.button-focus",
            invoked && FocusedId(host.Automation) == "SpinBig.Text",
            $"Up button invoked={invoked}; focus on the text={FocusedId(host.Automation)}.");
        for (int i = 0; i < 8; i++)
        {
            _ = host.Driver.Invoke("SpinBig.Up");
        }

        Expect(host, "SpinBig", "4294967295", "Eight more presses clamp at the maximum");
        _ = host.Driver.Invoke("SpinBig.Down");
        Expect(host, "SpinBig", "4294967294", "The Down button from the maximum");

        Press(host, VirtualKeyShort.RETURN);
        E2EResults.Record("additions.spinner.closed", host.Driver.WaitForNoDialog(), "Enter in a spin box pressed the default button.");
        JsonElement report = host.Report();
        JsonElement mainReport = report.GetProperty("SpinMain");
        JsonElement bigReport = report.GetProperty("SpinBig");
        E2EResults.Record(
            "additions.spinner.report",
            report.GetProperty("accepted").GetBoolean() &&
                mainReport.GetProperty("text").GetString() == "5000" &&
                !mainReport.GetProperty("valid").GetBoolean() &&
                mainReport.GetProperty("lastValid").GetInt64() == 53 &&
                bigReport.GetProperty("valid").GetBoolean() &&
                bigReport.GetProperty("value").GetInt64() == 4294967294,
            $"report main={mainReport.GetRawText().Replace(Environment.NewLine, " ")}, big={bigReport.GetRawText().Replace(Environment.NewLine, " ")}.");
        Finish(start, "spin box");
    }

    [TestMethod]
    [TestCategory("Dialogs")]
    [TestCategory("E2EOnly")]
    public void Additions_UnsignedSpinnerFullRange()
    {
        int start = E2EResults.CheckCount;
        using Host host = Host.Start("unsigned-spinner");
        if (!EnsureForeground(host.Hwnd))
        {
            Assert.Fail("The unsigned spinner dialog is not the foreground window.");
        }

        AutomationElement main = host.Driver.WaitForElement("UnsignedMain")!;
        E2EResults.Record(
            "additions.unsigned-spinner.automation",
            main.ControlType == ControlType.Spinner && main.Patterns.RangeValue.IsSupported &&
                main.Patterns.RangeValue.Pattern.Minimum.Value == 0 &&
                main.Patterns.RangeValue.Pattern.Maximum.Value == (double)ulong.MaxValue &&
                main.Patterns.Value.IsSupported && main.Patterns.Value.Pattern.Value == "9223372036854775808",
            $"type={main.ControlType}, min={main.Patterns.RangeValue.Pattern.Minimum.Value}, max={main.Patterns.RangeValue.Pattern.Maximum.Value}, text='{main.Patterns.Value.Pattern.Value}'.");

        _ = FocusAutomationId(host.Automation, "UnsignedMain.Text");
        Press(host, VirtualKeyShort.UP);
        Expect(host, "UnsignedMain", "9223372036854775809", "Up above long.MaxValue");
        Press(host, VirtualKeyShort.DOWN);
        Expect(host, "UnsignedMain", "9223372036854775808", "Down above long.MaxValue");
        SelectAllAndType("18446744073709551615");
        Press(host, VirtualKeyShort.UP);
        Expect(host, "UnsignedMain", "18446744073709551615", "Up at ulong.MaxValue stays there");
        Press(host, VirtualKeyShort.DOWN);
        Expect(host, "UnsignedMain", "18446744073709551614", "Down from ulong.MaxValue");
        SelectAllAndType("0");
        Press(host, VirtualKeyShort.DOWN);
        Expect(host, "UnsignedMain", "0", "Down at the unsigned minimum stays there");

        _ = FocusAutomationId(host.Automation, "UnsignedBounded.Text");
        Press(host, VirtualKeyShort.UP);
        Expect(host, "UnsignedBounded", "9223372036854775809", "Up from the unsigned minimum");
        Press(host, VirtualKeyShort.DOWN);
        Expect(host, "UnsignedBounded", "9223372036854775808", "Down to the unsigned minimum");
        Press(host, VirtualKeyShort.DOWN);
        Expect(host, "UnsignedBounded", "9223372036854775808", "Down at the unsigned minimum stays there");
        SelectAllAndType("18446744073709551615");
        Press(host, VirtualKeyShort.UP);
        Expect(host, "UnsignedBounded", "18446744073709551615", "Up at the unsigned maximum stays there");

        AutomationElement largeStep = host.Driver.WaitForElement("UnsignedLargeStep")!;
        E2EResults.Record(
            "additions.unsigned-spinner.large-step-range",
            largeStep.Patterns.RangeValue.IsSupported &&
                largeStep.Patterns.RangeValue.Pattern.Minimum.Value == 1 &&
                largeStep.Patterns.RangeValue.Pattern.Maximum.Value == (double)ulong.MaxValue &&
                largeStep.Patterns.RangeValue.Pattern.SmallChange.Value == (double)ulong.MaxValue,
            $"min={largeStep.Patterns.RangeValue.Pattern.Minimum.Value}, max={largeStep.Patterns.RangeValue.Pattern.Maximum.Value}, step={largeStep.Patterns.RangeValue.Pattern.SmallChange.Value}.");
        _ = FocusAutomationId(host.Automation, "UnsignedLargeStep.Text");
        bool upInvoked = host.Driver.Invoke("UnsignedLargeStep.Up");
        Expect(host, "UnsignedLargeStep", "18446744073709551615", "A ulong-sized step clamps at the maximum");
        bool upAtMaxInvoked = host.Driver.Invoke("UnsignedLargeStep.Up");
        Expect(host, "UnsignedLargeStep", "18446744073709551615", "A large step at the maximum stays there");
        bool downInvoked = host.Driver.Invoke("UnsignedLargeStep.Down");
        Expect(host, "UnsignedLargeStep", "1", "A large step clamps at the minimum");
        bool downAtMinInvoked = host.Driver.Invoke("UnsignedLargeStep.Down");
        Expect(host, "UnsignedLargeStep", "1", "A large step at the minimum stays there");
        E2EResults.Record(
            "additions.unsigned-spinner.button-focus",
            upInvoked && upAtMaxInvoked && downInvoked && downAtMinInvoked && FocusedId(host.Automation) == "UnsignedLargeStep.Text",
            $"button invocations={upInvoked}/{upAtMaxInvoked}/{downInvoked}/{downAtMinInvoked}; focus='{FocusedId(host.Automation)}'.");

        Press(host, VirtualKeyShort.RETURN);
        E2EResults.Record("additions.unsigned-spinner.closed", host.Driver.WaitForNoDialog(), "Enter closed the unsigned spinner dialog.");
        JsonElement report = host.Report();
        E2EResults.Record(
            "additions.unsigned-spinner.report",
            report.GetProperty("accepted").GetBoolean() &&
                report.GetProperty("UnsignedMain").GetProperty("text").GetString() == "0" &&
                report.GetProperty("UnsignedBounded").GetProperty("text").GetString() == "18446744073709551615" &&
                report.GetProperty("UnsignedLargeStep").GetProperty("text").GetString() == "1",
            $"report={report.GetRawText().Replace(Environment.NewLine, " ")}.");
        Finish(start, "unsigned spinner");
    }

    [TestMethod]
    [TestCategory("Dialogs")]
    [TestCategory("E2EOnly")]
    public void Additions_EditableGridTwoColumns()
    {
        int start = E2EResults.CheckCount;
        using Host host = Host.Start("grid2");
        if (!EnsureForeground(host.Hwnd))
        {
            Assert.Fail("The grid dialog is not the foreground window.");
        }

        AutomationElement grid = host.Driver.WaitForElement("GridMain")!;
        E2EResults.Record("additions.grid2.name", grid.Name == "Values", $"grid name='{grid.Name}'.");
        E2EResults.Record(
            "additions.grid2.cells",
            CellName(host, 0, 0) == "Value name, row 1" && CellName(host, 0, 1) == "Value, row 1" &&
                CellName(host, 1, 0) == "Value name, row 2" && CellName(host, 2, 0) == "Value name, new row" &&
                CellName(host, 3, 0) is null,
            $"names R0C0='{CellName(host, 0, 0)}', R0C1='{CellName(host, 0, 1)}', R2C0='{CellName(host, 2, 0)}', R3C0='{CellName(host, 3, 0)}' (absent expected).");
        E2EResults.Record(
            "additions.grid2.headers",
            host.Driver.WaitForElement("GridMain", TimeSpan.FromSeconds(1))!.FindFirstDescendant(cf => cf.ByName("Value name")) is not null &&
                host.Driver.WaitForElement("GridMain", TimeSpan.FromSeconds(1))!.FindFirstDescendant(cf => cf.ByName("Value")) is not null,
            "The header shows 'Value name' and 'Value'.");

        bool focused = ControlTestSupport.WaitUntil(() => FocusedId(host.Automation) == "GridMain.R0C0", TimeSpan.FromSeconds(3));
        E2EResults.Record("additions.grid2.initial-focus", focused, $"focus='{FocusedId(host.Automation)}', expected GridMain.R0C0.");

        // Delete removes the focused row when it is not being edited. The focus stays at the same index.
        Press(host, VirtualKeyShort.DOWN);
        ExpectFocus(host, "GridMain.R1C0", "Down moves to the same column of the next row");
        Press(host, VirtualKeyShort.DELETE);
        ExpectFocus(host, "GridMain.R1C0", "After Delete the focus is on the same index");
        E2EResults.Record(
            "additions.grid2.deleted",
            CellName(host, 1, 0) == "Value name, new row" && CellName(host, 2, 0) is null,
            $"row 2 is now the empty row ('{CellName(host, 1, 0)}'), no third row ('{CellName(host, 2, 0)}').");

        // Typing into the empty row makes it a real row and adds a new empty row. Enter commits and moves to the next column.
        TypeText("gamma");
        E2EResults.Record(
            "additions.grid2.add-row",
            Value(host, 1, 0) == "gamma" && CellName(host, 1, 0) == "Value name, row 2" && CellName(host, 2, 0) == "Value name, new row",
            $"R1C0='{Value(host, 1, 0)}', name='{CellName(host, 1, 0)}', new row name='{CellName(host, 2, 0)}'.");
        Press(host, VirtualKeyShort.RETURN);
        ExpectFocus(host, "GridMain.R1C1", "Enter commits and moves to the Value column");

        // A printable key on a cell that is not being edited starts the edit and replaces the text.
        TypeText("3");
        Press(host, VirtualKeyShort.RETURN);
        E2EResults.Record("additions.grid2.typed-into-cell", Value(host, 1, 1) == "3", $"R1C1='{Value(host, 1, 1)}', expected '3'.");
        ExpectFocus(host, "GridMain.R1C1", "Enter in the last column stays on the cell");

        // F2 edits in place. Left and Right move between columns when no edit is open.
        Press(host, VirtualKeyShort.UP);
        ExpectFocus(host, "GridMain.R0C1", "Up keeps the column");
        Press(host, VirtualKeyShort.F2);
        TypeText("9");
        Press(host, VirtualKeyShort.RETURN);
        E2EResults.Record("additions.grid2.f2-edit", Value(host, 0, 1) == "9", $"R0C1='{Value(host, 0, 1)}', expected '9'.");
        Press(host, VirtualKeyShort.LEFT);
        ExpectFocus(host, "GridMain.R0C0", "Left moves to the previous column");
        Press(host, VirtualKeyShort.RIGHT);
        ExpectFocus(host, "GridMain.R0C1", "Right moves to the next column");
        Press(host, VirtualKeyShort.LEFT);

        // Escape in an edit reverts the cell and does not close the dialog. Delete in an edit is a character delete.
        Press(host, VirtualKeyShort.F2);
        TypeText("zz");
        Press(host, VirtualKeyShort.ESCAPE);
        E2EResults.Record(
            "additions.grid2.escape-reverts",
            Value(host, 0, 0) == "alpha" && host.Driver.WaitForElement(DialogRootId, TimeSpan.FromSeconds(1)) is not null,
            $"R0C0='{Value(host, 0, 0)}' after Escape, dialog still open={host.Driver.WaitForElement(DialogRootId, TimeSpan.FromSeconds(1)) is not null}.");
        Press(host, VirtualKeyShort.F2);
        Press(host, VirtualKeyShort.DELETE);
        E2EResults.Record(
            "additions.grid2.delete-in-edit",
            CellName(host, 0, 0) == "Value name, row 1" && CellName(host, 1, 0) == "Value name, row 2" && CellName(host, 2, 0) == "Value name, new row",
            "Delete while editing deleted the selected text, not the row: the rows are all still there.");
        Press(host, VirtualKeyShort.ESCAPE);
        E2EResults.Record("additions.grid2.escape-after-delete", Value(host, 0, 0) == "alpha", $"R0C0='{Value(host, 0, 0)}', expected 'alpha'.");

        // Clearing a two-column row removes it on commit. Focus stays on the following row's first column.
        Press(host, VirtualKeyShort.RIGHT);
        ExpectFocus(host, "GridMain.R0C1", "Right moves to the value column before clearing the row");
        Press(host, VirtualKeyShort.F2);
        Press(host, VirtualKeyShort.DELETE);
        Press(host, VirtualKeyShort.RETURN);
        E2EResults.Record("additions.grid2.clear-value", Value(host, 0, 1) == string.Empty, $"R0C1='{Value(host, 0, 1)}', expected empty.");
        Press(host, VirtualKeyShort.LEFT);
        ExpectFocus(host, "GridMain.R0C0", "Left returns to the name column before clearing the row");
        Press(host, VirtualKeyShort.F2);
        Press(host, VirtualKeyShort.DELETE);
        Press(host, VirtualKeyShort.RETURN);
        E2EResults.Record(
            "additions.grid2.removed-row-focus",
            Value(host, 0, 0) == "gamma" && FocusedId(host.Automation) == "GridMain.R0C0" && CellName(host, 1, 0) == "Value name, new row",
            $"following row value='{Value(host, 0, 0)}', focus='{FocusedId(host.Automation)}', next row name='{CellName(host, 1, 0)}'.");

        // Tab leaves the grid.
        Press(host, VirtualKeyShort.TAB);
        ExpectFocus(host, PrimaryId, "Tab leaves the grid for OK");
        Press(host, VirtualKeyShort.RETURN);
        E2EResults.Record("additions.grid2.closed", host.Driver.WaitForNoDialog(), "Enter on OK closed the dialog.");
        JsonElement report = host.Report();
        string rows = RowsOf(report);
        E2EResults.Record(
            "additions.grid2.report",
            report.GetProperty("accepted").GetBoolean() && report.GetProperty("columns").GetInt32() == 2 && rows == "gamma|3",
            $"accepted={report.GetProperty("accepted").GetBoolean()}, rows='{rows}', expected 'gamma|3'.");
        Finish(start, "two column grid");
    }

    [TestMethod]
    [TestCategory("Dialogs")]
    [TestCategory("E2EOnly")]
    public void Additions_EditableGridOneColumn()
    {
        int start = E2EResults.CheckCount;
        using Host host = Host.Start("grid1");
        if (!EnsureForeground(host.Hwnd))
        {
            Assert.Fail("The grid dialog is not the foreground window.");
        }

        E2EResults.Record(
            "additions.grid1.cells",
            CellName(host, 0, 0) == "Value, new row" && CellName(host, 0, 1) is null && CellName(host, 1, 0) is null,
            $"R0C0='{CellName(host, 0, 0)}', R0C1='{CellName(host, 0, 1)}' (absent expected), R1C0='{CellName(host, 1, 0)}' (absent expected).");
        bool focused = ControlTestSupport.WaitUntil(() => FocusedId(host.Automation) == "GridMain.R0C0", TimeSpan.FromSeconds(3));
        E2EResults.Record("additions.grid1.initial-focus", focused, $"focus='{FocusedId(host.Automation)}', expected GridMain.R0C0.");

        TypeText("x1");
        Press(host, VirtualKeyShort.RETURN);
        E2EResults.Record(
            "additions.grid1.first-row",
            Value(host, 0, 0) == "x1" && CellName(host, 0, 0) == "Value, row 1" && CellName(host, 1, 0) == "Value, new row",
            $"R0C0='{Value(host, 0, 0)}' name='{CellName(host, 0, 0)}', R1C0 name='{CellName(host, 1, 0)}'.");
        Press(host, VirtualKeyShort.DOWN);
        ExpectFocus(host, "GridMain.R1C0", "Down reaches the empty row");
        TypeText("x2");
        Press(host, VirtualKeyShort.RETURN);
        Press(host, VirtualKeyShort.DOWN);
        ExpectFocus(host, "GridMain.R2C0", "Down reaches the new empty row");
        Press(host, VirtualKeyShort.DOWN);
        ExpectFocus(host, "GridMain.R2C0", "Down on the last row stays");

        // Escape in an edit keeps the old text and the dialog.
        Press(host, VirtualKeyShort.UP);
        Press(host, VirtualKeyShort.UP);
        ExpectFocus(host, "GridMain.R0C0", "Up twice reaches the first row");
        Press(host, VirtualKeyShort.F2);
        TypeText("zz");
        Press(host, VirtualKeyShort.ESCAPE);
        E2EResults.Record(
            "additions.grid1.escape-reverts",
            Value(host, 0, 0) == "x1" && host.Driver.WaitForElement(DialogRootId, TimeSpan.FromSeconds(1)) is not null,
            $"R0C0='{Value(host, 0, 0)}' after Escape.");

        // A row emptied by an edit is removed when the edit is committed.
        Press(host, VirtualKeyShort.F2);
        Press(host, VirtualKeyShort.DELETE);
        Press(host, VirtualKeyShort.RETURN);
        E2EResults.Record(
            "additions.grid1.empty-row-removed",
            Value(host, 0, 0) == "x2" && CellName(host, 0, 0) == "Value, row 1" && CellName(host, 1, 0) == "Value, new row" && CellName(host, 2, 0) is null,
            $"R0C0='{Value(host, 0, 0)}' name='{CellName(host, 0, 0)}', R1C0='{CellName(host, 1, 0)}', R2C0='{CellName(host, 2, 0)}' (absent expected).");

        // Enter when no edit is open goes to the dialog (the default button).
        Press(host, VirtualKeyShort.RETURN);
        E2EResults.Record("additions.grid1.closed", host.Driver.WaitForNoDialog(), "Enter with no edit open pressed OK.");
        JsonElement report = host.Report();
        string rows = RowsOf(report);
        E2EResults.Record(
            "additions.grid1.report",
            report.GetProperty("accepted").GetBoolean() && report.GetProperty("columns").GetInt32() == 1 && rows == "x2",
            $"accepted={report.GetProperty("accepted").GetBoolean()}, rows='{rows}', expected 'x2'.");
        Finish(start, "one column grid");
    }

    private static void Finish(int start, string what)
    {
        IReadOnlyList<string> failures = E2EResults.FailedChecksSince(start);
        Assert.AreEqual(0, failures.Count, $"Checks of the {what} failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private static void Press(Host host, VirtualKeyShort key)
    {
        _ = host;
        Keyboard.Type(key);
        Thread.Sleep(150);
    }

    private static void TypeText(string text)
    {
        Keyboard.Type(text);
        Thread.Sleep(250);
    }

    private static void Expect(Host host, string id, string expected, string what)
    {
        string actual = string.Empty;
        bool ok = ControlTestSupport.WaitUntil(() => (actual = host.Driver.TextOf(id)) == expected, TimeSpan.FromSeconds(2));
        E2EResults.Record($"additions.{id}.{Slug(what)}", ok, $"{what}: '{id}' shows '{actual}', expected '{expected}'.");
    }

    private static void ExpectFocus(Host host, string expected, string what)
    {
        bool ok = ControlTestSupport.WaitUntil(() => FocusedId(host.Automation) == expected, TimeSpan.FromSeconds(2));
        E2EResults.Record($"additions.focus.{Slug(what)}", ok, $"{what}: focus='{FocusedId(host.Automation)}', expected '{expected}'.");
    }

    private static string Slug(string text) =>
        new(text.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray());

    private static string? CellName(Host host, int row, int column)
    {
        AutomationElement? cell = host.Driver.WaitForElement($"GridMain.R{row}C{column}", TimeSpan.FromMilliseconds(row >= 3 || column >= 1 ? 600 : 1500));
        return cell?.Name;
    }

    private static string Value(Host host, int row, int column) => host.Driver.TextOf($"GridMain.R{row}C{column}");

    private static string RowsOf(JsonElement report) =>
        string.Join(";", report.GetProperty("rows").EnumerateArray().Select(row => string.Join("|", row.EnumerateArray().Select(c => c.GetString()))));

    private static void Names(Host host, params (string Id, string Expected)[] expected)
    {
        foreach ((string id, string name) in expected)
        {
            AutomationElement? element = host.Driver.WaitForElement(id, TimeSpan.FromSeconds(3));
            E2EResults.Record(
                $"additions.literal.name.{id}",
                element?.Name == name,
                $"'{id}' UI Automation name='{element?.Name}', expected '{name}'.");
        }
    }

    private static string ToggleOf(Host host, string id)
    {
        try
        {
            return host.Driver.WaitForElement(id, TimeSpan.FromSeconds(2))?.Patterns.Toggle.Pattern.ToggleState.Value.ToString() ?? "missing";
        }
        catch (Exception)
        {
            return "unreadable";
        }
    }

    private static void SelectAllAndType(string text)
    {
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(text);
        Thread.Sleep(250);
    }

    private static long StyleOf(IntPtr hwnd) => GetWindowLongPtrW(hwnd, GwlStyle).ToInt64();

    private static (int Width, int Height) ClientSizeOf(IntPtr hwnd)
    {
        if (!GetClientRect(hwnd, out Win32.NativeRect rect))
        {
            throw new InvalidOperationException("The client rectangle could not be read.");
        }

        return (rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    /// <summary>Distance in pixels from a button's right edge to the client area's right edge.</summary>
    private static double Gap(Host host, string automationId)
    {
        System.Drawing.Rectangle client = ClientScreenOf(host.Hwnd);
        AutomationElement? element = host.Driver.WaitForElement(automationId, TimeSpan.FromSeconds(2));
        return element is null ? double.NaN : client.Right - element.BoundingRectangle.Right;
    }

    private static System.Drawing.Rectangle ClientScreenOf(IntPtr hwnd)
    {
        var origin = new NativePoint();
        _ = ClientToScreen(hwnd, ref origin);
        (int width, int height) = ClientSizeOf(hwnd);
        return new System.Drawing.Rectangle(origin.X, origin.Y, width, height);
    }

    /// <summary>The screen point near the bottom right corner that hit-tests as a sizing corner, or null.</summary>
    private static System.Drawing.Point? FindSizingCorner(IntPtr hwnd)
    {
        if (!Win32.GetWindowRect(hwnd, out Win32.NativeRect window))
        {
            return null;
        }

        for (int inset = 1; inset <= 12; inset++)
        {
            int x = window.Right - inset;
            int y = window.Bottom - inset;
            IntPtr lParam = (IntPtr)((y << 16) | (x & 0xFFFF));
            if ((int)SendMessageW(hwnd, WmNcHitTest, IntPtr.Zero, lParam) == HtBottomRight)
            {
                return new System.Drawing.Point(x, y);
            }
        }

        return null;
    }

    /// <summary>Presses the left button at <paramref name="from"/> and moves the pointer by the offset in steps.</summary>
    private static void DragBy(System.Drawing.Point from, int dx, int dy)
    {
        // The approach has no button down, so the cursor is placed on the corner. The press and the move are real.
        Mouse.Position = from;
        Thread.Sleep(150);
        Mouse.Down(MouseButton.Left);
        const int steps = 12;
        for (int i = 1; i <= steps; i++)
        {
            Mouse.MoveTo(new System.Drawing.Point(from.X + dx * i / steps, from.Y + dy * i / steps));
            Thread.Sleep(40);
        }

        Thread.Sleep(150);
        Mouse.Up(MouseButton.Left);
        Thread.Sleep(400);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out Win32.NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    /// <summary>One launched head with a host dialog open over it, and the report file that the dialog writes.</summary>
    private sealed class Host : IDisposable
    {
        private static readonly TimeSpan ReportTimeout = TimeSpan.FromSeconds(15);
        private readonly string _reportPath;

        private Host(AppSession session, EditingDriver driver, UIA3Automation automation, string reportPath, AutomationElement? dialog)
        {
            Session = session;
            Driver = driver;
            Automation = automation;
            _reportPath = reportPath;
            Dialog = dialog!;
            Hwnd = dialog is null ? IntPtr.Zero : DialogHandle(dialog);
        }

        internal AppSession Session { get; }

        internal EditingDriver Driver { get; }

        internal UIA3Automation Automation { get; }

        internal AutomationElement Dialog { get; }

        internal IntPtr Hwnd { get; }

        internal static Host Start(string scenario, bool expectDialog = true)
        {
            E2EConfig config = GuiGate.Config;
            E2EGuards.RequireE2EOnlyFlavor(config);
            config.EnsureExecutablesExist();

            string scope = "dialog-additions";
            string reportPath = Path.Combine(config.DataRootFor(scope), $"{scenario}-{Guid.NewGuid():N}.json");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["BWC_E2E_DIALOG_HOST"] = scenario,
                ["BWC_E2E_DIALOG_HOST_REPORT"] = reportPath,
            };
            foreach (KeyValuePair<string, string> pair in config.DataRootEnvironment(scope))
            {
                environment[pair.Key] = pair.Value;
            }

            foreach (KeyValuePair<string, string> pair in config.TempEnvironment(scope))
            {
                environment[pair.Key] = pair.Value;
            }

            var session = new AppSession(config, config.GpeditExePath, environment: environment);
            var driver = new EditingDriver(session);
            var automation = new UIA3Automation();
            AutomationElement? dialog = null;
            if (expectDialog)
            {
                dialog = driver.WaitForElement(DialogRootId, TimeSpan.FromSeconds(20));
                bool shown = dialog is not null;
                E2EResults.Record($"additions.{scenario}.dialog-shown", shown, $"host dialog '{scenario}' present={shown}.");
                if (!shown)
                {
                    string detail = File.Exists(reportPath) ? File.ReadAllText(reportPath) : "no report";
                    automation.Dispose();
                    driver.Dispose();
                    session.Dispose();
                    Assert.Fail($"The host dialog '{scenario}' did not open: {detail}");
                }
            }

            return new Host(session, driver, automation, reportPath, dialog);
        }

        /// <summary>Waits for the report the dialog writes when it closes (or when it fails to open) and parses it.</summary>
        internal JsonElement Report()
        {
            DateTime deadline = DateTime.UtcNow + ReportTimeout;
            string last = string.Empty;
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(_reportPath))
                {
                    try
                    {
                        last = File.ReadAllText(_reportPath);
                        using JsonDocument document = JsonDocument.Parse(last);
                        return document.RootElement.Clone();
                    }
                    catch (Exception ex) when (ex is IOException or JsonException)
                    {
                        // The file is still being written. Try again.
                    }
                }

                Thread.Sleep(100);
            }

            E2EResults.Record("additions.report", false, $"No readable report at '{_reportPath}' within {ReportTimeout.TotalSeconds:0} s. Last text: '{last}'.");
            throw new AssertFailedException($"No readable report at '{_reportPath}'.");
        }

        public void Dispose()
        {
            Automation.Dispose();
            Driver.Dispose();
            Session.Dispose();
        }
    }
}

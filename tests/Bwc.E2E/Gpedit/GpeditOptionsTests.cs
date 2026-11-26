using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Gpedit;

/// <summary>
/// Tests of the Options pane of the policy-setting dialog over the F1 fixture templates and a sandbox policy object
/// that the
/// test seeds. The failure modes are in docs/failure-modes/gpedit-options.md; the section number is named in each test. The
/// dialog is opened the way a user opens it, the controls are driven through UI Automation and real keys, and the
/// values are
/// read from the E2E readout of the pane (the value Apply would write) and from a reopened dialog. The checks go to the E2E
/// result artifact. Nothing is written to the policy files by the product in this piece.
/// </summary>
[TestClass]
public sealed class GpeditOptionsTests
{
    private const string Root = GpeditRun.RootName;
    private const string Computer = "Computer Configuration";
    private const string User = "User Configuration";
    private const string Admin = "Administrative Templates";
    private const string Oracle = "BWC Oracle";

    // The ids of the option controls are the kind prefix of the classic form plus the label of the presentation.
    private const string MinMaxSpin = "numericUpDownNumber from 10 to 500:";
    private const string LongSpin = "numericUpDownLong number:";
    private const string SoftSpin = "numericUpDownSoft number:";
    private const string PlainBox = "textBoxPlain text:";
    private const string RequiredBox = "textBoxRequired text:";
    private const string ComboBoxId = "comboBoxPick or type a city:";
    private const string ComboNoSortId = "comboBoxPick or type a size:";
    private const string UnsortedDrop = "dropDownListUnsorted choice:";
    private const string DecimalDrop = "dropDownListNumber choice:";
    private const string ZetaCheck = "checkBoxZeta check";
    private const string AlphaCheck = "checkBoxAlpha check";

    private const string AmpText = "textBoxName & address:";
    private const string AmpCheck = "checkBoxSave & close";
    private const string AmpSpin = "numericUpDownRock & roll count:";
    private const string AmpDrop = "dropDownListPick & choose:";

    private const double Tolerance = 2.5;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    // ---- 1, 16: one control per element, in presentation order --------------------------------

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Options_Elements()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("options", "elements");
        using GpeditRun run = Start("gpedit-options-elements", out _);
        run.GoTo(Root, Computer, Admin, Oracle, "Tools & Utilities");

        using (GpeditDialog? dialog = GpeditDialog.Open(run, "Labels with single ampersands setting"))
        {
            checks.Record("amp.opens", dialog is not null, "the policy with four elements opens.");
            if (dialog is null)
            {
                checks.AssertAllPassed();
                return;
            }

            string[] ids = [AmpText, AmpCheck, AmpSpin, AmpDrop];
            float[] tops = ids.Select(id => dialog.RectDip(id)?.Y ?? float.NaN).ToArray();
            checks.Record("amp.present", tops.All(t => !float.IsNaN(t)), $"tops={string.Join(",", tops)}.");
            checks.Record(
                "amp.presentationOrder",
                tops.Zip(tops.Skip(1), (a, b) => b > a).All(x => x),
                $"text box, check box, number box, drop-down follow one another downwards: tops={string.Join(",", tops)}.");
            checks.Record(
                "amp.elementIds",
                dialog.ElementIds().OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(["AmpCheck", "AmpChoice", "AmpNumber", "AmpText"]),
                $"readout elements={string.Join(",", dialog.ElementIds())}.");
            checks.Record("amp.paneInside", PaneContains(dialog, ids), "every control lies inside the Options pane.");
            _ = dialog.Screenshot("gpedit-options-elements-amp.png");
            Close(dialog);
        }

        run.GoTo(Root, Computer, Admin, Oracle, "Elements", "Texts");
        using (GpeditDialog? dialog = GpeditDialog.Open(run, "Multiple text lines setting"))
        {
            checks.Record("multi.opens", dialog is not null, "the multi-line policy opens.");
            if (dialog is not null)
            {
                float label = dialog.RectDip("labelOne entry per line.")?.Y ?? float.NaN;
                float box = dialog.RectDip("multiTextBoxMultiLines")?.Y ?? float.NaN;
                checks.Record("multi.labelThenBox", !float.IsNaN(label) && !float.IsNaN(box) && box > label, $"label top={label}, box top={box}.");
                checks.Record("multi.noValueForLabel", dialog.ElementIds().SequenceEqual(["MultiLines"]), $"elements={string.Join(",", dialog.ElementIds())}.");
                Close(dialog);
            }
        }

        run.GoTo(Root, Computer, Admin, Oracle, "Elements", "Booleans");
        using (GpeditDialog? dialog = GpeditDialog.Open(run, "Two checks defined in reverse name order setting"))
        {
            checks.Record("checks.opens", dialog is not null, "the policy with two check boxes opens.");
            if (dialog is not null)
            {
                float zeta = dialog.RectDip(ZetaCheck)?.Y ?? float.NaN;
                float alpha = dialog.RectDip(AlphaCheck)?.Y ?? float.NaN;
                checks.Record("checks.presentationOrder", !float.IsNaN(zeta) && !float.IsNaN(alpha) && zeta < alpha, $"Zeta top={zeta}, Alpha top={alpha}: the order of the presentation, not of the names.");
                Close(dialog);
            }
        }

        run.GoTo(Root, Computer, Admin, Oracle, "Basics");
        using (GpeditDialog? dialog = GpeditDialog.Open(run, "Class machine flag setting"))
        {
            checks.Record("none.opens", dialog is not null, "a policy with no element opens.");
            if (dialog is not null)
            {
                checks.Record("none.emptyPane", dialog.ElementIds().Length == 0, $"elements={string.Join(",", dialog.ElementIds())}.");
                Close(dialog);
            }
        }

        run.WriteArtifacts("gpedit-options-elements.png");
        checks.AssertAllPassed();
    }

    // ---- 2, 4: defaults and the enabled state ---------------------------------------------------

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Options_Defaults()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("options", "defaults");
        using GpeditRun run = Start("gpedit-options-defaults", out _);
        run.GoTo(Root, Computer, Admin, Oracle, "Elements", "Numbers");

        using (GpeditDialog? dialog = GpeditDialog.Open(run, "Decimal with a range setting"))
        {
            checks.Record("number.opens", dialog is not null, "the number policy opens.");
            if (dialog is null)
            {
                checks.AssertAllPassed();
                return;
            }

            checks.Record("number.default", Number(dialog, "MinMaxNumber") == "25", $"default number={Number(dialog, "MinMaxNumber")}, expected 25 (defaultValue).");
            checks.Record("number.disabledAtNotConfigured", !dialog.OptionsEnabled() && !dialog.IsEnabled(MinMaxSpin + ".Text"), "the number box is disabled while the policy is Not Configured.");
            _ = dialog.Select(GpeditDialog.Enabled);
            checks.Record("number.enabledAtEnabled", dialog.OptionsEnabled() && dialog.IsEnabled(MinMaxSpin + ".Text"), "the number box is enabled once Enabled is selected.");
            _ = dialog.TypeInto(MinMaxSpin + ".Text", "40");
            checks.Record("number.typed", Number(dialog, "MinMaxNumber") == "40", $"typed number={Number(dialog, "MinMaxNumber")}.");
            _ = dialog.Select(GpeditDialog.Disabled);
            checks.Record("number.disabledAtDisabled", !dialog.OptionsEnabled() && !dialog.IsEnabled(MinMaxSpin + ".Text"), "the number box is disabled again at Disabled.");
            _ = dialog.Select(GpeditDialog.Enabled);
            checks.Record("number.keepsTyped", dialog.OptionsEnabled() && Number(dialog, "MinMaxNumber") == "40", $"the typed value survives Disabled and Enabled: {Number(dialog, "MinMaxNumber")}.");
            Close(dialog);
        }

        using (GpeditDialog? dialog = GpeditDialog.Open(run, "Long decimal with a range setting"))
        {
            checks.Record("long.opens", dialog is not null, "the long decimal policy opens.");
            if (dialog is not null)
            {
                checks.Record("long.default", Number(dialog, "LongNumber") == "123456789012", $"default={Number(dialog, "LongNumber")}, expected 123456789012 (above 32 bits).");
                Close(dialog);
            }
        }

        // The defaults are written by an Apply that did not touch the pane.
        using (GpeditDialog? dialog = GpeditDialog.Open(run, "Decimal with a soft range setting"))
        {
            checks.Record("soft.opens", dialog is not null, "the soft number policy opens.");
            if (dialog is not null)
            {
                checks.Record("soft.default", Number(dialog, "SoftNumber") == "50", $"default={Number(dialog, "SoftNumber")}, expected 50.");
                _ = dialog.Select(GpeditDialog.Enabled);
                _ = dialog.Invoke(GpeditDialog.Apply);
                checks.Record("soft.applied", ShellRun.WaitUntil(() => !dialog.IsEnabled(GpeditDialog.Apply), TimeSpan.FromSeconds(3)), "Apply of Enabled with the untouched defaults succeeds.");
                Close(dialog);
            }
        }

        using (GpeditDialog? again = GpeditDialog.Open(run, "Decimal with a soft range setting"))
        {
            checks.Record("soft.reopens", again is not null, "the policy opens again.");
            if (again is not null)
            {
                checks.Record("soft.defaultWritten", again.IsSelected(GpeditDialog.Enabled) && Number(again, "SoftNumber") == "50", $"state enabled={again.IsSelected(GpeditDialog.Enabled)}, number={Number(again, "SoftNumber")}.");
                Close(again);
            }
        }

        run.GoTo(Root, Computer, Admin, Oracle, "Elements", "Texts");
        using (GpeditDialog? dialog = GpeditDialog.Open(run, "Plain text setting"))
        {
            checks.Record("text.opens", dialog is not null, "the text policy opens.");
            if (dialog is not null)
            {
                checks.Record("text.default", Text(dialog, "PlainText") == "plain default", $"default text='{Text(dialog, "PlainText")}'.");
                checks.Record("text.shown", dialog.TextOf(PlainBox) == "plain default", $"the box shows '{dialog.TextOf(PlainBox)}'.");
                Close(dialog);
            }
        }

        using (GpeditDialog? dialog = GpeditDialog.Open(run, "Text with a combo box and suggestions setting"))
        {
            checks.Record("combo.opens", dialog is not null, "the combo box policy opens.");
            if (dialog is not null)
            {
                checks.Record("combo.default", Text(dialog, "ComboText") == "Oslo", $"default text='{Text(dialog, "ComboText")}'.");
                Close(dialog);
            }
        }

        using (GpeditDialog? dialog = GpeditDialog.Open(run, "Multiple text lines setting"))
        {
            checks.Record("multi.opens", dialog is not null, "the multi-line policy opens.");
            if (dialog is not null)
            {
                checks.Record("multi.default", Lines(dialog, "MultiLines").Length == 0, $"default lines={string.Join("|", Lines(dialog, "MultiLines"))}.");
                Close(dialog);
            }
        }

        run.GoTo(Root, Computer, Admin, Oracle, "Elements", "Choices");
        using (GpeditDialog? dialog = GpeditDialog.Open(run, "Choice without sorting and with a default item setting"))
        {
            checks.Record("choice.opens", dialog is not null, "the unsorted choice policy opens.");
            if (dialog is not null)
            {
                checks.Record("choice.defaultItem", Choice(dialog, "UnsortedChoice") == 1, $"selected item={Choice(dialog, "UnsortedChoice")}, expected 1 (defaultItem).");
                Close(dialog);
            }
        }

        using (GpeditDialog? dialog = GpeditDialog.Open(run, "Choice with decimal items setting"))
        {
            checks.Record("choiceNoDefault.opens", dialog is not null, "the decimal choice policy opens.");
            if (dialog is not null)
            {
                // G4 changed this check: it expected the first item (0). Classic selects nothing when the presentation has no
                // defaultItem (F7, B/Pol_Enum_StringItems), so OK is refused until one is chosen (PolicyDefaults.For).
                checks.Record("choiceNoDefault.first", Choice(dialog, "DecimalChoice") == -1, $"selected item={Choice(dialog, "DecimalChoice")}, expected nothing selected (-1).");
                Close(dialog);
            }
        }

        run.GoTo(Root, User, Admin, Oracle, "Elements", "Booleans");
        using (GpeditDialog? dialog = GpeditDialog.Open(run, "Boolean with string values setting"))
        {
            checks.Record("check.opens", dialog is not null, "the check box policy opens.");
            if (dialog is not null)
            {
                checks.Record("check.defaultChecked", Boolean(dialog, "StringCheck") == true, $"checked={Boolean(dialog, "StringCheck")}, expected true (defaultChecked).");
                checks.Record("check.disabledAtNotConfigured", !dialog.IsEnabled("checkBoxString check"), "the check box is disabled while the policy is Not Configured.");
                Close(dialog);
            }
        }

        run.GoTo(Root, Computer, Admin, Oracle, "Tools & Utilities");
        using (GpeditDialog? dialog = GpeditDialog.Open(run, "Labels with single ampersands setting"))
        {
            checks.Record("amp.opens", dialog is not null, "the ampersand policy opens.");
            if (dialog is not null)
            {
                checks.Record("amp.textDefault", Text(dialog, "AmpText") == "Fish & Chips", $"default text='{Text(dialog, "AmpText")}': the ampersand stays.");
                checks.Record("amp.numberDefault", Number(dialog, "AmpNumber") == "3", $"default number={Number(dialog, "AmpNumber")}.");
                Close(dialog);
            }
        }

        run.WriteArtifacts("gpedit-options-defaults.png");
        checks.AssertAllPassed();
    }

    // ---- 5, 6: number boxes ---------------------------------------------------------------------

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Options_Numbers()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("options", "numbers");
        using GpeditRun run = Start("gpedit-options-numbers", out _);
        run.GoTo(Root, Computer, Admin, Oracle, "Elements", "Numbers");

        using (GpeditDialog? dialog = GpeditDialog.Open(run, "Decimal with a range setting"))
        {
            checks.Record("opens", dialog is not null, "the number policy opens.");
            if (dialog is null)
            {
                checks.AssertAllPassed();
                return;
            }

            _ = dialog.Select(GpeditDialog.Enabled);
            checks.Record("hasSpin", dialog.Find(MinMaxSpin + ".Up", 1) is not null && dialog.Find(MinMaxSpin + ".Down", 1) is not null, "a number box with spin buttons has both.");
            checks.Record("spinName", ShellRun.SafeName(dialog.Find(MinMaxSpin + ".Text", 1)) == "Number from 10 to 500:", $"name='{ShellRun.SafeName(dialog.Find(MinMaxSpin + ".Text", 1))}'.");
            _ = dialog.FocusControl(MinMaxSpin + ".Text");
            dialog.Press(VirtualKeyShort.UP);
            checks.Record("stepUp", Number(dialog, "MinMaxNumber") == "30", $"Up from 25 gives {Number(dialog, "MinMaxNumber")}, expected 30 (spinStep 5).");
            dialog.Press(VirtualKeyShort.DOWN);
            dialog.Press(VirtualKeyShort.DOWN);
            checks.Record("stepDown", Number(dialog, "MinMaxNumber") == "20", $"Down twice gives {Number(dialog, "MinMaxNumber")}, expected 20.");
            for (int i = 0; i < 4; i++)
            {
                dialog.Press(VirtualKeyShort.DOWN);
            }

            checks.Record("clampMin", Number(dialog, "MinMaxNumber") == "10", $"Down past the minimum gives {Number(dialog, "MinMaxNumber")}, expected 10.");
            _ = dialog.TypeInto(MinMaxSpin + ".Text", "498");
            dialog.Press(VirtualKeyShort.UP);
            checks.Record("clampMax", Number(dialog, "MinMaxNumber") == "500", $"Up from 498 gives {Number(dialog, "MinMaxNumber")}, expected 500.");
            dialog.Press(VirtualKeyShort.UP);
            checks.Record("stayMax", Number(dialog, "MinMaxNumber") == "500", $"Up at the maximum gives {Number(dialog, "MinMaxNumber")}.");
            _ = dialog.TypeInto(MinMaxSpin + ".Text", "1a2b");
            checks.Record("digitsOnly", Number(dialog, "MinMaxNumber") == "12", $"typing '1a2b' gives {Number(dialog, "MinMaxNumber")}, expected 12 (letters are ignored).");
            checks.Record("shownPlain", dialog.TextOf(MinMaxSpin + ".Text") == "12", $"the box shows '{dialog.TextOf(MinMaxSpin + ".Text")}'.");
            Close(dialog);
        }

        using (GpeditDialog? dialog = GpeditDialog.Open(run, "Long decimal with a range setting"))
        {
            checks.Record("long.opens", dialog is not null, "the long decimal policy opens.");
            if (dialog is not null)
            {
                _ = dialog.Select(GpeditDialog.Enabled);
                _ = dialog.FocusControl(LongSpin + ".Text");
                dialog.Press(VirtualKeyShort.UP);
                checks.Record("long.step", Number(dialog, "LongNumber") == "123456790012", $"Up gives {Number(dialog, "LongNumber")}, expected 123456790012 (spinStep 1000).");
                Close(dialog);
            }
        }

        run.GoTo(Root, User, Admin, Oracle, "Elements", "Numbers");
        using (GpeditDialog? dialog = GpeditDialog.Open(run, "Decimal that is required setting"))
        {
            checks.Record("plain.opens", dialog is not null, "the number policy with spin turned off opens.");
            if (dialog is not null)
            {
                _ = dialog.Select(GpeditDialog.Enabled);
                const string Plain = "textBoxRequired number:";
                checks.Record("plain.noSpin", dialog.Find(Plain, 2) is not null && dialog.Find("numericUpDownRequired number:.Up", 1) is null, "spin=false gives a plain box with no spin buttons.");
                _ = dialog.TypeInto(Plain, "a1b2");
                checks.Record("plain.digitsOnly", Number(dialog, "RequiredNumber") == "12", $"typing 'a1b2' gives {Number(dialog, "RequiredNumber")}, expected 12.");
                Close(dialog);
            }
        }

        using (GpeditDialog? dialog = GpeditDialog.Open(run, "Long decimal required, soft, stored as text setting"))
        {
            checks.Record("longText.opens", dialog is not null, "the long decimal text policy opens.");
            if (dialog is not null)
            {
                _ = dialog.Select(GpeditDialog.Enabled);
                const string LongText = "textBoxRequired long number:";
                string maximum = ulong.MaxValue.ToString(CultureInfo.InvariantCulture);
                _ = dialog.TypeInto(LongText, maximum);
                checks.Record(
                    "longText.ulongMax",
                    dialog.TextOf(LongText) == maximum && Number(dialog, "LongTextNumber") == maximum,
                    $"field='{dialog.TextOf(LongText)}', readout={Number(dialog, "LongTextNumber")}, expected {maximum}.");

                _ = dialog.TypeInto(LongText, string.Empty);
                checks.Record(
                    "longText.blankEditing",
                    dialog.TextOf(LongText) == string.Empty && Number(dialog, "LongTextNumber") == maximum,
                    $"blank field retains readout {Number(dialog, "LongTextNumber")}, expected {maximum}.");

                const string Overflow = "18446744073709551616";
                const string LastValidPrefix = "1844674407370955161";
                _ = dialog.TypeInto(LongText, Overflow);
                checks.Record(
                    "longText.overflowTypedRejected",
                    dialog.TextOf(LongText) == LastValidPrefix && Number(dialog, "LongTextNumber") == LastValidPrefix,
                    $"overflow field='{dialog.TextOf(LongText)}', readout={Number(dialog, "LongTextNumber")}, expected valid prefix {LastValidPrefix}.");
                Close(dialog);
            }
        }

        run.GoTo(Root, User, Admin, Oracle, "Elements", "Texts");
        using (GpeditDialog? dialog = GpeditDialog.Open(run, "Text with a length limit setting"))
        {
            checks.Record("maxLength.opens", dialog is not null, "the text policy with a length limit opens.");
            if (dialog is not null)
            {
                _ = dialog.Select(GpeditDialog.Enabled);
                _ = dialog.TypeInto("textBoxShort text:", "abcdefghijklmnop");
                checks.Record("maxLength.cut", Text(dialog, "ShortText") == "abcdefghijkl", $"text='{Text(dialog, "ShortText")}', expected the first 12 characters (maxLength).");
                Close(dialog);
            }
        }

        run.WriteArtifacts("gpedit-options-numbers.png");
        checks.AssertAllPassed();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Options_UnsignedLongDecimalSpinner()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("options", "unsigned-long-decimal");
        const string name = "gpedit-options-unsigned-long-decimal";
        GpeditSandbox sandbox = GpeditSandbox.Create(name);
        sandbox.DialogEnabled = true;
        sandbox.SeedStandard();
        sandbox.UseTemplatesWithBadCases();
        AddUnsignedLongDecimalTemplate(sandbox);
        using GpeditRun run = GpeditRun.Open(name, sandbox);
        run.GoTo(Root, Computer, Admin, Oracle, "Elements", "Numbers");

        const string rowName = "Long decimal above signed maximum setting";
        const string spinner = "numericUpDownWide number:";
        string minimum = (long.MaxValue + 1UL).ToString(CultureInfo.InvariantCulture);
        string maximum = ulong.MaxValue.ToString(CultureInfo.InvariantCulture);
        using (GpeditDialog? dialog = GpeditDialog.Open(run, rowName))
        {
            checks.Record("opens", dialog is not null, "the unsigned long-decimal policy opens.");
            if (dialog is null)
            {
                checks.AssertAllPassed();
                return;
            }

            _ = dialog.Select(GpeditDialog.Enabled);
            AutomationElement control = dialog.Find(spinner, 2)!;
            checks.Record(
                "full-range-automation",
                control.Patterns.RangeValue.IsSupported &&
                    control.Patterns.RangeValue.Pattern.Minimum.Value == (double)(long.MaxValue + 1UL) &&
                    control.Patterns.RangeValue.Pattern.Maximum.Value == (double)ulong.MaxValue &&
                    control.Patterns.RangeValue.Pattern.SmallChange.Value == (double)ulong.MaxValue &&
                    dialog.TextOf(spinner + ".Text") == minimum,
                $"min={control.Patterns.RangeValue.Pattern.Minimum.Value}, max={control.Patterns.RangeValue.Pattern.Maximum.Value}, step={control.Patterns.RangeValue.Pattern.SmallChange.Value}, text='{dialog.TextOf(spinner + ".Text")}'.");

            _ = dialog.FocusControl(spinner + ".Text");
            dialog.Press(VirtualKeyShort.UP);
            checks.Record("step-to-max", Number(dialog, "WideNumber") == maximum, $"Up from the minimum gives {Number(dialog, "WideNumber")}, expected {maximum}.");
            dialog.Press(VirtualKeyShort.DOWN);
            checks.Record("step-to-min", Number(dialog, "WideNumber") == minimum, $"Down from the maximum gives {Number(dialog, "WideNumber")}, expected {minimum}.");
            dialog.Press(VirtualKeyShort.DOWN);
            checks.Record("clamp-min", Number(dialog, "WideNumber") == minimum, $"Down at the minimum gives {Number(dialog, "WideNumber")}, expected {minimum}.");
            _ = dialog.TypeInto(spinner + ".Text", maximum);
            dialog.Press(VirtualKeyShort.UP);
            checks.Record("clamp-max", Number(dialog, "WideNumber") == maximum, $"Up at ulong.MaxValue gives {Number(dialog, "WideNumber")}, expected {maximum}.");
            _ = dialog.Invoke(GpeditDialog.Apply);
            checks.Record("applied", ShellRun.WaitUntil(() => !dialog.IsEnabled(GpeditDialog.Apply), TimeSpan.FromSeconds(3)), "Apply accepted the unsigned long-decimal maximum.");
            _ = dialog.Invoke(GpeditDialog.Ok);
            _ = dialog.WaitClosed();
        }

        using (GpeditDialog? again = GpeditDialog.Open(run, rowName))
        {
            checks.Record("reopens", again is not null, "the unsigned long-decimal policy reopens.");
            if (again is not null)
            {
                checks.Record("maximum-kept", Number(again, "WideNumber") == maximum && again.TextOf(spinner + ".Text") == maximum, $"readout={Number(again, "WideNumber")}, text='{again.TextOf(spinner + ".Text")}', expected {maximum}.");
                Close(again);
            }
        }

        run.WriteArtifacts("gpedit-options-unsigned-long-decimal.png");
        checks.AssertAllPassed();
    }

    // ---- 6, 7: combo boxes and drop-down lists -------------------------------------------------

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Options_Choices()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("options", "choices");
        using GpeditRun run = Start("gpedit-options-choices", out _);
        run.GoTo(Root, Computer, Admin, Oracle, "Elements", "Texts");

        using (GpeditDialog? dialog = GpeditDialog.Open(run, "Text with a combo box and suggestions setting"))
        {
            checks.Record("combo.opens", dialog is not null, "the combo box policy opens.");
            if (dialog is not null)
            {
                string[] items = dialog.ItemsOf("ComboText");
                checks.Record("combo.sorted", items.SequenceEqual(["Cairo", "Lima", "Oslo", "Paris"]), $"suggestions={string.Join("|", items)}, expected sorted (document order is Paris, Oslo, Lima, Cairo).");
                _ = dialog.Select(GpeditDialog.Enabled);
                _ = dialog.TypeInto(ComboBoxId, "Berlin");
                checks.Record("combo.editable", Text(dialog, "ComboText") == "Berlin", $"typed text='{Text(dialog, "ComboText")}': a combo box takes text that is not a suggestion.");
                Close(dialog);
            }
        }

        using (GpeditDialog? dialog = GpeditDialog.Open(run, "Text with an unsorted required combo box setting"))
        {
            checks.Record("comboNoSort.opens", dialog is not null, "the unsorted combo box policy opens.");
            if (dialog is not null)
            {
                string[] items = dialog.ItemsOf("ComboNoSortText");
                checks.Record("comboNoSort.order", items.SequenceEqual(["Large", "Medium", "Small"]), $"suggestions={string.Join("|", items)}.");
                Close(dialog);
            }
        }

        run.GoTo(Root, Computer, Admin, Oracle, "Elements", "Choices");
        using (GpeditDialog? dialog = GpeditDialog.Open(run, "Choice without sorting and with a default item setting"))
        {
            checks.Record("noSort.opens", dialog is not null, "the unsorted choice policy opens.");
            if (dialog is not null)
            {
                string[] items = dialog.ItemsOf("UnsortedChoice");
                checks.Record("noSort.order", items.SequenceEqual(["Zulu", "Alpha", "Mike"]), $"items={string.Join("|", items)}, expected the document order (noSort).");
                checks.Record("noSort.defaultItem", Choice(dialog, "UnsortedChoice") == 1, $"selected item={Choice(dialog, "UnsortedChoice")}, expected 1 (Alpha).");
                _ = dialog.Select(GpeditDialog.Enabled);
                _ = dialog.Choose(UnsortedDrop, "Mike");
                checks.Record("noSort.choose", Choice(dialog, "UnsortedChoice") == 2, $"after choosing Mike the item is {Choice(dialog, "UnsortedChoice")}, expected 2.");
                Close(dialog);
            }
        }

        using (GpeditDialog? dialog = GpeditDialog.Open(run, "Choice with decimal items setting"))
        {
            checks.Record("sorted.opens", dialog is not null, "the sorted choice policy opens.");
            if (dialog is not null)
            {
                string[] items = dialog.ItemsOf("DecimalChoice");
                checks.Record("sorted.order", items.SequenceEqual(["One", "Three", "Two", "Zero"]), $"items={string.Join("|", items)}, expected sorted by name.");
                _ = dialog.Select(GpeditDialog.Enabled);
                _ = dialog.Choose(DecimalDrop, "Two");
                checks.Record("sorted.storesItemIndex", Choice(dialog, "DecimalChoice") == 2, $"choosing Two gives item {Choice(dialog, "DecimalChoice")}, expected its template index 2, not its position in the sorted list.");
                Close(dialog);
            }
        }

        run.WriteArtifacts("gpedit-options-choices.png");
        checks.AssertAllPassed();
    }

    // ---- 3, 12: values are applied and shown again ----------------------------------------------

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Options_Roundtrip()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("options", "roundtrip");
        using GpeditRun run = Start("gpedit-options-roundtrip", out _);

        run.GoTo(Root, Computer, Admin, Oracle, "Elements", "Texts");
        RoundTrip(
            run, checks, "text", "Plain text setting",
            d => d.TypeInto(PlainBox, "hello & bye"),
            d => Text(d, "PlainText") == "hello & bye" && d.TextOf(PlainBox) == "hello & bye");
        RoundTrip(
            run, checks, "combo", "Text with a combo box and suggestions setting",
            d => d.TypeInto(ComboBoxId, "Berlin"),
            d => Text(d, "ComboText") == "Berlin");
        RoundTrip(
            run, checks, "multi", "Multiple text lines setting",
            d =>
            {
                _ = d.FocusControl("multiTextBoxMultiLines");
                Keyboard.Type("one");
                Keyboard.Type(VirtualKeyShort.RETURN);
                Keyboard.Type("two");
                Thread.Sleep(300);
            },
            d => Lines(d, "MultiLines").SequenceEqual(["one", "two"]));

        run.GoTo(Root, Computer, Admin, Oracle, "Elements", "Numbers");
        RoundTrip(
            run, checks, "number", "Decimal with a range setting",
            d => d.TypeInto(MinMaxSpin + ".Text", "77"),
            d => Number(d, "MinMaxNumber") == "77" && d.TextOf(MinMaxSpin + ".Text") == "77");

        run.GoTo(Root, Computer, Admin, Oracle, "Elements", "Choices");
        RoundTrip(
            run, checks, "choice", "Choice with decimal items setting",
            d => d.Choose(DecimalDrop, "Two"),
            d => Choice(d, "DecimalChoice") == 2,
            noDefaultItem: true);

        run.GoTo(Root, Computer, Admin, Oracle, "Elements", "Booleans");
        RoundTrip(
            run, checks, "check", "Two checks defined in reverse name order setting",
            d => d.Toggle(ZetaCheck),
            d => Boolean(d, "ZetaCheck") == true && Boolean(d, "AlphaCheck") == false);

        run.WriteArtifacts("gpedit-options-roundtrip.png");
        checks.AssertAllPassed();
    }

    private static void RoundTrip(GpeditRun run, GpeditChecks checks, string name, string row, Action<GpeditDialog> edit, Func<GpeditDialog, bool> verify, bool noDefaultItem = false)
    {
        using (GpeditDialog? dialog = GpeditDialog.Open(run, row))
        {
            checks.Record($"{name}.opens", dialog is not null, $"'{row}' opens.");
            if (dialog is null)
            {
                return;
            }

            checks.Record($"{name}.cleanAtOpen", !dialog.IsEnabled(GpeditDialog.Apply), "Apply is disabled at open.");
            _ = dialog.Select(GpeditDialog.Enabled);
            _ = dialog.Invoke(GpeditDialog.Apply);
            if (noDefaultItem)
            {
                // G4 changed this step for a drop-down with no defaultItem: nothing is selected, so Apply is refused with a message
                // box, as in classic (B/Pol_Enum_StringItems operation 0), and the choice made next is what gets applied.
                (string Caption, string Text)? refused = dialog.WaitMessage();
                checks.Record($"{name}.baseline", refused is not null && dialog.IsEnabled(GpeditDialog.Apply), $"Apply with no item selected is refused with a message and Apply stays enabled: {refused?.Text}.");
                if (refused is not null)
                {
                    _ = dialog.DismissMessage();
                }
            }
            else
            {
                checks.Record($"{name}.baseline", ShellRun.WaitUntil(() => !dialog.IsEnabled(GpeditDialog.Apply), TimeSpan.FromSeconds(3)), "Apply with the default values succeeds and Apply goes disabled.");
            }

            edit(dialog);
            checks.Record($"{name}.optionEnablesApply", ShellRun.WaitUntil(() => dialog.IsEnabled(GpeditDialog.Apply), TimeSpan.FromSeconds(3)), "one change of the option control enables Apply.");
            checks.Record($"{name}.dirtyReadout", dialog.Read("Dirty") == "1", $"readout Dirty={dialog.Read("Dirty")}.");
            _ = dialog.Invoke(GpeditDialog.Apply);
            checks.Record($"{name}.applied", ShellRun.WaitUntil(() => !dialog.IsEnabled(GpeditDialog.Apply), TimeSpan.FromSeconds(3)), "Apply with the changed option succeeds.");
            _ = dialog.Invoke(GpeditDialog.Ok);
            checks.Record($"{name}.okCloses", dialog.WaitClosed(), "OK closes the dialog.");
        }

        using (GpeditDialog? again = GpeditDialog.Open(run, row))
        {
            checks.Record($"{name}.reopens", again is not null, "the policy opens again.");
            if (again is null)
            {
                return;
            }

            checks.Record($"{name}.stateKept", again.IsSelected(GpeditDialog.Enabled), "Enabled is shown after the reopen.");
            checks.Record($"{name}.valueKept", verify(again), $"the value that was entered is shown after the reopen ({DescribeReadout(again)}).");
            checks.Record($"{name}.enabledAfterReopen", again.OptionsEnabled(), "the options are enabled for an Enabled policy.");
            checks.Record($"{name}.cleanAfterReopen", !again.IsEnabled(GpeditDialog.Apply), "Apply is disabled after the reopen.");
            Close(again);
        }
    }

    // ---- 9, 10: list elements and Show Contents ---------------------------------------------------

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Options_List()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("options", "list");
        GpeditSandbox sandbox = GpeditSandbox.Create("gpedit-options-list");
        sandbox.DialogEnabled = true;
        sandbox.SeedStandard();
        sandbox.SeedLists();
        using GpeditRun run = GpeditRun.Open("gpedit-options-list", sandbox);
        run.GoTo(Root, Computer, Admin, Oracle, "Elements", "Lists");

        // A plain list: one column, add rows by typing, delete one, OK.
        const string PlainButton = "buttonPlain entries:";
        using (GpeditDialog? dialog = GpeditDialog.Open(run, "List with default naming setting"))
        {
            checks.Record("plain.opens", dialog is not null, "the plain list policy opens.");
            if (dialog is null)
            {
                checks.AssertAllPassed();
                return;
            }

            checks.Record("plain.buttonName", ShellRun.SafeName(dialog.Find(PlainButton, 2)) == "Show...", $"button name='{ShellRun.SafeName(dialog.Find(PlainButton, 2))}'.");
            checks.Record("plain.buttonDisabled", !dialog.IsEnabled(PlainButton), "the Show... button is disabled while the policy is Not Configured.");
            _ = dialog.Select(GpeditDialog.Enabled);
            checks.Record("plain.buttonEnabled", dialog.IsEnabled(PlainButton), "the Show... button is enabled once Enabled is selected.");
            _ = dialog.Invoke(GpeditDialog.Apply);

            // Classic, B/Pol_List_Plain operation 0: Enabled with the empty list is refused with a message box, the dialog stays
            // open and Apply stays enabled. The G3 expectation, that an empty list can be applied, was wrong.
            (string Caption, string Text)? emptyRefusal = dialog.WaitMessage();
            checks.Record("plain.baseline", emptyRefusal is not null && dialog.IsEnabled(GpeditDialog.Apply), $"an Enabled list with no rows is refused with a message box (B/Pol_List_Plain operation 0) and Apply stays enabled: message={emptyRefusal?.Text}.");
            if (emptyRefusal is not null)
            {
                _ = dialog.DismissMessage();
            }

            _ = dialog.Invoke(PlainButton);
            GpeditContents? contents = GpeditContents.Wait(dialog);
            checks.Record("plain.contentsOpens", contents is not null, "Show... opens the Show Contents dialog.");
            if (contents is null)
            {
                checks.AssertAllPassed();
                return;
            }

            checks.Record("plain.caption", contents.Caption == ResourceText("Options", "ContentsCaption"), $"caption='{contents.Caption}'.");
            checks.Record("plain.oneColumn", contents.CellName(0, 0) == "Value, new row" && contents.Cell(0, 1) is null, $"first cell name='{contents.CellName(0, 0)}', second column cell present={contents.Cell(0, 1) is not null}.");
            _ = ClassicDialogTestsBridge.EnsureForeground(ClassicDialogTestsBridge.HandleOf(contents.Cell(0, 0)!));
            contents.Cell(0, 0)?.Focus();
            Thread.Sleep(150);
            Keyboard.Type(VirtualKeyShort.ESCAPE);
            Thread.Sleep(200);
            bool escapeFromNewRowCloses = contents.WaitClosed();
            checks.Record("plain.escapeFromNewRowCloses", escapeFromNewRowCloses, "Escape from the untouched trailing row cancels Show Contents.");
            if (!escapeFromNewRowCloses)
            {
                _ = contents.Cancel();
                _ = contents.WaitClosed();
            }

            checks.Record("plain.escapeFromNewRowKeeps", ListData(dialog, "PlainList").Length == 0, "Escape from the untouched trailing row leaves the list empty.");
            _ = dialog.Invoke(PlainButton);
            contents = GpeditContents.Wait(dialog);
            checks.Record("plain.escapeReopens", contents is not null, "Show Contents reopens after Escape from its untouched trailing row.");
            if (contents is null)
            {
                checks.AssertAllPassed();
                return;
            }

            contents.Edit(0, 0, "one");
            contents.Cell(1, 0)?.Focus();
            Thread.Sleep(150);
            contents.Edit(1, 0, "two");
            contents.Edit(2, 0, "three");
            checks.Record("plain.rowsTyped", contents.Rows(1).Select(r => r[0]).SequenceEqual(["one", "two", "three"]), $"rows={string.Join("|", contents.Rows(1).Select(r => r[0]))}.");
            contents.DeleteRow(1);
            checks.Record("plain.rowDeleted", contents.Rows(1).Select(r => r[0]).SequenceEqual(["one", "three"]), $"rows={string.Join("|", contents.Rows(1).Select(r => r[0]))}.");
            _ = contents.Cancel();
            checks.Record("plain.cancelCloses", contents.WaitClosed(), "Cancel closes Show Contents.");
            checks.Record("plain.cancelKeeps", ListData(dialog, "PlainList").Length == 0 && dialog.IsEnabled(GpeditDialog.Apply), $"after Cancel the list holds {ListData(dialog, "PlainList").Length} entries and Apply is {(dialog.IsEnabled(GpeditDialog.Apply) ? "enabled" : "disabled")}; the refused Apply above left the state change pending, as classic does (B/Pol_List_Plain operation 0).");

            _ = dialog.Invoke(PlainButton);
            contents = GpeditContents.Wait(dialog);
            checks.Record("plain.reopenEmpty", contents is not null && contents.Rows(1).Count == 0, "Cancel discarded the rows: the grid opens empty again.");
            if (contents is not null)
            {
                contents.Edit(0, 0, "one");
                contents.Edit(1, 0, "two");
                contents.Edit(2, 0, "three");
                contents.DeleteRow(1);
                string trailingCellName = contents.CellName(2, 0);
                _ = ClassicDialogTestsBridge.EnsureForeground(ClassicDialogTestsBridge.HandleOf(contents.Cell(2, 0)!));
                contents.Cell(2, 0)?.Focus();
                Thread.Sleep(150);
                bool trailingCellFocused = dialog.FocusedId() == "dataGrid.R2C0";
                Keyboard.Type(VirtualKeyShort.RETURN);
                Thread.Sleep(200);
                bool enterFromNewRowCloses = contents.WaitClosed();
                checks.Record(
                    "plain.enterFromNewRowCloses",
                    trailingCellFocused && trailingCellName == "Value, new row" && enterFromNewRowCloses,
                    $"Enter on the untouched trailing row closed Show Contents={enterFromNewRowCloses}, focused trailing cell={trailingCellFocused}, cell name='{trailingCellName}'.");
                if (!enterFromNewRowCloses)
                {
                    _ = contents.Ok();
                    _ = contents.WaitClosed();
                }
            }

            checks.Record("plain.okKeeps", ListData(dialog, "PlainList").SequenceEqual(["one", "three"]), $"entries={string.Join("|", ListData(dialog, "PlainList"))}.");
            checks.Record("plain.okEnablesApply", ShellRun.WaitUntil(() => dialog.IsEnabled(GpeditDialog.Apply), TimeSpan.FromSeconds(3)), "OK with a changed list enables Apply.");
            _ = dialog.Invoke(GpeditDialog.Apply);
            checks.Record("plain.applied", ShellRun.WaitUntil(() => !dialog.IsEnabled(GpeditDialog.Apply), TimeSpan.FromSeconds(3)), "Apply with the list succeeds.");

            // A duplicate value is refused with the focus on the cell.
            _ = dialog.Invoke(PlainButton);
            contents = GpeditContents.Wait(dialog);
            checks.Record("plain.reopenWithRows", contents is not null && contents.Rows(1).Select(r => r[0]).SequenceEqual(["one", "three"]), "after Apply the grid shows the applied rows.");
            if (contents is not null)
            {
                contents.Edit(2, 0, "one");
                _ = contents.Ok();
                (string Caption, string Text)? message = dialog.WaitMessage();
                string expected = string.Format(CultureInfo.CurrentCulture, ResourceText("Options", "ContentsDuplicateValue"), "one");
                checks.Record("plain.duplicateMessage", message is not null && message.Value.Text.Trim() == expected.Trim() && message.Value.Caption.Trim() == ResourceText("Options", "ContentsErrorCaption").Trim(), $"message={message?.Caption}|{message?.Text}.");
                _ = dialog.DismissMessage();
                checks.Record("plain.duplicateStays", contents.IsOpen, "the Show Contents dialog stays open after the message.");
                checks.Record("plain.duplicateFocus", ShellRun.WaitUntil(() => dialog.FocusedId() == "dataGrid.R2C0", TimeSpan.FromSeconds(3)), $"focus='{dialog.FocusedId()}', expected the offending cell dataGrid.R2C0.");
                _ = contents.Cancel();
                _ = contents.WaitClosed();
            }

            _ = dialog.Invoke(GpeditDialog.Ok);
            _ = dialog.WaitClosed();
        }

        // An explicit list: two columns, name first, the stored rows are shown.
        const string ExplicitButton = "buttonName and value entries:";
        using (GpeditDialog? dialog = GpeditDialog.Open(run, "List with explicit values setting"))
        {
            checks.Record("explicit.opens", dialog is not null, "the explicit list policy opens.");
            if (dialog is not null)
            {
                checks.Record("explicit.enabled", dialog.IsSelected(GpeditDialog.Enabled) && dialog.IsEnabled(ExplicitButton), "the seeded list reads as Enabled and Show... is enabled.");
                _ = dialog.Invoke(ExplicitButton);
                GpeditContents? contents = GpeditContents.Wait(dialog);
                checks.Record("explicit.contentsOpens", contents is not null, "Show... opens the Show Contents dialog.");
                if (contents is not null)
                {
                    checks.Record(
                        "explicit.columns",
                        contents.CellName(0, 0).StartsWith("Value name,", StringComparison.Ordinal) && contents.CellName(0, 1).StartsWith("Value,", StringComparison.Ordinal),
                        $"column names '{contents.CellName(0, 0)}' and '{contents.CellName(0, 1)}': the name column is first.");
                    List<string[]> rows = contents.Rows(2);
                    checks.Record("explicit.rows", rows.Count == 2 && rows[0].SequenceEqual(["first", "one"]) && rows[1].SequenceEqual(["second", "two"]), $"rows={string.Join(";", rows.Select(r => string.Join("=", r)))}.");

                    // An explicit name with empty data is valid and reaches the policy value unchanged.
                    contents.Edit(2, 0, "third");
                    _ = contents.Ok();
                    bool nameOnlyClosed = contents.WaitClosed();
                    JsonElement? explicitValue = dialog.ValueOf("ExplicitList");
                    bool nameOnlyStored = explicitValue is { } value && value.GetProperty("entries").EnumerateArray().Any(entry =>
                        entry.GetProperty("n").GetString() == "third" && entry.GetProperty("d").GetString() == string.Empty);
                    checks.Record("explicit.nameOnlyAccepted", nameOnlyClosed && nameOnlyStored && dialog.IsEnabled(GpeditDialog.Apply), $"closed={nameOnlyClosed}, stored={nameOnlyStored}, apply={dialog.IsEnabled(GpeditDialog.Apply)}.");

                    _ = dialog.Invoke(ExplicitButton);
                    contents = GpeditContents.Wait(dialog);
                    checks.Record("explicit.nameOnlyShown", contents is not null && contents.Rows(2).Any(row => row.SequenceEqual(["third", string.Empty])), "the empty explicit value is present when Show Contents reopens.");
                    if (contents is not null)
                    {
                        // An empty explicit name is still refused.
                        contents.Edit(3, 1, "x");
                        _ = contents.Ok();
                        (string Caption, string Text)? message = dialog.WaitMessage();
                        string expected = ResourceText("Options", "ContentsEmptyName");
                        checks.Record("explicit.emptyNameMessage", message is not null && message.Value.Text.Trim() == expected.Trim(), $"message={message?.Caption}|{message?.Text}.");
                        _ = dialog.DismissMessage();
                        checks.Record("explicit.emptyNameFocus", ShellRun.WaitUntil(() => dialog.FocusedId() == "dataGrid.R3C0", TimeSpan.FromSeconds(3)), $"focus='{dialog.FocusedId()}', expected the empty name cell dataGrid.R3C0.");

                        // The same name twice is still refused.
                        contents.Edit(3, 0, "first");
                        _ = contents.Ok();
                        message = dialog.WaitMessage();
                        expected = string.Format(CultureInfo.CurrentCulture, ResourceText("Options", "ContentsDuplicateName"), "first");
                        checks.Record("explicit.duplicateNameMessage", message is not null && message.Value.Text.Trim() == expected.Trim(), $"message={message?.Caption}|{message?.Text}.");
                        _ = dialog.DismissMessage();
                        _ = contents.Cancel();
                        _ = contents.WaitClosed();
                    }
                }

                Close(dialog);
            }
        }

        // An additive list: a row removed in Show Contents is in ListValue.Removed and leaves the file.
        const string AdditiveButton = "buttonAdditive entries:";
        using (GpeditDialog? dialog = GpeditDialog.Open(run, "Additive list setting"))
        {
            checks.Record("additive.opens", dialog is not null, "the additive list policy opens.");
            if (dialog is not null)
            {
                _ = dialog.Invoke(AdditiveButton);
                GpeditContents? contents = GpeditContents.Wait(dialog);
                checks.Record("additive.contentsOpens", contents is not null, "Show... opens the Show Contents dialog.");
                if (contents is not null)
                {
                    checks.Record("additive.rows", contents.Rows(1).Select(r => r[0]).SequenceEqual(["alpha", "bravo", "charlie"]), $"rows={string.Join("|", contents.Rows(1).Select(r => r[0]))}.");
                    contents.DeleteRow(1);
                    _ = contents.Ok();
                    _ = contents.WaitClosed();
                }

                JsonElement? value = dialog.ValueOf("AdditiveList");
                string[] entries = ListData(dialog, "AdditiveList");
                string[] removed = RemovedOf(value);
                checks.Record("additive.entries", entries.SequenceEqual(["alpha", "charlie"]), $"entries={string.Join("|", entries)}.");
                checks.Record("additive.removed", removed.SequenceEqual(["bravo=bravo"]), $"removed={string.Join("|", removed)}, expected the deleted entry with its stored name (bravo=bravo).");
                _ = dialog.Invoke(GpeditDialog.Apply);
                checks.Record("additive.applied", ShellRun.WaitUntil(() => !dialog.IsEnabled(GpeditDialog.Apply), TimeSpan.FromSeconds(3)), "Apply with the removed entry succeeds.");
                _ = dialog.Invoke(GpeditDialog.Ok);
                _ = dialog.WaitClosed();
            }
        }

        using (GpeditDialog? again = GpeditDialog.Open(run, "Additive list setting"))
        {
            checks.Record("additive.reopens", again is not null, "the additive list policy opens again.");
            if (again is not null)
            {
                checks.Record("additive.fileHasNoDeleted", ListData(again, "AdditiveList").SequenceEqual(["alpha", "charlie"]), $"entries after the reopen={string.Join("|", ListData(again, "AdditiveList"))}: the removed entry left the policy.");
                Close(again);
            }
        }

        run.WriteArtifacts("gpedit-options-list.png");
        checks.AssertAllPassed();
    }

    // ---- 11: an ampersand in a label ------------------------------------------------------------

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Options_Literal()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("options", "literal");
        using GpeditRun run = Start("gpedit-options-literal", out _);
        run.GoTo(Root, Computer, Admin, Oracle, "Tools & Utilities");

        using (GpeditDialog? dialog = GpeditDialog.Open(run, "Labels with single ampersands setting"))
        {
            checks.Record("single.opens", dialog is not null, "the policy with ampersands in every label opens.");
            if (dialog is null)
            {
                checks.AssertAllPassed();
                return;
            }

            Dictionary<string, string> names = new[] { AmpText, AmpCheck, AmpSpin + ".Text", AmpDrop, "labelName & address:", "labelRock & roll count:", "labelPick & choose:" }
                .ToDictionary(id => id, id => ShellRun.SafeName(dialog.Find(id, 1)));
            checks.Record("single.textName", names[AmpText] == "Name & address:", $"text box name='{names[AmpText]}'.");
            checks.Record("single.checkName", names[AmpCheck] == "Save & close", $"check box name='{names[AmpCheck]}'.");
            checks.Record("single.spinName", names[AmpSpin + ".Text"] == "Rock & roll count:", $"number box name='{names[AmpSpin + ".Text"]}'.");
            checks.Record("single.dropName", names[AmpDrop] == "Pick & choose:", $"drop-down name='{names[AmpDrop]}'.");
            checks.Record(
                "single.labels",
                names["labelName & address:"] == "Name & address:" && names["labelRock & roll count:"] == "Rock & roll count:" && names["labelPick & choose:"] == "Pick & choose:",
                $"label names={names["labelName & address:"]}|{names["labelRock & roll count:"]}|{names["labelPick & choose:"]}.");
            string[] items = dialog.ItemsOf("AmpChoice");
            checks.Record("single.items", items.SequenceEqual(["Salt & Pepper", "Tom & Jerry"]), $"drop-down items={string.Join("|", items)}.");

            // None of the labels is an access key: Alt+N (Name), Alt+S (Save), Alt+R (Rock), Alt+P (Pick) do not move the focus
            // into the pane. The dialog keeps its own access keys (Alt+E selects Enabled).
            _ = dialog.Select(GpeditDialog.NotConfigured);
            _ = dialog.Select(GpeditDialog.Enabled);

            // G4 added this choice: the drop-down has no default item, so Alt+N (Next Setting) would be refused for the
            // missing choice, as in classic, and the message box would take the keys that follow.
            _ = dialog.Choose(AmpDrop, "Tom & Jerry");
            foreach (char letter in "NSRP")
            {
                _ = ClassicDialogTestsBridge.EnsureForeground(dialog.Hwnd);
                _ = Bwc.E2E.Dialogs.ClassicDialogTests.SendAltLetter(letter);
                Thread.Sleep(250);
            }

            string focused = dialog.FocusedId();
            checks.Record("single.noAccessKey", !focused.Contains("&", StringComparison.Ordinal) && dialog.Find(GpeditDialog.Ok, 1) is not null, $"focus after Alt+N, S, R, P is '{focused}'.");
            _ = dialog.Select(GpeditDialog.Disabled);
            _ = Bwc.E2E.Dialogs.ClassicDialogTests.SendAltLetter('E');
            checks.Record("single.dialogKeysWork", ShellRun.WaitUntil(() => dialog.IsSelected(GpeditDialog.Enabled), TimeSpan.FromSeconds(3)), "Alt+E still selects Enabled.");
            Close(dialog);
        }

        using (GpeditDialog? dialog = GpeditDialog.Open(run, "Label with a double ampersand setting"))
        {
            checks.Record("double.opens", dialog is not null, "the policy with a double ampersand label opens.");
            if (dialog is not null)
            {
                string name = ShellRun.SafeName(dialog.Find("textBoxFish && Chips:", 2));
                checks.Record("double.name", name == "Fish && Chips:", $"text box name='{name}': both ampersands stay (literal text).");
                Close(dialog);
            }
        }

        run.WriteArtifacts("gpedit-options-literal.png");
        checks.AssertAllPassed();
    }

    // ---- 13: validation ---------------------------------------------------------------------------

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Options_Validation()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("options", "validation");
        using GpeditRun run = Start("gpedit-options-validation", out _);

        run.GoTo(Root, Computer, Admin, Oracle, "Elements", "Numbers");
        using (GpeditDialog? dialog = GpeditDialog.Open(run, "Decimal with a range setting"))
        {
            checks.Record("number.opens", dialog is not null, "the number policy opens.");
            if (dialog is null)
            {
                checks.AssertAllPassed();
                return;
            }

            const string Field = "Number from 10 to 500";
            _ = dialog.Select(GpeditDialog.Enabled);
            _ = dialog.TypeInto(MinMaxSpin + ".Text", "5");
            _ = dialog.Invoke(GpeditDialog.Apply);
            (string Caption, string Text)? message = dialog.WaitMessage();
            string caption = string.Format(CultureInfo.CurrentCulture, ResourceText("Options", "OptionsNumericCaption"), Field);
            string text = string.Format(CultureInfo.CurrentCulture, ResourceText("Options", "OptionsMinText"), 10, 5);
            checks.Record("number.minMessage", message is not null && message.Value.Caption.Trim() == caption.Trim() && message.Value.Text == text, $"message={message?.Caption}|{message?.Text}.");
            _ = dialog.DismissMessage();
            checks.Record("number.minReset", ShellRun.WaitUntil(() => dialog.TextOf(MinMaxSpin + ".Text") == "10", TimeSpan.FromSeconds(3)), $"the box shows '{dialog.TextOf(MinMaxSpin + ".Text")}', expected the minimum 10.");
            checks.Record("number.minFocus", ShellRun.WaitUntil(() => dialog.FocusedId() == MinMaxSpin + ".Text", TimeSpan.FromSeconds(3)), $"focus='{dialog.FocusedId()}'.");
            checks.Record("number.minNothingApplied", CellsOf(run, "Decimal with a range setting").ElementAtOrDefault(1) == "Not configured" && dialog.IsEnabled(GpeditDialog.Apply), $"list cells={string.Join("|", CellsOf(run, "Decimal with a range setting"))}; Apply enabled={dialog.IsEnabled(GpeditDialog.Apply)}.");

            _ = dialog.TypeInto(MinMaxSpin + ".Text", "600");
            _ = dialog.Invoke(GpeditDialog.Apply);
            message = dialog.WaitMessage();
            text = string.Format(CultureInfo.CurrentCulture, ResourceText("Options", "OptionsMaxText"), 500, 600);
            checks.Record("number.maxMessage", message is not null && message.Value.Text == text, $"message={message?.Caption}|{message?.Text}.");
            _ = dialog.DismissMessage();
            checks.Record("number.maxReset", ShellRun.WaitUntil(() => dialog.TextOf(MinMaxSpin + ".Text") == "500", TimeSpan.FromSeconds(3)), $"the box shows '{dialog.TextOf(MinMaxSpin + ".Text")}', expected the maximum 500.");
            _ = dialog.Invoke(GpeditDialog.Apply);
            checks.Record("number.appliesAfterReset", ShellRun.WaitUntil(() => !dialog.IsEnabled(GpeditDialog.Apply), TimeSpan.FromSeconds(3)) && CellsOf(run, "Decimal with a range setting").ElementAtOrDefault(1) == "Enabled", $"list cells={string.Join("|", CellsOf(run, "Decimal with a range setting"))}.");
            _ = dialog.Screenshot("gpedit-options-validation-number.png");
            Close(dialog);
        }

        run.GoTo(Root, Computer, Admin, Oracle, "Elements", "Texts");
        using (GpeditDialog? dialog = GpeditDialog.Open(run, "Text that is required setting"))
        {
            checks.Record("required.opens", dialog is not null, "the required text policy opens.");
            if (dialog is not null)
            {
                _ = dialog.Select(GpeditDialog.Enabled);
                _ = dialog.Invoke(GpeditDialog.Ok);
                (string Caption, string Text)? message = dialog.WaitMessage();
                string caption = string.Format(CultureInfo.CurrentCulture, ResourceText("Dialog", "DialogRequiredCaption"), "Required text");
                checks.Record("required.message", message is not null && message.Value.Caption.Trim() == caption.Trim() && message.Value.Text == ResourceText("Dialog", "DialogRequiredText"), $"message={message?.Caption}|{message?.Text}.");
                _ = dialog.DismissMessage();
                checks.Record("required.focus", ShellRun.WaitUntil(() => dialog.FocusedId() == RequiredBox, TimeSpan.FromSeconds(3)), $"focus='{dialog.FocusedId()}', expected the offending box {RequiredBox}.");
                checks.Record("required.staysOpen", dialog.Find(GpeditDialog.Ok, 2) is not null && dialog.IsEnabled(GpeditDialog.Apply), "the dialog stays open and Apply stays enabled.");
                Close(dialog);
            }
        }

        using (GpeditDialog? dialog = GpeditDialog.Open(run, "Text with an unsorted required combo box setting"))
        {
            checks.Record("combo.opens", dialog is not null, "the required combo box policy opens.");
            if (dialog is not null)
            {
                _ = dialog.Select(GpeditDialog.Enabled);
                _ = dialog.Invoke(GpeditDialog.Apply);
                (string Caption, string Text)? message = dialog.WaitMessage();
                string caption = string.Format(CultureInfo.CurrentCulture, ResourceText("Dialog", "DialogRequiredCaption"), "Pick or type a size");
                checks.Record("combo.message", message is not null && message.Value.Caption.Trim() == caption.Trim(), $"message={message?.Caption}|{message?.Text}.");
                _ = dialog.DismissMessage();
                Close(dialog);
            }
        }

        run.GoTo(Root, User, Admin, Oracle, "Elements", "Texts");
        using (GpeditDialog? dialog = GpeditDialog.Open(run, "Multiple text lines with limits setting"))
        {
            checks.Record("multi.opens", dialog is not null, "the limited multi-line policy opens.");
            if (dialog is not null)
            {
                const string Box = "multiTextBoxLimitedLines";
                _ = dialog.Select(GpeditDialog.Enabled);
                _ = dialog.Invoke(GpeditDialog.Apply);
                (string Caption, string Text)? message = dialog.WaitMessage();
                string caption = string.Format(CultureInfo.CurrentCulture, ResourceText("Options", "OptionsEmptyLinesCaption"), "LimitedLines");
                checks.Record("multi.emptyMessage", message is not null && message.Value.Caption.Trim() == caption.Trim() && message.Value.Text == ResourceText("Options", "OptionsEmptyLinesText"), $"message={message?.Caption}|{message?.Text}.");
                _ = dialog.DismissMessage();
                checks.Record("multi.emptyFocus", ShellRun.WaitUntil(() => dialog.FocusedId() == Box, TimeSpan.FromSeconds(3)), $"focus='{dialog.FocusedId()}'.");

                TypeLines(dialog, Box, "a", "b", "c", "d");
                _ = dialog.Invoke(GpeditDialog.Apply);
                message = dialog.WaitMessage();
                caption = string.Format(CultureInfo.CurrentCulture, ResourceText("Options", "OptionsCountCaption"), "LimitedLines");
                string text = string.Format(CultureInfo.CurrentCulture, ResourceText("Options", "OptionsCountText"), 3, 3);
                checks.Record("multi.countMessage", message is not null && message.Value.Caption.Trim() == caption.Trim() && message.Value.Text == text, $"message={message?.Caption}|{message?.Text}.");
                _ = dialog.DismissMessage();

                TypeLines(dialog, Box, "0123456789", "0123456789", "0123456789");
                _ = dialog.Invoke(GpeditDialog.Apply);
                message = dialog.WaitMessage();
                caption = string.Format(CultureInfo.CurrentCulture, ResourceText("Options", "OptionsLengthCaption"), "LimitedLines");
                text = string.Format(CultureInfo.CurrentCulture, ResourceText("Options", "OptionsLengthText"), 20, 20);
                checks.Record("multi.lengthMessage", message is not null && message.Value.Caption.Trim() == caption.Trim() && message.Value.Text == text, $"message={message?.Caption}|{message?.Text}.");
                _ = dialog.DismissMessage();
                checks.Record("multi.nothingApplied", CellsOf(run, "Multiple text lines with limits setting").ElementAtOrDefault(1) == "Not configured", $"list cells={string.Join("|", CellsOf(run, "Multiple text lines with limits setting"))}.");
                Close(dialog);
            }
        }

        run.WriteArtifacts("gpedit-options-validation.png");
        checks.AssertAllPassed();

        static void TypeLines(GpeditDialog dialog, string id, params string[] lines)
        {
            _ = dialog.FocusControl(id);
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            Keyboard.Type(VirtualKeyShort.DELETE);
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    Keyboard.Type(VirtualKeyShort.RETURN);
                }

                Keyboard.Type(lines[i]);
            }

            Thread.Sleep(300);
        }
    }

    // ---- 14: tab order -----------------------------------------------------------------------------

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Options_TabOrder()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("options", "taborder");
        using GpeditRun run = Start("gpedit-options-taborder", out _);
        run.GoTo(Root, Computer, Admin, Oracle, "Tools & Utilities");

        using GpeditDialog? dialog = GpeditDialog.Open(run, "Labels with single ampersands setting");
        checks.Record("opens", dialog is not null, "the policy with four elements opens.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        // Not Configured: the option controls are disabled, so Tab goes from Supported on straight to Help.
        _ = dialog.FocusControl(GpeditDialog.SupportedOn);
        string[] plain = dialog.TabSequence(1);
        checks.Record("disabledSkipped", plain.SequenceEqual([GpeditDialog.Help]), $"Tab after Supported on goes to '{string.Join(",", plain)}', expected the Help box.");

        _ = dialog.Select(GpeditDialog.Enabled);
        _ = dialog.FocusControl(GpeditDialog.SupportedOn);
        string[] forward = dialog.TabSequence(8);
        string[] expected = [AmpText, AmpCheck, AmpSpin + ".Text", AmpDrop, GpeditDialog.Help, GpeditDialog.Ok, GpeditDialog.Cancel, GpeditDialog.Apply];
        checks.Record("forward", forward.SequenceEqual(expected), $"tabs={string.Join(",", forward)}.");
        run.Measure("tabsForward", forward);

        _ = dialog.FocusControl(GpeditDialog.Help);
        var backward = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            string before = dialog.FocusedId();
            dialog.PressShiftTab();
            _ = ShellRun.WaitUntil(() => dialog.FocusedId() != before, TimeSpan.FromSeconds(1));
            backward.Add(dialog.FocusedId());
        }

        string[] expectedBack = [AmpDrop, AmpSpin + ".Text", AmpCheck, AmpText, GpeditDialog.SupportedOn];
        checks.Record("backward", backward.SequenceEqual(expectedBack), $"shift-tabs={string.Join(",", backward)}.");
        run.Measure("tabsBackward", backward);

        _ = dialog.FocusControl(GpeditDialog.Disabled);
        _ = dialog.Select(GpeditDialog.Disabled);
        _ = dialog.FocusControl(GpeditDialog.SupportedOn);
        string[] afterDisabled = dialog.TabSequence(1);
        checks.Record("disabledAgain", afterDisabled.SequenceEqual([GpeditDialog.Help]), $"after Disabled, Tab after Supported on goes to '{string.Join(",", afterDisabled)}'.");
        _ = dialog.Screenshot("gpedit-options-taborder-dialog.png");
        Close(dialog);
        run.WriteArtifacts("gpedit-options-taborder.png");
        checks.AssertAllPassed();
    }

    // ---- 15: layout --------------------------------------------------------------------------------

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Options_Layout()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("options", "layout");
        using GpeditRun run = Start("gpedit-options-layout", out _);
        run.GoTo(Root, Computer, Admin, Oracle, "Tools & Utilities");

        using GpeditDialog? dialog = GpeditDialog.Open(run, "Labels with single ampersands setting");
        checks.Record("opens", dialog is not null, "the policy with four elements opens.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        RectangleF? pane = dialog.RectDip(GpeditDialog.OptionsPane);
        RectangleF? text = dialog.RectDip(AmpText);
        RectangleF? check = dialog.RectDip(AmpCheck);
        RectangleF? spin = dialog.RectDip(AmpSpin);
        RectangleF? drop = dialog.RectDip(AmpDrop);
        RectangleF? spinLabel = dialog.RectDip("labelRock & roll count:");
        RectangleF? dropLabel = dialog.RectDip("labelPick & choose:");
        RectangleF? textLabel = dialog.RectDip("labelName & address:");
        checks.Record("allFound", pane is not null && text is not null && check is not null && spin is not null && drop is not null && spinLabel is not null && dropLabel is not null && textLabel is not null, "every control and label has a rectangle.");
        if (pane is not { } p || text is not { } t || check is not { } c || spin is not { } s || drop is not { } d || spinLabel is not { } sl || dropLabel is not { } dl || textLabel is not { } tl)
        {
            checks.AssertAllPassed();
            return;
        }

        static bool Near(float actual, double expected) => Math.Abs(actual - expected) <= Tolerance;
        checks.Record("textSize", Near(t.Width, 220) && Near(t.Height, 23), $"text box {t.Width:0.#} x {t.Height:0.#}, expected 220 x 23.");
        checks.Record("spinSize", Near(s.Width, 120) && Near(s.Height, 23), $"number box {s.Width:0.#} x {s.Height:0.#}, expected 120 x 23.");
        checks.Record("dropSize", Near(d.Width, 143) && Near(d.Height, 24), $"drop-down {d.Width:0.#} x {d.Height:0.#}, expected 143 x 24.");
        checks.Record("checkHeight", Near(c.Height, 22), $"check box height {c.Height:0.#}, expected 22.");
        checks.Record("labelHeight", Near(sl.Height, 26) && Near(dl.Height, 26) && Near(tl.Height, 26), $"label heights {sl.Height:0.#}, {dl.Height:0.#}, {tl.Height:0.#}, expected 26.");
        checks.Record("spinGap", Near(s.X - (sl.X + sl.Width), 6), $"gap from the label to the number box {(s.X - (sl.X + sl.Width)):0.#}, expected 6.");
        checks.Record("dropGap", Near(d.X - (dl.X + dl.Width), 6), $"gap from the label to the drop-down {(d.X - (dl.X + dl.Width)):0.#}, expected 6.");
        checks.Record("textBelowLabel", Near(t.Y - (tl.Y + tl.Height), 3) && Near(t.X, tl.X), $"text box top is {(t.Y - (tl.Y + tl.Height)):0.#} below its label, expected 3.");
        checks.Record("inset", Near(tl.X - p.X, 4) && Near(tl.Y - p.Y, 1 + 0), $"first label at {(tl.X - p.X):0.#},{(tl.Y - p.Y):0.#} from the pane, expected 4,1 (the pane has a 1 DIP border: the controls start 1 DIP in).");

        RectangleF[] all = [tl, t, c, sl, s, dl, d];
        bool overlap = false;
        for (int i = 0; i < all.Length && !overlap; i++)
        {
            for (int j = i + 1; j < all.Length; j++)
            {
                RectangleF inter = RectangleF.Intersect(all[i], all[j]);
                if (inter.Width > 0.5 && inter.Height > 0.5)
                {
                    overlap = true;
                    break;
                }
            }
        }

        checks.Record("noOverlap", !overlap, "no two controls overlap.");
        checks.Record("rowsFollow", t.Y > tl.Y && c.Y > t.Y && s.Y >= c.Y && d.Y >= s.Y && (d.Y - s.Y) <= 40, $"tops: label {tl.Y:0.#}, text {t.Y:0.#}, check {c.Y:0.#}, number {s.Y:0.#}, drop-down {d.Y:0.#}.");
        run.Measure("layout", new[] { $"text {t}", $"check {c}", $"spin {s}", $"drop {d}", $"pane {p}" });
        _ = dialog.Screenshot("gpedit-options-layout-dialog.png");
        Close(dialog);
        run.WriteArtifacts("gpedit-options-layout.png");
        checks.AssertAllPassed();
    }

    // ---- 12, 16: Previous Setting and Next Setting ---------------------------------------------------

    [TestMethod]
    [TestCategory("Gpedit")]
    [TestCategory("E2EOnly")]
    public void Gpedit_Options_Navigation()
    {
        E2EGuards.RequireE2EOnlyFlavor(GuiGate.Config);
        var checks = new GpeditChecks("options", "navigation");
        using GpeditRun run = Start("gpedit-options-navigation", out _);
        run.GoTo(Root, Computer, Admin, Oracle, "Tools & Utilities");

        using GpeditDialog? dialog = GpeditDialog.Open(run, "Label with a double ampersand setting");
        checks.Record("opens", dialog is not null, "the first policy of the category opens.");
        if (dialog is null)
        {
            checks.AssertAllPassed();
            return;
        }

        const string FishBox = "textBoxFish && Chips:";
        checks.Record("firstControls", dialog.ElementIds().SequenceEqual(["DoubleAmpText"]), $"elements={string.Join(",", dialog.ElementIds())}.");
        _ = dialog.Select(GpeditDialog.Enabled);
        _ = dialog.TypeInto(FishBox, "abc");
        checks.Record("typed", Text(dialog, "DoubleAmpText") == "abc", $"text='{Text(dialog, "DoubleAmpText")}'.");

        // Next applies the pending change like Apply, then shows the next policy with its own controls.
        _ = dialog.Invoke(GpeditDialog.Next);
        checks.Record("moved", ShellRun.WaitUntil(() => dialog.Read("Policy").EndsWith("Pol_Amp_LabelSingle", StringComparison.Ordinal), TimeSpan.FromSeconds(5)), $"policy={dialog.Read("Policy")}.");
        checks.Record(
            "controlsReplaced",
            dialog.ElementIds().OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(["AmpCheck", "AmpChoice", "AmpNumber", "AmpText"]) && dialog.Find(FishBox, 1) is null && dialog.Find(AmpText, 2) is not null,
            $"elements={string.Join(",", dialog.ElementIds())}, the old text box is gone={dialog.Find(FishBox, 1) is null}.");
        checks.Record("newDefaults", Text(dialog, "AmpText") == "Fish & Chips" && !dialog.OptionsEnabled(), $"text='{Text(dialog, "AmpText")}', options enabled={dialog.OptionsEnabled()}: the new policy is Not Configured with its own defaults.");
        checks.Record("newClean", !dialog.IsEnabled(GpeditDialog.Apply), "Apply is disabled for the new policy.");

        _ = dialog.Invoke(GpeditDialog.Previous);
        checks.Record("movedBack", ShellRun.WaitUntil(() => dialog.Read("Policy").EndsWith("Pol_Amp_LabelDouble", StringComparison.Ordinal), TimeSpan.FromSeconds(5)), $"policy={dialog.Read("Policy")}.");
        checks.Record("appliedKept", dialog.IsSelected(GpeditDialog.Enabled) && Text(dialog, "DoubleAmpText") == "abc" && dialog.OptionsEnabled(), $"state enabled={dialog.IsSelected(GpeditDialog.Enabled)}, text='{Text(dialog, "DoubleAmpText")}': the pending change was applied by the move.");
        Close(dialog);
        run.WriteArtifacts("gpedit-options-navigation.png");
        checks.AssertAllPassed();
    }

    // ---- support -----------------------------------------------------------------------------------

    private static void AddUnsignedLongDecimalTemplate(GpeditSandbox sandbox)
    {
        const string policyName = "Pol_LongDecimal_UnsignedRange";
        const string elementId = "WideNumber";
        const string valueName = "WideNumber";
        const string key = @"Software\Policies\BWC-Oracle\Elements\Number";
        string minimum = (long.MaxValue + 1UL).ToString(CultureInfo.InvariantCulture);
        string maximum = ulong.MaxValue.ToString(CultureInfo.InvariantCulture);

        string admxPath = Path.Combine(sandbox.TemplateRoot, "BwcOracle.admx");
        XDocument admx = XDocument.Load(admxPath);
        XElement admxRoot = admx.Root ?? throw new InvalidOperationException("The copied oracle ADMX has no root element.");
        XNamespace admxNs = admxRoot.Name.Namespace;
        XElement policies = admxRoot.Element(admxNs + "policies") ?? throw new InvalidOperationException("The copied oracle ADMX has no policies element.");
        policies.Add(
            new XElement(admxNs + "policy",
                new XAttribute("name", policyName),
                new XAttribute("class", "Machine"),
                new XAttribute("displayName", $"$(string.{policyName})"),
                new XAttribute("explainText", $"$(string.{policyName}_Help)"),
                new XAttribute("presentation", $"$(presentation.{policyName})"),
                new XAttribute("key", key),
                new XElement(admxNs + "parentCategory", new XAttribute("ref", "Numbers")),
                new XElement(admxNs + "supportedOn", new XAttribute("ref", "bwcbase:SUPPORTED_BwcAny")),
                new XElement(admxNs + "elements",
                    new XElement(admxNs + "longDecimal",
                        new XAttribute("id", elementId),
                        new XAttribute("valueName", valueName),
                        new XAttribute("minValue", minimum),
                        new XAttribute("maxValue", maximum)))));
        admx.Save(admxPath);

        string admlPath = Path.Combine(sandbox.TemplateRoot, "en-US", "BwcOracle.adml");
        XDocument adml = XDocument.Load(admlPath);
        XElement admlRoot = adml.Root ?? throw new InvalidOperationException("The copied oracle ADML has no root element.");
        XNamespace admlNs = admlRoot.Name.Namespace;
        XElement resources = admlRoot.Element(admlNs + "resources") ?? throw new InvalidOperationException("The copied oracle ADML has no resources element.");
        XElement strings = resources.Element(admlNs + "stringTable") ?? throw new InvalidOperationException("The copied oracle ADML has no string table.");
        strings.Add(
            new XElement(admlNs + "string", new XAttribute("id", policyName), "Long decimal above signed maximum"),
            new XElement(admlNs + "string", new XAttribute("id", policyName + "_Help"), "A long decimal from long.MaxValue + 1 through ulong.MaxValue."));
        XElement presentations = resources.Element(admlNs + "presentationTable") ?? throw new InvalidOperationException("The copied oracle ADML has no presentation table.");
        presentations.Add(
            new XElement(admlNs + "presentation",
                new XAttribute("id", policyName),
                new XElement(admlNs + "longDecimalTextBox",
                    new XAttribute("refId", elementId),
                    new XAttribute("defaultValue", minimum),
                    new XAttribute("spin", "true"),
                    new XAttribute("spinStep", maximum),
                    "Wide number:")));
        adml.Save(admlPath);
    }

    private static GpeditRun Start(string name, out GpeditSandbox sandbox)
    {
        sandbox = GpeditSandbox.Create(name);
        sandbox.DialogEnabled = true;
        sandbox.SeedStandard();
        return GpeditRun.Open(name, sandbox);
    }

    private static void Close(GpeditDialog dialog)
    {
        dialog.Press(VirtualKeyShort.ESCAPE);
        _ = dialog.WaitClosed();
    }

    private static string[] CellsOf(GpeditRun run, string rowName) =>
        run.Rows().FirstOrDefault(r => r.Name == rowName)?.Cells ?? [];

    private static bool PaneContains(GpeditDialog dialog, IEnumerable<string> ids)
    {
        RectangleF? pane = dialog.RectDip(GpeditDialog.OptionsPane);
        if (pane is not { } p)
        {
            return false;
        }

        foreach (string id in ids)
        {
            RectangleF? r = dialog.RectDip(id);
            if (r is not { } rect || rect.Left < p.Left - 1 || rect.Top < p.Top - 1 || rect.Right > p.Right + 1 || rect.Bottom > p.Bottom + 1)
            {
                return false;
            }
        }

        return true;
    }

    private static string DescribeReadout(GpeditDialog dialog) =>
        string.Join(", ", dialog.OptionsReadout().Where(p => p.Key.StartsWith("V.", StringComparison.Ordinal)).Select(p => $"{p.Key}={p.Value}"));

    // The readout values are compact JSON: {"k":"num","v":"25"}, {"k":"text","v":"x"}, {"k":"bool","v":true},
    // {"k":"lines","v":["a"]}, {"k":"choice","v":1} and {"k":"list","entries":[{"n":null,"d":"x"}],"removed":[...]}.
    private static string Number(GpeditDialog dialog, string id) =>
        dialog.ValueOf(id) is { } v && v.GetProperty("k").GetString() == "num" ? v.GetProperty("v").GetString() ?? string.Empty : "<none>";

    private static string Text(GpeditDialog dialog, string id) =>
        dialog.ValueOf(id) is { } v && v.GetProperty("k").GetString() == "text" ? v.GetProperty("v").GetString() ?? string.Empty : "<none>";

    private static bool? Boolean(GpeditDialog dialog, string id) =>
        dialog.ValueOf(id) is { } v && v.GetProperty("k").GetString() == "bool" ? v.GetProperty("v").GetBoolean() : null;

    private static int Choice(GpeditDialog dialog, string id) =>
        dialog.ValueOf(id) is { } v && v.GetProperty("k").GetString() == "choice" ? v.GetProperty("v").GetInt32() : -1;

    private static string[] Lines(GpeditDialog dialog, string id) =>
        dialog.ValueOf(id) is { } v && v.GetProperty("k").GetString() == "lines"
            ? v.GetProperty("v").EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray()
            : ["<none>"];

    private static string[] ListData(GpeditDialog dialog, string id) =>
        dialog.ValueOf(id) is { } v && v.GetProperty("k").GetString() == "list"
            ? v.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("d").GetString() ?? string.Empty).ToArray()
            : ["<none>"];

    private static string[] RemovedOf(JsonElement? value) =>
        value is { } v && v.GetProperty("k").GetString() == "list"
            ? v.GetProperty("removed").EnumerateArray().Select(e => $"{e.GetProperty("n").GetString()}={e.GetProperty("d").GetString()}").ToArray()
            : ["<none>"];

    /// <summary>One string of the resource files of the editor library (Strings/en-US/&lt;file&gt;.resw).</summary>
    private static string ResourceText(string file, string key)
    {
        string path = Path.Combine(GuiGate.Config.RepoRoot, "src", "Bwc.Gpedit.Editor", "Strings", "en-US", file + ".resw");
        XDocument document = XDocument.Load(path);
        XElement? data = document.Root?.Elements("data").FirstOrDefault(e => (string?)e.Attribute("name") == key);
        return data?.Element("value")?.Value ?? $"<missing {key}>";
    }
}

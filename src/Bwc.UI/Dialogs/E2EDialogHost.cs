#if BWC_E2E
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Bwc.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.UI.Dialogs;

/// <summary>
/// E2E flavor only. Opens one test dialog over the main window of a head process, so the E2E tests can drive the 0.2.0
/// classic dialog additions (resizable frame, literal text, spin box, editable grid) before the policy dialog exists. It
/// starts only when <c>BWC_E2E_DIALOG_HOST</c> names a scenario, which only the tests set, and writes a JSON report to
/// the file named by <c>BWC_E2E_DIALOG_HOST_REPORT</c> when the dialog closes or fails
/// (docs/failure-modes/classic-dialog.md, section 14.20). Nothing here is compiled into the ship flavor.
/// </summary>
internal static class E2EDialogHost
{
    internal const string ScenarioVariable = "BWC_E2E_DIALOG_HOST";
    internal const string ReportVariable = "BWC_E2E_DIALOG_HOST_REPORT";

    private static DispatcherQueueTimer? _timer;
    private static string _scenario = string.Empty;

    /// <summary>Runs when Bwc.UI loads. On the UI thread of a head it waits for the main window and opens the dialog.</summary>
    // CA2255 asks libraries not to use module initializers. This is the E2E-only entry point (the heads are not edited
    // for it), and nothing here exists in the ship flavor.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        string? scenario = Environment.GetEnvironmentVariable(ScenarioVariable);
        if (string.IsNullOrWhiteSpace(scenario))
        {
            return;
        }

        _scenario = scenario;
        try
        {
            DispatcherQueue? queue = DispatcherQueue.GetForCurrentThread();
            if (queue is null)
            {
                WriteFailure("startup-failed", "Bwc.UI loaded on a thread with no DispatcherQueue.");
                return;
            }

            _timer = queue.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(250);
            _timer.IsRepeating = true;
            _timer.Tick += OnTick;
            _timer.Start();
        }
        catch (Exception ex)
        {
            WriteFailure("startup-failed", ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void OnTick(DispatcherQueueTimer timer, object args)
    {
        nint owner;
        using (Process self = Process.GetCurrentProcess())
        {
            owner = self.MainWindowHandle;
        }

        if (owner == 0)
        {
            return;
        }

        timer.Stop();
        timer.Tick -= OnTick;
        try
        {
            HostDialog dialog = Create(_scenario);
            dialog.ShowAsync(owner).Forget("E2E host dialog");
        }
        catch (Exception ex)
        {
            WriteFailure("constructor-failed", ex.GetType().Name + ": " + ex.Message, ex.GetType().Name);
        }
    }

    private static HostDialog Create(string scenario) => scenario switch
    {
        "fixed" => new FixedDialog(),
        "resize" => new ResizableDialog(),
        "literal" => new LiteralDialog(),
        "duplicate" => new DuplicateDialog(),
        "spinner" => new SpinnerDialog(),
        "unsigned-spinner" => new UnsignedSpinnerDialog(),
        "grid2" => new GridDialog(["Value name", "Value"], [["alpha", "1"], ["beta", "2"]]),
        "grid1" => new GridDialog(["Value"], []),
        _ => throw new ArgumentException("Unknown dialog host scenario '" + scenario + "'."),
    };

    private static void WriteFailure(string state, string message, string? exceptionType = null) =>
        WriteReport(new Dictionary<string, object?>
        {
            ["scenario"] = _scenario,
            ["state"] = state,
            ["error"] = message,
            ["exception"] = exceptionType,
        });

    private static void WriteReport(Dictionary<string, object?> values)
    {
        string? path = Environment.GetEnvironmentVariable(ReportVariable);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            WriteValue(writer, values);
        }

        File.WriteAllText(path, Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case Dictionary<string, object?> map:
                writer.WriteStartObject();
                foreach (KeyValuePair<string, object?> pair in map)
                {
                    writer.WritePropertyName(pair.Key);
                    WriteValue(writer, pair.Value);
                }

                writer.WriteEndObject();
                break;
            case System.Collections.IEnumerable list:
                writer.WriteStartArray();
                foreach (object? item in list)
                {
                    WriteValue(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    private static ClassicControlDefinition Label(string id, string text, int x, int y, int cx, int cy, bool literal = false) =>
        new(ClassicControlKind.Label, 65535, id, text, x, y, cx, cy) { LiteralText = literal };

    private static ClassicControlDefinition Edit(string id, int x, int y, int cx, int cy, bool multiline = false) =>
        new(ClassicControlKind.Edit, 1000, id, id, x, y, cx, cy) { TabStop = true, Multiline = multiline };

    private static ClassicControlDefinition Button(int id, string automationId, string text, int x, int y, bool isDefault = false, bool literal = false) =>
        new(ClassicControlKind.Button, id, automationId, text, x, y, 50, 14) { TabStop = true, IsDefault = isDefault, LiteralText = literal };

    private static ClassicControlDefinition[] OkCancel(int x, int y) =>
    [
        Button(1, "DialogPrimary", "OK", x, y, isDefault: true),
        Button(2, "DialogSecondary", "Cancel", x + 55, y),
    ];

    /// <summary>The dialog base of the host: Enter and Escape through the template, a report when it closes.</summary>
    private abstract class HostDialog : ClassicDialog
    {
        private readonly List<int> _commands = [];

        protected HostDialog(ClassicDialogDefinition template)
            : base(template)
        {
            Closed += OnHostClosed;
        }

        protected override void OnCommand(int id, string automationId)
        {
            _commands.Add(id);
            base.OnCommand(id, automationId);
        }

        /// <summary>Adds the values of the dialog's controls to the report.</summary>
        protected abstract void Describe(Dictionary<string, object?> report);

        private void OnHostClosed(object sender, WindowEventArgs args)
        {
            var report = new Dictionary<string, object?>
            {
                ["scenario"] = _scenario,
                ["state"] = "closed",
                ["accepted"] = Accepted,
                ["commands"] = _commands.ToArray(),
            };
            try
            {
                Describe(report);
            }
            catch (Exception ex)
            {
                report["describeError"] = ex.GetType().Name + ": " + ex.Message;
            }

            WriteReport(report);
        }
    }

    /// <summary>A dialog with no flag set: the frame must stay fixed.</summary>
    private sealed class FixedDialog : HostDialog
    {
        internal FixedDialog()
            : base(new ClassicDialogDefinition(
                "E2E Fixed",
                "E2E host",
                180,
                70,
                [Label("HostLabel", "&Name:", 7, 9, 40, 8), Edit("HostEdit", 50, 7, 120, 12), .. OkCancel(65, 49)]))
        {
        }

        protected override void Describe(Dictionary<string, object?> report) => report["text"] = RequireControl<TextBox>("HostEdit").Text;
    }

    /// <summary>A resizable dialog whose buttons stay at the bottom right and whose text box fills the rest.</summary>
    private sealed class ResizableDialog : HostDialog
    {
        private const int MarginDlu = 7;
        private const int ButtonWidthDlu = 50;
        private const int ButtonHeightDlu = 14;
        private const int GapDlu = 5;

        private readonly TextBox _edit;
        private readonly Button _ok;
        private readonly Button _cancel;

        internal ResizableDialog()
            : base(new ClassicDialogDefinition(
                "E2E Resizable",
                "E2E host",
                200,
                90,
                [Edit("HostEdit", MarginDlu, MarginDlu, 186, 56, multiline: true), .. OkCancel(88, 69)])
            {
                Resizable = true,
                MinWidthDlu = 160,
                MinHeightDlu = 70,
            })
        {
            _edit = RequireControl<TextBox>("HostEdit");
            _ok = RequireControl<Button>("DialogPrimary");
            _cancel = RequireControl<Button>("DialogSecondary");
        }

        protected override void OnClientResized(double widthDip, double heightDip)
        {
            double margin = DluX(MarginDlu);
            double marginY = DluY(MarginDlu);
            double buttonWidth = DluX(ButtonWidthDlu);
            double buttonHeight = DluY(ButtonHeightDlu);
            double top = heightDip - marginY - buttonHeight;

            Canvas.SetLeft(_cancel, widthDip - margin - buttonWidth);
            Canvas.SetTop(_cancel, top);
            Canvas.SetLeft(_ok, widthDip - margin - buttonWidth - DluX(GapDlu) - buttonWidth);
            Canvas.SetTop(_ok, top);
            _edit.Width = Math.Max(1, widthDip - 2 * margin);
            _edit.Height = Math.Max(1, top - DluY(GapDlu) - marginY);
        }

        protected override void Describe(Dictionary<string, object?> report) => report["text"] = _edit.Text;
    }

    /// <summary>Literal and non-literal text side by side. The would-be access keys of the literal controls collide.</summary>
    private sealed class LiteralDialog : HostDialog
    {
        internal LiteralDialog()
            : base(new ClassicDialogDefinition(
                "E2E Literal",
                "E2E host",
                210,
                120,
                [
                    Label("LiteralLabel", "Fish && Chips &", 7, 7, 120, 8, literal: true),
                    Label("MnemonicLabel", "&Name:", 7, 21, 40, 8),
                    Edit("NameBox", 50, 19, 100, 12),
                    Button(100, "LiteralButton", "A&B", 7, 36, literal: true),
                    new(ClassicControlKind.CheckBox, 101, "LiteralCheckA", "R&D", 7, 55, 80, 10) { TabStop = true, LiteralText = true },
                    new(ClassicControlKind.CheckBox, 102, "LiteralCheckB", "Q&D", 90, 55, 80, 10) { TabStop = true, LiteralText = true },
                    new(ClassicControlKind.CheckBox, 103, "MnemonicCheck", "&Dup", 7, 70, 80, 10) { TabStop = true },
                    .. OkCancel(100, 99),
                ]))
        {
        }

        protected override void Describe(Dictionary<string, object?> report)
        {
            report["mnemonicChecked"] = RequireControl<CheckBox>("MnemonicCheck").IsChecked == true;
            report["literalCheckAChecked"] = RequireControl<CheckBox>("LiteralCheckA").IsChecked == true;
            report["name"] = RequireControl<TextBox>("NameBox").Text;
        }
    }

    /// <summary>Two non-literal controls with one access key. Building it must throw.</summary>
    private sealed class DuplicateDialog : HostDialog
    {
        internal DuplicateDialog()
            : base(new ClassicDialogDefinition(
                "E2E Duplicate",
                "E2E host",
                180,
                60,
                [Button(100, "AlphaButton", "&Alpha", 7, 7), Button(101, "AppleButton", "&Apple", 65, 7), .. OkCancel(65, 40)]))
        {
        }

        protected override void Describe(Dictionary<string, object?> report)
        {
        }
    }

    /// <summary>Two spin boxes and an edit, to read the keys, the range and the tab order.</summary>
    private sealed class SpinnerDialog : HostDialog
    {
        internal SpinnerDialog()
            : base(new ClassicDialogDefinition(
                "E2E Spinner",
                "E2E host",
                200,
                90,
                [
                    Label("MainLabel", "&Amount:", 7, 9, 55, 8),
                    new(ClassicControlKind.Spinner, 1100, "SpinMain", "Amount", 65, 7, 60, 12) { TabStop = true, Minimum = 0, Maximum = 100, Step = 5 },
                    Label("BigLabel", "&Big:", 7, 27, 55, 8),
                    new(ClassicControlKind.Spinner, 1101, "SpinBig", "Big", 65, 25, 80, 12) { TabStop = true, Minimum = 0, Maximum = uint.MaxValue, Step = 1 },
                    Label("OtherLabel", "&Other:", 7, 45, 55, 8),
                    Edit("OtherEdit", 65, 43, 80, 12),
                    .. OkCancel(88, 69),
                ]))
        {
            RequireControl<ClassicSpinBox>("SpinMain").Value = 10;
            RequireControl<ClassicSpinBox>("SpinBig").Value = 4294967290;
        }

        protected override void Describe(Dictionary<string, object?> report)
        {
            foreach (string id in new[] { "SpinMain", "SpinBig" })
            {
                ClassicSpinBox spin = RequireControl<ClassicSpinBox>(id);
                bool valid = spin.TryGetValue(out long value);
                report[id] = new Dictionary<string, object?>
                {
                    ["text"] = spin.Text,
                    ["valid"] = valid,
                    ["value"] = value,
                    ["lastValid"] = spin.Value,
                };
            }
        }
    }

    /// <summary>Unsigned ranges beyond long.MaxValue, including a full-width step.</summary>
    private sealed class UnsignedSpinnerDialog : HostDialog
    {
        internal UnsignedSpinnerDialog()
            : base(new ClassicDialogDefinition(
                "E2E Unsigned Spinner",
                "E2E host",
                230,
                108,
                [
                    Label("UnsignedMainLabel", "&Unsigned:", 7, 9, 55, 8),
                    new(ClassicControlKind.Spinner, 1100, "UnsignedMain", "Unsigned", 65, 7, 150, 12)
                    {
                        TabStop = true,
                        UnsignedMinimum = 0,
                        UnsignedMaximum = ulong.MaxValue,
                        UnsignedStep = 1,
                    },
                    Label("UnsignedBoundedLabel", "&Bounded:", 7, 27, 55, 8),
                    new(ClassicControlKind.Spinner, 1101, "UnsignedBounded", "Bounded", 65, 25, 150, 12)
                    {
                        TabStop = true,
                        UnsignedMinimum = (ulong)long.MaxValue + 1,
                        UnsignedMaximum = ulong.MaxValue,
                        UnsignedStep = 1,
                    },
                    Label("UnsignedLargeStepLabel", "&Large step:", 7, 45, 55, 8),
                    new(ClassicControlKind.Spinner, 1102, "UnsignedLargeStep", "Large step", 65, 43, 150, 12)
                    {
                        TabStop = true,
                        UnsignedMinimum = 1,
                        UnsignedMaximum = ulong.MaxValue,
                        UnsignedStep = ulong.MaxValue,
                    },
                    .. OkCancel(118, 87),
                ]))
        {
            RequireControl<ClassicSpinBox>("UnsignedMain").Text = "9223372036854775808";
            RequireControl<ClassicSpinBox>("UnsignedBounded").Text = "9223372036854775808";
            RequireControl<ClassicSpinBox>("UnsignedLargeStep").Text = "1";
        }

        protected override void Describe(Dictionary<string, object?> report)
        {
            foreach (string id in new[] { "UnsignedMain", "UnsignedBounded", "UnsignedLargeStep" })
            {
                ClassicSpinBox spin = RequireControl<ClassicSpinBox>(id);
                report[id] = new Dictionary<string, object?> { ["text"] = spin.Text };
            }
        }
    }

    /// <summary>The editable grid with one or two columns.</summary>
    private sealed class GridDialog : HostDialog
    {
        internal GridDialog(string[] columns, string[][] rows)
            : base(new ClassicDialogDefinition(
                columns.Length == 1 ? "E2E Grid One" : "E2E Grid Two",
                "E2E host",
                200,
                120,
                [
                    new(ClassicControlKind.EditableGrid, 1200, "GridMain", "Values", 7, 7, 186, 80) { TabStop = true, Columns = columns },
                    .. OkCancel(88, 99),
                ]))
        {
            RequireControl<ClassicEditableGrid>("GridMain").SetRows(rows.Select(row => (IReadOnlyList<string>)row).ToArray());
        }

        protected override void Describe(Dictionary<string, object?> report)
        {
            ClassicEditableGrid grid = RequireControl<ClassicEditableGrid>("GridMain");
            report["columns"] = grid.ColumnCount;
            report["rowCount"] = grid.RowCount;
            report["rows"] = grid.Rows;
        }
    }
}
#endif

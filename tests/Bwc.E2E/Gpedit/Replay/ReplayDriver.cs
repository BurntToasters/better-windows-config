using System.Text.Json.Nodes;
using Bwc.E2E.ConsoleFrame;
using Bwc.Policy.Tests.Golden;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace Bwc.E2E.Gpedit.Replay;

/// <summary>
/// Replays one capture case through the window of the editor: every structured <c>editPolicy</c> operation of the
/// case becomes
/// the keys and clicks of a user (open the policy, choose the state, set the options and the comment, press the
/// buttons in the
/// order the capture recorded), and after every recorded snapshot the five files of the sandbox must equal the
/// step folder
/// byte for byte. An absent file must be absent. Failure modes: docs/failure-modes/gpedit-dialog.md, section 15.
/// </summary>
internal sealed class ReplayDriver
{
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(8);

    private readonly GpeditRun _run;
    private readonly ReplaySandbox _sandbox;
    private readonly GpeditChecks _checks;
    private readonly ReplayReport _report;

    internal ReplayDriver(GpeditRun run, ReplaySandbox sandbox, GpeditChecks checks, ReplayReport report)
    {
        _run = run;
        _sandbox = sandbox;
        _checks = checks;
        _report = report;
    }

    /// <summary>Replays the case. The sandbox must have been seeded with <see cref="ReplaySandbox.SeedStart"/> before
    /// the editor started.</summary>
    /// <returns>True when every snapshot matched. The replay of a case stops at its first difference, because the later
    /// steps would only repeat it.</returns>
    internal bool Run(GoldenCase golden)
    {
        _report.AddCase(golden.Id);
        var step = 0;
        foreach (var op in golden.Ops)
        {
            switch (op.Kind)
            {
                case "snapshot":
                    if (!CompareStep(golden, golden.Steps[step], op.Index))
                    {
                        return false;
                    }

                    step++;
                    break;
                case "editPolicy":
                    Edit(golden, op);
                    break;
                case "selectNode":
                case "readList":
                    break;
                default:
                    _checks.Record($"{golden.Id}.op{op.Index}.kind", false, $"the replay cannot run an operation of kind '{op.Kind}'.");
                    return false;
            }
        }

        return true;
    }

    // ---- the files ----------------------------------------------------------------------------------

    private bool CompareStep(GoldenCase golden, GoldenStep step, int opIndex)
    {
        var id = $"{golden.Id}.step{step.Number}";
        var matched = ShellRun.WaitUntil(() => Differences(step).Count == 0, SettleTimeout);
        var problems = Differences(step);
        foreach (var name in GoldenFiles.Names)
        {
            _ = _report.AddFile(golden, step, name, _sandbox.Read(name));
        }

        _checks.Record(
            id,
            matched || problems.Count == 0,
            problems.Count == 0
                ? $"after operation {opIndex} the five files equal the capture step '{step.Label}' byte for byte."
                : $"after operation {opIndex} (step '{step.Label}'): {string.Join(" | ", problems)}.");
        return problems.Count == 0;
    }

    private List<string> Differences(GoldenStep step)
    {
        var problems = new List<string>();
        foreach (var name in GoldenFiles.Names)
        {
            var actual = _sandbox.Read(name);
            if (step.Files[name] is null)
            {
                if (actual is not null)
                {
                    problems.Add($"{name} must be absent but holds {actual.Length} bytes");
                }

                continue;
            }

            var expected = File.ReadAllBytes(step.PathOf(name));
            if (actual is null)
            {
                problems.Add($"{name} is missing; classic wrote {expected.Length} bytes");
            }
            else if (!expected.AsSpan().SequenceEqual(actual))
            {
                problems.Add($"{name} differs: {Diff(expected, actual)}");
            }
        }

        problems.AddRange(_sandbox.Extras().Select(e => $"unexpected file {e} in the policy folder"));
        return problems;
    }

    private static string Diff(byte[] expected, byte[] actual)
    {
        var at = 0;
        while (at < expected.Length && at < actual.Length && expected[at] == actual[at])
        {
            at++;
        }

        return $"expected {expected.Length} bytes, got {actual.Length}, first difference at offset {at}";
    }

    // ---- one editPolicy operation -------------------------------------------------------------------

    private void Edit(GoldenCase golden, GoldenOp op)
    {
        var ctx = $"{golden.Id}.op{op.Index}";
        var rootName = op.Text("root") == "Computer" ? "Computer Configuration" : "User Configuration";
        var path = new List<string> { GpeditRun.RootName, rootName };
        path.AddRange((op.Op["path"] as JsonArray ?? []).Select(n => n!.GetValue<string>()));
        _run.GoTo([.. path]);

        var row = op.Text("policy") + " setting";
        using var dialog = GpeditDialog.Open(_run, row);
        _checks.Record($"{ctx}.opens", dialog is not null, $"the dialog of '{row}' opens on the node {string.Join(" > ", path)}.");
        if (dialog is null)
        {
            return;
        }

        var options = op.Op["options"] as JsonArray;
        var state = op.Text("state") ?? (options is { Count: > 0 } ? "Enabled" : null);
        if (state is not null)
        {
            var radio = state switch
            {
                "Enabled" => GpeditDialog.Enabled,
                "Disabled" => GpeditDialog.Disabled,
                _ => GpeditDialog.NotConfigured,
            };
            if (!dialog.IsSelected(radio))
            {
                _ = dialog.Select(radio);
            }

            _checks.Record($"{ctx}.state", dialog.IsSelected(radio), $"the state '{state}' is selected.");
        }

        if (options is { Count: > 0 })
        {
            _ = ShellRun.WaitUntil(dialog.OptionsEnabled, TimeSpan.FromSeconds(3));
            foreach (var option in options.OfType<JsonObject>())
            {
                SetOption(dialog, option, ctx);
            }
        }

        if (op.Text("comment") is { } comment && dialog.TextOf(GpeditDialog.Comment) != comment)
        {
            var typed = dialog.SetComment(comment);
            _checks.Record($"{ctx}.comment", typed && dialog.TextOf(GpeditDialog.Comment) == comment, $"the comment box holds '{dialog.TextOf(GpeditDialog.Comment)}'.");
        }

        Press(dialog, op, ctx);
    }

    private void Press(GpeditDialog dialog, GoldenOp op, string ctx)
    {
        var presses = (op.Op["press"] as JsonArray)?.Select(p => p!.GetValue<string>()).ToArray() ?? ["OK"];
        var observed = (op.Data?["presses"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        for (var k = 0; k < presses.Length; k++)
        {
            var seen = k < observed.Count ? observed[k] : null;
            if (seen?["pressed"] is JsonValue flag && flag.TryGetValue(out bool wasPressed) && !wasPressed)
            {
                // The button was disabled and the capture did not press it.
                _checks.Record($"{ctx}.press{k}.disabled", !dialog.IsEnabled(IdOf(presses[k])), $"classic had {presses[k]} disabled and so does the dialog.");
                continue;
            }

            var refused = (seen?["messageBoxes"] as JsonArray)?.Count > 0;
            var button = IdOf(presses[k]);
            _ = dialog.Invoke(button);
            if (refused)
            {
                var message = dialog.WaitMessage();
                _checks.Record($"{ctx}.press{k}.refused", message is not null, $"classic refused {presses[k]} with a message box; the dialog shows {(message is null ? "none" : "'" + message.Value.Text + "'")}.");
                if (message is not null)
                {
                    _ = dialog.DismissMessage();
                }

                _checks.Record($"{ctx}.press{k}.staysOpen", IsOpen(dialog), "the dialog stays open after the refusal.");
            }
            else if (presses[k] == "Apply")
            {
                _checks.Record(
                    $"{ctx}.press{k}.applied",
                    ShellRun.WaitUntil(() => !dialog.IsEnabled(GpeditDialog.Apply), TimeSpan.FromSeconds(4)),
                    "Apply is disabled again after it saved.");
            }
            else
            {
                _checks.Record($"{ctx}.press{k}.closes", dialog.WaitClosed(), $"{presses[k]} closes the dialog.");
            }
        }

        if (IsOpen(dialog))
        {
            // The capture driver closes a dialog that is still open with Cancel.
            _ = dialog.Invoke(GpeditDialog.Cancel);
            _checks.Record($"{ctx}.cancelAtEnd", dialog.WaitClosed(), "Cancel closes the dialog that the presses left open.");
        }
    }

    private static string IdOf(string press) =>
        press switch
        {
            "OK" => GpeditDialog.Ok,
            "Apply" => GpeditDialog.Apply,
            "Cancel" => GpeditDialog.Cancel,
            _ => throw new InvalidOperationException($"The replay cannot press '{press}'."),
        };

    private static bool IsOpen(GpeditDialog dialog) => dialog.Find(GpeditDialog.Cancel, 0.6) is not null;

    // ---- the options --------------------------------------------------------------------------------

    private void SetOption(GpeditDialog dialog, JsonObject option, string ctx)
    {
        var kind = option["expectKind"]?.GetValue<string>() ?? string.Empty;
        var elementId = option["elementId"]?.GetValue<string>() ?? string.Empty;
        var label = option["label"]?.GetValue<string>() ?? string.Empty;
        var id = $"{ctx}.option.{elementId}";
        switch (kind)
        {
            case "checkBox":
                var wanted = option["checked"]!.GetValue<bool>();
                if (CheckedOf(dialog, elementId) != wanted)
                {
                    _ = dialog.Toggle("checkBox" + label);
                }

                _checks.Record(id, CheckedOf(dialog, elementId) == wanted, $"the check box '{label}' is {(wanted ? "checked" : "unchecked")}.");
                break;
            case "decimalTextBox":
            case "longDecimalTextBox":
            case "spinner":
                var digits = option["text"]!.GetValue<string>();
                var numberBox = new[] { "textBox" + label, "numericUpDown" + label + ".Text" }.FirstOrDefault(c => dialog.Find(c, 0.5) is not null);
                var numberTyped = numberBox is not null && dialog.TypeInto(numberBox, digits);
                _checks.Record(id, numberTyped && TextOfElement(dialog, elementId, "num") == digits, $"the number box '{label}' holds '{TextOfElement(dialog, elementId, "num")}', expected '{digits}'.");
                break;
            case "textBox":
            case "comboBox":
                var text = option["text"]?.GetValue<string>() ?? option["item"]!.GetValue<string>();
                var box = (kind == "comboBox" ? "comboBox" : "textBox") + label;
                var typed = dialog.TypeInto(box, text);
                _checks.Record(id, typed && TextOfElement(dialog, elementId, "text") == text, $"the {kind} '{label}' holds '{TextOfElement(dialog, elementId, "text")}', expected '{text}'.");
                break;
            case "dropdownList":
                var item = option["item"]!.GetValue<string>();
                var chosen = dialog.Choose("dropDownList" + label, item);
                _checks.Record(id, chosen, $"the item '{item}' is chosen in the drop-down '{label}'.");
                break;
            case "multiText":
                var lines = (option["lines"] as JsonArray ?? []).Select(l => l!.GetValue<string>()).ToArray();
                var multi = TypeLines(dialog, "multiTextBox" + (label.Length > 0 ? label : elementId), lines);
                _checks.Record(id, multi, $"the lines [{string.Join("|", lines)}] are typed.");
                break;
            case "list":
                SetList(dialog, option, label, id);
                break;
            default:
                _checks.Record(id, false, $"the replay cannot set an option of kind '{kind}'.");
                break;
        }
    }

    private static bool? CheckedOf(GpeditDialog dialog, string elementId) =>
        dialog.ValueOf(elementId) is { } v && v.GetProperty("k").GetString() == "bool" ? v.GetProperty("v").GetBoolean() : null;

    private static string TextOfElement(GpeditDialog dialog, string elementId, string kind) =>
        dialog.ValueOf(elementId) is { } v && v.GetProperty("k").GetString() == kind ? v.GetProperty("v").GetString() ?? string.Empty : "<none>";

    private static bool TypeLines(GpeditDialog dialog, string id, string[] lines)
    {
        if (!dialog.FocusControl(id))
        {
            return false;
        }

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Thread.Sleep(100);
        Keyboard.Type(VirtualKeyShort.DELETE);
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                Keyboard.Type(VirtualKeyShort.RETURN);
            }

            Keyboard.Type(lines[i]);
        }

        Thread.Sleep(300);
        return true;
    }

    private void SetList(GpeditDialog dialog, JsonObject option, string label, string id)
    {
        var rows = (option["rows"] as JsonArray ?? []).Select(r => ((JsonArray)r!).Select(c => c!.GetValue<string>()).ToArray()).ToList();
        var clear = option["clearRows"]?.GetValue<bool>() == true;
        _ = dialog.Invoke("button" + label);
        var contents = GpeditContents.Wait(dialog);
        _checks.Record($"{id}.opens", contents is not null, $"Show... of '{label}' opens the Show Contents dialog.");
        if (contents is null)
        {
            return;
        }

        var columns = contents.Cell(0, 1) is null ? 1 : 2;
        var existing = contents.Rows(columns).Count;
        if (clear)
        {
            for (var i = 0; i < existing; i++)
            {
                contents.DeleteRow(0);
            }
        }

        var at = clear ? 0 : existing;
        foreach (var cells in rows)
        {
            for (var c = 0; c < cells.Length; c++)
            {
                contents.Edit(at, c, cells[c]);
            }

            at++;
        }

        var held = contents.Rows(columns).Count;
        _ = contents.Ok();
        if (held == 0)
        {
            // Classic refuses OK on a grid with no row; the capture driver cancels the grid and the list keeps its rows.
            var message = dialog.WaitMessage();
            _checks.Record($"{id}.emptyRefused", message is not null, "OK of an empty Show Contents grid is refused with a message box.");
            if (message is not null)
            {
                _ = dialog.DismissMessage();
            }

            _ = contents.Cancel();
        }

        _checks.Record($"{id}.closes", contents.WaitClosed(), "the Show Contents dialog closes.");
    }
}

using System.Text.Json.Nodes;
using System.Windows.Automation;
using Bwc.Oracle.Capture.Gpedit.Probe;

namespace Bwc.Oracle.Capture.Gpedit.Driver;

// One control group of the options pane: a label and the control that holds the value, found by native window class and style.
internal sealed class OptionGroup
{
    internal int Index { get; init; }

    internal int ControlIndex { get; init; }

    internal string Kind { get; init; } = "unknown";

    internal string Label { get; init; } = string.Empty;

    internal string PanelId { get; init; } = string.Empty;

    internal string ControlId { get; init; } = string.Empty;

    internal string ClassName { get; init; } = string.Empty;

    internal IntPtr Container { get; init; }

    internal IntPtr Control { get; init; }

    internal NativeRect Rect { get; init; }

    // How the state of a check box is read (failure mode 35 of the driver document).
    internal CheckReader Reader { get; init; } = CheckReader.BmGetCheck;

    // What the test for a check box saw on a BUTTON child: style, UI Automation answer. Untracked.
    internal JsonObject? CheckEvidence { get; init; }

    // What the test for the Show button of a list saw on each inner BUTTON: style, UI Automation answer, verdict (failure mode 36). Untracked.
    internal JsonArray? PushEvidence { get; init; }
}

// Options pane of the setting dialog. The controls are created in code by classic, so they are found by what they are (an EDIT, a
// multi-line EDIT, an EDIT inside an up-down control, a COMBOBOX, a check box, a Show button), not by guessed ids; the guessed kind is
// recorded next to the real id and class. Failure modes 6 to 9 of the driver document.
internal sealed partial class PolicyDialog
{
    private static readonly string[] NumericKinds = { "decimalTextBox", "longDecimalTextBox", "spinner" };

    private static bool Has(WinInfo window, string className) => window.Class.Contains("." + className + ".", StringComparison.OrdinalIgnoreCase);

    internal List<OptionGroup> DiscoverOptions()
    {
        AutomationElement panel = Element("flowLayoutPanelDynamic");
        IntPtr panelWindow = new(panel.Current.NativeWindowHandle);
        if (panelWindow == IntPtr.Zero)
        {
            throw new DriverException("the options panel has no window handle");
        }

        List<WinInfo> children = ProbeWindows.Children(panelWindow)
            .Where(c => c.Visible && Win32.GetParent(c.Hwnd) == panelWindow)
            .OrderBy(c => c.Rect.Top).ThenBy(c => c.Rect.Left).ToList();
        var groups = new List<OptionGroup>();
        int controlIndex = 0;
        foreach (WinInfo child in children)
        {
            string kind = "unknown";
            string label = string.Empty;
            IntPtr control = IntPtr.Zero;
            string controlId = string.Empty;
            string className = child.Class;
            CheckReader? reader = null;
            JsonObject? checkEvidence = null;
            JsonArray? pushEvidence = null;
            if (CheckBoxRead.IsButtonClass(child.Class))
            {
                uint buttonStyle = DriverNative.Style(child.Hwnd);
                CheckUia? uia = CheckBoxRead.NeedsUia(child.Class, buttonStyle) ? ReadCheckUia(child.Hwnd) : null;
                reader = CheckBoxRead.Judge(child.Class, buttonStyle, uia);
                checkEvidence = new JsonObject
                {
                    ["buttonStyle"] = $"0x{buttonStyle:X8}",
                    ["bsType"] = (int)(buttonStyle & DriverNative.BsTypeMask),
                    ["ownerDraw"] = CheckBoxRead.IsOwnerDraw(buttonStyle),
                    ["uiaControlType"] = uia?.ControlType,
                    ["uiaHasToggle"] = uia?.HasToggle,
                    ["panelIdHint"] = CheckBoxRead.PanelHint(GpeditDriver.IdOf(child.Hwnd)),
                    ["reader"] = reader is { } chosen ? CheckBoxRead.ViaName(chosen) : null,
                };
            }

            if (reader is not null)
            {
                kind = "checkBox";
                label = child.Title;
                control = child.Hwnd;
                controlId = GpeditDriver.IdOf(child.Hwnd);
            }
            else if (Has(child, "STATIC"))
            {
                kind = "staticText";
                label = child.Title;
            }
            else
            {
                List<WinInfo> inner = ProbeWindows.Children(child.Hwnd);
                label = inner.FirstOrDefault(d => Has(d, "STATIC") && d.Title.Length > 0)?.Title ?? string.Empty;
                WinInfo? combo = inner.FirstOrDefault(d => Has(d, "COMBOBOX"));
                WinInfo? edit = inner.FirstOrDefault(d => Has(d, "EDIT"));
                WinInfo? push = null;
                if (combo is null && edit is null)
                {
                    (push, pushEvidence) = FindPushButton(inner);
                }

                if (combo is not null)
                {
                    uint style = DriverNative.Style(combo.Hwnd) & DriverNative.CbsMask;
                    kind = style == DriverNative.CbsDropDownList ? "dropdownList" : style == DriverNative.CbsDropDown ? "comboBox" : "unknown";
                    control = combo.Hwnd;
                    className = combo.Class;
                }
                else if (edit is not null)
                {
                    control = edit.Hwnd;
                    className = edit.Class;
                    controlId = GpeditDriver.IdOf(edit.Hwnd);
                    bool multiline = (DriverNative.Style(edit.Hwnd) & DriverNative.EsMultiline) != 0;
                    bool nested = Win32.GetParent(edit.Hwnd) != child.Hwnd;
                    string hint = (controlId + " " + GpeditDriver.IdOf(child.Hwnd)).ToLowerInvariant();
                    kind = multiline ? "multiText"
                        : nested ? "spinner"
                        : hint.Contains("longdecimal", StringComparison.Ordinal) ? "longDecimalTextBox"
                        : hint.Contains("decimal", StringComparison.Ordinal) ? "decimalTextBox"
                        : "textBox";
                }
                else if (push is not null)
                {
                    kind = "list";
                    control = push.Hwnd;
                    className = push.Class;
                    controlId = GpeditDriver.IdOf(push.Hwnd);
                }
            }

            if (kind != "staticText" && control != IntPtr.Zero && string.IsNullOrEmpty(controlId))
            {
                controlId = GpeditDriver.IdOf(control);
            }

            groups.Add(new OptionGroup
            {
                Index = groups.Count,
                ControlIndex = kind == "staticText" ? -1 : controlIndex,
                Kind = kind,
                Label = label,
                PanelId = GpeditDriver.IdOf(child.Hwnd),
                ControlId = controlId,
                ClassName = className,
                Container = child.Hwnd,
                Control = control,
                Rect = child.Rect,
                Reader = reader ?? CheckReader.BmGetCheck,
                CheckEvidence = checkEvidence,
                PushEvidence = pushEvidence,
            });
            if (kind != "staticText")
            {
                controlIndex++;
            }
        }

        return groups;
    }

    // The first inner BUTTON that is a push button: a native push style as ever, or an owner-drawn Windows Forms button that UI Automation says
    // is a Button or offers Invoke and is not a check box (failure mode 36). The evidence lists every inner BUTTON that was looked at.
    private static (WinInfo? Push, JsonArray? Evidence) FindPushButton(List<WinInfo> inner)
    {
        JsonArray? evidence = null;
        foreach (WinInfo candidate in inner.Where(d => CheckBoxRead.IsButtonClass(d.Class)))
        {
            uint style = DriverNative.Style(candidate.Hwnd);
            PushUia? uia = PushButtonRead.NeedsUia(candidate.Class, style) ? ReadPushUia(candidate.Hwnd) : null;
            bool isPush = PushButtonRead.IsPush(candidate.Class, style, uia);
            evidence ??= new JsonArray();
            evidence.Add(new JsonObject
            {
                ["buttonStyle"] = $"0x{style:X8}",
                ["bsType"] = (int)(style & DriverNative.BsTypeMask),
                ["ownerDraw"] = CheckBoxRead.IsOwnerDraw(style),
                ["uiaControlType"] = uia?.ControlType,
                ["uiaHasInvoke"] = uia?.HasInvoke,
                ["uiaHasToggle"] = uia?.HasToggle,
                ["isPush"] = isPush,
            });
            if (isPush)
            {
                return (candidate, evidence);
            }
        }

        return (null, evidence);
    }

    // UI Automation facts about an owner-drawn BUTTON that may be a push button. No answer is no evidence.
    private static PushUia? ReadPushUia(IntPtr hwnd)
    {
        try
        {
            AutomationElement element = AutomationElement.FromHandle(hwnd);
            ControlType type = element.Current.ControlType;
            bool invoke = element.TryGetCurrentPattern(InvokePattern.Pattern, out _);
            bool toggle = element.TryGetCurrentPattern(TogglePattern.Pattern, out _);
            return new PushUia(type == ControlType.Button, invoke, type == ControlType.CheckBox, toggle, type.ProgrammaticName);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or ArgumentException
            or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    // UI Automation facts about an owner-drawn BUTTON. No answer (the window went away, the element is not available) is no evidence.
    private static CheckUia? ReadCheckUia(IntPtr hwnd)
    {
        try
        {
            AutomationElement element = AutomationElement.FromHandle(hwnd);
            ControlType type = element.Current.ControlType;
            bool toggle = element.TryGetCurrentPattern(TogglePattern.Pattern, out _);
            return new CheckUia(type == ControlType.CheckBox, toggle, type.ProgrammaticName);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or ArgumentException
            or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    // The state of a check box in the BM_GETCHECK numbers (0 off, 1 on, 2 indeterminate). A check box that is not a native check style is read
    // through TogglePattern; if the pattern is missing it falls back to BM_GETCHECK, which is unreliable for an owner-drawn button, and via says so.
    private static int ReadCheckState(OptionGroup group, out string via)
    {
        if (group.Reader == CheckReader.TogglePattern)
        {
            AutomationElement button = AutomationElement.FromHandle(group.Control);
            if (button.TryGetCurrentPattern(TogglePattern.Pattern, out object? toggle))
            {
                via = CheckBoxRead.ViaName(CheckReader.TogglePattern);
                return CheckBoxRead.ToCheckState(((TogglePattern)toggle).Current.ToggleState);
            }

            via = CheckBoxRead.ViaName(CheckReader.BmGetCheck) + " (TogglePattern missing)";
            return DriverNative.CheckState(group.Control);
        }

        via = CheckBoxRead.ViaName(CheckReader.BmGetCheck);
        return DriverNative.CheckState(group.Control);
    }

    internal JsonArray ReadOptions(bool readLists)
    {
        var result = new JsonArray();
        foreach (OptionGroup group in DiscoverOptions())
        {
            result.Add(DescribeGroup(group, readLists));
        }

        return result;
    }

    private JsonObject DescribeGroup(OptionGroup group, bool readLists)
    {
        var node = new JsonObject
        {
            ["index"] = group.Index,
            ["controlIndex"] = group.ControlIndex,
            ["kind"] = group.Kind,
            ["label"] = group.Label,
            ["panelId"] = group.PanelId,
            ["controlId"] = group.ControlId,
            ["className"] = group.ClassName,
            ["rect"] = new JsonArray(group.Rect.Left, group.Rect.Top, group.Rect.Width, group.Rect.Height),
        };
        if (group.CheckEvidence is not null)
        {
            node["checkEvidence"] = group.CheckEvidence.DeepClone();
        }

        if (group.PushEvidence is not null)
        {
            node["pushEvidence"] = group.PushEvidence.DeepClone();
        }

        if (group.Control == IntPtr.Zero)
        {
            return node;
        }

        node["enabled"] = Win32Ext.IsWindowEnabled(group.Control);
        switch (group.Kind)
        {
            case "textBox" or "decimalTextBox" or "longDecimalTextBox" or "spinner":
                node["text"] = DriverNative.GetText(group.Control);
                break;
            case "multiText":
                string text = DriverNative.GetText(group.Control);
                node["text"] = text;
                node["lines"] = new JsonArray(text.Split("\r\n").Select(l => (JsonNode?)JsonValue.Create(l)).ToArray());
                break;
            case "checkBox":
                node["checkState"] = ReadCheckState(group, out string stateVia);
                node["checkStateVia"] = stateVia;
                break;
            case "dropdownList" or "comboBox":
                List<string> items = DriverNative.ComboItems(group.Control);
                node["items"] = new JsonArray(items.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
                int selected = DriverNative.ComboSelected(group.Control);
                node["selectedIndex"] = selected;
                node["selectedText"] = selected >= 0 && selected < items.Count ? items[selected] : null;
                if (group.Kind == "comboBox")
                {
                    node["text"] = DriverNative.GetText(group.Control);
                }

                break;
            case "list":
                node["text"] = DriverNative.GetText(group.Control);
                if (readLists && Win32Ext.IsWindowEnabled(group.Control))
                {
                    node["contents"] = ReadListContents(group);
                }
                else if (readLists)
                {
                    node["contents"] = "not read: the Show button is disabled";
                }

                break;
            default:
                node["tree"] = ProbeUia.Tree(AutomationElement.FromHandle(group.Container), 5, 80, rawView: true);
                break;
        }

        return node;
    }

    private static string Normalize(string label) => WithoutAmp(label).Trim().TrimEnd(':').Trim().ToLowerInvariant();

    private static OptionGroup FindGroup(OptionSpec spec, List<OptionGroup> groups, out string how)
    {
        List<OptionGroup> candidates = groups.Where(g => g.Kind != "staticText").ToList();
        if (spec.Label is not null)
        {
            how = "exact";
            List<OptionGroup> hits = candidates.Where(g => string.Equals(g.Label, spec.Label, StringComparison.Ordinal)).ToList();
            if (hits.Count == 0)
            {
                how = "normalized";
                hits = candidates.Where(g => Normalize(g.Label) == Normalize(spec.Label)).ToList();
            }

            if (hits.Count == 0)
            {
                throw new DriverException($"no option labelled '{spec.Label}'. Labels found: " + string.Join(" | ", candidates.Select(c => $"'{c.Label}' ({c.Kind})")));
            }

            if (hits.Count > 1)
            {
                if (spec.Index is { } pick && pick < hits.Count)
                {
                    how += "+index";
                    return hits[pick];
                }

                throw new DriverException($"{hits.Count} options are labelled '{spec.Label}'; give 'index'");
            }

            return hits[0];
        }

        int at = spec.Index!.Value;
        if (at >= candidates.Count)
        {
            throw new DriverException($"option index {at} does not exist; the dialog has {candidates.Count} controls");
        }

        how = "index";
        return candidates[at];
    }

    private static bool KindMatches(string actual, string expected) =>
        actual == expected || (NumericKinds.Contains(expected) && NumericKinds.Contains(actual)) || (actual == "textBox" && NumericKinds.Contains(expected));

    internal JsonObject SetOption(OptionSpec spec)
    {
        List<OptionGroup> groups = DiscoverOptions();
        OptionGroup group = FindGroup(spec, groups, out string how);
        if (spec.ExpectKind is not null && !KindMatches(group.Kind, spec.ExpectKind))
        {
            throw new DriverException($"option '{group.Label}' is a {group.Kind} (class {group.ClassName}, id '{group.ControlId}'), not a {spec.ExpectKind}");
        }

        if (group.Control == IntPtr.Zero)
        {
            throw new DriverException($"option '{group.Label}' has no control to set (kind {group.Kind})", evidence: DescribeGroup(group, false));
        }

        RequireOwned(group.Control);
        if (!Win32Ext.IsWindowEnabled(group.Control))
        {
            throw new DriverException($"option '{group.Label}' is disabled (the state must be Enabled first)");
        }

        JsonObject result = group.Kind switch
        {
            "textBox" or "decimalTextBox" or "longDecimalTextBox" or "spinner" =>
                SetEdit(group, Require(spec.Text, "text", group), numeric: true, typed: spec.Typed is true),
            "multiText" => SetEdit(
                group,
                spec.Lines is not null ? string.Join("\r\n", spec.Lines) : Require(spec.Text, "text or lines", group),
                numeric: false,
                typed: spec.Typed is true),
            "checkBox" => SetCheck(group, Require(spec.Checked, "checked", group)),
            "dropdownList" => SetCombo(group, spec),
            "comboBox" => spec.Text is not null ? SetEdit(group, spec.Text, numeric: false, notifyCombo: true, typed: spec.Typed is true) : SetCombo(group, spec),
            "list" => EditList(group, spec),
            _ => throw new DriverException($"option '{group.Label}' is of an unknown kind (class {group.ClassName})", evidence: DescribeGroup(group, false)),
        };
        result["set"] = "option";
        result["label"] = group.Label;
        result["labelMatch"] = how;
        result["kind"] = group.Kind;
        result["controlId"] = group.ControlId;
        result["className"] = group.ClassName;
        return result;
    }

    private static T Require<T>(T? value, string field, OptionGroup group)
        where T : struct => value ?? throw new DriverException($"'{field}' does not apply to option '{group.Label}' ({group.Kind})");

    private static string Require(string? value, string field, OptionGroup group) =>
        value ?? throw new DriverException($"'{field}' does not apply to option '{group.Label}' ({group.Kind})");

    // Sets edit text, moves focus to OK so a numeric edit validates, and reads the text again: observed can differ from requested (clamped
    // to the range, or reset), which is the point of the numeric cases. A single-line edit takes WM_SETTEXT unless the option says typed. A
    // multi-line edit (ES_MULTILINE) is always typed with key presses: WM_SETTEXT sends it no EN_CHANGE, so the dialog would not notice the
    // edit (failure mode 30 of the driver document). The edit inside a combo box has the style on the combo, not on the edit, so it is
    // never counted as multi-line.
    private JsonObject SetEdit(OptionGroup group, string text, bool numeric, bool notifyCombo = false, bool typed = false)
    {
        bool multiline = group.Kind != "comboBox" && (DriverNative.Style(group.Control) & DriverNative.EsMultiline) != 0;
        bool keys = typed || multiline;
        string before = DriverNative.GetText(group.Control);
        if (keys)
        {
            TypeInto(group.Control, $"the control of '{group.Label}'", text, multiline, requireCleared: multiline);
        }
        else
        {
            DriverNative.SetText(group.Control, text);
            if (notifyCombo)
            {
                DriverNative.NotifyParent(group.Control, DriverNative.CbnEditChange);
            }
        }

        string immediate = DriverNative.GetText(group.Control);
        CommitFocus();
        string observed = DriverNative.GetText(group.Control);
        bool isNumeric = numeric && NumericKinds.Contains(group.Kind);
        bool same = multiline
            ? string.Equals(NormalizeBreaks(observed), NormalizeBreaks(text), StringComparison.Ordinal)
            : string.Equals(observed, text, StringComparison.Ordinal);
        if (!isNumeric && !keys && !same)
        {
            throw new DriverException($"the text of '{group.Label}' did not stick (asked '{text}', read '{observed}')");
        }

        return new JsonObject
        {
            ["typed"] = keys,
            ["via"] = keys ? "keys" : "WM_SETTEXT",
            ["multiline"] = multiline,
            ["requested"] = text,
            ["before"] = before,
            ["immediate"] = immediate,
            ["observed"] = observed,
            ["clamped"] = !same,
        };
    }

    private static string NormalizeBreaks(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    // Real key presses, as a user types: the edit limit (maxLength) applies to typed text and not to WM_SETTEXT, and a multi-line edit
    // only tells the dialog about the edit when it is typed. The control must have the keyboard focus, which is read back before any key
    // is sent. A multi-line text is split at any line break and Enter is pressed between the lines; before every line after the first the
    // dialog must still be open (Enter can press its default button) and the focus must still be on the control. name is the control as
    // the messages call it. requireCleared: after select all and delete the control must be empty. Failure mode 30 of the driver document.
    private void TypeInto(IntPtr control, string name, string text, bool multiline, bool requireCleared)
    {
        DriverInput.RequireForeground(Hwnd, driver.Pid);
        ProbeUia.Focus(AutomationElement.FromHandle(control));
        Thread.Sleep(200);
        AutomationElement? focused = AutomationElement.FocusedElement;
        if (focused is null || focused.Current.NativeWindowHandle != control.ToInt32())
        {
            throw new DriverException($"{name} did not get the keyboard focus, so nothing was typed");
        }

        DriverInput.Chord(new[] { DriverInput.VkControl }, DriverInput.VkA);
        DriverInput.Key(DriverInput.VkDelete);
        if (requireCleared)
        {
            string left = DriverNative.GetText(control);
            if (left.Length > 0)
            {
                throw new DriverException($"the old text of {name} ({left.Length} characters) was not removed by select all and delete, so nothing was typed");
            }
        }

        string[] lines = multiline ? NormalizeBreaks(text).Split('\n') : text.Split("\r\n");
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                DriverInput.Key(ProbeInput.VkReturn);
                RequireTypingTarget(control, name, afterEnter: true);
            }

            DriverInput.TypeText(lines[i]);
        }

        if (multiline)
        {
            RequireTypingTarget(control, name, afterEnter: lines.Length > 1);
        }
    }

    // Nothing more is typed unless the setting dialog is open, the foreground window belongs to mmc and the focus is on the control.
    private void RequireTypingTarget(IntPtr control, string name, bool afterEnter)
    {
        if (!IsOpen)
        {
            throw new DriverException(afterEnter
                ? $"the setting dialog is no longer open after a typed Enter while {name} was typed: Enter pressed its default button. Nothing more was typed."
                : $"the setting dialog is no longer open while {name} was typed. Nothing more was typed.");
        }

        IntPtr front = DriverNative.GetForegroundWindow();
        if (front == IntPtr.Zero || DriverNative.ProcessOf(front) != (uint)driver.Pid)
        {
            throw new DriverException(
                $"focus stolen while {name} was typed: the foreground window is 0x{front:X} '{ProbeWindows.ClassOf(front)}' '{ProbeWindows.TitleOf(front)}', not a window of mmc.exe (pid {driver.Pid}). No more keys were sent.");
        }

        AutomationElement? focused = AutomationElement.FocusedElement;
        if (focused is null || focused.Current.NativeWindowHandle != control.ToInt32())
        {
            throw new DriverException($"the keyboard focus left {name} while it was typed. No more keys were sent.");
        }
    }

    private JsonObject SetCheck(OptionGroup group, bool wanted)
    {
        int before = ReadCheckState(group, out string readVia);
        string via = "none needed";
        if ((before == 1) != wanted)
        {
            via = "Toggle";
            AutomationElement button = AutomationElement.FromHandle(group.Control);
            if (button.TryGetCurrentPattern(TogglePattern.Pattern, out object? toggle))
            {
                ((TogglePattern)toggle).Toggle();
            }
            else
            {
                via = "BM_CLICK";
                DriverNative.Click(group.Control);
            }

            Thread.Sleep(300);
        }

        int observed = ReadCheckState(group, out string afterVia);
        readVia = JoinVia(readVia, afterVia);
        if ((observed == 1) != wanted)
        {
            DriverNative.Click(group.Control);
            Thread.Sleep(300);
            observed = ReadCheckState(group, out string againVia);
            readVia = JoinVia(readVia, againVia);
            via += " then BM_CLICK";
        }

        if ((observed == 1) != wanted)
        {
            throw new DriverException($"the check box '{group.Label}' did not reach {wanted} (check state {observed})");
        }

        return new JsonObject { ["requested"] = wanted, ["beforeCheckState"] = before, ["observedCheckState"] = observed, ["via"] = via, ["checkStateVia"] = readVia };
    }

    private static string JoinVia(string all, string next) => all.Split(" then ").Contains(next) ? all : all + " then " + next;

    // Drop-down item by exact text (raw, with any ampersand) or by position. A drop-down list only takes an existing item.
    private JsonObject SetCombo(OptionGroup group, OptionSpec spec)
    {
        List<string> items = DriverNative.ComboItems(group.Control);
        int index;
        if (spec.ItemIndex is { } position)
        {
            index = position;
        }
        else if (spec.Item is not null)
        {
            index = items.FindIndex(i => string.Equals(i, spec.Item, StringComparison.Ordinal));
        }
        else
        {
            throw new DriverException($"'item' or 'itemIndex' is needed for '{group.Label}' ({group.Kind})");
        }

        if (index < 0 || index >= items.Count)
        {
            throw new DriverException(
                $"'{group.Label}' has no item {(spec.Item is not null ? $"'{spec.Item}'" : $"#{index}")}. Items: " + string.Join(" | ", items.Select(i => $"'{i}'")),
                evidence: new JsonArray(items.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()));
        }

        int before = DriverNative.ComboSelected(group.Control);
        DriverNative.ComboSelect(group.Control, index);
        Thread.Sleep(200);
        CommitFocus();
        int observed = DriverNative.ComboSelected(group.Control);
        if (observed != index)
        {
            throw new DriverException($"'{group.Label}' did not select item {index} (selected index {observed})");
        }

        return new JsonObject
        {
            ["requestedItem"] = spec.Item,
            ["requestedIndex"] = index,
            ["beforeIndex"] = before,
            ["observedIndex"] = observed,
            ["observedText"] = items[observed],
            ["items"] = new JsonArray(items.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
        };
    }
}

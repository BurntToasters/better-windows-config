using System.Text.Json.Nodes;
using System.Windows.Automation;
using Bwc.Oracle.Capture.Gpedit.Probe;
using Bwc.Oracle.Capture.Gpedit.Session;

namespace Bwc.Oracle.Capture.Gpedit.Driver;

// The classic setting dialog of one policy (a WinForms form, automation ids are control names): title, state, comment, help, supported
// on, buttons. Options pane controls are in PolicyDialog.Options.cs and the Show Contents dialog of list elements in PolicyDialog.Lists.cs.
// Failure modes 4 to 9 and 13 of the driver document.
internal sealed partial class PolicyDialog
{
    private static readonly (string Id, string Label)[] StateRadios =
    {
        ("radioButtonNotConfigured", "Not Configured"),
        ("radioButtonEnabled", "Enabled"),
        ("radioButtonDisabled", "Disabled"),
    };

    private readonly GpeditDriver driver;
    private AutomationElement element;

    internal PolicyDialog(GpeditDriver driver, IntPtr hwnd, string requestedName, string openMethod)
    {
        this.driver = driver;
        Hwnd = hwnd;
        element = AutomationElement.FromHandle(hwnd);
        RequestedName = requestedName;
        OpenMethod = openMethod;
    }

    internal IntPtr Hwnd { get; private set; }

    internal string RequestedName { get; }

    internal string OpenMethod { get; }

    internal string TitleMatch { get; private set; } = "unchecked";

    internal string LastTitle { get; private set; } = string.Empty;

    internal bool IsOpen => GpeditDriver.IsOpen(Hwnd);

    internal AutomationElement Element(string id) =>
        GpeditDriver.ById(element, id)
            ?? throw new DriverException($"no control '{id}' in the setting dialog. Ids found: " + string.Join(", ", IdsFound()));

    private IEnumerable<string> IdsFound() => ProbeUia.Find(element, TreeScope.Descendants, Condition.TrueCondition)
        .Select(e => e.Current.AutomationId).Where(i => !string.IsNullOrEmpty(i)).Distinct().Take(80);

    internal string Title() => ProbeUia.NameOf(Element("labelTitle"));

    private static string WithoutAmp(string text) => text.Replace("&", string.Empty, StringComparison.Ordinal);

    // The dialog must be the one for the requested policy: the title label equals the display name (exact first, then ignoring a
    // mnemonic ampersand). A wrong dialog is cancelled before anything is set.
    internal void VerifyTitle()
    {
        string label = Title();
        string window = ProbeWindows.TitleOf(Hwnd);
        LastTitle = label;
        if (string.Equals(label, RequestedName, StringComparison.Ordinal))
        {
            TitleMatch = "exact";
        }
        else if (string.Equals(window, RequestedName, StringComparison.Ordinal))
        {
            TitleMatch = "windowTitleExact";
        }
        else if (string.Equals(WithoutAmp(label), WithoutAmp(RequestedName), StringComparison.Ordinal))
        {
            TitleMatch = "ignoringAmpersand";
        }
        else
        {
            WinInfo? info = ProbeWindows.TopLevel(driver.Pid).FirstOrDefault(w => w.Hwnd == Hwnd);
            if (info is not null)
            {
                driver.CancelWindow(info);
            }

            throw new DriverException($"the dialog is for '{label}' (window title '{window}'), not '{RequestedName}'. It was cancelled.");
        }
    }

    internal string ReadText(string id)
    {
        AutomationElement el = Element(id);
        int handle = el.Current.NativeWindowHandle;
        if (handle != 0)
        {
            return DriverNative.GetText(new IntPtr(handle));
        }

        if (el.TryGetCurrentPattern(TextPattern.Pattern, out object? pattern))
        {
            return ((TextPattern)pattern).DocumentRange.GetText(-1);
        }

        return ProbeUia.NameOf(el);
    }

    internal string ReadState()
    {
        var selected = new List<string>();
        foreach ((string id, string label) in StateRadios)
        {
            AutomationElement radio = Element(id);
            if (radio.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? p) && ((SelectionItemPattern)p).Current.IsSelected)
            {
                selected.Add(label);
            }
        }

        return selected.Count == 1 ? selected[0] : "unknown (" + selected.Count + " selected)";
    }

    internal JsonObject SetState(string state)
    {
        (string id, string label) = StateRadios.FirstOrDefault(r => r.Label == state);
        if (id is null)
        {
            throw new DriverException($"unknown state '{state}'");
        }

        string before = ReadState();
        AutomationElement radio = Element(id);
        string via = "SelectionItem";
        if (!ProbeUia.Select(radio))
        {
            if (ProbeUia.Invoke(radio))
            {
                via = "Invoke";
            }
            else
            {
                ProbeUia.Click(radio, doubleClick: false);
                via = "guarded click";
            }
        }

        Thread.Sleep(300);
        string after = ReadState();
        if (after != label)
        {
            throw new DriverException($"the state radio '{label}' did not stay selected (now '{after}')");
        }

        return new JsonObject { ["set"] = "state", ["requested"] = state, ["before"] = before, ["observed"] = after, ["via"] = via };
    }

    // The comment box is a multi-line edit: WM_SETTEXT would send it no change notification, so the dialog would not notice the edit (Apply
    // stays disabled, OK writes nothing). The text is typed with key presses; an empty text is select all and delete. The enabled state of
    // Apply before and after is recorded as evidence that the dialog noticed. Failure mode 30 of the driver document.
    internal JsonObject SetComment(string text)
    {
        AutomationElement box = Element("textBoxComment");
        IntPtr handle = new(box.Current.NativeWindowHandle);
        if (handle == IntPtr.Zero)
        {
            throw new DriverException("the comment box has no window handle");
        }

        RequireOwned(handle);
        string before = DriverNative.GetText(handle);
        bool? applyBefore = ApplyEnabled();
        TypeInto(handle, "the comment box", text, multiline: true, requireCleared: true);
        Thread.Sleep(200);
        string observed = DriverNative.GetText(handle);
        bool? applyAfter = ApplyEnabled();
        if (!string.Equals(NormalizeBreaks(observed), NormalizeBreaks(text), StringComparison.Ordinal))
        {
            throw new DriverException($"the comment text did not stick (asked {text.Length} characters, read {observed.Length}; Apply enabled before {applyBefore}, after {applyAfter})");
        }

        return new JsonObject
        {
            ["set"] = "comment",
            ["via"] = "keys",
            ["requested"] = text,
            ["before"] = before,
            ["observed"] = observed,
            ["applyEnabledBefore"] = applyBefore,
            ["applyEnabledAfter"] = applyAfter,
        };
    }

    // The enabled state of the Apply button, or null when the dialog has none.
    internal bool? ApplyEnabled() => GpeditDriver.ById(element, "buttonApply")?.Current.IsEnabled;

    // A control that is not inside this dialog is never written to.
    internal void RequireOwned(IntPtr control)
    {
        if (!DriverNative.IsChild(Hwnd, control))
        {
            throw new DriverException($"window 0x{control:X} is not inside the setting dialog 0x{Hwnd:X}");
        }
    }

    // Moves focus to the OK button so a numeric edit validates and clamps, as it does when the user leaves it.
    internal void CommitFocus()
    {
        ProbeUia.Focus(Element("buttonOK"));
        Thread.Sleep(300);
    }

    internal JsonObject Read(bool readLists)
    {
        var buttons = new JsonObject();
        foreach (string name in GpeditOp.Buttons)
        {
            AutomationElement? button = GpeditDriver.ById(element, "button" + name);
            buttons[name] = button is null ? null : new JsonObject { ["enabled"] = button.Current.IsEnabled };
        }

        LastTitle = Title();
        return new JsonObject
        {
            ["title"] = LastTitle,
            ["windowTitle"] = ProbeWindows.TitleOf(Hwnd),
            ["state"] = ReadState(),
            ["comment"] = ReadText("textBoxComment"),
            ["supportedOn"] = ReadText("labelSupportedOnText"),
            ["help"] = ReadText("textBoxHelp"),
            ["buttons"] = buttons,
            ["options"] = ReadOptions(readLists),
        };
    }

    private static string IdFor(string button) => "button" + button;

    // Presses one button of the dialog. The button gets focus first (a pending numeric edit validates), then Invoke. The result says
    // whether the dialog closed, whether a message box blocked it (captured and answered), and what classic wrote (settled files).
    internal JsonObject Press(string button, string? answer)
    {
        var result = new JsonObject { ["button"] = button };
        AutomationElement target = Element(IdFor(button));
        bool enabled = target.Current.IsEnabled;
        result["enabledBefore"] = enabled;
        if (!enabled)
        {
            result["pressed"] = false;
            result["reason"] = "the button is disabled";
            return result;
        }

        string titleBefore = Title();
        result["titleBefore"] = titleBefore;
        var known = driver.Dialogs().Select(w => w.Hwnd).ToHashSet();
        bool mayWrite = button != "Cancel";
        FileWatch watch = driver.Phases.Run("pressFileWatchBegin", () => FileWatch.Begin(driver.Context.Paths, driver.LastSnapshot));

        using (driver.Phases.Begin("pressInvoke"))
        {
            ProbeInput.Foreground(Hwnd);
            ProbeUia.Focus(target);
            Thread.Sleep(250);
            if (!ProbeUia.Invoke(target))
            {
                ProbeUia.Click(target, doubleClick: false);
                result["via"] = "guarded click";
            }
            else
            {
                result["via"] = "Invoke";
            }
        }

        result["pressed"] = true;
        var boxes = new JsonArray();
        bool closes = button is "OK" or "Cancel";
        bool closed = false;
        DateTime end = DateTime.UtcNow + (closes ? TimeSpan.FromSeconds(15) : button == "Apply" ? TimeSpan.FromMilliseconds(1200) : TimeSpan.FromSeconds(6));
        DateTime started = DateTime.UtcNow;
        using (driver.Phases.Begin("pressWaitLoop"))
        {
            while (DateTime.UtcNow < end)
            {
                driver.Context.ThrowIfCancelled();
                driver.ThrowIfDead();
                JsonArray found = driver.AnswerNewMessageBoxes(known, answer);
                if (found.Count > 0)
                {
                    foreach (JsonNode? box in found)
                    {
                        boxes.Add(box?.DeepClone());
                    }

                    result["blockedByMessageBox"] = true;
                    break;
                }

                if (closes && !IsOpen)
                {
                    closed = true;
                    break;
                }

                if (button is "Previous" or "Next" && MovedTo(known, titleBefore))
                {
                    break;
                }

                if (button == "Apply" && (DateTime.UtcNow - started).TotalMilliseconds >= 800)
                {
                    break;
                }

                Thread.Sleep(100);
            }
        }

        result["messageBoxes"] = boxes;
        result["closed"] = closed;
        if (closes && !closed && boxes.Count == 0)
        {
            string shot = driver.Shot(Hwnd, "stuck-" + button);
            throw new DriverException($"the dialog stayed open 15 seconds after {button}. Screenshot: {shot}");
        }

        if (button is "Previous" or "Next")
        {
            string titleAfter = IsOpen ? Title() : string.Empty;
            LastTitle = titleAfter;
            result["titleAfter"] = titleAfter;
            result["moved"] = titleAfter != titleBefore;
        }
        else if (IsOpen)
        {
            result["applyEnabledAfter"] = ApplyEnabled();
        }

        JsonObject settled = driver.Phases.Run("pressFileSettle", () => watch.Settle(driver.Context.Token, mayWrite ? 1200 : 300, mayWrite));
        if (watch.AfterSnapshot is not null)
        {
            driver.LastSnapshot = watch.AfterSnapshot;
        }

        result["files"] = settled;
        return result;
    }

    // After Previous or Next the form shows another policy: the title changes in the same window, or a new dialog replaces it.
    private bool MovedTo(HashSet<IntPtr> known, string titleBefore)
    {
        if (!IsOpen)
        {
            WinInfo? next = driver.NewDialogs(known).FirstOrDefault(d => driver.Classify(d) == DialogKind.Setting);
            if (next is null)
            {
                return false;
            }

            Hwnd = next.Hwnd;
            element = AutomationElement.FromHandle(Hwnd);
            return true;
        }

        return Title() != titleBefore;
    }
}

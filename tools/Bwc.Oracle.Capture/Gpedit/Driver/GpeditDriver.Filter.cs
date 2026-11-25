using System.Text.Json.Nodes;
using Bwc.Oracle.Capture.Gpedit.Probe;

namespace Bwc.Oracle.Capture.Gpedit.Driver;

// View > Filter Options (a Win32 dialog, resource dialog 3500 of the policy snap-in) and the Filter On toggle. The dialog is driven by
// its control ids with window messages. Failure mode 19 of the driver document: the filter is saved in the console state, so a run that
// sets it must clear it.
internal sealed partial class GpeditDriver
{
    private const int FilterManaged = 3501;
    private const int FilterConfigured = 3502;
    private const int FilterCommented = 3503;
    private const int FilterKeywordsEnabled = 3504;
    private const int FilterKeywords = 3505;
    private const int FilterInTitle = 3506;
    private const int FilterInHelp = 3507;
    private const int FilterInComment = 3508;
    private const int FilterRequirementsEnabled = 3509;
    private const int FilterPlatformCombo = 3510;
    private const int FilterSelectAll = 3512;
    private const int FilterClearAll = 3513;
    private const int FilterMatchCombo = 3517;
    private const string FilterOptionsItem = "Filter Options...";
    private const string FilterOnItem = "Filter On";

    private static IntPtr Item(IntPtr dialog, int id) =>
        Win32Ext.GetDlgItem(dialog, id) is var h && h != IntPtr.Zero ? h : throw new DriverException($"the Filter Options dialog has no control {id}");

    private JsonObject ReadFilterDialog(IntPtr dialog)
    {
        JsonObject Combo(int id)
        {
            IntPtr h = Item(dialog, id);
            List<string> items = DriverNative.ComboItems(h);
            int selected = DriverNative.ComboSelected(h);
            return new JsonObject
            {
                ["items"] = new JsonArray(items.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
                ["selectedIndex"] = selected,
                ["selectedText"] = selected >= 0 && selected < items.Count ? items[selected] : null,
                ["enabled"] = Win32Ext.IsWindowEnabled(h),
            };
        }

        JsonObject Check(int id)
        {
            IntPtr h = Item(dialog, id);
            return new JsonObject { ["checkState"] = DriverNative.CheckState(h), ["enabled"] = Win32Ext.IsWindowEnabled(h) };
        }

        return new JsonObject
        {
            ["managed"] = Combo(FilterManaged),
            ["configured"] = Combo(FilterConfigured),
            ["commented"] = Combo(FilterCommented),
            ["keywordsEnabled"] = Check(FilterKeywordsEnabled),
            ["keywords"] = new JsonObject { ["text"] = DriverNative.GetText(Item(dialog, FilterKeywords)), ["enabled"] = Win32Ext.IsWindowEnabled(Item(dialog, FilterKeywords)) },
            ["match"] = Combo(FilterMatchCombo),
            ["inTitle"] = Check(FilterInTitle),
            ["inHelp"] = Check(FilterInHelp),
            ["inComment"] = Check(FilterInComment),
            ["requirementsEnabled"] = Check(FilterRequirementsEnabled),
            ["platform"] = Combo(FilterPlatformCombo),
        };
    }

    private void SetFilterCombo(IntPtr dialog, int id, string text, string name)
    {
        IntPtr combo = Item(dialog, id);
        if (!Win32Ext.IsWindowEnabled(combo))
        {
            throw new DriverException($"the filter control '{name}' is disabled");
        }

        List<string> items = DriverNative.ComboItems(combo);
        int index = items.FindIndex(i => string.Equals(i, text, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new DriverException($"the filter control '{name}' has no item '{text}'. Items: " + string.Join(" | ", items.Select(i => $"'{i}'")));
        }

        DriverNative.ComboSelect(combo, index);
        Thread.Sleep(200);
        if (DriverNative.ComboSelected(combo) != index)
        {
            throw new DriverException($"the filter control '{name}' did not select '{text}'");
        }
    }

    private void SetFilterCheck(IntPtr dialog, int id, bool wanted, string name)
    {
        IntPtr box = Item(dialog, id);
        if (!Win32Ext.IsWindowEnabled(box))
        {
            throw new DriverException($"the filter check box '{name}' is disabled");
        }

        if ((DriverNative.CheckState(box) == 1) != wanted)
        {
            DriverNative.Click(box);
            Thread.Sleep(250);
        }

        if ((DriverNative.CheckState(box) == 1) != wanted)
        {
            throw new DriverException($"the filter check box '{name}' did not reach {wanted}");
        }
    }

    // Opens View > Filter Options, applies the given values (a null field is left alone), reads the dialog before and after, and
    // presses OK or Cancel. The dialog is never left open.
    internal JsonObject SetFilter(FilterSpec spec, string press, string? answer)
    {
        JsonObject opened = ActivateMenuItem("View", FilterOptionsItem);
        WinInfo? dialog = ProbeUia.Wait(() => Dialogs().FirstOrDefault(d => Classify(d) == DialogKind.FilterOptions), TimeSpan.FromSeconds(10));
        if (dialog is null)
        {
            throw new DriverException("the Filter Options dialog did not open", evidence: opened);
        }

        var result = new JsonObject { ["opened"] = opened["keys"]?.DeepClone() };
        try
        {
            Thread.Sleep(300);
            result["before"] = ReadFilterDialog(dialog.Hwnd);
            IntPtr hwnd = dialog.Hwnd;
            if (spec.KeywordsEnabled is { } kw)
            {
                SetFilterCheck(hwnd, FilterKeywordsEnabled, kw, "Enable Keyword Filters");
            }

            if (spec.RequirementsEnabled is { } rq)
            {
                SetFilterCheck(hwnd, FilterRequirementsEnabled, rq, "Enable Requirements Filters");
            }

            if (spec.Managed is not null)
            {
                SetFilterCombo(hwnd, FilterManaged, spec.Managed, "Managed");
            }

            if (spec.Configured is not null)
            {
                SetFilterCombo(hwnd, FilterConfigured, spec.Configured, "Configured");
            }

            if (spec.Commented is not null)
            {
                SetFilterCombo(hwnd, FilterCommented, spec.Commented, "Commented");
            }

            if (spec.Keywords is not null)
            {
                IntPtr edit = Item(hwnd, FilterKeywords);
                if (!Win32Ext.IsWindowEnabled(edit))
                {
                    throw new DriverException("the keyword edit is disabled (set keywordsEnabled true)");
                }

                DriverNative.SetText(edit, spec.Keywords);
                if (DriverNative.GetText(edit) != spec.Keywords)
                {
                    throw new DriverException("the keyword text did not stick");
                }
            }

            if (spec.Match is not null)
            {
                SetFilterCombo(hwnd, FilterMatchCombo, spec.Match, "Match");
            }

            if (spec.InTitle is { } title)
            {
                SetFilterCheck(hwnd, FilterInTitle, title, "Policy Setting Title");
            }

            if (spec.InHelp is { } help)
            {
                SetFilterCheck(hwnd, FilterInHelp, help, "Help Text");
            }

            if (spec.InComment is { } comment)
            {
                SetFilterCheck(hwnd, FilterInComment, comment, "Comment");
            }

            if (spec.Platforms is not null)
            {
                DriverNative.Click(Item(hwnd, spec.Platforms == "all" ? FilterSelectAll : FilterClearAll));
                Thread.Sleep(300);
            }

            result["after"] = ReadFilterDialog(hwnd);
            result["screenshot"] = Shot(hwnd, "filter-options");
            var known = Dialogs().Select(w => w.Hwnd).ToHashSet();
            DriverNative.Click(Item(hwnd, press == "OK" ? 1 : Wm.IDCANCEL));
            DateTime end = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < end)
            {
                JsonArray boxes = AnswerNewMessageBoxes(known, answer);
                if (boxes.Count > 0)
                {
                    result["messageBoxes"] = boxes;
                    result["blockedByMessageBox"] = true;
                    break;
                }

                if (!IsOpen(hwnd))
                {
                    result["closed"] = true;
                    break;
                }

                Thread.Sleep(100);
            }

            result["press"] = press;
            return result;
        }
        finally
        {
            if (IsOpen(dialog.Hwnd))
            {
                CancelWindow(dialog);
            }

            WaitListStable(TimeSpan.FromSeconds(8));
        }
    }

    // Turns the Filter On menu item to the wanted state. Reads the check mark before and after; an item that is grayed and already in the
    // wanted state needs nothing.
    internal JsonObject SetFilterOn(bool on)
    {
        JsonObject before = MenuItemInfo("View", FilterOnItem);
        bool current = before["checked"]!.GetValue<bool>();
        var result = new JsonObject { ["wanted"] = on, ["checkedBefore"] = current };
        if (current != on)
        {
            result["activated"] = ActivateMenuItem("View", FilterOnItem)["keys"]?.DeepClone();
            Thread.Sleep(800);
            WaitListStable(TimeSpan.FromSeconds(8));
            bool after = MenuItemInfo("View", FilterOnItem)["checked"]!.GetValue<bool>();
            result["checkedAfter"] = after;
            if (after != on)
            {
                throw new DriverException($"Filter On is {(after ? "checked" : "unchecked")} after the toggle, not {(on ? "checked" : "unchecked")}", evidence: result);
            }
        }
        else
        {
            result["checkedAfter"] = current;
        }

        result["status"] = ReadStatus();
        return result;
    }

    // All filters off and Filter On unchecked, then the status bar must not say Filtered.
    internal JsonObject ClearFilter()
    {
        var spec = new FilterSpec
        {
            Managed = "Any",
            Configured = "Any",
            Commented = "Any",
            KeywordsEnabled = false,
            RequirementsEnabled = false,
        };
        var result = new JsonObject { ["dialog"] = SetFilter(spec, "OK", null), ["filterOn"] = SetFilterOn(false) };
        bool filtered = ReadStatus()["filtered"]!.GetValue<bool>();
        result["stillFiltered"] = filtered;
        if (filtered)
        {
            throw new DriverException("the status bar still says Filtered after clearing the filter", evidence: result);
        }

        return result;
    }
}

using System.Text.Json.Nodes;

namespace Bwc.Oracle.Capture;

// `binary`: opens View > Display Binary Data on one value of each type, and Modify Binary Data on a REG_BINARY value.
// The dialog variant is named by its control ids (template 2020 has 8 controls, template 2021 has 10 with id 2026, the
// "Value Type:" label). Each Format radio is clicked in turn (a display change only) and the first list line is read.
// Every dialog is closed with IDCANCEL. Tracked: regedit-binary.json. Text: local/binary-text.json.
internal static class BinaryCapture
{
    private const int BinaryDataA = 2020;
    private const int BinaryDataB = 2021;
    private const int EditBinaryValue = 103;
    private const int DisplayBinaryCommand = 679;
    private const int ModifyBinaryCommand = 917;
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(10);

    private sealed record Sample(string Name, string Type, string Key, string Value);

    private static readonly Sample[] Samples =
    {
        new("reg-sz", "REG_SZ", "Strings", "Ascii"),
        new("reg-expand-sz", "REG_EXPAND_SZ", "Strings", "ExpandSz"),
        new("reg-dword", "REG_DWORD", "Numbers", "Dword1"),
        new("reg-qword", "REG_QWORD", "Numbers", "Qword0"),
        new("reg-binary-one-byte", "REG_BINARY", "Binary", "OneByte"),
        new("reg-binary-300-bytes", "REG_BINARY", "Binary", "Wrap300"),
        new("reg-none", "REG_NONE", "Binary", "NoneData"),
        new("reg-multi-sz", "REG_MULTI_SZ", "MultiSz", "Several"),
        new("reg-resource-list", "REG_RESOURCE_LIST", "OddTypes", "Resource8"),
    };

    internal static void Run(RepoPaths paths, bool allowDpi)
    {
        uint systemDpi = OracleSession.RequireDpi(allowDpi);
        ResourceDump dump = ResourceDump.Load(paths);
        var display = new JsonArray();
        var displayText = new JsonArray();
        var notMeasured = new JsonArray();
        uint windowDpi = systemDpi;

        foreach (Sample sample in Samples)
        {
            try
            {
                Observed observed = ViewBinary(dump, sample);
                display.Add(observed.Tracked);
                displayText.Add(observed.Text);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"binary: {sample.Name} not measured: {ex.Message}");
                notMeasured.Add(new JsonObject { ["name"] = sample.Name, ["reason"] = Report.StableReason(ex.Message) });
            }
        }

        JsonObject modifyTracked;
        JsonObject modifyText;
        try
        {
            Observed modify = ModifyBinary(dump);
            modifyTracked = modify.Tracked;
            modifyText = modify.Text;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"binary: modify-binary-data not measured: {ex.Message}");
            modifyTracked = new JsonObject { ["measured"] = false, ["reason"] = Report.StableReason(ex.Message) };
            modifyText = new JsonObject();
            notMeasured.Add(new JsonObject { ["name"] = "modify-binary-data", ["reason"] = Report.StableReason(ex.Message) });
        }

        Report.WriteJson(paths.BinaryJson, new JsonObject
        {
            ["schema"] = 1,
            ["generator"] = "Bwc.Oracle.Capture binary",
            ["os"] = Report.OsInfo(),
            ["regedit"] = Report.RegeditInfo(),
            ["dpi"] = Report.DpiInfo(systemDpi, windowDpi, allowDpi),
            ["note"] = "Variant A is template 2020 (no Value Type label). Variant B is template 2021 (with the label, id 2026). Text is in local/binary-text.json.",
            ["displayBinaryData"] = display,
            ["modifyBinaryData"] = modifyTracked,
            ["notMeasured"] = notMeasured,
        });

        Report.WriteJson(paths.LocalText("binary-text.json"), new JsonObject
        {
            ["displayBinaryData"] = displayText,
            ["modifyBinaryData"] = modifyText,
        });
    }

    private static Observed ViewBinary(ResourceDump dump, Sample sample)
    {
        using RegeditWindow w = UiProbe.StartAt(UiProbe.LastKeyFor(sample.Key), waitForList: true);
        int index = IndexOf(w, sample.Value);
        w.SelectListRow(index);
        w.FocusList();
        Thread.Sleep(300);

        (IntPtr menu, int position) = w.Find("&View", "&Display Binary Data...");
        if (w.IsGrayed(menu, position))
        {
            return new Observed(sample.Name,
                new JsonObject { ["name"] = sample.Name, ["valueType"] = sample.Type, ["measured"] = true, ["enabled"] = false, ["opened"] = false },
                new JsonObject { ["name"] = sample.Name });
        }

        HashSet<IntPtr> before = UiProbe.VisibleTopLevels(w.ProcessId);
        w.PostMenuCommand("&View", "&Display Binary Data...");
        IntPtr dlg = UiProbe.WaitForNewTopLevel(w.ProcessId, before, OpenTimeout, RegeditWindow.DialogClass);
        if (dlg == IntPtr.Zero)
        {
            throw new InvalidOperationException($"View > Display Binary Data is enabled for {sample.Type} but opened no dialog.");
        }

        Thread.Sleep(800);
        try
        {
            List<UiProbe.Ctl> controls = UiProbe.Controls(w, dlg);
            int? template = dump.MatchDialog(UiProbe.SortedControlIds(controls));
            string variant = template switch
            {
                BinaryDataA => "A",
                BinaryDataB => "B",
                _ => throw new InvalidOperationException($"the Binary Data dialog matched no template; control ids: {string.Join(",", UiProbe.SortedControlIds(controls))}"),
            };

            List<UiProbe.Ctl> radios = controls.Where(c => c.Class == "Button" && IsRadio(c.Style)).ToList();
            int? defaultRadio = radios.FirstOrDefault(r => r.Checked == 1) is { } d ? d.Id : null;
            IntPtr listBox = UiProbe.ChildOfClass(w, dlg, "ListBox");
            string listClass = listBox == IntPtr.Zero ? "none" : "ListBox";

            var units = new JsonArray();
            var unitsText = new JsonArray();
            foreach (UiProbe.Ctl radio in radios)
            {
                Win32.PostMessageW(radio.Hwnd, Wm.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                Thread.Sleep(400);
                int count = listBox == IntPtr.Zero ? -1 : UiProbe.ListBoxCount(w, listBox);
                string firstLine = listBox == IntPtr.Zero || count <= 0 ? string.Empty : UiProbe.ListBoxText(w, listBox, 0);
                // LB_GETTEXT returns no string when the list is owner-drawn without stored strings. That is recorded, not a 0.
                units.Add(new JsonObject
                {
                    ["radioId"] = radio.Id,
                    ["lineCount"] = count,
                    ["firstLineReadable"] = firstLine.Length > 0,
                });
                unitsText.Add(new JsonObject { ["radioId"] = radio.Id, ["text"] = radio.Text, ["firstLine"] = firstLine });
            }

            var tracked = new JsonObject
            {
                ["name"] = sample.Name,
                ["valueType"] = sample.Type,
                ["measured"] = true,
                ["enabled"] = true,
                ["opened"] = true,
                ["dialogTemplate"] = UiProbe.Num(template),
                ["variant"] = variant,
                ["listClass"] = listClass,
                ["defaultRadioId"] = UiProbe.Num(defaultRadio),
                ["radioIds"] = new JsonArray(radios.Select(r => (JsonNode?)JsonValue.Create(r.Id)).ToArray()),
                ["units"] = units,
                ["controls"] = UiProbe.ControlsTracked(controls),
            };
            var text = new JsonObject
            {
                ["name"] = sample.Name,
                ["title"] = UiProbe.TextOf(dlg),
                ["radios"] = unitsText,
                ["controls"] = UiProbe.ControlsLocal(controls),
            };
            return new Observed(sample.Name, tracked, text);
        }
        finally
        {
            w.CloseDialog(dlg);
        }
    }

    // Modify Binary Data on a REG_BINARY value opens the Edit Binary Value dialog. The caption goes to the local text.
    private static Observed ModifyBinary(ResourceDump dump)
    {
        using RegeditWindow w = UiProbe.StartAt(UiProbe.LastKeyFor("Binary"), waitForList: true);
        int index = IndexOf(w, "OneByte");
        w.SelectListRow(index);
        w.FocusList();
        Thread.Sleep(300);

        HashSet<IntPtr> before = UiProbe.VisibleTopLevels(w.ProcessId);
        Win32.PostMessageW(w.Main, Wm.COMMAND, new IntPtr(ModifyBinaryCommand), IntPtr.Zero);
        IntPtr dlg = UiProbe.WaitForNewTopLevel(w.ProcessId, before, OpenTimeout, RegeditWindow.DialogClass);
        if (dlg == IntPtr.Zero)
        {
            throw new InvalidOperationException("Modify Binary Data opened no dialog for a REG_BINARY value.");
        }

        Thread.Sleep(800);
        try
        {
            Observed described = UiProbe.Describe(w, dlg, "modify-binary-data");
            int? template = dump.MatchDialog(UiProbe.SortedControlIds(UiProbe.Controls(w, dlg)), UiProbe.TextOf(dlg));
            described.Tracked["dialogTemplate"] = UiProbe.Num(template);
            described.Tracked["measured"] = true;
            described.Tracked["expectedTemplate"] = EditBinaryValue;
            described.Tracked["matchesExpectedTemplate"] = template == EditBinaryValue;
            return described;
        }
        finally
        {
            w.CloseDialog(dlg);
        }
    }

    private static bool IsRadio(int style)
    {
        int type = style & 0xF;
        return type is 4 or 9;
    }

    private static int IndexOf(RegeditWindow w, string name)
    {
        w.WaitForListPopulated(TimeSpan.FromSeconds(25));
        List<string> names = w.ListNames();
        int index = names.IndexOf(name);
        if (index < 0)
        {
            throw new InvalidOperationException($"value '{name}' is not in the list.");
        }

        return index;
    }
}

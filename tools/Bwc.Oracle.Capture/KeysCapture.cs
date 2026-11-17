using System.Text.Json.Nodes;

namespace Bwc.Oracle.Capture;

// `keys`: presses each navigation key with focus in the tree and again in the value list, and posts each command id of
// the accelerator table with WM_COMMAND. Every run is a fresh regedit process with BWC-Oracle\Mixed selected. The state
// before and after is read, every window that opened is described and then closed. Nothing is chosen from a menu.
// Tracked: regedit-keys.json (ids, classes, counts, flags). Text: local/keys-text.json.
internal static class KeysCapture
{
    private const string Focus0 = "tree";
    private const string Focus1 = "list";

    private sealed record KeySpec(string Name, int Vk, KeyInput.Mods Mods);

    private static readonly KeySpec[] Keys =
    {
        new("F4", 0x73, default),
        new("Alt+1", 0x31, new KeyInput.Mods(Alt: true)),
        new("Alt+2", 0x32, new KeyInput.Mods(Alt: true)),
        new("Alt+3", 0x33, new KeyInput.Mods(Alt: true)),
        new("Alt+4", 0x34, new KeyInput.Mods(Alt: true)),
        new("Alt+5", 0x35, new KeyInput.Mods(Alt: true)),
        new("Alt+6", 0x36, new KeyInput.Mods(Alt: true)),
        new("Alt+7", 0x37, new KeyInput.Mods(Alt: true)),
        new("Alt+8", 0x38, new KeyInput.Mods(Alt: true)),
        new("Alt+9", 0x39, new KeyInput.Mods(Alt: true)),
        new("Alt+Up", 0x26, new KeyInput.Mods(Alt: true)),
        new("Shift+F10", 0x79, new KeyInput.Mods(Shift: true)),
        new("Shift+F6", 0x75, new KeyInput.Mods(Shift: true)),
        new("Backspace", 0x08, default),
    };

    // The command ids the accelerator table posts (F4 642, Alt+1..9 689..697, Alt+Up 698, Shift+F10 896), posted
    // directly.
    private static readonly int[] Commands = { 642, 689, 690, 691, 692, 693, 694, 695, 696, 697, 698, 896 };

    private sealed record KeyRun(
        string Name,
        string Kind,
        string Focus,
        int? Accelerator,
        int? CommandId,
        bool Changed,
        JsonObject Tracked,
        JsonObject Text);

    internal static void Run(RepoPaths paths, bool allowDpi)
    {
        uint systemDpi = OracleSession.RequireDpi(allowDpi);
        ResourceDump dump = ResourceDump.Load(paths);
        List<(int Key, int Flags, int Command)> accelerators = dump.Accelerators();

        var runs = new List<KeyRun>();
        var notMeasured = new JsonArray();
        uint windowDpi = 0;

        foreach (KeySpec key in Keys)
        {
            foreach (string focus in new[] { Focus0, Focus1 })
            {
                string name = $"{key.Name} ({focus})";
                try
                {
                    int? accel = AcceleratorFor(accelerators, key);
                    (KeyRun run, uint dpi) = PressKey(key, focus, accel);
                    runs.Add(run);
                    windowDpi = dpi;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"keys: {name} not measured: {ex.Message}");
                    notMeasured.Add(new JsonObject { ["name"] = name, ["reason"] = Report.StableReason(ex.Message) });
                }
            }
        }

        foreach (int id in Commands)
        {
            string name = $"WM_COMMAND {id}";
            try
            {
                (KeyRun run, uint dpi) = PostCommand(id);
                runs.Add(run);
                windowDpi = dpi;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"keys: {name} not measured: {ex.Message}");
                notMeasured.Add(new JsonObject { ["name"] = name, ["reason"] = Report.StableReason(ex.Message) });
            }
        }

        if (windowDpi == 0)
        {
            windowDpi = systemDpi;
        }

        // A key whose accelerator command changed something while the key itself changed nothing is a delivery suspect.
        var keyRuns = new JsonArray();
        var localRuns = new JsonArray();
        foreach (KeyRun run in runs)
        {
            JsonObject tracked = run.Tracked;
            if (run.Kind == "key")
            {
                KeyRun? command = run.Accelerator is int accel ? runs.FirstOrDefault(r => r.Kind == "command" && r.CommandId == accel) : null;
                tracked["deliveryMismatch"] = command is null ? null : JsonValue.Create(!run.Changed && command.Changed);
            }

            keyRuns.Add(tracked);
            localRuns.Add(run.Text);
        }

        Report.WriteJson(paths.KeysJson, new JsonObject
        {
            ["schema"] = 1,
            ["generator"] = "Bwc.Oracle.Capture keys",
            ["os"] = Report.OsInfo(),
            ["regedit"] = Report.RegeditInfo(),
            ["dpi"] = Report.DpiInfo(systemDpi, windowDpi, allowDpi),
            ["note"] = "Each run is a fresh regedit process with BWC-Oracle\\Mixed selected. Keys are posted as messages with the modifier state set on the regedit thread. Every window that opened was closed. Text is in local/keys-text.json.",
            ["runs"] = keyRuns,
            ["notMeasured"] = notMeasured,
        });

        Report.WriteJson(paths.LocalText("keys-text.json"), new JsonObject
        {
            ["runs"] = localRuns,
        });
    }

    private static int? AcceleratorFor(List<(int Key, int Flags, int Command)> table, KeySpec key)
    {
        foreach ((int vk, int flags, int command) in table)
        {
            if (vk == key.Vk && (flags & 0x1C) == key.Mods.AccelFlags)
            {
                return command;
            }
        }

        return null;
    }

    private static (KeyRun Run, uint Dpi) PressKey(KeySpec key, string focus, int? accel)
    {
        using RegeditWindow w = UiProbe.StartAt(UiProbe.LastKeyFor("Mixed"), waitForList: true);
        IntPtr target = focus == Focus0 ? w.Tree : w.ListView;
        UiProbe.EnsureFocus(w, target);
        uint dpi = Win32.GetDpiForWindow(w.Main);
        KeyRun run = Observe(w, $"{key.Name} ({focus})", "key", focus, accel, null, () => KeyInput.Press(w.Main, target, key.Vk, key.Mods),
            keyJson: (key.Vk, key.Mods));
        return (run, dpi);
    }

    private static (KeyRun Run, uint Dpi) PostCommand(int id)
    {
        using RegeditWindow w = UiProbe.StartAt(UiProbe.LastKeyFor("Mixed"), waitForList: true);
        UiProbe.EnsureFocus(w, w.Tree);
        uint dpi = Win32.GetDpiForWindow(w.Main);
        KeyRun run = Observe(w, $"WM_COMMAND {id}", "command", Focus0, null, id, () => Win32.PostMessageW(w.Main, Wm.COMMAND, new IntPtr(id), IntPtr.Zero));
        return (run, dpi);
    }

    private static KeyRun Observe(RegeditWindow w, string name, string kind, string focus, int? accel, int? commandId, Action trigger,
        (int Vk, KeyInput.Mods Mods)? keyJson = null)
    {
        UiProbe.Probe before = UiProbe.Read(w);
        HashSet<IntPtr> windowsBefore = UiProbe.VisibleTopLevels(w.ProcessId);
        trigger();
        UiProbe.Settle800();
        UiProbe.Probe after = UiProbe.Read(w);

        var opened = new JsonArray();
        var openedText = new JsonArray();
        // Sorted by class, then handle: handle values differ between runs, the class of each window does not.
        List<IntPtr> fresh = UiProbe.VisibleTopLevels(w.ProcessId)
            .Where(h => !windowsBefore.Contains(h))
            .OrderBy(h => RegeditWindow.ClassOf(h), StringComparer.Ordinal)
            .ThenBy(h => h.ToInt64())
            .ToList();
        foreach (IntPtr h in fresh)
        {
            Observed described = UiProbe.Describe(w, h, name);
            opened.Add(described.Tracked);
            openedText.Add(described.Text);
        }

        foreach (IntPtr h in fresh)
        {
            UiProbe.CloseWindow(w, h);
        }

        bool changed = before.FocusClass != after.FocusClass || before.FocusId != after.FocusId || before.CaretDepth != after.CaretDepth
            || before.CaretIndex != after.CaretIndex || before.CaretPath != after.CaretPath || before.ListCount != after.ListCount
            || before.ListSelected != after.ListSelected || before.Address != after.Address || before.FocusText != after.FocusText
            || fresh.Count > 0;

        var tracked = new JsonObject
        {
            ["name"] = name,
            ["kind"] = kind,
            ["focus"] = focus,
            ["accelerator"] = UiProbe.Num(accel),
            ["commandId"] = UiProbe.Num(commandId),
            ["measured"] = true,
            ["changed"] = changed,
            ["before"] = UiProbe.TrackedProbe(before),
            ["after"] = UiProbe.TrackedProbe(after),
            ["newWindows"] = opened,
        };
        if (keyJson is { } k)
        {
            tracked["vk"] = k.Vk;
            var modifiers = new JsonArray();
            foreach (string modifier in k.Mods.Names())
            {
                modifiers.Add(modifier);
            }

            tracked["modifiers"] = modifiers;
        }

        var text = new JsonObject
        {
            ["name"] = name,
            ["before"] = UiProbe.LocalProbe(before),
            ["after"] = UiProbe.LocalProbe(after),
            ["newWindows"] = openedText,
        };

        return new KeyRun(name, kind, focus, accel, commandId, changed, tracked, text);
    }
}

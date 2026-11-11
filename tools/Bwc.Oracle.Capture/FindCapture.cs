using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Bwc.Oracle.Capture;

// `find`: seeds keys and values of every data type with the needle "ab" in names and data (case variants,
// whole-string matches, substrings), drives classic Find for each option set, then repeats Find Next (F3) and
// records every hit as (key path, value name). The dialog is filled by WM_SETTEXT and BM_SETCHECK, and the
// search is started with BM_CLICK on Find Next. Cancel is the only way the dialog is closed. Find state is
// restored with the applet snapshot after the capture.
internal static class FindCapture
{
    private const string Needle = "ab";
    private const string Prefix = @"Computer\HKEY_CURRENT_USER\Software\BWC-Oracle\";
    private const string StartKey = @"Computer\HKEY_CURRENT_USER\Software\BWC-Oracle\Find";
    private const int MaxHits = 40;

    // Dialog control ids of the classic Find dialog (template 106): Find Next, Cancel, the find-what edit,
    // the three Look at checkboxes, Match whole string only, and Search in subtree.
    private const int FindNextId = 1;
    private const int CancelId = 2;
    private const int FindWhatId = 1150;
    private const int WholeId = 1151;
    private const int KeysId = 1152;
    private const int ValuesId = 1153;
    private const int DataId = 1154;
    private const int SubtreeId = 1161;

    private static readonly TimeSpan DialogTimeout = TimeSpan.FromSeconds(25);

    private sealed record Options(string Name, bool Keys, bool Values, bool Data, bool Whole);

    private static readonly Options[] Configs =
    {
        new("keys-values-data", Keys: true, Values: true, Data: true, Whole: false),
        new("keys-values-data-whole", Keys: true, Values: true, Data: true, Whole: true),
        new("keys-only", Keys: true, Values: false, Data: false, Whole: false),
        new("values-only", Keys: false, Values: true, Data: false, Whole: false),
        new("data-only", Keys: false, Values: false, Data: true, Whole: false),
        new("values-data-whole", Keys: false, Values: true, Data: true, Whole: true),
    };

    internal static void Run(RepoPaths paths, bool allowDpi)
    {
        uint systemDpi = OracleSession.RequireDpi(allowDpi);
        var seedRecords = new JsonArray();
        Seed(seedRecords);

        var runs = new JsonArray();
        uint windowDpi = systemDpi;
        foreach (Options options in Configs)
        {
            (JsonObject run, uint dpi) = RunOne(options);
            runs.Add(run);
            windowDpi = dpi;
        }

        Report.WriteJson(paths.FindSequenceJson, new JsonObject
        {
            ["schema"] = 1,
            ["generator"] = "Bwc.Oracle.Capture find",
            ["os"] = Report.OsInfo(),
            ["regedit"] = Report.RegeditInfo(),
            ["dpi"] = Report.DpiInfo(systemDpi, windowDpi, allowDpi),
            ["needle"] = Needle,
            ["startKey"] = @"HKEY_CURRENT_USER\Software\BWC-Oracle\Find",
            ["searchInSubtree"] = true,
            ["matchCase"] = "not offered by classic Find; the search is case-insensitive",
            ["note"] = "Registry value names are case-insensitive, so the seeded AB and ab in the Find key are one value; the later write (AB) holds the data AB. A hit reports the key by its tree path and the value name as shown in the list. The sequence stops when the selection leaves the Find tree.",
            ["seed"] = seedRecords,
            ["runs"] = runs,
        });
    }

    private static (JsonObject Run, uint Dpi) RunOne(Options options)
    {
        RegistryGuard.SetAppletValue(string.Empty, "LastKey", RegType.Sz, SeedSpec.Sz(StartKey));
        using RegeditWindow w = RegeditWindow.Start();
        w.WaitForListPopulated(DialogTimeout);
        uint dpi = Win32.GetDpiForWindow(w.Main);

        var before = new HashSet<IntPtr>(w.OwnedDialogs());
        w.PostMenuCommand("&Edit", "&Find...\tCtrl+F");
        IntPtr dlg = w.WaitForNewDialog(before, DialogTimeout);
        Thread.Sleep(400);

        Fill(w, dlg, options);
        Win32.PostMessageW(GetControl(dlg, FindNextId), Wm.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        Thread.Sleep(600);

        // Cancel closes the dialog if Find Next left it open. CloseDialog returns at once when it is already gone.
        w.CloseDialog(dlg);

        var hits = new JsonArray();
        string endedBy = "max-hits";
        (string Address, string Value)? previous = null;
        for (int i = 0; i < MaxHits; i++)
        {
            (string Address, string Value) state = i == 0 ? ReadAfterSettle(w) : ReadAfterFindNext(w, previous!.Value);
            if (previous is not null && state == previous.Value)
            {
                endedBy = "no-change";
                break;
            }

            // Hits outside the seeded Find tree are other registry content. They are not recorded, and the sequence stops.
            if (!state.Address.StartsWith(StartKey, StringComparison.OrdinalIgnoreCase))
            {
                endedBy = "left-find-tree";
                break;
            }

            hits.Add(new JsonObject { ["key"] = Relative(state.Address), ["value"] = state.Value });
            previous = state;

            IntPtr dialogAfter = w.OwnedDialogs().FirstOrDefault();
            if (dialogAfter != IntPtr.Zero)
            {
                endedBy = "dialog";
                w.CloseDialog(dialogAfter);
                break;
            }

            Win32.PostMessageW(w.Main, Wm.COMMAND, new IntPtr(671), IntPtr.Zero);
        }

        w.CloseGracefully();
        return (new JsonObject
        {
            ["flags"] = new JsonObject
            {
                ["keys"] = options.Keys,
                ["values"] = options.Values,
                ["data"] = options.Data,
                ["wholeString"] = options.Whole,
                ["searchInSubtree"] = true,
            },
            ["name"] = options.Name,
            ["hits"] = hits,
            ["endedBy"] = endedBy,
        }, dpi);
    }

    private static void Fill(RegeditWindow w, IntPtr dlg, Options options)
    {
        IntPtr edit = GetControl(dlg, FindWhatId);
        Win32Ext.SendMessageText(edit, Wm.WM_SETTEXT, IntPtr.Zero, Needle);
        SetCheck(w, dlg, WholeId, options.Whole);
        SetCheck(w, dlg, KeysId, options.Keys);
        SetCheck(w, dlg, ValuesId, options.Values);
        SetCheck(w, dlg, DataId, options.Data);
        SetCheck(w, dlg, SubtreeId, true);
    }

    private static void SetCheck(RegeditWindow w, IntPtr dlg, int id, bool value)
    {
        IntPtr control = GetControl(dlg, id);
        w.Send(control, Wm.BM_SETCHECK, new IntPtr(value ? 1 : 0), IntPtr.Zero);
    }

    private static IntPtr GetControl(IntPtr dlg, int id)
    {
        IntPtr h = Win32Ext.GetDlgItem(dlg, id);
        if (h == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Find dialog 0x{dlg:X} has no control {id}.");
        }

        return h;
    }

    // Waits for the selection to move after a Find Next or F3, then returns the first state that two reads agree on.
    private static (string Address, string Value) ReadAfterFindNext(RegeditWindow w, (string Address, string Value) previous)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        (string Address, string Value) last = ReadState(w);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(250);
            (string Address, string Value) now = ReadState(w);
            if (now == last && now != previous)
            {
                return now;
            }

            last = now;
        }

        return last;
    }

    private static (string Address, string Value) ReadAfterSettle(RegeditWindow w)
    {
        Thread.Sleep(500);
        return ReadState(w);
    }

    // The hit is the key shown in the address bar plus the selected value name ("" when a key is the hit).
    private static (string Address, string Value) ReadState(RegeditWindow w)
    {
        string address = w.TreePathOf(w.TreeCaret());
        int selected = w.SelectedListIndex();
        string value = selected >= 0 ? w.ListItemText(selected, 0) : string.Empty;
        return (address, value);
    }

    private static string Relative(string address)
    {
        return address.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? address[Prefix.Length..] : address;
    }

    private static void Seed(JsonArray records)
    {
        RegistryGuard.DeleteOracleTree();
        using (RegistryGuard.CreateOracleKey(string.Empty))
        {
        }

        SeedKey("Find", new (string Name, uint Type, byte[] Data)[]
        {
            ("ab", RegType.Sz, SeedSpec.Sz("whole name, lower")),
            ("xAbx", RegType.Sz, SeedSpec.Sz("Data ab with mixed case")),
            ("AB", RegType.Sz, SeedSpec.Sz("AB")),
            ("Name", RegType.Sz, SeedSpec.Sz("ab")),
            ("dw", RegType.Dword, SeedSpec.Dword(0xAB)),
            ("bin", RegType.Binary, Encoding.ASCII.GetBytes("ab")),
            ("ms", RegType.MultiSz, SeedSpec.MultiSz("zz", "ab")),
            ("ex", RegType.ExpandSz, SeedSpec.Sz("%ab%")),
            ("none", RegType.Sz, SeedSpec.Sz("nothing here")),
            ("Mixed Ab", RegType.Sz, SeedSpec.Sz("xx")),
        }, records);

        SeedKey(@"Find\Cab", Array.Empty<(string, uint, byte[])>(), records);
        SeedKey(@"Find\ABC", new (string Name, uint Type, byte[] Data)[] { ("k", RegType.Sz, SeedSpec.Sz("AB-data")) }, records);
        SeedKey(@"Find\ab", new (string Name, uint Type, byte[] Data)[] { ("v", RegType.Sz, SeedSpec.Sz("nothing")) }, records);
        SeedKey(@"Find\Zed\Inner\abracadabra", Array.Empty<(string, uint, byte[])>(), records);
        SeedKey(@"Find\Zed\Inner\ab", new (string Name, uint Type, byte[] Data)[] { ("q", RegType.Sz, SeedSpec.Sz("z")) }, records);
        SeedKey(@"Find\Plain", new (string Name, uint Type, byte[] Data)[] { ("ab", RegType.Sz, SeedSpec.Sz("ab")) }, records);
        Console.WriteLine($"find: seeded the Find tree with needle '{Needle}'");
    }

    private static void SeedKey(string relative, IEnumerable<(string Name, uint Type, byte[] Data)> values, JsonArray records)
    {
        string full = RegistryGuard.OracleRoot + @"\" + relative;
        using RegistryKey key = RegistryGuard.CreateOracleKey(relative);
        var list = new JsonArray();
        foreach ((string name, uint type, byte[] data) in values)
        {
            RegistryGuard.SetRawValue(key, full, name, type, data);
            list.Add(new JsonObject
            {
                ["name"] = name,
                ["type"] = RegType.Name(type),
                ["data"] = type is RegType.Sz or RegType.ExpandSz or RegType.MultiSz
                    ? Encoding.Unicode.GetString(data).TrimEnd('\0').Replace('\0', '|')
                    : Util.Hex(data),
            });
        }

        records.Add(new JsonObject { ["key"] = relative, ["values"] = list });
    }
}

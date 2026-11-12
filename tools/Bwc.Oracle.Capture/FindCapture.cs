using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Bwc.Oracle.Capture;

// `find`: seeds keys and values of every data type with the needle "ab" in names and data (case variants,
// whole-string matches, substrings), drives classic Find for each option set, then repeats Find Next (F3) and
// records every hit as (key path, value name). The dialog is filled by WM_SETTEXT and BM_SETCHECK, and the
// search is started with BM_CLICK on Find Next. Cancel is the only way the dialog is closed. Each run also
// records the FindFlags DWORD observed after the app closes, and the file derives the bit for each option
// from the runs where it was on and off. Find state is restored with the applet snapshot after the capture.
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

    private sealed record Options(
        string Name,
        bool Keys,
        bool Values,
        bool Data,
        bool Whole,
        bool Subtree);

    private static readonly Options[] Configs =
    {
        new("keys-values-data", Keys: true, Values: true, Data: true, Whole: false, Subtree: true),
        new("keys-values-data-whole", Keys: true, Values: true, Data: true, Whole: true, Subtree: true),
        new("keys-only", Keys: true, Values: false, Data: false, Whole: false, Subtree: true),
        new("values-only", Keys: false, Values: true, Data: false, Whole: false, Subtree: true),
        new("data-only", Keys: false, Values: false, Data: true, Whole: false, Subtree: true),
        new("values-data-whole", Keys: false, Values: true, Data: true, Whole: true, Subtree: true),
        new("keys-values-data-no-subtree", Keys: true, Values: true, Data: true, Whole: false, Subtree: false),
    };

    private static readonly string[] LayoutOptionNames =
    {
        "wholeString",
        "keys",
        "values",
        "data",
        "searchInSubtree",
    };

    // The control-to-bit table in the classic Find dialog procedure. This build does not update
    // the persisted DWORD, so the table is recorded as the layout oracle for the bits BWC writes.
    private static readonly (string Option, uint Mask)[] DialogMask =
    {
        ("wholeString", 0x00000001),
        ("keys", 0x00000002),
        ("values", 0x00000004),
        ("data", 0x00000008),
        ("searchInSubtree", 0x00000010),
    };

    internal static void Run(RepoPaths paths, bool allowDpi)
    {
        uint systemDpi = OracleSession.RequireDpi(allowDpi);
        var seedRecords = new JsonArray();
        Seed(seedRecords);

        var runs = new JsonArray();
        var observations = new List<Observation>();
        uint windowDpi = systemDpi;
        foreach (Options options in Configs)
        {
            Observation observation = RunOne(options);
            observations.Add(observation);
            runs.Add(observation.Run);
            windowDpi = observation.Dpi;
        }

        FindFlagsLayout layout = DeriveFindFlagsLayout(observations);
        foreach (Observation observation in observations)
        {
            AddFindFlagsLayout(observation.Run, layout, observation.FindFlags);
        }

        bool findFlagsWritten = observations.Any(observation => observation.FindFlags != 0);

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
            ["findFlagsWrittenByClassic"] = findFlagsWritten,
            ["findFlagsDialogMask"] = CreateDialogMaskJson(),
            ["findFlagsNote"] = findFlagsWritten
                ? "findFlagsLayout maps the bits observed across the captured option sets."
                : "Every captured run left FindFlags at 0x00000000. Classic saved LastKey and View on exit but did not update the Find options. findFlagsDialogMask records the dialog control table (ids 1151, 1152, 1153, 1154, 1161); BWC must write that layout itself.",
            ["note"] = "Registry value names are case-insensitive, so the seeded AB and ab in the Find key are one value; the later write (AB) holds the data AB. A hit reports the key by its tree path and the value name as shown in the list. The sequence stops when the selection leaves the Find tree.",
            ["seed"] = seedRecords,
            ["runs"] = runs,
        });
    }

    private static Observation RunOne(Options options)
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
        uint findFlags = ReadFindFlags();
        Console.WriteLine($"find: {options.Name} wrote FindFlags=0x{findFlags:X8}");
        return new Observation(
            options,
            new JsonObject
            {
                ["flags"] = new JsonObject
                {
                    ["keys"] = options.Keys,
                    ["values"] = options.Values,
                    ["data"] = options.Data,
                    ["wholeString"] = options.Whole,
                    ["searchInSubtree"] = options.Subtree,
                },
                ["name"] = options.Name,
                ["findFlags"] = findFlags,
                ["hits"] = hits,
                ["endedBy"] = endedBy,
            },
            findFlags,
            dpi);
    }

    // Reads the DWORD classic would write when the Find dialog's Find Next button closes it.
    private static uint ReadFindFlags()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            RegistryGuard.AppletSubPath,
            writable: false);

        if (key is null)
        {
            throw new InvalidOperationException(
                "The Regedit applet key is missing after the Find capture.");
        }

        RegistryNative.RawValue? raw = RegistryNative.QueryRaw(key.Handle, "FindFlags");
        if (raw is null)
        {
            throw new InvalidOperationException(
                "Classic regedit did not write the FindFlags value during the Find capture.");
        }

        if (raw.Value.Type != RegType.Dword || raw.Value.Data.Length != sizeof(uint))
        {
            throw new InvalidOperationException(
                $"The FindFlags value has type {raw.Value.Type} and {raw.Value.Data.Length} bytes, not a DWORD.");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(raw.Value.Data);
    }

    // Derives each option's bit by intersecting the DWORDs of runs where the option was on and
    // excluding every bit that appeared in a run where it was off. A bit that cannot be mapped
    // is reported as a candidate or as an unmapped bit instead of being guessed.
    private static FindFlagsLayout DeriveFindFlagsLayout(IReadOnlyList<Observation> observations)
    {
        var layout = new FindFlagsLayout();
        uint mappedMask = 0;

        foreach (string option in LayoutOptionNames)
        {
            uint trueBits = uint.MaxValue;
            uint falseBits = 0;
            bool anyTrue = false;

            foreach (Observation observation in observations)
            {
                if (GetOptionState(observation.Options, option))
                {
                    anyTrue = true;
                    trueBits &= observation.FindFlags;
                }
                else
                {
                    falseBits |= observation.FindFlags;
                }
            }

            uint candidates = anyTrue ? (trueBits & ~falseBits) : 0;
            layout.CandidateMasks[option] = candidates;

            if (IsSingleBit(candidates, out uint bit))
            {
                layout.MappedBits[option] = bit;
                mappedMask |= bit;
            }
        }

        uint observedMask = 0;
        foreach (Observation observation in observations)
        {
            observedMask |= observation.FindFlags;
        }

        layout.UnmappedMask = observedMask & ~mappedMask;
        return layout;
    }

    private static void AddFindFlagsLayout(JsonObject run, FindFlagsLayout layout, uint findFlags)
    {
        var runLayout = new JsonObject();

        foreach (string option in LayoutOptionNames)
        {
            var entry = new JsonObject();

            if (layout.MappedBits.TryGetValue(option, out uint bit))
            {
                entry["bit"] = FormatBit(bit);
                entry["set"] = (findFlags & bit) != 0;
            }
            else
            {
                var candidates = new JsonArray();
                uint candidateMask = layout.CandidateMasks[option];

                for (int index = 0; index < 32; index += 1)
                {
                    uint candidate = 1u << index;
                    if ((candidateMask & candidate) != 0)
                    {
                        candidates.Add(FormatBit(candidate));
                    }
                }

                entry["candidateBits"] = candidates;
            }

            runLayout[option] = entry;
        }

        var unmappedBits = new JsonArray();
        uint runUnmapped = findFlags & layout.UnmappedMask;

        for (int index = 0; index < 32; index += 1)
        {
            uint bit = 1u << index;
            if ((runUnmapped & bit) != 0)
            {
                unmappedBits.Add(FormatBit(bit));
            }
        }

        runLayout["unmappedBits"] = unmappedBits;
        run["findFlagsLayout"] = runLayout;
    }

    private static bool GetOptionState(Options options, string option) => option switch
    {
        "wholeString" => options.Whole,
        "keys" => options.Keys,
        "values" => options.Values,
        "data" => options.Data,
        "searchInSubtree" => options.Subtree,
        _ => throw new ArgumentOutOfRangeException(nameof(option)),
    };

    private static bool IsSingleBit(uint value, out uint bit)
    {
        if (value != 0 && (value & (value - 1)) == 0)
        {
            bit = value;
            return true;
        }

        bit = 0;
        return false;
    }

    private static JsonObject CreateDialogMaskJson()
    {
        var mask = new JsonObject();

        foreach ((string option, uint bit) in DialogMask)
        {
            mask[option] = FormatBit(bit);
        }

        return mask;
    }

    private static string FormatBit(uint bit) => $"0x{bit:X8}";

    private sealed record Observation(Options Options, JsonObject Run, uint FindFlags, uint Dpi);

    private sealed class FindFlagsLayout
    {
        internal Dictionary<string, uint> MappedBits { get; } = new(StringComparer.Ordinal);

        internal Dictionary<string, uint> CandidateMasks { get; } = new(StringComparer.Ordinal);

        internal uint UnmappedMask { get; set; }
    }

    private static void Fill(RegeditWindow w, IntPtr dlg, Options options)
    {
        IntPtr edit = GetControl(dlg, FindWhatId);
        Win32Ext.SendMessageText(edit, Wm.WM_SETTEXT, IntPtr.Zero, Needle);
        SetCheck(w, dlg, WholeId, options.Whole);
        SetCheck(w, dlg, KeysId, options.Keys);
        SetCheck(w, dlg, ValuesId, options.Values);
        SetCheck(w, dlg, DataId, options.Data);
        SetCheck(w, dlg, SubtreeId, options.Subtree);
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

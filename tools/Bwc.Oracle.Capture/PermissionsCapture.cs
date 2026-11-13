using System.Buffers.Binary;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Bwc.Oracle.Capture;

// `permissions`: seeds HKCU\Software\BWC-Oracle\Perm with one explicit allow ACE per inheritance flag set, each for a
// different well-known group. Opens classic Edit > Permissions, presses Advanced and reads the entry list (through the
// list's own LVM_GETITEMTEXTW and LVM_GETCOLUMNW in regedit's address space). Opens the entry editor of the 0x2 row and
// reads the Applies-to combo items (CB_GETLBTEXT). Every window is cancelled with IDCANCEL; OK is never posted.
// Tracked output: permissions-inherit.json. Display texts and window titles go to the untracked
// local/permissions-text.json.
internal static class PermissionsCapture
{
    private const string PermRelative = "Perm";
    private const string PermLastKey = @"Computer\HKEY_CURRENT_USER\Software\BWC-Oracle\Perm";
    private const string PermDisplayKey = @"HKEY_CURRENT_USER\Software\BWC-Oracle\Perm";
    private const uint PermissionsCommandId = 678;
    private const uint KeyReadMask = 0x00020019;
    private const uint InheritedAce = 0x10;
    private const uint AceFlagsBits = 0x1F;
    private const uint EditorAceFlags = 0x2;
    private const int MinimumApplyChoices = 3;
    private const uint CbGetCount = 0x0146;
    private const uint CbGetCurSel = 0x0147;
    private const uint CbGetLbText = 0x0148;
    private const uint CbGetItemData = 0x0150;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(25);

    // One seeded row per inheritance flag set (winnt.h ACE flags). Each group is a well-known SID, not on the key.
    private static readonly SeedRow[] Seeds =
    {
        new("S-1-5-32-545", 0x0),
        new("S-1-5-32-546", 0x1),
        new("S-1-5-32-547", 0x2),
        new("S-1-5-32-551", 0x3),
        new("S-1-5-32-555", 0x9),
        new("S-1-5-32-559", 0xA),
        new("S-1-5-32-558", 0xB),
    };

    private sealed record SeedRow(string Sid, int AceFlags);

    private sealed record Row(int Index, string Type, string Principal, string Access, string InheritedFrom, string AppliesTo);

    private sealed record Measured(SeedRow Seed, string Account, Row Row);

    private sealed record ComboChoice(string Text, uint AceFlags);

    internal static void Run(RepoPaths paths, bool allowDpi)
    {
        uint systemDpi = OracleSession.RequireDpi(allowDpi);
        string[] accounts = Seeds.Select(s => AccountName(s.Sid)).ToArray();
        Seed();

        RegistryGuard.SetAppletValue(string.Empty, "LastKey", RegType.Sz, SeedSpec.Sz(PermLastKey));
        using RegeditWindow w = RegeditWindow.Start();
        w.WaitForListPopulated(Wait);
        uint windowDpi = Win32.GetDpiForWindow(w.Main);

        IntPtr sheet = OpenPermissions(w);
        string sheetTitle = RequireKeyTitle(sheet, "the Permissions sheet");
        IntPtr advanced = OpenAdvanced(w, sheet);
        string advancedTitle = RequireKeyTitle(advanced, "the Advanced Security Settings window");

        IntPtr list = w.ChildOfClass(advanced, "SysListView32");
        if (list == IntPtr.Zero)
        {
            throw new InvalidOperationException("the Advanced window has no SysListView32 entry list.");
        }

        List<string> columns = ReadColumns(w, list);
        int rowCount = WaitForStableRows(w, list, Seeds.Length);
        List<Row> rows = ReadRows(w, list, rowCount, columns);
        List<Measured> measured = MatchSeeds(rows, accounts);

        AssertOnly(w, sheet, advanced);
        Measured editorRow = measured.Single(m => m.Seed.AceFlags == EditorAceFlags);
        SelectRow(w, list, editorRow.Row.Index);
        IntPtr editButton = FindEditButton(w, advanced);
        string editButtonText = RegeditWindow.Normalize(TextOf(editButton));
        IntPtr entry = OpenEntry(w, editButton, advanced, sheet);
        string entryTitle = TextOf(entry);
        (List<ComboChoice> choices, int selectedIndex) = ReadAppliesTo(w, entry);
        ComboChoice? selected = selectedIndex >= 0 && selectedIndex < choices.Count ? choices[selectedIndex] : null;

        w.CloseDialog(entry);
        AssertOnly(w, sheet, advanced);
        w.CloseDialog(advanced);
        AssertOnly(w, sheet);
        w.CloseDialog(sheet);
        AssertOnly(w);
        w.CloseGracefully();

        WriteOutputs(paths, systemDpi, windowDpi, allowDpi, columns, rows, measured, choices, selected, editorRow,
            new JsonObject
            {
                ["sheetTitle"] = sheetTitle,
                ["advancedTitle"] = advancedTitle,
                ["entryTitle"] = entryTitle,
                ["editButton"] = editButtonText,
            });
    }

    // ---- Seeding and the DACL read-back ----

    private static void Seed()
    {
        RegistryGuard.DeleteOracleTree();
        using (RegistryGuard.CreateOracleKey(string.Empty))
        {
        }

        using RegistryKey key = RegistryGuard.CreateOracleKey(PermRelative);
        string full = RegistryGuard.OracleRoot + @"\" + PermRelative;
        RegistryGuard.SetRawValue(key, full, "Marker", RegType.Sz, SeedSpec.Sz("permissions"));

        RegistrySecurity security = key.GetAccessControl(AccessControlSections.Access);
        foreach (SeedRow row in Seeds)
        {
            (InheritanceFlags inherit, PropagationFlags propagate) = RegistryFlags(row.AceFlags);
            security.AddAccessRule(new RegistryAccessRule(
                new SecurityIdentifier(row.Sid),
                RegistryRights.ReadKey,
                inherit,
                propagate,
                AccessControlType.Allow));
        }

        RegistryGuard.SetOracleKeySecurity(PermRelative, security);
        VerifyExplicitAces();
        Console.WriteLine($"permissions: seeded {Seeds.Length} explicit ACEs on {PermDisplayKey}");
    }

    // ACE flags in winnt.h: OBJECT_INHERIT 0x1, CONTAINER_INHERIT 0x2, INHERIT_ONLY 0x8. The managed enums use the same bits.
    private static (InheritanceFlags Inherit, PropagationFlags Propagate) RegistryFlags(int aceFlags)
    {
        InheritanceFlags inherit = InheritanceFlags.None;
        if ((aceFlags & 0x1) != 0)
        {
            inherit |= InheritanceFlags.ObjectInherit;
        }

        if ((aceFlags & 0x2) != 0)
        {
            inherit |= InheritanceFlags.ContainerInherit;
        }

        PropagationFlags propagate = (aceFlags & 0x8) != 0 ? PropagationFlags.InheritOnly : PropagationFlags.None;
        return (inherit, propagate);
    }

    // Reads the raw DACL bytes and checks that the explicit ACEs are exactly the seeded ones, with the seeded flags.
    private static void VerifyExplicitAces()
    {
        using RegistryKey key = Registry.CurrentUser.OpenSubKey(
                                    @"Software\BWC-Oracle\" + PermRelative,
                                    RegistryRights.ReadKey | RegistryRights.ReadPermissions)
            ?? throw new IOException("Could not open the seeded key to read its DACL.");
        byte[] binary = key.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm();
        var raw = new RawSecurityDescriptor(binary, 0);
        RawAcl dacl = raw.DiscretionaryAcl ?? throw new InvalidOperationException("the seeded key has no DACL.");
        List<CommonAce> explicitAces = dacl
            .OfType<CommonAce>()
            .Where(a => ((uint)a.AceFlags & InheritedAce) == 0)
            .ToList();

        if (explicitAces.Count != Seeds.Length)
        {
            throw new InvalidOperationException($"the seeded DACL has {explicitAces.Count} explicit ACEs, expected {Seeds.Length}.");
        }

        foreach (SeedRow row in Seeds)
        {
            CommonAce[] hits = explicitAces.Where(a => a.SecurityIdentifier.Value == row.Sid).ToArray();
            if (hits.Length != 1)
            {
                throw new InvalidOperationException($"{row.Sid} has {hits.Length} explicit ACEs, expected one.");
            }

            CommonAce ace = hits[0];
            int flags = (int)((uint)ace.AceFlags & AceFlagsBits);
            if (flags != row.AceFlags || ace.AceQualifier != AceQualifier.AccessAllowed || ace.AccessMask != KeyReadMask)
            {
                throw new InvalidOperationException(
                    $"{row.Sid} reads back as flags 0x{flags:X}, qualifier {ace.AceQualifier}, mask 0x{ace.AccessMask:X}; " +
                    $"expected flags 0x{row.AceFlags:X}, AccessAllowed, mask 0x{KeyReadMask:X}.");
            }
        }
    }

    // The name the desktop uses for the group, without the BUILTIN\ prefix. Failure to translate fails the run.
    private static string AccountName(string sid)
    {
        string full = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
        return full[(full.LastIndexOf('\\') + 1)..];
    }

    // ---- Opening the windows ----

    private static IntPtr OpenPermissions(RegeditWindow w)
    {
        (IntPtr menu, int pos) = w.Find("&Edit", "&Permissions...");
        if (w.IsGrayed(menu, pos))
        {
            throw new InvalidOperationException("Edit > Permissions is grayed for the seeded key.");
        }

        uint id = Win32.GetMenuItemID(menu, pos);
        if (id != PermissionsCommandId)
        {
            throw new InvalidOperationException($"Edit > Permissions has command id {id}, expected {PermissionsCommandId}.");
        }

        Win32.PostMessageW(w.Main, Wm.COMMAND, new IntPtr(id), IntPtr.Zero);
        return WaitForDialogOwnedBy(w, w.Main, new HashSet<IntPtr>(), "the Permissions sheet");
    }

    private static IntPtr OpenAdvanced(RegeditWindow w, IntPtr sheet)
    {
        List<Button> buttons = Buttons(w, sheet);
        List<Button> advanced = buttons.Where(b => b.Text == "Advanced").ToList();
        if (advanced.Count != 1 || !advanced[0].Enabled)
        {
            throw new InvalidOperationException($"expected one enabled Advanced button on the sheet; buttons: {Describe(buttons)}");
        }

        Win32.PostMessageW(advanced[0].Hwnd, Wm.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        return WaitForDialogOwnedBy(w, sheet, new HashSet<IntPtr>(), "the Advanced Security Settings window");
    }

    private static IntPtr OpenEntry(RegeditWindow w, IntPtr editButton, IntPtr advanced, IntPtr sheet)
    {
        var before = new HashSet<IntPtr>(w.TopLevel(RegeditWindow.DialogClass));
        Win32.PostMessageW(editButton, Wm.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        return RegeditWindow.WaitForNewWindow(
            () => w.TopLevel(RegeditWindow.DialogClass)
                .FirstOrDefault(h => !before.Contains(h) && (OwnerOf(h) == advanced || OwnerOf(h) == sheet)),
            Wait,
            "the Permission Entry window");
    }

    private static IntPtr WaitForDialogOwnedBy(RegeditWindow w, IntPtr owner, HashSet<IntPtr> known, string what)
    {
        return RegeditWindow.WaitForNewWindow(
            () => w.TopLevel(RegeditWindow.DialogClass).FirstOrDefault(h => h != owner && !known.Contains(h) && OwnerOf(h) == owner),
            Wait,
            what);
    }

    private static IntPtr FindEditButton(RegeditWindow w, IntPtr advanced)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            List<Button> buttons = Buttons(w, advanced);
            List<Button> edit = buttons.Where(b => b.Text.StartsWith("Edit", StringComparison.Ordinal) ||
                                                   b.Text.StartsWith("View", StringComparison.Ordinal)).ToList();
            if (edit.Count == 1 && edit[0].Enabled)
            {
                return edit[0].Hwnd;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException($"expected one enabled Edit or View button after selecting the row; buttons: {Describe(buttons)}");
            }

            Thread.Sleep(200);
        }
    }

    // ---- Reading the Advanced list ----

    private static List<string> ReadColumns(RegeditWindow w, IntPtr list)
    {
        IntPtr header = w.ChildOfClass(list, "SysHeader32");
        if (header == IntPtr.Zero)
        {
            throw new InvalidOperationException("the entry list has no SysHeader32 child.");
        }

        int count = w.Send(header, Wm.HDM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
        var names = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            names.Add(ColumnText(w, list, i));
        }

        return names;
    }

    private static string ColumnText(RegeditWindow w, IntPtr list, int column)
    {
        const int chars = 256;
        using var text = new RemoteBuffer(w.ProcessHandle, chars * 2);
        using var col = new RemoteBuffer(w.ProcessHandle, 48);
        byte[] s = new byte[48];
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(0, 4), Msg.LVCF_TEXT);
        BinaryPrimitives.WriteInt64LittleEndian(s.AsSpan(16, 8), text.Address.ToInt64());
        BinaryPrimitives.WriteInt32LittleEndian(s.AsSpan(24, 4), chars);
        col.Write(s);
        if (w.Send(list, Msg.LVM_GETCOLUMNW, new IntPtr(column), col.Address) == IntPtr.Zero)
        {
            throw new InvalidOperationException($"LVM_GETCOLUMNW failed for column {column}.");
        }

        return ReadWide(text.ReadAll());
    }

    private static int WaitForStableRows(RegeditWindow w, IntPtr list, int minimum)
    {
        var deadline = DateTime.UtcNow + Wait;
        int previous = -1;
        while (DateTime.UtcNow < deadline)
        {
            int count = w.Send(list, Msg.LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
            if (count >= minimum && count == previous)
            {
                return count;
            }

            previous = count;
            Thread.Sleep(800);
        }

        throw new TimeoutException($"the entry list did not reach {minimum} rows and hold still within {Wait.TotalSeconds}s.");
    }

    private static List<Row> ReadRows(RegeditWindow w, IntPtr list, int count, List<string> columns)
    {
        int type = ColumnIndex(columns, "Type");
        int principal = ColumnIndex(columns, "Principal");
        int access = ColumnIndex(columns, "Access");
        int inherited = ColumnIndex(columns, "Inherited from");
        int applies = ColumnIndex(columns, "Applies to");

        var rows = new List<Row>(count);
        for (int i = 0; i < count; i++)
        {
            rows.Add(new Row(
                i,
                CellText(w, list, i, type),
                CellText(w, list, i, principal),
                CellText(w, list, i, access),
                CellText(w, list, i, inherited),
                CellText(w, list, i, applies)));
        }

        return rows;
    }

    private static int ColumnIndex(List<string> columns, string name)
    {
        int index = columns.FindIndex(c => string.Equals(c, name, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new InvalidOperationException($"no column '{name}' in the entry list; headers: {string.Join(" | ", columns)}");
        }

        return index;
    }

    private static string CellText(RegeditWindow w, IntPtr list, int row, int subItem)
    {
        const int chars = 512;
        using var text = new RemoteBuffer(w.ProcessHandle, chars * 2);
        using var lvi = new RemoteBuffer(w.ProcessHandle, 64);
        byte[] s = new byte[64];
        BinaryPrimitives.WriteInt32LittleEndian(s.AsSpan(4, 4), row);
        BinaryPrimitives.WriteInt32LittleEndian(s.AsSpan(8, 4), subItem);
        BinaryPrimitives.WriteInt64LittleEndian(s.AsSpan(24, 8), text.Address.ToInt64());
        BinaryPrimitives.WriteInt32LittleEndian(s.AsSpan(32, 4), chars - 1);
        lvi.Write(s);
        w.Send(list, Wm.LVM_GETITEMTEXTW, new IntPtr(row), lvi.Address);
        return ReadWide(text.ReadAll());
    }

    // Matches each seeded group to exactly one row. The principal is the group name, optionally followed by
    // " (machine\name)".
    private static List<Measured> MatchSeeds(List<Row> rows, string[] accounts)
    {
        var result = new List<Measured>(Seeds.Length);
        for (int s = 0; s < Seeds.Length; s++)
        {
            string account = accounts[s];
            List<Row> hits = rows.Where(r =>
                    r.Principal.Equals(account, StringComparison.OrdinalIgnoreCase) ||
                    r.Principal.StartsWith(account + " (", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (hits.Count != 1)
            {
                throw new InvalidOperationException(
                    $"{Seeds[s].Sid} ({account}) matched {hits.Count} rows, expected one. Principals: {string.Join(" | ", rows.Select(r => r.Principal))}");
            }

            result.Add(new Measured(Seeds[s], account, hits[0]));
        }

        return result;
    }

    private static void SelectRow(RegeditWindow w, IntPtr list, int index)
    {
        using var buf = new RemoteBuffer(w.ProcessHandle, 96);
        byte[] clear = new byte[96];
        BinaryPrimitives.WriteUInt32LittleEndian(clear.AsSpan(0, 4), 0x8);
        BinaryPrimitives.WriteUInt32LittleEndian(clear.AsSpan(12, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(clear.AsSpan(16, 4), Wm.LVIS_SELECTED);
        buf.Write(clear);
        w.Send(list, Wm.LVM_SETITEMSTATE, new IntPtr(-1), buf.Address);

        byte[] pick = new byte[96];
        BinaryPrimitives.WriteUInt32LittleEndian(pick.AsSpan(0, 4), 0x8);
        BinaryPrimitives.WriteInt32LittleEndian(pick.AsSpan(4, 4), index);
        BinaryPrimitives.WriteUInt32LittleEndian(pick.AsSpan(12, 4), Wm.LVIS_SELECTED | Wm.LVIS_FOCUSED);
        BinaryPrimitives.WriteUInt32LittleEndian(pick.AsSpan(16, 4), Wm.LVIS_SELECTED | Wm.LVIS_FOCUSED);
        buf.Write(pick);
        w.Send(list, Wm.LVM_SETITEMSTATE, new IntPtr(index), buf.Address);
        w.Send(list, Wm.LVM_ENSUREVISIBLE, new IntPtr(index), IntPtr.Zero);
        Thread.Sleep(400);
    }

    // ---- The entry editor's Applies-to combo ----

    // The entry editor has a Type combo (Allow, Deny) and the Applies-to combo, which is the only combo whose items
    // carry inherit-type data.
    // Each item's data is a pointer to aclui's SI_INHERIT_TYPE entry in regedit (24 bytes): pguid at 0, dwFlags at 8,
    // pszName at 16. The flags are classic's own ACE flags for that choice. Read with ReadProcessMemory, not a message.
    private static (List<ComboChoice> Choices, int Selected) ReadAppliesTo(RegeditWindow w, IntPtr entry)
    {
        // Combos without inherit-type data (the Type combo with Allow and Deny) are skipped.
        var combos = w.ChildrenOf(entry)
            .Where(c => RegeditWindow.ClassOf(c) == "ComboBox")
            .Select(c => (Hwnd: c, Choices: ComboChoices(w, c)))
            .Where(c => c.Choices.Count > 0)
            .ToList();
        if (combos.Count != 1)
        {
            throw new InvalidOperationException($"expected one Applies-to combo in the entry editor, found {combos.Count}.");
        }

        if (combos[0].Choices.Count < MinimumApplyChoices)
        {
            throw new InvalidOperationException($"the Applies-to combo has {combos[0].Choices.Count} choices, expected at least {MinimumApplyChoices}.");
        }

        int selected = w.Send(combos[0].Hwnd, CbGetCurSel, IntPtr.Zero, IntPtr.Zero).ToInt32();
        return (combos[0].Choices, selected);
    }

    // Returns the inherit-type choices of a combo, or an empty list when the combo's items carry no inherit-type data.
    private static List<ComboChoice> ComboChoices(RegeditWindow w, IntPtr combo)
    {
        int count = w.Send(combo, CbGetCount, IntPtr.Zero, IntPtr.Zero).ToInt32();
        var choices = new List<ComboChoice>(Math.Max(count, 0));
        for (int i = 0; i < count; i++)
        {
            IntPtr data = w.Send(combo, CbGetItemData, new IntPtr(i), IntPtr.Zero);
            if (data.ToInt64() < 0x10000)
            {
                return new List<ComboChoice>();
            }

            // aclui's SI_INHERIT_TYPE in regedit: pguid at 0, dwFlags at 8, pszName at 16. The name is a string
            // resource id (for example 7090), not a pointer, so the text comes from the list of the combo itself.
            byte[] entry = RemoteBuffer.ReadRemote(w.ProcessHandle, data, 24);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(8, 4));
            long nameValue = BinaryPrimitives.ReadInt64LittleEndian(entry.AsSpan(16, 8));
            string text = ComboItemText(w, combo, i);
            Console.WriteLine($"permissions: combo item {i} flags 0x{flags:X} name-id {nameValue} text '{text}'");
            choices.Add(new ComboChoice(text, flags));
        }

        return choices;
    }

    // CB_GETLBTEXT copies the text into the caller's buffer through cross-process message marshalling, so the buffer is
    // local. A buffer in regedit's address space is not written (the copy-back lands in this process).
    private static string ComboItemText(RegeditWindow w, IntPtr combo, int index)
    {
        IntPtr local = Marshal.AllocHGlobal(1024);
        try
        {
            IntPtr length = w.Send(combo, CbGetLbText, new IntPtr(index), local);
            if (length.ToInt64() <= 0)
            {
                throw new InvalidOperationException($"CB_GETLBTEXT returned {length.ToInt64()} for item {index}.");
            }

            return Marshal.PtrToStringUni(local, Math.Min((int)length, 511)) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(local);
        }
    }


    // ---- Buttons and dialog checks ----

    private sealed record Button(IntPtr Hwnd, string Text, bool Enabled);

    private static List<Button> Buttons(RegeditWindow w, IntPtr dialog)
    {
        return w.ChildrenOf(dialog)
            .Where(c => RegeditWindow.ClassOf(c) == "Button" && Win32.IsWindowVisible(c))
            .Select(c => new Button(c, RegeditWindow.Normalize(TextOf(c)), Win32Ext.IsWindowEnabled(c)))
            .ToList();
    }

    private static string Describe(List<Button> buttons) =>
        string.Join(" | ", buttons.Select(b => $"'{b.Text}'{(b.Enabled ? string.Empty : " (disabled)")}"));

    // Fails when any visible dialog of the started process is not one of the windows this run opened.
    private static void AssertOnly(RegeditWindow w, params IntPtr[] allowed)
    {
        List<IntPtr> extra = w.TopLevel(RegeditWindow.DialogClass).Where(h => !allowed.Contains(h)).ToList();
        if (extra.Count > 0)
        {
            throw new InvalidOperationException(
                "unexpected window(s) open, not clicked: " +
                string.Join("; ", extra.Select(h => $"0x{h:X} '{TextOf(h)}'")));
        }
    }

    // The key name appears in the window title. A title without it means the dialog is not for the seeded key.
    private static string RequireKeyTitle(IntPtr dialog, string what)
    {
        string title = TextOf(dialog);
        if (!title.Contains(PermRelative, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{what} title does not name the seeded key: '{title}'.");
        }

        return title;
    }

    // ---- Output ----

    private static void WriteOutputs(
        RepoPaths paths,
        uint systemDpi,
        uint windowDpi,
        bool allowDpi,
        List<string> columns,
        List<Row> rows,
        List<Measured> measured,
        List<ComboChoice> choices,
        ComboChoice? selected,
        Measured editorRow,
        JsonObject windowTexts)
    {
        string currentUser = Environment.UserName;
        var trackedRows = new JsonArray();
        var localRows = new JsonArray();
        foreach (Row row in rows)
        {
            Measured? seeded = measured.FirstOrDefault(m => m.Row.Index == row.Index);
            trackedRows.Add(seeded is null ? OtherRowJson(row, currentUser) : SeededRowJson(seeded));
            localRows.Add(new JsonObject
            {
                ["principal"] = row.Principal,
                ["type"] = row.Type,
                ["access"] = row.Access,
                ["inheritedFrom"] = row.InheritedFrom,
                ["appliesTo"] = row.AppliesTo,
            });
        }

        var tracked = new JsonObject
        {
            ["schema"] = 1,
            ["generator"] = "Bwc.Oracle.Capture permissions",
            ["os"] = Report.OsInfo(),
            ["regedit"] = Report.RegeditInfo(),
            ["dpi"] = Report.DpiInfo(systemDpi, windowDpi, allowDpi),
            ["key"] = PermDisplayKey,
            ["columns"] = Strings(columns),
            ["seededRowsFound"] = measured.Count,
            ["rows"] = trackedRows,
            ["appliesToChoices"] = new JsonArray(choices.Select(c => (JsonNode)new JsonObject
            {
                ["text"] = c.Text,
                ["aceFlags"] = (int)c.AceFlags,
            }).ToArray()),
            ["appliesToForEditorRow"] = new JsonObject
            {
                ["sid"] = editorRow.Seed.Sid,
                ["aceFlags"] = editorRow.Seed.AceFlags,
                ["selectedChoice"] = selected is null ? null : selected.Text,
                ["selectedChoiceAceFlags"] = selected is null ? null : JsonValue.Create((int)selected.AceFlags),
            },
            ["note"] = "Rows for the seeded groups carry the seeded ACE flags and the group name. Other rows " +
                       "(inherited from the parent keys) carry no SID and their principal is a placeholder, " +
                       "so no account or machine name is tracked. Display texts are the Microsoft strings of classic regedit.",
        };

        Report.WriteJson(paths.PermissionsInheritJson, tracked);
        Report.WriteJson(Path.Combine(paths.LocalDir, "permissions-text.json"), new JsonObject
        {
            ["windows"] = windowTexts,
            ["columns"] = Strings(columns),
            ["rows"] = localRows,
        });
    }

    private static JsonObject SeededRowJson(Measured m) => new()
    {
        ["sid"] = m.Seed.Sid,
        ["aceFlags"] = m.Seed.AceFlags,
        ["principal"] = m.Account,
        ["type"] = m.Row.Type,
        ["access"] = m.Row.Access,
        ["inheritedFrom"] = m.Row.InheritedFrom,
        ["appliesTo"] = m.Row.AppliesTo,
    };

    private static JsonObject OtherRowJson(Row row, string currentUser) => new()
    {
        ["sid"] = null,
        ["aceFlags"] = null,
        ["principal"] = "(other principal)",
        ["isCurrentUser"] = row.Principal.Contains(currentUser, StringComparison.OrdinalIgnoreCase),
        ["type"] = row.Type,
        ["access"] = row.Access,
        ["inheritedFrom"] = row.InheritedFrom,
        ["appliesTo"] = row.AppliesTo,
    };

    private static JsonArray Strings(IEnumerable<string> values) =>
        new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());

    // ---- Small helpers ----

    private static IntPtr OwnerOf(IntPtr h) => Win32Ext.GetWindow(h, Wm.GW_OWNER);

    private static string TextOf(IntPtr h)
    {
        var sb = new StringBuilder(512);
        Win32.GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string ReadWide(byte[] utf16)
    {
        string s = Encoding.Unicode.GetString(utf16);
        int nul = s.IndexOf('\0');
        return nul >= 0 ? s[..nul] : s;
    }
}

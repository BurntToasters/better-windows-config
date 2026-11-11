using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Bwc.Oracle.Capture;

// `sort`: seeds value names with digit runs, case variants and symbols under BWC-Oracle\Sort, reads the
// order classic regedit lists them in (read through the list's own LVM_GETITEMTEXTW in regedit's address
// space), then clicks the Name column header with a real mouse message pair and reads the order again.
// The list style bits are recorded too, so a sort flag would show up in the data.
internal static class SortCapture
{
    private const string SortLastKey = @"Computer\HKEY_CURRENT_USER\Software\BWC-Oracle\Sort";
    private const string SortRelative = @"Sort";
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(25);

    // Names are inserted in this order, which is not the order they are expected to appear in.
    private static readonly string[] Names =
    {
        "a10", "a2", "A3", "_x", "b", "a1", "B2", "ab", "a-b", "a b", "1", "10", "2", "Zeta", "alpha", "Alpha",
        "ALPHA", "x.y", "x_y", "~t", "#h", "0z", "a01", "a1b", "(x)", "[y]", "z9", "z10",
    };

    internal static void Run(RepoPaths paths, bool allowDpi)
    {
        uint systemDpi = OracleSession.RequireDpi(allowDpi);
        Seed();

        RegistryGuard.SetAppletValue(string.Empty, "LastKey", RegType.Sz, SeedSpec.Sz(SortLastKey));
        using RegeditWindow w = RegeditWindow.Start();
        w.WaitForListPopulated(ReadyTimeout);

        List<string> before = w.ListNames();
        IntPtr list = w.ListView;
        long style = Win32Ext.GetWindowLong(list, Wm.GWL_STYLE) & 0xFFFFFFFFL;

        IntPtr header = w.ChildOfClass(list, "SysHeader32");

        ClickHeader(w, list, header, column: 0);
        Thread.Sleep(1000);
        List<string> afterName = w.ListNames();

        ClickHeader(w, list, header, column: 1);
        Thread.Sleep(1000);
        List<string> afterType = w.ListNames();

        bool changedByName = !before.SequenceEqual(afterName);
        bool changedByType = !afterName.SequenceEqual(afterType);
        string styleBits = $"0x{style:X8}";

        var record = new JsonObject
        {
            ["schema"] = 1,
            ["generator"] = "Bwc.Oracle.Capture sort",
            ["os"] = Report.OsInfo(),
            ["regedit"] = Report.RegeditInfo(),
            ["dpi"] = Report.DpiInfo(systemDpi, Win32.GetDpiForWindow(w.Main), allowDpi),
            ["insertionOrder"] = Strings(Names),
            ["classicOrder"] = Strings(before),
            ["listStyle"] = styleBits,
            ["listSortFlagsSet"] = (style & (0x0010 | 0x0020)) != 0,
            ["headerClick"] = new JsonObject
            {
                ["clickedColumn"] = 0,
                ["orderAfter"] = Strings(afterName),
                ["changed"] = changedByName,
            },
            ["secondHeaderClick"] = new JsonObject
            {
                ["clickedColumn"] = 1,
                ["orderAfter"] = Strings(afterType),
                ["changed"] = changedByType,
            },
            ["note"] = "Names are the seeded test data under HKCU\\Software\\BWC-Oracle\\Sort. The default value is the empty name and is listed first by classic regedit.",
        };

        w.CloseGracefully();
        Report.WriteJson(paths.SortOrderJson, record);
    }

    private static void ClickHeader(RegeditWindow w, IntPtr list, IntPtr header, int column)
    {
        if (header == IntPtr.Zero)
        {
            throw new InvalidOperationException("the list has no SysHeader32 child to click.");
        }

        NativeRect cell = HeaderCellRect(w, header, column);
        int x = (cell.Left + cell.Right) / 2;
        int y = (cell.Top + cell.Bottom) / 2;
        IntPtr lp = new((y << 16) | (x & 0xFFFF));
        Win32.PostMessageW(header, Wm.LBUTTONDOWN, new IntPtr(Wm.MK_LBUTTON), lp);
        Thread.Sleep(80);
        Win32.PostMessageW(header, Wm.LBUTTONUP, IntPtr.Zero, lp);
        Console.WriteLine($"sort: clicked header column {column} at header client {x},{y}");
    }

    // HDM_GETITEMRECT writes into a RECT in regedit's address space, so the buffer is remote.
    private static NativeRect HeaderCellRect(RegeditWindow w, IntPtr header, int column)
    {
        using var buf = new RemoteBuffer(w.ProcessHandle, 16);
        buf.Write(new byte[16]);
        w.Send(header, Wm.HDM_GETITEMRECT, new IntPtr(column), buf.Address);
        byte[] b = buf.ReadAll();
        return new NativeRect
        {
            Left = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(0, 4)),
            Top = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(4, 4)),
            Right = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(8, 4)),
            Bottom = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(12, 4)),
        };
    }

    private static JsonArray Strings(IEnumerable<string> values) =>
        new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());

    private static void Seed()
    {
        RegistryGuard.DeleteOracleTree();
        using (RegistryGuard.CreateOracleKey(string.Empty))
        {
        }

        using RegistryKey key = RegistryGuard.CreateOracleKey(SortRelative);
        string full = RegistryGuard.OracleRoot + @"\" + SortRelative;
        RegistryGuard.SetRawValue(key, full, string.Empty, RegType.Sz, SeedSpec.Sz("default"));
        for (int i = 0; i < Names.Length; i++)
        {
            RegistryGuard.SetRawValue(key, full, Names[i], RegType.Dword, SeedSpec.Dword((uint)i));
        }

        Console.WriteLine($"sort: seeded {Names.Length} values and the default value under {full}");
    }
}

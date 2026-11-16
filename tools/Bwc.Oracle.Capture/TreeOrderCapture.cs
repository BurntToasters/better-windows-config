using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Bwc.Oracle.Capture;

// `tree-order`: seeds eleven sibling keys with case variants, symbols and non-ASCII names under BWC-Oracle\TreeOrder,
// starts classic regedit with that key as LastKey, and reads the children of the selected key in the tree in the order
// classic lists them (TVM_GETNEXTITEM and TVM_GETITEMW, read in regedit's address space). Writes tree-order.json.
internal static class TreeOrderCapture
{
    private const string ParentRelative = "TreeOrder";
    private const string TreeLastKey = @"Computer\HKEY_CURRENT_USER\Software\BWC-Oracle\TreeOrder";
    private const uint TvmExpand = Msg.TV_FIRST + 2;
    private const int TveExpand = 2;
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(25);

    // Inserted in this order, which is not the order classic shows them in.
    private static readonly string[] Names = { "Z", "10", "a", "é", "2", "B", "ä", "_x", "1", "-x", "~x" };

    internal static void Run(RepoPaths paths, bool allowDpi)
    {
        uint systemDpi = OracleSession.RequireDpi(allowDpi);
        Seed();

        RegistryGuard.SetAppletValue(string.Empty, "LastKey", RegType.Sz, SeedSpec.Sz(TreeLastKey));
        using RegeditWindow w = RegeditWindow.Start();
        w.WaitForListPopulated(ReadyTimeout);

        IntPtr parent = w.TreeCaret();
        string selected = parent == IntPtr.Zero ? string.Empty : w.TreeText(parent);
        if (selected != ParentRelative)
        {
            throw new InvalidOperationException($"the tree selection is '{selected}', not '{ParentRelative}'.");
        }

        if (w.TreeChild(parent) == IntPtr.Zero)
        {
            _ = w.Send(w.Tree, TvmExpand, new IntPtr(TveExpand), parent);
        }

        List<string> order = ReadChildren(w, parent);
        var record = new JsonObject
        {
            ["schema"] = 1,
            ["generator"] = "Bwc.Oracle.Capture tree-order",
            ["os"] = Report.OsInfo(),
            ["regedit"] = Report.RegeditInfo(),
            ["dpi"] = Report.DpiInfo(systemDpi, Win32.GetDpiForWindow(w.Main), allowDpi),
            ["parent"] = ParentRelative,
            ["insertionOrder"] = Strings(Names),
            ["classicOrder"] = Strings(order),
            ["note"] = "Names are the seeded test data under HKCU\\Software\\BWC-Oracle\\TreeOrder. The order is the order " +
                "classic regedit lists the children of the selected key in its tree, not the registry enumeration order.",
        };

        w.CloseGracefully();
        Report.WriteJson(paths.TreeOrderJson, record);
        Console.WriteLine($"tree-order: classic lists {order.Count} children: {string.Join(", ", order)}");
    }

    private static List<string> ReadChildren(RegeditWindow w, IntPtr parent)
    {
        var names = new List<string>();
        for (IntPtr child = w.TreeChild(parent); child != IntPtr.Zero; child = w.TreeNext(child))
        {
            names.Add(w.TreeText(child));
        }

        return names;
    }

    private static JsonArray Strings(IEnumerable<string> values) =>
        new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());

    private static void Seed()
    {
        RegistryGuard.DeleteOracleTree();
        using (RegistryGuard.CreateOracleKey(string.Empty))
        {
        }

        using RegistryKey key = RegistryGuard.CreateOracleKey(ParentRelative);
        string full = RegistryGuard.OracleRoot + @"\" + ParentRelative;
        RegistryGuard.SetRawValue(key, full, string.Empty, RegType.Sz, SeedSpec.Sz("default"));
        foreach (string name in Names)
        {
            using RegistryKey child = RegistryGuard.CreateOracleKey(ParentRelative + @"\" + name);
        }

        Console.WriteLine($"tree-order: seeded {Names.Length} keys under {full}");
    }
}

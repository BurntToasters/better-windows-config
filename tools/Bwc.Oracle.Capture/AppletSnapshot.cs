using Microsoft.Win32;

namespace Bwc.Oracle.Capture;

// Captures the whole HKCU\...\Applets\Regedit key tree (value names, exact REG_* types and raw bytes,
// subkey names) so that classic regedit's own state (LastKey, FindFlags, View, Favorites, window
// placement) can be restored exactly after a capture. Restore runs in a finally block; Describe() is
// compared before and after so a mismatch fails the run instead of being silently accepted.
internal sealed class AppletSnapshot
{
    private readonly bool _rootExisted;
    private readonly Node _root;

    private AppletSnapshot(bool rootExisted, Node root)
    {
        _rootExisted = rootExisted;
        _root = root;
    }

    private sealed class Node
    {
        internal Dictionary<string, RegistryNative.RawValue> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal Dictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal static AppletSnapshot Capture()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryGuard.AppletSubPath, writable: false);
        if (key is null)
        {
            return new AppletSnapshot(rootExisted: false, new Node());
        }

        return new AppletSnapshot(rootExisted: true, ReadNode(key));
    }

    private static Node ReadNode(RegistryKey key)
    {
        var node = new Node();
        foreach (string name in key.GetValueNames())
        {
            RegistryNative.RawValue? raw = RegistryNative.QueryRaw(key.Handle, name);
            if (raw is not null)
            {
                node.Values[name] = raw.Value;
            }
        }

        foreach (string child in key.GetSubKeyNames())
        {
            using RegistryKey? sub = key.OpenSubKey(child, writable: false);
            if (sub is not null)
            {
                node.Children[child] = ReadNode(sub);
            }
        }

        return node;
    }

    // One line per key or value, sorted, with hex bytes. Two snapshots are equal when their descriptions are.
    internal string Describe()
    {
        if (!_rootExisted)
        {
            return "applet key absent";
        }

        var lines = new List<string>();
        Flatten(_root, string.Empty, lines);
        lines.Sort(StringComparer.Ordinal);
        return string.Join("\n", lines);
    }

    private static void Flatten(Node node, string path, List<string> lines)
    {
        lines.Add($"K {path}");
        foreach (var (name, value) in node.Values)
        {
            lines.Add($"V {path}|{name}|{value.Type}|{Util.Hex(value.Data)}");
        }

        foreach (var (name, child) in node.Children)
        {
            Flatten(child, path + @"\" + name, lines);
        }
    }

    // Short form used in the run log.
    internal string Summary()
    {
        if (!_rootExisted)
        {
            return "applet key absent";
        }

        int keys = 0;
        int values = 0;
        Count(_root, ref keys, ref values);
        string[] managed = { "LastKey", "View", "FindFlags" };
        var parts = managed.Select(name =>
        {
            if (_root.Values.TryGetValue(name, out RegistryNative.RawValue v))
            {
                return $"{name}=type {v.Type} {Util.Hex(v.Data)}";
            }

            return $"{name}=<absent>";
        });
        return $"applet key present with {keys} subkeys and {values} values; " + string.Join("; ", parts);
    }

    private static void Count(Node node, ref int keys, ref int values)
    {
        keys++;
        values += node.Values.Count;
        foreach (Node child in node.Children.Values)
        {
            Count(child, ref keys, ref values);
        }
    }

    internal void Restore()
    {
        if (!_rootExisted)
        {
            RegistryGuard.DeleteAppletKeyTree();
        }
        else
        {
            RestoreNode(string.Empty, _root);
        }

        AppletSnapshot after = Capture();
        if (after.Describe() != Describe())
        {
            throw new InvalidOperationException($"Applet restore mismatch after restore. Before: {Summary()}. After: {after.Summary()}.");
        }
    }

    private static void RestoreNode(string relative, Node expected)
    {
        using RegistryKey key = RegistryGuard.OpenOrCreateAppletKey(relative);

        foreach (string name in key.GetValueNames())
        {
            if (!expected.Values.ContainsKey(name))
            {
                RegistryGuard.DeleteAppletValue(relative, name);
            }
        }

        foreach (var (name, value) in expected.Values)
        {
            RegistryNative.RawValue? current = RegistryNative.QueryRaw(key.Handle, name);
            if (current is null || current.Value.Type != value.Type || !current.Value.Data.AsSpan().SequenceEqual(value.Data))
            {
                RegistryGuard.SetAppletValue(relative, name, value.Type, value.Data);
            }
        }

        foreach (string child in key.GetSubKeyNames())
        {
            if (!expected.Children.ContainsKey(child))
            {
                RegistryGuard.DeleteAppletSubtree(Join(relative, child));
            }
        }

        foreach (var (child, node) in expected.Children)
        {
            RestoreNode(Join(relative, child), node);
        }
    }

    private static string Join(string relative, string child) => relative.Length == 0 ? child : relative + @"\" + child;
}

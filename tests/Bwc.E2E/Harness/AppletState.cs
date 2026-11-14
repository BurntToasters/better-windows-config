using System.Text;
using Microsoft.Win32;

namespace Bwc.E2E.Harness;

/// <summary>
/// Snapshot of the whole HKCU\...\Applets\Regedit tree: subkeys, value names, exact REG_* kinds, and data read
/// without environment expansion. Classic regedit saves its own state there (LastKey, View, FindFlags, window
/// placement) when it exits. Capture before a classic run and Restore in a finally block, so the developer's
/// state is the same afterwards. The only writes go under this key.
/// </summary>
internal sealed class AppletState
{
    internal const string SubPath = @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit";

    private readonly bool _rootExisted;
    private readonly Node _root;

    private AppletState(bool rootExisted, Node root)
    {
        _rootExisted = rootExisted;
        _root = root;
    }

    private sealed class Node
    {
        internal Dictionary<string, (RegistryValueKind Kind, object Data)> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal Dictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Reads the whole tree. Read only.</summary>
    internal static AppletState Capture()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SubPath, writable: false);
        return key is null ? new AppletState(false, new Node()) : new AppletState(true, ReadNode(key));
    }

    /// <summary>True when the applet key existed when the snapshot was taken.</summary>
    internal bool KeyExisted => _rootExisted;

    /// <summary>The number of values in the snapshot, at the top of the key.</summary>
    internal int ValueCount => _root.Values.Count;

    /// <summary>One line per key and value, sorted. Two states are equal when their descriptions are.</summary>
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

    /// <summary>Puts the tree back as it was in the snapshot. Throws when the result still differs.</summary>
    internal void Restore()
    {
        if (!_rootExisted)
        {
            Registry.CurrentUser.DeleteSubKeyTree(SubPath, throwOnMissingSubKey: false);
        }
        else
        {
            RestoreNode(SubPath, _root);
        }

        AppletState after = Capture();
        if (after.Describe() != Describe())
        {
            throw new InvalidOperationException(
                $"Applet state differs after restore. Before: {Summary()}. After: {after.Summary()}.");
        }
    }

    /// <summary>A short form for the check text.</summary>
    internal string Summary()
    {
        if (!_rootExisted)
        {
            return "applet key absent";
        }

        int keys = 0;
        int values = 0;
        Count(_root, ref keys, ref values);
        return $"applet key present with {keys} key(s) and {values} value(s)";
    }

    private static Node ReadNode(RegistryKey key)
    {
        var node = new Node();
        foreach (string name in key.GetValueNames())
        {
            // Without DoNotExpandEnvironmentNames an REG_EXPAND_SZ would be read expanded and written back changed.
            object data = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) ?? Array.Empty<byte>();
            node.Values[name] = (key.GetValueKind(name), data);
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

    private static void Flatten(Node node, string path, List<string> lines)
    {
        lines.Add($"K {path}");
        foreach (var (name, value) in node.Values)
        {
            lines.Add($"V {path}|{name}|{value.Kind}|{Canonical(value.Data)}");
        }

        foreach (var (name, child) in node.Children)
        {
            Flatten(child, path + @"\" + name, lines);
        }
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

    // Hex of the exact bytes the value stores, so a string, a number, and a binary value compare by content.
    private static string Canonical(object data) => data switch
    {
        byte[] bytes => Convert.ToHexString(bytes),
        string text => Convert.ToHexString(Encoding.Unicode.GetBytes(text)),
        int number => Convert.ToHexString(BitConverter.GetBytes(number)),
        long number => Convert.ToHexString(BitConverter.GetBytes(number)),
        string[] strings => Convert.ToHexString(Encoding.Unicode.GetBytes(string.Join("\0", strings) + "\0\0")),
        _ => Convert.ToHexString(Encoding.Unicode.GetBytes(data.ToString() ?? string.Empty)),
    };

    private static void RestoreNode(string keyPath, Node expected)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
            ?? throw new InvalidOperationException($@"Could not open HKCU\{keyPath} for writing.");

        foreach (string name in key.GetValueNames())
        {
            if (!expected.Values.ContainsKey(name))
            {
                key.DeleteValue(name, throwOnMissingValue: false);
            }
        }

        foreach (var (name, value) in expected.Values)
        {
            if (!ValueMatches(key, name, value))
            {
                key.SetValue(name, value.Data, value.Kind);
            }
        }

        foreach (string child in key.GetSubKeyNames())
        {
            if (!expected.Children.ContainsKey(child))
            {
                key.DeleteSubKeyTree(child, throwOnMissingSubKey: false);
            }
        }

        foreach (var (child, node) in expected.Children)
        {
            RestoreNode(keyPath + @"\" + child, node);
        }
    }

    private static bool ValueMatches(RegistryKey key, string name, (RegistryValueKind Kind, object Data) expected)
    {
        if (!key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase) || key.GetValueKind(name) != expected.Kind)
        {
            return false;
        }

        object current = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) ?? Array.Empty<byte>();
        return Canonical(current) == Canonical(expected.Data);
    }
}

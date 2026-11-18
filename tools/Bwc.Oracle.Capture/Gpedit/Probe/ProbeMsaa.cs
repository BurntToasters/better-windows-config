using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Accessibility;

namespace Bwc.Oracle.Capture.Gpedit.Probe;

// Read-only MSAA (IAccessible) view of a window, to see whether names exist where UI Automation gives none.
internal static class ProbeMsaa
{
    private const uint ObjIdWindow = 0x00000000;
    private const uint ObjIdMenu = 0xFFFFFFFD;
    private const uint ObjIdClient = 0xFFFFFFFC;
    private const int MaxChildren = 120;
    private static Guid iidAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint objectId, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IAccessible? accessible);

    internal static JsonObject DescribeWindow(IntPtr hwnd) => Describe(hwnd, ObjIdClient, withChildren: true);

    internal static JsonObject DescribeMenuBar(IntPtr hwnd) => Describe(hwnd, ObjIdMenu, withChildren: true);

    internal static string? NameOf(IntPtr hwnd)
    {
        try
        {
            IAccessible? acc = Get(hwnd, ObjIdClient) ?? Get(hwnd, ObjIdWindow);
            return acc?.get_accName(0);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static IAccessible? Get(IntPtr hwnd, uint objectId)
    {
        int hr = AccessibleObjectFromWindow(hwnd, objectId, ref iidAccessible, out IAccessible? acc);
        return hr == 0 ? acc : null;
    }

    private static JsonObject Describe(IntPtr hwnd, uint objectId, bool withChildren)
    {
        var node = new JsonObject { ["hwnd"] = "0x" + hwnd.ToString("X"), ["object"] = objectId == ObjIdMenu ? "menu" : "client" };
        IAccessible? acc;
        try
        {
            acc = Get(hwnd, objectId);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            node["error"] = ex.Message;
            return node;
        }

        if (acc is null)
        {
            node["available"] = false;
            return node;
        }

        node["available"] = true;
        node["name"] = Safe(() => acc.get_accName(0));
        node["role"] = Safe(() => acc.get_accRole(0)?.ToString());
        node["value"] = Safe(() => acc.get_accValue(0));
        int count = 0;
        try
        {
            count = acc.accChildCount;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            node["childCountError"] = ex.Message;
        }

        node["childCount"] = count;
        if (withChildren && count > 0)
        {
            var children = new JsonArray();
            for (int i = 1; i <= Math.Min(count, MaxChildren); i++)
            {
                int id = i;
                children.Add(new JsonObject
                {
                    ["id"] = id,
                    ["name"] = Safe(() => acc.get_accName(id)),
                    ["role"] = Safe(() => acc.get_accRole(id)?.ToString()),
                    ["state"] = Safe(() => acc.get_accState(id)?.ToString()),
                    ["shortcut"] = Safe(() => acc.get_accKeyboardShortcut(id)),
                });
            }

            node["children"] = children;
        }

        return node;
    }

    private static JsonNode? Safe(Func<string?> read)
    {
        try
        {
            return JsonValue.Create(read());
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return JsonValue.Create("<error: " + ex.Message + ">");
        }
    }
}

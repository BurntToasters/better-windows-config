using System.Runtime.InteropServices;
using System.Text;

namespace Bwc.UI.Dialogs;

/// <summary>IOleWindow (IID 00000114-0000-0000-C000-000000000046). The file dialog object implements it, and GetWindow
/// returns the dialog window.</summary>
[ComImport]
[Guid("00000114-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleWindowAnnotation
{
    [PreserveSig]
    int GetWindow(out nint phwnd);

    [PreserveSig]
    int ContextSensitiveHelp(int fEnterMode);
}

/// <summary>
/// IAccPropServices from oleacc.h (IID 6E26E776-04F0-495D-80E4-3330352E3169). The vtable is declared in header order.
/// Only SetHwndPropStr and ClearHwndProps are called; the other slots keep their place and are never invoked.
/// </summary>
[ComImport]
[Guid("6e26e776-04f0-495d-80e4-3330352e3169")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAccPropServices
{
    [PreserveSig]
    int SetPropValueSlot();

    [PreserveSig]
    int SetPropServerSlot();

    [PreserveSig]
    int ClearPropsSlot();

    [PreserveSig]
    int SetHwndPropSlot();

    [PreserveSig]
    int SetHwndPropStr(nint hwnd, uint idObject, uint idChild, Guid idProp, [MarshalAs(UnmanagedType.LPWStr)] string str);

    [PreserveSig]
    int SetHwndPropServerSlot();

    [PreserveSig]
    int ClearHwndProps(nint hwnd, uint idObject, uint idChild, ref Guid props, int cProps);
}

/// <summary>
/// Gives the Export dialog's branch path edit an accessible name through Dynamic Annotation (oleacc). The edit is a
/// shell control, so the shell gives it no UI Automation name. The name is set on the edit's window with
/// IAccPropServices::SetHwndPropStr and PROPID_ACC_NAME, and cleared with ClearHwndProps when the dialog closes.
/// </summary>
internal static class ExportBranchAnnotation
{
    /// <summary>CLSID_AccPropServices (oleacc.h, B5F8350B-0548-48B1-A6EE-88BD00B4A5E7).</summary>
    private static readonly Guid AccPropServicesClsid = new("b5f8350b-0548-48b1-a6ee-88bd00b4a5e7");

    /// <summary>PROPID_ACC_NAME (oleacc.h, 608D3DF8-8128-4AA7-A428-F55E49267291).</summary>
    private static readonly Guid PropIdAccName = new("608d3df8-8128-4aa7-a428-f55e49267291");

    private const uint ObjIdClient = 0xFFFFFFFC;
    private const uint ChildIdSelf = 0;

    private delegate bool EnumChildProc(nint hwnd, nint lParam);

    /// <summary>
    /// The branch path edit. Its control id is the one passed to AddEditBox when the shell maps it to a window, and
    /// otherwise the lowest visible edit in the dialog, which is the branch path (the file name edit sits above it).
    /// </summary>
    internal static nint FindBranchEdit(nint dialog, uint controlId)
    {
        nint byId = GetDlgItem(dialog, (int)controlId);
        if (byId != 0 && IsVisibleEdit(byId))
        {
            return byId;
        }

        nint lowest = 0;
        int lowestTop = int.MinValue;
        _ = EnumChildWindows(dialog, (hwnd, _) =>
        {
            if (!IsVisibleEdit(hwnd) || !Win32Dialog.TryGetWindowRect(hwnd, out Win32Dialog.WindowRect rect))
            {
                return true;
            }

            if (rect.Top > lowestTop)
            {
                lowestTop = rect.Top;
                lowest = hwnd;
            }

            return true;
        }, 0);
        return lowest;
    }

    /// <summary>Sets the accessible name of a window. Returns false when the annotation service refuses it.</summary>
    internal static bool TrySetName(nint hwnd, string name)
    {
        IAccPropServices? services = CreateServices();
        if (services is null)
        {
            return false;
        }

        return services.SetHwndPropStr(hwnd, ObjIdClient, ChildIdSelf, PropIdAccName, name) >= 0;
    }

    /// <summary>Removes the name set by <see cref="TrySetName"/>. A window that is already gone is ignored.</summary>
    internal static void ClearName(nint hwnd)
    {
        IAccPropServices? services = CreateServices();
        if (services is null)
        {
            return;
        }

        Guid prop = PropIdAccName;
        _ = services.ClearHwndProps(hwnd, ObjIdClient, ChildIdSelf, ref prop, 1);
    }

    private static IAccPropServices? CreateServices()
    {
        Type? type = Type.GetTypeFromCLSID(AccPropServicesClsid);
        return type is null ? null : Activator.CreateInstance(type) as IAccPropServices;
    }

    private static bool IsVisibleEdit(nint hwnd)
    {
        var name = new StringBuilder(32);
        if (GetClassNameW(hwnd, name, name.Capacity) == 0 || name.ToString() != "Edit")
        {
            return false;
        }

        return IsWindowVisible(hwnd);
    }

#pragma warning disable SYSLIB1054 // The callback and the small user32 surface do not justify LibraryImport plumbing.
    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint parent, EnumChildProc callback, nint lParam);

    [DllImport("user32.dll", EntryPoint = "GetDlgItem")]
    private static extern nint GetDlgItem(nint parent, int id);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);
#pragma warning restore SYSLIB1054
}

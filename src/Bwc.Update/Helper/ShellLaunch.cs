using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Bwc.Update;

/// <summary>
/// Starts a program at the integrity level of the desktop shell from an elevated process: the documented "run unelevated
/// from an elevated process" pattern. The desktop shell window (IShellWindows.FindWindowSW) gives the shell browser, its
/// active view gives the desktop folder's automation object, and that object's Application is the shell's own
/// IShellDispatch2, whose ShellExecute starts the program as the (unelevated) shell does. Nothing here falls back to a
/// plain start: any failure returns false and starts nothing.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ShellLaunch
{
    private const int ShowNormal = 1;
    private const int SwcDesktop = 0x8;
    private const int SwfoNeedDispatch = 0x1;
    private const int SvgioBackground = 0;

    private static readonly Guid ClsidShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid SidSTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IidShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid IidDispatch = new("00020400-0000-0000-C000-000000000046");

    /// <summary>True when the shell was asked to start the program. The call runs on its own STA thread.</summary>
    public static bool TryStartUnelevated(string exePath, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var started = false;
        var thread = new Thread(() => started = StartOnThisThread(exePath, arguments, workingDirectory))
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread.Join(TimeSpan.FromSeconds(30)) && started;
    }

    private static bool StartOnThisThread(string exePath, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var releases = new List<object>();
        try
        {
            var windowsType = Type.GetTypeFromCLSID(ClsidShellWindows);
            if (windowsType is null || Activator.CreateInstance(windowsType) is not IShellWindows windows)
            {
                return false;
            }

            releases.Add(windows);
            object location = 0;
            object? root = null;
            var hr = windows.FindWindowSW(ref location, ref root, SwcDesktop, out _, SwfoNeedDispatch, out var desktopDispatch);
            if (hr != 0 || desktopDispatch is null)
            {
                return false;
            }

            releases.Add(desktopDispatch);
            if (desktopDispatch is not IShellServiceProvider provider)
            {
                return false;
            }

            var service = SidSTopLevelBrowser;
            var iid = IidShellBrowser;
            if (provider.QueryService(ref service, ref iid, out var browserPointer) != 0 || browserPointer == IntPtr.Zero)
            {
                return false;
            }

            var browser = TakeObject<IShellBrowser>(browserPointer, releases);
            if (browser is null || browser.QueryActiveShellView(out var view) != 0 || view is null)
            {
                return false;
            }

            releases.Add(view);
            var dispatchIid = IidDispatch;
            if (view.GetItemObject(SvgioBackground, ref dispatchIid, out var backgroundPointer) != 0 || backgroundPointer == IntPtr.Zero)
            {
                return false;
            }

            var folderView = Marshal.GetObjectForIUnknown(backgroundPointer);
            _ = Marshal.Release(backgroundPointer);
            releases.Add(folderView);

            var application = folderView.GetType().InvokeMember("Application", BindingFlags.GetProperty, null, folderView, null);
            if (application is null)
            {
                return false;
            }

            releases.Add(application);
            _ = application.GetType().InvokeMember(
                "ShellExecute",
                BindingFlags.InvokeMethod,
                null,
                application,
                [exePath, string.Join(' ', arguments), workingDirectory, "open", ShowNormal]);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TargetInvocationException
            or MissingMemberException or InvalidComObjectException or NotSupportedException or ArgumentException)
        {
            return false;
        }
        finally
        {
            foreach (var released in releases)
            {
                if (Marshal.IsComObject(released))
                {
                    _ = Marshal.ReleaseComObject(released);
                }
            }
        }
    }

    private static T? TakeObject<T>(IntPtr pointer, List<object> releases)
        where T : class
    {
        var obj = Marshal.GetObjectForIUnknown(pointer);
        _ = Marshal.Release(pointer);
        releases.Add(obj);
        return obj as T;
    }

#pragma warning disable SYSLIB1096 // The classic COM interop is used on purpose: these are the shell's own interfaces.
    [ComImport]
    [Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        // Slots before FindWindowSW: get_Count, Item, _NewEnum, Register, RegisterPending, Revoke, OnNavigate, OnActivated.
        [PreserveSig]
        int GetCount();

        [PreserveSig]
        int Item();

        [PreserveSig]
        int NewEnum();

        [PreserveSig]
        int Register();

        [PreserveSig]
        int RegisterPending();

        [PreserveSig]
        int Revoke();

        [PreserveSig]
        int OnNavigate();

        [PreserveSig]
        int OnActivated();

        [PreserveSig]
        int FindWindowSW(
            [In, MarshalAs(UnmanagedType.Struct)] ref object location,
            [In, MarshalAs(UnmanagedType.Struct)] ref object? locationRoot,
            int windowClass,
            out int windowHandle,
            int options,
            [MarshalAs(UnmanagedType.IDispatch)] out object? dispatch);
    }

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid service, ref Guid riid, out IntPtr result);
    }

    [ComImport]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        // IOleWindow (2 slots) and the 10 slots before QueryActiveShellView.
        [PreserveSig]
        int GetWindow();

        [PreserveSig]
        int ContextSensitiveHelp();

        [PreserveSig]
        int InsertMenusSB();

        [PreserveSig]
        int SetMenuSB();

        [PreserveSig]
        int RemoveMenusSB();

        [PreserveSig]
        int SetStatusTextSB();

        [PreserveSig]
        int EnableModelessSB();

        [PreserveSig]
        int TranslateAcceleratorSB();

        [PreserveSig]
        int BrowseObject();

        [PreserveSig]
        int GetViewStateStream();

        [PreserveSig]
        int GetControlWindow();

        [PreserveSig]
        int SendControlMsg();

        [PreserveSig]
        int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out IShellView? view);
    }

    [ComImport]
    [Guid("000214E3-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        // IOleWindow (2 slots) and the 10 slots before GetItemObject.
        [PreserveSig]
        int GetWindow();

        [PreserveSig]
        int ContextSensitiveHelp();

        [PreserveSig]
        int TranslateAccelerator();

        [PreserveSig]
        int EnableModeless();

        [PreserveSig]
        int UIActivate();

        [PreserveSig]
        int Refresh();

        [PreserveSig]
        int CreateViewWindow();

        [PreserveSig]
        int DestroyViewWindow();

        [PreserveSig]
        int GetCurrentInfo();

        [PreserveSig]
        int AddPropertySheetPages();

        [PreserveSig]
        int SaveViewState();

        [PreserveSig]
        int SelectItem();

        [PreserveSig]
        int GetItemObject(uint item, ref Guid riid, out IntPtr result);
    }
#pragma warning restore SYSLIB1096
}

using System.Runtime.InteropServices;

namespace Bwc.Update;

/// <summary>
/// The "run unelevated from an elevated process" chain, as typed vtable calls: IShellWindows.FindWindowSW (the desktop
/// shell window), IServiceProvider.QueryService(SID_STopLevelBrowser) for the IShellBrowser, QueryActiveShellView,
/// GetItemObject (SVGIO_BACKGROUND) for the desktop folder's automation object, IShellFolderViewDual.get_Application
/// for the shell's own IShellDispatch2, then ShellExecute. The shell starts the program at its own integrity level.
/// Any failure returns false and starts nothing: there is no plain start here (update-elevation.md HN9, HN10).
/// </summary>
internal static unsafe class ShellChain
{
    internal const int SwcDesktop = 0x8;
    internal const int SwfoNeedDispatch = 0x1;
    internal const uint SvgioBackground = 0;

    private const ushort VtEmpty = 0;
    private const ushort VtI4 = 3;
    private const ushort VtBstr = 8;
    private const int ShowNormal = 1;

    /// <summary> True when the shell was asked to start the program. <paramref name="shellWindows"/> is the caller's
    /// (it releases it); every other pointer acquired here is released before this returns.
    /// </summary>
    public static bool Run(ComObject shellWindows, string exePath, IReadOnlyList<string> arguments, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(shellWindows);
        ArgumentNullException.ThrowIfNull(arguments);

        // The location is the desktop (VT_I4 0) and the root is empty, as the late-bound call passed them.
        ComVariant location = default;
        location.Vt = VtI4;
        ComVariant root = default;
        root.Vt = VtEmpty;
        int windowHandle;
        nint desktopPointer = 0;
        nint windows = shellWindows.Pointer;
        if (windows == 0)
        {
            return false;
        }

        int hr = ((delegate* unmanaged<nint, ComVariant*, ComVariant*, int, int*, int, nint*, int>)ComObject.Slot(
            windows, ComSlots.ShellWindowsFindWindowSW))(windows, &location, &root, SwcDesktop, &windowHandle, SwfoNeedDispatch, &desktopPointer);
        using ComObject? desktop = ComObject.Own(desktopPointer);
        if (hr != 0 || desktop is null)
        {
            return false;
        }

        hr = desktop.QueryInterface(ComIds.ServiceProvider, out ComObject? providerObject);
        using ComObject? provider = providerObject;
        if (hr != 0 || provider is null)
        {
            return false;
        }

        Guid service = ComIds.TopLevelBrowserService;
        Guid browserIid = ComIds.ShellBrowser;
        nint browserPointer = 0;
        hr = ((delegate* unmanaged<nint, Guid*, Guid*, nint*, int>)ComObject.Slot(provider.Pointer, ComSlots.ServiceProviderQueryService))(
            provider.Pointer, &service, &browserIid, &browserPointer);
        using ComObject? browser = ComObject.Own(browserPointer);
        if (hr != 0 || browser is null)
        {
            return false;
        }

        nint viewPointer = 0;
        hr = ((delegate* unmanaged<nint, nint*, int>)ComObject.Slot(browser.Pointer, ComSlots.ShellBrowserQueryActiveShellView))(
            browser.Pointer, &viewPointer);
        using ComObject? view = ComObject.Own(viewPointer);
        if (hr != 0 || view is null)
        {
            return false;
        }

        Guid dispatchIid = ComIds.Dispatch;
        nint backgroundPointer = 0;
        hr = ((delegate* unmanaged<nint, uint, Guid*, nint*, int>)ComObject.Slot(view.Pointer, ComSlots.ShellViewGetItemObject))(
            view.Pointer, SvgioBackground, &dispatchIid, &backgroundPointer);
        using ComObject? background = ComObject.Own(backgroundPointer);
        if (hr != 0 || background is null)
        {
            return false;
        }

        hr = background.QueryInterface(ComIds.ShellFolderViewDual, out ComObject? folderViewObject);
        using ComObject? folderView = folderViewObject;
        if (hr != 0 || folderView is null)
        {
            return false;
        }

        nint applicationPointer = 0;
        hr = ((delegate* unmanaged<nint, nint*, int>)ComObject.Slot(folderView.Pointer, ComSlots.FolderViewDualGetApplication))(
            folderView.Pointer, &applicationPointer);
        using ComObject? application = ComObject.Own(applicationPointer);
        if (hr != 0 || application is null)
        {
            return false;
        }

        hr = application.QueryInterface(ComIds.ShellDispatch2, out ComObject? shellDispatchObject);
        using ComObject? shellDispatch = shellDispatchObject;
        if (hr != 0 || shellDispatch is null)
        {
            return false;
        }

        return ShellExecute(shellDispatch, exePath, string.Join(' ', arguments), workingDirectory);
    }

    // ShellExecute(BSTR File, VARIANT vArgs, VARIANT vDir, VARIANT vOperation, VARIANT vShow). A VARIANT passed by
    // value is passed by reference to a copy on 64-bit Windows (x64 and ARM64), so the function takes pointers.
    private static bool ShellExecute(ComObject shellDispatch, string exePath, string arguments, string workingDirectory)
    {
        nint file = 0;
        ComVariant args = default;
        ComVariant directory = default;
        ComVariant operation = default;
        ComVariant show = default;
        try
        {
            file = Marshal.StringToBSTR(exePath);
            args = Bstr(arguments);
            directory = Bstr(workingDirectory);
            operation = Bstr("open");
            show.Vt = VtI4;
            show.Value = ShowNormal;

            nint self = shellDispatch.Pointer;
            int hr = ((delegate* unmanaged<nint, nint, ComVariant*, ComVariant*, ComVariant*, ComVariant*, int>)ComObject.Slot(
                self, ComSlots.ShellDispatch2ShellExecute))(self, file, &args, &directory, &operation, &show);

            // The late-bound call threw on a failed HRESULT and nothing else, so a success code of any kind counts.
            return hr >= 0;
        }
        finally
        {
            Marshal.FreeBSTR(file);
            Marshal.FreeBSTR(args.Value);
            Marshal.FreeBSTR(directory.Value);
            Marshal.FreeBSTR(operation.Value);
        }
    }

    private static ComVariant Bstr(string text) => new() { Vt = VtBstr, Value = Marshal.StringToBSTR(text) };
}

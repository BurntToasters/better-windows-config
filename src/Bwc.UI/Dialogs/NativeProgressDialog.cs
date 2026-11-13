using System.ComponentModel;
using System.Runtime.InteropServices;
using Bwc.UI.Theme;
using Windows.UI;

namespace Bwc.UI.Dialogs;

/// <summary>
/// A modeless, owned Win32 progress dialog built from a <see cref="Win32DialogTemplate"/>. The owner is
/// disabled while the dialog runs. The dialog ignores close requests (WM_CLOSE and SC_CLOSE), so only
/// <see cref="Complete"/> destroys it. Progress is a msctls_progress32 control with the given id.
/// </summary>
/// <remarks>
/// Create and use this object on the UI thread: the palette is read on that thread, and the progress
/// messages are sent to the dialog thread. In dark mode the caption uses the immersive dark attribute,
/// and the client and static text use the BWC dark palette. Light and high contrast keep the system colors.
/// </remarks>
public sealed class NativeProgressDialog : IDisposable
{
    private const int WmInitDialog = 0x0110;
    private const int WmClose = 0x0010;
    private const int WmSysCommand = 0x0112;
    private const int WmCtlColorDlg = 0x0136;
    private const int WmCtlColorStatic = 0x0138;
    private const int WmEraseBkgnd = 0x0014;
    private const uint CcmSetBkColor = 0x2001;
    private static readonly nint ClrDefault = unchecked((nint)0xFF000000u);
    private const nuint ScClose = 0xF060;
    private const nuint ScMask = 0xFFF0;
    private const uint PbmSetPos = 0x0402;
    private const uint PbmSetRange32 = 0x0406;
    private const int DwlpMsgResult = 0;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const string DarkExplorerTheme = "DarkMode_Explorer";

    private readonly nint _owner;
    private readonly int _progressId;
    private readonly DialogProcedure _procedure;
    private nint _handle;
    private bool _ownerDisabled;
    private bool _dark;
    private bool _darkBarTheme;
    private uint _darkText;
    private uint _darkFace;
    private nint _darkFaceBrush;

    private NativeProgressDialog(nint owner, int progressId)
    {
        _owner = owner;
        _progressId = progressId;
        _procedure = DialogProc;
    }

    /// <summary>
    /// Creates the dialog from the template, disables the owner, and shows the dialog. Throws when the
    /// window cannot be created; the owner is not disabled in that case.
    /// </summary>
    public static NativeProgressDialog Show(nint ownerHwnd, Win32DialogTemplate template, int progressBarId)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (ownerHwnd == 0)
        {
            throw new ArgumentException("A progress dialog needs an owner HWND.", nameof(ownerHwnd));
        }

        var dialog = new NativeProgressDialog(ownerHwnd, progressBarId);
        try
        {
            dialog.Create(template);
        }
        catch
        {
            dialog.Dispose();
            throw;
        }

        return dialog;
    }

    /// <summary>
    /// Sets the range and the position of the progress bar. The bar is sent the messages on the dialog's
    /// thread; a call from another thread is posted instead. Does nothing after <see cref="Complete"/>.
    /// </summary>
    public void SetProgress(int completed, int total)
    {
        nint dialog = _handle;
        if (dialog == 0)
        {
            return;
        }

        nint bar = NativeDialogApi.GetDlgItem(dialog, _progressId);
        if (bar == 0)
        {
            return;
        }

        int max = Math.Max(1, total);
        int value = Math.Clamp(completed, 0, max);
        SendOnDialogThread(bar, PbmSetRange32, 0, max);
        SendOnDialogThread(bar, PbmSetPos, (nuint)value, 0);
    }

    /// <summary>
    /// Re-enables and activates the owner, then destroys the dialog. Runs once; later calls do nothing.
    /// The owner is active before the dialog goes, so the system does not pick another window.
    /// </summary>
    public void Complete()
    {
        nint dialog = _handle;
        if (dialog == 0)
        {
            return;
        }

        _handle = 0;
        BwcTheme.Changed -= OnThemeChanged;
        RestoreOwner();
        _ = NativeDialogApi.DestroyWindow(dialog);
        FreeDarkBrush();
    }

    /// <summary>Ends the dialog if it is still open.</summary>
    public void Dispose() => Complete();

    private void Create(Win32DialogTemplate template)
    {
        byte[] bytes = template.ToBytes();
        nint procedure = Marshal.GetFunctionPointerForDelegate(_procedure);
        nint dialog;
        unsafe
        {
            fixed (byte* pointer = bytes)
            {
                dialog = NativeDialogApi.CreateDialogIndirectParam(0, pointer, _owner, procedure, 0);
            }
        }

        if (dialog == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateDialogIndirectParamW failed.");
        }

        _handle = dialog;
        BwcTheme.Changed += OnThemeChanged;
        _ = Win32Dialog.EnableWindow(_owner, 0);
        _ownerDisabled = true;
        _ = Win32Dialog.SetActiveWindow(dialog);
    }

    /// <summary>
    /// Dialog procedure. It answers only the messages the classic dialog needs. The rest use the default.
    /// </summary>
    private nint DialogProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case WmInitDialog:
                // The template is visible, but the window is not shown until this returns, so the caption
                // is dark before the first paint.
                ApplyTheme(hwnd);
                return 1;

            case WmCtlColorDlg:
            case WmCtlColorStatic:
                return PaintDark(hwnd, wParam);

            case WmEraseBkgnd:
                return EraseDark(hwnd, wParam);

            case WmClose:
                // Only Complete destroys the window. The default would destroy it on a close request.
                return 1;

            case WmSysCommand:
                return (wParam & ScMask) == ScClose ? 1 : 0;

            default:
                return 0;
        }
    }

    /// <summary>
    /// Fills the client with the dark face. The default erase paints the system face, so the dark brush is applied here.
    /// </summary>
    private nint EraseDark(nint hwnd, nuint hdc)
    {
        if (!_dark || _darkFaceBrush == 0 || NativeDialogApi.GetClientRect(hwnd, out Win32Dialog.WindowRect rect) == 0)
        {
            return 0;
        }

        _ = NativeDialogApi.FillRect((nint)hdc, ref rect, _darkFaceBrush);
        return 1;
    }

    private nint PaintDark(nint hwnd, nuint hdc)
    {
        if (!_dark || _darkFaceBrush == 0)
        {
            return 0;
        }

        _ = NativeDialogApi.SetTextColor((nint)hdc, _darkText);
        _ = NativeDialogApi.SetBkColor((nint)hdc, _darkFace);
        _ = Win32Dialog.SetWindowLongPtr(hwnd, DwlpMsgResult, _darkFaceBrush);
        return _darkFaceBrush;
    }

    private void ApplyTheme(nint hwnd)
    {
        BwcPalette palette = BwcTheme.Current;
        bool dark = palette.Kind == BwcThemeKind.Dark;
        int useDark = dark ? 1 : 0;
        unsafe
        {
            _ = NativeDialogApi.DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, &useDark, sizeof(int));
        }

        nint bar = NativeDialogApi.GetDlgItem(hwnd, _progressId);
        if (dark)
        {
            uint face = ColorRef(palette.Face);
            if (_darkFaceBrush == 0 || face != _darkFace)
            {
                FreeDarkBrush();
                _darkFaceBrush = NativeDialogApi.CreateSolidBrush(face);
                _darkFace = face;
            }

            _darkText = ColorRef(palette.WindowText);
            _dark = true;
        }
        else
        {
            FreeDarkBrush();
            _dark = false;
        }

        // The dark bar theme is a visual style. When it cannot be applied, the classic bar stays.
        if (bar != 0 && _darkBarTheme != dark)
        {
            _ = NativeDialogApi.SetWindowTheme(bar, dark ? DarkExplorerTheme : null, null);
            // CCM_SETBKCOLOR paints the bar track. CLR_DEFAULT restores the system track in light mode.
            _ = NativeDialogApi.SendMessageW(bar, CcmSetBkColor, 0, dark ? (nint)_darkFace : ClrDefault);
            _darkBarTheme = dark;
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        nint dialog = _handle;
        if (dialog == 0)
        {
            return;
        }

        ApplyTheme(dialog);
        _ = NativeDialogApi.InvalidateRect(dialog, 0, 1);
    }

    private void FreeDarkBrush()
    {
        if (_darkFaceBrush != 0)
        {
            _ = NativeDialogApi.DeleteObject(_darkFaceBrush);
            _darkFaceBrush = 0;
        }
    }

    private void RestoreOwner()
    {
        if (!_ownerDisabled)
        {
            return;
        }

        _ownerDisabled = false;
        try
        {
            _ = Win32Dialog.EnableWindow(_owner, 1);
            _ = Win32Dialog.SetActiveWindow(_owner);
        }
        catch (Exception)
        {
            // The owner handle may already be gone. The dialog is closing either way.
        }
    }

    private static void SendOnDialogThread(nint target, uint message, nuint wParam, nint lParam)
    {
        uint targetThread = NativeDialogApi.GetWindowThreadProcessId(target, out _);
        if (targetThread == NativeDialogApi.GetCurrentThreadId())
        {
            _ = NativeDialogApi.SendMessageW(target, message, wParam, lParam);
        }
        else
        {
            _ = NativeDialogApi.PostMessageW(target, message, wParam, lParam);
        }
    }

    private static uint ColorRef(Color color) => (uint)(color.R | (color.G << 8) | (color.B << 16));

    private delegate nint DialogProcedure(nint hwnd, uint message, nuint wParam, nint lParam);
}

/// <summary>The user32, gdi32, dwmapi, and uxtheme entry points the native progress dialog uses.</summary>
internal static unsafe partial class NativeDialogApi
{
    [LibraryImport("user32.dll", EntryPoint = "CreateDialogIndirectParamW", SetLastError = true)]
    internal static partial nint CreateDialogIndirectParam(
        nint hInstance,
        byte* template,
        nint hWndParent,
        nint dialogProc,
        nint initParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int DestroyWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    internal static partial nint GetDlgItem(nint hDlg, int id);

    [LibraryImport("user32.dll")]
    internal static partial nint SendMessageW(nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int PostMessageW(nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    internal static partial int InvalidateRect(nint hWnd, nint rect, int erase);

    [LibraryImport("gdi32.dll")]
    internal static partial uint SetTextColor(nint hdc, uint color);

    [LibraryImport("gdi32.dll")]
    internal static partial uint SetBkColor(nint hdc, uint color);

    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateSolidBrush(uint color);

    [LibraryImport("gdi32.dll")]
    internal static partial int DeleteObject(nint obj);

    [LibraryImport("user32.dll")]
    internal static partial int GetClientRect(nint hwnd, out Win32Dialog.WindowRect rect);

    [LibraryImport("user32.dll")]
    internal static partial int FillRect(nint hdc, ref Win32Dialog.WindowRect rect, nint brush);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(nint hwnd, uint attribute, int* value, uint size);

    [LibraryImport("uxtheme.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SetWindowTheme(nint hwnd, string? subAppName, string? subIdList);
}

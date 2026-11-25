using System.Diagnostics;
using System.Runtime.InteropServices;
using Bwc.UI.Interop;

namespace Bwc.UI.Dialogs;

/// <summary>
/// Keeps a resizable modal dialog from being dragged below its minimum size. WinUI has no minimum size on the
/// presenter, so a window subclass answers WM_GETMINMAXINFO. The minimum is the client size from the dialog plus
/// the non-client frame read from the window at that moment, so it is right at every DPI the window moves to.
/// </summary>
internal sealed unsafe class DialogMinimumSize : IDisposable
{
    private const nuint SubclassId = 0x42574D53; // 'BWMS'
    private const uint WM_GETMINMAXINFO = 0x0024;

    private readonly nint _hwnd;
    private readonly Func<uint, (int Width, int Height)> _minimumClient;
    private GCHandle _self;
    private bool _installed;

    internal DialogMinimumSize(nint hwnd, Func<uint, (int Width, int Height)> minimumClient)
    {
        _hwnd = hwnd;
        _minimumClient = minimumClient;
    }

    /// <summary>Installs the subclass. False when the system refused it, in which case no minimum is enforced.</summary>
    internal bool Install()
    {
        if (_installed)
        {
            return true;
        }

        _self = GCHandle.Alloc(this);
        if (Win32.SetWindowSubclass(_hwnd, &SubclassProc, SubclassId, (nuint)GCHandle.ToIntPtr(_self)) == 0)
        {
            _self.Free();
            return false;
        }

        _installed = true;
        return true;
    }

    /// <summary>Removes the subclass and frees the handle. Safe to call more than once and after the window is gone.</summary>
    public void Dispose()
    {
        if (!_installed)
        {
            return;
        }

        _installed = false;
        // A window that is already destroyed refuses the removal, and no message can reach it any more.
        _ = Win32.RemoveWindowSubclass(_hwnd, &SubclassProc, SubclassId);
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    [UnmanagedCallersOnly]
    private static nint SubclassProc(nint hWnd, uint msg, nuint wParam, nint lParam, nuint id, nuint refData)
    {
        nint result = Win32.DefSubclassProc(hWnd, msg, wParam, lParam);
        if (msg != WM_GETMINMAXINFO)
        {
            return result;
        }

        try
        {
            if (GCHandle.FromIntPtr((nint)refData).Target is DialogMinimumSize self && self._installed)
            {
                self.Apply(hWnd, (MinMaxInfo*)lParam);
            }
        }
        catch (Exception ex)
        {
            // Exceptions must never unwind into native code. The system default minimum stays.
            Trace.WriteLine("DialogMinimumSize: " + ex);
        }

        return result;
    }

    private void Apply(nint hwnd, MinMaxInfo* info)
    {
        if (!Win32Dialog.TryGetWindowRect(hwnd, out Win32Dialog.WindowRect window) ||
            !Win32Dialog.TryGetClientRect(hwnd, out Win32Dialog.WindowRect client))
        {
            return;
        }

        uint dpi = Win32Dialog.GetDpiForWindow(hwnd);
        (int minWidth, int minHeight) = _minimumClient(dpi);
        int frameWidth = window.Width - client.Width;
        int frameHeight = window.Height - client.Height;
        info->MinTrackWidth = Math.Max(info->MinTrackWidth, minWidth + frameWidth);
        info->MinTrackHeight = Math.Max(info->MinTrackHeight, minHeight + frameHeight);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public int ReservedX;
        public int ReservedY;
        public int MaxSizeX;
        public int MaxSizeY;
        public int MaxPositionX;
        public int MaxPositionY;
        public int MinTrackWidth;
        public int MinTrackHeight;
        public int MaxTrackWidth;
        public int MaxTrackHeight;
    }
}

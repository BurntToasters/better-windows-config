using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;

namespace Bwc.Spike.Interop;

/// <summary>
/// Common item dialogs via COM IFileOpenDialog / IFileSaveDialog, shown modally on the WinUI window's HWND.
/// The WinRT FileOpenPicker is intentionally not used (unreliable when elevated).
/// Filters are passed as COMDLG_FILTERSPEC pairs, which is the COM form of the Win32 "Name|Pattern" string.
/// </summary>
internal static unsafe class CommonFileDialogs
{
    private static readonly (string Name, string Spec)[] Filters =
    {
        ("Registration Files (*.reg)", "*.reg"),   // "Registration Files (*.reg)|*.reg"
        ("All Files", "*.*"),                       // "All Files|*.*"
    };

    /// <summary>Returns the chosen path, or null when the user cancelled. Throws on any other failure.</summary>
    public static string? ShowOpen(nint ownerHwnd)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialog();
        return ShowCore(dialog, ownerHwnd);
    }

    public static string? ShowSave(nint ownerHwnd)
    {
        var dialog = (IFileSaveDialog)new FileSaveDialog();
        return ShowCore(dialog, ownerHwnd);
    }

    private static string? ShowCore(IFileDialog dialog, nint ownerHwnd)
    {
        var names = new nint[Filters.Length];
        var specs = new nint[Filters.Length];
        try
        {
            for (var i = 0; i < Filters.Length; i++)
            {
                names[i] = Marshal.StringToCoTaskMemUni(Filters[i].Name);
                specs[i] = Marshal.StringToCoTaskMemUni(Filters[i].Spec);
            }
            var filterSpecs = stackalloc COMDLG_FILTERSPEC[Filters.Length];
            for (var i = 0; i < Filters.Length; i++)
            {
                filterSpecs[i] = new COMDLG_FILTERSPEC { pszName = new PCWSTR((char*)names[i]), pszSpec = new PCWSTR((char*)specs[i]) };
            }
            dialog.SetFileTypes((uint)Filters.Length, filterSpecs);
            dialog.SetFileTypeIndex(1);

            try
            {
                dialog.Show(new HWND(ownerHwnd));
            }
            catch (COMException ex) when (ex.HResult == Win32.HRESULT_CANCELLED)
            {
                return null;
            }

            dialog.GetResult(out var item);
            item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var pathPtr);
            try
            {
                return pathPtr.ToString();
            }
            finally
            {
                PInvoke.CoTaskMemFree(pathPtr.Value);
            }
        }
        finally
        {
            for (var i = 0; i < Filters.Length; i++)
            {
                Marshal.FreeCoTaskMem(names[i]);
                Marshal.FreeCoTaskMem(specs[i]);
            }
        }
    }
}

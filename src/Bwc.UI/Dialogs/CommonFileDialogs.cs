using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;

namespace Bwc.UI.Dialogs;

/// <summary>
/// Common item dialogs through COM IFileOpenDialog and IFileSaveDialog. The WinRT
/// FileOpenPicker and FileSavePicker are not used because they do not reliably accept a plain
/// owner HWND in an unpackaged process. The dialog disables the owner for its lifetime, so the
/// caller does not need its own modal gate.
/// </summary>
public static unsafe class CommonFileDialogs
{
    // HRESULT_FROM_WIN32(ERROR_CANCELLED). IFileDialog.Show reports cancel through this HRESULT.
    private const int HResultCancelled = unchecked((int)0x800704C7);

    private static readonly (string Name, string Spec)[] Filters =
    {
        ("Registration Files (*.reg)", "*.reg"),
        ("All Files", "*.*"),
    };

    /// <summary>
    /// Shows the open dialog owned by <paramref name="ownerHwnd"/>. Returns the chosen file system
    /// path, or null when the user cancelled.
    /// </summary>
    public static string? ShowOpen(nint ownerHwnd, string? defaultFileName = null)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialog();
        return ShowCore(dialog, ownerHwnd, defaultFileName, save: false);
    }

    /// <summary>
    /// Shows the save dialog owned by <paramref name="ownerHwnd"/>. Returns the approved file
    /// system path, or null when the user cancelled.
    /// </summary>
    public static string? ShowSave(nint ownerHwnd, string? defaultFileName = null)
    {
        var dialog = (IFileSaveDialog)new FileSaveDialog();
        return ShowCore(dialog, ownerHwnd, defaultFileName, save: true);
    }

    private static string? ShowCore(
        IFileDialog dialog,
        nint ownerHwnd,
        string? defaultFileName,
        bool save)
    {
        var names = new nint[Filters.Length];
        var specs = new nint[Filters.Length];

        try
        {
            for (var index = 0; index < Filters.Length; index++)
            {
                names[index] = Marshal.StringToCoTaskMemUni(Filters[index].Name);
                specs[index] = Marshal.StringToCoTaskMemUni(Filters[index].Spec);
            }

            var filterSpecs = stackalloc COMDLG_FILTERSPEC[Filters.Length];
            for (var index = 0; index < Filters.Length; index++)
            {
                filterSpecs[index] = new COMDLG_FILTERSPEC
                {
                    pszName = new PCWSTR((char*)names[index]),
                    pszSpec = new PCWSTR((char*)specs[index]),
                };
            }

            dialog.SetFileTypes((uint)Filters.Length, filterSpecs);
            dialog.SetFileTypeIndex(1);

            FILEOPENDIALOGOPTIONS options =
                FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM |
                FILEOPENDIALOGOPTIONS.FOS_PATHMUSTEXIST |
                (save
                    ? FILEOPENDIALOGOPTIONS.FOS_OVERWRITEPROMPT
                    : FILEOPENDIALOGOPTIONS.FOS_FILEMUSTEXIST);
            dialog.SetOptions(options);

            if (!string.IsNullOrWhiteSpace(defaultFileName))
            {
                fixed (char* fileName = defaultFileName)
                {
                    dialog.SetFileName(fileName);
                }
            }

            if (save)
            {
                fixed (char* extension = "reg")
                {
                    dialog.SetDefaultExtension(extension);
                }
            }

            try
            {
                dialog.Show(new HWND(ownerHwnd));
            }
            catch (COMException ex) when (ex.HResult == HResultCancelled)
            {
                return null;
            }

            dialog.GetResult(out IShellItem item);
            item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out PWSTR pathPointer);
            try
            {
                return pathPointer.ToString();
            }
            finally
            {
                PInvoke.CoTaskMemFree(pathPointer.Value);
            }
        }
        finally
        {
            for (var index = 0; index < Filters.Length; index++)
            {
                Marshal.FreeCoTaskMem(names[index]);
                Marshal.FreeCoTaskMem(specs[index]);
            }
        }
    }
}

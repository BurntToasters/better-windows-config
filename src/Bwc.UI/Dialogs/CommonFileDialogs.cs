using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;

namespace Bwc.UI.Dialogs;

/// <summary>The writer selected by the Export save dialog filter.</summary>
public enum ExportFileFormat
{
    /// <summary>Windows Registry Editor Version 5.00, UTF-16LE with a BOM.</summary>
    RegistrationV5,

    /// <summary>Registry text export, UTF-16LE with a BOM.</summary>
    Text,

    /// <summary>REGEDIT4, system ANSI code page.</summary>
    RegistrationRegedit4,
}

/// <summary>How much of the registry an export covers.</summary>
public enum ExportFileRange
{
    /// <summary>Every root classic regedit shows under Computer.</summary>
    WholeRegistry,

    /// <summary>The key selected in the tree and its descendants.</summary>
    SelectedBranch,
}

/// <summary>The path, writer, and range approved in the Export save dialog.</summary>
public sealed record ExportFileDialogResult(
    string FilePath,
    ExportFileFormat Format,
    ExportFileRange Range);

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

    // IDs for the IFileDialogCustomize controls. They are outside the range the shell uses.
    private const uint ExportRangeGroupId = 2000;
    private const uint ExportRangeListId = 2001;
    private const uint ExportRangeAllId = 2002;
    private const uint ExportRangeBranchId = 2003;
    private const uint ExportRangeBranchListId = 2004;
    private const uint ExportRangeBranchOnlyItemId = 2005;

    // CDCONTROLSTATEF bits. A control is enabled and visible when both are set.
    private const uint ControlEnabled = 0x00000001;
    private const uint ControlVisible = 0x00000002;

    private static readonly (string Name, string Spec)[] OpenFilters =
    {
        ("Registration Files (*.reg)", "*.reg"),
    };

    private static readonly (string Name, string Spec)[] SaveFilters =
    {
        ("Registration Files (*.reg)", "*.reg"),
        ("Text Files (*.txt)", "*.txt"),
        ("Win9x/NT4 Registration Files (*.reg)", "*.reg"),
    };

    /// <summary>
    /// Shows the open dialog owned by <paramref name="ownerHwnd"/>. Returns the chosen file system
    /// path, or null when the user cancelled. The dialog offers registry files only.
    /// </summary>
    public static string? ShowOpen(nint ownerHwnd, string? defaultFileName = null)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialog();
        return ShowCore(dialog, ownerHwnd, defaultFileName, save: false, OpenFilters);
    }

    /// <summary>
    /// Shows the save dialog owned by <paramref name="ownerHwnd"/>. Returns the approved file
    /// system path, or null when the user cancelled. This overload keeps the pre-RF7 behavior for
    /// callers that do not offer an export range.
    /// </summary>
    public static string? ShowSave(nint ownerHwnd, string? defaultFileName = null)
    {
        var dialog = (IFileSaveDialog)new FileSaveDialog();
        return ShowCore(dialog, ownerHwnd, defaultFileName, save: true, SaveFilters);
    }

    /// <summary>
    /// Shows the Export save dialog with the classic "Export range" group and the three export
    /// filters. The range group defaults to Selected branch. When the tree selection is Computer,
    /// <paramref name="hasSelectedKey"/> is false: All is selected and Selected branch is disabled.
    /// The caller receives the approved path, writer, and range, or null when the user cancelled.
    /// </summary>
    public static ExportFileDialogResult? ShowExportSave(
        nint ownerHwnd,
        string? defaultFileName,
        bool hasSelectedKey)
    {
        var dialog = (IFileSaveDialog)new FileSaveDialog();
        AddExportRange(dialog, hasSelectedKey);

        string? path = ShowCore(dialog, ownerHwnd, defaultFileName, save: true, SaveFilters);
        if (path is null)
        {
            return null;
        }

        ExportFileFormat format = ReadFormat(dialog);
        ExportFileRange range = ReadRange(dialog, hasSelectedKey);
        return new ExportFileDialogResult(path, format, range);
    }

    private static string? ShowCore(
        IFileDialog dialog,
        nint ownerHwnd,
        string? defaultFileName,
        bool save,
        (string Name, string Spec)[] filters)
    {
        using var filterSpecs = new FilterSpecifications(filters);
        filterSpecs.Apply(dialog);
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

    /// <summary>
    /// Adds the classic Export range panel through IFileDialogCustomize. The dialog stays the
    /// system dialog; the controls are the ones the shell hosts for custom file dialog clients.
    /// </summary>
    private static void AddExportRange(IFileDialog dialog, bool hasSelectedKey)
    {
        var customize = (IFileDialogCustomize)dialog;
        Check(customize.StartVisualGroup(ExportRangeGroupId, "Export range"));

        if (hasSelectedKey)
        {
            // One option group with both choices. Selected branch is the classic default.
            Check(customize.AddRadioButtonList(ExportRangeListId));
            Check(customize.AddControlItem(ExportRangeListId, ExportRangeAllId, "&All"));
            Check(customize.AddControlItem(ExportRangeListId, ExportRangeBranchId, "S&elected branch"));
            Check(customize.SetControlState(ExportRangeListId, ControlEnabled | ControlVisible));
            Check(customize.SetControlItemState(
                ExportRangeListId,
                ExportRangeBranchId,
                ControlEnabled | ControlVisible));
            Check(customize.SetSelectedControlItem(ExportRangeListId, ExportRangeBranchId));
        }
        else
        {
            // Computer has no branch. Two single-item groups let SetControlState disable only the
            // branch control while All stays selected and enabled. A single option group cannot
            // disable one item and keep the other interactive in the shell.
            Check(customize.AddRadioButtonList(ExportRangeListId));
            Check(customize.AddControlItem(ExportRangeListId, ExportRangeAllId, "&All"));
            Check(customize.SetControlState(ExportRangeListId, ControlEnabled | ControlVisible));
            Check(customize.SetSelectedControlItem(ExportRangeListId, ExportRangeAllId));

            Check(customize.AddRadioButtonList(ExportRangeBranchListId));
            Check(customize.AddControlItem(
                ExportRangeBranchListId,
                ExportRangeBranchOnlyItemId,
                "S&elected branch"));
            Check(customize.SetControlState(ExportRangeBranchListId, ControlVisible));
        }

        Check(customize.EndVisualGroup());
    }

    private static ExportFileFormat ReadFormat(IFileDialog dialog)
    {
        dialog.GetFileTypeIndex(out uint filterIndex);
        return filterIndex switch
        {
            2 => ExportFileFormat.Text,
            3 => ExportFileFormat.RegistrationRegedit4,
            _ => ExportFileFormat.RegistrationV5,
        };
    }

    private static ExportFileRange ReadRange(IFileDialog dialog, bool hasSelectedKey)
    {
        if (!hasSelectedKey)
        {
            return ExportFileRange.WholeRegistry;
        }

        var customize = (IFileDialogCustomize)dialog;
        Check(customize.GetSelectedControlItem(ExportRangeListId, out uint selected));
        return selected == ExportRangeAllId
            ? ExportFileRange.WholeRegistry
            : ExportFileRange.SelectedBranch;
    }

    private static void Check(int hresult)
    {
        if (hresult < 0)
        {
            Marshal.ThrowExceptionForHR(hresult);
        }
    }

    /// <summary>
    /// Owns the unmanaged filter name and pattern strings until the dialog has returned. The
    /// strings are passed as COMDLG_FILTERSPEC pairs, not the Win32 Name|Pattern form.
    /// </summary>
    private sealed class FilterSpecifications : IDisposable
    {
        private readonly nint[] _names;
        private readonly nint[] _specs;
        private readonly COMDLG_FILTERSPEC[] _specsArray;

        internal FilterSpecifications((string Name, string Spec)[] filters)
        {
            _names = new nint[filters.Length];
            _specs = new nint[filters.Length];
            _specsArray = new COMDLG_FILTERSPEC[filters.Length];

            for (var index = 0; index < filters.Length; index++)
            {
                _names[index] = Marshal.StringToCoTaskMemUni(filters[index].Name);
                _specs[index] = Marshal.StringToCoTaskMemUni(filters[index].Spec);
                _specsArray[index] = new COMDLG_FILTERSPEC
                {
                    pszName = new PCWSTR((char*)_names[index]),
                    pszSpec = new PCWSTR((char*)_specs[index]),
                };
            }
        }

        internal void Apply(IFileDialog dialog)
        {
            fixed (COMDLG_FILTERSPEC* specs = _specsArray)
            {
                dialog.SetFileTypes((uint)_specsArray.Length, specs);
            }
        }

        public void Dispose()
        {
            for (var index = 0; index < _names.Length; index++)
            {
                Marshal.FreeCoTaskMem(_names[index]);
                Marshal.FreeCoTaskMem(_specs[index]);
            }
        }
    }

    /// <summary>
    /// IFileDialogCustomize from shobjidl_core.h. CsWin32 does not generate this interface for the
    /// file dialog surface, so the vtable is declared here in the header order. Every method keeps
    /// its HRESULT so a denied customization is reported instead of throwing from a wrapper.
    /// </summary>
    [ComImport]
    [Guid("e6fdd21a-163f-4975-9c8c-a69f1ba37034")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialogCustomize
    {
        [PreserveSig]
        int EnableOpenDropDown(uint id);

        [PreserveSig]
        int AddMenu(uint id, [MarshalAs(UnmanagedType.LPWStr)] string label);

        [PreserveSig]
        int AddPushButton(uint id, [MarshalAs(UnmanagedType.LPWStr)] string label);

        [PreserveSig]
        int AddComboBox(uint id);

        [PreserveSig]
        int AddRadioButtonList(uint id);

        [PreserveSig]
        int AddCheckButton(
            uint id,
            [MarshalAs(UnmanagedType.LPWStr)] string label,
            [MarshalAs(UnmanagedType.Bool)] bool isChecked);

        [PreserveSig]
        int AddEditBox(uint id, [MarshalAs(UnmanagedType.LPWStr)] string text);

        [PreserveSig]
        int AddSeparator(uint id);

        [PreserveSig]
        int AddText(uint id, [MarshalAs(UnmanagedType.LPWStr)] string text);

        [PreserveSig]
        int SetControlLabel(uint id, [MarshalAs(UnmanagedType.LPWStr)] string label);

        [PreserveSig]
        int GetControlState(uint id, out uint state);

        [PreserveSig]
        int SetControlState(uint id, uint state);

        [PreserveSig]
        int GetEditBoxText(uint id, out nint text);

        [PreserveSig]
        int SetEditBoxText(uint id, [MarshalAs(UnmanagedType.LPWStr)] string text);

        [PreserveSig]
        int GetCheckButtonState(uint id, [MarshalAs(UnmanagedType.Bool)] out bool isChecked);

        [PreserveSig]
        int SetCheckButtonState(uint id, [MarshalAs(UnmanagedType.Bool)] bool isChecked);

        [PreserveSig]
        int AddControlItem(
            uint id,
            uint itemId,
            [MarshalAs(UnmanagedType.LPWStr)] string label);

        [PreserveSig]
        int RemoveControlItem(uint id, uint itemId);

        [PreserveSig]
        int RemoveAllControlItems(uint id);

        [PreserveSig]
        int GetControlItemState(uint id, uint itemId, out uint state);

        [PreserveSig]
        int SetControlItemState(uint id, uint itemId, uint state);

        [PreserveSig]
        int GetSelectedControlItem(uint id, out uint itemId);

        [PreserveSig]
        int SetSelectedControlItem(uint id, uint itemId);

        [PreserveSig]
        int StartVisualGroup(uint id, [MarshalAs(UnmanagedType.LPWStr)] string label);

        [PreserveSig]
        int EndVisualGroup();

        [PreserveSig]
        int MakeProminent(uint id);
    }
}

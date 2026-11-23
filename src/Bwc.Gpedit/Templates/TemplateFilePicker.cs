using System.Runtime.InteropServices;

namespace Bwc.Gpedit.Templates;

/// <summary>
/// The file picker of Add in the Add/Remove Templates dialog: the common item open dialog (COM IFileOpenDialog), the same
/// dialog the Registry Editor uses, with a Template files (*.admx) filter and an All files filter. The WinRT picker is not
/// used because it does not reliably take a plain owner window in an unpackaged process. The dialog disables its owner
/// while it is open. A cancelled dialog gives null and changes nothing (docs/failure-modes/gpedit-templates.md, section 4).
/// </summary>
internal static class TemplateFilePicker
{
    // HRESULT_FROM_WIN32(ERROR_CANCELLED).
    private const int HResultCancelled = unchecked((int)0x800704C7);

    private const uint FosNoChangeDir = 0x00000008;
    private const uint FosForceFileSystem = 0x00000040;
    private const uint FosPathMustExist = 0x00000800;
    private const uint FosFileMustExist = 0x00001000;
    private const uint SigdnFileSysPath = 0x80058000;

    /// <summary>Shows the picker owned by the window and returns the chosen path, or null when it was cancelled.</summary>
    /// <param name="owner">The window that owns the picker.</param>
    internal static string? Pick(nint owner)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialogObject();
        try
        {
            dialog.SetTitle(GpeditStrings.Templates("TemplatesPickerTitle"));
            dialog.SetFileTypes(
                2,
                [
                    new FilterSpec(GpeditStrings.Templates("TemplatesPickerFilterAdmx"), "*.admx"),
                    new FilterSpec(GpeditStrings.Templates("TemplatesPickerFilterAll"), "*.*"),
                ]);
            dialog.SetFileTypeIndex(1);
            dialog.SetOptions(FosNoChangeDir | FosForceFileSystem | FosPathMustExist | FosFileMustExist);
            int result = dialog.Show(owner);
            if (result == HResultCancelled)
            {
                return null;
            }

            Marshal.ThrowExceptionForHR(result);
            dialog.GetResult(out IShellItem item);
            try
            {
                item.GetDisplayName(SigdnFileSysPath, out nint name);
                try
                {
                    return Marshal.PtrToStringUni(name);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(name);
                }
            }
            finally
            {
                _ = Marshal.ReleaseComObject(item);
            }
        }
        finally
        {
            _ = Marshal.ReleaseComObject(dialog);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FilterSpec
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        internal string Name;

        [MarshalAs(UnmanagedType.LPWStr)]
        internal string Spec;

        internal FilterSpec(string name, string spec)
        {
            Name = name;
            Spec = spec;
        }
    }

    /// <summary>CLSID_FileOpenDialog.</summary>
    [ComImport]
    [Guid("dc1c5a9c-e88a-4dde-a5a1-60f82a20aef7")]
    private class FileOpenDialogObject
    {
    }

    /// <summary>
    /// IFileOpenDialog from shobjidl_core.h. The vtable is declared in header order, IModalWindow first, then IFileDialog,
    /// then the two methods of IFileOpenDialog. Slots that are never called keep their place with plain pointer arguments.
    /// </summary>
    [ComImport]
    [Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig]
        int Show(nint hwndOwner);

        void SetFileTypes(uint fileTypes, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] FilterSpec[] filters);

        void SetFileTypeIndex(uint index);

        void GetFileTypeIndex(out uint index);

        void Advise(nint events, out uint cookie);

        void Unadvise(uint cookie);

        void SetOptions(uint options);

        void GetOptions(out uint options);

        void SetDefaultFolder(nint item);

        void SetFolder(nint item);

        void GetFolder(out nint item);

        void GetCurrentSelection(out nint item);

        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetFileName(out nint name);

        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);

        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);

        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);

        void GetResult(out IShellItem item);

        void AddPlace(nint item, uint placement);

        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);

        void Close(int result);

        void SetClientGuid(ref Guid guid);

        void ClearClientData();

        void SetFilter(nint filter);

        void GetResults(out nint items);

        void GetSelectedItems(out nint items);
    }

    /// <summary>IShellItem (43826d1e-e718-42ee-bc55-a1e261c37bfe).</summary>
    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint context, ref Guid handler, ref Guid interfaceId, out nint result);

        void GetParent(out nint parent);

        void GetDisplayName(uint form, out nint name);

        void GetAttributes(uint mask, out uint attributes);

        void Compare(nint other, uint hint, out int order);
    }
}

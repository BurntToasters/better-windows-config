using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Windows.Win32;

namespace Bwc.UI.Dialogs;

/// <summary>
/// IFileDialogEvents from shobjidl_core.h, in header order. The shell pointers are passed as nint, because the
/// Export sink reads the dialog through the IFileDialogCustomize object it already holds.
/// </summary>
[ComImport]
[Guid("973510DB-7D7F-452B-8975-74A85828D354")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileDialogEvents
{
    [PreserveSig]
    int OnFileOk(nint pfd);

    [PreserveSig]
    int OnFolderChanging(nint pfd, nint psiFolder);

    [PreserveSig]
    int OnFolderChange(nint pfd);

    [PreserveSig]
    int OnSelectionChange(nint pfd);

    [PreserveSig]
    int OnShareViolation(nint pfd, nint psi, out int response);

    [PreserveSig]
    int OnTypeChange(nint pfd);

    [PreserveSig]
    int OnOverwrite(nint pfd, nint psi, out int response);
}

/// <summary>
/// The start of IFileDialog's vtable (IID 42F85136-DB7E-439C-85F1-E4075D135FC8), up to Unadvise. Show, the file type
/// methods, Advise, and Unadvise keep their header order so Advise and Unadvise land in the right slots.
/// </summary>
[ComImport]
[Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileDialogAdvise
{
    [PreserveSig]
    int Show(nint owner);

    [PreserveSig]
    int SetFileTypes(uint count, nint types);

    [PreserveSig]
    int SetFileTypeIndex(uint index);

    [PreserveSig]
    int GetFileTypeIndex(out uint index);

    [PreserveSig]
    int Advise(IFileDialogEvents events, out uint cookie);

    [PreserveSig]
    int Unadvise(uint cookie);
}

/// <summary>
/// The Export range sink. OK with a branch path that does not name an existing key returns S_FALSE, so the dialog
/// stays open, and the message box is shown after the callback returns, owned by the dialog. A path that OK approves
/// is kept for the caller. The branch edit is enabled whatever radio is chosen, as in classic (oracle export-range).
/// </summary>
[ComVisible(true)]
internal sealed class ExportRangeEvents : IFileDialogEvents
{
    private const int SOk = 0;
    private const int SFalse = 1;

    private readonly CommonFileDialogs.IFileDialogCustomize _customize;
    private readonly ExportBranchRules _rules;
    private readonly bool _hasSelectedKey;
    private readonly DispatcherQueue? _queue;

    internal ExportRangeEvents(CommonFileDialogs.IFileDialogCustomize customize, ExportBranchRules rules, bool hasSelectedKey)
    {
        _customize = customize;
        _rules = rules;
        _hasSelectedKey = hasSelectedKey;
        _queue = DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>The branch path that OK approved. Empty when All was chosen, or when no approval happened.</summary>
    internal string ApprovedBranch { get; private set; } = string.Empty;

    public int OnFileOk(nint pfd)
    {
        try
        {
            if (!IsBranchChosen())
            {
                return SOk;
            }

            string text = ReadBranchText();
            if (_rules.BranchExists(text))
            {
                ApprovedBranch = text;
                return SOk;
            }

            ShowMissingBranch();
            return SFalse;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The branch could not be checked. Nothing is approved, so the caller refuses the export.
            UiDiagnostics.Warn?.Invoke("The export branch path could not be checked: " + ex.Message);
            return SFalse;
        }
    }

    public int OnFolderChanging(nint pfd, nint psiFolder) => SOk;

    public int OnFolderChange(nint pfd) => SOk;

    public int OnSelectionChange(nint pfd) => SOk;

    public int OnShareViolation(nint pfd, nint psi, out int response)
    {
        response = 0;
        return SOk;
    }

    public int OnTypeChange(nint pfd) => SOk;

    public int OnOverwrite(nint pfd, nint psi, out int response)
    {
        response = 0;
        return SOk;
    }

    private bool IsBranchChosen()
    {
        if (!_hasSelectedKey)
        {
            return false;
        }

        _ = _customize.GetSelectedControlItem(CommonFileDialogs.ExportRangeListId, out uint selected);
        return selected == CommonFileDialogs.ExportRangeBranchId;
    }

    private string ReadBranchText()
    {
        int hresult = _customize.GetEditBoxText(CommonFileDialogs.ExportRangeBranchEditId, out nint text);
        if (hresult < 0)
        {
            Marshal.ThrowExceptionForHR(hresult);
        }

        if (text == 0)
        {
            return string.Empty;
        }

        try
        {
            return Marshal.PtrToStringUni(text) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    private void ShowMissingBranch()
    {
        if (_queue is null)
        {
            UiDiagnostics.Warn?.Invoke("The export branch message could not be shown: no UI thread queue.");
            return;
        }

        // The box is shown after this callback returns, so the file dialog's own message loop keeps running.
        string caption = _rules.MissingCaption;
        string message = _rules.MissingMessage;
        _ = _queue.TryEnqueue(() => { _ = ShowMissingBranchAsync(caption, message); });
    }

    private static async Task ShowMissingBranchAsync(string caption, string message)
    {
        // The file dialog is the active window while its OK is being checked, so the box is owned by it.
        nint owner = ActiveWindowHandle();
        ModalDialogWindow box = ErrorDialog.ForMessage(caption, message);
        _ = await box.ShowAsync(owner);
    }

    private static unsafe nint ActiveWindowHandle() => (nint)PInvoke.GetActiveWindow().Value;
}

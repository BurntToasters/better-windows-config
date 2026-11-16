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
    private readonly object _dialog;
    private nint _annotatedEdit;
    private DispatcherQueueTimer? _retry;

    /// <summary>
    /// The naming retry: every 100 ms for up to 60 seconds. A loaded machine can delay the shell's controls well past a
    /// few seconds, and the timer stops at once when the name is set or the dialog releases it.
    /// </summary>
    private const int RetryIntervalMs = 100;
    private const int MaxRetryAttempts = 600;

    internal ExportRangeEvents(
        CommonFileDialogs.IFileDialogCustomize customize,
        ExportBranchRules rules,
        bool hasSelectedKey,
        object dialog)
    {
        _customize = customize;
        _rules = rules;
        _hasSelectedKey = hasSelectedKey;
        _dialog = dialog;
        _queue = DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>
    /// Names the branch path edit for screen readers (Dynamic Annotation). The shell creates the edit after the dialog
    /// initializes, so the name is set on the first folder or selection event, and again if the edit window changes.
    /// </summary>
    private void AnnotateBranchEdit()
    {
        if (!TryAnnotateOnce())
        {
            StartRetry();
        }
    }

    /// <summary>
    /// Starts the naming as soon as the dialog is advised, so it does not depend on a shell event arriving. The timer
    /// keeps trying until the edit is named, or until the dialog releases it.
    /// </summary>
    internal void StartAnnotation() => StartRetry();

    /// <summary>
    /// One attempt. Returns true when the edit is named, or when naming is not possible at all (no dialog window).
    /// False means the edit window is not there yet, and the caller retries.
    /// </summary>
    private bool TryAnnotateOnce()
    {
        try
        {
            if (_annotatedEdit != 0)
            {
                return true;
            }

            // A file dialog object without IOleWindow cannot be named, so the retry stops. A dialog window that does not exist
            // yet is retried, because the shell creates it when the dialog is shown.
            if (_dialog is not IOleWindowAnnotation window)
            {
                return true;
            }

            if (window.GetWindow(out nint dialogWindow) < 0 || dialogWindow == 0)
            {
                return false;
            }

            nint edit = ExportBranchAnnotation.FindBranchEdit(dialogWindow, CommonFileDialogs.ExportRangeBranchEditId);
            if (edit == 0)
            {
                return false;
            }

            string name = BwcStrings.Get("FileDialogExportRangeSelectedBranch").Replace("&", string.Empty, StringComparison.Ordinal);
            if (ExportBranchAnnotation.TrySetName(edit, name))
            {
                _annotatedEdit = edit;
                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A callback must not throw into the shell. The edit stays unnamed and the export still works.
            UiDiagnostics.Warn?.Invoke("The export branch path edit could not be named: " + ex.Message);
            return true;
        }
    }

    /// <summary>Retries the naming on a timer until the edit window exists, for a bounded number of attempts.</summary>
    private void StartRetry()
    {
        if (_retry is not null || _queue is null)
        {
            return;
        }

        int attempts = 0;
        DispatcherQueueTimer timer = _queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(RetryIntervalMs);
        timer.Tick += (_, _) =>
        {
            attempts++;
            if (TryAnnotateOnce() || attempts >= MaxRetryAttempts)
            {
                timer.Stop();
                _retry = null;
            }
        };
        _retry = timer;
        timer.Start();
    }

    /// <summary>Clears the branch edit's name and stops any pending retry. Called when the dialog closes.</summary>
    internal void ReleaseAnnotation()
    {
        _retry?.Stop();
        _retry = null;
        if (_annotatedEdit == 0)
        {
            return;
        }

        ExportBranchAnnotation.ClearName(_annotatedEdit);
        _annotatedEdit = 0;
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
        catch (ExportBranchUnreadableException ex)
        {
            // The typed key exists but cannot be read. The classic box names it, and the dialog stays open.
            ShowBranchMessage(ex.Caption, ex.Message);
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

    public int OnFolderChange(nint pfd)
    {
        AnnotateBranchEdit();
        return SOk;
    }

    public int OnSelectionChange(nint pfd)
    {
        AnnotateBranchEdit();
        return SOk;
    }

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

    private void ShowMissingBranch() => ShowBranchMessage(_rules.MissingCaption, _rules.MissingMessage);

    /// <summary>Shows a box for the branch edit's check, after the callback returns. The dialog stays open.</summary>
    private void ShowBranchMessage(string caption, string message)
    {
        if (_queue is null)
        {
            UiDiagnostics.Warn?.Invoke("The export branch message could not be shown: no UI thread queue.");
            return;
        }

        // The box is shown after this callback returns, so the file dialog's own message loop keeps running.
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

/// <summary>
/// Thrown by the branch check (ExportBranchRules.BranchExists) when a typed key exists but cannot be read. The message
/// is the classic box text for that key, and Caption is its caption. The sink shows it and keeps the dialog open.
/// </summary>
public sealed class ExportBranchUnreadableException : Exception
{
    public ExportBranchUnreadableException(string caption, string message)
        : base(message)
    {
        Caption = caption;
    }

    /// <summary>The caption of the box.</summary>
    public string Caption { get; }
}

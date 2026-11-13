using Bwc.Core;
using Bwc.UI.Dialogs;

namespace Bwc.Regedit.Dialogs;

/// <summary>
/// The Import progress window (RT_DIALOG 110, DLG-019). It is a native Win32 dialog built from the classic
/// template: style 0x90C001C4 (no WS_SYSMENU, so no Close box), 240 x 59 DLU at (32, 24), the MS Shell Dlg
/// 8 pt font, and the controls in template order: 101 msctls_progress32, 100 the file name, and the "File:"
/// label. There is no Cancel button and no default button. The window ignores close requests until the host
/// completes the import. See docs/failure-modes/import-export.md section 14 and classic-dialog.md 4.7.
/// </summary>
public sealed class ImportProgressDialog : IDisposable
{
    // The RT_DIALOG 110 template values (en-US regedit.exe.mui), read from the resource.
    private const uint ClassicStyle = 0x90C001C4;
    private const int TemplateX = 32;
    private const int TemplateY = 24;
    private const int DialogWidthDlu = 240;
    private const int DialogHeightDlu = 59;
    private const ushort FontPointSize = 8;
    private const uint ProgressStyle = 0x50000000; // WS_CHILD | WS_VISIBLE
    private const uint StaticStyle = 0x50020000; // WS_CHILD | WS_VISIBLE | WS_GROUP

    private const int ProgressBarId = 101;
    private const int FileNameId = 100;
    private const int FileLabelId = 0xFFFF; // IDC_STATIC

    private const int FileLabelX = 10;
    private const int FileLabelY = 10;
    private const int FileLabelWidth = 24;
    private const int FileNameY = 22;
    private const int FileNameHeight = 8;
    private const int BarY = 40;
    private const int BarHeight = 9;
    private const int ContentWidth = 220;
    private const int LabelHeight = 8;

    private readonly NativeProgressDialog _window;

    private ImportProgressDialog(NativeProgressDialog window)
    {
        _window = window;
    }

    /// <summary>
    /// Shows the window owned by <paramref name="ownerHwnd"/>, with the owner disabled. Returns null when the
    /// window cannot be created; the import then runs without a window, and the failure is logged.
    /// </summary>
    public static ImportProgressDialog? TryShow(nint ownerHwnd, string caption, string fileLabel, string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(caption);
        ArgumentNullException.ThrowIfNull(fileLabel);
        ArgumentNullException.ThrowIfNull(fileName);

        var template = new Win32DialogTemplate(
            caption,
            ClassicStyle,
            exStyle: 0,
            TemplateX,
            TemplateY,
            DialogWidthDlu,
            DialogHeightDlu,
            ClassicDialogMetrics.TemplateFontFace,
            FontPointSize,
            [
                new Win32DialogItem(
                    ProgressBarId, "msctls_progress32", ProgressStyle, string.Empty,
                    FileLabelX, BarY, ContentWidth, BarHeight),
                new Win32DialogItem(
                    FileNameId, "Static", StaticStyle, fileName,
                    FileLabelX, FileNameY, ContentWidth, FileNameHeight),
                new Win32DialogItem(
                    FileLabelId, "Static", StaticStyle, fileLabel,
                    FileLabelX, FileLabelY, FileLabelWidth, LabelHeight),
            ]);

        try
        {
            return new ImportProgressDialog(NativeProgressDialog.Show(ownerHwnd, template, ProgressBarId));
        }
        catch (Exception ex)
        {
            Log.Warn("Registry Editor could not show the import progress window: " + ex.Message);
            return null;
        }
    }

    /// <summary>Sets the determinate position. Completed and total are sections, as the import counts them.</summary>
    public void SetProgress(int completed, int total) => _window.SetProgress(completed, total);

    /// <summary>Re-enables the owner and destroys the window after the import finished.</summary>
    public void Complete() => _window.Complete();

    public void Dispose() => _window.Dispose();
}

using Bwc.UI.Dialogs;
using Bwc.UI.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.Regedit.Dialogs;

/// <summary>
/// The Import progress window (RT_DIALOG 110, DLG-019). It shows the caption, the file label, the
/// file name, and a progress bar. The classic template has no Cancel button and no default button, so
/// the user cannot cancel the import from this window. Declared difference: classic DLG-019 (style
/// 0x90C001C4) has no system menu and no Close box, but the WinUI caption keeps both, because WinUI
/// restores WS_SYSMENU after the style is cleared. A close request from the Close box or Alt+F4 is
/// refused until the host ends the import. See docs/failure-modes/import-export.md section 14.
/// </summary>
public sealed class ImportProgressDialog : ClassicDialog
{
    // DLG-019 rectangles in dialog units, relative to the client area: 240 x 59 DLU.
    private const int DialogWidthDlu = 240;
    private const int DialogHeightDlu = 59;
    private const int FileLabelX = 10;
    private const int FileLabelY = 10;
    private const int FileLabelWidth = 24;
    private const int FileNameY = 22;
    private const int FileNameHeight = 8;
    private const int BarY = 40;
    private const int BarHeight = 9;
    private const int ContentWidth = 220;
    private const int LabelHeight = 8;

    private readonly TextBlock _fileLabel;
    private readonly TextBlock _fileName;
    private readonly ProgressBar _bar;
    private readonly Canvas _canvas;
    private bool _completedByHost;
    private bool _closed;

    /// <summary>Creates the window with the classic caption, the "File:" label, and the file name.</summary>
    public ImportProgressDialog(string caption, string fileLabel, string fileName)
        : base(caption, new ClassicFontSpec(ClassicDialogMetrics.TemplateFontFace, 8))
    {
        ArgumentException.ThrowIfNullOrEmpty(caption);
        ArgumentNullException.ThrowIfNull(fileLabel);
        ArgumentNullException.ThrowIfNull(fileName);

        _fileLabel = new TextBlock
        {
            Text = fileLabel,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = BwcPaint.WindowText,
        };
        AutomationProperties.SetAutomationId(_fileLabel, "ImportFileLabel");

        _fileName = new TextBlock
        {
            Text = fileName,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = BwcPaint.WindowText,
        };
        AutomationProperties.SetAutomationId(_fileName, "ImportFileName");

        _bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Value = 0,
            IsIndeterminate = true,
            Height = 12,
            BorderBrush = BwcPaint.Shadow,
            BorderThickness = new Thickness(1),
        };
        AutomationProperties.SetAutomationId(_bar, "ProgressBar");
        AutomationProperties.SetName(_bar, "Progress");

        _canvas = new Canvas();
        _canvas.Children.Add(_fileLabel);
        _canvas.Children.Add(_fileName);
        _canvas.Children.Add(_bar);
        SetFlowContent(_canvas);

        // A close request from the caption or Alt+F4 is refused by IsCloseAllowed until the import completes.
        Closed += OnProgressClosed;
    }

    /// <summary>True when the host closed the window because the import finished.</summary>
    public bool CompletedByHost => _completedByHost;

    /// <summary>
    /// Refuses a close request (Alt+F4) while the import runs. The owner stays disabled. The host's own close,
    /// after the import ends, goes through <see cref="CompleteFromHost"/>, which sets the flag first.
    /// </summary>
    protected override bool IsCloseAllowed() => _completedByHost;

    /// <summary>Sets a determinate position. Completed and total are sections, as the import counts them.</summary>
    public void SetProgress(int completed, int total)
    {
        if (_closed)
        {
            return;
        }

        _bar.IsIndeterminate = false;
        _bar.Maximum = Math.Max(1, total);
        _bar.Value = Math.Clamp(completed, 0, Math.Max(1, total));
    }

    /// <summary>Closes the window after the import finished. The close request is allowed here.</summary>
    public void CompleteFromHost()
    {
        if (_closed)
        {
            return;
        }

        _completedByHost = true;
        try
        {
            Close();
        }
        catch (Exception)
        {
            // The window was already gone. The Closed handler did not run, so mark it closed here.
            _closed = true;
        }
    }

    protected override void PrepareFlowLayout(ClassicFlowContext context)
    {
        ClassicDialogMetrics metrics = context.Metrics;
        double scale = context.Scale;

        _canvas.Width = metrics.ToPixelsX(DialogWidthDlu) / scale;
        _canvas.Height = metrics.ToPixelsY(DialogHeightDlu) / scale;

        Place(_fileLabel, FileLabelX, FileLabelY, FileLabelWidth, LabelHeight);
        Place(_fileName, FileLabelX, FileNameY, ContentWidth, FileNameHeight);
        Place(_bar, FileLabelX, BarY, ContentWidth, BarHeight);
    }

    private void Place(FrameworkElement element, int x, int y, int cx, int cy)
    {
        Canvas.SetLeft(element, DluX(x));
        Canvas.SetTop(element, DluY(y));
        element.Width = DluX(cx);
        element.Height = DluY(cy);
    }

    private void OnProgressClosed(object? sender, WindowEventArgs args)
    {
        Closed -= OnProgressClosed;
        _closed = true;
    }
}

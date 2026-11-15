using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.UI.Dialogs;

/// <summary>
/// Classic print progress dialog (RT_DIALOG 105). The client is 154 x 49 DLU. Cancel is the default button, as the
/// template declares. The host shows it owned by the main window while the job runs, and closes it when the job
/// ends. Cancel runs <paramref name="onCancel"/> once; the job then stops through its abort proc.
/// </summary>
public sealed class PrintProgressDialog : ClassicDialog
{
    private readonly ClassicDialogDefinition _template;
    private readonly TextBlock _text;
    private readonly Button _cancel;
    private readonly Canvas _canvas;
    private readonly Action? _onCancel;
    private bool _closed;
    private bool _completedByHost;

    /// <summary>Creates the progress dialog. The texts come from the caller's resources.</summary>
    public PrintProgressDialog(string caption, string printingText, string cancelText, Action? onCancel)
        : base(caption, new ClassicFontSpec(ClassicDialogMetrics.TemplateFontFace, 8))
    {
        ArgumentException.ThrowIfNullOrEmpty(caption);
        ArgumentNullException.ThrowIfNull(printingText);
        ArgumentNullException.ThrowIfNull(cancelText);

        _template = ClassicDialogTemplates.PrintProgress(caption, printingText, cancelText);
        _onCancel = onCancel;

        _text = (TextBlock)CreateControl(_template.Controls[1]);
        _cancel = (Button)CreateControl(_template.Controls[0]);
        AutomationProperties.SetAutomationId(_text, "PrintProgressText");

        _canvas = new Canvas();
        _canvas.Children.Add(_text);
        _canvas.Children.Add(_cancel);
        SetFlowContent(_canvas);

        Closed += OnProgressClosed;
    }

    /// <summary>True when the user cancelled or closed the dialog.</summary>
    public bool Cancelled { get; private set; }

    /// <summary>Closes the dialog after the job ends. The cancel callback is not invoked.</summary>
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
            // The dialog was already gone. The Closed handler did not run, so mark it closed here.
            _closed = true;
        }
    }

    protected override void OnDialogLoaded()
    {
        _ = _cancel.Focus(FocusState.Programmatic);
    }

    protected override void PrepareFlowLayout(ClassicFlowContext context)
    {
        ClassicDialogMetrics metrics = context.Metrics;
        double scale = context.Scale;

        _canvas.Width = metrics.ToPixelsX(_template.WidthDlu) / scale;
        _canvas.Height = metrics.ToPixelsY(_template.HeightDlu) / scale;

        Place(_text, _template.Controls[1]);
        Place(_cancel, _template.Controls[0]);
    }

    private void Place(FrameworkElement element, ClassicControlDefinition definition)
    {
        Canvas.SetLeft(element, DipX(definition.X));
        Canvas.SetTop(element, DipY(definition.Y));
        element.Width = DipX(definition.Cx);
        element.Height = DipY(definition.Cy);
    }

    private double DipX(int dlu) => Metrics.ToPixelsX(dlu) / Metrics.Scale;

    private double DipY(int dlu) => Metrics.ToPixelsY(dlu) / Metrics.Scale;

    private void OnProgressClosed(object? sender, WindowEventArgs args)
    {
        Closed -= OnProgressClosed;
        _closed = true;
        if (!_completedByHost)
        {
            Cancelled = true;
            _onCancel?.Invoke();
        }
    }
}

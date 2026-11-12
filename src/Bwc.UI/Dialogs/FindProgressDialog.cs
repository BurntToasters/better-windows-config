using Bwc.UI.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Bwc.UI.Dialogs;

/// <summary>
/// Classic Find progress dialog (RT_DIALOG 109, oracle DLG-017/018). The client is 170 x 64 DLU. The
/// Cancel button is the default button, as the template declares. The dialog is modal to the owner,
/// and Cancel closes it and cancels the background search. The host closes it when the search ends.
/// </summary>
/// <remarks>
/// The template model carries text controls only, so the searching icon static (ordinal 0x66) is
/// drawn here as a vector magnifier at the template position. No Microsoft icon bytes are copied.
/// </remarks>
public sealed class FindProgressDialog : ClassicDialog
{
    private const int IconLeftDlu = 7;
    private const int IconTopDlu = 8;
    private const double IconDip = 32;

    private readonly ClassicDialogDefinition _template;
    private readonly TextBlock _text;
    private readonly Button _cancel;
    private readonly Canvas _canvas;
    private readonly FrameworkElement _icon;
    private readonly Action? _onCancel;
    private bool _closed;
    private bool _completedByHost;

    /// <summary>
    /// Creates the progress dialog. <paramref name="onCancel"/> runs once when the user cancels,
    /// by the button, Escape, Enter on the default button, or the title bar close box.
    /// </summary>
    public FindProgressDialog(string caption, string searchingText, string cancelText, Action? onCancel)
        : base(caption, new ClassicFontSpec(ClassicDialogMetrics.TemplateFontFace, 8))
    {
        ArgumentException.ThrowIfNullOrEmpty(caption);
        ArgumentNullException.ThrowIfNull(searchingText);
        ArgumentNullException.ThrowIfNull(cancelText);

        _template = ClassicDialogTemplates.FindProgress(caption, searchingText, cancelText);
        _onCancel = onCancel;

        _text = (TextBlock)CreateControl(_template.Controls[1]);
        _cancel = (Button)CreateControl(_template.Controls[0]);

        _icon = CreateSearchIcon();
        AutomationProperties.SetAutomationId(_icon, "FindProgressIcon");

        _canvas = new Canvas();
        _canvas.Children.Add(_icon);
        _canvas.Children.Add(_text);
        _canvas.Children.Add(_cancel);
        SetFlowContent(_canvas);

        Closed += OnProgressClosed;
    }

    /// <summary>True when the host closed the dialog because the search finished, not because the user cancelled.</summary>
    public bool CompletedByHost => _completedByHost;

    /// <summary>True when the user cancelled or closed the dialog.</summary>
    public bool Cancelled { get; private set; }

    /// <summary>Closes the dialog after a completed search. The cancel callback is not invoked.</summary>
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

        Canvas.SetLeft(_icon, metrics.ToPixelsX(IconLeftDlu) / scale);
        Canvas.SetTop(_icon, metrics.ToPixelsY(IconTopDlu) / scale);
        _icon.Width = IconDip;
        _icon.Height = IconDip;
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

    private static FrameworkElement CreateSearchIcon()
    {
        var icon = new Canvas
        {
            IsHitTestVisible = false,
        };
        AutomationProperties.SetName(icon, "Searching");

        var glass = new Ellipse
        {
            Width = 18,
            Height = 18,
            Stroke = BwcBrushes.WindowText,
            StrokeThickness = 2,
        };
        Canvas.SetLeft(glass, 3);
        Canvas.SetTop(glass, 3);
        icon.Children.Add(glass);

        var handle = new Line
        {
            X1 = 18,
            Y1 = 18,
            X2 = 28,
            Y2 = 28,
            Stroke = BwcBrushes.WindowText,
            StrokeThickness = 3,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        icon.Children.Add(handle);
        return icon;
    }
}

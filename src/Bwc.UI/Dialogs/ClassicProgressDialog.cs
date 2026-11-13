using Bwc.UI.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.UI.Dialogs;

/// <summary>
/// A classic owned progress dialog with a progress bar and a Cancel button. Import and export use
/// one instance each while the registry work runs on the thread pool. Progress updates are applied
/// on the UI thread; Cancel, Escape, Enter on the default Cancel button, or the close box requests
/// cancellation through the callback. The host closes the dialog when the work ends.
/// </summary>
public sealed class ClassicProgressDialog : ClassicDialog
{
    private const int DialogWidthDlu = 280;
    private const int DialogHeightDlu = 82;
    private const int MarginDlu = 7;
    private const int TitleTopDlu = 7;
    private const int BarTopDlu = 22;
    private const int StatusTopDlu = 40;
    private const int ButtonTopDlu = 60;
    private const int ButtonWidthDlu = 50;
    private const int ButtonHeightDlu = 14;

    private readonly TextBlock _title;
    private readonly TextBlock _status;
    private readonly ProgressBar _bar;
    private readonly Button _cancel;
    private readonly Canvas _canvas;
    private readonly Action? _onCancel;
    private bool _closed;
    private bool _completedByHost;

    /// <summary>
    /// Creates the dialog. <paramref name="onCancel"/> runs once when the user cancels, by the
    /// button, Escape, Enter on the default button, or the title bar close box.
    /// </summary>
    public ClassicProgressDialog(string caption, string title, string cancelText, Action? onCancel)
        : base(caption, new ClassicFontSpec(ClassicDialogMetrics.TemplateFontFace, 8))
    {
        ArgumentException.ThrowIfNullOrEmpty(caption);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(cancelText);

        _onCancel = onCancel;

        _title = new TextBlock
        {
            Text = title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAutomationId(_title, "ProgressTitle");

        _bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Value = 0,
            IsIndeterminate = true,
            Height = 12,
            BorderBrush = BwcBrushes.Shadow,
            BorderThickness = new Thickness(1),
        };
        AutomationProperties.SetAutomationId(_bar, "ProgressBar");
        AutomationProperties.SetName(_bar, "Progress");

        _status = new TextBlock
        {
            Text = string.Empty,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = BwcBrushes.WindowText,
        };
        AutomationProperties.SetAutomationId(_status, "ProgressStatus");

        var cancelDefinition = new ClassicControlDefinition(
            ClassicControlKind.Button,
            2,
            "DialogSecondary",
            cancelText,
            0,
            0,
            ButtonWidthDlu,
            ButtonHeightDlu)
        {
            TabStop = true,
            IsDefault = true,
        };
        _cancel = (Button)CreateControl(cancelDefinition);

        _canvas = new Canvas();
        _canvas.Children.Add(_title);
        _canvas.Children.Add(_bar);
        _canvas.Children.Add(_status);
        _canvas.Children.Add(_cancel);
        SetFlowContent(_canvas);

        Closed += OnProgressClosed;
    }

    /// <summary>True when the host closed the dialog because the work finished, not because the user cancelled.</summary>
    public bool CompletedByHost => _completedByHost;

    /// <summary>True when the user cancelled or closed the dialog.</summary>
    public bool Cancelled { get; private set; }

    /// <summary>Sets a determinate position and the status line.</summary>
    public void SetProgress(int completed, int total, string status)
    {
        if (_closed)
        {
            return;
        }

        _bar.IsIndeterminate = false;
        _bar.Maximum = Math.Max(1, total);
        _bar.Value = Math.Clamp(completed, 0, Math.Max(1, total));
        _status.Text = status;
    }

    /// <summary>Switches to the marquee form used when the total is not known and sets the status line.</summary>
    public void SetIndeterminate(string status)
    {
        if (_closed)
        {
            return;
        }

        _bar.IsIndeterminate = true;
        _status.Text = status;
    }

    /// <summary>Replaces the status line without changing the bar.</summary>
    public void SetStatus(string status)
    {
        if (_closed)
        {
            return;
        }

        _status.Text = status;
    }

    /// <summary>Closes the dialog after completed work. The cancel callback is not invoked.</summary>
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

        _canvas.Width = metrics.ToPixelsX(DialogWidthDlu) / scale;
        _canvas.Height = metrics.ToPixelsY(DialogHeightDlu) / scale;

        Place(_title, MarginDlu, TitleTopDlu, DialogWidthDlu - (2 * MarginDlu), 11);
        Place(_bar, MarginDlu, BarTopDlu, DialogWidthDlu - (2 * MarginDlu), 12);
        Place(_status, MarginDlu, StatusTopDlu, DialogWidthDlu - (2 * MarginDlu), 11);
        Place(
            _cancel,
            DialogWidthDlu - MarginDlu - ButtonWidthDlu,
            ButtonTopDlu,
            ButtonWidthDlu,
            ButtonHeightDlu);
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
        if (!_completedByHost)
        {
            Cancelled = true;
            _onCancel?.Invoke();
        }
    }
}

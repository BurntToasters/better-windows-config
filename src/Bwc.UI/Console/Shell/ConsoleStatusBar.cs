using Bwc.UI.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.UI.Console.Shell;

/// <summary>
/// The status bar: one line of text on the face color, 23 DIP high as in the probed classic window. The text is long
/// text safe: one line, trimmed with an ellipsis, and the full string is the accessible name of the pane.
/// </summary>
public sealed class ConsoleStatusBar : UserControl
{
    /// <summary>The height in DIPs (46 px at 192 DPI in the probe).</summary>
    public const double BarHeight = 23;

    private readonly Border _frame = new();
    private readonly TextBlock _pane = new();
    private string _text = string.Empty;

    public ConsoleStatusBar()
    {
        AutomationProperties.SetAutomationId(this, "StatusBar");
        AutomationProperties.SetName(this, ConsoleStrings.Get("ConsoleStatusBarName"));
        IsTabStop = false;
        Height = BarHeight;

        _pane.TextWrapping = TextWrapping.NoWrap;
        _pane.TextTrimming = TextTrimming.CharacterEllipsis;
        _pane.VerticalAlignment = VerticalAlignment.Center;
        _pane.Margin = new Thickness(4, 0, 4, 0);
        _pane.IsTextSelectionEnabled = false;
        AutomationProperties.SetAutomationId(_pane, "StatusBar.Pane0");
        AutomationProperties.SetName(_pane, string.Empty);

        _frame.BorderThickness = new Thickness(0, 1, 0, 0);
        _frame.Child = _pane;
        Content = _frame;
        ApplyBrushes();
    }

    /// <summary>The text of the first pane. It is kept while the bar is hidden and shown again with the bar.</summary>
    public string Text
    {
        get => _text;
        set
        {
            _text = value ?? string.Empty;
            _pane.Text = _text;
            AutomationProperties.SetName(_pane, _text);
        }
    }

    /// <summary>Takes the colors from the current palette.</summary>
    public void ApplyBrushes()
    {
        _frame.Background = BwcBrushes.Face;
        _frame.BorderBrush = BwcBrushes.Highlight3D;
        _pane.Foreground = BwcBrushes.WindowText;
    }

    /// <summary>Takes the font from the dense metrics.</summary>
    public void ApplyMetrics()
    {
        _pane.FontSize = DenseMetrics.FontSize;
        _pane.FontFamily = DenseMetrics.FontFamily;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ConsoleStatusBarPeer(this);

    private sealed class ConsoleStatusBarPeer : FrameworkElementAutomationPeer
    {
        public ConsoleStatusBarPeer(ConsoleStatusBar owner)
            : base(owner)
        {
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.StatusBar;

        protected override string GetClassNameCore() => nameof(ConsoleStatusBar);
    }
}

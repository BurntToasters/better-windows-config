using Bwc.UI.Theme;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Bwc.UI.Console.Shell;

/// <summary>
/// A toolbar button that can show itself pressed, for a host command that is on or off (the Filter button of the Local
/// Group Policy Editor). A click only raises the toolbar's Invoked event; the host decides the new state and calls
/// <see cref="ConsoleToolbar.SetButtonChecked"/>. UI Automation sees a button with the Invoke pattern and the Toggle
/// pattern; Toggle does what a click does.
/// </summary>
internal sealed class ConsoleToolbarToggleButton : Button
{
    private bool _toggled;

    /// <summary>True while the button is drawn pressed.</summary>
    internal bool IsToggled
    {
        get => _toggled;
        set
        {
            if (_toggled == value)
            {
                return;
            }

            _toggled = value;
            UpdateFrame();
        }
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateFrame();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TogglePeer(this);

    /// <summary>The frame of the button template: sunken while toggled, flat otherwise.</summary>
    private void UpdateFrame()
    {
        if (GetTemplateChild("Frame") is not Border frame)
        {
            return;
        }

        frame.BorderBrush = _toggled ? BwcBrushes.Shadow : new SolidColorBrush(Colors.Transparent);
        frame.Background = _toggled ? BwcBrushes.Highlight3D : new SolidColorBrush(Colors.Transparent);
    }

    private sealed class TogglePeer : ButtonAutomationPeer, IToggleProvider
    {
        private readonly ConsoleToolbarToggleButton _owner;

        internal TogglePeer(ConsoleToolbarToggleButton owner)
            : base(owner)
        {
            _owner = owner;
        }

        public ToggleState ToggleState => _owner.IsToggled ? ToggleState.On : ToggleState.Off;

        public void Toggle() => Invoke();

        protected override object GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Toggle ? this : base.GetPatternCore(patternInterface);
    }
}

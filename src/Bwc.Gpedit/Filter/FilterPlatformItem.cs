using Bwc.UI.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Bwc.Gpedit.Filter;

/// <summary>
/// One line of the platform list of the Filter Options dialog: a check box and the name of a supported-on definition. A
/// click or the space bar ticks it. UI Automation sees a list item with the Toggle pattern, named after the definition.
/// </summary>
internal sealed class FilterPlatformItem : ListBoxItem
{
    private readonly TextBlock _tick;
    private bool _ticked;

    internal FilterPlatformItem(string key, string displayName, string automationId)
    {
        Key = key;
        DisplayName = displayName;
        AutomationProperties.SetAutomationId(this, automationId);
        AutomationProperties.SetName(this, displayName);

        // The box is a bordered square with a tick, in the palette colors like the check boxes of the dialog.
        _tick = new TextBlock
        {
            Text = string.Empty,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI Symbol"),
            FontSize = 11,
            Foreground = BwcPaint.WindowText,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var box = new Border
        {
            Width = 13,
            Height = 13,
            BorderThickness = new Thickness(1),
            BorderBrush = BwcPaint.Shadow,
            Margin = new Thickness(2, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _tick,
        };
        var label = new TextBlock
        {
            Text = displayName,
            Foreground = BwcPaint.WindowText,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(box);
        row.Children.Add(label);
        Content = row;

        Tapped += (_, _) => Ticked = !Ticked;
        KeyDown += OnItemKeyDown;
    }

    /// <summary>The key of the supported-on definition (<c>PlatformList.KeyOf</c>).</summary>
    internal string Key { get; }

    /// <summary>The text of the definition, shown and used as the accessible name.</summary>
    internal string DisplayName { get; }

    /// <summary>True while the platform is ticked.</summary>
    internal bool Ticked
    {
        get => _ticked;
        set
        {
            if (_ticked == value)
            {
                return;
            }

            bool before = _ticked;
            _ticked = value;
            _tick.Text = value ? "✓" : string.Empty;
            if (FrameworkElementAutomationPeer.FromElement(this) is TogglePeer peer)
            {
                peer.RaiseToggleChanged(before, value);
            }
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TogglePeer(this);

    private void OnItemKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Space)
        {
            Ticked = !Ticked;
            e.Handled = true;
        }
    }

    private sealed class TogglePeer : ListBoxItemAutomationPeer, IToggleProvider
    {
        private readonly FilterPlatformItem _owner;

        internal TogglePeer(FilterPlatformItem owner)
            : base(owner)
        {
            _owner = owner;
        }

        public ToggleState ToggleState => _owner.Ticked ? ToggleState.On : ToggleState.Off;

        public void Toggle() => _owner.Ticked = !_owner.Ticked;

        internal void RaiseToggleChanged(bool oldValue, bool newValue) =>
            RaisePropertyChangedEvent(
                TogglePatternIdentifiers.ToggleStateProperty,
                oldValue ? ToggleState.On : ToggleState.Off,
                newValue ? ToggleState.On : ToggleState.Off);

        protected override string GetNameCore() => _owner.DisplayName;

        protected override object GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Toggle ? this : base.GetPatternCore(patternInterface);
    }
}

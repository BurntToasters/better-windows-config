using Bwc.UI.Menu;
using Bwc.UI.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.Gpedit.Filter;

/// <summary>
/// A menu entry that is on or off (Filter On), drawn like a check item of the console menus and seen by UI Automation as an
/// entry with the Invoke pattern and the Toggle pattern. The shared check item has no Toggle pattern, so a screen reader or
/// a test cannot read its state; this one can. The menu builder makes the plain check item, and <see cref="Replace"/> swaps
/// it for this one, with the same text, id, access key and command. The shared check item's style belongs to its own type,
/// so this item takes the style of a plain entry and shows the check mark as its icon, in the same gutter.
/// </summary>
internal sealed class FilterToggleMenuItem : MenuFlyoutItem
{
    public static readonly DependencyProperty IsCheckedProperty = DependencyProperty.Register(
        nameof(IsChecked),
        typeof(bool),
        typeof(FilterToggleMenuItem),
        new PropertyMetadata(false, OnIsCheckedChanged));

    /// <summary>True while the command is on: the entry shows its check mark and reports Toggle state On.</summary>
    public bool IsChecked
    {
        get => (bool)GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    /// <summary>Replaces every check item with this command id in the list (and in its submenus) by a toggle item.</summary>
    internal static void Replace(IList<MenuFlyoutItemBase> items, int commandId)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is CheckMenuFlyoutItem source && source.Tag is int id && id == commandId)
            {
                items[i] = From(source);
            }
            else if (items[i] is MenuFlyoutSubItem sub)
            {
                Replace(sub.Items, commandId);
            }
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TogglePeer(this);

    private static FilterToggleMenuItem From(CheckMenuFlyoutItem source)
    {
        var item = new FilterToggleMenuItem
        {
            Text = source.Text,
            IsEnabled = source.IsEnabled,
            Tag = source.Tag,
            Style = FindStyle("BwcMenuFlyoutItemStyle"),
            Visibility = source.Visibility,
            MinWidth = source.MinWidth,
        };
        item.IsChecked = source.IsChecked;
        AutomationProperties.SetAutomationId(item, AutomationProperties.GetAutomationId(source));
        if (!string.IsNullOrEmpty(source.AccessKey))
        {
            item.AccessKey = source.AccessKey;
        }

        if (MenuItemActions.TryGet(source, out Action action))
        {
            item.Click += (_, _) => action();
            MenuItemActions.Register(item, action);
        }

        return item;
    }

    private static void OnIsCheckedChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FilterToggleMenuItem item)
        {
            return;
        }

        item.UpdateCheckState();
        if (FrameworkElementAutomationPeer.FromElement(item) is TogglePeer peer)
        {
            peer.RaiseToggleChanged((bool)args.OldValue, (bool)args.NewValue);
        }
    }

    private static Style? FindStyle(string key) =>
        Application.Current is { } application ? FindStyle(application.Resources, key) : null;

    private static Style? FindStyle(ResourceDictionary dictionary, string key)
    {
        if (dictionary.TryGetValue(key, out object? value) && value is Style style)
        {
            return style;
        }

        foreach (ResourceDictionary merged in dictionary.MergedDictionaries)
        {
            if (FindStyle(merged, key) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void UpdateCheckState()
    {
        Icon = IsChecked
            ? new FontIcon
            {
                Glyph = "\uE73E",
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                Foreground = BwcPaint.WindowText,
            }
            : null;
    }

    private sealed class TogglePeer : MenuFlyoutItemAutomationPeer, IToggleProvider
    {
        private readonly FilterToggleMenuItem _owner;

        internal TogglePeer(FilterToggleMenuItem owner)
            : base(owner)
        {
            _owner = owner;
        }

        public ToggleState ToggleState => _owner.IsChecked ? ToggleState.On : ToggleState.Off;

        public void Toggle()
        {
            if (_owner.IsEnabled)
            {
                Invoke();
            }
        }

        internal void RaiseToggleChanged(bool oldValue, bool newValue) =>
            RaisePropertyChangedEvent(
                TogglePatternIdentifiers.ToggleStateProperty,
                oldValue ? ToggleState.On : ToggleState.Off,
                newValue ? ToggleState.On : ToggleState.Off);

        protected override object GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Toggle ? this : base.GetPatternCore(patternInterface);
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.UI.Menu;

/// <summary>
/// A menu leaf that shows a check mark in its gutter while <see cref="IsChecked"/> is true. It is a plain
/// <see cref="MenuFlyoutItem"/>, so a click, a UI Automation Invoke, and a mnemonic run the same path as every
/// other leaf. The owner decides what the check means; this item only draws it.
/// </summary>
public sealed class CheckMenuFlyoutItem : MenuFlyoutItem
{
    /// <summary>Backing store for <see cref="IsChecked"/>.</summary>
    public static readonly DependencyProperty IsCheckedProperty = DependencyProperty.Register(
        nameof(IsChecked),
        typeof(bool),
        typeof(CheckMenuFlyoutItem),
        new PropertyMetadata(false, OnIsCheckedChanged));

    /// <summary>True when the check glyph is shown.</summary>
    public bool IsChecked
    {
        get => (bool)GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateCheckState();
    }

    private static void OnIsCheckedChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is CheckMenuFlyoutItem item)
        {
            item.UpdateCheckState();
        }
    }

    private void UpdateCheckState()
    {
        _ = VisualStateManager.GoToState(this, IsChecked ? "Checked" : "Unchecked", useTransitions: false);
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.UI.Menu;

/// <summary>
/// The command action of each menu leaf. A click and a mnemonic letter both run the same action, so the keyboard does not
/// need the item's Click event, which only the control can raise. The action is stored on the item itself, as the value of
/// an attached dependency property, and not in a table keyed by the managed wrapper: a WinUI item's wrapper can be
/// collected while the native item lives, and the menu then hands out a new wrapper that a wrapper-keyed table does not
/// know. The value lives in the native item's property store, so every wrapper of the item reads it
/// (docs/failure-modes/menus.md, "action lookup by item identity").
/// </summary>
public static class MenuItemActions
{
    private static readonly DependencyProperty ActionProperty = DependencyProperty.RegisterAttached(
        "Action",
        typeof(object),
        typeof(MenuItemActions),
        new PropertyMetadata(null));

#if BWC_E2E
    private static readonly List<WeakReference<MenuFlyoutItem>> RegisteredWrappers = [];

    /// <summary>
    /// E2E only. How many managed wrappers registered an action, and how many of them have been collected since. A test
    /// forces a collection and then drives the access keys, to prove the lookup does not depend on a wrapper staying alive.
    /// </summary>
    public static (int Registered, int Collected) E2EWrapperCounts()
    {
        lock (RegisteredWrappers)
        {
            return (RegisteredWrappers.Count, RegisteredWrappers.Count(reference => !reference.TryGetTarget(out _)));
        }
    }
#endif

    /// <summary>Records the action of a leaf (click and mnemonic run it). A second call replaces it.</summary>
    public static void Register(MenuFlyoutItem item, Action action)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(action);
        item.SetValue(ActionProperty, new ActionHolder(action));
#if BWC_E2E
        lock (RegisteredWrappers)
        {
            RegisteredWrappers.Add(new WeakReference<MenuFlyoutItem>(item));
        }
#endif
    }

    /// <summary>Returns the registered action of a leaf, or false when the leaf has none.</summary>
    public static bool TryGet(MenuFlyoutItem item, out Action action)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.GetValue(ActionProperty) is ActionHolder holder)
        {
            action = holder.Action;
            return true;
        }

        action = null!;
        return false;
    }

    /// <summary>A plain managed object around the delegate, so the property store holds a type that round-trips.</summary>
    private sealed class ActionHolder(Action action)
    {
        public Action Action { get; } = action;
    }
}

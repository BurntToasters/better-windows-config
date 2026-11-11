using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.UI.Menu;

/// <summary>
/// The command action of each menu leaf, keyed by the item. A click and a mnemonic letter both run the
/// same action, so the keyboard does not need the item's Click event, which only the control can raise.
/// </summary>
public static class MenuItemActions
{
    private static readonly ConditionalWeakTable<MenuFlyoutItem, Action> Actions = new();

    /// <summary>Records the action of a leaf. The action runs for a click and for its mnemonic.</summary>
    public static void Register(MenuFlyoutItem item, Action action)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(action);
        Actions.AddOrUpdate(item, action);
    }

    /// <summary>Returns the registered action of a leaf, or false when the leaf has none.</summary>
    public static bool TryGet(MenuFlyoutItem item, out Action action)
    {
        return Actions.TryGetValue(item, out action!);
    }
}

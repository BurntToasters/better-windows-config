using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Bwc.UI.Tree;

/// <summary>
/// The double-click time of the user's settings, used for the slow second click that starts a rename (BEH-010).
/// A click on an item that was already selected, after the double-click time has passed, is a slow click.
/// </summary>
internal static partial class ClickTiming
{
    [LibraryImport("user32.dll")]
    private static partial uint GetDoubleClickTime();

    /// <summary>The double-click time of the user, in milliseconds.</summary>
    public static double DoubleClickMilliseconds() => GetDoubleClickTime();

    /// <summary>True when the keyboard focus is on the element or inside it.</summary>
    public static bool HasFocusWithin(UIElement root)
    {
        if (root.XamlRoot is not { } xamlRoot)
        {
            return false;
        }

        for (DependencyObject? current = FocusManager.GetFocusedElement(xamlRoot) as DependencyObject;
            current is not null;
            current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, root))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The tick count, in milliseconds, that a selection time is measured against.</summary>
    public static long Now() => Environment.TickCount64;

    /// <summary>True when the item has been selected for at least the double-click time.</summary>
    public static bool IsSlowSecondClick(long selectedAt) => Now() - selectedAt >= GetDoubleClickTime();
}

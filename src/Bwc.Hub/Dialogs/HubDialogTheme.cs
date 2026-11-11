using Bwc.Hub.Theme;
using Bwc.UI.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Bwc.Hub.Dialogs;

/// <summary>
/// Makes a Hub dialog follow the BWC palette rather than the Windows app theme. The classic dialog
/// background is a theme resource of its root, and the message text takes the app's system brush, so
/// a light BWC palette on a dark Windows theme (or the reverse) left black or white text on the same
/// color. The root takes the palette's element theme, and every text block takes the palette text brush
/// once the dialog has loaded, because the visual tree only exists after that.
/// </summary>
internal static class HubDialogTheme
{
    public static void Apply(Window dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        if (dialog.Content is not FrameworkElement root)
        {
            return;
        }

        root.RequestedTheme = BwcTheme.ElementThemeFor(BwcTheme.Current);
        root.Loaded += (_, _) => RecolorText(root);
    }

    private static void RecolorText(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock text)
            {
                text.Foreground = HubBrushes.WindowText;
            }

            RecolorText(child);
        }
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Bwc.Hub.Theme;

/// <summary>
/// Theme brushes for code-built Hub content. The brushes are the Dense.xaml resources, which the
/// shared theme recolors in place when the palette changes, so a reference taken here stays current.
/// </summary>
internal static class HubBrushes
{
    public static Brush WindowText => Get("BwcWindowTextBrush");

    public static Brush Window => Get("BwcWindowBrush");

    public static Brush Face => Get("BwcFaceBrush");

    public static Brush Shadow => Get("BwcShadowBrush");

    public static Brush GrayText => Get("BwcGrayTextBrush");

    public static Brush Selection => Get("BwcSelectionBrush");

    public static Brush SelectionText => Get("BwcSelectionTextBrush");

    private static Brush Get(string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush brush)
        {
            return brush;
        }

        throw new InvalidOperationException($"The theme brush '{key}' is missing. Merge Bwc.UI Dense.xaml into App resources.");
    }
}

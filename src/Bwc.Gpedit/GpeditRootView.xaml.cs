using Bwc.UI.Theme;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.Gpedit;

/// <summary>The Local Group Policy Editor content. Empty for now: the console frame is added by later pieces.</summary>
public sealed partial class GpeditRootView : UserControl
{
    public GpeditRootView()
    {
        InitializeComponent();

        // The palette is pinned on RootGrid, as the other tools do, so this UserControl root keeps no
        // RequestedTheme and its ActualTheme follows Windows. A change of the effective Windows theme
        // raises ActualThemeChanged here, and the palette re-reads.
        ApplyTheme();
        BwcTheme.Changed += (_, _) => ApplyTheme();
        ActualThemeChanged += (_, _) => BwcTheme.RefreshSystem();
    }

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = BwcTheme.ElementThemeFor(BwcTheme.Current);
    }
}

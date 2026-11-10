using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Bwc.Regedit;

public sealed partial class RegeditRootView : UserControl
{
    private const string ResourceMap = "Bwc.Regedit/Resources";

    public RegeditRootView()
    {
        InitializeComponent();
        var loader = new ResourceLoader(ResourceLoader.GetDefaultResourceFilePath(), ResourceMap);
        TitleText.Text = loader.GetString("RegeditTitle");
    }
}

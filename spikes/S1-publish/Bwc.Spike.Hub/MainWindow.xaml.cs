using Bwc.Spike.UI;
using Microsoft.UI.Xaml;

namespace Bwc.Spike.Hub;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "BWC Spike Hub";
        ElevationText.Text = ProcessProbe.IsElevated() ? "elevated" : "not elevated";
    }
}

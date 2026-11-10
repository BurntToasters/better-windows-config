using Bwc.Spike.UI;
using Microsoft.UI.Xaml;

namespace Bwc.Spike.Tool;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "BWC Spike Tool";
        ElevationText.Text = ProcessProbe.IsElevated() ? "elevated" : "not elevated";
    }
}

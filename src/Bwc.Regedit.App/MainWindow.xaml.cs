using Microsoft.UI.Xaml;

namespace Bwc.Regedit.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "Registry Editor";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "bwc.ico"));
    }
}

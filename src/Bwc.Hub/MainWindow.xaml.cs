using System.ComponentModel;
using System.Diagnostics;
using Bwc.Core;
using Bwc.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.Hub;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = BwcStrings.ProductName;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "bwc.ico"));
        foreach (var tool in ToolCatalog.Tools)
        {
            ToolList.Children.Add(CreateRow(tool));
        }
    }

    private UIElement CreateRow(ToolCatalog.Tool tool)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(new TextBlock { Text = tool.DisplayName, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = tool.Description, VerticalAlignment = VerticalAlignment.Center });

        var launch = new Button { Content = "Launch" };
        AutomationProperties.SetAutomationId(launch, "Launch_" + tool.Id);
        launch.Click += (_, _) => Launch(tool);
        row.Children.Add(launch);
        return row;
    }

    private void Launch(ToolCatalog.Tool tool)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, tool.ExeName);
        if (!File.Exists(exe))
        {
            LaunchError.Text = $"{tool.DisplayName} is not installed: {exe} was not found.";
            return;
        }

        try
        {
            // UseShellExecute lets Windows apply the tool's own manifest (highestAvailable), as Explorer would.
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory })?.Dispose();
            LaunchError.Text = string.Empty;
        }
        catch (Win32Exception ex)
        {
            LaunchError.Text = $"{tool.DisplayName} did not start: {ex.Message}";
        }
    }
}

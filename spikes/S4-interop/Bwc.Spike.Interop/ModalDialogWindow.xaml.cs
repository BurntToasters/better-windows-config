using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Bwc.Spike.Interop;

/// <summary>Owned modal dialog (OverlappedPresenter.CreateForDialog, IsModal) opened by MainWindow.</summary>
public sealed partial class ModalDialogWindow : Window
{
    public ModalDialogWindow()
    {
        InitializeComponent();
        Title = "Modal Dialog (S4)";
        ((FrameworkElement)Content).KeyDown += OnRootKeyDown;
    }

    /// <summary>"ok: &lt;item&gt;" after OK, "cancelled" after Cancel or Escape, "closed" when closed any other way.</summary>
    public string Result { get; private set; } = "closed";

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectionText is null) return;
        SelectionText.Text = "Selected: " + SelectedValue();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Result = "ok: " + SelectedValue();
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Result = "cancelled";
        Close();
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            Result = "cancelled";
            Close();
            e.Handled = true;
        }
    }

    private string SelectedValue()
    {
        return (Picker.SelectedItem as ComboBoxItem)?.Content as string ?? "(none)";
    }
}

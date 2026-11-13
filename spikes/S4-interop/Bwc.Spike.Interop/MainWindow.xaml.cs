using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace Bwc.Spike.Interop;

public sealed partial class MainWindow : Window
{
    private readonly nint _hwnd;
    private MenuKeyRouter? _router;

    public MainWindow()
    {
        InitializeComponent();
        Title = "BWC Interop Spike S4";
        _hwnd = WindowNative.GetWindowHandle(this);
        AppWindow.Resize(new SizeInt32(900, 600));
        RegistryPermissionsDialog.EnsureKey();
        Closed += OnClosed;
        ((FrameworkElement)Content).Loaded += OnContentLoaded;
        ConfigureMenuArrowRing();
        // Arrow keys never reach the window-procedure hook (see MenuKeyRouter.HandleKeyFromXaml).
        Menu.PreviewKeyDown += OnMenuPreviewKeyDown;
    }

    /// <summary>
    /// Left/Right wrap around the top-level items through XYFocus (XAML's own focus navigation). Up and Down
    /// point at the item itself so XAML does not move focus away; Down is handled by MenuKeyRouter (opens the flyout).
    /// </summary>
    private void ConfigureMenuArrowRing()
    {
        var items = new[] { FileItem, EditItem, ViewItem };
        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i];
            item.XYFocusLeft = items[(i + items.Length - 1) % items.Length];
            item.XYFocusRight = items[(i + 1) % items.Length];
            item.XYFocusUp = item;
            item.XYFocusDown = item;
        }
    }

    private void OnMainComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MainComboText is null) return;
        MainComboText.Text = "Main combo: " + ((MainCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "(none)");
    }

    private void OnMenuPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (_router is not null && _router.HandleKeyFromXaml(e.Key))
        {
            e.Handled = true;
        }
    }

    private void OnContentLoaded(object sender, RoutedEventArgs e)
    {
        ((FrameworkElement)Content).Loaded -= OnContentLoaded;
        _router ??= new MenuKeyRouter(_hwnd, new[] { FileItem, EditItem, ViewItem }, text => MenuStateText.Text = text);
        OpenModalButton.Focus(FocusState.Programmatic);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _router?.Dispose();
        _router = null;
    }

    private void OnOpenModal(object sender, RoutedEventArgs e)
    {
        var dialog = new ModalDialogWindow();
        var dialogHwnd = WindowNative.GetWindowHandle(dialog);

        // Owner: AppWindow.OwnerWindowId is read-only in WinAppSDK 2.6 (AppWindow.Create(presenter, ownerId)
        // builds a non-XAML window), so the Win32 owner is set on the XAML window's HWND.
        Win32.SetWindowLongPtr(dialogHwnd, Win32.GWLP_HWNDPARENT, _hwnd);

        var presenter = OverlappedPresenter.CreateForDialog();
        presenter.IsModal = true;
        presenter.IsResizable = false;
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;
        dialog.AppWindow.SetPresenter(presenter);
        dialog.AppWindow.IsShownInSwitchers = false;
        dialog.AppWindow.Resize(new SizeInt32(440, 260));

        var ownerPos = AppWindow.Position;
        var ownerSize = AppWindow.Size;
        dialog.AppWindow.Move(new PointInt32(
            ownerPos.X + (ownerSize.Width - 440) / 2,
            ownerPos.Y + (ownerSize.Height - 260) / 2));

        // Owner stays disabled while the dialog is open. IsModal alone does NOT do this on WinAppSDK 2.6
        // (measured: IsWindowEnabled(owner) stayed TRUE with IsModal set), so the owner is disabled explicitly.
        Win32.EnableWindow(_hwnd, 0);
        dialog.Closed += (_, _) =>
        {
            Win32.EnableWindow(_hwnd, 1);
            Win32.SetActiveWindow(_hwnd);
            ModalResultText.Text = dialog.Result;
        };
        dialog.Activate();
    }

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        FileResultText.Text = Describe(() => CommonFileDialogs.ShowOpen(_hwnd));
    }

    private void OnSaveFile(object sender, RoutedEventArgs e)
    {
        FileResultText.Text = Describe(() => CommonFileDialogs.ShowSave(_hwnd));
    }

    private void OnPermissions(object sender, RoutedEventArgs e)
    {
        var hr = RegistryPermissionsDialog.Show(_hwnd);
        PermissionsResultText.Text = $"EditSecurity hr=0x{hr:X8}";
    }

    private static string Describe(Func<string?> show)
    {
        try
        {
            return show() ?? "cancelled";
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
            return "error: " + ex.Message;
        }
    }
}

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace Bwc.UI.Dialogs;

/// <summary>
/// Owned modal dialog window. The owner HWND is disabled while the dialog is open, the dialog is
/// centered over the owner, and the result task completes exactly once on Closed. Escape cancels
/// and Enter invokes the primary action unless a multi-line editor opts out.
/// </summary>
public partial class ModalDialogWindow : Window
{
    private TaskCompletionSource<bool>? _completion;
    private nint _ownerHwnd;
    private bool _ownerDisabled;
    private bool _accepted;
    private double _preferredWidthDip = 420;
    private double _preferredHeightDip = 220;

    public ModalDialogWindow()
    {
        InitializeComponent();
        DialogRoot.KeyDown += OnRootKeyDown;
        ((FrameworkElement)Content).Loaded += OnContentLoaded;
    }

    /// <summary>True after the primary action succeeded and before the window closed.</summary>
    public bool Accepted => _accepted;

    /// <summary>Primary button text. Defaults to OK.</summary>
    public string PrimaryText
    {
        get => PrimaryButton.Content as string ?? string.Empty;
        set => PrimaryButton.Content = value;
    }

    /// <summary>Secondary button text. Defaults to Cancel.</summary>
    public string SecondaryText
    {
        get => SecondaryButton.Content as string ?? string.Empty;
        set => SecondaryButton.Content = value;
    }

    /// <summary>False when the dialog is informational and only shows the primary button.</summary>
    protected bool ShowSecondaryButton
    {
        get => SecondaryButton.Visibility == Visibility.Visible;
        set => SecondaryButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>False for multi-line editors where Enter inserts a newline.</summary>
    protected bool SubmitsOnEnter { get; set; } = true;

    /// <summary>
    /// Shows the window owned by <paramref name="ownerHwnd"/> and returns true when the primary
    /// action completed. A second call returns the first task instead of creating a second modal.
    /// </summary>
    public Task<bool> ShowAsync(nint ownerHwnd)
    {
        if (_completion is not null)
        {
            return _completion.Task;
        }

        if (ownerHwnd == 0)
        {
            throw new ArgumentException("A modal dialog needs an owner HWND.", nameof(ownerHwnd));
        }

        _completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ownerHwnd = ownerHwnd;

        try
        {
            nint dialogHwnd = WindowNative.GetWindowHandle(this);
            _ = Win32Dialog.SetWindowLongPtr(dialogHwnd, Win32Dialog.GWLP_HWNDPARENT, ownerHwnd);

            var presenter = OverlappedPresenter.CreateForDialog();
            presenter.IsModal = true;
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            AppWindow.SetPresenter(presenter);
            AppWindow.IsShownInSwitchers = false;

            ApplySizeAndPosition(ownerHwnd);

            Closed += OnClosed;
            _ = Win32Dialog.EnableWindow(ownerHwnd, 0);
            _ownerDisabled = true;
            Activate();
        }
        catch
        {
            RestoreOwner();
            _completion.TrySetResult(false);
            try
            {
                Close();
            }
            catch
            {
                // The window was never shown. There is nothing else to clean up.
            }
        }

        return _completion.Task;
    }

    /// <summary>Sets the fixed size in DIP. The window is sized in physical pixels for the owner DPI.</summary>
    protected void SetPreferredSize(double widthDip, double heightDip)
    {
        if (widthDip <= 0 || heightDip <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(widthDip), "Dialog dimensions must be positive.");
        }

        _preferredWidthDip = widthDip;
        _preferredHeightDip = heightDip;
    }

    protected void SetHeading(string text)
    {
        HeadingText.Text = text;
    }

    protected void SetMessage(string? text)
    {
        MessageText.Text = text ?? string.Empty;
        MessageText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    protected void SetBody(UIElement content)
    {
        DialogBody.Content = content;
    }

    /// <summary>Moves keyboard focus to the primary button. Used by confirmations.</summary>
    protected void FocusPrimary()
    {
        _ = PrimaryButton.Focus(FocusState.Programmatic);
    }

    /// <summary>Shows an inline validation message and keeps the dialog open.</summary>
    protected void ShowValidation(string? message, bool warning = false)
    {
        if (string.IsNullOrEmpty(message))
        {
            ValidationText.Text = string.Empty;
            ValidationText.Visibility = Visibility.Collapsed;
            return;
        }

        ValidationText.Text = message;
        ValidationText.Visibility = Visibility.Visible;

        string key = warning ? "SystemFillColorCautionBrush" : "SystemFillColorCriticalBrush";
        if (Application.Current?.Resources is { } resources
            && resources.TryGetValue(key, out object? value)
            && value is Brush brush)
        {
            ValidationText.Foreground = brush;
        }
    }

    /// <summary>Subclasses set their result here and return false to keep the dialog open.</summary>
    protected virtual bool TryAccept() => true;

    /// <summary>Called once the visual tree is loaded. Subclasses focus their input.</summary>
    protected virtual void OnDialogLoaded()
    {
    }

    protected void Accept()
    {
        if (_accepted || !TryAccept())
        {
            return;
        }

        _accepted = true;
        Close();
    }

    protected void Cancel()
    {
        _accepted = false;
        Close();
    }

    private void ApplySizeAndPosition(nint ownerHwnd)
    {
        double scale = Win32Dialog.ScaleForWindow(ownerHwnd);
        int width = Math.Max(1, (int)Math.Round(_preferredWidthDip * scale));
        int height = Math.Max(1, (int)Math.Round(_preferredHeightDip * scale));
        AppWindow.Resize(new SizeInt32(width, height));

        if (!Win32Dialog.TryGetWindowRect(ownerHwnd, out Win32Dialog.WindowRect owner) ||
            owner.Width <= 0 ||
            owner.Height <= 0)
        {
            return;
        }

        int left = owner.Left + Math.Max(0, (owner.Width - width) / 2);
        int top = owner.Top + Math.Max(0, (owner.Height - height) / 2);
        AppWindow.Move(new PointInt32(left, top));
    }

    private void OnContentLoaded(object sender, RoutedEventArgs e)
    {
        ((FrameworkElement)Content).Loaded -= OnContentLoaded;
        OnDialogLoaded();
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        Accept();
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        Cancel();
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Cancel();
            return;
        }

        if (e.Key == VirtualKey.Enter && SubmitsOnEnter)
        {
            e.Handled = true;
            Accept();
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Closed -= OnClosed;
        RestoreOwner();
        _completion?.TrySetResult(_accepted);
    }

    private void RestoreOwner()
    {
        if (!_ownerDisabled)
        {
            return;
        }

        _ownerDisabled = false;
        _ = Win32Dialog.EnableWindow(_ownerHwnd, 1);
        _ = Win32Dialog.SetActiveWindow(_ownerHwnd);
    }
}

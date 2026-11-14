using System.Runtime.InteropServices;
using Bwc.UI;
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
    private AppWindow? _appWindow;
    private bool _accepted;
    private double _preferredWidthDip = 420;
    private double _preferredHeightDip = 220;

    public ModalDialogWindow()
        : this(fluentLayout: true)
    {
    }

    /// <summary>
    /// Creates the window. A classic dialog passes <c>false</c> and sets its own content, so the
    /// Fluent layout is never built for it. Owner handling and the result task are shared.
    /// </summary>
    protected ModalDialogWindow(bool fluentLayout)
    {
        if (!fluentLayout)
        {
            return;
        }

        InitializeComponent();
        // Handled events too: a focused Button marks Enter handled, and the dialog still needs Enter and Escape.
        DialogRoot.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), handledEventsToo: true);
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
            Activated += OnWindowActivated;
            _appWindow = AppWindow;
            _appWindow.Closing += OnClosing;
            _ = Win32Dialog.EnableWindow(ownerHwnd, 0);
            _ownerDisabled = true;
            Activate();
        }
        catch (Exception ex)
        {
            UiDiagnostics.Warn?.Invoke("A modal dialog could not be shown: " + ex.Message);
            AbandonShow();
        }

        return _completion.Task;
    }

    /// <summary>
    /// Runs when the dialog could not be shown. The owner is restored, the caller gets false, and the window is
    /// closed. A derived dialog releases what it subscribed to here, because its Closed event may never run.
    /// </summary>
    protected virtual void OnShowAbandoned()
    {
    }

    private void AbandonShow()
    {
        RestoreOwner();
        _completion?.TrySetResult(false);
        OnShowAbandoned();
        try
        {
            Close();
        }
        catch (InvalidOperationException ex)
        {
            // The window was never shown, so there is nothing else to clean up.
            UiDiagnostics.Warn?.Invoke("A dialog that was not shown could not be closed: " + ex.Message);
        }
        catch (COMException ex)
        {
            // The window handle is already gone. The owner was restored above.
            UiDiagnostics.Warn?.Invoke("A dialog whose window handle is gone could not be closed: " + ex.Message);
        }
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

    /// <summary>
    /// Closes the dialog and gives the owner back first. <c>Window.Close</c> does not raise
    /// <see cref="AppWindow.Closing"/>, so every programmatic close needs the restore here. The owner is
    /// enabled and activated while the dialog still exists. This hides the base method for subclasses.
    /// </summary>
    public new void Close()
    {
        RestoreOwner();
        base.Close();
    }

    /// <summary>
    /// Runs each time the dialog becomes the active window. Windows resets keyboard focus on activation, so
    /// subclasses put focus back inside the dialog here; otherwise Enter and Escape never reach it.
    /// </summary>
    protected virtual void OnDialogActivated()
    {
    }

    /// <summary>Subclasses set their result here and return false to keep the dialog open.</summary>
    protected virtual bool TryAccept() => true;

    /// <summary>
    /// Asked when a close request arrives from the title bar, Alt+F4, or the system menu. False refuses the
    /// request: the window stays open and the owner stays disabled. Escape, Enter, OK, Cancel, and a
    /// programmatic <c>Close()</c> do not ask, because they are not close requests from the caption.
    /// The default allows every close, so other dialogs keep their Close box.
    /// </summary>
    protected virtual bool IsCloseAllowed() => true;

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

    /// <summary>
    /// Sizes and places the window before it is activated. Runs while the owner is still enabled,
    /// so a throw here leaves nothing to restore. Classic dialogs override it to size the client
    /// area from the template and to clamp the position to the work area.
    /// </summary>
    protected virtual void ApplySizeAndPosition(nint ownerHwnd)
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
        // A control that handled the key owns it, except Enter on a focused button (see ClassicDialog).
        if (e.Handled && !(e.Key == VirtualKey.Enter && FocusManager.GetFocusedElement(DialogRoot.XamlRoot) is Button))
        {
            return;
        }

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

    /// <summary>
    /// Runs before the dialog is hidden when a close request comes from the caption or Alt+F4. A refused close
    /// keeps the owner disabled and the dialog open, so the owner is restored only when the close goes ahead.
    /// The owner is enabled and activated while the dialog still exists, so the system does not pick another
    /// window (such as the XAML input site) to activate.
    /// </summary>
    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!IsCloseAllowed())
        {
            args.Cancel = true;
            return;
        }

        RestoreOwner();
    }

    private void OnWindowActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated || _completion is null || _completion.Task.IsCompleted)
        {
            return;
        }

        OnDialogActivated();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Closed -= OnClosed;
        Activated -= OnWindowActivated;
        if (_appWindow is not null)
        {
            _appWindow.Closing -= OnClosing;
            _appWindow = null;
        }

        RestoreOwner();
        _completion?.TrySetResult(_accepted);
    }

    /// <summary>
    /// Re-enables the owner and makes it the active window. Idempotent: it does nothing once the owner
    /// is restored. A failed Win32 call leaves nothing else to undo, so it does not throw into the close path.
    /// </summary>
    private void RestoreOwner()
    {
        if (!_ownerDisabled)
        {
            return;
        }

        _ownerDisabled = false;
        try
        {
            _ = Win32Dialog.EnableWindow(_ownerHwnd, 1);
            _ = Win32Dialog.SetActiveWindow(_ownerHwnd);
        }
        catch (Exception)
        {
            // The owner handle may already be gone. The dialog is closing either way.
        }
    }
}


using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace Bwc.UI.Dialogs;

/// <summary>A rejected favorite name: the caption and the text of the message box that shows it.</summary>
/// <param name="Caption">The message box caption: RT_STRING 172 for a duplicate, else the prompt title.</param>
/// <param name="Message">The message text.</param>
public sealed record FavoriteNameRejection(string Caption, string Message);

/// <summary>
/// Add to Favorites (RT_DIALOG 112). A rejected name shows its message in its own box and keeps the prompt open
/// with the typed text selected, so the name can be corrected.
/// </summary>
public sealed class FavoriteNameDialog : ClassicDialog
{
    private readonly TextBox _input;
    private readonly Func<string, FavoriteNameRejection?> _validate;

    public FavoriteNameDialog(
        string title,
        string label,
        string initialText,
        Func<string, FavoriteNameRejection?> validate)
        : base(ClassicDialogTemplates.AddFavorite(title, label))
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentException.ThrowIfNullOrEmpty(label);
        ArgumentNullException.ThrowIfNull(validate);

        _validate = validate;
        _input = RequireControl<TextBox>("DialogTextInput");
        _input.Text = initialText ?? string.Empty;
        Value = initialText ?? string.Empty;
    }

    /// <summary>The name after a successful primary action.</summary>
    public string Value { get; private set; }

    protected override void OnDialogLoaded()
    {
        _ = _input.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        _input.SelectAll();
    }

    protected override bool TryAccept()
    {
        string text = _input.Text ?? string.Empty;
        FavoriteNameRejection? rejection = _validate(text);
        if (rejection is not null)
        {
            _ = ShowRejectionAsync(rejection);
            return false;
        }

        Value = text;
        return true;
    }

    private async Task ShowRejectionAsync(FavoriteNameRejection rejection)
    {
        // The box is owned by this prompt, so the prompt stays disabled until the box closes.
        ModalDialogWindow error = ErrorDialog.ForMessage(rejection.Caption, rejection.Message);
        _ = await error.ShowAsync(WindowNative.GetWindowHandle(this));

        _ = _input.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        _input.SelectAll();
    }
}

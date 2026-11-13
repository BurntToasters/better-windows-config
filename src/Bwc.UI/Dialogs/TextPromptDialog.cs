using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace Bwc.UI.Dialogs;

/// <summary>
/// One line text prompt for a key or value name, in the classic Load Hive layout (RT_DIALOG 104).
/// A rejected value shows the error in its own message box and keeps the prompt open.
/// </summary>
public sealed class TextPromptDialog : ClassicDialog
{
    private readonly TextBox _input;
    private readonly Func<string, string?>? _validate;

    public TextPromptDialog(
        string title,
        string label,
        string initialText = "",
        Func<string, string?>? validate = null)
        : base(ClassicDialogTemplates.TextPrompt(title, label))
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentException.ThrowIfNullOrEmpty(label);

        _validate = validate;
        _input = RequireControl<TextBox>("DialogTextInput");
        _input.Text = initialText ?? string.Empty;
        Value = initialText ?? string.Empty;
    }

    /// <summary>The text after a successful primary action.</summary>
    public string Value { get; private set; }

    protected override void OnDialogLoaded()
    {
        _ = _input.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        _input.SelectAll();
    }

    protected override bool TryAccept()
    {
        string text = _input.Text ?? string.Empty;
        string? error = _validate?.Invoke(text);
        if (error is not null)
        {
            _ = ShowErrorAsync(error);
            return false;
        }

        Value = text;
        return true;
    }

    private async Task ShowErrorAsync(string message)
    {
        // The error is owned by this prompt, so the prompt stays disabled until the error closes.
        ModalDialogWindow error = ErrorDialog.ForMessage(Title, message);
        _ = await error.ShowAsync(WindowNative.GetWindowHandle(this));

        _ = _input.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        _input.SelectAll();
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.UI.Dialogs;

/// <summary>One line text prompt used for key and value names.</summary>
public sealed class TextPromptDialog : ModalDialogWindow
{
    private readonly TextBox _input;
    private readonly Func<string, string?>? _validate;

    public TextPromptDialog(
        string title,
        string label,
        string initialText = "",
        Func<string, string?>? validate = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentException.ThrowIfNullOrEmpty(label);

        _validate = validate;
        Title = title;
        SetHeading(title);
        PrimaryText = "OK";
        SecondaryText = "Cancel";
        SetPreferredSize(440, 220);

        _input = new TextBox
        {
            Text = initialText,
            AcceptsReturn = false,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            MinHeight = 0,
            Padding = new Thickness(6, 3, 6, 3),
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI"),
            FontSize = 12,
        };

        AutomationProperties.SetAutomationId(_input, "DialogTextInput");
        AutomationProperties.SetName(_input, label);

        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(_input);
        SetBody(body);

        Value = initialText;
    }

    /// <summary>The text after a successful primary action.</summary>
    public string Value { get; private set; }

    protected override void OnDialogLoaded()
    {
        _input.Focus(FocusState.Programmatic);
        _input.SelectAll();
    }

    protected override bool TryAccept()
    {
        string text = _input.Text ?? string.Empty;
        string? error = _validate?.Invoke(text);
        if (error is not null)
        {
            ShowValidation(error);
            _input.Focus(FocusState.Programmatic);
            return false;
        }

        Value = text;
        return true;
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Bwc.UI.Dialogs;

/// <summary>
/// Shared layout for the typed value editors. The concrete editor owns parsing and the typed
/// result; this base only supplies the label, input, hint, and modal behavior.
/// </summary>
public abstract class ValueEditorDialog : ModalDialogWindow
{
    protected ValueEditorDialog(string title, string label)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentException.ThrowIfNullOrEmpty(label);

        Title = title;
        SetHeading(title);
        PrimaryText = "OK";
        SecondaryText = "Cancel";

        Input = new TextBox
        {
            AcceptsReturn = false,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            MinHeight = 0,
            Padding = new Thickness(6, 3, 6, 3),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
        };

        AutomationProperties.SetAutomationId(Input, "ValueEditorInput");
        AutomationProperties.SetName(Input, label);

        HintText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(Input);
        body.Children.Add(HintText);
        SetBody(body);
    }

    protected TextBox Input { get; }

    protected TextBlock HintText { get; }

    protected void SetHint(string? text, bool warning = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            HintText.Text = string.Empty;
            HintText.Visibility = Visibility.Collapsed;
            return;
        }

        HintText.Text = text;
        HintText.Visibility = Visibility.Visible;

        string key = warning ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush";
        if (Application.Current?.Resources is { } resources
            && resources.TryGetValue(key, out object? value)
            && value is Brush brush)
        {
            HintText.Foreground = brush;
        }
    }

    protected override void OnDialogLoaded()
    {
        Input.Focus(FocusState.Programmatic);
        Input.SelectAll();
    }
}

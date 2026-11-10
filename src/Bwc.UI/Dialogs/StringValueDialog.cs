using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.UI.Dialogs;

/// <summary>
/// String and expandable string editor. String uses one line; expandable string uses a
/// multi-line box because environment variable text can be long. The caller encodes the value.
/// </summary>
public sealed class StringValueDialog : ValueEditorDialog
{
    public StringValueDialog(string title, bool multiline, string initialText = "")
        : base(title, "Value data")
    {
        Input.Text = initialText;
        Input.AcceptsReturn = multiline;
        Input.TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap;
        Input.Height = multiline ? 150 : double.NaN;
        SubmitsOnEnter = !multiline;
        SetPreferredSize(multiline ? 470 : 440, multiline ? 330 : 210);
        SetHint(multiline
            ? "Enter the expandable string. Environment variables such as %SystemRoot% are expanded when read."
            : "Enter the string value. An empty string is saved as an empty REG_SZ value.");
    }

    /// <summary>The entered text after a successful primary action.</summary>
    public string Value { get; private set; } = string.Empty;

    protected override bool TryAccept()
    {
        Value = Input.Text ?? string.Empty;
        return true;
    }
}

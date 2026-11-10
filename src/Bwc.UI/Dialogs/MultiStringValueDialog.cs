using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.UI.Dialogs;

/// <summary>
/// Multi-string editor. One string per line. Empty lines produce empty strings and show a warning
/// without blocking the save, matching the classic editor.
/// </summary>
public sealed class MultiStringValueDialog : ValueEditorDialog
{
    public MultiStringValueDialog(string title, string initialText = "")
        : base(title, "Value data")
    {
        Input.Text = initialText;
        Input.AcceptsReturn = true;
        Input.TextWrapping = TextWrapping.Wrap;
        Input.Height = 170;
        SubmitsOnEnter = false;
        SetPreferredSize(470, 350);
        Input.TextChanged += OnTextChanged;
        UpdateHint();
    }

    /// <summary>The strings after a successful primary action.</summary>
    public IReadOnlyList<string> Value { get; private set; } = Array.Empty<string>();

    protected override bool TryAccept()
    {
        Value = ValueTextParser.SplitLines(Input.Text);
        return true;
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateHint();
        ShowValidation(null);
    }

    private void UpdateHint()
    {
        string[] lines = ValueTextParser.SplitLines(Input.Text);
        if (lines.Length == 0)
        {
            SetHint("No strings. An empty list is saved.");
            return;
        }

        bool hasEmpty = false;
        foreach (string line in lines)
        {
            if (line.Length == 0)
            {
                hasEmpty = true;
                break;
            }
        }

        if (hasEmpty)
        {
            SetHint(
                "One or more lines are empty. Empty strings will be saved as part of the multi-string value.",
                warning: true);
            return;
        }

        SetHint($"{lines.Length} string(s).");
    }
}

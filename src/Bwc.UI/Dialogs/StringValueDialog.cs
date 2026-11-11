using Microsoft.UI.Xaml.Controls;

namespace Bwc.UI.Dialogs;

/// <summary>
/// Edit String and Edit Expandable String. Classic regedit edits REG_EXPAND_SZ with the same Edit String
/// dialog as REG_SZ, so both use the Edit String template. The caller encodes the value. The value name is
/// read-only. <paramref name="multiline"/> is kept for existing callers and does not change the template.
/// </summary>
public sealed class StringValueDialog : ClassicDialog
{
    private readonly TextBox _input;

    public StringValueDialog(string title, bool multiline, string initialText = "", string valueName = "")
        : base(ClassicDialogTemplates.EditString(title))
    {
        _input = RequireControl<TextBox>("ValueEditorInput");
        _input.Text = initialText ?? string.Empty;
        RequireControl<TextBox>("ValueNameInput").Text = valueName ?? string.Empty;
    }

    /// <summary>The entered text after a successful primary action.</summary>
    public string Value { get; private set; } = string.Empty;

    protected override bool TryAccept()
    {
        Value = _input.Text ?? string.Empty;
        return true;
    }

    /// <summary>Selects the whole value and focuses it, as the classic dialog does when it opens.</summary>
    protected override void OnDialogLoaded()
    {
        _ = _input.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        _input.SelectAll();
    }
}

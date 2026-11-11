using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace Bwc.UI.Dialogs;

/// <summary>
/// Edit Multi-String (RT_DIALOG 114). One string per line. OK with empty lines shows the classic
/// warning (string 116 for one empty string, 117 for more), and the empty strings are removed
/// when the warning is answered. The warning has only OK, so Escape answers it the same way.
/// </summary>
public sealed class MultiStringValueDialog : ClassicDialog
{
    /// <summary>Oracle string 116: one empty string.</summary>
    private const string OneEmptyStringWarning =
        "Data of type REG_MULTI_SZ cannot contain empty strings.\r\nRegistry Editor will remove the empty string found.";

    /// <summary>Oracle string 117: more than one empty string.</summary>
    private const string ManyEmptyStringsWarning =
        "Data of type REG_MULTI_SZ cannot contain empty strings.\r\nRegistry Editor will remove all empty strings found.";

    private readonly TextBox _input;
    private bool _emptyStringsRemoved;

    /// <param name="title">The caption, from the caller's resource map.</param>
    /// <param name="initialText">The lines of the value, one string per line.</param>
    /// <param name="valueName">The value name shown read-only. Empty when the caller does not supply it.</param>
    public MultiStringValueDialog(string title, string initialText = "", string valueName = "")
        : base(ClassicDialogTemplates.EditMultiString(title))
    {
        _input = RequireControl<TextBox>("ValueEditorInput");
        _input.Text = initialText ?? string.Empty;
        RequireControl<TextBox>("ValueNameInput").Text = valueName ?? string.Empty;
    }

    /// <summary>The strings after a successful primary action. Empty strings are removed after the warning.</summary>
    public IReadOnlyList<string> Value { get; private set; } = Array.Empty<string>();

    protected override bool TryAccept()
    {
        string[] lines = ValueTextParser.SplitLines(_input.Text);
        int empty = lines.Count(line => line.Length == 0);
        if (empty > 0 && !_emptyStringsRemoved)
        {
            _ = ConfirmEmptyStringsAsync(empty);
            return false;
        }

        Value = _emptyStringsRemoved ? lines.Where(line => line.Length > 0).ToArray() : lines;
        return true;
    }

    /// <summary>Focuses the value, as the classic dialog does when it opens.</summary>
    protected override void OnDialogLoaded()
    {
        _ = _input.Focus(FocusState.Programmatic);
    }

    private async Task ConfirmEmptyStringsAsync(int empty)
    {
        string text = empty == 1 ? OneEmptyStringWarning : ManyEmptyStringsWarning;
        var warning = new ClassicMessageBox(
            "Registry Editor",
            text,
            ClassicMessageIcon.Warning,
            ClassicMessageButtons.Ok);

        // The warning is owned by this dialog, so this dialog stays disabled until the box closes.
        _ = await warning.ShowAsync(WindowNative.GetWindowHandle(this));
        _emptyStringsRemoved = true;
        Accept();
    }
}

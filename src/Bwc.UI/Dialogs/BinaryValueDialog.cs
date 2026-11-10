using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.UI.Dialogs;

/// <summary>
/// Binary editor. Hex bytes may be separated by spaces, tabs, commas, or line breaks. An odd digit
/// count or a non-hex character blocks the save; empty input writes a zero-length value.
/// </summary>
public sealed class BinaryValueDialog : ValueEditorDialog
{
    public BinaryValueDialog(string title, string initialText = "")
        : base(title, "Value data")
    {
        Input.Text = initialText;
        Input.AcceptsReturn = true;
        Input.TextWrapping = TextWrapping.Wrap;
        Input.Height = 170;
        SubmitsOnEnter = false;
        SetPreferredSize(500, 340);
        Input.TextChanged += OnTextChanged;
        UpdateHint();
    }

    /// <summary>The bytes after a successful primary action.</summary>
    public byte[] Value { get; private set; } = Array.Empty<byte>();

    protected override bool TryAccept()
    {
        if (!ValueTextParser.TryParseBinary(Input.Text, out byte[] value, out string? error))
        {
            ShowValidation(error);
            return false;
        }

        Value = value;
        return true;
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateHint();
        ShowValidation(null);
    }

    private void UpdateHint()
    {
        if (ValueTextParser.TryParseBinary(Input.Text, out byte[] value, out _))
        {
            SetHint($"{value.Length} byte(s).");
            return;
        }

        SetHint("Enter hexadecimal bytes separated by spaces or commas.");
    }
}

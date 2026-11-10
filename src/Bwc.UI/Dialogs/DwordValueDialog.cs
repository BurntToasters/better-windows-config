namespace Bwc.UI.Dialogs;

/// <summary>DWORD editor. Accepts decimal digits or a 0x hexadecimal prefix.</summary>
public sealed class DwordValueDialog : ValueEditorDialog
{
    public DwordValueDialog(string title, string initialText = "0")
        : base(title, "Value data")
    {
        Input.Text = initialText;
        SetPreferredSize(440, 230);
        SetHint("Enter a decimal number or a hexadecimal number with the 0x prefix. The value is 32 bits.");
    }

    /// <summary>The parsed value after a successful primary action.</summary>
    public uint Value { get; private set; }

    protected override bool TryAccept()
    {
        if (!ValueTextParser.TryParseDword(Input.Text, out uint value, out string? error))
        {
            ShowValidation(error);
            return false;
        }

        Value = value;
        return true;
    }
}

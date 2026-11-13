namespace Bwc.UI.Dialogs;

/// <summary>
/// Edit DWORD (32-bit) Value. Hexadecimal is selected by default. Decimal and hexadecimal are the
/// two bases, and switching converts the shown value. Accepts 0 to FFFFFFFF in the current base.
/// </summary>
public sealed class DwordValueDialog : ClassicNumberDialog
{
    public DwordValueDialog(string title, string initialText = "0", string valueName = "")
        : base(ClassicDialogTemplates.EditDword(title), 32, initialText, valueName)
    {
    }

    /// <summary>The parsed value after a successful primary action.</summary>
    public uint Value { get; private set; }

    protected override bool TryAccept()
    {
        if (!base.TryAccept())
        {
            return false;
        }

        Value = (uint)AcceptedValue;
        return true;
    }
}

namespace Bwc.UI.Dialogs;

/// <summary>
/// Edit QWORD (64-bit) Value. Hexadecimal is selected by default. Decimal and hexadecimal are the
/// two bases, and switching converts the shown value. Accepts 0 to FFFFFFFFFFFFFFFF in the current base.
/// </summary>
public sealed class QwordValueDialog : ClassicNumberDialog
{
    public QwordValueDialog(string title, string initialText = "0", string valueName = "")
        : base(ClassicDialogTemplates.EditQword(title), 64, initialText, valueName)
    {
    }

    /// <summary>The parsed value after a successful primary action.</summary>
    public ulong Value { get; private set; }

    protected override bool TryAccept()
    {
        if (!base.TryAccept())
        {
            return false;
        }

        Value = AcceptedValue;
        return true;
    }
}

namespace Bwc.UI.Dialogs;

/// <summary>
/// A yes/no confirmation, shown as a classic message box with the question icon. The primary
/// action returns true. Yes is the default button.
/// </summary>
public sealed class ConfirmDialog : ClassicMessageBox
{
    public ConfirmDialog(
        string title,
        string message,
        string primaryText = "Yes",
        string secondaryText = "No")
        : base(
            title,
            message,
            ClassicMessageIcon.Question,
            ClassicMessageButtons.YesNo,
            primaryText,
            secondaryText)
    {
    }
}

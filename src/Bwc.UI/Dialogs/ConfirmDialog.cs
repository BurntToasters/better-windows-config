namespace Bwc.UI.Dialogs;

/// <summary>A yes/no confirmation. The primary action returns true.</summary>
public sealed class ConfirmDialog : ModalDialogWindow
{
    public ConfirmDialog(
        string title,
        string message,
        string primaryText = "Yes",
        string secondaryText = "No")
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentNullException.ThrowIfNull(message);

        Title = title;
        SetHeading(title);
        SetMessage(message);
        PrimaryText = primaryText;
        SecondaryText = secondaryText;
        SetPreferredSize(470, 230);
    }

    protected override void OnDialogLoaded()
    {
        FocusPrimary();
    }
}

namespace Bwc.Regedit.Commands;

/// <summary>
/// The edit commands the registry view implements. The menu table receives this host instead of
/// capturing controls, so command state and execution stay in one place.
/// </summary>
public interface IRegeditEditCommands
{
    void NewKey();

    void NewStringValue();

    void NewExpandableStringValue();

    void NewBinaryValue();

    void NewDwordValue();

    void NewQwordValue();

    void NewMultiStringValue();

    void DeleteSelection();

    void RenameSelection();

    /// <summary>Edit &gt; Modify... for the single selected value. Also used for Enter and double-click.</summary>
    void ModifySelectedValue();

    /// <summary>Edit &gt; Modify Binary Data... for the single selected value.</summary>
    void ModifyBinaryData();

    /// <summary>Edit &gt; Copy Key Name for the selected key.</summary>
    void CopyKeyName();

    /// <summary>View &gt; Refresh, the same action as F5.</summary>
    void Refresh();

    void Find();

    void FindNext();
}

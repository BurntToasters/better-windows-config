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

    void Find();

    void FindNext();
}

using Bwc.UI.Dialogs;

namespace Bwc.Gpedit.Templates;

/// <summary>
/// The file picker of Add in the Add/Remove Templates dialog: the shared common item open dialog
/// (<see cref="CommonFileDialogs.ShowOpen"/>), the same dialog the Registry Editor uses, with a Template files (*.admx)
/// filter and an All files filter. The dialog disables its owner while it is open. A cancelled dialog gives null and
/// changes nothing (docs/failure-modes/gpedit-templates.md, section 4).
/// </summary>
internal static class TemplateFilePicker
{
    /// <summary>Shows the picker owned by the window and returns the chosen path, or null when it was cancelled.</summary>
    /// <param name="owner">The window that owns the picker.</param>
    internal static string? Pick(nint owner) =>
        CommonFileDialogs.ShowOpen(
            owner,
            GpeditStrings.Templates("TemplatesPickerTitle"),
            [
                (GpeditStrings.Templates("TemplatesPickerFilterAdmx"), "*.admx"),
                (GpeditStrings.Templates("TemplatesPickerFilterAll"), "*.*"),
            ]);
}

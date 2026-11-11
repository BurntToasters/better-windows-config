namespace Bwc.UI.Dialogs;

/// <summary>
/// Classic dialog templates transcribed from the regedit oracle
/// (docs/parity/oracle/regedit-resources.md). Numbers are dialog units. The control order,
/// mnemonics, ids, and tab stops are copied from the template, and the tab order is that order.
/// Only the rectangles and short labels are copied; no Microsoft resource bytes are included.
/// </summary>
public static class ClassicDialogTemplates
{
    private const string TemplateFont = ClassicDialogMetrics.TemplateFontFace;

    /// <summary>Edit String (RT_DIALOG 102): one-line value data, read-only value name. 252 x 84 DLU.</summary>
    public static ClassicDialogDefinition EditString(string caption) =>
        new(
            caption,
            "RT_DIALOG 102 (en-US regedit.exe.mui)",
            252,
            84,
            [
                Label("ValueDataLabel", "&Value data:", 6, 35, 161, 8),
                Edit("ValueEditorInput", 1001, 6, 46, 240, 12),
                Button("DialogPrimary", 1, "OK", 142, 64, 50, 14, isDefault: true),
                Button("DialogSecondary", 2, "Cancel", 196, 64, 50, 14),
                Label("ValueNameLabel", "Value &name:", 6, 6, 134, 8),
                Edit("ValueNameInput", 1000, 6, 17, 240, 12, readOnly: true),
            ])
        {
            FontFace = TemplateFont,
            FontPointSize = 8,
        };

    /// <summary>Edit DWORD (32-bit) Value (RT_DIALOG 111): value data with the Base group. 219 x 103 DLU.</summary>
    public static ClassicDialogDefinition EditDword(string caption) => NumberTemplate(caption, "RT_DIALOG 111 (en-US regedit.exe.mui)");

    /// <summary>Edit QWORD (64-bit) Value (RT_DIALOG 117): same geometry as the DWORD template. 219 x 103 DLU.</summary>
    public static ClassicDialogDefinition EditQword(string caption) => NumberTemplate(caption, "RT_DIALOG 117 (en-US regedit.exe.mui)");

    private static ClassicDialogDefinition NumberTemplate(string caption, string source) =>
        new(
            caption,
            source,
            219,
            103,
            [
                Label("ValueDataLabel", "&Value data:", 6, 35, 61, 8),
                Edit("ValueEditorInput", 1001, 6, 46, 92, 12),
                Group("BaseGroup", "Base", 105, 35, 108, 39),
                Radio("BaseHex", 1002, "&Hexadecimal", 115, 46, 60, 10, tabStop: true, isChecked: true),
                Radio("BaseDec", 1003, "&Decimal", 115, 59, 60, 10, tabStop: false, isChecked: false),
                Button("DialogPrimary", 1, "OK", 108, 81, 50, 14, isDefault: true),
                Button("DialogSecondary", 2, "Cancel", 163, 81, 50, 14),
                Label("ValueNameLabel", "Value &name:", 6, 6, 69, 8),
                Edit("ValueNameInput", 1000, 6, 17, 207, 12, readOnly: true),
            ])
        {
            FontFace = TemplateFont,
            FontPointSize = 8,
        };

    private static ClassicControlDefinition Label(string automationId, string text, int x, int y, int cx, int cy) =>
        new(ClassicControlKind.Label, 0, automationId, text, x, y, cx, cy);

    private static ClassicControlDefinition Edit(
        string automationId,
        int id,
        int x,
        int y,
        int cx,
        int cy,
        bool readOnly = false,
        bool multiline = false) =>
        new(ClassicControlKind.Edit, id, automationId, string.Empty, x, y, cx, cy)
        {
            TabStop = true,
            ReadOnly = readOnly,
            Multiline = multiline,
        };

    private static ClassicControlDefinition Button(
        string automationId,
        int id,
        string text,
        int x,
        int y,
        int cx,
        int cy,
        bool isDefault = false) =>
        new(ClassicControlKind.Button, id, automationId, text, x, y, cx, cy)
        {
            TabStop = true,
            IsDefault = isDefault,
        };

    private static ClassicControlDefinition Group(string automationId, string text, int x, int y, int cx, int cy) =>
        new(ClassicControlKind.GroupBox, 0, automationId, text, x, y, cx, cy);

    private static ClassicControlDefinition Radio(
        string automationId,
        int id,
        string text,
        int x,
        int y,
        int cx,
        int cy,
        bool tabStop,
        bool isChecked) =>
        new(ClassicControlKind.RadioButton, id, automationId, text, x, y, cx, cy)
        {
            TabStop = tabStop,
            Checked = isChecked,
        };
}

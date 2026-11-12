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

    /// <summary>Edit Multi-String (RT_DIALOG 114): one string per line, multi-line edit. 232 x 178 DLU.</summary>
    public static ClassicDialogDefinition EditMultiString(string caption) =>
        new(
            caption,
            "RT_DIALOG 114 (en-US regedit.exe.mui)",
            232,
            178,
            [
                Label("ValueDataLabel", "&Value data:", 6, 33, 220, 8),
                MultiLineEdit("ValueEditorInput", 1001, 6, 44, 220, 110),
                Button("DialogPrimary", 1, "OK", 120, 158, 50, 14, isDefault: true),
                Button("DialogSecondary", 2, "Cancel", 176, 158, 50, 14),
                Label("ValueNameLabel", "Value &name:", 6, 6, 220, 8),
                Edit("ValueNameInput", 1000, 6, 17, 220, 12, readOnly: true),
            ])
        {
            FontFace = TemplateFont,
            FontPointSize = 8,
        };

    /// <summary>Edit Binary Value (RT_DIALOG 103): the HEX control for the value data. 232 x 178 DLU.</summary>
    public static ClassicDialogDefinition EditBinary(string caption) =>
        new(
            caption,
            "RT_DIALOG 103 (en-US regedit.exe.mui)",
            232,
            178,
            [
                Label("ValueDataLabel", "&Value data:", 6, 33, 220, 8),
                HexControl("HexEditor", 1001, 6, 44, 220, 110),
                Button("DialogPrimary", 1, "OK", 120, 158, 50, 14, isDefault: true),
                Button("DialogSecondary", 2, "Cancel", 176, 158, 50, 14),
                Label("ValueNameLabel", "Value &name:", 6, 6, 220, 8),
                Edit("ValueNameInput", 1000, 6, 17, 220, 12, readOnly: true),
            ])
        {
            FontFace = TemplateFont,
            FontPointSize = 8,
        };

    /// <summary>
    /// Find (RT_DIALOG 106): the search text, Look at (Keys, Values, Data), the two options, and the
    /// Find Next and Cancel buttons. 264 x 96 DLU. The texts come from the caller's resource map.
    /// </summary>
    public static ClassicDialogDefinition Find(
        string caption,
        string findWhat,
        string lookAt,
        string keys,
        string values,
        string data,
        string matchWholeString,
        string searchInSubtree,
        string findNext,
        string cancel,
        bool subtreeChecked) =>
        new(
            caption,
            "RT_DIALOG 106 (en-US regedit.exe.mui)",
            264,
            96,
            [
                Label("FindWhatLabel", findWhat, 6, 8, 40, 8),
                Edit("FindText", 1150, 50, 6, 150, 14),
                CheckBox("FindKeys", 1152, keys, 12, 38, 35, 10, isChecked: true),
                CheckBox("FindValues", 1153, values, 12, 50, 35, 10, isChecked: true),
                CheckBox("FindData", 1154, data, 12, 62, 35, 10, isChecked: true),
                CheckBox("FindWholeString", 1151, matchWholeString, 6, 80, 109, 10, isChecked: false),
                CheckBox("FindSubtree", 1161, searchInSubtree, 120, 80, 109, 10, isChecked: subtreeChecked),
                Button("DialogPrimary", 1, findNext, 208, 6, 50, 14, isDefault: true),
                Button("DialogSecondary", 2, cancel, 208, 24, 50, 14),
                Group("FindLookAt", lookAt, 6, 26, 194, 50),
            ])
        {
            FontFace = TemplateFont,
            FontPointSize = 8,
        };

    /// <summary>
    /// Find progress (RT_DIALOG 109, oracle DLG-017/018): the searching line and the Cancel button.
    /// 170 x 64 DLU. The template's icon static is drawn by <see cref="FindProgressDialog"/>, because
    /// the classic icon is a stock icon and this template model carries text controls only.
    /// </summary>
    public static ClassicDialogDefinition FindProgress(
        string caption,
        string searchingText,
        string cancelText) =>
        new(
            caption,
            "RT_DIALOG 109 (en-US regedit.exe.mui)",
            170,
            64,
            [
                Button("DialogSecondary", 2, cancelText, 114, 43, 50, 14, isDefault: true),
                Label("FindProgressText", searchingText, 47, 12, 116, 10),
            ])
        {
            FontFace = TemplateFont,
            FontPointSize = 8,
        };

    /// <summary>
    /// Remove Favorites (RT_DIALOG 113): a multi-select, sorted list with OK and Cancel at the right.
    /// 194 x 103 DLU. The list label is the caller's text.
    /// </summary>
    public static ClassicDialogDefinition RemoveFavorites(string caption, string label, string primaryText, string secondaryText) =>
        new(
            caption,
            "RT_DIALOG 113 (en-US regedit.exe.mui)",
            194,
            103,
            [
                Button("DialogPrimary", 1, primaryText, 137, 7, 50, 14, isDefault: true),
                Button("DialogSecondary", 2, secondaryText, 137, 28, 50, 14),
                ListBox("FavoritesList", 1191, 7, 23, 121, 72, multiSelect: true),
                Label("FavoritesLabel", label, 7, 7, 93, 8),
            ])
        {
            FontFace = TemplateFont,
            FontPointSize = 8,
        };

    /// <summary>
    /// The one-line prompt for a key or value name (RT_DIALOG 104, Load Hive). 252 x 53 DLU. The
    /// label and the caption are the caller's text.
    /// </summary>
    public static ClassicDialogDefinition TextPrompt(string caption, string label) =>
        new(
            caption,
            "RT_DIALOG 104 (en-US regedit.exe.mui)",
            252,
            53,
            [
                Label("PromptLabel", label, 6, 6, 134, 8),
                Edit("DialogTextInput", 1001, 6, 17, 240, 12),
                Button("DialogPrimary", 1, "OK", 142, 35, 50, 14, isDefault: true),
                Button("DialogSecondary", 2, "Cancel", 196, 35, 50, 14),
            ])
        {
            FontFace = TemplateFont,
            FontPointSize = 8,
        };

    /// <summary>
    /// Display Binary Data (RT_DIALOG 2021, the template with Value Type): the value's bytes in a read-only
    /// list, the Byte, Word, Dword, and Qword formats, and the value type. 440 x 161 DLU. The caller's
    /// caption and OK text are used as given.
    /// </summary>
    public static ClassicDialogDefinition BinaryData(string caption, string okText) =>
        new(
            caption,
            "RT_DIALOG 2021 (en-US regedit.exe.mui)",
            440,
            161,
            [
                Label("DataLabel", "D&ata:", 4, 6, 20, 8),
                ListBox("HexViewerList", 2022, 4, 15, 433, 71, multiSelect: false),
                Group("FormatGroup", "Format", 4, 92, 212, 35),
                Radio("FormatByte", 2023, "&Byte", 24, 105, 30, 10, tabStop: false, isChecked: true),
                Radio("FormatWord", 2024, "&Word", 64, 105, 35, 10, tabStop: false, isChecked: false),
                Radio("FormatDword", 2025, "&Dword", 109, 105, 39, 10, tabStop: false, isChecked: false),
                Radio("FormatQword", 2027, "&Qword", 158, 105, 39, 10, tabStop: false, isChecked: false),
                Button("DialogPrimary", 1, okText, 161, 137, 40, 14, isDefault: true),
                Label("ValueTypeLabel", "Value Type:", 255, 105, 44, 8),
                Label("ValueTypeText", string.Empty, 303, 105, 58, 8),
            ])
        {
            FontFace = TemplateFont,
            FontPointSize = 8,
        };

    private static ClassicControlDefinition MultiLineEdit(string automationId, int id, int x, int y, int cx, int cy) =>
        Edit(automationId, id, x, y, cx, cy, multiline: true);

    private static ClassicControlDefinition HexControl(string automationId, int id, int x, int y, int cx, int cy) =>
        new(ClassicControlKind.HexEditor, id, automationId, "Value data", x, y, cx, cy)
        {
            TabStop = true,
        };

    private static ClassicControlDefinition ListBox(string automationId, int id, int x, int y, int cx, int cy, bool multiSelect) =>
        new(ClassicControlKind.ListBox, id, automationId, string.Empty, x, y, cx, cy)
        {
            TabStop = true,
            MultiSelect = multiSelect,
        };

    private static ClassicControlDefinition CheckBox(
        string automationId,
        int id,
        string text,
        int x,
        int y,
        int cx,
        int cy,
        bool isChecked) =>
        new(ClassicControlKind.CheckBox, id, automationId, text, x, y, cx, cy)
        {
            TabStop = true,
            Checked = isChecked,
        };

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

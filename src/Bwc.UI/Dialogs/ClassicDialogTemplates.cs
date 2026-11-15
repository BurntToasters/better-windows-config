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

    /// <summary>Edit QWORD (64-bit) Value (RT_DIALOG 117): same geometry as the DWORD template. 219 x 103
    /// DLU.</summary>
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
    /// Print progress (RT_DIALOG 105): the text "Printing the registry..." and a Cancel button, the default button.
    /// 154 x 49 DLU. The dialog is shown during the job, and Cancel stops it through the abort proc.
    /// </summary>
    public static ClassicDialogDefinition PrintProgress(string caption, string printingText, string cancelText) =>
        new(
            caption,
            "RT_DIALOG 105 (en-US regedit.exe.mui)",
            154,
            49,
            [
                Button("DialogSecondary", 2, cancelText, 51, 28, 50, 14, isDefault: true),
                Label("PrintProgressText", printingText, 29, 10, 93, 8),
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
    /// Add to Favorites (RT_DIALOG 112, DLG-024): one static label, the name edit, OK, and Cancel. 191 x 65 DLU.
    /// Positions are the measured classic rectangles at 192 DPI, converted to DLU (3 device pixels per x DLU, 25/8
    /// per y DLU). The caption and label are the caller's text.
    /// </summary>
    public static ClassicDialogDefinition AddFavorite(string caption, string label) =>
        new(
            caption,
            "RT_DIALOG 112 (en-US regedit.exe.mui)",
            191,
            65,
            [
                Label("FavoriteNameLabel", label, 7, 15, 75, 8),
                Edit("DialogTextInput", 1190, 7, 27, 120, 12),
                Button("DialogPrimary", 1, "OK", 133, 7, 50, 14, isDefault: true),
                Button("DialogSecondary", 2, "Cancel", 133, 24, 50, 14),
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

    /// <summary>Resource Lists (RT_DIALOG 116): one column list of full descriptors and Display... 170 x
    /// 120 DLU.</summary>
    public static ClassicDialogDefinition ResourceLists(string caption, string okText, string displayText) =>
        new(
            caption,
            "RT_DIALOG 116 (en-US regedit.exe.mui)",
            170,
            120,
            [
                ColumnList("ResourceListColumns", 1205, 10, 10, 150, 76, "Bus Number", "Interface Type"),
                Button("DialogPrimary", 1, okText, 37, 96, 40, 14),
                Button("ResourceDisplay", 1203, displayText, 93, 96, 40, 14),
            ])
        {
            FontFace = TemplateFont,
            FontPointSize = 8,
        };

    /// <summary>
    /// Resources (RT_DIALOG 1305): one full resource descriptor with five column lists (DMA, Interrupt, Memory, Port,
    /// Device specific), the Share Disposition frame, and the Data... button. 281 x 308 DLU.
    /// </summary>
    public static ClassicDialogDefinition ResourceDescriptor(string caption, string okText, string dataText) =>
        new(
            caption,
            "RT_DIALOG 1305 (en-US regedit.exe.mui)",
            281,
            308,
            [
                ColumnList("DmaColumns", 1306, 10, 15, 261, 36, "Channel", "Port"),
                ColumnList("InterruptColumns", 1307, 10, 63, 261, 36, "Vector", "Level", "Affinity", "Type"),
                ColumnList("MemoryColumns", 1308, 10, 111, 261, 36, "Physical Address", "Length", "Access"),
                ColumnList("PortColumns", 1309, 10, 159, 261, 36, "Physical Address", "Length", "Type"),
                ColumnList("DeviceSpecificColumns", 1304, 10, 207, 261, 36, "Reserved1", "Reserved2", "Data Size"),
                StaticText("DmaLabel", 65535, "DMA:", 10, 5, 20, 8),
                StaticText("InterruptLabel", 65535, "Interrupt:", 10, 53, 35, 8),
                StaticText("MemoryLabel", 65535, "Memory:", 10, 101, 28, 8),
                StaticText("PortLabel", 65535, "Port:", 10, 149, 17, 8),
                StaticText("DeviceSpecificLabel", 65535, "Device Specific Data:", 10, 197, 74, 8),
                Group("ShareDispositionGroup", "Share Disposition", 10, 244, 125, 39),
                StaticText("ShareUndetermined", 1310, "Undetermined", 18, 255, 46, 8, disabled: true),
                StaticText("ShareShared", 1311, "Shared", 18, 265, 24, 8, disabled: true),
                StaticText("ShareDeviceExclusive", 1312, "Device Exclusive", 72, 255, 58, 8, disabled: true),
                StaticText("ShareDriverExclusive", 1313, "Driver Exclusive", 72, 265, 54, 8, disabled: true),
                StaticText("InterfaceTypeLabel", 65535, "Interface Type:", 151, 245, 51, 8),
                StaticText("BusNumberLabel", 65535, "Bus Number:", 151, 255, 51, 8),
                StaticText("VersionLabel", 65535, "Version:", 151, 265, 51, 8),
                StaticText("RevisionLabel", 65535, "Revision:", 151, 275, 51, 8),
                StaticText("InterfaceTypeValue", 1303, string.Empty, 205, 245, 66, 8),
                StaticText("BusNumberValue", 1300, string.Empty, 205, 255, 66, 8),
                StaticText("VersionValue", 1301, string.Empty, 205, 265, 66, 8),
                StaticText("RevisionValue", 1302, string.Empty, 205, 275, 66, 8),
                Button("DialogPrimary", 1, okText, 92, 288, 40, 14, isDefault: true),
                Button("ResourceData", 1204, dataText, 148, 288, 40, 14),
            ])
        {
            FontFace = TemplateFont,
            FontPointSize = 8,
        };

    /// <summary>Requirements List (RT_DIALOG 1501): the requirement column list, the bus fields, and
    /// Display... 220 x 135 DLU.</summary>
    public static ClassicDialogDefinition ResourceRequirements(string caption, string okText, string displayText) =>
        new(
            caption,
            "RT_DIALOG 1501 (en-US regedit.exe.mui)",
            220,
            135,
            [
                ColumnList("RequirementColumns", 1502, 10, 10, 200, 66, "Alternative List", "Resource List", "Descriptor", "Device Type"),
                StaticText("InterfaceTypeLabel", 65535, "Interface Type:", 10, 80, 51, 8),
                StaticText("BusNumberLabel", 65535, "Bus Number:", 10, 90, 51, 8),
                StaticText("SlotNumberLabel", 65535, "Slot Number:", 10, 100, 51, 8),
                StaticText("InterfaceTypeValue", 1503, string.Empty, 64, 80, 66, 8),
                StaticText("BusNumberValue", 1504, string.Empty, 64, 90, 66, 8),
                StaticText("SlotNumberValue", 1505, string.Empty, 64, 100, 66, 8),
                Button("DialogPrimary", 1, okText, 62, 115, 40, 14),
                Button("RequirementDisplay", 1506, displayText, 118, 115, 40, 14),
            ])
        {
            FontFace = TemplateFont,
            FontPointSize = 8,
        };

    /// <summary>
    /// One requirement descriptor (RT_DIALOG 1601 Memory, 1700 Port, 1800 Interrupt, 1900 DMA). The field
    /// set comes from the
    /// kind. The Preferred and Alternative captions are template-disabled, so they are shown disabled. 158 x 135 DLU.
    /// </summary>
    public static ClassicDialogDefinition ResourceDetail(string caption, ResourceDetailKind kind, string okText) =>
        new(
            caption,
            DetailSource(kind),
            158,
            135,
            DetailControls(kind, okText))
        {
            FontFace = TemplateFont,
            FontPointSize = 8,
        };

    private static string DetailSource(ResourceDetailKind kind) => kind switch
    {
        ResourceDetailKind.Memory => "RT_DIALOG 1601 (en-US regedit.exe.mui)",
        ResourceDetailKind.Port => "RT_DIALOG 1700 (en-US regedit.exe.mui)",
        ResourceDetailKind.Interrupt => "RT_DIALOG 1800 (en-US regedit.exe.mui)",
        _ => "RT_DIALOG 1900 (en-US regedit.exe.mui)",
    };

    private static ClassicControlDefinition[] DetailControls(ResourceDetailKind kind, string okText)
    {
        var common = new List<ClassicControlDefinition>
        {
            Button("DialogPrimary", 1, okText, 58, 117, 40, 14, isDefault: true),
            Group("OptionGroup", "Option", 10, 72, 83, 39),
            StaticText("PreferredText", 2001, "Preferred", 34, 96, 37, 8, disabled: true),
            StaticText("AlternativeText", 2002, "Alternative", 34, 86, 35, 8, disabled: true),
        };
        switch (kind)
        {
            case ResourceDetailKind.Memory:
                common.AddRange(
                [
                    StaticText("AccessLabel", 65535, "Access:", 10, 10, 61, 8),
                    StaticText("LengthLabel", 65535, "Length:", 10, 20, 61, 8),
                    StaticText("AlignmentLabel", 65535, "Alignment:", 10, 30, 61, 8),
                    StaticText("MinimumAddressLabel", 65535, "Minimum Address:", 10, 40, 61, 8),
                    StaticText("MaximumAddressLabel", 65535, "Maximum Address:", 10, 50, 61, 8),
                    StaticText("ShareDispositionLabel", 65535, "Share Disposition:", 10, 60, 61, 8),
                    StaticText("AccessValue", 1602, string.Empty, 73, 10, 74, 8),
                    StaticText("LengthValue", 1603, string.Empty, 73, 20, 74, 8),
                    StaticText("AlignmentValue", 1606, string.Empty, 73, 30, 74, 8),
                    StaticText("MinimumAddressValue", 1604, string.Empty, 73, 40, 74, 8),
                    StaticText("MaximumAddressValue", 1605, string.Empty, 73, 50, 74, 8),
                    StaticText("ShareDispositionValue", 2000, string.Empty, 73, 60, 74, 8),
                ]);
                break;
            case ResourceDetailKind.Port:
                common.AddRange(
                [
                    StaticText("TypeLabel", 65535, "Type:", 10, 10, 61, 8),
                    StaticText("LengthLabel", 65535, "Length:", 10, 20, 61, 8),
                    StaticText("AlignmentLabel", 65535, "Alignment:", 10, 30, 61, 8),
                    StaticText("MinimumAddressLabel", 65535, "Minimum Address:", 10, 40, 61, 8),
                    StaticText("MaximumAddressLabel", 65535, "Maximum Address:", 10, 50, 61, 8),
                    StaticText("ShareDispositionLabel", 65535, "Share Disposition:", 10, 60, 61, 8),
                    StaticText("TypeValue", 1701, string.Empty, 73, 10, 74, 8),
                    StaticText("LengthValue", 1703, string.Empty, 73, 20, 74, 8),
                    StaticText("AlignmentValue", 1706, string.Empty, 73, 30, 74, 8),
                    StaticText("MinimumAddressValue", 1704, string.Empty, 73, 40, 74, 8),
                    StaticText("MaximumAddressValue", 1705, string.Empty, 73, 50, 74, 8),
                    StaticText("ShareDispositionValue", 2000, string.Empty, 73, 60, 74, 8),
                ]);
                break;
            case ResourceDetailKind.Interrupt:
                common.AddRange(
                [
                    StaticText("TypeLabel", 65535, "Type:", 10, 10, 61, 8),
                    StaticText("MinimumVectorLabel", 65535, "Minimum Vector:", 10, 20, 61, 8),
                    StaticText("MaximumVectorLabel", 65535, "Maximum Vector:", 10, 30, 61, 8),
                    StaticText("ShareDispositionLabel", 65535, "Share Disposition:", 10, 40, 61, 8),
                    StaticText("TypeValue", 1801, string.Empty, 73, 10, 74, 8),
                    StaticText("MinimumVectorValue", 1803, string.Empty, 73, 20, 74, 8),
                    StaticText("MaximumVectorValue", 1804, string.Empty, 73, 30, 74, 8),
                    StaticText("ShareDispositionValue", 2000, string.Empty, 73, 40, 74, 8),
                ]);
                break;
            default:
                common.AddRange(
                [
                    StaticText("MinimumChannelLabel", 65535, "Minimum Channel:", 10, 10, 61, 8),
                    StaticText("MaximumChannelLabel", 65535, "Maximum Channel:", 10, 20, 61, 8),
                    StaticText("ShareDispositionLabel", 65535, "Share Disposition:", 10, 30, 61, 8),
                    StaticText("MinimumChannelValue", 1902, string.Empty, 73, 10, 74, 8),
                    StaticText("MaximumChannelValue", 1903, string.Empty, 73, 20, 74, 8),
                    StaticText("ShareDispositionValue", 2000, string.Empty, 73, 30, 74, 8),
                ]);
                break;
        }

        return common.ToArray();
    }

    private static ClassicControlDefinition StaticText(
        string automationId,
        int id,
        string text,
        int x,
        int y,
        int cx,
        int cy,
        bool disabled = false) =>
        new(ClassicControlKind.Label, id, automationId, text, x, y, cx, cy)
        {
            Disabled = disabled,
        };

    private static ClassicControlDefinition ColumnList(
        string automationId,
        int id,
        int x,
        int y,
        int cx,
        int cy,
        params string[] columns) =>
        new(ClassicControlKind.ColumnList, id, automationId, string.Empty, x, y, cx, cy)
        {
            TabStop = true,
            Columns = columns,
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

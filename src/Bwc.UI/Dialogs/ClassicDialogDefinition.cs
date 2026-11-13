namespace Bwc.UI.Dialogs;

/// <summary>
/// Control classes a classic template can declare. Each maps to a WinUI control that carries a
/// classic style from <c>ClassicDialogStyles.xaml</c>.
/// </summary>
public enum ClassicControlKind
{
    /// <summary>Static text. A mnemonic moves focus to the next tab stop, as WM_SYSCHAR does.</summary>
    Label,

    /// <summary>Push button. Click, Enter when default, Escape when cancel, or its mnemonic.</summary>
    Button,

    /// <summary>Single line or multi-line text box.</summary>
    Edit,

    /// <summary>Check box with a mnemonic.</summary>
    CheckBox,

    /// <summary>Radio button with a mnemonic. Buttons in one dialog form one group.</summary>
    RadioButton,

    /// <summary>Etched frame with a caption. Never a tab stop.</summary>
    GroupBox,

    /// <summary>List box. Items are added by the dialog after construction.</summary>
    ListBox,

    /// <summary>Combo box. Items are added by the dialog after construction.</summary>
    ComboBox,

    /// <summary>The classic HEX control (Edit Binary Value), a hex editor at the template size.</summary>
    HexEditor,
}

/// <summary>
/// One control of a classic template. Rectangles are in dialog units (DLU), exactly as the
/// template declares them, and are converted to pixels for the window DPI when the dialog shows.
/// </summary>
/// <param name="Kind">The control class.</param>
/// <param name="Id">The Win32 control id from the template (1 OK, 2 Cancel, and so on). 0 when there is none.</param>
/// <param name="AutomationId">A stable id for UI Automation and tests. It is unique within one dialog.</param>
/// <param name="Text">Visible text. An ampersand marks the mnemonic letter, as in Win32.</param>
/// <param name="X">Left edge in DLU, relative to the dialog client area.</param>
/// <param name="Y">Top edge in DLU.</param>
/// <param name="Cx">Width in DLU.</param>
/// <param name="Cy">Height in DLU.</param>
public sealed record ClassicControlDefinition(
    ClassicControlKind Kind,
    int Id,
    string AutomationId,
    string Text,
    int X,
    int Y,
    int Cx,
    int Cy)
{
    /// <summary>True when the control is in the Tab order (WS_TABSTOP in the template).</summary>
    public bool TabStop { get; init; }

    /// <summary>True for a read-only edit (ES_READONLY).</summary>
    public bool ReadOnly { get; init; }

    /// <summary>True for a multi-line edit (ES_MULTILINE). Enter inserts a line break.</summary>
    public bool Multiline { get; init; }

    /// <summary>True for the default push button (BS_DEFPUSHBUTTON). Enter invokes it.</summary>
    public bool IsDefault { get; init; }

    /// <summary>Initial checked state for a check box or radio button (BST_CHECKED).</summary>
    public bool Checked { get; init; }

    /// <summary>True when the control starts disabled (WS_DISABLED).</summary>
    public bool Disabled { get; init; }

    /// <summary>True for a list box that accepts several selections (LBS_MULTIPLESEL).</summary>
    public bool MultiSelect { get; init; }
}

/// <summary>
/// A classic dialog template: caption, client size in DLU, font, and controls in template order.
/// The tab order is the control order filtered by <see cref="ClassicControlDefinition.TabStop"/>.
/// </summary>
/// <param name="Caption">The title bar text. Classic dialogs have no heading inside the client area.</param>
/// <param name="SourceTemplate">Where the numbers came from, for example "RT_DIALOG 102 in regedit.exe.mui".</param>
/// <param name="WidthDlu">Client width in DLU.</param>
/// <param name="HeightDlu">Client height in DLU.</param>
/// <param name="Controls">Controls in template order.</param>
public sealed record ClassicDialogDefinition(
    string Caption,
    string SourceTemplate,
    int WidthDlu,
    int HeightDlu,
    IReadOnlyList<ClassicControlDefinition> Controls)
{
    /// <summary>
    /// Font face name. "MS Shell Dlg" is resolved through FontSubstitutes, which maps it to
    /// Microsoft Sans Serif on the target machine.
    /// </summary>
    public string FontFace { get; init; } = ClassicDialogMetrics.TemplateFontFace;

    /// <summary>Font size in points.</summary>
    public int FontPointSize { get; init; } = 8;
}

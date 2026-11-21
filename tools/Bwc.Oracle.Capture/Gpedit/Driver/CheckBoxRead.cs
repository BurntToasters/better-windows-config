using System.Windows.Automation;

namespace Bwc.Oracle.Capture.Gpedit.Driver;

// How the state of a check box is read.
internal enum CheckReader
{
    BmGetCheck,
    TogglePattern,
}

// What UI Automation says about a BUTTON window: whether its control type is CheckBox, whether it offers TogglePattern, the control type name.
internal readonly record struct CheckUia(bool IsCheckBoxType, bool HasToggle, string ControlType);

// The decision "is this child of the options panel a check box, and which reader gives its state", on plain values, so the self test covers
// it without classic. A Windows Forms CheckBox whose FlatStyle is not System is owner-drawn (BS_OWNERDRAW), and BM_GETCHECK does not report
// its state. Failure mode 35 in docs/failure-modes/oracle-gpedit-driver.md.
internal static class CheckBoxRead
{
    internal const uint BsOwnerDraw = 0xB;

    internal static bool IsButtonClass(string className) => className.Contains(".BUTTON.", StringComparison.OrdinalIgnoreCase);

    // BS_CHECKBOX, BS_AUTOCHECKBOX, BS_3STATE, BS_AUTO3STATE.
    internal static bool IsNativeCheckStyle(uint style) => (style & DriverNative.BsTypeMask) is 2 or 3 or 5 or 6;

    internal static bool IsOwnerDraw(uint style) => (style & DriverNative.BsTypeMask) == BsOwnerDraw;

    // True when the BUTTON is owner-drawn, so UI Automation must say what it is. A native style needs no UI Automation call.
    internal static bool NeedsUia(string className, uint style) => IsButtonClass(className) && IsOwnerDraw(style);

    // The reader for a check box, or null when the BUTTON is not one. A native check style is BM_GETCHECK. An owner-drawn BUTTON is a check
    // box only when UI Automation gave evidence (control type CheckBox or a toggle pattern); no answer from UI Automation is no evidence.
    internal static CheckReader? Judge(string className, uint style, CheckUia? uia)
    {
        if (!IsButtonClass(className))
        {
            return null;
        }

        if (IsNativeCheckStyle(style))
        {
            return CheckReader.BmGetCheck;
        }

        if (IsOwnerDraw(style) && uia is { } evidence && (evidence.IsCheckBoxType || evidence.HasToggle))
        {
            return CheckReader.TogglePattern;
        }

        return null;
    }

    // Only a hint for the evidence. The panel id of a Windows Forms check box starts with "checkBox"; this never decides.
    internal static bool PanelHint(string panelId) => panelId.StartsWith("checkBox", StringComparison.Ordinal);

    // Off 0, On 1, Indeterminate 2: the BM_GETCHECK numbers.
    internal static int ToCheckState(ToggleState state) => state switch
    {
        ToggleState.Off => 0,
        ToggleState.On => 1,
        _ => 2,
    };

    internal static string ViaName(CheckReader reader) => reader == CheckReader.TogglePattern ? "TogglePattern" : "BM_GETCHECK";
}

namespace Bwc.Oracle.Capture.Gpedit.Driver;

// What UI Automation says about a BUTTON window that may be a push button: whether its control type is Button, whether it offers InvokePattern,
// and whether it looks like a check box (control type CheckBox, or TogglePattern). The control type name is for the evidence.
internal readonly record struct PushUia(bool IsButtonType, bool HasInvoke, bool IsCheckBoxType, bool HasToggle, string ControlType);

// The decision "is this inner BUTTON of an option container the Show button of a list", on plain values, so the self test covers it without
// classic. A Windows Forms Button whose FlatStyle is not System is owner-drawn (BS_OWNERDRAW), like the check box of CheckBoxRead. Failure
// mode 36 in docs/failure-modes/oracle-gpedit-driver.md.
internal static class PushButtonRead
{
    // BS_PUSHBUTTON and BS_DEFPUSHBUTTON.
    internal static bool IsNativePushStyle(uint style) => (style & DriverNative.BsTypeMask) is 0 or 1;

    // True when the BUTTON is owner-drawn, so UI Automation must say what it is. A native style needs no UI Automation call.
    internal static bool NeedsUia(string className, uint style) => CheckBoxRead.NeedsUia(className, style);

    // A native push style is a push button. An owner-drawn BUTTON is one only when UI Automation gave evidence of control type Button or
    // Invoke, and it is never one when it looks like a check box. No answer from UI Automation is no evidence.
    internal static bool IsPush(string className, uint style, PushUia? uia)
    {
        if (!CheckBoxRead.IsButtonClass(className))
        {
            return false;
        }

        if (IsNativePushStyle(style))
        {
            return true;
        }

        return CheckBoxRead.IsOwnerDraw(style) && uia is { } evidence && !evidence.IsCheckBoxType && !evidence.HasToggle
            && (evidence.IsButtonType || evidence.HasInvoke);
    }
}

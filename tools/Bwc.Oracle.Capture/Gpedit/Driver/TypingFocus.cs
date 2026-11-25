namespace Bwc.Oracle.Capture.Gpedit.Driver;

// Whether the keyboard focus is on the control that is about to be typed into. Failure mode 43 in docs/failure-modes/oracle-gpedit-driver.md.
internal static class TypingFocus
{
    // Accepted: UI Automation names the control (the reading before this change), or the window that has the keyboard focus in the thread of
    // the dialog is the control or a child of it. The inner edit of a Windows Forms number control has no UI Automation element in the control
    // view, so UI Automation names another element while the keys do reach the edit. Focus on the host of the control, on another window, or no
    // reading at all is refused.
    internal static bool IsOnControl(int uiaHandle, IntPtr keyboardFocus, IntPtr control, bool focusIsChildOfControl) =>
        uiaHandle == control.ToInt32() || (keyboardFocus != IntPtr.Zero && (keyboardFocus == control || focusIsChildOfControl));
}

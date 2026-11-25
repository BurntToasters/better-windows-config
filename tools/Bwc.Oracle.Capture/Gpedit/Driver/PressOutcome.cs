namespace Bwc.Oracle.Capture.Gpedit.Driver;

internal enum PressVerdict
{
    // The button is not one that closes the dialog (Apply, Previous, Next): nothing to judge here.
    NotJudged,
    Closed,
    MessageBox,

    // OK left the dialog open with no message box and no file change: classic refused the value without a box. An observed outcome.
    StayedOpen,

    // A Cancel that did not close, or an OK that stayed open and changed files. A driver failure.
    Failed,
}

// How the press of a button that closes the setting dialog ended, on plain values. Failure mode 44 in docs/failure-modes/oracle-gpedit-driver.md.
internal static class PressOutcome
{
    internal static PressVerdict Judge(string button, bool closed, int boxes, bool filesChanged)
    {
        if (boxes > 0)
        {
            return PressVerdict.MessageBox;
        }

        if (button is not ("OK" or "Cancel"))
        {
            return PressVerdict.NotJudged;
        }

        if (closed)
        {
            return PressVerdict.Closed;
        }

        return button == "OK" && !filesChanged ? PressVerdict.StayedOpen : PressVerdict.Failed;
    }
}

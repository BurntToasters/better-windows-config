using System.Text.Json.Nodes;

namespace Bwc.Oracle.Capture.Gpedit.Driver;

// The decision "did classic refuse the list on OK of Show Contents", and the fields it adds to the set entry of the option, on plain values so
// the self test covers it without classic. Failure mode 38 in docs/failure-modes/oracle-gpedit-driver.md.
internal static class ListRefusal
{
    // Classic refused the list when OK brought a message box and the dialog is still open after it. A box with the dialog gone is a warning
    // classic took the list after.
    internal static bool IsRefused(int boxCount, bool dialogOpenAfterBox) => boxCount > 0 && dialogOpenAfterBox;

    // Adds the fields of a message box on OK to the set entry. cancelled and cancelRoute apply only to a refusal (the driver cancelled the
    // dialog); boxesAfterCancel are the boxes found after that Cancel. The boxes themselves stay in the close of OK, where a press-like result
    // has them, so they take the same way through the scrubber as the boxes of a press.
    internal static void Apply(JsonObject entry, JsonArray boxes, bool dialogOpenAfterBox, bool? cancelled, string? cancelRoute, JsonArray boxesAfterCancel)
    {
        bool refused = IsRefused(boxes.Count, dialogOpenAfterBox);
        entry["okMessageBox"] = boxes.Count > 0;
        entry["refused"] = refused;
        entry["dialogOpenAfterBox"] = dialogOpenAfterBox;
        if (!refused)
        {
            return;
        }

        entry["cancelled"] = cancelled;
        entry["cancelClosedBy"] = cancelRoute;
        if (boxesAfterCancel.Count > 0)
        {
            entry["messageBoxesAfterCancel"] = boxesAfterCancel.DeepClone();
        }
    }
}

# Hex editor failure modes

Scope: the editable `HexEditor` in `src/Bwc.UI/Hex`, the new `HexEditorDialog` in
`src/Bwc.UI/Dialogs/HexEditorDialog.cs`, the `DetailsList.RowActivated` event, and the
double-click value editing wiring in `src/Bwc.Regedit`. The read-only viewer behavior recorded in
`hex-viewer.md` still applies to `HexViewerDialog`; this document covers the edit path and the
places where it can disagree with the user's intent.

## Read-only versus edit

### Default state

`HexEditor.IsReadOnly` defaults to true. `HexViewerDialog` sets it explicitly to true, so the
View > Display Binary Data path keeps the read-only contract: no key, paste, delete, or character
input changes the snapshot. `HexEditorDialog.SetValue` sets `IsReadOnly` to false before the data,
which is the only host allowed to enable editing in this batch.

### Edit path does not reach the registry by itself

The editor owns a private copy of the bytes. Every accepted change raises `DataChanged` and changes
only that copy. The registry is written by the host after the dialog returns `Accepted == true`,
using `RegistryStore.SetValue` with the original raw type. Canceling, Escape, closing the window,
or losing focus to another application leaves the registry untouched.

### Oversized or zero-length values

`SetData` copies the incoming memory into one `byte[]`, so memory stays O(n). A zero-length value
has zero rows but one append position at offset 0. Typing a hex pair or an ASCII character there
appends the byte, as classic regedit does. Delete does nothing. A value that is not
a multiple of sixteen has a short final row; cells past the length are never selectable and edits
clamp to the last real byte.

## Selection and nibbles

### Selection is byte based

Selection is one contiguous range of byte offsets. Clicking the hex or ASCII column selects one
byte. Shift click extends from the anchor. Arrows move one byte, Up and Down move sixteen bytes,
Page Up and Page Down move one visible page, Home and End move to the start and end of the current
row, and Ctrl+Home and Ctrl+End move to the first and last byte. Space moves one byte forward in the
hex column. Ctrl+A selects every byte. A selection that is longer than the data is clamped.

### Nibble state

The editor does not paint a separate nibble selection. Instead it tracks whether the next typed
hex digit belongs to the high or low nibble of the selected byte. The first digit replaces the high
nibble and clears the low nibble, and the second digit writes the low nibble and advances to the
next byte. Moving the selection, clicking, deleting, pasting, and `SetData` reset that state to the
high nibble. This is why typing `AA` at offset 0 produces `AA` and leaves the caret on byte 1,
not a shifted or partially updated byte.

### Editing a multi-byte selection

Typing over a selected range collapses the selection to its first byte, then applies the nibble
rules and advances one byte at a time. Bytes after the typed prefix stay untouched. This is
documented rather than silently deleting the rest of the range.

### Column focus determines the character meaning

A click in the hex column puts character input in hex mode. A click in the ASCII column puts
character input in ASCII mode. In hex mode only `0` to `9`, `a` to `f`, and `A` to `F` are accepted.
In ASCII mode characters 32 through 126 are written as one byte; other characters are ignored. The
same physical key can therefore mean `0x0A` or the letter `A` depending on where the user clicked.

## Insert and delete

### Overwrite versus insert

Overwrite is the default. Insert mode inserts a zero byte before applying the high nibble, or
inserts the ASCII byte, which grows the buffer and shifts the remainder right. The Insert key
toggles the mode when editable. Insert mode is preserved by `SetData` and is not reset by a new
dialog. Typing into an empty buffer or past the last byte appends in both modes.

### Append position

Completing a byte on the last offset moves the caret to the append position: offset equal to the
length, with no selection. Typing there appends a byte, so `FFEE` typed at the end of a three byte
value yields five bytes. Arrow keys still stop at the last byte.

### Paste in overwrite mode

Overwrite-mode paste writes the pasted bytes over the bytes starting at the caret and grows the
buffer past the end when needed. It does not remove the selection first. Insert-mode paste removes
any selection and inserts the bytes.

### Delete and Backspace

Delete removes the selected bytes. With no selection, Delete removes the byte at the caret.
Backspace with a single selected byte, the normal caret state, removes the byte before the caret,
as classic regedit does. Backspace with a multi-byte selection removes the selection. Backspace at
the append position removes the last byte. Removing the last remaining byte leaves an empty buffer
and a cleared selection. Every deletion clamps the caret to the new length.

### Row count changes

Overwrite edits keep the row count stable. Insert and delete can change the row count, so the
repeater source is rebuilt with the new row index count. Realized row elements are detached first
so a recycled row cannot show bytes that belong to the old length. The rebuilt rows still read
from the same single buffer, so memory remains O(n) and no element is created per byte.

## Clipboard

### Copy format

Ctrl+C copies the selected bytes as uppercase hexadecimal pairs separated by one space, for
example `00 01 02`. The text is the single clipboard format in this batch; a separate binary
stream format is not provided. Copy does not change the selection or the data. If another process
holds the clipboard, the failure is caught and reported through the editor help text, and the
selection remains usable.

### Paste format

Ctrl+V accepts hexadecimal text with optional spaces and commas. Every remaining character must be
a hex digit; otherwise the paste is ignored and the help text records an invalid clipboard. If
overwrite mode writes the bytes over the buffer from the selection start or caret and appends past
the end when necessary. Insert mode removes any selection and inserts the bytes, shifting the
remainder right. A pasted block raises one `DataChanged`, not one per byte.

## Registry integration

### Type preservation

The host reads the raw value, keeps `RegistryValueInfo.Type`, and writes with
`RegistryStore.SetValue(key, rawName, originalType, data)`. It does not call `SetBinary`, so a
REG_NONE value stays REG_NONE and an unknown type keeps its numeric type even though the editor
shows only bytes. Accepting without changing a byte writes the same bytes back with the same type.

### Double-click target

`DetailsList.RowActivated` is raised by a double tap on a realized details row, after the row has
become the single selection. Header clicks, the rename box, and multi-selection gestures do not
raise it. The registry host ignores the event when exactly one value row is not selected. Known
string, expandable string, DWORD, QWORD, and multi-string types keep their existing editors and are
a no-op for this batch; REG_BINARY, REG_NONE, DWORD big endian, link, resource types, and unknown
raw types open the hex editor.

### Value deleted under the dialog

The dialog owns a snapshot. If another process deletes the value while the dialog is open, the
snapshot stays editable and accepting writes the value back with the original type, recreating it.
If the value is already missing when the host reads it, the host shows the standard QueryValue
error with Win32 error 2 and opens no dialog. The list is not changed by a failed read.

### Save failure and access denied

Reading or writing can fail with `RegistryOperationException`, including access denied. The host
catches it around the background read and around the write, shows the standard registry error
dialog with operation, path, and Win32 code, and does not refresh the list. A write that fails
after the dialog closes cannot be undone from the dialog; the user sees the error and the old
registry value remains. The host never writes partially and never falls back to REG_BINARY.

### Modal owner and focus

`HexEditorDialog` derives from `ModalDialogWindow`, so it disables the owner, centers over it, and
restores it on close. If the owner handle is missing or another modal is already open, the host
logs and returns without showing a window. Escape cancels and OK accepts. Enter is deliberately
not a submit shortcut in this dialog: `SubmitsOnEnter` is false so Enter inside the hex body does
not save. Enter still activates the OK button when that button has keyboard focus. Focus returns
to the details list after the dialog closes only when the host actually showed it.

### Dialog state

The status line reports byte count, type name, display name, and Insert or Overwrite mode with the
automation id `HexStatus`. Selection changes, edits, and mode changes update it. `SetData` clears
the selection without raising `DataChanged`, so a host can replace the snapshot without treating
it as a user edit.

## UIA and recycling

### Row peers

The grid peer and row peer behavior from `hex-viewer.md` is unchanged. Only realized rows have row
peers, row automation ids are recomputed from the element index on prepare, and the row name is the
current row text. An edit refreshes the realized row text in place. Insert or delete rebuilds the
row source, which detaches the old elements, so a stale row cannot keep the old offset or bytes.
The editor help text keeps the `Length=...;SelectedOffset=...;SelectedLength=...` prefix and adds
`Mode=...` and `ReadOnly=...` for tests.

### Focus during edits

The editor keeps focus while typing. Rebuilding the repeater on insert or delete does not move
focus to another control. Clicking a row still focuses the editor before applying the selection,
so the first typed character after a click is not lost.

## Classic layout (UF7)

Classic Edit Binary Value (RT_DIALOG 103) shows eight bytes per row. The offset column shows four
hex digits (`0000`, `0008`, ...), and each row is the hex pairs followed by the ASCII column.

### Offset column clipped

The offset column is sized for four hex digits, and the text is `X4`. An offset above `FFFF` needs
more digits, so the column grows to fit the digits the current length needs and is never clipped.
Test: `OffsetColumnFourDigits` and `OffsetColumnGrowsAbove64K`.

### Row width not eight bytes

`BytesPerRow` is 8. A 16-byte row would make the offsets and the ASCII column disagree with the
classic dialog. Test: `RowHoldsEightBytes`.

### Append at the end

Typing or pasting at the end of the buffer appends. In overwrite mode the caret after the last byte
is not an overwrite target, so the append inserts; in insert mode it inserts too. Test:
`AppendAtEnd`.

### Editing an empty value

An empty value has no row. Typing a hex pair into an empty value appends one byte, and paste
appends too. Failure mode: the first typed digit is dropped, so the user cannot enter any byte
without a clipboard. Test: `HexEditingFixes_TypeIntoEmptyValue`.

### Typing past the last byte

Failure mode: after the last byte is completed, the caret clamps to that byte, so the next typed
pair overwrites it and the value can never grow by typing. Test: `HexEditingFixes_TypeAppendsAtEnd`.

### Backspace removes the byte under the caret

Failure mode: a single selected byte was treated as a selection, so Backspace removed the byte
under the caret instead of the byte before it. Test: `HexEditingFixes_BackspaceDeletesBeforeCaret`.

### Paste in overwrite mode inserts

Failure mode: overwrite-mode paste removed the selection and inserted, so the bytes after the
caret shifted right instead of being replaced. Test: `HexEditingFixes_PasteOverwritesInOverwriteMode`.

### Shift selection and mouse selection

Shift+Left, Shift+Right, Shift+Up, Shift+Down, and a shift-click extend the selection from the
anchor. A drag and a shift-click select the same range. The E2E test uses raw `keybd_event` so the
modifier reaches the control as the real key state. Test: `ShiftArrowSelection`.

### Copy and paste of hex text

Ctrl+C copies the selection as hex pairs separated by spaces. Ctrl+V reads hex pairs from the
clipboard and ignores separators. Text that is not hex is not pasted, and the clipboard issue is
reported in the status, not in the value. Test: `CopyPasteHexText`.

### Backspace and Delete

Delete and Backspace follow the rules in "Delete and Backspace" above, with the offset column
kept at its width. Test: `BackspaceDeleteKeys`.

## Double-click on a selected value row

The double-click path is `DetailsRowElement.OnDoubleTapped`, then `DetailsList.RowDoubleTapped`,
then `ValueCommands.ActivateValue`, which opens the hex editor for a binary value. A user who
double-clicks a selected binary row must get the editor every time. These are the ways it can fail.

### A list rebuild between the two presses

`SetRows` calls `ResetRows`, which clears the selection and recycles every row element
(`ItemsSource` is set to null and back). If a load finishes while the user's two presses are
pending, the second press is hit-tested on a different element. The gesture then does not complete
as a double tap, and the element that took the first press is detached (`Owner` is null) and
ignores the event. Rule: a rebuild that arrives between the presses does not lose the double tap
of the row that is still under the pointer at the second press.

### A double tap that is not a double tap

The second press comes after the system double-click time, or farther from the first press than
the system double-click distance (SM_CXDOUBLECLK, scaled by DPI). A user does not see this, and
the check must not rely on it. FlaUI sends its two presses back to back, so a missed double tap
from a slow input is a test fault, not a product fault.

### The click lands somewhere else

Another window covers the row at the click point, the row has no clickable point because it is
scrolled out, or the cursor is over the title bar. The presses then never reach the row. A check
that reports only "opened=false" hides which of these happened. Rule: the double-click is sent only when the app is foreground and UI Automation finds the row
or one of its children at the click point. The check waits up to five seconds for that. The
result names what was at the click point when the wait ended. The editor must still open after the
double-click; the precondition does not excuse a lost double tap.

### A gate drops the activation

`ValueCommands.ModifyValueAsync` returns without a dialog when another modal is open
(`DialogOpen`), when the window handle is not attached, or when the selection is no longer the
activated row. `StartCommand` drops the command while a registry write runs. A user sees nothing.
Rule: the gates only protect a real modal or a real write. A double-click on a single selected row
with no modal and no write opens the editor.

### Evidence in the check

`hexedit.dialog` records the click target and the list trace from the `BWC_E2E` build: the number of
row presses, the double taps the rows received, the outcome of the last double tap (opened, ignored
with its reason, or detached), and the number of row rebuilds. A failed open names its cause in the
result file.

## E2E and cleanup

### Editing test

The HexEdit test seeds `HKCU\Software\BWC-E2E\<runId>` with a binary value `0x00` through `0x2F`
and a string value, launches the published application with an isolated data root, navigates to
the key, and double-clicks the binary row. It edits the first bytes, accepts, verifies the bytes
and the REG_BINARY type through Win32, then reopens to change the length in insert mode and cancels
to prove the registry is unchanged. It reopens for Ctrl+A and Ctrl+C, and records the clipboard
check as informational when another process holds the clipboard. Escape must return focus to the
list.

### Cleanup

The test removes only the run subtree and its scoped data root, and disposes the application
session in a finally block. The shared assembly cleanup removes the empty `HKCU\Software\BWC-E2E`
parent when possible. No test writes to HKLM or to another user's hive.

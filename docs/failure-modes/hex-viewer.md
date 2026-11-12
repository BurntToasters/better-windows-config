# Hex viewer failure modes

Scope: the read-only `HexEditor` in `src/Bwc.UI/Hex`, `HexViewerDialog` in
`src/Bwc.UI/Dialogs/HexViewerDialog.cs`, the View > Display Binary Data wiring in
`src/Bwc.Regedit`, and the Hex E2E suite. The viewer never writes to the registry. The editable
path and `HexEditorDialog` are documented in `hex-editor.md`; this document records what the
read-only viewer does and what the implementation action is.

## Large values and virtualization

### One element per byte

A one megabyte value has 1,048,576 bytes. Creating one UI element per byte would exhaust memory and
stall layout. The editor stores the raw bytes once and uses an `ItemsRepeater` with a `StackLayout`
inside a `ScrollViewer`. Only the rows near the viewport are realized. A row is one panel with an
offset cell, sixteen hex byte cells, and sixteen ASCII cells. The row model is the row index, not a
list of row objects, so the editor does not allocate a collection entry per byte.

### Large value read on the UI thread

Reading a large value synchronously in the command handler would block input. The command reads the
raw value on the thread pool and applies the result on the UI thread. The registry reader opens and
closes its own handle for each call, so a background read is safe. A read failure is reported with
the standard registry error dialog.

### Huge selection copy

Ctrl+C with a very large selection builds one string for the whole selection. This allocates about
three characters per byte plus separators. The operation is explicit user action and runs once on
the UI thread. If allocation fails, the process reports the failure through the normal unhandled
exception path rather than silently truncating the clipboard text. The viewer does not add a
partial second format, so the clipboard has exactly one deterministic text representation.

## Odd lengths and empty data

### Partial final row

The row count is `(length + 15) / 16`. A value whose length is not a multiple of sixteen has a
short final row. Byte cells past the real length are empty and are not selectable. ASCII cells past
the real length are empty. The offset column still shows the correct base offset.

### Zero-length value

A zero-length value produces zero rows. The editor accepts the data, shows an empty body, and the
status line reports `0 bytes`. Ctrl+A and Ctrl+C do nothing because there is no selectable byte.
Selecting a row cannot select a byte outside the data.

### Empty or missing value

`RegistryReader.ReadValue` returns null when the value no longer exists. The command does not open
the dialog in that case. It shows the registry error dialog with operation `QueryValue`, the key
path, Win32 error 2, and the raw value name. The list is left unchanged and the value is not
recreated.

## Selection and clipboard

### Selection model

Selection is a single contiguous range of byte offsets. A click sets a one-byte selection. Shift
click extends from the anchor to the clicked byte, inclusive. Left and Right move one byte, Up and
Down move sixteen bytes, and Page Up and Page Down move one visible page. Home and End move to the
first and last byte of the data. Shift with an arrow extends the range. Ctrl+A selects every byte.
The selection is clamped to the data length at every entry point.

### Row recycling and stale selection

Rows are recycled while scrolling. The row element receives its index and the current selection on
every prepare, and selection changes are pushed to the realized rows. A recycled row therefore
cannot paint a highlight for a byte that belongs to a different row. The UIA peer reads the index
from the row element, not from a captured value, so `HexRow_<index>` always matches the visible row.

### Clipboard unavailable

`Clipboard.SetContent` can fail when another process holds the clipboard, for example during a
remote session or another copy operation. The copy path catches the failure, leaves the selection
and the viewer intact, and records the condition in the editor help text for diagnostics. It never
throws out of the key handler. The clipboard format is uppercase hexadecimal pairs separated by one
space, for example `00 01 02`. No binary clipboard format is provided in this batch.

### Clipboard content and read-only state

Copy does not change the selection and does not modify the value. The viewer does not handle
paste; `HexViewerDialog` sets `IsReadOnly` to true and exposes no edit path. The editable dialog
and its paste rules are documented in `hex-editor.md`.

## Registry views

### Display command state

View > Display Binary Data is enabled only when exactly one value row is selected. The state is
recomputed after every list selection change and after a value refresh. A key selection, an empty
list, or a multi-row selection disables the command. Invoking the command anyway shows a message
and opens no dialog.

### Value deleted while the dialog is open

The dialog owns a snapshot of the bytes read before it opened. Deleting the value from another
process, or from the same application after the dialog closes, does not change the snapshot and
cannot make the dialog read freed state. Closing the dialog does not refresh or mutate the list.
The next value refresh reflects the registry.

### Access denied

If opening the key or reading the value is denied, the reader throws
`RegistryOperationException`. The command catches it and shows the standard error dialog with the
operation, path, Win32 code, and system message. No dialog is opened and no partial data is shown.

### Raw name mapping

The display row name is mapped back to the raw registry name through
`RegistryValueSource.RawNamesByName` and `TryGetRawValueName`. The empty raw name for the default
value is preserved. The command never parses display text to recover a registry name.

### Value mutation

The command reads with `RegistryReader.ReadValue` only. It does not call the store, does not open a
writable key, and does not write a terminator or normalize the bytes. Every value type, including
strings and unknown types, is shown as its stored raw bytes.

## Window and focus

### Modal owner

The viewer derives from `ModalDialogWindow`, so it uses the same owned, disabled-owner modal path
as the other editors. If the window handle is not attached or another modal is already open, the
command logs and returns without creating a window. Escape and OK both complete the dialog.
Focus returns to the details list after the dialog closes. Focus is not sent to the list when the
dialog could not be shown.

### Dialog size and DPI

The dialog requests 720 by 480 DIP. The base class converts that to physical pixels with the owner
window DPI and centers the result over the owner. The hex editor uses DIP metrics only. No fixed
pixel font size is used.

### Monospace font and alignment

The hex columns require a monospace face. The editor tries Cascadia Mono and measures a narrow and
a wide glyph to confirm the face is monospace. If Cascadia Mono is not installed, it falls back to
Consolas. If both are missing, text still renders with the platform fallback but column alignment
is not guaranteed. The offset, byte, and ASCII columns use measured character widths rather than
hard-coded pixel offsets, so 100, 150, and 200 percent scale keep the columns aligned. All colors
come from theme brushes; the control has no hard-coded colors.

### Body height

The modal body is inside a scrolling presenter. The hex editor owns a finite height so its internal
vertical scrollbar remains usable instead of measuring to infinity. Horizontal overflow belongs to
the editor's own scroll viewer.

## UIA and tests

### Grid and text peers

The editor peer exposes `IGridProvider` with one row per sixteen bytes and eighteen columns: the
offset column, sixteen hex byte columns, and the ASCII column. Each realized row exposes
`IGridItemProvider`, `ISelectionItemProvider`, `IScrollItemProvider`, and a row level
`ITextProvider`. The row automation name is the full row text, so a client can read
`00000000  00 01 ...` without implementing a text range itself. An unrealized row has no peer; the
grid provider returns the realized row peer when one exists and does not force realization of a
distant row. This bounds UIA work on a multi-megabyte value.

### Keyboard focus in E2E

The E2E test selects a row through the UIA selection item pattern, asks the row peer for focus, and
sends Ctrl+A and Ctrl+C. If the test session cannot read the clipboard, the test records an
informational check with the clipboard exception and still asserts the selected length through the
editor help text. Clipboard availability does not decide the selection result.

### Cleanup

The Hex E2E test seeds only `HKCU\Software\BWC-E2E\<runId>`, launches the published application
with an isolated `BWC_DATA_ROOT` and `BWC_E2E_SANDBOX_ROOT`, and disposes the process session in a
finally block. It removes the run subtree and the scoped data root, and the shared assembly cleanup
removes the empty `HKCU\Software\BWC-E2E` parent when possible. No test targets HKLM or another
user hive.

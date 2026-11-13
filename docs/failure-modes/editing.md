# Registry editing failure modes

Scope: the modal dialog layer in `src/Bwc.UI/Dialogs`, the New, Delete, and Rename command
wiring in `src/Bwc.Regedit/RegeditRootView.xaml.cs` and `src/Bwc.Regedit/Commands/RegeditMenu.cs`,
the typed value editors, and the Editing E2E suite. The registry writer, codec, and store remain
the only types that mutate the registry. This document lists what can go wrong and the action the
implementation takes.

## Modal dialogs

### Owner, focus, and a disabled owner

A dialog is a separate top level window, not a XAML overlay. It is created with
`OverlappedPresenter.CreateForDialog`, the presenter is marked `IsModal`, and the Win32 owner is
set with `SetWindowLongPtr(GWLP_HWNDPARENT)`. `IsModal` alone does not disable the owner in the
WinAppSDK version used here, so the owner HWND is disabled with `EnableWindow(owner, 0)` before
the dialog activates. On `Closed` the owner is enabled again, `SetActiveWindow` restores focus,
and the result task completes exactly once.

If the owner handle is zero, for example because a command fired before `AttachWindow`, the
command logs a warning and returns without creating a window. A dialog that cannot activate is
closed and completes with a cancel result so the owner is never left disabled.

### Keys into an owned error box

The error box for an invalid key name is an owned top level window of the app and is the foreground
window while it is open. The E2E guard accepts only the main window or its own input site, so
`ControlTestSupport.SendKeys` refuses to send Enter to the box and the check fails with "The app's own
window ... had the foreground". The dismissal is sent to the foreground window directly, as the box
is the input target, and the check still requires the message text to clear and the dialog to close.

### Escape and Enter

Escape always cancels and closes with a false result. Enter invokes the primary action. Both are
handled on the dialog root so they work while a text box has focus. A multi-line editor opts out
of Enter submit because Enter inserts a newline there; Escape still cancels. The close button in
the title bar counts as cancel. Validation failures keep the window open and show a message.

### Duplicate names

The New Key prompt rejects a name that already exists in the selected parent. The New Value prompt
rejects a name that already exists in the selected key. Rename key and rename value reject a target
name that collides with a different case-insensitive name. A case-only rename is allowed because
it denotes the same registry object. Duplicate validation queries the reader before the write, so
the operation is not attempted and the dialog stays open.

### Empty and invalid input

Key names are validated with the same rules as `RegistryPath.Append`: non-empty, at most 255
characters, no separator, no NUL, and not `.` or `..`. Value names are allowed to be empty because
an empty name is the default value, but a name longer than 16383 characters or containing NUL is
rejected. Invalid input shows an inline error and does not close the dialog.

### DWORD and QWORD overflow

The numeric editors accept decimal digits or a `0x` hexadecimal prefix. The value is parsed as an
unsigned 64 bit integer first, then range checked against the 32 bit or 64 bit target. Overflow,
a sign character, a malformed prefix, or trailing characters produce an error while the dialog
remains open. The written data is always exactly 4 bytes for DWORD and 8 bytes for QWORD through
the codec.

### Embedded NUL in text

A raw registry string can contain an embedded NUL. The classic Data column stops at the first NUL.
The text editor therefore cannot represent the bytes after that NUL. Editing such a value would
silently discard the hidden tail, but the current command set only creates new values and renames
existing ones, so no existing string is rewritten by this batch. New text is encoded as UTF-16LE
with exactly one terminator, including for the empty string. Embedded NUL characters pasted into
the editor are written as part of the UTF-16 data; the display layer still shows text only up to
the first NUL.

### Binary odd length and non-hex input

The binary editor ignores spaces, tabs, commas, and line breaks. Every remaining character must be
a hex digit. An odd digit count or a non-hex character shows an error in the dialog and blocks the
save. Zero remaining digits is valid and writes a zero length binary value. A successful parse
shows the byte count.

### Multi-string empty strings

The multi-string editor treats each line as one string. Interior empty lines and a trailing newline
create empty entries. The dialog shows a warning that empty strings will be saved but does not
block the save, matching the classic behavior. An entirely empty editor maps to an empty list and
encodes the double NUL terminator through the codec.

## Registry operations

### Access denied

Every write goes through `RegistryStore`, which throws `RegistryOperationException` with the
operation, full path, and Win32 code. The UI catches it, closes the editor that was already
accepted, and shows the error dialog with the operation, path, code, and system message. The
application does not crash and the tree or list is left at its previous state.

### Key deleted under the selection

A key can disappear between selection and a write, or between the value prompt and the save. The
reader returns a file-not-found error, the operation is reported through the error dialog, and a
tree refresh removes the stale node. The selection falls back to Computer when the path no longer
resolves. A missing value during rename is reported the same way.

### Rename of the default value

The list displays the default value name as `(Default)`. The raw name is the empty string, and
renaming it is not allowed because the rename sequence copies then deletes by name. The F2 path
does not start for that row and the Edit command reports the reason if it is invoked anyway.
Names are mapped back through `RegistryValueSource.RawNamesByName`, never by parsing display text.

### Recursive delete confirmation

Deleting a key asks for confirmation. When the key has at least one subkey the message includes
the classic warning "Deleting this key will delete all of its subkeys." The confirmed operation
calls `RegistryStore.DeleteKey(path, recursive: true)`. Cancelling leaves the key and all of its
children untouched.

### Refresh and stale selection

Mutations rebuild the tree source instead of patching cached children. The target path is resolved
again with `FindPathAsync` so ancestors are expanded, then `SetRoots` replaces the flat rows. A
renamed key selects the new path, a deleted key selects its parent, and a new key selects itself.
Values are reloaded from the reader and the new or edited row is selected by display name. If the
path cannot resolve, Computer is selected and no stale node is shown.

### Dialog reentrancy

`RegeditRootView` tracks whether one modal is active. A second command while a modal is open logs
and returns without creating another window. The base class also guards `ShowAsync` so a dialog
instance cannot be shown twice. Because the owner is disabled, a menu or list click cannot start
a second command through the normal path, but a keyboard command that was already queued is still
ignored.

### Dialog cleanup

`ShowAsync` returns a task that completes on `Closed`, whether the user accepted, cancelled, or
closed the window with the title bar button. The completion source is completed exactly once, the
owner is re-enabled exactly once, and the closed handler is detached by the window lifetime. A
failed activation closes the window and restores the owner. No nested message loop is created.

## Test isolation

### Sandbox policy

Ship builds use `ShippingWritePolicy`. E2E builds use `SandboxWritePolicy` when
`BWC_E2E_SANDBOX_ROOT` contains a valid path at or below `HKCU\Software\BWC-E2E`. If the variable
is absent or malformed, the build falls back to a deny-all sandbox root at
`HKCU\Software\BWC-E2E\unset`, so an E2E build cannot write anywhere else. The chosen policy is
logged at startup.

### E2E isolation

Editing tests create a fresh `BWC_DATA_ROOT` scope and seed only
`HKCU\Software\BWC-E2E\<runId>`. They launch the published application with
`__COMPAT_LAYER=RunAsInvoker`, navigate through the live tree, and exercise the real dialog
windows. Every test removes its run subtree and its data root scope in a finally block and disposes
the process session, which closes or kills the application. No test targets HKLM or a user hive.

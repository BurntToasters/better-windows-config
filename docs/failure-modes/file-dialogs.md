# File dialog failure modes

This document covers the COM common item dialogs that back File > Import and File > Export in
the Registry Editor. The dialog code lives in `src/Bwc.UI/Dialogs/CommonFileDialogs.cs`; the
commands and refresh behavior live in `src/Bwc.Regedit/RegeditRootView.xaml.cs`. The registry and
`.reg` engine behavior stays in `import-export.md` and `reg-files.md`.

## 1. Why COM instead of a WinRT picker

The dialogs use `IFileOpenDialog`, `IFileSaveDialog`, and `IFileDialog.Show`. The WinRT
`FileOpenPicker` and `FileSavePicker` are deliberately not used. They do not reliably accept a
plain owner HWND in an unpackaged, elevated-capable process, and they can fail or open behind the
main window.

`IFileDialog.Show` performs its own modal loop on the calling thread. The call must happen on the
UI thread because the dialog is an apartment-threaded COM object. The registry work that follows
it runs on the thread pool.

## 2. Ownership and the disabled owner

The owner HWND is the main Registry Editor window, passed to `ShowOpen` or `ShowSave`. The dialog
sets that window as its owner. Windows then disables the owner for the lifetime of the dialog,
which is what makes the dialog modal: the tree, list, menu, and address bar cannot be clicked and
the window cannot be activated above the dialog.

The pattern is the same one `ModalDialogWindow` uses for in-process dialogs. The common dialog
does the disable itself, so the Registry Editor does not call `EnableWindow(FALSE)` around it.

If the owner closes while the dialog is open, the dialog closes first. A process shutdown during
the modal loop leaves no file selected.

## 3. Cancel

Closing the dialog, pressing Escape, or pressing the dialog's Cancel button raises
`HRESULT_FROM_WIN32(ERROR_CANCELLED)` from `Show`. `CommonFileDialogs` catches exactly that
HRESULT and returns null. Every other `COMException` propagates and is reported as a failure.

A null result is a normal outcome. Import and Export return before touching the registry or the
file system. A cancelled Export creates no file, does not truncate an existing file, and does not
change the current selection.

## 4. Filters

Both dialogs register two filter pairs:

| Name | Pattern |
| --- | --- |
| `Registration Files (*.reg)` | `*.reg` |
| `All Files` | `*.*` |

The first filter is selected by default through `SetFileTypeIndex(1)`. The strings are passed as
`COMDLG_FILTERSPEC` pairs and freed with `CoTaskMemFree` after `Show` returns. They are not the
Win32 `Name|Pattern` string form.

The filter is a convenience, not a validation gate. A user can pick any file through All Files.
Import then parses whatever file was chosen and reports a parse error if it is not a `.reg` file.

## 5. Paths with Unicode and spaces

The result comes from `IShellItem.GetDisplayName(SIGDN_FILESYSPATH)`, so the command receives a
full Win32 path. The dialog and the shell handle Unicode, spaces, and mixed separators. The path
is used as-is; Import never rewrites it and Export writes exactly the path the user approved.

The default Export file name is derived from the selected key's last component. Characters that
are legal in a registry key name but illegal in a Windows file name, such as `?`, `*`, `:`, and
`"`, are replaced with `_`. A name ending in a dot loses the dot. Root keys use their display name,
for example `HKEY_CURRENT_USER.reg`.

## 6. Long paths

The shell returns a path that may exceed `MAX_PATH`. .NET file APIs on Windows 11 accept long
paths when long path support is enabled for the process or the machine. If the returned path
cannot be opened, Import reports the I/O error and applies nothing; Export reports the failure and
leaves the destination alone when the failure happens before the final write.

The default file name is length limited by the dialog itself. A 255 character registry component
can produce a file name near the limit; the user can shorten it in the dialog before saving.

## 7. Elevated dialogs

The COM dialog runs in the Registry Editor's process and inherits its integrity level. An elevated
Registry Editor therefore shows an elevated dialog and can write protected keys. A non-elevated
editor shows a normal dialog and import failures are reported as access denied.

The E2E build launches with `__COMPAT_LAYER=RunAsInvoker`, so the test never raises UAC. The
dialog ownership assertions are the same at either integrity level.

## 8. Import partial writes

`RegFileImporter.ApplyFile` parses the whole file before it writes anything. A malformed file, a
missing file, an unreadable file, or a bad key path in the file causes no registry change.

Once applying starts, the import is not transactional. A native failure in the middle of a
section leaves the calls that already succeeded applied, and later sections do not run. The same
is true for a write policy denial: the rejected entry performs no write, but earlier entries stay
applied. This is the engine contract documented in `import-export.md`.

The UI reports the failure with the file path, the failing registry operation when one is
available, the Win32 code, and the message. It does not attempt to roll back.

## 9. Import parse errors

`RegFileParseException` carries a one-based line and column. Import shows the file path and the
message including that position. Because parsing finishes before applying, a parse error means
nothing was written.

An unrelated `IOException`, `UnauthorizedAccessException`, or argument error is also reported
with the file path. The tree and list are left as they were.

## 10. Import threading and refresh

The parse and apply call runs on the thread pool. The UI thread is not blocked while a large file
is imported, so the window remains responsive for repaint and input.

On success the view rebuilds the tree source and selects the key that was selected before the
import when that key still exists. If the import deleted it, the view falls back to Computer. The
value list is then reloaded for the selected key. The current value row selection is not restored;
only the key selection is.

## 11. Export access denied

Export reads the selected key and every readable descendant on the thread pool. An unreadable
descendant is skipped with its subtree by the engine. An unreadable start key throws
`RegistryOperationException`, and the UI reports it with the path and Win32 code. No file is
written when the document could not be built.

The final write happens after the complete document was serialized to memory. A failure while
writing the destination, such as access denied, a full disk, or a read-only file, is reported with
the destination path. The existing destination may already have been truncated by the final write
in that case.

## 12. File overwrite prompt

The save dialog sets `FOS_OVERWRITEPROMPT`. Choosing an existing file shows the shell's normal
confirmation. Confirming replaces the file; cancelling the confirmation returns to the save dialog,
not to the editor. Cancelling the save dialog itself returns null and nothing is written.

The E2E test always uses a fresh temporary path, so it does not depend on the text or button
layout of the overwrite prompt.

## 13. Computer and scope

Classic regedit can export the whole registry or a selected branch. This implementation exports
only the selected key and never the whole registry silently.

When the virtual Computer node is selected, `TryGetSelectedKeyPath` fails because Computer is not
a registry path. File > Export is disabled in that state. If the command is reached anyway, it
shows a selection message and returns. File > Import stays enabled because importing does not need
a selection.

## 14. Sandbox policy

The Import command builds its store through `RegeditWritePolicy.CreateStore` on the worker thread.
A shipping build uses `ShippingWritePolicy`. An E2E build uses `SandboxWritePolicy` rooted at
`BWC_E2E_SANDBOX_ROOT`, so a file that names a key outside the sandbox is rejected before the
native call.

Export reads through the existing reader and does not write the registry, so the write policy
does not limit what can be exported. The E2E test exports only its run key.

## 15. Automation timing

The common dialog is a separate top-level `#32770` window. The E2E test waits for that window by
process id and class, verifies its owner is the main HWND, verifies the main window is disabled,
and only then looks for the file name edit and the accept button.

The File command is activated by focusing the menu item and pressing Enter. A UIA `Invoke` would
block inside the modal `IFileDialog.Show` call and then time out, so the test does not use it to
open the dialog.

The dialog needs a moment to create its shell view. A missing edit is retried rather than treated
as a different dialog. If the expected controls are not found, the test sets the file name through
the keyboard as a fallback and records that it did so. If automation still fails, the test sends
Escape and waits for the window to disappear so no modal dialog is left open.

Escape is sent only after the dialog is confirmed foreground, with one retry, because the first
Escape can arrive before the shell view has keyboard focus.

## 16. Temp files and cleanup

Import and export tests create their files under `artifacts/e2e/import-export/<runId>`. The test
deletes that directory in a `finally` block, deletes the run key under
`HKCU\Software\BWC-E2E`, and disposes the application session, which closes the process. No test
writes a file outside that directory and no test touches a key outside the run key.

The E2E assembly cleanup sweeps stale run keys and the shared gate ensures one GUI test assembly
runs at a time.

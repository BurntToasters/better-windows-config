# File dialog failure modes

This document covers the COM common item dialogs that back File > Import and File > Export in
the Registry Editor. The dialog code lives in `src/Bwc.UI/Dialogs/CommonFileDialogs.cs`; the
commands and refresh behavior live in `src/Bwc.Regedit/Controllers/ImportExportCommands.cs`. The
registry and `.reg` engine behavior stays in `import-export.md` and `reg-files.md`.

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

The open dialog registers one filter pair and the save dialog registers three:

| Dialog | Name | Pattern |
| --- | --- | --- |
| Open | `Registration Files (*.reg)` | `*.reg` |
| Save | `Registration Files (*.reg)` | `*.reg` |
| Save | `Text Files (*.txt)` | `*.txt` |
| Save | `Win9x/NT4 Registration Files (*.reg)` | `*.reg` |

The first filter is selected by default through `SetFileTypeIndex(1)`. The strings are passed as
`COMDLG_FILTERSPEC` pairs and freed with `CoTaskMemFree` after `Show` returns. They are not the
Win32 `Name|Pattern` string form.

The filter is a convenience, not a validation gate. Import parses whatever file the user types and
reports string 133 when it is not a registry file. On Export, the selected save filter chooses the
writer: v5, text, or REGEDIT4. See `import-export.md` section 14.

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
section leaves the calls that already succeeded applied, and the importer continues with the next
line or section. A write policy denial performs no write for the rejected entry, but earlier
entries stay applied. This is the engine contract documented in `import-export.md`.

The UI reports the failure with the file path and the classic string 132. It does not attempt to
roll back.

## 9. Import parse errors

`RegFileParseException` carries a one-based line and column. Import shows the file path and the
message including that position. Because parsing finishes before applying, a parse error means
nothing was written.

An unrelated `IOException`, `UnauthorizedAccessException`, or argument error is also reported
with the file path. The tree and list are left as they were.

## 10. Import threading, progress, and refresh

The parse and apply call runs on the thread pool. The UI thread is not blocked while a large file
is imported, so the window remains responsive for repaint and input. A classic owned progress
dialog shows completed key sections out of the total. Cancel stops between sections, keeps the
sections that already finished, and closes the dialog. The window then refreshes the tree and the
list and shows string 132.

On success the view rebuilds the tree source and selects the key that was selected before the
import when that key still exists. If the import deleted it, the view falls back to Computer. The
value list is then reloaded for the selected key. The current value row selection is not restored;
only the key selection is.

## 11. Export access denied and progress

Export reads the selected key and every readable descendant on the thread pool. An unreadable
descendant is skipped with its subtree by the engine. An unreadable start key throws
`RegistryOperationException`, and the UI reports it with the path and Win32 code. No file is
written when the document could not be built.

The complete document is serialized to a temporary file in the destination folder, and the file is
moved into place only after the writer finished. A failure while writing the destination, such as
access denied, a full disk, or a read-only file, is reported with the destination path. The
temporary file is deleted, so the previous destination is not truncated. A classic owned progress
dialog reports running key and value counts. Cancel requests the export token, the temporary file
is deleted, and the destination is left absent or unchanged.

## 12. File overwrite prompt

The save dialog sets `FOS_OVERWRITEPROMPT`. Choosing an existing file shows the shell's normal
confirmation. Confirming replaces the file; cancelling the confirmation returns to the save dialog,
not to the editor. Cancelling the save dialog itself returns null and nothing is written.

The E2E test always uses a fresh temporary path, so it does not depend on the text or button
layout of the overwrite prompt.

## 13. Computer and scope

Classic regedit can export the whole registry or a selected branch. The save dialog adds the
classic "Export range" group through `IFileDialogCustomize`. "Selected branch" is the default and
exports the selected key. "All" calls `RegFileExporter.ExportWholeRegistry`.

When the virtual Computer node is selected, All is selected and enabled, and Selected branch is
visible but disabled with `SetControlState`; the export code also ignores the branch choice because
Computer is not a registry path. File > Export is enabled in that state. For a remote machine node,
which has no local path and no whole-registry meaning, the command shows the selection message and
returns. File > Import stays enabled because importing does not need a selection.

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
open the dialog. The file name, file type, range radio, and accept button are driven with real
keyboard and mouse input after a foreground check. UIA patterns read the selected radio and filter
state; they do not set the file name or invoke a control.

The dialog needs a moment to create its shell view. A missing edit, combo, or radio is retried
rather than treated as a different dialog. If automation still fails, the test sends Escape and
waits for the window to disappear so no modal dialog is left open.

Escape is sent only after the dialog is confirmed foreground, with one retry, because the first
Escape can arrive before the shell view has keyboard focus.

## 16. Temp files and cleanup

Import and export tests create their files under
`artifacts/e2e/import-export/<runId>/<scope>`. The export range artifacts stay under
`artifacts/e2e/import-export/<runId>/range`, the import progress file under `import-progress`, and
the canceled export destination under `export-cancel`. The tests delete their run key under
`HKCU\Software\BWC-E2E` and dispose the application session. The basic import/export test removes
only its own `basic` folder; the range artifacts remain for review. No test writes a file outside
`artifacts/e2e` and no test touches a key outside the run key.

The E2E assembly cleanup sweeps stale run keys and the shared gate ensures one GUI test assembly
runs at a time.

## 5. Parity filters and the Load Hive dialog (parity package)

Oracle strings 302 (Import) and 305 (Export) and 180 (All Files) define the filters. Each name
and pattern comes from `Bwc.UI` resources, so the English text stays out of code.

| Way it could fail | Expected behavior |
| --- | --- |
| Load Hive offers only `*.reg`, so `NTUSER.DAT` (no extension) cannot be chosen | Load Hive opens with the single filter `All Files` (`*.*`), string 180 |
| Import lists the hive filter first, so a `.reg` file is not the default | Index 1 is `Registration Files (*.reg)`; index 2 is `Registry Hive Files (*.*)`; index 3 is `All Files` |
| Export index 2 (hive) is written as text or REGEDIT4 | Index 2 routes to the hive exporter (`RegSaveKey`), index 3 to text, index 4 to REGEDIT4, index 5 (All Files) to the v5 writer |
| Export pattern for the hive filter is `*.*`, so the list differs from classic | The hive export filter pattern is `*.` as in string 305 |
| The export range panel is shown for the hive format on Computer | The hive format needs a key. On Computer the hive format is refused with the no-selection text |
| A hive file is written over an existing file in place | Hive export writes a temporary file and moves it over the destination only after `RegSaveKey` succeeds |
| Save filters keep their old indices after the reorder | The index-to-writer map is one table, and the E2E test reads it back through the dialog |
| The dialog cannot be shown while another modal is open | Unchanged: `FileDialogOpen` and `DialogOpen` refuse the request and log it |
| E2E driver presses Down one time too many for a one-based filter index, so index 3 selects Win9x/NT4 | `SelectFileType` takes a one-based index (1 is the first filter) and presses Down index minus 1 times after Home; index below 1 is refused |

# Print failure modes

Scope: File > Print in `src/Bwc.Regedit`, the `RegistryPrinter` in
`src/Bwc.Regedit/Printing`, and the Print E2E suite. This batch implements the R14 subset of
MENU-009 (id 660), BEH-027, and DLG-016. The parity inventory leaves much of the classic layout
open in OQ-44, so this document records the decisions taken and the failure behavior. No unit or
golden print output exists, and the E2E test cancels the dialog rather than producing paper.

## Print range and report content

### Chosen range

The command prints the selected branch only. It does not implement the classic All and Selected
branch radio panel from DLG-016, and it does not offer a page range. The Windows print dialog is
therefore opened with `PD_NOSELECTION`, `PD_NOPAGENUMS`, `PD_RETURNDC`,
`PD_USEDEVMODECOPIESANDCOLLATE`, and `PD_HIDEPRINTTOFILE`. The Print to File check box is hidden
because the implementation prints through the device context that `PrintDlgW` returns.

### Report shape

`RegistryPrinter.FormatReport` builds one deterministic text report. The first line is the header
`Registry Editor`. The selected key follows as `Key: <Computer path>`, then its values in registry
enumeration order. Every descendant follows depth first in registry enumeration order, each with
`Subkey: <Computer path>` and its values. Values use `RegistryValueDisplay.FormatName`,
`FormatType`, and `FormatData`, so the report shares the list's display names, type names, and
`(value not set)`, `(zero-length binary value)`, and invalid DWORD or QWORD text. Values without
data do not acquire data during printing. Names and data are escaped for line integrity: CR, LF,
and TAB become `\r`, `\n`, and `\t`.

The header, key path, and subkey paths are the only layout decisions in this batch. There is no
footer, page number, timestamp, or printer name, because OQ-44 leaves those open and a timestamp
would make E2E output unstable. Paper size, orientation, and copies come from the printer's
DEVMODE and the print dialog.

### Empty keys and roots

A selected key with no values prints `(no values)`. A key with no subkeys simply has no Subkey
blocks. A predefined root may be selected and printed; it is a valid `RegistryPath`, so the command
is enabled. Printing a root can produce a large report and is bounded by the same limit as any
other key. Computer is virtual and is never printable: it has no `RegistryPath`, so the menu item is
disabled and the keyboard path shows the standard no-selection message.

## No printer installed

`PrintDlgW` can fail before showing a usable dialog when no printer is installed or no default
printer exists. Windows may show its own message box first. When `PrintDlgW` returns false,
`CommDlgExtendedError` is checked. A value of zero means the user cancelled and produces no output
and no error dialog. A nonzero common dialog error, including `CDERR_NODEFAULTPRN` (0x0008), is
returned as a failure with that code. The view then shows the standard error dialog with the
selected key path and the code. No GDI job is started, so a missing printer cannot leave a partial
document. The E2E test treats "no print dialog, no crash, main window usable" as an informational
pass when the environment has no printer, because a test machine cannot be assumed to have one.

## Dialog ownership, modality, and cancel

### Owner and disabled owner

`PrintDlgW` receives `_windowHwnd` as `hwndOwner`. Windows disables the owner for the lifetime of
the dialog, which is the same observable behavior as the COM file dialogs and the aclui security
editor. On Windows 11 25H2 with an owner HWND, comdlg32 hands the request to the Windows system
PrintDialog (`PrintDialog.exe`, an `ApplicationFrameWindow`) instead of creating a classic `#32770`
common dialog. That host runs in another process and exposes no `GW_OWNER`, but the caller's window
is still disabled for the duration and `PrintDlgW` does not return until the host closes. The
implementation keeps using `PrintDlgW`; it does not attempt to bypass the platform dialog. The E2E
test accepts both shapes: a classic `#32770` owned by the main window, or the system
`ApplicationFrameWindow` titled `<app> - Print` while the main window is disabled.

### Cancel

Cancel is not an error. `PrintDlgW` returns false with `CommDlgExtendedError` equal to zero. The
method returns `PrintResult` with status `Cancelled`, the view logs nothing beyond the normal
command path, shows no dialog, prints nothing, and returns focus to the tree or list. A second
print request while the dialog is open is ignored by the `_dialogOpen` gate. A cancel after the
dialog has already returned cannot happen because no state is kept between requests.

### Reentrancy

The print command holds `_dialogOpen` for the print dialog and for the background GDI job. Menu
commands, keyboard commands, context menus, and other modal dialogs that already check
`_dialogOpen` therefore cannot start a second print, a Find, or a file dialog while the job is in
progress. The WinUI window remains responsive because the GDI work runs on the thread pool after
the native dialog returns; only the dialog itself runs on the UI thread, which is required for an
owned modal dialog.

## Ctrl+P and command state

File > Print is enabled only when `TryGetSelectedKeyPath` resolves the tree selection to a real
registry key. Computer disables it. The state is recomputed on every tree and list selection
change and after refreshes, like the other selection-dependent commands. Ctrl+P is handled in the
existing root key handler and only when focus is inside the tree or the details list. Ctrl+F, F3,
and F5 keep their existing order and behavior. Ctrl+P from the address bar or another editor does
not start a print. After the dialog closes, focus returns to the list when the list had focus before
the command, otherwise to the selected tree row.

## GDI failures and handle ownership

### Checked calls

Every GDI call is checked. The device context comes from `PrintDlgW` with `PD_RETURNDC`. A
`Consolas` 9 point font is created for the device's vertical DPI with `CreateFontW` and selected
with `SelectObject`. `GetDeviceCaps` supplies `HORZRES`, `VERTRES`, `LOGPIXELSX`, and
`LOGPIXELSY`. `GetTextExtentPoint32W` measures a representative digit string for the character
width and `Ag` for the line height, so the layout follows the printer's actual font metrics rather
than hard-coded pixels. A zero or negative page size, an unmeasurable font, or a failed
`CreateFontW` returns a failure before `StartDoc`.

`StartDocW` must succeed before any page is started. A failure at `StartPage`, `TextOutW`,
`EndPage`, or `EndDoc` aborts the job with `AbortDoc` when `StartDoc` had succeeded. The result
carries the stage name and the last Win32 error. A failure after `StartDoc` therefore does not leave
a half-open document. If `AbortDoc` itself fails, that fact is logged but the original failure is
returned so the user sees the actionable error.

### Handle cleanup

The device context is deleted with `DeleteDC` exactly once by `PrintDevice.Dispose`. The selected
old font is restored before the created font is deleted. The DEVMODE and DEVNAMES handles returned
by `PrintDlgW` are freed with `GlobalFree` even when the dialog succeeds, fails, or the job throws.
The E2E test checks that the process is still alive after cancel, which catches an obvious crash
from a double free; it does not attempt to measure GDI handle counts, because a short-lived test
process cannot prove a leak rate.

### Page width and wrapping

Margins are 0.75 inch on each side. The printable width is `HORZRES` minus the two margins, divided
by the measured character width. Lines longer than that column count are wrapped at the character
boundary, not at a word boundary, so a long registry value keeps every character and the report
stays aligned. The line height is the measured text height plus one fifth of that height, and a new
page starts before a line that would cross the bottom margin. If the measured character width is
zero, printing fails instead of looping with a zero-width column. A page that fits no line fails
with a clear error rather than printing an empty document forever.

## Huge subtrees and truncation

`FormatReport` stops at 1,000,000 characters by default. When the next block does not fit, the
report ends with a truncation marker and never exceeds the limit. Individual value data is also
capped at 512 characters through `RegistryValueDisplay.FormatData`, so one enormous binary value
cannot dominate the whole budget. The truncation rule is deterministic and applies to all value
types. Descendants are enumerated iteratively, so a deep tree cannot overflow the call stack during
report generation. The report is built before `StartDoc`; a registry failure therefore cannot leave
a started print job behind.

### Traversal cost

The service reads only keys reachable from the selected path. It opens each key with `KEY_READ` and
does not write, create, or delete anything. The 1,000,000 character cap bounds the text buffer, but
the traversal can still spend time enumerating a large subtree before the cap is reached. The work
runs on the thread pool, so the UI is not blocked while it runs. Closing the window does not cancel
the job; it uses the reader's snapshots and finishes or fails, then disposes its GDI objects.

## Registry errors during printing

### Access denied on the selected key

If the selected key cannot be opened or enumerated, `RegistryReader` throws
`RegistryOperationException`. `FormatReport` lets the selected key's failure propagate, and the
command returns a failure with the Win32 code and the reader's operation and path in the message.
No print job is started. The view shows the standard `ErrorDialog` with `DialogErrorTitle`, the
selected path, the code, and the message.

### Access denied on a descendant

A descendant that cannot be read does not abort the whole report. Its Subkey line is followed by
`(not readable: Win32 <code>)`, and traversal continues with the next sibling. This matches the
requirement to print readable descendants. A value that disappears between enumeration and its data
query is skipped by the reader's existing retry rule and does not create a partial line.

### Key or value deleted under the print job

The report is built from `RegistryReader` and then printed from the text. A key or value deleted
after the report is built does not change the paper, because the job does not read the registry
again. A key deleted before the report is built produces the access-denied or not-found path above.
The view never re-creates a missing key. A deletion that happens while the dialog is open is
visible in the report as the state at the moment the user pressed OK, and the tree refresh behavior
is unchanged.

## E2E behavior and cleanup

The Print test seeds only `HKCU\Software\BWC-E2E\<runId>`, launches the e2e flavor with an isolated
`BWC_DATA_ROOT` and `BWC_E2E_SANDBOX_ROOT`, and navigates to the seeded key. It records
`print.dialog`, `print.owner`, `print.cancel`, and `print.reenabled` and cancels the print dialog
through its Cancel button or Escape. On this build the dialog is the system
`ApplicationFrameWindow` host, so `print.owner` passes when the host has no HWND owner but the main
window is disabled, which is the modality contract the operating system provides. When a classic
`#32770` appears, `print.owner` also checks `GetWindow(GW_OWNER) == main` strictly.
It also records `print.menu.computer-disabled` and `print.menu.key-enabled` for the command state.
It never presses OK, so no spooler job is created and no PDF path is automated even when
"Microsoft Print to PDF" is the default printer. If the dialog does not appear because the machine
has no printer, the test records an informational skip with the observed state and still checks that
the application is alive and the main window is enabled. The session is disposed in a finally
block, the run subtree is removed, and the shared assembly cleanup removes the empty
`HKCU\Software\BWC-E2E` parent when possible. A stuck `BwcRegedit` process is killed by the session
dispose path, and the test never targets HKLM or another user hive.

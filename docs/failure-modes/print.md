# Print failure modes

Scope: File > Print in `src/Bwc.Regedit`, the `RegistryPrinter` in `src/Bwc.Regedit/Printing`, and
the Print E2E suite. This batch implements the R14 subset of MENU-009 (id 660), BEH-027, and DLG-016.
The parity inventory leaves much of the classic layout open in OQ-44, so this document records the
decisions taken and the failure behavior. The report is the classic text layout of `export.md`,
section 2, which the golden `docs/parity/oracle/exports/bwc-oracle-text.txt` pins, so print and
File > Export write the same text. The E2E tests never send a job to a physical printer. The E2E guard
refuses every device except Microsoft Print to PDF, and the PDF test is the only test that produces output
(see "E2E guard, progress window, and PDF test" below).

## Print range and report content

### Chosen range

The command prints the selected branch only. It does not implement the classic All and Selected
branch radio panel from DLG-016, and it does not offer a page range. The Windows print dialog is
therefore opened with `PD_NOSELECTION`, `PD_NOPAGENUMS`, `PD_RETURNDC`,
`PD_USEDEVMODECOPIESANDCOLLATE`, and `PD_HIDEPRINTTOFILE`. The Print to File check box is hidden
because the implementation prints through the device context that `PrintDlgW` returns.

Open (not implemented): the template 108 range panel (DLG-016) with All and Selected branch. The plan was
`PD_ENABLEPRINTTEMPLATEHANDLE`, but that flag replaces the whole dialog, and the standard printer controls would
go with it. A hook-based child panel was not validated, so the dialog is unchanged. The progress window (template
105) is implemented. The remote Key Name prefix (`\\127.0.0.1\HKEY_...`) and the remote print E2E test are also
open; the remote report walks the connection's store, but its Key Name lines still use the local path form.

### Report shape: one source with text export

`RegistryPrinter.WriteReport` calls `RegistryExportService.ExportTextFile` through
`RegistryTextExporter`, the same code that File > Export writes for the *Text Files (\*.txt)* filter.
There is no separate print formatter, and the report is never built as one string. For the same key,
the report and the text export are the same text (see the E2E parity check below). The shape is the
classic text layout of `export.md` section 2: `Key Name:`, `Class Name:`, `Last Write Time:`, then one
`Value n` block per value with `Name:`, `Type:`, and `Data:`, each block followed by a blank line. There
is no `Data Size:` line. Subkeys follow depth first in registry enumeration order. `<NO NAME>` and
`<NO CLASS>` are the empty-name forms.

The layout decisions in this batch are the page margins and font only. There is no footer, page
number, timestamp, or printer name, because OQ-44 leaves those open. Paper size, orientation, and
copies come from the printer's DEVMODE and the print dialog.

### Empty keys and roots

A selected key with no values prints its `Key Name:`, `Class Name:`, and `Last Write Time:` lines and
no `Value n` block. A key with no subkeys has no further key blocks. A predefined root may be
selected and printed; it is a valid `RegistryPath`, so the command is enabled. Computer has no
`RegistryPath`, so it prints with a null start key: the whole registry, every root, through the same
source (`RegistryPrinter.WriteReport`). Print is enabled at Computer, at a hive root, and at a key, local or
remote, as the oracle records (`docs/parity/oracle/regedit-print.json`). A remote key reads through its
connection's store, so the walk reads the remote computer.

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

File > Print is enabled when the tree selection is Computer or resolves to a key, local or remote
(`RegeditContext.UpdateCommandStates`). Ctrl+P is window-wide in the root key handler, so it also prints
from the address bar. The state is recomputed on every tree and list selection
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

## Huge subtrees, cancellation, and size

### Streamed, no size cap

The report has no character cap and no per-value cut. A 5000-character string is one complete line, and
a 4 KB binary value is 256 complete hex rows. The shared exporter writes into a page writer that draws
each line on the device and starts a new page when the next line would cross the bottom margin. Only
the current line is held in memory, so memory does not grow with the size of the subtree. Time still
grows with the size of the subtree.

The start key is read once with `GetKeyInfo` before `StartDocW`. A start key that cannot be read
returns its Win32 code, and no job is started. A registry error that happens later, during the walk,
stops the walk. The job is then aborted with `AbortDoc`, and the Win32 code is returned. Pages already
sent to the spooler are not kept.

### Traversal cost

The service reads only keys reachable from the selected path. It opens each key with `KEY_READ` and
does not write, create, or delete anything. The work runs on the thread pool, so the UI is not
blocked while it runs. Closing the window does not cancel the job; it uses the reader's snapshots
and finishes or fails, then disposes its GDI objects.

## Registry errors during printing

### Access denied on the selected key

If the selected key cannot be read, the start key check fails before `StartDocW`, as the text export
does for its start key. The command returns a failure with the Win32 code and the reader's operation and
path in the message. No print job is started. The view shows the standard `ErrorDialog` with
`DialogErrorTitle`, the selected path, the code, and the message.

### Access denied on a descendant

A descendant that cannot be read with error 2, 5, or 1018 is skipped together with its whole subtree,
as the text export does (`import-export.md`, section 18). The report does not carry a marker line for
it. The skip is silent, so the printed report can be missing a protected subtree. This is the same
known gap as the text export; it is not hidden from the user only because the report has no marker.

### Key or value deleted under the print job

The job reads the registry as it draws. A subkey deleted before the walk reaches it is skipped with its
subtree when the read fails with error 2, 5, or 1018. Any other read error aborts the job with
`AbortDoc`, as described above. The view never re-creates a missing key.

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

The report check (`Parity_PrintReport_UsesClassicLabels`) runs the E2E-only seam
`BWC_E2E_PRINT_REPORT_PATH`. The seam writes the report through `RegistryPrinter.WriteReport` to the named
file, and the spooler is skipped. The same test then runs File > Export as the Text Files filter on the
same key and compares the two. The comparison is exact text after one normalization: the export is
UTF-16LE with a byte-order mark and the report is UTF-8 without one. The seeded data includes a
5000-character REG_SZ and a 4 KB REG_BINARY. The string must be complete in both files, and the first and
last hex rows of the binary must be complete in both.

`RegistryPrinter.PrintToDevice` (the GDI page writer) has no automated test. A test would need a printer
device context, and the test rules forbid printing. The E2E checks cover the text that the page writer
draws, not its page layout.

## E2E guard, progress window, and PDF test

The E2E guard (`src/Bwc.Regedit/Printing/PrintE2EGuard.cs`, E2E flavor only) runs before any GDI call in
`RegistryPrinter.PrintJob`, and before CreateDC in `PrintNamedForE2E`. A job starts only when the device name is
exactly "Microsoft Print to PDF" and the DOCINFO output is a file under `BWC_E2E_PRINT_ARTIFACTS_DIR`. Any other
job logs "print refused by E2E guard" and returns a failure with no device context created. The probe seam
(`BWC_E2E_PRINT_GUARD_PROBE`) runs the guarded path for the default printer and the physical printers and writes
one line per name (`PrintGuardTests`). The probe checks `dcCreated=False`, so a refusal happens before CreateDC.

The progress window (template 105, `PrintProgressDialog`) is owned by the main window and shown while the job runs.
Cancel cancels the token. The spooler's abort proc (`SetAbortProc`, installed before StartDoc) returns FALSE when
cancelled, and the walk stops at its next line. The job then calls AbortDoc and returns Cancelled. In E2E builds
`BWC_E2E_PRINT_LINE_DELAY_MS` paces each line so a cancel test has time to press Cancel. Ship builds have no delay.

The PDF test (`PrintPdfTests`) is the only test that produces output. It first checks that "Microsoft Print to PDF"
exists with the "Microsoft Print To PDF" driver (EnumPrinters). If not, it records "not run" and passes. Otherwise
it prints through the guarded path to `artifacts/e2e/print/<run-id>.pdf` and checks the `%PDF-` header. The cancel
test uses the same printer and checks that no complete PDF (no `%%EOF`) and no spooler job remain.

## Parity package: classic report layout and Computer

| Way it could fail | Expected behavior |
| --- | --- |
| The report uses `Key:` and `Subkey:` text | Each key uses the classic text labels: `Key Name:`, `Class Name:` (or `<NO CLASS>`), `Last Write Time:`, then `Value n` with `Name:`, `Type:`, and `Data:`. No `Data Size:` line. E2E: `Parity_PrintReport_UsesClassicLabels` |
| The report differs from the text export of the same key | Equal text after the encoding normalization above. E2E: `Parity_PrintReport_UsesClassicLabels` |
| The report cuts long data or stops at a size | No cut and no cap. E2E: the 5000-character string is complete in both files, and the first and last rows of the 4 KB binary are complete |
| The report is built as one string before `StartDocW` | It is streamed through the page writer. No automated test (needs a printer device context) |
| A registry error during the walk leaves a half-written job | The job is aborted with `AbortDoc` and the Win32 code is returned. No automated test (needs a printer device context) |
| A start key that cannot be read still starts a job | It is checked with `GetKeyInfo` before `StartDocW`, and no job starts |
| The layout differs from the classic text layout | The printed text is the text export, which is the golden layout in `export.md` section 2. Layouts not in the golden are open points there |
| Print is disabled at Computer | Print is enabled at Computer, a hive root, and a key, local or remote. Computer prints with a null start key (the whole registry). E2E: `Print_OwnedDialogCancelsAndCommandStateFollowsSelection` |
| The labels come from `Resources.resw` | The labels are the text export's constants (`export.md` section 2). Strings 8101 to 8110 in `Resources.resw` are no longer used for printing, and 8107 (`Data Size:`) contradicts the golden |
| The E2E check sends a real job to the spooler | The E2E build writes the report through the shared source to an artifact file when `BWC_E2E_PRINT_REPORT_PATH` is set, and skips the spooler. Microsoft Print to PDF is not used |

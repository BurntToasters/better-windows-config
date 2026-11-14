# Import and export failure modes

This document covers `RegFileExporter`, `RegFileImporter`, the shared Registry Editor write
policy factory, and the `/s`, `/e`, `/a`, and `/m` command line switches. The parser and writer
rules stay in `reg-files.md`; this document adds the behavior that only appears when a document
touches the registry or a command line writes a file.

## 1. Export order

`RegFileExporter.Export` walks the start key depth-first in pre-order: the start key, its values,
then each subkey in registry enumeration order, recursively. It uses `RegistryReader`, so the
order is `RegEnumKeyEx` and `RegEnumValue` order, not the linguistic order the tree shows and not
a sorted order. The exporter never sorts, merges, or deduplicates.

Each key becomes exactly one `RegFileKey`, including a key with no values. An empty key section
is therefore present in the document even though it carries no value lines. A value keeps its
raw `uint` type and raw bytes, including the default value whose name is the empty string.

Key paths are written without the virtual `Computer\` prefix, for example
`HKEY_CURRENT_USER\Software\BWC-E2E\run`. The writer also removes that prefix if a caller hands
it one.

## 2. Access denied during export

An unreadable subkey is skipped together with its whole subtree. Its parent, its siblings, and
everything already emitted remain in the document. The skip is deliberate: one protected key
under `HKEY_LOCAL_MACHINE` must not abort an export of the rest of the tree, and classic regedit
continues after reporting the problem.

The start key is different. If the caller cannot read the key they asked to export, the export
throws `RegistryOperationException` with the Win32 code `ERROR_ACCESS_DENIED` (5) and the full
path. An empty file is not a useful answer for a request that could not read its root. Other
read failures, such as a start key that no longer exists, also throw and are not treated as a
skip.

Skipping is silent in the engine because `Bwc.Registry` has no logger and no user interface. The
command line reports success for the keys it could read; a document that is missing a protected
subtree is indistinguishable from one whose key was deleted between enumeration and read.

## 3. Huge trees and cancellation

The document is built in memory. A very large tree uses memory proportional to its keys, value
names, and raw value bytes, and the text writer then adds its own copy. There is no fixed size
cap in the exporter.

Export checks the cancellation token before every key, before reading that key's values, and
before every value is added. Cancellation throws `OperationCanceledException` and returns no
partial document. The command line exports into a `MemoryStream` and writes the file only after
the complete document has been serialized, so a canceled export leaves the output path alone.

Import checks cancellation between complete key sections, not inside one. The contract says an
import stops between two `.reg` entries, never inside one. A section that has already started is
finished, including all of its values, before cancellation is observed again.

## 4. Import order and partial writes

`RegFileImporter.Apply` applies key sections in document order. For each section:

1. If the section is a delete, `DeleteKey` runs with `recursive: true`, and
   `ERROR_FILE_NOT_FOUND` (2) is ignored. Any value lines attached to a delete section are
   ignored, matching the rule that a delete section only deletes.
2. Otherwise `CreateKey` runs. `RegCreateKeyEx` succeeds when the key already exists, so an
   existing key is reused and its other values are left alone.
3. Value lines in that section are applied in order. A delete value line calls `DeleteValue`
   and ignores only `ERROR_FILE_NOT_FOUND`. A normal line calls `SetValue` with the raw type and
   bytes.

An import is not transactional, and it does not stop at the first problem. A rejected write is
recorded in `RegFileImportResult.Failures` as a `WriteFailed` failure with its file line, key
path, value name, and Win32 error. The importer then continues with the next line, as classic
regedit does. A section whose key cannot be created or opened has its value lines skipped, and
the import continues with the next section. Writes that already succeeded stay applied. The write
policy runs before the native call, so a rejected entry causes no write.

`ApplyFile` reads the file with `RegFileReader.ParseFileLenient`. A line the grammar rejects is
skipped and recorded as a `MalformedLine` failure, and the rest of the file is applied. Only a
file-level problem refuses the whole file, and a refused file writes nothing. The refusal is a
`RegFileRefusal` code: `NotRegistryFile` for a missing or wrong header or an undecodable byte
order, `InvalidKeyPath` for a key path with an unknown root or a bad segment, and `ReadFailed` for
a file that cannot be opened or read. A document constructed by hand is not re-validated against
the file grammar, but every key path still goes through `RegistryPath.Parse`. A path that is not
an absolute registry path raises `ArgumentException` out of `Apply`, and the window and the
command line report it as a failed import.

Deleting a key that does not exist, deleting a value that does not exist, and creating a key
that already exists are all normal idempotent outcomes. Deleting a predefined root is rejected
by `RegistryWriter` as an argument error; a `.reg` file cannot contain that operation in a
successful import.

## 5. Delete lines

A key section `[-HKEY_CURRENT_USER\...]` deletes the key and everything below it. A value line
`@=-` or `"name"=-` deletes that value. Missing targets are ignored only for the two
file-not-found cases above. Access denied, a read-only key, or a policy denial is recorded as a
`WriteFailed` failure, and the import continues with the next line.

A value line that follows a delete key section is parsed but not applied. Classic files do not
produce that shape, and treating the section as a pure delete keeps the behavior predictable.

## 6. Missing keys and values

The importer creates every missing ancestor of a section's key before the section, top down, as
classic regedit does. A file that creates `HKCU\Software\A\B` without a section for
`HKCU\Software\A` therefore still imports. A predefined root always exists and is never created.
An ancestor that a concurrent writer creates first is not an error.

A value line under a key that the same file deleted earlier applies to the recreated key only if
a later section recreates it. Order matters and is preserved.

## 7. Write policy and sandbox denial

The Registry Editor and the command line share `RegeditWritePolicy.CreateStore`. In a shipping
build the policy is `ShippingWritePolicy`. In an E2E build it is `SandboxWritePolicy` rooted at
`BWC_E2E_SANDBOX_ROOT` when that variable holds a valid path under `HKCU\Software\BWC-E2E`, and
a deny-all root otherwise.

A `/s` file that names a key outside the sandbox is rejected by the policy before the native
call. The command line exits 1, logs the error, and writes a message to standard error when a
standard error handle exists. Earlier sections of the same file may already be applied, as
described in section 4. The E2E test proves the denied outside key does not exist.

## 8. File write failures

`/e` and `/a` serialize the complete document to memory first and then call `File.WriteAllBytes`.
A registry read failure therefore never truncates an existing file. The final file write can
still fail partway through because of a full disk, a quota, or a share violation; in that case
the process reports the error, exits 1, and the destination may be partial or absent. Export
does not modify the registry.

## 9. Encoding

The v5 formats, including `/e`, are UTF-16LE with a `FF FE` byte order mark and a CRLF header
`Windows Registry Editor Version 5.00`. REGEDIT4, including `/a`, has no byte order mark and is
encoded in the system ANSI code page from `GetACP()`.

Characters the ANSI code page cannot represent become `?` in a REGEDIT4 export. This is the
documented lossy behavior of classic regedit and is covered by `reg-files.md`. The v5 path keeps
the UTF-16 text.

The importer detects the format from the BOM and the first line. A file with no recognized
header, an odd UTF-16LE byte count, or a big-endian UTF-16 BOM is a refused file and applies
nothing. The window reports it with string 133, and the command line exits 1.

## 10. Unknown switches and arguments

Unknown switches, a missing file operand, extra positional arguments, and two operations in one
command line print the usage text to standard error and exit 2. No window is created and no
registry or file operation runs. A standard error handle may not exist for a GUI subsystem
process launched without redirection; the usage is still written to the log when logging is
available.

## 11. `/m`

`/m` opens a separate window. A launch with `/m` never joins the single-instance gate, so it does
not activate a window that is already running. A launch without `/m` activates the running window
instead of opening a second one, and a `.reg` path on that launch asks to import the file in the
running window. `/m` by itself continues to the normal window with the usual LastKey, shell, and
settings behavior. `/m` may appear before or after another switch; it does not change that
operation's exit code.

## 12. No window, exit codes, and flushing

`/s`, `/e`, and `/a` route before any `Window` is constructed. They do not create a XAML window,
do not activate one, and call `Environment.Exit` with the result. The normal launch path is
unchanged.

| Result | Exit code |
| --- | --- |
| Silent import succeeded, or skipped only malformed lines | 0 |
| Export succeeded | 0 |
| Normal window launched, including `/m` alone | process stays alive |
| Silent import refused (not a registry file, bad key path, unreadable file) | 1 |
| Silent import with at least one rejected write, including a policy denial | 1 |
| Export failed | 1 |
| Unknown switch or bad argument shape | 2 |

The oracle (`docs/parity/oracle/import-behavior.json`) records exit 0 for every file it could run,
including a file with a malformed line. It has no captured exit code for a refused file, a rejected
write, or a file it refused before running, so the exit 1 rows are BWC decisions. A future oracle
capture can confirm or change them.

In the window, a refused file shows string 133, 125, or 129 with the file path. A rejected write
shows string 132 after the rest of the file has been applied. A file that imports with only skipped
lines shows string 228. The window does not show a message for a skipped line.

The import progress window (DLG-019) has a caption, the `File:` label, the file name, and a progress bar.
It has no Cancel button and no default button. It is a native Win32 dialog built from an in-memory
`DLGTEMPLATEEX` with the classic style `0x90C001C4`, so it has no `WS_SYSMENU` and no Close box, as the oracle
has (see `classic-dialog.md` 4.7). The user cannot close the window while the import runs: Alt+F4 and the
window close requests are ignored, and the owner stays disabled. The host re-enables and activates the owner,
then destroys the window, when the import ends. The importer
still stops between sections when a caller passes a canceled token, but the window passes none, so a
window import always runs to the end and shows string 228, 132, or 133 for its result.

Errors go to the log through `Bwc.Core.Log` and to standard error when that handle is available.
The log writer flushes each entry itself; standard error is flushed before `Environment.Exit`
so a redirected parent sees the message.

## 13. Test cleanup

`tests/Bwc.Registry.Tests` uses `SandboxWritePolicy` and a unique run key under
`HKCU\Software\BWC-E2E`. Every test deletes that run key in a `finally` block. Tests never touch
the shipping registry and never export `HKEY_CURRENT_USER` or another live root.

`tests/Bwc.E2E/CommandLine` seeds values through `SandboxRegistry`, keeps temporary `.reg` files
under the E2E artifacts folder, and removes them in a `finally` block. The outside path used to
prove policy denial is `HKCU\Software\BWC-CLI-Outside`; the test asserts it does not exist and
removes it only if a failed run somehow created it. The E2E assembly cleanup removes the run
sandbox, and the test kills any `BwcRegedit` process that does not exit within its timeout.

## 14. The classic import and export dialogs

The window exports through the system `IFileSaveDialog`; it is never replaced by a custom window.
`IFileDialogCustomize` adds one visual group, "Export range", with two radio buttons, "&All" and
"S&elected branch" (oracle DLG-001). "Selected branch" is selected by default and exports the
selected key with `RegistryExportScope.Selection`. "All" selects
`RegistryExportScope.WholeRegistry`, which calls `RegFileExporter.ExportWholeRegistry` and writes
every classic root in root order. When the tree selection is the virtual Computer node, "All" is
selected and enabled, and "Selected branch" is visible but disabled, because Computer has no
registry path of its own. The shell cannot disable one item of an option group and keep the other
interactive, so Computer uses two single-item option groups inside the same visual group; the
branch group is disabled with `SetControlState`. The command also ignores the branch choice
whenever no key is selected, so a Computer export is always the whole registry. Selecting any key
uses one option group with both choices enabled and Selected branch selected by default. File >
Export stays enabled on Computer for this reason; for a remote machine node, which has neither a
local path nor a whole-registry meaning, the command keeps the existing selection message.

The save dialog registers three filter pairs:

| Name | Pattern | Format |
| --- | --- | --- |
| `Registration Files (*.reg)` | `*.reg` | v5, UTF-16LE with BOM |
| `Text Files (*.txt)` | `*.txt` | `RegistryTextExporter`, UTF-16LE with BOM |
| `Win9x/NT4 Registration Files (*.reg)` | `*.reg` | `REGEDIT4`, ANSI |

The first filter is selected when the dialog opens. The filter that is selected when the user
accepts the dialog chooses the writer. The open dialog registers only the `.reg` filter; an import
still parses whatever path the user types, so a non-registry file is refused with string 133 rather
than blocked by the dialog.

Classic regedit shows no success message after an export, and the oracle has no export success
string. BWC keeps that behavior: a completed export closes the progress dialog and shows no
message. The import result messages stay the oracle strings: 228 for success, 132 for a rejected
write or a cancel, 133 for a file that is not a registry file, 125 for an invalid key path, and 129
for a file that cannot be opened. String 128 is the remote-computer success text used by the
existing import path; it is unchanged.

The export progress dialog has a Cancel button. The import progress dialog (DLG-019) has no Cancel
button and no default button, as the oracle shows. Both run the registry work on the thread pool and
marshal progress back to the UI thread, so the window keeps painting and responding while a large
file is applied or exported. The import dialog reports completed key sections out of the total and is
driven by `IProgress<ImportProgress>`. The export dialog is driven by `IProgress<ExportProgress>` and
reports running key and value counts; Cancel requests the export token, which the engine checks before
each key, value, and subkey.

Export always writes to a temporary file in the destination folder and moves it into place only
after the writer finished. A canceled or failed export deletes that temporary file, so the
destination is absent or still has its previous contents; an existing destination is never
truncated by a canceled export. Import has no temporary file and no rollback: the completed writes of
an import stay applied, which is the classic behavior described in section 4.

In an E2E build with `BWC_E2E_SANDBOX_ROOT` set, "All" still calls
`RegFileExporter.ExportWholeRegistry`, but the source exposes the five classic roots with the
`HKEY_CURRENT_USER` subtree mapped to the run sandbox and the other roots empty. This keeps a GUI
test from reading the developer's whole registry into a test artifact and keeps the export bounded.
The production build uses the live reader and exports the real registry.

## 16. Import behind the write gate

File > Import of a .reg file, and a .reg file named on the command line or sent by a second launch,
apply the file under the write gate (`RunRegistryWriteAsync`). The gate disables the owner window and
holds a close request until the write ends, and the close then runs. Earlier builds ran the import as a
plain read on the thread pool, so closing the window during a large import let the process exit with
part of the file written.

Rule: a close during an import waits for the last key and value. The import is not cancelled, because
the DLG-019 progress window has no Cancel. A second launch that names another .reg file during the
import is queued (app-services.md, regedit file.reg).

Check: `ImportWriteGateTests.Import_CloseDuringImportWaitsForEveryKey` imports a 2000-key file, closes the
window as soon as the progress window is up, and checks that the process exits and that every key and
value is in the sandbox. The file is inside the run's artifact folder and writes only under the sandbox.

## Parity package: hive import, progress, and text

| Way it could fail | Expected behavior |
| --- | --- |
| Import with the hive filter when the process is not elevated | Refused with string 126, `Cannot import {0}: Insufficient privileges.`, and no restore runs. Registry Editor never self-elevates |
| Import with the hive filter on a remote node or on Computer | Refused with the same invalid-key text (string 125) or the read-only text; nothing is written |
| Import with the hive filter on a key that is not the one the user selected | The restore runs on the selected key only, after the classic confirmation (strings 55 and 56) |
| Restore confirmation is declined | No restore; the key is unchanged |
| Import progress shows a Cancel button | Classic DLG-019 has no Cancel button and no default button, so the import cannot be cancelled from the progress window |
| Import progress caption has a Close box, or Alt+F4 closes the window | Classic DLG-019 (style `0x90C001C4`, no `WS_SYSMENU`) has neither. The native window has neither, and its close requests are ignored until the host destroys it. E2E: `importprogress.style`, `importprogress.no-close-button`, `importprogress.altf4` |
| Import progress is a WinUI window, so the style is changed by WinUI | DLG-019 is a native dialog (`#32770`) from the classic template. E2E: `importprogress.class`, `importprogress.size` (240 x 59 DLU at the window DPI, within 1 px per edge) |
| Import progress in dark mode shows a light caption, face, or text | The caption uses the immersive dark attribute, and the face and text use the BWC dark palette. E2E: `importprogress.dark.caption`, `importprogress.dark.face` |
| Import progress window stays open or the main window stays disabled after the import | The owner is re-enabled and activated before the window is destroyed. E2E: `importprogress.main-enabled` |
| Alt+F4 or a title-bar close during an import closes the progress window or re-enables the main window | The close is refused while the import runs; the window stays open and the main window stays disabled. The host closes the window when the import ends |
| Import progress caption is the product name | Caption is string 300, `Import Registry File`, and the label is `File:` |
| Status texts are hard-coded in code | All status and caption text comes from `Resources.resw` |
| Export progress and import progress texts are inline | Moved to `Resources.resw` with the same wording |
| E2E finds the import progress window by the title `Registry Editor` | The import progress caption is `Import Registry File`. The lookup finds the import window by its native class (`#32770`) and its `msctls_progress32` child (id 101); the export window is still found by its `ProgressBar` automation id |
| E2E export cancel runs against a branch that finishes in about two seconds | The cancel branch seeds 5000 keys and the E2E-only pause `BWC_E2E_SLOW_EXPORT_MS=2` (per key) keeps the export running until Cancel |
| E2E pause variables are read in the ship build | `BWC_E2E_SLOW_EXPORT_MS` and `BWC_E2E_SLOW_IMPORT_MS` are read only under `#if BWC_E2E`; the ship build never reads them and runs at full speed |
| E2E pause variable is not a number, is negative, or is too large | Non-numeric or negative values mean no pause; values above 1000 are clamped to 1000 ms per key or section |
| Cancel arrives during the export pause | The pause waits on the cancellation token, so Cancel ends the wait at once; the export stops and the partial file is deleted |
| An E2E seed grows the user's `NTUSER.DAT` without a bound | Test seeds are capped at 5000 keys; the import file has 1000 sections and the export branch has 5000 keys |
| E2E import test checks the cancel controls | Replaced: the test checks the classic window (caption `Import Registry File`; no push button in the body; Alt+F4 refused with the main window still disabled; progress bar read at least twice with a strict increase while the window is open; main window disabled while it runs; completion message; imported keys; the native class, size, and style). The caption Close box and `GWL_STYLE` are plain checks, not known gaps |
| E2E progress check passes on the first partial value | The bar is read until two readings in the range 0 to maximum show an increase, so one reading at 1 of 1000 is not enough. The E2E pause is 10 ms per section, so the 1000-section import runs for about 10 s and stays open long enough to read |
| Yes is pressed on the confirmation before it is under the pointer | The E2E harness presses only when the confirmation is foreground, its button is enabled and stable, and the window at the pointer is the confirmation; see `classic-dialog.md` 1.10 |

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

An import is not transactional. If a native call fails in the middle of a section, the calls
that already succeeded stay applied. That can leave a key with some of its values updated. The
importer stops at that point and does not start the next section. The same is true when the
write policy rejects a path: the policy runs before the native call, so the rejected entry
causes no write, but earlier entries stay applied.

`ApplyFile` parses the whole file with `RegFileReader.ParseFile` before applying anything. A
malformed file, an unreadable file, or a bad path in the document therefore causes no registry
change at all. A document constructed by hand is not re-validated against the file grammar, but
every key path still goes through `RegistryPath.Parse`; a path that is not an absolute registry
path raises `ArgumentException`.

Deleting a key that does not exist, deleting a value that does not exist, and creating a key
that already exists are all normal idempotent outcomes. Deleting a predefined root is rejected
by `RegistryWriter` as an argument error; a `.reg` file cannot contain that operation in a
successful import.

## 5. Delete lines

A key section `[-HKEY_CURRENT_USER\...]` deletes the key and everything below it. A value line
`@=-` or `"name"=-` deletes that value. Missing targets are ignored only for the two
file-not-found cases above. Access denied, a read-only key, or a policy denial is reported as
`RegistryOperationException` and stops the import.

A value line that follows a delete key section is parsed but not applied. Classic files do not
produce that shape, and treating the section as a pure delete keeps the behavior predictable.

## 6. Missing keys and values

The importer does not invent missing ancestors. Each section creates exactly its own path, so a
file that creates `HKCU\Software\A\B` without a section for `HKCU\Software\A` fails when
`CreateKey` tries to open the missing parent. Classic regedit writes every ancestor section, so
a well-formed export always imports.

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
header, an odd UTF-16LE byte count, or a big-endian UTF-16 BOM is a parse failure and applies
nothing.

## 10. Unknown switches and arguments

Unknown switches, a missing file operand, extra positional arguments, and two operations in one
command line print the usage text to standard error and exit 2. No window is created and no
registry or file operation runs. A standard error handle may not exist for a GUI subsystem
process launched without redirection; the usage is still written to the log when logging is
available.

## 11. `/m`

`/m` is accepted as a no-op for multiple instances. This build has no single-instance gate, so
there is nothing to disable. `/m` by itself continues to the normal window with the usual
LastKey, shell, and settings behavior. `/m` may appear before or after another switch; it does
not change that operation's exit code.

## 12. No window, exit codes, and flushing

`/s`, `/e`, and `/a` route before any `Window` is constructed. They do not create a XAML window,
do not activate one, and call `Environment.Exit` with the result. The normal launch path is
unchanged.

| Result | Exit code |
| --- | --- |
| Silent import succeeded | 0 |
| Export succeeded | 0 |
| Normal window launched, including `/m` alone | process stays alive |
| Import or export failed, including a policy denial | 1 |
| Unknown switch or bad argument shape | 2 |

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

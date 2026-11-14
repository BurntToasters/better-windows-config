# Export format failure modes

This document covers `RegistryExportService`, `RegFileExporter`, `RegFileWriter`,
`RegistryTextExporter`, and `RegistryHiveExporter`. The three files in
`docs/parity/oracle/exports` remain the byte-level specification for `.reg` output. Classic
regedit has no captured text-export golden, so section 2 fixes the stable text format BWC writes
until an oracle capture replaces it. The package E5 contract is in
`docs/contracts/registry-engine.md`.

## 1. Entry points

- `RegFileExporter.Export` builds a `.reg` document for one subtree. The v5 format is the
  default.
- `RegFileExporter.ExportWholeRegistry` builds one document for all five roots classic regedit
  shows under `Computer`: `HKEY_CLASSES_ROOT`, `HKEY_CURRENT_USER`, `HKEY_LOCAL_MACHINE`,
  `HKEY_USERS`, and `HKEY_CURRENT_CONFIG`, in that order.
- `RegistryTextExporter.Export` streams the text format for one subtree.
  `RegistryTextExporter.ExportWholeRegistry` streams the same format for every classic root.
- `RegistryHiveExporter.Save` saves one subtree to a hive file through
  `RegistryWriter.SaveKey`, so the write policy, `SeBackupPrivilege`, `SeRestorePrivilege`, and
  the `REG_OPTION_*` checks behave exactly as they do for the existing hive tests.
- `RegistryExportService` is the UI-facing facade. A `RegistryExportScope` of `Selection`
  exports the selected key. `WholeRegistry` exports every classic root and is what a `Computer`
  selection uses. A selected predefined root exports that root's subtree, matching classic
  regedit, not the whole registry.
- `RegFileFormat.V5` is the default on every API. `RegFileFormat.Regedit4` selects the older
  header and ANSI encoding described in `reg-files.md`.

## 2. Stable text format

Classic regedit's *Text Files* export has no tracked oracle golden (inventory row EXP-032 and
OQ-34). BWC therefore fixes this format so the output is deterministic across machines:

```
Key Name:          HKEY_CURRENT_USER\Software\Bwc\Test
Class Name:        <NO CLASS>
Last Write Time:   2026-11-11 12:34:56 UTC

Value 0
  Name:            <NO NAME>
  Type:            REG_SZ
  Data:            default text

Value 1
  Name:            Text
  Type:            REG_SZ
  Data:            hello

Key Name:          HKEY_CURRENT_USER\Software\Bwc\Test\Child
...
```

Rules:

- The file is UTF-16 little-endian with a `FF FE` byte order mark and CRLF line endings, the
  same encoding as the v5 `.reg` format.
- `Key Name:`, `Class Name:`, and `Last Write Time:` start in column one. Their values start at
  column 20. `Value N` starts in column one, and `Name:`, `Type:`, and `Data:` are indented two
  spaces with their values starting at column 20.
- Key paths use the registry-style spelling, without `Computer\`. The writer removes one leading
  `Computer\` prefix in any casing.
- A key with an empty class name prints `<NO CLASS>`. A value with an empty name prints
  `<NO NAME>`.
- The last write time is the key's `LastWriteTimeUtc` formatted as
  `yyyy-MM-dd HH:mm:ss` followed by ` UTC`. It is the UTC time, not the local time classic shows.
- One blank line follows the three key metadata lines. Every value block is followed by one
  blank line. A key with no values therefore has one blank line between its metadata and the next
  key.
- Values appear in registry enumeration order and keys are written depth first in pre-order. The
  exporter never sorts. This is the same order as `.reg` export, not the tree's linguistic order.
- A text export is a readable snapshot, not an import format. Use `.reg` or a hive file when the
  data must be applied again.

## 3. Text data rendering and lossiness

The text format is deliberately display-oriented and is lossy in these cases:

- `REG_SZ`, `REG_EXPAND_SZ`, and `REG_LINK` stop at the first NUL, as the Data column does. Bytes
  after that NUL are not shown.
- Backslash, NUL, carriage return, line feed, and tab in a value name or string are escaped as
  `\\`, `\0`, `\r`, `\n`, and `\t`. Other characters are written literally.
- `REG_MULTI_SZ` is shown as a comma-separated list of quoted items, for example
  `"one", "", "two"`. This preserves empty items but not the exact terminator byte count.
- A valid four-byte `REG_DWORD` and an eight-byte `REG_QWORD` use the Data-column rendering:
  `0x%08x (%u)` with lowercase hex and an unsigned decimal value.
- A DWORD or QWORD of the wrong length, `REG_DWORD_BIG_ENDIAN`, `REG_NONE`, the resource types,
  and unknown types show uppercase hex bytes in stored order, separated by spaces.
- Empty string-like data prints `(value not set)`. Empty binary-like data prints
  `(zero-length binary value)`.
- Registry security descriptors, key class names beyond the string, volatile state, and the
  distinction between a missing default value and a zero-length default value are not part of
  the text format.
- A value name with an embedded NUL is representable in text because the NUL is escaped. It is
  not representable in a `.reg` file (section 7).

## 4. Streaming, cancellation, and partial text files

Text export writes each key block directly to the caller's `TextWriter` and retains only the
current key's value and subkey lists. It never builds the whole document, so a large subtree does
not multiply its memory use by the size of the output. The recursive walk uses the process call
stack; the registry depth limit is 512.

Cancellation is checked before the start key, before reading each key, before each value, and
before each subkey. A canceled text export throws `OperationCanceledException`. Because the
writer is streaming, text already written stays in the destination. The UI and the command line
must write to a temporary file and move it into place after success. A `.reg` export builds the
document first, so cancellation returns no document and the command line leaves the destination
alone. That describes the `RegFileExporter.Export` overload that returns a document. The streamed
`WriteRegFile` path is the one described in the paragraph above.

The hive save calls a single native `RegSaveKey` after its own cancellation check. Cancellation
cannot interrupt a save that has already started. The file is created by that call, so a native
failure can leave a partial file at the destination; the writer refuses an existing destination
before starting, and the caller deletes a failed save.

A `.reg` export streams too. `RegFileExporter` hands each finished key block to a writer, and
`RegistryExportService.WriteRegFile` writes it straight to the stream, so the whole document is never
held in memory. The bytes written are identical to `RegFileWriter.Write` on the built document; the
golden tests compare both paths. Cancellation stops between two key blocks and throws
`OperationCanceledException`. A `.reg` file that was being streamed is therefore partial after a
cancel or a failure. A start key that cannot be read leaves the stream empty, because the header is
written with the first key section. The UI and the command line must write to a temporary file and
move it into place after success, as for text export.

## 5. Progress

`ExportProgress` reports `Starting`, `KeyExported`, `ValueExported`, `KeySkipped`, and
`Completed` phases. In a `.reg` export, `KeyExported` and `ValueExported` are throttled to at most
one report every 100 ms. `Starting`, `KeySkipped`, and `Completed` are never throttled, and
`Completed` always carries the final running totals, so a caller that keeps the last report sees
the exact counts. Text export reports every event. It carries the current key path, the value name for a value event or null,
and running counts of exported keys, exported values, and skipped keys. Reports are synchronous
on the exporting thread. A progress handler that throws stops the export with that exception;
callers should keep handlers short and non-throwing. The counts are shared across every root of a
whole-registry export and are not reset at a root boundary.

The existing `RegFileExporter.Export` overloads keep their parameter order. The progress argument
is optional and comes last, so every existing caller compiles and behaves as before.

## 6. Whole-registry export and visibility

`ExportWholeRegistry` walks the five classic roots in the order listed in section 1. It uses the
same per-key rules as a subtree export:

- A subkey that cannot be opened because of access denied is skipped with its whole subtree. The
  parent, the siblings, and everything already written remain.
- A subkey that vanishes during the export is skipped the same way. The Win32 codes are
  `ERROR_FILE_NOT_FOUND` (2) and `ERROR_KEY_DELETED` (1018), raised when a key is deleted between
  its parent's listing and its own read. A start key that vanishes still throws.
- An inaccessible root is skipped, and the remaining roots are still exported. This is the
  visible behavior of classic regedit: another user's loaded `HKEY_USERS` hive or a protected
  `HKEY_LOCAL_MACHINE` branch must not abort the export of the other roots.
- The first root is not special in a whole-registry export. For a single-root or subtree export,
  a start key that cannot be read throws `RegistryOperationException` instead of producing an
  empty file.
- A read failure other than access denied, or the two vanished-key codes above, still throws. It is
  not treated as a skip.
- A value whose name contains an embedded NUL aborts a `.reg` whole-registry export with
  operation `ExportValueName` and Win32 error 123, exactly as a subtree export does. Text export
  escapes the NUL instead.
- `HKEY_CLASSES_ROOT` is the merged view Windows presents, so its export repeats the content of
  `HKEY_LOCAL_MACHINE\Software\Classes` and `HKEY_CURRENT_USER\Software\Classes`. Classic
  regedit exports the merged view, and BWC does the same. The registry's symbolic link keys are
  followed, so a link key exports the target subtree under the link's path.

Whole-registry `.reg` output is one document, not one file per root. It starts with one v5 or
REGEDIT4 header and then every key section in root order. Duplicate key paths cannot occur across
roots because every path includes its root.

## 7. What classic `.reg` files cannot represent

The `.reg` writer follows the goldens and the contract even when that is lossy:

- A value name with an embedded NUL cannot be addressed by Windows and cannot be written to a
  `.reg` file. The exporter throws instead of silently truncating the name.
- `REG_SZ` data after the first NUL is dropped by the writer. A zero-length string and a
  two-byte empty string both export as `""` and import as a two-byte empty string.
- REGEDIT4 conversion uses the system ANSI code page. Characters outside that code page become
  `?`, and string-like bytes are converted to ANSI before hex formatting.
- A key name containing `]` cannot round-trip. The writer emits it literally, as classic does. The
  import reads the last `]` on the header line and refuses the `]` that remains in the path, so
  BWC cannot re-import such a key. The oracle pins that refusal (`reg-files.md` section 4).
- A key name containing a NUL, line feed, or carriage return cannot be written. The exporter
  refuses it with `ExportKeyName` and Win32 error 123, before any line of that key is written.
- A `REG_SZ` whose text contains a line feed or carriage return is written as `hex(1):` with its
  raw bytes. A value name with a line feed or carriage return is refused like a NUL name
  (`reg-files.md` section 5). These two rules have no golden; they are the safe round-trip choice.
- The v5 format preserves every raw value type and byte length that a value name can address.
  A hive file is the only export that also preserves key security and the tree as a whole.

## 8. Hive export

`RegistryHiveExporter.Save` and `RegistryExportService.ExportHiveFile` accept a writer, a key
path, a destination path, a cancellation token, and optional progress. They reject a predefined
root before any native call, refuse an existing destination with `ERROR_FILE_EXISTS` (183), and
refuse a missing parent directory with `ERROR_PATH_NOT_FOUND` (3). Those checks are in
`RegistryWriter.SaveKey` and are not duplicated.

The save needs a token that holds both `SeBackupPrivilege` and `SeRestorePrivilege`; without them
the call fails with `ERROR_PRIVILEGE_NOT_HELD` (1314) before the native save. A sandbox policy
still limits which keys may be saved. A saved file starts with the `regf` signature. Restoring it
with `RestoreKey`, or loading it at a direct child of `HKEY_LOCAL_MACHINE` or `HKEY_USERS` when
the process has the privileges, reproduces the saved subtree. The unit test suite proves save and
restore inside `HKCU\Software\BWC-E2E`; the mount half runs only in the elevated suite because it
needs the hive privileges and a mount point outside the sandbox.

## 9. Tests and repeatable artifact

`tests/Bwc.Registry.Tests` proves:

- the two v5 goldens and the REGEDIT4 golden are reproduced byte for byte, including the v5
  default;
- the text format's key, value, type, and escaping rules;
- whole-registry output covers all five classic roots;
- cancellation stops before native work and progress reports every exported key and value;
- hive save and restore round trip inside the sandbox in an elevated test, with a non-elevated
  check that cancellation is observed before `RegSaveKey` and that a missing privilege leaves no
  file.

The artifact test writes `artifacts/export-formats/result.json` with the case names, text output,
whole-registry root list, and SHA-256 of each golden comparison. Re-running the test rewrites the
same content.

## Dot-named keys, text export skips, and progress volume (regression set)

### A key named "." or ".." stops the whole export

A registry key name may be `.` or `..`. The walk builds child paths with `RegistryPath.Append`, which
rejects those names, so the export throws `ArgumentException` and the .reg or text file is never
finished. Walks now build child paths from enumerated names with `AppendEnumeratedName`, which
accepts `.` and `..` and still refuses separators, NUL, and empty or overlong names. User-typed
paths keep using `Append`, which still refuses them. Note that the .reg reader refuses such a key
on import (`reg-files.md`), so only export is fixed here. Test: `RegFileExportWritesKeysNamedDotAndDotDot`
and `TextExportWritesKeysNamedDotAndDotDot`.

### The text export skips fewer error codes than the .reg export

The .reg export skips a child subtree when the open fails with error 2 (file not found), 5 (access
denied), or 1018 (key deleted). The text export skipped only 5, so one key that vanished during the
walk aborted the whole text export. Both exports now use the same skip set. Test:
`TextExportSkipsChildrenThatVanishedOrWereDeletedLikeTheRegExport`.

### Text export posts progress for every key

Failure mode: the text export reported progress per key, so a hive with a million keys posted a
million dispatcher messages and stalled the UI. Key progress is now throttled to about one report per
100 ms, like the .reg export. The Starting and Completed reports always go out and carry the final
counts. Test: `TextExportThrottlesKeyProgressReports`.

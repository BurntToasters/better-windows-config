# Export format failure modes

This document covers `RegistryExportService`, `RegFileExporter`, `RegFileWriter`,
`RegistryTextExporter`, and `RegistryHiveExporter`. The three files in
`docs/parity/oracle/exports` remain the byte-level specification for `.reg` output. Classic
regedit has a captured *Text Files* golden, so section 2 pins the classic text layout that BWC writes.
The package E5 contract is in
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

## 2. Classic text format (Text Files *.txt)

The golden is `docs/parity/oracle/exports/bwc-oracle-text.txt`. It is a real classic regedit *Text Files*
export of `HKCU\Software\BWC-Oracle`, written from the seed `bwc-oracle-text-seed.reg`. It is UTF-16LE
with a BOM and CRLF line ends, and every `Last Write Time:` value is masked as `<TIME>`. The golden test
(`tests/Bwc.Registry.Tests/RegistryTextGoldenTests.cs`) imports the seed under a sandbox run key, exports
it with `RegistryTextExporter`, masks and rewrites the path, and compares the bytes. The golden wins over
The golden test needs one policy exception: the sandbox refuses REG_LINK values, and the seed has one (open point 9).
Any other statement in this document is secondary to the golden.

Shape (`<NO CLASS>` and `<NO NAME>` are the empty-name forms):

```
Key Name:          HKEY_CURRENT_USER\Software\BWC-Oracle
Class Name:        <NO CLASS>
Last Write Time:   <TIME>
Value 0
  Name:            <NO NAME>
  Type:            REG_SZ
  Data:            Default value set

Value 1
  Name:            Other
  Type:            REG_SZ
  Data:            non-default sibling


Key Name:          HKEY_CURRENT_USER\Software\BWC-Oracle\dotted.key.name
```

Rules:

- Key block: three header lines (`Key Name:`, `Class Name:`, `Last Write Time:`, each label padded to
  19 characters), no blank line after them, then the value blocks, then one blank line. A key with no
  values is the three header lines and one blank line.
- Keys are written depth first in pre-order, subkeys in registry enumeration order. The exporter never
  sorts and never hoists the default value. Values are numbered `Value 0`, `Value 1`, ... in enumeration
  order.
- Value block: `Value n`, then `  Name:            ` (19 characters), `  Type:            `, and
  `  Data:            ` (padded to 19 characters even when the data is empty), then the data lines, then
  one blank line.
- No `Data Size:` line. No escaping: names, class names, and string data are written as stored.
- An empty value name prints `<NO NAME>`. An empty class name prints `<NO CLASS>`.
- Type names: `REG_NONE`, `REG_SZ`, `REG_EXPAND_SZ`, `REG_BINARY`, `REG_DWORD`, `REG_LINK`,
  `REG_MULTI_SZ`, `REG_RESOURCE_LIST`, `REG_FULL_RESOURCE_DESCRIPTOR`,
  `REG_RESOURCE_REQUIREMENTS_LIST`, `REG_QWORD`. Every other type, including 5
  (`REG_DWORD_BIG_ENDIAN`), prints `REG_UNKNOWN`.
- `Last Write Time:` is the key's last write time in local time, written as the current culture's short
  date, then ` - `, then the short time without seconds. en-US gives `11/14/2026 - 7:25 PM`. The golden
  masks this value, so the rule is not measured for other cultures (open point 1).
- The file ends after the last key's blank line, so it ends in `CRLF CRLF CRLF`.

Data lines by type. The first line goes after `  Data:            `; later lines are whole lines.

- `REG_SZ`, `REG_EXPAND_SZ`: the text up to the first NUL. Empty text leaves the label alone.
- `REG_MULTI_SZ`: split on NUL. Trailing empty strings (the terminators) are dropped. The first string goes
  on the Data line, and each later string goes on its own line indented 19 spaces. An empty middle string
  is a line of exactly 19 spaces. `one\0\0three\0\0` prints `one`, 19 spaces, `three`. An empty list
  (`00 00`) leaves the label alone.
- `REG_DWORD`: C `%#x`. Zero prints `0`; anything else prints `0x` and lowercase hex with no padding
  (`0x1234`, `0xffffffff`). Data shorter than four bytes is zero-extended: `01 02 03` prints `0x30201`.
- `REG_BINARY`, `REG_NONE`, `REG_LINK`, `REG_QWORD`, `REG_DWORD_BIG_ENDIAN`, and unknown types: a hex dump.
  Empty data leaves the label alone and has no rows.
- Hex dump rows start at column 0 and have 16 bytes each. A row is an 8-digit lowercase offset, three
  spaces, then `xx ` for each byte (lowercase). After the 8th byte of the row an extra `- ` is written, even
  when the row has exactly 8 bytes, and never for fewer. The hex field is padded with spaces to 50
  characters (columns 11 to 60), then one space, then the ASCII column at column 62, which is not padded.
  ASCII: bytes 0x00 to 0x08, 0x0A to 0x1F, and 0x7F to 0x9F print `.`; 0x09 prints a TAB; every other byte
  prints the character with the same code point (0x20 to 0x7E and 0xA0 to 0xFF).
- Resource types 8, 9, and 10 leave the Data label alone and print the structure below. If the parse
  fails, the whole value is a hex dump instead (open point 4).

Resource layouts (`REG_RESOURCE_LIST` is 8, `REG_FULL_RESOURCE_DESCRIPTOR` is 9,
`REG_RESOURCE_REQUIREMENTS_LIST` is 10). Interface type names are the classic bus names, and any value
above 11 prints `Invalid`. Bus and slot numbers are signed decimal, as the UI viewers show them.

- Full descriptor fields at column 21, labels padded to 19: `Interface Type:`, `Bus Number:`, `Version:`,
  `Revision:`. A resource list puts `Full Resource Descriptor n` at column 19 before them.
- Partial descriptor: `Partial Descriptor n` at column 21, its fields at column 23 with labels padded to
  17: `Resource:` and `Disposition:` (share disposition name), then:
  - Memory: `Start:` as `0x%08x`, `Length:` as bare hex, `Type:` as `Read / Write`, `Read Only`, or
    `Write Only` from the flags.
  - Interrupt: `Vector:` and `Level:` as decimal, `Affinity:` as `0x%08x`, `Type:` as `Latched` or
    `Level Sensitive` from the flags.
- Requirements list: header fields at column 19, labels padded to 21: `Interface Type:`, `Bus Number:`,
  `Slot Number:`. `List n` at column 19, `Descriptor n` at column 21, and descriptor fields at column 23,
  labels padded to 17: `Resource:`, `Option:` (`0x%08x`), `Disposition:`, `Type:`, then:
  - Port: `Length:` and `Alignment:` as `%#x`, `Minimum Address:` and `Maximum Address:` as `0x%08x`,
    `Type:` as `Port` or `Memory` from the flags.
  - Memory: `Length:` and `Alignment:` as bare hex, `Minimum Address:` and `Maximum Address:` as
    `0x%08x` (open point 2), `Type:` as `Read / Write` and so on.
  - Interrupt: `Minimum Vector:` and `Maximum Vector:` as `%#x`, `Type:` as `Latched` or `Level Sensitive`.
- A blank line follows each partial descriptor, each full descriptor, each requirement descriptor, and each
  list. The value's own blank line follows after that.

Open points. The golden does not measure these, so the rule shown is BWC's own:

1. Time format outside en-US. The golden masks the time. The `d - t` rule comes from the string table and
   the spec, and the separator and AM/PM text for other cultures are not measured.
2. Memory requirement Minimum. In the seed, Length equals Alignment (0x1000), so the golden cannot tell
   whether classic's `Minimum Address:` shows Length or Alignment. BWC assumes Minimum is the DWORD at
   offset 4 of the union (Alignment) and Maximum is the low DWORD at offset 8. The golden's Maximum is
   `0xfed00000`, the low DWORD of the minimum and not the maximum `0xfed00fff`, so BWC keeps that
   quirk. Port requirements use the plain fields (Minimum at 8, Maximum at 16) and match the golden.
3. Port, DMA, device-specific, bus-number, and large-memory layouts are not in the golden. A partial
   descriptor follows the field list of the matching UI viewer (`ResourceDescriptorDialog`): Port prints
   Start, Length, Type; DMA prints Channel and Port; Device Specific prints Reserved1, Reserved2, Data
   Size; large memory and bus number print Resource and Disposition only. A requirement of any type other
   than Port, Memory, or Interrupt prints Resource, Option, and Disposition only.
4. A resource value that does not parse prints as a hex dump of the whole value. Bytes after the last
   declared structure are ignored. Both are unmeasured.
5. A DWORD of length 0 or more than 4 bytes is unmeasured. BWC zero-extends the first four bytes. The
   golden only has the 3-byte case.
6. A raw CR or LF inside string data is written as stored and so starts a new line. The golden has no such
   case.
7. String 8107 in `src/Bwc.Regedit/Strings/en-US/Resources.resw` is `  Data Size:       `. It contradicts the
   golden. The golden wins, and the text format no longer uses the string.
8. The resource names (bus names, share dispositions, partial types, memory access, interrupt and port
   types) are English constants in `src/Bwc.Registry/Resources/ResourceNames.cs`. They are not localized.
   `Bwc.UI` keeps its own copy in `ResourceText`. `Bwc.UI` has no project reference to `Bwc.Registry`, and the
   two tables must be kept equal by hand.
9. The sandbox write policy (`SandboxWritePolicy`) refuses REG_LINK values, because `AllowsLinkValues` is
   false. The golden seed has one (`OddTypes\Link6`). `RegistryTextGoldenTests` wraps `SandboxWritePolicy` in
   a policy that allows that one value type, and every path is still decided by `SandboxWritePolicy`. The
   product policy is unchanged, and the manager should decide whether the test-only exception stays.

## 3. Text lossiness

The text format is for reading, and it loses information in these cases:

- `REG_SZ` and `REG_EXPAND_SZ` stop at the first NUL, so bytes after it are not shown.
- `REG_MULTI_SZ` drops its trailing terminators, so the exact byte count is not recoverable.
- A short `REG_DWORD` is zero-extended, so its stored length is not shown.
- Binary-like data is shown as hex, so the type of a value that stores text as bytes is not shown beyond
  its `Type:` line.
- Registry security descriptors, volatile state, and the difference between a missing default value and
  a zero-length default value are not part of the text format.
- A value name or string with an embedded NUL, tab, or line break is written as stored. Such a value is not
  representable in a `.reg` file (section 7).

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
  writes the name as stored, NUL included.
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
- the text format's key, value, type, data, and resource rules (section 2);
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

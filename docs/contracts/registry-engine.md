# Contract: registry engine (`Bwc.Registry`)

This contract fixes the behaviors that more than one work package depends on. Packages B2 to
B11 and R1 to R14 must follow it. A change to this file is a manager decision.

The governing rule: **behave like classic regedit, which uses the documented Win32 registry
API.** Where this contract and classic regedit disagree, classic regedit wins and this file is
corrected. Where regedit's behavior is unknown, the item is marked *verify against oracle* and
the owning package records the observed behavior as a golden before implementing it.

## Layering and threading

- `Bwc.Registry` has no reference to WinUI, `Bwc.UI`, or any dispatcher. It is usable from a
  console process (command-line `/s`, `/e`, `/a`) and from tests.
- All engine calls are synchronous and may run on any thread. The UI calls them on the thread
  pool and marshals results back with `DispatcherQueue`. The engine never blocks waiting on the
  UI.
- Every operation that loops over keys or values (enumerate, find, export, import, and the
  permission propagation walk) takes a `CancellationToken` and checks it at least once per key.
  Cancellation throws `OperationCanceledException` and leaves no partial write in progress: an
  import stops between two complete `.reg` entries, never inside one.
- `DeleteKey(recursive: true)` and `CopyTree` are each one native call (`RegDeleteTree`,
  `RegCopyTree`) and take no `CancellationToken`: a started native call cannot be interrupted. The
  sandbox link walk that runs before them is bounded by the 512-level depth limit. A cancelled
  delete or copy is therefore not offered, and the UI must not show a cancel control for them.
- Key handles are wrapped in `SafeRegistryHandle`-style owners. A handle is never shared between
  two concurrent operations; each operation opens what it needs.
- Results are snapshots. The engine does not watch for changes; the view refreshes on F5, as in
  regedit.

## Errors

- Every native failure becomes `RegistryOperationException` carrying the Win32 error code, the
  operation, and the full key path. Display text is chosen by the UI (it maps the code to the
  regedit message string from the oracle resource dump); the engine never formats user-facing
  text.
- Access denied on a read is a normal outcome, not a crash: an unreadable subkey still appears
  in the tree, and opening it shows regedit's error.

## Names

- Key and value names are .NET `string` values obtained through the Win32 API
  (`RegEnumKeyExW`, `RegEnumValueW`). The engine does not use `NtEnumerateKey` or counted-string
  native APIs. Keys whose names contain an embedded NUL are therefore reached exactly as far as
  regedit reaches them, no further. Value names with an embedded NUL are read with their own data
  (see Values), but reads and deletes by name cannot address them, so `RegistryWriter` refuses
  such names.
- Limits: a key name component is at most 255 characters, a value name at most 16,383, and nesting
  depth 512. `RegistryPath` and `RegistryWriter` validate the first two before any native call and
  throw `ArgumentException`, so the request never reaches Windows. Nesting depth is a platform limit;
  the sandbox link walk refuses deeper trees rather than recursing without bound.
- The default value has the name `""`. The UI shows it as `(Default)` from the oracle string
  table.
- Duplicate-name checks use `StringComparer.OrdinalIgnoreCase`, which matches the registry's
  own case-insensitive names. Sorting uses the linguistic order described next. Find matching
  is described under Find.
- Display order of keys and values is linguistic, not registry enumeration order. The M1
  screenshot (`docs/parity/oracle/regedit-100dpi.png`) shows the subkey `Ünïcødé Key ✓` between
  `Strings` and `ValueNames`, which ordinal order would put last. So regedit sorts with the
  user-locale case-insensitive comparison (`CompareStringEx` with `NORM_IGNORECASE`, as
  `lstrcmpiW` does). Values are shown with `(Default)` first and the rest in the same order.
  The engine returns enumeration order; the UI sorts. Digit runs (`Key2` against `Key10`) are
  *verify against oracle*.

## Values

- Value data is read as raw bytes plus the raw `uint` type. Nothing is normalized on read: odd
  types, wrong lengths (a 3-byte `REG_DWORD`), missing terminators, and embedded NULs survive a
  read and write round trip byte for byte.
- Reads loop on `ERROR_MORE_DATA` because a value can grow between the size query and the read.
  There is no engine-side size cap; the UI decides how much to render.
- Typed views (string, DWORD, QWORD, multi-string) are separate decode helpers in the value
  codec (package B4). Display strings for the Data column follow regedit exactly, including
  `(value not set)`, `(zero-length binary value)`, and `(invalid DWORD (32-bit) value)`; the
  exact strings come from the oracle string table.
- Numeric Data-column formats (oracle strings 29 and 32, verified against the installed
  `regedit.exe` and MUI with `LoadStringW`):
  - REG_DWORD: `0x%08x (%u)`, for example `0x00001388 (5000)` and `0xffffffff (4294967295)`.
  - REG_QWORD: `0x%08I64x (%I64u)`. The width is a minimum of eight digits, not sixteen, so
    5000 shows as `0x00001388 (5000)` and only values above 32 bits print more digits.
  - The hex part is lowercase and the decimal part is unsigned.
  - A DWORD whose length is not 4 shows `(invalid DWORD (32-bit) value)`; a QWORD whose length
    is not 8 shows `(invalid QWORD (64-bit) value)`. Length 0 uses the same messages. Classic
    regedit's rule for lengths above 4 and 8 is not in the oracle; the BWC rule is unconfirmed.
- REG_DWORD_BIG_ENDIAN (5) has no name in the oracle type table (ids 8000 to 8012 skip 8005),
  so it shows type `REG_UNKNOWN` and hex bytes in stored order. REG_NONE, the three resource
  types, and unknown numbers show hex bytes, or `(zero-length binary value)` when empty.
- Enumeration reads each value's name, type, and data in one `RegEnumValueW` call. A name with
  an embedded NUL returns its own data and never re-queries its truncated form. Buffers grow on
  `ERROR_MORE_DATA` with a cap of 16 attempts per value; a name longer than 32,768 characters or
  data larger than the managed array limit throws `RegistryOperationException`. Reads by name
  (`ReadValue`) use the same bounded retry.

## Symbolic links and redirection

- Keys are opened with default semantics, so symbolic link keys are followed transparently,
  as in regedit. The shipping policy keeps that behavior.
- Under a policy that sets `RejectsSymbolicLinks` (the E2E sandbox), a write never follows a link.
  The writer walks the path from its predefined root, opens each component as a link node, and
  refuses the write with access denied when any component is a link. Creating a key over a link,
  deleting or renaming a link, deleting a tree that contains a link, and copying a tree that contains
  a link are all refused. `REG_OPTION_OPEN_LINK` is passed only by these guard probes, never by a
  write itself. Section A and C of `docs/failure-modes/registry-writer.md` list the cases.
- No WOW64 view switching. regedit shows the native view; so does BWC (64-bit view on x64 and
  ARM64 builds). `KEY_WOW64_64KEY` is passed explicitly on every open so an x64 build running
  under emulation on ARM64 still sees the native registry.

## Writes

- Every mutating native call (`RegSetValueExW`, `RegCreateKeyExW`, `RegDeleteKeyExW`,
  `RegDeleteValueW`, `RegDeleteTreeW`, `RegRenameKey`, `RegRestoreKeyW`, `RegLoadKeyW`,
  `RegUnLoadKeyW`, `RegSetKeySecurity`, `RegReplaceKeyW`, `RegCopyTreeW`) lives in the single
  class `RegistryWriter`. A build check fails if any other file references them.
- `RegistryWriter` takes a write policy at construction. The shipping policy allows everything.
  The E2E build flavor's policy accepts only paths under the `--sandbox-root` key and throws
  before calling native code for anything else, including relative opens that would escape the
  root.
- The E2E sandbox also refuses: renaming the sandbox root; a copy whose source is outside the
  sandbox; a copy whose source or destination subtree contains a symbolic link key; a restore or
  replace whose hive file contains a symbolic link key or cannot be verified as a hive; a permission
  propagation whose subtree contains a link; and any hive mount (`HKLM` or `HKU`).
- Every policy refuses a copy into the source's own subtree, including `ShippingWritePolicy`.
- `SetKeySecurity` with `propagateToSubkeys` applies one descriptor to the key and every key below
  it, through the write policy for each key, and reports progress and honors cancellation between
  keys. It is not inheritance-aware (`failure-modes/registry-writer.md` section I).
- `DenyAllWritePolicy` refuses every write before native code runs. The Registry Editor uses it in
  E2E builds when `BWC_E2E_SANDBOX_ROOT` is missing or invalid, so nothing is written.
- `scripts/check-registry-writer.js` fails the build when a file outside `RegistryWriter*.cs`
  names a native write API (including `W`, `Ex`, `Nt*`, `Zw*`, and `SH*` variants), when managed
  registry writes appear outside the checker's allowlist, or when `reg.exe` or `regedit.exe` is
  launched to write.
- Rename of a key uses `RegRenameKey`. Rename of a value is copy then delete, as in regedit
  (there is no native value rename); if the delete fails the copy is removed. A rename to a name
  that already exists is refused with Win32 error 183 before any write. A case-only rename is delete
  then recreate, so the stored spelling changes.
- Hive operations run one at a time under one lock in `RegistryWriter`, because the hive privileges
  are process-wide.

## Find (package B7 and R9)

- Search starts after the current selection: the selected value if one is selected in the
  list, otherwise the selected key. It walks keys depth-first in pre-order (a key, then its
  subkeys in display order, then the next sibling, climbing to the parent's next sibling), across
  the remaining root keys in their tree order. It does not wrap; at the end it reports
  regedit's "Finished searching through the registry." message.
- At each key the check order is: key name (if *Keys* is checked), then each value in display
  order, checking value name (if *Values*) then value data (if *Data*). A value produces at most
  one hit per step: the name wins when both name and data match.
- Matching and ordering use the Windows NLS collation classic uses: `CompareStringEx` with
  `NORM_IGNORECASE` at the user default locale. Substring matching compares a needle-length
  window at every UTF-16 offset. `CurrentCultureIgnoreCase` and ordinal ignore case do not
  reproduce the oracle order. `docs/parity/oracle/sort-order.json` is the ordering golden.
- *Data* searches only `REG_SZ`, `REG_EXPAND_SZ`, and `REG_MULTI_SZ`, using the text the Data
  column shows. `REG_MULTI_SZ` is the items joined with single spaces; individual items are not
  separate candidates. DWORD, QWORD, binary, link, none, and unknown types are not searched.
  Whole-string matching compares that same displayed text, so a multi-string whose item is `ab`
  does not match the whole string `ab`. The oracle `find-sequence.json` is the golden.
- A cursor whose key or value disappeared resumes at the nearest later position in NLS order
  instead of reporting finished. A missing key resumes at the first later sibling subtree; a
  missing value resumes at the first value that sorts after it. If no later position exists,
  the search is finished. The search never wraps.
- *Search in subtree* unchecked is not "starting key only": the oracle's no-subtree run still
  reached the seeded descendants. The engine's configured `Roots` define the scope and the flag
  does not narrow the walk. The cross-root scope difference is not captured and stays OQ-10.
- F3 repeats the last search from the current hit. The search runs on a background thread
  with the regedit progress dialog and Cancel button. `RegistryFindFlagsCodec` uses the classic
  dialog mask (`0x1` whole, `0x2` keys, `0x4` values, `0x8` data, `0x10` subtree). Windows 11
  build 26100.8457 does not update the persisted `FindFlags` DWORD; BWC writes it. F3 state is a
  query built from that DWORD.

## `.reg` files (packages B5, B6)

- v5 format: header `Windows Registry Editor Version 5.00`, UTF-16LE with BOM, CRLF.
- REGEDIT4 format (`/a` and *Win9x/NT4 Registration Files*): header `REGEDIT4`, encoded in the
  system ANSI code page (`GetACP()`), CRLF. Characters the code page cannot represent are
  converted with the same `WideCharToMultiByte` default-character behavior regedit gets, so
  output matches the `/a` golden byte for byte. Import of a REGEDIT4 file decodes with
  `GetACP()`.
- Line wrapping of `hex:` data at 80 columns with `,\` continuation and two-space indent,
  matching the goldens in `docs/parity/oracle/exports/`. The goldens are the specification:
  BWC's export of the seeded tree must be byte-identical.
- Export walks keys and values in registry enumeration order, not the UI's linguistic order:
  the v5 golden lists `Ünïcødé Key ✓` last although the tree shows it before `ValueNames`.
- Export reproduces regedit's lossy cases exactly as the goldens show them (for example a
  `REG_SZ` with an embedded NUL exports truncated at the NUL). Byte-exact means identical to
  regedit's output, not lossless.
- A value whose name contains an embedded NUL cannot be written to a `.reg` file. The exporter
  throws `RegistryOperationException` (operation `ExportValueName`, error 123) and returns no
  document. Classic regedit does not refuse it: a probe export stopped silently at such a value
  and did not exit, so copying that output is not an option.
- The parser accepts everything regedit accepts, including `-` key deletion lines,
  `"name"=-` value deletion, `hex(N):` for any type, and continuation lines.
- Import (package E2) follows classic `regedit /s`, the oracle in
  `docs/parity/oracle/import-behavior.json`, and is specified in `docs/failure-modes/reg-import.md`.
  The lenient entry points are `RegFileReader.ParseLenient` and `RegFileReader.ParseFileLenient`.
  A malformed line is skipped and listed in `RegFileDocument.Problems`. Only a file-level problem
  refuses the whole file, and the refusal is a code on `RegFileRefusal`: `NotRegistryFile`,
  `InvalidKeyPath`, or `ReadFailed`. A refused file writes nothing.
- `RegFileImporter.Apply` and `ApplyFile` return a `RegFileImportResult` and do not throw for a
  malformed or refused file, or for a rejected write. The result carries `KeysWritten`,
  `KeysDeleted`, `ValuesWritten`, `ValuesDeleted`, and `Failures`. Each failure has its file line,
  a kind (`MalformedLine` or `WriteFailed`), the key path and value name for a write, and the Win32
  error. A write failure does not stop the import, and writes that succeeded stay applied.
- Import creates each missing ancestor key before its section, as classic regedit does. A
  predefined root (`[HKEY_CURRENT_USER]`) is never created or deleted; its values are written
  through the normal write path.
- Import reports progress through `IProgress<ImportProgress>` after each key section finishes.
  Cancellation is checked before each section and throws `OperationCanceledException`. Sections
  already finished stay applied.
- The engine returns codes only. The UI maps a refusal to regedit string 133 (not a registry file),
  129 or 130 (read failure), or 125 (invalid key). It maps a result with `HasWriteFailures` to
  string 132, the text of which is `Cannot import %1: Not all data was successfully written to the
  registry.  Some keys are open by the system or other processes, or you have insufficient
  privileges to perform this operation.` (two spaces after `registry.`).

## Export formats (package E5)

- `RegFileFormat.V5` is the default for every export entry point. `RegFileFormat.Regedit4`
  selects the `REGEDIT4` header and the ANSI encoding rules in the previous section.
- `RegFileExporter.Export` keeps its existing argument order. Every export method accepts a
  `CancellationToken` and an optional `IProgress<ExportProgress>` as the last two parameters.
  Existing callers compile unchanged.
- A `Computer` selection exports every root classic regedit shows, in this order:
  `HKEY_CLASSES_ROOT`, `HKEY_CURRENT_USER`, `HKEY_LOCAL_MACHINE`, `HKEY_USERS`,
  `HKEY_CURRENT_CONFIG`. A selected predefined root exports that root's subtree only.
  `RegFileExporter.ExportWholeRegistry` and `RegistryExportService` expose the whole-registry
  form. An inaccessible root is skipped, and the remaining roots are exported.
- Text export (`RegistryTextExporter`, *Text Files*) is a streaming readable format. There is no
  oracle golden, so `docs/failure-modes/export.md` fixes the stable layout: UTF-16LE with BOM,
  CRLF, `Key Name` / `Class Name` / `Last Write Time` blocks, and indented value entries. It is
  display-only and lossy for the cases listed there.
- Hive export (`RegistryHiveExporter.Save`, `RegistryExportService.ExportHiveFile`) saves one
  subtree through `RegistryWriter.SaveKey`. It uses the same write policy and
  `SeBackupPrivilege` / `SeRestorePrivilege` path as the hive tests. A predefined root is
  refused, an existing destination is refused, and cancellation is observed before the native
  save starts.
- A `.reg` value name with an embedded NUL still throws `RegistryOperationException`
  (`ExportValueName`, error 123). REG_SZ data after the first NUL is still dropped, and REGEDIT4
  still converts unrepresentable characters to `?`. Hive export is the only lossless whole-tree
  format. `docs/failure-modes/export.md` lists every case classic `.reg` cannot represent.

## Command line (package R13)

- `/s` silent import, `/e file [key]` and `/a file [key]` export, `/m` multiple instances. The
  console paths never create a XAML window and return regedit's exit codes.

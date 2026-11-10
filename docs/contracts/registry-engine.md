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
- Every operation that loops over keys or values (enumerate, find, export, import, delete
  tree, copy tree) takes a `CancellationToken` and checks it at least once per key. Cancellation
  throws `OperationCanceledException` and leaves no partial write in progress: an import stops
  between two complete `.reg` entries, never inside one.
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
  regedit reaches them, no further.
- Limits come from the platform: key name component 255 characters, value name 16,383
  characters, nesting depth 512. The engine does not pre-validate them; it passes the request
  to Windows and reports the Win32 error, so error paths match regedit.
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

## Symbolic links and redirection

- Keys are opened with default semantics, so symbolic link keys are followed transparently,
  as in regedit. The engine never passes `REG_OPTION_OPEN_LINK`.
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
- Rename of a key uses `RegRenameKey`. Rename of a value is copy then delete, as in regedit
  (there is no native value rename); if the delete fails the copy is removed.

## Find (package B7 and R9)

- Search starts after the current selection: the selected value if one is selected in the
  list, otherwise the selected key. It walks keys depth-first in pre-order (a key, then its
  subkeys in display order, then the next sibling, climbing to the parent's next sibling), across
  the remaining root keys in their tree order. It does not wrap; at the end it reports
  regedit's "Finished searching through the registry." message.
- At each key the check order is: key name (if *Keys* is checked), then each value in display
  order, checking value name (if *Values*) then value data (if *Data*).
- Matching is case-insensitive substring, or whole-string equality with *Match whole string
  only*.
- Which data types *Data* searches, and how `REG_MULTI_SZ` and binary data are matched, is
  *verify against oracle*: drive classic regedit's Find over the seeded tree in E2E and commit
  the hit sequence as a golden before implementing.
- F3 repeats the last search from the current hit. The search runs on a background thread
  with the regedit progress dialog and Cancel button.

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
- The parser accepts everything regedit accepts, including `-` key deletion lines,
  `"name"=-` value deletion, `hex(N):` for any type, and continuation lines. Error behavior for
  malformed files is *verify against oracle*.

## Command line (package R13)

- `/s` silent import, `/e file [key]` and `/a file [key]` export, `/m` multiple instances. The
  console paths never create a XAML window and return regedit's exit codes.

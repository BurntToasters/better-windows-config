# Registry reader failure modes

This document covers the read side of `src/Bwc.Registry`: `RegistryReader`, `RegistryKeyInfo`,
`RegistryValueInfo`, `RegistryValueType`, `RegistryValueCodec`, and `RegistryValueDisplay`.
The reader is synchronous, has no UI dependency, and may run on any thread. Every open goes
through `NativeRegistry.TryOpenKey` with `KEY_READ | KEY_WOW64_64KEY`.

## 1. Access denied

A key can exist and still refuse `KEY_READ`. `RegistryReader` does not swallow that error. The
native code becomes `RegistryOperationException` with operation `OpenKey` and the full requested
path. The tree can still show the key because enumeration of the parent only needs the parent's
read access. Opening the child later surfaces the error in the UI, which is what classic regedit
does.

`KeyExists` and `ValueExists` treat only `ERROR_FILE_NOT_FOUND` (2) as "missing". Access denied
is thrown, not reported as false, so a caller cannot mistake an unreadable key for an absent one.

## 2. Missing key versus missing value

`GetKeyInfo`, `EnumerateSubKeyNames`, and `EnumerateValues` throw when the key cannot be opened.
A missing key therefore produces `RegistryOperationException` with operation `OpenKey`.

`ReadValue` returns `null` only when the key opens and the value itself is missing
(`ERROR_FILE_NOT_FOUND`). A missing key throws `RegistryOperationException` with operation
`OpenKey`, so a caller can tell "the key is gone" from "this key has no such value".
`ReadDefaultValue` is the same call with the empty value name.

`KeyExists` returns false for a missing key. `ValueExists` returns false for a missing key or a
missing value and throws on every other failure.

## 3. ERROR_MORE_DATA races

Registry data can change between the size query and the read. Every read loop treats
`ERROR_MORE_DATA` (234) as "grow the buffer and retry":

- `GetKeyInfo` retries the class-name buffer.
- `EnumerateSubKeyNames` retries the same index with a larger name buffer.
- `EnumerateValues` retries the same index with a larger name buffer, then reads each value's
  bytes through `RegQueryValueExW`, which retries with a larger data buffer.
- `ReadValue` retries the value read with a larger data buffer.

A retry never advances the enumeration index. If the value is deleted between calls,
`ERROR_FILE_NOT_FOUND` ends `ReadValue` with `null`; other errors become
`RegistryOperationException`. The reader does not silently return a truncated buffer.

## 4. Huge names and values

`RegQueryInfoKey` supplies the maximum subkey name length and the maximum value name and data
lengths. The reader uses those sizes as starting points, not as caps. Names can legally reach
255 characters for keys and 16,383 characters for values. Value data has no engine-side cap.
The reader allocates what the API reports and grows on `ERROR_MORE_DATA`.

Starting from the reported maximum avoids most retries. It also means a single unusually large
value does not force every later value into a large buffer: each enumeration step reuses the
current buffer and only grows when a particular value needs it.

## 5. Embedded NUL in data

Value data is returned as raw bytes plus the raw `uint` type. The reader never normalizes,
trims, or re-encodes it. A `REG_SZ` with an embedded NUL keeps the NUL in `Data`. The display
decoder stops at the first UTF-16 NUL, which matches the lossy behavior classic regedit shows
and classic `.reg` export produces. `DecodeStringRaw` keeps the exact text, including an
optional terminator, for callers that need byte-level inspection.

Windows itself normalizes one case on write: `RegSetValueExW` appends a UTF-16 NUL to `REG_SZ`
data that does not already end with one. The reader therefore cannot observe a no-terminator
`REG_SZ` created through that API, and the E2E test asserts the normalized 16 bytes with a
comment explaining that the caller supplied 14. The codec still accepts no-terminator data
because imported hives and future direct-hive work can contain it.

Value names containing NUL are not reachable through a managed string argument because the
native wide-string API stops at the first NUL. The reader does not invent a workaround; this is
the same limit classic regedit has when a name is entered in the UI.

## 6. Empty data and the default value

A value with zero bytes is valid. `RegistryValueInfo.DataLength` is zero and `Data` is an empty
array. The display layer decides what to show: `(value not set)` for string-like types and
`(zero-length binary value)` for binary or opaque types. A zero-length `REG_DWORD` or `REG_QWORD`
is not valid for those types and displays the invalid-length message instead.

The default value has the name `""`. It appears in enumeration with that name and is read with
`ReadValue(key, string.Empty)` or `ReadDefaultValue(key)`. `FormatName` renders it as
`(Default)` for the UI.

## 7. Enumeration order

`EnumerateSubKeyNames` and `EnumerateValues` return registry enumeration order, not linguistic
order. The UI sorts. Tests compare the reader's order with the order returned by the .NET
`RegistryKey` wrapper, which also uses `RegEnumKeyExW` and `RegEnumValueW`, so a sort added in
the reader would show up as a mismatch.

Enumeration order is not a stable data model. Windows does not guarantee it across writes or
versions. A concurrent writer can add or remove entries during enumeration; the reader returns
the entries the API yields for that walk. It does not take a snapshot and it does not watch for
changes.

## 8. 64-bit view

Every open passes `KEY_WOW64_64KEY`. On ARM64 and x64 there is no redirection for the native
view, but the flag keeps behavior correct if the process is ever 32-bit under emulation. The
reader never passes `KEY_WOW64_32KEY` and never asks for `REG_OPTION_OPEN_LINK`.

Tests use `RegistryView.Registry64` for both seeding and cleanup. A mismatch between the test
view and the reader view would make the test fail with a missing key, which is the intended
alarm.

## 9. Buffer growth

Name buffers are measured in UTF-16 characters and data buffers in bytes. The reader keeps the
two sizes separate. On `ERROR_MORE_DATA` it grows to at least the returned requirement and at
least one element beyond the current size, so a stale or racing requirement cannot cause an
infinite retry loop. On success it builds the string from exactly the returned character count
and copies exactly the returned byte count.

The class-name buffer starts at 256 characters, which is the normal key-name component limit.
A longer class name causes `ERROR_MORE_DATA` and a retry. Class names are rare but the Win32
limit is much larger, so the loop has no fixed cap.

## 10. Cancellation

Every public read takes `CancellationToken cancellationToken = default`. Enumeration checks the
token once per name or value, at the top of each enumeration step. A cancelled token throws
`OperationCanceledException` and the partially built list is discarded; no partial result is
returned. Single-value reads check at entry and on each buffer retry. Opening a key itself is a
short native call that cannot be cancelled; the token is checked before the open and again in
the read or enumeration loop.

## 11. Class name

`RegistryKeyInfo.ClassName` comes from the `lpClass` buffer of `RegQueryInfoKey`, not from a
managed `RegistryKey` wrapper. Most keys have an empty class, which is reported as
`string.Empty`, not null. `RegQueryInfoKey` returns the class length without the terminating
NUL; the reader uses that count rather than scanning for NUL, so a class name that happens to
contain a NUL is preserved as far as the count allows.

## 12. Last write time

`RegQueryInfoKey` returns a `FILETIME` in UTC. The reader combines the low and high 32-bit words
into a 64-bit count and calls `DateTime.FromFileTimeUtc`, so `LastWriteTimeUtc.Kind` is
`DateTimeKind.Utc`. A zero time is the Win32 epoch (1601-01-01), not `DateTime.MinValue`.

## 13. Unknown and odd value types

The raw `uint` type is preserved. `RegistryValueInfo.TypeEnum` is a direct cast, so an unknown
type such as `0x1234` keeps that raw value even though no enum member exists. The type-name
table follows the oracle resource dump exactly: `REG_NONE`, `REG_SZ`, `REG_EXPAND_SZ`,
`REG_BINARY`, `REG_DWORD`, `REG_LINK`, `REG_MULTI_SZ`, `REG_RESOURCE_LIST`,
`REG_FULL_RESOURCE_DESCRIPTOR`, `REG_RESOURCE_REQUIREMENTS_LIST`, `REG_QWORD`, and
`REG_UNKNOWN`.

The oracle string table has no `REG_DWORD_BIG_ENDIAN` entry even though Win32 defines type 5.
`RegistryValueType.DwordBigEndian` exists for the raw number and the codec has a big-endian
decoder, but `ToTypeName(5)` returns `REG_UNKNOWN`, matching regedit's own name table. The
display layer shows type 5 as hex bytes for the same reason.

A three-byte `REG_DWORD` or a seven-byte `REG_QWORD` is preserved on read. The display layer
shows `(invalid DWORD (32-bit) value)` or `(invalid QWORD (64-bit) value)` instead of guessing.

## 14. Multi-string ambiguity

`DecodeMultiString` splits on UTF-16 NUL and reports whether the data ended with a double NUL.
Interior empty strings are preserved. The encoding is inherently ambiguous at the edges: a
single empty string and an empty list can share the same `\0\0` bytes, and a value with one
trailing NUL has no unambiguous string count. The decoder documents and tests the chosen
interpretation rather than pretending the ambiguity does not exist.

The Data-column rendering of `REG_MULTI_SZ` joins the decoded strings with single spaces. This
is provisional. Classic regedit's exact multi-string display has not been captured in the
oracle goldens, and the test named `MultiStringDisplayIsProvisional` records the current choice
so a later oracle capture can change it deliberately.

## 15. Display truncation

`RegistryValueDisplay.FormatData` takes `maxChars` (default 1024) and truncates long output with
a three-character ellipsis. The result is kept within `maxChars`, so the ellipsis counts toward
the budget. A non-positive budget returns an empty string. Truncation is a display concern
only; `RegistryValueInfo.Data` always contains the complete bytes.

## 16. Test cleanup

Each reader test creates one `HKCU\Software\BWC-E2E\<unique-run-id>` root with the 64-bit view,
seeds it, and deletes the whole root in a `finally` block. Cleanup uses
`DeleteSubKeyTree(..., throwOnMissingSubKey: false)` so a test that fails before creating the
root does not hide the original failure behind a cleanup exception. The parent
`HKCU\Software\BWC-E2E` may remain empty; acceptance checks for child keys. A crash that skips
`finally` leaves a key whose name contains the run id, which is easy to identify and remove.

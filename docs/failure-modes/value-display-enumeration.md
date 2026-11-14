# Value display and enumeration failure modes

This document covers the two read-side behaviors that decide what the Data column shows and
whether `RegistryReader.EnumerateValues` returns the right values:

- `RegistryValueDisplay.FormatData` (the classic regedit Data-column text).
- `RegistryReader.EnumerateValues` (the native `RegEnumValueW` loop), plus the export guard in
  `RegFileExporter`, which depends on the same names.

Each section lists the failure, the intended behavior, and where the test lives. Oracle ids refer
to the en-US string table in `docs/parity/oracle/regedit-resources.md`. Strings 29, 30, 32, and
33 were also read back from the installed `regedit.exe` and `en-US\regedit.exe.mui` with
`LoadStringW`, so the table matches the binary on this build.

## 1. DWORD (REG_DWORD, 4 bytes) rendering

Oracle string 29 is `0x%1!08x! (%1!u!)`.

- **Decimal only.** The old code printed `5000`. Intended: `0x00001388 (5000)`.
- **Hex width and casing.** Hex is lowercase and at least 8 digits (`%08x`). Zero is
  `0x00000000 (0)`. `0xFFFFFFFF` is `0xffffffff (4294967295)`, never a negative number.
- **Signedness.** The decimal part is unsigned (`%u`). A DWORD with the high bit set must not
  print as `-1`.
- **Invariant culture.** Digits never use locale grouping or a locale digit set.
- **Length not 4.** Oracle string 30 is `(invalid DWORD (32-bit) value)`. Zero length, 1 to 3
  bytes, and more than 4 bytes all use it. The rule for more than 4 bytes is the BWC contract,
  not an oracle fact: classic regedit may show the first four bytes, and this is not confirmed.
- **Budget.** Output that exceeds `maxChars` keeps the ellipsis inside the budget (existing rule).

Tests: `RegistryValueDisplayTests` (DWORD cases, invalid lengths).

## 2. QWORD (REG_QWORD, 8 bytes) rendering

Oracle string 32 is `0x%1!08I64x! (%1!I64u!)`.

- **Width is 8, not 16.** A 16-digit form (`0x%1!016I64x!`) was expected at first. The oracle
  and the installed binary both say `08`, and a scan of `regedit.exe` and the MUI found no
  16-digit variant. The
  width is a minimum, so 5000 prints as `0x00001388 (5000)` and only values above 32 bits print
  more digits (`0x100000000 (4294967296)`). Intended: follow the oracle.
- **Full 64-bit range.** `0xffffffffffffffff (18446744073709551615)` for the maximum. Decimal is
  unsigned.
- **Length not 8.** Oracle string 33 is `(invalid QWORD (64-bit) value)`.

Tests: `RegistryValueDisplayTests` (QWORD cases, invalid lengths).

## 3. Big-endian DWORD (REG_DWORD_BIG_ENDIAN, type 5)

- The oracle type-name table has no 8005 entry (8000 to 8004 and 8006 to 8012 are present), so
  classic regedit names type 5 `REG_UNKNOWN` (8012).
- No numeric format string exists for type 5, so classic regedit does not format it as a number
  as far as the string table shows. Intended: hex bytes in stored order (`11 22 33 44`), no byte
  swap, and the type column `REG_UNKNOWN`.
- Not confirmed by a screen capture of classic regedit. This follows from the missing string.

Tests: `RegistryValueDisplayTests.TypeFiveHasNoOracleNameAndShowsHex`.

## 4. Other types with no numeric format

REG_NONE (0), REG_RESOURCE_LIST (8), REG_FULL_RESOURCE_DESCRIPTOR (9),
REG_RESOURCE_REQUIREMENTS_LIST (10), and unknown numbers (12, 0x1234, 0xFFFFFFFF) have no
string in the oracle other than the generic ones.

- Non-empty data prints as space-separated uppercase hex bytes (`0A`, `DE AD`).
- Zero-length data prints `(zero-length binary value)` (oracle string 22).
- Type names: REG_NONE, REG_RESOURCE_LIST, REG_FULL_RESOURCE_DESCRIPTOR, and
  REG_RESOURCE_REQUIREMENTS_LIST come from oracle ids 8000 and 8008 to 8010. Unknown numbers
  print `REG_UNKNOWN` (8012). The number itself is never printed.

Tests: `RegistryValueDisplayTests.BinaryAndUnknownTypesShowHexBytes`, `TypeNamesFollowTheOracleTable`.

## 5. Strings (REG_SZ, REG_EXPAND_SZ, REG_LINK)

- Text stops at the first NUL, so `ab\0cd\0` displays `ab`. The terminator and the bytes after
  it are not shown.
- A missing terminator displays the full text. An odd trailing byte is ignored.
- Zero length displays `(value not set)` (oracle string 27).

Tests: `RegistryValueDisplayTests.StringValuesShowTextWithoutTheTerminator`,
`ZeroLengthStringValuesShowValueNotSet`.

## 6. Long data in the list

Oracle resources hold no truncation limit and no ellipsis string for the Data column. The
1024-character budget and the `...` suffix are BWC choices. They are kept, not confirmed against
regedit. Test: `LongValuesAreTruncatedWithinTheBudget`.

## 7. Embedded NUL in value names

`RegEnumValueW` returns the name with its counted length, so a name such as `abc\0def` arrives
with the NUL inside the string. The old code then re-read the value by that name through
`RegQueryValueExW`, which takes a NUL-terminated name and therefore queried `abc`.

- **Wrong data.** If a value named `abc` exists, the NUL-named value shows `abc`'s data.
- **Hang.** If `abc` does not exist, the re-query returns `ERROR_FILE_NOT_FOUND`. The old code
  retried the same index forever, so enumeration never ended.

Intended behavior: data and type come from the same `RegEnumValueW` call that returned the name,
so the value returns its own data, and enumeration always advances or stops. Reads by name
(`ReadValue`, `ValueExists`) and deletes by name cannot address a NUL-named value at all, because
the name is truncated at the NUL. `RegistryWriter.SetValue` and `DeleteValue` therefore refuse such
names (`ArgumentException`) before any native call; without the refusal, `DeleteValue("abc\0def")`
would delete `abc`. The truncated delete was observed on this machine: a `RegDeleteTree` over a
sandbox key removed the plain `abc` value and left the NUL-named value in place.

Tests: `RegistryReaderTests.EnumerationReturnsEmbeddedNulNamesWithTheirOwnData` (the NUL-named
value is seeded through the E2E-only writer seam, because `RegSetValueExW` cannot pass an embedded
NUL), `RegistryWriterSandboxTests` (name guard).

## 8. Names longer than 16,383 characters

The platform limits value names to 16,383 characters. Windows does not create longer names, but
`RegEnumValueW` can still report a larger length than the buffer. Intended: the name buffer grows
on `ERROR_MORE_DATA` up to a fixed cap (32,768 characters). Beyond the cap the reader throws
`RegistryOperationException` with `ERROR_MORE_DATA` instead of allocating without limit.

## 9. Values that change size between calls

A value can grow or shrink between the size query and the read.

- **Grow.** `RegEnumValueW` returns `ERROR_MORE_DATA` and reports the required size. The reader
  grows the matching buffer and retries the same index. The retry count is capped (16 attempts
  per index); after that the reader throws `RegistryOperationException` (`EnumValue`,
  `ERROR_MORE_DATA`) and never returns truncated data.
- **Shrink.** The returned length is the actual length. The reader uses it, not the buffer size.
- **Reads by name.** `ReadValue` uses the same retry rule with its own cap.

Test: `RegistryReaderTests.EnumerationTerminatesWhileValuesChangeSize`.

## 10. Values deleted or added during enumeration

Enumeration is by index, so a deletion can make the next index skip a value, and an addition
can make it appear. The engine returns a snapshot, as the contract says; it does not watch.

- A deletion must not hang or throw. `ERROR_NO_MORE_ITEMS` ends the loop normally.
- `ERROR_FILE_NOT_FOUND` from the enumeration call is not expected. If it appears it is an
  error (`RegistryOperationException`), not a silent skip.
- The retry on `ERROR_MORE_DATA` never advances the index.

Test: `RegistryReaderTests.EnumerationTerminatesWhileValuesAreDeleted`.

## 11. Huge data

- A reported size above the managed array limit (`Array.MaxLength`) must not reach
  `new byte[]` or an `(int)` cast. It throws `RegistryOperationException` with
  `ERROR_NOT_ENOUGH_MEMORY` (8) and a message that names the size.
- Data within the limit is read whole, with no engine-side cap. The UI decides what to render.

Test: `RegistryReaderTests.MaximumNameAndMegabyteDataRoundTripThroughEnumeration` (a 1 MB binary
value under a 16,383-character name round-trips whole).

## 12. Overflow in size arithmetic

- `checked((int)size)` in the old `QueryValueData` throws `OverflowException`. Size arithmetic
  must map to `RegistryOperationException`, never to an unhandled exception.
- `new string(buffer, 0, length)` with a length above the buffer throws
  `ArgumentOutOfRangeException`. The length is clamped against the buffer before the cast.
- `GrowBuffer` already caps at `int.MaxValue`; the new buffers use the same rule.

## 13. Unknown type numbers

Enumeration keeps the raw `uint` type for any number. Display (section 4) and export
(`RegFileExporter`) never reject a type. The exporter writes `hex(N):` for every type except
REG_SZ (1), REG_BINARY (3, written as `hex:`), and a 4-byte REG_DWORD (4, written as `dword:`),
as it did before this change.

## 14. Export guard for names a .reg file cannot hold

A value name with an embedded NUL cannot be written as a `.reg` name without changing its meaning.

- **Old behavior.** `RegFileWriter` wrote `\0` inside the quoted name. Nothing in the export
  says the name was changed, and regedit's own import does not promise to read that escape back.
- **Classic regedit.** Probed on this machine with `regedit /e` over a sandbox key holding
  `plain`, `abc\0def` (DWORD), and `abc` (string). The export contained only `plain`, with no
  error. The process then did not exit within 20 seconds and was killed. So classic regedit
  drops the rest of the key silently and can hang. That output is misleading and is not copied.
- **Intended behavior.** `RegFileExporter.Export` throws `RegistryOperationException` with
  operation `ExportValueName`, error `ERROR_INVALID_NAME` (123), and the key path, before any
  file is written. No partial document is returned. The message names the key.

Tests: `RegFileExporterTests.ExportRefusesValueNamesWithEmbeddedNulInEveryFormat`. The shipping
assembly test `ShippingAssemblyHasNoCountedNameWriteSeam` checks that the E2E-only seam used to
create these names is not compiled into shipping builds.

## 15. Cancellation

Every loop observes the cancellation token once per value (existing rule). A cancelled
enumeration throws `OperationCanceledException` and returns no partial list.

## 16. Remote registry

Remote connections use the same enumeration code through `_remote`. The rules above apply
unchanged.

## 17. Enumeration failure in the value list

The value list opens the key, enumerates its values, and then builds the rows. The engine's value
source turns an enumeration failure into an empty list, so the list alone could not tell an empty key
from a key that could not be read. Earlier builds showed the empty list silently for every failure
except access denied.

Rule: the probe that runs before each list load opens the key and enumerates its values, both on the
local reader and on a remote reader. A failure of either shows the classic `Cannot open key` message
(`MsgOpenKeyFailed`, caption `MsgCaptionOpenKeyError`) with the Win32 text as details, and the list is
empty. A failure with no Win32 code (any other exception in the load) shows the same message without
details. Every case is logged with the key path. The value source's own catch remains only for a
failure that happens between the probe and the read, which is logged.

E2E builds only: the `BWC_E2E_FAIL_VALUE_ENUMERATION` environment variable names one display path
(case-insensitive). When the probe runs for that path it reports `ERROR_NOT_ENOUGH_MEMORY` (8), so the
enumeration failure can be driven with real input. The ship build never reads the variable.

Check: `AppServices_ValueEnumerationFailureShowsClassicError` sets the variable to the run's sandbox key,
navigates to it, and checks the `Error Opening Key` message with the key path and the system text, and
an empty list. It writes nothing.

## 18. The list Data column follows classic regedit (0.1.2)

The Value list (`RegistryValueSource`) renders Name, Type, and Data the way classic regedit's list
does. The rules below were read from classic regedit with UI Automation on this machine
(`SysListView32` subitems, Edit elements named Name, Type, Data). They override sections 3, 4, 5, and 6
for the list only. `RegistryValueDisplay.FormatData` keeps its shared contract for export, print, and
Find, and `RegistryValueDisplayTests` pin that contract, so the list has its own formatter.

Failure modes, each with the classic rule the list must match:

- **Empty REG_SZ shows nothing, not `(value not set)`.** A zero-length or NUL-only REG_SZ shows an
  empty Data cell. `(value not set)` is only the synthetic (Default) row of a key that has no default
  value. Classic: `EmptySz` (0 bytes) and `NulSz` (00 00) both show an empty cell.
- **REG_MULTI_SZ stops at the first empty string.** Classic appends each string followed by one space,
  and stops when it reaches an empty string. `one`, empty, `two` shows `one ` (trailing space), and
  zero-length data shows an empty cell. Joining every string with one space shows `one  two` and is wrong.
- **REG_LINK cannot be seeded in the sandbox.** `SandboxWritePolicy.AllowsLinkValues` is false, so both the
  API and the silent import refuse hex(6). The E2E therefore does not compare REG_LINK rows; the rule below is
  from the one-off classic read on this machine. Classic shows the stored bytes as lowercase hex, including the UTF-16
  terminator: `\Registry\Machine` shows `5c 00 52 00 ... 00 00`. It is not text.
- **REG_DWORD_BIG_ENDIAN is a number.** Type name `REG_DWORD_BIG_ENDIAN`. The four bytes are read
  big-endian: `11 22 33 44` shows `0x11223344 (287454020)`. Section 3 (REG_UNKNOWN, hex) is wrong for
  the list.
- **Hex bytes are lowercase and space-separated.** `01 ab`, `0a de ad`, and the resource-list bytes
  `01 00 00 00 02 00 ...`. Upper case (section 4) is wrong for the list.
- **Zero-length binary data shows `(zero-length binary value)`.** Measured for REG_BINARY (`BinEmpty`),
  REG_NONE (`NoneEmpty`), and each of the resource types and unknown types in the E2E import matrix.
  The list gets this text from `RegistryValueDisplay.FormatData` (oracle string 22), not a copy.
- **Unknown type numbers print as hex, not REG_UNKNOWN.** Type name `0x1234` for 0x1234 and `0xabcd`
  for 0xABCD (lowercase, `0x` prefix). Section 4 (REG_UNKNOWN) is wrong for the list. The minimum digit
  width for values below 0x1000 is not measured; the E2E check uses 0x0C and records the result.
- **Strings and binary: cut at 192 characters, then `...`.** Classic showed a string of 192 characters
  in full, and any longer string (193, 194, 200, 300, 1100, 2500 characters) as its first 192 characters
  and `...` (195 in total). A binary value whose 192-character cut ends in a space (`... 3f `) shows the
  cut without that space (`... 3f...`), so the trailing space is trimmed first. The 1024-character budget
  (section 6) is wrong for the list.
- **REG_MULTI_SZ: whole strings, no `...`.** Classic adds each string with its space while the text fits
  in 192 characters, then stops. Sixty strings `s00` to `s59` show `s00 ... s47 ` (192 characters) with
  no ellipsis. Classic does not cut a string in the middle here.
- **A 16,383-character value name is not listed by classic.** Classic shows only the synthetic (Default)
  row for a key whose only value has that name, so the case is recorded as a note
  (`valuedisplay.longname`), not a pass. The BWC row keeps the full name.
- **Strings stop at the first NUL.** Unchanged from section 5 (text after the first NUL is not shown).
- **Numbers keep the oracle widths.** DWORD and QWORD use `0x%08x (%u)` (section 1 and 2), and an
  invalid length uses the oracle invalid-length strings. Classic's invalid-length display is not
  measured; the E2E check records it as a note when the row differs.
- **Name column.** The default value shows `(Default)`. A literal `(Default)` shows its own name. Both
  rows exist when both exist (see `list.md`, section 8).

Tests: `tests/Bwc.E2E/ValueDisplay/ValueDisplayTests.cs` (`ValueDisplay_ListMatchesClassicForEveryValueType`,
`ValueDisplay_ListMatchesClassicForLongNameAndData`, `ValueDisplay_ListMatchesClassicForLongestValueName`).
Each run writes its compared rows, from both lists, to `artifacts/e2e/value-display/classic-compare-<case>-<rid>.json`.

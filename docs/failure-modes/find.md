# Registry find failure modes

This document covers `RegistryFinder` in `src/Bwc.Registry/Find`. The finder is a read-only
engine. It calls `RegistryReader` only, never a mutating Win32 API, and has no UI dependency.
It is synchronous and may run on any thread.

The contract is `docs/contracts/registry-engine.md` under `Find`. The inventory rows are
BEH-039 to BEH-042, DLG-010, DLG-011, OQ-10, and OQ-11.

## 1. Traversal order

The walk is depth-first pre-order. At one key the order is:

1. The key name, when *Keys* is enabled.
2. Each value in display order: the default value first, then the other values sorted with the
   classic NLS collation (`CompareStringEx` with `NORM_IGNORECASE`). For each value, the value
   name when *Values* is enabled, then the value data when *Data* is enabled. A value produces at
   most one hit per search step: the name wins when both the name and the data match.
3. Each subkey in the same NLS order, recursively, using an explicit stack.

The walk is iterative. Registry depth can reach 512, and a recursive implementation would put
more than 512 frames on the call stack for a deep tree. The explicit stack also makes
cancellation checks straightforward.

The root paths themselves are visited as keys. A predefined root has no leaf name component,
so a predefined root name is never a *Keys* hit. This differs from the tree, where the root is a
visible label. Classic regedit's exact behavior for matching a root label is not captured, so
this is provisional.

## 2. No wrap

The search never wraps. When the last configured root is exhausted, `Find` returns `null`. The
caller turns that into regedit's `Finished searching through the registry.` message. F3 must not
silently restart at the first root, because a wrap would repeat hits and could loop forever when
the only match is the current hit.

## 3. Start after a selection and cursor ambiguity

`Find` accepts an optional `RegistryFindCursor`. The cursor names a key, an optional value name,
and one of three positions:

- `AfterKeyName`: the key name was the last position considered.
- `AfterValueName`: that value's name was the last position considered.
- `AfterValueData`: that value's data was the last position considered.

The walk skips every position up to and including the cursor position. A cursor at a key name
therefore resumes with that key's values and then its children. A cursor at a value name resumes
with that value's data. A cursor at value data resumes with the next value.

Classic Find reports a matching value once, even when both its name and its data contain the
search text. `RegistryFindCursor.FromHit` therefore resumes after value data for both value-name
and value-data hits. `AfterValueName` remains available to a caller that deliberately wants the
same value's data checked next, but Find Next does not use it.

Two conveniences cover the common UI cases:

- `RegistryFindCursor.FromKeySelection(key)` uses `AfterKeyName`, matching the contract phrase
  "starts after the current selection".
- `RegistryFindCursor.FromValueSelection(key, name)` uses `AfterValueData`, so a selected value
  is skipped completely.

The phrase "after the current selection" is ambiguous for a key selection. Reading it literally
means the selected key's own name is not checked, but its values and descendants are. This engine
does that. Classic regedit may include the selected key name on a fresh Find, and the oracle has
not recorded that sequence, so a later golden can change the cursor mapping deliberately.

When a cursor is supplied, the finder starts directly at the cursor key instead of rescanning
the registry from the first root. It builds the continuation from the cursor key's later
siblings and its ancestors' later siblings.

The registry can change between two Find Next presses. Classic regedit does not report
"finished" merely because the previous hit disappeared. The finder also continues from the
nearest position:

- If the cursor key no longer exists, the finder inserts its leaf name into the parent's sorted
  child list, using the same NLS order. It resumes with the first later sibling's subtree, then
  that parent's later siblings, and so on up to the search root. If the parent also disappeared,
  the climb repeats at the grandparent. Key-name, value-name, and value-data checks all run at
  the resumed position because the cursor itself is gone.
- If the cursor value no longer exists, the finder inserts its name into the key's sorted value
  list and resumes with the first value that sorts after it. Values before that position were
  already considered.
- If the cursor key exists but its value list cannot be read, the finder resumes with that key's
  children. No value in the key can be reported, so that is the nearest reachable position.
- If no later sibling or later root exists, the search is finished and `Find` returns `null`.

A cursor whose key is not inside any configured search root is never reached and returns `null`.
The search never wraps and never restarts at the first root after a deletion.

## 4. Default value name

The default value's real name is the empty string. `ValueName` is `null` for a key-name hit and
the empty string for a hit on the default value. `(Default)` is a UI display string, not a
registry name. Searching value names for `Default` therefore does not match the default value.
The default value can still produce a data hit.

## 5. Data matching (oracle `find-sequence.json`)

Classic Find searches data only for the string types. The oracle capture seeds the needle `ab`
into a DWORD (`0x000000AB`), a binary value (`61 62`, the ASCII bytes for `ab`), a multi-string,
an expandable string, and ordinary strings. The DWORD and the binary value never appear as Data
hits in any captured run. A `REG_LINK`, `REG_NONE`, unknown type, QWORD, or other non-string
type is therefore not searched either. This settles OQ-11.

The candidate text is the one the Data column shows:

- `REG_SZ` and `REG_EXPAND_SZ`: `RegistryValueCodec.DecodeString`, which stops at the first
  UTF-16 NUL.
- `REG_MULTI_SZ`: the decoded items joined with single spaces, the same text as
  `RegistryValueDisplay.FormatData`. The items are not searched individually. In the oracle's
  whole-string run, the multi-string `zz`, `ab` does not match the whole string `ab`, while the
  joined text contains `ab` for substring runs. That is exactly the captured sequence.
- Zero-length data is not searched. The display placeholders `(value not set)` and
  `(zero-length binary value)` are UI text, not stored data, and never produce a hit.

A value produces at most one hit per search step. If both its name and its data match, the
ValueName hit is reported first, and the next Find Next moves past the whole value. Only stored
data is searched; display placeholders are not. A hit reports one `MatchedText`, the candidate
text that contained or equaled the needle.

## 6. Duplicate hits

A value is one stop even when both its name and its data match. The value-name hit is reported,
and the next step moves past the whole value; the same value does not produce a second data hit.
A key can produce a key-name hit and later a hit inside the same key's values, because those are
different positions. Repeating an identical position is not allowed.

If the same key is reachable through overlapping configured roots, it is walked once per root.
Callers that supply custom roots should avoid overlapping roots when duplicate reporting would
be confusing.

## 7. Matching and culture

Matching and ordering both use the Windows NLS collation that classic regedit calls:
`CompareStringEx` with `NORM_IGNORECASE` and the user default locale. The managed current-culture
comparer is not used. It disagrees with the captured classic order on real names, for example
`_x` against `(x)`, `#h` against the other symbols, `a-b` against `a01`, and `x_y` against
`x.y`.

- Whole-string matching is `CompareStringEx(...) == CSTR_EQUAL` over the full text.
- Substring matching compares a needle-length window at every UTF-16 offset, exactly as
  `lstrcmpi`-style classic code does. A window crossing a separator or an embedded NUL is still
  compared; the API receives explicit lengths.
- Ordering sorts key and value names with the same call. `docs/parity/oracle/sort-order.json`
  records the classic order for 27 names, including the `(Default)` value first, digit runs as
  text (`1`, `10`, `2`), and the punctuation order above. The test suite replays that list.
- If the native call fails, which only happens for an invalid locale or flag, the code falls
  back to ordinal ignore case so a walk or sort still terminates. That fallback is not the
  semantic model and is not exercised on Windows.

The oracle's `matchCase` note says classic Find offers no match-case option and searches case
insensitively, so `NORM_IGNORECASE` is the only comparison flag. `NORM_IGNORECASE |
SORT_STRINGSORT` does not reproduce the recorded order.

The empty search string is rejected with `ArgumentException`. Whitespace is allowed and is
matched literally. At least one of *Keys*, *Values*, or *Data* must be enabled.

## 8. Access denied and missing keys

The finder never aborts the whole search because one key or value is unreadable. Opening a key,
enumerating its values, or enumerating its subkeys can fail with `RegistryOperationException`.
That key's unavailable phase is skipped and the walk continues with the next sibling or the next
root.

A key name can still be a hit when the key cannot be opened, because the name came from the
parent's enumeration. Its values and children are simply unavailable. A value whose data
disappears between enumeration and the size query is already omitted by
`RegistryReader.EnumerateValues`.

`Bwc.Registry` has no reference to `Bwc.Core.Log`, so skips are silent. There is no skipped-key
counter in this version. A caller that needs diagnostics can correlate the absence of hits with
its own tree inspection.

## 9. Keys deleted or renamed during a search

The engine works from a snapshot of names per key. If a key is deleted after its parent was
enumerated, opening it fails and that branch is skipped. If it is renamed, the old path fails
and the new name is discovered only if the parent is enumerated again, which this walk does not
do. If a value is deleted after value enumeration but before its data check, the reader's
enumeration has already read the data, so the snapshot value is matched. If it is deleted
before enumeration reaches it, the reader retries the same index and the value is not reported.

These are accepted race outcomes. The registry has no consistent snapshot across the whole tree.

## 10. Depth, huge trees, and cancellation

There is no artificial depth limit. The walk uses an explicit stack, so a tree 512 keys deep
does not overflow the managed call stack. Each `RegistryPath.Append` copies the component list,
so path construction is quadratic in depth. At the platform depth limit this is bounded and
small compared with registry I/O.

Every key and every value checks the `CancellationToken`. A cancelled token throws
`OperationCanceledException`; it is not caught by the access-denied handling. A pre-cancelled
token throws before the first registry access. Cancellation can happen between two complete
positions and never leaves a partial write, because the finder does not write.

The finder visits every key under every configured root. On a real machine that is expensive,
which is why the UI runs it on a background thread and why the engine exposes custom roots.
Tests scope the roots to one small sandbox key.

Data matching for a large string or multi-string value materializes its decoded display text,
so memory use grows with the value size. Values of other types are not searched and are not
decoded for Find. The engine has no value-size cap, matching the reader. Cancellation is the
only bound the engine offers; the UI progress dialog is the user-facing one.

## 11. Search in subtree (OQ-10)

OQ-10 records that the default state of the *Search in subtree* checkbox and its exact scope
are not fully known. This engine defaults `SearchInSubtree` to `true`.

The oracle now has a `keys-values-data-no-subtree` run. The capture cleared control 1161 with
`BM_SETCHECK` and confirmed `BM_GETCHECK == 0` before Find Next. Classic still reported all 13
hits from the seeded subtree, including values in child keys. The unchecked box therefore does
not mean "starting key only". The engine follows that evidence: `SearchInSubtree` does not
narrow the walk. Both values use the same continuation stack and the configured `Roots` define
the scope.

What the checkbox changes beyond the seeded tree is not captured. A run that starts in one key
and lets classic leave that subtree would be needed to pin down the cross-root stop. Until then,
the cross-subtree scope stays provisional and the flag remains a UI and persistence value, not a
reason to skip child keys.

## 12. Custom roots and scope

`RegistryFindQuery.Roots` defaults to `HKCR`, `HKCU`, `HKLM`, `HKU`, `HKCC` in that order. A
caller can pass one or more `RegistryPath` values instead, for example a single sandbox key, to
bound the search. Order is preserved and the walk does not wrap.

A custom root is itself checked as a key. This is useful for tests and for a UI that wants to
search one tree. Overlapping custom roots can report the same key more than once. Empty root
lists and null entries are rejected.

## 13. E2E seed and cleanup

Finder tests run in the E2E flavor against the real registry. Each test creates one unique
`HKCU\Software\BWC-E2E\find-<guid>` key with the 64-bit view, seeds a small tree through
`RegistryStore` and `SandboxWritePolicy`, and deletes the run root in a `finally` block with
`DeleteSubKeyTree(..., throwOnMissingSubKey: false)`. The oracle replay test seeds a deeper
`...\find-<guid>\Find` root from `find-sequence.json`. The run identifier is a GUID; its hex
digits never become part of a searched name because the custom root is the `Find` key.

The finder tests pass only their run root as `Roots`, so they do not scan the rest of the
registry and do not depend on unrelated machine state. Deleting the run root also removes any
value or subkey left by a failed assertion. The shared parent `HKCU\Software\BWC-E2E` is not
deleted because other test classes use it.

## 14. Test gaps

Two races are not tested against the real registry:

- A key disappearing between the parent's name enumeration and the finder's value read. The
  reader's `ERROR_FILE_NOT_FOUND` path covers the lower layer, and the finder's catch around
  enumeration covers the skip, but no deterministic E2E hook deletes the key at that exact
  moment.
- A security descriptor change that denies read access only after a cursor was created. The
  skip path is implemented with `RegistryOperationException`, but creating and restoring such a
  descriptor reliably needs privileges the E2E suite does not assume.

A cursor whose key was deleted and a cursor whose value was deleted are both tested: the finder
continues at the nearest later position instead of throwing, wrapping, or reporting finished.
A deleted cursor with no later position returns `null`.

## 15. Classic FindFlags codec

The classic Find dialog uses one in-memory mask for its five controls. The mask table in
`C:\Windows\regedit.exe` 10.0.26100.8457 stores these control id and bit pairs:

| Control | Mask | Meaning |
| --- | --- | --- |
| 1151 | `0x0001` | Match whole string only |
| 1152 | `0x0002` | Keys |
| 1153 | `0x0004` | Values |
| 1154 | `0x0008` | Data |
| 1161 | `0x0010` | Search in subtree |

The runtime capture shows that this build **does not persist the mask**. Seven option sets were
run, including one with Search in subtree cleared and confirmed by `BM_GETCHECK`. Every run
left `HKCU\Software\Microsoft\Windows\CurrentVersion\Applets\Regedit\FindFlags` at
`0x00000000`. Classic saved `LastKey` and `View` on exit (the final `LastKey` was the last hit
outside the seed tree) but did not update `FindFlags`. Its loader reads the DWORD at startup and
its saver writes the same startup value back; the dialog only updates the in-memory mask.

BWC must add the write itself. `RegistryFindFlagsCodec` uses the confirmed mask so a value BWC
writes is readable by classic's loader and by any future build that restores the write. Higher
bits, including the high-word match-case flag used by some reimplementations, are ignored because
classic Find has no match-case control (oracle `matchCase` note). The default used when the value
is missing or is not a DWORD is `Keys | Values | Data | SearchInSubtree` (`0x001E`); the subtree
default remains OQ-10. The caller reads and writes the DWORD through `RegistryReader` and
`RegistryWriter`; the codec itself performs no registry I/O, so it cannot bypass the write
policy. `RegistryFindFlagsCodec.CreateQuery` and `FromQuery` convert between the DWORD and
`RegistryFindQuery`, which is the state F3 needs.

The legacy `RegeditFindFlags` enum in `Bwc.Regedit` uses the wrong layout (`Keys=0x1`). RF6 must
replace its use with this codec when it writes the classic applet key. Until then, the enum is a
private BWC settings encoding and no classic DWORD is produced from it.

## 16. Oracle replay and artifact

`RegistryFinderTests.OracleSequenceReplaysForEveryCapturedRun` reads
`docs/parity/oracle/find-sequence.json`, seeds the same tree under a fresh sandbox root, and
replays all seven runs. The comparison is the recorded `(key, value)` pair per hit, in order.
Because the test roots the search at the seeded `Find` key, a run ends when the walk is
exhausted rather than when classic leaves the tree. The recorded hits are still the full prefix
that the oracle saw. The no-subtree run records the same 13 hits, which is why the engine no
longer treats the unchecked box as starting-key-only.

`FindFlagsCodecMatchesCapturedOracleBits` checks the captured DWORDs, the per-run layout, and
the `findFlagsDialogMask` control table. In this build all captured DWORDs are zero and every
layout has empty candidate bits, so the runtime part confirms the absence of a classic write.
The mask part then checks the codec against the dialog's confirmed bit assignment.
`FindFlagsRoundTripUsesClassicBits` covers encode and decode.

`OracleSortOrderMatchesTheCapturedClassicOrder` seeds the names from
`docs/parity/oracle/sort-order.json`, searches a common data value, and asserts the reported
value order. That covers the NLS ordering, `(Default)` first, text digit runs, and punctuation.
The replay test writes `artifacts/find-parity/result.json` with each run name, the captured
FindFlags DWORD, expected hit count, actual hit count, pass flag, and the hit pairs. The file is
diagnostic and does not replace the assertions.

## 17. Cursor key whose parent cannot be enumerated (F3)

`FindNext` resumes from the cursor key's path. The continuation rebuilds the siblings after the
cursor at each level by listing that level's parent. When the cursor's parent cannot be listed
(access denied, or the parent was removed), no sibling list exists for that level, so the cursor
key's own presence cannot be read from the listing. Intended behavior: the finder probes the cursor
key itself with `KeyExists`. If the key still exists, it is pushed back on the continuation and its
subtree is searched, so F3 does not skip the cursor key's subtree. If the probe fails with an error
other than "not found", the key is treated as present, so the search does not skip it. No automated
test: the sandbox cannot make a parent unreadable without the writer's own guard failing on it, because
the guard needs query and enumerate rights on the parent to restore its descriptor. The probe is
checked by review of `RegistryFinder.cs`.

Name matching for `Contains` uses one `FindNLSStringEx` call with `FIND_FROMSTART` and
`NORM_IGNORECASE` per search, not one `CompareStringEx` call per offset. The intent is the same match:
a needle-length window at any UTF-16 offset compared under the same collation. The per-offset loop
remains only as the fallback after a native error. Tests: the existing `RegistryFinderTests` and the
oracle replay (`find-sequence.json`) must pass unchanged.

## 18. F3 after the user moves the selection (parity package)

| Way it could fail | Expected behavior |
| --- | --- |
| F3 resumes from the last hit after the user selected another key | When the tree or list selection no longer matches the last hit, F3 searches from the current selection, as classic does |
| F3 shows "finished" after the user moved to a key before the exhausted point | A moved selection clears the exhausted state for that search |
| F3 with the selection equal to the last hit | Resumes after the hit (unchanged) |
| F3 after a value hit when the user selected a different value in the same key | Starts from the selected value, not the hit |
| Persisted last hit after restart | Compared with the selection the same way |

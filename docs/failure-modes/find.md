# Registry find failure modes

This document covers `RegistryFinder` in `src/Bwc.Registry/Find`. The finder is a read-only
engine. It calls `RegistryReader` only, never a mutating Win32 API, and has no UI dependency.
It is synchronous and may run on any thread.

The contract is `docs/contracts/registry-engine.md` under `Find`. The inventory rows are
BEH-039 to BEH-042, DLG-010, DLG-011, OQ-10, and OQ-11.

## 1. Traversal order

The walk is depth-first pre-order. At one key the order is:

1. The key name, when *Keys* is enabled.
2. Each value in display order: the default value first, then the other values sorted with
   `StringComparer.CurrentCultureIgnoreCase`. For each value, the value name when *Values* is
   enabled, then the value data when *Data* is enabled.
3. Each subkey in the same culture-aware order, recursively, using an explicit stack.

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
siblings and its ancestors' later siblings. If that continuation cannot be built, for example
because an ancestor's child list is unreadable, the finder falls back to a normal walk from the
first root. If the cursor position is never reached, `Find` returns `null`. It does not restart
from the beginning and it does not guess a nearby position.

A cursor whose key is not inside any configured search root is never reached and returns `null`.

## 4. Default value name

The default value's real name is the empty string. `ValueName` is `null` for a key-name hit and
the empty string for a hit on the default value. `(Default)` is a UI display string, not a
registry name. Searching value names for `Default` therefore does not match the default value.
The default value can still produce a data hit.

## 5. Data matching (OQ-11, provisional)

The Data check uses the following pragmatic mapping. Classic regedit's exact set is OQ-11 and
has not been captured in a golden sequence.

- `REG_SZ` and `REG_EXPAND_SZ`: the decoded display text, stopping at the first UTF-16 NUL.
- `REG_MULTI_SZ`: each decoded string is a candidate. The joined display text (single spaces
  between items) is also a candidate, so a search phrase can span item boundaries. A hit
  reports the individual string that matched when one did, otherwise the joined text.
- `REG_DWORD` and `REG_QWORD`: both the invariant decimal text and the `0x` hexadecimal text
  are candidates. A valid four-byte DWORD or eight-byte QWORD searches decimal first, then hex.
  An invalid length falls back to the hex byte display.
- `REG_BINARY`, `REG_NONE`, unknown types, and every other type not listed above: the hex byte
  display from `RegistryValueDisplay.FormatData`, for example `AE 01`. Arbitrary bytes are
  never decoded as text.
- Zero-length data is not searched. The display placeholders `(value not set)` and
  `(zero-length binary value)` are UI text, not value data, and must not produce a Data hit.
- `REG_LINK` is treated as an opaque type and searched as hex. This is provisional. The display
  layer treats a link as text, so a later oracle capture may align find with display.

A Data hit reports one `MatchedText`, the specific candidate that matched. A single value
produces at most one Data hit per search step even when several of its strings or several of
its decimal and hex forms contain the search text.

## 6. Duplicate hits

Positions are distinct. One value can produce a value-name hit and then, on the next step, a
data hit. Those are two legitimate find stops. A key can produce a key-name hit and later a hit
inside the same key's values. Repeating an identical position is not allowed.

If the same key is reachable through overlapping configured roots, it is walked once per root.
Callers that supply custom roots should avoid overlapping roots when duplicate reporting would
be confusing.

## 7. Matching and culture

Matching uses `StringComparison.CurrentCultureIgnoreCase`. Substring search uses
`string.Contains` with that comparison; whole-string search uses `string.Equals` with it. This
is linguistic, not ordinal and not invariant. The value and key sort uses
`StringComparer.CurrentCultureIgnoreCase`, which matches the display-order approximation in the
contract.

Regedit uses `CompareStringEx` with `NORM_IGNORECASE` (`lstrcmpiW`) for display order. The .NET
current-culture comparer is close but not guaranteed to be byte-for-byte identical for every
locale. Digit runs such as `Key2` against `Key10` remain unverified against the oracle.

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

Data matching for a large binary or opaque value materializes the full hex display text, so
memory use grows with the value size. The engine has no value-size cap, matching the reader.
Cancellation is the only bound the engine offers; the UI progress dialog is the user-facing
one.

## 11. Search in subtree (OQ-10)

OQ-10 records that the default state of the *Search in subtree* checkbox, its interaction with
the search start key, and whether it takes effect are unknown. This engine defaults
`SearchInSubtree` to `true`.

When it is `false`, only the starting key is searched. With a cursor, that is the cursor's key:
its values are checked after the cursor phase, and no child keys are visited. Without a cursor,
each configured root is checked without descending. Predefined roots have no values, so a
subtree-disabled search with only predefined roots normally returns `null`.

This interpretation is provisional. The Win32 dialog dump does not say whether the unchecked
box means "selected key only" or something else, and no oracle run has captured it.

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
`DeleteSubKeyTree(..., throwOnMissingSubKey: false)`.

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

A cursor whose key was deleted is tested: the finder returns `null` instead of throwing or
wrapping.

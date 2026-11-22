# Registry.pol codec and ordered model failure modes

Scope: piece E1 of release 0.2.0, `src/Bwc.Gpo/Pol`. A pure codec: bytes in, `PolDocument` model, bytes out. No file
I/O, no locking, no atomic write (those are later pieces). The binding spec is
`docs/contracts/policy-engine.md`, sections "Registry.pol format" and "Ordered model". Items marked *verify against
oracle* are assumptions until the classic probe goldens arrive; each lives in one named place in
`src/Bwc.Gpo/Pol/PolOrderRules.cs` (ordering) or `PolReader.cs` (strictness) so a correction touches one spot.

Every parse problem is a `PolFormatException` carrying the byte offset and a `PolFormatReason`. Parsing never throws
any other exception type for any byte sequence. Edit calls on a model throw `ArgumentException` for caller mistakes
(an invalid key, a data shape that does not match the type); those are programming errors, not file errors.

## 1. Reading

### 1.1 Empty file
Zero bytes: `PolFormatException`, reason `Empty`, offset 0. The caller (the store piece) decides whether a missing or
empty file means "no policy yet". Classic refuses a zero-byte Registry.pol as malformed (`D/File_Empty`), so the store
reports a failure for the side and never overwrites the file.

### 1.2 Header
- Fewer than 8 bytes (but not zero): `HeaderTruncated`, offset 0.
- First four bytes not `PReg` (`50 52 65 67`): `BadSignature`, offset 0. This includes a UTF-16 or UTF-8 byte order
  mark in front of the header (`FF FE`, `EF BB BF`): the BOM is not skipped and the file is refused.
- Signature right, version not `01 00 00 00`: `BadVersion`, offset 4.
- Header only (exactly 8 valid bytes): a valid, empty document.

### 1.3 Record framing
Record: `[` key NUL `;` name NUL `;` type(4) `;` size(4) `;` data(size) `]`, delimiters are UTF-16LE characters.
- A byte after the last record that does not start a record: `ExpectedRecordStart` at that offset. A single stray byte
  or zero padding after the last `]` is refused, not skipped.
- File ends inside a record at any point: `Truncated` at the offset where more bytes were needed.
- A string with no NUL terminator before the end of the file: `UnterminatedString`, offset of the string start.
- Missing `;` after the key, name, type or size: `MissingDelimiter` at the offset of the missing delimiter.
- Missing `]` after the data: `MissingRecordEnd` at that offset. A wrong `size` therefore usually shows up here.
- Odd byte count: the format is 16-bit units for text and raw 32-bit values between delimiters. A record that starts
  on an odd offset or has a one-byte tail is `Truncated` or `ExpectedRecordStart`; the reader never reads half a unit.
- Embedded NUL in a key or name: the string ends at the first NUL, so the bytes after it are read as the next field and
  fail as `MissingDelimiter`. A NUL is never accepted inside a string.
- Huge size field (up to `0xFFFFFFFF`): checked against the bytes remaining before any allocation or cast.
  `size + 2 > remaining` is `SizePastEnd` at the size field offset. No allocation depends on the field alone, so a
  four-byte lie cannot allocate memory.
- Size 0 is valid (empty data).
- Unknown type numbers (anything except 1, 2, 3, 4, 7, 11) are accepted and kept byte for byte. The model carries the
  raw number. Editing APIs refuse to create them.
- `REG_SZ` data without a terminator, or odd-length data for a string type: accepted by the reader and kept byte for
  byte (the framing is size-driven). `PolData.TryGetString` returns null for odd length and strips one trailing NUL
  when present. Setting a string through the edit API always writes the terminator.
- Data may contain `]`, `;` or `[` bytes; only `size` frames the data.
- Unpaired UTF-16 surrogates in keys or names are read as raw 16-bit units into the string (no decoding replacement),
  so writing them back is lossless.
- Stream input larger than `PolReader.MaxStreamBytes` (64 MiB) is refused with `FileTooLarge` before it is buffered.
  An `IOException` from the stream itself is not a format problem and propagates unchanged.

### 1.4 Keys
- A key with a leading backslash is refused by the reader (`PolFormatReason.KeyLeadingBackslash`): classic refuses to open
  such a file (capture `K/KeyLeadingBackslash_ReadAsWritten`). Only that shape is refused here; the other strictness
  decisions are *verify against oracle* (D9, piece E6c).
- A key with a trailing backslash, empty segments (`A\\B`) or an empty key is accepted by the reader and kept as
  written. The model does not normalize it; `A\B` and `A\B\` are two different keys. The policy layer reads such a key as the
  trimmed one and a save that changes something rewrites it trimmed (`PolDocument.TrimTrailingKeyBackslashes`, captures
  `K/KeyTrailingBackslash_*`). The edit API refuses to create them.
- Key names are compared case-insensitively (ordinal, by uppercase). The first spelling seen for a key is kept by the
  records already stored; no record is rewritten to unify spelling.
- Name length limits (255 per key segment, 16383 for a value name) are not enforced by the codec. `Bwc.Policy` checks
  names it creates.

### 1.5 Value names and duplicates
- Value names compare case-insensitively within a key. The empty name is the key's default value and is valid.
- Two records for the same key and name (any case): load replays in file order with the same rule as `SetValue`
  (section 2.1) except that a later record with different data keeps the place of the first record and takes the later
  data (`D/Dup_DifferentLastWins`, `D/Dup_NonAdjacent`, `D/Dup_Identical`). The first spelling of the name stays. The file
  therefore does not round-trip byte for byte when it holds duplicates; the result is the normalized form.
- Two records of one key that are not adjacent in the file (`A`, `B`, `A`) are grouped under one key on write. Same
  caveat: a file not already in classic order may be normalized.
- Duplicates with an instruction between them (GPO-4, settled). An instruction record is an ordinary record that is never
  interpreted (1.6). A file with `X=1`, `**del.X`, `X=0` (or `**delvals.` in place of `**del.X`) loads as `X=0`, `**del.X`:
  the value keeps the first place and takes the later data, the instruction stays after it, as classic saves it after an
  unrelated save (`D/Dup_DelBetweenDifferent`, `D/Dup_DelvalsBetween`, `D/Dup_DelBetween`).

### 1.6 Instructions (`**` value names)
- `**del.<name>`, `**delvals.`, `**DeleteValues`, `**DeleteKeys`, `**SecureKey`, `**soft.<name>` and every other name
  starting with `**` are ordinary records to the model. They are kept, in position, byte for byte, whatever their type
  and data. The model never interprets them, so loading `**del.X` does not remove an `X` record.
- Matching of instruction names is case-insensitive like every value name, so `**DEL.x` and `**del.X` are the same
  record. Which of them classic keeps or shows is *verify against oracle*.
- A `**del.` record with data other than `20 00 00 00` or a type other than 1 is kept as is. Only
  `AddDeleteInstruction` writes the canonical form (type 1, size 4, data `20 00 00 00`). The instruction calls of
  section 2.5a write the same canonical form.

### 1.7 What classic opened and refused in group D (E6c)

Crafted files of group D tell where the reader's strictness meets classic's. Six were refused (the store tests are in
gpo-store.md, L). These that look malformed or odd were opened:

- `D/File_TrailingBytes`: two stray bytes after the last record. Classic opened the file and the next save wrote it
  without them (the third record of the start file is the stray tail, the saved file holds the other two plus the new
  ones). Section 1.3 says the reader refuses any byte after the last record (`ExpectedRecordStart`). That is stricter than
  classic and the replay row of the case fails on it until the C dispatch decides: accept a tail that cannot start a record
  (and drop it on save), or declare a difference.
- `D/File_HeaderOnly`: the 8-byte header with no record is an empty document (1.2); opens and saves.
- `D/File_LargeValueName`: a value name of 300 characters; opens, is kept and saved as written (no name length limit in
  the reader).
- `D/Instruction_*`, `D/Soft_*`, `D/Delvals*`: instructions in any place; kept.

Ways this can fail: a zero-byte file reads as "no policy yet" and a save overwrites it (it is `Empty` and refused, as in
classic: `D/File_Empty`); a stray tail refuses the file where classic opened it (above; one stray character after the last
record is ignored and dropped on the next save, `D/File_TrailingBytes`; other tails stay refused, gap GAP-05); a long value name is refused; the unrelated save changes the
bytes of the records it did not touch.

## 2. Editing

### 2.1 Set to different data moves to the end (classic probe, 2026-11-18)
`SetValue` on an existing key and name with a different type or different data removes the old record and appends a new
one at the end of the key's records. Probe: Zeta=1, then Alpha=1, then Zeta=0 gave `[Alpha=1, Zeta=0]`. The new record
reuses the stored spelling of the key and name (assumed, not probed). Rule `PolOrderRules.ReplaceByAppend`.

### 2.1c In-place sets, in-place delete instruction and the soft record (groups B and C)

The policy writer needs three more edits, each settled by a capture:

- `SetValueInPlace(key, name, type, data)` keeps the record where it is and changes its data. A record that does not exist
  is appended. Element values are set this way: `C/Booleans_ReverseOrder` step 2 and `C/TwoInKey_ChangeFirstAgain` keep the
  changed value in its place, while the value of the policy itself is still removed and appended (`SetValue`).
- `AddDeleteInstructionInPlace(key, name)` replaces the value `name` by `**del.name` at the same position. Only the
  unchecked boolean uses it (`B/Pol_Bool_NoFalseValue` step 3, `B/Pol_Amp_LabelSingle` step 3). Every other delete is
  remove the value, remove an existing `**del.`, append the new one (`C/Shared_DeleteRecordsOrder`).
- `SetSoftValue(key, name, type, data)` stores the data under `**soft.<name>`, in place when the record exists
  (`B/Pol_LongDecimal_RequiredSoftText` steps 1 and 2). The record survives Disabled and Not Configured.

### 2.1a Set to identical data changes nothing
`SetValue` with the same type and the same data bytes returns the existing record, keeps its bytes and its position,
and does not count as a change (probe: an OK that changes nothing writes no file). `AddDeleteInstruction` follows the
same rule for an existing identical `**del.` record.

### 2.1b Change tracking
`PolDocument.Version` starts at 0 after `Create` or `Parse` and increases by one for every call that changed the model
(a set with different data, a new record, a removal that removed something). Calls that change nothing leave it alone.
`HasChanges` compares the bytes the document would write now with the bytes it would have written right after load
(or the empty document after `Create`); it is false after an edit that was undone, for example a value removed and
recreated at the same end position. The store uses `HasChanges` to skip the write when nothing changed. `Version`
alone is not enough because an edit and its undo both bump it.

### 2.2 Delete then recreate
`RemoveValue` removes the record. A later `SetValue` of that name appends at the end of its key's records, not at the
old position. Rule `PolOrderRules.AppendToKey` (the contract states it; the probe confirms the same shape for
Disabled and Not Configured, see 2.7).

### 2.3 New value in an existing or new key
A new value goes to the end of its key's records. A new key has no position of its own; keys are placed only by the
sort rule at write time.

### 2.4 Edit validation (`ArgumentException`)
- Key: null, empty, containing NUL, leading or trailing backslash, or an empty segment.
- Value name: null, containing NUL, or starting with `**` (instructions are created only through
  `AddDeleteInstruction`, `AddDeleteValuesInstruction`, `AddSoftInstruction`). The empty name is allowed.
- Type other than SZ, EXPAND_SZ, BINARY, DWORD, MULTI_SZ, QWORD.
- Data shape: DWORD not 4 bytes, QWORD not 8 bytes, a string type that is not an even length of at least 2 and ending
  in a NUL unit. BINARY accepts any length including 0.

### 2.5 Delete instruction
`AddDeleteInstruction(key, name)` adds `**del.<name>` (type 1, size 4, data `20 00 00 00`) at the end of the key
(unchanged when an identical one exists, replaced by append when its data differs). It does not remove the value record `name`; the policy layer decides which
combinations are allowed. `RemoveInstructions(key, name)` removes the `**del.<name>` and `**soft.<name>` records for
that value (the per-value instruction prefixes are one array in `PolOrderRules`) and reports how many it removed.

### 2.5a Delete-all-values and soft instructions (list instructions)

Piece E1b. ADMX list semantics need two more instructions the model can write: `**delvals.` (delete every value of the
key before the following records apply; a non-additive list writes it) and `**soft.<name>` (write the value only when it
is absent). API, all on `PolDocument`:

- `AddDeleteValuesInstruction(key)`: adds or moves `**delvals.` to the front of the key.
- `RemoveDeleteValuesInstruction(key)`: removes it (false when absent).
- `AddSoftInstruction(key, name)`: adds `**soft.<name>` at the end of the key.
- `RemoveSoftInstruction(key, name)`: removes it (false when absent).

Both instructions are written as type REG_SZ, size 4, data `20 00 00 00`, like `**del.` (*verify against oracle*: the
bytes of `**delvals.` and `**soft.` in classic, capture group B lists and group D reading). Constants:
`PolFormat.DeleteValuesName`, `PolFormat.SoftPrefix`.

Failure modes and the chosen behavior:

1. **Position of `**delvals.`.** A record in front of `**delvals.` is deleted when the file is applied, so a new
   `**delvals.` goes to the front of its key, before every other record (instructions included). Rule
   `PolOrderRules.PlaceDeleteValues`, *verify against oracle* (classic may put it right before the first list entry or at
   the end of the values that were removed).
2. **`**delvals.` repeated on re-apply.** One record per key (names compare case-insensitively), so it cannot stack. When
   the record is already first and has the canonical bytes the call changes nothing: same bytes, `Version` unchanged,
   `HasChanges` false.
3. **`**delvals.` that is not first** (loaded from a file in another order, for example a value and then `**delvals.`). The
   call moves it to the front and counts as a change. Its stored spelling is kept. A loaded record with a type or data
   other than the canonical ones is replaced by the canonical record, also at the front.
4. **`**delvals.` and a later value move.** `SetValue` with different data removes the value and appends it at the end of
   the key, so it stays behind `**delvals.`. A set with identical data leaves the value where it is. Value edits never move
   `**delvals.`. Removing every value of the key leaves `**delvals.` as the key's only record.
5. **`**soft.<name>` on a key that already holds the value `<name>`.** The soft record is appended at the end of the key.
   The value record is not touched and does not move (so a soft record can end up behind its value). Rule
   `PolOrderRules.AppendToKey` as for `**del.`, *verify against oracle* (classic may put the soft record in front of its
   value). An identical soft record that exists is no change; one with other data is replaced by append.
6. **Case.** `**DELVALS.` and `**Soft.NAME` in a loaded file are the same records as `**delvals.` and `**soft.name`. An
   add finds them and keeps their stored spelling. A new record is written as `**delvals.` and `**soft.` plus the name as
   given. Whether classic matches the prefixes case-insensitively is *verify against oracle*.
7. **A key whose only record is `**delvals.`.** The key exists (`GetKeys`, `RecordCount` 1) and round-trips byte for
   byte. Removing that record removes the key; a document with no other record is then empty.
8. **Removal.** Removing a record that is not there returns false and changes nothing. `RemoveInstructions(key, name)`
   removes `**soft.<name>` as before and never `**delvals.` (it belongs to the key, not to a value). `RemoveKey` removes
   it with the rest.
9. **Unknown `**` instructions** (`**DeleteValues`, `**Mystery`) are untouched by these calls: same bytes, same order
   among themselves. A new `**delvals.` goes in front of them.
10. **Loading does not interpret.** A file with `**delvals.` followed by values keeps every record; the model never
    removes the values that the instruction would delete when applied.
11. **Validation.** An invalid key (empty, NUL, leading, trailing or doubled backslash) or a soft name with NUL is an
    `ArgumentException` and writes nothing. The empty soft name is allowed like the empty `**del.` name.

### 2.6 Removing records
- `RemoveValue` of a record that does not exist returns false and changes nothing.
- `RemoveKey` removes every record whose key matches (case-insensitive), including instructions and unknown records, and
  returns the count. It does not remove child keys (`A` does not remove `A\B`).
- A key whose last record is removed disappears from the model. A document with no records is empty.

### 2.7 Probe sequences (golden tests)
Replayed in `PolRoundTripTests` on key `Software\Policies\BWC-Oracle\Probe`, all steps from classic:
1. Zeta=1 gives `[Zeta=1]`; then Alpha=1 gives `[Zeta=1, Alpha=1]`.
2. Zeta=0 gives `[Alpha=1, Zeta=0]`.
3. Alpha Disabled with no disabledValue is `RemoveValue(Alpha)` then `AddDeleteInstruction(Alpha)`: `[Zeta=0, **del.Alpha]`.
4. Zeta Not Configured is `RemoveValue(Zeta)`: `[**del.Alpha]`, no instruction written for Zeta.
The key sorted before `Software\Policies\Microsoft\Windows\CloudContent`. Whether the policy layer calls these
methods in this combination is its own decision; this piece only guarantees the record order they produce.

### 2.8 Empty document
Writing a document with no records writes the 8-byte header. Classic leaves a header-only file for an emptied
side (`B/Pol_List_Additive` step 6, `C/Clear_LastSetting_Machine`); the choice is the one method
`PolOrderRules.EmptyDocumentBytes` (callers that must treat "empty" differently test `IsEmpty` first).

## 3. Writing and round trip

### 3.1 Key order
Keys are written in registry order: segments split on `\`, each segment compared ordinally by uppercase, a key before
its descendants (a shorter prefix sorts first), then ties broken by ordinal compare for determinism. This is a tree
walk, not a flat string compare: with a flat compare `A1` would sort before `A\B`, with the tree order `A\B` (child
of `A`) sorts before `A1`. *verify against oracle*: rule `PolOrderRules.KeyComparer`. The `Bwc.Registry` regedit
display comparer was deliberately not reused: it uses NLS comparison at the user locale, which would make saved bytes
depend on the machine's locale.

### 3.2 Non-ASCII key letters
Uppercasing follows .NET `OrdinalIgnoreCase` (Unicode simple case mapping, culture independent), so the order is the
same on every machine. Windows registry key comparison uses its own upcase table; the two can differ for rare letters.
Unit test coverage uses a few non-ASCII letters to pin the .NET behavior. *verify against oracle*.

### 3.3 Values inside a key
Values keep model order: file order for loaded records, then edit order (a change of data or a recreate appends at
the end, identical data stays where it is).

### 3.4 Record bytes
Every record carries its own complete serialized bytes. A loaded record is written back from its original bytes, an
edited or created record is serialized once at creation. The writer is a concatenation, so unknown records and unknown
types cannot be altered by the model.

### 3.5 Round-trip drift
Read then write with no edit is byte-identical when the input has no duplicate (key, name) pairs, groups each key's
records together, and lists keys in `KeyComparer` order. Any other input is normalized (see 1.5, 3.1). The invariant
test reads the real machine and user `Registry.pol` when present, and asserts identity; it is inconclusive when the
files are absent and it never writes there. If a real file fails the invariant, the file is not in classic order and
the rule that differs is the finding to record.

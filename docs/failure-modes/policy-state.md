# Policy state reader and writer failure modes

Scope: piece E4 of release 0.2.0, `src/Bwc.Policy/Model`. `PolicyStateReader` turns a policy definition and a
`PolDocument` into a state (Enabled, Disabled, Not Configured), element values and the records the policy owns.
`PolicyStateWriter` applies a `PolicyEdit` to a `PolDocument`. `PolicyEditValidator` checks element values against the
definition. The writer only calls `PolDocument` APIs and never does file I/O. The contract is
`docs/contracts/policy-engine.md`, section "Ordered model".

Every rule below is tagged:

- *probe-confirmed*: seen in classic on 2026-11-18 (policies with a value name and DWORD values only).
- *verify against oracle*: a guess. It lives in `src/Bwc.Policy/Model/PolicyRules.cs` as one named member, so a
  capture golden corrects it in one place. The member name is given as `[Rule: Name]`.

Rules for the whole piece:

- Elements are written and read in template document order. The state of a policy comes from its own records, never from
  another policy. A policy never touches a value it does not name.
- The hive is the document the caller passes. Class Both is applied by calling once per document, machine and user; the
  two states are independent.
- An apply that changes nothing leaves `PolDocument.HasChanges` false (*probe-confirmed*). Every step is idempotent: a
  set with identical data, a removal that finds nothing and a delete instruction that exists are no-ops.
- Invalid element values never reach the document: `Apply` validates first and returns the problems, with no write.
- Typed problems carry the element id and a kind. The engine formats no text.

## 1. Policy value (value name, enabled and disabled values)

| Shape | Enabled writes | Disabled writes | Reads |
|---|---|---|---|
| value name, no enabledValue and no disabledValue | value = DWORD 1 (*probe-confirmed*) | value = DWORD 0 (*verify against oracle*, `[Rule: PlainValueNameDisabledWritesZero]`; the probe saw Zeta move to the end of its key, which needs a written 0) | record equal to 1: Enabled; equal to 0: Disabled |
| enabledValue and disabledValue | enabledValue | disabledValue (*probe-confirmed* for DWORD) | equal to either |
| enabledValue only | enabledValue | remove the value record, append `**del.<name>` (*probe-confirmed*) | equal to enabledValue: Enabled; `**del.` only: Disabled |
| disabledValue only | DWORD 1 (default) | disabledValue | equal to either |
| decimal values | DWORD (QWORD above 32 bits) | same | type and data must equal |
| string values | REG_SZ | REG_SZ | type and data must equal |
| delete literal as enabled or disabled value | remove the record, append `**del.<name>` (*verify against oracle*, `[Rule: DeleteLiteralWritesDeleteInstruction]`) | same | `**del.` with no record matches |
| no value name | nothing for the policy value | nothing | see section 2 |

Defaults 1 and 0 are `[Rule: DefaultEnabledValue]` and `[Rule: DefaultDisabledValue]` (*verify against oracle*; the
enabled default is *probe-confirmed*).

Not Configured removes the value record and writes no instruction (*probe-confirmed*). It also removes `**del.<name>` and
`**soft.<name>` for the value, so the state cannot read back as Disabled (*verify against oracle*,
`[Rule: NotConfiguredClearsInstructions]`).

### 1.1 Value in the file that does not match

- A record whose type or data equals neither literal (a DWORD 2 where 1 and 0 are expected, a REG_SZ where a DWORD is
  expected) reads as Not Configured (*verify against oracle*, `[Rule: UnmatchedValueState]`). Enabling then
  replaces it and moves it to the end of its key.
- Type comparison is exact: REG_SZ never matches REG_EXPAND_SZ and a DWORD never matches a REG_SZ of digits
  (*verify against oracle*, `[Rule: StoredTypeMatches]`).

### 1.2 Delete instructions

- Only `**del.Alpha` in the key: Disabled (*verify against oracle*, `[Rule: DeleteInstructionOnlyIsDisabled]`). Open
  lead: classic may show Not Configured, because choosing Not Configured on that state wrote nothing (probe lead). The
  two rules disagree on what OK writes, so the oracle settles them together; flipping the one member flips both.
- `**soft.` and `**delvals.` are never written by the engine today (`PolDocument` has no API for them). They are kept
  when the document is read and written back (E1), and removed with the value they belong to by Not Configured.
- Enabled removes `**del.<name>` for every name it writes.
- An instruction that is not owned by the policy (another policy's name, an unknown `**` name) is never touched.

## 2. Policy with no value name

| Shape | Enabled writes | Disabled writes | State is read as |
|---|---|---|---|
| enabledList only | every item at its key (item key, else list default key, else policy key) | nothing for the list; no value record can say Disabled | Enabled when every item record matches; else Not Configured |
| disabledList only | nothing | every item | Disabled when every item matches; else Not Configured |
| both lists | enabledList items; disabledList items are removed | disabledList items; enabledList items removed and `**del.` written | the list that matches in full |
| elements only | each element's value | each element's value removed and `**del.<valueName>` written (*verify against oracle*, `[Rule: DisabledClearsElementsWithDelete]`) | Enabled when any element has a value (*verify against oracle*, `[Rule: IsEnabledByElementEvidence]`); Disabled when a `**del.` for an element name exists and nothing is Enabled |
| nothing at all | nothing | nothing | Not Configured |

- With a value name, the elements are written after the value and the state comes from the value only. Without one, the
  state comes from the elements. Both are *verify against oracle*.
- An item with no value in the template is skipped (*verify against oracle*, `[Rule: ValuelessListItemIsSkipped]`).
- Items that name another key (item key, default key, element key) are read and written in that key. Disabling a policy
  with a two-key list removes values from both keys.
- A policy with no value name whose Enabled edit writes no record (a disabledList-only policy, or its only element is an
  unchecked boolean with no false side) reads back as Not Configured, because nothing in the file says Enabled. Apply
  reports `Changed` false for it. Classic may write something the model does not know (*verify against oracle*).
- Enabled removes the values of the disabledList that it does not write, without a delete instruction (*verify against
  oracle*, `[Rule: EnabledClearsDisabledListWithDelete]`, false).

## 3. Element kinds

For every element: an absent value (not in the edit) is "empty". A required element that is empty is a `Required`
problem. An optional empty element removes its value and writes no instruction (*verify against oracle*,
`[Rule: EmptyOptionalElementWritesDeleteInstruction]`, false). On read, no record means no value in the snapshot (a
partial set of element values is normal). An element only reads and writes when the policy is Enabled. Disabled and Not
Configured ignore the edit's element values.

### 3.1 boolean

| Shape | Check on | Check off | Reads |
|---|---|---|---|
| trueValue and falseValue | trueValue | falseValue | true or false by equality; no match: absent |
| trueValue, no falseValue | trueValue | remove the value; no instruction (*verify against oracle*, `[Rule: FalseBooleanWithoutFalseValueWritesDeleteInstruction]`, false) | equal: true; no record: false |
| trueList and falseList | every true item | every false item; true items removed | all true items match: true; all false items match: false |
| trueList only | true items | true items removed | match: true; else false |
| no values at all and a value name | DWORD 1 | DWORD 0 (*verify against oracle*, `[Rule: BooleanDefaultsToOneAndZero]`) | equal to 1: true; 0: false |
| no value name | nothing | nothing | absent |
| string values | REG_SZ | REG_SZ | exact |

Items of a list can sit in different keys. A false that writes nothing removes the values the true side wrote. Empty
(absent in the edit) removes the values of both sides.

### 3.2 decimal (DWORD) and longDecimal (QWORD)

- Writes DWORD, or QWORD for longDecimal. With `storeAsText` both write REG_SZ holding the invariant decimal digits
  (*probe-confirmed* shape from the contract, *verify against oracle* for the digit format).
- Range: `minValue` default 0, `maxValue` default 4294967295 for decimal and 18446744073709551615 for longDecimal. Below the
  minimum is `BelowMinimum`, above the maximum is `AboveMaximum`; both carry the limit. `soft` does not change the
  range check (*verify against oracle*, `[Rule: SoftIgnoredByValidation]`).
- Reads: DWORD (or QWORD) data; REG_SZ digits for `storeAsText`. Any other type or digits that do not parse: absent.
- A value above the 32-bit range in a template literal is written as QWORD (an edge the schema allows).

### 3.3 text

- REG_SZ; REG_EXPAND_SZ when `expandable`. The string is stored with one terminator.
- Empty text is empty (see above). A NUL character is `InvalidCharacter`.
- Length over `maxLength` is `TooLong` with the limit. Without `maxLength` the limit is 1023 (*verify against oracle*,
  `[Rule: DefaultTextMaxLength]`).
- Reads: the type must match `expandable` exactly.

### 3.4 multiText (REG_MULTI_SZ)

- Data is each line followed by NUL, then one more NUL. Empty lines are dropped (*verify against oracle*,
  `[Rule: NormalizeMultiTextLines]`). No lines left is empty.
- `maxStrings` counts non-empty lines (`TooManyLines`). `maxLength` counts all characters of the lines plus one
  separator between lines (`TooLong`, *verify against oracle*, `[Rule: MultiTextLength]`).
- Reads: REG_MULTI_SZ only; lines split on NUL, the final terminator dropped.

### 3.5 enum

- The edit holds an item index. An index outside the items is `InvalidChoice`.
- Writes the item's value at the element's value name (decimal as DWORD, string as REG_SZ), then the item's value list.
  Values of the other items' value lists are removed, so no stale list stays (*verify against oracle* for the removal,
  `[Rule: EnumClearsOtherItemLists]`).
- Reads the first item whose value equals the record. No record, or no match (a foreign value): absent. The value list is
  not used to choose the item.
- An item with no value writes only its value list. An element with no value name writes only the value list.

### 3.6 list

- The list owns its whole key. Entries are string values (REG_SZ; REG_EXPAND_SZ when `expandable`).
- Names (*verify against oracle*, OQ-06, `[Rule: ListEntryName]`):
  - `explicitValue`: the entry's name and value as given; a missing name is `EntryNameMissing`.
  - `valuePrefix` that is not empty: `Prefix1`, `Prefix2` and so on, one-based, in entry order.
  - otherwise (no attribute, or an empty prefix): the name is the data (value name = data).
- Empty entry data is `EntryEmpty`. Two entries with the same name (case-insensitive) are `DuplicateEntry`. A name that
  starts with `**` or holds NUL is `InvalidCharacter`.
- Non-additive (default): the key holds exactly the new entries, in order, after the write. When the existing values
  already equal the new entries (name, type, data, order) nothing is touched. Classic also writes `**delvals.` for the
  key; the model has no API for it, so it is not written yet (*verify against oracle*,
  `[Rule: ListReplaceWritesDelvals]`, false).
- Additive: the same, because removing an entry in the dialog must remove it from the file. The brief said an additive
  list keeps the values that are in the key but not in the entries; `[Rule: AdditiveListKeepsOtherValues]` (false)
  switches to that (*verify against oracle*).
- An empty list removes every value of the key. Required and empty is `Required`.
- Reads: every non-instruction record of the key whose type matches, in model order, as (name, data). Records of another
  type are foreign and ignored. A list-only policy is Enabled when the key has an entry.
- Disabled removes every value of the list key. A `**delvals.` record in the list key reads as Disabled when no entry is
  there (*verify against oracle*, `[Rule: ListDelvalsOnlyIsDisabled]`), but Disabled cannot write it yet.
- Two policies that share a list key read the same entries. Not Configured on one removes every value of the key
  (*verify against oracle*, `[Rule: NotConfiguredClearsWholeListKey]`).

## 4. Order of records (*verify against oracle*, `[Rule: WriteOrder]`)

Inside one apply: stale removals first, then the policy value, the enabledList or disabledList, then the elements in
template order (an enum writes its value before its value list, a boolean its true or false side), then delete
instructions. A value set to different data moves to the end of its key (*probe-confirmed*); a value with identical data
stays.

## 5. Other documents and foreign data

- Shared key (several policies with a value name in one key): each policy touches only its own names.
  *Probe-confirmed* order: enabling Zeta then Alpha, then disabling Zeta, gives Alpha then Zeta.
- Foreign values in the key (names no template names) stay byte for byte and are not in `OwnedRecords`.
- `OwnedRecords` lists, per policy: every existing value record at a name the policy writes, every `**del.` and
  `**soft.` record for those names, and every record of a list key. The result list and "Extra Registry Settings" use it.
- Class Both: the policy reads and writes each document on its own; an Enabled machine side says nothing about the user
  side.
- A key or value name the template gives that `PolDocument` refuses (a name starting with `**`, a key with a leading
  backslash) throws `ArgumentException` from the document call. This is a template fault the loader should have
  refused; the apply may then be partial. It is not a user error.
- A document that changes between `Read` and `Apply` is fine: `Apply` reads the document it is given, and the save
  sequence rereads the files under the lock (contract, "Save sequence").
- `Apply` with a policy that has no value name, no list and no elements (Enabled) writes nothing and reports no change.

## 6. What the tests assert

- The probe replay on `Pol_Shared_Zeta` and `Pol_Shared_Alpha`, and on `Pol_Value_Decimal` and `Pol_Value_EnabledOnly`:
  bytes after every step, `HasChanges` after a no-change apply.
- Each element kind written and read back, and Disabled and Not Configured for every shape in the table above.
- Validation: out of range, too long, required empty, wrong kind, unknown element, invalid choice, list problems.
- Foreign values and instructions that stay, wrong types, class Both on two documents, a shared list key.

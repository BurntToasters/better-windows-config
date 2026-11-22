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
| value name, no enabledValue and no disabledValue | value = DWORD 1 (*probe-confirmed*) | remove the value record, append `**del.<name>` (`[Rule: PlainValueNameDisabledWritesZero]`, false; captures `A/Machine_Disabled`, `A/User_Disabled`, settled; the earlier guess of DWORD 0 was wrong) | record equal to 1: Enabled; `**del.` only: Disabled (captures `A/Machine_Disabled`, `A/User_Disabled`); a DWORD 0 still reads as Disabled (`[Rule: PlainValueNameZeroReadsAsDisabled]`, *verify against oracle*, group D) |
| enabledValue and disabledValue | enabledValue | disabledValue (*probe-confirmed* for DWORD) | equal to either |
| enabledValue only | enabledValue | remove the value record, append `**del.<name>` (*probe-confirmed*) | equal to enabledValue: Enabled; a lone `**del.` reads as Not Configured (D7, `B/Pol_Value_EnabledOnly` step 1) |
| disabledValue only | DWORD 1 (default) | disabledValue | equal to disabledValue: Disabled; the DWORD 1 written by Enabled reads as Not Configured (D7, `B/Pol_Value_DisabledOnly` step 1) |
| decimal values | DWORD (QWORD above 32 bits) | same | type and data must equal |
| string values | REG_SZ | REG_SZ | type and data must equal |
| delete literal as enabled or disabled value | remove the record, append `**del.<name>` (*verify against oracle*, `[Rule: DeleteLiteralWritesDeleteInstruction]`) | same | `**del.` with no record matches |
| no value name | nothing for the policy value | nothing | see section 2 |

Defaults 1 and 0 are `[Rule: DefaultEnabledValue]` and `[Rule: DefaultDisabledValue]` (*verify against oracle*; the
enabled default is *probe-confirmed*).

Not Configured removes the value record and writes no instruction (*probe-confirmed*). Over Disabled it also removes the
`**del.<name>` instructions, but not over Not Configured: Not Configured over a lone `**del.` writes nothing and leaves it
(`B/Pol_Value_EnabledOnly` step 3, `B/Pol_Value_EnabledListOnly` step 3). `**soft.<name>` stays through Disabled and Not
Configured (`B/Pol_LongDecimal_RequiredSoftText` steps 3 and 4). Disabled over Disabled saves nothing
(`B/Pol_Value_EnabledOnly`, `B/Pol_Class_Machine`).

### 1.1 Value in the file that does not match

- A record whose type or data equals neither literal (a DWORD 2 where 1 and 0 are expected, a REG_SZ where a DWORD is
  expected) reads as Not Configured (*verify against oracle*, `[Rule: UnmatchedValueState]`). Enabling then
  replaces it and moves it to the end of its key.
- Type comparison is exact: REG_SZ never matches REG_EXPAND_SZ and a DWORD never matches a REG_SZ of digits
  (*verify against oracle*, `[Rule: StoredTypeMatches]`).

### 1.2 Delete instructions

- A lone `**del.<name>` reads as Disabled when the policy declares no enabledValue and no enabledList (`B/Pol_Class_Machine`,
  `B/Pol_Value_DisabledOnly`), and as Not Configured otherwise (D7, `B/Pol_Value_EnabledOnly`,
  `B/Pol_Value_EnabledListOnly`). The rule is `PolicyRules.LoneDeleteReadsAsDisabled`. An unchecked boolean box that wrote
  `**del.<name>` reads as Disabled (`B/Pol_Bool_NoFalseValue` step 3); a policy whose only boxes are unchecked reads as
  Disabled (`B/Pol_Bool_TrueFalseValues` step 3).
- A decimal element with `soft="true"` is stored under `**soft.<name>` with the typed data of the number (DWORD, QWORD or
  REG_SZ digits), not under its own name (`B/Pol_LongDecimal_RequiredSoftText` steps 1 and 2). The soft record counts as
  a value, so the policy keeps reading Enabled after Disabled and Not Configured. `B/Pol_Decimal_Soft` is on the awaiting
  recapture list (CAP-4), so only the long decimal case settles it.
  `**delvals.` is written for lists only, see section 7.
- Enabled removes `**del.<name>` for every name it writes.
- An instruction that is not owned by the policy (another policy's name, an unknown `**` name) is never touched.

## 2. Policy with no value name

| Shape | Enabled writes | Disabled writes | State is read as |
|---|---|---|---|
| enabledList only | every item at its key (item key, else list default key, else policy key) | removes every item and writes `**del.<item>` in the item's key (product today), or writes nothing (the earlier reading); *verify against oracle*, matrix B Pol_Value_EnabledListOnly Disabled; no value record can say Disabled | Enabled when every item record matches; else Not Configured |
| disabledList only | nothing | every item | Disabled when every item matches; else Not Configured |
| both lists | enabledList items; disabledList items are removed | disabledList items; the enabledList items stay (`B/Pol_Value_BothLists` step 2) | the list that matches in full |
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
- Disabled writes `**del.` for the enabledList items only when the policy has no value name. Not Configured removes the
  disabledList and, with no value name, the enabledList; with a value name it keeps the enabledList
  (`B/Pol_Value_BothLists` steps 2 to 4, `B/Pol_Value_EnabledListOnly`). The enabledList rule is fitted to those two cases
  alone and stays uncertain.

## 3. Element kinds

For every element: an absent value (not in the edit) is "empty". A required element that is empty is a `Required`
problem. An optional empty text writes an empty REG_SZ and an optional empty multi text writes one NUL (`B/Pol_Amp_LabelDouble`
step 1, `B/Pol_MultiText_Basic` step 1); an optional empty element of another kind removes its value and writes no
instruction (*verify against oracle*, `[Rule: EmptyOptionalElementWritesDeleteInstruction]`, false). An enum with no item
and a list with no row are refused as `Required` whether the element is required or not (D8, `B/Pol_Enum_DecimalItems`,
`B/Pol_List_Plain` step 7, `C/SharedList_EmptyList`). On read, no record means no value in the snapshot (a
partial set of element values is normal). An element only reads and writes when the policy is Enabled. Disabled and Not
Configured ignore the edit's element values.

### 3.1 boolean

| Shape | Check on | Check off | Reads |
|---|---|---|---|
| trueValue and falseValue | trueValue | falseValue | true or false by equality; no match: absent |
| trueValue, no falseValue | trueValue | remove the value and write `**del.<name>` in its place (`[Rule: FalseBooleanWithoutFalseValueWritesDeleteInstruction]`, true; `B/Pol_Bool_NoFalseValue` step 3, `B/Pol_Amp_LabelSingle` step 3) | equal: true; no record: false |
| trueList and falseList | DWORD 1 at the value name, then every true item | `**del.<name>` in place of the value, then every false item; the true items stay (`B/Pol_Bool_TrueFalseLists` steps 2 and 3) | all true items match: true; all false items match: false |
| trueList only | true items | true items removed | match: true; else false |
| no values at all and a value name | DWORD 1 | `**del.<name>` in place of the value, no DWORD 0 (`B/Pol_Amp_LabelSingle` step 3) | equal to 1: true; no record: false |
| no value name | nothing | nothing | absent |
| string values | REG_SZ | REG_SZ | exact |

Items of a list can sit in different keys. The unchecked delete instruction takes the place of the value, so the record
keeps its position (`C/Booleans_ReverseOrder` step 2 keeps Zeta before Alpha). Lists of the other side stay.

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
  Values of the other items' value lists stay: choosing another item keeps the earlier lists
  (`[Rule: EnumClearsOtherItemLists]`, false; `B/Pol_Enum_ItemValueList` steps 3 and 4).
- Reads the first item whose value equals the record. No record, or no match (a foreign value): absent. The value list is
  not used to choose the item.
- An item with no value writes only its value list. An element with no value name writes only the value list.

### 3.6 list

- The list owns its whole key. Entries are string values (REG_SZ; REG_EXPAND_SZ when `expandable`).
- Names (*verify against oracle*, OQ-06, `[Rule: ListEntryName]`):
  - `explicitValue`: the entry's name and value as given; a missing name is `EntryNameMissing`.
  - `valuePrefix` present, even empty: `Prefix1`, `Prefix2` and so on, one-based, in entry order. An empty prefix gives
    `1`, `2` (`B/Pol_List_EmptyPrefix` step 2).
  - no attribute: the name is the data (value name = data).
- Empty entry data is `EntryEmpty`. Two entries with the same name (case-insensitive) are `DuplicateEntry`. A name that
  starts with `**` or holds NUL is `InvalidCharacter`.
- Non-additive (default): the key holds `**delvals.` and then exactly the new entries, in order, after the write
  (`[Rule: ListReplaceWritesDelvals]`, true, settled by `B/Pol_List_Plain` and `C/SharedList_OneThenTwo`; section 7). When the existing values already equal
  the new entries (name, type, data, order) they are not rewritten.
- Additive: the key keeps the values the policy does not own and gets the entries; section 7
  (`[Rule: AdditiveListKeepsOtherValues]`, true, *verify against oracle*).
- An empty list is refused as `Required` and writes nothing, required or not (D8: `B/Pol_List_Plain` step 7,
  `C/SharedList_EmptyList`). The Show Contents box refuses an empty list, so the list a dialog shows keeps its stored rows.
- Reads: every non-instruction record of the key whose type matches, in model order, as (name, data). Records of another
  type are foreign and ignored. A list-only policy is Enabled when the key has an entry.
- Disabled removes every value of the list key and writes `**delvals.` (`[Rule: DisabledListWritesDelvals]`, section 7).
  A `**delvals.` record in the list key reads as Disabled when no entry is there (*verify against oracle*,
  `[Rule: ListDelvalsOnlyIsDisabled]`).
- Two policies that share a list key read the same entries. Not Configured on one removes every value of the key and
  its `**delvals.` (*verify against oracle*, `[Rule: NotConfiguredClearsWholeListKey]`).

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
- A value name starting with `**` that `PolDocument` refuses throws `ArgumentException` from the document call. This is a
  template fault the loader should have refused; the apply may then be partial. It is not a user error. A key with a
  leading backslash is not refused: the save succeeds and writes no record for it (section 12, capture
  `K/KeyLeadingBackslash_Enable`).
- A document that changes between `Read` and `Apply` is fine: `Apply` reads the document it is given, and the save
  sequence rereads the files under the lock (contract, "Save sequence").
- `Apply` with a policy that has no value name, no list and no elements (Enabled) writes nothing and reports no change.

## 6. What the tests assert

- The probe replay on `Pol_Shared_Zeta` and `Pol_Shared_Alpha`, and on `Pol_Value_Decimal` and `Pol_Value_EnabledOnly`:
  bytes after every step, `HasChanges` after a no-change apply.
- Each element kind written and read back, and Disabled and Not Configured for every shape in the table above.
- Validation: out of range, too long, required empty, wrong kind, unknown element, invalid choice, list problems.
- Foreign values and instructions that stay, wrong types, class Both on two documents, a shared list key.

## 7. List instructions: `**delvals.` and additive lists (piece E1b/E4b)

ADMX meaning (Microsoft ADMX schema): `additive="false"` (the default) replaces the values of the list key; Group Policy
expresses that as a `**delvals.` record in the key, in front of the entries. `additive="true"` adds to the values that
exist and leaves the values the policy does not own alone. The record is REG_SZ, size 4, data `20 00 00 00`
(`PolDocument.AddDeleteValuesInstruction`, failure modes in `docs/failure-modes/gpo-pol.md` 2.5a). Every rule below is
*verify against oracle* (capture group B lists, group C OQ-04, group D for reading `**delvals.` and `**soft.`) and is one
member of `PolicyRules`.

### 7.1 Writing

| Edit | Non-additive list | Additive list |
|---|---|---|
| Enabled with entries | clear the values of the key, `**delvals.` at the front, then the entries in order. When the values already equal the entries only `**delvals.` is ensured (`[Rule: ListReplaceWritesDelvals]`) | set the entries; values that are not entries stay; remove the entries in `ListValue.Removed` that still hold the data; remove a `**delvals.` of the key (`[Rule: AdditiveListKeepsOtherValues]`, `[Rule: AdditiveListRemovesDelvals]`) |
| Enabled with no entries | remove the values and the `**delvals.`; nothing is written (`[Rule: EnabledEmptyListWritesDelvals]`, false) | remove the `Removed` entries and a `**delvals.`; nothing else |
| Disabled | remove every value of the key, write `**delvals.` (`[Rule: DisabledListWritesDelvals]`, true) | the same: the policy cannot tell its own values from foreign ones, so it clears the key like the non-additive list |
| Not Configured | remove every value and the `**delvals.` of the key (`[Rule: NotConfiguredClearsWholeListKey]`) | the same |

Failure modes and the chosen behavior:

1. **`**delvals.` position.** First record of the key, before the entries and before any other instruction (gpo-pol 2.5a
   item 1). A later entry change keeps `**delvals.` where it is and re-appends the changed entries behind it.
2. **Repeated apply.** `**delvals.` never stacks (one record per key); an apply that finds the key already in the
   written shape changes nothing (`Changed` false, `HasChanges` false).
3. **`**delvals.` plus a later value move.** Changing one entry moves only that entry to the end of the key, behind the
   other entries. `**delvals.` stays first.
4. **Soft record on a key that already holds the value.** Lists never write `**soft.`. A `**soft.` record that exists in
   a list key is kept by Enabled and Disabled, and ignored by the reader (it is an instruction). The exception is the
   existing per-value rule: writing an entry named `a` removes `**del.a` and `**soft.a` first, like any value write. Not
   Configured keeps the record too: it removes the values and `**delvals.` only. `OwnedRecords` still lists it.
5. **Case.** `**DELVALS.` in a loaded file is the policy's `**delvals.`: the reader finds it, and a write keeps its
   spelling when it is first and canonical, otherwise replaces it.
6. **A key whose only record is `**delvals.`.** A list-only policy reads Disabled with no element values
   (`[Rule: ListDelvalsOnlyIsDisabled]`); a policy with a value name takes its state from its own value only.
7. **Not Configured removes the `**delvals.`.** After Not Configured, a list key that held only the policy's records is
   gone and the document can be empty. Instructions that are not `**delvals.` stay.
8. **Two policies share a list key (one additive, one not).** Each read sees the same entries and the same `**delvals.`.
   Enabling the non-additive one replaces the key, so the additive one's entries are gone and it reads the
   replacement; enabling the additive one afterwards removes the `**delvals.` and keeps the non-additive one's entries
   (it cannot know which values are whose). Not Configured on either removes the whole key. This is the cost of the
   shared key: a policy can only be sure of values it wrote itself, and it learns that from `ListValue.Removed`, which the
   dialog fills with the entries the user deleted.
9. **Additive removal is conservative.** An entry in `Removed` is removed only when the key holds a string value of that
   name (the entry's stored name, or its data when it has none) with the same data, and the name is not also an entry of
   the new list. A value whose data differs (someone else changed it) stays. `Removed` of a non-additive list is ignored
   because the replace removes everything.
10. **Unknown `**` instructions in a list key** are never touched by any state: not removed, not moved. They are
    owned records (`OwnedRecords`) but not entries.
11. **Reader.** `**delvals.` is an instruction: never an entry, always an owned record of the list policy of its key
    (every record of a list key is owned). A `**delvals.` behind the entries (a file not written by this engine) still
    reads Enabled with the entries.

### 7.2 Changed expectations

Tests that asserted the previous, non-ADMX behavior were updated, not removed:

- `List_Plain_NameIsTheData_AndAReplaceRewritesTheKey` now expects `**delvals.` first in the key.
- `List_Additive_FollowsTheAdditiveRule` expects the foreign value to stay.
- `List_UnderAPolicyValueName_WritesTheSwitchAndTheList` and `PolicyStateFlowTests`: Disabled on a list writes `**delvals.`
  and a list-only policy reads Disabled instead of Not Configured.

## Blind review additions

Added by the blind test-first pass (tests in `tests/Bwc.Policy.Tests/Blind/StateBlindTests.cs`). Each item is a way the
reader, writer or validator could fail that the sections above do not state.

### 8. Arguments and purity

- `Read`, `Apply` and `Validate` throw `ArgumentNullException` for a null policy, document or edit.
- `Read` never changes the document: the bytes and `HasChanges` are the same before and after.
- Element ids of an edit are matched ignoring case (the edit's dictionary is case-insensitive) and the snapshot answers
  the same. A problem carries the id as the caller gave it for an unknown element.

### 9. Idempotence and round trips

- Applying the same edit twice gives `Changed` false the second time and `HasChanges` false on a document that was parsed
  from the bytes of the first apply. This holds for Enabled, Disabled and Not Configured, for every shape in section 1 to 3.
- Not Configured on a document that holds only this policy's records leaves an empty document (`IsEmpty`). Not Configured
  on a document that holds nothing for the policy reports `Changed` false.
- Enabled with values then read gives back the same element values (round trip), for every element kind, including text
  with characters outside the basic plane and with leading or trailing blanks.

### 10. Several problems in one edit

`Validate` and `Apply` list every problem of the edit, in the document order of the elements of the policy (where an
unknown element id sits in that order is not specified), not only the first. When there is at least one, the document bytes are unchanged, whatever
the other elements held.

### 11. Number edges

A longDecimal accepts `ulong.MaxValue` without `maxValue`. A decimal without `maxValue` rejects 4294967296 as
`AboveMaximum` with the limit 4294967295. A decimal at `minValue` and at `maxValue` is valid. The limit in a problem is the
limit that was broken.

### 12. Names compare ignoring case

A record is found whatever the case of its key and value name in the file (`PolDocument` semantics), so a file written
with other spelling of the key still reads the same state. Writes keep the spelling the document already has.

Backslashes at the ends of a key (captures group K):

- A key that ends in a backslash in the file reads as the trimmed key (`K/KeyTrailingBackslash_ReadAsWritten`):
  `PolicyStateReader.Read` reads from a copy with the trailing backslashes removed (`PolKeySpelling`).
- A save that changes something rewrites those keys without the trailing backslash (`PolDocument.TrimTrailingKeyBackslashes`,
  `K/KeyTrailingBackslash_Enable`); a save that changes nothing leaves the file alone. A key with a trailing backslash
  in the template is written trimmed.
- A `Registry.pol` that holds a key starting with a backslash is refused when the store opens it
  (`PolFormatReason.KeyLeadingBackslash`, `GpoStoreFailure.Malformed`, `K/KeyLeadingBackslash_ReadAsWritten`). Only that
  one shape is refused; the general strictness of the reader (D9) belongs to E6c.
- A policy whose template key starts with a backslash is saved and no record is written for it (`PolicyStateWriter`,
  `K/KeyLeadingBackslash_Enable`); `Changed` is false and `PolicySave.Save` still writes, because the state shown changed.

### 13. Order of elements in the output

Elements are written in template document order, not in name order, even when they share a key
(`Pol_Shared_ElementsReverseOrder` writes `Gate`, then `ZetaElement`, then `AlphaElement`). Two checks of one policy in
reverse name order (`Pol_Bool_TwoChecksReverseOrder`) are written `ZetaCheck` then `AlphaCheck`.

### 14. Foreign instructions

A `**del.<name>` of a value that this policy does not name, in a key this policy uses, is never touched by Enabled,
Disabled or Not Configured of this policy, and is not in `OwnedRecords`.

### 15. A template fault reaches the document call

A policy whose value name starts with `**` makes the document refuse the call (section 5). The exception is
`ArgumentException`. A policy whose key has a leading backslash writes nothing and does not throw (section 12). The loader may accept such a template (it only limits length), so the
engine must not turn it into a silent write of a wrong record.

### 16. Saving

The one save that applies an edit and its comment to the files, and the replay of the captures against it, are in
`docs/failure-modes/policy-save.md`.

# Policy save and golden replay failure modes

Scope: piece E6a of release 0.2.0. `PolicySave.Save` in `src/Bwc.Policy/Model/PolicySave.cs` (the one save every store
uses), the replay harness in `tests/Bwc.Policy.Tests/Golden`, and the file-rule tests in `tests/Bwc.Gpo.Tests/Golden`.
This page was written before the code (dispatch T); dispatch C wrote the code and marked the rules settled. The rules it names are settled by the captures in
`docs/parity/oracle/gpedit/goldens` (groups A and K for this piece); a capture is the specification. The reader and writer
rules are in `docs/failure-modes/policy-state.md`, the store in `docs/failure-modes/gpo-store.md`, the file codec in
`docs/failure-modes/gpo-pol.md`. Piece E6b (groups B and C, the option mapping and the defaults function) is in
`docs/failure-modes/policy-defaults.md`.

The contract of `PolicySave.Save(store, side, policy, edit, comment)`:

1. Validate with `PolicyEditValidator` first. A problem returns the problems and reads, locks and writes nothing.
2. One `GpoStore.Save` of the side. Inside it: `PolicyStateWriter.Apply` on the models read under the lock, then the
   comment, then `RestrictNamespacesTo(policy.Namespace)` when the state the dialog shows, a record or the comment changed.
3. `edit == null` saves the comment alone. The Registry.pol model is still read, so a malformed file refuses the save.
4. Nothing is written when the edit throws, and nothing when nothing changed.

## S. Save

| Id | Way it fails | Expected | Case, test |
|---|---|---|---|
| S1 | An invalid edit still sets the comment, takes the lock or writes. | Problems returned, `Result` null, every file as it was. | `PolicySaveContractTests.S1_...`; E group cases in E6c |
| S2 | `Apply` finds a problem under the lock (the template changed since the dialog). | The comment is not set, nothing is written, the problems come back. | no group A or K case; E group (E6c) |
| S3 | The comment is set before the state apply and the apply throws: a partial model, or a comment written without its state (GPE-3). | Models read under the lock are discarded; no file changes. | no direct test: the store writes only after the edit returns; E6c adds a throwing-template case |
| S4 | A template fault (`ArgumentException`, policy-state section 15) leaves the lock file held or a temp file behind. | The exception reaches the caller, the lock is free, no file changed. | none yet; see S12 |
| S5 | `RestrictNamespacesTo` runs on every save, so a save that changes nothing adds a `using` line and writes `comment.cmtx`. | Nothing is written: `A/Machine_NotConfigured`, the second press of `A/Machine_Apply_OK`, step 2 of `K/KeyLeadingBackslash_Enable`. | replay (written files equal `changedFiles` of the press), `PolicySaveContractTests.S5_...` |
| S6 | `RestrictNamespacesTo` has no caller (D5): the first save writes no `comment.cmtx` when there is no comment. | The first save writes `comment.cmtx` with one `using` line (`A/Machine_Enabled`, `A/User_Enabled`). | replay, `CmtxGoldenTests` |
| S7 | The namespace list keeps a namespace that no comment uses, or drops one a comment still uses. | Only the policy's namespace and namespaces still used by a comment. | group C (E6b) |
| S8 | A comment-only save (`edit == null`, or state Not Configured with a comment) writes the comment file only (D3, D10). | It also writes gpt.ini (version +1) and, when `Registry.pol` is missing, the empty document of 8 bytes (`PReg` and version 1): `A/Machine_CommentOnly`, `A/User_CommentOnly`, `A/Both_UnderComputer_CommentOnly`, `A/Both_UnderUser_CommentOnly`. | replay, `GptIniGoldenTests` |
| S9 | A class Both policy saved under one root touches the other side. | One side per call: `A/Both_UnderComputer_Enabled` leaves every `User` file absent, `A/Both_UnderUser_Enabled` every `Machine` file. | replay |
| S10 | Two saves in one session (Computer, then User) mix the sides or the versions. | `A/Machine_Then_User`, `A/User_Then_Machine`, `A/Both_Computer_Then_User`: gpt.ini `Version` 65537 after the second save, each side's files as in its step. | replay |
| S11 | The comment is compared with the text read before the lock instead of the text read under it. | The comment is read from the model under the lock; equal text is no change. | replay (`A/Machine_Apply_OK`, second press) |
| S12 | A policy whose key has a leading backslash throws, or writes a record under a trimmed key. | Classic saves it: `Registry.pol` stays the empty 8-byte document (no record), `comment.cmtx` gets the `using` line of the edge namespace, gpt.ini is `Version=1` with no extension line; Not Configured afterwards writes nothing. `K/KeyLeadingBackslash_Enable`. | replay |
| S13 | A trailing-backslash key is written with the backslash, or Not Configured leaves the record. | The record sits under the trimmed key; Not Configured clears it: `Registry.pol` back to the empty document, version 2, extension line stays with one space (D4). `K/KeyTrailingBackslash_Enable`. | replay |
| S14 | The cancellation token is checked after the first write. | Cancelled before the first write: `OperationCanceledException`, nothing written. | store tests |
| S15 | A malformed `Registry.pol` is overwritten, or a comment-only save skips reading it and bumps the version on top of it. | The save throws a malformed-file failure and writes nothing (D9). | `K/KeyLeadingBackslash_ReadAsWritten` (refused start), `PolicySaveContractTests.S15_...` |
| S16 | Read-only mode, lock timeout or a write failure are hidden in the outcome. | `GpoStoreException` reaches the caller unchanged. | `tests/Bwc.Gpo.Tests` (store tests) |
| S17 | `PolicyStateWriter.Apply` returns problems inside the store edit (the template changed between the dialog and the lock) and the edit returns with a model that is partly changed, or after the comment was set. | `PolicySave.Save` returns the problems and `Changed` false, the comment is not set, the store writes nothing and does not refresh (S2). The code throws a private `EditRefusedException` inside the edit, so `GpoStore.Save` discards the models and writes nothing, and the catch maps it to the problems outcome. | `PolicySaveContractTests.S17_...` |

Not decided and not tested here: a policy of class Machine saved with side User (the UI never offers it).

## G. File rules the A and K goldens settle (in `Bwc.Gpo`)

Tested alone by `GptIniGoldenTests` and `CmtxGoldenTests` in `tests/Bwc.Gpo.Tests/Golden`, each test named for its case.

| Id | Rule | Case |
|---|---|---|
| D2 | A missing gpt.ini key is appended at the end of the General section, in the order the sides are first saved. | `A/User_Enabled`: `Version` then `gPCUserExtensionNames`. |
| D3 | A comment-only save writes gpt.ini and raises the version. | `A/Machine_CommentOnly`. |
| D4 | When the last record of a side is cleared the extension line stays with one space as its value. | `K/KeyTrailingBackslash_Enable` step 2. |
| D5 | The first save writes `comment.cmtx` with one `using` line even with no comment. | `A/Machine_Enabled`. |
| D10 | A save that finds no `Registry.pol` writes the empty document even when the edit adds no record. | `A/Machine_CommentOnly`. |
| D11 | The extension pair is added only when `Registry.pol` holds a record: a save that leaves the file empty writes `Version=` and no extension line. | `A/Machine_CommentOnly`, `K/KeyLeadingBackslash_Enable`. Found by reading the goldens in this dispatch; the tests state it, the C dispatch settles it. |

## R. Replay harness

The harness copies the start files into a fresh sandbox under the OS temp folder, replays every structured `editPolicy`
operation of the case through `PolicySave.Save`, and compares the five files of every step byte for byte. It fails in all
these ways, and none of them is a skip.

| Id | Way it goes wrong | What the harness does |
|---|---|---|
| R1 | The case folder, `case.json`, `complete.json` or a step folder is missing, or the snapshots do not match the steps. | Fails naming the path. |
| R2 | A golden file differs from the size or SHA-256 in `case.json` (a wrong, stale or line-ending-converted golden). | Fails before any replay, naming the file. |
| R3 | A step folder holds a file that is none of the five names. | Fails. |
| R4 | A file that classic did not write exists in the sandbox, or a written one is absent. | Fails naming the file; an absent file must be absent. |
| R5 | The GPO folder holds anything but the five files after a save (a temp file, a log, a backup). | Fails listing the extras. |
| R6 | Classic refused a press (a message box, no change) and the engine saved or reported no problem. | Fails: problems expected, nothing written. |
| R7 | A step with no change writes, or a changing step writes other files than classic's `changedFiles`. | Fails comparing the written set with `changedFiles` of the press. |
| R8 | The template of an operation is not in the loaded set (a bad edge fixture). | Fails with the template load failures. |
| R9 | The step shows CAP-4: a set changed an option's text, the state did not change, Apply stayed disabled, no message box. The golden is then a write that a person would get and the capture did not. | Fails with the tool's wording (rule ported from `UnnoticedEdit.Find`). The positive example is a sandbox copy of `B/Pol_Decimal_MinMax` with its alternate-value set changed to the CAP-4 pattern; the ten recaptured cases (`B/Pol_Decimal_MinMax`, `E/Decimal_EdgeValues` and the others) are checked not to be flagged. |
| R10 | A case is on `awaiting-recapture.txt`. | Its row reports "awaiting recapture" with `Assert.Inconclusive`; it is never replayed. |
| R11 | The list names a case that has no folder, appears twice, or no longer shows CAP-4 (it was recaptured). | Fails: the list is wrong. |
| R12 | The list is not empty at a release. | `AwaitingRecaptureListIsEmpty` (`ReleaseGate`) fails while it holds any case. The quick gate leaves `ReleaseGate` out; the full gate runs it in the step `test-policy-release-gate` (quality-gate.md, GATE-5). |
| R13 | An Enabled operation leaves an element with no option value and no stored value: the dialog showed its default. | The value comes from `PolicyDefaults.For` (E6b, policy-defaults.md, F and B3). Until that function is written the row fails with `NotImplementedException`; the harness invents no default. |
| R14 | An option value cannot be mapped (unknown element id, enum item that is not a label, a number that is not text of digits while classic showed no box). | Fails naming the option. |
| R15 | A start that classic refused (`startRefused`) opens, saves or changes in the engine. | Fails: the store must report a failure on open, a save must throw a malformed-file failure, and the start files stay byte-identical. |
| R16 | The state the engine reads before an operation differs from the state the dialog showed (`before.state`). | Fails naming the operation (K read cases). |
| R17 | An operation kind the engine cannot replay is met. | Fails; only `snapshot`, `editPolicy`, `selectNode` and `readList` are accepted. |
| R18 | A case folder exists on disk that no row names. | `ReplayRowsCoverEveryCaseOfTheGroups` fails. |
| R19 | The sandbox is under a real policy folder, or a run folder is left behind. | The sandbox is a new GUID folder under the temp folder, built through `SandboxGpoStorePolicy`, removed in `Dispose`. |

## E6c. Groups D and E (written before the tests, dispatch T)

Piece E6c adds the replay rows of group D (113 crafted Registry.pol start states, each followed by one unrelated save) and
group E (30 validation cases), a state matrix over group D, and a store-level test of the refused starts. R1 to R19 above
apply to every row. The one group E case on `awaiting-recapture.txt`, `E/Decimal_Soft_OutOfRange`, stays Inconclusive (R10). The recaptured
group E cases `E/Decimal_EdgeValues`, `E/Decimal_NotNumber`, `E/Decimal_StoreAsText_OutOfRange` and `E/LongDecimal_AboveMax`
are replayed against their recaptured goldens. No
group D case has a crafted `gpt.ini`: every D start state is one or two Registry.pol files, and the version file of a step
is what classic wrote around them.

### M. State matrix (group D)

`PolicyStateMatrixTests` in `tests/Bwc.Policy.Tests/Golden`. Each test names the case it reads.

| Id | Way it fails | Expected | Case, test |
|---|---|---|---|
| M1 | The state the engine reads from a crafted file differs from `before.state` of the dialog (duplicates, delete records, wrong types, partial values, mismatched values, list shapes, foreign records, case differences). | Equal for every `editPolicy` operation of every D case that is not a refused start, read under the root the operation names. | every D case, `M1_EngineReadsTheStateTheDialogShowed` |
| M2 | A class Both policy is read from the wrong file: the machine file under User, the user file under Computer, or both. | Computer reads `Machine.Registry.pol`, User reads `User.Registry.pol`; a missing file is Not Configured. | `D/Both_MachineFileOnly`, `D/Both_UserFileOnly`, `D/Both_BothFiles`, `D/User_FlagInMachineFile` |
| M3 | A case lists no state to compare and the test passes empty. | At least 100 D cases and at least 150 operations with a `before.state` are compared; fewer fails. | `M3_...` |
| M4 | Reading changes the document or the folder. | A save with no edit and no comment change writes nothing for every D start (the dialog's Cancel and an OK with no change). | every D read operation, replay |
| M5 | The element values the dialog showed for an Enabled policy differ from what the engine reads: a check box state, a number (the box shows the stored number held inside the minimum and maximum), the selected enum item (none when no item matches). | Equal. An element the file has no record for shows its default (policy-defaults.md, F). | `D/Partial_*`, `D/Mismatch_*`, `D/WrongType_*`, `M5_...` |
| M6 | The one unrelated save changes records of other policies, reorders them or drops them. | Every file equals the step folder byte for byte (replay row of the case). Duplicates, a delete record between two values, an out-of-order key or value, two spellings of one key and a stray tail are normalized as classic did. | all D rows |
| M7 | A file classic opened is refused because of a stray tail (`D/File_TrailingBytes`: two stray bytes after the last record). | Classic opened it and the next save dropped the stray bytes; the engine reads the records before the tail and the save writes the file without them. | `D/File_TrailingBytes` |
| M8 | A file classic opened is refused for a size or name rule of ours (`D/File_LargeValueName`: a 300 character value name). | Opens and saves; the record is kept byte for byte. | `D/File_LargeValueName` |
| M9 | A file with the header and no record is refused. | An empty document: opens, saves, Not Configured. | `D/File_HeaderOnly` |

### X. Refused starts (D9)

Classic refused to open the GPO in six D cases (`startRefused` in `case.json`): `D/File_BadHeader`, `D/File_Empty`,
`D/File_MissingCloseBracket`, `D/File_OnlyGarbage`, `D/File_TruncatedLastRecord`, `D/File_VersionTwo`. Every one holds a
machine `Registry.pol` only. The replay row of each reuses the refused-start path (R15). The store-level test is
`PolMalformedStartGoldenTests` in `tests/Bwc.Gpo.Tests/Golden`.

| Id | Way it fails | Expected | Case, test |
|---|---|---|---|
| X1 | A zero-byte `Registry.pol` opens as an empty document and a save overwrites it (D9). | Open reports a failure for the side (`PolFailure`, no `Pol`); a save throws `GpoStoreException` with `GpoStoreFailure.Malformed`. | `D/File_Empty` |
| X2 | A file with a bad signature (`D/File_BadHeader`, `D/File_OnlyGarbage`), version 2 (`D/File_VersionTwo`), a record with no closing bracket (`D/File_MissingCloseBracket`) or a record cut in the middle (`D/File_TruncatedLastRecord`) opens. | Each is refused as X1. | the five cases |
| X3 | A refused start is changed: rewritten, emptied, trimmed, or given a version file, a comment file or a lock leftover. | The start file is byte-identical after the failed save; no other file of the five exists in the sandbox; the folder holds nothing else. (Classic itself wrote a version file with an empty General section at the refusal; the engine writes nothing.) | all six |
| X4 | A comment-only save or a save with no edit skips the read of the refused file and writes. | Any save that reads `Registry.pol` of the refused side is refused. | `PolMalformedStartGoldenTests`, `PolicySaveContractTests.S15_...` |
| X5 | The set of refused starts changes with a recapture and the test goes on passing. | The test lists the refused starts found in the `case.json` files of group D and fails when the set is not exactly the six names above. | `PolMalformedStartGoldenTests` |
| X6 | A policy edit reaches `PolicyStateWriter.Apply` on a refused start through `PolicySave`. | `PolicySave.Save` throws the store exception (not a problems outcome) and the files stay as they were. | the replay rows (R15) |

### F. Foreign records

A record that no loaded policy owns is invisible in the editor and kept untouched, in place, on every save (inventory row
BEH-014, user decision of 2026-11-22). The D cases that carry foreign records: `D/Foreign_KeyOutsideRoots`,
`D/Foreign_KeyUnderRoot`, `D/Foreign_OnlyUnknownOutside`, `D/Foreign_OnlyUnknownUnderPolicies`, `D/Foreign_TopValue`,
`D/Foreign_ValueInPolicyKey`, `D/Foreign_ValueOnly`, `D/Instruction_UnknownAlone`, `D/Del_OtherName`. The test finds
them from the engine (records that no policy of the fixture templates owns), so a foreign record in another case is
checked too.

| Id | Way it fails | Expected | Case, test |
|---|---|---|---|
| F1 | A foreign record is dropped by a save that touches another policy (the unrelated Enabled save, or its Not Configured save). | Every foreign record of the start file is in the file after each save, with the same raw bytes. | `PolicyStateMatrixTests.F1_...` |
| F2 | A foreign record moves relative to the other foreign records, or its key or value name changes spelling. | The foreign records appear in the order of the start file with the same spelling. | `F2_...` |
| F3 | A foreign `**` instruction (an unknown name, alone in an unknown key) is dropped or turned into a delete or a soft record. | Kept as is. | `D/Instruction_UnknownAlone` |
| F4 | The premise is wrong: classic dropped a record the test calls foreign. | The test first checks the classic file after every step against the same rule and fails with the case name when classic did not keep the records; the expectation follows the capture, not the engine. | every case with foreign records |
| F5 | No case is found and the test passes empty. | The nine cases above must all be among the cases found. | `F5_...` |
| F6 | A key spelled in another letter case than the template (`D/Case_KeyDiffers`, `D/Case_ValueNameDiffers`, `D/Case_KeyTwoSpellings`) is read as foreign, or an unrelated save rewrites it with the template spelling. | Classic reads it as the record of the policy (the state of the dialog) and an unrelated save leaves it as written. | the three cases, replay |

### V. Validation (group E)

Rows in `PolicyGoldenReplayDETests` replay each E case; `PolicyEditRefusalTests` asks the validator and `PolicySave`
directly for every press that classic refused. The table maps the cases to what classic did.

| Id | Way it fails | Expected | Case |
|---|---|---|---|
| V1 | A required element left empty (text, combo box, number with no digits, multi-text with no line) or an enum with no choice is saved. | A `Required` problem, `Result` null, nothing written. | `E/Combo_Required_Empty`, `E/Decimal_Required_Empty` step 2, `E/Text_Required_Empty` steps 1 and 2, `E/Enum_Required_Defaults`, `E/LongDecimal_Required_Empty` step 2, `E/Ok_DefaultsOfRequired` steps 3 to 6, `E/MultiText_Limits` step 3, `E/Next_WithInvalid` |
| V2 | A multi-text with more lines than `maxStrings` or a line longer than `maxLength` is saved. | `TooManyLines` and `TooLong` problems, nothing written. | `E/MultiText_Limits` steps 1 and 2 |
| V3 | A list with an empty entry, a duplicate entry (plain or explicit names), or an explicit entry with no name is saved. | `EntryEmpty`, `DuplicateEntry`, `DuplicateEntry` and `EntryNameMissing` problems, nothing written. | `E/List_Plain_EmptyEntry`, `E/List_Plain_DuplicateEntries`, `E/List_Explicit_DuplicateNames`, `E/List_Explicit_EmptyName` |
| V4 | A list entry classic accepted is refused: an explicit entry with a name and an empty value (also a one-cell row, which classic read as an empty value), a 300 character entry, quotes, commas and an ampersand. | Written as classic wrote it: the empty value is a record with an empty string. The validator must not refuse an empty explicit value. | `E/List_Explicit_EmptyValue`, `E/List_Explicit_NameOnly`, `E/List_Plain_LongEntry`, `E/List_Plain_SpecialCharacters` |
| V5 | A text set longer than its `maxLength` without typing (the driver sets the text; a person cannot, because the box cuts typed text at the limit) is refused, or written cut. | Classic wrote all 20 characters. The validator has no `TooLong` rule for a text any more (E6c); the box cuts typed text, which is the UI's job. | `E/Text_TooLong_SetText` |
| V6 | A text typed longer than `maxLength` is written whole. | The control cuts typed text at `maxLength` (rule C4); the engine writes 12 characters. | `E/Text_TooLong_Typed` |
| V7 | A text of only spaces counts as empty and is refused for a required text. | Written as typed: three spaces. | `E/Text_Required_Empty` step 3 |
| V8 | A number above the maximum or below the minimum is refused by the engine where classic wrote the limit. | Classic's box never refuses a number: it writes the limit (rule C2). The engine still refuses an out-of-range number it is handed. | `E/Decimal_AboveMax`, `E/Decimal_BelowMin`, `E/Decimal_Apply`, `E/Decimal_TypedAboveMax` |
| V9 | The value of a number box with no digits is written as 0 or as the minimum. | A box that is not required reads as its default (rule C3): 25 for the number with a range. | `E/Decimal_Empty` |
| V10 | Letters typed in a number box are written or refuse the save. | Typing letters adds nothing (rule C1): `12ab` is 12. | `E/Decimal_TypedLetters` |
| V11 | Apply writes nothing on an out-of-range number, or the Cancel after Apply writes again. | Apply writes the clamped value, the later Cancel changes nothing. | `E/Decimal_Apply` |
| V12 | Next Setting with an invalid value saves or moves on; Previous (disabled) is pressed. | Next shows the message and writes nothing; Previous is recorded as not pressed and skipped (B2). | `E/Next_WithInvalid`, `E/Previous_WithInvalid` |
| V13 | A refusal that classic showed with a message box is accepted by the engine because the harness never asked it. | The refusal of the dialog itself (a number with no digits on a required box) still goes to `PolicySave` with the element empty and must give `Required`. | `PolicyEditRefusalTests` |
| V14 | A refusal gives the wrong kind or the wrong element. | The kind and the element id match the table of the test. | `PolicyEditRefusalTests` |
| V15 | A step where classic did not notice the edit (CAP-4) is replayed as "no change". | Those cases are on `awaiting-recapture.txt` and stay Inconclusive (now only `E/Decimal_Soft_OutOfRange`). A case that shows the pattern and is not on the list fails (R9). | see R9 |
| V16 | A refusal leaves the lock held, the store dirty or a temp file. | The sandbox folder holds only the five files and no refresh happened. | every refusal row |

### C. Dialog control rules used by the E rows (test side)

Classic's controls change what a person types before the dialog saves. The rules are in
`tests/Bwc.Policy.Tests/Golden/DialogControlRules.cs`, applied to the E cases only, and every rule is checked by the byte
compare of the rows. Where these rules end up in BWC code (the G4 dialog) is decided there; the engine keeps its stricter
validator.

| Id | Rule | Capture |
|---|---|---|
| C1 | In a number box, typed characters that are not digits are not taken (typing). Set text is not filtered. | `E/Decimal_TypedLetters` |
| C2 | The number a box returns is held inside the minimum and maximum of the element (a decimal up to 4294967295, a long decimal up to 18446744073709551615 when the element states no maximum), for a soft element and one stored as text too. | `E/Decimal_TypedAboveMax`, `E/Decimal_AboveMax`, `E/Decimal_BelowMin`, `E/Decimal_Apply` |
| C2b | A number box given a minus sign and digits (WM_SETTEXT `-5` on a 10 to 500 box, `-1` on a 0 to 65535 box stored as text) writes the minimum. Letters set in the box keep the shown value (C3). | `E/Decimal_NotNumber` step 'negative' (10), `E/Decimal_StoreAsText_OutOfRange` step 'negative' (`"0"`) |
| C3 | A number box with no digits (empty, or letters set without typing) on an element that is not required reads as the default of the control. A required one is refused. | `E/Decimal_Empty`, `E/Decimal_Required_Empty` |
| C4 | A text box or combo box cuts typed text at `maxLength` (1023 when the element states none). Set text is not cut. | `E/Text_TooLong_Typed`, `E/Text_TooLong_SetText` |
| C5 | A row of an explicit list with only a name has an empty value. | `E/List_Explicit_NameOnly` |
| C6 | A rule is applied to a case outside group E. | None may be: the B and C rows keep passing without the rules. |
| C7 | A rule hides an engine difference (a wrong rule gives a wrong value that happens to match). | The rows compare every byte of every file; a rule that is not what classic did cannot give the golden. |

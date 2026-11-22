# Policy save and golden replay failure modes

Scope: piece E6a of release 0.2.0. `PolicySave.Save` in `src/Bwc.Policy/Model/PolicySave.cs` (the one save every store
uses), the replay harness in `tests/Bwc.Policy.Tests/Golden`, and the file-rule tests in `tests/Bwc.Gpo.Tests/Golden`.
This page was written before the code (dispatch T). The rules it names are settled by the captures in
`docs/parity/oracle/gpedit/goldens` (groups A and K for this piece); a capture is the specification. The reader and writer
rules are in `docs/failure-modes/policy-state.md`, the store in `docs/failure-modes/gpo-store.md`, the file codec in
`docs/failure-modes/gpo-pol.md`.

The contract of `PolicySave.Save(store, side, policy, edit, comment)`:

1. Validate with `PolicyEditValidator` first. A problem returns the problems and reads, locks and writes nothing.
2. One `GpoStore.Save` of the side. Inside it: `PolicyStateWriter.Apply` on the models read under the lock, then the
   comment, then `RestrictNamespacesTo(policy.Namespace)` when the state or the comment changed.
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
| R9 | The step shows CAP-4: a set changed an option's text, the state did not change, Apply stayed disabled, no message box. The golden is then a write that a person would get and the capture did not. | Fails with the tool's wording (rule ported from `UnnoticedEdit.Find`). |
| R10 | A case is on `awaiting-recapture.txt`. | Its row reports "awaiting recapture" with `Assert.Inconclusive`; it is never replayed. |
| R11 | The list names a case that has no folder, appears twice, or no longer shows CAP-4 (it was recaptured). | Fails: the list is wrong. |
| R12 | The list is not empty at a release. | `AwaitingRecaptureListIsEmpty` (`ReleaseGate`) fails while it holds any case. |
| R13 | An Enabled operation of a policy with elements gives no value for an element and the policy was not Enabled: the dialog showed defaults. | Fails with "needs the defaults function of E6b"; no default is invented here. |
| R14 | An option value cannot be mapped (unknown element id, enum item that is not a label, a number that is not text of digits while classic showed no box). | Fails naming the option. |
| R15 | A start that classic refused (`startRefused`) opens, saves or changes in the engine. | Fails: the store must report a failure on open, a save must throw a malformed-file failure, and the start files stay byte-identical. |
| R16 | The state the engine reads before an operation differs from the state the dialog showed (`before.state`). | Fails naming the operation (K read cases). |
| R17 | An operation kind the engine cannot replay is met. | Fails; only `snapshot`, `editPolicy`, `selectNode` and `readList` are accepted. |
| R18 | A case folder exists on disk that no row names. | `ReplayRowsCoverEveryCaseOfTheGroups` fails. |
| R19 | The sandbox is under a real policy folder, or a run folder is left behind. | The sandbox is a new GUID folder under the temp folder, built through `SandboxGpoStorePolicy`, removed in `Dispose`. |

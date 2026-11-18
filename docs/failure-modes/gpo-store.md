# GPO store failure modes

Scope: piece E3 of release 0.2.0: `src/Bwc.Gpo/Store`, `src/Bwc.Gpo/Comments`, `src/Bwc.Gpo/Refresh`. It covers every
way the store can damage the machine's real local group policy object (GPO) folder, or report a state that is not on
disk. This page was written before the code. The binding spec is `docs/contracts/policy-engine.md` (sections gpt.ini,
comment.cmtx, save sequence, write policy and E2E seams, read-only detection). The Registry.pol codec is
`docs/failure-modes/gpo-pol.md` and is not repeated here. The static guard is `docs/failure-modes/policy-writer.md`:
inside `src/Bwc.Gpo` the only file that may write is `Store/GpoFileWriter.cs`, and the only file that may name
`RefreshPolicyEx` is `Refresh/PolicyRefresh.cs`. Everything else here builds bytes in memory and hands them to the writer.

Items marked *verify against oracle* are assumptions until the classic capture (groups A, C, G) arrives. Each one lives in
one named place so the correction touches one spot:

| Choice | Place | Current value |
| --- | --- | --- |
| Line order of a new `gpt.ini`, and where a missing key is inserted into an existing one | `GptIniRules.NewFileLineOrder`, `GptIniRules.InsertionIndex` | `gPCMachineExtensionNames`, `Version`, `gPCUserExtensionNames` (the probe file's order). Only the saved side's extension line is added. |
| Extension pair add: group order and tool order inside a group | `GptIniRules.AddPair` | A new `[{CSE}{tool}]` group is inserted in ordinal GUID order; a tool GUID joins an existing group in ordinal order. Existing text is never reordered. |
| When a pair is removed | `GptIniRules.RemovesPairWhenSideEmpty` | Never (false). |
| Version at 16-bit overflow | `GptIniRules.Bump` | The half wraps to 0 and the other half is left alone (no carry). Not observable in practice (65536 saves). |
| Does a comment-only change bump the version | `GptIniRules.CommentOnlyChangeBumpsVersion` | False. A comment-only save writes `comment.cmtx` only. |
| Shape of a comment entry | `CmtxFormat.WriteComment` | `<comment policyRef="ns0:Name" commentText="$(resource.ns0_Name)"></comment>` under `admTemplate`, and `<string id="ns0_Name">text</string>` in `stringTable`. |
| The first-save rewrite of the namespace list | `CmtxDocument.RestrictNamespacesTo` | Offered as a method. The store does not call it. The policy layer calls it inside its edit once group C settles the rule. |
| Refresh call and flags | `PolicyRefresh.cs`, `UserenvPolicyRefresher` | `RefreshPolicyEx(machine, RP_FORCE)` after every save that wrote `Registry.pol`, for the saved side only (group G). |
| Zero-byte `Registry.pol` | `GpoStore` load | Read as an empty document (no policy yet); a bad header is a failure, never overwritten (matrix D). |
| Home detection | `GpoAccess.IsHomeEdition` | `EditionID` starts with `Core` (Core, CoreN, CoreSingleLanguage, CoreCountrySpecific). |

## Named files

| File | Role |
| --- | --- |
| `Store/GpoFileWriter.cs` | The only writer. Atomic temp-then-replace, attribute carry-over, backup, prune, line append for the recorder. Every public method asks the write policy first. |
| `Store/IGpoStorePolicy.cs`, `SystemGpoStorePolicy.cs`, `SandboxGpoStorePolicy.cs`, `GpoPathChecks.cs` | GPO root, backup root, lock name, refresher, and the path check every write passes. |
| `Store/GptIni.cs`, `GptIniRules.cs` | gpt.ini model. In-memory only. |
| `Store/GpoStore.cs` and its snapshot, session and result types | Open, and the save sequence under the named mutex. |
| `Store/GpoAccess.cs` | Read-only detection. Reads a DACL, writes nothing. |
| `Comments/CmtxDocument.cs`, `CmtxFormat.cs` | comment.cmtx model, parser and the classic serializer. In-memory only. |
| `Refresh/PolicyRefresh.cs` | `IPolicyRefresher`, the real one, and the recorder for the sandbox. |

## A. Writing one file

| ID | Case | Intended behavior |
| --- | --- | --- |
| A1 | The process dies or the machine loses power after the temp file is written and before the replace | The target is untouched (old bytes). A temp file named `<target>.bwc-<hex>.tmp` remains. The next write in that folder deletes every `*.bwc-*.tmp` first (it holds the lock, so no live writer owns one). |
| A2 | The temp file cannot be created (disk full, folder not writable) | Typed failure with path, operation `CreateTemp`, Win32 code. Nothing else changes. |
| A3 | The write or flush fails half way | The temp file is deleted (best effort), the target is untouched, typed failure `Io`. |
| A4 | The replace fails because gpsvc, classic MMC, or an antivirus holds the target open (sharing violation 32, lock violation 33) | Retry a bounded number of times (default 5, 50 ms apart), then typed failure `InUse` with the path. The temp file is deleted. The target is untouched. |
| A5 | The target has the read-only attribute | Refuse with `ReadOnlyTarget` before the replace. The attribute is never cleared. The temp file is deleted. A failure in step 3 of the save sequence stops the sequence, so `gpt.ini` is not bumped for a `Registry.pol` that was not written. |
| A6 | The target is hidden or system | Those attributes (and not-content-indexed) are copied to the temp file before the replace, so the new file has the same attributes. Archive is set by the replace as usual. |
| A7 | The target has an explicit ACL | `File.Replace` keeps the replaced file's DACL, so the new file gets the old file's ACL, not the folder default. A new file (no target yet) inherits from the folder. |
| A8 | Temp and target on different volumes | Cannot happen: the temp file is created next to the target, in the same folder. Replace is never a cross-volume copy. A reparse point that moves the folder to another volume is refused by C-group checks (G3). |
| A9 | The target does not exist yet | The temp file is moved into place with no overwrite. If a file appeared in between (another process), the move fails and is reported as `Io` with the path; nothing is replaced. |
| A10 | The folder (`Machine`, `User`, or the GPO root itself) does not exist | The writer creates it (through the write policy, inside the root only). A new folder inherits the parent ACL. Opening a store with these folders missing is normal (empty documents). |
| A11 | Empty content | Written as zero bytes if the caller asks. The store never asks for it: `Registry.pol` always has at least the header. |
| A12 | A path that is a directory, or a file path with an alternate data stream (`:name`) | Refused by the path check (`PolicyDenied`). |
| A13 | The caller cancels | A write already started finishes. The token is checked before the first write of a save and between loads, never between two files of the same save, because the contract wants each file complete or untouched and the sequence to run to the end once started. |
| A14 | Two threads in one process write the same path | The store serializes saves with the named mutex; the writer itself is not thread safe and documents that. The recorder append takes a lock. |

## B. Backup

| ID | Case | Intended behavior |
| --- | --- | --- |
| B1 | First save of a session | Under the lock, before the first write, the store copies every GPO file that exists (`Machine\Registry.pol`, `Machine\comment.cmtx`, `User\Registry.pol`, `User\comment.cmtx`, `gpt.ini`) into a new folder `yyyyMMdd-HHmmss-fff` (UTC) under the backup root. A session can save both sides, so all five files are copied, not only the side being saved. |
| B2 | A file that does not exist yet | An empty marker file `<relative path>.absent` is written in the backup folder, so a restore can tell "absent" from "forgotten". |
| B3 | Later saves in the same session | No new backup. A session is one `GpoStore` instance. A save that wrote nothing does not count as the first write and makes no backup. |
| B4 | Two backups in the same millisecond | The folder name gets `-1`, `-2`... so no folder is reused. |
| B5 | The backup copy fails (disk full, backup root not writable, source locked) | The save stops before any GPO file is written: typed failure `BackupFailed`. A partial backup folder is left (it is named, so it is visible) and counts toward the 10. The session is not marked as backed up, so the next save retries. |
| B6 | More than 10 backup folders | After a successful backup the writer deletes the oldest folders so that 10 remain. Only folders whose name matches the timestamp pattern are considered; foreign folders and files in the backup root are never touched. |
| B7 | Pruning fails (a file is open, access denied) | The save continues. The failure is reported in `GpoSaveResult.PruneFailure` (path and Win32 code) and never thrown. Backups are an aid; a prune failure must not block policy changes. |
| B8 | A backup folder that is a reparse point | Delete only the link (non-recursive), never its target. |
| B9 | Backup root missing | Created by the writer through the write policy. |
| B10 | Backup root inside the GPO root, or the reverse | The sandbox policy refuses a backup root inside the GPO root (the folder must hold only what classic writes). The system policy cannot overlap. |

## C. gpt.ini

| ID | Case | Intended behavior |
| --- | --- | --- |
| C1 | ANSI code page | The file is handled as single bytes (each byte one char, ISO-8859-1 mapping), so every byte of existing content survives whatever code page the machine uses. Everything BWC adds is ASCII (digits, GUIDs, key names), identical in every ANSI code page. |
| C2 | BOM | A UTF-8 BOM at the start is kept as a prefix and written back. A UTF-16 BOM is `GptIniFormatException`: a save is refused rather than writing ANSI into a UTF-16 file. |
| C3 | CRLF, LF, CR, no final terminator | Every existing line keeps its own terminator. A new line uses the file's first terminator (CRLF when the file has none). When the last line has no terminator and a line is appended, that line gets one first. A new file ends with CRLF like the probe file. |
| C4 | Version overflow past 65535 | The half wraps to 0 and the other half is unchanged. No carry into the other side (a carry would silently bump the other side's version). *verify against oracle*, not observable. |
| C5 | A user save | Bumps the high 16 bits (+65536). A machine save bumps the low 16 bits (+1). The other half never changes. |
| C6 | Version absent or section absent | Read as 0. On save the line is inserted by `GptIniRules.InsertionIndex`; a missing `[General]` is added at the top. |
| C7 | Version not a decimal uint (`Version=abc`, negative, over 4294967295, empty) | `GptIniFormatException` on save: the store never invents a value for a line it cannot read. Open still succeeds and reports the problem in the snapshot. |
| C8 | Two `Version` lines, two `[General]` sections | The first of each wins for reading and editing. The rest are untouched bytes. |
| C9 | Unknown lines and sections (`[Other]`, comments, blank lines, odd spacing) | Kept byte for byte, in place. Only the value text of an edited line changes (spaces around `=` are kept). |
| C10 | Extension pair for one side | The pair `{35378EAC-683F-11D2-A89A-00C04FBBCFA2}{D02B1F72-...}` (machine) or `{...F73-...}` (user) is added to `gPCMachineExtensionNames` or `gPCUserExtensionNames` when absent. Re-adding does nothing, so there are no duplicates. Compared case-insensitively on the GUID text. |
| C11 | The line has other extension groups (`[{AAAA...}{BBBB...}]`) | Kept as written. The registry group is found by its first GUID. Where a new group goes is *verify against oracle* (ordinal order). A tool GUID joins an existing registry group in ordinal order. |
| C12 | The extension line is not a list of bracket groups | `GptIniFormatException` on save. The line is never rewritten. |
| C13 | Pair removal | Never done. Whether classic removes the pair when a side's last setting is cleared is *verify against oracle*; leaving it is harmless (the CSE finds an empty `Registry.pol`). |
| C14 | Save with no policy change | `gpt.ini` bytes unchanged, so it is not written. The model reports `HasChanges` by byte comparison. |
| C15 | Machine and User halves of a policy of class Both | A Both policy is edited once per root. Each root is its own save, so each bumps its own half and adds its own pair. The store has no "both" save. |
| C16 | `gpt.ini` absent | Read as a new file. The first save creates it in the probe order. |
| C17 | A very large or binary file | A size cap (1 MiB) applies; over it is `GptIniFormatException`. NUL bytes are kept as ordinary bytes. |

## D. comment.cmtx

| ID | Case | Intended behavior |
| --- | --- | --- |
| D1 | The probe file shape | Serializing a parsed probe file gives the same bytes: UTF-8 BOM, `<?xml version='1.0' encoding='utf-8'?>`, CRLF, two-space indent, empty elements as open plus close tags, attributes in file order, no final newline (the real files on this machine end at `</policyComments>`). |
| D2 | Foreign content (elements, attributes, comments, processing instructions, other namespaces) | Kept in the tree and written back in the same place. Layout is normalized to the classic shape. |
| D3 | A comment on a policy with a namespace not yet listed | A `using` is added with the next free prefix `nsN` (N one past the highest existing number, so no existing reference changes). |
| D4 | Comment text | Stored in the string table as `<string id="nsN_Policy">text</string>`, referenced by the `comment` entry. Setting the same text changes nothing. Empty or null text removes the entry and the string. |
| D5 | Text with `<`, `&`, quotes, newlines, `>` | Escaped. `\r` is written as `&#xD;` so a CRLF in the text survives a parse. Characters that are not valid in XML 1.0 are refused with `ArgumentException` before the model changes. |
| D6 | A comment entry whose string is missing from the table | Read as no comment. Kept on disk until the entry is edited. |
| D7 | Two entries for the same policy | The first wins for reading and editing; the rest stay as foreign content. |
| D8 | An entry for a policy whose prefix is not declared | Kept as foreign content and never matched. |
| D9 | The file is not well formed XML, has a DTD, is not `policyComments`, or is over the size cap (4 MiB) | `CmtxFormatException`. DTD processing is prohibited and no resolver is set, so an entity can never read a file. The store treats the file as unreadable: a save that needs to change it is refused (`Malformed`); a save that does not need it leaves the file alone. |
| D10 | No BOM, or a UTF-16 file | Parsed by the XML decoder; written back as UTF-8 with BOM (the classic shape). A UTF-16 file is therefore converted on the first write. |
| D11 | Edit that is undone | `HasChanges` compares bytes with the baseline, so no write. |
| D12 | The first-save namespace-list rewrite seen in the probe | See the table above: `RestrictNamespacesTo(uri)` keeps the namespace of the edited policy and any namespace that a comment still uses. Not called by the store. |
| D13 | cmtx absent | Read as the empty classic document. The first comment or namespace change writes it. |

## E. Store: open and save

| ID | Case | Intended behavior |
| --- | --- | --- |
| E1 | Open reads without the lock | Open never creates the mutex, so a non-elevated viewer cannot fail on the mutex ACL. It returns a snapshot that may mix files during another save; the save re-reads under the lock, so no write ever uses a stale snapshot. |
| E2 | Any of the five files missing | Empty documents. Not an error. |
| E3 | `Registry.pol` with a bad header or truncated | The side's snapshot carries the `PolFormatException` and no document. A save on that side is refused (`Malformed`); the file is never overwritten. |
| E4 | The GPO folder missing, or only `Machine` or `User` missing | Open returns empty documents. Save creates folders. |
| E5 | The folder exists but cannot be read | Open reports the failure per file with path and Win32 code (typed), not a throw, so the caller can show read-only mode. |
| E6 | An outside edit between open and save | Save re-reads all files under the lock and applies the caller's edit to the fresh model, so the outside edit is kept. |
| E7 | An edit that changes nothing, or a net no-op | No backup, no write, no `gpt.ini` bump, no refresh. The result says `Wrote == false`. |
| E8 | The edit callback throws | Nothing is written. The exception propagates unchanged. The lock is released. |
| E9 | Write order | `comment.cmtx` (only when its bytes change), `Registry.pol` (only when its bytes change), `gpt.ini` (only after `Registry.pol` was written, or when a comment-only change is configured to bump), then refresh. |
| E10 | A write fails in the middle | The sequence stops with the typed exception. Files already written stay (as in classic). The backup holds the pre-session state. The result is not returned; the exception carries the path. |
| E11 | The store is read-only (access said so) | Every save throws `ReadOnlyMode` before the write policy runs and before any file is opened. |
| E12 | A caller edits the model it got from Open and hands it to Save | There is no such API. Save takes only an edit delegate, applied to the model read under the lock. |
| E13 | Save of the user side writes the machine half | Cannot happen: the edit session exposes one side, and the version bump and the pair follow that side only. Tested. |
| E14 | Dispose or reuse | The store holds no handle between calls. Each save opens and closes its own mutex handle. |

## F. Locking

| ID | Case | Intended behavior |
| --- | --- | --- |
| F1 | Another BWC instance is saving | This save waits on the named mutex, up to the lock timeout (default 15 s). |
| F2 | The lock times out | Typed failure `LockTimeout` (path is the lock name). Nothing was read or written. |
| F3 | The previous holder died holding the mutex (abandoned) | The wait succeeds, the state is re-read from disk, and the save goes on. A half-written state cannot exist because writes are atomic per file; a crash between files is case F4. |
| F4 | Crash between the three files of a save | Files written before the crash stay; the rest are as they were. On the next save the store re-reads whatever is on disk and applies the edit again, so `gpt.ini` ends up bumped by the new save. Documented, as classic. If `Registry.pol` was written and `gpt.ini` was not, gpsvc may not notice the change until the next bump. |
| F5 | Two stores in one thread | A Windows mutex is recursive for its owner thread. Saves never nest, so this is harmless. |
| F6 | The mutex exists with an ACL that denies this user | Typed failure `LockUnavailable` (not a hang). Only an elevated process saves, and the elevated token owns the mutex. |
| F7 | The sandbox | The lock name comes from the sandbox root (hash), so tests never collide with the real name or with each other, and use the `Local\` namespace so an unelevated test process works. |

## G. Write policy and sandbox

| ID | Case | Intended behavior |
| --- | --- | --- |
| G1 | Every write passes the policy first | Every public write method of `GpoFileWriter` calls `EnsureWriteAllowed(path)` before any file operation. A denial is `PolicyDenied` with the path. |
| G2 | Path outside the root and the backup root | `PolicyDenied`. The check works on the full normalized path, so `..`, `.`, doubled separators and a different case cannot leave the root. A path that only shares a prefix (`GroupPolicy2`) is outside. |
| G3 | A reparse point (junction, symlink) at or below the root, or in the chain to the target | The sandbox constructor refuses a root with a reparse point at the root or in any folder between the run data root and the root, and any reparse point found below the root. The write check refuses a target whose existing parent chain or the target itself is a reparse point. The system policy applies the same per-write check. |
| G4 | The sandbox root is the run data root, or outside it | The constructor throws `PolicyDenied`. Inside means strictly below, on a folder boundary. |
| G5 | Sandbox root with a short (8.3) name or a `\\?\` prefix | Compared as given after `GetFullPath`; a spelling that does not match the run data root is refused (safe side). |
| G6 | An environment variable steers the policy | None is read in `Bwc.Gpo`. The tool picks the policy. |
| G7 | The tool passes no policy | There is no default. A writer without a policy cannot be built. |
| G8 | Deny-all (E2E root unset) | Implemented by the tool as an `IGpoStorePolicy` whose `EnsureWriteAllowed` always throws. The writer's first action is that call, so no file operation precedes it. |
| G9 | The system policy points at the wrong folder | `SystemGpoStorePolicy` builds `<SystemDirectory>\GroupPolicy` and `<CommonApplicationData>\BetterWindowsConfig\gpedit\backup` and nothing else. Tests assert only the shape of the strings; they never write there. |
| G10 | A test touches the real folder | No test writes there. One invariant test reads it (inconclusive when absent): it loads `gpt.ini` and both `comment.cmtx` files and requires byte-identical serialization. Tests never call `UserenvPolicyRefresher`. |

## H. Refresh

| ID | Case | Intended behavior |
| --- | --- | --- |
| H1 | `RefreshPolicyEx` returns false | The save has already succeeded on disk. The result carries `RefreshFailure` (Win32 code, side) and the save is not rolled back. The policy is applied at the next background refresh. |
| H2 | The call blocks | It cannot be cancelled once started. It runs inside the save, after the lock is released, so another BWC instance is not held up. |
| H3 | Home edition (no `userenv` policy engine for local GPO) | The tool is read-only there, so no save and no refresh occur. If a refresh were called anyway the failure is H1. |
| H4 | User side refresh from an elevated process | `RefreshPolicyEx(false, ...)` refreshes the calling user's policy. That is the elevated admin's token, which for a split-token admin is the same user. Documented, as classic. |
| H5 | Wrong flag | `RP_FORCE` (1) forces a refresh even if the version did not change (BWC bumps it anyway). Which flags classic uses is group G, *verify against oracle*. |
| H6 | A refresh when no policy value changed | Not called: a comment-only save, or a save with no change. |
| H7 | The recorder | `RecordingPolicyRefresher` appends one line per call (`Machine` or `User`) to a file in the sandbox root through `GpoFileWriter` (policy checked), and calls nothing native. |
| H8 | Refresh named elsewhere | Only `PolicyRefresh.cs` names the API; the guard enforces it. |

## I. Read-only detection

| ID | Case | Intended behavior |
| --- | --- | --- |
| I1 | Not elevated | `NotElevated`. Uses the token's membership in Administrators (UAC filtered tokens report false). |
| I2 | No write access | `NoWriteAccess`: an `AccessCheck` of the process token against the folder's security descriptor for `FILE_ADD_FILE` (and `FILE_ADD_SUBDIRECTORY` on the nearest existing ancestor when the folder is missing). The security descriptor is read; nothing is created, opened for write, or locked. |
| I3 | Home edition | `HomeEdition`. |
| I4 | More than one applies | All reasons are reported, in the fixed order not elevated, no write access, Home. |
| I5 | False positive: the check says no, a write would work | Possible with an unusual DACL or a privilege (backup, restore) the check ignores. Read-only is the safe side; the user can fix the folder ACL. |
| I6 | False negative: the check says yes, the write fails | Possible with a read-only attribute on a file, a file ACL that denies write, or a sharing violation. The write fails with a typed exception (A4, A5); the access check is a prediction, the write is the authority. |
| I7 | The folder is missing | The nearest existing ancestor is checked. |
| I8 | The token cannot be duplicated or the descriptor cannot be read | Treated as no write access, with no exception. |
| I9 | The test process is elevated | The `GpoAccessTests` case that expects `NotElevated` is inconclusive. |

## J. Tests

Everything runs in a temp sandbox under the test's own temp folder, through `SandboxGpoStorePolicy`. No test writes the
real GroupPolicy folder, no test calls the real refresher, and one invariant test reads the real files only. A test file
that holds a policy path literal (the cmtx namespace URL counts) holds no write API, and the sandbox helper that deletes
folders holds no literal, so `scripts/check-policy-writer.js` passes without an allowlist entry.

| Test class | Covers |
| --- | --- |
| `GptIniTests` | Probe file byte for byte; versions; bump halves; wrap; unknown content; pair add and duplicates; BOM; line endings; malformed. |
| `CmtxTests` | Probe file byte for byte; add, edit, remove; namespaces; foreign content; escaping; hostile input. |
| `GpoSaveTests` | Write order, no write on no change, re-read under lock, atomic replace and temp sweep, backup once and pruned to 10, user vs machine half, read-only mode, failure paths. |
| `GpoStoreOpenTests` | Missing files, malformed files, snapshot. |
| `SandboxGpoStorePolicyTests` | Refusals: outside, `..`, prefix sibling, reparse point, backup inside root, run data root itself. |
| `GpoAccessTests` | Reasons, aggregation, the real access check against a temp folder and a system folder. |
| `PolicyRefreshTests` | Recorder lines. |
| `GpoStoreRealFolderTests` | Read-only invariant over the real folder. |

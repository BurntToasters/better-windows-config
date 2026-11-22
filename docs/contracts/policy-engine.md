# Contract: policy engine (`Bwc.Gpo` and `Bwc.Policy`)

This contract fixes the behaviors that more than one work package depends on. Packages E1 to E5,
G1 to G6, G0 and A2 must follow it. A change to this file is a manager decision.

The governing rule: **behave like classic Local Group Policy Editor (`gpedit.msc`), byte for
byte on disk.** Where this contract and classic disagree, classic wins and this file is
corrected. Where classic's behavior is not captured, the rule is a row of the Gaps section of
`docs/parity/gpedit-audit.md` and the owning package records the observed behavior as a golden before changing it. Goldens are
captured from classic by the oracle (`docs/parity/oracle/gpedit/`) using BWC-authored fixture
templates only.

Two projects carry the engine:

- `Bwc.Gpo` is the only writer of the local group policy object (GPO) folder. It knows the file
  formats (`Registry.pol`, `gpt.ini`, `comment.cmtx`) and the save sequence. It knows nothing about
  ADMX templates. Local Security Policy reuses it in 0.3.0.
- `Bwc.Policy` reads ADMX and ADML templates, links them into a category and policy tree, and
  reads and writes policy state through `Bwc.Gpo`. It references `Bwc.Gpo` and never touches the
  GPO folder itself.

## Layering and threading

- Neither project has a reference to WinUI, `Bwc.UI`, or any dispatcher. Both are usable from a
  console process and from tests.
- All engine calls are synchronous and may run on any thread. The UI calls them on the thread pool
  and marshals results back with `DispatcherQueue`. The engine never blocks waiting on the UI.
- Every operation that loops over files, records, categories, or policies (template load, tree
  link, filter, import, backup pruning) takes a `CancellationToken` and checks it at least once per
  file or per record batch. Cancellation throws `OperationCanceledException` and leaves no partial
  write in progress: a save is either complete for a given file or leaves that file untouched.
- Results are snapshots. The engine does not watch for changes. The view refreshes explicitly.

## Errors

- Every failure that comes from the file system or the operating system becomes a typed exception
  that carries the full path involved, the operation, and the Win32 error code. Template problems
  carry the file path and, when known, the line and position. Display text is chosen by the UI; the
  engine never formats user-facing text.
- A template that fails to load is a normal outcome, not a crash: the load result lists the
  failures and the remaining templates still load. Classic shows a load error for the same case.
- Read access denied on the GPO folder is a normal outcome and puts the editor in read-only mode
  (see Read-only detection). It is not an exception on open.

## `Bwc.Gpo`: Registry.pol format

A `Registry.pol` file is a header followed by zero or more records. Everything is little-endian.

- Header: eight bytes `50 52 65 67 01 00 00 00` (the signature `PReg` and version 1).
- Record: the UTF-16LE characters `[`, key, NUL, `;`, value name, NUL, `;`, then a 32-bit type,
  then `;`, then a 32-bit size, then `;`, then `size` bytes of data, then `]`. Strings are UTF-16LE
  and each is terminated by one UTF-16 NUL (two zero bytes) before the next `;`. The type and the
  size are raw four-byte values, not text. The delimiters `[`, `;`, and `]` are UTF-16LE characters
  (two bytes each).
- Type is the registry type number (for example 1 for `REG_SZ`, 2 for `REG_EXPAND_SZ`, 4 for
  `REG_DWORD`, 7 for `REG_MULTI_SZ`, 11 for `REG_QWORD`). `size` is the byte length of the data,
  including string terminators.
- A delete record has the value name `**del.<name>`, type `REG_SZ` (1), size 4, and data
  `20 00 00 00`. It removes the named value when the policy is applied.
- Unknown value names that start with `**` are instructions. The engine keeps every record it
  does not model, including unknown instructions, byte for byte, in its original position. Classic keeps
  every instruction in place and shows none (`D/Instruction_KeyLevel`, `D/Instruction_Unknown`, `D/Instruction_UnknownAlone`).
- A file that is empty or has a bad header is reported to the caller as a typed failure with a
  reason. Classic refuses a zero-byte file, a bad header, a cut record and a missing
  closing bracket (`D/File_*`); one stray character after the last record is ignored.

### Ordered model

`Bwc.Gpo` models the file the way classic does, so saved bytes match classic's bytes.

- Load replays the records in file order. Each record sets or deletes a value in the model.
- Setting a value to different data removes its record and appends a new one at the end of its key
  (probe, 2026-11-18: Zeta then Alpha enabled, then Zeta disabled, gave Alpha then Zeta). Setting a value
  to the data it already has changes nothing.
- Deleting a value and then creating it again appends it at the end of its key.
- Disabled on a policy with a value name and no `disabledValue` removes the value record and appends
  `**del.<valueName>` at the end of the key (probe, 2026-11-18).
- Not Configured removes the value record and writes no delete instruction (probe, 2026-11-18).
- Rules settled by groups B and C (E6b), each in `docs/failure-modes/policy-state.md` with its case: D6 (a `resources`
  element gets `minRequiredRevision="1.0"`, `C/Cmtx_FirstSave_*`), D7 (a lone `**del.` or the DWORD 1 of a disabledValue-only
  policy reads Not Configured, `B/Pol_Value_EnabledOnly`, `B/Pol_Value_DisabledOnly`), D8 (an empty list and an unselected
  enum are refused, `B/Pol_List_Plain`, `C/SharedList_EmptyList`), a version half at 65535 goes to 1
  (`C/Version_FromHighMachine`), records of one key keep their order when a value changes in place
  (`C/Booleans_ReverseOrder`), a CR in a comment is written raw (`C/Comment_MultiLine`), the registry pair leaves the
  extension line and foreign groups stay (`C/GptIni_PairRemoval`), and `B/Pol_Value_BothLists` keeps the enabled items on
  Disabled.
- An OK or Apply that changes nothing writes no file and does not bump `gpt.ini` (probe, 2026-11-18).
- Save writes keys in registry-sorted order and, inside a key, values in creation order.
- Records the model does not understand, and unknown `**` instructions, are kept and written back
  byte for byte.
- Read then write of a file with no edit produces the same bytes whenever the input is already in
  the order classic writes. What classic normalizes in a file that is not in that order: keys go in tree order
  (`D/Order_KeysDescending`, `D/Order_ParentAfterChild`), a duplicate name keeps its first place with its last data
  (`D/Dup_DifferentLastWins`), a key spelled two ways takes the first spelling (`D/Case_KeyTwoSpellings`) and one stray
  character after the last record is dropped (`D/File_TrailingBytes`).

The questions that were open are closed by captures (`docs/parity/gpedit-inventory.md`, section 4, OQ-01 to OQ-09):

- Record order inside a key: a changed value moves to the end of its key (`C/TwoInKey_ChangeFirstAgain`); keys are in tree order.
- A false boolean without `falseValue` is a `**del.<name>` record in place of the value (`B/Pol_Bool_NoFalseValue` step 3).
- The state of a policy from a partial or foreign file: `docs/failure-modes/policy-state.md`, section 1 (`D/*`). A plain flag
  reads its data as a number, a lone `**del.` reads Disabled only when the policy has no enabled or disabled value or list.
- Not Configured on a shared key removes every value and the delete-values record of the key (`C/SharedList_DisableAndShrink`).
- A first save creates the empty 8-byte `Registry.pol` (`A/Machine_CommentOnly`, `K/KeyLeadingBackslash_Enable`), and a
  side that ends up with no records keeps the 8-byte file (`B/Pol_List_Additive` step 6, `C/Clear_LastSetting_Machine`).
- List values: explicit names and values as given, `valuePrefix` names are the prefix and a number from 1, the others are named
  like their data (`B/Pol_List_*`).
- A comment-only change bumps the version (`A/Machine_CommentOnly`, `C/Comment_OnlyAfterSave`); the registry pair leaves the
  extension list of a side with no setting left and foreign pairs stay (`C/GptIni_PairRemoval`, `C/GptIni_ForeignExtensions`).
- Instructions are kept in place and not shown (`D/Instruction_*`), and there is no Extra Registry Settings node (BEH-014).

The rules that no capture reaches are the rows GAP-01 to GAP-06 of the Gaps section of `docs/parity/gpedit-audit.md`.

## `Bwc.Gpo`: gpt.ini

- Encoding is the system ANSI code page, line endings are CRLF.
- `Version` is one 32-bit number. The user version is in the high 16 bits and the machine version
  is in the low 16 bits. A machine save bumps the low half and a user save bumps the high half.
  Which saves bump the number is the save sequence below; a comment-only save bumps it too
  (`A/Machine_CommentOnly`).
- The extension lists name pairs of GUIDs. The pair is the client-side extension for registry
  policy `{35378EAC-683F-11D2-A89A-00C04FBBCFA2}` followed by the snap-in that edited the
  policy: `{D02B1F72-3407-48AE-BA88-E8213C6761F1}` for Computer Configuration (machine) and
  `{D02B1F73-3407-48AE-BA88-E8213C6761F1}` for User Configuration (user). Existing entries for
  other extensions are preserved. The exact line text and ordering are fixed by `GptIniGoldenTests`
  (captures `A/User_Enabled`, `A/Machine_Then_User`, `A/User_Then_Machine`): a missing line goes at the
  end of the General section. When a side holds no setting the line stays with one space as its
  value (`K/KeyTrailingBackslash_Enable` step 2), and a missing line is not added when `Registry.pol`
  holds no record (`A/Machine_CommentOnly`). Foreign extension groups stay (`C/GptIni_ForeignExtensions`).

## `Bwc.Gpo`: comment.cmtx

- Encoding is UTF-8 with a byte order mark.
- The root element is `policyComments` in the namespace
  `http://www.microsoft.com/GroupPolicy/CommentDefinitions`.
- Children, in order: `policyNamespaces` containing `using` elements, `comments` containing
  `admTemplate` elements, and `resources` containing a `stringTable`.
- The engine keeps parts of the file it does not model, so a comment edited in BWC does not lose
  anything another tool put there. The exact serialization (attribute order, indentation,
  `minRequiredRevision` and `revision` values) is fixed by the `CmtxGoldenTests` golden.

## `Bwc.Gpo`: save sequence

Each OK or Apply of a setting in the editor runs one save:

1. Read the current files from disk under the lock (see below). Apply the edit to the model that
   was just read, not to a model loaded earlier. An outside edit made between open and save is
   therefore kept.
2. Write `comment.cmtx`.
3. Write `Registry.pol`.
4. Write `gpt.ini` with the version bump and the extension pair.
5. Refresh policy with `RefreshPolicyEx` (see E2E seams for the test replacement).

Rules for the whole sequence:

- Every file write is atomic: write a temp file next to the target, flush it, then replace the
  target. A failed write leaves the old file in place and removes the temp file.
- Read, modify, write runs under one lock for the three files, held across steps 1 to 4. The lock
  is a file, `write.lock`, in the protected backup root (`IGpoStorePolicy.LockPath`), opened with no
  sharing for the whole save (`src/Bwc.Gpo/Store/WriteLock.cs`). It is not a named mutex, which any
  user could take first (GPO-2), and not a file in the GPO folder, so the folder holds only what
  classic writes. Another BWC instance waits. A lock that cannot be taken in a bounded time is a
  typed failure (`GpoStoreFailure.LockTimeout`).
- Every byte written to the GPO folder or the backup folder goes through one file:
  `src/Bwc.Gpo/Store/GpoFileWriter.cs` (atomic temp-then-replace, backup copies). Every refresh call
  goes through `src/Bwc.Gpo/Refresh/PolicyRefresh.cs`. `scripts/check-policy-writer.js` enforces both.
- Before the first write of a session, the engine copies the three files to
  `%ProgramData%\BetterWindowsConfig\gpedit\backup` in a new timestamped folder. A file that does
  not exist yet is recorded as absent. The engine keeps the last 10 backup folders and removes
  older ones. The backup is not repeated for later saves in the same session.
- A failure in step 2, 3, or 4 stops the sequence and is reported with the path and Win32 code.
  Files already written stay written, as in classic; the backup holds the pre-session state.
- Classic applies the policy to the live registry right after a save: the probe (2026-11-18) saw the
  new `HKLM` values within 4 seconds of OK. BWC therefore refreshes after every save. The exact call
  and its flags are part of oracle group G.
- On the first save of a session, classic rewrote `Machine\comment.cmtx` even with no comment change:
  its `policyNamespaces` list changed to hold only the namespace of the edited policy. Later saves in
  the same session left it alone. For a first save with no comment the list holds one `using` line
  (`A/Machine_Enabled`, `A/User_Enabled`); `PolicySave.Save` calls `RestrictNamespacesTo` when a save
  changed something. Which namespaces stay beyond that is not captured beyond the one-namespace case (gap GAP-07 of the audit).

## `Bwc.Gpo`: write policy and E2E seams

- Every write to the GPO folder passes a write policy first, as `RegistryWriter` does in the
  registry engine. The policy is a small interface in `Bwc.Gpo` (it names the GPO root, the backup
  root, the lock name and the refresher). `Bwc.Gpo` has two implementations: the system one and a
  sandbox one that takes a root and a run data root and refuses a root outside the run data root or
  with a reparse point. Both compile in every flavor and neither reads an environment variable. The
  tool's `GpeditStorePolicy` picks one: always the system one in the ship flavor; under
  `#if BWC_E2E` the sandbox one from `BWC_E2E_GPO_ROOT`, or deny-all when it is unset.
- The shipping policy uses `%SystemRoot%\System32\GroupPolicy`.
- Under `#if BWC_E2E`, the E2E flavor never uses the real folder:
  - `BWC_E2E_GPO_ROOT` names a sandbox folder. It must be inside the E2E run data root. A path
    that is outside the run data root, or that has a reparse point in it or below it, is refused.
  - When `BWC_E2E_GPO_ROOT` is unset, the policy is deny-all and every write throws before any file
    operation.
  - `BWC_E2E_ADMX_ROOT` replaces the system template folder with fixture templates.
  - `BWC_E2E_IMPORT_STORE` replaces the import store with a folder inside the run data root.
  - A refresh recorder file replaces `RefreshPolicyEx`: the E2E flavor appends one line per
    refresh request to a file inside the sandbox and calls nothing native. Tests read that file.
- All seams are compiled only under `#if BWC_E2E`. The ship flavor has no environment variable
  that redirects a write.
- `scripts/check-policy-writer.js` allows file writes to GroupPolicy or PolicyDefinitions paths and
  `RefreshPolicy*` P/Invokes only in named `Bwc.Gpo` files.

## `Bwc.Gpo`: read-only detection

The editor opens read-only, with a notice, when any of these is true:

- The process is not elevated.
- The process has no write access to the GPO folder. The check is an access check on the folder,
  not a write attempt, so detection never changes disk state.
- The Windows edition is Home.

Read-only mode reads templates and the existing `Registry.pol` and refuses every save before the
write policy runs. Classic refuses to open in these cases; opening read-only is a declared
difference. Writing on Home is not part of 0.2.0.

## `Bwc.Policy`: ADMX and ADML parsing

- The parser never validates against the ADMX or ADML XSD. Shipped Microsoft files break the
  schema (`dropdownList@oSort`, `checkBox@defaultItem`, `listBox@required`), and classic loads
  them. The parser reads the attributes and elements it needs and ignores the rest.
- Hardening, because the app runs elevated and the files are untrusted input:
  - DTD processing is prohibited and no `XmlResolver` is set.
  - A file larger than the size cap is refused before it is parsed. The cap is set in one constant
    and tested.
  - A template file or folder that is a reparse point is refused.
  - Name lengths are checked: a template file name, a namespace, a policy name, and a registry key
    or value name longer than the Windows limits are refused with a typed failure.
- Policy class is Machine, User, or Both. A policy of class Both appears under both roots and
  writes to the matching hive file of each.
- A policy's registry key and value name come from the policy. An element's own `key` overrides the
  policy key for that element.
- `enabledValue` and `disabledValue`, and `enabledList` and `disabledList`, describe the extra
  values a policy writes when it is Enabled or Disabled. A policy with no value name writes only its
  elements.
- Element kinds:
  - `boolean` (a check box, with `trueValue` and `falseValue` or lists of them)
  - `decimal` (a number, with `minValue`, `maxValue`, `soft`, and `storeAsText`)
  - `longDecimal` (a 64-bit number)
  - `text` (a string, with `expandable`, `required`, `maxLength`)
  - `multiText` (a multi-string)
  - `enum` (a drop-down; each item has a value and may have its own value list)
  - `list` (a table of values; `additive`, `explicitValue`, `valuePrefix`, and `expandable`
    change how entries are named and stored)
- Presentation controls come from the ADML `presentationTable` and include `checkBox`,
  `comboBox`, `decimalTextBox`, `longDecimalTextBox`, `dropdownList`, `listBox`, `textBox`,
  `multiTextBox`, and `text`. `comboBox` has no use on the reference machine, so its behavior is
  covered by the fixture templates only.
- `storeAsText` on a decimal writes the number as `REG_SZ` text. `expandable` writes
  `REG_EXPAND_SZ`.
- The parser and linker return a snapshot of the loaded set: namespaces, categories, policies,
  supported-on definitions, and the list of load failures.

## `Bwc.Policy`: template sources

- Two sources feed the set:
  - The system folder `C:\Windows\PolicyDefinitions`, with ADML under the language subfolder that
    matches the UI language and `en-US` as the fallback.
  - The BWC import store `%ProgramData%\BetterWindowsConfig\PolicyDefinitions`. Imported templates
    are copied there with the same layout. The store folder has an administrators-only ACL, built
    with the same code the updater uses (`src/Bwc.Update/Helper/ProtectedAcl.cs`, linked by source).
- When both sources define the same namespace, the system source wins and the import is listed
  as shadowed.
- Under `#if BWC_E2E`, `BWC_E2E_ADMX_ROOT` replaces the system folder and `BWC_E2E_IMPORT_STORE`
  replaces the import store.
- Classic `.adm` files are not supported.
- Tests over the real system folder assert invariants only (for example that load succeeds and
  stays under the load-time limit), because the template set differs between machines.

## Failure modes and goldens

- Each piece writes its failure-mode list first, in `docs/failure-modes/`, then the code and its
  tests. No unit test is added after the code (AGENTS.md).
- Tracked goldens hold only BWC-authored fixture output. Microsoft text, dumps, and screenshots
  stay untracked.

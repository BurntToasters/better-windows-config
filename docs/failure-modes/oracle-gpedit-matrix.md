# Oracle gpedit matrix runner failure modes

The matrix runner (`tools/Bwc.Oracle.Capture/Gpedit/Matrix`) is the one big capture of 0.2.0: groups A to H and K of the oracle plan, driven
through the driver (`oracle-gpedit-driver.md`) inside `GpeditSession` (`oracle-gpedit-session.md`). Its tracked output is the golden set
under `docs/parity/oracle/gpedit/goldens/` that the engine packages E3 and E4 and the replay tests G4 compare against byte for byte. This
document lists every way the run can produce a wrong golden, lose work, or leave the machine changed, and what the code does about each.
It was written before the matrix code. Sections of the session and driver documents still hold and are not repeated.

Commands: `gpedit matrix [--only A,B,...] [--case <id>] [--resume] [--dry-run] [--list]`. `--list` and `--dry-run` need no elevation, start
nothing and write nothing. A real run needs an elevated terminal on the VM desktop and a VM snapshot taken first.

Vocabulary: a case is one named scenario with its own start state, one classic start, a list of driver operations and one golden folder.
A group (A to H, K) is a list of cases. A step is one recorded point inside a case (a snapshot of the five GPO files after an operation).

## 1. The machine's real policy leaks into a golden

The VM has its own local policy (`Machine\Registry.pol`, a `gpt.ini` with other extension pairs, comments). If a case started from it, the
golden would hold bytes that only this VM can produce.

- Before every case the runner empties the session's `GroupPolicy` folder (every child deleted, the folder and its ACL kept, reparse
  points refused) and then writes only the case's declared start files. No case starts from the machine's files.
- The folder is read back after the reset and compared with the declared start state (names and sha256); a difference fails the case
  before classic starts.
- The real files are safe: the session backed them up before the first case and restores them after the last one, whatever happens.
- The system `PolicyDefinitions` templates are not part of any golden. Counts and names that depend on them (the All Settings node, the
  Administrative Templates children, tree children outside BWC Oracle) are written to the untracked log only (section 12).
- Tracked files contain no machine name, user name or SID (the report writer redacts them, and `case.json` holds none).

## 2. Nondeterministic bytes in a golden

Timestamps, version counters carried from the real `gpt.ini`, and a GUID or time that classic might write would make two runs differ.

- `gpt.ini` goldens start from a known version: every case records its declared start state (`startState` in `case.json`, and the start
  files in `<case>/start/`). A case that needs a non-zero version crafts the `gpt.ini` it starts from.
- Only file bytes, not file times, are stored. `case.json` carries no clock value, no pid, no window handle, no duration; timings are not
  stored at all (the untracked run log has them).
- Each golden is written from a temp folder. When a golden already exists, the runner compares the new bytes with it and prints
  `CHANGED` (and counts it in the summary) if any file differs, so a nondeterministic byte shows up on the second run. The README of the
  goldens says to run the capture twice and compare with `git diff`.
- A file that cannot be read stably (locked) is not recorded; the settle step of the driver waits for it (driver section 14).

## 3. Case order dependence

Cases must not depend on which case ran before.

- Every case resets files, MMC state and registry first (sections 1, 5, 6) and starts its own classic. Nothing is carried in memory
  between cases except the cancellation token.
- Cases are sorted by id inside a group. `--case` and `--only` run any subset in any order and give the same goldens as a full run,
  which the reset makes true.
- Steps inside a case are an intended sequence (Enabled, then alternate values, then Disabled, then Not Configured). The case id and the
  step labels say so, and the goldens of a step are the state after that step from the declared start.

## 4. A failed case poisons the next case

A hung dialog, a stuck menu, an mmc that died, or a half written file.

- After every case (ok or failed) the driver closes mmc (`Close`: dialogs answered first, then `WM_CLOSE`, a 15 s wait). The session
  restore kills the recorded mmc if it stays.
- The next case starts only after the reset (sections 1, 5, 6) and a check that no mmc process the session started is alive. A stuck mmc
  that cannot be closed ends the group; the remaining cases of that group are listed as not run and the session restore repairs the machine.
- The next group runs in a new session, so a poisoned group costs at most that group.
- A case is complete only when every operation is `ok` and none was skipped. A failed case writes no golden and no `complete.json`. Its
  failure is written to the untracked log folder. The run goes on with the next case and exits with code 1 at the end.

## 5. MMC console state carries over (filter, sort, tree width, last node)

MMC saves per-user console state on exit and applies it on the next start, so a filter or sort from an earlier case would change what a
later case sees.

- Before every case the runner deletes `%APPDATA%\Microsoft\MMC\gpedit` (file or folder, never a link). The session restore puts the
  original state back after the last case.
- Cases that change filter or sort end with `clearFilter` and verify the status text no longer says Filtered. The reset after the case is
  the safety net, not the plan.
- Classic opens at its defaults for every case, which is also the state the goldens describe.

## 6. Live registry leftovers between cases

Classic applies a saved policy to the live registry (group G). A leftover value would be there when the next case starts, and group G
reads exactly those keys.

- Before every case the runner deletes the four BWC-Oracle keys through `GpeditRegistry.Delete`, which refuses any other key, and then
  reads them back. A key that survives fails the case before classic starts.
- Group G cases read the registry before the first operation (`liveRegistryAtStart` in the result) and the case fails if it is not empty.
- The Group Policy service may still apply a refresh that started before the reset. The reset therefore waits 1500 ms, deletes, waits 500
  ms and checks again; a value that appears later is seen in the result of the first operation.
- The machine's own values elsewhere in the registry are never read or written by the matrix.
- Side effect that cannot be avoided: an emptied or changed local GPO is applied to the VM by the next refresh. The VM is resettable and
  the session restores the files, but it does not run a refresh. After the run, revert the snapshot or run `gpupdate /force`.

## 7. Refresh timing

Classic applies the policy a few seconds after OK (probe: within 4 s). A read taken at once sees nothing; a file read too late may see the
next step.

- File bytes are read from the driver's settle (stable for 500 ms, driver section 14) and then once more by an explicit `snapshot`
  operation, which is a separate read after the settle. A step is recorded from the snapshot, not from the press result.
- Group G reads the registry after OK, Apply and Not Configured with the registry settle of the driver (wait up to 6 s for a change, then
  stable 500 ms) and then waits a further fixed 4000 ms and reads again (`late` entry), so a late apply is not missed. A change that
  arrives only in the late read is recorded as such.
- Nothing in the file goldens depends on the registry timing.

## 8. A bad template left installed

Group H installs a broken template next to the good fixture.

- Each H case is its own session (own template set: the good fixture plus that one bad fixture). Templates are installed from the manifest
  and removed by the restore by hash (session document, section 8); a bad template never outlives its case.
- The bad fixture folders contain no file name that collides with the good set. A collision makes the session refuse before any change.
- Group K does the same with the edge fixtures (`fixture/edge/<Name>`: a valid template with one policy whose key has a leading or
  trailing backslash): each case is its own session with the good fixture plus that one edge fixture, installed and removed like a
  bad fixture. The three cases of one edge fixture share its template, not a session.
- Other groups install only the good fixture.

## 9. Crafted start files not restored, or crafted files mistaken for real ones

Group D, C and F write files into the real `System32\GroupPolicy`.

- Writing is allowed only inside `GpeditPaths.GroupPolicyDir` of the session, as the five known relative names, through one function that
  checks the full path, refuses reparse points and creates folders only below it. Nothing else is written outside `artifacts/`.
- The session backed up the original before the first write and restores it after the group, by hash, and verifies (session document,
  section 6). The crafted files are therefore removed by the same restore that removes classic's files.
- The crafted bytes come from the in-memory `PregWriter` and contain only BWC-authored names (`Software\Policies\BWC-Oracle`, the
  fixture value names). They are stored beside the golden in `<case>/start/` so the E4 tests have the same input.

## 10. Partial run

A crash, Ctrl+C, a locked machine or a kill leaves some cases done.

- Each case writes its golden into `<case>.tmp` first (all files, then `complete.json` last) and renames the folder in one move. A folder
  without `complete.json`, or a `.tmp` folder, is never trusted: `--resume` deletes leftover `.tmp` folders and redoes the case.
- `--resume` skips a case whose folder exists, has `complete.json` and whose recorded plan hash equals the plan hash of the current code
  (so a changed case is redone). Without `--resume` every selected case is run again.
- Ctrl+C is checked between cases and inside every wait (the token). The group's session restores the machine, the run prints which
  cases are done, and exits with code 1.
- A group whose cases are all done is skipped without starting a session.
- The plan version is part of the plan hash. It was raised to 2 when the driver began to type multi-line text with key presses (a
  comment set with `WM_SETTEXT` was never noticed by the dialog, driver document section 30), when `case.json` began to carry the
  operations as data (section 19) and when the roots are expanded at the start (driver document section 31). The goldens of
  version 1 hold bytes the old driver wrote, so `--resume` must not trust them: with version 2 every case is captured again. A
  change of the version without a reason in this list is a mistake, because it throws away hours of capture.

## 11. Rerun of one group, and output overwritten by a failed rerun

- `--only` selects groups, `--case` one case (id form `B/Pol_Class_Machine`; the group letter prefix is required), and the two combine.
  Other groups' folders are not touched.
- A rerun writes into `<case>.tmp` and replaces the old folder only after the new one is complete. A failed or cancelled rerun leaves the
  previous good golden in place. The replace is rename old to `<case>.old`, rename new, delete old; a crash between the two renames
  leaves `<case>.old`, which `--resume` restores when the real folder is missing.
- The untracked log of a rerun goes to a new timestamped folder; earlier logs are kept (they are small, screenshots are per run).

## 12. Microsoft text in tracked output

Menu text, message box text, dialog captions, status bar text, header names and tree names outside the fixture are Microsoft strings.

- `case.json` is built from explicit fields, not from a copy of the driver result. Every string goes through one scrubber: a string is
  kept only when it is a BWC fixture string (every string of the fixture ADML and the fixture ADMX names) or one of the short labels
  quoted in the plan (Enabled, Disabled, Not Configured, Computer Configuration, User Configuration, Administrative Templates, All Settings,
  Extra Registry Settings, Setting, State, Comment, OK, Cancel, Apply, Filter Options, Show Contents, File, Action, View, Help, Extended,
  Standard). Anything else is stored as `#` plus the first 12 hex digits of its SHA-256. The same text always gives the same hash.
- Message box title and text, menu item text and status bar text are therefore hashes; the raw text is in the untracked log
  (`artifacts/oracle/gpedit/matrix/<run>/...`). Numbers (counts, ids, rectangles, button ids) are kept.
- Tracked goldens contain file bytes only for files classic wrote from BWC fixture policies and BWC crafted input.
- A self check before the write scans the finished `case.json` for any string that is neither scrubbed nor in the allowed set and fails
  the case instead of writing it.
- Screenshots and the raw driver results are only under `artifacts/` (git-ignored).

## 13. Dialog or validation text read wrongly

- Group E records each message box as title hash, text hash, button list (the standard button names are kept) and the answer used, and
  whether the dialog stayed open. The raw texts are in the log. The oracle string id is looked up by the audit from the log text (the
  matrix does not guess it).
- A validation case that unexpectedly writes a file is recorded as such (`wroteFiles`), not as a pass.

## 14. Crafted state read as a different policy or a policy that classic hides

- A group D case names the affected policies and the node they are listed in. A policy row that is not found is a failed operation of
  the case, not an empty state. The state column is read from the list and the dialog state from the dialog; both are stored.
- `Pol_Both` rows are read under the root the case names; the same policy under the other root is a separate operation.

## 15. Duration, hangs and a locked machine

- Every operation has the driver's bounded waits. A hung call ends the case with an error, not the run.
- `--list` prints an estimate per group (start time per case plus a per operation cost) and the total. It is an estimate for planning,
  never used by the code.
- Cold start. The first classic start of a session (the fixture templates were just installed) and the first start after classic
  crashed can take minutes before the console tree answers. Seen on the VM: 230 s in group A, 195 s plus 292 s in group B, and more
  than 7 minutes right after a crash. UI Automation calls block for that time. A watchdog sized for a warm start then closed classic
  in the middle of a case that would have finished. The watchdog gives the first case of a session, and every case run after a failed
  case, 20 more minutes.
- Classic crashes on its own. Seen on the VM: an access violation in `mmcndmgr.dll` (exception codes `c0000005`, then `c000041d`)
  while the driver expanded Computer Configuration right after a user save. A case whose classic ended before the driver closed it
  (a crash or the watchdog) runs once more with a fresh, cold classic before it counts as failed. Only the second result counts, and
  the log line names the first failure. A failure while classic is still running (a missing node, a wrong value) is not retried,
  because it would fail the same way.
- The crash is also made less likely by the start order: for every group except H the runner sets `expandRootsAtStart`, and the
  driver expands Computer Configuration and User Configuration once before the first operation (driver document section 31).
  Group H is left out because its first operation must see the error UI of the bad template. A warm-up that fails is a start error
  of the case, so a classic that crashed during it is covered by the retry above; a warm-up that fails with classic still running
  (no children, no pattern) is not retried and fails the case with its message.
- A locked screen or secure desktop makes every operation fail; the run stops after three failed cases in a row in one group (the cause
  is then the machine, not the case) and says so.

## 16. Wrong arguments and unelevated run

- Unknown group letters, unknown case ids or an empty selection are errors with exit code 2 before anything starts.
- A real run without elevation, off the interactive desktop, or with a backup manifest present refuses with exit code 2 and changes
  nothing (the session's preflight, run for the first selected group before any case is touched).
- `--dry-run` and `--list` build every case, every operation list and every crafted file in memory and validate all operations with the
  driver's own validator. They report the counts per group. They never read the machine's GroupPolicy folder and never write.

## 17. Disk and path problems under goldens

- The golden folder is under the repo (`docs/parity/oracle/gpedit/goldens`). Folder names are the case ids with only letters, digits,
  `-`, `_` and `.`; case ids are checked for this when the plan is built.
- Files are written with exact bytes (no newline conversion). `case.json` is UTF-8 without BOM, LF, indented, sorted property order as built.
- A write failure (disk full, antivirus lock) fails the case; the temp folder is deleted; the old golden stays.

## 18. Visual groups (F) record layout that depends on the VM

- Window size, DPI and theme change rectangles. The runner records DPI awareness and the main window size in the untracked log and in
  `case.json` as numbers; the audit compares them with the BWC window at the same size. Screenshots are untracked.
- Tree icons are recorded as a hash of a small crop of the node's icon area and as an untracked PNG. A selected node has a different
  background, so the crop is taken with the node not selected where the driver can arrange it, and the hash is only a lead for a person.
- Toolbar, column widths, control rectangles and tab order come from UI Automation and window messages; a value the driver cannot read is
  recorded as `null` with the reason in the log, never guessed.

## 19. Operations in `case.json` that engine tests cannot use

The engine tests replay goldens, so `case.json` has to say what each operation was, as data, not only as a sentence. A sentence
cannot be parsed back, and a policy found by its display name or an option found by its label says nothing about the ADMX policy or
element it stands for.

- Each entry of `ops` holds `op`: the whole operation as a JSON object, written with the options that the plan hash uses (camel case,
  nulls left out). `plan`, the description sentence, stays. The plan hash is computed from the same serialization, so what a
  replay reads is what decided the hash.
- Three plan-only fields name the ADMX identity: `policyId` (the policy name), `policyNamespace` (the target namespace of the template
  that defines it) and, on an option, `elementId`. The driver ignores them when it runs an operation. They are set from the fixture
  data (`MatrixOps.Edit` from the policy, the option builders from the element), never typed by hand per case, so a case cannot name
  a policy that the fixture does not have. A lookup by label or position resolves to the element before the id is set; an element
  that cannot be found fails the build of the plan.
- A missing id would give a golden that cannot be replayed. Plan validation reports an `editPolicy` operation without `policyId`
  and `policyNamespace`, and an option without `elementId`, as a plan problem (`--dry-run` and `--list` print it and exit with an
  error). Group H is exempt: it edits a stand-in policy for a template that does not load, so no id exists. Its ids stay unset.
- The ids are strings of this repository (fixture ids), so they need no scrubbing and no text observed from classic is written into
  `ops`. The case's `observed` part keeps going through the scrubber. The ops file reader accepts the new fields on `editPolicy`
  and refuses them on every other kind, like any other field.
- The edge fixtures of group K have a namespace of their own. It is read from the edge ADMX (`policyNamespaces/target`), not
  assumed to be the namespace of the good fixture.

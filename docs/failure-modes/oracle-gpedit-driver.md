# Oracle gpedit driver failure modes

The driver (`tools/Bwc.Oracle.Capture/Gpedit/Driver`) drives classic `gpedit.msc` by UI Automation,
Win32 messages and a few real key presses, inside a `GpeditSession` (see `oracle-gpedit-session.md`
for what the session backs up and restores). A matrix runner (groups A to H and K of the oracle plan) feeds
it operations and records what classic wrote. This document lists every way a driven step can lie, that
is, report success while classic did something else, and what the driver does about each. It was
written before the driver code. Sections 20 to 24 of the session document (stale list, DPI, tabs,
coordinate clicks, tree selection) still hold and are reused, not repeated.

Entry points: `gpedit ops <file.json> [--dry-run]`. Output: `artifacts/oracle/gpedit/ops/<name>.result.json`
and `ops/<name>/*.png` (git-ignored). A result is trusted only when every field it relies on says `ok`.

The rule behind everything below: an operation records what it asked for AND what it read back from
classic afterwards. A mismatch is an error of the operation, never a silent success.

## 1. Wrong tree node selected

A path segment can match a similar node (`Windows Settings` under Computer and under User, a category
that exists under only one root, `All Settings` next to a category with a similar name), the console tree
can lazily fill, and a click can move the selection.

- A path starts at the `Computer Configuration` or `User Configuration` item, never at the first match
  anywhere. Each segment is matched among the direct children of the previous node, by exact ordinal
  text (case counts, `&` is literal in a tree).
- Each node is expanded and its children are polled for up to 20 seconds. A missing segment fails with
  the list of children that did exist (this is also how "this category does not appear under User" is
  recorded: the `treeChildren` operation returns the child names without selecting).
- Selection uses SelectionItem only. After selecting, the selected tree item is read back and its
  full path (walked up to the root) must equal the requested path, before and after the list is read.
- The list is checked for a change (section 2); a node that did not select is an error, not an empty list.

## 2. Wrong or stale result list

Section 20 of the session document: MMC keeps several `SysListView32`. The driver re-finds the list
for every use (visible by native `IsWindowVisible`, on screen, header with Setting and State ranks
first) and records its hwnd and headers in every list result.

- After navigation the list is read until two reads 400 ms apart give the same row names (up to 8 s),
  so a half refreshed list is not recorded.
- A placeholder node (`Software Settings`) has no Setting and State list; the result then carries the
  headers that were found and `settingStateList: false` instead of failing.
- The result carries `headers`, `listHwnd`, the row count and a status bar text. A caller that expects
  Setting and State checks `settingStateList`.

## 3. Wrong row with a similar name

The fixture has `alpha policy` and `Alpha Policy`, `Policy 1`, `Policy 10`, `Policy 2`, and names with
a literal `&`. Prefix or case-insensitive matching picks the wrong one.

- A row is chosen only by exact ordinal equality of the first column text with the requested display
  name. No prefix, no `Contains`.
- Zero matches fail with the closest row names. More than one match (possible under All Settings, where
  names repeat) fails with the count unless the operation gives `occurrence` (zero based, list order).
- Row text comes from UI Automation names of the row and its first cell; they are compared as read, so
  `&` in a name is compared as a literal character.

## 4. Dialog for the wrong policy

A key press can open the row that had focus, a previous dialog can still be open, or a second dialog
can appear.

- No setting dialog may be open before opening a policy; one that is open is recorded and cancelled
  (never OK) and the operation continues.
- The new dialog is the one top-level window of the mmc process that was not known before. Its title
  label (`labelTitle`) and window title are read and must equal the requested display name. Because
  a WinForms label may drop a lone `&` as a mnemonic, the comparison is exact first and, only if that
  fails, equal after removing `&` from both; the result says which rule matched (`titleMatch`).
- A title mismatch cancels the dialog and fails the operation before anything is set.

## 5. Previous and Next Setting moves to another policy

Previous and Next switch the same dialog to another policy of the node (the list order, which depends
on sort and filter), possibly after applying pending changes.

- `Previous` and `Next` are presses in a sequence, recorded with the policy title before and after the
  press, read again like a new dialog (state, comment, options). The operation never assumes which
  policy it moved to.
- Whatever policy the dialog shows at the end of the operation is closed by Cancel unless the sequence
  ends in OK or Apply, and the result records the final title, so a caller can tell a changed policy
  from the requested one.
- A pending change with Previous or Next may be applied by classic; the file settle step (section 14)
  runs after every press that can write, and the result lists which press produced which change.

## 6. Control not found by id

WinForms automation ids are control names. Fixed controls (`buttonOK`, `radioButtonEnabled`,
`textBoxComment`, `labelTitle`, `textBoxHelp`, `labelSupportedOnText`) are stable. Controls of the
options pane are created in code; the probe showed names built from the element label
(`textBoxText option:`, `dropDownListDrop-down option:`, `checkBoxCheck box option`,
`buttonList option`, `flowLayoutPanelDecimal option:`), and the numeric edit inside a number control has
no UI Automation element at all in the control view.

- Fixed controls are found by id inside the dialog and the lookup fails with the ids that were found.
- Options are found by the visible label (the label control of each group, or the check box text),
  not by a guessed id. Groups are the direct children of `flowLayoutPanelDynamic`, ordered by screen
  position. Each group is classified from its native window classes and styles (an `EDIT`, a
  multi-line `EDIT`, an `EDIT` inside an up-down control, a `COMBOBOX` drop-down or drop-down list, a
  `BUTTON` check box, a `BUTTON` named `Show...`), and the result records the guessed kind next to the
  automation id and class so a wrong guess is visible.
- Label match is exact ordinal first, then equal after trimming a trailing colon and ignoring `&` and
  case; the rule used is recorded (`labelMatch`). Two groups with the same label fail unless `index`
  is given.
- A group that cannot be classified is recorded as kind `unknown` with its window tree and the
  operation that needed it fails.

## 7. Value typed into the wrong control

Focus can sit elsewhere, and a text edit and a numeric edit look alike.

- Text of a single-line edit is sent to the exact control window of the matched group (`WM_SETTEXT`), and
  read back with `WM_GETTEXT`. `typed: true` types it with real key events
  instead, after the control is focused and the focused element is read back as that control: the edit
  limit (a text `maxLength`) applies to typed text and not to `WM_SETTEXT`, so limit cases use `typed`. The read value must equal the requested text, except for a numeric control (section 9)
  where the read value is recorded as `observed` and compared by the caller.
- A multi-line edit (window style `ES_MULTILINE`: the multi-text element and the comment box) is always typed with
  real key events, whatever `typed` says, because `WM_SETTEXT` sends no change notification to such a control
  (section 30).
- Before any set, the control must be enabled (`IsWindowEnabled`); a disabled control (state is Not
  Configured or Disabled) fails with that reason. The op order is state first, then comment, then options.
- The dialog is brought to the foreground and the control belongs to the dialog window that was
  verified in section 4 (checked by walking its parent chain).

## 8. Combo box and enum item names with `&`

The fixture has items `Tom & Jerry` and `Salt & Pepper`, and labels with `&&`.

- Items are read from the combo with `CB_GETLBTEXT`, which returns the raw text. The requested item is
  matched by exact ordinal text first; `itemIndex` can select by position. A text that matches no item
  fails with the list of items read.
- Selection sends `CB_SETCURSEL` and then the parent `WM_COMMAND` with `CBN_SELCHANGE`, so WinForms raises
  the selection change event the way a user click would. The selected index and text are read back.
- An editable combo box (suggestions) takes arbitrary text (`WM_SETTEXT` plus `CBN_EDITCHANGE`); a
  drop-down list takes an item only.
- The result records both the raw text and whether the display shows a doubled `&`; the driver does
  not decide which one classic means.

## 9. Numeric spinner clamping and late validation

The number control is a spin edit. Text set into it is validated and clamped when focus leaves, not
when text changes, so a value read at once looks unchanged, and a value above the maximum silently
becomes the maximum.

- After setting text the driver moves focus to the OK button (a real focus change inside the
  dialog, nothing is pressed), waits 300 ms, and reads the edit again. That second text is `observed`; the first is
  `requested`. The result has both and `clamped: requested != observed`.
- Pressing OK or Apply first focuses that button for the same reason, so a value typed last is committed
  before the press.
- Range and step are not guessed: they are what the classic dialog does. Out of range text is one of the
  matrix cases, and the observed value or a validation box (section 13) is the evidence.
- A non numeric text may be refused with a box or reset; both are recorded.

## 10. Show Contents grid editing

A list element is a `Show...` button that opens a modal `Show Contents` dialog (`ListBoxDialog`) with a
`dataGrid`. A grid cell edit has an edit mode, a new row placeholder and a commit on Enter or on
leaving the row.

- The driver reads the grid (rows and cells) with UI Automation values; it records the grid tree if the
  structure is not as expected.
- Setting rows is done with the keyboard only: focus the grid, `Ctrl+Home`, `Down` and `Right` to the
  cell, `F2`, `Ctrl+A`, the text typed as Unicode key events, `Enter`. The grid is read back after
  every cell and the operation fails if any cell differs. (No UI Automation value writes: the registry
  writer check refuses `SetValue` calls it cannot type, and a grid cell edit needs the edit mode anyway.)
- Removing old rows is not attempted by guessing; `clearRows` selects each row header and presses
  `Delete`, and then the grid is read back; an unexpected count fails.
- OK or Cancel of the grid dialog is pressed by id and the dialog must close; the grid dialog is never
  left open (section 23).

## 11. Focus stolen

The VM desktop can raise another window (a notification, another tool). Key presses and focus based
steps then go elsewhere.

- Every key press is preceded by bringing the intended window to the foreground, and the foreground
  window's process is read back; if it is not the mmc process the key press is not sent and the
  operation fails with the foreground window's class and title.
- Patterns (Invoke, SelectionItem, Toggle) and window messages do not depend on focus and are preferred;
  keys are used only for menus (Alt, mnemonics, Escape), the context menu key and the opening of a row.
- The GUI mutex of the session keeps other oracle tools away; a foreign window is still possible and is
  the reason for the foreground check.

## 12. MMC modal error boxes

MMC or a snap-in can show an error box at any time (a snap-in failed, a template error). The box blocks
the main window: UI Automation calls on it can hang or time out.

- Every wait loop checks the process for an unexpected top-level window. One that appears is captured
  (title, every static text, every button, a screenshot) and dismissed with a safe button, and the
  operation is marked with `unexpectedWindows`.
- Pattern calls that can enter a modal loop run on a worker thread with a timeout (the probe's
  bounded invoke); after the timeout the driver looks at the windows instead of waiting.
- If a box cannot be dismissed, the run stops with a fatal error (the session restore closes mmc).

## 13. Validation message boxes

OK, Apply, Next or Previous can show a box (required value missing, number out of range, empty list)
and leave the dialog open.

- After each press the driver watches for the dialog to close, or for a new window. A new window is a
  message box: title, text, buttons and a screenshot are captured, then it is answered with
  `messageBoxAnswer` of the operation (default order OK, Close, Cancel, No) and must close.
- The press result says `blockedByMessageBox: true` and the dialog is still open; the operation
  then cancels the dialog (never retries OK by itself) and records `dialogCancelledAfterBox`.
- The text of a box is read from native static controls first and from UI Automation text second; both
  are stored if they differ.

## 14. Timing of file writes

Classic writes `Registry.pol`, `gpt.ini` (and `comment.cmtx` on a first comment) within about 10 ms of
the OK and applies to the live registry within about 4 s. A read taken at once sees nothing; a read
taken too late can see the next operation. An OK with no change writes nothing.

- After every operation that can write, the driver polls the file hashes every 50 ms and returns when
  they have been unchanged for 500 ms after the last change, or when 1.2 s passed with no change at
  all (that is how "writes nothing" is recorded). The overall limit is 10 s; hitting it is
  `settleTimedOut: true`, not success.
- A locked file counts as unstable until it can be read again.
- Then the live registry under the BWC-Oracle keys is polled the same way (stable 500 ms, limit 10 s)
  and recorded with its own change flag.
- The result holds the hashes before and after, the first-seen time of each file, the gpt.ini version
  before and after and, when anything changed, the full snapshot of the changed files.

## 15. Multiple saves per OK

One OK can write a file more than once (comment file first, then the policy file, or a second write of
`gpt.ini`).

- The settle step keeps every distinct hash it sees per file, with the time. `writes` per file is the
  number of distinct hashes after the first state. The final state is the one the result reports as
  after; the intermediate hashes are kept in `history`.

## 16. User class policies write `User\Registry.pol`

A user policy changes `User\Registry.pol` and the user part of the gpt.ini version, not the machine
part.

- The file set watched is the five files of `PolFiles.Names` (Machine and User `Registry.pol` and
  `comment.cmtx`, `gpt.ini`). The result splits the gpt.ini version into machine and user halves and
  marks which file group changed (`machine`, `user`).
- The first user policy creates the `User` folder and files: a file that did not exist before is a
  change like any other (`-` to hash).

## 17. Class Both appears under both roots

`Pol_Class_Both` is listed under Computer Configuration and User Configuration and writes to the
matching hive file of each; the rows are different nodes with the same display name.

- Rows are always found within the node that was selected, so the same display name under the two roots
  is two separate operations, each preceded by its own `selectNode` and checked by section 1.
- The result records the root the policy was edited under, and `files.changed` shows which hive file was
  written. A Both policy edited under Computer must not change the User files; that is a result to
  record, not an assumption.

## 18. The All Settings node

`All Settings` lists every policy of every category, with thousands of rows, repeated names and rows
that exist under both roots, and it is slow to read through UI Automation.

- It is a normal path under Administrative Templates. Rows are read with a cached UI Automation request
  and a cap (`maxRows`, default 6000) and the result says `truncated` when the cap was hit.
- Opening a policy there needs `occurrence` when the name repeats (section 3).
- All Settings can be sorted and filtered like a category, so section 19 and 20 apply.

## 19. Filter state persisted in MMC console state

View > Filter Options and Filter On are saved in the MMC console state, so a later operation sees a
filtered list; the session restores `%APPDATA%\Microsoft\MMC\gpedit` only at the end.

- The status bar text `Filtered N of M` is recorded in every list read, and `filtered` is set from it.
- `setFilter` returns the dialog values before and after and reads the View menu check mark of Filter On
  afterwards. The ops runner never changes the filter on its own.
- The sample and the matrix runner end a filter group with `clearFilter` (all filters off, Filter On
  off) and the runner verifies the status bar no longer says `Filtered`.
- A run that is cancelled mid filter is covered by the session restore of the console state (session
  document, section 10).

## 20. Sort state persisted

A header click sorts and clicking again reverses. The sort is kept per node by MMC and is saved in the
console state, so rows come back in a different order later.

- Every list read records the sort arrow of each column header (`HDF_SORTUP` and `HDF_SORTDOWN` read
  from the header control), so the order of rows is always interpreted together with its sort.
- `sortColumn` records the arrow before and after and the first rows after. It uses Invoke on the header
  item, and only if there is none a guarded click (`ProbeClickGuard`). It never presses more than once
  per operation, so the number of toggles is the number of operations.
- The matrix runner (not this library) must click until the wanted direction shows and then read.

## 21. Menus are not in UI Automation

The MMC menu bar is not an HMENU of the main window and not a UI Automation menu bar (the probe found
only the system menu). Pop-up menus are normal `#32768` windows.

- Top menus are opened with Alt and the access key (File, Action, View, Help). The popup is found as a
  `#32768` window of the mmc process; failing to find one in 2 s is an error with a screenshot.
- Items are read from the popup's `HMENU` (`MN_GETHMENU`): text with the raw `&`, id, grayed, checked,
  separator, and the nested items of submenus by `GetSubMenu`, so nothing nested has to be opened.
  UI Automation names of the popup are stored beside them.
- An item is activated by its access key typed into the open menu (read from the raw text), and for a
  submenu by its key and the next key. An item without a unique key is not activated; the operation
  fails instead of pressing arrows blindly.
- Every operation ends with the popups closed: Escape is pressed until no `#32768` window of the process
  is left (at most 6 times); if one stays the run stops (it would block every later step).

## 22. Result list context menu

- The row is selected (section 3), the list focused, and the context menu key (VK_APPS) pressed. The
  menu is then dumped like a top menu. Shift+F10 is not used (it needs a held modifier).
- With no policy named, the context menu of whatever is selected is dumped and the result says which
  row was selected, so a background menu is never assumed.
- The context menu is dismissed with Escape and the selection is unchanged by it.

## 23. A dialog left open between operations

An error half way through an operation could leave the setting dialog, the Show Contents dialog, the
Filter Options dialog or a message box open, so every later operation fails or edits the wrong thing.

- Every operation runs in a try block whose epilogue closes whatever the operation opened: nested
  dialogs first (Show Contents, message boxes), then the setting dialog by Cancel, then Filter Options
  by Cancel. If the Cancel did not close the window, Escape, then `WM_CLOSE`. The epilogue then
  checks that the process has no visible top-level window besides the main window, and the result
  records `epilogue` actions.
- The runner treats a window that survives the epilogue as fatal and stops: the remaining operations are
  listed as `skipped` with the reason.

## 24. Continuing after a failed operation

- After a failed operation the runner repeats the epilogue and then continues with the next one when the
  main window is alive and no window is stuck. Operations are written to be self contained: any
  operation that depends on a node carries `root` and `path`, and the runner selects that node first
  unless the tree already shows exactly it (read back through the parents). A failed navigation
  therefore fails the next operation instead of editing in the wrong node.
- A failed write operation is not retried (a second OK could write a second time).
- Fatal conditions: mmc exited, the main window is gone, a window cannot be closed, a popup menu cannot
  be closed, Ctrl+C. The rest is `skipped`.

## 25. Live registry observed too early or from the wrong key

- Only keys under `Software\Policies\BWC-Oracle` and `Software\BWC-Oracle-Gpedit` (HKLM and HKCU,
  64-bit view) are read, recursively and read only: value name, type and data. Nothing else is read or
  written. Deleting is the session's job (session document, section 9).
- The registry settle (section 14) waits for the key set to stop changing; no change after files changed
  is recorded as `registryChanged: false` with the wait time; it is not an error, since classic may
  not have applied it yet or may not apply a given change.

## 26. Ops file errors

A mistyped kind, a missing parameter or a state name that does not exist would otherwise surface in the
middle of a run on the VM.

- The file is validated completely before anything starts (`--dry-run` does only that, unelevated, and
  prints the plan). Unknown kinds or properties, missing required parameters, bad state names, bad
  button names and bad menu names are errors with the operation index. Exit code 2.
- A real run validates again, and refuses to start on an error.
- Property names are case sensitive and unknown properties are errors, so a typo cannot be ignored.

## 27. Hung calls, cancellation, process death

- A UI Automation or message call that does not return within its limit is abandoned (patterns run
  bounded; messages use `SMTO_ABORTIFHUNG` with a 5 s timeout). The operation fails; the next
  operation first checks that mmc still responds.
- Ctrl+C sets the session cancellation token; the runner checks it between operations and inside waits
  and ends with a cancelled result file; the session restores the machine.
- A dead mmc (`HasExited`) is fatal. The result file keeps everything recorded before it.
- An expand that reaches classic while classic is still busy with a save can crash classic (seen: an access violation in
  `mmcndmgr.dll` when Computer Configuration was expanded right after a user save). Before each tree expand the driver waits up to
  30 s for classic's input queue to be idle (`WaitForInputIdle`). When the wait times out, the expand still runs and the result
  records a note.
- A second protection against that crash: for a matrix case the runner asks for both configuration roots to be expanded once right
  after classic starts, before any save, so no root is expanded for the first time after a save (section 31).

## 28. Screenshots and result files

- A screenshot is best effort: it never makes an operation fail. The path is recorded or the reason.
- The result file is rewritten after every operation, so a hang keeps the earlier results. The JSON
  writer redacts the user's SID and the machine name. Files stay under `artifacts/` and are untracked,
  because they hold Microsoft UI text.

## 29. Quoting of what classic shows

- Text read from classic (titles, help, labels, box texts, menu items) goes into git-ignored result
  files only. The sample ops file and this document name BWC fixture policies and no Microsoft text.

## 30. Multi-line edit controls and the comment box need real key presses

A multi-line edit control (`ES_MULTILINE`) sends no `EN_CHANGE` when its text is set with `WM_SETTEXT`. The setting dialog never
notices the edit, so Apply stays disabled and OK writes nothing, while a read back of the control returns exactly the requested text.
Seen in the first capture: the four comment-only goldens of group A had a disabled Apply button after the comment was set. The step
looked successful and the golden was wrong.

- The way a text is entered is decided by the window style of the target control, never by the caller: a control with `ES_MULTILINE`
  is typed with key presses (focus, focus read back, Ctrl+A, Delete, then the text with Enter between lines) even when the option
  says `typed: false` or says nothing. A single-line edit keeps `WM_SETTEXT` unless `typed` is true, because the case that sets a
  too long text without typing (`E/Text_TooLong_SetText`) depends on the difference. The comment box is typed the same way.
- The result records how the text was entered (`via`: `keys` or `WM_SETTEXT`). For the comment it also records the enabled state of
  Apply before and after the text (`applyEnabledBefore`, `applyEnabledAfter`), so a golden audit can see that the dialog noticed the edit.
- Typing goes to the wrong window. The foreground check of section 11 runs first. Before every line the driver checks again that the
  setting dialog is still open, that the foreground window is a window of mmc and that the focused element is still the control. A
  failed check stops typing at once with a message that names what was wrong.
- Enter is the default button. A typed Enter that reaches the dialog's default button closes the dialog (or applies the setting),
  and the rest of the text would go to another window or nowhere. The check before each line catches the closed dialog and throws a
  `DriverException` that says the setting dialog is no longer open after a typed Enter. Text that follows is never sent.
- Old text stays because Ctrl+A did not select it. After the select all and delete, a multi-line control or the comment box must be
  empty; if not, the step fails before any text is typed.
- Line breaks differ (`\n` in the plan, `\r\n` in the control). The text read back and the requested text are compared with every
  line break normalized to one form. For the comment a difference is a `DriverException`. For a multi-text option the read back is
  recorded as `observed` (and `clamped` when it differs), as for any typed option, because a limit of the element can legitimately
  change what the control holds.
- An empty comment text means select all and delete by keys, and the read back must be empty.

## 31. The tree roots are expanded once at the start

Classic crashed (access violation in `mmcndmgr.dll`) when the driver expanded Computer Configuration for the first time right after
a save on the user side (case `A/User_Then_Machine`). The wait for an idle input queue (section 27) lowers the risk and does not remove it.

- For a case run by the matrix runner (every group except H) the ops file sets `expandRootsAtStart`. After classic started and before
  the start snapshot and the first operation, the driver expands Computer Configuration and then User Configuration: wait idle,
  expand the root item with the same call the tree resolution uses (expanding an expanded node is a no-op, so a later expand of
  the same node does nothing), and wait until the root shows its children and the list of names stopped changing. Nothing deeper
  is expanded (not Administrative Templates) and the selection is not changed.
- Group H installs a bad template and must see classic's error UI through its own first operation (`dismissDialogs`), so it does
  not use the warm-up. The hand-run `gpedit ops` command leaves the flag off unless the file sets it.
- A dialog of classic is open at that point (an error box of the start). The warm-up neither expands nor dismisses it; the result
  records that the warm-up was skipped and names the dialog, so the first operation sees the dialog as before.
- The result file holds a `warmUp` object: per root the expand state before and after and the number of children, or the skip
  reason, or the error. A root that was already Expanded with at least one child before the warm-up is not waited for: its entry
  says `skippedWait: true` with the reason (section 32).
- The warm-up fails (no root item, no ExpandCollapse pattern, no children within the limit, classic exited). That is a start error
  of the case: the result carries `startError`, the case is not complete, and when classic ended the matrix runner's one retry with
  a fresh classic applies (matrix document, section 15). A failure is never ignored, because the case would then run on a classic
  whose state is not the state the goldens assume.

## 32. Phase times, the warm-up early return and start evidence

The first 24 goldens showed that the capture is slow in places nothing measured: the warm-up took about 10 s per root although classic
already had both roots expanded, and about 43 s of one `editPolicy` operation was unexplained. The driver now records how long each
phase takes, skips the one wait that was not needed, and keeps evidence when the console tree does not appear. Nothing else about
what is clicked, typed, read or waited for changes.

- The warm-up returns at once for a root that was Expanded before the call and already shows at least one child item. The root's
  entry in `warmUp` then holds `skippedWait: true` and `skippedWaitReason`. Expanded alone is not enough: classic can report a root as
  Expanded while its children are not loaded yet, and returning then would leave the first operation to meet a tree that is still
  filling (the crash of section 31). A root that is collapsed, or expanded with no child yet, runs the old code path exactly: expand,
  then wait until the list of names stopped changing for 1500 ms, with the 60 s limit.
- The result file gets a `timing` object at the root (classic start, tree ready, warm-up total, all in milliseconds) and a `phases`
  array on every operation entry (`{ "name": ..., "ms": ... }`, in the order the phases ended). A phase that wraps others, such as
  `execute`, ends after them and so is listed after them. Timings are new keys and never part of `data` or `files`, because the
  golden builder copies `data` whole for most operation kinds: a timing inside `data` would reach a tracked golden and make every
  recapture differ. The plan hash and `GpeditOp.Describe()` are not changed.
- The recorder must never break an operation. It only measures with `Stopwatch` and adds to a list, and the runner builds the JSON
  of the phases inside a `try`: when that fails the phases are dropped and the operation result stays. A phase that ended with an
  exception is still recorded, then the exception continues unchanged.
- The measurement must not change behaviour. No sleep, timeout or wait is added, shortened or reordered. The recorder does no UI
  automation call and does not touch classic.
- A failed start ("no console tree within 30 seconds", "the console tree stayed empty for 30 seconds") now carries evidence in
  `startEvidence`: the visible top-level windows of the mmc process, the class names of the direct children of the main window and
  a screenshot name. Collecting it can itself fail while the tree is missing (the window is gone, a UIA call throws, the screenshot
  hangs). Every part is collected inside its own `try` and a failed part is recorded as an error text, so the original start error
  and its message are always the ones that are thrown. The message text is not changed.

## 33. Where a tree hop spends its time, and a start that classic refuses

A one-case run (`A/Machine_Enabled`, 71 s) showed two costs that section 32 could not explain. Both roots were already Expanded with three
children, so the warm-up took its early return, and still each root took 8.2 s, all of it inside `ResolveNode` before the early-return check.
The `nodeSelection` phase of the `editPolicy` operation took 29.9 s for 5 hops, about 6 s a hop, also for hops that were already expanded
(no expand call). Note that `nodeSelection` is the whole of `SelectNode`: the hops, the select call, the wait for the selection to read
back, the fixed 400 ms pause and the wait for the result list to settle. So the time may also sit outside the hops. This section makes the
next run say which step it is, and teaches the driver one more start outcome. It does not change what is clicked, typed, read or waited for.

Per-hop timing:

- `ResolveNode` (and `FindTree`) record, per call and per hop, how long each step took: `FindTree`, `WaitIdle` (and whether it returned
  idle), the expand (whether an expand call was made, which means the item was Collapsed; whether the bounded call timed out; the time to get
  the pattern, to read the state and to call it), each `ChildItems` call, the `NameOf` reads and the number of `WaitChild` polls with the
  time slept. `SelectNode` also records the time of its own steps after the hops: the select call, each `SelectedPath` read while waiting for
  the selection, the 400 ms pause, the wait for the list to settle and the last `SelectedPath`. The warm-up root entry gets the same detail
  under `resolve`, plus the time of its own child count and, on the slow path, of its expand and its polling.
- Where it goes: a `hopPhases` array on the operation entry (one object per `ResolveNode` or `SelectNode` call, in the order they ran) and
  `warmUp.roots[].resolve`. Never `data`, never `files`, never the `hops` array of `SelectNode` (which stays a list of names). The golden
  builder copies `data` whole for most operation kinds and `hops` is the only one of these keys it drops by name, so a timing inside `data`
  would reach a tracked golden. The driver therefore keeps the hop detail in a list of its own, which the runner copies into the entry next
  to `phases`. The scrubber also drops `phases`, `hopPhases` and `timing` by name from anything it cleans, and the self test builds a
  golden from a result that carries all three and from a `data` that carries `hopPhases`, and requires none of them in any file of the golden.
- Measuring must not add or reorder a UI Automation call. The expand split uses the same `TryGetCurrentPattern`, state read and bounded call
  as before, only with a stopwatch around each; the "was Expand called" answer comes from that same state read, not from a new one. The
  per-call lists are capped (20 values, then a count and a total), so a long poll cannot make the result file huge.
- A hop that throws (no such child, classic gone) still leaves its detail up to the failure, because it is added to the list before the
  steps run and filled in as they end. Building the JSON is inside a `try`: a failure drops the hop detail and keeps the operation result.

A start that classic refuses:

- Seen: a Registry.pol with a key that has a leading backslash (written as the edge template spells it) makes classic show a `#32770` dialog
  and no console tree. The first start evidence recorded `noConsoleTree` for it and the case failed on every `--resume`, but the refusal
  is the answer for that case. A case can say its start files are crafted and so may be refused (`StartMayBeRefused` on the case, set for
  every case built from crafted start files, which are the cases of group D and the read cases of group K). Cases that start from an empty
  or normal GPO never say so, and for them a missing console tree stays a failed start.
- The refusal is decided only when all of these hold after the 30 s wait for the tree has ended without a tree: the case allows it, mmc.exe
  is alive, a last look for the tree finds none, and a visible top level window of the mmc process with class `#32770` and a non empty
  rectangle exists. A timeout alone, or a missing tree with no such dialog, is still `no console tree within 30 seconds`. The class is
  the test, never the dialog text, so the decision does not depend on the language of the VM.
- A slow start that shows a transient dialog is not taken for a refusal: the 30 s wait is not shortened, a last look for the tree runs
  before a dialog is accepted, and the same dialog window must still be visible, with the tree still missing, after the evidence and the
  screenshots were taken (that takes a moment). A dialog that went away, or a tree that appeared, ends in the normal start error or the normal start.
- The dialog is not dismissed before the evidence exists: first the window list, the main window's child classes, the texts of every
  control of each such dialog (class and text) and the screenshots, then the flag. The dialog texts are Microsoft text and go into the
  result file under `artifacts` only (section 29). Classic is then closed by the same `Close` a normal case ends with (message boxes
  answered, other windows cancelled, then `WM_CLOSE`); the session restore and the runner's stray `mmc` close are still behind it.
- The result gets `startRefused: true` and a `startRefusal` object (dialog classes, titles, control texts, screenshot names) and no
  `startError`. The operations are reported `skipped: true` with a reason, never `ok: false`. The GroupPolicy files are read once more
  after classic closed (`filesAfterRefusal`) so the golden can state whether they still equal the start files.
- A refusal whose dialog window is the one this driver took for the main window (its title holds "Group Policy") is handled the same way,
  because the check looks at every visible top level window of the process, not at `Dialogs()`, which leaves the main window out.

## 34. Finding the console tree and the selected node without a search of the whole window

The per-hop timing of section 33 (`A/Machine_Enabled`, 76 s) showed where the time goes. `FindTree` costs about 8.1 s a call: a
`TreeScope.Descendants` search for a Tree over the whole main window, then a second one by class name when the first finds nothing. It runs
in each warm-up root, each `ResolveNode` and inside each `SelectedPath()`, which then adds a `FindFirst` for the selected item and costs about
10.5 s. The tree-ready wait and the start refusal check use the same search. The hops themselves cost under 100 ms. About 50 s of the 76 s
are these searches. This section replaces how the two elements are looked up and nothing else: no wait, timeout or sleep is shortened,
removed or reordered, no action sent to classic changes (expand, select, click, keys), and the data read is the data read before. Every fast
route has a fallback to the old search, so the worst case is the old cost.

The routes:

- Tree by window. The visible `SysTreeView32` window below the main window is found with Win32 (the same child enumeration the list lookup
  and `FindTreeWindow` use), and its element is taken with `AutomationElement.FromHandle`. The route name is `hwnd`. The old searches
  keep the names `uiaType` (Descendants search for ControlType Tree) and `uiaClass` (Descendants search for class `SysTreeView32`), and
  `findTreeVia` in the hop timing says which one found the tree (`timing.treeReadyVia` says it for the tree-ready wait at the start). There is no cache: the window lookup costs milliseconds, and a cache
  would need the checks below on every use anyway.
- Selected node by pattern. The tree element's `SelectionPattern.GetSelection()` gives the selected item. The path is then built with the
  same parent walk and the same names as before. The route is counted in the hop timing under `routes` (`selectedPath.selectionPattern`
  or the reason for the fallback, `selectedPath.fallback...`), and the tree route of the lookup inside `SelectedPath()` under
  `selectedPath.findTree.<route>`.

What can go wrong and the guard of each:

- Two visible `SysTreeView32` windows (a snap-in that shows a second tree, a tree inside the result pane). The old search returned the first
  match in UI Automation order, and the window order of Win32 need not be the same. The fast route is used only when exactly one visible
  `SysTreeView32` window with an area exists. Two or more means the old search runs.
- A hidden `SysTreeView32` (MMC keeps views it showed before, and a favorites tree). A window that is not visible, or has an empty
  rectangle, is not a candidate. One visible window next to hidden ones is the tree.
- The handle of a window that is destroyed between the enumeration and the use. `FromHandle` throws (`ElementNotAvailableException`,
  `ArgumentException`, `InvalidOperationException`, `COMException`, `Win32Exception`) or the window is gone when checked after it. Every
  such outcome falls back to the old search. The window must also still exist and still be visible after the element was taken, and the
  element's native window handle must be that handle.
- `FromHandle` of a window that UI Automation does not know yet (classic is still starting). The element is accepted only when its control
  type is Tree or its class name is `SysTreeView32`, which is what the old searches accepted. Any other element, or a read of those two
  properties that throws, falls back. While classic still shows no tree window at all, the tree-ready wait polls as before: no window means
  the old search for that poll, and the 30 s wait, its timeout and its failure evidence stay unchanged.
- `SelectionPattern` missing on the tree element (the proxy of the tree view may not offer it). No pattern means the old `FindFirst`.
  A pattern call that throws (element gone, classic busy) means the old `FindFirst`, which then throws or answers exactly as it did
  before. When classic is dead, `Main()` and the old search throw the same dead-process error as before.
- `GetSelection()` returns no item. That is also what a tree with nothing selected returns, but it can also be a proxy that does not
  report the selection, so it is not taken as the answer: the old `FindFirst` runs and decides (null when it finds none, as before).
- `GetSelection()` returns more than one item. The console tree selects one node, so this is unexpected and falls back.
- The returned element is not a TreeItem. The parent walk would then stop at once and give a short or empty list. It falls back.
- The returned item does not report `IsSelected` through its `SelectionItemPattern`. The old search matched on exactly that property,
  so an item that does not report it is not the one the old search would have found. It falls back. This is the guard for a selection
  that differs between the routes: the fast answer is used only when it satisfies the old condition (TreeItem and IsSelected), and
  everything after is the same walk, so the same UI state gives the same list of names.
- A stale cached element. Nothing is cached across calls (tree element, selected item or handle), so there is nothing to go stale. Each
  call enumerates the windows and takes a fresh element.
- Wrong timing data. The route names go only to the untracked result file (`hopPhases` and `warmUp.roots[].resolve`), never into `data`,
  and the scrubber and the golden builder drop them by name as they drop the other timing keys (section 33).

Not changed here, found while reading the code: other `TreeScope.Descendants` searches under the main window. `ReadStatus` (UI Automation names
of the status bar, a StatusBar search over the main window on each call, run by the `readList` and `readStatus` operations and by the filter
operations) and `DistinctTabs` (a TabItem search over the main window, run by the tab operations) have the cost of the old `FindTree`, which
is about 8 s a call. `ListCandidates` asks for the HeaderItem descendants of each visible list on every `VisibleList()` call, which runs
in each poll of `WaitListStable` (inside every `SelectNode`) and of `FindPolicyRow`. Its cost on a list of several hundred rows was not
measured.

## 35. A Windows Forms check box that is not a native check style

The first three cases that set a check box option (`B/Pol_Bool_TrueFalseValues`, `B/Pol_Bool_StringValues`, `B/Pol_Bool_TrueFalseLists`)
stopped with `no option labelled 'Number check'. Labels found: '' (unknown)`. The recorded option was `kind: unknown`, label empty, panel id
`checkBoxNumber check`, class `WindowsForms10.BUTTON.app.0.297b065_r40_ad1`. The check box is a direct child of the options panel with a
BUTTON class, but its style is not one of the native check styles (BS_CHECKBOX, BS_AUTOCHECKBOX, BS_3STATE, BS_AUTO3STATE). A Windows Forms
`CheckBox` whose `FlatStyle` is not `System` is created owner-drawn (BS_OWNERDRAW, type 0xB). `DiscoverOptions` then fell into the branch
for a container, looked for STATIC, EDIT and COMBOBOX children, found none, and left kind and label empty. This section teaches the driver
to recognize that control and to read its state. It changes no wait, timeout or sleep and no action sent to classic other than the check box
path of `SetCheck`. A native check box is found, read and set exactly as before, and no UI Automation call is made for it.

Recognition: a direct child with a BUTTON class is a check box when its style is a native check style (as before), or when its BS_ type is
owner-draw and its UI Automation element (`AutomationElement.FromHandle`) has the control type CheckBox or supports `TogglePattern`. The
label is the window text, as for a native check box. The decision is a pure function over plain values (class, style, control type, whether
the toggle pattern exists) in `Driver/CheckBoxRead.cs`, covered by `gpedit selftest` before the code was written.

Reading the state: a native check style keeps BM_GETCHECK. A check box recognized through UI Automation is read through
`TogglePattern.Current.ToggleState`, mapped to the BM_GETCHECK numbers (Off 0, On 1, Indeterminate 2). `DescribeGroup` and all three reads of
`SetCheck` (before, after, and the read-back after the extra click) use the same reader.

What can go wrong and the guard of each:

- The owner-drawn check box is not recognized (this failure). The guard is the recognition above. If a case still fails, the option keeps
  `kind: unknown` and the evidence of the unknown option now holds the BUTTON style, the BS_ type, the UI Automation control type and
  whether a toggle pattern exists (`checkEvidence`, untracked), so the next run says which of the tests failed instead of an empty label.
- BM_GETCHECK is wrong for an owner-drawn button. Windows Forms draws the check mark itself and the native state of such a button is not
  kept, so BM_GETCHECK can answer 0 for a checked box. The guard is the reader: a check box that is not a native check style is never read
  with BM_GETCHECK while the toggle pattern exists. If the pattern is missing at a read, the reader falls back to BM_GETCHECK and says so in
  `checkStateVia`, so a state read that way is known to be unreliable. `SetCheck` still fails when the read-back does not equal the wanted state.
- A push button, a radio button or a check box of another kind is taken as a check box. An owner-drawn BUTTON is a check box only with the
  UI Automation evidence (control type CheckBox or a toggle pattern). A push button has the type Button and no toggle pattern, a Windows Forms
  radio button has RadioButton and no toggle pattern, so both stay as before (unknown, or handled by the container branch). The panel id
  prefix `checkBox` is written into the evidence as a hint only and never decides. A UI Automation read that throws (window gone,
  element not available) is "no evidence", so the child stays as before.
- A toggle that did not take. `SetCheck` uses the toggle pattern first, as before, waits as before, reads the state with the same reader and,
  if it is not the wanted state, clicks once and reads again, as before. The operation fails when the state is still not the wanted one. The
  two readers are never mixed within one call, so a toggle cannot be judged by a reader that cannot see it. If the toggle pattern is missing
  at the toggle, the fallback (BM_CLICK) is unchanged.
- Three-state check boxes. Indeterminate is 2 for both readers and is kept as a value (`checkState: 2`), never turned into On or Off. The
  test `state == 1` for "checked" is unchanged, so an indeterminate box is not checked and `SetCheck` toggles it when Checked is wanted. Whether a
  toggle from Indeterminate lands on On or Off is decided by the read-back, not assumed.
- A diagnostic field leaks into a golden. The reader name is written next to the state as `checkStateVia`, and the evidence as
  `checkEvidence`. The `readOptions` data goes through the scrubber into the golden, so both keys are in the scrubber's drop list, as the
  other diagnostics are. The golden holds `kind: "checkBox"`, the label and `checkState`, as for a native check box, and the self test builds
  a golden from an option node that carries both keys and requires that neither text appears in it. The goldens already captured hold native
  check boxes only and are not changed (the plan version and hash are not touched).

## 36. A Windows Forms push button that is not a native push style, and the Show Contents dialog behind it

The first three list cases (`B/Pol_List_Plain`, `B/Pol_List_EmptyPrefix`, `B/Pol_List_Additive`) stopped with `option 'Plain entries:' is a unknown
(class WindowsForms10.Window.8.app.0.297b065_r40_ad1, id ''), not a list`. The recorded option had the panel id `flowLayoutPanelPlain entries:`
and the label, so the container and its STATIC were found, but the inner `Show...` button was not. `DiscoverOptions` took the button only when
its BS_ type was 0 (BS_PUSHBUTTON) or 1 (BS_DEFPUSHBUTTON). A Windows Forms `Button` whose `FlatStyle` is not `System` is created owner-drawn
(BS_OWNERDRAW, type 0xB), exactly as the check box of section 35, so the button was left out and the container got no kind. No list option was
ever captured before this run, so nothing after the button had run against classic either. This section teaches the driver to recognize the
button and records what was read in the list path and not changed. It changes no wait, timeout or sleep and no action sent to classic. A
native push button is found as before, and no UI Automation call is made for it.

Recognition: an inner BUTTON child of a container is the list's push button when its style is a native push style (as before), or when its
BS_ type is owner-draw and its UI Automation element (`AutomationElement.FromHandle`) has the control type Button or supports `InvokePattern`
and is not a check box (control type CheckBox or `TogglePattern`). The decision is a pure function over plain values in
`Driver/PushButtonRead.cs`, next to `CheckBoxRead`, covered by `gpedit selftest` before the code was written. The search for the push button
runs only when the container holds no COMBOBOX and no EDIT, so a container of another kind makes no extra UI Automation call.

What can go wrong and the guard of each:

- The owner-drawn push button is not recognized (this failure). The guard is the recognition above. If a case still fails, the option keeps
  `kind: unknown` and carries `pushEvidence` (untracked), one entry per inner BUTTON child: style, BS_ type, owner-draw flag, UI Automation
  control type, whether Invoke and Toggle patterns exist and what the judge decided, so the next run says which test failed.
- A check box is taken for a push button. An owner-drawn BUTTON is a push button only with UI Automation evidence, and a control type
  CheckBox or a toggle pattern always excludes it. A check box that is a direct child of the panel never reaches this search (section 35
  takes it first). A check box that is nested in a container with no combo or edit is not a list. It stays unknown. A radio button (control
  type RadioButton, no Invoke) is not a push button either, and a UI Automation read that throws is "no evidence".
- The Show Contents dialog is also Windows Forms. Every step of it was read for the same native-only assumptions (BM_GETCHECK, BS style,
  native LISTVIEW or EDIT, class name). `OpenShowContents`, `ReadGrid`, `WriteCell`, `ReadCell`, `ClearGrid` and `CloseShowContents` use UI
  Automation ids and patterns plus real key presses, and the dialog is classified by the automation id `ListBoxDialog` after the class
  prefix `WindowsForms10`. None of them tests a BS style or sends a BM_ message other than the BM_CLICK fallback, which a Forms button
  answers. They need no change. What cannot be proved without classic is listed below and stays as it is, and each of its failures carries
  the grid tree as evidence.
- A diagnostic field leaks into a golden. `pushEvidence` is written next to the option and reaches the scrubber through the `readOptions`
  data, so it is in the scrubber's drop list with the other diagnostics, and the self test builds a golden from an option node that carries
  it and requires that the text does not appear. The goldens already captured hold no list option and are not changed (the plan version and
  hash are not touched).

Read and not changed, because it cannot be proved without classic (for the next run to confirm from its evidence):

- `PolicyDialog.GridRowElements` expects the rows as UI Automation `DataItem` children of `dataGrid`, with a fallback to any child that has
  children. `PolicyDialog.CellValue` reads a cell through `ValuePattern` and falls back to its name. A Windows Forms `DataGridView` may expose
  other types. An empty `rows` in the result with the grid tree attached would show it. Answered by the run that found section 37: the rows
  are `Custom` elements, not `DataItem`, and the fallback took the column header row as a data row. Section 37 replaces both.
- `PolicyDialog.WriteCell` and `SelectGridRow` assume `ProbeUia.Focus(grid)` puts the keyboard focus in the grid, that Ctrl+Home goes to the
  first cell and that F2 opens the edit mode. The read-back of each cell fails the operation if not. Still open after section 37. Section 37
  fixes only the row and column numbers these two functions were given.
- `GpeditDriver.CancelWindow` falls back to `GetDlgItem(IDCANCEL)` and BM_CLICK, which assumes a native dialog id. For the Show Contents dialog
  the first route (`buttonCancel` by automation id, Invoke) is the one in use, and Escape follows. The fallback is harmless on a Forms dialog.
- `GpeditDriver.IsOpen` (`IsWindow` and `IsWindowVisible`) decides that the dialog closed. A `ShowDialog` form is hidden before it is
  destroyed, so it is read as closed from the hide.

## 37. The Show Contents grid as Windows Forms exposes it

The Show button now opens the dialog (section 36), and the three list cases (`B/Pol_List_Plain`, `B/Pol_List_EmptyPrefix`,
`B/Pol_List_Additive`) then failed in the steps that clear the list and write 2, 1 or 0 rows (operations 2, 4 and 6) with `the grid still has rows
after 200 delete passes`, thrown from `ClearGrid`. In operations 4 and 6 the grid held no entry at all, and the clear still looped. The failure
was in how the driver read the grid, not in the keys it sent. This section changes how rows and cells are told apart and how a row is selected
and deleted. It changes no wait, timeout or sleep, no plan version and no golden.

What the grid looks like (UI Automation control view, `evidence` of operation 2 in `artifacts/oracle/gpedit/matrix/B/Pol_List_Plain/result.json`):
`dataGrid` is a Windows Forms `DataGridView`, control type Table, patterns Grid and Table. Its children are not `DataItem` elements:

- `Top Row`, control type Custom, no patterns. Its children are two Header elements: `Top Left Header Cell` and the column header `Value`.
- `Row 0`, control type Custom. Its children are a Header named `Row 0` (patterns Invoke and Value, value empty), which is the row header, and
  an Edit named `Value Row 0, Not sorted.` (patterns Invoke, Value, GridItem, TableItem), which is the data cell. Its value was `e`.
- `Row 1`, the same shape, with the data cell value `(null)`. This is the new row of the grid (`AllowUserToAddRows`). `(null)` is the text
  Windows Forms accessibility gives a null cell value. Operations 4 and 6 held only `Top Row` and one row with `(null)`.

Why the clear never ended, from reading the code against that tree:

1. `GridRowElements` found no `DataItem` and fell back to every child that is not a Header and has children. `Top Row` is such a child, so
   the column header row counted as a data row. Its cells read the header names through the name fallback of `CellValue`, so it was never empty.
2. Every row's children include the row header, so the cell indexes of `ReadCell` were one more than the indexes `WriteCell` walks (Ctrl+Home
   goes to the first data cell, then Right).
3. `IsEmptyRow` took `(null)` as content, so the new row was a row to delete, and it cannot be deleted.
4. The row index used for the Down presses of `SelectGridRow` counted `Top Row`, so it pointed one row too far.

What the code does now. The classification is a pure function over plain values in `Driver/GridRead.cs`, covered by `gpedit selftest` before the
code was written. Input is, per child of the grid, its control type and name, and per cell of that child its control type, value (null when
there is no `ValuePattern`), name and whether it has `GridItemPattern`. Output:

- The header row is any child whose cells are all Header elements. It is never a data row. A child of control type ScrollBar or Edit (a cell
  editor that is open) is never a row. When any child is a `DataItem`, the `DataItem` children are the rows (the shape the code first assumed);
  otherwise every other child with at least one cell is a row.
- The data cells of a row are its non-Header cells, and when some of them have `GridItemPattern`, only those. The row header is never a data
  cell, so cell column 0 is the first value column for the read and the write alike.
- The text of a data cell is its value. A cell with `GridItemPattern` and no value reads as empty (its name is a description such as
  `Value Row 0, Not sorted.`, not the content). A cell without `GridItemPattern` falls back to its name, as `CellValue` did.
- A cell text of exactly `(null)`, null, or only white space is empty. The new row is the last data row when all its data cells are empty. It is
  not a row of the list: it is not reported, not counted and never deleted. An empty row that is followed by a filled row is a real row.
- A row is filled when any data cell is not empty. The row index used for the keyboard (Down presses after Ctrl+Home) is the index among the data
  rows, where the new row, when it exists, is the last.

`ClearGrid` deletes the last data row that is not the new row, one per pass, until none is left. A pass selects the row by invoking its row
header (the Windows Forms row header does select the whole row on its default action), then moves the keyboard focus to the grid and presses
Delete. If the row has no row header, or the invoke fails, or UI Automation says the row is not selected, the old keyboard route is used
(Ctrl+Home, Down to the row, Shift+Space, Delete). Before any key press the driver checks that no cell editor is open (a child of the grid with
control type Edit, or a focused Edit element that has a window handle) and presses Escape first if one is. The clear never types text. After each
pass the grid is read again. The pass log holds the route, whether an editor was closed, what UI Automation said about the selection, the data
row count and filled row count before and after, and the data cell values before and after. The clear stops with the pass log and the grid tree
as evidence when 3 passes in a row do not lower the data row count, and keeps 200 as the hard cap.

`ReadGrid` reports the data rows only (`rowCount`, `filledRows`, `rows`), with `(null)` shown as an empty string. `WriteCell` keeps the keyboard
route with the data row index, and `ReadCell` reads the same data cell and compares with `(null)` read as empty. Writing into the new row makes
a row, which is expected, and a new last row appears.

What can go wrong and the guard of each:

- The header row is counted as data. The guard is the rule that a row whose cells are all Header elements is the header row. The self test has
  the shape of operation 2 and an empty grid with only the header row.
- The row header shifts the cell columns. The guard is that Header cells are never data cells, in the read, the read-back and the evidence. The
  self test has a one column and a two column grid.
- The new row is deleted or counted. The guard is the placeholder rule, plus the stop after 3 passes without progress, so a new row that was not
  recognized ends the clear with evidence and not after 200 passes of key presses.
- A planned cell is the text `(null)`. That text would be read as empty. The guard is a matrix plan check (`gpedit selftest`) that no planned
  list cell is exactly `(null)`.
- Keys land in a cell editor. This is how the stray `e` in data row 0 during operation 2 might have come about (the dialog was cancelled, so
  nothing was saved), but the cause is not proved. The guards are the editor check with Escape before each key, no typing key in a clear, and
  the per pass log, so a stray character shows in `afterValues` of the pass in which it appeared.
- The row header invoke does not select the row. The grid has no selection pattern, and neither the rows nor the headers offer
  `SelectionItemPattern`, so the selection is most likely not observable and is recorded as `unknown`. The guard is the progress rule. A pass
  that does not delete anything is logged, and the third in a row stops the clear.
- A delete removes more than one row, or a different row than the last. The pass log has the values before and after, so it shows. The clear
  aims at an empty list, so it carries on from what it reads.
- The grid reads late after a delete, so a pass looks like no progress. The 300 ms pause after the key stays, the read comes after it, and the
  limit of 3 passes absorbs one late read.
- A diagnostic field leaks into a golden. The classification is written next to the grid as `gridRead` and the pass log as `clearPasses`. Both
  are in the scrubber's drop list, and the self test builds a golden from a node that carries both keys and requires that neither text appears.
  The goldens already captured hold no list option and are not changed.

Unproved, for the next real run to confirm from `result.json` (`ops[n].data` of the list steps, or `ops[n].evidence` on a failure):

- Whether the row header invoke selects the row and whether Delete then removes it: `clearPasses[].route`, `selection`, `rowsBefore` and
  `rowsAfter`.
- Whether the new row always reads `(null)` (or empty), also after rows were written: `gridRead.placeholder` and the row values.
- Whether the keyboard write into the new row creates the row and moves the new row down: the `cells` entries of `EditList` and the
  `afterEdit` grid.
- Where the stray `e` came from: `afterValues` of each clear pass.
- Whether a list with explicit values (name and value) exposes two data cells per row, and whether Right moves between them: not seen yet.
  The first run with `B/Pol_List_ExplicitValue` will show it in `gridRead.rows[].cells`.
- Whether classic keeps an empty entry as a row after OK and reopening, which decides whether a committed empty row can exist at all.

## 38. Classic refuses a list on OK of Show Contents

Section 37 made the list grid work, and the case `B/Pol_List_Plain` then passed operations 2 and 4 (Enabled, list cleared, 2 rows or 1 row
written, OK) with the cells read back. Operation 6, the `rows0` variant (Enabled, open Show Contents, clear the grid, write nothing, OK),
failed with `the Show Contents dialog was blocked by a message box on OK`. The evidence holds one message box (title "Group Policy", class
`#32770`, one OK button) that the driver answered with BM_CLICK. So classic does not accept an empty list from the Show Contents dialog: it says
so in a message box and keeps the dialog. That is classic behaviour, and recording it is the job of the oracle. The driver treated it as its
own failure and threw, which stopped the operation before the rest of the options and the presses of the policy dialog, so the files written
after the policy dialog's OK were never captured. Group E plans the same kind of list on purpose (`List_Plain_EmptyEntry`,
`List_Explicit_EmptyName`, `List_Explicit_EmptyValue`, `List_Explicit_NameOnly`) and Group C has `ListNone` with `ClearRows` and no rows, so
all of them would have failed the same way. This section turns the refusal into an observed outcome. It changes no wait before the press, no
timeout of the existing steps, no plan version and no golden.

What the driver does now, when OK of Show Contents brings a message box:

1. The message box is captured and answered exactly as before (`CloseShowContents` and `AnswerNewMessageBoxes`, the same path a press of the
   policy dialog takes). The fields of the box are the ones every box carries (`title`, `class`, `kind`, `texts`, `buttons`, `answeredWith`,
   `answeredVia`, `answerRequested`, `answerFallback`, plus the untracked `uiaTexts`, `uiaButtons` and `screenshot`).
2. The driver then reads whether the Show Contents dialog is still open. It polls for up to 1.5 s in steps of 100 ms, because a dialog that
   classic accepts after the box may need a moment to go away, and a read at once could call that dialog refused. The wait is new and only on
   this path. No existing wait is shortened.
3. When the dialog is still open, the list was refused. The driver cancels the dialog with the existing `CancelWindow` route and reads that
   it closed. The edit operation then continues as for any other option: the other options, the read after the sets, and the planned presses of
   the policy dialog, so the files written after the OK of the policy dialog are captured like every other step.
4. When the dialog is gone after the box, classic took the list and only warned. That is not a refusal. The set entry says the box appeared
   and that the dialog closed, and nothing is cancelled.

How it shows in `result.json` and in a golden: the set entry of the option (`ops[n].data.sets[m]`) has `close` (as before, with `button`,
`messageBoxes` and `blockedByMessageBox`) and, new, `okMessageBox: true`, `refused` (true or false), `dialogOpenAfterBox` (true or false) and,
only for a refusal, `cancelled` (the dialog closed after Cancel, true or false) and `cancelClosedBy`. The golden keeps `okMessageBox`, `refused`,
`dialogOpenAfterBox`, `cancelled` and the message boxes. These are classic behaviour: that it refused, which box it showed and whether it kept
the dialog. The text of the box is not in the golden. It goes through the same scrubber as the message boxes of a press: `title` and `texts`
are not BWC text and become the hash form `#` plus 12 hex digits, the button texts and the driver tokens (`OK`, `#32770`, `MessageBox`,
`BM_CLICK`) stay as they are, and `uiaTexts`, `uiaButtons` and `screenshot` are dropped. `cancelClosedBy` names the route that closed the
dialog and is a diagnostic, like `closedBy`, so it is in the scrubber's drop list. The same hash for the same text lets two goldens show that two
refusals showed the same box, and no Microsoft sentence is learned.

What can go wrong and the guard of each:

- A refusal is taken for a failure (this failure). The guard is the rule above. A message box on OK of Show Contents is an observed outcome and
  `EditList` no longer throws for it.
- The dialog closed after the box and the list was taken as refused. The guard is the 1.5 s poll before the decision, and `refused` is true only
  when the dialog is still open after it. A dialog that closes later than that would be cancelled by the driver that found it open, so the
  `cancelled` field says what really happened (`already closed` is a closed dialog). This cannot be told apart from a real refusal without
  classic. The next run reads it from `dialogOpenAfterBox`.
- The dialog does not close after Cancel. That is a driver failure, as before: the operation throws with the box, the Cancel route and the
  windows that are open as evidence. The `finally` of `EditList` still tries Cancel once more, and the cleanup after the operation closes what is left.
- A second message box comes after the first, or while the dialog is cancelled. The driver answers boxes that exist when it looks, in every
  step of the poll for the open state and again after Cancel. A box that stops Cancel from closing the dialog makes the dialog stay open, which is the failure above
  with the windows as evidence. A box found after Cancel is recorded in `messageBoxesAfterCancel`.
- A message box at any other moment of the list path (opening Show Contents, reading or writing the grid, the clear) is not a refusal. It
  stays a failure with the box as evidence, as before.
- A diagnostic or Microsoft text reaches a golden. The box takes the path of every box (section 12 and the scrubber). The self test builds the
  set entry of a refused list with a box whose title and text carry a marker and requires that the marker is not in the cleaned data, that the
  box is in the cleaned data in its hash form, and that `refused`, `dialogOpenAfterBox` and `cancelled` are. It also checks that
  `cancelClosedBy` is dropped.
- A refusal hides a driver problem. The set entry holds `before`, `clearPasses`, `cells` and `afterEdit` as for a list that was accepted, so a
  grid that was not written as planned is still caught by the read-back of every cell before OK. A refusal does not weaken that check.

Unproved, for the next real run to confirm from `result.json`:

- That the empty-list message box is the only box on OK of an empty list, and that the dialog stays open after it (`dialogOpenAfterBox`).
- That Cancel closes the Show Contents dialog after the box (`cancelled`, `cancelClosedBy`).
- What classic holds in the file after the policy dialog's OK when the list was refused, and what the read after the sets shows
  (`afterSet`): the list as before the edit, or empty.
- Whether the Group E cases (empty entry, empty name, empty value, name only) are refused by the same box or accepted, and whether any of them
  shows a second box.

## 39. A Show Contents read after the edit loses the list

After section 38 the case `B/Pol_List_Plain` ran without a driver failure, and the result was still wrong. Operations 2 and 4 (Enabled, 2 rows
or 1 row written into the grid, every cell read back, OK of Show Contents closed the dialog) both ended with the same message box on OK of the
policy dialog as operation 0, the Enabled step with an empty list: classic said that no entries were added, kept the policy dialog, and the
driver cancelled it at the end. No file changed in any Enabled step. A golden made from that run says that classic cannot save a list, which is
false: a person who types the rows and presses OK twice gets them saved.

What the driver did between the OK of Show Contents and the OK of the policy dialog, and a person does not do: the read after the sets
(`afterSet`) opened Show Contents a second time to read the list, and closed it with Cancel. The read showed both rows, and so did the read
after the refusal (`dialogStillOpen`). The best explanation is that classic keeps the grid of the dialog but takes the list it saves from the
last close of the dialog, so a Cancel there leaves it with no list. Another explanation is that the OK of Show Contents through UI Automation
never handed the rows over. The rows in the second read do not tell these two apart, because the dialog may be built from its own grid and not
from the list of the policy dialog.

What the driver does now: the reads of the policy dialog after the sets (`afterSet`) and after a press that left the dialog open
(`dialogStillOpen`) no longer open Show Contents. The list option there records its button as before (text, enabled, the push evidence) and no
`contents`. What the grid held is in the set entry of the list (`before`, `cells`, `afterEdit`), read while the edit was open and before its
OK. The read before the sets (`readBefore`) still opens Show Contents and cancels it: it comes before any edit of the case, and an edit of the
list opens the dialog again and closes it with OK. No wait, timeout or plan version changes. No tracked golden holds a list case yet.

What can go wrong and the guard of each:

- The cause is the second explanation, so this change does not help. The next run of `B/Pol_List_Plain` shows it: operations 2 and 4 still
  bring the message box and no file changes. Then the next fix is the route of the OK of Show Contents (a real mouse click on it, as a person
  does), and this section says so.
- A case with a list that is already filled (from an earlier step or a start file) is read before the sets, cancelled, and then saved with no
  edit of the list. If the first explanation is right, classic would then save that policy with no list or refuse it, and the golden would
  record a Cancel that the case never planned. The next run decides the explanation. If it is the first, the read before the sets must close
  a filled list with OK, or skip it, before groups C and D run, and this section is extended with that rule.
- A golden loses the list contents of `afterSet`. No tracked golden is a list case, so no tracked golden changes. The contents are in the set
  entry, which the golden keeps.

Unproved, for the next real run to confirm from `result.json`:

- That operations 2 and 4 of `B/Pol_List_Plain` now press OK with no message box and change `Machine\Registry.pol`, `gpt.ini` and
  `Machine\comment.cmtx`, and that the file holds the rows that were typed.
- That operation 0 (Enabled, empty list) still shows the message box. That would make it classic behaviour for an empty list, and not a
  side effect of the read.

Result of the next run (case `B/Pol_List_Plain` alone, then the long capture): the first explanation is right. In all ten list cases of
group B, the 2-row and 1-row steps now press OK with no message box and write the files, and the Enabled step with an empty list still shows
the box, so that box is classic behaviour. The `rows0` step (Show Contents refused, then cancelled) writes nothing and shows no box: the list
classic saves is the one loaded when the policy dialog opened (1 row), so nothing changed. Classic's Cancel of Show Contents therefore puts the
list back to its value from the file, not to the value of the last OK. The read before the sets stays as it is: it cancels before any edit,
when the two values are the same. BWC may differ here on purpose; the audit decides.

## 40. All Settings makes every UI Automation search over the main window take minutes

The long capture showed one pattern in every F case that visits `All Settings` (about 3400 policies). The node itself is not slow: `selectNode`
takes 11 to 12 s and `pressKey` on its list takes 11 s (`F/Keys_ListAllSettings`). The steps that ask UI Automation for one element of the
whole main window take minutes. In `F/AllSettings_Computer` `readStatus` takes 422 s, `readList` 427 s and `readLayout` 439 s. In
`F/Toolbar_States` the `readLayout` at All Settings takes 492 s and the case took 3123 s. In `F/Tabs_Extended_Standard` the `selectTab` at
All Settings takes 905 s (two searches), and the next `selectTab` was killed by the case watchdog (`MatrixRunner.cs`, "taking too long,
closing classic") after 651 s, so the case failed with `ElementNotAvailableException`. The same calls on a node of the fixture take 4 to 13 s.

Cause. Three helpers search `TreeScope.Descendants` from the main window for one control type: `ReadStatus` (StatusBar, `GpeditDriver.List.cs`),
`DistinctTabs` (TabItem, called twice by `SelectTab`, once more through `ReadTabs`) and `ReadLayout` (ToolBar, `GpeditDriver.Visual.cs`).
`readList` reaches the first of them through `ReadStatus`. A search from the main window walks into the `SysListView32` and asks for every row
and every cell, across the process boundary, so its cost grows with the row count. The searches that start at the list itself (the header
lookups of `ListCandidates`, `SortColumn` and `ReadLayout`) are not the cause: `readList` minus `readStatus` is 5 s. The console tree lookups
were already moved to window handles in section 34.

An observation decides how far the change goes. The old search returns different things at different nodes. In `F/Toolbar_States` the
`readLayout` at the placeholder node `Software Settings` takes 373 ms and finds the two toolbars (`uiaToolbars`). The same call at the root
node, at Administrative Templates, at a category, with a row selected and at All Settings takes 9 to 13 s (492 s at All Settings) and finds
none. The tracked goldens of the F cases hold that empty list. That is a property of the old search at a node with a visible list, and it
cannot be told apart from classic without a run. So the old search stays exactly as it was wherever it is cheap.

What the driver does now. Before each of the three helpers it counts the rows of the visible, on-screen `SysListView32` windows below the main
window with `LVM_GETITEMCOUNT` (Win32 only, no UI Automation) and takes the largest. From 1000 rows it takes the narrow route. Below that, or
when the count cannot be read, it runs the old search with the same calls and the same results as before. The narrow route is:

- Status bar: the visible `msctls_statusbar32` windows below the main window, each taken with `AutomationElement.FromHandle`, then the same
  descendants search below the bar as before (`uiaTexts`).
- Tabs: the visible `AMCCustomTab` windows (the tab strip of the view, as the probe showed it), each taken with `FromHandle`, then the TabItem
  descendants below it. `DistinctTabs` still removes repeated items by name and rectangle.
- Toolbars: the visible `ToolbarWindow32` windows, each taken with `FromHandle` and kept when the element is a ToolBar. The button search
  below a bar is unchanged.
- Column headers of a list with 1000 rows or more: the Header children of the list, then the HeaderItem children of each. When that finds
  nothing, the old search below the list runs.
- `readList` of such a list with `namesOnly` and a `maxRows` below the row count reads the first `maxRows` rows with a tree walker that
  stops when it has them (name and control type cached) and takes `rowCount` from `LVM_GETITEMCOUNT`. Until now it read all 3400 names and kept 60.
  A list below 1000 rows, and a read with cells, are read as before.

`result.json` says which route ran in `uiaRoute` (`hwnd` or `search`) on the status object, on the tab list and on the layout. The key is a
diagnostic and is on the scrubber's drop list, like `closedBy`. No wait, timeout or sleep changes, and nothing is sent to classic. Every node
below 1000 rows is read by the same calls as before.

What can go wrong and the guard of each:

- A narrow lookup finds nothing because a class name differs on another system. Tabs then come back empty and `selectTab` fails at once with
  `no tab 'Standard'. Tabs: ` and an empty list, which is a cheap and visible failure and not a 15 minute wait. The status text does not depend
  on it: the native status bar read comes first and is unchanged. Toolbars would be an empty list, as the old search gave.
- A small list takes the narrow route, or a big one the slow route. The route follows the row count only, and `uiaRoute` records it. A row count
  that cannot be read (the list does not answer within 5 s) takes the old search, which is exact but slow, and `uiaRoute` says `search`.
- A golden changes. Only the layout of a node with 1000 rows or more can change: `uiaToolbars` may now hold the two toolbars where the old
  search found none. The tracked F goldens that reach this are `F/Toolbar_States` (the operation at All Settings) and `F/AllSettings_Computer`
  and `_User` (the optional `readLayout`). A rerun reports them as changed against the earlier golden, in that one key. Every other node, and every
  other key of those layouts, is read by the same calls as before. The status, tab, header and row data of All Settings is untracked or dropped.
- The walker of the capped read returns fewer rows than asked. `truncated` stays `rowCount > rows.Count`, so a short read would show as
  `truncated: true` with fewer rows than `maxRows`, in the untracked result.

The self test checks the route decision (the threshold, a hidden or off-screen list not counted, no list and an unreadable count taking the old
search) and the choice of the windows of each lookup on made-up window lists.

Unproved, for the next real run to confirm from `result.json`:

- That `readStatus`, `readList` and `readLayout` at All Settings take seconds and not minutes, with `uiaRoute` `hwnd` on them and `search` on
  every smaller node.
- That `selectTab` finds `Extended` and `Standard` at All Settings (`F/Tabs_Extended_Standard` passes both All Settings operations).
- What `uiaToolbars` holds at All Settings now.

## 41. A column header scrolled out of the list cannot be clicked

`F/Sort_Columns` clicked `Setting` and `State` three times each (by a guarded click, the headers have no Invoke) and then failed three times on `Comment` with
`click refused: 'Comment' has no usable bounding rectangle`. The screenshot of operation 12 (`012-sortColumn.png`) shows classic at its
small default size: the description pane takes the left half of the view, the result list is narrow, the `State` column is cut at the right edge
and a horizontal scroll bar sits under the list. The `Comment` header is beyond the right edge, so UI Automation reports it with an empty
rectangle, and the click guard (section 11) refuses a click it cannot place. The window must not be resized or maximized, because other F
goldens record its layout.

What the driver does now. Before a header click it reads the rectangle of the header item. The header is clickable when the rectangle is finite,
at least 1 by 1, and its centre is inside the list window. When it is not, the driver brings the header into view by the first route that
works, and reads the rectangle again after each route:

1. `ScrollItemPattern.ScrollIntoView` on the header item, when the item offers it.
2. `ScrollPattern` of the list, up to 20 large steps to the right, when the list scrolls horizontally.
3. `LVM_SCROLL` with a distance computed from the column widths (`LVM_GETCOLUMNWIDTH`), the width of the list client area and the current position
   (`GetScrollPos`): the smallest scroll that shows the column, or its left edge when the column is wider than the list. Up to 8 messages.

The click is then the same as before: Invoke when the item has it, the guarded click otherwise. A header that is still not clickable after the
last route fails the operation, with the rectangle and the routes tried as evidence. The result gets `scrolledBy` (`none needed`, `ScrollItem`,
`ScrollPattern` or `LVM_SCROLL`) and `scrollSteps`. Both are diagnostics and are on the scrubber's drop list. A header that needs no scroll
(`Setting`, `State`, and every list that fits) costs one rectangle read, so its click and the goldens do not change.

What can go wrong and the guard of each:

- A scroll moves the list for the later steps. Later reads take rows by name, not by position, and the sort order is read after each click as before.
- A scroll shows the header only partly and the click lands next to it. The centre of the rectangle must be inside the list window, and the click
  guard checks the window under the point.
- A route scrolls the wrong way or by another step. Every route is followed by a new read of the rectangle, and the message route reads
  `GetScrollPos` each time and does not rely on a remembered position.
- The window is resized to make room. Nothing in this change touches the size or the layout of the window.

The self test checks the scroll distance (a column left of the view, inside it, partly right of it, wider than the view, at the start) and the
clickable test (empty, infinite, outside and inside rectangles).

Unproved, for the next real run to confirm from `result.json`:

- Which route brought `Comment` into view (`scrolledBy`), and that its three clicks give `sortAfter` up, down and up in the third column.

## 42. A node that the filter hides is a result, not a failure

`F/Filter_Options` operation 26 (`setFilter` with Managed set to No) worked, and every operation after it failed (44 optional failures). Classic's
tree under `BWC Oracle` then shows only `Basics`, because the filter is on and every Sort cases policy is managed. Each later operation selects
`Sort cases` first (`EnsureNode`), so `filterOn`, `readList`, `clearFilter` and the next `setFilter` all failed with
`no tree node 'Sort cases' under ...BWC Oracle. Children: [Basics]`, and the filter, which stays on in classic, was never cleared. A node that
disappears under a filter is classic behaviour, and recording it is the job of the oracle.

What the driver does now. `WaitChild` attaches what it knew to its error: the segment that is missing, the path of the parent that was found
(empty when the parent is the root configuration node) and the children that were there. A missing root configuration node carries none of it.
The operations that name a node (`readList`, `setFilter`, `filterOn`, `clearFilter`) act on that detail when selecting the node fails with it:

1. The driver reads whether Filter On is checked (the check mark of View > Filter On, read from the menu without activating it). When it is not
   checked, or cannot be read, the original error stands: a missing node with no filter is a failure.
2. `readList` returns, as its data, `nodeHidden: true`, `filterOn: true`, the missing `segment`, the `parentPath`, the `parentChildren` with
   `parentChildCount`, and `selectedPath`, the node that is selected now. It reads no list and selects nothing.
3. `setFilter`, `filterOn` and `clearFilter` select the nearest visible ancestor (the parent, then its parent, up to the root configuration node, with
   the same selection every operation uses) and run from there as they always do. Their data gets the same description as `hiddenNode`, with
   `selectedInstead`, the ancestor that was selected.

The next operation that names `Sort cases` after the filter was cleared selects it as usual. The operations of the plan and `PlanVersion` (2) do not change.

How it shows in a golden. The names are the fixture's (`BWC Oracle`, `Basics`, `Sort cases`) and are kept. Any other name would be a hash.
No timing, route or handle is in the description. A node that is visible gives the data it gave before, with no new key.

What can go wrong and the guard of each:

- A mistyped path under a filter is recorded as hidden. The description holds the parent's children, so a typo shows as a name that is not among
  them, and the paths of the matrix come from the fixture and are not typed.
- The filter is on and the node is missing for another reason (classic did not fill it). The description is the same, and the screenshot of the next
  operation shows the rest. A missing node with the filter off still fails as before.
- The ancestor cannot be selected either. Each ancestor that is missing too moves one level up. When none can be selected, the error of the first
  missing node is thrown.
- Filter Options is grayed at the ancestor. `ActivateMenuItem` fails as it does for any grayed item, with the menu as evidence. The ancestor is
  a node of Administrative Templates, where the item is enabled when the case selects `Sort cases`, which is its child.
- The menu read that decides leaves a menu open. `DumpTopMenu` closes menus in its `finally` block, as every menu read does.
- A hidden node changes a tracked golden of a visible node. It cannot: the new keys appear only when selecting the node failed.

The self test checks the description (its fields, the ancestor order, no description for a missing root node, none when the filter is off) and that
the scrubber keeps the fixture names and hashes the others.

Unproved, for the next real run to confirm from `result.json`:

- That `F/Filter_Options` operations 27 to 29 now return the hidden description (`readList`) or run from `BWC Oracle` (`filterOn`, `clearFilter`),
  and that operation 30 selects `Sort cases` again after the clear.
- That the same happens for the later variants that hide the node, and for none of the first four.

## 43. The numeric edit of a number control never passes the focus check

`E/Decimal_TypedAboveMax` and `E/Decimal_TypedLetters` (the cases that type into a number control) failed in operation 0 with
`the control of 'Number from 10 to 500:' did not get the keyboard focus, so nothing was typed`. The evidence says the control is kind `spinner`,
class `WindowsForms10.EDIT.app.0.297b065_r40_ad1`, enabled, text `10`. The screenshot `left-open-*.png` shows the dialog with Enabled selected and
a text caret inside the edit of the number control. So the focus did land, and the check failed.

Cause (`PolicyDialog.Options.cs`, `TypeInto` and `RequireTypingTarget`). Both compare the native window handle of
`AutomationElement.FocusedElement` with the handle of the edit. A Windows Forms number control is a host window with an inner EDIT, and the inner
edit has no UI Automation element in the control view (section 6), so UI Automation reports the focus on another element whose handle is not the
edit. The comparison was made on the wrong layer. Typed keys go to the window that has the real keyboard focus, and that is what a person's
typing needs.

What the driver does now. The focus is accepted when one of these holds: UI Automation reports the handle of the control (as before); the window
that has the keyboard focus in the thread of the dialog (`GetGUIThreadInfo`, `hwndFocus`) is the control; or that window is a child of the control
(the edit inside a combo box). Nothing else is accepted. Focus on the host of the number control, on the dialog or on another control is still
refused. Both places use it: before the first key, and after each typed Enter and at the end of a multi-line text. The keys are the same real key
presses as before. The text of the error is unchanged and now adds the two readings (UI Automation handle and keyboard focus handle), so a
refusal can be read from the result.

What can go wrong and the guard of each:

- A real loss of focus is accepted. The Win32 reading is the window that receives the keys, so it is stronger than the UI Automation reading, and the
  test is the exact handle or a child of the control. For a control that must be cleared, select all and delete is still checked by reading the text.
- A control that passed before now fails. The old condition is still one of the three, so nothing that passed can fail.
- The thread of the dialog cannot be read. The Win32 reading then says nothing and the UI Automation reading decides, as before.
- The typed text lands in the host and not in the inner edit. The text of the edit is read back after the typing and after the commit
  (`immediate`, `observed`), so a text that did not arrive shows in the result.

The self test checks the decision on made-up handles: the control, a child of it, the host of it, nothing, and the old condition alone.

Unproved, for the next real run to confirm from `result.json`:

- That `E/Decimal_TypedAboveMax` types `9999` and `E/Decimal_TypedLetters` types `12ab` (`typed: true`, `via: keys`), and what the edit holds
  after the commit (`immediate`, `observed`, `clamped`). That is what classic does with typed letters and with a typed value above the maximum.
- Whether the number control takes letters at all. It may drop them while typing, which would show as an `immediate` with no letters.

## 44. OK that leaves the dialog open with no message box is an outcome

`E/LongDecimal_Required_Empty` operation 4 (a required long decimal given `x`, then OK) failed with `the dialog stayed open 15 seconds after OK`.
The screenshot `stuck-OK.png` shows the policy dialog open after OK with the letter in the field and no message box. Operation 2 of the same case
(the empty text) showed a box and was recorded as a refusal. So classic refuses a bad value in two ways, with a box and without one, and the second is
classic behaviour. `Press` treated it as a driver failure (`closes && !closed && boxes.Count == 0`) and threw before the file settle and before
`presses` was filled, so the read of the open dialog and the Cancel at the end had no data to go with.

What the driver does now. After the 15 s wait for OK, which is not shortened, when the dialog is still open and no message box came, the press
records the outcome: `stayedOpen: true`, next to `closed: false` and `messageBoxes: []` that it already has, and the screenshot as before. The press
goes on to the file settle that every press does. Then it decides on the settled files:

- No file changed: the outcome stands. The operation continues as for a refusal with a box: the read of the open dialog (`dialogStillOpen`) and the
  Cancel at the end (`cancelledAtEnd`).
- A file changed while the dialog stayed open: classic wrote something and kept the dialog, which a refusal does not explain. The press throws
  `the dialog stayed open after OK but files changed`, with the press as evidence.
- The dialog is still open after the Cancel at the end: unchanged, a failure, as for any Cancel.

Cancel still needs the dialog to close, and a press that is not OK is judged as before. `Apply` leaves the dialog open on purpose and never failed
here, so it does not get the key. `stayedOpen` is classic behaviour (that it kept the dialog and wrote nothing) and reaches the golden next to `closed`
and `messageBoxes`. The screenshot name is a diagnostic as before.

What can go wrong and the guard of each:

- A slow close is taken as a stay. The wait is the old 15 s. If the dialog closes later, the read of the open dialog is skipped (it only runs when
  the dialog is open) and the Cancel at the end is not pressed, so a late close shows as a `stayedOpen: true` entry without `dialogStillOpen`. The
  next run reads it.
- A write that comes after the settle is missed. The settle of this press is the settle of every OK: the file grace of 1200 ms and then the live registry
  wait, the same as for an OK that closes.
- A stay with a changed file passes as an outcome. The decision uses `changed` of the settle and throws for it.
- The Cancel at the end does not close the dialog. It throws as before, and the `finally` cleanup of the operation closes what is left.

The self test checks the decision for each combination of button, closed, message boxes and changed files: closed, a box, stayed open with no
change, stayed open with a change, and a Cancel that does not close.

Unproved, for the next real run to confirm from `result.json`:

- That operation 4 of `E/LongDecimal_Required_Empty` records `stayedOpen: true`, no file change, `dialogStillOpen` and a Cancel that closes the dialog.
- Whether other Group E cases stay open without a box (a letter in the other number controls). This change records them the same way.

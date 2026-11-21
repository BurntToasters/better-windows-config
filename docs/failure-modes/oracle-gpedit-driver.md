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

# Details list failure modes (report view, package UF6)

Scope: `src/Bwc.UI/List` (`DetailsList`, `DetailsRowElement`, `DetailsHeaderPanel`,
`DetailsColumn`, `DetailsRow`) and the E2E tests in `tests/Bwc.E2E/List/ListBehaviorTests.cs`
and `tests/Bwc.E2E/Controls/ListTests.cs`. The oracle is the classic Registry Editor in report
view, captured read-only in `docs/parity/oracle/sort-order.json`,
`regedit-row-metrics.json`, and `regedit-selection-colors.json`.

This file was written before the code. Each section lists the ways a behavior can fail, then the
design choice that prevents it, then the E2E check that would catch it.

## 1. Sort order

The oracle order is produced by the native collation, not by ordinal comparison. Ordinal would put
`[y]` after `0z` and `~t` after `z10`. A .NET culture comparison puts `_x` before `(x)` and `#h`.
Only `CompareStringW(LOCALE_USER_DEFAULT, NORM_IGNORECASE)` reproduces the oracle: `#h (x) [y] _x
~t 0z 1 10 2 a b a01 a1 a10 a1b a2 A3 ab a-b alpha b B2 x.y x_y z10 z9 Zeta`. The registry folds
names case-insensitively, so `Alpha` and `ALPHA` collapse into `alpha`.

Failure modes:

- Ordinal or culture comparison used instead of the native one. Punctuation and `a-b` land in the
  wrong place. Prevented by calling `CompareStringW` through P/Invoke.
- The default value (`(Default)` as a display name, empty name in the registry) sorts by its
  text, so it lands after `#h`. Prevented by a key of the empty string for the `(Default)` row.
- Digit runs compared as numbers (`1, 2, 10`). Prevented: the native text collation is used.
- Header click reorders the rows. The oracle says `changed: false` for both Name and Type. The
  header click handler does nothing and the sort indicator is gone.
- Large lists (200,000 values) sort on the UI thread and freeze input. Lists above 10,000 rows
  sort on the thread pool and apply on the UI thread, guarded by a version number so a stale sort
  never replaces a newer list.
- A row set that changes while a background sort runs is replaced by the stale result. The version
  check discards it, and `Unloaded` bumps the version.

E2E: `order equals the oracle` reads the realized rows through UIA while stepping with Home and
Down, and compares them with `classicOrder` from `sort-order.json`. `header click does not
reorder` clicks Name and Type and compares the order again.

## 2. Selection and focus

Selection (the set of selected indexes) and focus (the index that owns the keyboard focus
rectangle) are separate. Classic lets the focused row be unselected, so the focus rectangle can sit
on a row that is not highlighted.

Failure modes:

- A focus move with Ctrl+arrow changes the selection. Ctrl+arrow moves focus only.
- Ctrl+Space does nothing, or toggles the anchor instead of the focused row. Ctrl+Space toggles the
  focused row and makes it the anchor.
- Shift+arrow starts a new range from the focus instead of the anchor. Shift+arrow extends from the
  anchor and replaces the selection. Shift+Ctrl+arrow adds the range to the existing selection.
- Ctrl+A selects all, but leaves focus on a stale index. Ctrl+A keeps the focus.
- The focus rectangle is drawn on every selected row, or on none when the selection has two rows.
  The rectangle belongs to the focused row only, and it is drawn only while the list has keyboard
  focus. The row brush is local to the list, so the shell's pane gating (`SelectedRows.Count == 1`)
  cannot hide it. Selected rows use the selection text brush and unselected rows use the window
  text brush.
- The focus rectangle stays visible while the rename box has focus. The list loses focus when
  the rename box takes it, so the rectangle hides there.
- A focus index that points past the end of a shorter list after a `SetRows` call. `SetRows`
  resets focus and anchor.

E2E: `Ctrl+arrow moves focus only` checks that Ctrl+Down twice and then Ctrl+Space selects the
row two below the original selection, leaving the original selected.

## 3. Mouse selection

- A right press selects a row on press (`PointerPressed` fires before `RightTapped`). A right
  click on a selected row in a multi-selection then collapses the selection. Prevented: press
  handling reads only the left button; right-click selection is applied in `RightTapped`, and
  only when the clicked row is not already selected.
- A right click on an unselected row keeps the old selection. It selects only the clicked row.
- A left click with Ctrl toggles, with Shift extends, and a plain left click selects one row.
- Clicking the empty area below the last row does nothing. Classic clears the selection there.
  The empty area is a hit-testable panel under the rows, and a left press there clears the
  selection and gives the list focus. Scrollbar presses are excluded because the scroll viewer
  handles them.
- A press on a row marked handled stops the list from taking focus. The row handler calls
  `Focus` before marking the event handled.

E2E: `right-click keeps multi-selection` (right-click a selected row, count stays 2),
`right-click on unselected row selects only it`, and `empty-area click clears selection`.

### 3a. Mouse presses that never reach the row or the grip (baseline E2E)

Baseline failures seen in one full run, before any change to the list:

- A Ctrl+click is treated as a plain click when Control is not down for the press. `list.multi.ctrl-click`
  counted 2 after row 1, then Ctrl+click on rows 2 and 3. The result {2, 3} means the first Ctrl+click
  acted as a plain click, or the row 2 press was lost. `list.right-click.keeps-multi-selection` counted 2
  with the same set, so the right click kept the rows that were selected. Cause not yet confirmed: the
  press reads Control with `IsKeyDown` (the key state of the UI thread), and the harness holds Control
  with raw input before a UIA click. Unconfirmed: whether the Control key-down reached the foreground window.
- An empty-area press leaves the selection alone when the click before it selected nothing. The check
  `list.empty-area.clears-selection` read 0 selected before the click, so the setup click never selected row 0.
- The divider press never reaches the grip. `list.divider.trace` read `Presses=0;Moves=0;Releases=0` after a
  +60 DIP drag, so `OnDividerPressed` never ran. The UI Automation hit test still reported the `Column
  divider` separator at the drag point. A probe with a clean foreground (WindowFromPoint and the foreground
  window both the Registry Editor) read `Presses=0` after a press on the grip, so the grip never got
  `PointerPressed`, although UIA places it there. Cause not yet found. A second probe run had Windows Terminal
  in the foreground at the grip point, so that run cannot be used. Foreign windows from other test runs must
  be ruled out before a mouse check is judged.

## 4. Type-ahead

Classic list views use incremental search: typed characters form a prefix buffer that resets after
about one second of no typing. A repeated single character cycles through the values that start
with it. A longer prefix selects the first value that starts with it, starting at the focused
value.

Failure modes:

- The buffer never resets, so a second search after a pause continues the first. The timeout is
  1000 ms measured with `Environment.TickCount64`.
- Control characters (Enter, Tab, Escape, Backspace) or Ctrl/Alt combinations feed the buffer.
  Only printable characters are used, and a held Ctrl or Alt skips the search.
- Typing a repeated character (`aaa`) searches from the focused value and selects the same
  value again. Repeated characters cycle to the next value after the focus.
- A prefix search starts after the focus and skips the current value. A prefix search starts at
  the focused value, so typing `al` on `alpha` keeps `alpha`.
- No match leaves the selection in place; a match replaces it with a single selection and reveals
  the row.
- Type-ahead while the rename box is open is consumed by the rename box. The list ignores
  characters while `_renaming` is true.
- Matching is case-insensitive. `A` matches `alpha`. The match uses `OrdinalIgnoreCase` on the
  displayed name, so `(Default)` matches `(`.
- The space character starts or extends a search, so `a b` can be typed. Ctrl+Space is a toggle,
  not a search character.

E2E: `type-ahead prefix and cycling` types `a` repeatedly and reads the selected names, then types
`al` and checks `alpha`.

## 5. Column headers and dividers

- A header click sorts or changes an arrow. Header clicks are a no-op.
- The divider grip is narrower than the pointer target, so a resize drag misses. The grip is 4
  DIP wide, centered on the divider.
- A drag moves the width by the wrong amount because the start position is taken in the wrong
  space. The start is recorded in the list's coordinates when the grip is pressed.
- A drag under the 40 DIP minimum produces a negative or zero width. The width is clamped to 40.
- The grip loses the drag when the pointer leaves it. The grip captures the pointer on press.
- A double-click on a divider is seen as two taps and not as a double tap. The grip handles
  `DoubleTapped`, and it marks the event handled so the drag path does not run.
- Auto-size measures only a bounded sample (200 rows plus the realized rows) so a large list does
  not stall input. A new width below the minimum is clamped.
- A divider double-click after the last column resize changes the wrong column. The column index is
  bound when the grip is built.

E2E: `divider drag resizes` drags the Name divider by 60 DIP and checks that the Name header title
grew by the same amount. `divider double-click auto-sizes` narrows the Name column, double-clicks
the divider, and checks that the width grew to fit the longest seeded name.

## 6. UI Automation

- The list has no Grid or Table pattern, so Narrator and the UIA test cannot read rows and columns.
  `DetailsList` returns a custom peer with a Table pattern (and therefore Grid) that reports
  `RowCount` as the number of rows and `ColumnCount` as the number of columns.
- The row and column counts go stale after a `SetRows` call. The counts are read from the live
  list on each call.
- `GetColumnHeaders` returns an empty array. The header titles have a peer with the HeaderItem
  control type, and the array holds them in column order.
- A row exposes the wrong control type. Rows stay DataItem with SelectionItem, ScrollItem, and
  GridItem.
- The automation ids used by tests change: `ListControl`, `ListHeader`, `ListBody`, `ListRow_N`,
  `Header<Key>`, and `ListRenameBox` stay as they are.
- A realized row reports a stale index after a sort or scroll. Row peers read the index from the
  element, which the repeater updates on recycle.
- A UIA `GetItem` for an unrealized row throws. It returns null for rows that are not realized.

E2E: `UIA Grid row and column counts` reads the Grid and Table patterns from the list control.

## 7. Geometry and theme (kept)

- The icon and row geometry from the previous package stay: 18 DIP rows, a 16 DIP icon at x = 2,
  and the name at x = 18. Changing them fails the existing row-height check in
  `Controls/ListTests.cs`.
- Theme brushes come from `Bwc.UI.Theme` and are not changed here. A focus rectangle that uses
  a brush of its own is the only new brush, and it is created in the list folder.

## Test plan summary

Every behavior above has an E2E check in `tests/Bwc.E2E/List/ListBehaviorTests.cs` that drives
the shipped Registry Editor with real keyboard and mouse input against a seeded HKCU sandbox. The
checks write screenshots to `artifacts/e2e/visual/list/`. The one pure function (the comparer) is
verified through the order check, not in isolation.

## Rename commit on lost focus (regression)

### Clicking another row cancels the rename

Failure mode: the list rename box cancelled when it lost focus, so a user who typed a new name and
clicked another row lost the typed text. The tree rename commits on focus loss, and classic regedit
commits a value rename the same way, so the list now commits on lost focus. Escape still cancels, and
Enter still commits. Test: `ListRenameFocus_ClickingAnotherRowCommitsTheRename`.

## 8. Value row identity: the default row and a literal (Default) (0.1.2)

Each row carries its raw registry name, an `IsDefault` flag (the unnamed value, or the synthetic row
that stands in for it), and `IsSynthetic` (the placeholder with no registry value behind it). Display
text is never used to find a row or a registry value.

Failure modes:

- **Two rows show `(Default)`.** A key can hold the unnamed default value and a value literally named
  `(Default)`. Classic lists both. A map keyed by display text keeps one of them, so Modify, Delete,
  Rename, the binary viewer, and the Find cursor act on the wrong value.
- **A synthetic row beside a literal (Default).** A key with a literal `(Default)` and no unnamed value
  must still get the synthetic row, and the synthetic row must be the only row with an empty raw name.
  The old check `ContainsKey("(Default)")` in the display map could not tell the two apart.
- **Edit String on the synthetic row writes the literal.** The synthetic row must write the unnamed
  default (raw name empty), never the value named `(Default)`.
- **Rename of the literal row renames the default.** Rename is refused for the unnamed value only.
  The literal row renames its own raw name.
- **Delete of the synthetic row.** No registry value is behind it, so Delete skips it. Delete of the
  literal row removes only the value named `(Default)`, and the unnamed default stays.
- **Selection after refresh picks the wrong row.** F5, rename, new value, and an edit restore the
  selection by raw name. Restoring by display text selects the first `(Default)` row, which may be the
  synthetic or the literal one. The synthetic row has raw name empty, so it is restored by empty name.
- **A new value named `(Default)` selects the default row.** Renaming a value to `(Default)` restores
  the selection by its raw name, `(Default)`, which is the literal row.
- **Background sort keeps the wrong selection.** Above 10,000 rows the sort applies on the UI thread.
  It must map the selection, focus, anchor, and an open rename box by row identity, not by name.
- **Find cursor reads the wrong row.** The Find cursor takes the raw name of the first selected row
  through `TryGetRawValueName`, which must read the row's raw name.

Design: `DetailsRow` holds `RawName`, `IsDefault`, and `IsSynthetic`. `RegistryValueSource` no longer
builds a display-name map. `RegeditContext.TryGetRawValueName` and `IsSyntheticDefault` read the row.
Refresh and selection restore use raw names. The background sort maps rows by reference.

E2E: `List_LiteralDefaultRowsKeepTheirIdentity` (in `tests/Bwc.E2E/List/LiteralDefaultRowTests.cs`)
selects the literal row and the synthetic row in turn, edits the literal row, and checks the registry
value of each. `ValueDisplay_ListMatchesClassicForLiteralDefault` compares both rows with classic.

## 8a. Commands on the (Default) rows (0.1.2)

Classic regedit, one-off read on this machine: on a key with a literal (Default) and no unnamed value, Delete
on the synthetic (Default) row asks "Deleting certain registry values could cause system instability. Are you
sure...?" (Yes/No). Yes shows "Error Deleting Values" with "Unable to delete all specified values." and deletes
nothing; the literal (Default) stays.

Failure modes:

- **Delete on the synthetic row is disabled.** Edit > Delete must stay enabled for the synthetic row, or the
  command never reaches the confirmation classic shows.
- **Delete on the synthetic row removes the literal.** The synthetic row has no raw name, so it must never
  reach the delete call. The shared text is `MsgDeleteValuesFailed`.
- **Delete of the literal removes the default too.** Delete of a literal row removes only the value named
  `(Default)`.
- **Modify of the literal creates the default.** Modify on the literal row writes the value named `(Default)`
  and leaves the unnamed default absent.

E2E: `tests/Bwc.E2E/List/DefaultRowCommandTests.cs` (`List_DeleteOnSyntheticDefaultRefusesAndKeepsLiteral`,
`List_ModifyLiteralChangesOnlyTheLiteral`, `List_DeleteLiteralKeepsRealDefault`).

## 9. One sort, one comparer (0.1.2)

The list order comes from one comparer, `ListCollation`, in `DetailsList`. The value source does not
sort. Sorting used `StringComparer.CurrentCultureIgnoreCase` in the source and the native collation in
the list, so the two could disagree.

Failure modes:

- **Two comparers disagree.** A name that the managed culture orders one way and the native collation
  orders the other way lands in a different place after the list sort. Only the list sort may order rows.
- **`(Default)` sorted by text.** `ListCollation.SortKey` maps the display text `(Default)` to empty.
  A literal `(Default)` then sorts first, ahead of the real default. The first row must be the default
  row by `IsDefault`, and the literal row sorts by its own name.
- **Default row not first when it is synthetic.** The synthetic row is first when the key has no
  unnamed value, and the same rule applies.
- **Background sort order differs from the small sort.** Above 10,000 rows the sort runs on the thread
  pool. It must use the same comparer and the same default-first rule, and a stale result is dropped by
  version.
- **Sort order between two literal names that differ only by case.** Registry names are unique without
  regard to case, so this cannot happen in one key.
- **The walk reads rows while the list is still scrolling.** A recycled row element is detached from its
  row, so its UIA name is empty until the repeater fills it again, and the element keeps its old row id
  meanwhile. The walk of the large-list test sent the PageDown keys and then waited for "no blank row"
  before it read the rows a second time. Right after the keys that wait is already true for the rows of
  the previous page, so it returned before the list processed the scroll, and the second read caught
  recycled rows with empty names (rows 7390 to 7399 in the two failed full runs, none in the runs of the
  test alone, with no wait timeout in the run's timings). The product has no blank row after the sort: the
  rows are blank only while a scroll is being applied. Intended: the test judges the one snapshot that
  its wait accepted, and that wait first sees the viewport move (the last realized row index changes) and
  then sees every realized row named, within a bounded time. A snapshot that stays blank or wrong at the
  timeout is judged as it is and fails, so a missing or wrong name still fails.

E2E: `List_TenThousandValuesSortInBackground` (in `tests/Bwc.E2E/List/LargeValueSortTests.cs`) seeds
10,001 values in one key, checks that the first row is `(Default)`, and walks the realized rows with
Home, PageDown, and End. It checks each row against the native collation order computed in the test, on a
snapshot taken after the viewport moved and every realized row has a name.

## 10. Key identity of the value list (0.1.2)

The list shows the rows of one key. Before this change the rows were replaced only when the new key's
load finished, and every value command took its key from the live tree selection and its value names
from the rows. So a command could pair key B with the rows of key A.

Failure modes:

- **Stale rows after a tree change.** Selecting key B while key A's rows are still shown (a slow or
  remote load). Delete on a row shown from A deletes the same-named value in B.
- **Rename committed to the new key.** Rename a value in A, then right-click key B. The tree selects B
  before focus moves, and the rename box commits on focus loss. The same-named value in B is renamed.
- **Rename refresh reloads the wrong key.** After a rename committed to A, the refresh reads the tree key
  (B) and selects the new name there.
- **Rename box survives a row replacement.** A refresh replaces the rows while the rename box is open. The
  box still holds the old row index, so the commit names a different row.
- **Find cursor pairs keys.** The Find cursor reads the tree key and the first selected row. Rows from
  another key would give a cursor for a value that the key does not hold.
- **New value during a load.** New value writes to whatever key the tree shows at execution time, with no
  listing bound to it.

Design:

- The list is bound to one key, `RegeditContext.ListKey`. The key and the rows change together. A tree
  selection of a different key clears the rows and binds the new key at once, so no command can see the
  old rows. A selection of the same key keeps the rows; the reload replaces them.
- Every list command takes its key from `ListKey`, and its rows from the list. With no bound key (Computer,
  or a node without a key) New value shows the no-selection text, and the row commands have no rows to act on.
- A rename in progress is committed to its own key before the list is rebound. Classic regedit commits a
  rename on focus loss to the key the edit started in. BWC commits it on the tree selection that moves
  away, which runs before focus moves, so the commit lands in the old key. A rename whose row is no longer
  in the bound list is cancelled, never applied to another key.
- A refresh after a write reloads only when the bound key is the key that was written. Otherwise the write
  does not touch the displayed list.
- The Find cursor (`FindController`, read-only) still reads the tree key and the first selected row. The rows
  always belong to the tree key, because a key change clears them, so the pairing holds without a change there.
- `DetailsList.SetRows` ends a rename in progress without committing it, because the row index it holds
  is no longer valid.

E2E: `tests/Bwc.E2E/List/ListKeyIdentityTests.cs`.
- `ListKeyIdentity_RenameCommitsToItsOwnKey` starts renaming A\X, right-clicks B, and checks that A's X is
  renamed and B's X is untouched.
- `ListKeyIdentity_DeleteDuringLoadTouchesNothing` holds B's load with the E2E-only delay hook, invokes
  Delete, and checks that B's X still exists.
- `ListKeyIdentity_NulNamedValueIsRefused` covers the NUL-named value (section 11 of
  `value-display-enumeration.md`): Modify is refused, and a multi-delete that includes it deletes the rest
  and refreshes the list.

## 11. Value names with an embedded NUL (0.1.2)

A value named `abc\0def` is listed (its name, data, and type come from one `RegEnumValueW` call), but no
command can address it by name. Win32 name APIs read the name up to the first NUL, so every name-based call
reaches the value `abc`.

Failure modes:

- **Modify shows another value.** `ReadValue("abc\0def")` returns `abc`'s data and type. The editor opens
  on the wrong value and a save writes to `abc`.
- **Delete stops the batch.** `DeleteValue` throws `ArgumentException` for a NUL name. The multi-delete loop
  catches only `RegistryOperationException`, so the first NUL-named row stops the loop, and the refresh
  after the loop is skipped.
- **Rename reads the wrong value.** `RenameValue` reads the old name first, so it copies `abc`'s data
  before it refuses.
- **Binary and hex commands read the wrong value.** Modify Binary Data, Display Binary Data, and the
  hex editor use the same name read.

Design: every command on a NUL-named row is refused before any read or write. Modify, Modify Binary Data,
Display Binary Data, and Rename show the classic invalid-name text (`DialogInvalidValueName`). Multi-delete
attempts every other value, catches the refusal per value, reports the first failure after the work, and
refreshes. The engine refuses a name with NUL on every name-based read (`ReadValue`, `ValueExists`) with
`ArgumentException`, the same as the writer.

Tests: `tests/Bwc.Registry.Tests/RegistryValueNulNameTests.cs` (reader, writer, and rename guards), and
`ListKeyIdentity_NulNamedValueIsRefused` in `tests/Bwc.E2E/List/ListKeyIdentityTests.cs`.

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

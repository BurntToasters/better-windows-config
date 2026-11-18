# Console list failure modes

Scope: piece U2b of release 0.2.0. `ConsoleList` in `src/Bwc.UI/Console/List/`: the result list of the MMC 3.0 user-mode
frame (`ConsoleShell` result pane slot). Caller-defined columns, caller-supplied rows, header sort, column resize and
auto-fit, four view modes (Large Icons, Small Icons, List, Detail), single and extended selection, keyboard, type-ahead,
activate and context events, UIA. It knows no snap-in, so Local Security Policy (0.3.0) reuses it. The registry value
list (`src/Bwc.UI/List`) is a different control and is not touched. Add/Remove Columns, Export List and the description
pane are later pieces; they use the column state API defined here.

Evidence (untracked, local): `artifacts/oracle/gpedit/probe.json`, step `result-list` (UI Automation dump of the classic
SysListView32 at 192 DPI) and `docs/parity/oracle/gpedit/gpedit-resources.md`. Probe facts used: the header is 48 px
(24 logical) high; a detail row is 38 px (19 logical) high and as wide as the list (full row select); the list is a
List with the Scroll, Grid and Table patterns; a row is a ListItem with the Invoke, SelectionItem and ScrollItem
patterns whose name is the text of the first column (the host adds the word the classic host adds); the list is named
by an internal window caption and the header by another one; header items are named by their column text and have no
automation id.

Declared choices that are not measured from classic (the audit lists them): the arrow of the sorted header is a 9 by 5
logical triangle centered at the top of the header cell (the comctl32 v6 look, recalled, not measured); the minimum
column width is 12 logical pixels; large icon cells are 75 by 75, an icon is 32 logical pixels in Large Icons and 16 in
the other modes; Small Icons and List items are as wide as the widest of the first 500 labels (at least 80, at most 250)
and 19 high; the type-ahead pause is one second; the Detail row and header heights are scaled with the dense font size
the way the registry list does it.

This file was written before the code. Each section lists the ways a behavior can fail, the design choice that prevents
it and the E2E check that catches it. Every `Console_List_*` test writes `artifacts/e2e/visual/console-list/
console-list-<name>.json` (measurements) and a picture of the client area next to it. The host used by the tests is
`BwcGpedit.exe` with the E2E-only data seam `BWC_E2E_CONSOLE_ROWS` (row count, default 8) described in section 16.

## 1. Empty list

No rows, or the last row removed while it was selected or focused. A focus index or an anchor that still points at row
0 makes the next arrow key select a row that does not exist; Home and End on an empty list index `-1`; a context key
has no row.

- Handling: every index access is range checked; `SetRows` with no rows clears selection, focus, anchor and the
  type-ahead buffer and raises one `SelectionChanged` only when something was selected. Navigation keys do nothing,
  Enter raises nothing, the context key raises a background request (row null). The header stays visible in Detail.
- Detector: `Console_List_Large3000` ends with a refresh that leaves 0 rows (the host's second refresh leaves none)
  and then presses Down, Home, End, Enter and the context key; `Console_List_ActivateAndContext` presses the context
  key with no focused row.

## 2. 3,000 rows: scroll and memory

One UI element per row makes opening a large list slow and keeps memory high; End then walks 3,000 elements. A layout
that estimates the extent wrongly leaves the scroll bar short of the last row.

- Handling: `ItemsRepeater` virtualizes in every mode; only the rows in view (plus the repeater cache) have elements, the
  extent is exact because every item has one size per mode. End, PageDown and reveal compute the offset from the index
  and the item size, not from realized elements. Icons are cached per kind and pixel size by `IconProvider`.
- Detector: `Console_List_Large3000` opens 3,000 rows and records the open time (bound 15 s, measured value written),
  the realized element count (bound 150), the working set growth after End, PageDown x20 and Home (bound 250 MB), the time
  from End to focus index 2999 (bound 3 s) and the time of one UIA read of the visible rows after scrolling (bound 2 s).
  The bounds are generous on purpose: they catch an unvirtualized list (3,000 elements, tens of seconds), not tuning.

## 3. Sort of equal keys

An unstable sort puts rows with an equal key in an arbitrary order, so clicking State twice shuffles the rows inside each
state, and descending is not the reverse of ascending.

- Handling: the list keeps the caller's order (`source order`) and every sort is a stable sort of that order
  (`OrderBy` with the column comparer; descending is `OrderByDescending`, also stable). Equal rows therefore keep the
  caller's order in both directions, and ascending, descending, ascending gives the first result again. The comparer is
  the caller's per column; the default compares the cell text with the native collation (`CompareStringW`, ignore case)
  the registry list uses. A comparer that throws is caught: the list keeps its previous order, clears the sort mark and
  reports through `UiDiagnostics.Warn`.
- Detector: `Console_List_SortBothWays` sorts State ascending and descending and checks that the rows inside each state
  keep the source order, and sorts Name ascending then descending then ascending and compares the first and third order.

## 4. Sort while a row is selected

Rows are re-ordered, so a selection kept as indexes selects other rows after the sort, and the focus rectangle jumps.

- Handling: selection, focus and anchor are kept as row objects, never as indexes. After a sort the indexes are
  recomputed, the focused row is revealed, and `SelectionChanged` is not raised (the set did not change).
- Detector: `Console_List_ViewModesAndSelection` selects a row, sorts two columns and checks the same row (by name) is
  still the only selected one and still the focused one.

## 5. View switch with the selection off screen

Switching from Detail (3,000 rows, scrolled to row 2,500) to Large Icons changes every offset. The scroll offset of the
old mode shows an arbitrary part of the new layout, or none (past the end), and the selected row is not visible.

- Handling: after a switch the list lays out once and reveals the focused row (the first selected row when nothing is
  focused, row 0 otherwise). Offsets are recomputed for the new mode; the old one is never reused.
- Detector: `Console_List_ViewModesAndSelection` (300 rows) focuses a row near the end, switches through the four modes
  and checks after each that the focused row's element is inside the list rectangle.

## 6. Column resized to zero

A drag far to the left asks for a zero or negative width. A zero-width column has no divider to grab again, its header
text is invisible, and auto-fit of a column the user cannot see is meaningless.

- Handling: widths are clamped to 12 logical pixels (declared choice) and to 4,096 at the top; NaN and infinity are
  ignored. A hidden column keeps its width in the state. Auto-fit never goes below the minimum.
- Detector: `Console_List_ResizeAndAutoFit` drags a divider far left and checks the width is the minimum, and drags
  right again from there (the divider is still grabbable).

## 7. Very long text

A 10,000 character cell or header grows the element, the auto-fit width and the type-ahead cost; wrapping would make rows
of different heights and break the offset arithmetic.

- Handling: every text is one line, trimmed with an ellipsis; Large Icons labels wrap to two lines and trim. Auto-fit is
  clamped to 4,096 and measures the first 200 rows, the 100 longest texts of the column and the realized rows (the widest
  text of a proportional font is nearly always one of the longest). The UIA name is the full text.
- Detector: `Console_List_ResizeAndAutoFit` auto-fits a column with a 5,000 character cell (the seam adds one to row
  3 when `BWC_E2E_CONSOLE_LONGTEXT` is set) and checks the width is at most 4,096 and the row height is still 19 logical.

## 8. Icon load failure

A module icon that is absent, or a classic icon the system cannot read, must not leave a hole, throw in prepare, or make
the row taller.

- Handling: `IconProvider.Get(IconReference, scale)` never throws and returns the BWC fallback glyph. A row without an
  icon reference shows no icon. The list wraps the call in `try`; any exception gives no image and one log line.
  Large icons request twice the pixel scale, so a 32 DIP icon is 64 px at 200 percent.
- Detector: `Console_List_ColumnsAndHeaders` has rows whose small and large icon is a module icon of a file that does
  not exist; the picture shows the glyph and the test checks the icon cell of that row has non background pixels and the
  row is still 19 logical high.

## 9. DPI scale

Pixel sizes cached at one scale stay wrong after the window moves to a monitor of another scale; hit areas of 4 logical
pixels (the column divider) become 1 device pixel at 100 percent.

- Handling: sizes are logical (DIPs); icons are fetched at `XamlRoot.RasterizationScale` and re-applied when
  `XamlRoot.Changed` reports a new scale; the divider grip is 4 logical pixels as in the registry list. No device pixel is
  cached.
- Detector: every test reads the current window DPI and compares logical sizes (24, 19, 75) so they hold at 96 and at 192
  DPI. A live monitor change cannot be driven by the harness and stays unverified (listed as unfinished).

## 10. High contrast

Hard-coded colors disappear in high contrast. The sort arrow, the selection and the focus rectangle must follow the
contrast palette.

- Handling: every brush comes from `DenseTheme` (the palette brushes of `BwcBrushes`), which are updated in place when
  the palette changes; the arrow is a `Path` filled with the window text brush; `ActualThemeChanged` re-applies the
  brushes of the realized elements.
- Detector: none in this piece (the harness cannot switch contrast themes; the registry high contrast suite covers the
  shared brushes). The pictures show the default theme only.

## 11. Focus after a view switch

Re-binding the repeater recycles every element, including the one that has the keyboard focus if the focus lived in a
row. The focus would fall to nothing and the next key would go to the window.

- Handling: the keyboard focus belongs to the list control itself, not to a row; the focused row is a logical index drawn
  as a dotted rectangle. A switch changes the layout only, the control keeps the focus, and the focused row is kept.
- Detector: `Console_List_ViewModesAndSelection` switches mode with the keyboard focus in the list (through the host's
  View menu, which returns the focus to the list) and checks the focused pane is the result pane and an arrow key
  still moves the focus row in the new mode.

## 12. Type-ahead with non-ASCII text

Prefix search with an ordinal comparison fails for `é` against `É`; a high surrogate arrives alone before its low
surrogate; a dead key or an IME composition arrives as several characters.

- Handling: the buffer is a string appended per `CharacterReceived`; the search uses `CurrentCultureIgnoreCase`
  `StartsWith`; a buffer that ends with a lone high surrogate does not search. Control characters, Ctrl and Alt
  combinations are not search input. A pause of one second starts a new buffer. One repeated letter cycles through the
  rows that start with it, from the row after the focus. The match selects the row (single selection) and reveals it.
- Detector: `Console_List_TypeAhead` types with `FlaUI.Keyboard.Type` (which sends Unicode input) letters, a repeated
  letter, a two letter prefix, `É` (matches the row `Écho`) and the pause.

## 13. Rows replaced while scrolling

The host refreshes the list while the user is at the end of 3,000 rows and the new list has 100: the scroll offset is past
the new extent, an element for a removed row is still prepared, the selection points at rows that no longer exist.

- Handling: `SetRows` swaps the whole list in one step (the repeater source is set to null and then to the new list), then
  puts the old scroll offset back, clamped to the new extent. `SetRows(rows, keepSelectionByTag: true)` re-selects the rows whose tag equals a selected row's tag
  and drops the others; the default clears the selection. A refresh while a sort is set re-applies the sort.
- Detector: `Console_List_Large3000` scrolls to the end, presses the host's Refresh (the seam replaces 3,000 rows with 100,
  then 0) and checks every realized row carries the new name, the old offset is clamped so the view shows the end of the 100 rows (the offset is kept, not reset to the top), the app answers UIA and the selection is dropped because the tags are gone.

## 14. Header click, divider drag and double click

A click on a divider must not sort; a drag that ends on the next header must not sort; a double click on a divider is an
auto-fit, not two sorts; losing the pointer capture (a dialog opens) leaves the drag active forever.

- Handling: the divider grips are drawn after the titles and take the pointer; the press takes the pointer capture on the
  list (as the registry list does), so the moves and the release reach it for the whole drag, and the drag ends on release
  or capture loss; a title raises the sort on `Tapped` (a press and release on the same title) only.
- Detector: `Console_List_ResizeAndAutoFit` double clicks a divider and checks the sort did not change;
  `Console_List_SortBothWays` clicks each title.

## 15. Keyboard per view mode, selection and events

Arrow keys mean different things per mode: Detail has one column of rows; Large and Small Icons flow left to right and
wrap; List flows top to bottom and wraps to the next column. The number per line depends on the viewport, which changes
with the window and the scroll bar. Extended selection (Shift, Ctrl) must keep one anchor. Enter with several selected
rows must not activate an arbitrary one. The context key with no focus must still give one request.

- Handling: Up and Down move by 1 in Detail and List and by the number per line in the icon modes; Left and Right move
  by 1 in the icon modes, by the rows per column in List and scroll horizontally in Detail. PageUp and PageDown move one
  viewport; Home and End the first and last item. Shift extends from the anchor and replaces the selection, Shift+Ctrl
  adds the range, Ctrl moves the focus only, Ctrl+Space toggles, Ctrl+A selects all. Enter and double click raise
  `ItemActivated` for the focused row (the first selected row when no row is focused); with no row nothing is raised.
  Shift+F10, the context-menu key and a right click raise `ContextRequested` once with the row (null for the empty area);
  a right click on an unselected row selects it first, on a selected row keeps the selection. Inside the console shell
  the shell handles the context keys first and the host forwards a keyboard request of the result pane to
  `RaiseContextAtFocus`, so one key gives one request.
- Detector: `Console_List_ViewModes` (layout and arrow keys per mode), `Console_List_MultiSelect`,
  `Console_List_ActivateAndContext`. The host writes each event into the status bar.

## 16. E2E seam leaking into the product

The synthetic data source must not exist in a release build, and a malformed value must not crash the host.

- Handling: the seam is under `#if BWC_E2E` in `GpeditRootView`. `BWC_E2E_CONSOLE_ROWS` is parsed with `TryParse`; a
  missing, non numeric or negative value gives 8 rows, more than 100,000 is clamped. The ship build has the same list with
  the three placeholder columns and no rows, until G1 replaces the source. UIA help text with counters is also E2E only.
- Detector: `node scripts/dotnet-build.js --rid win-arm64 --flavor ship` builds without the seam; `Console_List_Large3000`
  uses 3,000.

## 17. Hiding the sort column and column state

Hiding or removing the column the list is sorted by leaves a sort mark on a header nobody sees, and a persisted state
that names a column the caller no longer defines must not fail.

- Handling: hiding the sort column clears the sort and raises `SortChanged`; `ApplyState` ignores unknown ids and
  duplicated ids, ignores widths that are not numbers, clamps widths, keeps the defined order for columns missing from
  the state, and drops a sort by an unknown or hidden column. `GetState` returns plain values (id, width, visible, order,
  sort id, direction, view mode) for the caller to persist.
- Detector: `Console_List_ColumnState` starts the host with `BWC_E2E_CONSOLE_STATE_MENU` (e2e flavor only), which adds two
  View entries: one writes `GetState` into the status bar, the other applies a fixed state (comment first at 150, name at
  222, state hidden, sorted by name) through `ApplyState`. The test checks both through the header, the readout and the
  rows. Hiding the sort column through `SetColumnVisible` and an unknown id in a state have no E2E check (listed as
  unfinished); the code path is a few lines and is read in review.

## 18. Tests, bounds and artifacts

| Test | Sections |
| --- | --- |
| `Console_List_ColumnsAndHeaders` | 1, 8, 9, UIA names and patterns |
| `Console_List_SortBothWays` | 3, 10 (arrow), 14 |
| `Console_List_ResizeAndAutoFit` | 6, 7, 14 |
| `Console_List_ViewModes` | 11, 15 |
| `Console_List_ViewModesAndSelection` | 4, 5, 11 |
| `Console_List_MultiSelect` | 15 |
| `Console_List_ActivateAndContext` | 1, 15 |
| `Console_List_TypeAhead` | 12 |
| `Console_List_Large3000` | 1, 2, 13 |
| `Console_List_ColumnState` | 17 |

Bounds of `Console_List_Large3000` (constants at the top of `ConsoleListScaleTests.cs`; the measured values are in the
measurements file): open to row 0 on screen 15 s; End to the last row focused 3 s; one UIA read of the realized rows 2 s;
realized elements 150; working set growth 250 MB. They catch an unvirtualized list (3,000 elements: minutes and
gigabytes), not tuning. Marquee (rubber band) selection, drag and drop of items and column reordering by dragging a
header are not part of this piece.

## 19. View mode command through the menu keyboard

Found while running the view mode tests: the menu keyboard runs the action of a menu item by looking it up in a weak table keyed by the managed wrapper of the item (`MenuItemActions`). A radio item that nothing else refers to is collected, the lookup then finds no action, and the access key closes the popup without changing the mode. After any menu command the keyboard focus also stays on the menu bar item, so the arrow keys stop driving the list.

- Handling (host side, shared menu code untouched): `GpeditRootView.PinMenuItems` keeps every menu item reachable for the life of the view; the view mode commands go through `SetViewMode`, which sets the mode and queues `ConsoleList.FocusList`, so the list keeps the keyboard. The shared fix (store the action on the item itself, restore the focus after a leaf command) belongs to the menu code and is reported, not made here.
- Detector: `Console_List_ViewModes` and `Console_List_ViewModesAndSelection` switch every mode with Alt+V and the access key, then check the mode, that the focused pane is the result pane and that the arrow keys still move the focus.

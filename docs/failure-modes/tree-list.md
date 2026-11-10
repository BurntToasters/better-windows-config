# Dense tree and details list failure modes

Scope: `src/Bwc.UI/Tree` (`DenseTree`, `TreeNode`, `FlatNodeList`), `src/Bwc.UI/List`
(`DetailsList`, `DetailsColumn`, `DetailsRow`, header and row panels), their code-built
automation peers, and the demo wiring in `src/Bwc.Regedit/RegeditDemoData.cs` plus
`RegeditRootView`.

Both controls follow the S2 and S3 decisions: `ItemsRepeater` with a `StackLayout` inside a
`ScrollViewer`, fixed-height row elements with no item container chrome, and column widths in a
shared model that only invalidates arrange. The tree flattens its visible nodes into one list and
raises one range notification per inserted or removed block.

## Tree

### Virtualization and measured row height

The oracle tree row is 17 DIP. A row element that lets a template add `MinHeight`, `Padding`,
`Margin`, or a border can render taller than 17 DIP and drift the scroll extent. Rows are custom
`Panel` elements whose `Height` is set to `DenseMetrics.TreeRowHeight`, whose `MeasureOverride`
returns that height, and whose template has no outer container. The E2E test measures a realized
row bounding rectangle at 200 percent scale and requires 34 px within 1 px.

Scrolling is virtualized: `ItemsRepeater` realizes a viewport-sized window. Column width changes
must not re-template rows. `StackLayout` is created in XAML with `Spacing=0` so position is
exactly `index * rowHeight`.

### DPI and metric changes

All geometry is in DIPs. A physical-pixel constant would be wrong on the 200 percent test
machine. `DenseTree` reads `DenseMetrics.TreeRowHeight` and `DenseMetrics.TreeIndent` when it
builds a row and re-applies them on `DenseMetrics.Changed`. Rows already realized are updated in
place. The tree does not cache a pixel size.

`DenseMetrics.ApplyFont` can round heights to whole DIPs. Type-ahead, PageUp, and PageDown compute
the page size from the live viewport divided by the live row height, not from a startup constant.

### Expand of 10,000 or more children

The S2 spike measured a 156 ms single-insert peak for a very large node. A single range insert is
therefore rejected. `DenseTree` collects the visible children, then inserts at most 500 rows per
`DispatcherQueue.TryEnqueue` callback. Each callback checks the node is still expanded and that
its cancellation token is still live. The model list is updated before the range notification so
the repeater can read the payload at the notified index.

A node with 2,000 children takes four chunks. The E2E test expands that node and reads the row
count from the tree automation help text until it reaches the full expected count.

### Cancellation when collapsing during a chunked expand

Collapse while chunks are still queued is the normal case for a user who changes their mind. Each
pending expansion owns a `CancellationTokenSource` keyed by node. Collapse cancels it, marks the
node collapsed, and removes the visible descendants already inserted. A queued chunk that runs
later sees the cancelled token or `IsExpanded == false` and stops. It never inserts rows for a
collapsed node.

Expanding the same node again cancels any older token for that node and starts a fresh chunk
sequence. Stale chunks from the first attempt cannot append after the second attempt because the
token is checked before every insert.

### Expander hit target

The chevron sits in a 16 by 16 DIP transparent hit target, and that width is reserved on leaf
rows too so labels in one column line up. A smaller target is hard to hit on a touch screen. The
chevron does not steal the row tap: the expander handler marks the event handled, the row handler
selects. Tapping the text selects without toggling. Keyboard Right and Left provide the same
operations without a pointer.

### Keyboard focus

The tree is one tab stop, not one tab stop per row. `IsTabStop` is true, rows are not focusable,
and selection is tracked by the control. Pointer taps call `Focus(FocusState.Pointer)`. If focus
is inside the rename text box, the tree key handler returns before interpreting arrows, Home, or
End, so editing does not move the selection. `FocusSelected` selects the first row when there is
no selection and scrolls it into view, which gives a deterministic entry point after the splitter
or address bar had focus.

### Type-ahead races

Type-ahead uses a 500 ms buffer. A pointer or arrow-key selection change clears the buffer so a
new prefix starts at the new position. The search starts after the current index for the first
character and at the current index for later characters, wrapping once. A rename text box swallows
characters before the tree sees them, so typing a name does not also jump the selection. The
buffer length is bounded to keep a key held down from growing it without limit.

### Rename commit and cancel

F2 shows a single `TextBox` with automation id `TreeRenameBox` over the selected row. Enter commits
and raises `RenameRequested`; Escape cancels and restores focus to the tree. Losing focus cancels
rather than commits, so clicking another row cannot silently rename a key. The demo host never
writes the registry: it receives the event and decides what to do. The text box is positioned in
the scrolled content so it stays aligned while the tree is scrolled.

### UIA stale peers

`ItemsRepeater` recycles row elements. A peer that stores the model index from construction points
at the wrong node after an insert, collapse, or scroll. The row peer asks its element for the
current node and asks the tree for the current visible index on every call, and the element's
automation id is refreshed for every realized row after a structural change. Tests re-find
elements through the main window after each operation instead of keeping a peer across a model
change. The tree root exposes the current visible row count in its help text so a test does not
have to infer a 2,000 row insert from the virtualized window alone.

### Selection after collapse

Collapsing a node removes its descendants. If the selection was inside that subtree, the
selection moves to the collapsed node and `SelectionChanged` fires once. Otherwise the selection
object stays the same and no event fires. After a model reset the selection is cleared.

### ItemsRepeater recycling

`ElementPrepared` receives a recycled element. The attach path compares the new node, depth, and
selection state with the old values and writes only what changed. It never allocates a brush per
row: the selection and text brushes come from theme resources and are shared. Attaching during
`ElementPrepared` must not change a layout property that invalidates the repeater's parent chain.
The row height and indent are the only layout writes, and they are skipped when unchanged.

### Theme and high contrast

Selection uses `ListViewItemBackgroundSelected` and `ListViewItemForegroundSelected` when those
theme resources exist, with `AccentFillColorSecondaryBrush` and
`TextOnAccentFillColorPrimaryBrush` as fallbacks. No selection color is hard coded. Row elements
re-resolve the brushes when `ActualThemeChanged` fires. The transparent backgrounds keep hit
testing working while letting the pane background show through.

## List

### Fixed row height and virtualization

The list row is 18 DIP. Row elements are fixed-height panels with no container chrome. The header
is 20 DIP. The E2E test measures 36 px and 34 px at 200 percent scale within 1 px. The list uses
`ItemsRepeater` plus `StackLayout`, so scrolling realizes only the viewport window and selection
updates only realized rows plus the selected-index set.

### Sort stability

`OrderBy` with an ordinal comparer is used for every column. It is stable, so rows with equal keys
keep their previous relative order. `List.Sort` would be unstable and make two sorts of equal
keys produce different visible orders. Descending sort reverses the comparer, not the result, so
the secondary order is not accidentally reversed.

### Background sort and disposal

Sorting more than 10,000 rows on the UI thread would block input for the duration measured by the
spike (about 150 ms for 200,000 rows). `SortBy` copies the row references, sorts the copy on the
thread pool, and applies the result on the UI thread. A monotonically increasing version number
is captured before the sort. When the continuation runs, it applies only if the version still
matches, so a second sort or a key change wins. `SetRows` and `Unloaded` increment the version;
a continuation from a discarded list therefore does nothing and does not touch disposed
elements. The sort buffer is reused to avoid large-object-heap churn.

### Selection and multi-select

Selection is an index set plus an anchor index. A plain click replaces the selection, Ctrl-click
toggles one row, and Shift-click extends from the anchor. The row peer exposes `SelectionItem`, so
UIA `Select`, `AddToSelection`, and `RemoveFromSelection` have the same effect. `SelectionChanged`
fires once per user operation, not once per row, and `SelectedRows` returns rows in visible order.
After a sort the selection is cleared because indices no longer describe the same rows.

### Rename commit and cancel

F2 shows `ListRenameBox` over the name cell of the focused row when that row is renameable. Enter
raises `RenameRequested` with the old row and the new text; Escape cancels. The demo does not
mutate its data. Focus loss cancels. The box is not created for a non-renameable row.

### Column resize

Each divider has a 4 DIP transparent grip centered on the boundary, with an east-west cursor and
pointer capture for the duration of the drag. Dragging changes only the shared column model and
invalidates measure and arrange on realized rows and the header. It does not reset the repeater
item source or rebuild templates. Widths are clamped to a 40 DIP minimum and rounded to whole
DIPs so a drag does not accumulate fractional error.

### Header double-click auto-size

Double-clicking a divider measures the header and a bounded sample of rows (the first 200 plus
every realized row) and sets that width. Measuring all 200,000 rows would block the UI. The
sample limit is a deliberate tradeoff, documented in the method. The auto-size path reuses one
probe `TextBlock` instead of creating a text layout per row.

### Header clicks and sort indicator

A click on a sortable header sorts ascending the first time. A click on the already ascending
column sorts descending; a click on the already descending column sorts ascending again. The
indicator is a small arrow in the header cell and is updated in the same operation as the data.
The divider grip is on top of the header hit target, so a resize drag or double-click does not
also sort.

### Theme and high contrast

The header background, divider, selection, and text brushes are theme resources. Rows and header
re-resolve selection brushes on `ActualThemeChanged`. Hard-coded black or blue would be unreadable
in dark mode and high contrast. The list does not set `RequestedTheme`, so it follows the app.

### UIA grid items and stale peers

Each row peer implements `SelectionItem`, `ScrollItem`, and `GridItem`. `GridItem.Row` is the
current visible index, `Column` is 0, `ColumnSpan` is the number of columns, and `RowSpan` is 1.
The index is read from the row element at call time, so a recycled peer does not report the
position it had before a sort or scroll. Tests re-find rows after a sort.

## Tests

### Flakiness

The controls tests launch the published app through `AppSession`, wait for automation ids with a
timeout, and use `SendKeys` so the foreground check is recorded. They never write the real
registry and never depend on a machine's actual key values. A chunked 2,000 row expand is allowed
several seconds and polled; a fixed sleep is not used for the assertion. Height checks use a 1 px
tolerance because a bounding rectangle is reported in physical pixels and a border can round by
one pixel at 200 percent. A missing row peer is treated as a failure with a screenshot, not as an
empty pass. UIA elements are re-acquired after each structural change so recycling cannot make a
stale peer look correct.

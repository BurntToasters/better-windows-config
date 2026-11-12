# Dense tree classic behavior failure modes

Scope: `src/Bwc.UI/Tree` (`DenseTree`, `TreeRowElement`, `TreeNode`, `FlatNodeList`) and the
E2E coverage in `tests/Bwc.E2E/Tree/TreeBehaviorTests.cs` and `tests/Bwc.E2E/Controls/TreeTests.cs`.
This document lists how each classic TreeView behavior (package UF5) can fail, so the tests are
written against the failure list and not against the happy path only. The geometry notes in
`tree-list.md` (row height, chevron, icon, indent) still apply and are not changed here.

Reference behavior is the Win32 TreeView used by classic regedit. Items marked "spec" were given
by the package and not yet confirmed on the classic oracle; the report for the package lists which
ones were checked on classic regedit.

## Double-click

- A double-click on the label toggles expand state once. Two single taps must still only select.
- A double-click on the chevron toggles once, not twice. The chevron handles its own double-tap so
  the row handler does not run a second toggle on the same gesture.
- A double-click on a leaf does nothing and does not move the selection away from the clicked row.
- A double-click on an unselected row selects that row first, then toggles it.
- A double-click while a chunked expand is still inserting rows cancels nothing unexpected: a
  second toggle collapses through the normal collapse path and cancels the pending chunks.
- A pointer press on a row moves XAML focus from the tree to a ScrollViewer: an outer one that wraps
  the tree, or the tree's own. The move happens after the double-tap is handled, so a double-click
  expand or collapse ends with the ScrollViewer focused. UI Automation does not expose it, so the
  tree reads as unfocused (UIA showed an InputSiteWindowClass pane) and type-ahead and arrow keys
  looked dead. Observed on the E2E run: focus was on the tree after the first double-click and on
  the ScrollViewer after the second. A keyboard collapse or expand never moved it. Fix:
  `DenseTree` answers `LosingFocus` by keeping focus on the tree when the new owner is a ScrollViewer
  that contains the tree or sits inside it. The rename box is excluded; it takes focus on purpose.

## Numpad keys (+, -, *)

- Numpad `+` (VK_ADD) expands the selected node when it has children and is collapsed. It is a
  no-op on an expanded node and on a leaf. It must not expand siblings.
- Numpad `-` (VK_SUBTRACT) collapses the selected node when it is expanded. It is a no-op on a
  collapsed node. A collapse with the selection inside moves the selection to the node.
- Numpad `*` (VK_MULTIPLY, spec: classic TreeView "expand all below") expands the selected node and
  every descendant that has children. Each level loads children lazily through the normal loader.
  It must stop cleanly when a load fails, must not leave a node expanded with no children, and must
  not block the UI thread while it walks a large subtree (chunked inserts still apply).
- The old behavior (expand every sibling of the selection) is removed. A test that presses `*` on a
  node with collapsed siblings must see those siblings stay collapsed.

## Right and Left

- Right on a collapsed node with children expands it and keeps the selection on it.
- Right on an expanded node with children moves to its first child.
- Right on an expanded node with no visible child, or on a leaf, does nothing. It must not jump to
  the next sibling, which is what the old code did when the expanded node had no visible rows.
- Left on an expanded node collapses it and keeps the selection on it.
- Left on a collapsed node or a leaf moves to the parent. Left on a root with no parent does nothing.
- Left and Right with no selection select the first row and do not throw.

## Type-ahead

- A letter selects the next visible row whose name starts with the typed prefix, searching from
  the row after the current one. Matching is case-insensitive.
- Quick typing extends the prefix ("So" then "f" selects "Software"). The buffer resets after a
  pause of 1000 ms (spec: classic incremental search timeout is about one second; measured on the
  control, not from source). A key pressed after the pause starts a new prefix.
- Typing the same letter again (and again) cycles through the rows that start with that letter.
  A repeated-letter buffer ("aa") must cycle, not match the literal "aa" prefix.
- The search wraps from the end of the list to the start.
- A prefix with no match leaves the selection where it is and does not throw.
- Control characters (Enter, Tab, Escape, Backspace) do not start or extend a prefix.
- The buffer is bounded (32 characters) so a held key cannot grow it without limit.
- While the rename box is open, typed characters go to the box and never to type-ahead.

## Horizontal scrolling

- A row whose indent plus text is wider than the viewport makes a horizontal scrollbar appear
  inside the tree. The bar must not appear when every row fits.
- The row background and the selection fill span the full content width, not the viewport width,
  so a scrolled selection is not cut off at the right edge.
- Scrolling horizontally moves the labels and the rename box together. The rename box is positioned
  in content coordinates.
- The content width is the widest row that has been realized. A row that is virtualized away does
  not hold the width; this is accepted and is what the E2E test sees for a deep path that is kept
  in the viewport.

## Rename (F2)

- F2 opens the rename box over the selected row with the whole name selected.
- Enter commits: `RenameRequested` fires once with the typed name, and the box closes.
- Escape cancels: no `RenameRequested`, and the box closes with focus back on the tree.
- Focus loss commits (classic behavior: clicking elsewhere applies the rename). It must not cancel.
- Enter followed by the focus loss that the close causes must not raise `RenameRequested` twice.
  The first commit clears the rename state before the event, so the focus loss is a no-op.
- A structural change (SetRoots, a collapse that removes the node) while renaming ends the rename
  without raising a rename for a node that is no longer visible.
- An empty or unchanged name is passed through unchanged; the host decides what to write.

## Expand errors

- A load that throws (access denied, a deleted key) must not crash the app and must not escape as an
  unobserved task exception.
- The failed node is collapsed again (`IsExpanded` false), its chevron shows the collapsed state, and
  no rows are inserted for it.
- `ExpandFailed` is raised with the node and the exception so the host can tell the user. A
  cancelled load (collapse during load) is not a failure and raises nothing.
- A failed load leaves `HasChildren` true, so a later Right or `+` retries the load.
- The loading state never stays on a node after the load returns, whether it succeeded, failed, or
  was cancelled.

## Performance and lookups

- Every node-to-row lookup (selection, reveal, UIA index, collapse, rename position) is O(1) through
  an index that is updated with every range insert, range remove, and reset. A linear `IndexOf` on
  the flat list is a failure.
- A node with 10,000 children expands in chunks of 500. The first page is visible within the budget
  in `TreeBehaviorTests` (2 s from the expand key to the first chunk, measured on this machine).
- Index maintenance after a chunk insert is linear in the number of rows after the insert point, and
  never re-walks the subtree.

## UI Automation

- Tree items expose `ExpandCollapse`, `SelectionItem`, and `ScrollItem`. `ExpandCollapseState` is
  `LeafNode` for a node without children, `Collapsed` or `Expanded` otherwise.
- Expanding or collapsing raises a property-changed event for `ExpandCollapseState` on the row
  peer. Selecting raises one for `SelectionItem.IsSelected` on the new row and on the old row.
- The tree control itself exposes a `Tree` control type (`DenseTreeAutomationPeer`). It does not
  expose a container Selection pattern in this package; clients find the selected row through the
  row `SelectionItem` pattern.
- Automation ids and names used by tests are kept: `TreeControl`, `TreeRow_<index>`, the node name
  as the item name, and `TreeRenameBox` for the rename box. A recycled row never reports the index
  it had before an insert, collapse, or scroll.
- The help text row count (`Rows=<n>;Selected=<name>`) is kept for tests. It is E2E-only.

## Keyboard navigation extremes

- Home selects the first visible row and End the last visible row.
- PageUp and PageDown move by the page size computed from the live viewport and row height. They
  clamp at both ends and do not throw on an empty tree.
- No keyboard path moves the selection while the rename box has focus.

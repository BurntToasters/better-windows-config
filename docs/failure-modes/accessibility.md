# Accessibility failure modes

Scope: the UI Automation (UIA) surface of the registry key tree (`src/Bwc.UI/Tree`), the value list
(`src/Bwc.UI/List`), the hex editor and its Edit Binary Value dialog (`src/Bwc.UI/Hex`,
`src/Bwc.UI/Dialogs/HexEditorDialog.cs`), the classic dialogs (`src/Bwc.UI/Dialogs`, the names and tab order),
and the high contrast palette (`src/Bwc.UI/Theme`). The checks live in `tests/Bwc.E2E/Accessibility`, and they
drive the real app through FlaUI (UIA3). Category: `Accessibility`.

This list was written before the tests and the fixes (AGENTS.md order). Every entry names how it can fail, so
each test asserts the failure, not only the happy path. The Status column is filled in from the run. A row
marked `fixed` was failing before the change; `pass` means it passed on the first run; `open` means it is still
failing, with the reason.

Rules that apply to every check:

- No test changes a system or user setting. High contrast is never switched on through SystemParametersInfo.
  The high contrast check uses the E2E-only launch variable `BWC_E2E_HIGH_CONTRAST`, read only in E2E builds.
- Registry data is seeded only under `HKCU\Software\BWC-E2E\<run-id>`.
- A check that cannot see the element records a failure. It never passes by skipping.

## 1. Registry key tree

### 1.1 Expand and collapse
- 1.1.1 Items with children lack the ExpandCollapse pattern, so UIA clients cannot expand them.
- 1.1.2 ExpandCollapseState reads `Collapsed` after the rows are visible (no property change raised, or the
  state comes from a stale node).
- 1.1.3 Expand is a no-op on a node whose children are still loading in a chunked expand.
- 1.1.4 Collapse on an already collapsed node throws (it must be a no-op, not an exception).
- 1.1.5 A leaf reports ExpandCollapse (state `LeafNode` expected; a leaf that reports `Collapsed` is wrong).

### 1.2 Selection
- 1.2.1 SelectionItem.IsSelected is true on zero rows or on two rows at once.
- 1.2.2 SelectionItem.Select selects the node but the keyboard focus or the detail view stays on the old node.
- 1.2.3 The selected row's IsSelected reads stale after a keyboard move (the row was recycled).
- 1.2.4 RemoveFromSelection empties the selection and strands the keyboard anchor (the tree keeps one selection).

### 1.3 Name, id, scroll
- 1.3.1 The tree item Name is not the key name (it shows the path, a display string, or is empty).
- 1.3.2 The automation id `TreeRow_<n>` points at a different node after an insert or collapse (stale index).
- 1.3.3 ScrollItem.ScrollIntoView returns without revealing a row that is outside the viewport.
- 1.3.4 The tree control itself has no Name, so it reads as an unlabeled Tree.

### 1.4 Reachability
- 1.4.1 The `Computer` root is not a TreeItem named `Computer`, or it is not the first row.
- 1.4.2 The hives (HKEY_CLASSES_ROOT, HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE, HKEY_USERS, HKEY_CURRENT_CONFIG)
  are not children of `Computer` after it expands, so a client cannot reach them.
- 1.4.3 A hive or key is reachable only through a row that is not realized (no way to scroll to it).

## 2. Value list (details list)

### 2.1 Table and Grid patterns
- 2.1.1 The list control does not expose Table or Grid. Control type is not Table.
- 2.1.2 RowCount counts only realized rows, so a client sees fewer rows than values.
- 2.1.3 ColumnCount is not 3 (Name, Type, Data).
- 2.1.4 Column header items have an empty Name, so the headers cannot be read as Name, Type, Data.
- 2.1.5 GetColumnHeaders returns the list itself for every column, not the header item.

### 2.2 Rows and cells
- 2.2.1 Rows do not expose SelectionItem, or IsSelected is stale after a sort or scroll.
- 2.2.2 SelectionItem.Select on a row does not change the list selection.
- 2.2.3 Row Name is not the value name, or the default row is not named `(Default)`.
- 2.2.4 A literal value named `(Default)` and the unnamed default value share a name and one of them is lost.
- 2.2.5 GridPattern.GetItem(row, column) returns the row peer for every column, so the name cell is not a cell:
  it has no Value or Name of its own, and column 0 and column 2 return the same element.
- 2.2.6 GridPattern.GetItem for an unrealized row returns the list, so a client cannot reach a distant cell
  without scrolling it in first.
- 2.2.7 The Data cell of a row has an empty Value, so a client reads no data for it.

## 3. Hex editor and Edit Binary Value

### 3.1 Grid and Text
- 3.1.1 The hex editor peer does not expose Grid, or RowCount is not `ceil(length / 8)`.
- 3.1.2 A realized row peer has an empty name, so the bytes are not readable through UIA.
- 3.1.3 The hex editor has no Text or Value pattern: the content is not readable as text.
- 3.1.4 GridPattern.GetItem(row, column) returns the row for every column, not a byte cell, so a byte cell value
  (for example `00` at row 0, column 1) cannot be read.
- 3.1.5 The Edit Binary Value dialog hex control has an empty name instead of `Hex editor`.
- 3.1.6 Row peers of a hex row are unreachable after a scroll (the row is recycled and the peer is stale).

### 3.2 Hex row selection
- 3.2.1 SelectionItem on a hex row does not match IsRowSelected.
- 3.2.2 Text pattern ranges on a row return the wrong text (the row text is not the value of GetText).

## 4. Dialog controls (names and tab order)

The control names are the UIA Name. A decorative static text and a group frame may have an empty name only when
its label is another control's name. Every other control in these dialogs needs a non-empty Name.

### 4.1 Names
- 4.1.1 An Edit has an empty Name because its template text is empty. Affected: Edit String (value data, value
  name), Edit DWORD, Edit QWORD, Edit Multi-String, Edit Binary Value (value name), Find (Find what), Add to
  Favorites (favorite name), Export range (branch path), the rename prompt (Text).
- 4.1.2 A ListBox has an empty Name: Remove Favorites (`FavoritesList`), Binary Data viewer (`HexViewerList`).
- 4.1.3 A ColumnList (Resource Lists, Resources, Requirements List) has an empty Name.
- 4.1.4 A button Name keeps the `&` mnemonic cue (`&Find Next`).
- 4.1.5 A check box or radio Name keeps the `&` cue (`&Keys`, `&All`, `&Hexadecimal`).
- 4.1.6 A disabled template control (Display..., Data..., Preferred, Alternative) loses its Name.
- 4.1.7 The Find dialog root has the caption Name `Find` only, and its controls have no `Find` context.

### 4.2 Tab order
Order is the classic template order in `docs/parity/regedit-inventory.md` (the "Tab stops in template order" text
of each DLG row). The check presses Tab from the first control and records the focused control's automation id.

- 4.2.1 Edit String tab stops: `ValueEditorInput`, `DialogPrimary`, `DialogSecondary`, `ValueNameInput` (DLG-003).
- 4.2.2 Edit DWORD: `ValueEditorInput`, `BaseHex`, `DialogPrimary`, `DialogSecondary`, `ValueNameInput` (DLG-021).
- 4.2.3 Edit Multi-String: `ValueEditorInput`, `DialogPrimary`, `DialogSecondary`, `ValueNameInput` (DLG-028).
- 4.2.4 Edit Binary Value: hex control, `DialogPrimary`, `DialogSecondary`, `ValueNameInput` (DLG-005).
- 4.2.5 Find: `FindText`, keys, values, data, whole string, subtree, `DialogPrimary`, `DialogSecondary` (DLG-010).
- 4.2.6 Add to Favorites: `DialogTextInput`, `DialogPrimary`, `DialogSecondary` (DLG-024).
- 4.2.7 Remove Favorites: `DialogPrimary`, `DialogSecondary`, `FavoritesList` (DLG-026).
- 4.2.8 Export range panel: `All` then the branch edit (DLG-001). The branch edit is a tab stop only when enabled.
- 4.2.9 The read-only value name edit is skipped by Tab (it is a tab stop in the template).
- 4.2.10 A mnemonic static label takes focus (labels are not tab stops).
- 4.2.11 A disabled control is in the tab sequence.

## 5. High contrast

The check does not change the system setting. The E2E launch variable `BWC_E2E_HIGH_CONTRAST=1` makes the palette
behave as if high contrast were on and uses a fixed palette. The palette values are fixed in
`BwcSystemColors.FixedHighContrastPalette` (E2E builds only), so the check knows the expected colors.

- 5.1 The override is ignored, so the palette stays Light or Dark, and the check sees the wrong colors.
- 5.2 The tree selection uses the Explorer selection color, not COLOR_HIGHLIGHT.
- 5.3 The list selection fill is not COLOR_HIGHLIGHT and the selected text is not COLOR_HIGHLIGHTTEXT.
- 5.4 The focus rectangle is hard-coded (not COLOR_WINDOWTEXT).
- 5.5 A dialog face is a hard-coded dark color instead of COLOR_BTNFACE.
- 5.6 A Fluent control (button, text box) keeps the accent color instead of the HC palette.
- 5.7 A color is hard-coded to a value outside the fixed palette (a pixel from the dark palette shows up).
- 5.8 The override writes a setting (must never happen; the check reads the registry before and after).
- 5.9 Antialiasing shifts a sample off its region (the sample must be taken from the interior of a region).

## 6. Nested dialog focus (cross reference)

A child dialog opened from a dialog (Resources from Resource Lists, a detail viewer from Requirements List) closes,
and Escape then does not close the owner. This is `docs/failure-modes/classic-dialog.md` section 3.10. The
ResourceViewers check closes each nested owner with Escape, so it covers this failure.

- 6.1 The owner is left disabled after the child closes (checked through `IsEnabled`).
- 6.2 The owner is enabled but not foreground after the child closes.
- 6.3 The owner is foreground but XAML keyboard focus is outside the owner root, so the root KeyDown never runs.
- 6.4 The owner's focus is restored before the child is destroyed, and the system resets it on destroy.

These were the hypotheses before the cause was found. The cause is in `classic-dialog.md` section 3.10: Escape had no
handler on the Resource Lists and Requirements List viewers (no Cancel and no default button), with or without a child.

## Test map

| Check | Test file | Category |
| --- | --- | --- |
| 1.x tree patterns and names | `tests/Bwc.E2E/Accessibility/TreeAccessibilityTests.cs` | Accessibility |
| 2.x list grid and table | `tests/Bwc.E2E/Accessibility/ListAccessibilityTests.cs` | Accessibility |
| 3.x hex grid and text | `tests/Bwc.E2E/Accessibility/HexAccessibilityTests.cs` | Accessibility |
| 4.x dialog names and tab order | `tests/Bwc.E2E/Accessibility/DialogAccessibilityTests.cs` | Accessibility |
| 5.x high contrast | `tests/Bwc.E2E/Accessibility/HighContrastTests.cs` | Accessibility |
| 6.x nested focus | `tests/Bwc.E2E/Dialogs/ResourceViewerTests.cs` | Dialogs |

## Status

Last full run of the Accessibility, Dialogs, Theme, and Hex categories: 17 of 17 tests and 373 of 373 checks pass. The
Export range checks (4.1.1, 4.2.8) and the focus rectangle check (5.4) pass for real, with the Dynamic Annotation name
in place. Check names are in `artifacts/e2e/result-win-arm64-e2e.json`.

Failures found by the first runs and fixed (the list of ids is the check name):

- 2.2.5 and 2.2.7 (list cells): `GridPattern.GetItem` returned the row for every column. Fixed with cell peers
  (`DetailsCellAutomationPeer`), so the name cell and data cell are separate DataItems with their text. Checks
  `a11y.list.name-cell`, `a11y.list.cells-distinct`, `a11y.list.data-cell`.
- 3.1.3, 3.1.4 (hex): no Value pattern and no byte cells. Fixed with `HexCellAutomationPeer` and a Value pattern on the
  editor. Checks `a11y.hex.value`, `a11y.hex.byte-cell`, `a11y.hex.offset-cell`, `a11y.hex.ascii-cell`.
- 4.1.1 and 4.1.2 (dialog names): edits, the favorites list, and the column lists had no Name. Fixed in the templates
  (the label each one carries). Checks `a11y.*.names`.
- 4.2.x (Find, Remove Favorites): Find Next is disabled while the search text is empty, so Tab skips it (the test types
  text first). The Remove Favorites list takes focus on an item with no id (the test reads the id of the nearest parent).
- 5.x (high contrast): no E2E palette override, the Fluent theme keys of the dialog controls, and the list header
  (`LayerFillColorAltBrush`) painted outside the palette. Fixed with `BWC_E2E_HIGH_CONTRAST`, `BwcDialogResources`
  (theme keys mapped to the palette on the dialog root), and `HeaderHost.Background = BwcBrushes.Face`.
- 6.x (nested focus): see `classic-dialog.md` section 3.10. The root cause was Escape with no handler, not the owner's
  window state. Checks `resources.list.escape-without-child`, `resources.list.escape-after-child`,
  `resources.requirements.escape-after-child`.

Checked and passing without a code change: tree patterns and names, hives, Computer root, list Grid/Table counts and
headers, list SelectionItem, default row name, hex Grid counts, hex row name, dialog tab orders, the high contrast
selection fills and dialog face, and the system setting staying unchanged.

Resolved in the second pass:

- 4.1.1 Export range, branch path. The control is a shell edit box from `IFileDialogCustomize.AddEditBox`, which the
  shell does not name. `ExportBranchAnnotation` (`src/Bwc.UI/Dialogs/ExportBranchAnnotation.cs`) sets its name with
  Dynamic Annotation: `CoCreateInstance(CLSID_AccPropServices)` and `IAccPropServices::SetHwndPropStr(hwnd, OBJID_CLIENT,
  CHILDID_SELF, PROPID_ACC_NAME, "Selected branch")`. The name is the classic string without its mnemonic. IIDs and GUIDs
  were checked in the Windows 10.0.26100 SDK (`oleacc.h`). The edit is found through `IOleWindow::GetWindow` on the
  dialog, then `GetDlgItem(2006)`, or else the lowest visible Edit window (the branch path sits below the file name).
  The annotation is set on folder, selection, or retry, and cleared with `ClearHwndProps` when the dialog closes.
  Check `a11y.export.names`.
- 4.2.8 Export tab order. The shell keeps the range radios as one tab stop, so the check runs Tab 20 times and requires
  the branch edit directly after the Selected branch radio. Check `a11y.export.tab-order`. The shell exposes no
  automation ids for these controls, so the order is read from the control type and name.
- 5.4 Focus rectangle. With the tree focused, the selected row draws a dotted one-pixel rectangle in
  COLOR_HIGHLIGHTTEXT on its top edge. The densest of the top three device rows is sampled. Check
  `a11y.hc.tree-focus-rect`. The list focus rectangle is not sampled.

Open items (not faked):

- 2.2.6 and 3.2 (an unrealized row, hex selection through Text) are not exercised: the test lists are fully realized.
- The list focus rectangle (5.4, list pane) is not sampled.
- 4.1.3 (resource viewer column lists) has a name in the template, but no accessibility check reads it.
- 5.6 (Fluent buttons in the dialog) is covered by the dialog face and edit checks, not by a separate button check.
- The high contrast checks ignore a 6 pixel edge band of the client area (the window edge line), and colors with fewer
  than 8 pixels (antialiasing). Both rules are in `HighContrastTests.cs`.

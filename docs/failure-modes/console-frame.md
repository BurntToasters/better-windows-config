# Console frame: Add/Remove Columns, Customize View and Export List failure modes

Scope: piece U3a of release 0.2.0. In the shared console frame (`src/Bwc.UI/Console/**`, used by the Local Group Policy
Editor in `src/Bwc.Gpedit`): View > Add/Remove Columns (the column chooser), View > Customize View (the view switches), and
Action > Export List with its toolbar button. VIEW-1 of the plan: the View menu held Console Tree, Toolbar and Status Bar
entries with hard-coded labels; classic keeps those switches in Customize View only. The Registry Editor uses other parts of
`Bwc.UI` and must not change; the existing Registry Editor tests are its guard.

This file was written before the code (tests first). Each section lists the ways the feature can fail, the inventory row, the
capture or resource, the design choice that prevents it and the E2E check that catches it. The tests are in
`tests/Bwc.E2E/Console/` (`ConsoleColumnsTests`, `ConsoleCustomizeTests`, `ConsoleExportTests`, support in
`ConsoleFrameSupport.cs`). Every test writes `artifacts/e2e/visual/console-frame/console-frame-<test>.json` (the frame host
tests) or `artifacts/e2e/visual/gpedit/gpedit-<test>.json` (the policy editor tests), a picture, and its checks
(`console.frame.<group>.<id>`, `console.editor.<group>.<id>`, `gpedit.<group>.<id>`) to the E2E result artifact. Each export
test also copies the exported file next to its measurements and records the SHA-256 of the copy, so the run leaves a file
that can be compared again.

## Specification sources

A capture is the specification; Microsoft text is never copied: rows are named by command role and BWC's own labels, and
strings by dll and id.

| Source | What it settles |
| --- | --- |
| `F/Menus_Computer`, `F/Menus_User` (View dumps, ops 3, 9, 15, 21, 27, 33, 39, 45) | the View layout: columns command, separator, four view modes (Detail checked), separator, Customize View at the two configuration nodes and the placeholders; the filter entries between the second separator and Customize View at the nodes that have the filter (with a separator after them). Customize View is not grayed. The Action dumps: an export list entry at every node kind except with a policy row selected |
| `F/Toolbar_States` | the Export List button is visible and enabled in every dump, with a policy row selected too; the console tree toggle is visible and enabled |
| `mmcndmgr.dll` RT_DIALOG 1070 | the column chooser: 346 x 172 dialog units, an Available list (4084), Add (4085), Remove (4086), a Displayed list (4088), Move Up (4089), Move Down (4090), Restore Defaults (4087), OK, Cancel; tab order 4084, 4085, 4086, 4088, 4089, 4090, 4087, 1, 2 |
| `mmcndmgr.dll` RT_DIALOG 1086 | Customize View: 214 x 223 dialog units, nine check boxes (4143 console tree, 4144 standard menus, 4139 standard toolbar, 4141 status bar, 4142 description bar, 4145 taskpad navigation tabs, 4172 action pane, 4138 snap-in menus, 4140 snap-in toolbars) and an OK button only |
| `mmcbase.dll` strings 13347, 13348 | the file types of the Export List save dialog: ANSI text with tabs, ANSI text with commas (13347), Unicode text with tabs, Unicode text with commas (13348) |
| `mmcbase.dll` strings 13344 to 13346 | the three write errors: cannot create the file, cannot write the file, the file is read-only |
| `mmcbase.dll` strings 13436, 13448, 14086 | Export List, its status help line and its menu form |

Inventory rows: MENU-017, MENU-019 to MENU-030, CTX-002, TOOL-008, TOOL-011, TOOL-016, TOOL-017, DLG-061, DLG-073, DLG-117,
DLG-118, VIEW-1 of the plan.

Decisions of this file where the captures are silent. Every one is a BWC definition, is listed again in the audit and needs
the user's word before it counts as settled (the plan rule: a difference from classic needs the user):

- **The first column is fixed.** The captures hold no run of the chooser. BWC keeps the first column of a list (Setting, Name)
  first and shown: Remove is disabled for it, Move Up and Move Down are disabled for it, and no other column can move above
  it (Move Up is disabled for the second column).
- **Add puts the column at the end of Displayed.** Remove puts the column in Available, which is always in the list's
  definition order. Restore Defaults sets Displayed to every column in the definition order and empties Available; it works
  on the dialog and applies at OK, like every other change. Widths are not part of it.
- **Customize View has the nine classic check boxes in the classic order; the four BWC does not have are disabled.**
  BWC honors Console tree, Standard toolbar, Status bar, Description bar and Snap-in toolbars. Standard menus, Taskpad
  navigation tabs, Action pane and Snap-in menus have no part in the frame, so those four boxes are shown disabled. Open
  for the user: leave them out instead, or build them.
- **The toolbar band holds two groups.** Standard toolbar is the eight standard buttons; Snap-in toolbars is the host's
  extension buttons (the editor's Filter button). Each switch hides its group; the band is gone when both are off.
- **The description bar is the description pane of the Extended tab.** Clearing the switch hides the pane and the list takes
  its room; the Standard tab shows no pane either way.
- **The switches act at once**, with the dialog still open, and the dialog has no Cancel: OK closes it.
- **Saved state.** The policy editor settings file holds `ColumnOrder` (the column ids in display order, hidden ones
  included), `HiddenColumns` (ids), and the five switches `ViewConsoleTree`, `ViewStandardToolbar`, `ViewSnapInToolbars`,
  `ViewStatusBar`, `ViewDescriptionBar` (true when shown; absent means shown). The choice is one for the list, not one per
  node: a node whose layout has other columns applies the named ones in the saved order, and the unnamed ones follow shown.
  The first column cannot be hidden or moved by the file.
- **Export List exports the list as shown now:** every row (a selection does not narrow it), in the sort order shown, the
  filtered rows while Filter On is on, and only the displayed columns in display order. The first line holds the displayed
  column headers. Every line, the last too, ends with CR LF. Tab types join cells with a tab; a tab, CR or LF inside a cell
  becomes one space. Comma types join cells with a comma; a cell containing a comma, double quote, CR or LF is wrapped in
  double quotes with each quote doubled. ANSI types write the system ANSI code page; Unicode types write UTF-16LE with a byte
  order mark. A list with no rows exports its header line alone. The first type (ANSI tab) is the default.
- **Command ids.** Add/Remove Columns 1000 and Customize View 13231 in the View popup; Export List 13436 in the Action popup
  (the string id that names the help line, as Options 13220 and Customize View 13231 already follow). The old ids 1001 to
  1003 are gone. The frame rejects a host entry with any frame id (`ConsoleMenu.FrameIds`).

## The E2E seam

No new seam is needed. The frame host tests use the existing placeholder host (`BWC_E2E_GPEDIT_HOST=console-frame`,
`BWC_E2E_CONSOLE_ROWS`, `BWC_E2E_CONSOLE_NAMES=typeahead`): the rows are known, so the fixtures are exact (row i of N has the
name `Setting {(i * 37 + 11) % N:D5}`, the state i % 3 and the comment i % 2; typeahead names the first ten rows from a fixed
set with accented and CJK names). The list publishes `Order=` (the shown columns) and `Hidden=` in its help text, which the
tests read. The save dialog is the real common item dialog, driven with real input through `SystemFileDialogDriver` exactly as
the Registry Editor tests drive Export (there is no injected path seam). The policy editor tests use the sandbox policy
object and the oracle fixture templates (`GpeditSandbox.SeedStandard`).

| Id | Control |
| --- | --- |
| `MenuCommand_1000`, `MenuCommand_13231` | Add/Remove Columns and Customize View in the View popup (59003) |
| `MenuCommand_13436` | Export List in the Action popup (59002) |
| `ConsoleToolbar_ExportList` | the toolbar button (exists today; it becomes enabled) |
| `ConsoleColumns` | the root of the column chooser (its own top level window of the process) |
| `ConsoleColumns.Available`, `.Displayed` | the two list boxes (list items named by the column header, with the SelectionItem pattern) |
| `ConsoleColumns.Add`, `.Remove`, `.MoveUp`, `.MoveDown`, `.Restore`, `.Ok`, `.Cancel` | the buttons |
| `ConsoleCustomize` | the root of Customize View |
| `ConsoleCustomize.ConsoleTree`, `.StandardMenus`, `.StandardToolbar`, `.StatusBar`, `.DescriptionBar`, `.TaskpadTabs`, `.ActionPane`, `.SnapInMenus`, `.SnapInToolbars` | the nine check boxes (the Toggle pattern) |
| `ConsoleCustomize.Ok` | OK (there is no `ConsoleCustomize.Cancel`) |
| `ConsoleDescriptionPane` | the description pane (exists today) |

## 1. The View menu has the wrong entries or the wrong layout (MENU-019 to MENU-022, MENU-030, VIEW-1)

Capture: `Menus_Computer`, `Menus_User`, the View dumps. Classic View: columns command, separator, Large Icons, Small Icons,
List, Detail (checked at the start), separator, Customize View; with the filter entries after the second separator and a
separator before Customize View at the nodes that have the filter.

- The Console Tree, Toolbar or Status Bar entry is still in the View menu (VIEW-1); its label is hard-coded.
- Add/Remove Columns or Customize View is missing, disabled, or in the wrong place (columns first, Customize View last).
- A separator is missing, so the groups run together (the check reads the groups the separators make: 1, 4, 1 here).
- The host's entries land before Add/Remove Columns or after Customize View.
- A frame id is used twice (a host entry with 1000 or 13436), or the old ids 1001 to 1003 stay in the host.
- An existing access key lost to the new labels (the view mode keys G, M, L, D keep working).

Handling: the frame builds the View popup, `ConsoleMenu` owns the ids, the labels come from the frame's `.resw`.
Checks: `Console_Columns_Menu_Layout` (`entries`, `groups`, `noOldToggles`, `columnsEnabled`, `customizeEnabled`,
`columnsLabel`, `customizeLabel`, `detailStart`, `opens`), `Console_Customize_Menu_NoToggles` (`noToggleIds`, `noToggleNames`,
`customizeLast`, `opens`); the editor layout with the filter entries is the existing `Gpedit_Filter_Menu_View` (the order
of its entries must still hold). Changed existing tests: `Console_Shell_MenuKeyboard` (`viewItems`, `viewNoToggles`).

## 2. The column chooser has the wrong parts or layout (DLG-061, DLG-118)

Resource: `mmcndmgr.dll` RT_DIALOG 1070.

- A part is missing: either list, Add, Remove, Move Up, Move Down, Restore Defaults, OK, Cancel.
- The lists hold the wrong columns at the start (Displayed is every shown column in display order; Available is every hidden
  one; both are empty of the other's items).
- Add is enabled with nothing to add; the dialog is not modal; Escape does not close it.
- The window is far from 346 x 172 dialog units (about 519 x 279 DIP), or its parts are in another reading order (Available
  left of Displayed, Add above Remove between them, the move buttons right of Displayed, OK left of Cancel at the bottom).

Handling: one dialog from the table above, built on `ClassicDialog`. The tab order follows the reading order and is not
asserted separately (disabled buttons leave the sequence).
Check: `Console_Columns_Dialog_Layout` (`part.*`, `size`, `listsAcross`, `addRemoveBetween`, `moveButtonsRight`,
`okCancelLast`, `displayedAtStart`, `availableEmptyAtStart`, `addDisabledWithNothing`, `restoreOkCancelEnabled`, `modal`,
`escapeCloses`, `ownerEnabledAgain`, `listUntouched`).

## 3. The chooser rules are wrong (BWC definition, DLG-118)

- The first column can be removed or moved (the list would lose its label column).
- Move Up works on the second column (it passes the first), Move Down on the last, or either does something with no selection.
- Move Up and Move Down do not move the selected column by one place, or the selection does not follow it.
- Remove does not move the column to Available, or Available is not in the definition order after several removals.
- Add puts the column anywhere but the end of Displayed, or leaves it in Available.
- A change shows in the list before OK.

Check: `Console_Columns_Dialog_Operations` (`fixed.*`, `second.*`, `last.*`, `remove*`, `add*`, `available.definitionOrder`,
`onlyFixedLeft`, `cancelChangesNothing`).

## 4. OK, Cancel and Restore Defaults apply wrongly (DLG-118)

- OK does not apply the order, or applies the order and not the visibility (or the reverse); the header changes and the cells
  do not (a cell under the wrong header), or the cells change and the header does not.
- Cancel or Escape applies something.
- Restore Defaults changes the list at once, or leaves a column in Available, or keeps the order.
- The dialog opens on the definition state and not on the state last applied.
- Hiding the sorted column leaves a sort mark on a header that is not there (the list clears the sort; the chooser must
  not undo that).

Check: `Console_Columns_Apply_OkAndRestore` (`hidden.*`, `second.*`, `restore.*`, `cancelKeepsHidden`, `third.*`, `restored.*`,
`sort.*`).

## 5. The order and visibility are not saved (settings)

- The settings do not hold the column order and the hidden columns, or hold them only at close and lose them on a kill (the
  settings rule: written when the window closes and shortly after a change settles).
- The state is restored on the wrong columns, applied to the rows but not shown on the header, or lost at a node with another
  column set (All Settings has a Path column the saved state does not name: it follows the named columns, shown).
- The first column can be hidden or moved by a file; an unknown id, a repeated id or a hidden id nobody has stops the window
  or hides a real column.
- The newest choice is not the one saved (Restore Defaults after a restart keeps the old order).

Checks: `Console_Columns_Persist_AcrossRestart` (`first.applied`, `settingsFileWritten`, `settingsNameColumns`,
`second.restored`, `second.cells`, `second.allSettings`, `second.restoredDefaults`, `third.defaults`),
`Console_Columns_Persist_BadSettingsFile` (`opens`, `order`, `hidden`, `rowsShown`, `dialogDisplayed`). Not covered: the type
of a value in the file being wrong (a string where an array goes); the existing settings rule treats the file as one unit.

## 6. Customize View has the wrong parts or layout (DLG-073, DLG-117)

Resource: `mmcndmgr.dll` RT_DIALOG 1086.

- A check box is missing, or they are in another order (console tree, standard menus, standard toolbar, status bar,
  description bar, taskpad tabs, action pane, then the snap-in group: menus, toolbars), or the names do not name the role.
- The dialog has a Cancel button (classic has OK only), or OK does not close it.
- The five switches BWC honors start unchecked or disabled; one of the four that BWC does not have is enabled and does nothing.
- The window is far from 214 x 223 dialog units (about 321 x 362 DIP), or is not modal.

Check: `Console_Customize_Dialog_Layout` (`part.*`, `noCancel`, `name.*`, `size`, `order`, `okLast`, `built.*`, `unbuilt.*`,
`modal`, `okCloses`).

## 7. A switch does not act at once, or does not match the toolbar (TOOL-008, TOOL-017)

- A switch acts only at OK, or acts when its dialog closes by Escape and not by OK.
- Console tree hides the tree and leaves the splitter, the list does not fill the room, or the keyboard focus is lost in a
  hidden pane (the shell already moves it; the check reads that the result pane stays).
- Standard toolbar or Status bar hides the part and the panes do not move, or hiding the toolbar hides the menu bar.
- Description bar hides the pane and the list stays narrow.
- The tree toggle button and the Console tree box disagree (the button hid the tree, the box still shows on).
- After OK a hidden part comes back by itself, or ticking a box does not bring its part back.

Checks: `Console_Customize_Switches_ActAtOnce` (`start`, `button.hides`, `sync.*`, `tree.off`, `tree.listWider`, `toolbar.off`,
`menuBarStays`, `status.off`, `description.*`, `afterOk.*`, `second.*`, `tree.on`, `toolbar.on`, `status.on`, `description.on`,
`restored`). Changed existing test: `Console_Shell_ShowHide` hides the toolbar and the status bar through Customize View
(`toolbarHidden`, `statusHidden`, `textSetWhileHidden`).

## 8. The snap-in toolbars switch is wrong

- Snap-in toolbars off leaves the Filter button, or hides the standard buttons with it.
- A node change brings the Filter button back while the switch is off, or shows it at a node that has no filter while the
  switch is on (TOOL-015 stays the rule for where the button exists).
- Standard toolbar and Snap-in toolbars both off leave an empty band.
- The filter menu entries stop working with the button hidden.

Check: `Console_Customize_SnapInToolbars_Editor` (`start`, `snapIn.off`, `snapIn.standardStays`, `standard.off`,
`standard.on`, `standard.filterStaysOff`, `node.filterHidden`, `node.filterToggleAction`, `again.on`, `noFilterNode`).

## 9. The switches are not saved, or a saved choice locks the user out

- The switches are lost at restart (the old View menu toggles were never saved).
- Only some of the five are saved, or a saved choice is applied to the parts and not to the dialog boxes (the box shows on, the
  part is hidden).
- The newest choice is not the one saved.
- A file that hides every part leaves no way back: the menu bar or View > Customize View is gone, or the boxes show on.

Checks: `Console_Customize_Persist_AcrossRestart` (`first.applied`, `settingsFileWritten`, `settingsKeys`, `second.hiddenAgain`,
`second.toolbar`, `second.boxes`, `second.restored`, `third.allShow`), `Console_Customize_Persist_EverythingHiddenInFile`
(`allHidden`, `menuBarStays`, `customizeOpens`, `boxesOff`, `back`).

## 10. Export List is missing, extra, or wrongly enabled (MENU-017, MENU-010, TOOL-011, TOOL-016)

Capture: the Action dumps and `Toolbar_States`.

- The Action menu lacks Export List at a node kind (configuration nodes, placeholders, Administrative Templates, categories,
  All Settings, both sides), or has it with a policy row selected (classic has none there).
- Export List is grayed, labelled differently from the Export List words, or sits after Help (Help stays last).
- The entry stays after the policy row selection went (a stale selection), or is missing after it came back.
- The toolbar button stays disabled (it is disabled today), is disabled at a node kind, or is disabled with a policy row
  selected (TOOL-016: the toolbar states do not follow the selection).
- The Registry Editor gains the entry through the shared frame code.

Check: `Console_Export_Menu_NodeKinds` (`<kind>.present`, `.label`, `.helpLast`, `.beforeHelp`, `.button`, `policyRow.absent`,
`policyRow.button`, `rowGone.*`). Changed existing test: `Console_Shell_ToolbarStates` expects Export List enabled at the
start (TOOL-011). Not covered: the list context menus, which hold an export entry at some nodes (CTX-001, CTX-005; CTX-004
holds none): that is a G2 menu and stays with it.

## 11. The save dialog is wrong, or Cancel writes (mmcbase.dll 13347, 13348)

- The dialog has other file types, another order, or opens on another default than the first (ANSI tab).
- It is not modal, or Cancel leaves the main window disabled.
- Cancel (Escape) writes a file, or changes the list.
- The command works once and not a second time (a dialog left behind).

Check: `Console_Export_Dialog_Types` (`button.enabled`, `opens`, `modal`, `default`, `type.2` to `type.4`, `cancel`,
`ownerEnabledAgain`, `cancelWritesNothing`, `listUntouched`, `buttonStillWorks`). Every other export test records `<id>.dialogOpened`,
`<id>.fileType` and `<id>.saved` for the dialog it drives.

## 12. The exported text is wrong (BWC definition)

The format is in the decisions above. No capture holds an exported file.

- The header line is missing, holds hidden columns, or holds the columns in definition order instead of display order.
- The rows are in the caller's order and not the order shown, or only the selected rows are written, or the filtered-out rows
  are written.
- A line ends with LF alone, or the last line has no line end.
- A comma type writes a cell containing a comma, quote, CR or LF without CSV quoting, doubles no quote, or splits a line break
  inside one cell into extra records.
- A tab type quotes, or leaves a tab or line break inside a cell.
- A Unicode type has no byte order mark, writes UTF-8, or damages the accented and CJK names; an ANSI type writes UTF-16.
- A list with no rows writes nothing, or fails.
- The export changes the list, the selection, the sort, a policy file or the settings.

Checks: `Console_Export_File_Ansi` (`tab.fixture`, `comma.fixture`, `listUntouched`),
`Console_Export_CommaCellQuotesLineBreaks` (a two-line cell quoted inside one CSV record), `Console_Export_File_Unicode` (`tab.bom`,
`tab.fixture`, `comma.fixture`), `Console_Export_File_FollowsTheView` (`sorted`, `columns.applied`, `selected`, `fixture`,
`selectionKept`), `Console_Export_File_EmptyList` (`button.enabled`, `fixture`), `Console_Export_Editor_SharedKeys`
(`rows`, `tab.fixture`, `comma.fixture`, `policyFilesUntouched`, `listUntouched`), `Console_Export_Editor_FilteredList`
(`filter.on`, `filter.rows`, `fixture`, `filterStaysOn`). Each compares the file with a fixture of
`tests/Bwc.E2E/Console/ExportFixtures` byte for byte; the notes name the first differing byte and the two sizes, never the
text. `Console_Export_CommaCellQuotesLineBreaks` uses the E2E multiline-comment seam and compares the exported file byte for
byte; a tab inside a cell and a long text (the E2E long-comment seam is 5,000 letters and would need its own fixture) remain
open.

## 13. A write failure is not reported (mmcbase.dll 13344 to 13346)

- The file cannot be created, cannot be written, or is read-only, and the export says nothing, closes the window, or leaves a
  partial file named as the target.
- The failure leaves the main window disabled.

Handling: write to the chosen path in one operation; on a failure show a BWC message for each of the three causes and leave
nothing behind. No E2E check yet: reaching the failure needs the overwrite confirmation of the system dialog (the common
dialog asks before it returns an existing path) and a file the process cannot write, and no driver for that confirmation
exists in the harness. This section is open for the code piece or the manager to close with a driver or a seam.

## Safety

- Every file the tests create is inside the run data root of the test (`BWC_DATA_ROOT` scope `console-<name>`): the
  settings file, the policy sandbox, the export folder. The export artifact copies go to `artifacts/e2e/visual`.
- No test touches the real registry, the real GroupPolicy folder or `%ProgramData%`; the policy editor runs on the sandbox
  policy object (`GpeditSandbox`) and the Local Group Policy hive of the sandbox only.
- No test elevates or raises a UAC prompt; the E2E builds are `asInvoker`.
- The save dialog is the real system dialog, driven with real input; the tests only type paths under the run folder.
- The Registry Editor tests are the guard that the shared frame code did not change that tool: the full E2E run (shared
  `Bwc.UI`) is part of the piece's gate.
- The checks never print file text; they print sizes, ids and the first differing byte.

## Declared differences

All of these are BWC definitions the captures cannot settle; the audit lists them with the open question.

1. The first column is fixed (cannot be removed or moved; nothing moves above it).
2. Add appends; Restore Defaults restores order and visibility only.
3. Four Customize View boxes are shown disabled (Standard menus, Taskpad navigation tabs, Action pane, Snap-in menus).
4. The toolbar band has two switchable groups; the description bar is the description pane of the Extended tab.
5. The exported text format (header line, quoting, CR LF, encodings, header-only for no rows) and the filter default.
6. The saved-state keys and the rule that one saved column choice serves every node.
7. Export List in the Action popup is placed before Help; the context menus are not part of this piece.

# Local Group Policy Editor commands failure modes

Scope: piece G2 of release 0.2.0. In `src/Bwc.Gpedit` and `src/Bwc.Gpedit.App`: the Action menu, the context menus of the
console tree and the result list, the toolbar commands (Properties, Refresh, Help and the history buttons), the keys (F5, F1,
Alt+Enter, Enter, Delete, Backspace, Alt+Left, Alt+Right, Alt+Up), Refresh that reloads the model, the placement of the
window before its first show, and the findings G2-1, GPE-4, GPE-2, GPE-5, EVT-1 and DOC-2 of the plan. Filter, Filter
Options, Add/Remove Templates and Export List are other pieces (G5, G6, U3a) and have no test here.

This file was written before the code (tests first). Each section lists the ways a command can fail, the design choice that
prevents it and the E2E check that catches it. The tests are in `tests/Bwc.E2E/Gpedit/Commands/`. Every test writes
`artifacts/e2e/visual/gpedit/gpedit-<test>.json` and a picture of the client area, and its checks (`gpedit.<group>.<name>.<id>`)
to the E2E result artifact.

## Specification sources

The classic captures are the specification of what exists in classic: `docs/parity/oracle/gpedit/goldens/F/Menus_Computer`,
`F/Menus_User`, `F/Toolbar_States`, `F/Keys_List`, `F/Keys_Tree`, `F/Keys_ListAllSettings`, and the rows MENU, CTX, TOOL, ACC
and BEH of `docs/parity/gpedit-inventory.md`. Menu entries are compared with the captures by command role, never by Microsoft
text: the labels are BWC's own and live in the piece's own `.resw`. What the captures show, by role:

| Role | Classic (captures) | BWC at G2 |
| --- | --- | --- |
| Edit (policy setting dialog) | Action menu and list context menu with a policy row selected only, first entry, enabled (MENU-010, MENU-012, CTX-007) | the same |
| Help (plain entry) | last entry of the Action menu and of every list context menu, enabled (MENU-018, CTX-001) | the same, in both context menus and the tree menu |
| Help Topics | Help menu, enabled (MENU-033); F1 is the accelerator of the help command (ACC-002) | the same |
| Refresh | no entry in any dump; F5 opens no window and no menu (ACC-004, ACC-027, ACC-044) | BWC addition (BEH-022): Action menu and all three context menus, enabled at every node kind |
| Export List | every node kind but a policy row (MENU-017) | not in G2: no handler exists; owned by U3a |
| Properties button | hidden in the classic toolbar (TOOL-009) | hidden (A3a-1, section 11.12); Edit stays in the Action menu, the policy row menu and Alt+Enter |
| Refresh button | hidden in the classic toolbar (TOOL-009) | shown, enabled at every node kind |
| Back, Forward, Up, Show/Hide tree, Help buttons | Back enabled with history, Forward disabled until Back, Up enabled at every non-root node, tree toggle and Help enabled (TOOL-004 to TOOL-014, TOOL-016) | the same |

Not captured, so decided here and listed in the audit (CTX-009): the context menu of the console tree holds Refresh and Help
only (A3a-1 left it as it is). Selection by a right click on a tree node is not asserted.

## The E2E seam

Compiled only under `BWC_E2E`. The readout is the UIA help text of `GpeditRoot` (`Key=Value;`, see
`gpedit-tree-list.md`, section 13). G2 adds four keys to the existing ones (`Node`, `Rows`, `Edits`, `LastEdit`, `Loaded`,
`Problems`, `Failures`, `ProblemList`, `Page`, `Status`):

| Key | Meaning |
| --- | --- |
| `Refreshes` | the number of reloads that finished and were applied (0 after the first load) |
| `Reloading` | 1 while a reload is in flight, otherwise 0 |
| `HelpRequests` | the number of times the Help command ran; in the E2E flavor it opens nothing and only counts |
| `LastCommand` | the role of the last command that ran: `Edit`, `Refresh`, `Help`, `Back`, `Forward`, `Up` |

Command ids (the same id in the menu bar, where the automation id is `MenuCommand_<id>`, and in the context menus, where it is
`ContextMenuCommand_<id>`, so that one command is one id; CTX-011): Edit 2001, Refresh 2002, Help 2003. Help Topics keeps the
frame id 12804. The Action popup is the frame id 59002.

## 1. The Action menu is empty or has the wrong entries (G2-1)

The Action popup is empty today. The entries depend on the node and on the selected row, and classic builds them again every
time the popup opens.

- At a node with no row selected: no Edit, Refresh enabled, Help enabled and last.
- With a policy row selected: Edit first and enabled, Refresh, Help last.
- With a folder row selected, or after the node changed and the selection was cleared: no Edit. A menu that is built once
  and kept shows Edit at a folder (stale selection).
- Computer Configuration and User Configuration give identical menus at every node kind (CTX-008).
- A disabled entry that looks usable, or an enabled entry that does nothing. Every enabled entry runs its command.

Handling: the popup is rebuilt from the current node and the selected row each time it opens (the menu bar's opening event),
and an entry is enabled only when its command can run. Checks: `Gpedit_Menu_Action_NodeKinds`, `Gpedit_Menu_Action_PolicyRow`
(`nodes`, `edit`, `folderRow`, `staleSelection`), `Gpedit_Menu_Action_Commands`, `Gpedit_Menu_Help_Topics`.

## 2. A command acts on the wrong item

- The Action or toolbar Edit opens the dialog of the row that was selected before, not the current one.
- A right click on a row that is not selected edits the selected row instead of the clicked one (the list selects the row on
  a right click, as every list view does).
- After the node changed, Edit still names the policy of the previous node.
- Alt+Enter, Delete or Enter with the keyboard focus in the console tree acts on the row selected in the list.
- The tasks of a context menu run the command of the Action popup with another target.

Handling: one command object per role reads its target (selected policy row, current node) when it runs, never when it was
built. A key command checks which pane holds the focus. Checks: `Gpedit_ContextMenu_List_PolicyRow` (`clickedRowEdited`),
`Gpedit_Keys_WrongPane`, `Gpedit_Menu_Action_PolicyRow` (`staleSelection`), `Gpedit_Menu_Action_Commands`.

## 3. The context menus (G2-1: requests are dropped)

`OnContextMenuRequested` drops every request today. Classic opens the menu on a right click, on the context-menu key and on
Shift+F10, and a dismissed menu leaves the focus where it was (CTX-011).

- The list menu with a policy row: Edit first, Refresh, Help last. Without a row, or with a folder row: no Edit.
- The key opens the menu for the focused row, a right click for the clicked row, with no row for the empty area (CTX-008).
- The tree menu: Refresh and Help, no Edit; Shift+F10 and the right click give the same menu.
- Escape closes the menu and the focus stays in the pane it was in; the command of an entry does not run.
- A second request while a menu is open opens a second menu, or throws.

Checks: `Gpedit_ContextMenu_List_PolicyRow` (`menu`, `key`, `shiftF10`, `dismiss`), `Gpedit_ContextMenu_List_NoRow`,
`Gpedit_ContextMenu_Tree`.

## 4. The toolbar states and commands (G2-1: Properties and Refresh do nothing)

Today Properties and Refresh are enabled and do nothing, and Help is disabled.

- Back is enabled with history and disabled at the start; Forward is enabled only after Back and ends when a new node is
  selected. Up is disabled at the root and enabled at every other node (TOOL-004 to TOOL-007).
- Show/Hide Console Tree and Help are enabled at every node kind (TOOL-008, TOOL-013); Help runs the Help command.
- Properties is hidden, as in classic (TOOL-009, section 11.12); it was enabled only while a policy row was selected. Edit
  runs from the Action menu, the policy row menu and Alt+Enter.
- Refresh is enabled at every node kind and with a row selected, and reloads (section 5).
- A state that is set once at the first selection and never again (a node kind that changes it, a row selection).

Handling: `UpdateToolbarStates` runs after a node change, a row selection change, a reload and the end of a dialog. Checks:
`Gpedit_Toolbar_States` (one check per button and node kind), `Gpedit_Toolbar_History`, `Gpedit_Toolbar_Commands`.

## 5. Refresh does not reload, or reloads wrongly (G2-1, GPE-4)

`_loadCancel` is a one-shot token source that is never disposed, so the model cannot load a second time.

- F5, the toolbar button and the menu entry do nothing, or reload only the first time. A second F5 fails.
- The reload reads only the policy files and not the template set (BEH-022 reads both).
- The reload writes a policy file, a comment file or the version file. Refresh is read only.
- The selection is lost: the tree goes back to the root, the list loses its selected row, the expanded nodes collapse, the
  status text and the Extended pane are blank until the next click.
- The selected node is gone in the new template set: the window keeps a node of the old model. The nearest surviving
  ancestor is selected.
- A burst of Refresh requests starts several loads that race, the older one finishes last and its model wins, or the load
  count grows without end. A request while a load is in flight is merged: the last request wins, `Reloading` returns to 0.
- A load leaks: the token source or the task of the replaced load is not cancelled and disposed.
- A reload fails (an unreadable policy file, a broken template): the window shows the new problems, keeps working, and the
  next Refresh after the file is fixed recovers. The failure of a reload never throws into a task that nobody observes
  (the `finally` of `RunPolicyDialogAsync` has the same fault).
- The disk is changed and the window shows the new state before any Refresh: classic reads on request only.
- A reload while the setting dialog is open: the dialog stays on the model it was opened with and stays open; the
  main window does not swap its store under the dialog. The reload is refused or deferred until the dialog closes, and
  Refresh works after it closes.
- Event handlers of the replaced model stay subscribed, so one Enter raises two edit requests after two reloads.

Handling: one reload gate with a per-load token source that is cancelled and disposed when the next load starts or the view
unloads; the new model replaces the old in one step on the UI thread; the selection is kept by node path and row tag.
Checks: `Gpedit_Refresh_Reloads` (`unchangedBeforeRefresh`, `firstRefresh`, `secondRefresh`, `filesUntouched`, `backForward`),
`Gpedit_Refresh_Templates`, `Gpedit_Refresh_KeepsSelection`, `Gpedit_Refresh_VanishedNode`, `Gpedit_Refresh_Burst`,
`Gpedit_Refresh_Failure`, `Gpedit_Refresh_WhileDialogOpen`, `Gpedit_Keys_F5`.

## 6. History keeps nodes of a replaced model (GPE-5)

`_history` has no limit and holds `TreeNode` objects of the model that a reload replaced.

- After a reload Back selects a node that is no longer in the tree: nothing is shown, or the list of the old model is shown.
- Back after a reload shows the state of the old model (the nodes carry the model).
- The history grows without bound while a user navigates.
- Back and Forward after a reload walk to a node whose category no longer exists.

Handling: the history stores node paths, not nodes; a path that does not resolve in the new model is dropped; the list is
capped (the check only requires that Back walks to existing nodes). Checks: `Gpedit_Refresh_Reloads` (`backForward`),
`Gpedit_Refresh_VanishedNode` (`historyExists`), `Gpedit_Toolbar_History`.

## 7. Keys reach the wrong pane or do the wrong thing (G2-1)

F5, F1, Alt+Enter, Delete and Alt+Up have no handler today. The captures show:

- F5 opens no window and no menu and keeps the selection, in the list and in the tree (ACC-027, ACC-044). In BWC it
  reloads (section 5).
- Enter on a policy row opens the dialog (ACC-048); Enter on a folder row opens the folder. Alt+Enter on a policy row opens
  the same dialog; on a folder row or in the tree it does nothing.
- F1 is the help command (ACC-002): it runs the Help command in any pane. The E2E check asserts only that the command fired
  once, through `HelpRequests`; no key opens two help requests.
- Delete: classic has no Delete entry in any menu and no capture shows a window or a write; Delete does nothing in the list
  and in the tree. A Delete that edits, clears a setting or writes a policy file is the failure.
- Backspace in the tree selects the parent node (ACC-024), it is not a history key. Alt+Left and Alt+Right walk the history
  (the console frame already raises Back and Forward for them). In the captures Alt+Left, Alt+Right and Alt+Up pressed with the
  focus on the result side change nothing (ACC-030 to ACC-032); Alt+Up with the focus in the result pane changes nothing.
- A key press that is handled in one pane but also reaches the other (Enter edits and also opens a folder).
- A key handled while a menu or the dialog is open.
- Ctrl+A in the policy list selects every row, so the description shows the folder and not the policy (ACC-043). The policy
  list is single-select (ConsoleList.MultiSelect is off in Bwc.Gpedit): Ctrl+A does nothing there, and Shift or Ctrl with a
  click or an arrow key selects only the row it reaches. Check: Gpedit_Keys_ListInert.

Checks: `Gpedit_Keys_F5`, `Gpedit_Keys_Edit`, `Gpedit_Keys_WrongPane`, `Gpedit_Keys_F1`, `Gpedit_Keys_Delete`,
`Gpedit_Keys_History`.

## 8. The window placement jumps after the first show (GPE-2)

The editor shows its window at the default place and moves it to the saved place in a low priority dispatch after `Loaded`, so
the user sees the window jump. The Registry Editor applies `Bwc.UI` `WindowPlacement` before the first show.

- The first visible rectangle of the window is not the saved rectangle; a second rectangle follows.
- The saved rectangle is applied twice with two results, or never (the final rectangle is the default).
- A saved rectangle that is off every display is shown off screen (the existing `Gpedit_Persistence_BadSettingsFile` covers it).
- The placement code is a copy of the shared one and drifts from it (DOC-2: the comments say the placement is a later piece).

Handling: the head reads the settings and calls the shared placement before `Activate`; the view no longer moves the window.
Check: `Gpedit_Window_PlacementBeforeShow` samples every visible top-level window of the process from before its start and
requires the first visible rectangle to be the saved one and no other rectangle afterwards.

## 9. Event handlers are never unsubscribed (EVT-1)

`BwcTheme.Changed` is a static event and the view subscribes a lambda that nothing removes; `AppWindow.Closing` and `Changed`
are never removed either. A view that is replaced or closed stays alive and its handlers keep running. A reload that
re-subscribes its handlers without removing the old ones doubles every command.

Handling: named handlers, removed in `Unloaded` and when a model is replaced. Checks: one Enter raises exactly one edit and one
tree move adds exactly one history step after repeated reloads (`Gpedit_Refresh_Burst`, `noDuplicateEdit`,
`noDuplicateHistory`); the placement and close paths are covered by the persistence tests.

## 10. Stale comments and wording (DOC-2)

The comments of `MainWindow.xaml.cs` and `GpeditRootView.xaml.cs` still say the commands and the window state are later
pieces, and the fixture help text says Disabled writes 0 while the captures show a delete record. They are corrected in the
code dispatch; there is no E2E check. The fixture wording is not a check here: the existing dialog tests read it.

## 11. The context menu of the result pane is not the classic menu (A3a-1: CTX-001 to CTX-006, TOOL-009)

Before A3a-1 the menu of the result list with no policy row builds Refresh, a View submenu that holds the filter entries (at the
nodes that have the filter) and Help. The captures (F/Menus_Computer, F/Menus_User, the menu key with no row selected) give
more: an Export List entry, a View submenu with the columns command, the four view modes (the current one checked), the filter
entries between the modes and Customize View, an icon arrangement submenu, a line-up entry and Help, with separators between
the groups. The tree menu (CTX-009, a BWC definition), the folder row menu and the policy row menu (CTX-007) are not part of
this section and keep their entries.

Layout by node kind, from the captures (a separator between the groups; the Refresh entry is the DIFF-007 addition, placed
after the line-up entry, in the group of the arrangement entries):

| Node | Top level | View submenu |
| --- | --- | --- |
| Computer or User Configuration, Windows Settings | Export List, View, Arrange Icons, Line Up Icons, Refresh, Help | columns, four modes, Customize View |
| Administrative Templates, a category, All Settings | the same | columns, four modes, the three filter entries, Customize View |
| Software Settings (a placeholder page) | View, Arrange Icons, Line Up Icons, Refresh, Help (no Export List) | columns, four modes, Customize View |

Ways it fails, with the design choice that prevents each and the check that catches it. Tests: `Gpedit_ContextMenu_Layout_NodeKinds`,
`Gpedit_ContextMenu_Layout_Commands`, `Gpedit_Toolbar_PropertiesHidden`, in `tests/Bwc.E2E/Gpedit/Commands/GpeditContextMenuLayoutTests.cs`.
Check ids are `gpedit.contextlayout.nodes.<side>.<kind>.<id>` and `gpedit.contextlayout.commands.<id>`.

11.1 The empty area menu lacks the entries of the capture (Export List, the View content, the arrangement entries). One builder
makes the menu for every node kind from a per-kind description, so a kind cannot keep an old list. Checks: `top.ids`, `view.ids`,
`arrange.ids` at each kind.

11.2 The entries are in the wrong order, or Help is not last. The order is the capture's, with Refresh after the line-up entry.
Checks: `top.ids` (exact id sequence), `top.helpLast`.

11.3 The separators are missing, doubled or in the wrong place (the filter group and its separator show at a node without the
filter, or a separator stays behind with no group). The groups are compared with the groups of the capture, read from the golden
file by the test. Checks: `top.groups`, `view.groups`, `arrange.groups`.

11.4 Export List shows at Software Settings (the capture has none there) or is missing at the other nodes. Checks: `top.ids`.

11.5 The filter entries show in the View submenu at a node without the filter, or are missing at a node with it, or Re-Apply
Filter is not grayed while the filter is off. Checks: `view.ids`, `view.grayed`.

11.6 The state of the entries is the state when the menu was built long ago: the checked view mode is the mode of the list now
(the menu is built for each request), and the arrangement toggle shows its state. Checks: `view.checked` against the capture
(Detail checked), `afterMode.checked` (the new mode is checked after a mode change from this menu), `autoArrange.checked`.

11.7 An entry has no command behind it, or a command that differs from the one of the menu bar: a view mode from the menu
sets the list mode (the list readout `Mode`), Add/Remove Columns and Customize View open their dialogs, Export List opens the
save dialog, Line Up Icons counts (`LineUps` in the readout) and changes neither the mode, the selection nor the rows, Auto
Arrange toggles (`AutoArrange`). The menu closes after a command. Checks: `mode.runs`, `columns.opens`, `customize.opens`,
`export.opens`, `lineUp.runs`, `lineUp.keepsMode`, `autoArrange.runs`, `menuCloses`.

11.8 An entry shows the resource key instead of a label (a missing string) or has no label. Checks: `labels` (no label equals
its key and the BWC words are present).

11.9 The Computer and the User side differ, or only one side is built. The test runs both sides, with the same expectations.
Checks: every check above, with `user` in the kind.

11.10 The menu key (no row) and the right click give different menus. Check: `key.ids`.

11.11 BWC has no icon arrangement: the list lays its items out in item order in a grid, with no free placement and no drag. The
arrangement entries run over the existing view modes: Line Up Icons lays the grid out again (the list keeps its mode, scroll
position and selection) and Auto Arrange is a check item that lays the grid out again when it is checked. This is recorded in the audit (CTX-003) and is the one place where an entry has no visible effect beyond the layout
pass. Checks: `lineUp.runs`, `lineUp.keepsMode`, `autoArrange.runs`.

11.12 The Properties toolbar button shows although classic hides it (TOOL-009, F/Toolbar_States: entries 6 to 9 and 11 to 14
are hidden in every dump). The editor hides the button for good; the Edit command stays in the Action menu, the policy row
menu and Alt+Enter. The visible standard buttons are those of the capture, in the capture's order, plus Refresh
(DIFF-007) and without the action pane toggle (TOOL-014, declared). Checks: `Gpedit_Toolbar_PropertiesHidden` (`absent` at
each node kind and with a policy row selected, `visible` is the exact list), `Gpedit_Toolbar_Commands` (`editViaMenu`).

11.13 The hidden button is hidden only until the first selection change (the visibility is reset by the next update of the button
states). Check: `Gpedit_Toolbar_PropertiesHidden` selects a node, a policy row and a folder row and looks each time.

Declared with no code (the manager, user to confirm): CTX-010 (no menu on a column header or on the description pane: not
captured, and BWC has no commands for them), TOOL-014 (no action pane toggle: BWC has no action pane), ACC-001, ACC-003,
ACC-005 and ACC-008 (Ctrl+P, Ctrl+F10, Ctrl+F5 and Ctrl+Alt+Shift+F7 are not bound: not captured, and the multiple-window console
keys have no meaning in BWC's single-window frame).

## Decisions of the code dispatch

- A Refresh while the setting dialog is open is refused (logged, no state change, `LastCommand` keeps its value). A load that
  finishes while the dialog is open is dropped, so the model under the dialog is never replaced.
- A Refresh while a reload is in flight cancels it and starts one more load; the replaced load is never applied, so a burst
  gives one applied reload (`Refreshes` counts applied reloads only). A Refresh during the first load waits for it, then loads
  again.
- The history holds the nodes of the tree in use, at most 100. A reload maps each entry to the node with the same names in the
  new tree and drops the ones that no longer exist; the current entry is the node that is selected after the reload. A history
  step to a node that the tree cannot show (an ancestor was collapsed since) leaves the selection where it was and drops
  the entry.
- The Action popup is built once with Edit, Refresh and Help; Edit is shown only while a policy row is selected (a removed
  popup entry stays in the popup's visual tree, so hiding is used). The context menus are built new for each request.
- Help in the ship flavor shows the native About box (DIFF-006, the Registry Editor's Help); in the E2E flavor it only counts.
- Alt+Up goes up one level while the console tree holds the focus; with the focus in the result pane it does nothing (ACC-032).
- The window placement is applied by the head before the first show with the Registry Editor's `WindowPlacement` source file,
  which the head compiles by link. It lives in `Bwc.Regedit`, not in `Bwc.UI`; moving it to `Bwc.UI` is a later cleanup.

## Safety and repeatability

The tests run on the E2E flavor only, with the sandbox policy object, the F1 fixture templates (or a copy of them under the run
data root) and the run data root of the scope; nothing writes the real policy folder, the real registry or `%ProgramData%`. The
tests never elevate and never prompt. A test that changes a policy file on disk uses `GpeditSandbox` (the guard
`scripts/check-policy-writer.js` allows no write API next to a policy file name in a test class). Every test records its
checks and measurements as `artifacts/e2e/visual/gpedit/gpedit-<test>.json` and `artifacts/e2e/result-<rid>-<flavor>.json`, which
is the repeatable artifact of AGENTS.md.

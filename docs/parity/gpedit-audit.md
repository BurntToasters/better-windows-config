# Better Windows Config: gpedit parity audit

## Method

This file holds the verdicts for `docs/parity/gpedit-inventory.md` (release 0.2.0, branch `next-0.2.0`). Each of the 534 inventory rows has one line below, and the declared differences are listed again in their own section. Every verdict started as `open`. The audit piece (A1) and the pieces that build each feature move a row to a verdict, in this file and in the inventory together, so the Status column of the inventory stays equal to this file.

Verdicts:

- `pass`: a named test or golden proves the row. E2E tests are named by method (`tests/Bwc.E2E`), engine tests by class (`tests/Bwc.Policy.Tests`, `tests/Bwc.Gpo.Tests`). A row that states no behavior says so.
- `declared-difference`: BWC differs on purpose. The reason is in the row.
- `gap`: not proven or not built. The Piece column names the work piece that closes it.
- `removed`: the row was retired by a user decision. It carries the reason and needs no further verdict.
- `open`: not judged yet.

Inputs for the verdicts: the capture goldens under `docs/parity/oracle/gpedit/goldens` (336 cases), the static dump, `string-parity.json` for MSG rows, and the visual review against the group F captures. The recapture of the 11 CAP-4 cases changes some recorded bytes; a POL or VIEW row that cites one of them stays `open` until the recapture is committed.

When a piece closes a gap, it changes the row here and in the inventory to `pass` with its test, or to `declared-difference` with the reason.

State after A1 (2026-11-22, HEAD 5ccff10): every row has a verdict. E2E evidence is the latest full arm64 E2E flavor run (`artifacts/e2e/result-win-arm64-e2e-full.json`, from commit a28ee65) and the later filtered runs; every test named here passed. The U3b tests (`Console_Options_*`, `Gpedit_Help_*`) came after the full run and passed in filtered runs, marked "filtered run". Engine classes passed in the latest quick gate. MSG counts are those of the current `string-parity.json`; the counts in the inventory's oracle column are older. The 7 `open` rows wait for the CAP-4 recapture (`tests/Bwc.Policy.Tests/Golden/awaiting-recapture.txt`).

## Totals

| Section | pass | declared-difference | gap | removed | open |
| --- | --- | --- | --- | --- | --- |
| MENU | 36 | 4 | 0 | 1 | 0 |
| CTX | 3 | 1 | 7 | 0 | 0 |
| TOOL | 15 | 0 | 2 | 0 | 0 |
| DLG | 35 | 88 | 0 | 0 | 0 |
| ACC | 24 | 2 | 24 | 0 | 0 |
| MSG | 3 | 73 | 17 | 0 | 0 |
| VIEW | 28 | 14 | 1 | 0 | 0 |
| TREE | 16 | 0 | 3 | 1 | 0 |
| BEH | 23 | 4 | 1 | 1 | 0 |
| POL | 63 | 0 | 5 | 0 | 7 |
| ADMX | 32 | 0 | 0 | 0 | 0 |
| All (534) | 277 | 186 | 61 | 3 | 7 |

## Gaps

Engine rules that no capture settles are listed first (E6c). Each has a pointer comment in the source. The gap rows of the audit follow them.

| ID | Rule | What is missing | Where |
| --- | --- | --- | --- |
| GAP-01 | The value-name prefixes (`**del.`, `**soft.`) removed with their value when a value is replaced or cleared. | No capture sets or clears a value that has a soft or delete record next to it and then saves. | `PolOrderRules.PerValueInstructionPrefixes` |
| GAP-02 | The flag of the refresh call after a save. | The editor does not show which call it makes. | `UserenvPolicyRefresher` |
| GAP-03 | An additive list keeps the foreign values of its key. | B/Pol_List_Additive starts empty; no start has a foreign value at an additive key. | `PolicyRules.AdditiveListKeepsOtherValues` |
| GAP-04 | Enabling an additive list removes a delete-values record of its key. | No start has a delete-values record at an additive key. | `PolicyRules.AdditiveListRemovesDelvals` |
| GAP-05 | A tail after the last record that is not one stray character (odd length, several characters, a lone NUL) is refused. | D/File_TrailingBytes has one stray character only. | `PolReader.IsStrayTail` |
| GAP-06 | A key that a new record joins takes the spelling of its ancestors; on load two spellings of one key merge under the first. | D/Case_KeyDiffers and D/Case_KeyTwoSpellings show one level; deeper trees and ties between several first spellings are not captured. | `PolDocument.SpellNewKey` |
| GAP-07 | The namespaces that stay in comment.cmtx after the first save of a session. | Only the one-namespace case is captured (A/Machine_Enabled, A/User_Enabled). | `CmtxDocument.RestrictNamespacesTo` |

Gap rows of the audit (A1). Each line names the row, what is missing and the piece that closes it: A3 for a fix or a test after this audit, A2 for the elevated run (a real save with refresh). Gap rows by section: CTX 7, TOOL 2, ACC 24, MSG 17, VIEW 1, TREE 3, BEH 1, POL 5, ADMX 1.

| Row | What is missing | Piece |
| --- | --- | --- |
| CTX-001 | The empty area menu has no export list, no view modes in its View submenu, no icon arrangement submenu and no line-up entry. | A3 |
| CTX-002 | The View submenu of the empty area menu lacks the columns command, the four view modes and Customize View. | A3 |
| CTX-003 | No icon arrangement submenu and no line-up entry in any context menu. | A3 |
| CTX-004 | The Software Settings menu lacks the View submenu content and the icon arrangement entries. | A3 |
| CTX-005 | The Administrative Templates menu lacks the export list, view modes, Customize View and icon arrangement entries. | A3 |
| CTX-006 | Category and All Settings menus: the same entries as CTX-005 are missing. | A3 |
| CTX-010 | No menu on a column header or the description pane; not captured, so capture classic or declare with the user. | A3 |
| TOOL-009 | The Properties button shows although classic hides it; hide it or have the user declare it (Refresh is DIFF-007). | A3 |
| TOOL-014 | No action pane toggle button (no action pane); build it, or have the user declare it. | A3 |
| ACC-001 | Ctrl+P is not captured and not bound; capture classic, then build or declare. | A3 |
| ACC-003 | Ctrl+F10 is not captured and not bound; capture classic, then build or declare. | A3 |
| ACC-005 | Ctrl+F5 is not captured and not bound; capture classic, then build or declare. | A3 |
| ACC-008 | Ctrl+Alt+Shift+F7 is not captured and not bound; capture classic, then build or declare. | A3 |
| ACC-017 | Tree Home: no test. | A3 |
| ACC-018 | Tree End: no test. | A3 |
| ACC-019 | Tree PageDown: no test. | A3 |
| ACC-020 | Tree PageUp: no test. | A3 |
| ACC-021 | Tree Plus: no test. | A3 |
| ACC-022 | Tree Minus: no test. | A3 |
| ACC-023 | Tree Asterisk: no test. | A3 |
| ACC-029 | Tree Esc with no menu open: no test. | A3 |
| ACC-030 | Alt+Left with the focus on the result side (the capture shows no change): no test. | A3 |
| ACC-031 | Alt+Right with the focus on the result side (the capture shows no change): no test. | A3 |
| ACC-034 | List Down at the last row: no test in the editor. | A3 |
| ACC-035 | List Up: no test in the editor. | A3 |
| ACC-036 | List Home: no test in the editor. | A3 |
| ACC-037 | List End: no test in the editor. | A3 |
| ACC-038 | List PageDown: no test in the editor. | A3 |
| ACC-039 | List PageUp: no test in the editor. | A3 |
| ACC-040 | List Space: no test in the editor. | A3 |
| ACC-041 | List Right: no test in the editor. | A3 |
| ACC-042 | List Left: no test in the editor. | A3 |
| ACC-043 | List Ctrl+A: no test in the editor (classic keeps the focus on the same row). | A3 |
| MSG-001 | gpedit.dll id 9 (the window title form) differs (STR-1). | A3 |
| MSG-023 | The Add/Remove Templates menu label is not found by the string check (access key and ellipsis form). | A3 |
| MSG-024 | The Show Contents refusal messages have BWC wording that the check does not find. | A3 |
| MSG-028 | The number and text validation messages have BWC wording that the check does not find. | A3 |
| MSG-029 | admtmpl.dll id 236 differs (STR-1). | A3 |
| MSG-031 | Template file errors show the engine code; user wording per code is open. | A3 |
| MSG-032 | Template element errors show the engine code; user wording per code is open. | A3 |
| MSG-033 | Template resource errors show the engine code; user wording per code is open. | A3 |
| MSG-037 | The Re-Apply Filter labels are not found by the string check (access key form). | A3 |
| MSG-038 | The platform match texts and the three Filter Options refusal messages have BWC wording. | A3 |
| MSG-050 | The view mode and arrange labels are not found (the classic string holds a help line after the label). | A3 |
| MSG-059 | The Export List write errors and file types have BWC wording or filters that the check does not find. | A3 |
| MSG-062 | The toolbar names are not found (the classic string holds a tooltip copy after the name). | A3 |
| MSG-064 | The Export List and action pane toggle names are not found (ellipsis and tooltip forms). | A3 |
| MSG-091 | Setting dialog form texts are outside the string check; only button and radio names are asserted. | A3 |
| MSG-092 | Show Contents form texts are outside the string check and untested. | A3 |
| MSG-093 | Setting dialog resource texts are outside the string check and untested. | A3 |
| VIEW-023 | No test asserts the tree icons by node kind. | A3 |
| TREE-010 | No test asserts the Administrative Templates icons per side. | A3 |
| TREE-017 | No test asserts the category and placeholder icons. | A3 |
| TREE-019 | Plus, Minus and Asterisk on the tree are not asserted. | A3 |
| BEH-027 | The refresh timing of group G needs a real elevated save with refresh. | A2 |
| POL-068 | The live registry after OK (group G), elevated only. | A2 |
| POL-069 | The live registry after Apply then Cancel (group G), elevated only. | A2 |
| POL-070 | The live registry after Disabled and Not Configured (group G), elevated only. | A2 |
| POL-071 | The live registry after a comment-only change (group G), elevated only. | A2 |
| POL-072 | The live registry types for element policies (group G), elevated only. | A2 |

| Piece | Rows |
| --- | --- |
| A3 | CTX-001, CTX-002, CTX-003, CTX-004, CTX-005, CTX-006, CTX-010, TOOL-009, TOOL-014, ACC-001, ACC-003, ACC-005, ACC-008, ACC-017, ACC-018, ACC-019, ACC-020, ACC-021, ACC-022, ACC-023, ACC-029, ACC-030, ACC-031, ACC-034, ACC-035, ACC-036, ACC-037, ACC-038, ACC-039, ACC-040, ACC-041, ACC-042, ACC-043, MSG-001, MSG-023, MSG-024, MSG-028, MSG-029, MSG-031, MSG-032, MSG-033, MSG-037, MSG-038, MSG-050, MSG-059, MSG-062, MSG-064, MSG-091, MSG-092, MSG-093, VIEW-023, TREE-010, TREE-017, TREE-019 |
| A2 | BEH-027, POL-068, POL-069, POL-070, POL-071, POL-072 |

Notes for the fix pieces:

- The context menu rows (CTX-001 to CTX-006) share one cause: `BuildEmptyAreaItems` and `BuildTreeMenuItems` build Refresh, the filter entries and Help only.
- The MSG gaps are mostly forms the string check cannot match (access keys, ellipses, a help line or tooltip after the text) and wording BWC chose itself. A3 either aligns the text or extends the check, then writes `string-parity.json` again.
- The latest full E2E run fails one check only, the category guard: `Gpedit_ReadOnly_Notice` has no E2EOnly category and is not on the allowlist. The test itself passes. A3 adds the category.

## Declared differences

The declared differences of section 3 of the inventory, and the two user decisions of 2026-11-22.

| ID | Difference | Reason | Piece |
| --- | --- | --- | --- |
| DIFF-001 | BWC opens read-only when the process is not elevated or the edition is Home. Classic refuses to open. | Showing current policy is useful and safe; writing is blocked. | G4, E3 |
| DIFF-002 | The Extended view pane is native. Classic shows an HTML page. | The HTML control is not available to a WinUI tool. | U2c |
| DIFF-003 | Save reads, modifies and writes under a file lock, so an edit made outside BWC is not overwritten. | Safer than classic's whole-file rewrite from a stale model. | E3 |
| DIFF-004 | A backup of the three GPO files (Registry.pol, gpt.ini, comment.cmtx) is kept before the first write of a session in `%ProgramData%\BetterWindowsConfig\gpedit\backup`, last 10. | Recovery from a bad edit; classic keeps no backup. | E3 |
| DIFF-005 | Imported templates live in the BWC store, not the system folder. Classic `.adm` files are not supported. | Keeps system files untouched and the store admin-only. | G6, E5 |
| DIFF-006 | Help Topics and About follow the precedent set by the Registry Editor. | Consistent with the existing tool. | U3b |
| DIFF-007 | Refresh is a BWC addition: the Action menu, all three context menus, the toolbar button and F5 reload the templates and the policy files. Classic has no Refresh entry in any dump and F5 does nothing (ACC-004, ACC-027, ACC-044, BEH-022). | A policy file or template that changes while the editor runs is otherwise visible only after a restart. | G2 |
| MENU-037 | No Favorites menu. | Removed by the user decision of 2026-11-22. Classic shows no Favorites menu at run time, so BWC builds none. | none |
| TREE-020 | No Extra Registry Settings node. | Removed by the user decision of 2026-11-22. Classic shows no Extra Registry Settings node; records that match no policy stay invisible (BEH-014). | none |
| BEH-012 | Retired: the Extra Registry Settings node row. | Retired by the user decision of 2026-11-22 (no Extra Registry Settings node); replaced by BEH-014. | none |

The differences decided during the overnight run, with the rows that carry them (the reason is quoted in each row):

| Difference | Rows | Source |
| --- | --- | --- |
| Template load errors show as a status bar notice and the Problems list, not a start message box. | DLG-121, DLG-122, VIEW-038, VIEW-040 | `docs/failure-modes/gpedit-templates.md` |
| One bad template does not hide the rest, and every problem is recorded. | BEH-009 | `docs/failure-modes/gpedit-templates.md` |
| Template import refuses classic `.adm` files. | BEH-008 | DIFF-005, `docs/failure-modes/gpedit-templates.md` |
| Sorting is kept per node. | BEH-025 | `docs/failure-modes/gpedit-filter.md` |
| The Options page fixes or disables the console-authoring parts and has OK and Cancel. | DLG-119 | `docs/failure-modes/console-options.md` |
| The setting dialog has no maximize box, and each radio button is a tab stop. | DLG-089, DLG-095, VIEW-027 to VIEW-037 | `docs/failure-modes/gpedit-dialog.md` |
| The tree context menu is a BWC definition (not captured). | CTX-009 | `docs/failure-modes/gpedit-commands.md` |

Not differences: the default list order and the policy id ordering follow classic (VIEW-013, pass). The Export List format and encodings (`docs/failure-modes/console-frame.md`, declared difference 5) are a BWC definition, but no inventory row states classic's format, so no row carries them. The Help web site entry (MENU-034) exists as in classic and opens the project page; it is `pass`, for the user to confirm. Resources classic never shows in the local editor (static menus, wizard pages, Favorites dialogs, the author mode and MDI tables) are `declared-difference` "not reachable in the local editor, not built".

## Rows

### MENU

| ID | Verdict | Evidence or reason | Piece |
| --- | --- | --- | --- |
| MENU-001 | pass | Console_Shell_Layout (menuNames: File, Action, View, Help in that order). The static Favorites, snap-in and Window entries are not built (MENU-037, MENU-041). | U3, G2 |
| MENU-002 | pass | Console_Options_Menu_FileLayout (filtered run). | U3 |
| MENU-003 | pass | Console_Options_Menu_FileLayout and Console_Options_Menu_AccessKey (filtered run). | U3b |
| MENU-004 | pass | Console_Options_Menu_FileLayout (filtered run). | U3, G2 |
| MENU-005 | pass | Console_Options_Menu_FileLayout (filtered run). | U3 |
| MENU-006 | pass | Gpedit_Menu_Action_NodeKinds and Gpedit_Menu_Action_PolicyRow. | G2 |
| MENU-007 | pass | Gpedit_Filter_Menu_Action, Console_Export_Menu_NodeKinds, Gpedit_Menu_Action_NodeKinds. The Refresh entry is the DIFF-007 addition. | G2 |
| MENU-008 | pass | Gpedit_Filter_Menu_Action, Gpedit_Import_Menu_Placement, Console_Export_Menu_NodeKinds. The Refresh entry is the DIFF-007 addition. | G2, G5, G6 |
| MENU-009 | pass | Gpedit_Filter_Menu_Action, Console_Export_Menu_NodeKinds. The Refresh entry is the DIFF-007 addition. | G2, G5 |
| MENU-010 | pass | Gpedit_Filter_Menu_Action, Gpedit_Menu_Action_PolicyRow, Console_Export_Menu_NodeKinds. The Refresh entry is the DIFF-007 addition. | G2, G3a |
| MENU-011 | pass | Gpedit_Import_Menu_Placement. | G6 |
| MENU-012 | pass | Gpedit_Menu_Action_PolicyRow and Gpedit_Menu_Action_Commands. | G2, G3a |
| MENU-013 | pass | Gpedit_Filter_Menu_Action. | G5 |
| MENU-014 | pass | Gpedit_Filter_Menu_Action. | G5 |
| MENU-015 | pass | Gpedit_Filter_Menu_Action and Gpedit_Filter_KeptOnAcrossNodesAndReApply. | G5 |
| MENU-016 | pass | Gpedit_Filter_Menu_Action. | G2, G5 |
| MENU-017 | pass | Console_Export_Menu_NodeKinds. | U3a |
| MENU-018 | pass | Gpedit_Menu_Action_Commands (Help runs the Help command once). | U3b |
| MENU-019 | pass | Console_Columns_Menu_Layout. | U3a |
| MENU-020 | pass | Console_Columns_Menu_Layout and Console_Shell_MenuKeyboard. | U3a |
| MENU-021 | pass | Gpedit_Filter_Menu_View. | U3a, G5 |
| MENU-022 | pass | Console_Columns_Menu_Layout and Console_Columns_Dialog_Layout. | U3a |
| MENU-023 | pass | Console_List_ViewModes and Console_Columns_Menu_Layout. | U3a |
| MENU-024 | pass | Console_List_ViewModes and Console_Columns_Menu_Layout. | U3a |
| MENU-025 | pass | Console_List_ViewModes and Console_Columns_Menu_Layout. | U3a |
| MENU-026 | pass | Console_Columns_Menu_Layout (the list starts in Detail). | U3a |
| MENU-027 | pass | Gpedit_Filter_Menu_View. | G5 |
| MENU-028 | pass | Gpedit_Filter_Menu_View. | G5 |
| MENU-029 | pass | Gpedit_Filter_Menu_View. | G5 |
| MENU-030 | pass | Console_Columns_Menu_Layout and Console_Customize_Menu_NoToggles. | U3a |
| MENU-031 | pass | Gpedit_Help_Menu_NodeKinds (filtered run). | U3b |
| MENU-032 | pass | Gpedit_Help_Menu_NodeKinds (filtered run). | U3b |
| MENU-033 | pass | Gpedit_Menu_Help_Topics. Help Topics runs the Help command (DIFF-006). | U3b |
| MENU-034 | pass | Gpedit_Help_WebSite_Entry (filtered run). BWC has the entry, enabled, as classic does; it opens the project page (console-options.md). User to confirm. | U3b, A1 |
| MENU-035 | pass | Gpedit_Help_About_Box (filtered run). The box follows the Registry Editor (DIFF-006). | U3b |
| MENU-036 | pass | Gpedit_Help_About_SnapIn and Gpedit_Help_Menu_NodeKinds (filtered run). | U3b |
| MENU-037 | removed | Removed by the user decision of 2026-11-22. Classic shows no Favorites menu at run time, so BWC builds none. | none |
| MENU-038 | declared-difference | Static only: not reachable in the local editor, not built. | none |
| MENU-039 | declared-difference | Static only: not reachable in the local editor, not built. | none |
| MENU-040 | declared-difference | Static only: not reachable in the local editor, not built. Its view mode and Edit ids are covered by MENU-012 and MENU-023 to MENU-025. | none |
| MENU-041 | declared-difference | Static only: author mode, MDI and Window menus are not shown by the local editor, not built. | none |

### CTX

| ID | Verdict | Evidence or reason | Piece |
| --- | --- | --- | --- |
| CTX-001 | gap | Gpedit_ContextMenu_List_NoRow checks the menu opens and Help runs, but the BWC empty area menu holds Refresh, the View submenu and Help only: no export list, no view modes in the View submenu, no icon arrangement submenu and no line-up entry. | A3 |
| CTX-002 | gap | The View submenu of the empty area menu holds the filter entries only (BuildEmptyAreaItems); the columns command, the four view modes and Customize View are missing. | A3 |
| CTX-003 | gap | No icon arrangement submenu and no line-up entry in any BWC context menu. | A3 |
| CTX-004 | gap | The Software Settings menu lacks the View submenu content and the icon arrangement entries (same cause as CTX-001). | A3 |
| CTX-005 | gap | Gpedit_Filter_ContextMenu_List proves the filter entries in the View submenu and no Add/Remove Templates; the export list, view modes, Customize View and icon arrangement entries are missing. | A3 |
| CTX-006 | gap | Same as CTX-005 at category nodes and All Settings (Gpedit_Filter_ContextMenu_List). | A3 |
| CTX-007 | pass | Gpedit_Filter_ContextMenu_List and Gpedit_ContextMenu_List_PolicyRow. The Refresh entry is the DIFF-007 addition. | G2, G3a, G5 |
| CTX-008 | pass | Gpedit_ContextMenu_List_NoRow and Gpedit_Menu_Action_NodeKinds. | G2 |
| CTX-009 | declared-difference | Not captured. BWC definition (gpedit-commands.md, gpedit-filter.md): the tree menu holds the filter entries flat where the node has the filter, then Refresh and Help. Gpedit_ContextMenu_Tree and Gpedit_Filter_ContextMenu_Tree. | G2, A1 |
| CTX-010 | gap | Not captured. BWC shows no menu on a column header (OnEditorListContext returns) or on the description pane; capture classic or declare the absence with the user. | A3 |
| CTX-011 | pass | Gpedit_ContextMenu_List_PolicyRow (a command runs and the menu closes; Escape leaves the focus in the list) and Gpedit_ContextMenu_Tree. | G2 |

### TOOL

| ID | Verdict | Evidence or reason | Piece |
| --- | --- | --- | --- |
| TOOL-001 | pass | Console_Shell_Layout (menu and toolbar bands, toolbar control type). | U3, G2 |
| TOOL-002 | pass | Console_Shell_Layout (menu names and order). The button widths are text widths and are not asserted. | U3 |
| TOOL-003 | pass | Console_Shell_Layout (buttonSize 23 by 22 DIP, buttons in order) and Gpedit_Filter_Toolbar_Button. | U3, G2 |
| TOOL-004 | pass | Gpedit_Toolbar_States and Gpedit_Toolbar_History. | G2 |
| TOOL-005 | pass | Gpedit_Toolbar_States and Gpedit_Toolbar_History. | G2 |
| TOOL-006 | pass | Console_Shell_Layout (standard order with its separators). | U3 |
| TOOL-007 | pass | Gpedit_Toolbar_States and Gpedit_Toolbar_Commands. | G2 |
| TOOL-008 | pass | Gpedit_Toolbar_Commands and Console_Customize_Switches_ActAtOnce. | U3a |
| TOOL-009 | gap | BWC shows the Properties and Refresh buttons that classic keeps hidden (gpedit-commands.md, G2). Refresh is DIFF-007; Properties is not a decided difference: hide it or have the user declare it. | A3 |
| TOOL-010 | pass | Console_Shell_Layout (standard order with its separators). | U3 |
| TOOL-011 | pass | Console_Export_Menu_NodeKinds and Console_Export_Dialog_Types. | U3a |
| TOOL-012 | pass | Console_Shell_Layout (standard order with its separators). | U3 |
| TOOL-013 | pass | Gpedit_Toolbar_States and Gpedit_Toolbar_Commands. | U3b |
| TOOL-014 | gap | BWC has no action pane and no action pane toggle button (the standard buttons end with Help, Console_Shell_Layout); not a decided difference. | A3 |
| TOOL-015 | pass | Gpedit_Filter_Toolbar_Button and Console_Customize_SnapInToolbars_Editor. | G5 |
| TOOL-016 | pass | Gpedit_Toolbar_States and Gpedit_Filter_Toolbar_Button. | G2 |
| TOOL-017 | pass | Console_Customize_Switches_ActAtOnce and Console_Shell_ShowHide. | U3a |

### DLG

| ID | Verdict | Evidence or reason | Piece |
| --- | --- | --- | --- |
| DLG-001 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-002 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-003 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-004 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-005 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-006 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-007 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-008 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-009 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-010 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-011 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-012 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-013 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-014 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-015 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-016 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-017 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-018 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-019 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-020 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-021 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-022 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-023 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-024 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-025 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-026 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-027 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-028 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-029 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-030 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-031 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-032 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-033 | pass | Gpedit_Import_Dialog_Layout. | G6 |
| DLG-034 | pass | Gpedit_Import_Refused_BadFixtures and Gpedit_Import_StoreUnavailable (the details box with Close shows on a refusal). | G6 |
| DLG-035 | pass | Gpedit_Filter_Dialog_Layout. | G5 |
| DLG-036 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-037 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-038 | pass | Console_Options_Dialog_Layout (filtered run). | U3b |
| DLG-039 | declared-difference | A Favorites dialog: not reachable in the local editor (no Favorites menu, MENU-037), not built. | none |
| DLG-040 | declared-difference | A Favorites dialog: not reachable in the local editor (no Favorites menu, MENU-037), not built. | none |
| DLG-041 | declared-difference | A Favorites dialog: not reachable in the local editor (no Favorites menu, MENU-037), not built. | none |
| DLG-042 | declared-difference | A Favorites dialog: not reachable in the local editor (no Favorites menu, MENU-037), not built. | none |
| DLG-043 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-044 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-045 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-046 | declared-difference | DIFF-006: About follows the Registry Editor's native box. Gpedit_Help_About_Box (filtered run). | U3b |
| DLG-047 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-048 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-049 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-050 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-051 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-052 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-053 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-054 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-055 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-056 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-057 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-058 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-059 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-060 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-061 | pass | Console_Columns_Dialog_Layout. | U3a |
| DLG-062 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-063 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-064 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-065 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-066 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-067 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-068 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-069 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-070 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-071 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-072 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-073 | pass | Console_Customize_Dialog_Layout. | U3a |
| DLG-074 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-075 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-076 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-077 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-078 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-079 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-080 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-081 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-082 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-083 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-084 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-085 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-086 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-087 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-088 | declared-difference | Not opened by any menu of the local editor: not reachable in the local editor, not built. | none |
| DLG-089 | declared-difference | No maximize box: "BWC keeps its dialog frame without a maximize box" (gpedit-dialog.md, declared difference, section 4). The size, minimum and control set pass in Gpedit_Dialog_Frame and Gpedit_Dialog_Resize. | G3a |
| DLG-090 | pass | Gpedit_Dialog_Frame and Gpedit_Dialog_Literal (display name, Previous and Next). The 16 x 16 icon is the module icon or its stand-in glyph (gpedit-dialog.md). | G3a |
| DLG-091 | pass | Gpedit_Dialog_InitialStates and PolicyStateMatrixTests. | G3a, E6c |
| DLG-092 | pass | Gpedit_Dialog_ApplyOk, Gpedit_Dialog_OkSaves and PolicyGoldenReplayTests (C/Comment_*). | G3a, E6a |
| DLG-093 | pass | Gpedit_Dialog_Frame (Supported on box, read-only). | G3a |
| DLG-094 | pass | Gpedit_Dialog_Frame and Gpedit_Dialog_Resize (geometry against the forms data). | G3a, G3b |
| DLG-095 | declared-difference | Each radio button is a tab stop: "the shared dialog base gives every control a fixed tab stop, classic stops only on the selected radio button" (gpedit-dialog.md). The rest of the order passes in Gpedit_Dialog_Frame and Gpedit_Options_TabOrder. | G3a |
| DLG-096 | pass | Gpedit_Dialog_ApplyOk. | G3a |
| DLG-097 | pass | Gpedit_Options_TabOrder and Gpedit_Options_Defaults. | G3b |
| DLG-098 | pass | Gpedit_Dialog_ApplyOk, Gpedit_Dialog_Cancel, Gpedit_Replay_GroupA and PolicyGoldenReplayTests. | G3a, G4 |
| DLG-099 | pass | Gpedit_Dialog_PreviousNext (first, last, middle and only policy). | G3a |
| DLG-100 | pass | Gpedit_Options_Navigation, PolicyGoldenReplayDETests and PolicyEditRefusalTests (E/Next_WithInvalid, E/Previous_WithInvalid). | G3a, E6c |
| DLG-101 | pass | Gpedit_Options_Validation, Gpedit_Dialog_Validation and PolicyEditRefusalTests. | G3b, E6c |
| DLG-102 | pass | Gpedit_Options_Elements and Gpedit_Options_Choices. | G3b |
| DLG-103 | pass | Gpedit_Options_Elements and Gpedit_Options_Numbers. | G3b |
| DLG-104 | pass | Gpedit_Options_Elements and Gpedit_Options_Numbers. | G3b |
| DLG-105 | pass | Gpedit_Options_Elements and Gpedit_Options_Layout. | G3b |
| DLG-106 | pass | Gpedit_Options_Elements and Gpedit_Options_Choices. | G3b |
| DLG-107 | pass | Gpedit_Options_Elements and Gpedit_Options_Validation. | G3b |
| DLG-108 | pass | Gpedit_Options_Elements and Gpedit_Options_Choices. | G3b |
| DLG-109 | pass | Gpedit_Options_Elements and Gpedit_Options_List. | G3b |
| DLG-110 | pass | Gpedit_Options_Literal. | G3b |
| DLG-111 | pass | Gpedit_Options_List. | G3b |
| DLG-112 | pass | Gpedit_Options_List, Gpedit_Options_List_EmptyRefused and PolicyEditRefusalTests. | G3b, E6c |
| DLG-113 | pass | Gpedit_Filter_Dialog_Layout. | G5 |
| DLG-114 | pass | Gpedit_Filter_Dialog_Refusals. The message texts are BWC's own (see MSG-038). | G5 |
| DLG-115 | pass | Gpedit_Import_Dialog_Layout. | G6 |
| DLG-116 | pass | Gpedit_Import_Add_GoodTemplate, Gpedit_Import_Add_PickerCancelWritesNothing and Gpedit_Import_Refused_BadFixtures. | G6 |
| DLG-117 | pass | Console_Customize_Dialog_Layout (nine boxes, OK only). Four boxes are shown disabled (console-frame.md decision 3), user to confirm. | U3a |
| DLG-118 | pass | Console_Columns_Dialog_Layout and Console_Columns_Dialog_Operations. | U3a |
| DLG-119 | declared-difference | Options console-authoring parts fixed or disabled: "BWC has no console files, no authoring and one mode" (console-options.md); the page has OK and Cancel. Console_Options_Dialog_Layout and Console_Options_Dialog_OkCancel (filtered run). | U3b |
| DLG-120 | declared-difference | DIFF-006: About follows the Registry Editor. Gpedit_Help_About_Box (filtered run). | U3b |
| DLG-121 | declared-difference | Load errors as a status bar notice and list: "A modal box at start would block the window for every reader of the editor" (gpedit-templates.md). Gpedit_Templates_LoadErrors_BoxCases. | G6 |
| DLG-122 | declared-difference | Same decision as DLG-121; the notice "does not re-appear when a node is selected, because it is a pane and not an event" (gpedit-templates.md). Gpedit_Templates_Notice_NotAnEvent. | G6 |
| DLG-123 | pass | Gpedit_Dialog_Validation, Gpedit_Options_Validation and Gpedit_Filter_Dialog_Refusals. The load-error box is replaced (DLG-121). | G3a, G6 |

### ACC

| ID | Verdict | Evidence or reason | Piece |
| --- | --- | --- | --- |
| ACC-001 | gap | Ctrl+P (table 128) is not captured and BWC binds nothing to it; capture classic's effect, then build or declare. | A3 |
| ACC-002 | pass | Gpedit_Keys_F1. | G2, U3b |
| ACC-003 | gap | Ctrl+F10 (table 128) is not captured and BWC binds nothing to it; capture classic's effect, then build or declare. | A3 |
| ACC-004 | pass | Gpedit_Keys_F5: no window and no menu. The reload is the DIFF-007 addition. | G2 |
| ACC-005 | gap | Ctrl+F5 (table 128) is not captured and BWC binds nothing to it; capture classic's effect, then build or declare. | A3 |
| ACC-006 | pass | Console_Shell_FocusCycle. | G2 |
| ACC-007 | pass | Console_Shell_FocusCycle. | G2 |
| ACC-008 | gap | Ctrl+Alt+Shift+F7 (table 128) is not captured and BWC binds nothing to it; capture classic's effect, then build or declare. | A3 |
| ACC-009 | pass | Console_Shell_FocusCycle. | G2 |
| ACC-010 | pass | Console_Shell_FocusCycle. | G2 |
| ACC-011 | declared-difference | Table not reached in the local editor (no matching menu command), not built. | none |
| ACC-012 | declared-difference | Table not reached in the local editor (no matching menu command), not built. | none |
| ACC-013 | pass | Gpedit_TreeBlind_ExpandCollapseAndKeys. | G2 |
| ACC-014 | pass | Gpedit_TreeBlind_ExpandCollapseAndKeys. | G2 |
| ACC-015 | pass | Gpedit_TreeBlind_ExpandCollapseAndKeys and Gpedit_TreeBlind_PlaceholderPages. | G2 |
| ACC-016 | pass | Gpedit_TreeBlind_ExpandCollapseAndKeys and Gpedit_TreeBlind_PlaceholderPages. | G2 |
| ACC-017 | gap | Home in the tree is not asserted by any test. | A3 |
| ACC-018 | gap | End in the tree is not asserted by any test. | A3 |
| ACC-019 | gap | PageDown in the tree is not asserted by any test. | A3 |
| ACC-020 | gap | PageUp in the tree is not asserted by any test. | A3 |
| ACC-021 | gap | Plus in the tree is not asserted by any test. | A3 |
| ACC-022 | gap | Minus in the tree is not asserted by any test. | A3 |
| ACC-023 | gap | Asterisk in the tree is not asserted by any test. | A3 |
| ACC-024 | pass | Gpedit_Keys_History (Backspace selects the parent). | G2 |
| ACC-025 | pass | Console_Shell_FocusCycle. | G2 |
| ACC-026 | pass | Console_Shell_FocusCycle. | G2 |
| ACC-027 | pass | Gpedit_Keys_F5: no window and no menu, selection kept. The reload is the DIFF-007 addition. | G2 |
| ACC-028 | pass | Console_Shell_FocusCycle. | G2 |
| ACC-029 | gap | Esc in the tree with no menu open is not asserted by any test. | A3 |
| ACC-030 | gap | The capture shows no change with the focus on the result side; Gpedit_Keys_History asserts Alt+Left from the tree only. The result-side case is not asserted. | A3 |
| ACC-031 | gap | Same as ACC-030 for Alt+Right. | A3 |
| ACC-032 | pass | Gpedit_Keys_History (Alt+Up in the result pane changes nothing). | G2 |
| ACC-033 | pass | Gpedit_ContextMenu_Tree and Console_Shell_ContextMenuKey. The capture's focus was on the result side; the tree menu itself is CTX-009. | G2 |
| ACC-034 | gap | The list keys of F/Keys_List (Down at the last row) are not asserted in the editor. | A3 |
| ACC-035 | gap | Up in the result list is not asserted in the editor. | A3 |
| ACC-036 | gap | Home in the result list is not asserted in the editor. | A3 |
| ACC-037 | gap | End in the result list is not asserted in the editor. | A3 |
| ACC-038 | gap | PageDown in the result list is not asserted in the editor. | A3 |
| ACC-039 | gap | PageUp in the result list is not asserted in the editor. | A3 |
| ACC-040 | gap | Space in the result list is not asserted in the editor. | A3 |
| ACC-041 | gap | Right in the result list is not asserted in the editor. | A3 |
| ACC-042 | gap | Left in the result list is not asserted in the editor. | A3 |
| ACC-043 | gap | Ctrl+A in the result list is not asserted in the editor (Console_List_MultiSelect runs on the frame host only). | A3 |
| ACC-044 | pass | Gpedit_Keys_F5. The reload is the DIFF-007 addition. | G2 |
| ACC-045 | pass | Console_Shell_FocusCycle. | G2 |
| ACC-046 | pass | Console_Shell_FocusCycle. | G2 |
| ACC-047 | pass | Gpedit_ContextMenu_List_PolicyRow and Gpedit_ContextMenu_List_NoRow (the menu key opens the F/Menus_* menu). | G2 |
| ACC-048 | pass | Gpedit_Keys_Edit. | G2, G3a |
| ACC-049 | pass | The row states no tracked behavior: the results depend on the machine's own templates. | A1 |
| ACC-050 | pass | Gpedit_Keys_Edit (Alt+Enter edits a policy row), Gpedit_Keys_Delete (Delete does nothing), Gpedit_Keys_F1 and Gpedit_Keys_WrongPane. BWC definitions in gpedit-commands.md. | G2, A1 |

### MSG

| ID | Verdict | Evidence or reason | Piece |
| --- | --- | --- | --- |
| MSG-001 | gap | string-parity: equal 1, differs 1, template 0, absent 13. Id 9 (the window title form) differs; fix the wording or declare it (STR-1). The other absent ids are GPO, RSoP and domain texts not shown. | A3 |
| MSG-002 | declared-difference | string-parity: equal 3, differs 0, template 0, absent 12. Not shown: BWC never shows the ids that are not equal; the equal ids pass (string-parity). | none |
| MSG-003 | declared-difference | string-parity: equal 3, differs 0, template 0, absent 12. Not shown: BWC never shows the ids that are not equal; the equal ids pass (string-parity). | none |
| MSG-004 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 16. Not shown: BWC never shows any id of this block. | none |
| MSG-005 | declared-difference | string-parity: equal 1, differs 0, template 0, absent 15. Not shown: BWC never shows the ids that are not equal; the equal ids pass (string-parity). | none |
| MSG-006 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 16. Not shown: BWC never shows any id of this block. | none |
| MSG-007 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 15. Not shown: BWC never shows any id of this block. | none |
| MSG-008 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 11. Not shown: BWC never shows any id of this block. | none |
| MSG-009 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 12. Not shown: BWC never shows any id of this block. | none |
| MSG-010 | declared-difference | string-parity: equal 1, differs 0, template 0, absent 14. Not shown: BWC never shows the ids that are not equal; the equal ids pass (string-parity). | none |
| MSG-011 | declared-difference | string-parity: equal 2, differs 0, template 0, absent 12. Not shown: BWC never shows the ids that are not equal; the equal ids pass (string-parity). | none |
| MSG-012 | declared-difference | string-parity: equal 1, differs 0, template 0, absent 8. Not shown: BWC never shows the ids that are not equal; the equal ids pass (string-parity). | none |
| MSG-013 | declared-difference | string-parity: equal 2, differs 0, template 0, absent 7. Not shown: BWC never shows the ids that are not equal; the equal ids pass (string-parity). | none |
| MSG-014 | declared-difference | string-parity: equal 2, differs 0, template 0, absent 13. Not shown: BWC never shows the ids that are not equal; the equal ids pass (string-parity). | none |
| MSG-015 | declared-difference | string-parity: equal 5, differs 0, template 0, absent 3. Not shown: BWC never shows the ids that are not equal; the equal ids pass (string-parity). | none |
| MSG-016 | declared-difference | string-parity: equal 1, differs 0, template 0, absent 4. Not shown: BWC never shows the ids that are not equal; the equal ids pass (string-parity). | none |
| MSG-017 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 14. Not shown: BWC never shows any id of this block. | none |
| MSG-018 | declared-difference | string-parity: equal 1, differs 0, template 0, absent 14. Not shown: BWC never shows the ids that are not equal; the equal ids pass (string-parity). | none |
| MSG-019 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 16. Not shown: BWC never shows any id of this block. | none |
| MSG-020 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 6. Not shown: BWC never shows any id of this block. | none |
| MSG-021 | declared-difference | string-parity: equal 2, differs 0, template 0, absent 2. Not shown: BWC never shows the ids that are not equal; the equal ids pass (string-parity). | none |
| MSG-022 | declared-difference | string-parity: equal 6, differs 0, template 0, absent 2. Not shown: BWC never shows the ids that are not equal; the equal ids pass (string-parity). | none |
| MSG-023 | gap | string-parity: equal 3, differs 0, template 0, absent 9. The Add/Remove Templates menu label (id 22) is shown by BWC but not found (access key and ellipsis form); the rest are .adm and size texts not shown. | A3 |
| MSG-024 | gap | string-parity: equal 1, differs 0, template 0, absent 14. The Show Contents refusal messages (ids 33 to 36) are shown by BWC with its own words and not found; the rest are .adm, merged GPO and Extra Registry texts not shown. | A3 |
| MSG-025 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 16. Not shown: BWC never shows any id of this block. | none |
| MSG-026 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 7. Not shown: BWC never shows any id of this block. | none |
| MSG-027 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 4. Not shown: BWC never shows any id of this block. | none |
| MSG-028 | gap | string-parity: equal 0, differs 0, template 0, absent 6. The number and text validation messages (ids 100 to 103) have BWC counterparts (DLG-101) that the check does not find; the template file message is not shown. | A3 |
| MSG-029 | gap | string-parity: equal 0, differs 1, template 0, absent 1. Id 236 differs (STR-1); id 237 is not shown. | A3 |
| MSG-030 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 8. Not shown: BWC never shows any id of this block. | none |
| MSG-031 | gap | string-parity: equal 0, differs 0, template 1, absent 3. BWC shows the engine failure code instead of these texts; the user wording per code is open in gpedit-templates.md. | A3 |
| MSG-032 | gap | string-parity: equal 0, differs 0, template 3, absent 10. BWC shows the engine failure code instead of these texts; the user wording per code is open in gpedit-templates.md. | A3 |
| MSG-033 | gap | string-parity: equal 0, differs 0, template 1, absent 10. BWC shows the engine failure code instead of these texts; the user wording per code is open in gpedit-templates.md. | A3 |
| MSG-034 | pass | string-parity: equal 4, differs 0, template 0, absent 0. Every id is equal (string-parity). | G1 |
| MSG-035 | pass | string-parity: equal 6, differs 0, template 0, absent 0. Every id is equal (string-parity). | G5 |
| MSG-036 | pass | string-parity: equal 4, differs 0, template 0, absent 0. Every id is equal (string-parity). | G5 |
| MSG-037 | gap | string-parity: equal 3, differs 0, template 0, absent 3. The Re-Apply Filter labels (ids 3104 and 3105) are shown by BWC but not found (access key form); the platform match text and the status text are BWC words (gpedit-filter.md). | A3 |
| MSG-038 | gap | string-parity: equal 0, differs 0, template 3, absent 5. The platform match texts and the three refusal messages of Filter Options (ids 3119 to 3123) are shown by BWC with its own words; the three status templates are not compared. | A3 |
| MSG-039 | declared-difference | string-parity: equal 1, differs 0, template 0, absent 6. Not shown: BWC never shows the ids that are not equal; the equal ids pass (string-parity). | none |
| MSG-040 | declared-difference | string-parity: equal 2, differs 0, template 0, absent 1. Not shown: BWC never shows the ids that are not equal; the equal ids pass (string-parity). | none |
| MSG-041 | declared-difference | string-parity: equal 0, differs 0, template 3, absent 4. Not shown: BWC never shows any id of this block. | none |
| MSG-042 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 5. Not shown: BWC never shows any id of this block. | none |
| MSG-043 | declared-difference | string-parity: equal 4, differs 0, template 0, absent 238. The taskpad icon descriptions are not shown: BWC has no taskpads. | none |
| MSG-044 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 5. Not shown: BWC never shows any id of this block. | none |
| MSG-045 | declared-difference | string-parity: equal 0, differs 0, template 2, absent 6. Not shown: BWC never shows any id of this block. | none |
| MSG-046 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 3. Not shown: BWC never shows any id of this block. | none |
| MSG-047 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 5. Not shown: BWC never shows any id of this block. | none |
| MSG-048 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 2. Not shown: BWC never shows any id of this block. | none |
| MSG-049 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 1. Not shown: BWC never shows any id of this block. | none |
| MSG-050 | gap | string-parity: equal 1, differs 0, template 0, absent 15. The view mode and arrange labels (ids 12001 to 12009) carry a help line after the label, so the check does not find the labels BWC shows; the rest are snap-in errors and Favorites texts not shown. | A3 |
| MSG-051 | declared-difference | string-parity: equal 0, differs 0, template 1, absent 5. Not shown: BWC never shows any id of this block. | none |
| MSG-052 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 3. Not shown: BWC never shows any id of this block. | none |
| MSG-053 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 1. Not shown: BWC never shows any id of this block. | none |
| MSG-054 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 2. Not shown: BWC never shows any id of this block. | none |
| MSG-055 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 2. Not shown: BWC never shows any id of this block. | none |
| MSG-056 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 12. Not shown: BWC never shows any id of this block. | none |
| MSG-057 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 15. Not shown: BWC never shows any id of this block. | none |
| MSG-058 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 16. Not shown: BWC never shows any id of this block. | none |
| MSG-059 | gap | string-parity: equal 0, differs 0, template 0, absent 16. The Export List write errors and file types (ids 13344 to 13348) are shown by BWC with its own words or filters and are not found (console-frame.md). | A3 |
| MSG-060 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 4. Not shown: BWC never shows any id of this block. | none |
| MSG-061 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 16. Not shown: BWC never shows any id of this block. | none |
| MSG-062 | gap | string-parity: equal 0, differs 0, template 0, absent 15. The toolbar names (ids 13392 to 13398) carry a tooltip copy after the name, so the check does not find the names BWC shows; the rest are frame errors not shown. | A3 |
| MSG-063 | declared-difference | string-parity: equal 1, differs 0, template 0, absent 15. Not shown: BWC never shows the ids that are not equal; the equal ids pass (string-parity). | none |
| MSG-064 | gap | string-parity: equal 1, differs 0, template 0, absent 11. Export List (id 13436) and the action pane toggle name (id 13432) are not found (ellipsis and tooltip forms); the size units are not shown. | A3 |
| MSG-065 | declared-difference | string-parity: equal 2, differs 0, template 0, absent 14. Not shown: BWC never shows the ids that are not equal; the equal ids pass (string-parity). | none |
| MSG-066 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 5. Not shown: BWC never shows any id of this block. | none |
| MSG-067 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 16. Not shown: BWC never shows any id of this block. | none |
| MSG-068 | declared-difference | string-parity: equal 1, differs 0, template 0, absent 14. Not shown: BWC never shows the ids that are not equal; the equal ids pass (string-parity). | none |
| MSG-069 | declared-difference | string-parity: equal 1, differs 0, template 0, absent 15. Not shown: BWC never shows the ids that are not equal; the equal ids pass (string-parity). | none |
| MSG-070 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 16. Not shown: BWC never shows any id of this block. | none |
| MSG-071 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 12. Not shown: BWC never shows any id of this block. | none |
| MSG-072 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 16. Not shown: BWC never shows any id of this block. | none |
| MSG-073 | declared-difference | string-parity: equal 1, differs 0, template 0, absent 15. Not shown: BWC never shows the ids that are not equal; the equal ids pass (string-parity). | none |
| MSG-074 | declared-difference | string-parity: equal 2, differs 0, template 0, absent 14. Not shown: BWC never shows the ids that are not equal; the equal ids pass (string-parity). | none |
| MSG-075 | declared-difference | string-parity: equal 0, differs 0, template 2, absent 14. Not shown: BWC never shows any id of this block. | none |
| MSG-076 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 16. Not shown: BWC never shows any id of this block. | none |
| MSG-077 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 11. DIFF-006: About follows the Registry Editor, so the classic About text is not shown. | none |
| MSG-078 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 5. Not shown: BWC never shows any id of this block. | none |
| MSG-079 | declared-difference | string-parity: equal 1, differs 0, template 0, absent 1. Not shown: BWC never shows the ids that are not equal; the equal ids pass (string-parity). | none |
| MSG-080 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 9. Not shown: BWC never shows any id of this block. | none |
| MSG-081 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 16. Not shown: BWC never shows any id of this block. | none |
| MSG-082 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 8. Not shown: BWC never shows any id of this block. | none |
| MSG-083 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 7. Not shown: BWC never shows any id of this block. | none |
| MSG-084 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 2. Not shown: BWC never shows any id of this block. | none |
| MSG-085 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 2. Not shown: BWC never shows any id of this block. | none |
| MSG-086 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 6. Not shown: BWC never shows any id of this block. | none |
| MSG-087 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 2. Not shown: BWC never shows any id of this block. | none |
| MSG-088 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 7. Not shown: BWC never shows any id of this block. | none |
| MSG-089 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 3. Not shown: BWC never shows any id of this block. | none |
| MSG-090 | declared-difference | string-parity: equal 0, differs 0, template 0, absent 1. Not shown: BWC never shows any id of this block. | none |
| MSG-091 | gap | Managed form texts are outside string-parity. Gpedit_Dialog_Frame asserts the button and radio names only; the label, Supported on, Comment and Help texts are not compared. | A3 |
| MSG-092 | gap | Managed form texts of the Show Contents dialog are outside string-parity and no test compares them. | A3 |
| MSG-093 | gap | Managed resource texts of the setting dialog are outside string-parity and no test compares them. | A3 |

### VIEW

| ID | Verdict | Evidence or reason | Piece |
| --- | --- | --- | --- |
| VIEW-001 | pass | Console_Shell_Layout (four stacked parts, sizes in DIP at any DPI). The 1536 x 1074 px window is the capture machine's placement, not a rule. | U3, G2 |
| VIEW-002 | pass | Console_Shell_Layout (menuAndToolbarBands). | U3 |
| VIEW-003 | pass | Console_Shell_Layout (menuAndToolbarBands, buttonSize). | U3 |
| VIEW-004 | pass | Console_Shell_Layout (tree 156 DIP) and Console_Shell_SplitterDragAndLimits. | U3, G1 |
| VIEW-005 | pass | Console_Extended_PaneWidth and Console_Extended_DescriptionPane (pane ratio and placement). | U3, G1 |
| VIEW-006 | pass | Console_Shell_Layout (statusHeight 23 DIP). | U3 |
| VIEW-007 | pass | Console_List_ColumnsAndHeaders (headerHeight 24 DIP). | U3, G1 |
| VIEW-008 | pass | Gpedit_ListBlind_CategoryRows. | G1 |
| VIEW-009 | pass | Gpedit_ListBlind_AllSettings and Gpedit_Filter_Results_AllSettings. | G1, G5 |
| VIEW-010 | pass | Gpedit_ListBlind_States; the strings are equal in string-parity (MSG-022). | G1 |
| VIEW-011 | pass | Gpedit_ListBlind_States; the strings are equal in string-parity (MSG-034). | G1 |
| VIEW-012 | pass | Gpedit_ListBlind_CategoryRows. | G1 |
| VIEW-013 | pass | Gpedit_ListBlind_SortCasesOrder and Gpedit_Sort_DefaultOrder. The default order follows classic. | G1, G5 |
| VIEW-014 | pass | Gpedit_Sort_Columns_ThreeClicksEach and Gpedit_Sort_AnotherNode (sort per node, BEH-025). | G5 |
| VIEW-015 | pass | Gpedit_Sort_DigitsCaseAndSymbols and Gpedit_ListBlind_SortCasesDigitRuns. | G5 |
| VIEW-016 | pass | Console_Extended_Tabs and Console_Extended_TabSwitch. | U2c, G1 |
| VIEW-017 | declared-difference | DIFF-002: the Extended pane is native. Console_Extended_DescriptionPane and Gpedit_ListBlind_ExtendedPane. | U2c |
| VIEW-018 | pass | Console_Extended_TabSwitch. | U2c |
| VIEW-019 | pass | Gpedit_ListBlind_StatusBar and Gpedit_List_StatusBar (the count; the text is BWC's own). | G1 |
| VIEW-020 | pass | Gpedit_Filter_CaptureReplay and Gpedit_Filter_OnOffAndStaysOn (flag and narrowed count). The status words are BWC's own (gpedit-filter.md). | G5 |
| VIEW-021 | pass | Gpedit_TreeBlind_PlaceholderPages. Software Settings shows the BWC placeholder page of BEH-003. | G1 |
| VIEW-022 | pass | Gpedit_TreeBlind_PlaceholderPages. | G1 |
| VIEW-023 | gap | No test asserts the tree icons by node kind (root, configuration nodes, folders, Administrative Templates per side, All Settings). | A3 |
| VIEW-024 | pass | Gpedit_TreeBlind_ShapeAndInitialState. | G1 |
| VIEW-025 | pass | Gpedit_ListBlind_LiteralAmpersand. | G1 |
| VIEW-026 | pass | Gpedit_Dialog_Frame and Gpedit_Dialog_Resize (client 684 x 604 DIP). | G3a |
| VIEW-027 | declared-difference | The control and tab-stop counts are of classic's WinForms control tree; BWC differs by the declared radio button tab stops (gpedit-dialog.md). The enabled rule passes in Gpedit_Options_Defaults and Gpedit_Dialog_InitialStates. | G3a |
| VIEW-028 | declared-difference | Counts of classic's control tree, as VIEW-027. Gpedit_Options_Literal and Gpedit_Options_TabOrder prove the labels and the enabled rule. | G3b |
| VIEW-029 | declared-difference | Counts of classic's control tree, as VIEW-027. Gpedit_Options_Numbers and Gpedit_Options_TabOrder prove the number box and the enabled rule. | G3b |
| VIEW-030 | declared-difference | Counts of classic's control tree, as VIEW-027. Gpedit_Options_Numbers proves the long number box. | G3b |
| VIEW-031 | declared-difference | Counts of classic's control tree, as VIEW-027. Gpedit_Options_Layout proves the text box. | G3b |
| VIEW-032 | declared-difference | Counts of classic's control tree, as VIEW-027. Gpedit_Options_Choices proves the combo box. | G3b |
| VIEW-033 | declared-difference | Counts of classic's control tree, as VIEW-027. Gpedit_Options_Elements proves the multi-line box. | G3b |
| VIEW-034 | declared-difference | Counts of classic's control tree, as VIEW-027. Gpedit_Options_Choices proves the drop-down list. | G3b |
| VIEW-035 | declared-difference | Counts of classic's control tree, as VIEW-027. Gpedit_Options_List proves the list. | G3b |
| VIEW-036 | declared-difference | Counts of classic's control tree, as VIEW-027. Gpedit_Options_Elements proves the check boxes in presentation order. | G3b |
| VIEW-037 | declared-difference | Counts of classic's control tree, as VIEW-027. Console_Extended_HelpShapes and Gpedit_Dialog_Frame cover the scrolling help text. | G3a |
| VIEW-038 | declared-difference | Load errors as a status bar notice and list instead of the start box (DLG-121). The tree without the bad category passes in Gpedit_Templates_LoadErrors_BoxCases. | G6 |
| VIEW-039 | pass | Gpedit_Templates_LoadErrors_UndefinedUsing (category and policy show and open). BWC also lists the problem in the notice (gpedit-templates.md), user to confirm. | G6 |
| VIEW-040 | declared-difference | Load errors as a status bar notice and list instead of the box (DLG-121). Left out of the tree: Gpedit_Templates_LoadErrors_AttributeCases. | G6 |
| VIEW-041 | pass | Gpedit_Templates_LoadErrors_BoxCases, Gpedit_TreeBlind_BadTemplatesLeaveGoodTree and Gpedit_Tree_BadTemplates. One bad template does not hide the rest. | G6 |
| VIEW-042 | pass | Gpedit_Filter_OnOffAndStaysOn, Gpedit_Filter_CaptureReplay and Gpedit_Filter_KeptOnAcrossNodesAndReApply. | G5 |
| VIEW-043 | pass | Gpedit_Menu_Action_NodeKinds (both sides) and Gpedit_Toolbar_States. | G2 |

### TREE

| ID | Verdict | Evidence or reason | Piece |
| --- | --- | --- | --- |
| TREE-001 | pass | Gpedit_TreeBlind_ShapeAndInitialState and Gpedit_Tree_Shape. The icon part is VIEW-023. | G1 |
| TREE-002 | pass | Gpedit_TreeBlind_ShapeAndInitialState and Gpedit_Tree_Shape. The icon part is VIEW-023. | G1 |
| TREE-003 | pass | Gpedit_TreeBlind_ShapeAndInitialState and Gpedit_Tree_Shape. The icon part is VIEW-023. | G1 |
| TREE-004 | pass | Gpedit_TreeBlind_ShapeAndInitialState and Gpedit_Tree_Shape. | G1 |
| TREE-005 | pass | Gpedit_TreeBlind_ShapeAndInitialState. The icon part is VIEW-023. | G1 |
| TREE-006 | pass | Gpedit_TreeBlind_ShapeAndInitialState and Gpedit_TreeBlind_PlaceholderPages. | G1 |
| TREE-007 | pass | Gpedit_TreeBlind_ShapeAndInitialState and Gpedit_TreeBlind_PlaceholderPages. | G1 |
| TREE-008 | pass | Gpedit_TreeBlind_ShapeAndInitialState and Gpedit_TreeBlind_PlaceholderPages. | G1 |
| TREE-009 | pass | Gpedit_TreeBlind_ShapeAndInitialState and Gpedit_Tree_Shape. The icon part is VIEW-023. | G1 |
| TREE-010 | gap | No test asserts the Administrative Templates icons per side (see VIEW-023). | A3 |
| TREE-011 | pass | Gpedit_TreeBlind_ShapeAndInitialState and TemplateSetTests. | G1 |
| TREE-012 | pass | Gpedit_TreeBlind_ShapeAndInitialState and Gpedit_Tree_Shape. | G1 |
| TREE-013 | pass | Gpedit_TreeBlind_ShapeAndInitialState. | G1 |
| TREE-014 | pass | Gpedit_TreeBlind_ShapeAndInitialState and Gpedit_ListBlind_States. | G1 |
| TREE-015 | pass | Gpedit_TreeBlind_ShapeAndInitialState and Gpedit_Tree_Shape. | G1 |
| TREE-016 | pass | Gpedit_ListBlind_CategoryRows. | G1 |
| TREE-017 | gap | No test asserts the category and placeholder icons (see VIEW-023). | A3 |
| TREE-018 | pass | Gpedit_TreeBlind_PlaceholderPages and Gpedit_TreeBlind_ExpandCollapseAndKeys. | G1 |
| TREE-019 | gap | Right, Left (Gpedit_TreeBlind_ExpandCollapseAndKeys) and Backspace (Gpedit_Keys_History) pass; Plus, Minus and Asterisk are not asserted (ACC-021 to ACC-023). | A3 |
| TREE-020 | removed | Removed by the user decision of 2026-11-22. Classic shows no Extra Registry Settings node; records that match no policy stay invisible (BEH-014). | none |

### BEH

| ID | Verdict | Evidence or reason | Piece |
| --- | --- | --- | --- |
| BEH-001 | pass | Gpedit_TreeBlind_ShapeAndInitialState and Gpedit_Tree_Shape. | G1 |
| BEH-002 | pass | Gpedit_TreeBlind_ShapeAndInitialState and Gpedit_Tree_Shape. | G1, E2 |
| BEH-003 | pass | Gpedit_TreeBlind_PlaceholderPages and Gpedit_Tree_PlaceholderPages. | G1 |
| BEH-004 | pass | Gpedit_TreeBlind_ShapeAndInitialState, Gpedit_ListBlind_States and Gpedit_ListBlind_CategoryRows. | G1, E2 |
| BEH-005 | pass | Gpedit_ReadOnly_Notice, Gpedit_Dialog_ReadOnly and GpoAccessTests (DIFF-001). | G4, E3 |
| BEH-006 | pass | GpoAccessTests (the Home edition gives the read-only reason) and Gpedit_ReadOnly_Notice (the notice path). The Home text itself is not shown on a Home machine by any run. | G4 |
| BEH-007 | pass | Gpedit_Import_Add_GoodTemplate, Gpedit_Import_Shadowed and TemplateSourcesTests. | G6, E5 |
| BEH-008 | declared-difference | Template import refuses .adm (DIFF-005): "Classic `.adm` files are not supported" (gpedit-templates.md, DIFF-005). Gpedit_Import_Refused_ClassicAdm. | G6, E5 |
| BEH-009 | declared-difference | One bad template does not hide the rest, and every problem is recorded: "BWC does the same, and records every problem, so the list can show all of them" (gpedit-templates.md). The list replaces the message (DLG-121). Gpedit_TreeBlind_AllBadTemplatesRecordedGoodTreeKept and Gpedit_Templates_LoadErrors_BoxCases. | G6, E2 |
| BEH-010 | pass | Gpedit_Filter_CaptureReplay, Gpedit_Filter_Results_AllSettings, PolicyFilterGoldenTests and FilterTests. | G5, E5 |
| BEH-011 | pass | PolicySaveContractTests and GpoSaveTests (write order and one refresh call). The refresh flag is GAP-02. | G4, E3 |
| BEH-012 | removed | Retired by the user decision of 2026-11-22 (no Extra Registry Settings node); replaced by BEH-014. | none |
| BEH-013 | pass | Gpedit_Toolbar_History and Gpedit_Keys_History. | G2 |
| BEH-014 | pass | PolicyGoldenReplayDETests (D/Foreign_*, D/Instruction_*) and PolicyGoldenReplayTests (B/Pol_Outside_KeyOutsideRoots). | E6c, G4 |
| BEH-015 | pass | Gpedit_ListBlind_States, Gpedit_Dialog_InitialStates and PolicyStateMatrixTests. | E6c, G1 |
| BEH-016 | pass | Gpedit_ListBlind_States and Gpedit_List_States. | G1, E6a |
| BEH-017 | pass | Gpedit_TreeBlind_SystemTemplatesNoRoot and Gpedit_Tree_SystemTemplates (the window answers while the real templates load). | G1 |
| BEH-018 | pass | Gpedit_TreeBlind_PersistenceRestart (the root is selected again). | G1 |
| BEH-019 | pass | Gpedit_Keys_Edit (Enter), Gpedit_Menu_Action_Commands (Edit), Gpedit_ListBlind_ExtendedPane and Console_Extended_Link (the link), Console_List_ActivateAndContext (double click). | G2, G3a |
| BEH-020 | pass | Gpedit_Dialog_PreviousNext and Gpedit_Refresh_WhileDialogOpen. | G3a |
| BEH-021 | pass | Gpedit_TreeBlind_PersistenceRestart, Gpedit_Persistence_Restart, Console_Columns_Persist_AcrossRestart and Gpedit_Window_PlacementBeforeShow. | G1, U3a |
| BEH-022 | declared-difference | DIFF-007: Refresh is a BWC addition. Gpedit_Refresh_Reloads, Gpedit_Refresh_KeepsSelection and Gpedit_Refresh_Templates. | G2 |
| BEH-023 | pass | Gpedit_ListBlind_ExtendedPane and Console_Extended_DescriptionPane. | U2c, G1 |
| BEH-024 | pass | Gpedit_ListBlind_StatusBar. | G1 |
| BEH-025 | declared-difference | Sort per node: "Not decided by the captures: all Basics comments are alike" (gpedit-filter.md); BWC keeps a sort per node. Gpedit_Sort_AnotherNode. | G5 |
| BEH-026 | pass | Gpedit_Dialog_OkSaves (the list shows the saved state). The live registry part (G/*) is BEH-027. | G4, G1 |
| BEH-027 | gap | Elevated only: the refresh timing of group G needs a real save with refresh. | A2 |
| BEH-028 | pass | CmtxGoldenTests and PolicyGoldenReplayTests (C/Comment_*, C/Cmtx_*). | E6a, G4 |
| BEH-029 | pass | PolicyGoldenReplayTests (B/Pol_Outside_KeyOutsideRoots). The live part (G/Outside_KeyOutsideRoots) is A2. | E6b |

### POL

| ID | Verdict | Evidence or reason | Piece |
| --- | --- | --- | --- |
| POL-001 | pass | PolicyGoldenReplayTests and Gpedit_Replay_GroupA. | E6a |
| POL-002 | pass | PolicyGoldenReplayTests and Gpedit_Replay_GroupA. | E6a |
| POL-003 | pass | PolicyGoldenReplayTests and Gpedit_Replay_GroupA. | E6a |
| POL-004 | pass | PolicyGoldenReplayTests and Gpedit_Replay_GroupA. | E6a |
| POL-005 | pass | PolicyGoldenReplayTests and Gpedit_Replay_GroupA. | E6a |
| POL-006 | pass | PolicyGoldenReplayTests and Gpedit_Replay_GroupA. | E6a |
| POL-007 | pass | PolicyGoldenReplayTests, GptIniGoldenTests and Gpedit_Replay_GroupA. | E6a |
| POL-008 | pass | PolicyGoldenReplayTests and Gpedit_Replay_GroupA. | E6a |
| POL-009 | pass | PolicyGoldenReplayTests, GptIniGoldenTests and Gpedit_Replay_GroupA. | E6a |
| POL-010 | pass | PolicyGoldenReplayTests and Gpedit_Replay_GroupB_ElementKinds. | E6b |
| POL-011 | pass | PolicyGoldenReplayTests (B cases). The live case G/Value_EnabledDisabledValues is A2 (POL-068 to POL-072). | E6b |
| POL-012 | pass | PolicyGoldenReplayTests. | E6b |
| POL-013 | pass | PolicyGoldenReplayTests. | E6b |
| POL-014 | pass | PolicyGoldenReplayTests. | E6b |
| POL-015 | pass | PolicyGoldenReplayTests. | E6b |
| POL-016 | pass | PolicyGoldenReplayTests (B case). The live case G/Outside_KeyOutsideRoots is A2. | E6b |
| POL-017 | pass | PolicyGoldenReplayTests. | E6b |
| POL-018 | open | Awaiting recapture: B/Pol_Both_WithElements.Computer and .User are CAP-4 cases. | E6b |
| POL-019 | pass | PolicyGoldenReplayTests and Gpedit_Replay_GroupB_ElementKinds. | E6b |
| POL-020 | open | Awaiting recapture: B/Pol_Decimal_MinMax, B/Pol_Decimal_Soft and B/Pol_Decimal_StoreAsText are CAP-4 cases. | E6b |
| POL-021 | open | Awaiting recapture: B/Pol_LongDecimal_Basic is a CAP-4 case. | E6b |
| POL-022 | pass | PolicyGoldenReplayTests and Gpedit_Replay_GroupB_ElementKinds. | E6b |
| POL-023 | pass | PolicyGoldenReplayTests. | E6b |
| POL-024 | pass | PolicyGoldenReplayTests and Gpedit_Replay_GroupB_ElementKinds. | E6b |
| POL-025 | pass | PolicyGoldenReplayTests. Engine rules beyond the captures are GAP-03 and GAP-04. | E6b |
| POL-026 | pass | PolicyGoldenReplayTests. | E6b |
| POL-027 | pass | PolicyGoldenReplayTests. | E6b |
| POL-028 | pass | PolicyGoldenReplayTests. | E6b |
| POL-029 | pass | PolicyGoldenReplayTests. | E6b |
| POL-030 | pass | PolicyGoldenReplayTests. | E6b |
| POL-031 | pass | PolicyGoldenReplayTests. | E6a, E6b |
| POL-032 | pass | PolicyGoldenReplayTests and GptIniGoldenTests. | E6a |
| POL-033 | pass | PolicyGoldenReplayTests and GptIniGoldenTests. | E6a, E6b |
| POL-034 | pass | PolicyGoldenReplayTests. | E6b |
| POL-035 | pass | PolicyGoldenReplayTests. | E6b |
| POL-036 | pass | PolicyGoldenReplayTests. | E6b |
| POL-037 | pass | PolicyGoldenReplayTests and CmtxGoldenTests. | E6a |
| POL-038 | pass | PolicyGoldenReplayTests, CmtxGoldenTests and GptIniGoldenTests. | E6a |
| POL-039 | pass | CmtxGoldenTests and PolicyGoldenReplayTests. Later-session namespaces are GAP-07. | E6a |
| POL-040 | pass | GptIniGoldenTests and PolicyGoldenReplayTests. | E6a |
| POL-041 | pass | GptIniGoldenTests and PolicyGoldenReplayTests. | E6a |
| POL-042 | pass | GptIniGoldenTests and PolicyGoldenReplayTests. | E6a |
| POL-043 | pass | PolicyGoldenReplayDETests and PolicyStateMatrixTests. | E6b, E6c |
| POL-044 | pass | PolicyGoldenReplayDETests and PolicyStateMatrixTests. | E6c |
| POL-045 | pass | PolicyGoldenReplayDETests. | E6c |
| POL-046 | pass | PolicyGoldenReplayDETests and PolicyStateMatrixTests. The soft prefix on a replaced value is GAP-01. | E6c |
| POL-047 | pass | PolicyGoldenReplayDETests and PolicyStateMatrixTests. | E6c |
| POL-048 | pass | PolicyGoldenReplayDETests and PolicyStateMatrixTests. | E6c |
| POL-049 | pass | PolicyGoldenReplayDETests and PolicyStateMatrixTests. | E6c |
| POL-050 | pass | PolicyGoldenReplayDETests and PolicyStateMatrixTests. | E6c |
| POL-051 | pass | PolicyGoldenReplayDETests and PolicyStateMatrixTests. | E6c |
| POL-052 | pass | PolicyGoldenReplayDETests. | E6c |
| POL-053 | pass | PolicyGoldenReplayDETests. | E6c |
| POL-054 | pass | PolicyGoldenReplayDETests. Deeper spellings are GAP-06. | E6c |
| POL-055 | pass | PolicyGoldenReplayDETests. | E6c, G4 |
| POL-056 | pass | PolicyGoldenReplayDETests and PolicyStateMatrixTests. | E6c |
| POL-057 | pass | PolMalformedStartGoldenTests, PolicyGoldenReplayDETests (refused-start path) and Gpedit_Save_Errors_MalformedAtSave. | E6c, G4 |
| POL-058 | pass | PolicyGoldenReplayDETests and PolMalformedStartGoldenTests. Other tails are GAP-05. | E6c |
| POL-059 | open | Awaiting recapture: E/Decimal_EdgeValues is a CAP-4 case. | E6c, G3b |
| POL-060 | open | Awaiting recapture: E/Decimal_NotNumber is a CAP-4 case. | E6c, G3b |
| POL-061 | open | Awaiting recapture: E/Decimal_Soft_OutOfRange and E/Decimal_StoreAsText_OutOfRange are CAP-4 cases. | E6c, G3b |
| POL-062 | open | Awaiting recapture: E/LongDecimal_AboveMax is a CAP-4 case. | E6c, G3b |
| POL-063 | pass | PolicyGoldenReplayDETests and PolicyEditRefusalTests. | E6c, G3b |
| POL-064 | pass | PolicyGoldenReplayDETests, PolicyEditRefusalTests and Gpedit_Dialog_Defaults. | E6c, G3b |
| POL-065 | pass | PolicyGoldenReplayDETests, PolicyEditRefusalTests and Gpedit_Options_List. | E6c, G3b |
| POL-066 | pass | PolicyGoldenReplayDETests and PolicyEditRefusalTests. | E6c, G3b |
| POL-067 | pass | PolicyGoldenReplayDETests and PolicyEditRefusalTests. | E6c, G3a |
| POL-068 | gap | Elevated only: the live registry after a real save with refresh (group G). | A2 |
| POL-069 | gap | Elevated only: the live registry after a real save with refresh (group G). | A2 |
| POL-070 | gap | Elevated only: the live registry after a real save with refresh (group G). | A2 |
| POL-071 | gap | Elevated only: the live registry after a real save with refresh (group G). | A2 |
| POL-072 | gap | Elevated only: the live registry after a real save with refresh (group G). | A2 |
| POL-073 | pass | PolicyGoldenReplayTests (K) and KeyBackslashTests. | E6a |
| POL-074 | pass | PolicyGoldenReplayTests (K) and KeyBackslashTests. | E6a |
| POL-075 | pass | PolicyGoldenReplayTests (K) and KeyBackslashTests. | E6c |

### ADMX

| ID | Verdict | Evidence or reason | Piece |
| --- | --- | --- | --- |
| ADMX-001 | pass | Gpedit_Import_Shadowed, Gpedit_Import_Refused_Duplicates and TemplateSourcesTests. | G6, E5 |
| ADMX-002 | pass | AdmlLanguageChainTests (tests/Bwc.Policy.Tests): the UI culture's parents, then en-US, as classic's fallback list does (finding LANG-1). | A3 |
| ADMX-003 | pass | StrictTemplateTests and SystemTemplatesInvariantTests (the shipped templates load). The H attribute cases are refused as in classic (ADMX-012, ADMX-013). | G6 |
| ADMX-004 | pass | HardeningTests and Gpedit_TreeBlind_BadTemplatesLeaveGoodTree (H/DtdEntity). | G6 |
| ADMX-005 | pass | PolicyTemplateGoldenTests and Gpedit_Templates_LoadErrors_BoxCases. | G6 |
| ADMX-006 | pass | PolicyTemplateGoldenTests and Gpedit_Templates_LoadErrors_BoxCases. | G6 |
| ADMX-007 | pass | PolicyTemplateGoldenTests and Gpedit_Templates_LoadErrors_BoxCases. | G6 |
| ADMX-008 | pass | PolicyTemplateGoldenTests and Gpedit_Templates_LoadErrors_UndefinedUsing. BWC also lists it (VIEW-039). | G6 |
| ADMX-009 | pass | PolicyTemplateGoldenTests and Gpedit_Templates_LoadErrors_BoxCases. | G6 |
| ADMX-010 | pass | PolicyTemplateGoldenTests and Gpedit_Templates_LoadErrors_BoxCases. | G6 |
| ADMX-011 | pass | PolicyTemplateGoldenTests and Gpedit_Templates_LoadErrors_BoxCases. | G6 |
| ADMX-012 | pass | PolicyTemplateGoldenTests and Gpedit_Templates_LoadErrors_AttributeCases. | G6 |
| ADMX-013 | pass | PolicyTemplateGoldenTests and Gpedit_Templates_LoadErrors_AttributeCases. | G6 |
| ADMX-014 | pass | AdmxParserTests and PolicyGoldenReplayTests (B/Pol_Element_KeyOverride). | E2, E6b |
| ADMX-015 | pass | AdmxParserTests, PolicyGoldenReplayTests (B/Pol_Value_*) and PolicyStateMatrixTests (D/Mismatch_*). | E2, E6b |
| ADMX-016 | pass | AdmxParserTests and Gpedit_Options_Elements. The bytes of some number cases wait for the recapture (POL-020, POL-021). | E2, E6b |
| ADMX-017 | pass | Gpedit_Options_Elements and Gpedit_Options_Choices. | E2, G3b |
| ADMX-018 | pass | AdmxParserTests, Gpedit_Options_Numbers and PolicyGoldenReplayDETests. The bytes of some number cases wait for the recapture (POL-059 to POL-062). | E6b, E6c |
| ADMX-019 | pass | TemplateSetTests, AdmxBlindTests and Gpedit_TreeBlind_ShapeAndInitialState. | E2, G1 |
| ADMX-020 | pass | AdmxParserTests, BadTemplateTests and Gpedit_ListBlind_ExtendedPane (the requirements text). | E2 |
| ADMX-021 | pass | KeyBackslashTests and PolicyGoldenReplayTests (K). | E6a, E6c |
| ADMX-022 | pass | Gpedit_ListBlind_AllSettings (display-name order, open for a capture with known templates, gpedit-filter.md). | G1, G5 |
| ADMX-023 | pass | BadTemplateTests and Gpedit_Templates_LoadErrors_BoxCases. | G6, E2 |
| ADMX-024 | pass | Gpedit_TreeBlind_SystemTemplatesNoRoot, Gpedit_Tree_SystemTemplates and SystemTemplatesInvariantTests. The start-time number (PERF-1) is not recorded. | E2, A1 |
| ADMX-025 | pass | AdmxParserTests and Gpedit_ListBlind_CategoryRows. | E2 |
| ADMX-026 | pass | Gpedit_Import_Refused_BadFixtures and TemplateImportTests. | G6 |
| ADMX-027 | pass | Gpedit_Import_Add_GoodTemplate and TemplateImportIntegrityTests. | G6 |
| ADMX-028 | pass | Gpedit_Import_Remove_Template and TemplateStoreWriterTests. | G6 |
| ADMX-029 | pass | TemplateStoreAclTests and Gpedit_Import_Refused_ClassicAdm (DIFF-005). | G6, E5 |
| ADMX-030 | pass | TemplateImportIntegrityTests. | G6 |
| ADMX-031 | pass | AdmlTests, Gpedit_ListBlind_LiteralAmpersand and Gpedit_Options_Literal. | E2, G3a |
| ADMX-032 | pass | PolicyFilterGoldenTests, FilterTests and Gpedit_Filter_CaptureReplay. | G5, E6c |

## Open questions

OQ-01 to OQ-09 are listed in section 4 of the inventory and stay open until the engine pieces (E6a, E6b, E6c) answer them from the POL rows. OQ-09 is answered by the user decision of 2026-11-22: no Extra Registry Settings node, records that match no policy stay invisible and are kept untouched (BEH-014).

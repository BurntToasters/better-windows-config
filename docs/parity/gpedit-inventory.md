# Better Windows Config: gpedit parity inventory

## 1. Introduction

**Purpose.** This is the parity inventory for the Local Group Policy Editor in Better Windows Config (BWC), a WinUI 3 re-implementation of the classic Local Group Policy Editor. Each user-visible feature and each on-disk behavior of classic is one row with a stable ID. Implementation packages move rows to pass as they are built, and the final audit re-checks every row. Inputs: docs/parity/oracle/gpedit (resource dumps, fixture templates, captures) and docs/contracts/policy-engine.md.

**The classic tool.** Classic is `gpedit.msc`, an MMC 3.0 console. The snap-in is `gpedit.dll`. The Administrative Templates extension is `admtmpl.dll`. The policy-setting dialog is not a native dialog: it comes from the managed assembly `Microsoft.GroupPolicy.AdmTmplEditor`. The build under test is Windows 11 26H2 (10.0.26300), en-US. The frame resources (menus, toolbar, dialogs) come from `mmc.exe`, `mmcndmgr.dll` and `mmcbase.dll` and their `.mui` files.

**ID scheme.** An ID is PREFIX-NNN with a three-digit number, assigned in row order within each section. Prefixes: MENU (main menu), CTX (context menus), TOOL (toolbar), DLG (dialogs), ACC (accelerators and keyboard), MSG (strings), VIEW (layout and density), TREE (console tree nodes and their order), BEH (behavior not in the resources), POL (Registry.pol, gpt.ini and comment.cmtx file bytes), ADMX (template handling), DIFF (declared differences from classic). IDs are never reused; a retired row keeps its ID.

**Status values.** open: not yet verified (every row starts here except removed rows). pass: verified against the oracle by a test or golden. declared-difference: BWC intentionally differs; the reason is recorded in the row or in section 4. gap: known missing and not yet declared. removed: the row was retired by a user decision (see section 3) and is kept for its ID only; no audit verdict is owed. DIFF rows start as declared-difference.

**Who fills what.** Piece I1 filled every table with status open, from the static dump (`gpedit-resources.json`, `gpedit-forms.json`, `gpedit-msc.json`) and the capture goldens in `docs/parity/oracle/gpedit/goldens`. MENU, CTX, TOOL, DLG, ACC and MSG come from the static dump and the group F captures, by resource id, command id and control id. TREE, BEH and ADMX come from the finished BWC pieces and the plan. POL and VIEW come from the capture groups A to H and K. DIFF rows and the open questions are as stated by the plan. The audit (`docs/parity/gpedit-audit.md`) gives each row its verdict.

**Row granularity.** MENU has one row per menu occurrence or layout, with the menu ids as the capture dumped them (they are run-time ids of the console frame and differ per menu). CTX describes the menu opened with the menu key on the result list. DLG has one layout row per dialog template in the five modules (88 dialogs, 629 controls, control ids in template order) and behavior rows for the setting dialog, its option kinds, the Show Contents dialog, the Filter Options dialog and the frame dialogs. ACC has one row per accelerator table entry that the local editor can reach, one row per key of the tree and list key captures, and rows for the tables that are not reached. MSG has one row per RT_STRING block of the five modules (the 17 taskpad icon blocks are one row) and one row per managed resource file; the string parity check (`string-parity.json`) gives per-string status counts, so a row's verdict follows it. POL has one row per behavior area and names the capture case folders that specify it (a trailing * names a family). VIEW, TREE, BEH and ADMX have one row per behavior.

**Source notes on ids.** Where a row names a command id as dumped, it is the id seen in the capture of the menu, not a stable resource id. A row that cites an F, A, B, C, D, E, G, H or K case names a folder below `docs/parity/oracle/gpedit/goldens`. Text that the capture stored only as a hash is described by role, never quoted.

**Coverage.** MENU 41, CTX 11, TOOL 17, DLG 123 (88 layout rows and 35 behavior rows), ACC 50, MSG 93 (covering all 1092 string ids), VIEW 43, TREE 20, BEH 29 (13 earlier rows and 16 added), POL 75, ADMX 32: 534 rows. DIFF 6. Three rows are removed rows: MENU-037, TREE-020 and BEH-012.

**Package names.** The Package column uses the piece ids of the 0.2.0 plan (for example E1, G3a, U2b). A row may name two pieces.

**Source notes.** Strings are quoted only as the short UI labels already named in the plan. Microsoft string text, dumps and screenshots stay untracked and are never copied into this file.

## 2. Inventory tables

### 2.1 MENU: main menu

| ID | Feature | Oracle source | Package | Status |
| --- | --- | --- | --- | --- |
| MENU-001 | Layout: the menu bar shows four popups in this order: File, Action, View, Help. Access keys F, A, V, H. The static table also names a Favorites entry (id 13240), a snap-in entry (id 13241) and, in other tables, a Window popup; none of them shows at run time. Separators: see the per-popup layout rows. | mmc.exe RT_MENU 194; F/Menus_Computer, F/Menus_User | U3, G2 | pass |
| MENU-002 | File popup. | mmc.exe RT_MENU 194; F/Menus_Computer, F/Menus_User | U3 | pass |
| MENU-003 | File > Options (command id 13220, access key P). Enabled at every node kind and with a policy row selected. | mmc.exe RT_MENU 194, id 13220; F/Menus_Computer, F/Menus_User | U3b | pass |
| MENU-004 | File > Exit (command id 57665, access key X). Enabled at every node kind. | mmc.exe RT_MENU 194, id 57665; F/Menus_Computer, F/Menus_User | U3, G2 | pass |
| MENU-005 | File layout: two items with one separator between them (Options, separator, Exit). Identical at every node kind and with a policy row selected. Classic has no New, Open, Save, Add/Remove Snap-in or recent file entry in this mode. | mmc.exe RT_MENU 194; F/Menus_Computer, F/Menus_User | U3 | pass |
| MENU-006 | Action popup. Its content depends on the selected node or row; see the layout rows that follow. | F/Menus_Computer, F/Menus_User | G2 | pass |
| MENU-007 | Action layout at the two configuration nodes and at the placeholder nodes: export list (id 1014 as dumped), separator, Help (id 1019 as dumped). No filter entries. | F/Menus_Computer, F/Menus_User | G2 | pass |
| MENU-008 | Action layout at the Administrative Templates node: Add/Remove Templates, filter toggle, Filter Options, filter re-apply entry (grayed), separator, tasks submenu holding the same four entries (re-apply grayed), separator, export list, separator, Help. Ids as dumped: 1018, 1020, 1022, 1024; submenu 1019, 1021, 1023, 1025; 1014; 1027. | F/Menus_Computer, F/Menus_User | G2, G5, G6 | pass |
| MENU-009 | Action layout at a category node and at All Settings: filter toggle, Filter Options, filter re-apply entry (grayed), separator, tasks submenu holding the same three entries, separator, export list, separator, Help. No Add/Remove Templates. Ids as dumped: 1018, 1020, 1022; submenu 1019, 1021, 1023; 1014; 1025. | F/Menus_Computer, F/Menus_User | G2, G5 | pass |
| MENU-010 | Action layout with a policy row selected: Edit, filter toggle, Filter Options, filter re-apply entry (grayed), separator, tasks submenu holding the same four entries, separator, Help. No export list, no Add/Remove Templates. Ids as dumped: 1012, 1014, 1016, 1018; submenu 1013, 1015, 1017, 1019; 1021. | F/Menus_Computer, F/Menus_User | G2, G3a | pass |
| MENU-011 | Action > Add/Remove Templates (admtmpl.dll string 22). Present only at the Administrative Templates node. | admtmpl.dll str 22; F/Menus_Computer, F/Menus_User | G6 | pass |
| MENU-012 | Action > Edit (opens the policy setting dialog; gpedit.dll RT_MENU 1234 id 32784 has the same entry). Present only with a policy row selected. | gpedit.dll RT_MENU 1234, id 32784; F/Menus_Computer, F/Menus_User | G2, G3a | pass |
| MENU-013 | Action > filter toggle (admtmpl.dll strings 3100 and 3101). Present at the Administrative Templates node, at category nodes, at All Settings and with a policy row selected. | admtmpl.dll str 3100, 3101; F/Menus_Computer, F/Menus_User | G5 | pass |
| MENU-014 | Action > Filter Options (admtmpl.dll strings 3102 and 3103). Same nodes as the filter toggle. Opens the Filter Options dialog. | admtmpl.dll str 3102, 3103; F/Menus_Computer, F/Menus_User | G5 | pass |
| MENU-015 | Action > filter re-apply entry (admtmpl.dll strings 3104 and 3105). Same nodes as the filter toggle. Grayed in every dump (no filter was on). | admtmpl.dll str 3104, 3105; F/Menus_Computer, F/Menus_User | G5 | pass |
| MENU-016 | Action > tasks submenu (a copy of the policy and filter entries). Present at the Administrative Templates node, at category nodes, at All Settings and with a policy row selected. | F/Menus_Computer, F/Menus_User | G2, G5 | pass |
| MENU-017 | Action > export list (mmcbase.dll string 13436). Present at every node kind except with a policy row selected. | mmcbase.dll str 13436; F/Menus_Computer, F/Menus_User | U3a | pass |
| MENU-018 | Action > Help (a plain Help entry, last item of the Action popup at every node kind and with a policy row selected). | F/Menus_Computer, F/Menus_User | U3b | pass |
| MENU-019 | View popup. | mmc.exe RT_MENU 194; F/Menus_Computer, F/Menus_User | U3a | pass |
| MENU-020 | View layout at the two configuration nodes and at the placeholder nodes: columns command, separator, four list view modes, separator, Customize View. Ids as dumped: 1000, 1002 to 1005, 1009. | F/Menus_Computer, F/Menus_User | U3a | pass |
| MENU-021 | View layout at the Administrative Templates node, at category nodes, at All Settings and with a policy row selected: columns command, separator, four list view modes, separator, filter toggle, Filter Options, filter re-apply entry (grayed), separator, Customize View. Ids as dumped: 1000, 1002 to 1005, 1008, 1009, 1010, 1012. | F/Menus_Computer, F/Menus_User | U3a, G5 | pass |
| MENU-022 | View > Add/Remove Columns (id 1000 as dumped). Opens the column chooser dialog (mmcndmgr.dll dialog 1070). | F/Menus_Computer, F/Menus_User; mmcndmgr.dll RT_DIALOG 1070 | U3a | pass |
| MENU-023 | View > Large Icons view mode (id 1002 as dumped; gpedit.dll RT_MENU 1234 id 32782). | gpedit.dll RT_MENU 1234, id 32782; F/Menus_Computer, F/Menus_User | U3a | pass |
| MENU-024 | View > Small Icons view mode (id 1003 as dumped; gpedit.dll RT_MENU 1234 id 32776). | gpedit.dll RT_MENU 1234, id 32776; F/Menus_Computer, F/Menus_User | U3a | pass |
| MENU-025 | View > List view mode (id 1004 as dumped; gpedit.dll RT_MENU 1234 id 32780). | gpedit.dll RT_MENU 1234, id 32780; F/Menus_Computer, F/Menus_User | U3a | pass |
| MENU-026 | View > Detail view mode (id 1005 as dumped). Checked in every dump, so Detail is the start mode. | F/Menus_Computer, F/Menus_User | U3a | pass |
| MENU-027 | View > filter toggle (id 1008 as dumped, admtmpl.dll strings 3100 and 3101). Only where the Action menu has it. | admtmpl.dll str 3100, 3101; F/Menus_Computer, F/Menus_User | G5 | pass |
| MENU-028 | View > Filter Options (id 1009 as dumped at these nodes, admtmpl.dll strings 3102 and 3103). | admtmpl.dll str 3102, 3103; F/Menus_Computer, F/Menus_User | G5 | pass |
| MENU-029 | View > filter re-apply entry (id 1010 as dumped, admtmpl.dll strings 3104 and 3105). Grayed in every dump. | admtmpl.dll str 3104, 3105; F/Menus_Computer, F/Menus_User | G5 | pass |
| MENU-030 | View > Customize View (command id 13231 in the static frame table, grayed there; id 1009 as dumped at the configuration nodes and 1012 elsewhere). Opens the view dialog (mmcndmgr.dll dialog 1086). | mmc.exe RT_MENU 162, id 13231; F/Menus_Computer, F/Menus_User | U3a | pass |
| MENU-031 | Help popup. | mmc.exe RT_MENU 194; F/Menus_Computer, F/Menus_User | U3b | pass |
| MENU-032 | Help layout: Help Topics, web site entry, separator, About. The two configuration nodes and the placeholder nodes add a fifth entry, snap-in About entry, after About. Ids: 12804, 13245, 57664, 13242. | mmc.exe RT_MENU 194; F/Menus_Computer, F/Menus_User | U3b | pass |
| MENU-033 | Help > Help Topics (command id 12804). Enabled. | mmc.exe RT_MENU 194, id 12804; F/Menus_Computer, F/Menus_User | U3b | pass |
| MENU-034 | Help > web site entry (command id 13245). Enabled. The audit decides whether BWC adds it. | mmc.exe RT_MENU 194, id 13245; F/Menus_Computer, F/Menus_User | U3b, A1 | pass |
| MENU-035 | Help > About (command id 57664). Enabled. | mmc.exe RT_MENU 194, id 57664; F/Menus_Computer, F/Menus_User | U3b | pass |
| MENU-036 | Help > snap-in About entry (id 13242). Present only at the two configuration nodes and the placeholder nodes. | F/Menus_Computer, F/Menus_User | U3b | pass |
| MENU-037 | Removed 2026-11-22 by user decision: Favorites popup and its entries. Classic shows no Favorites menu at run time (the table row with id 13240 is not on the bar in any dump). BWC builds none. | mmc.exe RT_MENU 194, id 13240; F/Menus_Computer, F/Menus_User | none | removed |
| MENU-038 | Static only: RT_MENU 10 of gpedit.dll (GPO link list menu: override entries, New, Add, Edit, Delete, Rename, Refresh, Properties). Not reached from the local editor. | gpedit.dll RT_MENU 10 | A1 | declared-difference |
| MENU-039 | Static only: RT_MENU 30 of gpedit.dll (two entries for a GPO link). Not reached from the local editor. | gpedit.dll RT_MENU 30 | A1 | declared-difference |
| MENU-040 | Static only: RT_MENU 1234 of gpedit.dll (result list menu: view modes, arrange icons, New, Edit, Delete, Rename, Remove GPO, Refresh, Properties). Only its view mode and Edit ids appear in the dumped menus. | gpedit.dll RT_MENU 1234 | A1 | declared-difference |
| MENU-041 | Static only: RT_MENU 128, 129, 157, 162, 192 of mmc.exe (author mode, MDI and Window menus). None is shown by the local editor. | mmc.exe RT_MENU 128, 129, 157, 162, 192 | A1 | declared-difference |

### 2.2 CTX: context menus

| ID | Feature | Oracle source | Package | Status |
| --- | --- | --- | --- | --- |
| CTX-001 | Context menu opened with the menu key on the result list at the two configuration nodes and at the Windows Settings placeholder: export list, separator, View submenu, separator, icon arrangement submenu, icon line-up entry, separator, Help. Ids as dumped at the configuration node: 1013, View 1016 to 1025, 1028, 1029, 1031. | F/Menus_Computer, F/Menus_User (dumpMenu Context) | G2 | pass |
| CTX-002 | View submenu of that menu: columns command, separator, four list view modes (Detail checked), separator, Customize View. | F/Menus_Computer, F/Menus_User | G2, U3a | pass |
| CTX-003 | icon arrangement submenu holds one entry, automatic arrangement entry (gpedit.dll RT_MENU 1234 id 32797); icon line-up entry (id 32794) follows the submenu. | gpedit.dll RT_MENU 1234, ids 32794, 32797; F/Menus_Computer, F/Menus_User | G2 | pass |
| CTX-004 | Context menu at the Software Settings placeholder: View submenu, separator, icon arrangement submenu, icon line-up entry, separator, Help. No export list. Ids as dumped: 1015 to 1030. | F/Menus_Computer, F/Menus_User | G2 | pass |
| CTX-005 | Context menu at the Administrative Templates node: export list, separator, View submenu with the filter entries (toggle, Filter Options, filter re-apply entry grayed) between the view modes and Customize View, separator, icon arrangement submenu, icon line-up entry, separator, Help. Ids as dumped: 1013, 1016 to 1028, 1031, 1032, 1034. No Add/Remove Templates in this menu. | F/Menus_Computer, F/Menus_User | G2, G5 | pass |
| CTX-006 | Context menu at a category node and at All Settings: the same layout as at the Administrative Templates node. | F/Menus_Computer, F/Menus_User | G2, G5 | pass |
| CTX-007 | Context menu with a policy row selected: Edit, filter toggle, Filter Options, filter re-apply entry (grayed), separator, tasks submenu with the same four entries, separator, Help. Ids as dumped: 1012, 1014, 1016, 1018; submenu 1013, 1015, 1017, 1019; 1021. Identical to the Action popup with a policy row selected. | F/Menus_Computer, F/Menus_User | G2, G3a, G5 | pass |
| CTX-008 | The menu key on the result list opens the context menu with no row selected; with a row selected the menu is the policy row menu. Computer and User Configuration give identical menus at every node kind. | F/Menus_Computer, F/Menus_User | G2 | pass |
| CTX-009 | Not captured: a context menu on the console tree (the capture presses the menu key on the result list only). The audit sets the tree menu. | gap in the captures | G2, A1 | declared-difference |
| CTX-010 | Not captured: a context menu on a list column header and on the empty area of the Extended description pane. | gap in the captures | A1 | declared-difference |
| CTX-011 | Each command of a context menu does what the same command of the Action or View popup does, and a dismissed menu leaves focus where it was. | F/Menus_Computer, F/Menus_User | G2 | pass |

### 2.3 TOOL: toolbar

| ID | Feature | Oracle source | Package | Status |
| --- | --- | --- | --- | --- |
| TOOL-001 | Two toolbar bands stack above the panes: the menu band (y 62, 44 px high at the capture size) and the standard band (y 110, 44 px high). Both are 1500 px wide in a window 1536 px wide. | F/Toolbar_States | U3, G2 | pass |
| TOOL-002 | Menu band: four visible text buttons in this order: File, Action, View, Help, widths 34, 49, 40 and 40 px (command ids 21513, 21509, 21510, 21514). Three more entries (21508, 21511, 21512) exist and are hidden. | F/Toolbar_States | U3 | pass |
| TOOL-003 | Standard band metrics: buttons 23 x 22 px, separators 8 px wide, first button at x 0. Visible x positions: 0, 23, 54, 77, 108, 139, 162, then 193 when the filter button shows. | F/Toolbar_States | U3, G2 | pass |
| TOOL-004 | Back (command 1): visible, enabled in every dump. | F/Toolbar_States | G2 | pass |
| TOOL-005 | Forward (command 2): visible, disabled in every dump. | F/Toolbar_States | G2 | pass |
| TOOL-006 | Separator after Forward (command 3): visible. | F/Toolbar_States | U3 | pass |
| TOOL-007 | Up One Level (command 4): visible, enabled in every dump (every dumped node has a parent). | F/Toolbar_States | G2 | pass |
| TOOL-008 | console tree toggle (command 5): visible, enabled, checked in every dump. | F/Toolbar_States | U3a | pass |
| TOOL-009 | Hidden entries 6 to 9 and 11 to 14 (a separator and seven buttons): present in the band and hidden in every dump; entry 12 is enabled but hidden. Their positions leave no gap. | F/Toolbar_States | U3 | pass |
| TOOL-010 | Separator before Export List (command 10): visible. | F/Toolbar_States | U3 | pass |
| TOOL-011 | Export List (command 15; mmcbase.dll string 13436 names it): visible, enabled in every dump. | F/Toolbar_States; mmcbase.dll str 13436 | U3a | pass |
| TOOL-012 | Separator after Export List (command 16): visible. | F/Toolbar_States | U3 | pass |
| TOOL-013 | Help (command 17): visible, enabled in every dump (its UIA name is the short word Help). | F/Toolbar_States | U3b | pass |
| TOOL-014 | action pane toggle (command 18): visible, enabled in every dump. | F/Toolbar_States | U3a | declared-difference |
| TOOL-015 | Filter button (command 20, preceded by a separator, command 19): present only at the Administrative Templates node, at category nodes, at All Settings and with a policy row selected. Absent at the two configuration nodes and at placeholder nodes. Enabled, not checked while no filter is on. | F/Toolbar_States | G5 | pass |
| TOOL-016 | Toolbar states do not change with the selected node kind except for the filter button, and do not change with a policy row selected. | F/Toolbar_States | G2 | pass |
| TOOL-017 | The toolbar and the console tree can be hidden and shown from the Customize View dialog. | mmcndmgr.dll RT_DIALOG 1086 | U3a | pass |

### 2.4 DLG: dialogs

| ID | Feature | Oracle source | Package | Status |
| --- | --- | --- | --- | --- |
| DLG-001 | Layout: error box with a details area (gpedit.dll dialog 200, 275 x 138 dialog units, 5 controls). Control ids in order: 204, 201, 202, 203, 8. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 200 | A1 | declared-difference |
| DLG-002 | Layout: GPO properties, General tab (gpedit.dll dialog 500, 252 x 218 dialog units, 17 controls). Control ids in order: 501, 510, 505, 506, 507, 508, 509, 502, 503, 504; 7 unlabeled static controls. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 500 | A1 | declared-difference |
| DLG-003 | Layout: GPO properties, Links tab (gpedit.dll dialog 550, 252 x 218 dialog units, 7 controls). Control ids in order: 555, 553, 554, 551; 3 unlabeled static controls. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 550 | A1 | declared-difference |
| DLG-004 | Layout: GPO picker (gpedit.dll dialog 900, 331 x 181 dialog units, 6 controls). Control ids in order: 901, 927, 928, 929, 938; 1 unlabeled static control. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 900 | A1 | declared-difference |
| DLG-005 | Layout: GPO picker, variant (gpedit.dll dialog 977, 331 x 181 dialog units, 6 controls). Control ids in order: 901, 927, 928, 929, 938; 1 unlabeled static control. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 977 | A1 | declared-difference |
| DLG-006 | Layout: delete confirmation for a GPO link (gpedit.dll dialog 1050, 252 x 91 dialog units, 6 controls). Control ids in order: 1054, 1051, 1052, 1053, 1, 2. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 1050 | A1 | declared-difference |
| DLG-007 | Layout: name prompt for a new GPO (gpedit.dll dialog 1060, 300 x 113 dialog units, 7 controls). Control ids in order: 1061, 1062, 1063, 1064, 1065, 1, 2. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 1060 | A1 | declared-difference |
| DLG-008 | Layout: missing directory container error (gpedit.dll dialog 1070, 350 x 125 dialog units, 3 controls). Control ids in order: 1071, 1072, 1. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 1070 | A1 | declared-difference |
| DLG-009 | Layout: RSoP wizard page (gpedit.dll dialog 1088, 331 x 181 dialog units, 9 controls). Control ids in order: 1305, 1405, 1403, 1301, 1300, 1310, 1304; 2 unlabeled static controls. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 1088 | A1 | declared-difference |
| DLG-010 | Layout: RSoP wizard page (gpedit.dll dialog 1089, 331 x 181 dialog units, 9 controls). Control ids in order: 1305, 1405, 1403, 1301, 1300, 1310, 1304; 2 unlabeled static controls. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 1089 | A1 | declared-difference |
| DLG-011 | Layout: RSoP wizard page (gpedit.dll dialog 1090, 331 x 181 dialog units, 5 controls). Control ids in order: 1107, 1417; 3 unlabeled static controls. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 1090 | A1 | declared-difference |
| DLG-012 | Layout: RSoP wizard page (gpedit.dll dialog 1091, 331 x 181 dialog units, 5 controls). Control ids in order: 1305, 1304; 3 unlabeled static controls. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 1091 | A1 | declared-difference |
| DLG-013 | Layout: RSoP wizard page (gpedit.dll dialog 1092, 331 x 181 dialog units, 6 controls). Control ids in order: 1304, 1305, 1306, 1300, 1404; 1 unlabeled static control. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 1092 | A1 | declared-difference |
| DLG-014 | Layout: RSoP wizard page (gpedit.dll dialog 1093, 331 x 181 dialog units, 5 controls). Control ids in order: 1304, 1305, 1301, 1405, 1406. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 1093 | A1 | declared-difference |
| DLG-015 | Layout: RSoP wizard page (gpedit.dll dialog 1094, 331 x 181 dialog units, 20 controls). Control ids in order: 1412, 1413, 1304, 1305, 1306, 1407, 1307, 1408, 1405, 1406, 1308, 1409, 1411, 1410, 1300; 5 unlabeled static controls. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 1094 | A1 | declared-difference |
| DLG-016 | Layout: RSoP wizard page (gpedit.dll dialog 1095, 331 x 181 dialog units, 8 controls). Control ids in order: 1404, 1414, 1305, 1405, 1302, 1304; 2 unlabeled static controls. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 1095 | A1 | declared-difference |
| DLG-017 | Layout: RSoP wizard page (gpedit.dll dialog 1096, 331 x 181 dialog units, 9 controls). Control ids in order: 1306, 1300, 1307, 1310, 1302, 1304; 3 unlabeled static controls. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 1096 | A1 | declared-difference |
| DLG-018 | Layout: RSoP wizard page (gpedit.dll dialog 1097, 331 x 181 dialog units, 7 controls). Control ids in order: 1301, 1300, 1310, 1302, 1304; 2 unlabeled static controls. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 1097 | A1 | declared-difference |
| DLG-019 | Layout: RSoP wizard page (gpedit.dll dialog 1098, 331 x 181 dialog units, 7 controls). Control ids in order: 1301, 1300, 1310, 1302, 1304; 2 unlabeled static controls. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 1098 | A1 | declared-difference |
| DLG-020 | Layout: RSoP wizard page (gpedit.dll dialog 1099, 331 x 181 dialog units, 4 controls). Control ids in order: 1301, 1401; 2 unlabeled static controls. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 1099 | A1 | declared-difference |
| DLG-021 | Layout: RSoP properties, General tab (gpedit.dll dialog 1100, 252 x 218 dialog units, 8 controls). Control ids in order: 1307, 1301, 1414, 1404, 1415, 1300, 1310; 1 unlabeled static control. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 1100 | A1 | declared-difference |
| DLG-022 | Layout: RSoP properties, General tab, variant (gpedit.dll dialog 1102, 252 x 218 dialog units, 3 controls). Control ids in order: 1301; 2 unlabeled static controls. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 1102 | A1 | declared-difference |
| DLG-023 | Layout: RSoP properties, error tab (gpedit.dll dialog 1103, 252 x 218 dialog units, 6 controls). Control ids in order: 1301, 1306, 1300; 3 unlabeled static controls. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 1103 | A1 | declared-difference |
| DLG-024 | Layout: domain controller browser (gpedit.dll dialog 1104, 240 x 200 dialog units, 4 controls). Control ids in order: 1301, 1, 2; 1 unlabeled static control. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 1104 | A1 | declared-difference |
| DLG-025 | Layout: RSoP wizard page (gpedit.dll dialog 1105, 331 x 181 dialog units, 4 controls). Control ids in order: 1107; 3 unlabeled static controls. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 1105 | A1 | declared-difference |
| DLG-026 | Layout: RSoP wizard page (gpedit.dll dialog 1106, 331 x 181 dialog units, 7 controls). Control ids in order: 1301, 1401, 1306, 1300; 3 unlabeled static controls. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 1106 | A1 | declared-difference |
| DLG-027 | Layout: RSoP wizard page (gpedit.dll dialog 1108, 331 x 181 dialog units, 8 controls). Control ids in order: 1301, 1401, 1304, 1306, 1300; 3 unlabeled static controls. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 1108 | A1 | declared-difference |
| DLG-028 | Layout: RSoP wizard page (gpedit.dll dialog 1109, 331 x 181 dialog units, 5 controls). Control ids in order: 1301, 1401, 1304; 2 unlabeled static controls. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 1109 | A1 | declared-difference |
| DLG-029 | Layout: domain picker (gpedit.dll dialog 1203, 305 x 159 dialog units, 5 controls). Control ids in order: 1302, 1307, 1308, 1301; 1 unlabeled static control. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 1203 | A1 | declared-difference |
| DLG-030 | Layout: computer picker (gpedit.dll dialog 1237, 305 x 159 dialog units, 4 controls). Control ids in order: 1304, 1305, 1306, 1300. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 1237 | A1 | declared-difference |
| DLG-031 | Layout: user picker (gpedit.dll dialog 1238, 305 x 159 dialog units, 2 controls). Control ids in order: 1308, 1301. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 1238 | A1 | declared-difference |
| DLG-032 | Layout: RSoP progress (gpedit.dll dialog 1416, 331 x 221 dialog units, 7 controls). Control ids in order: 1417, 1301, 1307, 1401; 3 unlabeled static controls. Not opened by any dumped menu entry. | gpedit.dll RT_DIALOG 1416 | A1 | declared-difference |
| DLG-033 | Layout: Add/Remove Templates dialog (admtmpl.dll dialog 300, 288 x 150 dialog units, 5 controls). Control ids in order: 301, 302, 303, 304, 8. Opened by the Action entry that adds or removes templates. | admtmpl.dll RT_DIALOG 300 | G6 | pass |
| DLG-034 | Layout: template details box with a Close button (admtmpl.dll dialog 800, 275 x 141 dialog units, 5 controls). Control ids in order: 804, 801, 802, 803, 8. | admtmpl.dll RT_DIALOG 800 | G6 | pass |
| DLG-035 | Layout: Filter Options dialog (admtmpl.dll dialog 3500, 330 x 351 dialog units, 29 controls). Control ids in order: 609, 3501, 3502, 3503, 3504, 3515, 3505, 3517, 3516, 3506, 3507, 3508, 3509, 608, 3514, 3510, 3511, 3512, 3513, 1, 2; 8 unlabeled static controls. Opened by Filter Options. | admtmpl.dll RT_DIALOG 3500 | G5 | pass |
| DLG-036 | Layout: web address prompt (mmc.exe dialog 131, 180 x 74 dialog units, 4 controls). Control ids in order: 1, 2, 1001; 1 unlabeled static control. Not opened by any dumped menu entry. | mmc.exe RT_DIALOG 131 | A1 | declared-difference |
| DLG-037 | Layout: package add prompt (mmc.exe dialog 132, 244 x 149 dialog units, 4 controls). Control ids in order: 1, 2, 1000; 1 unlabeled static control. Not opened by any dumped menu entry. | mmc.exe RT_DIALOG 132 | A1 | declared-difference |
| DLG-038 | Layout: console Options dialog (mmc.exe dialog 158, 252 x 218 dialog units, 11 controls). Control ids in order: 1028, 1027, 1025, 1022, 1023, 1024, 1029; 4 unlabeled static controls. Opened by File > Options. | mmc.exe RT_DIALOG 158 | U3b | pass |
| DLG-039 | Layout: favorites add dialog (mmc.exe dialog 170, 290 x 185 dialog units, 8 controls). Control ids in order: 171, 172, 1, 2, 173; 3 unlabeled static controls. Not opened by any dumped menu entry. | mmc.exe RT_DIALOG 170 | A1 | declared-difference |
| DLG-040 | Layout: new Favorites folder (mmc.exe dialog 174, 232 x 86 dialog units, 5 controls). Control ids in order: 175, 1, 2; 2 unlabeled static controls. Not opened by any dumped menu entry. | mmc.exe RT_DIALOG 174 | A1 | declared-difference |
| DLG-041 | Layout: favorites organizer (mmc.exe dialog 176, 256 x 275 dialog units, 10 controls). Control ids in order: 172, 173, 178, 179, 177, 171, 180, 1; 2 unlabeled static controls. Not opened by any dumped menu entry. | mmc.exe RT_DIALOG 176 | A1 | declared-difference |
| DLG-042 | Layout: Favorites folder picker (mmc.exe dialog 183, 167 x 175 dialog units, 4 controls). Control ids in order: 1, 2, 172; 1 unlabeled static control. Not opened by any dumped menu entry. | mmc.exe RT_DIALOG 183 | A1 | declared-difference |
| DLG-043 | Layout: disk cleanup prompt (mmc.exe dialog 199, 252 x 218 dialog units, 4 controls). Control ids in order: 1032, 1031, 1033, 1034. Not opened by any dumped menu entry. | mmc.exe RT_DIALOG 199 | A1 | declared-difference |
| DLG-044 | Layout: main console dialog (mmc.exe dialog 200, 328 x 238 dialog units, 12 controls). Control ids in order: 1040, 1041, 1, 2, 1035, 1036, 1037, 1038, 1039; 3 unlabeled static controls. Not opened by any dumped menu entry. | mmc.exe RT_DIALOG 200 | A1 | declared-difference |
| DLG-045 | Layout: one-control strip (mmc.exe dialog 201, 370 x 17 dialog units, 1 controls). Control ids in order: 1002. Not opened by any dumped menu entry. | mmc.exe RT_DIALOG 201 | A1 | declared-difference |
| DLG-046 | Layout: About dialog (mmcndmgr.dll dialog 107, 206 x 127 dialog units, 4 controls). Control ids in order: 1, 4000, 4160, 4161. Probably opened by an About entry (not captured). | mmcndmgr.dll RT_DIALOG 107 | U3b | declared-difference |
| DLG-047 | Layout: taskpad Tasks dialog (mmcndmgr.dll dialog 108, 252 x 218 dialog units, 7 controls). Control ids in order: 4036, 1014, 1015, 1017, 1020, 1021; 1 unlabeled static control. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 108 | A1 | declared-difference |
| DLG-048 | Layout: ActiveX insert, page 1 (mmcndmgr.dll dialog 143, 317 x 143 dialog units, 4 controls). Control ids in order: 212, 1000; 2 unlabeled static controls. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 143 | A1 | declared-difference |
| DLG-049 | Layout: ActiveX insert, page 2 (mmcndmgr.dll dialog 144, 317 x 193 dialog units, 2 controls). Control ids in order: 1022; 1 unlabeled static control. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 144 | A1 | declared-difference |
| DLG-050 | Layout: web link, page 1 (mmcndmgr.dll dialog 145, 317 x 193 dialog units, 5 controls). Control ids in order: 1023, 1024, 4099; 2 unlabeled static controls. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 145 | A1 | declared-difference |
| DLG-051 | Layout: web link, page 2 (mmcndmgr.dll dialog 146, 317 x 193 dialog units, 2 controls). Control ids in order: 1022; 1 unlabeled static control. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 146 | A1 | declared-difference |
| DLG-052 | Layout: ActiveX insert, page 3 (mmcndmgr.dll dialog 149, 317 x 193 dialog units, 3 controls). Control ids in order: 4099; 2 unlabeled static controls. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 149 | A1 | declared-difference |
| DLG-053 | Layout: General page (mmcndmgr.dll dialog 156, 252 x 218 dialog units, 1 controls). Control ids in order: 4024. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 156 | A1 | declared-difference |
| DLG-054 | Layout: standalone snap-in picker (mmcndmgr.dll dialog 159, 252 x 225 dialog units, 6 controls). Control ids in order: 1049, 1, 2, 4007; 2 unlabeled static controls. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 159 | A1 | declared-difference |
| DLG-055 | Layout: snap-in list manager (mmcndmgr.dll dialog 160, 448 x 274 dialog units, 17 controls). Control ids in order: 4173, 4008, 4182, 4180, 4174, 4175, 4176, 4179, 4177, 4178, 4186, 4007, 1, 2; 3 unlabeled static controls. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 160 | A1 | declared-difference |
| DLG-056 | Layout: extension picker (mmcndmgr.dll dialog 161, 259 x 314 dialog units, 12 controls). Control ids in order: 4189, 4190, 4020, 4026, 4176, 4007, 1, 2; 4 unlabeled static controls. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 161 | A1 | declared-difference |
| DLG-057 | Layout: Standalone tab (mmcndmgr.dll dialog 1055, 259 x 225 dialog units, 11 controls). Control ids in order: 4018, 1053, 4011, 4007, 4008, 4009, 4014, 4021; 3 unlabeled static controls. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 1055 | A1 | declared-difference |
| DLG-058 | Layout: Extensions tab (mmcndmgr.dll dialog 1056, 259 x 226 dialog units, 11 controls). Control ids in order: 4022, 4018, 4025, 4023, 4020, 4014, 4010, 4007, 4021, 4026; 1 unlabeled static control. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 1056 | A1 | declared-difference |
| DLG-059 | Layout: General page of a taskpad (mmcndmgr.dll dialog 1057, 252 x 218 dialog units, 17 controls). Control ids in order: 1001, 4091, 4147, 4148, 4149, 4162, 4151, 4150, 4152, 4092, 4033, 1012; 5 unlabeled static controls. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 1057 | A1 | declared-difference |
| DLG-060 | Layout: taskpad options page (mmcndmgr.dll dialog 1059, 252 x 218 dialog units, 6 controls). Control ids in order: 4146, 4128, 4129, 1, 2; 1 unlabeled static control. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 1059 | A1 | declared-difference |
| DLG-061 | Layout: Add/Remove Columns dialog (mmcndmgr.dll dialog 1070, 346 x 172 dialog units, 12 controls). Control ids in order: 4084, 4085, 4086, 4088, 4089, 4090, 4087, 1, 2; 3 unlabeled static controls. Opened by View > Add/Remove Columns. | mmcndmgr.dll RT_DIALOG 1070 | U3a | pass |
| DLG-062 | Layout: New Task wizard page (mmcndmgr.dll dialog 1072, 317 x 193 dialog units, 3 controls). Control ids in order: 4099; 2 unlabeled static controls. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 1072 | A1 | declared-difference |
| DLG-063 | Layout: New Task wizard page (mmcndmgr.dll dialog 1074, 317 x 143 dialog units, 5 controls). Control ids in order: 4072, 4071, 4130; 2 unlabeled static controls. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 1074 | A1 | declared-difference |
| DLG-064 | Layout: New Task wizard page (mmcndmgr.dll dialog 1075, 317 x 143 dialog units, 12 controls). Control ids in order: 4167, 4166, 4113, 4169, 4003, 4168, 4004, 4170, 4165, 4163; 2 unlabeled static controls. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 1075 | A1 | declared-difference |
| DLG-065 | Layout: New Task wizard page (mmcndmgr.dll dialog 1076, 317 x 143 dialog units, 12 controls). Control ids in order: 4055, 4052, 4050, 4054, 4053, 4080, 4061, 4060, 4051, 4059, 4058; 1 unlabeled static control. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 1076 | A1 | declared-difference |
| DLG-066 | Layout: New Task wizard page (mmcndmgr.dll dialog 1077, 317 x 143 dialog units, 8 controls). Control ids in order: 4153, 4154, 4159, 4069, 4068, 4067, 4110; 1 unlabeled static control. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 1077 | A1 | declared-difference |
| DLG-067 | Layout: command line page of a task (mmcndmgr.dll dialog 1080, 252 x 218 dialog units, 11 controls). Control ids in order: 4055, 4052, 4050, 4054, 4053, 4080, 4061, 4060, 4051, 4059, 4058. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 1080 | A1 | declared-difference |
| DLG-068 | Layout: General page of a task (mmcndmgr.dll dialog 1081, 252 x 218 dialog units, 4 controls). Control ids in order: 4072, 4071, 4130; 1 unlabeled static control. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 1081 | A1 | declared-difference |
| DLG-069 | Layout: task icon page (mmcndmgr.dll dialog 1082, 252 x 218 dialog units, 12 controls). Control ids in order: 4167, 4166, 4113, 4169, 4003, 4168, 4004, 4170, 4165, 4163; 2 unlabeled static controls. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 1082 | A1 | declared-difference |
| DLG-070 | Layout: New Taskpad View wizard page (mmcndmgr.dll dialog 1083, 317 x 193 dialog units, 4 controls). Control ids in order: 4099; 3 unlabeled static controls. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 1083 | A1 | declared-difference |
| DLG-071 | Layout: New Taskpad View wizard page (mmcndmgr.dll dialog 1084, 317 x 143 dialog units, 12 controls). Control ids in order: 4147, 4148, 4149, 4162, 4033, 4151, 4150, 4152, 4092; 3 unlabeled static controls. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 1084 | A1 | declared-difference |
| DLG-072 | Layout: New Taskpad View wizard page (mmcndmgr.dll dialog 1085, 317 x 143 dialog units, 5 controls). Control ids in order: 1001, 4091; 3 unlabeled static controls. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 1085 | A1 | declared-difference |
| DLG-073 | Layout: Customize View dialog (mmcndmgr.dll dialog 1086, 214 x 223 dialog units, 13 controls). Control ids in order: 4171, 4191, 4143, 4144, 4139, 4141, 4142, 4145, 4172, 4192, 4138, 4140, 1. Opened by View > Customize View. | mmcndmgr.dll RT_DIALOG 1086 | U3a | pass |
| DLG-074 | Layout: New Taskpad View wizard page (mmcndmgr.dll dialog 1087, 317 x 155 dialog units, 13 controls). Control ids in order: 4046, 4047, 4048, 1034, 4044, 1041, 4125; 6 unlabeled static controls. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 1087 | A1 | declared-difference |
| DLG-075 | Layout: New Task wizard page (mmcndmgr.dll dialog 1089, 317 x 143 dialog units, 7 controls). Control ids in order: 4115, 4116, 4155; 4 unlabeled static controls. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 1089 | A1 | declared-difference |
| DLG-076 | Layout: New Task wizard page (mmcndmgr.dll dialog 1090, 317 x 193 dialog units, 11 controls). Control ids in order: 4100, 4036, 4124, 1014, 1015, 1017, 1020, 1021; 3 unlabeled static controls. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 1090 | A1 | declared-difference |
| DLG-077 | Layout: New Taskpad View wizard page (mmcndmgr.dll dialog 1097, 317 x 193 dialog units, 5 controls). Control ids in order: 4100, 1018; 3 unlabeled static controls. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 1097 | A1 | declared-difference |
| DLG-078 | Layout: New Taskpad View wizard page (mmcndmgr.dll dialog 1098, 317 x 143 dialog units, 5 controls). Control ids in order: 4146, 4128, 4129; 2 unlabeled static controls. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 1098 | A1 | declared-difference |
| DLG-079 | Layout: New Task wizard page (mmcndmgr.dll dialog 1099, 317 x 143 dialog units, 3 controls). Control ids in order: 4157; 2 unlabeled static controls. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 1099 | A1 | declared-difference |
| DLG-080 | Layout: Advanced snap-in options (mmcndmgr.dll dialog 4187, 300 x 73 dialog units, 4 controls). Control ids in order: 4188, 1, 2; 1 unlabeled static control. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 4187 | A1 | declared-difference |
| DLG-081 | Layout: waiting for a snap-in (mmcndmgr.dll dialog 6000, 252 x 68 dialog units, 3 controls). Control ids in order: 6004, 6005, 2. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 6000 | A1 | declared-difference |
| DLG-082 | Layout: unhandled snap-in exception (mmcndmgr.dll dialog 6006, 252 x 159 dialog units, 9 controls). Control ids in order: 6001, 6002, 6007, 6008, 6009, 1; 3 unlabeled static controls. Not opened by any dumped menu entry. | mmcndmgr.dll RT_DIALOG 6006 | A1 | declared-difference |
| DLG-083 | Layout: snap-in error, variant with details (mmcbase.dll dialog 104, 317 x 134 dialog units, 7 controls). Control ids in order: 1003, 1004, 1005, 1006, 1; 2 unlabeled static controls. Not opened by any dumped menu entry. | mmcbase.dll RT_DIALOG 104 | A1 | declared-difference |
| DLG-084 | Layout: snap-in error, variant (mmcbase.dll dialog 105, 317 x 99 dialog units, 6 controls). Control ids in order: 1003, 1004, 1005, 1; 2 unlabeled static controls. Not opened by any dumped menu entry. | mmcbase.dll RT_DIALOG 105 | A1 | declared-difference |
| DLG-085 | Layout: snap-in error, variant (mmcbase.dll dialog 106, 317 x 99 dialog units, 6 controls). Control ids in order: 1003, 1004, 1005, 1; 2 unlabeled static controls. Not opened by any dumped menu entry. | mmcbase.dll RT_DIALOG 106 | A1 | declared-difference |
| DLG-086 | Layout: console message box (mmcbase.dll dialog 107, 291 x 79 dialog units, 3 controls). Control ids in order: 1003, 1, 1007. Not opened by any dumped menu entry. | mmcbase.dll RT_DIALOG 107 | A1 | declared-difference |
| DLG-087 | Layout: icon picker (mmcbase.dll dialog 1004, 212 x 181 dialog units, 7 controls). Control ids in order: 1005, 1006, 1, 2, 1007; 2 unlabeled static controls. Not opened by any dumped menu entry. | mmcbase.dll RT_DIALOG 1004 | A1 | declared-difference |
| DLG-088 | Layout: snap-in end prompt (mmcbase.dll dialog 5001, 252 x 88 dialog units, 3 controls). Control ids in order: 2, 1; 1 unlabeled static control. Not opened by any dumped menu entry. | mmcbase.dll RT_DIALOG 5001 | A1 | declared-difference |
| DLG-089 | Setting dialog (managed Editor form of the AdmTmplEditor assembly): client 684 x 604 DIP, minimum window 700 x 640 DIP, sizing border, maximize box, no minimize box. Capture at 192 DPI: window 1400 x 1286 px for every option kind. Control names: buttonPrevious, buttonNext, radioButtonNotConfigured, radioButtonEnabled, radioButtonDisabled, textBoxComment, labelSupportedOnText, textBoxHelp, buttonOK, buttonCancel, buttonApply, plus the title label, icon box, split container and dynamic options panel. | gpedit-forms.json Editor form; F/Dialog_Layouts | G3a | declared-difference |
| DLG-090 | Setting dialog header: icon, policy name, Previous Setting and Next Setting buttons. The name is the display name, not the policy id. | gpedit-forms.json; B/*, F/Dialog_Layouts | G3a | pass |
| DLG-091 | Setting dialog states: Not Configured, Enabled, Disabled radio buttons in that order. Initial state follows the policy state read from the side's file. | gpedit-forms.json; B/*, D/Mismatch_*, D/Partial_* | G3a, E6c | pass |
| DLG-092 | Setting dialog Comment box: multi-line, kept per policy, saved in comment.cmtx. | gpedit-forms.json; C/Comment_* | G3a, E6a | pass |
| DLG-093 | Setting dialog Supported on box: read-only, shows the supported-on display text. | gpedit-forms.json; B/* | G3a | pass |
| DLG-094 | Setting dialog Options pane (left) and Help pane (right), split 301 : 5 : 372 DIP at the default size; both grow with the form. | gpedit-forms.json | G3a, G3b | pass |
| DLG-095 | Setting dialog tab order: Previous Setting, Next Setting, the selected radio button, Comment, Supported on, Help, OK, Cancel; focus starts on Previous Setting. Control order and tab stops per option kind are in the capture. | gpedit-forms.json; F/Dialog_Layouts | G3a | declared-difference |
| DLG-096 | Apply starts disabled and becomes enabled when the state, the comment or an option value changes from what was last applied or read. | E/*, B/* | G3a | pass |
| DLG-097 | Not Configured disables the option controls; Enabled enables them (enabled control counts differ between the two states for every option kind). | F/Dialog_Layouts | G3b | pass |
| DLG-098 | OK applies when changed and closes; Apply applies and stays; Cancel and Escape change nothing and write nothing. | A/Machine_Cancel, A/Machine_Apply_Cancel, A/Machine_Apply_OK, C/Apply_Then_OK, C/NoChange_Apply | G3a, G4 | pass |
| DLG-099 | Previous Setting and Next Setting move through the shown policies in display order. Behavior at the first policy, the last policy, a middle policy and the only policy of a node is in the capture. | F/PrevNext_Ends | G3a | pass |
| DLG-100 | Previous Setting and Next Setting with a required number left empty: Next Setting shows a message box and stays; Previous Setting shows none. Neither writes. | E/Previous_WithInvalid, E/Next_WithInvalid | G3a, E6c | pass |
| DLG-101 | Some invalid edits raise a standard message box with one OK button on OK or Apply (required text or combo box empty, required drop-down with no choice, multi-line limits, empty or duplicate list names and entries); the dialog stays open and nothing is written. Other edits are accepted and written; the case folders say which. The message text is not tracked. | E/* | G3b, E6c | pass |
| DLG-102 | Option kind: check box (boolean), one or more. Layout row for two check boxes in reverse definition order. | F/Dialog_Layouts (Pol_Bool_TwoChecksReverseOrder) | G3b | pass |
| DLG-103 | Option kind: number box (decimal) with minimum, maximum and step. Layout and enabled state. | F/Dialog_Layouts (Pol_Decimal_MinMax) | G3b | pass |
| DLG-104 | Option kind: long number box (long decimal). | F/Dialog_Layouts (Pol_LongDecimal_Basic) | G3b | pass |
| DLG-105 | Option kind: text box. | F/Dialog_Layouts (Pol_Text_Plain) | G3b | pass |
| DLG-106 | Option kind: combo box with suggestions. | F/Dialog_Layouts (Pol_Text_ComboBoxSuggestions) | G3b | pass |
| DLG-107 | Option kind: multi-line text box. | F/Dialog_Layouts (Pol_MultiText_Basic) | G3b | pass |
| DLG-108 | Option kind: drop-down list (enum). | F/Dialog_Layouts (Pol_Enum_DecimalItems) | G3b | pass |
| DLG-109 | Option kind: list with a Show Contents button. | F/Dialog_Layouts (Pol_List_Plain) | G3b | pass |
| DLG-110 | Option labels keep an ampersand as a literal ampersand (no access key made from it). | F/Dialog_Layouts (Pol_Amp_LabelSingle), B/Pol_Amp_* | G3b | pass |
| DLG-111 | Show Contents dialog (managed ListBoxDialog form): a grid, a label, OK and Cancel. Plain lists have one column; explicit lists have name and value columns. | gpedit-forms.json ListBoxDialog form; E/List_* | G3b | pass |
| DLG-112 | Show Contents on OK: an empty or duplicate plain entry and an empty or duplicate explicit name are refused with a message box and the list stays open; an empty explicit value, a one-cell row, a long entry and special characters are accepted. | E/List_Plain_EmptyEntry, E/List_Explicit_EmptyName, E/List_Explicit_EmptyValue, E/List_Explicit_DuplicateNames, E/List_Explicit_NameOnly | G3b, E6c | pass |
| DLG-113 | Filter Options dialog: parts Managed, Configured, Commented (each Any, Yes, No), Keywords (text, match Any, All or Exact, search in title, help text, comment) and Requirements (platform list, match Any or All), OK and Cancel. Control ids 3501 to 3517 and 608, 609. | admtmpl.dll RT_DIALOG 3500; F/Filter_Options | G5 | pass |
| DLG-114 | Filter Options refuses a keyword filter with no word, a keyword filter with no Within box, and a requirements filter with no platform, each with a message box. | admtmpl.dll str 3121 to 3123; F/Filter_Options | G5 | pass |
| DLG-115 | Add/Remove Templates dialog (admtmpl.dll dialog 300): a list view of the current templates (302), Add (303), Remove (304) and Close buttons. | admtmpl.dll RT_DIALOG 300 | G6 | pass |
| DLG-116 | Add/Remove Templates add: a file picker, the file is checked and copied into the import store. Not captured live. A second dialog (admtmpl.dll 800: a details box and a Close button) exists in the resources. | admtmpl.dll RT_DIALOG 300, 800 | G6 | pass |
| DLG-117 | Customize View dialog: check boxes for the console tree, standard menus, standard toolbar, status bar, description bar, taskpad navigation tabs, action pane, snap-in menus and snap-in toolbars (control ids 4143, 4144, 4139, 4141, 4142, 4145, 4172, 4138, 4140), and an OK button only. | mmcndmgr.dll RT_DIALOG 1086 | U3a | pass |
| DLG-118 | Add/Remove Columns dialog: available and displayed column lists with Add, Remove, Move Up, Move Down, Restore Defaults, OK and Cancel. | mmcndmgr.dll RT_DIALOG 1070 | U3a | pass |
| DLG-119 | Options dialog (File > Options): console name box (1027), icon picker button (1025), console mode drop-down (1022) with a description area (1023), a check box for not saving changes (1024) and a check box for letting the user customize views (1029). The template has no OK or Cancel button. | mmc.exe RT_DIALOG 158 | U3b | declared-difference |
| DLG-120 | About dialog: product information and an OK button. BWC follows the Registry Editor (DIFF-006). | mmcndmgr.dll RT_DIALOG 107 | U3b | declared-difference |
| DLG-121 | Template load error message box: one box at start when any template fails; it has an OK button and the Administrative Templates caption; text is not tracked. | H/* | G6 | declared-difference |
| DLG-122 | The template load error box appears once at start and not again when nodes are selected or the bad policy is browsed. | H/MalformedXml (steps 0, 3, 10) | G6 | declared-difference |
| DLG-123 | Message boxes of validation and load errors are standard boxes: owner disabled while open, Enter and OK dismiss. | E/*, H/* | G3a, G6 | pass |

### 2.5 ACC: accelerators and keyboard

| ID | Feature | Oracle source | Package | Status |
| --- | --- | --- | --- | --- |
| ACC-001 | Table 128, entry 1: Ctrl+P, command 13210. | mmc.exe RT_ACCELERATOR 128 | G2, A1 | declared-difference |
| ACC-002 | Table 128, entry 2: F1, command 13215. | mmc.exe RT_ACCELERATOR 128 | G2, U3b | pass |
| ACC-003 | Table 128, entry 3: Ctrl+F10, command 13211. | mmc.exe RT_ACCELERATOR 128 | A1 | declared-difference |
| ACC-004 | Table 128, entry 4: F5, command 13209 (refresh). In F/Keys_List and F/Keys_Tree the press opens no window and no menu. | mmc.exe RT_ACCELERATOR 128; F/Keys_List, F/Keys_Tree | G2 | pass |
| ACC-005 | Table 128, entry 5: Ctrl+F5, command 13212. | mmc.exe RT_ACCELERATOR 128 | A1 | declared-difference |
| ACC-006 | Table 128, entry 6: F6, command 13213 (next pane). In F/Keys_Tree step 16 the focus is on the result side. | mmc.exe RT_ACCELERATOR 128; F/Keys_Tree | G2 | pass |
| ACC-007 | Table 128, entry 7: Shift+F6, command 13214 (previous pane). | mmc.exe RT_ACCELERATOR 128 | G2 | pass |
| ACC-008 | Table 128, entry 8: Ctrl+Alt+Shift+F7, command 13235. | mmc.exe RT_ACCELERATOR 128 | A1 | declared-difference |
| ACC-009 | Table 128, entry 9: Tab, command 13213 (next pane). F/Keys_Tree step 13 moves the focus from the tree to the result side; F/Keys_List step 12 moves it from the list. | mmc.exe RT_ACCELERATOR 128; F/Keys_Tree, F/Keys_List | G2 | pass |
| ACC-010 | Table 128, entry 10: Shift+Tab, command 13214 (previous pane). F/Keys_Tree step 14 returns the focus to the tree. | mmc.exe RT_ACCELERATOR 128; F/Keys_Tree | G2 | pass |
| ACC-011 | Table 129 (author mode, 5 entries: Ctrl+N 57600, Ctrl+O 57601, Ctrl+M 13201, Ctrl+S 57603, Ctrl+W 57648). The matching commands are not on any dumped menu, so the table is not reached. | mmc.exe RT_ACCELERATOR 129; F/Menus_* | A1 | declared-difference |
| ACC-012 | Table 157 (MDI user mode, 2 entries: Ctrl+N 13243, Ctrl+W 57648). The matching commands are not on any dumped menu, so the table is not reached. | mmc.exe RT_ACCELERATOR 157; F/Menus_* | A1 | declared-difference |
| ACC-013 | Tree pane, Left (F/Keys_Tree step 1): the selection moves to the parent category of the start node. | F/Keys_Tree | G2 | pass |
| ACC-014 | Tree pane, Right (step 2): the selection moves to the first child. | F/Keys_Tree | G2 | pass |
| ACC-015 | Tree pane, Down (step 3): the selection moves to the next node. | F/Keys_Tree | G2 | pass |
| ACC-016 | Tree pane, Up (step 4): the selection moves to the previous node. | F/Keys_Tree | G2 | pass |
| ACC-017 | Tree pane, Home (step 5): the selection moves to the root, Local Computer Policy. | F/Keys_Tree | G2 | pass (Gpedit_Keys_TreeWalk) |
| ACC-018 | Tree pane, End (step 6): the selection moves to the last visible node. | F/Keys_Tree | G2 | pass (Gpedit_Keys_TreeWalk) |
| ACC-019 | Tree pane, PageDown (step 7): from the last visible node the selection does not change. | F/Keys_Tree | G2 | pass (Gpedit_Keys_TreeWalk) |
| ACC-020 | Tree pane, PageUp (step 8): the selection moves up by a page. | F/Keys_Tree | G2 | declared-difference |
| ACC-021 | Tree pane, Plus (step 9): no selection change; opens no window and no menu. | F/Keys_Tree | G2 | pass (Gpedit_Keys_TreeExpand) |
| ACC-022 | Tree pane, Minus (step 10): no selection change; opens no window and no menu. | F/Keys_Tree | G2 | pass (Gpedit_Keys_TreeExpand) |
| ACC-023 | Tree pane, Asterisk (step 11): no selection change; opens no window and no menu. | F/Keys_Tree | G2 | pass (Gpedit_Keys_TreeExpand) |
| ACC-024 | Tree pane, Backspace (step 12): the selection moves to the parent node. | F/Keys_Tree | G2 | pass |
| ACC-025 | Tree pane, Tab (step 13): focus moves out of the tree to the result side; the selection stays. | F/Keys_Tree | G2 | pass |
| ACC-026 | Tree pane, Shift+Tab (step 14): focus returns to the tree; the selection stays. | F/Keys_Tree | G2 | pass |
| ACC-027 | Tree pane, F5 (step 15): opens no window and no menu; selection stays. | F/Keys_Tree | G2 | pass |
| ACC-028 | Tree pane, F6 (step 16): focus moves to the result side. | F/Keys_Tree | G2 | pass |
| ACC-029 | Tree pane, Esc (step 17): no effect on selection or focus; no window. | F/Keys_Tree | G2 | pass (Gpedit_Keys_TreeExpand) |
| ACC-030 | Tree pane, Alt+Left (step 18): no change to selection or focus in this capture (history keys; the capture's focus is on the result side). | F/Keys_Tree | G2 | gap |
| ACC-031 | Tree pane, Alt+Right (step 19): no change in this capture. | F/Keys_Tree | G2 | gap |
| ACC-032 | Tree pane, Alt+Up (step 20): no change in this capture. | F/Keys_Tree | G2 | pass |
| ACC-033 | Tree pane, Apps (step 21): no popup menu opens in this capture (focus is on the result side). | F/Keys_Tree | G2 | pass |
| ACC-034 | Result list, Down (F/Keys_List step 1): the focus stays on the last row (the start row is the last). | F/Keys_List | G2 | pass (Gpedit_Keys_ListWalk) |
| ACC-035 | Result list, Up (step 2): the focus moves to the previous row. | F/Keys_List | G2 | pass (Gpedit_Keys_ListWalk) |
| ACC-036 | Result list, Home (step 3): the focus moves to the first row. | F/Keys_List | G2 | pass (Gpedit_Keys_ListWalk) |
| ACC-037 | Result list, End (step 4): the focus moves to the last row. | F/Keys_List | G2 | pass (Gpedit_Keys_ListWalk) |
| ACC-038 | Result list, PageDown (step 5): the focus stays on the last row. | F/Keys_List | G2 | pass (Gpedit_Keys_ListWalk) |
| ACC-039 | Result list, PageUp (step 6): the focus moves to the first row. | F/Keys_List | G2 | pass (Gpedit_Keys_ListWalk) |
| ACC-040 | Result list, Space (step 7): no window, no menu; focus stays. | F/Keys_List | G2 | pass (Gpedit_Keys_ListInert) |
| ACC-041 | Result list, Right (step 8): no window, no menu; focus stays. | F/Keys_List | G2 | pass (Gpedit_Keys_ListInert) |
| ACC-042 | Result list, Left (step 9): no window, no menu; focus stays. | F/Keys_List | G2 | pass (Gpedit_Keys_ListInert) |
| ACC-043 | Result list, Ctrl+A (step 10): no window, no menu; focus stays on the same row. | F/Keys_List | G2 | pass (Gpedit_Keys_ListInert) |
| ACC-044 | Result list, F5 (step 11): no window, no menu; focus stays. | F/Keys_List | G2 | pass |
| ACC-045 | Result list, Tab (step 12): the focus leaves the list for the next pane. | F/Keys_List | G2 | pass |
| ACC-046 | Result list, Shift+Tab (step 13): no popup, no window in this capture. | F/Keys_List | G2 | pass |
| ACC-047 | Result list, Apps (step 14): no popup in this capture; the context menu itself is captured by F/Menus_*. | F/Keys_List, F/Menus_* | G2 | pass |
| ACC-048 | Result list, Enter (step 15): opens the policy setting dialog for the focused policy row. | F/Keys_List | G2, G3a | pass |
| ACC-049 | Result list on All Settings: Down, Down, Up, End, Home, PageDown. Results depend on the machine's own templates and are not tracked. | F/Keys_ListAllSettings | A1 | pass |
| ACC-050 | Keys planned from the plan but not captured as separate steps: Alt+Enter (properties or edit), Delete, F1 (help). The audit decides which classic honors. | none; plan G2 | G2, A1 | pass |

### 2.6 MSG: strings

| ID | Feature | Oracle source | Package | Status |
| --- | --- | --- | --- | --- |
| MSG-001 | gpedit.dll string ids 1 to 15 (15 strings): snap-in and editor titles. | gpedit.dll RT_STRING block 1; parity check: equal 1, differs 0, template 0, absent 14 | A1 | declared-difference |
| MSG-002 | gpedit.dll string ids 16 to 30 (15 strings): RSoP archive and node text. | gpedit.dll RT_STRING block 2; parity check: equal 3, differs 0, template 0, absent 12 | A1 | declared-difference |
| MSG-003 | gpedit.dll string ids 33 to 47 (15 strings): tree node names and node descriptions. | gpedit.dll RT_STRING block 3; parity check: equal 3, differs 0, template 0, absent 12 | A1 | declared-difference |
| MSG-004 | gpedit.dll string ids 48 to 63 (16 strings): RSoP status values. | gpedit.dll RT_STRING block 4; parity check: equal 0, differs 0, template 0, absent 16 | A1 | declared-difference |
| MSG-005 | gpedit.dll string ids 64 to 79 (16 strings): RSoP titles and mode names. | gpedit.dll RT_STRING block 5; parity check: equal 1, differs 0, template 0, absent 15 | A1 | declared-difference |
| MSG-006 | gpedit.dll string ids 80 to 95 (16 strings): GPO link and filter text. | gpedit.dll RT_STRING block 6; parity check: equal 0, differs 0, template 0, absent 16 | A1 | declared-difference |
| MSG-007 | gpedit.dll string ids 96 to 110 (15 strings): RSoP wizard text. | gpedit.dll RT_STRING block 7; parity check: equal 0, differs 0, template 0, absent 15 | A1 | declared-difference |
| MSG-008 | gpedit.dll string ids 115 to 125 (11 strings): GPO save and query errors. | gpedit.dll RT_STRING block 8; parity check: equal 0, differs 0, template 0, absent 11 | A1 | declared-difference |
| MSG-009 | gpedit.dll string ids 130 to 143 (12 strings): domain controller errors. | gpedit.dll RT_STRING block 9; parity check: equal 0, differs 0, template 0, absent 12 | A1 | declared-difference |
| MSG-010 | gpedit.dll string ids 144 to 158 (15 strings): directory query errors. | gpedit.dll RT_STRING block 10; parity check: equal 0, differs 0, template 0, absent 15 | A1 | declared-difference |
| MSG-011 | gpedit.dll string ids 160 to 175 (14 strings): column names and domain labels. | gpedit.dll RT_STRING block 11; parity check: equal 2, differs 0, template 0, absent 12 | A1 | declared-difference |
| MSG-012 | gpedit.dll string ids 176 to 191 (9 strings): yes, no and unknown values. | gpedit.dll RT_STRING block 12; parity check: equal 1, differs 0, template 0, absent 8 | A1 | declared-difference |
| MSG-013 | gpedit.dll string ids 192 to 200 (9 strings): RSoP value text. | gpedit.dll RT_STRING block 13; parity check: equal 2, differs 0, template 0, absent 7 | A1 | declared-difference |
| MSG-014 | gpedit.dll string ids 225 to 239 (15 strings): component status columns. | gpedit.dll RT_STRING block 15; parity check: equal 1, differs 0, template 0, absent 14 | A1 | declared-difference |
| MSG-015 | gpedit.dll string ids 240 to 255 (8 strings): file dialog filters and save error templates. | gpedit.dll RT_STRING block 16; parity check: equal 5, differs 0, template 0, absent 3 | A1 | declared-difference |
| MSG-016 | gpedit.dll string ids 256 to 271 (5 strings): RSoP summary text. | gpedit.dll RT_STRING block 17; parity check: equal 1, differs 0, template 0, absent 4 | A1 | declared-difference |
| MSG-017 | gpedit.dll string ids 272 to 287 (14 strings): WMI filter text. | gpedit.dll RT_STRING block 18; parity check: equal 0, differs 0, template 0, absent 14 | A1 | declared-difference |
| MSG-018 | gpedit.dll string ids 289 to 303 (15 strings): WMI filter list text. | gpedit.dll RT_STRING block 19; parity check: equal 0, differs 0, template 0, absent 15 | A1 | declared-difference |
| MSG-019 | gpedit.dll string ids 304 to 319 (16 strings): RSoP failure text. | gpedit.dll RT_STRING block 20; parity check: equal 0, differs 0, template 0, absent 16 | A1 | declared-difference |
| MSG-020 | gpedit.dll string ids 320 to 330 (6 strings): RSoP wizard help text. | gpedit.dll RT_STRING block 21; parity check: equal 0, differs 0, template 0, absent 6 | A1 | declared-difference |
| MSG-021 | gpedit.dll string ids 337 to 340 (4 strings): editor window titles. | gpedit.dll RT_STRING block 22; parity check: equal 2, differs 0, template 0, absent 2 | A1 | declared-difference |
| MSG-022 | admtmpl.dll string ids 4 to 15 (8 strings): list column names and state names. | admtmpl.dll RT_STRING block 1; parity check: equal 6, differs 0, template 0, absent 2 | A1 | declared-difference |
| MSG-023 | admtmpl.dll string ids 16 to 31 (12 strings): template node text and browse text. | admtmpl.dll RT_STRING block 2; parity check: equal 3, differs 0, template 0, absent 9 | A1 | declared-difference |
| MSG-024 | admtmpl.dll string ids 32 to 47 (15 strings): template dialog messages. | admtmpl.dll RT_STRING block 3; parity check: equal 0, differs 0, template 0, absent 15 | A1 | declared-difference |
| MSG-025 | admtmpl.dll string ids 48 to 63 (16 strings): classic template (.adm) parse errors. | admtmpl.dll RT_STRING block 4; parity check: equal 0, differs 0, template 0, absent 16 | A1 | declared-difference |
| MSG-026 | admtmpl.dll string ids 64 to 73 (7 strings): classic template parse errors (help and client extension). | admtmpl.dll RT_STRING block 5; parity check: equal 0, differs 0, template 0, absent 7 | A1 | declared-difference |
| MSG-027 | admtmpl.dll string ids 90 to 93 (4 strings): error follow-up text. | admtmpl.dll RT_STRING block 6; parity check: equal 0, differs 0, template 0, absent 4 | A1 | declared-difference |
| MSG-028 | admtmpl.dll string ids 100 to 105 (6 strings): input validation messages. | admtmpl.dll RT_STRING block 7; parity check: equal 0, differs 0, template 0, absent 6 | A1 | declared-difference |
| MSG-029 | admtmpl.dll string ids 236 to 237 (2 strings): template administration text. | admtmpl.dll RT_STRING block 15; parity check: equal 0, differs 0, template 0, absent 2 | A1 | declared-difference |
| MSG-030 | admtmpl.dll string ids 240 to 250 (8 strings): registry value display text. | admtmpl.dll RT_STRING block 16; parity check: equal 0, differs 0, template 0, absent 8 | A1 | declared-difference |
| MSG-031 | admtmpl.dll string ids 300 to 303 (4 strings): template file error text. | admtmpl.dll RT_STRING block 19; parity check: equal 0, differs 0, template 1, absent 3 | G6 | pass |
| MSG-032 | admtmpl.dll string ids 304 to 319 (13 strings): template element error text. | admtmpl.dll RT_STRING block 20; parity check: equal 0, differs 0, template 3, absent 10 | G6 | pass |
| MSG-033 | admtmpl.dll string ids 320 to 330 (11 strings): template resource error text. | admtmpl.dll RT_STRING block 21; parity check: equal 0, differs 0, template 1, absent 10 | G6 | pass |
| MSG-034 | admtmpl.dll string ids 2000 to 2003 (4 strings): comment column values and the path column name. | admtmpl.dll RT_STRING block 126; parity check: equal 4, differs 0, template 0, absent 0 | G1 | pass |
| MSG-035 | admtmpl.dll string ids 3000 to 3005 (6 strings): filter combo values and keyword match modes. | admtmpl.dll RT_STRING block 188; parity check: equal 2, differs 0, template 0, absent 4 | G5 | pass |
| MSG-036 | admtmpl.dll string ids 3100 to 3103 (4 strings): filter menu text. | admtmpl.dll RT_STRING block 194; parity check: equal 0, differs 0, template 0, absent 4 | G5 | pass |
| MSG-037 | admtmpl.dll string ids 3104 to 3119 (6 strings): re-apply filter text and filter status text. | admtmpl.dll RT_STRING block 195; parity check: equal 1, differs 0, template 0, absent 5 | G5 | declared-difference |
| MSG-038 | admtmpl.dll string ids 3120 to 3127 (8 strings): filter requirement text and messages. | admtmpl.dll RT_STRING block 196; parity check: equal 0, differs 0, template 3, absent 5 | G5 | declared-difference |
| MSG-039 | mmc.exe string ids 128 to 134 (7 strings): console titles. | mmc.exe RT_STRING block 9; parity check: equal 1, differs 0, template 0, absent 6 | A1 | declared-difference |
| MSG-040 | mmc.exe string ids 208 to 210 (3 strings): window button names. | mmc.exe RT_STRING block 14; parity check: equal 0, differs 0, template 0, absent 3 | A1 | declared-difference |
| MSG-041 | mmcndmgr.dll string ids 7001 to 7007 (7 strings): progress text. | mmcndmgr.dll RT_STRING block 438; parity check: equal 0, differs 0, template 3, absent 4 | A1 | declared-difference |
| MSG-042 | mmcndmgr.dll string ids 7008 to 7012 (5 strings): completion text. | mmcndmgr.dll RT_STRING block 439; parity check: equal 0, differs 0, template 0, absent 5 | A1 | declared-difference |
| MSG-043 | mmcndmgr.dll string ids 30008 to 30267 (242 strings in 17 blocks): descriptions of the taskpad icons. | mmcndmgr.dll RT_STRING blocks 30008, 30016, 30032, 30048, 30064, 30080, 30096, 30112, 30132, 30146, 30166, 30176, 30192, 30208, 30224, 30240, 30256; parity check: equal 0, differs 0, template 0, absent 242 | A1 | declared-difference |
| MSG-044 | mmcbase.dll string ids 1 to 5 (5 strings): object names. | mmcbase.dll RT_STRING block 1; parity check: equal 0, differs 0, template 0, absent 5 | A1 | declared-difference |
| MSG-045 | mmcbase.dll string ids 101 to 108 (8 strings): message box titles. | mmcbase.dll RT_STRING block 7; parity check: equal 0, differs 0, template 2, absent 6 | A1 | declared-difference |
| MSG-046 | mmcbase.dll string ids 128 to 130 (3 strings): console titles. | mmcbase.dll RT_STRING block 9; parity check: equal 0, differs 0, template 0, absent 3 | A1 | declared-difference |
| MSG-047 | mmcbase.dll string ids 181 to 187 (5 strings): default names of new items. | mmcbase.dll RT_STRING block 12; parity check: equal 0, differs 0, template 0, absent 5 | A1 | declared-difference |
| MSG-048 | mmcbase.dll string ids 213 to 214 (2 strings): taskpad category names. | mmcbase.dll RT_STRING block 14; parity check: equal 0, differs 0, template 0, absent 2 | A1 | declared-difference |
| MSG-049 | mmcbase.dll string ids 1008 to 1008 (1 strings): icon file filter. | mmcbase.dll RT_STRING block 64; parity check: equal 0, differs 0, template 0, absent 1 | A1 | declared-difference |
| MSG-050 | mmcbase.dll string ids 12000 to 12015 (16 strings): snap-in error text and menu help text. | mmcbase.dll RT_STRING block 751; parity check: equal 1, differs 0, template 0, absent 15 | A1 | declared-difference |
| MSG-051 | mmcbase.dll string ids 12016 to 12021 (6 strings): error text. | mmcbase.dll RT_STRING block 752; parity check: equal 0, differs 0, template 1, absent 5 | A1 | declared-difference |
| MSG-052 | mmcbase.dll string ids 12804 to 12813 (3 strings): Help menu item help text. | mmcbase.dll RT_STRING block 801; parity check: equal 0, differs 0, template 0, absent 3 | A1 | declared-difference |
| MSG-053 | mmcbase.dll string ids 13201 to 13201 (1 strings): snap-in management help text. | mmcbase.dll RT_STRING block 826; parity check: equal 0, differs 0, template 0, absent 1 | A1 | declared-difference |
| MSG-054 | mmcbase.dll string ids 13220 to 13231 (2 strings): Options and Customize View help text. | mmcbase.dll RT_STRING block 827; parity check: equal 0, differs 0, template 0, absent 2 | A1 | declared-difference |
| MSG-055 | mmcbase.dll string ids 13244 to 13245 (2 strings): web site help text. | mmcbase.dll RT_STRING block 828; parity check: equal 0, differs 0, template 0, absent 2 | A1 | declared-difference |
| MSG-056 | mmcbase.dll string ids 13300 to 13311 (12 strings): console file messages. | mmcbase.dll RT_STRING block 832; parity check: equal 0, differs 0, template 0, absent 12 | A1 | declared-difference |
| MSG-057 | mmcbase.dll string ids 13312 to 13327 (15 strings): view creation messages. | mmcbase.dll RT_STRING block 833; parity check: equal 0, differs 0, template 0, absent 15 | A1 | declared-difference |
| MSG-058 | mmcbase.dll string ids 13328 to 13343 (16 strings): Window menu help text. | mmcbase.dll RT_STRING block 834; parity check: equal 0, differs 0, template 0, absent 16 | A1 | declared-difference |
| MSG-059 | mmcbase.dll string ids 13344 to 13359 (16 strings): console file write errors. | mmcbase.dll RT_STRING block 835; parity check: equal 0, differs 0, template 0, absent 16 | A1 | declared-difference |
| MSG-060 | mmcbase.dll string ids 13360 to 13375 (4 strings): console file open errors. | mmcbase.dll RT_STRING block 836; parity check: equal 0, differs 0, template 0, absent 4 | A1 | declared-difference |
| MSG-061 | mmcbase.dll string ids 13376 to 13391 (16 strings): snap-in management messages. | mmcbase.dll RT_STRING block 837; parity check: equal 0, differs 0, template 0, absent 16 | A1 | declared-difference |
| MSG-062 | mmcbase.dll string ids 13392 to 13406 (15 strings): toolbar button names. | mmcbase.dll RT_STRING block 838; parity check: equal 0, differs 0, template 0, absent 15 | A1 | declared-difference |
| MSG-063 | mmcbase.dll string ids 13408 to 13423 (16 strings): list and column messages. | mmcbase.dll RT_STRING block 839; parity check: equal 1, differs 0, template 0, absent 15 | A1 | declared-difference |
| MSG-064 | mmcbase.dll string ids 13424 to 13439 (12 strings): size unit text. | mmcbase.dll RT_STRING block 840; parity check: equal 1, differs 0, template 0, absent 11 | A1 | declared-difference |
| MSG-065 | mmcbase.dll string ids 13440 to 13455 (16 strings): menu item names. | mmcbase.dll RT_STRING block 841; parity check: equal 2, differs 0, template 0, absent 14 | A1 | declared-difference |
| MSG-066 | mmcbase.dll string ids 13456 to 13460 (5 strings): Edit menu help text. | mmcbase.dll RT_STRING block 842; parity check: equal 0, differs 0, template 0, absent 5 | A1 | declared-difference |
| MSG-067 | mmcbase.dll string ids 14000 to 14015 (16 strings): View and Properties menu help text. | mmcbase.dll RT_STRING block 876; parity check: equal 0, differs 0, template 0, absent 16 | A1 | declared-difference |
| MSG-068 | mmcbase.dll string ids 14016 to 14031 (15 strings): extension and vendor text. | mmcbase.dll RT_STRING block 877; parity check: equal 1, differs 0, template 0, absent 14 | A1 | declared-difference |
| MSG-069 | mmcbase.dll string ids 14032 to 14047 (16 strings): snap-in list text. | mmcbase.dll RT_STRING block 878; parity check: equal 1, differs 0, template 0, absent 15 | A1 | declared-difference |
| MSG-070 | mmcbase.dll string ids 14048 to 14063 (16 strings): Open and Edit menu help text. | mmcbase.dll RT_STRING block 879; parity check: equal 0, differs 0, template 0, absent 16 | A1 | declared-difference |
| MSG-071 | mmcbase.dll string ids 14064 to 14079 (12 strings): View menu help text. | mmcbase.dll RT_STRING block 880; parity check: equal 0, differs 0, template 0, absent 12 | A1 | declared-difference |
| MSG-072 | mmcbase.dll string ids 14080 to 14095 (16 strings): web link and ActiveX text. | mmcbase.dll RT_STRING block 881; parity check: equal 0, differs 0, template 0, absent 16 | A1 | declared-difference |
| MSG-073 | mmcbase.dll string ids 14096 to 14111 (16 strings): taskpad menu text. | mmcbase.dll RT_STRING block 882; parity check: equal 0, differs 0, template 0, absent 16 | A1 | declared-difference |
| MSG-074 | mmcbase.dll string ids 14112 to 14127 (16 strings): window state text. | mmcbase.dll RT_STRING block 883; parity check: equal 1, differs 0, template 0, absent 15 | A1 | declared-difference |
| MSG-075 | mmcbase.dll string ids 14128 to 14143 (16 strings): snap-in detail text. | mmcbase.dll RT_STRING block 884; parity check: equal 0, differs 0, template 2, absent 14 | A1 | declared-difference |
| MSG-076 | mmcbase.dll string ids 14144 to 14159 (16 strings): taskpad command text. | mmcbase.dll RT_STRING block 885; parity check: equal 0, differs 0, template 0, absent 16 | A1 | declared-difference |
| MSG-077 | mmcbase.dll string ids 14160 to 14175 (11 strings): About text. | mmcbase.dll RT_STRING block 886; parity check: equal 0, differs 0, template 0, absent 11 | A1 | declared-difference |
| MSG-078 | mmcbase.dll string ids 14176 to 14180 (5 strings): extension text. | mmcbase.dll RT_STRING block 887; parity check: equal 0, differs 0, template 0, absent 5 | A1 | declared-difference |
| MSG-079 | mmcbase.dll string ids 57344 to 57345 (2 strings): console title. | mmcbase.dll RT_STRING block 3585; parity check: equal 1, differs 0, template 0, absent 1 | A1 | declared-difference |
| MSG-080 | mmcbase.dll string ids 57600 to 57609 (9 strings): File menu help text. | mmcbase.dll RT_STRING block 3601; parity check: equal 0, differs 0, template 0, absent 9 | A1 | declared-difference |
| MSG-081 | mmcbase.dll string ids 57616 to 57631 (16 strings): recent file help text. | mmcbase.dll RT_STRING block 3602; parity check: equal 0, differs 0, template 0, absent 16 | A1 | declared-difference |
| MSG-082 | mmcbase.dll string ids 57632 to 57644 (8 strings): Edit command help text. | mmcbase.dll RT_STRING block 3603; parity check: equal 0, differs 0, template 0, absent 8 | A1 | declared-difference |
| MSG-083 | mmcbase.dll string ids 57648 to 57654 (7 strings): Window menu command help text. | mmcbase.dll RT_STRING block 3604; parity check: equal 0, differs 0, template 0, absent 7 | A1 | declared-difference |
| MSG-084 | mmcbase.dll string ids 57664 to 57665 (2 strings): About and Exit help text. | mmcbase.dll RT_STRING block 3605; parity check: equal 0, differs 0, template 0, absent 2 | A1 | declared-difference |
| MSG-085 | mmcbase.dll string ids 57680 to 57681 (2 strings): window switch help text. | mmcbase.dll RT_STRING block 3606; parity check: equal 0, differs 0, template 0, absent 2 | A1 | declared-difference |
| MSG-086 | mmcbase.dll string ids 59136 to 59141 (6 strings): key name abbreviations. | mmcbase.dll RT_STRING block 3697; parity check: equal 0, differs 0, template 0, absent 6 | A1 | declared-difference |
| MSG-087 | mmcbase.dll string ids 59392 to 59393 (2 strings): toolbar and status text. | mmcbase.dll RT_STRING block 3713; parity check: equal 0, differs 0, template 0, absent 2 | A1 | declared-difference |
| MSG-088 | mmcbase.dll string ids 61184 to 61190 (7 strings): system menu help text. | mmcbase.dll RT_STRING block 3825; parity check: equal 0, differs 0, template 0, absent 7 | A1 | declared-difference |
| MSG-089 | mmcbase.dll string ids 61202 to 61215 (3 strings): system menu help text, restore and task list. | mmcbase.dll RT_STRING block 3826; parity check: equal 0, differs 0, template 0, absent 3 | A1 | declared-difference |
| MSG-090 | mmcbase.dll string ids 61445 to 61445 (1 strings): print preview help text. | mmcbase.dll RT_STRING block 3841; parity check: equal 0, differs 0, template 0, absent 1 | A1 | declared-difference |
| MSG-091 | Managed resource file Editor.resources (322 entries): setting dialog form texts, sizes and anchors. | gpedit-forms.json Microsoft.GroupPolicy.AdmTmplEditor.Editor.resources | G3a, G3b | declared-difference |
| MSG-092 | Managed resource file ListBoxDialog.resources (56 entries): setting dialog form texts, sizes and anchors. | gpedit-forms.json Microsoft.GroupPolicy.AdmTmplEditor.ListBoxDialog.resources | G3a, G3b | pass |
| MSG-093 | Managed resource file resources (22 entries): setting dialog form texts, sizes and anchors. | gpedit-forms.json Microsoft.GroupPolicy.AdmTmplEditor.resources | G3a, G3b | pass |

### 2.7 VIEW: layout and density

| ID | Feature | Oracle source | Package | Status |
| --- | --- | --- | --- | --- |
| VIEW-001 | Window at capture: 1536 x 1074 px, per-monitor DPI aware, four stacked parts: menu band, standard toolbar band, panes, status bar. | F/Toolbar_States, F/AllSettings_Computer | U3, G2 | pass |
| VIEW-002 | Menu band rectangle: x 20, y 62, 1500 x 44 px. | F/Toolbar_States | U3 | pass |
| VIEW-003 | Standard toolbar band rectangle: x 20, y 110, 1500 x 44 px; buttons 23 x 22 px. | F/Toolbar_States | U3 | pass |
| VIEW-004 | Console tree pane rectangle: x 16, y 166, 312 x 846 px. The saved scope pane width in the console file is 156. | F/AllSettings_Computer; gpedit-msc.json | U3, G1 | pass |
| VIEW-005 | Result list rectangle in the Extended view: x 762, y 220, 754 x 750 px. | F/AllSettings_Computer | U3, G1 | pass |
| VIEW-006 | Status bar rectangle: x 16, y 1012, 1504 x 46 px. | F/AllSettings_Computer | U3 | pass |
| VIEW-007 | Column header band: 48 px high, first header at the list's left edge. | F/AllSettings_Computer | U3, G1 | pass |
| VIEW-008 | Policy nodes show three columns in this order: setting, state, comment. | F/Nodes_Computer, F/Nodes_User, F/Tabs_Extended_Standard | G1 | pass |
| VIEW-009 | All Settings has four columns: setting, state, comment and path; the last two are not visible in the capture window. | F/AllSettings_Computer, F/AllSettings_User | G1, G5 | pass |
| VIEW-010 | State column text: three names, from admtmpl.dll strings 8, 9 and 10 (Enabled, Disabled, Not configured as the list spells them). Folder rows show no state. | F/Nodes_Computer, F/Nodes_User | G1 | pass |
| VIEW-011 | Comment column text: a yes or no word, from admtmpl.dll strings 2001 and 2002. Folder rows show nothing. | F/Nodes_Computer, F/Nodes_User | G1 | pass |
| VIEW-012 | Rows of a node: folder rows first (child categories), then the policies of the side. Mixed nodes (a category with both) show both kinds. | F/Nodes_Computer, F/Nodes_User | G1 | pass |
| VIEW-013 | Default row order, no sort mark: folder rows first, by name; policy rows in the order of their policy ids (ordinal compare), which is not the display-name order. | F/Nodes_Computer, F/Sort_Columns | G1, G5 | pass |
| VIEW-014 | Header click sorting: each of the three columns clicked three times; the order after each click is recorded. Another node keeps its own sort. | F/Sort_Columns | G5 | pass |
| VIEW-015 | Text order of the first column for digits (1, 10, 2), upper and lower case, a leading symbol and non-ASCII names. | F/Sort_Columns; B/Pol_Sort_* | G5 | pass |
| VIEW-016 | Tab strip of the result pane with two tabs, Extended and Standard. Extended is active at start. Switching tabs keeps the rows. | F/Tabs_Extended_Standard | U2c, G1 | pass |
| VIEW-017 | Extended view: a description area between the console tree and the list (x 328 to 762 at capture) that shows the selected item's description and requirements. Classic draws it as an HTML page (DIFF-002). Pictures are untracked. | F/Tabs_Extended_Standard (screenshots), F/AllSettings_Computer | U2c | declared-difference |
| VIEW-018 | Standard view: the list alone, no description pane. | F/Tabs_Extended_Standard | U2c | pass |
| VIEW-019 | Status bar: a count of the policy rows of a node (folder rows are not counted); empty on navigation nodes and on nodes with no policy. The text is not tracked, the count is. | F/Nodes_Computer, F/Nodes_User, F/Placeholders_* | G1 | pass |
| VIEW-020 | Status bar with a filter on: the filtered flag and the narrowed count after each filter setting. | F/Filter_Options | G5 | pass |
| VIEW-021 | Placeholder node lists: Software Settings has an empty list and one header. Windows Settings lists its placeholder children (5 under Computer, 4 under User) in a single column. | F/Placeholders_Computer, F/Placeholders_User | G1 | pass |
| VIEW-022 | Computer Configuration and User Configuration nodes list three rows: Software Settings, Windows Settings, Administrative Templates, in one column. | F/Placeholders_Computer, F/Placeholders_User | G1 | pass |
| VIEW-023 | Tree node icons, by image index: root 25, Computer Configuration 27, User Configuration 28, folder 23 (selected 24), Administrative Templates 60 and 61 under Computer and 70 and 71 under User, All Settings 68. | F/Tree_Computer, F/Tree_User, F/AllSettings_Computer | G1, U2a | declared-difference |
| VIEW-024 | Initial tree expansion: the root and both configuration nodes are expanded, other nodes collapsed. Administrative Templates expands to its categories and All Settings. | F/Tree_Computer, F/Tree_User | G1 | pass |
| VIEW-025 | A category name with a literal ampersand (Tools & Utilities in the fixture) shows the ampersand as typed, in the tree and in the list. | F/Tree_Computer, F/Nodes_Computer | G1 | pass |
| VIEW-026 | Setting dialog size: 1400 x 1286 px at 192 DPI, the same for every option kind and both states. | F/Dialog_Layouts | G3a | pass |
| VIEW-027 | Setting dialog, plain flag policy (no options): 29 controls, 10 tab stops; 28 enabled when Not Configured, 29 when Enabled. | F/Dialog_Layouts (Pol_Class_Machine) | G3a | declared-difference |
| VIEW-028 | Setting dialog, labels with ampersands: 41 controls, 15 tab stops; 33 enabled when Not Configured, 40 when Enabled. | F/Dialog_Layouts (Pol_Amp_LabelSingle) | G3b | declared-difference |
| VIEW-029 | Setting dialog, number box: 34 controls, 12 tab stops; 29 enabled when Not Configured, 33 when Enabled. | F/Dialog_Layouts (Pol_Decimal_MinMax) | G3b | declared-difference |
| VIEW-030 | Setting dialog, long number box: 34 controls, 12 tab stops; 29 and 33 enabled. | F/Dialog_Layouts (Pol_LongDecimal_Basic) | G3b | declared-difference |
| VIEW-031 | Setting dialog, text box: 32 controls, 11 tab stops; 30 and 32 enabled. | F/Dialog_Layouts (Pol_Text_Plain) | G3b | declared-difference |
| VIEW-032 | Setting dialog, combo box: 33 controls, 11 tab stops; 30 and 33 enabled. | F/Dialog_Layouts (Pol_Text_ComboBoxSuggestions) | G3b | declared-difference |
| VIEW-033 | Setting dialog, multi-line text: 33 controls, 11 tab stops; 30 and 32 enabled. | F/Dialog_Layouts (Pol_MultiText_Basic) | G3b | declared-difference |
| VIEW-034 | Setting dialog, drop-down list: 32 controls, 11 tab stops; 29 and 31 enabled. | F/Dialog_Layouts (Pol_Enum_DecimalItems) | G3b | declared-difference |
| VIEW-035 | Setting dialog, list: 32 controls, 11 tab stops; 30 and 32 enabled. | F/Dialog_Layouts (Pol_List_Plain) | G3b | declared-difference |
| VIEW-036 | Setting dialog, two check boxes: 31 controls, 12 tab stops; 27 and 30 enabled. | F/Dialog_Layouts (Pol_Bool_TwoChecksReverseOrder) | G3b | declared-difference |
| VIEW-037 | Setting dialog, long multi-paragraph help text: 29 controls, 10 tab stops; 27 and 28 enabled. The help pane scrolls. | F/Dialog_Layouts (Pol_Explain_MultiParagraph) | G3a | declared-difference |
| VIEW-038 | Template load error: one message box at start, then the console opens; the tree shows Administrative Templates without the bad template's category. | H/MalformedXml, H/MissingAdml, H/DuplicatePolicy, H/DtdEntity, H/UndefinedString, H/ListItemNoKey, H/BooleanNoValueName | G6 | declared-difference |
| VIEW-039 | A template whose using prefix is undefined loads without an error box: its category and policy show and the policy opens. | H/UndefinedUsing | G6 | pass |
| VIEW-040 | Three fixtures that might have loaded (lower-case class, upper-case and mixed-case boolean attributes) each raised the error box and were left out of the tree. | H/ClassLowerCase, H/BoolAttributeUpper, H/BoolAttributeMixed | G6 | declared-difference |
| VIEW-041 | The good templates still show, and still open, next to a bad one. | H/* | G6 | pass |
| VIEW-042 | Filter on: a node narrows to the matching policies; the narrowed rows are recorded for each filter setting; the filter stays on in another node until cleared. | F/Filter_Options | G5 | pass |
| VIEW-043 | Menus and toolbar enabled states are the same on the computer and the user side. | F/Menus_Computer, F/Menus_User, F/Toolbar_States | G2 | pass |

### 2.8 TREE: console tree

| ID | Feature | Oracle source | Package | Status |
| --- | --- | --- | --- | --- |
| TREE-001 | Root node Local Computer Policy; computer icon (image 25); selected at start; expanded. | F/Tree_Computer, F/Tree_User | G1 | pass |
| TREE-002 | Computer Configuration node, first child of the root (image 27, expanded at start). | F/Tree_Computer | G1 | pass |
| TREE-003 | User Configuration node, second child of the root (image 28, expanded at start). | F/Tree_User | G1 | pass |
| TREE-004 | Each configuration node has three children in this order: Software Settings, Windows Settings, Administrative Templates. | F/Tree_Computer, F/Tree_User | G1 | pass |
| TREE-005 | Software Settings has no children on either side (folder image 23, selected 24). | F/Tree_Computer, F/Tree_User | G1 | pass |
| TREE-006 | Windows Settings under Computer Configuration has five placeholder children in order: Name Resolution Policy, scripts (startup), Deployed Printers, Security Settings, Policy-based QoS. | F/Tree_Computer | G1 | pass |
| TREE-007 | Windows Settings under User Configuration has four placeholder children in order: scripts (logon), Security Settings, Policy-based QoS, Deployed Printers. This order differs from the Computer side. | F/Tree_User | G1 | pass |
| TREE-008 | Placeholder nodes have no children. | F/Tree_Computer, F/Tree_User, F/Placeholders_* | G1 | pass |
| TREE-009 | Administrative Templates children: the categories of the loaded templates, then All Settings last (All Settings is a leaf, image 68). | F/Tree_Computer, F/Tree_User | G1 | pass |
| TREE-010 | Administrative Templates uses image 60 (selected 61) under Computer and 70 (selected 71) under User. | F/Tree_Computer, F/Tree_User | G1 | declared-difference |
| TREE-011 | Categories under a parent sort by display name. | F/Tree_Computer, F/Tree_User | G1 | pass |
| TREE-012 | A category shows on a side only when it or a descendant holds a policy that applies to that side. Machine-only and user-only branches show on one side only. | F/Tree_Computer, F/Tree_User | G1 | pass |
| TREE-013 | A category that holds no policy and has no child with one is not shown on either side. | F/Tree_Computer (the empty category) | G1 | pass |
| TREE-014 | A policy of class Both appears under both configuration roots. | F/Tree_Computer, F/Tree_User | G1 | pass |
| TREE-015 | Nested categories to three levels (Machine only, Machine nested, Machine deep on the computer side; User only and User nested on the user side). | F/Tree_Computer, F/Tree_User | G1 | pass |
| TREE-016 | A policy placed directly under the top category shows in that category's list, next to the subcategory folder rows. | F/Tree_User, F/Nodes_User | G1 | pass |
| TREE-017 | Category node icons: folder style, same as placeholder nodes except Administrative Templates and its descendants. | F/Tree_Computer, F/Tree_User | G1, U2a | declared-difference |
| TREE-018 | Selecting a node updates the result list at once; selecting a placeholder node shows an empty or children-only list. | F/Placeholders_Computer, F/Placeholders_User | G1 | pass |
| TREE-019 | Expand and collapse keys on the tree (Right, Left, Plus, Minus, Asterisk, Backspace). | F/Keys_Tree | G2 | pass (Gpedit_Keys_TreeWalk, Gpedit_Keys_TreeExpand) |
| TREE-020 | Removed 2026-11-22 by user decision: an Extra Registry Settings node. Classic shows none; records that match no policy stay invisible (see BEH-014). BWC builds none. | F/Tree_Computer, F/Tree_User; D/Foreign_* | none | removed |

### 2.9 BEH: behavior not in the resources

| ID | Feature | Oracle source | Package | Status |
| --- | --- | --- | --- | --- |
| BEH-001 | The tree has one root, Local Computer Policy, with two children in this order: Computer Configuration and User Configuration. | Plan; classic probe | G1 | pass |
| BEH-002 | Each of the two roots has three children in this order: Software Settings, Windows Settings, Administrative Templates. Administrative Templates is built from the loaded templates. The User Configuration child list of Windows Settings is taken from the first capture run. | Plan; classic probe | G1, E2 | pass |
| BEH-003 | Placeholder nodes are shown like classic and have no children in BWC: Software Settings, and under Windows Settings: Name Resolution Policy, Scripts, Deployed Printers, Security Settings, Policy-based QoS. Selecting one opens a page saying the feature is not available yet. | Plan | G1 | pass |
| BEH-004 | Administrative Templates fully work for both roots: categories form the subtree, policies of the selected category fill the result list, and a policy of class Both appears under both roots. | Plan | G1, E2 | pass |
| BEH-005 | Read-only mode: when the process is not elevated or has no write access to the GPO folder, the editor opens, shows current state, and refuses every save. | Plan; policy-engine contract | G4, E3 | pass |
| BEH-006 | Home notice: on the Home edition the editor opens read-only and shows a notice. Writing on Home is not part of 0.2.0. | Plan | G4 | pass |
| BEH-007 | Template sources: the system folder `C:\Windows\PolicyDefinitions` plus the BWC import store `%ProgramData%\BetterWindowsConfig\PolicyDefinitions`. The system folder wins on a duplicate namespace. | Plan; policy-engine contract | G6, E5 | pass |
| BEH-008 | Template import: the Add/Remove Templates dialog copies chosen files into the import store. Classic `.adm` files are not accepted. | Plan | G6, E5 | declared-difference |
| BEH-009 | A template that fails to load is listed in a load-error message and the remaining templates still load. | Plan | G6, E2 | declared-difference |
| BEH-010 | Filter: the Filter Options dialog, Filter On, and All Settings narrow the result list the way classic does. Filter result rules come from the capture run. | Plan; classic Filter Options dialog | G5, E5 | pass |
| BEH-011 | Each OK or Apply writes comment.cmtx, then Registry.pol, then gpt.ini, then refreshes policy. | Plan; policy-engine contract | G4, E3 | pass |
| BEH-012 | Retired 2026-11-22 by user decision: the Extra Registry Settings node. Classic has no such node, so BWC builds none. Records that match no policy stay invisible and are kept untouched on every save (BEH-014). The original row text said the node lists values found in Registry.pol that no loaded template explains. | Plan; user decision | none | removed |
| BEH-013 | Back, Forward and Up history in the frame follow the selected tree node. | Plan | G2 | pass |
| BEH-014 | Records in Registry.pol that match no loaded policy stay invisible in the tree and the list, and are kept untouched, in place, on every save. Decision of 2026-11-22; no Extra Registry Settings node exists. Settles OQ-09. | D/Foreign_*, D/Instruction_*, B/Pol_Outside_KeyOutsideRoots; user decision | E6c, G4 | pass |
| BEH-015 | The state shown for a policy comes from the Registry.pol of the node's side. A class Both policy is read from the file of the side whose root it is shown under. | D/Both_BothFiles, D/Both_MachineFileOnly, D/Both_UserFileOnly, B/Pol_Class_Both.* | E6c, G1 | pass |
| BEH-016 | The comment column is yes when a comment exists for the policy in the side's comment.cmtx. | C/Comment_*, F/Nodes_* | G1, E6a | pass |
| BEH-017 | The model loads in the background: the window stays responsive, and the children of Administrative Templates appear after the model arrives. | Plan; docs/failure-modes/gpedit-tree-list.md | G1 | pass |
| BEH-018 | Classic opens on the root node every time; the last node is not restored. | docs/failure-modes/gpedit-tree-list.md section 11 | G1 | pass |
| BEH-019 | The setting dialog opens from Enter on a policy row, a double click, the Edit entry of the Action and context menus, and the link in the Extended pane. | F/Keys_List step 15, F/Menus_*; docs/failure-modes/gpedit-tree-list.md section 8 | G2, G3a | pass |
| BEH-020 | One setting dialog is open at a time; closing it returns the focus to the list row of the last policy shown. | docs/failure-modes/gpedit-dialog.md section 12 | G3a | pass |
| BEH-021 | BWC settings kept between runs: window placement, console tree width, column widths, view mode, Extended or Standard tab. | Plan; docs/failure-modes/gpedit-tree-list.md | G1, U3a | pass |
| BEH-022 | Refresh by the F5 accelerator (command 13209) reloads the template set and the policy files and rebuilds the tree and list without losing the selected node. The dumped menus and toolbar of classic have no Refresh entry; plan G2 builds one and the audit decides. | mmc.exe RT_ACCELERATOR 128 (F5); F/Keys_*; F/Menus_* | G2 | declared-difference |
| BEH-023 | The Extended pane shows, for a selected policy row, its title, an edit link, the requirements text and the description; for a selected folder row, its title and description; with no row selected, the selected category or the hint for the navigation node. | F/Tabs_Extended_Standard, F/Nodes_* | U2c, G1 | pass |
| BEH-024 | The status bar counts policy rows only. A node that holds folder rows and no policy shows an empty bar or a count of the policies it does hold. | F/Nodes_Computer, F/Nodes_User | G1 | pass |
| BEH-025 | Sorting is kept per node: another node keeps its own sort after a header click elsewhere. | F/Sort_Columns | G5 | declared-difference |
| BEH-026 | After OK or Apply the list's state and comment cells for that policy show the new values at once. | A/*, G/* | G4, G1 | pass |
| BEH-027 | Policy refresh runs after every save that changed a file, for both sides and for comment-only changes; its timing is the one recorded in group G. | G/* | G4, E3 | gap |
| BEH-028 | A comment edit is written to comment.cmtx and everything another tool put into that file is kept. | C/Comment_*, C/Cmtx_* | E6a, G4 | pass |
| BEH-029 | Records with a policy key outside the usual policy roots are read and written like any other. | B/Pol_Outside_KeyOutsideRoots, G/Outside_KeyOutsideRoots | E6b | pass |

### 2.10 POL: Registry.pol, gpt.ini and comment.cmtx bytes

| ID | Feature | Oracle source | Package | Status |
| --- | --- | --- | --- | --- |
| POL-001 | First save of an Enabled machine policy into an empty GroupPolicy folder: Machine Registry.pol with the policy's records, gpt.ini with version low half 1 and the registry extension pair, and comment.cmtx. | A/Machine_Enabled, A/Machine_EnabledWithComment | E6a | pass |
| POL-002 | First save of an Enabled user policy: User Registry.pol, gpt.ini with the version high half 1 and the user extension pair, and the user comment.cmtx. | A/User_Enabled, A/User_EnabledWithComment | E6a | pass |
| POL-003 | First save of a Disabled policy, machine and user. | A/Machine_Disabled, A/User_Disabled | E6a | pass |
| POL-004 | Not Configured on an empty folder: nothing is written. | A/Machine_NotConfigured | E6a | pass |
| POL-005 | Cancel after choosing Enabled, and Apply followed by Cancel: Cancel writes nothing; the Apply stays written. | A/Machine_Cancel, A/Machine_Apply_Cancel | E6a | pass |
| POL-006 | Apply then OK with no further change: written once. | A/Machine_Apply_OK, C/Apply_Then_OK | E6a | pass |
| POL-007 | Comment only on a Not Configured policy: comment.cmtx, a header-only Registry.pol (8 bytes) and gpt.ini with version 1 are written, and the state stays Not Configured. | A/Machine_CommentOnly, A/User_CommentOnly, A/Both_UnderComputer_CommentOnly, A/Both_UnderUser_CommentOnly | E6a | pass |
| POL-008 | A class Both policy saves into the file of the root it was edited under. | A/Both_UnderComputer_Enabled, A/Both_UnderUser_Enabled | E6a | pass |
| POL-009 | Machine and user saves in one folder, in both orders and with a class Both policy: both files, one gpt.ini, the version halves. | A/Machine_Then_User, A/User_Then_Machine, A/Both_Computer_Then_User | E6a | pass |
| POL-010 | Plain flag policies of class Machine, User and Both and the policy directly under the top category: Enabled writes the default value, Disabled writes a delete instruction for the value, Not Configured removes the record. | B/Pol_Class_Machine, B/Pol_Class_User, B/Pol_Class_Both.Computer, B/Pol_Class_Both.User, B/Pol_Top_DirectUnderRoot | E6b | pass |
| POL-011 | Enabled and disabled values as numbers and as text. | B/Pol_Value_Decimal, B/Pol_Value_String, G/Value_EnabledDisabledValues | E6b | pass |
| POL-012 | A policy with only an enabled value or only a disabled value. | B/Pol_Value_EnabledOnly, B/Pol_Value_DisabledOnly | E6b | pass |
| POL-013 | Policies with an enabled list, a disabled list, or both next to a value name. | B/Pol_Value_EnabledListOnly, B/Pol_Value_DisabledListOnly, B/Pol_Value_BothLists | E6b | pass |
| POL-014 | A policy with no value name that writes only its elements. | B/Pol_Value_NoValueName_ElementsOnly | E6b | pass |
| POL-015 | Nested categories and the position of a policy in the tree do not change the bytes saved. | B/Pol_Nested_MachineTop, B/Pol_Nested_MachineMid, B/Pol_Nested_MachineDeep, B/Pol_Nested_UserDeep | E6b | pass |
| POL-016 | A policy whose key lies outside the usual policy roots. | B/Pol_Outside_KeyOutsideRoots, G/Outside_KeyOutsideRoots | E6b | pass |
| POL-017 | An element with its own registry key. | B/Pol_Element_KeyOverride | E6b | pass |
| POL-018 | A class Both policy that has an element, under both roots. | B/Pol_Both_WithElements.Computer, B/Pol_Both_WithElements.User | E6b | pass |
| POL-019 | Check boxes: true and false values, true and false lists, string values, no false value, two check boxes defined in reverse name order. | B/Pol_Bool_*, C/Booleans_ReverseOrder | E6b | pass |
| POL-020 | Number elements: range, required, soft limit, store as text. | B/Pol_Decimal_MinMax, B/Pol_Decimal_Required, B/Pol_Decimal_Soft, B/Pol_Decimal_StoreAsText | E6b | pass |
| POL-021 | Long number elements, including required, soft and text forms. | B/Pol_LongDecimal_Basic, B/Pol_LongDecimal_RequiredSoftText | E6b | pass |
| POL-022 | Text elements: plain, expandable, maximum length, required, combo box with and without sorting. | B/Pol_Text_* | E6b | pass |
| POL-023 | Multi-line text elements and their limits. | B/Pol_MultiText_Basic, B/Pol_MultiText_Limits | E6b | pass |
| POL-024 | Drop-down list elements: decimal items, string items, items with value lists, no-sort default item, required. | B/Pol_Enum_* | E6b | pass |
| POL-025 | List elements: plain, additive, expandable, explicit values, value prefix, empty prefix, policy value name. | B/Pol_List_* | E6b | pass |
| POL-026 | Two policies that share one list key. | B/Pol_SharedList_One, B/Pol_SharedList_Two | E6b | pass |
| POL-027 | Three policies that share one key (Alpha, Mid, Zeta), saved one by one. | B/Pol_Shared_Alpha, B/Pol_Shared_Mid, B/Pol_Shared_Zeta | E6b | pass |
| POL-028 | A policy whose elements are defined in reverse name order: the order of the records written. | B/Pol_Shared_ElementsReverseOrder, C/Elements_ReverseOrder | E6b | pass |
| POL-029 | Labels with ampersands and explain texts (attribute omitted, empty, multi-paragraph) do not change the bytes saved. | B/Pol_Amp_DisplayName, B/Pol_Amp_LabelSingle, B/Pol_Amp_LabelDouble, B/Pol_Explain_* | E6b | pass |
| POL-030 | Policies used to show the sort order of the list: their saved bytes. | B/Pol_Sort_* | E6b | pass |
| POL-031 | Record order inside a key: a value set to new data moves to the end of its key; the same data changes nothing; Disabled then Enabled moves it to the end. Bears on OQ-01. | C/TwoInKey_ChangeFirstAgain, C/TwoInKey_ClassThenDecimal, C/TwoInKey_DecimalThenClass, C/Shared_MidAlphaZeta, C/Shared_ZetaAlphaMid | E6a, E6b | pass |
| POL-032 | Re-applying the same values, and Apply or OK with no change: nothing is written and gpt.ini does not change. | C/ReApply_SameValues, C/NoChange_Apply, C/Apply_Then_OK | E6a | pass |
| POL-033 | Clearing a setting with Not Configured removes its records; the last setting of a side leaves an emptied Registry.pol and the extension line with one space as its value. Bears on OQ-04 and OQ-05. | C/Clear_LastSetting_Machine, C/Clear_LastSetting_User, C/Clear_LastSetting_Disabled, C/Clear_LastSetting_WithComment, C/Clear_MachineAndUser, C/Clear_OneOfTwo | E6a, E6b | pass |
| POL-034 | Disabled records of several policies in one key and clearing them one by one. | C/Shared_DeleteRecordsOrder, C/Shared_MidAlphaZeta | E6b | pass |
| POL-035 | A list policy with its own value name across Enabled, a shorter list, Disabled and Not Configured. | C/List_PolicyValueName | E6b | pass |
| POL-036 | Shared lists: enabling, shrinking, disabling and clearing in both orders; an Enabled list with no rows. | C/SharedList_DisableAndShrink, C/SharedList_EmptyList, C/SharedList_OneThenTwo, C/SharedList_TwoThenOne | E6b | pass |
| POL-037 | Comments: add, edit, remove on an Enabled policy, on a Not Configured policy, on two policies, on a user policy, multi-line and non-ASCII text. | C/Comment_Enabled_AddEditRemove, C/Comment_NotConfigured_AddEditRemove, C/Comment_TwoPolicies, C/Comment_User, C/Comment_MultiLine | E6a | pass |
| POL-038 | A comment-only change after a normal save writes comment.cmtx and gpt.ini and raises the version. Bears on the comment half of OQ-07. | C/Comment_OnlyAfterSave, C/Comment_Enabled_AddEditRemove | E6a | pass |
| POL-039 | comment.cmtx skeleton: first save with no start file, first save over a file that names foreign namespaces, a user save after a machine save, the namespace list after the first save, and minRequiredRevision on an existing resources element. | C/Cmtx_NoStartFile, C/Cmtx_FirstSave_ForeignNamespaces, C/Cmtx_FirstSave_ThenUser | E6a | pass |
| POL-040 | gpt.ini version arithmetic: halves, carry, a start with the machine half at 65535, a start with the user half at 65535, a known start, six alternating saves. | C/Version_FromHighMachine, C/Version_FromHighUser, C/Version_FromKnownStart, C/Version_MachineUserCarry | E6a | pass |
| POL-041 | gpt.ini extension lists: the registry pair is added next to foreign pairs, foreign pairs are kept, and the pair leaves a side's list when that side has no setting left. Bears on OQ-07. | C/GptIni_ForeignExtensions, C/GptIni_PairRemoval, C/Clear_LastSetting_Machine, C/Clear_LastSetting_User | E6a | pass |
| POL-042 | gpt.ini bytes: ANSI text, CRLF line ends, a key that is missing is appended at the end of its section. | A/Machine_Enabled, A/User_Enabled, C/GptIni_ForeignExtensions | E6a | pass |
| POL-043 | Delete records on their own and next to values: the state shown and what the next save keeps. Depends on the policy shape (lone delete record reads as Not Configured for an enabled-only policy and as Disabled for a plain flag). | D/Del_Only, D/Del_OtherName, D/Del_ThenValue, D/Value_ThenDel, D/User_DelOnly, D/Del_DisabledValuePolicy | E6b, E6c | pass |
| POL-044 | Delete-all-values records and key-level instructions in a policy key. | D/Delvals, D/Delvals_After, D/Instruction_KeyLevel, D/List_WithDelvals | E6c | pass |
| POL-045 | Unknown instructions in a record key and alone in an unknown key. Bears on OQ-08. | D/Instruction_Unknown, D/Instruction_UnknownAlone | E6c | pass |
| POL-046 | Soft records. | D/Soft_Only, D/Soft_WithValue | E6c | pass |
| POL-047 | The data of a policy value does not match its enabled or disabled value: state shown for each mismatch (number as text, neither value, wrong case, wrong item, out of range, too long). | D/Mismatch_* | E6c | pass |
| POL-048 | Partial files: some of the values a policy would write are missing (booleans, elements without the gate, gate without elements). Bears on OQ-03. | D/Partial_* | E6c | pass |
| POL-049 | Values stored with the wrong registry type: how the state reads them. | D/WrongType_* | E6c | pass |
| POL-050 | List shapes in a file: plain, additive, explicit, prefix, policy value name, shared key, expandable as plain, entries of the wrong type, gap names, word names. Bears on OQ-06. | D/List_*, D/EnabledList_*, D/DisabledList_*, D/BothLists_* | E6c | pass |
| POL-051 | Enum values with item value lists: matching item, partial item, wrong item, item values without the choice. | D/EnumValueList_* | E6c | pass |
| POL-052 | Duplicate records in a file: identical, different (first or last wins), non-adjacent, split by a delete record, a key in two runs. | D/Dup_* | E6c | pass |
| POL-053 | Record order in a start file and what the next save normalizes: keys ascending, descending, interleaved, parent after child; values ascending and descending. | D/Order_* | E6c | pass |
| POL-054 | Letter case of keys and value names that differs from the template. | D/Case_KeyDiffers, D/Case_KeyTwoSpellings, D/Case_ValueNameDiffers | E6c | pass |
| POL-055 | Records that match no loaded policy (unknown key under the root, outside the roots, unknown value in a policy key, unknown value alone): invisible in the editor and kept untouched on every save. Decision of 2026-11-22. | D/Foreign_* | E6c, G4 | pass |
| POL-056 | Class Both policies with the value in one file or both files, and a user flag written in the machine file. | D/Both_BothFiles, D/Both_MachineFileOnly, D/Both_UserFileOnly, D/User_FlagEnabled, D/User_FlagInMachineFile | E6c | pass |
| POL-057 | Malformed Registry.pol start files make classic refuse to open the policy object: bad header, zero bytes, header only is accepted, missing close bracket, garbage, truncated last record, format version two. The refused cases keep the files unchanged. | D/File_BadHeader, D/File_Empty, D/File_MissingCloseBracket, D/File_OnlyGarbage, D/File_TruncatedLastRecord, D/File_VersionTwo | E6c, G4 | pass |
| POL-058 | Odd but accepted Registry.pol files: header only, two stray bytes after the last record, a 300 character value name. | D/File_HeaderOnly, D/File_TrailingBytes, D/File_LargeValueName | E6c | pass |
| POL-059 | Number edits at the limits, just outside them and typed key by key: the bytes written for each. The CAP-4 cases among them (the capture set the number without a state change) were recaptured (C10), and their recorded bytes are final. | E/Decimal_AboveMax, E/Decimal_BelowMin, E/Decimal_EdgeValues, E/Decimal_Apply, E/Decimal_TypedAboveMax, E/Decimal_Empty | E6c, G3b | pass |
| POL-060 | A number box given letters or left empty when required: the bytes written or the refusal. The CAP-4 cases among them were recaptured (C10). | E/Decimal_NotNumber, E/Decimal_TypedLetters, E/Decimal_Required_Empty | E6c, G3b | pass |
| POL-061 | Soft limits and store-as-text numbers out of range. The CAP-4 cases among them were recaptured (C10). | E/Decimal_Soft_OutOfRange, E/Decimal_StoreAsText_OutOfRange | E6c, G3b | pass |
| POL-062 | Long numbers: above the maximum, a 25 digit number, required left empty (a message box on one step). The CAP-4 cases among them were recaptured (C10). | E/LongDecimal_AboveMax, E/LongDecimal_Required_Empty | E6c, G3b | pass |
| POL-063 | Required text and combo box left empty: a message box on OK and nothing written; spaces only are accepted and written. Text longer than its maximum, typed or set: written. | E/Text_Required_Empty, E/Combo_Required_Empty, E/Text_TooLong_SetText, E/Text_TooLong_Typed | E6c, G3b | pass |
| POL-064 | A required drop-down with no choice: a message box and nothing written. OK on every required policy with its defaults: some are written, the rest are refused with a message box. | E/Enum_Required_Defaults, E/Ok_DefaultsOfRequired | E6c, G3b | pass |
| POL-065 | List entries: a plain list with an empty or duplicate entry and an explicit list with an empty or duplicate name are refused with a message box; an explicit list with an empty value or a one-cell row, a 300 character entry and special characters are written. | E/List_Plain_EmptyEntry, E/List_Plain_DuplicateEntries, E/List_Plain_LongEntry, E/List_Plain_SpecialCharacters, E/List_Explicit_EmptyName, E/List_Explicit_EmptyValue, E/List_Explicit_DuplicateNames, E/List_Explicit_NameOnly | E6c, G3b | pass |
| POL-066 | Multi-line text limits (too many lines, a line too long, nothing): each step is refused with a message box. | E/MultiText_Limits | E6c, G3b | pass |
| POL-067 | Next Setting with a required number empty: a message box, nothing written. Previous Setting with the same edit: no message box, nothing written. | E/Next_WithInvalid, E/Previous_WithInvalid | E6c, G3a | pass |
| POL-068 | The live registry after OK on a machine, a user and a class Both policy, and after a second OK with no change. | G/Machine_Ok, G/User_Ok, G/Both_Computer_Ok, G/Both_User_Ok | E6a, G4 | gap |
| POL-069 | The live registry after Apply and then Cancel. | G/Machine_Apply, G/User_Apply, G/Both_Computer_Apply, G/Both_User_Apply | E6a, G4 | gap |
| POL-070 | The live registry after Disabled and after Not Configured: applied values are changed or removed. | G/Machine_Disabled, G/User_Disabled, G/Both_Computer_Disabled, G/Both_User_Disabled, G/Machine_NotConfigured, G/User_NotConfigured, G/Both_Computer_NotConfigured, G/Both_User_NotConfigured | E6a, G4 | gap |
| POL-071 | The live registry after a comment-only change and after Enabled with the comment. | G/Machine_CommentOnly, G/User_CommentOnly, G/Both_Computer_CommentOnly, G/Both_User_CommentOnly | E6a, G4 | gap |
| POL-072 | Registry types and values written live for element policies, then removed by Not Configured. | G/Elements_* | E6a, G4 | gap |
| POL-073 | A policy key with a leading backslash: Enabled then Not Configured writes the key in the form classic uses. | K/KeyLeadingBackslash_Enable | E6a | pass |
| POL-074 | A policy key with a trailing backslash: Enabled then Not Configured. | K/KeyTrailingBackslash_Enable | E6a | pass |
| POL-075 | Reading a Registry.pol whose key is spelled as the template spells it and one spelled trimmed, for both edge templates. The leading-backslash file as written makes classic refuse to open. | K/KeyLeadingBackslash_ReadAsWritten, K/KeyLeadingBackslash_ReadTrimmed, K/KeyTrailingBackslash_ReadAsWritten, K/KeyTrailingBackslash_ReadTrimmed | E6c | pass |

### 2.11 ADMX: template handling

| ID | Feature | Oracle source | Package | Status |
| --- | --- | --- | --- | --- |
| ADMX-001 | Two template sources: the system folder and the BWC import store. The system folder wins on a duplicate namespace and the import is listed as shadowed. | docs/contracts/policy-engine.md; src/Bwc.Policy/Store/TemplateSources.cs | G6, E5 | pass |
| ADMX-002 | Language: the ADML is taken from the language chain of the UI culture (its parents, then en-US), as classic's fallback list does (finding LANG-1). | src/Bwc.Policy/Admx/TemplateLoader.cs, src/Bwc.Policy/Admx/AdmlLanguageChain.cs; H/MissingAdml; test class AdmlLanguageChainTests | A3 | pass |
| ADMX-003 | No schema validation: attributes and elements that the parser does not need are ignored; shipped templates that break the schema still load. | docs/contracts/policy-engine.md; H/ClassLowerCase, H/BoolAttributeUpper, H/BoolAttributeMixed | G6 | pass |
| ADMX-004 | Hardening: no DTD, no resolver, size cap, depth cap, reparse points refused, name length limits. | src/Bwc.Policy/Admx/SafeXml.cs, AdmxLimits.cs; H/DtdEntity | G6 | pass |
| ADMX-005 | A malformed XML template is skipped and reported; the other templates load. | H/MalformedXml | G6 | pass |
| ADMX-006 | A template with a missing ADML is skipped and reported. | H/MissingAdml | G6 | pass |
| ADMX-007 | A template that refers to a string the ADML does not define is skipped and reported. | H/UndefinedString | G6 | pass |
| ADMX-008 | A template with an undefined using prefix loads in classic with no error; its category and policy show. | H/UndefinedUsing | G6 | pass |
| ADMX-009 | A template that defines a policy twice is skipped and reported. | H/DuplicatePolicy | G6 | pass |
| ADMX-010 | A list item with no key is skipped and reported. | H/ListItemNoKey | G6 | pass |
| ADMX-011 | A boolean element with no value name is skipped and reported. | H/BooleanNoValueName | G6 | pass |
| ADMX-012 | Policy class values: Machine, User, Both. Lower-case class text raised an error in classic. | H/ClassLowerCase; docs/contracts/policy-engine.md | G6 | pass |
| ADMX-013 | Boolean attribute spellings in upper and mixed case raised an error in classic. | H/BoolAttributeUpper, H/BoolAttributeMixed | G6 | pass |
| ADMX-014 | Policy key and value name; an element's own key overrides the policy key for that element. | B/Pol_Element_KeyOverride; docs/contracts/policy-engine.md | E2, E6b | pass |
| ADMX-015 | Policy enabledValue and disabledValue, enabledList and disabledList (decimal and string forms). | B/Pol_Value_*, D/Mismatch_* | E2, E6b | pass |
| ADMX-016 | Element kinds: boolean, decimal, long decimal, text, multi-text, enum, list. | B/Pol_Bool_*, Pol_Decimal_*, Pol_LongDecimal_*, Pol_Text_*, Pol_MultiText_*, Pol_Enum_*, Pol_List_* | E2, E6b | pass |
| ADMX-017 | Presentation controls from the ADML presentation table: check box, combo box, decimal and long decimal text boxes, drop-down list, list box, text box, multi-line text box, static text. | B/Pol_Text_ComboBox*, F/Dialog_Layouts; docs/contracts/policy-engine.md | E2, G3b | pass |
| ADMX-018 | Number options: minimum, maximum, soft limit, store-as-text; text options: expandable, required, maximum length. | B/Pol_Decimal_*, B/Pol_Text_*, E/Decimal_*, E/Text_* | E6b, E6c | pass |
| ADMX-019 | Category tree built from the parent category references, including parents in another file, cycles cut out and policies whose category is missing. | F/Tree_Computer, F/Tree_User; docs/failure-modes/policy-admx.md | E2, G1 | pass |
| ADMX-020 | Supported-on definitions and the requirements text; an unresolved supported-on is reported. | B/*, F/Nodes_*; docs/failure-modes/policy-admx.md | E2 | pass |
| ADMX-021 | The policy key may start or end with a backslash; reading and writing treat the key as written and as trimmed in the way classic does. | K/KeyLeadingBackslash_*, K/KeyTrailingBackslash_* | E6a, E6c | pass |
| ADMX-022 | All Settings lists every policy of the side, sorted by display name, with the path column. | F/AllSettings_Computer, F/AllSettings_User | G1, G5 | pass |
| ADMX-023 | The load result lists every failure with its reason and keeps the templates that loaded. | H/*; docs/contracts/policy-engine.md | G6, E2 | pass |
| ADMX-024 | The load of the real system templates stays under the load-time limit and succeeds. | docs/contracts/policy-engine.md | E2, A1 | pass |
| ADMX-025 | Policy identity is the namespace plus the policy name; the display name comes from the ADML and is what the tree, list and dialog show. | B/*, F/Nodes_* | E2 | pass |
| ADMX-026 | Template import check: a file that cannot be imported is refused with a named problem and nothing is copied. | src/Bwc.Policy/Store/TemplateImport.cs | G6 | pass |
| ADMX-027 | Template import copies the ADMX and the ADML into the store; the bytes written are the bytes that were checked. | src/Bwc.Policy/Store/TemplateImport.cs; plan F16 | G6 | pass |
| ADMX-028 | Template remove deletes the imported files of one namespace and reports what it removed. | src/Bwc.Policy/Store/TemplateImportTypes.cs | G6 | pass |
| ADMX-029 | The import store folder is administrators only; file names in the store follow the template file name rules; classic .adm files are refused. | src/Bwc.Policy/Store/TemplateStoreChecks.cs, TemplateFileNames.cs | G6, E5 | pass |
| ADMX-030 | A cap on the number of files read from an import source folder. | plan F17 | G6 | pass |
| ADMX-031 | Policy text from the ADML (display name, explain text, labels) keeps ampersands and multi-paragraph text as written. | B/Pol_Amp_*, B/Pol_Explain_* | E2, G3a | pass |
| ADMX-032 | Filter evaluation over the template set: managed, configured, commented, keywords and requirements, with the result rows recorded for each filter setting. | F/Filter_Options; src/Bwc.Policy/Filter | G5, E6c | pass |

## 3. Declared differences

Each row is an intended difference from classic. The audit carries these over as declared differences.

Two user decisions of 2026-11-22 are recorded as removed rows, not as differences, because classic shows neither feature at run time: no Favorites menu (MENU-037) and no Extra Registry Settings node (TREE-020 and BEH-012; records that match no policy stay invisible and are kept untouched on every save, BEH-014). The audit lists both decisions with the declared differences.

| ID | Difference | Reason | Package | Status |
| --- | --- | --- | --- | --- |
| DIFF-001 | BWC opens read-only when the process is not elevated or the edition is Home. Classic refuses to open. | Showing current policy is useful and safe; writing is blocked. | G4, E3 | declared-difference |
| DIFF-002 | The Extended view pane is native. Classic shows an HTML page. | The HTML control is not available to a WinUI tool. | U2c | declared-difference |
| DIFF-003 | Save reads, modifies and writes under a file lock, so an edit made outside BWC is not overwritten. | Safer than classic's whole-file rewrite from a stale model. | E3 | declared-difference |
| DIFF-004 | A backup of the three GPO files (Registry.pol, gpt.ini, comment.cmtx) is kept before the first write of a session in `%ProgramData%\BetterWindowsConfig\gpedit\backup`, last 10. | Recovery from a bad edit; classic keeps no backup. | E3 | declared-difference |
| DIFF-005 | Imported templates live in the BWC store, not the system folder. Classic `.adm` files are not supported. | Keeps system files untouched and the store admin-only. | G6, E5 | declared-difference |
| DIFF-006 | Help Topics and About follow the precedent set by the Registry Editor. | Consistent with the existing tool. | U3b | declared-difference |

## 4. Open questions

Each question is closed by the capture case named with it (E6c). Nothing is left open; the rules that no capture reaches are
rows of the Gaps section of `docs/parity/gpedit-audit.md`.

- OQ-01. Record order inside a key when a policy writes several values. Closed: a value set to new data moves to the end of its key, the same data changes nothing, and keys are written in tree order whatever the order they were in (POL-031; C/TwoInKey_ChangeFirstAgain, C/Shared_MidAlphaZeta, D/Order_KeysDescending, D/Order_ParentAfterChild, D/Order_ValuesAscending).
- OQ-02. How a boolean that is false is stored when the template has no `falseValue`. Closed: a `**del.` record of the value name, no value (B/Pol_Bool_NoFalseValue step 3). A checked box writes the DWORD 1 (step 2).
- OQ-03. How policy state is inferred from a partial or foreign Registry.pol. Closed: the rules of `docs/failure-modes/policy-state.md` section 1, derived from all D cases of each kind (D/Partial_*, D/Mismatch_*, D/WrongType_*, D/Del_*, D/Foreign_*, D/List_*). Records that match no policy change nothing (D/Foreign_ValueInPolicyKey).
- OQ-04. What Not Configured removes from a key shared by policies or lists. Closed: every value and the delete-values record of the key, also when the other policy that shares the key is still Disabled (C/SharedList_DisableAndShrink step 6, C/SharedList_OneThenTwo).
- OQ-05. What is written when the file ends up with no records. Closed: a Registry.pol of the 8-byte header and the extension line of gpt.ini with one space (B/Pol_List_Additive step 6, C/Clear_LastSetting_Machine, POL-033).
- OQ-06. How list values are named. Closed: a plain list takes its entry as the name and the data, a prefix list the prefix and a number from 1 (Item1, Item2), an explicit list the name cell with the value cell as data, a policy-value-name list names its entries like a plain list and writes the policy value as the DWORD 1 (B/Pol_List_Plain, B/Pol_List_ValuePrefix, B/Pol_List_ExplicitValue, B/Pol_List_PolicyValueName). Reading a plain list takes only entries whose name equals the data (D/List_AdditiveValues, D/List_PlainValues).
- OQ-07. When gpt.ini extension pairs are removed, and whether a comment-only change bumps the version. Closed: the pair leaves a side when the side has no setting left, foreign pairs stay (POL-041; C/GptIni_PairRemoval, C/GptIni_ForeignExtensions); a comment-only change writes comment.cmtx and gpt.ini and raises the version (POL-038; C/Comment_OnlyAfterSave).
- OQ-08. Which `**` instructions classic keeps or shows. Closed: it keeps every one in place and shows none (D/Instruction_KeyLevel, D/Instruction_Unknown, D/Instruction_UnknownAlone; BEH-014). The lone delete record that reads Disabled is in D/Del_Only.
- OQ-09. The contents of the Extra Registry Settings node. Closed: there is no node; records that match no policy stay invisible and are kept untouched (BEH-014, TREE-020; D/Foreign_*).

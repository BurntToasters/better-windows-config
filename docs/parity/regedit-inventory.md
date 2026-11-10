# Better Windows Config: regedit parity inventory

## 1. Introduction

**Purpose.** This is the parity inventory for Better Windows Config (BWC), a WinUI 3 re-implementation of classic Windows Registry Editor (regedit.exe 10.0.26100.8457, en-US). Each user-visible feature of classic regedit is one row with a stable ID. Implementation packages move rows to pass as they are built, and the final audit re-checks every row. Inputs: docs/parity/oracle (resource dump, layout metrics, screenshot, seed tree, export goldens) and docs/contracts/registry-engine.md.

**ID scheme.** An ID is PREFIX-NNN with a three-digit number, assigned in row order within each section. Prefixes: MENU (main menu), CTX (context menus), DLG (dialogs), ACC (accelerators and keyboard), MSG (strings), VIEW (layout and density), BEH (behavior not in the resources), EXP (export and import bytes). IDs are never reused; a retired row keeps its ID.

**Status values.** open: not yet verified (every row starts here). pass: verified against the oracle by a test or golden. declared-difference: BWC intentionally differs; the reason is recorded in the row or in section 4. gap: known missing and not yet declared.

**Row granularity.** MENU and CTX have one row per menu occurrence, so a command that appears in two menus has two rows. Separators are not rows; each menu has a layout row that records separator placement. The five unlabeled popups in RT_MENU 104 to 108 are menu bodies and are covered by the CTX layout rows. DLG has one layout row per dialog (controls, order, tab stops, default button) and one row per non-trivial control behavior that cites control ids. ACC has one row per accelerator table entry plus four keyboard behaviors that are not table entries. MSG has one row per string id. Package names are defined in section 3; a row may name two packages.

**Source notes.** regedit.exe and en-US/regedit.exe.mui have identical menus, dialogs, accelerator tables and string tables (compared field by field in the JSON), so rows cite the regedit.exe module. Strings are verbatim; CR and LF are shown as \r and \n, and the mnemonic markers (&) are kept. Behavior rows marked Brief come from the project brief, not from the oracle dump.

**Coverage and cross-check.**

- MENU: 37 rows = 1 layout row + 35 RT_MENU 103 item occurrences (File 8, Edit 14 incl. the New submenu, View 5, Favorites 2, Help 1, plus 5 popup headers) + 1 runtime favorites row. Each JSON node was walked once; 12 separators are in the layout row.
- CTX: 41 rows = 5 layout rows + 36 items from RT_MENU 104 to 108 (the 5 unlabeled container popups are menu bodies, not rows). 9 separators are in the layout rows.
- DLG: 51 rows = 26 dialog layout rows + 25 behavior rows. The layout rows list all 212 controls in the 26 dialogs; each control id was asserted to appear in its dialog row.
- ACC: 28 rows = 24 accelerator table entries (asserted in table order against the JSON commands) + 4 keyboard behaviors.
- MSG: 276 rows = 278 string ids minus 2 skipped (8100 and 8111, format fragments; see section 2.5).
- VIEW: 20 rows; BEH: 49 rows; EXP: 32 rows (29 value-class rows and 3 file-format rows).
- Total: 534 rows. Open questions: 49 (section 4). IDs are unique within each prefix, and every row starts with status open.

## 2. Inventory tables

### 2.1 MENU: main menu (RT_MENU 103)

| ID | Feature | Oracle source | Package | Status |
| --- | --- | --- | --- | --- |
| MENU-001 | Layout: main menu bar (RT_MENU 103, MENUEX, help id 0). Top-level popups in order: File, Edit, View, Favorites, Help. Separators (12): in &File, after '&Export...'; in &File, after '&Unload Hive...'; in &File, after '&Disconnect Network Registry...' (separator carries command id 673, no text); in &File, after '&Print...'; in &Edit > &New, after '&Key'; in &Edit, after '&New'; in &Edit, after '&Permissions...'; in &Edit, after '&Rename'; in &Edit, after '&Copy Key Name'; in &View, after 'Address &Bar'; in &View, after 'Sp&lit'; in &View, after '&Display Binary Data...'. | RT_MENU 103 | U3 | open |
| MENU-002 | &File | RT_MENU 103, id 512 | U3 | open |
| MENU-003 | &File > &Import... | RT_MENU 103, id 658 | U3, R7 | open |
| MENU-004 | &File > &Export... | RT_MENU 103, id 659 | U3, R7 | open |
| MENU-005 | &File > &Load Hive... | RT_MENU 103, id 680 | U3, R8 | open |
| MENU-006 | &File > &Unload Hive... | RT_MENU 103, id 681 | U3, R8 | open |
| MENU-007 | &File > &Connect Network Registry... | RT_MENU 103, id 657 | U3, R8 | open |
| MENU-008 | &File > &Disconnect Network Registry... | RT_MENU 103, id 768 | U3, R8 | open |
| MENU-009 | &File > &Print... (accelerator Ctrl+P) | RT_MENU 103, id 660 | U3, R14 | open |
| MENU-010 | &File > E&xit | RT_MENU 103, id 661 | U3, R1 | open |
| MENU-011 | &Edit | RT_MENU 103, id 513 | U3 | open |
| MENU-012 | &Edit > &New | RT_MENU 103, id 516 | U3, R3 | open |
| MENU-013 | &Edit > &New > &Key | RT_MENU 103, id 663 | U3, R3 | open |
| MENU-014 | &Edit > &New > &String Value | RT_MENU 103, id 664 | U3, R3 | open |
| MENU-015 | &Edit > &New > &Binary Value | RT_MENU 103, id 665 | U3, R3 | open |
| MENU-016 | &Edit > &New > &DWORD (32-bit) Value | RT_MENU 103, id 674 | U3, R3 | open |
| MENU-017 | &Edit > &New > &QWORD (64-bit) Value | RT_MENU 103, id 688 | U3, R3 | open |
| MENU-018 | &Edit > &New > &Multi-String Value | RT_MENU 103, id 676 | U3, R3 | open |
| MENU-019 | &Edit > &New > &Expandable String Value | RT_MENU 103, id 677 | U3, R3 | open |
| MENU-020 | &Edit > &Permissions... | RT_MENU 103, id 678 | U3, R11 | open |
| MENU-021 | &Edit > &Delete (accelerator Del) | RT_MENU 103, id 913 | U3, R4 | open |
| MENU-022 | &Edit > &Rename | RT_MENU 103, id 914 | U3, R4 | open |
| MENU-023 | &Edit > &Copy Key Name | RT_MENU 103, id 675 | U3, R12 | open |
| MENU-024 | &Edit > &Find... (accelerator Ctrl+F) | RT_MENU 103, id 662 | U3, R9 | open |
| MENU-025 | &Edit > Find Ne&xt (accelerator F3) | RT_MENU 103, id 671 | U3, R9 | open |
| MENU-026 | &View | RT_MENU 103, id 514 | U3 | open |
| MENU-027 | &View > Address &Bar | RT_MENU 103, id 668 | U3, U11 | open |
| MENU-028 | &View > Sp&lit | RT_MENU 103, id 670 | U3, U2 | open |
| MENU-029 | &View > &Display Binary Data... | RT_MENU 103, id 679 | U3, R6 | open |
| MENU-030 | &View > &Refresh (accelerator F5) | RT_MENU 103, id 648 | U3, R2 | open |
| MENU-031 | &View > &Font | RT_MENU 103, id 699 | U3, U1 | open |
| MENU-032 | F&avorites | RT_MENU 103, id 517 | U3, R10 | open |
| MENU-033 | F&avorites > &Add to Favorites... | RT_MENU 103, id 1281 | U3, R10 | open |
| MENU-034 | F&avorites > &Remove Favorite... | RT_MENU 103, id 1280 | U3, R10 | open |
| MENU-035 | F&avorites > (dynamic favorites list, built at run time from the Favorites key; no resource id) | None in RT_MENU 103 (runtime list); see BEH-001 and OQ-08 | R10, U3 | open |
| MENU-036 | &Help | RT_MENU 103, id 515 | U3 | open |
| MENU-037 | &Help > &About Registry Editor... | RT_MENU 103, id 667 | U3, U4 | open |

### 2.2 CTX: context menus (RT_MENU 104 to 108)

| ID | Feature | Oracle source | Package | Status |
| --- | --- | --- | --- | --- |
| CTX-001 | Layout: Tree key context menu, identified by its contents (Find, Permissions, Copy Key Name, Go to HKEY_LOCAL_MACHINE, New, Delete, Rename, Export, Expand); the menu for key nodes. Separators (4): in &New, after '&Key'; in menu root, after '&Find...'; in menu root, after '&Rename'; in menu root, after '&Permissions...'. | RT_MENU 104 | R12 | open |
| CTX-002 | Expand | RT_MENU 104, id 915 | R12, U5 | open |
| CTX-003 | &New | RT_MENU 104, id 516 | R12, R3 | open |
| CTX-004 | &New > &Key | RT_MENU 104, id 663 | R12, R3 | open |
| CTX-005 | &New > &String Value | RT_MENU 104, id 664 | R12, R3 | open |
| CTX-006 | &New > &Binary Value | RT_MENU 104, id 665 | R12, R3 | open |
| CTX-007 | &New > &DWORD (32-bit) Value | RT_MENU 104, id 674 | R12, R3 | open |
| CTX-008 | &New > &QWORD (64-bit) Value | RT_MENU 104, id 688 | R12, R3 | open |
| CTX-009 | &New > &Multi-String Value | RT_MENU 104, id 676 | R12, R3 | open |
| CTX-010 | &New > &Expandable String Value | RT_MENU 104, id 677 | R12, R3 | open |
| CTX-011 | &Find... | RT_MENU 104, id 662 | R12, R9 | open |
| CTX-012 | &Delete | RT_MENU 104, id 913 | R12, R4 | open |
| CTX-013 | &Rename | RT_MENU 104, id 914 | R12, R4 | open |
| CTX-014 | &Export | RT_MENU 104, id 659 | R12, R7 | open |
| CTX-015 | &Permissions... | RT_MENU 104, id 678 | R12, R11 | open |
| CTX-016 | &Copy Key Name | RT_MENU 104, id 675 | R12 | open |
| CTX-017 | Go &to HKEY_LOCAL_MACHINE | RT_MENU 104, id 918 | R12, R2 | open |
| CTX-018 | Layout: List value context menu, identified by its contents (Modify, Modify Binary Data, Delete, Rename); the menu for value rows. Separators (1): in menu root, after 'Modify &Binary Data...' (separator carries command id 673, no text). | RT_MENU 105 | R12 | open |
| CTX-019 | &Modify... | RT_MENU 105, id 912 | R12, R5 | open |
| CTX-020 | Modify &Binary Data... | RT_MENU 105, id 917 | R12, R6 | open |
| CTX-021 | &Delete | RT_MENU 105, id 913 | R12, R4 | open |
| CTX-022 | &Rename | RT_MENU 105, id 914 | R12, R4 | open |
| CTX-023 | Layout: List background context menu, identified by its contents (New submenu only); the menu for the empty list area. Separators (1): in &New, after '&Key'. | RT_MENU 106 | R12 | open |
| CTX-024 | &New | RT_MENU 106, id 516 | R12, R3 | open |
| CTX-025 | &New > &Key | RT_MENU 106, id 663 | R12, R3 | open |
| CTX-026 | &New > &String Value | RT_MENU 106, id 664 | R12, R3 | open |
| CTX-027 | &New > &Binary Value | RT_MENU 106, id 665 | R12, R3 | open |
| CTX-028 | &New > &DWORD (32-bit) Value | RT_MENU 106, id 674 | R12, R3 | open |
| CTX-029 | &New > &QWORD (64-bit) Value | RT_MENU 106, id 688 | R12, R3 | open |
| CTX-030 | &New > &Multi-String Value | RT_MENU 106, id 676 | R12, R3 | open |
| CTX-031 | &New > &Expandable String Value | RT_MENU 106, id 677 | R12, R3 | open |
| CTX-032 | Layout: Remote computer root context menu, identified by its contents (Expand, Export, Disconnect); attachment to the remote root is inferred (OQ-03). Separators (2): in menu root, after 'Expand'; in menu root, after '&Export' (separator carries command id 673, no text). | RT_MENU 107 | R12 | open |
| CTX-033 | Expand | RT_MENU 107, id 915 | R12, U5 | open |
| CTX-034 | &Export | RT_MENU 107, id 659 | R12, R7 | open |
| CTX-035 | &Disconnect | RT_MENU 107, id 768 | R12, R8 | open |
| CTX-036 | Layout: Text edit context menu, identified by its contents (Cut, Copy, Paste, Delete, Select All with standard edit command ids); the control it attaches to is not in the dump (OQ-02). Separators (1): in menu root, after '&Delete' (separator carries command id 673, no text). | RT_MENU 108 | R12 | open |
| CTX-037 | Cu&t | RT_MENU 108, id 24 | R12, R5 | open |
| CTX-038 | &Copy | RT_MENU 108, id 3 | R12, R5 | open |
| CTX-039 | &Paste | RT_MENU 108, id 22 | R12, R5 | open |
| CTX-040 | &Delete | RT_MENU 108, id 46 | R12, R5 | open |
| CTX-041 | Select &All | RT_MENU 108, id 1024 | R12, R5 | open |

### 2.3 DLG: dialogs (RT_DIALOG)

Layout rows give the template order, the default button, the tab stops and the read-only and template-disabled controls. Child templates (WS_CHILD, no caption) are panels embedded in common file dialogs; their use is inferred from the style.

| ID | Feature | Oracle source | Package | Status |
| --- | --- | --- | --- | --- |
| DLG-001 | Layout: Export range panel (child template with the Export range group; intended for the Export file dialog, inferred). Child template (WS_CHILD), no caption. Template DIALOGEX, style 0x54000444, size 264 x 191 DLU at (32, 24). Controls in template order (6): 1119 Static; 1283 Button 'Export range'; 1280 Button '&All'; 1281 Button 'S&elected branch'; no id Static 'Selected branch'; 1282 Edit. Default button: none. Tab stops in template order: 1280 Button '&All', 1282 Edit. Read-only edits: none. Template-disabled controls: 1119 Static. | RT_DIALOG 100 | R7, U4 | open |
| DLG-002 | Export range radios: All (control 1280) and Selected branch (1281) choose the export scope. The branch path edit (1282) has no ES_READONLY bit (style 0x50830080). Whether it is enabled only for Selected branch is not in the dump (OQ-15). | RT_DIALOG 100, controls 1280, 1281, 1282 | R7, B5 | open |
| DLG-003 | Layout: Edit String. Caption 'Edit String'. Template DIALOGEX, style 0x90C801C4, size 252 x 84 DLU at (32, 24). Controls in template order (6): no id Static '&Value data:'; 1001 Edit; 1 Button 'OK'; 2 Button 'Cancel'; no id Static 'Value &name:'; 1000 Edit. Default button: 1 Button 'OK'. Tab stops in template order: 1001 Edit, 1 Button 'OK', 2 Button 'Cancel', 1000 Edit. Read-only edits: 1000 Edit. Template-disabled controls: none. | RT_DIALOG 102 | R5, U4 | open |
| DLG-004 | Value name edit (control 1000) is read-only (ES_READONLY, style 0x50810880) in dialogs 102, 103, 111, 114 and 117. Only the value data edit (1001) accepts input; names change only through Rename (OQ-47). | RT_DIALOG 102, 103, 111, 114, 117 (control 1000) | R5, R4 | open |
| DLG-005 | Layout: Edit Binary Value. Caption 'Edit Binary Value'. Template DIALOGEX, style 0x90C801C4, size 232 x 178 DLU at (32, 24). Controls in template order (6): no id Static '&Value data:'; 1001 HEX; 1 Button 'OK'; 2 Button 'Cancel'; no id Static 'Value &name:'; 1000 Edit. Default button: 1 Button 'OK'. Tab stops in template order: 1001 HEX, 1 Button 'OK', 2 Button 'Cancel', 1000 Edit. Read-only edits: 1000 Edit. Template-disabled controls: none. | RT_DIALOG 103 | R6, U4 | open |
| DLG-006 | Hex editor: control 1001 is class HEX (220 x 110 DLU, client edge, border, vertical scroll bar, tab stop). OK saves and Cancel discards. Byte editing behavior is covered by the U9 and U10 packages. | RT_DIALOG 103, control 1001 | U9, U10 | open |
| DLG-007 | Layout: Load Hive. Caption 'Load Hive'. Template DIALOGEX, style 0x90C801C4, size 252 x 53 DLU at (32, 24). Controls in template order (4): no id Static '&Key Name:'; 1001 Edit; 1 Button 'OK'; 2 Button 'Cancel'. Default button: 1 Button 'OK'. Tab stops in template order: 1001 Edit, 1 Button 'OK', 2 Button 'Cancel'. Read-only edits: none. Template-disabled controls: none. | RT_DIALOG 104 | R8, U4 | open |
| DLG-008 | Key Name prompt: control 1001 takes the name of the key that receives the hive. Duplicate names, empty names and validation are not in the dump (OQ-20). The hive commands act only under HKEY_LOCAL_MACHINE and HKEY_USERS (BEH-022). | RT_DIALOG 104, control 1001 | R8, B8 | open |
| DLG-009 | Layout: Print progress. Caption 'Print'. Template DIALOGEX, style 0x90C801C4, size 154 x 49 DLU at (32, 24). Controls in template order (2): 2 Button 'Cancel'; no id Static 'Printing the registry...'. Default button: 2 Button 'Cancel'. Tab stops in template order: 2 Button 'Cancel'. Read-only edits: none. Template-disabled controls: none. | RT_DIALOG 105 | R14, U4 | open |
| DLG-010 | Layout: Find. Caption 'Find'. Template DIALOGEX, style 0x90C801C4, size 264 x 96 DLU at (32, 24). Controls in template order (10): no id Static 'Fi&nd what:'; 1150 Edit; 1152 Button '&Keys'; 1153 Button '&Values'; 1154 Button '&Data'; 1151 Button 'Match &whole string only'; 1161 Button 'Search in &subtree'; 1 Button '&Find Next'; 2 Button 'Cancel'; 1160 Button 'Look at'. Default button: 1 Button '&Find Next'. Tab stops in template order: 1150 Edit, 1152 Button '&Keys', 1153 Button '&Values', 1154 Button '&Data', 1151 Button 'Match &whole string only', 1161 Button 'Search in &subtree', 1 Button '&Find Next', 2 Button 'Cancel'. Read-only edits: none. Template-disabled controls: none. | RT_DIALOG 106 | R9, U4 | open |
| DLG-011 | Look at group (1160) holds the Keys (1152), Values (1153) and Data (1154) checkboxes that choose what is matched. Match whole string only (1151) switches substring matching to whole-string equality. Tab stops in template order: Find what edit, Keys, Values, Data, Match whole string only, Search in subtree, Find Next (default), Cancel. | RT_DIALOG 106, controls 1150 to 1154, 1160 | R9, B7 | open |
| DLG-012 | Search in subtree checkbox (control 1161): default state and effect are not described in the dump (OQ-10). | RT_DIALOG 106, control 1161 | R9, B7 | open |
| DLG-013 | Layout: Disconnect Network Registry computer list. Caption 'Disconnect Network Registry'. Template DIALOGEX, style 0x90C801C4, size 250 x 111 DLU at (32, 24). Controls in template order (4): no id Static 'Computer name:'; 1175 SysListView32; 1 Button 'OK'; 2 Button 'Cancel'. Default button: 1 Button 'OK'. Tab stops in template order: 1175 SysListView32, 1 Button 'OK', 2 Button 'Cancel'. Read-only edits: none. Template-disabled controls: none. | RT_DIALOG 107 | R8, U4 | open |
| DLG-014 | Computer list (control 1175, SysListView32, report view with no column header, style 0x50814041, 236 x 63 DLU) beside a Computer name label, with OK and Cancel. Which command opens this dialog is open (OQ-01). | RT_DIALOG 107, control 1175 | R8, B9 | open |
| DLG-015 | Layout: Print range panel (child template; intended for the Print dialog, inferred). Child template (WS_CHILD), no caption. Template DIALOGEX, style 0x50000440, size 288 x 178 DLU at (0, 0). Controls in template order (5): 1072 Button 'Print range'; 1280 Button 'A&ll'; 1281 Button 'S&elected branch'; no id Static 'Selected branch'; 1282 Edit. Default button: none. Tab stops in template order: 1280 Button 'A&ll', 1282 Edit. Read-only edits: none. Template-disabled controls: none. | RT_DIALOG 108 | R14, U4 | open |
| DLG-016 | Print range panel (child template, no caption): All (1280) and Selected branch (1281) radios with a branch path edit (1282). Print behavior is open (OQ-44). | RT_DIALOG 108, controls 1280 to 1282 | R14 | open |
| DLG-017 | Layout: Find progress. Caption 'Find'. Template DIALOGEX, style 0x90C801C4, size 170 x 64 DLU at (32, 24). Controls in template order (3): 2 Button 'Cancel'; no id Static 'Searching the registry...'; no id Static 'ordinal 0x0066'. Default button: 2 Button 'Cancel'. Tab stops in template order: 2 Button 'Cancel'. Read-only edits: none. Template-disabled controls: none. | RT_DIALOG 109 | R9, U4 | open |
| DLG-018 | Find progress: Cancel (2) is the default button. Static icon (ordinal 0x66, SS_ICON style) with the text Searching the registry. Cancel stops the background search (registry-engine contract). | RT_DIALOG 109, controls 2 and the icon static | R9, B7 | open |
| DLG-019 | Layout: Import Registry File progress. Caption 'Import Registry File'. Template DIALOGEX, style 0x90C001C4, size 240 x 59 DLU at (32, 24). Controls in template order (3): 101 msctls_progress32; 100 Static; no id Static 'File:'. Default button: none. Tab stops in template order: none. Read-only edits: none. Template-disabled controls: none. | RT_DIALOG 110 | R7, U4 | open |
| DLG-020 | Import progress: a progress bar (101) and a file name line (100) with no Cancel button and no default button. Completion and error surface are open (OQ-18). | RT_DIALOG 110, controls 100, 101 | R7 | open |
| DLG-021 | Layout: Edit DWORD (32-bit) Value. Caption 'Edit DWORD (32-bit) Value'. Template DIALOGEX, style 0x90C801C4, size 219 x 103 DLU at (32, 24). Controls in template order (9): no id Static '&Value data:'; 1001 Edit; no id Button 'Base'; 1002 Button '&Hexadecimal'; 1003 Button '&Decimal'; 1 Button 'OK'; 2 Button 'Cancel'; no id Static 'Value &name:'; 1000 Edit. Default button: 1 Button 'OK'. Tab stops in template order: 1001 Edit, 1002 Button '&Hexadecimal', 1 Button 'OK', 2 Button 'Cancel', 1000 Edit. Read-only edits: 1000 Edit. Template-disabled controls: none. | RT_DIALOG 111 | R5, U4 | open |
| DLG-022 | Base group with Hexadecimal (1002, group start and tab stop) and Decimal (1003, not a tab stop) radios. Switching the base converts the displayed value. Default base on open is open (OQ-12). | RT_DIALOG 111, controls 1002, 1003 | R5, B4 | open |
| DLG-023 | Decimal input above the DWORD maximum triggers RT_STRING 119 (the value may be truncated to continue). A caption is probably RT_STRING 110 (Overflow) or 111 (Warning); the dump does not state which one is used. | RT_DIALOG 111; RT_STRING 119 | R5, B4 | open |
| DLG-024 | Layout: Add to Favorites. Caption 'Add to Favorites'. Template DIALOGEX, style 0x90C801C4, size 191 x 65 DLU at (32, 24). Controls in template order (4): no id Static '&Favorite name:'; 1190 Edit; 1 Button 'OK'; 2 Button 'Cancel'. Default button: 1 Button 'OK'. Tab stops in template order: 1190 Edit, 1 Button 'OK', 2 Button 'Cancel'. Read-only edits: none. Template-disabled controls: none. | RT_DIALOG 112 | R10, U4 | open |
| DLG-025 | Favorite name edit (1190) must not duplicate an existing favorite (RT_STRING 171, caption RT_STRING 172). Name comparison rules are open (OQ-43). | RT_DIALOG 112, control 1190 | R10 | open |
| DLG-026 | Layout: Remove Favorites. Caption 'Remove Favorites'. Template DIALOGEX, style 0x80C800C4, size 194 x 103 DLU at (0, 0). Controls in template order (4): 1 Button 'OK'; 2 Button 'Cancel'; 1191 ListBox; no id Static 'Select Favorite(s):'. Default button: 1 Button 'OK'. Tab stops in template order: 1 Button 'OK', 2 Button 'Cancel', 1191 ListBox. Read-only edits: none. Template-disabled controls: none. | RT_DIALOG 113 | R10, U4 | open |
| DLG-027 | Select Favorite(s) list (1191, ListBox with multiple selection and sorted entries, style 0x50A1010B). Several favorites can be chosen for removal. The dump has no confirmation text for this action (OQ-43). | RT_DIALOG 113, control 1191 | R10 | open |
| DLG-028 | Layout: Edit Multi-String. Caption 'Edit Multi-String'. Template DIALOGEX, style 0x90C801C4, size 232 x 178 DLU at (32, 24). Controls in template order (6): no id Static '&Value data:'; 1001 Edit; 1 Button 'OK'; 2 Button 'Cancel'; no id Static 'Value &name:'; 1000 Edit. Default button: 1 Button 'OK'. Tab stops in template order: 1001 Edit, 1 Button 'OK', 2 Button 'Cancel', 1000 Edit. Read-only edits: 1000 Edit. Template-disabled controls: none. | RT_DIALOG 114 | R6, U4 | open |
| DLG-029 | Multi-line value editor: control 1001 holds one string per line, with vertical and horizontal scroll bars and WANT_RETURN, so Enter adds a line. | RT_DIALOG 114, control 1001 | R6, B4 | open |
| DLG-030 | Blank strings: an empty line produces RT_STRING 116 (the empty string found will be removed) or RT_STRING 117 (all empty strings will be removed). The choice between them is inferred from the wording (OQ-13). | RT_DIALOG 114; RT_STRING 116, 117 | R6, B4 | open |
| DLG-031 | Layout: computer picker (caption Import Registry File, label Select Computer:). Caption 'Import Registry File'. Template DIALOGEX, style 0x90C801C4, size 250 x 111 DLU at (32, 24). Controls in template order (4): no id Static 'Select Computer:'; 102 SysListView32; 1 Button 'OK'; 2 Button 'Cancel'. Default button: 1 Button 'OK'. Tab stops in template order: 102 SysListView32, 1 Button 'OK', 2 Button 'Cancel'. Read-only edits: none. Template-disabled controls: none. | RT_DIALOG 115 | R8, U4 | open |
| DLG-032 | Computer picker with caption Import Registry File and label Select Computer: (control 102, SysListView32). Its caption does not match a picker for Import, and its layout resembles dialog 107 (OQ-01). | RT_DIALOG 115, control 102 | R8, R7 | open |
| DLG-033 | Layout: Resource Lists viewer. Caption 'Resource Lists'. Template DIALOGEX, style 0x90C801C4, size 170 x 120 DLU at (32, 24). Controls in template order (3): 1 Button 'OK'; 1203 Button 'Display...'; 1205 ColumnListBox 'Bus Number;Interface Type'. Default button: none. Tab stops in template order: 1 Button 'OK', 1203 Button 'Display...', 1205 ColumnListBox 'Bus Number;Interface Type'. Read-only edits: none. Template-disabled controls: none. | RT_DIALOG 116 | R6, U4 | open |
| DLG-034 | Resource list viewer: column list box 1205 (columns Bus Number and Interface Type). Display... (1203) is enabled in the template and opens a detail view whose target is not in the dump (OQ-16). | RT_DIALOG 116, controls 1203, 1205 | R6 | open |
| DLG-035 | Layout: Edit QWORD (64-bit) Value. Caption 'Edit QWORD (64-bit) Value'. Template DIALOGEX, style 0x90C801C4, size 219 x 103 DLU at (32, 24). Controls in template order (9): no id Static '&Value data:'; 1001 Edit; no id Button 'Base'; 1002 Button '&Hexadecimal'; 1003 Button '&Decimal'; 1 Button 'OK'; 2 Button 'Cancel'; no id Static 'Value &name:'; 1000 Edit. Default button: 1 Button 'OK'. Tab stops in template order: 1001 Edit, 1002 Button '&Hexadecimal', 1 Button 'OK', 2 Button 'Cancel', 1000 Edit. Read-only edits: 1000 Edit. Template-disabled controls: none. | RT_DIALOG 117 | R5, U4 | open |
| DLG-036 | Hexadecimal and Decimal base as in the DWORD editor (1002, 1003). Decimal input above the QWORD maximum triggers RT_STRING 120 (truncation prompt). Default base is open (OQ-12). | RT_DIALOG 117; RT_STRING 120 | R5, B4 | open |
| DLG-037 | Layout: Resources viewer (full resource descriptor). Caption 'Resources'. Template DIALOGEX, style 0x90C801C4, size 281 x 308 DLU at (32, 24). Controls in template order (25): 1 Button 'OK'; 1204 Button '&Data...'; 1306 ColumnListBox 'Channel;Port'; 1307 ColumnListBox 'Vector;Level;Affinity;Type'; 1308 ColumnListBox 'Physical Address;Length;Access'; 1309 ColumnListBox 'Physical Address;Length;Type'; 1304 ColumnListBox 'Reserved1;Reserved2;Data Size'; no id Static 'DMA:'; no id Static 'Interrupt:'; no id Static 'Memory:'; no id Static 'Port:'; no id Static 'Device Specific Data:'; no id Button 'Share Disposition'; 1310 Static 'Undetermined'; 1311 Static 'Shared'; 1312 Static 'Device Exclusive'; 1313 Static 'Driver Exclusive'; no id Static 'Interface Type:'; no id Static 'Bus Number:'; no id Static 'Version:'; no id Static 'Revision:'; 1303 Static; 1300 Static; 1301 Static; 1302 Static. Default button: 1 Button 'OK'. Tab stops in template order: 1 Button 'OK', 1204 Button '&Data...', 1306 ColumnListBox 'Channel;Port', 1307 ColumnListBox 'Vector;Level;Affinity;Type', 1308 ColumnListBox 'Physical Address;Length;Access', 1309 ColumnListBox 'Physical Address;Length;Type', 1304 ColumnListBox 'Reserved1;Reserved2;Data Size'. Read-only edits: none. Template-disabled controls: 1204 Button '&Data...', 1310 Static 'Undetermined', 1311 Static 'Shared', 1312 Static 'Device Exclusive', 1313 Static 'Driver Exclusive'. | RT_DIALOG 1305 | R6, U4 | open |
| DLG-038 | Resource descriptor viewer: five column list boxes, a Share Disposition group, and Data... (1204). Data... and the four Share Disposition captions (1310 to 1313) are template-disabled (WS_DISABLED). Runtime rules are open (OQ-15). | RT_DIALOG 1305, controls 1204, 1310 to 1313 | R6 | open |
| DLG-039 | Layout: Requirements List viewer. Caption 'Requirements List'. Template DIALOGEX, style 0x90C801C4, size 220 x 135 DLU at (13, 15). Controls in template order (9): 1 Button 'OK'; 1506 Button 'Display...'; 1502 ColumnListBox 'Alternative List;Resource List;Descriptor;Device Type'; no id Static 'Interface Type:'; no id Static 'Bus Number:'; no id Static 'Slot Number:'; 1503 Static; 1504 Static; 1505 Static. Default button: none. Tab stops in template order: 1 Button 'OK', 1506 Button 'Display...', 1502 ColumnListBox 'Alternative List;Resource List;Descriptor;Device Type'. Read-only edits: none. Template-disabled controls: 1506 Button 'Display...'. | RT_DIALOG 1501 | R6, U4 | open |
| DLG-040 | Requirements list viewer: column list box 1502 with four columns, and the Interface Type, Bus Number and Slot Number fields. Display... (1506) is template-disabled. Runtime rule is open (OQ-15). | RT_DIALOG 1501, controls 1502, 1506 | R6 | open |
| DLG-041 | Layout: Memory requirement detail viewer. Caption 'Memory'. Template DIALOGEX, style 0x90C801C4, size 158 x 135 DLU at (6, 18). Controls in template order (16): 1 Button 'OK'; no id Button 'Option'; 2001 Static 'Preferred'; 2002 Static 'Alternative'; no id Static 'Minimum Address:'; no id Static 'Maximum Address:'; no id Static 'Share Disposition:'; no id Static 'Access:'; 1605 Static; no id Static 'Length:'; no id Static 'Alignment:'; 1602 Static; 1603 Static; 1606 Static; 1604 Static; 2000 Static. Default button: 1 Button 'OK'. Tab stops in template order: 1 Button 'OK'. Read-only edits: none. Template-disabled controls: 2001 Static 'Preferred', 2002 Static 'Alternative'. | RT_DIALOG 1601 | R6, U4 | open |
| DLG-042 | Requirement detail viewers (Memory 1601, Port 1700, Interrupt 1800, DMA 1900): static text fields only, so the dialogs are read-only. The Preferred (2001) and Alternative (2002) captions are template-disabled; the runtime rule is open (OQ-15) and the mapping to requirement types is open (OQ-16). | RT_DIALOG 1601, 1700, 1800, 1900 (controls 2001, 2002) | R6 | open |
| DLG-043 | Layout: Port requirement detail viewer. Caption 'Port'. Template DIALOGEX, style 0x90C801C4, size 158 x 135 DLU at (6, 18). Controls in template order (16): 1 Button 'OK'; no id Button 'Option'; 2001 Static 'Preferred'; 2002 Static 'Alternative'; no id Static 'Minimum Address:'; no id Static 'Maximum Address:'; no id Static 'Share Disposition:'; no id Static 'Type:'; 1705 Static; no id Static 'Length:'; no id Static 'Alignment:'; 1701 Static; 1703 Static; 1706 Static; 1704 Static; 2000 Static. Default button: 1 Button 'OK'. Tab stops in template order: 1 Button 'OK'. Read-only edits: none. Template-disabled controls: 2001 Static 'Preferred', 2002 Static 'Alternative'. | RT_DIALOG 1700 | R6, U4 | open |
| DLG-044 | Layout: Column List Box Styles (developer dialog). Caption 'Column List Box Styles'. Template DIALOGEX, style 0x90C800C0, size 170 x 145 DLU at (141, 63). Controls in template order (18): no id Button 'Column List Box Styles'; 1760 Button '&Standard'; 1763 Button '&Border'; 1755 Button 'S&ort'; 1756 Button 'Notif&y'; 1757 Button 'V&ert. Scroll Bar'; no id Button '&Multiple Selection'; no id Button 'E&xtended Selection'; 1764 Button '&Popout Headings'; 1765 Button '&Springy Columns'; no id Button 'Basic Styles'; 1751 Button '&Visible'; 1752 Button '&Disabled'; 1753 Button '&Group'; 1754 Button '&Tab Stop'; 1 Button 'OK'; 2 Button 'Cancel'; 1758 Button '&Disable No-Scroll'. Default button: 1 Button 'OK'. Tab stops in template order: 1760 Button '&Standard', 1763 Button '&Border', 1755 Button 'S&ort', 1756 Button 'Notif&y', 1757 Button 'V&ert. Scroll Bar', no id Button '&Multiple Selection', no id Button 'E&xtended Selection', 1764 Button '&Popout Headings', 1765 Button '&Springy Columns', 1751 Button '&Visible', 1752 Button '&Disabled', 1753 Button '&Group', 1754 Button '&Tab Stop', 1 Button 'OK', 2 Button 'Cancel', 1758 Button '&Disable No-Scroll'. Read-only edits: none. Template-disabled controls: no id Button '&Multiple Selection', no id Button 'E&xtended Selection'. | RT_DIALOG 1750 | U7, U4 | open |
| DLG-045 | Developer dialog: checkboxes for list box style bits (Standard, Border, Sort, Notify, Vert. Scroll Bar, Popout Headings, Springy Columns, Visible, Disabled, Group, Tab Stop, Disable No-Scroll). The Multiple Selection and Extended Selection captions are template-disabled. Reachability is open (OQ-17). | RT_DIALOG 1750 | U7, U8 | open |
| DLG-046 | Layout: Interrupt requirement detail viewer. Caption 'Interrupt'. Template DIALOGEX, style 0x90C801C4, size 158 x 135 DLU at (6, 18). Controls in template order (12): 1 Button 'OK'; no id Button 'Option'; 2001 Static 'Preferred'; 2002 Static 'Alternative'; no id Static 'Maximum Vector:'; no id Static 'Share Disposition:'; no id Static 'Type:'; no id Static 'Minimum Vector:'; 1801 Static; 1803 Static; 1804 Static; 2000 Static. Default button: 1 Button 'OK'. Tab stops in template order: 1 Button 'OK'. Read-only edits: none. Template-disabled controls: 2001 Static 'Preferred', 2002 Static 'Alternative'. | RT_DIALOG 1800 | R6, U4 | open |
| DLG-047 | Layout: DMA requirement detail viewer. Caption 'DMA'. Template DIALOGEX, style 0x90C801C4, size 158 x 135 DLU at (6, 18). Controls in template order (10): 1 Button 'OK'; no id Button 'Option'; 2001 Static 'Preferred'; 2002 Static 'Alternative'; no id Static 'Minimum Channel:'; no id Static 'Share Disposition:'; no id Static 'Maximum Channel:'; 1902 Static; 1903 Static; 2000 Static. Default button: 1 Button 'OK'. Tab stops in template order: 1 Button 'OK'. Read-only edits: none. Template-disabled controls: 2001 Static 'Preferred', 2002 Static 'Alternative'. | RT_DIALOG 1900 | R6, U4 | open |
| DLG-048 | Layout: Binary Data viewer (variant A). Caption 'Binary Data'. Template DIALOGEX, style 0x90C801C4, size 440 x 161 DLU at (5, 18). Controls in template order (8): 502 Static 'D&ata:'; 2022 ListBox; 503 Button 'Format'; 2023 Button '&Byte'; 2024 Button '&Word'; 2025 Button '&Dword'; 2027 Button '&Qword'; 1 Button 'OK'. Default button: 1 Button 'OK'. Tab stops in template order: 2022 ListBox, 1 Button 'OK'. Read-only edits: none. Template-disabled controls: none. | RT_DIALOG 2020 | R6, U4 | open |
| DLG-049 | Format radios Byte (2023), Word (2024), Dword (2025) and Qword (2027) set the unit shown in list box 2022 (single selection, notify, not sorted; style 0x50A10001). The default unit and the list text per unit are not in the dump (OQ-14). | RT_DIALOG 2020, controls 2022, 2023 to 2027 | R6, B4 | open |
| DLG-050 | Layout: Binary Data viewer (variant B, with Value Type). Caption 'Binary Data'. Template DIALOGEX, style 0x90C801C4, size 440 x 161 DLU at (5, 18). Controls in template order (10): 502 Static 'D&ata:'; 2022 ListBox; 503 Button 'Format'; 2023 Button '&Byte'; 2024 Button '&Word'; 2025 Button '&Dword'; 2027 Button '&Qword'; 1 Button 'OK'; no id Static 'Value Type:'; 2026 Static. Default button: 1 Button 'OK'. Tab stops in template order: 2022 ListBox, 1 Button 'OK'. Read-only edits: none. Template-disabled controls: none. | RT_DIALOG 2021 | R6, U4 | open |
| DLG-051 | Variant with a Value Type: label (static 65535) and a type text field (2026). Which command opens variant A (2020) and which opens variant B (2021) is not in the dump (OQ-14). | RT_DIALOG 2021, controls 2026 and 65535 | R6 | open |

### 2.4 ACC: accelerators and keyboard (RT_ACCELERATOR 100)

| ID | Feature | Oracle source | Package | Status |
| --- | --- | --- | --- | --- |
| ACC-001 | Tab: move focus between the tree and the value list (pane switch; command 640 has no menu item or string in the dump; brief: Tab between panes). | RT_ACCELERATOR 100, entry 1 (key VK_TAB, flags 0x0003, command 640) | R12, U5 | open |
| ACC-002 | Shift+Tab: reverse pane switch (command 641; no menu item or string in the dump; expected reverse of Tab, verify; OQ-04). | RT_ACCELERATOR 100, entry 2 (key VK_TAB, flags 0x0007, command 641) | R12, U5 | open |
| ACC-003 | F2: Rename the selected key or value (command 914; Edit > Rename). | RT_ACCELERATOR 100, entry 3 (key VK_F2, flags 0x0003, command 914) | R12, R4 | open |
| ACC-004 | F3: Find Next (command 671; Edit > Find Next). | RT_ACCELERATOR 100, entry 4 (key VK_F3, flags 0x0003, command 671) | R12, R9 | open |
| ACC-005 | F4: command 642; no menu item or string in the dump (OQ-04). | RT_ACCELERATOR 100, entry 5 (key VK_F4, flags 0x0003, command 642) | R12 | open |
| ACC-006 | F5: Refresh the view (command 648; View > Refresh); the only refresh trigger (BEH-007). | RT_ACCELERATOR 100, entry 6 (key VK_F5, flags 0x0003, command 648) | R12, R2 | open |
| ACC-007 | F6: same command as Tab (640); pane switch (verify; OQ-04). | RT_ACCELERATOR 100, entry 7 (key VK_F6, flags 0x0003, command 640) | R12, U5 | open |
| ACC-008 | Shift+F10: command 896; no menu item or string in the dump; purpose not confirmed (OQ-04). | RT_ACCELERATOR 100, entry 8 (key VK_F10, flags 0x0007, command 896) | R12 | open |
| ACC-009 | Delete: Delete the selection (command 913; Edit > Delete). | RT_ACCELERATOR 100, entry 9 (key VK_DELETE, flags 0x0003, command 913) | R12, R4 | open |
| ACC-010 | Ctrl+F: Find (command 662; Edit > Find). | RT_ACCELERATOR 100, entry 10 (key ASCII 0x06 (Ctrl+F), flags 0x0002, command 662) | R12, R9 | open |
| ACC-011 | Ctrl+P: Print (command 660; File > Print). | RT_ACCELERATOR 100, entry 11 (key ASCII 0x10 (Ctrl+P), flags 0x0002, command 660) | R12, R14 | open |
| ACC-012 | Ctrl+T: Go to HKEY_LOCAL_MACHINE (command 918; the tree key menu item 918, which is not on the main menu). | RT_ACCELERATOR 100, entry 12 (key ASCII 0x14 (Ctrl+T), flags 0x0002, command 918) | R12, R2 | open |
| ACC-013 | Alt+D: command 672; no menu item or string in the dump; expected address bar focus (not verified; OQ-04). | RT_ACCELERATOR 100, entry 13 (key 'D', flags 0x0011, command 672) | R12 | open |
| ACC-014 | Ctrl+L: command 672, same as Alt+D (not verified; OQ-04). | RT_ACCELERATOR 100, entry 14 (key ASCII 0x0C (Ctrl+L), flags 0x0002, command 672) | R12 | open |
| ACC-015 | Alt+1: command 689; no menu item or string in the dump; purpose unknown (OQ-04). | RT_ACCELERATOR 100, entry 15 (key '1', flags 0x0011, command 689) | R12 | open |
| ACC-016 | Alt+2: command 690; no menu item or string in the dump; purpose unknown (OQ-04). | RT_ACCELERATOR 100, entry 16 (key '2', flags 0x0011, command 690) | R12 | open |
| ACC-017 | Alt+3: command 691; no menu item or string in the dump; purpose unknown (OQ-04). | RT_ACCELERATOR 100, entry 17 (key '3', flags 0x0011, command 691) | R12 | open |
| ACC-018 | Alt+4: command 692; no menu item or string in the dump; purpose unknown (OQ-04). | RT_ACCELERATOR 100, entry 18 (key '4', flags 0x0011, command 692) | R12 | open |
| ACC-019 | Alt+5: command 693; no menu item or string in the dump; purpose unknown (OQ-04). | RT_ACCELERATOR 100, entry 19 (key '5', flags 0x0011, command 693) | R12 | open |
| ACC-020 | Alt+6: command 694; no menu item or string in the dump; purpose unknown (OQ-04). | RT_ACCELERATOR 100, entry 20 (key '6', flags 0x0011, command 694) | R12 | open |
| ACC-021 | Alt+7: command 695; no menu item or string in the dump; purpose unknown (OQ-04). | RT_ACCELERATOR 100, entry 21 (key '7', flags 0x0011, command 695) | R12 | open |
| ACC-022 | Alt+8: command 696; no menu item or string in the dump; purpose unknown (OQ-04). | RT_ACCELERATOR 100, entry 22 (key '8', flags 0x0011, command 696) | R12 | open |
| ACC-023 | Alt+9: command 697; no menu item or string in the dump; purpose unknown (OQ-04). | RT_ACCELERATOR 100, entry 23 (key '9', flags 0x0011, command 697) | R12 | open |
| ACC-024 | Alt+Up: command 698; no menu item or string in the dump; purpose unknown (OQ-04). | RT_ACCELERATOR 100, entry 24 (key VK_UP, flags 0x0091, command 698) | R12 | open |
| ACC-025 | Backspace: move the selection to the parent key (brief; not an accelerator table entry). | Brief (not in the accelerator table) | R12, U5 | open |
| ACC-026 | Numpad * (multiply): expand all descendants of the selected key (brief; not an accelerator table entry). | Brief (not in the accelerator table) | U5, R12 | open |
| ACC-027 | Type-ahead in the tree: typing selects the next key whose name starts with the typed text (brief; exact timing and wrap rules are verify items). | Brief (not in the accelerator table) | U5 | open |
| ACC-028 | Type-ahead in the value list: typing selects the next value whose name starts with the typed text (brief; exact rules are verify items). | Brief (not in the accelerator table) | U7 | open |

### 2.5 MSG: strings (RT_STRING)

Skipped (format fragments with no user text): 8100 (" : " joiner) and 8111 (a 19-space pad). Kept as display templates: 23 and 24 (New Key and New Value counter, %%u), 29 and 32 (DWORD and QWORD display formats).

| ID | Feature | Oracle source | Package | Status |
| --- | --- | --- | --- | --- |
| MSG-001 | Registry Editor | RT_STRING 16 | R1, U2 | open |
| MSG-002 | Name | RT_STRING 17 | U7 | open |
| MSG-003 | Data | RT_STRING 18 | U7 | open |
| MSG-004 | Computer | RT_STRING 19 | U5 | open |
| MSG-005 | (Default) | RT_STRING 20 | B4, U7 | open |
| MSG-006 | Modify &Binary Data... | RT_STRING 21 | U3, R6 | open |
| MSG-007 | (zero-length binary value) | RT_STRING 22 | B4 | open |
| MSG-008 | New Key #%%u | RT_STRING 23 | R3, B3 | open |
| MSG-009 | New Value #%%u | RT_STRING 24 | R3, B3 | open |
| MSG-010 | Collapse | RT_STRING 25 | U5, R12 | open |
| MSG-011 | &Modify... | RT_STRING 26 | U3, R5 | open |
| MSG-012 | (value not set) | RT_STRING 27 | B4 | open |
| MSG-013 | 0x%1!08x! (%1!u!) | RT_STRING 29 | B4 | open |
| MSG-014 | (invalid DWORD (32-bit) value) | RT_STRING 30 | B4 | open |
| MSG-015 | Type | RT_STRING 31 | U7 | open |
| MSG-016 | 0x%1!08I64x! (%1!I64u!) | RT_STRING 32 | B4 | open |
| MSG-017 | (invalid QWORD (64-bit) value) | RT_STRING 33 | B4 | open |
| MSG-018 | My Computer | RT_STRING 34 | U5 | open |
| MSG-019 | Registry editing has been disabled by your administrator. | RT_STRING 40 | R1, B8 | open |
| MSG-020 | Finished searching through the registry. | RT_STRING 41 | R9, B7 | open |
| MSG-021 | Click the computer you want to connect to. | RT_STRING 42 | R8, U4 | open |
| MSG-022 | Command line argument requires a filename and none was specified. | RT_STRING 43 | R13 | open |
| MSG-023 | Are you sure you want to permanently delete this key and all of its subkeys? | RT_STRING 48 | R4, B3 | open |
| MSG-024 | Confirm Key Delete | RT_STRING 49 | R4 | open |
| MSG-025 | Deleting certain registry values could cause system instability. Are you sure you want to permanently delete these values? | RT_STRING 50 | R4, B3 | open |
| MSG-026 | Confirm Value Delete | RT_STRING 51 | R4 | open |
| MSG-027 | Deleting certain registry values could cause system instability. Are you sure you want to permanently delete this value? | RT_STRING 52 | R4, B3 | open |
| MSG-028 | Are you sure you want to unload the current key and all of its subkeys? | RT_STRING 53 | R8, B8 | open |
| MSG-029 | Confirm Unload Hive | RT_STRING 54 | R8, B8 | open |
| MSG-030 | The key will be restored on top of key: %1.\r\nAll value entries and subkeys of this key will be deleted.\r\nDo you want to continue the operation? | RT_STRING 55 | R8, B3 | open |
| MSG-031 | Confirm Restore Key | RT_STRING 56 | R8, B3 | open |
| MSG-032 | Error Renaming Key | RT_STRING 64 | R4, B3 | open |
| MSG-033 | The Registry Editor cannot rename %1. Error while renaming key. | RT_STRING 66 | R4, B3 | open |
| MSG-034 | The Registry Editor cannot rename %1. The specified key name is too long. Type a shorter name and try again. | RT_STRING 67 | R4, B3 | open |
| MSG-035 | The Registry Editor cannot rename %1. The specified key name already exists. Type another name and try again. | RT_STRING 68 | R4, B3 | open |
| MSG-036 | The Registry Editor cannot rename %1. Specify a key name without a backslash (\). | RT_STRING 69 | R4, B3 | open |
| MSG-037 | The Registry Editor cannot rename %1. The specified key name is empty. Type another name and try again. | RT_STRING 70 | R4, B3 | open |
| MSG-038 | Error Renaming Value | RT_STRING 72 | R4, B3 | open |
| MSG-039 | The Registry Editor cannot rename %1. Error while renaming value. | RT_STRING 73 | R4, B3 | open |
| MSG-040 | The Registry Editor cannot rename %1. The specified value name already exists. Type another name and try again. | RT_STRING 74 | R4, B3 | open |
| MSG-041 | The Registry Editor cannot rename %1. The specified value name is empty. Type another name and try again. | RT_STRING 75 | R4, B3 | open |
| MSG-042 | Error Deleting Key | RT_STRING 80 | R4, B3 | open |
| MSG-043 | Cannot delete %1:  Error while deleting key. | RT_STRING 82 | R4, B3 | open |
| MSG-044 | Error Deleting Values | RT_STRING 88 | R4, B3 | open |
| MSG-045 | Unable to delete all specified values. | RT_STRING 89 | R4, B3 | open |
| MSG-046 | Error Opening Key | RT_STRING 96 | R2, B2 | open |
| MSG-047 | %1 cannot be opened.\nAn error is preventing this key from being opened. | RT_STRING 97 | R2, B2 | open |
| MSG-048 | %1 cannot be opened.\nAn error is preventing this key from being opened.\nDetails: %2 | RT_STRING 98 | R2, B2 | open |
| MSG-049 | Error Displaying Value | RT_STRING 100 | U8, B2 | open |
| MSG-050 | Cannot display %1:  Error reading the value's contents. | RT_STRING 101 | U8, B2 | open |
| MSG-051 | Cannot display value: Insufficient memory. Try closing down some applications, and try again.\r\n If you still see this message, try restarting Windows. | RT_STRING 102 | U8, B2 | open |
| MSG-052 | Overflow | RT_STRING 110 | R5, U4 | open |
| MSG-053 | Warning | RT_STRING 111 | R5, U4 | open |
| MSG-054 | Error Editing Value | RT_STRING 112 | R5, U4 | open |
| MSG-055 | Cannot edit %1:  Error reading the value's contents. | RT_STRING 114 | R5, B2 | open |
| MSG-056 | Cannot edit %1:  Error writing the value's new contents. | RT_STRING 115 | R5, B3 | open |
| MSG-057 | Data of type REG_MULTI_SZ cannot contain empty strings.\r\nRegistry Editor will remove the empty string found. | RT_STRING 116 | R6, B4 | open |
| MSG-058 | Data of type REG_MULTI_SZ cannot contain empty strings.\r\nRegistry Editor will remove all empty strings found. | RT_STRING 117 | R6, B4 | open |
| MSG-059 | Cannot edit: Insufficient memory. Try closing down some applications, and try again.\r\nIf you still see this message, try restarting Windows. | RT_STRING 118 | R5, U4 | open |
| MSG-060 | The decimal value entered is greater than the maximum value of a DWORD (32-bit number).\r\nShould the value be truncated in order to continue? | RT_STRING 119 | R5, B4 | open |
| MSG-061 | The decimal value entered is greater than the maximum value of a QWORD (64-bit number).\r\nShould the value be truncated in order to continue? | RT_STRING 120 | R5, B4 | open |
| MSG-062 | Cannot import %1: The specified file is not a registry script.\r\nYou can only import binary registry files from within the registry editor. | RT_STRING 123 | R7, B6 | open |
| MSG-063 | Segoe UI | RT_STRING 124 | U1 | open |
| MSG-064 | Cannot import %1: The key selected is invalid. | RT_STRING 125 | R7, B6 | open |
| MSG-065 | Cannot import %1: Insufficient privileges. | RT_STRING 126 | R7, B8 | open |
| MSG-066 | The keys and values contained in %1 have been successfully added to the registry on %2. | RT_STRING 128 | R7, B6 | open |
| MSG-067 | Cannot import %1: Error opening the file. There may be a disk or file system error. | RT_STRING 129 | R7, B6 | open |
| MSG-068 | Cannot import %1: Error reading the file. There may be a disk error or the file may be corrupt. | RT_STRING 130 | R7, B6 | open |
| MSG-069 | Cannot import %1: Error accessing the registry on %2. | RT_STRING 131 | R7, B9 | open |
| MSG-070 | Cannot import %1: Not all data was successfully written to the registry.  Some keys are open by the system or other processes, or you have insufficient privileges to perform this operation. | RT_STRING 132 | R7, B6 | open |
| MSG-071 | Cannot import %1: The specified file is not a registry file. You can import only registry files. | RT_STRING 133 | R7, B6 | open |
| MSG-072 | Cannot import %1: The specified file is not intended for use with this version of Windows. | RT_STRING 134 | R7, B6 | open |
| MSG-073 | Cannot import %1: The file specified does not exist on %2. | RT_STRING 135 | R7, B9 | open |
| MSG-074 | Cannot export %1: Error opening the file. There may be a disk or file system error. | RT_STRING 138 | R7, B5 | open |
| MSG-075 | Cannot export %1: Error writing the file. There may be a disk or file system error. | RT_STRING 141 | R7, B5 | open |
| MSG-076 | Cannot export %1: Insufficient privileges. | RT_STRING 142 | R7, B8 | open |
| MSG-077 | Cannot export %1: The key selected is invalid. | RT_STRING 143 | R7, B2 | open |
| MSG-078 | Cannot print: Insufficient memory to begin job. Try closing down some applications, and try again. If you still see this message, try restarting Windows. | RT_STRING 144 | R14, U4 | open |
| MSG-079 | Cannot print: An error occurred during printing. Check your printer and your printer's settings for problems, and try again. | RT_STRING 145 | R14, U4 | open |
| MSG-080 | Cannot print:  Error reading a registry value's contents. | RT_STRING 146 | R14, B2 | open |
| MSG-081 | The selected branch does not exist. Make sure that the correct path is given. | RT_STRING 148 | R7, U11 | open |
| MSG-082 | Error Connecting Network Registry | RT_STRING 152 | R8, B9 | open |
| MSG-083 | You cannot connect to your own computer. | RT_STRING 153 | R8, B9 | open |
| MSG-084 | Unable to connect to %1.  Make sure that this computer is on the network, has remote administration enabled, and that both computers are running the remote registry service. | RT_STRING 154 | R8, B9 | open |
| MSG-085 | Unable to connect to all of the roots of the computer's registry.  Disconnect from the remote registry and then reconnect before trying again. | RT_STRING 155 | R8, B9 | open |
| MSG-086 | Unable to connect to %1.  Make sure you have permission to administer this computer. | RT_STRING 156 | R8, B9 | open |
| MSG-087 | Error Creating Key | RT_STRING 160 | R3, B3 | open |
| MSG-088 | Cannot create key: Error while opening the key %1. | RT_STRING 161 | R3, B3 | open |
| MSG-089 | Cannot create key: Error writing to the registry. | RT_STRING 162 | R3, B3 | open |
| MSG-090 | Cannot create key: Unable to generate a unique name. | RT_STRING 163 | R3, B3 | open |
| MSG-091 | Cannot create key: You do not have the requisite permissions to create a new key under %1. | RT_STRING 164 | R3, B3 | open |
| MSG-092 | Error Creating Value | RT_STRING 168 | R3, B3 | open |
| MSG-093 | Cannot create value: Error writing to the registry. | RT_STRING 169 | R3, B3 | open |
| MSG-094 | Cannot create value: Unable to generate a unique name. | RT_STRING 170 | R3, B3 | open |
| MSG-095 | There is already a favorite with that name. | RT_STRING 171 | R10 | open |
| MSG-096 | Error Adding Favorite | RT_STRING 172 | R10 | open |
| MSG-097 | Favorite | RT_STRING 173 | R10 | open |
| MSG-098 | All Files#*.*# | RT_STRING 180 | R7 | open |
| MSG-099 | Load Hive | RT_STRING 181 | R8 | open |
| MSG-100 | Cannot Load %1: Insufficient privileges. | RT_STRING 182 | R8, B8 | open |
| MSG-101 | Cannot Load %1: Error while loading hive. | RT_STRING 183 | R8, B8 | open |
| MSG-102 | Cannot Unload %1: Insufficient privileges. | RT_STRING 184 | R8, B8 | open |
| MSG-103 | Cannot Unload %1: Error while unloading hive. | RT_STRING 185 | R8, B8 | open |
| MSG-104 | Unload Hive | RT_STRING 186 | R8 | open |
| MSG-105 | Cannot Unload %1: Access is denied. | RT_STRING 187 | R8, B8 | open |
| MSG-106 | Cannot Load %1: The process cannot access the file because it is being used by another process. | RT_STRING 188 | R8, B8 | open |
| MSG-107 | Cannot Load %1: Access is denied. | RT_STRING 189 | R8, B8 | open |
| MSG-108 | Cannot save subtree: Insufficient memory. Try closing down some applications, and try again. If you still see this message, try restarting Windows. | RT_STRING 212 | R7, B5 | open |
| MSG-109 | Cannot save subtree:  Error reading a registry value's contents. | RT_STRING 213 | R7, B5 | open |
| MSG-110 | Cannot save subtree to %1: Error writing the file. There may be a disk or file system error. | RT_STRING 214 | R7, B5 | open |
| MSG-111 | Cannot save subtree to %1: Error opening the file. There may be a disk or file system error. | RT_STRING 215 | R7, B5 | open |
| MSG-112 | The keys and values contained in %1 have been successfully added to the registry. | RT_STRING 228 | R7, B6 | open |
| MSG-113 | Cannot import %1: Error accessing the registry. | RT_STRING 231 | R7, B6 | open |
| MSG-114 | Cannot import %1: The file specified does not exist. | RT_STRING 235 | R7, B6 | open |
| MSG-115 | Import Registry File | RT_STRING 300 | R7 | open |
| MSG-116 | Export Registry File | RT_STRING 301 | R7 | open |
| MSG-117 | Registration Files (*.reg)#*.reg#Registry Hive Files (*.*)#*.*#All Files#*.*# | RT_STRING 302 | R7 | open |
| MSG-118 | REG | RT_STRING 303 | R7 | open |
| MSG-119 | Adding information can unintentionally change or delete values and cause components to stop working correctly. If you do not trust the source of this information in %1, do not add it to the registry.\r\n\n Are you sure you want to continue? | RT_STRING 304 | R7, B6 | open |
| MSG-120 | Registration Files (*.reg)#*.reg#Registry Hive Files (*.*)#*.#Text Files (*.txt)#*.txt#Win9x/NT4 Registration Files (*.reg)#*.reg#All Files#*.*# | RT_STRING 305 | R7, B11 | open |
| MSG-121 | Insufficient memory. Try closing down some applications, and try again.\r\n If you still see this message, try restarting Windows. | RT_STRING 306 | R7, B6 | open |
| MSG-122 | Registration Entries | RT_STRING 309 | R13, R7 | open |
| MSG-123 | Mer&ge | RT_STRING 310 | R13, R7 | open |
| MSG-124 | Go &to HKEY_CURRENT_USER | RT_STRING 311 | R12 | open |
| MSG-125 | Contains commands for working with the whole registry. | RT_STRING 512 | U3 | open |
| MSG-126 | Contains commands for editing values or keys. | RT_STRING 513 | U3 | open |
| MSG-127 | Contains commands for customizing the registry window. | RT_STRING 514 | U3 | open |
| MSG-128 | Contains commands for displaying Help for and information about Registry Editor. | RT_STRING 515 | U3 | open |
| MSG-129 | Contains commands for creating new keys or values. | RT_STRING 516 | U3, R3 | open |
| MSG-130 | Contains commands for accessing frequently used keys. | RT_STRING 517 | U3, R10 | open |
| MSG-131 | Refreshes the window. | RT_STRING 648 | U3, R2 | open |
| MSG-132 | Connects to a remote computer's registry. | RT_STRING 657 | U3, R8 | open |
| MSG-133 | Imports a file into the registry. | RT_STRING 658 | U3, R7 | open |
| MSG-134 | Exports all or part of the registry to a file. | RT_STRING 659 | U3, R7 | open |
| MSG-135 | Prints all or part of the registry. | RT_STRING 660 | U3, R14 | open |
| MSG-136 | Quits the Registry Editor. | RT_STRING 661 | U3, R1 | open |
| MSG-137 | Finds a text string in a key, value, or data. | RT_STRING 662 | U3, R9 | open |
| MSG-138 | Adds a new key. | RT_STRING 663 | U3, R3 | open |
| MSG-139 | Adds a new string value. | RT_STRING 664 | U3, R3 | open |
| MSG-140 | Adds a new binary value. | RT_STRING 665 | U3, R3 | open |
| MSG-141 | Displays program information, version number, and copyright. | RT_STRING 667 | U3, U4 | open |
| MSG-142 | Shows or hides the address bar. | RT_STRING 668 | U3, U11 | open |
| MSG-143 | Changes position of split between two panes. | RT_STRING 670 | U3, U2 | open |
| MSG-144 | Finds the next occurrence of text specified in previous search. | RT_STRING 671 | U3, R9 | open |
| MSG-145 | Adds a new 32 bit value. | RT_STRING 674 | U3, R3 | open |
| MSG-146 | Copies the name of the selected key to the Clipboard. | RT_STRING 675 | U3, R12 | open |
| MSG-147 | Adds a new multi-string value. | RT_STRING 676 | U3, R3 | open |
| MSG-148 | Adds a new expandable string value. | RT_STRING 677 | U3, R3 | open |
| MSG-149 | Displays the permissions for a key. | RT_STRING 678 | U3, R11 | open |
| MSG-150 | Displays a value's data in binary form. | RT_STRING 679 | U3, R6 | open |
| MSG-151 | Loads a hive file to the registry. | RT_STRING 680 | U3, R8 | open |
| MSG-152 | Unloads a hive from the registry. | RT_STRING 681 | U3, R8 | open |
| MSG-153 | Adds a new 64 bit value. | RT_STRING 688 | U3, R3 | open |
| MSG-154 | Disconnects from a remote computer's registry. | RT_STRING 768 | U3, R8 | open |
| MSG-155 | Modifies the value's data. | RT_STRING 912 | U3, R5 | open |
| MSG-156 | Deletes the selection. | RT_STRING 913 | U3, R4 | open |
| MSG-157 | Renames the selection. | RT_STRING 914 | U3, R4 | open |
| MSG-158 | Expands or collapses the branch. | RT_STRING 915 | U3, U5 | open |
| MSG-159 | Edit the value's data in binary form. | RT_STRING 917 | U3, R6 | open |
| MSG-160 | Removes keys from the Favorites list. | RT_STRING 1280 | U3, R10 | open |
| MSG-161 | Adds keys to the Favorites list. | RT_STRING 1281 | U3, R10 | open |
| MSG-162 | Create Link | RT_STRING 7001 | R11, B10 | open |
| MSG-163 | Query Value | RT_STRING 7002 | R11, B10 | open |
| MSG-164 | Set Value | RT_STRING 7003 | R11, B10 | open |
| MSG-165 | Enumerate Subkeys | RT_STRING 7004 | R11, B10 | open |
| MSG-166 | Notify | RT_STRING 7005 | R11, B10 | open |
| MSG-167 | Create Subkey | RT_STRING 7006 | R11, B10 | open |
| MSG-168 | Delete | RT_STRING 7007 | R11, B10 | open |
| MSG-169 | Write DAC | RT_STRING 7008 | R11, B10 | open |
| MSG-170 | Write Owner | RT_STRING 7009 | R11, B10 | open |
| MSG-171 | Read Control | RT_STRING 7010 | R11, B10 | open |
| MSG-172 | Read | RT_STRING 7011 | R11, B10 | open |
| MSG-173 | Full Control | RT_STRING 7012 | R11, B10 | open |
| MSG-174 | Special Access... | RT_STRING 7013 | R11, B10 | open |
| MSG-175 | Registry &Key | RT_STRING 7014 | R11 | open |
| MSG-176 | R&eplace Permission on Existing Subkeys | RT_STRING 7015 | R11, B10 | open |
| MSG-177 | Audit Permission on Existing Subkeys | RT_STRING 7016 | R11, B10 | open |
| MSG-178 | Do you want to replace the permission on all existing subkeys within %1? | RT_STRING 7017 | R11, B10 | open |
| MSG-179 | Do you want to audit all existing subkeys within %1? | RT_STRING 7018 | R11, B10 | open |
| MSG-180 | Read | RT_STRING 7019 | R11, B10 | open |
| MSG-181 | Registry Editor could not retrieve the security information. \nThe key currently selected does not give you access to retrieve such information. | RT_STRING 7050 | R11, B10 | open |
| MSG-182 | Registry Editor could not retrieve the security information. \nThe key currently selected is marked for deletion. | RT_STRING 7051 | R11, B10 | open |
| MSG-183 | Registry Editor could not retrieve the security information. \nThe key currently selected is not accessible. | RT_STRING 7052 | R11, B10 | open |
| MSG-184 | Registry Editor could not save the security information. \nThe key currently selected does not give you access to save such information. | RT_STRING 7070 | R11, B10 | open |
| MSG-185 | Registry Editor could not save the security information. \nThe key currently selected is marked for deletion. | RT_STRING 7071 | R11, B10 | open |
| MSG-186 | Registry Editor could not set security in the key currently selected, or some of its subkeys.  | RT_STRING 7072 | R11, B10 | open |
| MSG-187 | Registry Editor could not set owner on the key currently selected, or some of its subkeys.  | RT_STRING 7073 | R11, B10 | open |
| MSG-188 | Registry Editor could not set security in the key currently selected, or some of its subkeys. \nThese keys do not give you access to change security information. | RT_STRING 7074 | R11, B10 | open |
| MSG-189 | Registry Editor could not set security in all subkeys. \nThe key currently selected contains one or more subkeys marked for deletion. | RT_STRING 7075 | R11, B10 | open |
| MSG-190 | Registry Editor could not set security in all subkeys. \nThe key currently selected contains one or more inaccessible subkeys. | RT_STRING 7076 | R11, B10 | open |
| MSG-191 | This key only | RT_STRING 7090 | R11, B10 | open |
| MSG-192 | This key and subkeys | RT_STRING 7091 | R11, B10 | open |
| MSG-193 | Subkeys only | RT_STRING 7092 | R11, B10 | open |
| MSG-194 | Internal | RT_STRING 7500 | R6, B4 | open |
| MSG-195 | Isa | RT_STRING 7501 | R6, B4 | open |
| MSG-196 | Eisa | RT_STRING 7502 | R6, B4 | open |
| MSG-197 | Micro Channel | RT_STRING 7503 | R6, B4 | open |
| MSG-198 | Turbo Channel | RT_STRING 7504 | R6, B4 | open |
| MSG-199 | PCI | RT_STRING 7505 | R6, B4 | open |
| MSG-200 | VME | RT_STRING 7506 | R6, B4 | open |
| MSG-201 | Nu | RT_STRING 7507 | R6, B4 | open |
| MSG-202 | PCMCIA | RT_STRING 7508 | R6, B4 | open |
| MSG-203 | C Bus | RT_STRING 7509 | R6, B4 | open |
| MSG-204 | MPI | RT_STRING 7510 | R6, B4 | open |
| MSG-205 | MPSA | RT_STRING 7511 | R6, B4 | open |
| MSG-206 | Level Sensitive | RT_STRING 7520 | R6, B4 | open |
| MSG-207 | Latched | RT_STRING 7521 | R6, B4 | open |
| MSG-208 | Read / Write | RT_STRING 7530 | R6, B4 | open |
| MSG-209 | Read Only | RT_STRING 7531 | R6, B4 | open |
| MSG-210 | Write Only | RT_STRING 7532 | R6, B4 | open |
| MSG-211 | Memory | RT_STRING 7540 | R6, B4 | open |
| MSG-212 | Port | RT_STRING 7541 | R6, B4 | open |
| MSG-213 | Invalid | RT_STRING 7542 | R6, B4 | open |
| MSG-214 | Port | RT_STRING 7560 | R6, B4 | open |
| MSG-215 | Interrupt | RT_STRING 7561 | R6, B4 | open |
| MSG-216 | Memory | RT_STRING 7562 | R6, B4 | open |
| MSG-217 | DMA | RT_STRING 7563 | R6, B4 | open |
| MSG-218 | Device Specific | RT_STRING 7564 | R6, B4 | open |
| MSG-219 | Undetermined | RT_STRING 7580 | R6, B4 | open |
| MSG-220 | Device Exclusive | RT_STRING 7581 | R6, B4 | open |
| MSG-221 | Driver Exclusive | RT_STRING 7582 | R6, B4 | open |
| MSG-222 | Shared | RT_STRING 7583 | R6, B4 | open |
| MSG-223 | REG_NONE | RT_STRING 8000 | B4, U7 | open |
| MSG-224 | REG_SZ | RT_STRING 8001 | B4, U7 | open |
| MSG-225 | REG_EXPAND_SZ | RT_STRING 8002 | B4, U7 | open |
| MSG-226 | REG_BINARY | RT_STRING 8003 | B4, U7 | open |
| MSG-227 | REG_DWORD | RT_STRING 8004 | B4, U7 | open |
| MSG-228 | REG_LINK | RT_STRING 8006 | B4, U7 | open |
| MSG-229 | REG_MULTI_SZ | RT_STRING 8007 | B4, U7 | open |
| MSG-230 | REG_RESOURCE_LIST | RT_STRING 8008 | B4, U7 | open |
| MSG-231 | REG_FULL_RESOURCE_DESCRIPTOR | RT_STRING 8009 | B4, U7 | open |
| MSG-232 | REG_RESOURCE_REQUIREMENTS_LIST | RT_STRING 8010 | B4, U7 | open |
| MSG-233 | REG_QWORD | RT_STRING 8011 | B4, U7 | open |
| MSG-234 | REG_UNKNOWN | RT_STRING 8012 | B4, U7 | open |
| MSG-235 | Key Name:           | RT_STRING 8101 | B11 | open |
| MSG-236 | Class Name:         | RT_STRING 8102 | B11 | open |
| MSG-237 | Last Write Time:    | RT_STRING 8103 | B11 | open |
| MSG-238 | Value  | RT_STRING 8104 | B11 | open |
| MSG-239 |   Name:             | RT_STRING 8105 | B11 | open |
| MSG-240 |   Type:             | RT_STRING 8106 | B11 | open |
| MSG-241 |   Data Size:        | RT_STRING 8107 | B11 | open |
| MSG-242 |   Data:             | RT_STRING 8108 | B11 | open |
| MSG-243 | <NO NAME> | RT_STRING 8109 | B11 | open |
| MSG-244 | <NO CLASS> | RT_STRING 8110 | B11 | open |
| MSG-245 | Full Resource Descriptor  | RT_STRING 8150 | R6, B11 | open |
| MSG-246 |   Partial Descriptor  | RT_STRING 8151 | R6, B11 | open |
| MSG-247 |   Interface Type:     | RT_STRING 8152 | R6, B11 | open |
| MSG-248 |   Bus Number:         | RT_STRING 8153 | R6, B11 | open |
| MSG-249 |   Version:            | RT_STRING 8154 | R6, B11 | open |
| MSG-250 |   Revision:           | RT_STRING 8155 | R6, B11 | open |
| MSG-251 |     Resource:         | RT_STRING 8160 | R6, B11 | open |
| MSG-252 |     Disposition:      | RT_STRING 8161 | R6, B11 | open |
| MSG-253 |     Type:             | RT_STRING 8162 | R6, B11 | open |
| MSG-254 |     Start:            | RT_STRING 8163 | R6, B11 | open |
| MSG-255 |     Length:           | RT_STRING 8164 | R6, B11 | open |
| MSG-256 |     Level:            | RT_STRING 8165 | R6, B11 | open |
| MSG-257 |     Vector:           | RT_STRING 8166 | R6, B11 | open |
| MSG-258 |     Affinity:         | RT_STRING 8167 | R6, B11 | open |
| MSG-259 |     Channel:          | RT_STRING 8168 | R6, B11 | open |
| MSG-260 |     Port:             | RT_STRING 8169 | R6, B11 | open |
| MSG-261 |     Reserved1:        | RT_STRING 8170 | R6, B11 | open |
| MSG-262 |     Reserved2:        | RT_STRING 8171 | R6, B11 | open |
| MSG-263 |     Data: | RT_STRING 8172 | R6, B11 | open |
| MSG-264 | Interface Type:       | RT_STRING 8180 | R6, B11 | open |
| MSG-265 | Bus Number:           | RT_STRING 8181 | R6, B11 | open |
| MSG-266 | Slot Number:          | RT_STRING 8182 | R6, B11 | open |
| MSG-267 | List  | RT_STRING 8183 | R6, B11 | open |
| MSG-268 |   Descriptor  | RT_STRING 8184 | R6, B11 | open |
| MSG-269 |     Option:           | RT_STRING 8185 | R6, B11 | open |
| MSG-270 |     Alignment:        | RT_STRING 8186 | R6, B11 | open |
| MSG-271 |     Minimum Address:  | RT_STRING 8187 | R6, B11 | open |
| MSG-272 |     Maximum Address:  | RT_STRING 8188 | R6, B11 | open |
| MSG-273 |     Minimum Vector:   | RT_STRING 8189 | R6, B11 | open |
| MSG-274 |     Maximum Vector:   | RT_STRING 8190 | R6, B11 | open |
| MSG-275 |     Minimum Channel:  | RT_STRING 8191 | R6, B11 | open |
| MSG-276 |     Maximum Channel:  | RT_STRING 8192 | R6, B11 | open |

### 2.6 VIEW: layout and density

| ID | Feature | Oracle source | Package | Status |
| --- | --- | --- | --- | --- |
| VIEW-001 | DPI basis. The metrics were captured at 192 DPI (200 percent scale; window dpi 192). The parity basis is 96 DPI (dpi.requiredForParity = 96). Device-pixel values below are for 192 DPI; the 96 DPI value is half, unless stated. The screenshot file regedit-100dpi.png is named for 100 percent but was captured at 200 percent. | regedit-metrics.json (dpi, window); regedit-100dpi.png | U1, U2 | open |
| VIEW-002 | Top-level window: 2700 x 1610 device px; client 2674 x 1500; client origin offset (13, 97). Window class RegEdit_RegEdit; title text Registry Editor (string 16). | regedit-metrics.json (window) | U2, R1 | open |
| VIEW-003 | Menu bar: height 38 px (19 at 96 DPI), spanning the full client width (2674 px). Item rectangles as (x, width): File (13, 65), Edit (78, 68), View (146, 79), Favorites (225, 122), Help (347, 78). Menu font Segoe UI, height -24 (9 pt at 192 DPI), weight 400. | regedit-metrics.json (menuBar, nonClientFonts.menu) | U3, U1 | open |
| VIEW-004 | Non-client fonts and system metrics: Segoe UI, height -24 device px (9 pt at 192 DPI), weight 400, not italic, for the menu, status, caption, small caption and message fonts. Caption height 44 px. Border 2 px; padded border 8 px. SM_CYMENU 39; SM_CXEDGE and SM_CYEDGE 2; SM_CXVSCROLL and SM_CYHSCROLL 34; SM_CXSMICON 32; SM_CYCAPTION 45. | regedit-metrics.json (nonClientFonts, systemMetrics) | U1, U2 | open |
| VIEW-005 | Address bar: Edit control at client (13, 97), 2674 x 38 px, full client width, empty text in the default capture. It shows the full path with Computer first, for example Computer\HKEY_CURRENT_USER\Software\BWC-Oracle\Mixed. | regedit-metrics.json (addressBar); regedit-100dpi.png | U11 | open |
| VIEW-006 | Pane split: tree pane right edge at x = 445 and list pane left edge at x = 453, so the splitter gap is 8 px (4 px at 96 DPI). Tree pane x 13, width 432; list pane x 453, width 2234. Both panes start at y 135 and are 1462 px high. | regedit-metrics.json (splitter, tree, list) | U2 | open |
| VIEW-007 | Tree rows: item height 34 px (17 at 96 DPI); indent 32 px per level (16 at 96 DPI). Text x offsets: root Computer 52, depth 1 84, depth 2 116. Font Segoe UI 9 pt (height -24), charSet 0. Total items in the capture: 48. | regedit-metrics.json (tree) | U5 | open |
| VIEW-008 | Tree icons and glyphs: the Computer root has a computer icon; hive and key nodes have yellow folder icons (closed and open); expandable nodes show a chevron (collapsed) or a down chevron (expanded). The tree pane shows a vertical and a horizontal scroll bar. Pixel sizes of tree node icons were not measured (only list icons were). | regedit-100dpi.png | U5 | open |
| VIEW-009 | Tree sibling order: alphabetical ignoring case in the capture, for example appdatalow, BWC-Oracle, ChangeTracker, Classes, Clients, Google, Helium, imput, Microsoft, Node.js, Parallels, Policies, RegisteredApplications, Wow6432Node. Under BWC-Oracle, the Unicode key Ünïcødé Key ✓ sits between Strings and ValueNames. Root order in the capture: Computer, HKEY_CLASSES_ROOT, HKEY_CURRENT_USER. The export golden orders this key differently (OQ-25). | regedit-100dpi.png; regedit-metrics.json (tree.firstVisibleItems); exports/bwc-oracle-v5.reg | U5, B2 | open |
| VIEW-010 | Tree selection and focus: the selected key (Mixed) is drawn with a highlight fill and a focus rectangle. Exact colors and rectangle style were not captured (OQ-27). | regedit-100dpi.png | U5 | open |
| VIEW-011 | Child windows (for automation and layout parity): Edit (address bar, id 0), SysTreeView32 (id 1), SysListView32 (id 2), SysHeader32 (list header, id 0). | regedit-metrics.json (children) | U5, U7, U11 | open |
| VIEW-012 | List header: height 40 px (20 at 96 DPI); width 2230 px. Columns: Name 240 px, Type 240 px, Data 576 px (120, 120 and 288 at 96 DPI). Column titles: Name (string 17), Type (string 31), Data (string 18). | regedit-metrics.json (list.header) | U7 | open |
| VIEW-013 | List rows: height 36 px (18 at 96 DPI). Icon rectangle 32 x 36 at x 4; label at x 36, width 204. Font Segoe UI 9 pt, charSet 0. The first row starts at y 40 under the header. | regedit-metrics.json (list.rowHeight, list.firstRows) | U7 | open |
| VIEW-014 | Value sort order: (Default) first, then the other values alphabetically. Capture order for the Mixed key: (Default), Big, Blob, Expand, List, Number, Raw, Text. Collation for mixed case, digits, symbols and non-ASCII names is not captured (OQ-26). | regedit-metrics.json (list.firstRows); regedit-100dpi.png | U7, B2 | open |
| VIEW-015 | Value icons by type: REG_SZ, REG_EXPAND_SZ and REG_MULTI_SZ show the red ab icon; REG_BINARY, REG_DWORD, REG_QWORD and REG_NONE show the 011 110 binary icon. Capture: (Default), Expand, List and Text use ab; Big, Blob, Number and Raw use the binary icon. Icons for REG_LINK, the resource types and unknown types were not captured. Icon size is the list icon rectangle (32 x 36 device px). | regedit-100dpi.png; regedit-metrics.json (list.firstRows.icon) | U7 | open |
| VIEW-016 | Data column text (capture): REG_SZ as stored; REG_MULTI_SZ strings joined with a single space (a b); REG_BINARY as space-separated hex pairs (01 02 03); REG_DWORD as 0x%08x with decimal in parentheses (0x00001234 (4660)); REG_QWORD as 0x%08I64x with decimal (0x102030405060708 (72623859790382856), so a leading zero beyond the 8-digit minimum is dropped); zero-length REG_NONE as (zero-length binary value). The default value is always listed as (Default); with no default value its Data is (value not set). | regedit-100dpi.png; RT_STRING 20, 22, 27, 29, 32 | B4, U7 | open |
| VIEW-017 | Row selection and focus in the value list: not visible in the capture, because no list row was selected. Colors and focus rectangle style for list rows are not captured (OQ-27). | regedit-100dpi.png | U7 | open |
| VIEW-018 | Status bar: absent. Metrics record statusBar.present = false and the View menu has no Status Bar item. BWC keeps the classic layout without a status bar. | regedit-metrics.json (statusBar); RT_MENU 103 (View) | U2 | open |
| VIEW-019 | Dialog font: all 26 dialog templates declare MS Shell Dlg, 8 pt. Dialog units (DLU) are as declared in each template; the rendered face and the DLU-to-pixel conversion were not captured (OQ-28). | regedit-resources.json (dialogs.font) | U4 | open |
| VIEW-020 | Capture environment: Arm64 processor architecture; OS build 26200 (update 9711, display version 25H2); productName reported as Windows 10 Enterprise; compat layer RunAsInvoker; regedit file version 10.0.26100.8457. Layout parity is measured on this environment only (OQ-30). | regedit-metrics.json (os, regedit) | U1 | open |

### 2.7 BEH: behavior not in the resources

| ID | Feature | Oracle source | Package | Status |
| --- | --- | --- | --- | --- |
| BEH-001 | Favorites are stored under HKCU\Software\Microsoft\Windows\CurrentVersion\Applets\Regedit\Favorites, the key classic regedit also uses, so favorites are shared between the two editors. | Brief (not in the oracle dump); value names and format open (OQ-49) | R10, B2 | open |
| BEH-002 | LastKey and view state are persisted in the parent key of Favorites (HKCU\Software\Microsoft\Windows\CurrentVersion\Applets\Regedit). Value names, types and fields are not in the dump (OQ-49). The metrics record regedit.lastKey for the capture. | Brief; regedit-metrics.json (regedit.lastKey) | R2, B2 | open |
| BEH-003 | FindFlags (Keys, Values, Data, Match whole string only, Search in subtree) are persisted in the same parent key. Names and encoding: open (OQ-49). | Brief | R9, R2 | open |
| BEH-004 | Window position and size are persisted across launches. Storage location and format: open (OQ-49). | Brief | R1, U2 | open |
| BEH-005 | Splitter position is persisted across launches (default gap 8 px at 192 DPI). Storage: open (OQ-49). | Brief; regedit-metrics.json (splitter) | U2, R2 | open |
| BEH-006 | Value list column widths are persisted across launches (defaults 240, 240, 576 device px). Storage: open (OQ-49). | Brief; regedit-metrics.json (list.header) | U7, R2 | open |
| BEH-007 | No live refresh: the tree and value list are snapshots. Only F5 (Refresh; accelerator 648; View > Refresh) re-reads the registry. The engine does not watch for changes. | registry-engine.md (Threading); RT_ACCELERATOR 100 (648); RT_MENU 103 (View > Refresh) | R2, B2 | open |
| BEH-008 | No undo: no Undo command or accelerator appears in any menu or in the accelerator table. Deletes, renames and edits take effect immediately. | RT_MENU 103 to 108 (absence); RT_ACCELERATOR 100 (absence) | U3, R4 | open |
| BEH-009 | No drag-and-drop: keys and values cannot be dragged between keys, panes or other windows. No drag-related menu item or string exists in the dump. | Brief; RT_MENU and RT_STRING (absence) | U5, U7 | open |
| BEH-010 | Inline rename of keys and values, started by F2 (accelerator 914) or by a slow second click on the selected item (brief). Name errors use RT_STRING 66 to 70 (keys) and 73 to 75 (values). | Brief; RT_ACCELERATOR 100 (914); RT_STRING 66 to 75 | R4, U5 | open |
| BEH-011 | Default new names: New Key #1 and New Value #1 (RT_STRING 23 and 24, with a counter that starts at 1). The counter rule (first unused number) is verify. | RT_STRING 23, 24; brief | R3 | open |
| BEH-012 | Value rename is copy then delete (there is no native value rename). If the delete fails, the copy is removed. Errors: RT_STRING 72 to 75. | registry-engine.md (Writes); RT_STRING 72 to 75 | R4, B3 | open |
| BEH-013 | Delete confirmations: deleting a key asks RT_STRING 48 (caption RT_STRING 49). Deleting one value asks RT_STRING 52 (caption RT_STRING 51). Deleting several values asks RT_STRING 50 (caption RT_STRING 51). A partial failure reports RT_STRING 89 (caption RT_STRING 88). | RT_STRING 48 to 52, 88, 89 | R4, B3 | open |
| BEH-014 | Address bar navigation: a typed path is navigated to on Enter. Paths may start with the abbreviations HKLM, HKCU, HKCR, HKU and HKCC (brief). Invalid path handling is open (OQ-38). | Brief; RT_STRING and RT_MENU (no abbreviation strings in the dump) | U11, R2 | open |
| BEH-015 | Single instance per user: a second launch activates the running window unless /m is given. | Brief | R13 | open |
| BEH-016 | Command line: /s silent import of a .reg file; /e file [key] export; /a file [key] export in REGEDIT4 ANSI format; /m allows multiple instances. Console paths never create a window and return regedit exit codes (contract). The exit code table is open (OQ-46). | Brief; registry-engine.md (Command line) | R13, B5 | open |
| BEH-017 | A .reg file given on the command line is imported. Whether the confirmation text RT_STRING 304 appears on this path is verify. | Brief; RT_STRING 304 | R13, R7 | open |
| BEH-018 | Double-clicking a .reg file imports it after the confirmation text RT_STRING 304 (Adding information can unintentionally change or delete values...). The dialog caption and default button are not in the dump. | Brief; RT_STRING 304 | R13, R7 | open |
| BEH-019 | Import reads v5 text (UTF-16 with BOM) and REGEDIT4 text (ANSI with GetACP). Binary registry files import only from inside the editor (RT_STRING 123). Cancel stops between complete entries, never inside one. | registry-engine.md (.reg files, Threading); RT_STRING 123 | R7, B6 | open |
| BEH-020 | Export offers v5 Unicode .reg; REGEDIT4 ANSI .reg (filter RT_STRING 305, Win9x/NT4 Registration Files); a hive file (save subtree, RT_STRING 212 to 215); and a text file (filter Text Files; labels RT_STRING 8101 to 8110). Only the v5 and REGEDIT4 formats have goldens (OQ-34). | RT_STRING 212 to 215, 305, 8101 to 8110; exports/ | R7, B5, B11 | open |
| BEH-021 | Export range: All or Selected branch (dialog 100). A selected branch path that does not exist is refused with RT_STRING 148. | RT_DIALOG 100; RT_STRING 148 | R7, B5 | open |
| BEH-022 | Load Hive and Unload Hive act only under HKEY_LOCAL_MACHINE and HKEY_USERS (brief). | Brief; RT_MENU 103 (File > Load Hive, Unload Hive) | R8, B8 | open |
| BEH-023 | Remote registry roots: a connected remote computer shows only HKEY_LOCAL_MACHINE and HKEY_USERS (brief). | Brief | R8, B9 | open |
| BEH-024 | Disconnect removes a connected remote computer from the tree (File > Disconnect Network Registry, RT_MENU 103 id 768; RT_MENU 107 id 768 on the remote root). | RT_MENU 103, 107 | R8, B9 | open |
| BEH-025 | Connecting to your own computer is refused (RT_STRING 153). Connection failures use RT_STRING 154 to 156; a connect that cannot reach all roots reports RT_STRING 155. | RT_STRING 153 to 156 | R8, B9 | open |
| BEH-026 | Permissions (Edit > Permissions, RT_MENU 103 id 678; context item 678) opens the standard ACL editor for the selected key, with Advanced for special permissions (brief). The ACL UI is an OS component; the dump has only its strings (RT_STRING 7001 to 7092). Behavior is open (OQ-42). | Brief; RT_MENU 103, 104; RT_STRING 7001 to 7092 | R11, B10 | open |
| BEH-027 | Print of the selected branch (or all): a progress dialog with Cancel (dialog 105) and a print range panel (dialog 108). Printed text uses labels RT_STRING 8101 to 8192 (inferred from the text; no print golden exists; OQ-44). | Brief; RT_DIALOG 105, 108; RT_STRING 8101 to 8192 | R14, B11 | open |
| BEH-028 | Tree keyboard navigation: Up and Down move the selection; Home and End move to the first and last items (brief; exact rules verify). PageUp and PageDown page. Left collapses an expanded key or moves to its parent. Right expands a collapsed key or moves to its first child (brief). | Brief | U5, U6 | open |
| BEH-029 | Value list multi-select: several values can be selected and deleted together (RT_STRING 50 and 89 cover the multi-value case). | Brief; RT_STRING 50, 89 | U7, R4 | open |
| BEH-030 | Focus: Tab, Shift+Tab and F6 move focus between the tree and the value list (accelerator rows). Focus at launch and after refresh is open (OQ-48). | Brief; RT_ACCELERATOR 100 | U5, U7 | open |
| BEH-031 | Mouse: a right-click on a key in the tree, a value in the list, or the empty list area opens the matching context menu (CTX rows). A single click selects. | RT_MENU 104 to 106 (context menus); brief | R12, U5 | open |
| BEH-032 | Expand and Collapse: menu item 915 (Expand) in the tree key menu and in the remote root menu. Its label switches to Collapse (RT_STRING 25) when the branch is expanded; the switch is inferred from the string set (OQ-06). | RT_MENU 104, 107 (id 915); RT_STRING 25 | U5, R12 | open |
| BEH-033 | Go to HKEY_LOCAL_MACHINE: Ctrl+T and the tree key menu item 918 navigate to HKEY_LOCAL_MACHINE. The sibling string Go to HKEY_CURRENT_USER (RT_STRING 311) has no menu item in the dump (OQ-05). | RT_ACCELERATOR 100 (918); RT_MENU 104 id 918; RT_STRING 311 | R2, R12 | open |
| BEH-034 | Symbolic link keys are followed with default open semantics; REG_OPTION_OPEN_LINK is never passed. | registry-engine.md (Symbolic links and redirection) | B2 | open |
| BEH-035 | Native registry view only: KEY_WOW64_64KEY is passed on every open, so no WOW64 redirection is shown. | registry-engine.md (Symbolic links and redirection) | B2 | open |
| BEH-036 | Access denied on a read is not a crash: a subkey that cannot be read still appears in the tree, and opening it shows regedit error text (RT_STRING 96 to 98). | registry-engine.md (Errors); RT_STRING 96 to 98 | U5, B2 | open |
| BEH-037 | Names and limits come from the platform: key name component 255 characters, value name 16383 characters, nesting depth 512. The engine passes requests through and reports the Win32 error. Name comparison for find, sort and duplicates is ordinal ignore case. | registry-engine.md (Names, Errors) | B2, B3 | open |
| BEH-038 | The default value has the empty name and is listed as (Default) even when absent (the capture shows (value not set)). | registry-engine.md (Names); regedit-100dpi.png | B2, U7 | open |
| BEH-039 | Find order: the search starts after the current selection (the selected value if one is selected, else the selected key), walks keys depth first in display order across the remaining roots, and never wraps. At the end it reports RT_STRING 41. | registry-engine.md (Find); RT_STRING 41 | B7, R9 | open |
| BEH-040 | Find check order: per key, the key name (if Keys), then each value name (if Values), then its data (if Data), in display order. Matching is case-insensitive substring, or whole-string equality when Match whole string only is checked. | registry-engine.md (Find); RT_DIALOG 106 | B7 | open |
| BEH-041 | Which data types the Data check searches, and how REG_MULTI_SZ and binary data match: verify against the oracle (OQ-11). | registry-engine.md (Find) | B7 | open |
| BEH-042 | F3 (Find Next) repeats the last search from the current hit. The search runs on a background thread with the progress dialog (dialog 109) and its Cancel button. | registry-engine.md (Find); RT_ACCELERATOR 100 (671); RT_DIALOG 109 | R9, B7 | open |
| BEH-043 | Elevation: the brief says regedit uses highestAvailable, and a standard user runs with limited access and gets access-denied messages (RT_STRING 126, 142, 182, 189). The manifest level of regedit.exe is not in the inputs (OQ-24). | Brief; RT_STRING 126, 142, 182, 189 | B8, R1 | open |
| BEH-044 | Load Hive and Unload Hive report insufficient privileges (RT_STRING 182, 184) and access denied (RT_STRING 187, 189). The privilege enable step is not in the dump (OQ-20, OQ-21). | RT_STRING 182 to 189 | R8, B8 | open |
| BEH-045 | Policy lockout: when registry editing is disabled by the administrator, RT_STRING 40 is the message. Behavior when set is open (OQ-23). | RT_STRING 40 | R1, B8 | open |
| BEH-046 | Edit dispatch: Modify (id 912) opens the editor for the value type. Probable mapping: REG_SZ and REG_EXPAND_SZ to Edit String (dialog 102), REG_DWORD to dialog 111, REG_QWORD to dialog 117, REG_MULTI_SZ to dialog 114. Modify Binary Data (id 917) opens the hex editor (dialog 103). The mapping is inferred from the dialog captions; verify, including which types Modify Binary Data applies to (OQ-03). | RT_MENU 105 (ids 912, 917); RT_DIALOG 102, 103, 111, 114, 117 (inferred mapping) | R5, R6 | open |
| BEH-047 | Raw writes: values are written with their raw type and bytes, with no normalization on read or write (wrong-length DWORD and odd types survive a round trip). Reads loop on ERROR_MORE_DATA with no engine size cap. | registry-engine.md (Values) | B3, B4 | open |
| BEH-048 | Tree root: the root node is labeled Computer (RT_STRING 19) and holds the hive roots. Its menu and rename rules are not in the dump (OQ-39). | RT_STRING 19; regedit-100dpi.png | U5, R2 | open |
| BEH-049 | View > Display Binary Data (id 679) opens the Binary Data dialog (2020 or 2021) for the selected value. Which variant opens when is open (OQ-14). | RT_MENU 103 id 679; RT_DIALOG 2020, 2021 | R6, B4 | open |

### 2.8 EXP: export and import bytes

Each value class is checked byte for byte against bwc-oracle-v5.reg; ANSI-representable values are also checked against bwc-oracle-regedit4.reg. The Many300 class is checked against bwc-oracle-many300-v5.reg. Rows that cannot pass byte-for-byte through a .reg round trip are marked in their text and listed in section 4.

| ID | Feature | Oracle source | Package | Status |
| --- | --- | --- | --- | --- |
| EXP-001 | Default value, REG_SZ: @="Default value set" on key Default; sibling value Other is REG_SZ "non-default sibling" on the same key. | seed-tree.md (Default); exports/bwc-oracle-v5.reg; exports/bwc-oracle-regedit4.reg | B4, B5 | open |
| EXP-002 | Key without a default value (NoDefault): the file writes only "Other"="no default value on this key"; no @ line is written. | seed-tree.md (NoDefault); exports/bwc-oracle-v5.reg | B2, B5 | open |
| EXP-003 | REG_SZ empty string, 2 bytes (Strings, Empty): written as "Empty"="". | seed-tree.md (Strings); exports/bwc-oracle-v5.reg | B4, B5, B6 | open |
| EXP-004 | REG_SZ with zero bytes and no NUL (Strings, EmptyNoNul): the golden writes "EmptyNoNul"="". Re-import creates a 2-byte value, so a byte-for-byte round trip is not possible (OQ-32). | seed-tree.md (Strings); exports/bwc-oracle-v5.reg | B4, B5, B6 | open |
| EXP-005 | REG_SZ ASCII (Strings, Ascii, 32 bytes): "Ascii"="Hello World 123". | seed-tree.md (Strings); exports/bwc-oracle-v5.reg; exports/bwc-oracle-regedit4.reg | B4, B5, B6 | open |
| EXP-006 | REG_SZ with double quotes (Strings, Quotes, 44 bytes): each embedded quote is written as a backslash followed by a quote. | seed-tree.md (Strings); exports/bwc-oracle-v5.reg | B4, B5, B6 | open |
| EXP-007 | REG_SZ with backslashes (Strings, Backslashes, 32 bytes): each backslash is doubled in the file. | seed-tree.md (Strings); exports/bwc-oracle-v5.reg | B4, B5, B6 | open |
| EXP-008 | REG_SZ Unicode (Strings, Unicode, 26 bytes, with a check mark and CJK characters): v5 writes UTF-16 characters. REGEDIT4 writes characters the ANSI code page cannot represent as code page replacement bytes (golden bytes). | seed-tree.md (Strings); exports/bwc-oracle-v5.reg; exports/bwc-oracle-regedit4.reg | B4, B5, B6 | open |
| EXP-009 | REG_EXPAND_SZ (Strings, ExpandSz; and Mixed, Expand): written as hex(2) with the UTF-16LE bytes of the value, including the terminator. | seed-tree.md (Strings, Mixed); exports/bwc-oracle-v5.reg | B4, B5, B6 | open |
| EXP-010 | REG_SZ with no terminating NUL (Strings, NoNul, 26 bytes): the golden writes it as a normal string. Re-import adds a terminator (28 bytes), so a byte-for-byte round trip fails (OQ-32). | seed-tree.md (Strings); exports/bwc-oracle-v5.reg | B4, B5, B6 | open |
| EXP-011 | REG_SZ with an embedded NUL (Strings, EmbeddedNul, 16 bytes: abc NUL def): the golden writes "abc", truncated at the first NUL. The engine keeps raw bytes; the writer must match the golden (OQ-31). | seed-tree.md (Strings); exports/bwc-oracle-v5.reg | B4, B5, B6 | open |
| EXP-012 | REG_SZ of 5000 characters (Strings, Long5000, 10002 bytes): one line of 5013 characters, not wrapped, written as a string and not as hex. | seed-tree.md (Strings); exports/bwc-oracle-v5.reg | B4, B5, B6 | open |
| EXP-013 | REG_DWORD values (Numbers: Dword0, Dword1, DwordMax; also Mixed, Number and the Unicode name value): written as dword:00000000, dword:00000001 and dword:ffffffff, with lower-case hex digits. | seed-tree.md (Numbers, Mixed, ValueNames); exports/bwc-oracle-v5.reg | B4, B5, B6 | open |
| EXP-014 | REG_DWORD_BIG_ENDIAN, type 5 (Numbers, DwordBigEndian): written as hex(5):12,34,56,78, not as dword:. | seed-tree.md (Numbers); exports/bwc-oracle-v5.reg | B4, B5, B6 | open |
| EXP-015 | REG_DWORD with 3 bytes (Numbers, DwordWrongLen3): written as hex(4):01,02,03; the type and the length are kept. | seed-tree.md (Numbers); exports/bwc-oracle-v5.reg | B4, B5, B6 | open |
| EXP-016 | REG_QWORD 0 and maximum (Numbers, Qword0 and QwordMax; Mixed, Big): written as hex(b) with 8 bytes in stored (little-endian) order, lower-case hex. | seed-tree.md (Numbers, Mixed); exports/bwc-oracle-v5.reg | B4, B5, B6 | open |
| EXP-017 | REG_BINARY empty (Binary, Empty): written as "Empty"=hex: with no data. REG_BINARY of one byte (Binary, OneByte): written as hex:ab. | seed-tree.md (Binary); exports/bwc-oracle-v5.reg | B4, B5, B6 | open |
| EXP-018 | REG_BINARY of 300 bytes (Binary, Wrap300): written as hex: with comma-separated lower-case bytes, wrapped as described in EXP-030. | seed-tree.md (Binary); exports/bwc-oracle-v5.reg; exports/bwc-oracle-regedit4.reg | B4, B5, B6 | open |
| EXP-019 | REG_NONE: empty (Binary, NoneEmpty) written as hex(0): with no data; NoneData (deadbeef) written as hex(0):de,ad,be,ef; Mixed, Raw written as hex(0):. | seed-tree.md (Binary, Mixed); exports/bwc-oracle-v5.reg | B4, B5, B6 | open |
| EXP-020 | REG_MULTI_SZ: Several (alpha, beta, gamma); EmptyList (single NUL pair, written as hex(7):00,00); WithEmpty (one, empty, three; the empty string is kept). Mixed, List is written as hex(7):61,00,00,00,62,00,00,00,00,00. | seed-tree.md (MultiSz, Mixed); exports/bwc-oracle-v5.reg | B4, B5, B6 | open |
| EXP-021 | REG_LINK, type 6 (OddTypes, Link6): written as hex(6) with the UTF-16LE bytes of \Registry\Machine\Software and the terminator. | seed-tree.md (OddTypes); exports/bwc-oracle-v5.reg | B4, B5, B6 | open |
| EXP-022 | Resource types (OddTypes): REG_RESOURCE_LIST (Resource8) as hex(8), REG_FULL_RESOURCE_DESCRIPTOR (Full9) as hex(9), REG_RESOURCE_REQUIREMENTS_LIST (Req10) as hex(a); all written as raw hex with no decoding. | seed-tree.md (OddTypes); exports/bwc-oracle-v5.reg | B4, B5, B6 | open |
| EXP-023 | Unknown type 0x12345 (OddTypes, Custom12345): written as hex(12345):ca,fe,ba,be. | seed-tree.md (OddTypes); exports/bwc-oracle-v5.reg | B4, B5, B6 | open |
| EXP-024 | Value names with special characters (ValueNames): a=b, =, quote"name (quote escaped), back\slash (backslash doubled), @ and [bracket] (quoted, so not confused with the default marker), Space name, dots.in.name, Ünïcødé name ✓ (dword 0x2a) and a 200-character name. | seed-tree.md (ValueNames); exports/bwc-oracle-v5.reg; exports/bwc-oracle-regedit4.reg | B4, B5, B6 | open |
| EXP-025 | Key names with spaces, dots and Unicode (Key With Spaces, dotted.key.name, Ünïcødé Key ✓): sections use the full path under HKEY_CURRENT_USER\Software\BWC-Oracle; REGEDIT4 writes the Unicode key name with code page replacement bytes. | seed-tree.md (key headings); exports/bwc-oracle-v5.reg; exports/bwc-oracle-regedit4.reg | B5, B6 | open |
| EXP-026 | Nested keys to depth 4 (Nested, Nested\Level1, Nested\Level1\Level2, Nested\Level1\Level2\Level3): sections written parent first, each with its own values. | seed-tree.md (Nested); exports/bwc-oracle-v5.reg | B5, B6 | open |
| EXP-027 | Key with 300 values (Many300): one section with all 300 DWORD values V001 to V300 in enumeration order. The many300 golden and the v5 golden agree. | seed-tree.md (Many300); exports/bwc-oracle-many300-v5.reg; exports/bwc-oracle-v5.reg | B4, B5 | open |
| EXP-028 | Mixed key with all types: values written in enumeration order Text, Expand, Number, Big, Blob, List, Raw. This differs from the display order (VIEW-014; OQ-25). | seed-tree.md (Mixed); exports/bwc-oracle-v5.reg | B2, B5 | open |
| EXP-029 | File header and encoding: v5 starts with Windows Registry Editor Version 5.00, is UTF-16LE with a BOM, and uses CRLF; REGEDIT4 starts with REGEDIT4, is ANSI (GetACP) and uses CRLF. One blank line follows the header and each section. | exports/bwc-oracle-v5.reg; exports/bwc-oracle-regedit4.reg; registry-engine.md (.reg files) | B5, B6 | open |
| EXP-030 | Hex wrap: in the golden each hex: line is 78 characters including the trailing backslash, and continuation lines begin with two spaces. The contract says 80 columns; the golden governs (OQ-33). | exports/bwc-oracle-v5.reg; registry-engine.md (.reg files) | B5, B6 | open |
| EXP-031 | Order: keys are written in ordinal ignore case order (Ünïcødé Key ✓ comes last, as in the golden). Values are written in enumeration order, not sorted. The display order differs (OQ-25). | exports/bwc-oracle-v5.reg; exports/bwc-oracle-regedit4.reg | B5, B2 | open |
| EXP-032 | Golden coverage: the v5 and REGEDIT4 goldens cover the whole seed tree under BWC-Oracle; the many300 golden covers Many300 only. No golden exists for text export, hive export, print, or command-line exit codes (OQ-34). | exports/ (three files); seed-tree.md | B5, B11, R13 | open |

## 3. Package map

| Package | Meaning |
| --- | --- |
| B2 | enumerate and read |
| B3 | write, delete and rename |
| B4 | value codec and display strings |
| B5 | .reg writer |
| B6 | .reg parser |
| B7 | find |
| B8 | hives and privileges |
| B9 | remote |
| B10 | security object |
| B11 | text export |
| U1 | theme metrics |
| U2 | window, splitter and status |
| U3 | menu system |
| U4 | modal dialogs |
| U5 | tree |
| U6 | tree keyboard and focus (tree package) |
| U7 | list |
| U8 | list display and item rendering |
| U9 | hex editor |
| U10 | hex editor input and behavior |
| U11 | address bar |
| R1 | main window |
| R2 | navigation state, LastKey and view persistence |
| R3 | New |
| R4 | Delete and Rename |
| R5 | String, DWORD and QWORD editors |
| R6 | Multi-String, Binary and resource viewers |
| R7 | Import and Export |
| R8 | hive and remote UI |
| R9 | Find |
| R10 | Favorites |
| R11 | Permissions |
| R12 | context menus and shortcuts |
| R13 | command line and instancing |
| R14 | Print |

## 4. Open questions

Behaviors that the inputs do not determine. Each one must be settled against the oracle or the brief before the related row can pass.

OQ-01. Which command opens which computer picker. Dialog 107 (caption Disconnect Network Registry, label Computer name:) and dialog 115 (caption Import Registry File, label Select Computer:) both hold a computer list with OK and Cancel. The dump does not say which command opens each one, or whether 115 is the Import remote target picker. Captions and labels disagree, so the oracle must be checked.

OQ-02. Attachment of the text edit context menu (RT_MENU 108). The control it attaches to is not in the dump.

OQ-03. Context menu visibility rules. The dump lists the items but not when each one shows: Go to HKEY_LOCAL_MACHINE (918) in RT_MENU 104; Modify (912) versus Modify Binary Data (917) in RT_MENU 105, possibly by value type; Export (659) and Disconnect (768) in RT_MENU 107; whether RT_MENU 104 appears on the Computer root.

OQ-04. Commands with no menu item or string: 642 (F4), 672 (Alt+D and Ctrl+L), 689 to 697 (Alt+1 to Alt+9), 698 (Alt+Up), 896 (Shift+F10). Commands 640 and 641 are the Tab and Shift+Tab pane switch in the brief, but the dump does not name them.

OQ-05. String 311, Go &to HKEY_CURRENT_USER, has no menu item in the dump. Where it is shown, and whether it toggles with Ctrl+T, is not known.

OQ-06. Expand and Collapse label. String 25 (Collapse) is the likely second label of menu item 915 (Expand). The dump does not say when the label switches.

OQ-07. About Registry Editor (menu item 667) has no RT_DIALOG in the dump. The content and surface of the About box are not known.

OQ-08. Dynamic favorites list: item text, order, maximum count, click behavior and command ids are not in the dump.

OQ-09. Menu help strings (512 to 517, 648, 657 to 681, 688, 768, 912 to 917, 1280, 1281) describe menu items. This build has no status bar (VIEW-018), so where these texts appear, if anywhere, is not known.

OQ-10. Find dialog control 1161, Search in subtree: default state, interaction with the search start key, and whether it takes effect in this build.

OQ-11. Find Data scope: which value types the Data check searches (REG_MULTI_SZ items, binary, DWORD display text). The contract says to verify this with a golden hit sequence.

OQ-12. Edit DWORD and Edit QWORD base radio default on open, whether the choice persists, and the rules for rejected decimal or hex input. Strings 30 and 33 are display text for invalid stored data, not input errors.

OQ-13. Multi-String blank-line warning: the choice between string 116 (one empty string) and 117 (all empty strings) is inferred from the wording. Confirm against the oracle.

OQ-14. Binary Data dialogs 2020 and 2021: which command opens each, the default Format radio, and the list text for each unit.

OQ-15. Template-disabled controls (WS_DISABLED): Data... (1204, dialog 1305), Display... (1506, dialog 1501), Share Disposition captions (1310 to 1313), Preferred and Alternative captions (2001 and 2002 in dialogs 1601, 1700, 1800 and 1900), and Multiple and Extended Selection captions (dialog 1750). The runtime enable rules are not in the dump.

OQ-16. Viewer mapping: Resource Lists (116), Resources (1305) and Requirements List (1501), and the detail viewers (1601, 1700, 1800, 1900), are mapped by caption and content only. Which descriptor type (REG_RESOURCE_LIST, REG_FULL_RESOURCE_DESCRIPTOR, REG_RESOURCE_REQUIREMENTS_LIST) each one opens, and how the Display... and Data... buttons route, is not verified.

OQ-17. Column List Box Styles (dialog 1750): is it developer-only? No menu item in the dump opens it. Decide whether BWC implements it.

OQ-18. Import progress (dialog 110): behavior on error, whether it closes automatically, and whether success is shown by a message box (strings 128, 228) or otherwise.

OQ-19. Export progress: the dump has no export progress dialog. Whether export shows progress (reusing dialog 105 or 109, or none) is not known.

OQ-20. Load Hive order and rules: whether the hive file picker (filter strings 302 and 180) comes before the Key Name prompt (dialog 104); the default key name; duplicate key names (no string in the dump for that case); and name validation.

OQ-21. Unload Hive target: which key is unloaded, and what happens when the selection is not a hive root (no string in the dump for that case).

OQ-22. Restore Key strings (55, 56): no menu item in the dump. Inferred as the confirmation for importing a binary hive file over a key (RegRestoreKey). Verify.

OQ-23. Policy lockout (string 40): which policy value triggers it, and what BWC should show or block when it is set.

OQ-24. Elevation: the oracle ran under compat layer RunAsInvoker. The manifest level of regedit.exe (asInvoker, highestAvailable or requireAdministrator) is not in the inputs. The brief says highestAvailable; check it against regedit.exe.

OQ-25. Display sort versus export order. The screenshot shows Ünïcødé Key ✓ between Strings and ValueNames (a collation that treats Ü as U). The v5 golden writes the same key last (ordinal ignore case). The contract says display order must match the oracle; capture the seeded tree's display order and decide.

OQ-26. Value list collation beyond (Default) first: sort rules for mixed case, digits, symbols and non-ASCII names (the seed ValueNames key) are not captured by the metrics or the screenshot.

OQ-27. Colors and sizes not measured: selection fill and focus rectangle colors and style, tree icon pixel sizes, open and closed folder icon variants, and list selection appearance. Only the list icon rectangle and the tree item height were measured.

OQ-28. Dialog font and DLU conversion: templates declare MS Shell Dlg 8 pt. The rendered face and the pixel size of one DLU were not captured.

OQ-29. DPI basis: the screenshot is named regedit-100dpi.png but was captured at 192 DPI (200 percent). The 96 DPI parity values are assumed to be half the device values. Font, icon and glyph sizes may not scale linearly; confirm at 96 DPI with a fresh capture.

OQ-30. Metrics environment: captured on an Arm64 build that reports productName Windows 10 Enterprise with build 26200. Confirm the same layout on x64, which BWC also targets.

OQ-31. Embedded NUL export: the v5 golden writes EmbeddedNul (abc, NUL, def) as "abc", truncated at the first NUL. The engine contract requires raw byte preservation. The .reg writer must match the golden, so decide whether this is a declared-difference for the round trip.

OQ-32. Lossy classes and round trip: the golden writes EmptyNoNul (0 bytes) as "" and NoNul (26 bytes, no terminator) as a normal string. Re-importing yields a 2-byte value and a 28-byte value. The EXP rows require a byte-for-byte round trip, so these classes cannot pass as written.

OQ-33. Hex wrap width: golden hex lines are 78 characters including the trailing backslash. The contract says 80 columns. The golden governs; correct the contract or record the difference.

OQ-34. Golden coverage gaps: no golden exists for text (*.txt) export, hive file export (save subtree), print output, or command-line exit codes and console messages.

OQ-35. Binary hive import path: the RegRestoreKey path and its confirmation (strings 55, 56) are inferred. No golden exists.

OQ-36. Double-click and Enter: the dump and the brief do not say whether double-click on a value opens its editor, whether double-click on a key toggles expansion, or what Enter does.

OQ-37. New item behavior: after New Key or New Value, whether the new item enters inline rename mode and which text is selected.

OQ-38. Address bar: behavior for an invalid path (which message, if any; string 148 is a candidate); accepted forms (for example a leading Computer\ and HKEY_ prefixes); and case rules for the abbreviations.

OQ-39. Root node operations: whether the Computer root and hive roots can be renamed, deleted or dragged (the brief says no drag-and-drop). The context menu shown on the Computer root is not in the dump.

OQ-40. My Computer (string 34): the dump does not say where it appears. The tree root is labeled Computer (string 19).

OQ-41. Shell verbs and file type strings: Registration Entries (309) and Mer&ge (310) suggest an Explorer verb for .reg files. The brief does not cover it. Whether BWC registers a file type or a Merge verb is a decision, not a parity fact.

OQ-42. Permissions: the ACL editor is an OS component and is not in the dump. Whether BWC uses the same component, and the prompt flows for replace and audit (strings 7015 to 7018; the checkboxes that trigger them are not in any dialog in the dump), are not known.

OQ-43. Favorites: the name uniqueness rule (case sensitivity; string 171) and whether Remove asks for confirmation (no string in the dump).

OQ-44. Print details: default print range, header and footer content, paper and orientation, and page layout. No print golden exists.

OQ-45. Remote connect: how the computer list is enumerated and what the computer-name prompt accepts. String 42 describes the picker.

OQ-46. Command-line exit codes and console messages for /s, /e and /a. The contract covers the paths but not the code table.

OQ-47. Read-only name in value editors (style-derived: ES_READONLY on control 1000 in dialogs 102, 103, 111, 114 and 117). Confirm that names cannot be changed from the editors.

OQ-48. Launch selection and focus: the selection and focus on first launch (no saved LastKey), and focus after an F5 refresh, are not in the dump.

OQ-49. Persisted value names and formats: LastKey, view state, FindFlags, window placement, splitter position, column widths and the Favorites values under HKCU\Software\Microsoft\Windows\CurrentVersion\Applets\Regedit. The brief gives the Favorites path; the other names and types are not in the inputs.

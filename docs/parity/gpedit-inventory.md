# Better Windows Config: gpedit parity inventory

## 1. Introduction

**Purpose.** This is the parity inventory for the Local Group Policy Editor in Better Windows Config (BWC), a WinUI 3 re-implementation of the classic Local Group Policy Editor. Each user-visible feature and each on-disk behavior of classic is one row with a stable ID. Implementation packages move rows to pass as they are built, and the final audit re-checks every row. Inputs: docs/parity/oracle/gpedit (resource dumps, fixture templates, captures) and docs/contracts/policy-engine.md.

**The classic tool.** Classic is `gpedit.msc`, an MMC 3.0 console. The snap-in is `gpedit.dll`. The Administrative Templates extension is `admtmpl.dll`. The policy-setting dialog is not a native dialog: it comes from the managed assembly `Microsoft.GroupPolicy.AdmTmplEditor`. The build under test is Windows 11 26H2 (10.0.26300), en-US. The frame resources (menus, toolbar, dialogs) come from `mmc.exe`, `mmcndmgr.dll` and `mmcbase.dll` and their `.mui` files.

**ID scheme.** An ID is PREFIX-NNN with a three-digit number, assigned in row order within each section. Prefixes: MENU (main menu), CTX (context menus), TOOL (toolbar), DLG (dialogs), ACC (accelerators and keyboard), MSG (strings), VIEW (layout and density), TREE (console tree nodes and their order), BEH (behavior not in the resources), POL (Registry.pol, gpt.ini and comment.cmtx file bytes), ADMX (template handling), DIFF (declared differences from classic). IDs are never reused; a retired row keeps its ID.

**Status values.** open: not yet verified (every row starts here). pass: verified against the oracle by a test or golden. declared-difference: BWC intentionally differs; the reason is recorded in the row or in section 4. gap: known missing and not yet declared. DIFF rows start as declared-difference.

**Who fills what.** Piece O1 (static oracle) fills MENU, CTX, DLG, ACC and MSG from the static resource dump, which needs no administrator rights. Piece O3b (capture matrix) fills POL and VIEW from the live captures. TOOL and TREE rows are filled from the console file and the first capture run. BEH, ADMX and DIFF rows below are stated now from the plan and refined by the audit.

**Package names.** The Package column uses the piece ids of the 0.2.0 plan (for example E1, G3a, U2b). A row may name two pieces.

**Source notes.** Strings are quoted only as the short UI labels already named in the plan. Microsoft string text, dumps and screenshots stay untracked and are never copied into this file.

## 2. Inventory tables

### 2.1 MENU: main menu

| ID | Feature | Oracle source | Package | Status |
| --- | --- | --- | --- | --- |

### 2.2 CTX: context menus

| ID | Feature | Oracle source | Package | Status |
| --- | --- | --- | --- | --- |

### 2.3 TOOL: toolbar

| ID | Feature | Oracle source | Package | Status |
| --- | --- | --- | --- | --- |

### 2.4 DLG: dialogs

| ID | Feature | Oracle source | Package | Status |
| --- | --- | --- | --- | --- |

### 2.5 ACC: accelerators and keyboard

| ID | Feature | Oracle source | Package | Status |
| --- | --- | --- | --- | --- |

### 2.6 MSG: strings

| ID | Feature | Oracle source | Package | Status |
| --- | --- | --- | --- | --- |

### 2.7 VIEW: layout and density

| ID | Feature | Oracle source | Package | Status |
| --- | --- | --- | --- | --- |

### 2.8 TREE: console tree

| ID | Feature | Oracle source | Package | Status |
| --- | --- | --- | --- | --- |

### 2.9 BEH: behavior not in the resources

| ID | Feature | Oracle source | Package | Status |
| --- | --- | --- | --- | --- |
| BEH-001 | The tree has one root, Local Computer Policy, with two children in this order: Computer Configuration and User Configuration. | Plan; classic probe | G1 | open |
| BEH-002 | Each of the two roots has three children in this order: Software Settings, Windows Settings, Administrative Templates. Administrative Templates is built from the loaded templates. The User Configuration child list of Windows Settings is taken from the first capture run. | Plan; classic probe | G1, E2 | open |
| BEH-003 | Placeholder nodes are shown like classic and have no children in BWC: Software Settings, and under Windows Settings: Name Resolution Policy, Scripts, Deployed Printers, Security Settings, Policy-based QoS. Selecting one opens a page saying the feature is not available yet. | Plan | G1 | open |
| BEH-004 | Administrative Templates fully work for both roots: categories form the subtree, policies of the selected category fill the result list, and a policy of class Both appears under both roots. | Plan | G1, E2 | open |
| BEH-005 | Read-only mode: when the process is not elevated or has no write access to the GPO folder, the editor opens, shows current state, and refuses every save. | Plan; policy-engine contract | G4, E3 | open |
| BEH-006 | Home notice: on the Home edition the editor opens read-only and shows a notice. Writing on Home is not part of 0.2.0. | Plan | G4 | open |
| BEH-007 | Template sources: the system folder `C:\Windows\PolicyDefinitions` plus the BWC import store `%ProgramData%\BetterWindowsConfig\PolicyDefinitions`. The system folder wins on a duplicate namespace. | Plan; policy-engine contract | G6, E5 | open |
| BEH-008 | Template import: the Add/Remove Templates dialog copies chosen files into the import store. Classic `.adm` files are not accepted. | Plan | G6, E5 | open |
| BEH-009 | A template that fails to load is listed in a load-error message and the remaining templates still load. | Plan | G6, E2 | open |
| BEH-010 | Filter: the Filter Options dialog, Filter On, and All Settings narrow the result list the way classic does. Filter result rules come from the capture run. | Plan; classic Filter Options dialog | G5, E5 | open |
| BEH-011 | Each OK or Apply writes comment.cmtx, then Registry.pol, then gpt.ini, then refreshes policy. | Plan; policy-engine contract | G4, E3 | open |
| BEH-012 | Extra Registry Settings: a node under Administrative Templates lists values found in Registry.pol that no loaded template explains. Its contents are settled by the oracle. | Plan | G6, E4 | open |
| BEH-013 | Back, Forward and Up history in the frame follow the selected tree node. | Plan | G2 | open |

### 2.10 POL: Registry.pol, gpt.ini and comment.cmtx bytes

| ID | Feature | Oracle source | Package | Status |
| --- | --- | --- | --- | --- |

### 2.11 ADMX: template handling

| ID | Feature | Oracle source | Package | Status |
| --- | --- | --- | --- | --- |

## 3. Declared differences

Each row is an intended difference from classic. The audit carries these over as declared differences.

| ID | Difference | Reason | Package | Status |
| --- | --- | --- | --- | --- |
| DIFF-001 | BWC opens read-only when the process is not elevated or the edition is Home. Classic refuses to open. | Showing current policy is useful and safe; writing is blocked. | G4, E3 | declared-difference |
| DIFF-002 | The Extended view pane is native. Classic shows an HTML page. | The HTML control is not available to a WinUI tool. | U2c | declared-difference |
| DIFF-003 | Save reads, modifies and writes under a file lock, so an edit made outside BWC is not overwritten. | Safer than classic's whole-file rewrite from a stale model. | E3 | declared-difference |
| DIFF-004 | A backup of the three GPO files (Registry.pol, gpt.ini, comment.cmtx) is kept before the first write of a session in `%ProgramData%\BetterWindowsConfig\gpedit\backup`, last 10. | Recovery from a bad edit; classic keeps no backup. | E3 | declared-difference |
| DIFF-005 | Imported templates live in the BWC store, not the system folder. Classic `.adm` files are not supported. | Keeps system files untouched and the store admin-only. | G6, E5 | declared-difference |
| DIFF-006 | Help Topics and About follow the precedent set by the Registry Editor. | Consistent with the existing tool. | U3b | declared-difference |

## 4. Open questions

The oracle must settle these before the engine is frozen. They are the same list as the policy engine contract and are not guessed.

- OQ-01. Record order inside a key when a policy writes several values.
- OQ-02. How a boolean that is false is stored when the template has no `falseValue`.
- OQ-03. How policy state is inferred from a partial or foreign Registry.pol.
- OQ-04. What Not Configured removes from a key shared by policies or lists.
- OQ-05. What is written when the file ends up with no records.
- OQ-06. How list values are named.
- OQ-07. When gpt.ini extension pairs are removed, and whether a comment-only change bumps the version.
- OQ-08. Which `**` instructions classic keeps or shows.
- OQ-09. The contents of the Extra Registry Settings node.

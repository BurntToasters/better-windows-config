# Better Windows Config: gpedit parity audit

## Method

This file holds the verdicts for `docs/parity/gpedit-inventory.md` (release 0.2.0, branch `next-0.2.0`). Each of the 534 inventory rows has one line below, and the 6 declared differences are listed again in their own section. Every verdict starts as `open`. The audit piece (A1) and the pieces that build each feature move a row to a verdict, in this file and in the inventory together, so the Status column of the inventory stays equal to this file.

Verdicts:

- `pass`: a named test or golden proves the row. E2E tests are named by method (`tests/Bwc.E2E`), engine tests by class (`tests/Bwc.Policy.Tests`, `tests/Bwc.Gpo.Tests`). A row that states no behavior says so.
- `declared-difference`: BWC differs on purpose. The reason is in the row.
- `gap`: not proven or not built. The Piece column names the work piece that closes it.
- `removed`: the row was retired by a user decision. It carries the reason and needs no further verdict.
- `open`: not judged yet.

Inputs for the verdicts: the capture goldens under `docs/parity/oracle/gpedit/goldens` (336 cases), the static dump, `string-parity.json` for MSG rows, and the visual review against the group F captures. The recapture of the 11 CAP-4 cases changes some recorded bytes; a POL or VIEW row that cites one of them stays `open` until the recapture is committed.

When a piece closes a gap, it changes the row here and in the inventory to `pass` with its test, or to `declared-difference` with the reason.

## Totals

| Section | pass | declared-difference | gap | removed | open |
| --- | --- | --- | --- | --- | --- |
| MENU | 0 | 0 | 0 | 1 | 40 |
| CTX | 0 | 0 | 0 | 0 | 11 |
| TOOL | 0 | 0 | 0 | 0 | 17 |
| DLG | 0 | 0 | 0 | 0 | 123 |
| ACC | 0 | 0 | 0 | 0 | 50 |
| MSG | 0 | 0 | 0 | 0 | 93 |
| VIEW | 0 | 0 | 0 | 0 | 43 |
| TREE | 0 | 0 | 0 | 1 | 19 |
| BEH | 0 | 0 | 0 | 1 | 28 |
| POL | 0 | 0 | 0 | 0 | 75 |
| ADMX | 0 | 0 | 0 | 0 | 32 |
| All (534) | 0 | 0 | 0 | 3 | 531 |

## Gaps

Engine rules that no capture settles are listed first (E6c). Each has a pointer comment in the source. After them come the known sources of gaps for the audit rows, to be judged by the audit and not yet verdicts.

| ID | Rule | What is missing | Where |
| --- | --- | --- | --- |
| GAP-01 | The value-name prefixes (`**del.`, `**soft.`) removed with their value when a value is replaced or cleared. | No capture sets or clears a value that has a soft or delete record next to it and then saves. | `PolOrderRules.PerValueInstructionPrefixes` |
| GAP-02 | The flag of the refresh call after a save. | The editor does not show which call it makes. | `UserenvPolicyRefresher` |
| GAP-03 | An additive list keeps the foreign values of its key. | B/Pol_List_Additive starts empty; no start has a foreign value at an additive key. | `PolicyRules.AdditiveListKeepsOtherValues` |
| GAP-04 | Enabling an additive list removes a delete-values record of its key. | No start has a delete-values record at an additive key. | `PolicyRules.AdditiveListRemovesDelvals` |
| GAP-05 | A tail after the last record that is not one stray character (odd length, several characters, a lone NUL) is refused. | D/File_TrailingBytes has one stray character only. | `PolReader.IsStrayTail` |
| GAP-06 | A key that a new record joins takes the spelling of its ancestors; on load two spellings of one key merge under the first. | D/Case_KeyDiffers and D/Case_KeyTwoSpellings show one level; deeper trees and ties between several first spellings are not captured. | `PolDocument.SpellNewKey` |
| GAP-07 | The namespaces that stay in comment.cmtx after the first save of a session. | Only the one-namespace case is captured (A/Machine_Enabled, A/User_Enabled). | `CmtxDocument.RestrictNamespacesTo` |

Sources of gaps for the audit rows:

- Static dialogs that no dumped menu opens (DLG layout rows with piece A1).
- Menu entries not yet decided (the Help web site entry) and menus not captured (the console tree context menu, the column header and description pane menus).
- The keys Alt+Enter, Delete and F1, not captured as steps.
- MSG blocks with absent strings in the parity check (see the parity counts in each MSG row).

| Piece | Rows |
| --- | --- |
| none yet |  |

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

The setting dialog differences in `docs/failure-modes/gpedit-dialog.md` (no maximize box, each radio button a tab stop, the module icon or a stand-in glyph) are candidates for declared differences. The audit decides them when it judges the DLG rows.

## Rows

### MENU

| ID | Verdict | Evidence or reason | Piece |
| --- | --- | --- | --- |
| MENU-001 | open |  | U3, G2 |
| MENU-002 | open |  | U3 |
| MENU-003 | open |  | U3b |
| MENU-004 | open |  | U3, G2 |
| MENU-005 | open |  | U3 |
| MENU-006 | open |  | G2 |
| MENU-007 | open |  | G2 |
| MENU-008 | open |  | G2, G5, G6 |
| MENU-009 | open |  | G2, G5 |
| MENU-010 | open |  | G2, G3a |
| MENU-011 | open |  | G6 |
| MENU-012 | open |  | G2, G3a |
| MENU-013 | open |  | G5 |
| MENU-014 | open |  | G5 |
| MENU-015 | open |  | G5 |
| MENU-016 | open |  | G2, G5 |
| MENU-017 | open |  | U3a |
| MENU-018 | open |  | U3b |
| MENU-019 | open |  | U3a |
| MENU-020 | open |  | U3a |
| MENU-021 | open |  | U3a, G5 |
| MENU-022 | open |  | U3a |
| MENU-023 | open |  | U3a |
| MENU-024 | open |  | U3a |
| MENU-025 | open |  | U3a |
| MENU-026 | open |  | U3a |
| MENU-027 | open |  | G5 |
| MENU-028 | open |  | G5 |
| MENU-029 | open |  | G5 |
| MENU-030 | open |  | U3a |
| MENU-031 | open |  | U3b |
| MENU-032 | open |  | U3b |
| MENU-033 | open |  | U3b |
| MENU-034 | open |  | U3b, A1 |
| MENU-035 | open |  | U3b |
| MENU-036 | open |  | U3b |
| MENU-037 | removed | Removed by the user decision of 2026-11-22. Classic shows no Favorites menu at run time, so BWC builds none. | none |
| MENU-038 | open |  | A1 |
| MENU-039 | open |  | A1 |
| MENU-040 | open |  | A1 |
| MENU-041 | open |  | A1 |

### CTX

| ID | Verdict | Evidence or reason | Piece |
| --- | --- | --- | --- |
| CTX-001 | open |  | G2 |
| CTX-002 | open |  | G2, U3a |
| CTX-003 | open |  | G2 |
| CTX-004 | open |  | G2 |
| CTX-005 | open |  | G2, G5 |
| CTX-006 | open |  | G2, G5 |
| CTX-007 | open |  | G2, G3a, G5 |
| CTX-008 | open |  | G2 |
| CTX-009 | open |  | G2, A1 |
| CTX-010 | open |  | A1 |
| CTX-011 | open |  | G2 |

### TOOL

| ID | Verdict | Evidence or reason | Piece |
| --- | --- | --- | --- |
| TOOL-001 | open |  | U3, G2 |
| TOOL-002 | open |  | U3 |
| TOOL-003 | open |  | U3, G2 |
| TOOL-004 | open |  | G2 |
| TOOL-005 | open |  | G2 |
| TOOL-006 | open |  | U3 |
| TOOL-007 | open |  | G2 |
| TOOL-008 | open |  | U3a |
| TOOL-009 | open |  | U3 |
| TOOL-010 | open |  | U3 |
| TOOL-011 | open |  | U3a |
| TOOL-012 | open |  | U3 |
| TOOL-013 | open |  | U3b |
| TOOL-014 | open |  | U3a |
| TOOL-015 | open |  | G5 |
| TOOL-016 | open |  | G2 |
| TOOL-017 | open |  | U3a |

### DLG

| ID | Verdict | Evidence or reason | Piece |
| --- | --- | --- | --- |
| DLG-001 | open |  | A1 |
| DLG-002 | open |  | A1 |
| DLG-003 | open |  | A1 |
| DLG-004 | open |  | A1 |
| DLG-005 | open |  | A1 |
| DLG-006 | open |  | A1 |
| DLG-007 | open |  | A1 |
| DLG-008 | open |  | A1 |
| DLG-009 | open |  | A1 |
| DLG-010 | open |  | A1 |
| DLG-011 | open |  | A1 |
| DLG-012 | open |  | A1 |
| DLG-013 | open |  | A1 |
| DLG-014 | open |  | A1 |
| DLG-015 | open |  | A1 |
| DLG-016 | open |  | A1 |
| DLG-017 | open |  | A1 |
| DLG-018 | open |  | A1 |
| DLG-019 | open |  | A1 |
| DLG-020 | open |  | A1 |
| DLG-021 | open |  | A1 |
| DLG-022 | open |  | A1 |
| DLG-023 | open |  | A1 |
| DLG-024 | open |  | A1 |
| DLG-025 | open |  | A1 |
| DLG-026 | open |  | A1 |
| DLG-027 | open |  | A1 |
| DLG-028 | open |  | A1 |
| DLG-029 | open |  | A1 |
| DLG-030 | open |  | A1 |
| DLG-031 | open |  | A1 |
| DLG-032 | open |  | A1 |
| DLG-033 | open |  | G6 |
| DLG-034 | open |  | G6 |
| DLG-035 | open |  | G5 |
| DLG-036 | open |  | A1 |
| DLG-037 | open |  | A1 |
| DLG-038 | open |  | U3b |
| DLG-039 | open |  | A1 |
| DLG-040 | open |  | A1 |
| DLG-041 | open |  | A1 |
| DLG-042 | open |  | A1 |
| DLG-043 | open |  | A1 |
| DLG-044 | open |  | A1 |
| DLG-045 | open |  | A1 |
| DLG-046 | open |  | U3b |
| DLG-047 | open |  | A1 |
| DLG-048 | open |  | A1 |
| DLG-049 | open |  | A1 |
| DLG-050 | open |  | A1 |
| DLG-051 | open |  | A1 |
| DLG-052 | open |  | A1 |
| DLG-053 | open |  | A1 |
| DLG-054 | open |  | A1 |
| DLG-055 | open |  | A1 |
| DLG-056 | open |  | A1 |
| DLG-057 | open |  | A1 |
| DLG-058 | open |  | A1 |
| DLG-059 | open |  | A1 |
| DLG-060 | open |  | A1 |
| DLG-061 | open |  | U3a |
| DLG-062 | open |  | A1 |
| DLG-063 | open |  | A1 |
| DLG-064 | open |  | A1 |
| DLG-065 | open |  | A1 |
| DLG-066 | open |  | A1 |
| DLG-067 | open |  | A1 |
| DLG-068 | open |  | A1 |
| DLG-069 | open |  | A1 |
| DLG-070 | open |  | A1 |
| DLG-071 | open |  | A1 |
| DLG-072 | open |  | A1 |
| DLG-073 | open |  | U3a |
| DLG-074 | open |  | A1 |
| DLG-075 | open |  | A1 |
| DLG-076 | open |  | A1 |
| DLG-077 | open |  | A1 |
| DLG-078 | open |  | A1 |
| DLG-079 | open |  | A1 |
| DLG-080 | open |  | A1 |
| DLG-081 | open |  | A1 |
| DLG-082 | open |  | A1 |
| DLG-083 | open |  | A1 |
| DLG-084 | open |  | A1 |
| DLG-085 | open |  | A1 |
| DLG-086 | open |  | A1 |
| DLG-087 | open |  | A1 |
| DLG-088 | open |  | A1 |
| DLG-089 | open |  | G3a |
| DLG-090 | open |  | G3a |
| DLG-091 | open |  | G3a, E6c |
| DLG-092 | open |  | G3a, E6a |
| DLG-093 | open |  | G3a |
| DLG-094 | open |  | G3a, G3b |
| DLG-095 | open |  | G3a |
| DLG-096 | open |  | G3a |
| DLG-097 | open |  | G3b |
| DLG-098 | open |  | G3a, G4 |
| DLG-099 | open |  | G3a |
| DLG-100 | open |  | G3a, E6c |
| DLG-101 | open |  | G3b, E6c |
| DLG-102 | open |  | G3b |
| DLG-103 | open |  | G3b |
| DLG-104 | open |  | G3b |
| DLG-105 | open |  | G3b |
| DLG-106 | open |  | G3b |
| DLG-107 | open |  | G3b |
| DLG-108 | open |  | G3b |
| DLG-109 | open |  | G3b |
| DLG-110 | open |  | G3b |
| DLG-111 | open |  | G3b |
| DLG-112 | open |  | G3b, E6c |
| DLG-113 | open |  | G5 |
| DLG-114 | open |  | G5 |
| DLG-115 | open |  | G6 |
| DLG-116 | open |  | G6 |
| DLG-117 | open |  | U3a |
| DLG-118 | open |  | U3a |
| DLG-119 | open |  | U3b |
| DLG-120 | open |  | U3b |
| DLG-121 | open |  | G6 |
| DLG-122 | open |  | G6 |
| DLG-123 | open |  | G3a, G6 |

### ACC

| ID | Verdict | Evidence or reason | Piece |
| --- | --- | --- | --- |
| ACC-001 | open |  | G2, A1 |
| ACC-002 | open |  | G2, U3b |
| ACC-003 | open |  | A1 |
| ACC-004 | open |  | G2 |
| ACC-005 | open |  | A1 |
| ACC-006 | open |  | G2 |
| ACC-007 | open |  | G2 |
| ACC-008 | open |  | A1 |
| ACC-009 | open |  | G2 |
| ACC-010 | open |  | G2 |
| ACC-011 | open |  | A1 |
| ACC-012 | open |  | A1 |
| ACC-013 | open |  | G2 |
| ACC-014 | open |  | G2 |
| ACC-015 | open |  | G2 |
| ACC-016 | open |  | G2 |
| ACC-017 | open |  | G2 |
| ACC-018 | open |  | G2 |
| ACC-019 | open |  | G2 |
| ACC-020 | open |  | G2 |
| ACC-021 | open |  | G2 |
| ACC-022 | open |  | G2 |
| ACC-023 | open |  | G2 |
| ACC-024 | open |  | G2 |
| ACC-025 | open |  | G2 |
| ACC-026 | open |  | G2 |
| ACC-027 | open |  | G2 |
| ACC-028 | open |  | G2 |
| ACC-029 | open |  | G2 |
| ACC-030 | open |  | G2 |
| ACC-031 | open |  | G2 |
| ACC-032 | open |  | G2 |
| ACC-033 | open |  | G2 |
| ACC-034 | open |  | G2 |
| ACC-035 | open |  | G2 |
| ACC-036 | open |  | G2 |
| ACC-037 | open |  | G2 |
| ACC-038 | open |  | G2 |
| ACC-039 | open |  | G2 |
| ACC-040 | open |  | G2 |
| ACC-041 | open |  | G2 |
| ACC-042 | open |  | G2 |
| ACC-043 | open |  | G2 |
| ACC-044 | open |  | G2 |
| ACC-045 | open |  | G2 |
| ACC-046 | open |  | G2 |
| ACC-047 | open |  | G2 |
| ACC-048 | open |  | G2, G3a |
| ACC-049 | open |  | A1 |
| ACC-050 | open |  | G2, A1 |

### MSG

| ID | Verdict | Evidence or reason | Piece |
| --- | --- | --- | --- |
| MSG-001 | open |  | A1 |
| MSG-002 | open |  | A1 |
| MSG-003 | open |  | A1 |
| MSG-004 | open |  | A1 |
| MSG-005 | open |  | A1 |
| MSG-006 | open |  | A1 |
| MSG-007 | open |  | A1 |
| MSG-008 | open |  | A1 |
| MSG-009 | open |  | A1 |
| MSG-010 | open |  | A1 |
| MSG-011 | open |  | A1 |
| MSG-012 | open |  | A1 |
| MSG-013 | open |  | A1 |
| MSG-014 | open |  | A1 |
| MSG-015 | open |  | A1 |
| MSG-016 | open |  | A1 |
| MSG-017 | open |  | A1 |
| MSG-018 | open |  | A1 |
| MSG-019 | open |  | A1 |
| MSG-020 | open |  | A1 |
| MSG-021 | open |  | A1 |
| MSG-022 | open |  | A1 |
| MSG-023 | open |  | A1 |
| MSG-024 | open |  | A1 |
| MSG-025 | open |  | A1 |
| MSG-026 | open |  | A1 |
| MSG-027 | open |  | A1 |
| MSG-028 | open |  | A1 |
| MSG-029 | open |  | A1 |
| MSG-030 | open |  | A1 |
| MSG-031 | open |  | G6 |
| MSG-032 | open |  | G6 |
| MSG-033 | open |  | G6 |
| MSG-034 | open |  | G1 |
| MSG-035 | open |  | G5 |
| MSG-036 | open |  | G5 |
| MSG-037 | open |  | G5 |
| MSG-038 | open |  | G5 |
| MSG-039 | open |  | A1 |
| MSG-040 | open |  | A1 |
| MSG-041 | open |  | A1 |
| MSG-042 | open |  | A1 |
| MSG-043 | open |  | A1 |
| MSG-044 | open |  | A1 |
| MSG-045 | open |  | A1 |
| MSG-046 | open |  | A1 |
| MSG-047 | open |  | A1 |
| MSG-048 | open |  | A1 |
| MSG-049 | open |  | A1 |
| MSG-050 | open |  | A1 |
| MSG-051 | open |  | A1 |
| MSG-052 | open |  | A1 |
| MSG-053 | open |  | A1 |
| MSG-054 | open |  | A1 |
| MSG-055 | open |  | A1 |
| MSG-056 | open |  | A1 |
| MSG-057 | open |  | A1 |
| MSG-058 | open |  | A1 |
| MSG-059 | open |  | A1 |
| MSG-060 | open |  | A1 |
| MSG-061 | open |  | A1 |
| MSG-062 | open |  | A1 |
| MSG-063 | open |  | A1 |
| MSG-064 | open |  | A1 |
| MSG-065 | open |  | A1 |
| MSG-066 | open |  | A1 |
| MSG-067 | open |  | A1 |
| MSG-068 | open |  | A1 |
| MSG-069 | open |  | A1 |
| MSG-070 | open |  | A1 |
| MSG-071 | open |  | A1 |
| MSG-072 | open |  | A1 |
| MSG-073 | open |  | A1 |
| MSG-074 | open |  | A1 |
| MSG-075 | open |  | A1 |
| MSG-076 | open |  | A1 |
| MSG-077 | open |  | A1 |
| MSG-078 | open |  | A1 |
| MSG-079 | open |  | A1 |
| MSG-080 | open |  | A1 |
| MSG-081 | open |  | A1 |
| MSG-082 | open |  | A1 |
| MSG-083 | open |  | A1 |
| MSG-084 | open |  | A1 |
| MSG-085 | open |  | A1 |
| MSG-086 | open |  | A1 |
| MSG-087 | open |  | A1 |
| MSG-088 | open |  | A1 |
| MSG-089 | open |  | A1 |
| MSG-090 | open |  | A1 |
| MSG-091 | open |  | G3a, G3b |
| MSG-092 | open |  | G3a, G3b |
| MSG-093 | open |  | G3a, G3b |

### VIEW

| ID | Verdict | Evidence or reason | Piece |
| --- | --- | --- | --- |
| VIEW-001 | open |  | U3, G2 |
| VIEW-002 | open |  | U3 |
| VIEW-003 | open |  | U3 |
| VIEW-004 | open |  | U3, G1 |
| VIEW-005 | open |  | U3, G1 |
| VIEW-006 | open |  | U3 |
| VIEW-007 | open |  | U3, G1 |
| VIEW-008 | open |  | G1 |
| VIEW-009 | open |  | G1, G5 |
| VIEW-010 | open |  | G1 |
| VIEW-011 | open |  | G1 |
| VIEW-012 | open |  | G1 |
| VIEW-013 | open |  | G1, G5 |
| VIEW-014 | open |  | G5 |
| VIEW-015 | open |  | G5 |
| VIEW-016 | open |  | U2c, G1 |
| VIEW-017 | open |  | U2c |
| VIEW-018 | open |  | U2c |
| VIEW-019 | open |  | G1 |
| VIEW-020 | open |  | G5 |
| VIEW-021 | open |  | G1 |
| VIEW-022 | open |  | G1 |
| VIEW-023 | open |  | G1, U2a |
| VIEW-024 | open |  | G1 |
| VIEW-025 | open |  | G1 |
| VIEW-026 | open |  | G3a |
| VIEW-027 | open |  | G3a |
| VIEW-028 | open |  | G3b |
| VIEW-029 | open |  | G3b |
| VIEW-030 | open |  | G3b |
| VIEW-031 | open |  | G3b |
| VIEW-032 | open |  | G3b |
| VIEW-033 | open |  | G3b |
| VIEW-034 | open |  | G3b |
| VIEW-035 | open |  | G3b |
| VIEW-036 | open |  | G3b |
| VIEW-037 | open |  | G3a |
| VIEW-038 | open |  | G6 |
| VIEW-039 | open |  | G6 |
| VIEW-040 | open |  | G6 |
| VIEW-041 | open |  | G6 |
| VIEW-042 | open |  | G5 |
| VIEW-043 | open |  | G2 |

### TREE

| ID | Verdict | Evidence or reason | Piece |
| --- | --- | --- | --- |
| TREE-001 | open |  | G1 |
| TREE-002 | open |  | G1 |
| TREE-003 | open |  | G1 |
| TREE-004 | open |  | G1 |
| TREE-005 | open |  | G1 |
| TREE-006 | open |  | G1 |
| TREE-007 | open |  | G1 |
| TREE-008 | open |  | G1 |
| TREE-009 | open |  | G1 |
| TREE-010 | open |  | G1 |
| TREE-011 | open |  | G1 |
| TREE-012 | open |  | G1 |
| TREE-013 | open |  | G1 |
| TREE-014 | open |  | G1 |
| TREE-015 | open |  | G1 |
| TREE-016 | open |  | G1 |
| TREE-017 | open |  | G1, U2a |
| TREE-018 | open |  | G1 |
| TREE-019 | open |  | G2 |
| TREE-020 | removed | Removed by the user decision of 2026-11-22. Classic shows no Extra Registry Settings node; records that match no policy stay invisible (BEH-014). | none |

### BEH

| ID | Verdict | Evidence or reason | Piece |
| --- | --- | --- | --- |
| BEH-001 | open |  | G1 |
| BEH-002 | open |  | G1, E2 |
| BEH-003 | open |  | G1 |
| BEH-004 | open |  | G1, E2 |
| BEH-005 | open |  | G4, E3 |
| BEH-006 | open |  | G4 |
| BEH-007 | open |  | G6, E5 |
| BEH-008 | open |  | G6, E5 |
| BEH-009 | open |  | G6, E2 |
| BEH-010 | open |  | G5, E5 |
| BEH-011 | open |  | G4, E3 |
| BEH-012 | removed | Retired by the user decision of 2026-11-22 (no Extra Registry Settings node); replaced by BEH-014. | none |
| BEH-013 | open |  | G2 |
| BEH-014 | open |  | E6c, G4 |
| BEH-015 | open |  | E6c, G1 |
| BEH-016 | open |  | G1, E6a |
| BEH-017 | open |  | G1 |
| BEH-018 | open |  | G1 |
| BEH-019 | open |  | G2, G3a |
| BEH-020 | open |  | G3a |
| BEH-021 | open |  | G1, U3a |
| BEH-022 | open |  | G2 |
| BEH-023 | open |  | U2c, G1 |
| BEH-024 | open |  | G1 |
| BEH-025 | open |  | G5 |
| BEH-026 | open |  | G4, G1 |
| BEH-027 | open |  | G4, E3 |
| BEH-028 | open |  | E6a, G4 |
| BEH-029 | open |  | E6b |

### POL

| ID | Verdict | Evidence or reason | Piece |
| --- | --- | --- | --- |
| POL-001 | open |  | E6a |
| POL-002 | open |  | E6a |
| POL-003 | open |  | E6a |
| POL-004 | open |  | E6a |
| POL-005 | open |  | E6a |
| POL-006 | open |  | E6a |
| POL-007 | open |  | E6a |
| POL-008 | open |  | E6a |
| POL-009 | open |  | E6a |
| POL-010 | open |  | E6b |
| POL-011 | open |  | E6b |
| POL-012 | open |  | E6b |
| POL-013 | open |  | E6b |
| POL-014 | open |  | E6b |
| POL-015 | open |  | E6b |
| POL-016 | open |  | E6b |
| POL-017 | open |  | E6b |
| POL-018 | open |  | E6b |
| POL-019 | open |  | E6b |
| POL-020 | open |  | E6b |
| POL-021 | open |  | E6b |
| POL-022 | open |  | E6b |
| POL-023 | open |  | E6b |
| POL-024 | open |  | E6b |
| POL-025 | open |  | E6b |
| POL-026 | open |  | E6b |
| POL-027 | open |  | E6b |
| POL-028 | open |  | E6b |
| POL-029 | open |  | E6b |
| POL-030 | open |  | E6b |
| POL-031 | open |  | E6a, E6b |
| POL-032 | open |  | E6a |
| POL-033 | open |  | E6a, E6b |
| POL-034 | open |  | E6b |
| POL-035 | open |  | E6b |
| POL-036 | open |  | E6b |
| POL-037 | open |  | E6a |
| POL-038 | open |  | E6a |
| POL-039 | open |  | E6a |
| POL-040 | open |  | E6a |
| POL-041 | open |  | E6a |
| POL-042 | open |  | E6a |
| POL-043 | open |  | E6b, E6c |
| POL-044 | open |  | E6c |
| POL-045 | open |  | E6c |
| POL-046 | open |  | E6c |
| POL-047 | open |  | E6c |
| POL-048 | open |  | E6c |
| POL-049 | open |  | E6c |
| POL-050 | open |  | E6c |
| POL-051 | open |  | E6c |
| POL-052 | open |  | E6c |
| POL-053 | open |  | E6c |
| POL-054 | open |  | E6c |
| POL-055 | open |  | E6c, G4 |
| POL-056 | open |  | E6c |
| POL-057 | open |  | E6c, G4 |
| POL-058 | open |  | E6c |
| POL-059 | open |  | E6c, G3b |
| POL-060 | open |  | E6c, G3b |
| POL-061 | open |  | E6c, G3b |
| POL-062 | open |  | E6c, G3b |
| POL-063 | open |  | E6c, G3b |
| POL-064 | open |  | E6c, G3b |
| POL-065 | open |  | E6c, G3b |
| POL-066 | open |  | E6c, G3b |
| POL-067 | open |  | E6c, G3a |
| POL-068 | open |  | E6a, G4 |
| POL-069 | open |  | E6a, G4 |
| POL-070 | open |  | E6a, G4 |
| POL-071 | open |  | E6a, G4 |
| POL-072 | open |  | E6a, G4 |
| POL-073 | open |  | E6a |
| POL-074 | open |  | E6a |
| POL-075 | open |  | E6c |

### ADMX

| ID | Verdict | Evidence or reason | Piece |
| --- | --- | --- | --- |
| ADMX-001 | open |  | G6, E5 |
| ADMX-002 | open |  | A3 |
| ADMX-003 | open |  | G6 |
| ADMX-004 | open |  | G6 |
| ADMX-005 | open |  | G6 |
| ADMX-006 | open |  | G6 |
| ADMX-007 | open |  | G6 |
| ADMX-008 | open |  | G6 |
| ADMX-009 | open |  | G6 |
| ADMX-010 | open |  | G6 |
| ADMX-011 | open |  | G6 |
| ADMX-012 | open |  | G6 |
| ADMX-013 | open |  | G6 |
| ADMX-014 | open |  | E2, E6b |
| ADMX-015 | open |  | E2, E6b |
| ADMX-016 | open |  | E2, E6b |
| ADMX-017 | open |  | E2, G3b |
| ADMX-018 | open |  | E6b, E6c |
| ADMX-019 | open |  | E2, G1 |
| ADMX-020 | open |  | E2 |
| ADMX-021 | open |  | E6a, E6c |
| ADMX-022 | open |  | G1, G5 |
| ADMX-023 | open |  | G6, E2 |
| ADMX-024 | open |  | E2, A1 |
| ADMX-025 | open |  | E2 |
| ADMX-026 | open |  | G6 |
| ADMX-027 | open |  | G6 |
| ADMX-028 | open |  | G6 |
| ADMX-029 | open |  | G6, E5 |
| ADMX-030 | open |  | G6 |
| ADMX-031 | open |  | E2, G3a |
| ADMX-032 | open |  | G5, E6c |

## Open questions

OQ-01 to OQ-09 are listed in section 4 of the inventory and stay open until the engine pieces (E6a, E6b, E6c) answer them from the POL rows. OQ-09 is answered by the user decision of 2026-11-22: no Extra Registry Settings node, records that match no policy stay invisible and are kept untouched (BEH-014).

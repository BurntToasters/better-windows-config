# Local Group Policy Editor tree and result list failure modes

Scope: piece G1 of release 0.2.0. In `src/Bwc.Gpedit`: the store and template choice (`Policy/GpeditStorePolicy.cs`), the model
load (`Policy/GpeditModel.cs`), the settings (`Policy/GpeditSettings.cs`), the console tree (`Tree/**`), the result list, the
description model and the status text (`List/**`), and the host wiring in `GpeditRootView`. The editor reads only: no save
wiring, no commands (pieces G2 to G6). Reads of the local policy object go through `Bwc.Gpo` (`GpoStore.Open`), templates
through `Bwc.Policy` (`TemplateSources`), states through `PolicyStateReader`.

Evidence (untracked, local): `artifacts/oracle/gpedit/probe.json` (steps `panes`, `navigate`, `result-list`, classic at 192
DPI, so every pixel value below is halved to DIPs) and `docs/parity/oracle/gpedit/gpedit-resources.md`. Oracle facts used:

- The tree has the root "Local Computer Policy" (the string `%s Policy` of gpedit.dll with "Local Computer"), expanded and
  selected at start; under it Computer Configuration and User Configuration, both expanded; under each, in this order,
  Software Settings, Windows Settings, Administrative Templates, all collapsed.
- The result list of Windows Settings (computer side) has one column "Name", 180 DIP wide, with the rows Name Resolution Policy,
  Scripts (Startup/Shutdown), Deployed Printers, Security Settings, Policy-based QoS, in that order.
- The result list of an Administrative Templates category has the columns Setting (350 DIP), State (100 DIP) and Comment (the
  rest, 139 DIP at the probe size). A policy row has the UIA name "<display name> setting", and its State cell is Enabled,
  Disabled or Not configured (admtmpl.dll strings 8, 9, 10). The Comment cell is "Yes" when the policy has a comment and "No"
  when it has none (admtmpl.dll strings 2001 and 2002). The status bar text of a category is "%1!d! setting(s)" (admtmpl.dll
  string 3126). The status bar is empty at the root.
- Classic starts on Local Computer Policy and does not restore a selection: a console opened in user mode saves no state, so
  there is no "last node" to persist (see section 11).

Declared differences from classic (the audit lists them): placeholder nodes open a BWC page that says the feature is not
available yet instead of the snap-in's own page; the description pane is native; the State cell of a side whose Registry.pol
cannot be read is empty (classic does not open in that case); icons that classic draws from image lists inside its modules
are BWC glyphs (section 12).

Not from the probe, so marked uncertain and kept in one table (`GpeditWindowsSettings`): the children of User Configuration >
Windows Settings (Scripts (Logon/Logoff), Deployed Printers, Security Settings, Policy-based QoS), whether Software Settings
shows an expander, the tree and list sort order (display name, native collation at the user locale, ignoring case, folders
before policies), and whether a category with no policy for the side is hidden (it is: classic shows only categories that hold
a policy of the side, the fixture README records the intent and the capture matrix F settles it). Each of these becomes a
golden when the capture matrix runs; until then the E2E tests assert the rule, not Microsoft data.

This file was written before the code. Each section lists the ways a behavior can fail, the design choice that prevents it and
the E2E check that catches it. Every `Gpedit_Tree_*` and `Gpedit_List_*` test writes a JSON measurement file and a picture of
the client area under `artifacts/e2e/visual/gpedit/` (`gpedit-<test>.json`, `gpedit-<test>.png`). The host is `BwcGpedit.exe`
of the E2E flavor with these seams (all compiled only under `BWC_E2E`):

| Variable | Meaning |
| --- | --- |
| `BWC_E2E_ADMX_ROOT` | the template folder that replaces the system folder (the F1 fixture) |
| `BWC_E2E_IMPORT_STORE` | the import store folder; unset means a folder under the run data root |
| `BWC_E2E_GPO_ROOT` | the sandbox policy object root, strictly inside the run data root; unset means deny-all and an empty read root |
| `BWC_E2E_GPEDIT_HOST` | `console-frame` keeps the placeholder frame host the Console_* tests use; unset is the real editor view |
| `BWC_E2E_GPEDIT_SHAPE` | `1` makes the host publish the whole tree as the UIA full description of `GpeditRoot` |

The host publishes its state as the UIA help text of `GpeditRoot` in the E2E flavor only (`Key=Value;` pairs; see section 13).

## 1. The store policy picks the real folder in a test run

The ship flavor must never read an environment variable that moves the policy root, and the E2E flavor must never point at
the real folder by accident.

- Handling: `GpeditStorePolicy` returns `SystemGpoStorePolicy` in the ship flavor and reads nothing from the environment. Under
  `BWC_E2E` it builds a `SandboxGpoStorePolicy` from `BWC_E2E_GPO_ROOT` with the run data root `AppPaths.BaseDirectory`
  (which is `BWC_DATA_ROOT`); a root outside the run root, a reparse point, an invalid path or a constructor failure becomes
  the deny-all policy and a recorded problem. An unset variable is deny-all. The deny-all policy names an empty read root
  inside the run data root (it does not exist), so reads see no files, writes and removes throw
  `GpoStoreException(PolicyDenied)`, and its refresher throws. Nothing in this piece writes: the store is only opened.
- Detector: `Gpedit_List_States` runs with a sandbox root and sees the seeded states; `Gpedit_Tree_SystemTemplates` runs with no
  root and sees every state empty or Not configured (no read of the real folder is needed) and the harness check
  `gpo.real-unchanged` stays green.

## 2. Template folders

- The ship system folder is `<Windows>\PolicyDefinitions` and the import store is
  `%ProgramData%\BetterWindowsConfig\PolicyDefinitions`, both read through `TemplateSources`; the store is loaded only when its
  ACL passes (the engine decides). In the E2E flavor an unset `BWC_E2E_ADMX_ROOT` falls back to the real system folder, read
  only, so one test can prove the real set loads; an unset `BWC_E2E_IMPORT_STORE` is a folder under the run data root, never
  the real ProgramData store.
- A missing system folder, an unreadable folder or an exception from the loader gives an empty `TemplateSet` and a recorded
  problem, never an exception on the UI thread. Cancellation (the window closes while the load runs) ends the load quietly.
- Detector: `Gpedit_Tree_BadTemplates` loads the fixture plus a malformed ADMX and an ADMX with no ADML: the tree has every
  good category, the readout lists the two failures.

## 3. The load blocks the window

3,403 policies in 222 files take time to read.

- Handling: the model load runs on the thread pool and the window shows the tree skeleton at once (roots, Software Settings,
  Windows Settings with its children, Administrative Templates with an expander). The Administrative Templates node loads its
  children through the tree's lazy loader, which awaits the model. A selection that needs the model (Administrative Templates
  and everything below it) shows an empty list until the model arrives, then refreshes the current node once.
- A stale result must not replace a newer one: the refresh looks at the current selection when the model arrives, and a list
  built for a node is dropped when the selection moved on before it was built.
- Detector: `Gpedit_Tree_SystemTemplates` reads `Loaded=1` and `LoadMs` within a bound (30 s, the real set loads in a few
  seconds), and the first root category row of Administrative Templates appears in UIA.

## 4. The category tree

Ways it can be wrong: a policy of class Machine shows under User Configuration; a class Both policy shows once; an empty
category shows; a category whose only policies are in a child is hidden; order is the file order; a category with the same
display name as a sibling replaces it; All Settings is missing, first, or inside a category; a cycle or a missing parent
crashes the build.

- Handling: per side, a category is shown when it or a descendant holds a policy that applies to the side (`AppliesToMachine`,
  `AppliesToUser`; class Both is both). A policy is a row in its own category only. Siblings sort by display name with the
  user-locale collation, ignoring case, ties by the engine's source order. A category the linker cut out of a cycle or whose
  parent is undefined (`IsDetached`) is not shown; the policies keep their All Settings rows. All Settings is the last child
  of Administrative Templates on both sides. Child nodes of a category are created once on first need and cached on the node.
- Detector: `Gpedit_Tree_Shape` compares the whole tree (the full description of `GpeditRoot`) with the expected fixture tree:
  BWC Oracle shows under both sides, Machine only and Shared keys only on the computer side, User only and User nested only on
  the user side, Intentionally empty on neither, All Settings last.

## 5. Initial state and the placeholder nodes

- The tree starts with the root selected, the root and the two configuration nodes expanded and every other node collapsed.
  Software Settings has no children. Windows Settings has its placeholder children. Placeholder nodes have no children.
- Selecting a placeholder (Software Settings or a Windows Settings child) puts the BWC "not available yet" page in the result
  pane in place of the list and the list's rows are cleared. Selecting any other node puts the list back. The page keeps the
  Extended tab strip so the pane layout does not jump, names the node, and is one UIA element with the id
  `GpeditPlaceholderPage`. The Windows Settings node itself shows a list of its children (a single Name column) and the
  description text of classic (gpedit.dll 254 and 256), and double click or Enter on such a row selects that child.
- Focus: a tree click or key keeps the focus in the tree. When the list goes away while it held the focus, the focus moves to the
  result pane target the shell already tracks.
- Detector: `Gpedit_Tree_PlaceholderPages` selects Software Settings, a Windows Settings child on each side and a category in
  turn and checks which of page and list exists after each, and that the page names the node.

## 6. Rows

Ways it can be wrong: folders after policies; a policy row without the word "setting" in its UIA name or a folder row with it;
State shows the wrong side's file for a class Both policy; State shows Enabled for a partial file; a Comment from another
policy; a row object reused after the node changed; 3,403 rows read the state at once.

- Handling: rows of a category are its visible child categories (folder rows, plain name, no State or Comment) then its policies
  for the side (`ConsoleListRow` with Setting, State, Comment). The UIA name comes from `ItemNameFormatter` and the row's tag
  (policy rows get " setting"). The state of each policy is read from the Registry.pol of the node's side with
  `PolicyStateReader.Read` (a class Both policy is read from the document of the node's side). The comment is
  `CmtxDocument.GetComment(policy.Namespace, policy.Name)`: any non-empty text is "Yes". A pol or cmtx that did not load gives
  an empty State or "No". All Settings lists every policy of the side (`AllSettings.List`) sorted by display name, with the
  same three columns (the Path column and the filter belong to G5).
- Rows are rebuilt from the model on every selection; no row object is shared between nodes. Reading a state is a small
  document lookup, only for the rows of the node.
- Detector: `Gpedit_List_Category` (names, order, columns, folder rows), `Gpedit_List_States` (Enabled, Disabled, Not configured,
  a class Both policy that differs per side, Comment Yes and No), `Gpedit_List_UnreadablePolicyFile` (a garbage file gives empty
  States, Comment still works, a problem is recorded, the app stays alive).

## 7. Read failures never reach the UI thread

- `GpeditModel.Load` catches every exception except cancellation and turns it into a `GpeditProblem` (kind, path, detail).
  Template load failures come from `TemplateSet.Failures`; a template store that is not ready, an unreadable `Registry.pol`
  (`GpoSideSnapshot.PolFailure`), an unreadable `comment.cmtx`, an unreadable `gpt.ini` and a thrown exception are the other
  kinds. G6 shows the list; this piece only records it and counts it in the readout.
- Event handlers of the host catch exceptions of their own work, log them with `Bwc.Core.Log` and keep the previous view.
- Detector: the bad template and garbage file tests above; the app answers UIA afterwards.

## 8. The Extended pane

Ways it can be wrong: a policy with no help text or no supported-on text breaks the layout; the Edit link does nothing; the
category text shows for a policy; the pane keeps the text of the previous selection after the node changed.

- Handling: one policy row selected: title is the display name, link "Edit policy setting", sections Requirements (the
  supported-on display text, empty text shows the label only) and Description (the explain text). One folder row selected: its
  title and explain text, no link. Nothing selected: the selected tree node's category (title, explain text) or, for other
  nodes, the classic hint for navigation nodes (the gpedit.dll descriptions 251 to 256 and admtmpl.dll 236) or "Select an item
  to view its description." The Edit link, Enter and double click on a policy row raise `PolicyEditRequested` with the policy and
  the side; piece G3a handles it. Until then nothing subscribes and the event is a no-op.
- Detector: `Gpedit_List_ExtendedPane` selects a policy with help text, one without, a folder row and a category and compares
  the pane's UIA text; it invokes the link and reads the event counter in the E2E readout.

## 9. Status bar

- A category and All Settings show "N setting(s)" with N the number of policy rows; every other node shows nothing. The text is
  set on every selection and when the model arrives. Nothing else writes the status bar in the real host (the frame host's
  command echo stays only in the `console-frame` mode).
- Detector: `Gpedit_List_StatusBar`.

## 10. The frame test host

The Console_* tests need the placeholder tree, the synthetic rows (`BWC_E2E_CONSOLE_ROWS` and friends) and the command echo in
the status bar. Those moved, unchanged, behind `BWC_E2E_GPEDIT_HOST=console-frame` (file `GpeditRootView.E2EFrame.cs`, E2E
flavor only). `tests/Bwc.E2E/Console/ConsoleShellSupport.cs` sets the variable for every Console launch; nothing else in the
tests changes. In the ship flavor the host is the real editor view only.

- Failure: a Console test launched without the variable would show the real tree. Detector: the Console suite stays green.

## 11. Persistence

Persisted in `%LOCALAPPDATA%\BetterWindowsConfig\gpedit\settings.json` (the data root under test) through `Bwc.Core`'s
`SettingsStore`: the window rectangle (physical pixels, maximized flag), the tree pane width, the column widths by id, the view
mode and the Extended or Standard tab. The last selected node is not persisted: classic opens on the root every time and
the regedit-style "LastKey" has no classic counterpart here.

Ways it can fail: a corrupt, huge or hand-edited file; a window saved off screen; a width of 0, negative, NaN or 10^9; an unknown
view mode or tab name; a write failure; saving on every pixel of a drag; the head's own load handler resizing the window after
the view restored it.

- Handling: the store recovers a corrupt file (the bad file is kept beside it). Every restored number is range checked and a bad
  one is ignored. Placement is fitted into the work area of the nearest display (the size is reduced, then moved inside). The
  placement is applied after the head's Loaded handler ran (dispatcher, low priority) and only once. Saving is debounced by
  500 ms and runs at window closing; a failure is logged and never thrown.
- Failure mode (0.2.0 audit): a file that holds `null` for a map (`"ColumnWidths": null` or `"Sorts": null`) makes the JSON reader
  replace the empty default, and the view iterated the map directly: a `NullReferenceException` in the constructor stopped the
  window at start-up. Handling: a null map is the same as no entry. Detector: `Gpedit_Persistence_BadSettingsFile` starts
  with `{ "ColumnWidths": null, "Sorts": null, "HiddenColumns": null, "ColumnOrder": null }`.
- Detector: `Gpedit_Persistence_Restart` changes the tree width, a column width, the view mode and the tab, moves and resizes the
  window, closes it, starts it again with the same data root and compares; it also checks the root is selected again.

## 12. Icons

Classic draws its tree and folder icons from image lists (resource bitmaps) inside `gpedit.dll` and `admtmpl.dll`, which a
`ModuleIcon` (a group icon reference) cannot address. The icon ids recorded from the resources are: `admtmpl.dll` group icon 4
(a page of lines, a policy setting), group icon 6 (the filter funnel, used by G5), group icons 1 to 3 (a user with a computer,
and two folder style icons), `gpedit.dll` group icons 1 to 3 and 5 (script style status icons and an information icon).
Handling: policy rows use `ModuleIcon.ById("admtmpl.dll", 4)` with the string value glyph as fallback; nodes and folder rows use
the BWC folder and computer glyphs (`IconKind.ClosedFolder`, `IconKind.Computer`). A missing module gives the fallback and no
error. This is an open item for the audit (tree icons).

## 13. The E2E readout

Under `BWC_E2E` the help text of `GpeditRoot` is `Mode=gpedit;Loaded=0|1;LoadMs=n;Templates=n;Categories=n;PolicyCount=n;
Failures=n;Problems=n;ProblemList=kind/detail/file|...;Store=sandbox|denyall|system;Node=a > b;Page=list|placeholder;
Layout=Navigation|Settings;Rows=n;ShownFolders=n;ShownPolicies=n;Status=text;Links=n;Edits=n;LastEdit=namespace:name`. With
`BWC_E2E_GPEDIT_SHAPE=1` the full description of `GpeditRoot` holds the whole tree as indented text.
It never exists in the ship flavor (a screen reader would announce it). Values never contain `;`: a `;` in a name is shown as `,`.
A test reads it with UIA, so the tests prove what the host believes and the UIA tree proves what is shown.

## 14. Findings of the first E2E runs

- A template that fails to load can still contribute categories: the engine keeps what parsed before the failure (DuplicatePolicy,
  UndefinedString and UndefinedUsing each show a "Bad fixture ..." category). The tree shows what the engine returns and the
  failure is a recorded problem. `Gpedit_Tree_BadTemplates` therefore adds only the cases that contribute nothing (MalformedXml,
  DtdEntity, MissingAdml) and expects the good tree unchanged.
- The toolbar Up One Level button selects the parent node in the real host (the frame host still only echoes the command).
- A settings file with absurd numbers or unknown names is ignored per value (`Gpedit_Persistence_BadSettingsFile`).

## 15. Blind review additions

Added by the blind test pass (worker R2), derived from sections 1 to 14 only, before any product code or the first tests were read.
Tests are in `tests/Bwc.E2E/Gpedit/Blind/GpeditTreeListBlindTests.cs` (`Gpedit_TreeBlind_*`, `Gpedit_ListBlind_*`).

- **Zero-row categories (section 9).** A category that holds only subcategories (computer side BWC Oracle, user side User only)
  has zero policy rows. Failure: the status bar is empty, or shows a count of the folder rows. Rule: the text is the fixed
  format with N = 0 ("0 setting(s)"), and the format has no singular form ("1 setting(s)").
- **Literal ampersand (sections 4, 6, 8, 9).** A name with `&` (category "Tools & Utilities", policy "Research & Development
  flag") must show as written in the tree, the folder row, the policy row, the description title and the readout. Failure:
  the `&` is dropped or the next letter becomes a mnemonic.
- **Order inside a category (sections 4, 6).** Folder rows come first, each group sorted by display name ignoring case, ties in
  source order ("alpha policy" before "Alpha Policy": same name ignoring case, defined in that order). The rule for runs of digits
  ("Policy 10" against "Policy 2") is not stated: the doc says native collation only, so it stays an open question until the
  capture matrix settles it. The leading `#` and the accented capital are only checked against their neighbors in the fixture.
- **Which side's rows (section 4, 6).** All Settings holds every policy of the side exactly once: with the fixture 59 rows on the
  computer side and 13 on the user side (70 policies, 2 of class Both). A class Machine policy never shows on the user side, a
  class User policy never on the computer side.
- **Tree expand and collapse (sections 4, 5).** Collapsing and expanding a node again gives the same children (no duplicates,
  none lost; the children are cached on the node). A double click on a folder row selects that child and leaves its parent
  expanded in the tree. The Right and Left keys expand and collapse the selected node, Up and Down move the selection and the
  list follows each move.
- **Node trail after a placeholder (section 5).** Leaving a placeholder page for a category brings back the list with the Setting,
  State and Comment headers and the rows of the category (a stale single-column Name header from the Windows Settings list, or
  the placeholder page left on screen, is the failure).
- **Absent policy file (sections 1, 6).** A side that has no policy file at all (a sandbox with nothing seeded) is not an
  unreadable file: nothing is Enabled or Disabled and no problem about that side is expected. The doc names only the unreadable
  case (empty State) and the deny-all case ("empty or Not configured"), so whether classic's "Not configured" or an empty cell
  shows for an absent file is open; the test accepts either and fails on Enabled or Disabled.
- **Sandbox root outside the run root (section 1).** The deny-all rule for a root outside the run root has its own detector: the
  readout says `Store=denyall` with at least one problem and the tree still loads.
- **Missing template folder (section 2).** `BWC_E2E_ADMX_ROOT` pointing to a folder that does not exist gives `Templates=0`, a
  problem, the skeleton tree and a live window. Administrative Templates shows no BWC Oracle.
- **Persistence keys (section 11).** The doc lists the persisted values but not the key names of `settings.json`. The tests do not
  assume names: they read the file the product wrote and rewrite every number in it to a bad value (0, negative, 10^9) and
  every string to an unknown name, then require a normal start. Failure: a crash, an invisible or off-screen window, a zero
  width pane.
- **Selection is not persisted (section 11).** Selecting a category, closing and starting again shows the root selected.
- **Fallback wording (section 8), settled.** The description pane text for a node with no explain text is classic's "Select an
  item to view its description." (artifacts/oracle/gpedit/probe.json, 247 occurrences), not "see". It shows on the root node
  ("Local Computer Policy"), on a category or folder node with no explain text (for example Elements), and for the same
  folder when it is a selected row of the list. A folder with explain text shows that text instead. Navigation nodes other than
  the root (Computer Configuration, User Configuration, Administrative Templates, All Settings) also show some hint text with
  their own title and no Edit link (`Gpedit_ListBlind_ExtendedPane`); the fallback wording applies to every node that has no
  text of its own. Detector: `Gpedit_ListBlind_ExtendedPaneFallbackWording`.
- **Default column widths and persisted widths are DIPs (section 11).** Name 180, Setting 350, State 100, Comment 139 DIP at any
  DPI, and `settings.json` stores column widths in DIPs (a drag adds DIPs, a restart shows the same DIP width, nothing is
  scaled twice). Detectors: `Gpedit_ListBlind_DefaultColumnWidthsInDip` and `Gpedit_ListBlind_PersistedColumnWidthIsDip`. The
  test process must be per monitor DPI aware (`ControlTestSupport.PrepareDpi`), or UI Automation rectangles come back
  virtualized at half size on a 192 DPI machine and a correct product looks like Name 90 DIP.

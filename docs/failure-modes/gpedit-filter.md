# Local Group Policy Editor filter and sort failure modes

Scope: piece G5 of release 0.2.0. In `src/Bwc.Gpedit`: the Filter Options dialog, Filter On (a toggle), Re-Apply Filter, the
filtered result lists (policy nodes and All Settings), folders with no matching policy hidden while the filter is on, the
status bar filter flag and narrowed count, the filter entries of the Action menu, the View menu, the list and tree context
menus and the toolbar Filter button, and header-click sort with its saved state. The filter engine is `src/Bwc.Policy/Filter`
(`PolicyFilter`, `FilterOptions`, `FilterRules`, `AllSettings`, `PlatformList`; `docs/failure-modes/policy-filter.md`). The
UI must use that engine and keep no second filter.

This file was written before the code (tests first). Each section lists the ways the feature can fail, the inventory row, the
capture, the design choice that prevents it and the E2E check that catches it. The tests are in
`tests/Bwc.E2E/Gpedit/Filter/`. Every test writes `artifacts/e2e/visual/gpedit/gpedit-<test>.json` and a picture, and its checks
(`gpedit.filter.<name>.<id>`, `gpedit.sort.<name>.<id>`) to the E2E result artifact.

## Specification sources

A capture is the specification; Microsoft text is never copied: rows are named by fixture names, menus and the dialog by
command role and BWC's own labels.

| Capture (under `docs/parity/oracle/gpedit/goldens/F`) | What it settles |
| --- | --- |
| `Filter_Options` | 17 filter settings on the `Sort cases` node after four saves; the dialog as it stood after OK; the rows read after each; the hidden nodes (ops 27, 55, 67, 72); OK turns Filter On on by itself (op 7); the refusal of a requirements filter with no platform (op 62) |
| `Sort_Columns` | each column clicked three times with the order after each click; the order after the last click; another node read after the clicks |
| `AllSettings_Computer`, `AllSettings_User` | the All Settings node: headers, layout, the toolbar with the filter button; the counts and rows are untracked (they depend on the machine's own templates) |
| `Menus_Computer`, `Menus_User` | the entries per node kind of the Action menu, the View menu and the context menu |
| `Toolbar_States` | the toolbar per node kind and with a policy row selected |

Inventory rows: MENU-008, MENU-009, MENU-010, MENU-014, MENU-021, MENU-028, CTX-005, CTX-007, TOOL-015, DLG-035, DLG-113,
DLG-114, VIEW-013 to VIEW-015, VIEW-020, VIEW-042, BEH-010, BEH-025, ADMX-032.

What the captures say, by role, and what BWC does:

| Role | Classic (captures) | BWC at G5 |
| --- | --- | --- |
| Filter On (toggle), Filter Options, Re-Apply Filter | Action menu at Administrative Templates, category nodes, All Settings and with a policy row selected; also in the View menu there; in the list context menu there (in a View submenu, and as copies in the tasks submenu). Absent at the configuration nodes and the placeholders (MENU-007 to MENU-010, MENU-013 to MENU-015, MENU-021, CTX-005, CTX-007). Re-Apply is grayed in every dump (no filter was on). Identical on both sides (CTX-008) | the classic structure, from the captures: flat entries and a tasks submenu copy in the Action popup, flat entries in the View menu between the view modes and Customize View, a View submenu with the entries in the list context menu without a row, flat entries plus a tasks submenu with a policy row; the console tree menu (not captured, CTX-009) gets the three flat entries at the same node kinds |
| Filter button | present at the same node kinds, last after a separator, enabled and not checked while no filter is on (TOOL-015) | the button `ConsoleToolbar_Filter` at the same node kinds, pressed while the filter is on |
| Filter is on after OK | the dialog was closed with OK and the toggle was already checked (op 7) | the same |
| Sort | a header click sorts the rows in the order they are shown now (a tie keeps that order); first click ascending, second descending, third ascending (ops 6 to 14) | the same; see section 14 |

Decisions of this file where the captures are silent (every one is listed again in the audit, as declared differences or
open items):

- Re-Apply Filter is enabled while Filter On is on and grayed while it is off. Every dump has the filter off, so only the
  grayed state is captured.
- The status bar text is BWC's own words. While no filter is on it holds one number, the count of policy rows (VIEW-019);
  while Filter On is on at a list node it holds two numbers, shown and total, and the status bar shows a filter flag in a
  second pane. Classic's own text is not tracked; the capture's `statusNumbers` of op 64 read `[8, 8]` for a filter that
  hides nothing.
- Select All of the requirements filter selects the supported-on definitions of the loaded templates (the engine's
  `PlatformList`). Classic listed the products of the machine's own templates, so op 66 (every platform) hid every folder
  there and shows every row here (`FC11` in `policy-filter.md`).

## The E2E seam

Compiled only under `BWC_E2E`. The readout is the UIA help text of `GpeditRoot` (`Key=Value;`, see `gpedit-tree-list.md`
section 13). G5 adds three keys to the existing ones:

| Key | Meaning |
| --- | --- |
| `FilterOn` | 1 while Filter On is on, otherwise 0 |
| `FilterShown` | the number of policy rows the current list shows (equals `ShownPolicies`) while the filter is on, otherwise equals `FilterTotal` |
| `FilterTotal` | the number of policy rows the current list would show with no filter |

The list control already publishes `Sort=<column id or none>` and `SortDir=<None, Ascending or Descending>` in its own help
text (`ConsoleList`, `Console_List_SortBothWays`); the sort tests read those. The header items report the sort as the UIA item
status `Sorted ascending` or `Sorted descending` (`ConsoleListHeader_<column id>`; column ids `setting`, `state`, `comment`,
and `path` for All Settings).

New automation ids (in the style of the existing ones):

| Id | Control |
| --- | --- |
| `MenuCommand_2004`, `MenuCommand_2005`, `MenuCommand_2006` | Filter On (a checkable entry), Filter Options, Re-Apply Filter in the Action popup (59002) and the View popup (59003) |
| `ContextMenuCommand_2004`, `ContextMenuCommand_2005`, `ContextMenuCommand_2006` | the same commands as flat entries in the list context menu (with a policy row) and the tree context menu |
| `MenuCommand_2101`, `ContextMenuCommand_2101` | the tasks submenu (a sub item): in the Action popup at the nodes with the filter and with a policy row, in the policy row context menu |
| `TasksCommand_2001` (Edit, policy row only), `TasksCommand_2004`, `TasksCommand_2005`, `TasksCommand_2006` | the entries inside the tasks submenu; the same command ids as the flat entries, another prefix so that the two places stay tellable |
| `ContextMenuCommand_2100` | the View submenu of the list context menu without a row, at the nodes with the filter; its filter entries are `ViewSubmenuCommand_2004`, `ViewSubmenuCommand_2005`, `ViewSubmenuCommand_2006`, after the view modes and before Customize View |
| `ConsoleToolbar_Filter` | the toolbar Filter button; it has the Invoke and the Toggle pattern; ToggleState On while the filter is on |
| `StatusBar.Pane1` | the status bar filter flag; it exists and has a name only while Filter On is on |
| `GpeditFilterOptions` | the root of the Filter Options dialog (its own top level window of the editor process) |
| `GpeditFilterOptions.Managed`, `.Configured`, `.Commented` | the three combo boxes (items Any, Yes, No) |
| `GpeditFilterOptions.KeywordsEnabled`, `.KeywordText`, `.KeywordMatch` | the keyword check box, the text box and the match combo (items Any, All, Exact) |
| `GpeditFilterOptions.InTitle`, `.InHelp`, `.InComment` | the three Within check boxes |
| `GpeditFilterOptions.RequirementsEnabled`, `.PlatformMatch`, `.PlatformList`, `.SelectAll`, `.ClearAll` | the requirements check box, the match combo (items Any, All), the platform list (list items with the Toggle pattern, named by the text of the supported-on definition), and the two buttons |
| `GpeditFilterOptions.Ok`, `.Cancel` | OK and Cancel |
| `GpeditFilterOptions.Message` | the message box over the dialog; its UIA name is its text |

Command ids: Filter On 2004, Filter Options 2005, Re-Apply Filter 2006; the same id in every menu (one command is one id).
Labels start with `Filter On`, `Filter Options` and `Re-Apply Filter`.

## 1. The dialog has the wrong parts or the wrong layout (DLG-035, DLG-113)

Capture: `Filter_Options` (the dialog read before and after each OK); `admtmpl.dll` dialog 3500 (330 x 351 dialog units, 29
controls, tab order 609, 3501 to 3503, 3504, 3515, 3505, 3517, 3516, 3506 to 3508, 3509, 608, 3514, 3510 to 3513, OK, Cancel).

- A part is missing: Managed, Configured, Commented, the keyword text, the match combo, a Within box, the platform list,
  Select All, Clear All, the requirements match combo.
- A combo has other items or another order than Any, Yes, No (match: Any, All, Exact; requirements: Any, All).
- The keyword details are enabled while their check box is off, or stay disabled after it is ticked; the same for the
  requirements details (the capture shows them disabled until their check box is ticked, enabled afterwards).
- The platform list is empty, holds the machine's products instead of the loaded templates' definitions, or Select All and
  Clear All do not tick and untick every entry.
- The window is far from 495 x 570 DIP (330 x 351 units at the classic font), or its parts are in another reading order
  (three combos, keyword group, requirements group, buttons; OK left of Cancel), or the Tab order differs from DLG-035.

Handling: one dialog built from the table above, the platform list from `PlatformList.From`. Check: `Gpedit_Filter_Dialog_Layout`
(`part.*`, `*Items`, `alwaysEnabled`, `detailsOffAtStart`, `keywordDetailsOn`, `platformDetailsOn`, `platformNames`, `selectAll`,
`clearAll`, `size`, `keywordsBelowCombos` to `buttonsLast`, `tabOrder`).

## 2. The dialog refuses wrongly or not at all (DLG-114)

Capture: op 62 (requirements on, no platform: a message box, the dialog not closed, Filter On off, the list unfiltered with
all eight rows); the other two refusals are from `admtmpl.dll` strings 3122 and 3123.

- OK with the keyword filter on and no word accepts the options (an empty filter that hides everything or nothing).
- OK with the keyword filter on and no Within box ticked accepts them.
- OK with the requirements filter on and no platform accepts them.
- The refusal closes the dialog, or keeps it open but turns Filter On on, or filters the list before the options are valid.
- Two refusals share one text (the user cannot tell the cause).
- After the cause is fixed OK is still refused, or the message box blocks the dialog for good.

Handling: `FilterOptions.Validate` and `PolicyFilter.TryCreate` decide; the dialog maps each `FilterProblem` to its own
message box. Check: `Gpedit_Filter_Dialog_Refusals` (`keywordNoWord`, `keywordNoWithin`, `requirementsNoPlatform`, each with
`message`, `dismissed`, `dialogStaysOpen`, `filterStaysOff`, `cancelCloses`, `listUnfiltered`, `flagOff`; `messagesDiffer`;
`fixed.*`).

## 3. The dialog does not remember, or Cancel changes something

Capture: the dialog read at the start of each `setFilter` and `clearFilter` shows the options of the last OK (op 37 and
following: keywords on, the text and the match as accepted).

- The dialog opens with the defaults each time instead of the last accepted options.
- Cancel applies the changes made in the dialog, or turns Filter On on.
- Switching Filter On off forgets the options.
- OK does not turn Filter On on (op 7: it is on after OK).

Check: `Gpedit_Filter_Dialog_RemembersAndCancels` (`onAfterOk`, `rememberedConfigured`, `rememberedKeywords`,
`rememberedWithin`, `cancelKeepsRows`, `optionsKeptWhileOff`, `cancelDidNotTurnOn`).

## 4. A filter setting shows other rows than classic (VIEW-042, ADMX-032, BEH-010)

Capture: `Filter_Options` ops 6 to 69, replayed by the test for each `setFilter` operation, with the dialog values the capture
recorded after OK. The seed is the four saves of the case (Zulu sort case Enabled, alpha policy Disabled, Policy 10 Not
Configured with a comment, Policy 2 Enabled with a comment) written into the sandbox by `FilterSeed.SortCases`.

- The UI builds its own predicate and differs from `PolicyFilter` (Configured counts Not Configured with a comment, Commented
  counts the state, Managed mixes up the key roots, Match All finds words in different texts, Exact matches `Policy 1` for
  `Policy 2`, a keyword is searched in an unticked text).
- The rows are read from the state before the last save (a stale snapshot).
- The list shows rows of other nodes, or a hidden policy row stays listed.
- The filter is built from the options typed into the dialog and not from the options the dialog accepted.
- The requirements filter with every platform (op 66) differs from the engine: BWC expects all eight rows (see the decisions).

Check: `Gpedit_Filter_CaptureReplay` (`opNN.applied`, `.on`, `.rows`, `.status`, `.flag`, `.nodeHidden`, `.off`, `.backToAll`;
the unfiltered list `unfiltered.rows`, `unfiltered.status`). Operation 62 is section 2, operation 70 is section 5.

## 5. Filter On is not kept, or does not toggle (VIEW-042)

Capture: ops 70 to 72 (the filter stays on when another node is selected); ops 9, 13 and following (Filter On off returns
the whole list); ops 62 to 64 (Filter On with no restriction lists every row).

- Filter On works only at the node where it was set, or turns off at the next selection.
- It turns off at a node that has no filter entries (the configuration nodes) and is lost on the way back.
- A filter with no restriction hides rows, or shows no flag and a one-number status.
- Switching it off leaves rows hidden or the flag showing.
- The user side has its own filter (the options are one set for the editor).

Check: `Gpedit_Filter_OnOffAndStaysOn` (`start.off`, `toggleOn`, `on.rows`, `on.status`, `on.flag`, `on.counts`, `stays.node`,
`stays.user`, `stays.configNode`, `toggleOff`, `off.status`, `button.*`).

## 6. The status bar flag and count are wrong (VIEW-020, VIEW-019)

Capture: `statusNumbers` of the unfiltered list (`[8]`, op 5) and of the filter that hides nothing (`[8, 8]`, op 64). The
text itself is not tracked.

- The count does not narrow with the list (the status says 8 while 3 rows show).
- The count lists folder rows, or counts the hidden policies.
- The flag shows while the filter is off, or does not show while it is on (`StatusBar.Pane1`).
- The numbers are not shown and total (`3` and `8`) in this order.
- The count does not change when the node changes.

Check: `status` and `flag` of `Gpedit_Filter_CaptureReplay`, `Gpedit_Filter_OnOffAndStaysOn`, `Gpedit_Filter_Results_AllSettings`,
`Gpedit_Filter_FoldersWithNoMatchAreHidden` (`basics.status`).

## 7. Folders with no matching policy are shown, or the wrong ones hide (VIEW-042, BEH-010)

Capture: ops 27, 55, 67, 71 and 72 (`nodeHidden`, `parentChildren`, `selectedInstead`): a node with no matching policy in its
own list and below is hidden in the tree and in the parent's list; the selected node that gets hidden is replaced by its
nearest visible ancestor; All Settings is never hidden; the node and its tree item come back when the filter goes off.

- The tree and the list keep a folder whose subtree has no match.
- A folder that has a match only in a deeper category is hidden (BWC Oracle keeps Basics).
- The selected node is hidden and the list keeps showing it, or the window selects nothing, or the root.
- All Settings is hidden when nothing matches (it is shown empty).
- The folder row count (`ShownFolders`) differs from the tree.
- After Filter On goes off a hidden folder is not restored in the tree or in the list.
- The hidden folder is opened through a stale folder row.

Check: `Gpedit_Filter_FoldersWithNoMatchAreHidden` (`rows.onlyBasics`, `tree.onlyBasics`, `node.stays`, `hiddenSelected.moved`,
`hiddenSelected.noRow`, `off.folders`, `off.tree`), `Gpedit_Filter_NoMatchHidesWholeBranch` (`selectionMoves`, `oracleHidden`,
`allSettingsStays`, `allSettings.empty`, `allSettings.node`, `back`).

## 8. Re-Apply Filter does nothing, or is enabled when it should not be (MENU-015)

- The entry is enabled while no filter is on (capture: grayed in every dump), or grayed while one is on.
- The entry does nothing, or switches the filter off, or shows the rows of the state before the edit.
- A policy edited while the filter is on stays in or out of the list until the node is selected again; after Re-Apply the list
  is the filter applied to the new states.

Check: `Gpedit_Filter_KeptOnAcrossNodesAndReApply` (`off.reapply`, `on.reapply`, `reapply.same`, `reapply.afterEdit.*`,
`offAgain.reapply`) and the `reapply` check of every menu check.

## 9. The filtered All Settings node is wrong (BEH-010, VIEW-009)

Capture: `AllSettings_*` (counts untracked). The sandbox seed makes the rows known: Computer side Configured Yes gives Class
machine flag, Class both flag and the decimal policy (3 of 59); Commented Yes the first two; User side Configured Yes gives
Class user flag and Class both flag (2 of 13).

- All Settings ignores the filter, or filters only the first time.
- The total is the number of the node's policies and not of the side (59 and 13).
- An empty result removes the node.
- A class Both policy shows twice, or shows on the wrong side.
- The node has three columns. The headers of both captures list four: Setting, State, Comment and a fourth that holds the path
  (VIEW-009); the third and fourth are off screen in the capture window. Every other node has three. The test asserts the
  header id `ConsoleListHeader_path` and four cells per row with a non-empty fourth cell; the text of the path is not
  captured (the rows of the capture are untracked), so its content is not asserted.

Check: `Gpedit_Filter_Results_AllSettings` (`computer.pathHeader`, `computer.fourCells`, `computer.*`, `user.*`, `off`).

## 10. A filter entry is missing, extra or in the wrong place in the Action menu (MENU-007 to MENU-010, MENU-013 to MENU-016)

Capture: `Menus_Computer`, `Menus_User` (ops 8, 14, 20, 26, 32, 38, 44). Classic's Action menu at a node with the filter
holds the three entries flat, then a separator, then a tasks submenu with the same entries (and Edit first with a policy row),
then the rest, Help last. At the configuration nodes and the placeholders it holds neither the entries nor the submenu.

- The entries exist at the configuration nodes and the placeholders, or are missing at Administrative Templates, category
  nodes, All Settings or with a policy row selected.
- The order is not Filter On, Filter Options, Re-Apply Filter; with a policy row Edit stays first; Help stays last.
- The tasks submenu is missing at a node with the filter, exists at a node without it, sits above the flat entries or below
  Help, or holds other entries than the flat ones (with a policy row it starts with Edit).
- Filter On or Filter Options is grayed, or Re-Apply is enabled with no filter (section 8), in the popup or in the submenu.
- The toggle entry is not checked while the filter is on.
- Filter Options does not open the dialog; the entry acts on the node of the previous selection.
- The two configuration sides differ.

Handling: the Action popup is rebuilt each time it opens (as G2 does); the submenu is built with it. Labels are BWC's own
words from the piece's `.resw`; the tests check that they start with Filter On, Filter Options and Re-Apply Filter.
Check: `Gpedit_Filter_Menu_Action` (one check group per node kind of the table in the test: the flat checks, `submenuPresent`,
`submenuAfterFlat`, `inside.*`, `submenuAbsent`; `policyRow.*` with `inside.edit` and `submenuBeforeHelp`; `options.opensDialog`,
`toggle.*`, `policyRow.on.tasks.*`).

## 11. A filter entry is missing or wrong in the View menu (MENU-021, MENU-027 to MENU-029)

Capture: ops 9, 15, 21, 27, 33, 39, 45. The View menu has the three entries flat, after the four list modes and before
Customize View, at the nodes with the filter; no submenu copy. BWC's console frame ids: modes 1004 to 1007, Customize View
13231.

- The entries are missing, appear at nodes without them, sit before the view modes or after Customize View.
- A View entry runs another command than the Action entry (one command, one id).

Check: `Gpedit_Filter_Menu_View` (per node kind, `afterModes`, `beforeCustomize`, `toggle.runs`, `toggle.checked`,
`reapply.runs`, `options.opensDialog`, `toggle.off`).

## 12. The context menus lack the filter entries or have them in the wrong structure (CTX-005 to CTX-007, CTX-009)

Capture: ops 5, 11, 17, 23, 29, 35 and 42 of `Menus_Computer` and `Menus_User`. Classic's list context menu with no row has no
flat filter entry: at the nodes with the filter its View submenu holds them (after the view modes, before Customize View); at
the other nodes the View submenu holds none. With a policy row the menu is Edit, the three entries flat, a tasks submenu with the
same four entries, Help last (identical to the Action popup, CTX-007). The menu of a folder row and the console tree menu are not
captured.

- A policy row menu has no filter entries, has them before Edit, lacks the tasks submenu, or Help is not last.
- The empty-area menu has flat filter entries, lacks the View submenu entries at the nodes that have the filter, or holds
  filter entries (flat, in a View submenu or in a tasks submenu) at the nodes that do not.
- A tasks submenu appears in the empty-area menu (classic has none there).
- The console tree menu of a node has the three entries under the wrong rule. This is a BWC definition: the tree menu was
  decided in G2 as Refresh and Help; at the nodes with the filter it also gets the three entries, flat (CTX-009 is not captured).
- A context menu entry runs on another node than the menu was opened for.

Check: `Gpedit_Filter_ContextMenu_List` (`row.*`, `row.inside.*`, `rowOn.*`, `rowOn.tasks.*`, `<kind>.empty.flat.*`,
`<kind>.empty.submenuPresent`, `<kind>.empty.inside.*`, `<kind>.empty.tasks.submenuAbsent`, `<kind>.empty.view.*`, `folderRow.base.*`),
`Gpedit_Filter_ContextMenu_Tree` (one group per node kind with Shift+F10 in the tree, `toggleRuns`, `on.*`).

## 13. The toolbar Filter button is wrong (TOOL-015, TOOL-016)

Capture: `Toolbar_States`, `AllSettings_Computer` (the last button, after a separator).

- The button shows at the configuration nodes or the placeholders, or is missing at the nodes that have it or with a policy row
  selected.
- It is not enabled, is pressed with no filter on, or stays pressed after the filter went off through a menu.
- It does not toggle the filter.
- It shifts the other buttons or changes their states (TOOL-016).

Check: `Gpedit_Filter_Toolbar_Button` (`<kind>.presence`, `<kind>.state`, `row.present`, `afterHelp`, `press.*`,
`pressedAtOtherNode`, `menuOff`, `press.released`, `othersUnchanged`).

## 14. A header click sorts wrongly (VIEW-014)

Capture: `Sort_Columns` ops 6 to 14, replayed by `Gpedit_Sort_Columns_ThreeClicksEach`: Setting, State and Comment, three clicks
each, the rows after each click, and the sort mark on the header.

- The first click sorts descending, or the second does not reverse the first, or the third does not return to ascending.
- A new column starts descending.
- A sort starts from the caller's original order instead of the order shown now. This is the difference the capture settles:
  the State sort after the Setting sort keeps `#Symbol policy`, `Alpha Policy`, `Éclair policy`, `Policy 1`, `Policy 10`
  inside the Not configured group (name order), and the Comment sort after the State sort lists `alpha policy` and
  `Zulu sort case` before the rest (the state order) and `Policy 2` before `Policy 10` among the commented rows. `ConsoleList`
  rebuilds the order from its source on every click today, so these checks fail until a click sorts the rows as shown.
- A descending sort reverses ties (classic keeps them in the order before the click).
- More than one header shows a sort mark, or the mark is on the wrong column.
- A sort changes the status count.

Check: `Gpedit_Sort_Columns_ThreeClicksEach` (`<column>.<click>.clicked`, `.order`, `.mark`, `.arrowOnlyHere`,
`afterLastClick`, `status`).

## 15. The text order is wrong (VIEW-015)

Capture: op 6 and op 7.

- A leading symbol sorts after letters; digits compare as numbers (Policy 2 before Policy 10; classic: Policy 1, Policy 10,
  Policy 2, which also answers SORT-1 and the ignored blind test); upper and lower case do not tie; the accented capital sorts
  after the letters of the same word or at the end.
- A tie in the plain sort changes the source order (`alpha policy` before `Alpha Policy`).

Check: `Gpedit_Sort_DigitsCaseAndSymbols`; `Gpedit_ListBlind_SortCasesDigitRuns` (the ignored test, now asserting Policy 1,
Policy 10, Policy 2).

## 16. The sort of one node and another (BEH-025)

Capture: op 17 reads the Basics list after the Sort cases node was sorted by Comment (the capture label says another node keeps
its own sort). All twelve Basics rows have the comment No, so the rows read the same whether or not that sort applies to
Basics. The header sort mark read at Basics is the Comment mark of the sort made at Sort cases, which hints that classic keeps
one sort for the policy nodes of a side. The capture cannot decide, so the test asserts only what both readings give.

- Basics shows the Sort cases order, or Sort cases loses its order after a visit to Basics.
- A sort made at Basics breaks the sort of the node the user comes back to.

Check: `Gpedit_Sort_AnotherNode` (`firstNode`, `otherNodeRows`, `firstNodeKept`, `basics.sortedByName`). Open question for the
manager: per node (BEH-025 as written) or shared by the policy nodes (the hint of op 17). A later capture with different
comments in two nodes decides it.

## 17. The sort is not saved (BEH-025, settings)

- The settings do not hold the sort column and direction, or the sort is saved only at close and lost on a kill (the existing
  rule of the settings file applies: written when the window closes and shortly after a change settles).
- The sort is restored on the wrong column, or restored but not applied to the rows, or not shown on the header.
- A saved sort of a column that does not exist for the node (Path outside All Settings) breaks the list: it is ignored.
- The newest sort is not the one saved (the third run reads the second run's choice).
- A corrupt sort value in the settings file stops the window (the persistence tests cover the corrupt file as a whole).

Check: `Gpedit_Sort_StateSavedAcrossRestart` (`first.descending`, `settingsFile`, `second.order`, `second.mark`,
`second.header`, `second.changed`, `third.mark`, `third.rowsSorted`).

## 18. The default order is wrong (VIEW-013)

Capture: `Sort_Columns` op 5 and op 17, and every list of `Nodes_Computer` and `Nodes_User`. A node with no sort mark lists
folder rows first, by name, then policy rows in the order of their policy ids. Op 5 shows no sort mark (its three sort values
are alike). The ids settle the compare: `Pol_SharedList_One` and `Pol_SharedList_Two` come before `Pol_Shared_Alpha` in
`Nodes_Computer` op 25, which an ordinal compare gives ('L' is below '_') and a case-insensitive one does not. So the compare is
ordinal on the policy id (the `name` attribute of the policy).

- The rows are in the display-name order (what the editor does today), or in the order of the ADMX file.
- The compare of the ids ignores case (`Pol_Shared_Alpha` before `Pol_SharedList_One`).
- Folders follow the policies, or the folders are not by name.
- A default node shows a sort mark.

Check: `Gpedit_Sort_DefaultOrder` (`sortCases.order`, `sortCases.notByName`, `sortCases.noMark`, `sortCases.noHeaderMark`,
`basics.order`, and one check per node of both Nodes captures). The G1 tests that asserted the display-name order are changed to
the captured order: `Gpedit_ListBlind_SortCasesOrder`, the `BasicsComputer` and `BasicsUser` lists of `GpeditTreeListBlindTests`,
and `ComputerBasics` and the state arrays of `Gpedit_List_States` and `Gpedit_List_UnreadablePolicyFile`.

## Decisions and the captures that settle them

The plan rule: a capture is the specification, and a difference from classic needs the user.

- **Default order (VIEW-013).** Settled by section 18: policy ids, ordinal; folders first by name. VIEW-013 reads "ascending by
  the first column", which the captures contradict; the row stays open and its text is updated. **All Settings:** the captures
  of that node do not track its rows ("depends on the machine's own templates"), so its default order is not a capture fact.
  ADMX-022 says by display name and nothing contradicts it, so `Gpedit_ListBlind_AllSettings` keeps the display-name order and
  says why; open for a capture on a machine with known templates.
- **Path column (VIEW-009).** Settled by section 9: the headers of `AllSettings_Computer` and `AllSettings_User` list four
  columns (Setting, State, Comment and a fourth). `Gpedit_ListBlind_AllSettings` now asserts four cells and the `path` header
  (`user.four-columns`, `user.path-header`). The content of the path is not captured and not asserted.
- **Context menus (CTX-005 to CTX-007, MENU-016).** Settled by sections 10 to 12: classic has a tasks submenu and a View
  submenu, and the tests assert that structure. The submenu entries use the same command ids with their own automation id
  prefix. Not captured, so BWC definitions: the console tree menu (flat entries, CTX-009) and the menu of a folder row (no
  filter assertion).
- **Per-node versus shared sort (BEH-025).** Not decided by the captures: all Basics comments are alike. `Gpedit_Sort_AnotherNode`
  asserts only what both readings give; its `otherNodeRows` check is the capture's op 17 order (the default order of Basics).
- **BWC definitions the captures cannot give:** Re-Apply Filter is enabled only while Filter On is on (every dump has the filter
  off); the status bar shows two numbers (shown, total) and a flag pane while the filter is on; Select All of the requirements
  filter selects the supported-on definitions of the loaded templates, so op 66 shows every row (FC11).

## Safety and repeatability

The tests run on the E2E flavor only, with the sandbox policy object (`GpeditSandbox`; the seeds go through the store of the
sandbox), the F1 fixture templates and the run data root of the scope; nothing writes the real policy folder, the real registry or
`%ProgramData%`. The tests never elevate and never prompt. The capture cases are read from `docs/parity/oracle/gpedit/goldens`
through `GoldenCase`, which checks the golden files by size and hash and fails when a case is missing. Every test records its
checks and measurements as `artifacts/e2e/visual/gpedit/gpedit-<test>.json` and `artifacts/e2e/result-<rid>-<flavor>.json`, which
is the repeatable artifact of AGENTS.md.

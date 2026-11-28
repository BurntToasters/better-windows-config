# Local Group Policy Editor templates failure modes

Scope: piece G6 of release 0.2.0. In `src/Bwc.Gpedit`: the Add/Remove Templates dialog (Action > Add/Remove Templates, present
only at the Administrative Templates node), the template import over `Bwc.Policy.Store.TemplateImport`, the removal of an imported
template, the reload of the tree and lists after the dialog closes, the shadowed state of an import, and the load-error list
(finding G6-1: `GpeditModel.Problems` is recorded and never shown). The engine rules (the import checks C1 to C22, the copy D, the
removal E, the read integrity and the source folder cap I, findings F16 and F17) are `docs/failure-modes/template-store.md`. The
UI uses that engine and keeps no second copy of its checks. There is no Extra Registry Settings node (user decision).

This file was written before the code (tests first). Each section lists the ways the feature can fail, the inventory row, the
capture or declared difference, the design choice that prevents it and the E2E check that catches it. The tests are in
`tests/Bwc.E2E/Gpedit/Templates/`. Every test writes `artifacts/e2e/visual/gpedit/gpedit-<test>.json` and a picture, and its checks
(`gpedit.import.<name>.<id>`, `gpedit.templates.<name>.<id>`) to the E2E result artifact.

## Specification sources

Classic's own dialogs were not captured live (DLG-116 says so); the sources are the resources of `admtmpl.dll` and the H group.
Microsoft text is never copied: rows are named by fixture names, and the dialog by command role and BWC's own labels.

| Source | What it settles |
| --- | --- |
| `admtmpl.dll` dialog 300 (DLG-033, DLG-115) | the Add/Remove Templates dialog: 288 x 150 dialog units, five controls: a list of the current templates (302), Add (303), Remove (304), Close (8), and a label (301) |
| `admtmpl.dll` dialog 800 (DLG-034) | a details box with a text area and a Close button, 275 x 141 dialog units, five controls |
| `F/Menus_Computer`, `F/Menus_User` (MENU-008, MENU-011, CTX-005) | Add/Remove Templates is the first entry of the Action menu at the Administrative Templates node, again in the tasks submenu there, and in no other menu and at no other node, and not in the context menu |
| `H/*` (DLG-121, DLG-122, VIEW-038 to VIEW-041, ADMX-003 to ADMX-011) | which fixture templates load and which are reported (table in section 10) |
| BEH-008, DIFF-005, ADMX-001, ADMX-026 to ADMX-030 | an import copies into the BWC store, classic `.adm` is refused, the system folder wins on a duplicate namespace and the import is listed as shadowed |

Inventory rows: MENU-008, MENU-011, CTX-005, DLG-033, DLG-034, DLG-115, DLG-116, DLG-121 to DLG-123, VIEW-038 to VIEW-041,
BEH-007 to BEH-009, ADMX-001, ADMX-003 to ADMX-011, ADMX-023, ADMX-026 to ADMX-030, DIFF-005.

## BWC definitions and declared differences

Everything here is a decision of this file where the captures are silent. Each one is listed again in the audit.

- **The load-error list replaces the start message box (declared difference from DLG-121 and DLG-122).** Classic shows one message
  box at start when any template fails, once, with an OK button. BWC shows no box at start. A modal box at start would block the
  window for every reader of the editor (the tests, a screen reader user who only wants the templates that loaded) and the
  failure is better kept than dismissed. Two places instead: a status bar notice (a pane, `StatusBar.Pane2`, with the number of
  problems, present only while there is at least one) and the Problems list in the Add/Remove Templates dialog (section 10). The
  notice appears when the load that found problems finishes and again after every reload that still finds them; it does not
  re-appear when a node is selected, because it is a pane and not an event (the point of DLG-122 is kept: the user is not
  interrupted again).
- **One bad template does not hide the rest (declared difference).** Classic refuses the file it cannot load and shows none of its
  content, and the others show. BWC does the same, and records every problem, so the list can show all of them.
- **A template with an undefined using loads and is listed (declared difference from VIEW-039).** Classic loads it with no box.
  The engine records `UndefinedUsing` and keeps the file (`BadTemplateTests`), so BWC lists the problem and shows the category and
  the policy. The notice counts it. The user can decide to keep this or to hide it (open for the audit).
- **The reason column is the engine code (open).** A problem row shows the engine's failure code name verbatim (`MalformedXml`,
  `MissingAdml`, `DtdProhibited`, `MissingAttribute`, `UndefinedString`, `DuplicatePolicy`, `InvalidAttributeValue`,
  `UndefinedUsing`) and the engine's detail. User wording per code is a later string pass; the tests read the code names, so a
  later wording change keeps the codes in the row's help text.
- **Add/Remove Templates is not a policy write.** Its entry is enabled at the Administrative Templates node in every access state.
  Whether the process can write the import store is the store's answer (an `AccessDenied` or `OwnerNotAllowed` write failure is
  shown in the details box, never an unhandled exception), not a second read-only rule.
- **Remove has no confirmation box.** Remove acts at once on the selected template. A removal that the engine refuses
  (`UsedByTemplate`) shows the details box.
- **One reload per closed dialog, only if something changed.** The tree, the list and the model reload once when the dialog closes
  after at least one successful Add or Remove, and not when nothing changed or every attempt was refused. The reload is the Refresh
  command (G2) and keeps the node that is selected.
- **The caption X and Alt+F4 do not close the dialog while an Add or Remove runs (0.2.0 audit).** Close, Escape and Enter were
  already ignored while busy, but the caption close was not: the dialog closed under the running operation, the main window was
  enabled again while the operation still ran, and a refused Add or Remove lost its details box (the dialog was closed, so
  nothing showed the reason). `IsCloseAllowed` is `!_busy`, as in the policy setting dialog.
- **The modal reservation starts before the import-store list is read.** Until the Add/Remove dialog is shown and closed, Edit,
  Filter Options, frame dialogs and Help cannot open another modal. The template continuation keeps the reservation it acquired
  before `ReadRows` and checks `_closed` after the await, so a close of the editor cannot create a dialog over a closed owner.
- **Refresh waits until the last modal closes.** A Refresh requested during template preload or any other modal stays queued;
  a model load that finishes while a modal is open cannot replace the tree under it. After the modal closes, one fresh reload
  applies the queued request. Closing the editor during preload drops the queued reload.
- **The dialog lists the store, not the system folder.** The list holds the files of the import store, one row per `.admx` file,
  ordered by file name (ordinal, ignoring case). System templates are never listed and never removable (E1).

## The E2E seam

Compiled only under `BWC_E2E`, in the style of the other G pieces. The readout is the UIA help text of `GpeditRoot`
(`gpedit-tree-list.md`, section 13). G6 adds one key:

| Key | Meaning |
| --- | --- |
| `TemplateReloads` | the number of reloads that closing the Add/Remove Templates dialog started (0 until one does) |

The E2E flavor also accepts `BWC_E2E_GPEDIT_TEMPLATE_READ_DELAY_MS` from 0 through 15000. The import dialog test uses it to hold
the store listing open long enough to exercise other modal entrypoints and a queued Refresh. It is ignored by the ship flavor.

The existing keys carry the rest: `Templates` (loaded template files), `PolicyCount`, `Failures` (engine failures), `Problems` and
`ProblemList` (every recorded problem; in a clean sandbox run there is none), `Reloading`, `Loaded`, `Node`.

New automation ids (in the style of the existing ones):

| Id | Control |
| --- | --- |
| `MenuCommand_2007` | Add/Remove Templates in the Action popup (command id 2007, continuing the G5 ids 2004 to 2006); label starts `Add/Remove Templates` |
| `TasksCommand_2007` | the same command inside the tasks submenu at the Administrative Templates node |
| `StatusBar.Pane2` | the template problem notice; it exists and has a name only while there is at least one template problem; its name contains the number of problems as digits |
| `GpeditTemplates` | the root of the Add/Remove Templates dialog (its own top level window of the editor process, modal over the editor) |
| `GpeditTemplates.List` | the list of imported templates; one `ListItem` per file of the store, named by the file name (`Imp.admx`); the item help text is `Namespace=<namespace>;Status=<Loaded, Shadowed or NotLoaded>;` (the namespace is empty for a file that does not load) |
| `GpeditTemplates.Add`, `.Remove`, `.Close` | the three buttons; Remove is enabled only while a list row is selected |
| `GpeditTemplates.Problems` | the load-error list in the dialog; one `ListItem` per recorded template problem, named by the file name (or the folder name, or empty when no path is known); the item help text is `Code=<engine code>;Detail=<engine detail>;Kind=<TemplateFailure, TemplateStore or LoadException>;` |
| `GpeditTemplateDetails` | the details box (DLG-034, DLG-116): its own top level window over the dialog |
| `GpeditTemplateDetails.Text` | its text; one line per problem of a refused Add or Remove: `<ProblemCode>[/<load code>] <file name> <detail>`, for example `LoadFailed/MalformedXml BwcMalformedXml.admx` |
| `GpeditTemplateDetails.Close` | its Close button; Enter and Escape also close it |

The file picker of Add is the COM common item dialog that the Registry Editor uses (`file-dialogs.md`): the tests drive it with
real input (type the full path in the file name box, press Open) through `SystemFileDialogDriver`. There is no injected path and no
second code path for the E2E flavor.

## 1. The menu entry is missing, extra or in the wrong place (MENU-008, MENU-011, CTX-005)

Capture: `F/Menus_Computer`, `F/Menus_User`. At the Administrative Templates node classic's Action menu starts with Add/Remove
Templates, then the filter entries, then the separator and the tasks submenu that holds the same entries; no other node and no
policy row has it; the list and tree context menus never have it.

- The entry is missing at the Administrative Templates node of either side, is disabled, or is not the first entry there.
- The entry exists at the configuration nodes, the placeholders, a category node, All Settings, or with a policy row selected.
- The entry is in the list context menu (with a row or without) or in the tree context menu (CTX-005 says it is not).
- The tasks submenu at the Administrative Templates node lacks the entry (MENU-008 says the submenu holds the same entries).
- Another entry moved: the filter entries are no longer in the order Filter On, Filter Options, Re-Apply Filter, or Help is no
  longer last.
- The label is not BWC's words (it starts with `Add/Remove Templates`).

Handling: the Action popup is built once and the entry is shown and hidden with the node (as G2 and G5 do); it is shown for
the node kind `AdministrativeTemplates` only, first, before Filter On. Check: `Gpedit_Import_Menu_Placement` (`<kind>.present`, `<kind>.first`,
`<kind>.enabled`, `<kind>.label`, `<kind>.filter.*`, `<kind>.beforeFilter`, `<kind>.tasks`, `<kind>.tasksAbsent`, `<kind>.helpLast`,
`policyRow.absent`, `listMenu.absent`, `treeMenu.absent`).

## 2. The dialog has the wrong parts or the wrong layout (DLG-033, DLG-115)

Capture: `admtmpl.dll` dialog 300 (288 x 150 dialog units: 432 x 225 DIP at the classic font, five controls) and the readout of
the dialog after it opens.

- A part is missing: the list, Add, Remove, Close.
- The list is not the current store content (it lists the system folder, or is empty while the store holds a file).
- Remove is enabled with no row selected, or stays disabled with a row selected.
- Add or Close is disabled.
- The window is far from 432 x 225 DIP, is not modal (the editor can be used while it is open), or Escape does not close it.
- Closing the dialog does nothing visible but leaves the entry grayed or the window behind.
- The buttons are in another reading order (Add, Remove, Close, left to right or top to bottom as the resource has them).

Handling: one dialog built from the table above over `TemplateSources.Load` and `ImportedFiles`. Check:
`Gpedit_Import_Dialog_Layout` (`part.*`, `listEmptyAtStart`, `removeDisabledAtStart`, `addCloseEnabled`, `size`, `order`,
`modal`, `escapeCloses`, `ownerEnabledAgain`, `closeCloses`, `noReloadForNothing`).

## 3. Add does not import, or imports the wrong bytes (BEH-008, ADMX-026 to ADMX-029, DLG-116)

Capture: none (DLG-116 is not captured live). The engine decides: `TemplateImport.Check` then `Import`.

- Add does not open a file picker, or opens a save picker, or no `.admx` filter is offered.
- The picker returns a path and nothing happens, or the import runs on the UI thread and freezes the window.
- The file is checked with other rules than the engine's, or is not checked at all.
- The stored ADMX or ADML differs from the checked source (F16): the check is for one file, the store gets another.
- The list does not show the new row (file name, namespace, status `Loaded`).
- The store gets a file the user did not pick (a neighbour, a temp file), or the source folder, the system folder or the real
  `%ProgramData%` is written.
- The source file is changed, moved or deleted by the import.
- Two files for one template are not both copied (the ADML goes to the `en-US` folder of the store, or the UI culture folder).

Handling: Add calls `Import` through the engine only, off the UI thread, and shows the list again from `TemplateSources.Load`. Check:
`Gpedit_Import_Add_GoodTemplate` (`pickerOpens`, `listRow`, `rowNamespace`, `rowStatus`, `noDetails`, `notReloadedYet`,
`storeOnlyTwoFiles`, `storeAdmxBytes`, `storeAdmlBytes`, `closes`, `reload.*`, `systemUnchanged`, `sourceUnchanged`) and
`Gpedit_Import_Add_SurvivesRestart` (`first.added`, `first.reloaded`, `restart.policy`, `restart.clean`, `restart.category`,
`restart.row`, `restart.noReload`).

## 4. A cancelled picker changes something

- Cancel or Escape in the picker imports a file, shows the details box, or creates the store folder.
- The dialog closes or the list changes.

Check: `Gpedit_Import_Add_PickerCancelWritesNothing` (`cancelled`, `dialogStillOpen`, `listUnchanged`, `noDetails`, `noStore`,
`closes`, `noReload`).

## 5. A refused file is imported, or the refusal hides its cause (DLG-116, C1 to C22)

The details box (DLG-034) shows the typed problems and has a Close button. A refusal writes nothing.

- A refused file reaches the store, or leaves a folder, an orphan ADML or a temp file behind (G2 of the engine).
- The refusal shows no box, one generic box for every cause, or a box with no Close.
- The box does not name the problem code (the engine's `TemplateImportProblemCode`, with the loader code for `LoadFailed`) or the file.
- The dialog is closed by a refusal, or its list changes, or a reload starts.
- After a refusal the next Add is blocked (the box stays, the dialog is left disabled).
- A classic `.adm` file is imported or silently ignored (BEH-008, DIFF-005: refused as `ClassicAdmNotSupported`). A file with any
  other extension is `NotAdmx`.
- A template whose namespace the system folder already defines is imported (it would be shadowed at once): refused as
  `NamespaceDefinedBySystem`. A namespace already in the store is `NamespaceAlreadyImported`; a different template with the same
  file name is `FileNameInUse`.
- A template that uses a namespace only a neighbour file of the source folder defines is imported and fails to link in the store:
  refused as `UsingNotInstalled` with the namespace; after the neighbour is imported first the dependent imports.
- The load problems of other files in the source folder block the import (C18): they must not.

Handling: each `TemplateImportProblem` is a line of the details box. Check: `Gpedit_Import_Refused_BadFixtures` (per fixture, the eleven
fixtures of the H group: `<case>.box`, `<case>.names`, `<case>.codes`, `<case>.closes`, `<case>.dialogStays`, `<case>.listUnchanged`,
`<case>.noStore`, `<case>.sourceUnchanged`; then `afterRefusal.add` and `afterRefusal.storeOnlyGood` (the next Add works, and a broken
neighbour is not copied), `oneReload`, `systemUnchanged`), `Gpedit_Import_Refused_ClassicAdm` (`adm.box`, `adm.code`, `adm.names`,
`other.box`, `other.code`, `dialogStays`, `listUnchanged`, `noStore`, `noReload`), `Gpedit_Import_Refused_Duplicates` (`first.added`,
`again.*`, `system.*`, `fileName.*`, `storeUnchanged`, `listUnchanged`, `oneReload`), `Gpedit_Import_Dependents` (`neighbour.box`,
`neighbour.code`, `neighbour.namespace`, `neighbour.closes`, `neighbour.noStore`, `base.added`, `dependent.added`, `bothListed`,
`bothLoaded`).

The details box text is one line per problem: `<ProblemCode>[/<load code>] <file name> <detail>`. The engine problem codes are
`SourceMissing`, `ClassicAdmNotSupported`, `NotAdmx`, `UnsafeFileName`, `SourceReparsePoint`, `SourceTooLarge`, `SourceUnreadable`,
`SourceChanged`, `LoadFailed`, `AdmlMissing`, `NamespaceDefinedBySystem`, `NamespaceAlreadyImported`, `FileNameInUse`,
`UsingNotInstalled`, `StoreUnavailable`, `WriteFailed`, `VerifyFailed`, `NotImported`, `UsedByTemplate` and `SourceFolderTooLarge`
(`template-store.md`, section I). The fixtures give: `MalformedXml` (`LoadFailed/MalformedXml`), `DtdEntity`
(`LoadFailed/DtdProhibited`), `MissingAdml` (`AdmlMissing/MissingAdml`), `UndefinedString`, `DuplicatePolicy`, `UndefinedUsing`
(`LoadFailed/` and the code of the same name), `ListItemNoKey` and `BooleanNoValueName` (`LoadFailed/MissingAttribute`), and
`ClassLowerCase`, `BoolAttributeUpper`, `BoolAttributeMixed` (`LoadFailed/InvalidAttributeValue`).

## 6. Remove deletes the wrong thing, or cannot delete (ADMX-028, E1 to E7)

- Remove with no row selected does something.
- Remove deletes a file that is not the selected row's, a system template, or a file outside the store.
- A template that other imports use is removed (`UsedByTemplate` must refuse, show the details box and keep every file).
- A template that does not load cannot be removed (it must go through `RemoveFile`, by file name, E7).
- Only the ADMX is removed and the ADML stays, or the reverse.
- The row stays after a removal, or the next row is selected wrongly, or Remove stays enabled with nothing selected.
- A removed template cannot be imported again.

Handling: Remove calls `Remove` (by namespace) for a file that loaded and `RemoveFile` (by name) for a file that did not.
Check: `Gpedit_Import_Remove_Template` (`addOne`, `addTwo`, `listOrdered`, `remove`, `removed.row`, `removed.storeOnlyTwo`, `removed.noDetails`,
`removed.removeDisabled`, `removed.otherKept`, `removed.reload`, `removed.templates`, `removed.treeLost`, `removed.treeKept`,
`removed.importAgain`, `again.reload`), `Gpedit_Import_Remove_BrokenFile` (`import`, `imported.reload`, `broken.row`,
`broken.status`, `remove`, `broken.removed`, `broken.fileGone`, `broken.noDetails`, `broken.reload`, `broken.templates`,
`broken.treeLost`), `Gpedit_Import_Dependents` (`removeBase`, `removeBase.code`, `removeBase.names`, `removeBase.filesKept`,
`removeDependent`, `removeBaseAfter`, `storeEmpty`, `oneReload`).

## 7. The reload after Close is missing, extra, or loses the user's place (BEH-007)

- The tree and list show the old templates after a successful Add or Remove.
- The reload happens at every Add (while the dialog is open) or every Close, even with no change.
- The reload runs more than once for one dialog session, or runs when every attempt was refused.
- The reload moves the selected node or the open list to the root.
- The policies of the new template do not list, or the category does not show in the tree, or the policies of a removed template
  stay.
- After the reload the model's counts (`Templates`, `PolicyCount`) differ from a fresh start on the same folders.

Check: `Gpedit_Import_Add_GoodTemplate` (`reload.count`, `reload.node`, `reload.templates`, `reload.policyCount`, `reload.category`,
`reload.noProblems`, `reload.policyRow`), `Gpedit_Import_Remove_Template` (`removed.reload`, `removed.treeLost`),
`Gpedit_Import_Close_NoChangeNoReload` (`idle.closes`, `idle.noReload`, `refused.box`, `refused.closes`, `refused.noReload`,
`cancel.picker`, `cancel.closes`, `cancel.noReload`, `keepsNode`).

## 8. A shadowed import is shown as a normal one (ADMX-001)

The system folder wins on a duplicate namespace (B1); the import file stays in the store and `TemplateSources.Load` lists it in
`Shadowed`. This happens when a system template that has the same namespace is installed after the import.

- The row shows `Loaded` for a file whose policies the system template provides.
- The shadowed policies show twice in the tree.
- The shadowed file cannot be removed.
- The engine failure `NamespaceShadowed` is recorded and shown nowhere (it is a recorded problem like the others: section 10).

Handling: the row status is `Shadowed` and the row can be removed; the problem is in the Problems list with its code. Check:
`Gpedit_Import_Shadowed` (`import`, `imported.reload`, `refreshed`, `policiesOnce`, `categoryOnce`, `status`, `namespace`, `problems.listed`,
`removable`, `removed.storeEmpty`, `removed.reload`, `removed.stillOnce`).

## 9. An unusable store breaks the dialog (A3, A5, A6, A7, C21)

- The store path is a file, a link, has a rejected ACL or cannot be read: the dialog fails to open, shows a raw exception, or lists
  a stale content.
- Add writes into an untrusted folder.
- The unusable store is reported nowhere.

Handling: the dialog opens with an empty list, Add is refused with `StoreUnavailable` in the details box (the engine checks the store
before any copy, C21), and the problem is in the Problems list (kind `TemplateStore`). Check: `Gpedit_Import_StoreUnavailable`
(`loads`, `opens`, `listEmpty`, `problems.store`, `notice`, `add.box`, `add.code`, `add.noWrite`, `closes`, `noReload`).

## 10. The load errors are recorded and never shown (G6-1, BEH-009, ADMX-005 to ADMX-011, VIEW-038 to VIEW-041)

Capture: `H/*` (the fixtures installed next to the good fixture; classic shows one box at start and the tree without the bad
template). What loads and what is reported, for the engine and therefore for BWC:

| Fixture (under `fixture/bad`) | Capture rows | Tree and list in BWC | Reported as (engine code) |
| --- | --- | --- | --- |
| `MalformedXml` | VIEW-038, ADMX-005 | the category `Bad fixture MalformedXml` is not in the tree | `MalformedXml` |
| `MissingAdml` | VIEW-038, ADMX-006 | no category | `MissingAdml` |
| `DtdEntity` | VIEW-038, ADMX-004 | no category | `DtdProhibited` |
| `UndefinedString` | VIEW-038, ADMX-007 | no category | `UndefinedString` |
| `DuplicatePolicy` | VIEW-038, ADMX-009 | no category | `DuplicatePolicy` |
| `ListItemNoKey` | VIEW-038, ADMX-010 | no category | `MissingAttribute` (detail `key`) |
| `BooleanNoValueName` | VIEW-038, ADMX-011 | no category | `MissingAttribute` (detail `valueName`) |
| `ClassLowerCase` | VIEW-040, ADMX-012 | no category | `InvalidAttributeValue` (detail `class`) |
| `BoolAttributeUpper` | VIEW-040, ADMX-013 | no category | `InvalidAttributeValue` |
| `BoolAttributeMixed` | VIEW-040, ADMX-013 | no category | `InvalidAttributeValue` |
| `UndefinedUsing` | VIEW-039, ADMX-008 | the category and the policy show and open | `UndefinedUsing` (listed; classic is silent: declared difference above) |

- A problem is recorded and no place shows it (the state of the code today).
- The notice shows with no problem, does not show with one, shows a wrong count, or stays after the problem is gone (a reload that
  finds none).
- The Problems list is missing from the dialog, lists a problem twice, lists a file that loaded cleanly, drops the code or the file
  name, or shows only the first problem.
- One bad template hides the good ones, or a failing file's category stays in the tree (VIEW-041).
- A template that failed shows its policies; `UndefinedUsing` hides its category.
- A modal box blocks the window at start (the declared difference above is that none does).
- The notice or the list needs the dialog or a node selection to appear; the notice shows only after the first selection.
- The notice re-appears as an event after a node selection (it is a pane that stays as it is).
- Selecting nodes, browsing the good policies and opening the good policy dialog fail while a template is bad.
- A row shows the bare engine code (MSG-031 to MSG-033: classic shows a sentence), a code has no sentence (the row shows the key
  name), or the sentence replaces the code so that it is not visible any more. Each code of `TemplateFailureCode` has the entry
  `TemplateProblem<Code>` in `Templates.resw` (a store problem has `TemplateProblemStore`, an unexpected error
  `TemplateProblemLoadException`); the row shows the sentence in the middle column and the code in the last one. The details box
  of a refused import adds the sentence of the loader code after the code line. BWC wording: classic's own texts are about
  parse errors in .adm and .admx files and are not the same set of causes.

Handling: `GpeditModel.Problems` of kinds `TemplateFailure`, `TemplateStore` and `LoadException` feed the notice (count) and the
Problems list (one row each); the sandbox and policy-file problems (kinds `StorePolicy`, `PolicyFile`, `CommentFile`,
`VersionFile`) are not template problems and are not in the list. Check: `Gpedit_Templates_LoadErrors_BoxCases`
(`notice`, `noticeCount`, `noModalBox`, `tree.good`, `tree.noBadCategory`, `policyCount`, `goodPolicies`, `opens`, `list.count`,
`list.noDuplicates`, `list.kind`, `list.<case>`, `sentence.<case>`, `list.storeEmpty`, `noReload`),
`Gpedit_Templates_LoadErrors_UndefinedUsing` (`notice`, `tree.category`, `policy.row`, `policy.opens`, `list.count`, `list.code`,
`list.sentence`, `list.detail`), `Gpedit_Templates_LoadErrors_AttributeCases` (`notice`, `tree.noCategory`, `list.count`, `list.<case>`, `sentence.<case>`),
`Gpedit_Templates_Notice_ClearsAndReturns` (`clean.noNotice`, `added.refreshed`, `added.notice`, `added.list`, `removed.refreshed`,
`removed.noNotice`, `removed.list`, `again.refreshed`, `again.notice`, `afterReload.list`), `Gpedit_Templates_Notice_NotAnEvent`
(`shown`, `<node>.same`, `goodRows`, `noReloadStarted`).

## 11. A failed reload loses the last good model

- The reload after Close fails (a template read error) and the window shows nothing, or the old tree is kept but the notice is gone.
- The notice and list describe the model before the reload.

Check: `Gpedit_Templates_Notice_ClearsAndReturns` (`removed.list`, `afterReload.list`: the list holds the problems of the last load only); the model swap rule is G2's (`gpedit-commands.md`, Refresh).

## 12. The import-store preload races another modal or a Refresh (A5)

- While Add/Remove Templates is listing the import store, Edit, Filter Options, Add/Remove Columns, File Options or Help opens a
  second modal owned by the same editor window.
- The template continuation opens its dialog after the editor was closed while `ReadRows` was awaiting.
- Refresh during preload or any modal is dropped, or a load finishing during a modal replaces the model under that modal.
- A queued reload starts before the last modal closes, or more than one queued request starts separate reloads afterward.
- A modal closes with a pending reload and the editor never applies the latest templates and policy files.

Handling: reserve one modal slot before any asynchronous preparation and keep it until the owning dialog returns. Every modal
entrypoint checks that slot. Refreshes queue while the slot is reserved; load results that finish meanwhile are held or discarded
and a fresh coalesced load starts after release. A closed view discards its continuation and queue. The bounded E2E-only read
delay makes the preload window deterministic. Checks: `Gpedit_Import_Dialog_ModalRace` and
`Gpedit_Import_Dialog_ClosedDuringPreload` in `tests/Bwc.E2E/Gpedit/Templates/GpeditImportDialogTests.cs`, with the normal
Gpedit screenshot and JSON artifacts.

## Decisions and the sources that settle them

- **No start message box (DLG-121, DLG-122).** The capture says one box at start. BWC shows the status bar notice and the list
  instead (see above). Reason: every existing E2E test that loads a bad template (the G1 blind tests, the Refresh tests) needs the
  window to be usable at start, and a modal box would be the first thing every such reader meets. Open for the user: restore the box
  as an option, keep both, or keep the pane only.
- **The place of the list (G6-1).** The inventory names only the start box (DLG-121). The list in the Add/Remove Templates dialog is
  the place where the user is already managing templates, and it holds all problems with their codes, which a box cannot.
- **Status words `Loaded`, `Shadowed`, `NotLoaded`** are the help text values the tests read; the visible words are the piece's
  `.resw` strings.
- **The cap of 1000 ADMX files** and the read integrity rule: `template-store.md`, section I.

## Safety and repeatability

The tests run on the E2E flavor only, with the sandbox policy object (`GpeditSandbox`), the F1 fixture templates as the system
folder (a copy under the run data root for the tests that add or remove files while the editor runs), the import store inside the
run data root (`BWC_E2E_IMPORT_STORE`) and the source folders of the imports inside the run data root as well. Nothing writes the
real policy folder, the real template folders, the real registry or `%ProgramData%`. The tests never elevate and never prompt; the
file picker is the common item dialog driven with real input, owned by the editor's dialog, in the editor process at the test's
integrity level. Every test that imports or removes hashes the system folder and the source folder before and after and fails on a
change. Template text in the tests is authored by BWC (the oracle fixture and the helper that writes small templates); no
Microsoft text is copied. Every test records its checks and measurements as `artifacts/e2e/visual/gpedit/gpedit-<test>.json` and
`artifacts/e2e/result-<rid>-<flavor>.json`, which is the repeatable artifact of AGENTS.md. The engine side of F16 and F17 is
`tests/Bwc.Policy.Tests/TemplateImportIntegrityTests.cs`.

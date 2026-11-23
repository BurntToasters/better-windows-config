# Console frame: File > Options and the Help menu failure modes

Scope: piece U3b of release 0.2.0. In the shared console frame (`src/Bwc.UI/Console/**`, used by the Local Group Policy
Editor in `src/Bwc.Gpedit`): the File popup (Options, separator, Exit), the Options dialog, and the Help popup (Help Topics,
a web site entry, separator, About, and the snap-in About entry). There is no Favorites menu (MENU-037, removed by user
decision) and no Window menu. The Registry Editor does not use the console frame and must not change; its tests are the
guard. F1, Help Topics and the Help command are G2's (`gpedit-commands.md`, sections 1 and 7) and are not tested again here.

This file was written before the code (tests first). Each section lists the ways the feature can fail, the inventory row, the
capture or resource, the design choice that prevents it and the E2E check that catches it. The tests are
`tests/Bwc.E2E/Console/ConsoleOptionsTests.cs` (support in `ConsoleOptionsSupport.cs`) and
`tests/Bwc.E2E/Gpedit/Commands/GpeditHelpTests.cs` (support in `GpeditHelpSupport.cs`). Every test writes
`artifacts/e2e/visual/console-frame/console-frame-<test>.json` (frame host) or `artifacts/e2e/visual/gpedit/gpedit-<test>.json`
(policy editor), a picture, and its checks (`console.frame.<group>.<id>`, `gpedit.<group>.<id>`) to the E2E result artifact.
The persistence tests also record the settings file text as a measurement, so the run leaves a file that can be compared again.

## Specification sources

A capture is the specification; Microsoft text is never copied: rows are named by command role and BWC's own labels, and
strings by dll and id.

| Source | What it settles |
| --- | --- |
| `F/Menus_Computer`, `F/Menus_User` (File dumps, ops 1, 7, 13, 19, 25, 31, 37, 43) | the File popup at every node kind and with a policy row selected: command 13220, separator, command 57665; all enabled; identical everywhere |
| the same captures (Help dumps, ops 4, 10, 16, 22, 28, 34, 40, 46) | the Help popup: commands 12804 and 13245, separator, command 57664. The fifth entry, command 13242, follows at the configuration node of each capture (op 4) and the two placeholders (ops 34, 40) and nowhere else. Every entry is enabled |
| `mmc.exe` RT_MENU 194, `mmcbase.dll` strings 13220, 13245, 57664 | the command ids and the roles of the entries |
| `mmc.exe` RT_DIALOG 158 | the Options page: icon picture (1028, 20 x 20 units), name box (1027), Change Icon button (1025) to the right of the name box, a rule, a note line, the console mode drop-down (1022), a Description group holding a text area (1023), the not-saving check box (1024), the customize-views check box (1029). The page is 252 x 218 dialog units. The template has no OK or Cancel: the property sheet around it owns them |
| `shell32` ShellAbout, `src/Bwc.Regedit/Dialogs/ShellDialogs.cs` (`AboutBox.Show`), `src/Bwc.Gpedit/GpeditAbout.cs` | the native About box that the Registry Editor shows; DLG-120, DIFF-006 |

Inventory rows: MENU-002 to MENU-005, MENU-018 (the plain Action > Help entry, G2's), MENU-031 to MENU-037, DLG-119, DLG-120,
ACC-002 (F1, G2's), TOOL-013 (the Help button, G2's), DIFF-006.

Decisions of this file where the captures are silent. Every one is a BWC definition, is listed again in the audit and needs the
user's word before it counts as settled (the plan rule: a difference from classic needs the user):

- **The Registry Editor has no Help Topics entry**, so "as the Registry Editor does it" settles only About. Help Topics keeps
  G2's definition: it is the Help command (the same command as F1, the Action > Help entry and the Help button). In the ship
  flavor the Help command shows the native About box (DIFF-006, `gpedit-commands.md`, "Decisions of the code dispatch"); in the E2E flavor it opens
  nothing and only counts in `HelpRequests`. BWC has no help file; if one is ever added, Help Topics is the entry that opens it.
- **About** (command 57664) shows the native About box through `GpeditAbout.Show`, the Registry Editor's box: the caption is
  "About " plus the tool's window title, the text is the product, the version line and the copyright line. It opens in both
  flavors (the tests need the real box), is modal to the main window, and does not count as a Help request. The command
  returns first and the box opens from the dispatcher, so a UI Automation Invoke of the entry is not held until the box closes.
- **The web site entry** (command 13245) is added; the audit may still remove it (MENU-034). Its label is BWC's own words and
  names the web. It opens the project page, `https://github.com/BurntToasters/better-windows-config` (the address the update
  feed already names), with the shell, in the default browser, from a fixed constant: no address comes from a file or the
  user. In the E2E flavor it never launches anything: it counts the request in `WebSiteRequests` and publishes the address in
  `WebSiteUrl`.
- **The snap-in About entry** (command 13242) shows a second native box of the same text, with the caption "About " plus the
  snap-in name `Local Group Policy Editor snap-in` (a BWC string). It shows at the two configuration nodes and the placeholder
  nodes (Software Settings, Windows Settings, both sides) and nowhere else: not at the root node "Local Computer Policy"
  (the captures start below it; the brief lists the nodes above), not at Administrative Templates, categories, All Settings,
  or with a policy row selected.
- **Options keeps the classic page parts and fixes the ones that belong to console authoring.** BWC has no console files, no
  authoring and one mode, so: the name box is read-only and shows the tool's window title; Change Icon is disabled; the console
  mode drop-down is disabled and shows the one mode (BWC's own words), with its own description text in the Description area;
  the not-saving check box is checked and disabled (BWC never writes a console file; the settings file holds window and view
  state only). The classic note line (the options take effect the next time) is not shown, because BWC applies the one real
  choice at once. The icon picture and the rule of the classic page are not drawn: BWC has no console icon to show and the
  classic dialog control set of the frame has no picture or rule control (the code of this piece, `ConsoleOptionsDialog`,
  lays out the other parts in the reading order and the 252 x 250 dialog unit size the tests check).
- **The customize-views check box is real** and starts checked. Cleared and confirmed with OK, View > Customize View stays in
  its place (last entry of the View popup, the same separator groups) and is disabled; nothing already applied changes (the
  hidden parts stay hidden, the shown parts stay shown). Set again and confirmed, the command is enabled and the dialog shows
  the switches as they were. File > Options is never disabled, so a saved choice cannot lock the user out.
- **OK and Cancel.** OK applies and closes; Cancel and Escape close and apply nothing; the dialog opens on the state last
  applied. There is no Apply button (one real choice). The dialog is modal and is its own top level window.
- **Saved state.** The policy editor settings file holds `AllowCustomizeViews` (true when allowed; absent means allowed), next
  to the other view keys of U3a. The choice is written when the window closes and shortly after it changes. The frame host has
  no settings file; the choice lives for the run.
- **Command ids.** Options 13220 and Exit 57665 in the File popup; Help Topics 12804, web site 13245, About 57664 and snap-in
  About 13242 in the Help popup (the classic ids, as in the captures). The Help command of the Action popup keeps 2003.
  The frame rejects a host entry with any of these ids (`ConsoleMenu.FrameIds`).

## The E2E seam

The frame host tests use the existing placeholder host (`BWC_E2E_GPEDIT_HOST=console-frame`); the policy editor tests use the
sandbox policy object (`GpeditSandbox.SeedStandard`) and its settings file under the run data root. Two readout keys are new
(E2E flavor only, in the readout of `GpeditRoot` that `gpedit-tree-list.md` section 13 describes):

| Key | Meaning |
| --- | --- |
| `WebSiteRequests` | the number of times the web site entry ran; in the E2E flavor it launches nothing and only counts |
| `WebSiteUrl` | the address the entry opens (empty before the first request) |

The About boxes are found by the window API as the Registry Editor test does (`AppServicesSupport.WaitForOwnedWindow`: class
`#32770`, owned by the main window, exact caption), their static texts are read with `ChildTexts`, and they are closed with
Enter once the foreground guard made them the foreground window, or with WM_CLOSE when Enter could not be sent, so no run
leaves a box open. No browser is ever started by a test.

| Id | Control |
| --- | --- |
| `MenuCommand_59001`, `MenuCommand_59004` | the File and Help top level items |
| `MenuCommand_13220`, `_57665`, `_12804`, `_13245`, `_57664`, `_13242` | the popup entries |
| `ConsoleOptions` | the root of the Options dialog (its own top level window of the process) |
| `ConsoleOptions.Name`, `.ChangeIcon`, `.Mode`, `.ModeDescription`, `.NoSave`, `.CustomizeViews` | the parts (an edit box, a button, a drop-down, a text area, two check boxes with the Toggle pattern) |
| `ConsoleOptions.Ok`, `.Cancel` | the buttons |

## 1. The File popup has the wrong entries or the wrong layout (MENU-002 to MENU-005)

Capture: `Menus_Computer`, `Menus_User`, the File dumps. Classic File in this mode: Options, separator, Exit; no New, Open, Save,
Add/Remove Snap-in or recent file entry.

- An entry is missing, extra (a New or Save that BWC cannot do), or in another order.
- The separator is missing, or sits elsewhere.
- Options or Exit is disabled at a node kind or with a policy row selected (Options is disabled today: the frame has no handler).
- The popup differs between the two sides or between node kinds, or a popup built once keeps the entries of the node before.
- The keys: Options has access key P and Exit has X (MENU-003, MENU-004). A host entry still wins a key from Exit, as
  `Console_Shell_MenuKeyboard` checks; this file adds no host entry.
- A Favorites menu appears in the menu bar (MENU-037).

Handling: the frame builds the popup and owns the ids (`ConsoleMenu`); the editor's File entries are none.
Checks: `Console_Options_Menu_FileLayout` (`<kind>.entries`, `.groups`, `.enabled`, `.labels` for the thirteen node kinds,
`policyRow.*`, `userPolicyRow.*`, `accessKeys`, `exitKey`, `noFavorites`), `Console_Options_Menu_AccessKey` (`frameHost.*`:
the same layout in the frame host; `altFP.opens`, `altFP.cancel`, `ownerEnabledAgain`).

## 2. Options does not open, or opens twice, or leaves the window disabled (MENU-003)

- The command does nothing (the entry is enabled but has no handler), or opens a second dialog over the first.
- The dialog is not modal, or the main window stays disabled after it closes.
- Alt+F then P does nothing.

Checks: `Console_Options_Dialog_Layout` (`opens`, `modal`, `cancelCloses`, `ownerEnabledAgain`), `Console_Options_Menu_AccessKey`
(`altFP.*`).

## 3. The Options dialog has the wrong parts or layout (DLG-119)

Resource: `mmc.exe` RT_DIALOG 158.

- A part is missing: the name box, the Change Icon button, the mode drop-down, the description area, the two check boxes, OK or
  Cancel.
- The parts run in another reading order (the icon button to the right of the name box; below them the mode, its description,
  the not-saving box, the customize-views box; then the buttons with OK left of Cancel).
- The window is far from the classic page plus a button row (the page is 252 x 218 units, about 378 x 354 DIP; the window is
  about 400 x 430 DIP).
- The caption does not name Options.

Check: `Console_Options_Dialog_Layout` (`part.*`, `caption`, `size`, `rects`, `order.*`).

## 4. A fixed part is editable, or a real part is fixed (BWC definition)

- The name box can be edited (it does not report itself read-only), or shows something other than the tool's window title.
- Change Icon or the console mode drop-down is enabled and does nothing; the description area is empty or repeats the mode.
- The not-saving box is unchecked (BWC would claim to save a console file) or enabled.
- The customize-views box starts unchecked or disabled.

Checks: `Console_Options_Dialog_Layout` (`name.isToolName`, `name.readOnly`, `icon.disabled`, `mode.disabled`, `mode.text`,
`mode.description`, `noSave.fixed`, `customize.on`). Not covered: a write into the name box (the registry-writer guard bans the UI Automation call that
writes a value, so the check reads the read-only flag only).

## 5. OK, Cancel and Escape apply wrongly

- Cancel or Escape applies the choice, or the next opening shows the box as it was left.
- OK does not apply, or applies after a restart only.
- The dialog opens on the definition state and not on the state last applied.

Check: `Console_Options_Dialog_OkCancel` (`cancel.*`, `ok.*`, `escape.*`, `again.*`, `ownerEnabledAgain`).

## 6. The customize-views choice is not wired to View > Customize View, or undoes the view

- Cleared and confirmed, View > Customize View stays enabled and opens its dialog, or disappears (the layout of the View
  popup changes), or is disabled but the keyboard still reaches it.
- Clearing the choice hides or shows a part, or resets a switch that Customize View had set.
- Setting it again leaves the command disabled, or the switches show other than they were.

Checks: `Console_Options_Dialog_OkCancel` (`ok.disables`, `ok.staysLast`, `ok.groupsKept`, `again.enables`, `again.customizeOpens`),
`Console_Options_CustomizeViews_KeepsSwitches` (`hide.*`, `forbid.*`, `allow.*`).

## 7. The choice is not saved, or a saved choice locks the user out

- The choice is lost at restart, saved only at close and lost on a kill (the settings rule: written when the window closes and
  shortly after a change settles), or the newest choice is not the one saved.
- The key breaks the other keys of the file (the five view switches).
- A file that forbids customizing and hides every part leaves no way back: the menu bar is gone, or File > Options is disabled
  or does not open, or the box shows the wrong state.

Checks: `Console_Options_Persist_AcrossRestart` (`first.*`, `settingsFileWritten`, `settingsKey`, `otherKeysKept`, `second.*`,
`third.enabled`), `Console_Options_Persist_EverythingHiddenInFile` (`allHidden`, `menuBarStays`, `customizeDisabled`,
`optionsEnabled`, `optionsOpens`, `boxOff`, `boxSet`, `customizeBack`, `customizeOpens`, `boxesOff`, `partsBack`). Not covered: a
value of the wrong type in the file (a string where a boolean goes); the settings rule treats the file as one unit.

## 8. The Help popup has the wrong entries or the wrong layout (MENU-031, MENU-032, MENU-033, MENU-035)

Capture: the Help dumps. Classic Help: Help Topics, the web site entry, a separator, About, and at some nodes the snap-in About
entry after About.

- An entry is missing or in another order; the separator is missing or between other entries.
- An entry is disabled at a node kind.
- The popup of one node is shown at the next (a popup built once keeps the snap-in entry, or loses it when the user comes back
  to a node that has it). The nodes are visited in alternating order so this shows.
- Two entries share an access key, or an entry has none.
- The popup holds an entry of the Registry Editor's tool or a Favorites item.

Check: `Gpedit_Help_Menu_NodeKinds` (`<kind>.entries`, `.groups`, `.enabled`, `.labels`, `.accessKeys` for the thirteen node
kinds, `policyRow.*`, `userPolicyRow.*`, `altHOpens`).

## 9. The snap-in About entry shows at the wrong nodes (MENU-036)

- It shows at Administrative Templates, a category, All Settings, or with a policy row selected (the extension's nodes).
- It does not show at a configuration node or a placeholder, on either side.
- It shows at the root node "Local Computer Policy" (BWC definition: it does not).

Checks: `Gpedit_Help_Menu_NodeKinds` (the `.entries` and `.groups` of each kind), `Gpedit_Help_About_SnapIn` (`absentAtTemplates`).

## 10. The web site entry is wrong, or a test starts a browser (MENU-034)

- The entry is missing, disabled, or has no web word in its label (the audit may drop the entry; then this section, the test and
  the proposal change together).
- It names another address, an address that is not https, or an address built from input.
- It counts as a Help request, edits the selected policy row, opens a window of the tool or leaves the main window disabled.
- A test launches a real browser. The E2E flavor never does: it counts and publishes the address. The ship path (ShellExecute of
  the constant) is the one part no test runs; it is reviewed in the audit.

Check: `Gpedit_Help_WebSite_Entry` (`present`, `label`, `runs`, `address`, `secure`, `notHelp`, `noWindow`, `again`, `noEdit`).

## 11. About is wrong, or a box stays open (MENU-035, DLG-120, DIFF-006)

- The box has another caption, lacks the product, the version (from the executable's product version) or the copyright line.
- The box is not modal, or the main window stays disabled after it closes.
- It counts as a Help request (About is not the Help command).
- The box does not close, a second opening fails, or a box is left behind: the probe closes every box and the test counts the
  windows of the process before and after.
- The snap-in About box has the caption of the main About box, or none of its own.

Checks: `Gpedit_Help_About_Box` (`first.*`, `second.*`: `opens`, `product`, `version`, `copyright`, `modal`, `notHelp`, `closes`,
`enter`, `ownerEnabledAgain`, `noLeftover`), `Gpedit_Help_About_SnapIn` (`opens`, `product`, `version`, `copyright`,
`differsFromAbout`, `modal`, `notHelp`, `closes`, `enter`, `ownerEnabledAgain`, `noLeftover`).

## 12. Help Topics and F1 (not repeated)

`Gpedit_Menu_Help_Topics` (Help Topics is enabled and runs the Help command), `Gpedit_Keys_F1` (F1 fires the command once in any
pane) and `Gpedit_Menu_Action_Commands` (`helpRuns`, `helpOpensNoDialog`) already cover them. None of them contradicts the
captured layout, so no existing test changes in this piece. The new popup entries must leave their checks true: Help Topics stays
enabled with id 12804, and the Help command stays 2003 and last in the Action popup.

## Safety

- Every file the tests create is inside the run data root of the test (`BWC_DATA_ROOT` scope `console-<name>`): the settings
  file, the policy sandbox. The pictures and measurements go to `artifacts/e2e/visual`.
- No test touches the real registry, the real GroupPolicy folder or `%ProgramData%`; the policy editor runs on the sandbox
  policy object (`GpeditSandbox`) and the Local Group Policy hive of the sandbox only. The choices of this piece write the
  settings file only, never a policy file.
- No test elevates or raises a UAC prompt; the E2E builds are `asInvoker`.
- No test starts a web browser: the web site entry is counted, not launched, in the E2E flavor.
- The native About boxes are closed by the probe (Enter, then WM_CLOSE); the editor process is also killed when the run ends.
- The Registry Editor tests are the guard that the shared frame code did not change that tool: the full E2E run is part of the
  piece's gate.
- The checks never print file text beyond the settings file the test itself wrote and the About text of the box.

## Declared differences

All of these are BWC definitions the captures cannot settle; the audit lists them with the open question.

1. Options is one page with OK and Cancel and no Apply; the classic page sits in a property sheet.
2. The name box is read-only and shows the tool's window title; Change Icon and the console mode are disabled; the mode shows the
   one mode with its own description; the not-saving box is checked and disabled.
3. The customize-views box is real: it disables View > Customize View, takes effect at once (the classic note about the next
   opening is not shown), and is saved as `AllowCustomizeViews`.
4. Help Topics is the Help command (no help file; DIFF-006); About is the Registry Editor's native box.
5. The web site entry is added and opens the project page (MENU-034, for the audit to confirm or remove).
6. The snap-in About entry shows a second native box titled with BWC's snap-in name, at the configuration nodes and the
   placeholders, and not at the root node.
7. In the E2E flavor About opens the real box while Help only counts, and the web site entry counts and never launches.
8. No Favorites menu (MENU-037, removed by decision) and no Window menu.

# Basics failure modes

Scope: the everyday value and key behaviors of the Registry Editor (package RF1): Enter and
double-click on a value, the Modify items in the Edit menu and the value context menu, Copy Key
Name, F5 refresh, Delete and Rename routed by the focused pane, the multi-value delete text, the
synthetic `(Default)` row, and the enabled state rules of the Edit and context menus. The control
side lives in `src/Bwc.UI/Tree`, `src/Bwc.UI/List`, and `src/Bwc.UI/Shell`. The commands and menu
content live in `src/Bwc.Regedit`. The oracle is `docs/parity/regedit-inventory.md` (MENU-021 to
MENU-030, CTX-012 to CTX-022, BEH-013, BEH-038, BEH-046, BEH-007) and the classic menu strings in
`Resources.resw`.

Each section lists the ways the behavior can fail, then the rule the code follows, then the check
that proves it. The E2E checks live in `tests/Bwc.E2E/Basics/BasicsTests.cs` and in the RF1 gap call
sites of `ContextMenus` and `Shell`.

## Enter and double-click on a value

### Failure: the activation path returns early for typed values

The old activation path only opened the hex editor, and it returned for every type that has a typed
editor. Enter and double-click on a string or number value did nothing.

Rule: activation runs the same dispatch as Modify. REG_SZ and REG_EXPAND_SZ open Edit String,
REG_DWORD opens Edit DWORD, REG_QWORD opens Edit QWORD, and REG_MULTI_SZ opens Edit Multi-String.
Every other type, including REG_BINARY, REG_NONE, and unknown numbers, opens Edit Binary (the hex
editor) as classic regedit does.

Check: `BasicsTests` activates one value of each of the six types with Enter and with a double-click,
and checks the dialog caption and the editor control (`StringValueDialog`, `DwordValueDialog`,
`QwordValueDialog`, `MultiStringValueDialog`, `HexEditorDialog`) before it cancels.

### Failure: activation opens an editor for a stale or multi-row selection

A double-click or Enter raised while the selection changed could open the editor for another row.

Rule: the activated row must be the only selected row, otherwise the command returns without a
dialog. The registry is read again before the editor opens, so a value deleted behind the app's back
shows the classic read error instead of an editor with stale data.

## Modify and Modify Binary Data

### Failure: Modify is disabled for REG_BINARY

The context menu enabled Modify only for typed values, so binary values showed a disabled Modify.
Known gap `contextmenu.value.modify` (RF1).

Rule: Modify is enabled for every single selected value, and it dispatches by type as above. A
REG_BINARY value opens the hex editor from Modify. Modify is disabled for zero or more than one
selected value.

### Failure: Modify Binary Data is enabled for the default row that does not exist

A synthetic `(Default)` row has no registry value behind it, so a binary editor would have nothing to
save to a real value.

Rule: Modify Binary Data and Display Binary Data are disabled while the only selected row is the
synthetic default row. Modify on the synthetic row opens Edit String, and accepting it creates the
default value with an empty name.

## Edit menu dynamic items

### Failure: the Edit menu never gains Modify items

Classic regedit shows Modify... and Modify Binary Data... at the top of the Edit menu when a value is
selected. The BWC Edit menu had no such items.

Rule: the Edit menu has two leading items, Modify... (912) and Modify Binary Data... (917), and a
separator after them. They are present in the menu model and hidden (`Visibility = Collapsed`) while
no value is selected. They are shown while at least one value row is selected. Their enabled state
follows the single-value rule above.

Failure to avoid: inserting and removing menu items at run time. The items stay in the model, so the
menu keeps its order and the command ids stay findable by the existing `SetCommandEnabled` walk.

Check: `BasicsTests` opens Edit with no value selected and checks that neither Modify item is
present, then selects a value, opens Edit again, and checks both items are present and enabled.

## New Key and New Value

### Failure: New asks for the name in a dialog before the object exists

Classic regedit creates the object first with a counter name and then renames it inline. A prompt is
a different control, shows the wrong order of operations, and can leave nothing behind when it is
cancelled.

Rule: New Key creates the child under the selected key with the first unused name from `New Key #N`,
refresh selects it, and the tree inline editor opens with the whole default name selected. Escape
cancels the rename and keeps the default name. New Value does the same in the list with
`New Value #N`: it writes the classic default data, refreshes and selects the row, and opens the
list inline editor. No value data dialog opens from New. Enter commits the typed name through the
same path as F2.

The classic default data is: REG_SZ and REG_EXPAND_SZ an empty string, REG_BINARY zero bytes,
REG_DWORD and REG_QWORD zero, and REG_MULTI_SZ one empty string, encoded as the double NUL
terminator. The counter uses the first unused number, so a previous default named `New Key #1` or
`New Value #1` moves the next New command to `#2`.

Failure to avoid: starting the inline editor in the menu click handler. The closing menu restores
focus and would commit or cancel the editor at once, so the editor starts on the next low priority
dispatcher turn, after the refresh selection and after the menu closes.

Check: `EditingTests` checks `editing.newkey.inline`, `editing.newkey.escape`,
`editing.newkey.duplicate`, `editing.newkey.commit`, `editing.newvalue.inline`,
`editing.newvalue.escape`, `editing.newvalue.duplicate`, and the per-type
`editing.newvalue.*` checks. Typing after the editor opens without clearing proves the default name
was selected. The captures `new-key-inline.png` and `new-value-inline.png` are written under
`artifacts/e2e/visual/editing`.

## Copy Key Name

### Failure: the Edit menu item is a stub

`MenuEditCopyKeyName` was built without an invoke action and was permanently disabled.

Rule: the Edit item is enabled whenever a key is selected (the same rule as the context menu item).
It calls the same routine as the tree's Ctrl+C. The routine writes the classic display path,
`Computer\HKEY_CURRENT_USER\...`, which is `RegistryPath.ToDisplayString()`. The address bar uses the
same form. A clipboard that is held by another process is logged and leaves the selection usable.

Check: `BasicsTests` copies through the Edit menu and reads the clipboard with the same probe the
keyboard test uses, and compares the text with the run key path.

## F5 refresh

### Failure: refresh collapses the tree

The old refresh rebuilt the tree from new nodes, so every expanded key collapsed to Computer. The
selection was restored, but the view was not the same.

Rule: refresh first records the expanded display paths of the old tree, the selected display path,
the selected list row names, and the vertical scroll offsets of the tree and the list. It builds a new
source, resolves each recorded expanded path in parent-first order, expands it, selects the recorded
key, and then restores the scroll offsets. A path that no longer exists is skipped, and the selection
falls back to Computer as before.

Failure to avoid: resolving remote display paths with the local-only `RegistryPath.TryParse`. The
resolver walks the tree by node name, so the local `Computer\HKEY...` form and the remote
`\\COMPUTER\HKEY...` form resolve the same way.

### Failure: refresh loses the list selection or scroll

Loading the rows for the new node clears the list selection. The recorded row names are selected again
after the rows arrive, and the list scroll offset is restored after the same dispatch.

### Failure: a value added behind the app's back is not shown

Rule: the list reloads from a fresh read of the registry on every refresh. The check adds a value
through the sandbox helper while the app is open, presses F5, and expects the new row.

Check: `BasicsTests` expands a child key, selects a value, adds a value behind the app, presses F5,
then checks the expansion, the selection, and the new row.

## Delete and Rename routing

### Failure: Delete with tree focus deletes the selected values

The Delete handler checked the list selection first. With the tree focused and a value still selected
in the list, Delete asked to delete the value, not the key.

Rule: Delete and Rename go to the pane that has focus. The tree's Delete key always means Delete Key.
The list's Delete key always means Delete Values. The menu item uses the focused pane, and when no
pane has focus (the menu bar is active), it uses the pane that had focus last. A Delete on the tree
asks the key confirmation, and the list asks the value confirmation.

Failure to avoid: a Delete from the tree with the list selection still present deletes the values.

Check: `BasicsTests` selects a key with a value selected in the list, moves focus to the tree with
Tab, presses Delete, checks that the dialog is the key confirmation (`Confirm Key Delete`), confirms,
and checks that the key is gone.

### Failure: Rename in the tree renames the selected value

Rule: Rename follows the same pane rule. Tree focus renames the key, list focus renames the single
selected renameable value.

### Failure: Rename opens a prompt dialog instead of the inline editor

Rule: Edit > Rename, the tree and value context menus, and F2 all use the existing inline editor for
the focused pane. Tree focus shows `TreeRenameBox`; list focus shows `ListRenameBox`. The current
name is shown and selected. Enter commits through the same validation as the context menu, Escape
cancels, and a click elsewhere applies the tree rename as before.

### Failure: an inline rename commits an empty or invalid name

Rule: a key rename to an empty name, a name with a backslash, a name that is too long, or an existing
sibling name shows the classic `Error Renaming Key` text (oracle strings 66 to 70) and writes
nothing. A value rename to an empty name, a name over 16383 characters, or an existing value name
shows the classic `Error Renaming Value` text (strings 73 to 75) and writes nothing. The default
value row is never renameable. The object keeps its last valid name.

Check: `EditingTests` checks `editing.renamevalue.menu`, `editing.renamekey.menu`, and
`editing.renamekey.invalid`. `MessageTests` checks the duplicate key text through
`messages.renamekey.exists.*` and the refused run-key rename through `messages.renamekey.*`.

## Multi-value delete text

Rule: one selected value uses `MsgDeleteValueConfirm` (oracle string 52), and two or more use
`MsgDeleteValuesConfirm` (oracle string 50). Both use the caption `Confirm Value Delete` (string 51).
The synthetic `(Default)` row is never deleted, and it is not counted in the text.

Check: `BasicsTests` selects two values, presses Delete, checks the dialog text against string 50,
and cancels.

## The synthetic (Default) row

### Failure: a key without a default value shows no (Default) row

Classic regedit lists `(Default)` for every key, with data `(value not set)` when no default exists
(BEH-038, oracle string 27).

Rule: the value source adds one synthetic row, first in the list, named `(Default)`, type `REG_SZ`,
data `(value not set)`, not renameable. It is not in the raw name map, so the commands treat it as
having no registry value. Rename, Delete, Modify Binary Data, and Display Binary Data do not act on
it. Enter and Modify open Edit String, and accepting it writes the default value.

Failure to avoid: a real default value that exists is listed once, with its own data, and no
synthetic row is added.

Check: `BasicsTests` opens a key that has no default value and checks that the first list row is
`(Default)` with data `(value not set)`. It also checks the real default value row for a key that
has one.

## Enabled state rules

These rules are the oracle rules for the Edit and value menus, and the context menus use the same
ones:

- Delete (913): enabled for tree focus on a key that is not a predefined root and not Computer. For
  list focus, enabled when at least one real value is selected. Computer and HKEY roots are never
  deleted.
- Rename (914): enabled for tree focus on a key that is not a root and not Computer. For list focus,
  enabled for exactly one renameable value.
- Copy Key Name (675): enabled when a key is selected (not Computer).
- Export (659) and Print (660): enabled when a key is selected.
- Modify (912): enabled for exactly one selected value of any type.
- Modify Binary Data (917): enabled for exactly one selected real value.
- Display Binary Data (679): enabled for exactly one selected real value.
- New (516 and its submenu): enabled when a key is selected.

Failure to avoid: a menu item that stays enabled while the pane that owns it has no target. The
enabled state is recomputed on every selection change and on every focus change between the tree
and the list.

## View > Refresh

### Failure: View > Refresh is disabled

The item was built without an enabled state and without an action, so it was disabled.
Known gap for `view.refresh` in `ShellTests` (RF1).

Rule: View > Refresh is enabled and runs the same refresh as F5.

## Typed editors and read-only types

### Failure: a DWORD or QWORD with the wrong data length does nothing

A REG_DWORD whose data is not four bytes, or a REG_QWORD whose data is not eight, has no typed
editor. Modify, Enter, and double-click did nothing, because the typed editor was skipped and the
hex editor was not opened.

Rule: a number value with the wrong length opens the binary editor (`HexEditorDialog`) with its
bytes and its type, as classic regedit does. Accepting it writes the bytes back with the same type.

Check: `RobustnessTests.Robustness_InvalidLengthDwordOpensBinaryEditor` seeds a REG_DWORD with two
bytes, opens it with Modify, checks that the hex editor (`HexEditor`) is open, and cancels it.

### Failure: resource list types open the writable hex editor

REG_RESOURCE_LIST, REG_FULL_RESOURCE_DESCRIPTOR, and REG_RESOURCE_REQUIREMENTS_LIST are hardware
resource data. Modify opened the writable hex editor, so a typed edit could corrupt the device data.

Rule: these three types open read-only. The call site uses the read-only `HexViewerDialog`
(caption `Binary Data`) for now. A later change replaces the viewer's content with the classic
formats without changing the call site. Modify Binary Data follows the same rule.

Check: `RobustnessTests.Robustness_ResourceListOpensReadOnly` seeds a REG_RESOURCE_LIST value, opens it
with Modify, checks that the read-only viewer (its `HexViewerList` list) is open and the writable editor
(`HexEditor`) is not, and closes it.

### Failure: multi-value delete stops at the first failure

The delete loop threw on the first value it could not delete. The values after it stayed, the list
was not refreshed, and the work ran on the UI thread.

Rule: the delete continues past a failing value and runs on the thread pool with the write gate
held. Every value that can be deleted is deleted. The failures are logged, and one classic error
message (the first failure) is shown after the work. The list is refreshed in every case.

Check: no automated check. A failing value delete needs a value removed behind the app, and the E2E
harness has no value delete helper (`SandboxRegistry` deletes whole keys only). The rule is covered by
reading the code path; it is not covered by a run.

## Context menus

### Failure: the value context menu Delete deletes the key

The value context menu used the focused-pane delete. When the list did not hold focus (the menu was
opened by a right click that did not move focus), the pane was the tree, and the menu deleted the
selected key recursively.

Rule: the value context menu Delete is bound to the value delete directly. It deletes values only,
for the rows selected in the list, and never the key.

Check: `RobustnessTests.Robustness_ValueMenuDeleteRemovesOnlyTheValue` right clicks a value in the list
with the tree focused, chooses Delete from the value menu, and checks that the dialog asks about a
value (not a key), that the value is removed, and that the key and its other value remain.

### Failure: the tree context menu label and layout differ from the classic menu

RT_MENU 104 has separators after Find, after Rename, and after Permissions, and none after New. The
menu also disabled Export on Computer, which the main menu allows, and it kept the label Expand on
an expanded key.

Rule: the tree context menu has the classic separators and no separator after New. Export is
enabled on Computer, as the main menu does. The Expand item (915) shows Collapse (RT_STRING 25) when
the node is expanded, and choosing it collapses the node.

Check: `RobustnessTests.Robustness_ExpandedKeyMenuShowsCollapse` expands a key, opens its tree context
menu, and checks that item 915 is labeled `Collapse`. The separator placement and the Export item on
Computer have no automated check; they are covered by the oracle rows CTX-001 and CTX-014 only.

## Tree expansion errors

### Failure: expanding a key without access shows nothing

The tree source turned an enumeration failure into an empty child list, so the expand had no error
to report. The key opened with no children and no message, and the `ExpandFailed` event had no
subscriber.

Rule: expanding a key that cannot be enumerated with access denied collapses the node and shows the
classic `Cannot open key` message (`MsgOpenKeyFailed` under `MsgCaptionOpenKeyError`), with the system
text as the details line. Other enumeration failures keep the empty list and are logged. The expander
stays on the key, so the next expand retries.

Check: `RobustnessTests.Robustness_ExpandOfDeniedKeyShowsClassicError` expands `HKEY_LOCAL_MACHINE` and then
`SECURITY`, which a standard user cannot enumerate, and checks the classic text (`... cannot be opened.`,
caption `Error Opening Key`) before it dismisses the message. The check writes nothing.

## Busy gate for registry writes

### Failure: a command starts while a long write runs

A recursive Delete Key runs on the thread pool. New Key, New Value, inline Rename, F5, and address
bar navigation did not check whether a write was running. Their registry calls raced the delete,
and a refresh read a half-deleted tree.

Rule: one gate is checked by every command dispatch. While a registry write runs, the command is
ignored and logged, and the owner window is disabled, so no input reaches it. When the write ends the
window is enabled again and the view is refreshed by the write's own command. Closing the window
during a write waits for the write to finish, then closes. The write is not cancelled, because a
recursive delete has no safe cancellation point.

Check: `RobustnessTests.Robustness_BusyGateDisablesWindowDuringRecursiveDelete` seeds a key with 4000
subkeys, deletes it, and checks that the owner window is disabled while the delete runs. It then
posts a close request during the delete and checks that the window closes only after the delete
finished. The test sends no keystrokes during the delete: with the owner disabled, keys would go to
whatever window has focus, so the command-level gate is not exercised by input in E2E.

### Failure: two connects to the same name leak a connection

Connect Network Registry passed the duplicate check before the connect, and the connect ran outside
the gate. Two connects to the same name both passed, and the second connection was kept without a
tree node for it.

Rule: Connect runs under the write gate. After the connect succeeds, the duplicate check runs again.
A connection that is a duplicate is disposed and the duplicate message is shown.

Check: no automated check. A second connect needs a reachable remote computer, which the E2E
environment does not have.

## Tree context menu targets

### Failure: Permissions and Unload Hive use the list selection

Both commands were disabled or refused when the list had selected rows, even when the tree was the
focused pane. The tree context menu Permissions item acted on the list state, not on the tree node.

Rule: a command takes its target from the pane it was invoked from. The tree context menu passes the
tree node. The main menu uses the focused pane: when the tree has focus, the selected key is the
target whatever the list selection is. When the list has focus with a value selected, Permissions
and Unload Hive are disabled.

Check: no automated check. The security editor is a system dialog that the E2E driver does not open.

## Keys with names the path type cannot hold

### Failure: keys named `.` or `..` are hidden and names lose trailing spaces

`RegistryPath` rejects `.` and `..` as components, so the child list dropped those keys. The tree
and the restore path also trimmed the names, so a key named `Name ` with a trailing space was
searched as `Name` and could not be restored or refreshed.

Rule: the tree lists every child key, including `.` and `..`, with its display path built from the
parent. Those nodes have no children and no values, because `RegistryPath` cannot name them, so they
show without a value list. Display paths are not trimmed. Trailing spaces stay in the names.

Known gap: opening, renaming, or deleting a key named `.` or `..` needs `RegistryPath` to accept
those components. That type lives in `src/Bwc.Registry`, which this change does not own.

## Testing

The whole behavior is tested through the real application: the checks drive menus, keys, and dialogs
in a sandbox key under `HKCU\Software\BWC-E2E\<run-id>`. The tests never write outside the sandbox
and never touch the classic regedit applet state. Each modal dialog is cancelled, except the key
delete confirmation, which is accepted on the sandbox key only. The sandbox is removed in the cleanup
step, and the result artifact is written by the E2E harness.

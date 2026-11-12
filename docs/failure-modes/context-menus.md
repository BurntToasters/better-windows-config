# Context menu failure modes

Scope: the right-click and keyboard context menus for the Registry Editor tree and details list.
The control side lives in `src/Bwc.UI/Tree` and `src/Bwc.UI/List`; the menu content and command
wiring live in `src/Bwc.Regedit`. The menus reproduce RT_MENU 104, 105, and 106 from the oracle.
RT_MENU 108, the text edit menu, is out of scope and is not attached to any control.

## Right-click selection

### The first right click opens the menu

The tree takes the pointer focus on the right button press, not on the release. A focus change after
the flyout is shown dismisses it, so a first right click on a tree row whose pane did not have focus
closed its own menu. The list already focused on press. The E2E driver makes one right click and
fails when no menu opens; it does not retry.

### Selection happens before the popup

A right-click on a tree row selects that node first, then raises the context request. A right-click
on a details row selects that row first, then raises the request. This is the classic behavior: the
menu commands always describe the object under the pointer, even when another object was selected
before. The control also takes pointer focus before raising the event so a command that moves focus
after the menu closes returns to a sensible control.

### The chevron does not open the key menu

A right-click on the tree expansion chevron is not a key context request. The chevron hit area is
checked before the row raises the event. This prevents an accidental key menu when the user meant
to collapse or expand a node.

### Empty list background

A right-click in the details list body where there is no row raises a background request with a
null row. Only the New submenu is shown for that request, because a value command has no value to
act on. A click in the column header or in the inline rename box is ignored: the header keeps its
sort and resize behavior, and the rename box keeps its text editing behavior. A right-click on a
row never reaches the background handler because the row marks the routed event handled.

## Popup ownership and focus

### Flyout placement

Each request builds a fresh `MenuFlyout` and calls `ShowAt(element, point)` with the element and
point supplied by the control. Building on demand keeps enabled states in step with the current
selection and command state. The previous flyout is hidden before a new one is shown, so two
popups cannot stack.

### Menu closing

A `MenuFlyout` closes on Escape, on an outside click, and after a command is invoked. The host does
not keep the flyout open across a dialog. When a command opens a modal dialog, the flyout has
already closed normally, so the disabled owner window is not left with an orphaned popup.

### Focus after closing

The tree and list take pointer focus when the popup opens. When a modal command finishes, the host
returns focus to the details list after the hex editor and to the same control after other dialogs.
If the flyout is dismissed without a command, focus stays on the control that raised the request.

## Modal and duplicate request guards

### Duplicate requests while a modal is open

The registry host tracks `_dialogOpen` and `_fileDialogOpen`. A context request that arrives while
either flag is set is logged and ignored. The same guard protects a second command from the same
menu. The owner window is disabled while a modal dialog is open, so this path is mostly defensive,
but it also protects scripted UIA requests and a request that arrives just as a dialog is opening.

### Dialog failure does not leave the menu open

If the window handle is not attached, the modal helper logs the condition and returns false. The
flyout is already closed, so the user sees no dialog and no stale popup. Registry read or write
failures use the standard registry error dialog and do not refresh the list.

## Menu content and command state

### Exact oracle order

The tree menu order is Expand, New, separator, Find, separator, Delete, Rename, separator, Export,
Permissions, separator, Copy Key Name, Go to HKEY_LOCAL_MACHINE. The New submenu is Key, separator,
String, Binary, DWORD, QWORD, Multi-String, Expandable String. The value menu order is Modify,
Modify Binary Data, separator, Delete, Rename. The background menu contains only New and its
submenu. Text comes from `Resources.resw` and is parsed with the same mnemonic and accelerator
split used by the main menu.

### Disabled Permissions

Permissions is present in the tree menu and always disabled in this batch. The oracle command id
678 exists, but the security editor is an R11 deliverable. Showing a disabled item preserves the
menu layout and makes the missing capability explicit instead of hiding it.

### Delete, Rename, and Export state

The tree menu enables Delete and Rename only when the selected path is not a predefined root. Export
and Copy Key Name are enabled whenever a key is selected. New is enabled whenever a key is selected.
The value menu enables Delete for the selected value rows, Rename only for one renameable value, and
Modify Binary Data for any single value. The typed Modify item is enabled only for a value whose
existing editor accepts an initial value: string, expandable string, DWORD, QWORD, or multi-string.
Binary and unknown types keep Modify disabled and use Modify Binary Data instead. This matches the
main menu state where the same command ids overlap.

### Expand and Go to HKEY_LOCAL_MACHINE

Expand acts on the selected node and does nothing when the node is a leaf or already expanded. Go to
HKEY_LOCAL_MACHINE resolves the predefined root through the tree source, rebuilds the roots, and
selects the root. If the root cannot be resolved, the host shows the address bar key-not-found
message and leaves the current selection in place.

## Clipboard

### Copy Key Name

Copy Key Name writes the selected key's `Computer\HKEY...` display path to the clipboard. That is
the same long form used by the address bar and the settings file. Copying is best effort: if
another process holds the clipboard, the host logs the failure and leaves the menu and selection
usable. No partial text is written.

## Keyboard

### Delete and Enter

Delete on the tree or list calls the same `DeleteSelection` path as the main menu and the context
menu, including the confirmation dialog. Delete on an empty list does nothing. Enter on a single
value row raises the same activation event as a double-click: binary and unknown types open the
editable hex dialog; types with a typed editor stay on that editor path. Enter on the tree is not
used because the tree already uses arrow keys and F2 for edits.

### Ctrl+C, F5, and the menu key

Ctrl+C on a tree row copies the key name. F5 refreshes the current tree path and reloads the values
through the normal selection path. Shift+F10 and the Menu key open the context menu at the selected
row when a row is selected. These chords do not replace F2 rename, Ctrl+F find, F3 find next, or
the U10 double-click hex editor. The root key handler is registered with `handledEventsToo` so a
control that consumed another key cannot hide Ctrl+F or F3.

## E2E and cleanup

### Test scope

The ContextMenu E2E test seeds `HKCU\Software\BWC-E2E\<runId>` with a key, a child key, and values,
launches the published Registry Editor with an isolated data root and
`BWC_E2E_SANDBOX_ROOT`, and navigates to the run key. It right-clicks the tree row, value row, and
empty list background, checks menu order and enabled state, exercises Copy Key Name, Expand, New >
Key, Modify Binary Data, Rename, Delete, and the keyboard chords. Modal dialogs are cancelled so the
test never needs an administrator prompt.

### Cleanup

The test removes only the run subtree, its scoped data root, and temporary files. The application
session is disposed in a finally block, which closes the process. The shared assembly cleanup
removes the empty `HKCU\Software\BWC-E2E` parent when possible. No test writes to HKLM through the
sandbox helpers, and no test leaves a BwcRegedit process running.

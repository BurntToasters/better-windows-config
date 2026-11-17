# Keyboard failure modes (K1a)

Scope: the classic accelerators of the Registry Editor window (command table 100 in
`docs/parity/oracle/regedit-resources.md`), the address bar auto-suggest list, the context menu keys, the
window-wide accelerators, slow-click rename (BEH-010), and the text edit menus. The measured meanings are
`docs/parity/oracle/regedit-keys.json` (with `local/keys-text.json`) and `docs/parity/oracle/regedit-context.json`
(with `local/context-text.json`). Code: `src/Bwc.UI/Shell/ClassicWindowShell.xaml(.cs)`,
`src/Bwc.Regedit/RegeditRootView.xaml.cs`, `src/Bwc.Regedit/Controllers/AddressSuggestions.cs`,
`src/Bwc.UI/Tree/DenseTree.xaml.cs`, `src/Bwc.UI/List/DetailsList.xaml.cs`, `src/Bwc.UI/Menu/TextEditMenu.cs`.

## F4 and the address bar list

### F4 does nothing

Measured: F4 (642) with the tree or the list focused moves the focus to the address bar edit and opens
an "Auto-Suggest Dropdown" window. Nothing else changes (the caret path and the list stay). BWC must
focus the address bar and open its list, not change the selection.

### The list is a key path list, not a history list

The list suggests the subkeys of the parent that start with the last typed component, as full paths
(`Computer\HKEY_CURRENT_USER\Soft` gives `Computer\HKEY_CURRENT_USER\Software`). Entries that are not
subkeys (values, text) are never listed. The walk must not expand the tree.

### A stale answer must not open the list

Each request gets a generation number. An answer for older text, or one that arrives after focus left the
address bar, does not show.

### Enter, Escape, Up and Down

With the list open: Down and Up move the highlight, Enter navigates to the highlighted entry (or to the
typed text when none is highlighted), and Escape closes the list only. The typed text stays and focus stays
in the address bar. With the list closed, Escape restores the last committed path and focuses the tree, as
before.

### Hidden address bar

Declared: the measurement covers the bar when it is shown. With View > Address Bar off, F4 shows the bar and
focuses it, as Ctrl+L does (ACC-013). This is not measured in classic with the bar hidden.

## Window-wide accelerators

### Ctrl+P needs tree or list focus

Ctrl+P (660) must print from the address bar too. The root handler must not check the focused pane.

### Delete and F2 keep their text box meaning

With the address bar focused, Delete removes text and F2 is not rename. The tree and list keep their own
Delete and F2 handling.

### F3, F5, Ctrl+F, Ctrl+T, Alt+D, Ctrl+L

These work whichever pane has focus. The root key handler uses `handledEventsToo`, so a text box that consumed
the key cannot hide them.

## Alt+Up, Backspace and the parent key

### Alt+Up from the tree, the list, or the address bar

Measured: Alt+Up (698) selects the parent key with tree focus and with list focus (the list then shows the
parent's values). The tree's own Up must not handle Alt+Up, or it would move one row instead.

### Backspace selects the parent with tree focus only

Measured: Backspace with tree focus selects the parent key. With list focus nothing changes. The address bar
keeps Backspace as a text key.

### Root has no parent

Alt+Up or Backspace on Computer does nothing (no key is selected above the root).

## Alt+1 to Alt+9 and Shift+F6

### Alt+digit does nothing

Measured for Alt+1 through Alt+9 (689 to 697) with tree and list focus: no menu, no navigation, no window. The
commands exist only as accelerators with no handler in classic. BWC must not open the menu bar or move focus.

### Shift+F6 does nothing

Measured: Shift+F6 has no accelerator in classic. BWC's F6 cycle must not run backward on Shift+F6. F6 and Tab
keep the same cycle as before.

## Shift+F10 and the Apps key

### Context menu of the focused pane

With tree focus the menu of the selected key (or the Computer root menu) opens. With list focus and a
selected value the value menu opens. With list focus and nothing selected the list background menu opens.
Shift+F10 and the Apps key do the same. The menu opens at the selected row, not at the window corner.

### Nothing selected in the tree

Shift+F10 with tree focus and no selection opens nothing.

## Slow click rename (BEH-010)

### A rename needs an already selected item and a focused control

A rename starts only when all of these hold at the press: the item is the only selection before the click, the
control already had keyboard focus before the click (a click that moves focus from the other pane does not
rename), no Shift or Ctrl is held, and the item has been selected for at least the double-click time.

### The rename starts after the double-click time, not at the click

The rename is a one-shot timer of the double-click time. It starts only if no other input comes in that time.

### Any other input cancels the pending rename

A second click (a double-click), another pointer press, a key press, a selection change, or the control losing
focus cancels the timer. A double-click therefore opens the item (the value editor for a value, expansion for a
key) and never starts a rename.

### The double-click time is the user's setting

The threshold is `GetDoubleClickTime()`, not a constant.

## Context menus (keyboard and pointer)

### Computer root menu

The Computer root gets RT_MENU 107: Expand or Collapse, a separator, Export (enabled), a separator, and
Disconnect (disabled on the local computer). Delete, Rename, Permissions, and New are not in it.

### Hive and key menus

RT_MENU 104 as measured: Expand (enabled only with children), New, Find..., separator, Delete and Rename
(disabled on a hive root), separator, Export, Permissions..., separator, Copy Key Name. Go to
HKEY_LOCAL_MACHINE is not in the measured menu; Ctrl+T still does it.

### Remote nodes keep their menu

The remote branch of `ContextMenuController` is not changed by this piece.

## Text edit menus

### Hex area (RT_MENU 108)

Cut, Copy, Paste, Delete, a separator, and Select All. Cut, Copy, and Delete need a selection; Cut, Paste,
and Delete need an editable hex area; Select All needs data. Paste follows the clipboard text. Declared: the
measurement of Paste was grayed, and the state of the clipboard at that time is not recorded, so Paste
follows the clipboard here.

### Text boxes (system edit menu)

Undo, a separator, Cut, Copy, Paste, Delete, a separator, Select All. Undo needs undo history. Cut, Copy,
and Delete need a selection. Paste needs clipboard text and an editable box. Select All needs text that is not
all selected already.

### Declared differences

Not offered: Right to left Reading order, Show Unicode control characters, Insert Unicode control character
(its submenu), and the Open IME and Reconversion items. These belong to the input method and bidirectional
text support, which BWC does not have.

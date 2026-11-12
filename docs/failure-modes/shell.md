# Shell failure modes

Scope: the classic window shell in `src/Bwc.UI/Shell/ClassicWindowShell.xaml(.cs)`: the focus order
between the tree pane, the list pane, and the address bar; the splitter between the panes; the
address bar's look, focus, and accelerators; and the View > Address Bar toggle. The menu popups are
in `docs/failure-modes/menus.md`. The oracle is `docs/parity/oracle/regedit-metrics.json` (address
bar, splitter, and pane geometry) and the keyboard rows ACC-001 to ACC-014 in
`docs/parity/regedit-inventory.md`.

Classic order was checked read-only on the reference machine: from the tree, Tab goes to the list,
then to the address bar, then back to the tree. F6 follows the same cycle. Alt+D moved focus from the
list to the address bar. Ctrl+L is the second accelerator for the same command (ACC-014).

## Focus order

### Tab skips the address bar

The XAML tab order is the document order, and the address bar is the first child of the root grid.
Tab from the tree or the list therefore leaves the shell, and the address bar is never reached by
Tab. The shell must route Tab, Shift+Tab, and F6 itself, so the cycle is tree, list, address bar
(only while visible), tree. A check presses Tab four times from the tree and expects the list, the
address bar, the tree, and the list again.

### F6 does nothing

F6 is a separate accelerator for the same pane switch (ACC-007). It is handled in the same routine
as Tab. A check presses F6 three times from the list and expects the address bar, the tree, and the
list.

### The splitter is a tab stop

`SplitterThumb` has `IsTabStop="True"`, so Tab lands on the 4 DIP splitter between the tree and the
list. Classic regedit never stops on the splitter in the Tab cycle. The thumb keeps focusability for
View > Split, which focuses it on purpose, and it leaves the Tab cycle. A check presses Tab from the
tree and fails if the splitter has keyboard focus.

### Shift+Tab goes forward

Shift+Tab is the reverse cycle (ACC-002). A reverse routine that reuses the forward order sends
focus the wrong way. A check presses Shift+Tab from the tree and expects the address bar when it is
visible.

### Focus stuck when the address bar is hidden

With the address bar collapsed, the cycle must skip it. Tab from the list must go to the tree. A
focus change to a collapsed control is silently lost, which leaves no element focused.

## Splitter

### Drag is not clamped to the classic minimum

The tree width is clamped to a minimum and maximum. A drag past the minimum must stop at the
minimum, and the list pane must keep its minimum width too. The minimum tree width used here is
120 DIP. Classic minimums were not measured for this package; a drag of the classic splitter would
change its saved View value, so the classic minimums are not captured here.

### Escape does not cancel a drag

While the splitter is dragged with the mouse, Escape must restore the split that was in effect
when the drag started, and the drag must end. The thumb's `CancelDrag` raises `DragCompleted` with
`Canceled` set. The shell restores the width recorded at `DragStarted` on that event. Without the
handler the tree stays at the dragged width. A check holds the mouse on the splitter, moves it,
presses Escape, releases the mouse, and expects the tree width from before the drag.

### Mouse drag of the splitter (measured)

A probe that presses the thumb, moves 60 DIP, and holds the button reads the tree at 552 px (432 px before),
with DragStarted 1, DragDelta 12. The drag path works. The `shell.splitterDrag.dragMoves` failure
(432 px during the drag) did not reproduce in the probe; the cause is not confirmed.

The cursor check reads a different cursor handle on most samples over the thumb. The SizeWE handle
(0x10011) appeared on only one of six samples, so the cursor is set and then replaced. `SplitterSurface`
sets `ProtectedCursor`, but another element's cursor wins on most moves. Open.

### Keyboard split steps after an Escape

After a cancelled drag the keyboard split mode state must be clear. A stale `_keyboardSplitMode`
makes the next Left or Right move the split when focus is elsewhere. The drag start clears the
flag already; a check repeats the Escape cancel and then sends Left to the tree, expecting no width
change.

### The width is read before the restore is laid out

Escape puts the tree width back from before the split, but the column change is applied on the next
layout pass. A check that reads the width as soon as focus returns sees the widened width. The baseline
run read 480 px for `shell.split.escapeWidth` (restored width 432 px), while the next split began at 432
px and one Right press gave 448 px, so the restore had happened. The check waits for the width to settle
before it reads. The restore itself is unchanged and the check still compares with the same 432 px.

## Address bar

### Address bar looks like a rounded Fluent box

The default WinUI TextBox has rounded corners and a 2 DIP accent line on focus. The classic address
edit is a rectangle with a white field, a 1 px border (light on the top and sides, darker on the
bottom), and no accent. The style sets `CornerRadius` to 0 and overrides the TextBox border brushes
on the address bar element so focus does not change the border color. The height stays at 19 DIP,
which is the oracle address bar height at 192 DPI divided by two, so the row is 38 px as in the
oracle.

### Address bar font and height drift

The address bar font is the menu font size (12 DIP Segoe UI), and the height is read from
`DenseMetrics.AddressBarHeight`. A font change must recompute the height in `ApplyMetrics`. A check
reads the AddressBar UIA height and compares it with the oracle within one DIP.

### Ctrl+L and Alt+D do nothing

Both accelerators (ACC-013 and ACC-014) run command 672, which focuses the address bar. Alt+D is
not a menu mnemonic (no top-level item has the access key D), so the menu router passes it on to
XAML. The shell must bind both keys with `KeyboardAccelerator` on the root. With the address bar
hidden, the accelerator shows the bar and then focuses it. What classic does with a hidden bar was not
captured; showing the bar is the chosen behavior.

### Enter does not navigate

Enter in the address bar raises `AddressBarSubmitted`. The host (`RegeditRootView`) navigates the
tree to the typed path. A shell that handles Enter without raising the event leaves the tree on the
old key.

### Escape does not restore the path

Escape in the address bar must put back the last committed path, which is `CurrentPath`, and return
keyboard focus to the tree. Editing the text does not change `CurrentPath`, so a stale path would
show a path that is not the current key.

### Address bar hidden, focus lost

Hiding the address bar while it has focus must move focus to the tree. Otherwise keyboard focus
goes to a collapsed control and no key reaches the shell.

## View > Address Bar

### Layout does not reflow

Toggling the address bar must collapse its row to zero height and let the tree and list take the
space. A hidden row that keeps its height leaves a blank band under the menu bar. A check toggles
the bar off and on and reads the tree top against the menu bar bottom.

### Toggle item has no Invoke, and a mouse click does nothing

Measured on the baseline build: `ToggleMenuFlyoutItem` (View > Address Bar) exposes the UIA Toggle
pattern but not Invoke. A UIA click on it leaves the View popup open and the bar at 38 px, so
`shell.addressbar.hiddenAfterToggle` and `shell.reflow.hiddenGapClosed` fail on the first toggle. The Toggle
pattern does flip the bar and close the popup. A normal `MenuFlyoutItem` (View > Split) closes on a click.

The item is now a `CheckMenuFlyoutItem`, a plain `MenuFlyoutItem` subclass with a gutter check glyph. It
exposes Invoke, and Invoke runs the command: the bar goes absent and the popup closes. The E2E helpers try
Invoke before a click. Rebuilt-app run: `shell.addressbar.hiddenAfterToggle`, `visibleAfterToggle`,
`shell.reflow.hiddenGapClosed`, `shell.reflow.shownAgain`, and `shell.hiddenTab.skipsHidden` all pass.

Still open: a mouse click at the item's centre did not close the popup even on the new class, while the
same click on View > Split does. The Invoke route is verified; the mouse route is not explained.

### Check mark out of step


The View > Address Bar check mark follows `IsAddressBarVisible`. The menu check test in
`tests/Bwc.E2E/Menus/MenuTests.cs` covers it.

## Window edge

### Dark strip behind the panes in light mode

The window client area is painted with the face brush. A pane or a splitter that uses a dark or
unset brush shows as a dark strip at the edge of the client area. A capture sampled at the right
and bottom client edges must match the face color. The check is in `ShellKeyboardTests.cs`.

## Parity package: About icon and font

| Way it could fail | Expected behavior |
| --- | --- |
| The icon from ExtractIconW leaks after ShellAboutW | DestroyIcon releases it after the call |
| View > Font applies only the size | The chosen face and weight are applied (LOGFONT). Blocked on DenseMetrics (outside this package), see the report |

# Console shell failure modes

Scope: piece U2a of release 0.2.0. `ConsoleShell` in `src/Bwc.UI/Console/Shell/`: the MMC 3.0 user-mode frame (menu bar,
standard toolbar, console tree, vertical splitter, result pane slot, status bar) that the Local Group Policy Editor hosts
and Local Security Policy (0.3.0) reuses. The frame knows nothing about policies: the caller supplies the menu table,
the tree nodes, the result content, the toolbar enable states and the status text. The result list, the description pane,
Customize View, Export List and Favorites are later pieces (U2b to U3b) and are not covered here.

Evidence (untracked, local): `artifacts/oracle/gpedit/probe.json` (UI Automation dump of classic at 192 DPI),
`docs/parity/oracle/gpedit/gpedit-resources.md` (mmc.exe menus 129, 157, 192, 194, accelerator table 117, bitmap 202).
Probe facts used: the tree is 312 px wide at 192 DPI (156 logical); the splitter is 6 px (3 logical); the status bar is
46 px (23 logical); the toolbar buttons are 23 by 22 logical; the menu bar items are File, Action, View, Help; the tree is
a Tree named by an internal window caption, the status bar is a StatusBar with automation id `StatusBar` and a first pane
`StatusBar.Pane0`; the F6 and Shift+F6 accelerators exist (ids 13213 and 13214).

The mechanics shared with the Registry Editor shell (menu keyboard hook, splitter thumb, DPI, theme) are described in
`ui-shell.md`, `menus.md` and `tree.md`. This file lists only what the console frame adds.

Declared choices that are not measured from classic (the audit lists them): the minimum tree width and the minimum result
width are 48 logical pixels each; the result pane slot is focusable even when it holds no content; Tab and Shift+Tab join
F6 and Shift+F6 in the pane cycle; Up One Level and Refresh are shown by default (the probed gpedit toolbar hides them),
and a host can hide any standard button.

## 1. Focus lost when a pane is hidden

Hiding the console tree while the tree has the keyboard focus collapses the focused element. WinUI then moves the focus
to nothing, so the next key press goes to the window and the cycle keys do nothing until the user clicks. The same holds
for a toolbar button that was clicked and then hidden.

- Handling: `IsTreeVisible = false` first moves the focus to the result pane when the focus is inside the tree pane, then
  collapses. A toolbar button never takes the focus (not a tab stop, no focus on pointer interaction), and the status bar
  has no focusable element, so those two need no move.
- Detector: `Console_Shell_ShowHide` hides the tree with the keyboard focus in it and checks the focused pane is the
  result pane afterwards and that F6 still works.

## 2. Splitter dragged to zero or past the window

A drag delta is relative to the thumb, so a fast drag to the left edge asks for a negative width and a drag past the right
edge asks for more than the window. A zero tree width hides the tree with no way to see the splitter again except by
chance; a width larger than the window pushes the splitter and the result pane out of view.

- Handling: the requested width is clamped to the minimum tree width (48) and to the client width minus the splitter minus
  the minimum result width (48). The drag changes the preferred width the caller persists; the clamp is applied on every
  delta and on every size change, not only at startup.
- Detector: `Console_Shell_SplitterDragAndLimits` drags far left, far right and back, and reads the tree pane rectangle
  after each. Escape during a drag puts back the width from before the drag.

## 3. DPI change while the app runs

The window moves to a monitor of another scale. DIP sizes (156, 3, 22, 23) stay right because XAML scales them. The toolbar
icons are bitmaps cut from a module strip at the pixel size of the scale in effect when they were loaded, so they stay
blurry or too small after the move if nothing reloads them.

- Handling: the shell re-reads the icons when `XamlRoot.Changed` reports a new rasterization scale, and the icon cache is
  per pixel size. No pixel value is cached anywhere else.
- Detector: `Console_Shell_Layout` reads the current window DPI and checks the tree width equals 156 logical at that DPI
  (the logical width is the same at every scale, so the check is meaningful at 96 and at 192). The toolbar reports the icon
  pixel size it rendered in its help text; the test checks it equals 16 times the scale. A live monitor change cannot be
  driven by the E2E harness and stays unverified (listed as unfinished).

## 4. High contrast

Hard-coded colors disappear in high contrast. The toolbar bitmaps are fixed colors on a black mask in the module strip; the
mask must become transparent or each icon sits in a black square on the system face color.

- Handling: every brush of the shell comes from `BwcBrushes` and is re-read on `BwcTheme.Changed`. Black pixels of the
  strip become transparent. Fallback glyphs use the window-text brush, so they follow the contrast theme.
- Detector: none in this piece. The harness cannot switch contrast themes; the Registry Editor high contrast suite covers
  the shared brushes. The shell screenshot in `Console_Shell_Layout` shows the default theme only.

## 5. Toolbar icon module missing or changed

The icons are read at run time from `mmc.exe` in the Windows system folder (bitmap resource 202, a 16 pixel strip of
19 images). A Windows build can move the resource, remove the file or change the strip width.

- Handling: the strip must have a height of 16 and a width that is a multiple of 16, and the image index must be inside
  it. Anything else, and any exception while loading, gives the fallback glyph (a Segoe icon font glyph) for that button
  and never throws. The result is logged once. The shell never ships the Microsoft bitmaps.
- Detector: `Console_Shell_ToolbarIconFallback` starts the app with `BWC_E2E_CONSOLE_ICON_MODULE` naming a file that does
  not exist (honored only in the e2e flavor) and checks every button is still present, named and enabled as the host set
  it, and that the toolbar reports the fallback source. `Console_Shell_Layout` checks the module source in the normal
  start.

## 6. Menu accelerators collide with the caller's

The skeleton entries (File > Options, File > Exit, View > Customize View, Help > Help Topics) carry access keys. A caller's
entries in the same popup can use the same letter. Two items with one access key make a keyboard user cycle instead of
reach the caller's item, and a top-level collision makes Alt+letter open the wrong menu.

- Handling: `ConsoleMenu.Build` removes the access key of a skeleton entry when a caller entry in the same popup uses it
  (the caller wins; the entry stays reachable with the arrow keys). Only popup collisions are resolved; the four top-level letters are fixed. Two caller
  entries with one key are left alone (the menu keyboard already cycles through them, as Win32 does).
- Detector: `Console_Shell_MenuKeyboard` starts the app with `BWC_E2E_CONSOLE_COLLIDE` (e2e flavor only), which adds a
  File entry whose letter is the Exit letter, presses Alt+F then that letter, and checks the caller entry ran and the
  window is still open.

## 7. F6 cycle with the tree hidden

With the tree collapsed the cycle has one member. A cycle that still includes the tree focuses a collapsed element, which
fails silently and leaves the focus where it was or loses it.

- Handling: the cycle is built from the panes that are visible. With one visible pane F6, Tab and their Shift forms keep
  the focus there (and handle the key, so XAML tab navigation does not walk into the toolbar or menu bar).
- Detector: `Console_Shell_ShowHide` hides the tree and presses F6, Tab and Shift+Tab twice each, checking the focus stays on the result pane.

## 8. Status bar hidden, then text set

A caller sets text while the bar is collapsed (for example a refresh finishes). If the text lives in the collapsed element
only, showing the bar later shows stale or empty text; if the set throws on a collapsed element the caller crashes.

- Handling: `StatusText` is a plain property of the shell that writes the pane; collapsing the bar does not clear it and a
  set while collapsed is stored. Showing the bar shows the latest text.
- Detector: `Console_Shell_ShowHide` hides the bar, sets text through the host, shows the bar and reads the pane text.

## 9. Keyboard menu entry with a modal open

Alt or F10 while a modal dialog window is open must not drive the owner's menu bar: the owner is disabled, so its window
does not receive the key. Per window state in `ClassicMenuKeyboard` also ends menu mode when the top window is
deactivated, so a menu bar left active when a dialog opens does not stay active.

- Handling: the shell uses the shared `ClassicMenuKeyboard`, which hooks the owner window tree only. The shell does not add
  its own Alt handling, so the behavior is the one the Registry Editor suites already cover with its modal dialogs.
- Detector: none in this piece: the scaffold host has no dialog. The first console dialog (U3a) adds the check.

## 10. Very long status text

A policy count or an error message can be thousands of characters. A text block that wraps grows the status bar and pushes
the panes up; one that does not trim widens the window's minimum size.

- Handling: the pane is one line, trimmed with an ellipsis, with a fixed bar height. The full text is the element's
  accessible name, so a screen reader and the test still read it.
- Detector: `Console_Shell_StatusBar` starts the app with a 5,000 character status text (`BWC_E2E_CONSOLE_STATUS_TEXT`,
  e2e flavor only) and checks the bar height equals 23 logical and the pane stays inside the bar.

## 11. Window narrower than the tree

Shrinking the window below 156 plus the splitter plus the minimum result width leaves the result pane negative or the
splitter outside the client area. Restoring the size must bring the tree back to the width the user chose, not to the
shrunken one.

- Handling: the preferred width (the property the caller persists) is never changed by a size change. The width in use is
  `min(preferred, max(minimum tree, client width - splitter - minimum result))`, recomputed on every size change. A client
  narrower than both minimum widths gives the result pane its minimum and lets the right edge clip, as any fixed layout.
- Detector: `Console_Shell_WindowNarrowerThanTree` shrinks the window, reads the tree width, restores the size and reads it
  again.

## 12. Cycle keys and the menu bar or toolbar

A Tab pressed with the focus on the menu bar would walk XAML's tab order into toolbar buttons or the splitter, none of
which classic reaches by Tab.

- Handling: the shell handles Tab, Shift+Tab, F6 and Shift+F6 itself for the whole control, so the toolbar buttons and the
  splitter are outside the cycle; a key from the menu bar starts the cycle at the tree (at the result pane when Shift is
  down).
- Detector: `Console_Shell_FocusCycle` presses the keys six times and checks only the tree pane and the result pane are
  ever focused.

## 13. Back, Forward and context-menu keys

Alt+Left and Alt+Right must run Back and Forward only while those commands are enabled; a disabled command must not be
raised and must not eat the key. Shift+F10 and the context-menu key must raise exactly one event: the tree already turns
those keys into its own context request, so a second raise from the shell would show two menus.

- Handling: the shell reads the enable state it was given for the button and raises the command only when enabled. For the
  context keys the shell handles the key before the tree sees it and raises one event with the pane that holds the focus
  (tree, result or other) and the selected tree node; mouse context requests from the tree are forwarded as the same event
  with a position.
- Detector: `Console_Shell_ToolbarStates` and `Console_Shell_ContextMenuKey` (keys through the host, which records each
  command and event in the status bar text).

## 14. Extension toolbar buttons and command ids

An extension button with the id of a standard button would be treated as the standard command, and two extension buttons
with one id would share enable state.

- Handling: standard ids are 1 to 99; extension ids start at 100. A duplicate or reserved id throws `ArgumentException` at
  registration, not later at click time.
- Detector: covered by the argument checks at registration; the scaffold host registers no extension button, so no E2E
  test drives the slot (listed as unfinished).

## 15. Missing resource keys and menu rebuild

A missing key in `Console.resw` gives an empty label, which makes a blank menu item. Replacing the menu table a second time
while the menu keyboard hook exists leaves the hook holding the removed items, so Alt no longer opens the new menus.

- Handling: `ConsoleStrings.Get` returns the key name when the string is empty (visible in review and in the test that
  reads the menu), and `SetMenu` re-creates the menu keyboard on the new items.
- Detector: `Console_Shell_MenuKeyboard` reads the menu names and opens one with Alt, so a missing label or a stale hook
  fails it.

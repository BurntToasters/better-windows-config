# Console Extended view failure modes

Scope: piece U2c of release 0.2.0. `ConsoleExtendedView` in `src/Bwc.UI/Console/Extended/`: hosts any result content (the
`ConsoleList`) with an optional description pane on its left and a tab strip (Extended, Standard) at the bottom. It knows
no snap-in: the caller supplies a `ConsoleDescriptionModel` (title, labeled plain text sections, links) and answers the
link event, so Local Security Policy (0.3.0) reuses it. A one line hook in `ConsoleShell` lets the result content take
Tab (see section 8).

Evidence (untracked, local): `artifacts/oracle/gpedit/probe.json` (steps `panes` and `result-list`, classic at 192 DPI)
and `docs/parity/oracle/gpedit/gpedit-resources.md` (admtmpl.dll `CLASSICRESULT.HTML`, string 247 "Requirements:").
Oracle facts used, in logical pixels (the UIA rectangles of the HTML pane are reported unscaled, the others at 192 DPI,
all halved): the Extended page is one `Internet Explorer_Server` pane over the whole result pane, 593 wide; its body
table is 589 wide with a left column of 212 (ratio 0.360) and the list starting at x 535; the tab strip (class
`AMCCustomTab`, type Tab, no patterns, not focusable) is 19 high under the result pane and holds two TabItems named
`Extended` (69 wide) and `Standard` (68 wide) with only the Invoke pattern; a text row in the left column starts 5 from
the column edge. Extended is the tab shown at start. mmc.exe has no accelerator for the tabs (the accelerator tables hold
F6, Shift+F6 and Tab only), and the tab items are not focusable, so classic has no tab key to copy.

Declared choices that are not measured from classic (the audit lists them): Ctrl+Tab and Ctrl+Shift+Tab switch the tab
(the only keys; classic has none); the tab items are painted as simple bottom tabs side by side (69 and 68 wide) instead
of the overlapping slanted ones; the title sits inside the description pane (classic draws a page title band across the
whole page above the list); the description pane never has a splitter and has a minimum width of 48 DIP; the title,
labels and text use the dense font; the tab strip height scales with the dense font size as the list rows do; a section
with no text shows its label only; the link is underlined and colored by BWC (blue light and dark variants, the system
hotlight color in high contrast). The description text itself is not from Microsoft resources: the placeholder wording is
BWC authored and the real text comes from the policy model (piece G).

This file was written before the code. Each section lists the ways a behavior can fail, the design choice that prevents
it and the E2E check that catches it. Every `Console_Extended_*` test writes `artifacts/e2e/visual/console-extended/
console-extended-<name>.json` (measurements) and a picture of the client area next to it. The host is `BwcGpedit.exe`
with the E2E-only seam `BWC_E2E_CONSOLE_HELP` (section 12) and the seam rows of `console-list.md`.

## 1. Empty help text

A policy with no help text gives a section with an empty string. An empty `TextBlock` is an unnamed Text element and an
empty gap; a null text throws in `Text =`.

- Handling: null and empty text are the same. The label stays and no body element is created, so UIA shows no unnamed
  Text and the layout has no blank block. An empty title is shown as an empty line of the title height, not hidden, so
  the pane layout does not jump. Null labels, ids or sections in a model are treated as empty; the model is copied on
  set, so a caller changing its lists afterward changes nothing.
- Detector: `Console_Extended_HelpShapes` selects a row whose help is empty and checks the Description label exists, no
  Text element has an empty name and no exception left the app (the app still answers UIA).

## 2. Very long help text

A 50,000 character help text makes a very tall `TextBlock`; WinUI text has a maximum layout height and past it clips or
throws, and a selectable block of that size is slow.

- Handling: the text is split at line breaks into paragraphs of at most 4,000 characters each (split at a space where
  there is one), each in its own `TextBlock` inside the scrolled stack. The scroller has a vertical bar; the horizontal
  scroll is off so the text wraps. The full text stays the accessible content (the paragraph blocks are named by their
  parts; the first carries the section id). The splitting is by length, not by content, so no text is dropped or
  duplicated (the check compares the concatenation).
- Detector: `Console_Extended_HelpShapes` selects a row whose help is 60,000 characters, checks the description scroller
  reports a scrollable height, scrolls to the end with the keyboard (focus on the scroller, End key) and checks the
  vertical offset grew and the app is responsive, and that the sum of the paragraph lengths equals the model length.

## 3. Help with ampersands and line breaks

A `&` in help text must show as a character: a label control or a menu path would turn it into a mnemonic. `\r\n`, `\r`
and `\n` in the text must each give one line break, not two blank lines or none, and angle brackets must not be read as
markup (the classic page is HTML, the model is plain text).

- Handling: the pane uses `TextBlock` only (no access keys in a TextBlock). Line breaks are normalized to `\n` before
  the text is set and each `\n` is a line break. Nothing is parsed: `<b>x</b>` shows as typed. The text of a link and of
  a label is also literal.
- Detector: `Console_Extended_HelpShapes` selects a row whose help is `Tom & Jerry <b>x</b>` plus three lines separated
  by `\r\n`, `\r` and `\n`, and checks the UIA name holds the ampersand and the brackets, and that the block is at
  least three line heights taller than a one line block.

## 4. No selection and a category selected

With nothing selected, or a tree node (a category) selected and no policy row, the pane must not show the last policy's
data, and must not be empty without a reason.

- Handling: the host sets a category model (the tree node name as the title, one unlabeled hint section) whenever no
  row is selected. The model of a row replaces it, and clearing the selection returns to the category model. The hint is
  BWC wording: a short sentence, not Microsoft's. A null model (a caller with nothing to say) shows an empty pane that
  keeps its width.
- Detector: `Console_Extended_DescriptionPane` checks the category model at start (title is the tree node, hint present,
  no link), selects a row (title is the row, link and both labeled sections present) and clears the selection with
  Ctrl+click (back to the category).

## 5. Pane narrower than its text

At a narrow window or after the tree splitter is dragged right, the 36 percent pane is a few pixels wide. A long word
overflows and the horizontal scroll would appear or the text would clip silently; a zero width gives a negative layout
size.

- Handling: the pane width is `clamp(0.36 * view width, 48, max(48, view width - 48))`; the content never gets less than
  48 when the view has room, and the pane is never negative. Text wraps at any character when a word does not fit (WinUI
  `TextWrapping.Wrap`), the horizontal scroll is off, so no text sits right of the pane edge. The ratio is of the whole
  view (list included), as in the classic table.
- Detector: `Console_Extended_PaneWidth` measures the pane width at three window widths and after dragging the tree
  splitter far right, checks it equals the formula within one DIP at each, and that a long unbroken help word stays
  inside the pane rectangle.

## 6. Standard selected, then the selection changes

In Standard the pane is collapsed. A model set while it is collapsed must be kept, so the next switch to Extended shows
the current row, not the row from before; a collapsed pane must not throw on text set, or fire a link event.

- Handling: `Description` is a plain property that rebuilds the pane's children whether it is visible or not; the tab
  only collapses the pane and its column. The link of a collapsed pane is not reachable (collapsed elements are not
  focusable, and the Tab hook skips them).
- Detector: `Console_Extended_TabSwitch` selects Standard, changes the row selection twice with the arrow keys, selects
  Extended and checks the title is the row now selected; it also checks the pane was not showing in Standard (no
  rectangle).

## 7. Tab switch keeps list selection and focus

Moving the list between columns or re-parenting it on a tab switch would recycle its rows and drop its selection, its
focus row and its scroll offset, and the keyboard focus would fall to nothing when the focused element is collapsed.

- Handling: the content element is never moved. It stays in the same grid cell; only the description pane and its column
  collapse, so the list is resized, not rebuilt (its own resize path keeps selection, focus and offset, see
  `console-list.md`). When the keyboard focus is inside the pane (the link or the scroller) and the tab changes to
  Standard, the focus moves to the content's focus target first, as the shell does for the tree. The tab items take no
  focus on a click or UIA invoke, so a switch from the mouse leaves the focus where it was.
- Detector: `Console_Extended_TabSwitch` selects a row and focuses the list, switches with Ctrl+Tab, then with a click
  on the other tab, then with UIA Select, and checks after each that the list readout (selected tags, focus index, mode)
  is unchanged, the focused pane is the result pane and an arrow key still moves the list focus; and starting with
  focus on the link, the switch to Standard leaves the focus on the list.

## 8. Tab and the link by keyboard

The shell handles Tab for the pane cycle before anything inside the result pane sees it, so the link is unreachable;
and a link that takes Enter or Space twice (key down and key up) raises two events.

- Handling: `ConsoleShell` gives the result content one hook, `ResultPaneTabHandler` (the only change in
  `Console/Shell`): for Tab and Shift+Tab with the focus inside the result pane the shell first asks the handler, and it
  runs the pane cycle only when the handler answers false. `ConsoleExtendedView` answers true while it can move the focus
  to another stop of its own, in this order: the content focus target (the list), each link in model order, the
  description scroller when it can scroll. Past the last stop forward, or before the first stop backward, it answers
  false and the shell cycles to the next pane. The link is a `HyperlinkButton`: Enter and Space raise one `Click`, which
  raises one `DescriptionLinkInvoked` with the link id.
- Detector: `Console_Extended_Link` presses Tab from the list and checks the link has the keyboard focus (UIA Hyperlink
  type, name from the model), presses Enter and checks the host recorded exactly one invocation with the link id, then
  Tab again and checks the focus left the result pane (to the tree through the cycle). Shift+Tab from the link returns to
  the list, and Shift+Tab from the tree enters the result pane at the list (the shell's cycle entry), not at the link.

## 9. DPI

Pixel sizes from the 192 DPI probe copied as constants are wrong at 96 and 125 percent; the ratio of the pane and the tab
height must not depend on the scale.

- Handling: all sizes are logical (DIPs): the ratio 0.36, the minimum 48, the tab height 19 (scaled by the dense font
  size over its base) and the tab widths 69 and 68. Text wraps at the width in use; nothing is cached in pixels.
- Detector: the tests read the window DPI and compare logical sizes (the pane width formula, the tab height 19 within one
  DIP), so they hold at 96 and 192. A live monitor change cannot be driven by the harness and stays unverified.

## 10. High contrast

Fixed colors disappear against a contrast palette: a blue link on a black window, a black text on a dark window.

- Handling: the pane and strip colors come from `BwcBrushes` (updated in place on a palette change); the link brush is
  re-read on `BwcTheme.Changed` and uses the hotlight system color when the palette is high contrast (and the window text
  color when the system cannot say).
- Detector: `Console_Extended_HighContrast` starts with `BWC_E2E_HIGH_CONTRAST=1`, takes a picture, and checks the pane
  background pixel equals the palette window color, the selected tab background equals the window color and the unselected
  tab equals the face color, and that the link ink is not the light theme blue.

## 11. Tab strip in UI Automation

A strip built from plain `Border`s shows no Tab and no TabItems, and a screen reader cannot switch the view; items with the
same name or no name cannot be told apart.

- Handling: the strip has a custom peer of type Tab (Selection pattern, single, selection required); each item has a peer of
  type TabItem named by its label with the SelectionItem and Invoke patterns. Select, Invoke and a click all call the same
  setter, which raises `SelectedTabChanged` once per real change. The strip is named by a BWC label. The tab text is
  `Extended` and `Standard` (the short labels of the classic strip).
- Detector: `Console_Extended_Tabs` finds the Tab control and its two TabItems by type and name, reads IsSelected,
  invokes Standard through the Invoke pattern, selects Extended through SelectionItem, and checks one change event each
  (the host counts them in the status bar) and that selecting the selected tab raises none.

## 12. E2E seam leaking into the product

The synthetic help texts must not exist in a ship build, and a malformed value must not crash the host.

- Handling: the seam is under `#if BWC_E2E` in `GpeditRootView`. `BWC_E2E_CONSOLE_HELP` is one of `empty`, `long`,
  `markup`, `wide` (an unbroken 300 character word) or anything else (ignored: the normal placeholder). The ship build
  has the same pane with the placeholder model and no rows, until piece G supplies the policy text. The UIA readout of the
  view (`Tab`, `PaneWidth`, `Paragraphs`) is E2E only.
- Detector: `node scripts/dotnet-build.js --rid win-arm64 --flavor ship` builds without the seam.

## 13. Tests, bounds and artifacts

| Test | Sections |
| --- | --- |
| `Console_Extended_Tabs` | 11 |
| `Console_Extended_DescriptionPane` | 4, 9 |
| `Console_Extended_PaneWidth` | 5, 9 |
| `Console_Extended_HelpShapes` | 1, 2, 3, 5 |
| `Console_Extended_TabSwitch` | 6, 7 |
| `Console_Extended_Link` | 8 |
| `Console_Extended_HighContrast` | 10 |

Not covered by an E2E check: a live DPI change (section 9), the real system high contrast switch (the harness uses the
fixed palette), copying with the keyboard on a text selected by the mouse (selection by mouse drag is not driven here; the
copy of the whole pane text with Ctrl+C on the scroller is checked in `Console_Extended_DescriptionPane`).

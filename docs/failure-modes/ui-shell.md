# UI shell, menu, and keyboard failure modes

The classic Registry Editor shell is a WinUI `MenuBar` over an address bar, a tree pane, a
splitter, and a list pane. `ClassicMenuKeyboard` adds the Win32 menu keyboard model through a
window subclass. This document lists the ways that shell can fail, how the failure shows up,
and what the code does about it.

## 1. WinUI chrome makes rows taller than the oracle

Default WinUI controls carry `MinHeight`, `Padding`, `Margin`, and border values that are larger
than the classic metrics. A `MenuBar` row that should be 19 DIP can render at 32 or 40 DIP
because the item template keeps its own minimum height. The shell avoids this by setting explicit
heights (`DenseMetrics.MenuBarHeight`, `ListRowHeight`, `HeaderHeight`) and by applying keyed
styles in `Themes/Dense.xaml` that zero `MenuFlyoutItem.MinHeight` and remove separator margins.
A visual check must measure the rendered row, not just the property value, because a template can
still add padding above the requested height. Styles are keyed so ordinary `TextBox` controls
elsewhere in the application keep their normal Fluent sizing.

## 2. DPI scaling

The oracle capture is in physical pixels at 200 percent. `DenseMetrics` stores DIPs, so the
captured values are halved exactly once. Applying the halving twice makes every row half its
intended size. Applying it zero times makes every row twice its intended size. Window sizes are a
separate case: `AppWindow.Resize` takes physical pixels, so the default 1000x700 DIP window is
multiplied by the XamlRoot rasterization scale before it is passed to `Resize`. A hard-coded
1000x700 pixel call looks correct on a 100 percent display and shrinks on a 200 percent display.

## 3. Per-monitor DPI changes

Dragging the window between monitors with different scales changes the XamlRoot rasterization
scale. XAML re-lays out DIP-sized elements, so the shell metrics stay correct, but any pixel
value cached once (for example a manually computed window size) becomes stale. The shell reads
metrics from `DenseMetrics` in DIPs and does not cache pixel sizes. A splitter width that was
dragged to 600 DIP stays 600 DIP after the move, which may exceed the new monitor width; the
splitter clamp is re-applied on each drag and keyboard move, not only at startup.

## 4. Theme and high contrast brushes

Hard-coded colors break dark mode and high contrast. Every brush in `Dense.xaml` and
`ClassicWindowShell.xaml` is a `ThemeResource` (for example `LayerFillColorDefaultBrush` and
`DividerStrokeColorDefaultBrush`). A missing theme resource is a XAML parse failure at startup,
not a silent fallback. The list and tree panes therefore use only brushes shipped with
`XamlControlsResources`. High contrast is verified by checking that the splitter remains visible
against both pane backgrounds.

## 5. Menu keyboard hook misses descendant HWNDs

WinUI keyboard focus lives in a child HWND of the top-level window, so `WM_SYSKEYDOWN` and
`WM_KEYDOWN` are delivered there, not to the top-level HWND. `ClassicMenuKeyboard` subclasses the
top-level window and every descendant returned by `GetWindow(GW_CHILD)` plus `GW_HWNDNEXT`.
Hook only the top-level window and Alt, F10, and mnemonics appear dead. Hook too early and the
descendant HWND may not exist yet, so the shell creates the router on `Loaded` and calls
`HookTree` again after layout. HWNDs created later (for example by a new flyout) are not hooked
automatically; the public `HookTree(nint)` exists so a caller can refresh the set.

## 6. Alt or F10 swallowed before the window procedure

XAML's island input pipeline handles some keys before the window subclass sees them. Arrow keys
are the known case, so the shell handles Down in `MenuBar.PreviewKeyDown` and leaves Left and
Right to the XYFocus ring. If a future WinUI version starts consuming Alt or F10 in XAML, the
window hook will never run and the bar will not activate. The shell cannot detect that from
managed code; the symptom is a menu bar that works with the mouse but not with the keyboard, and
the remedy is to add the key to `HandleKeyFromXaml` and to the PreviewKeyDown handler.

## 7. Escape leaves a flyout open

Escape has two meanings. While the bar is focused, Escape deactivates the bar and restores the
saved focus. While a flyout is open, XAML closes the flyout and the router moves back to
`BarFocused` without restoring focus. If the router deactivated the bar at the same time, the
flyout would be left open with no focused item, and the next key would go to the old control.
The router only changes its own mode for Escape while a flyout is open; it does not call
`Collapse` there, because XAML is already closing the flyout.

## 8. Mnemonics collide

Top-level mnemonics are F, E, V, A, and H. The classic Edit menu uses `&New` for N, `&Delete` for
D, and `E&xit` for X inside File, so the same letter can appear at different levels without
collision. The router only looks at top-level items, in order, and takes the first match. A
duplicate top-level mnemonic therefore makes the later item unreachable by keyboard. The menu
table from the oracle is checked for duplicates when it is edited. Submenu mnemonics are handled
by XAML once the flyout is open.

## 9. Focus restore to a closed element

`ClassicMenuKeyboard` saves the focused element before activating the bar and calls `Focus` on it
when the bar is deactivated. If that element was removed from the visual tree while the menu was
open (for example a flyout item that closed its own dialog), `Focus` returns false and focus may
stay on the menu bar. The router does not throw. The fallback is the window's normal focus
navigation: the next Tab or click moves focus to a live element. A future fix would walk up to the
nearest live ancestor before restoring.

## 10. Splitter limits and keyboard mode

The splitter clamps the tree pane to 120 through 600 DIP for both pointer drags and keyboard
moves. A drag that goes past a limit is clipped, and the accumulated drag width is clamped rather
than wrapped. Keyboard mode focuses the splitter with `BeginSplitKeyboardMode`, moves 8 DIP per
Left or Right press, and leaves the mode on Enter or Escape by focusing the shell. If the splitter
loses focus any other way (a click, a flyout, Alt+Tab), the KeyDown handler stops receiving keys;
that is treated as leaving keyboard mode. The splitter is a `Thumb`, so a drag that starts on it
captures the pointer and does not select text or move the window.

## 11. Address bar toggle changes layout

The address bar row uses `Auto` height and the bar itself is collapsed when hidden. If the row
kept a fixed 19 DIP height, hiding the bar would leave an empty strip. If only visibility changed
and the row stayed `Auto`, the row would correctly shrink to zero, but both are set so the layout
is unambiguous. Toggling during a splitter drag is not supported: the drag continues against the
old tree width. Toggling from the View menu closes the flyout first, so the shell does not try to
focus the address bar while a flyout still owns focus.

## 12. Empty menu table

An empty table produces a `MenuBar` with no items. `ClassicMenuKeyboard` guards activation,
mnemonic lookup, and state publication against zero items, so Alt and F10 do nothing instead of
indexing item zero. `RegeditMenu.Populate` clears the bar and adds nothing, which is a valid, if
useless, window. The failure is caught by a visual check that the File menu exists.

## 13. Missing .resw keys

Every menu string comes from `src/Bwc.Regedit/Strings/en-US/Resources.resw`. A missing key makes
`ResourceLoader.GetString` return an empty string, so the menu item renders with no text and no
access key. The parser tolerates an empty string and produces no exception, which hides the
mistake. Missing keys are therefore caught by a table driven check that every oracle id has a
non-empty text, not by waiting for a crash. The `.pri` file must contain the resource map; a
resource map name typo has the same empty-string symptom.

## 14. XAML generated Main

If a project defines `DISABLE_XAML_GENERATED_MAIN` without providing a replacement entry point,
the application fails to link or starts without initializing the XAML framework. The Registry
Editor uses the generated `Main`, which calls `Application.Start` and creates the `App` instance.
The menu keyboard uses `SetWindowSubclass` from managed code, so no custom entry point and no
`DISABLE_XAML_GENERATED_MAIN` define are needed. Removing the generated Main also removes the
XAML resource initialization that loads `Bwc.UI/Themes/Dense.xaml`.

## 15. Publish drops the .pri file (0xC000027B)

An unpackaged WinUI app needs `BwcRegedit.pri` next to the executable. A build folder normally has
it; a hand-copied or partially published folder may not. Without it the process starts and then
terminates with `0xC000027B` (an application internal exception) before showing a window, because
the XAML resource lookup cannot resolve `ms-appx:///Bwc.UI/Themes/Dense.xaml`. The launch smoke
checks the bin output for `BwcRegedit.pri`; publishing is done by the normal build pipeline, which
generates the `.pri` from the XAML and `.resw` inputs.

## 16. App.xaml missing XamlControlsResources

`XamlControlsResources` supplies the Fluent control templates and the theme brushes used by the
shell. Without it, `MenuBar`, `Thumb`, and `TextBox` have no template, and startup fails with
`0xC000027B` or renders an empty window. The Registry Editor `App.xaml` merges
`XamlControlsResources` before `ms-appx:///Bwc.UI/Themes/Dense.xaml`, so the keyed styles in the
dense dictionary can refer to theme resources. Removing either merged dictionary breaks the
shell even though the C# compiles.

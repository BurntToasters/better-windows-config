# Menu failure modes

Scope: the menu bar (File, Edit, View, Favorites, Help), its popups and submenus, the mnemonic
underlines, the keyboard model that opens and navigates them, and the window edge that sits beside
the client area. The menu table is built in `src/Bwc.Regedit/Commands/RegeditMenu.cs`. The
chrome and keyboard live in `src/Bwc.UI/Themes/Dense.xaml`, `src/Bwc.UI/Shell/ClassicWindowShell.xaml(.cs)`,
`src/Bwc.UI/Menu/`. The oracle is `docs/parity/oracle/regedit-metrics.json` (menu bar and top-level
rectangles) and `docs/parity/oracle/regedit-menu-metrics.json` (popup metrics measured from classic
regedit for this package).

## Menu bar geometry

### Text clipped by a forced height

The classic bar is 19 DIP at 200 percent (38 px). The default MenuBarItem template keeps its own
minimum height and padding, so a 19 DIP bar clips the text unless the item template is replaced.
The bar height and the item height both come from `DenseMetrics.MenuBarHeight`. A font size change
recomputes both through `ApplyMetrics`. A check must read the text rectangle and see no clipped
glyph rows at 100, 125, 150, 175, 200, 225, 250, and 300 percent.

### Item width drifts from the oracle

Each top-level rectangle in the oracle is the text width plus a fixed padding on each side. If the
padding is set per item by guess, File, Edit, View, Favorites, and Help drift apart. The padding
is one value for all items, derived from the oracle widths and the Segoe UI 12 DIP text widths,
and the E2E check compares each item rectangle with the oracle within one DIP at the window DPI.

### Mnemonic text width differs from the oracle

The oracle widths include the mnemonic letter. The WinUI item must render the same text with the
ampersand removed. A menu that shows a literal ampersand fails the width check.

## Popup geometry

### Fluent chrome instead of classic chrome

The default MenuFlyoutPresenter has rounded corners, a shadow, and Fluent item padding. Each item is
the classic item height from the measurement. The presenter and item templates are replaced, not
patched with individual setters, because the corner radius and item hover shape come from template
parts and theme resources.

### Corner shape disagrees between captures

`regedit-menu-metrics.json` says "Square popup corners". A read-only capture of classic regedit for
this package (File popup, 548 by 410 px at 192 DPI) shows a curved 1 px border: the border line
starts about 8 device px from each corner, so the radius is about 8 px, which is 4 DIP. The
presenter uses 4 DIP. A square presenter fails the corner check in `MenuTests.cs`, and a radius of
8 DIP (16 device px) would round the corners more than the capture does.

### Shadow missing

Classic popups have a soft shadow. The capture shows the edge darkening over about 12 device px
below and to the right of the popup, lightening toward the background. The presenter casts a
ThemeShadow from its border. A presenter with no shadow reads as flat against the window.

### Popup width drifts from classic

Classic popups size to the widest label plus the accelerator column, with a fixed gap. The formula
that reproduces the measured widths at 192 DPI is, in device px, 66 (text left) + widest label +
44 (gap) + widest accelerator + 45 (right inset). It gives File 542 px and Edit 392 px for the item
rectangles (popups are 548 and 398 px, 3 px each side more), and it also gives View 401 px, Help
395 px, and Edit > New 411 px. A WinUI popup sized by its content is narrower, because a label
column with no reserved gap ends at the accelerator. Each leaf and submenu item takes the classic
width as its MinWidth. The label widths are GDI widths of the menu font, as the bar uses.

### Keyboard focus frame

The classic keyboard focus is a 1 px black rounded frame inside the item, and the fill does not change
(`#F9F9F9` in the light capture, the same as the unfocused items). The package matches that: a
0.5 DIP frame in the window text color, no fill change. A fill on the keyboard-focused item, or no
frame, fails the focus check in `MenuTests.cs`.

### Item height depends on the template

An item whose MinHeight is zero but whose template keeps a fixed Fluent row height is taller than
the measured classic height. The E2E check reads each item's UIA bounding rectangle and compares its
height with the measured item height.

### Separators are invisible

`MenuFlyoutSeparator` draws a line that uses a theme brush that is the same as the popup background
in the light palette. A separator must be a 1 px line of the classic shadow color, sampled from the
capture, and it must have the classic separator height, not Fluent's 1 px with 4 px margins.

### Flyout animation

The Fluent entrance transition runs on every open. Classic menus appear at once. The presenter
style clears the transitions when the platform allows it. A failure here is cosmetic, so the check
is a capture comparison and not a timing check.

### Submenu item left as default

`BuildFlyoutItem` styles leaf items with `BwcMenuFlyoutItemStyle` but builds `MenuFlyoutSubItem`
(New) without the style, so New keeps the Fluent chrome. Every MenuFlyoutSubItem must receive the
same item style as a leaf, and the submenu arrow must sit in the right gutter.

### Check mark and gutter

View > Address Bar is a toggle, so its item needs a check mark when the address bar is visible. The
check mark sits in a left gutter that exists on every item, so text does not move when the check
appears. The check mark is drawn from the item template and not from `IsChecked` on a ToggleMenuFlyoutItem,
because the command table uses MenuFlyoutItem and the id contract must not change.

### Accelerator text alignment

The oracle text after a tab is an accelerator such as Ctrl+P. It is right aligned in the right gutter,
not appended to the label. `KeyboardAcceleratorTextOverride` carries the text. A label that is
too long must not push the accelerator out of the popup. The popup width is the widest label plus
the two gutters.

## Mnemonics

### Underline visibility

Classic Windows underlines the mnemonic letter when the menu is opened from the keyboard (Alt, F10,
or a letter on the bar) and hides the underline for pointer use. The menu keeps one mode flag:
keyboard mode shows underlines on the bar and in every open popup, and a left or right button press
clears it. Showing underlines for pointer opening was the other allowed choice; this implementation
uses the keyboard-only choice.

### Underline on the wrong letter

The mnemonic letter is the one after the ampersand in the resource text. A search for the first
matching letter gives the wrong character for labels such as `F&avorites` (the first `a` is the
mnemonic only by luck) and `Sp&lit`. The underline index is the first character equal to the access key, with its case, in the visible
text. The parsed access key keeps the case of the resource mnemonic, so "Connect &Network" underlines
the N of Network and not the n of Connect. A case-insensitive match is only the fallback. The
resource table does not store the ampersand position, so an access key that appears in exact case
earlier in a label would still be chosen; no current label has that shape, and the bar and popup
checks cover File, Edit, View, and New.

### Access key without underline

WinUI's AccessKey shows a KeyTip box, not an underline. A KeyTip is not the classic look and it
must not be the only mnemonic cue. The underline is drawn by the item template.

## Keyboard model

### Letter inside an open popup

With a popup open, pressing a mnemonic letter invokes the matching leaf command or opens the
matching submenu. The match is case insensitive, only among enabled items, and the item must be in
the open popup (not the bar). Separators and disabled items are skipped. When a submenu is open, the
only the innermost open popup is searched, as in a Win32 menu, so a letter that matches a parent item while a submenu is open does nothing.

### Letter while no popup is open

A letter with the bar active opens the top-level item (existing behavior, see `TryMnemonic`).
A letter with the bar idle and no Alt does nothing, so ordinary typing in the tree is not stolen.

### Escape closes one level

Escape in a submenu closes the submenu and returns to the parent popup. Escape in a top-level popup
returns to the bar. Escape on the bar deactivates the menu and restores the focus that was active
before activation.

### Invoke through automation

A leaf is invoked through `IInvokeProvider` on the item's automation peer, so the same command
path runs as a click. A submenu is expanded through `IExpandCollapseProvider`. Calling `Click`
directly would skip the flyout close and leave the popup open.

## Window edge

### Black strip on the right and bottom edges

The window client area can be a pixel or two larger than the XAML content when the size is
rounded at a fractional scale. The area outside the root content is the island background, which is
not the face color. The shell paints its full area with the face brush and fills the root grid, so
the only gap left is outside the content. A capture sampled at the last two columns and rows must
match the face color.

## Measurement and E2E

### Classic regedit not found

The classic metrics run starts `C:\Windows\regedit.exe` with `__COMPAT_LAYER=RunAsInvoker` on the
child only. A launch without that variable can raise UAC and block the desktop. If the window class
`RegEdit_RegEdit` does not appear within the timeout, the measurement stops and the JSON is not
written with guessed numbers.

### Popup not in the main window capture

A popup is a separate top-level window of class `#32768`. A PrintWindow capture of the main window
does not include it. The popup is captured from its own rectangle, read from UI Automation, and
the screen is copied for that rectangle after it is foreground.

### Shared GUI mutex

Classic regedit and the BWC menu tests drive the desktop. Every run holds `Local\BwcGuiTestLock`
for the whole drive, so a parallel GUI test cannot move focus or close a popup in the middle of a
keyboard sequence.

### Screen DPI and rounding

The oracle is at 192 DPI. Checks use `OracleMetrics.DevicePixels` at the window DPI with a
tolerance of one DIP in device pixels. A 1 px rounding error at 125 percent is inside the tolerance.

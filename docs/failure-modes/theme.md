# Theme and palette failure modes (UF1)

Scope: `src/Bwc.UI/Theme` (`BwcTheme`, `BwcPalette`, `BwcBrushes`, system color reader, and
`BwcExplorerSelection`, which reads the tree selection from the Explorer visual style),
`src/Bwc.UI/Themes/Dense.xaml` (brush keys and control styles), `src/Bwc.UI/Tree/DenseTheme.cs`,
the color and focus code in `TreeRowElement`, `DetailsRowElement`, `DetailsHeaderPanel`, and
`ClassicWindowShell`. The E2E test is `tests/Bwc.E2E/Theme/ThemeTests.cs`.

Decisions in force: the theme follows the system (light, dark, high contrast) with an app
override of System, Light, or Dark. Light uses the system color table (`GetSysColor`). Dark is
computed. High contrast uses the system color table as well. Persistence of the override is the
app's job; this package never writes a settings file.

## Palette source

### Light colors do not come from the Fluent theme

If a light row takes its colors from `ThemeResource` keys, it shows Fluent blue and gray, not
classic regedit. The light palette is read from `GetSysColor` on every apply. The mapping is:

| Role | System color | Value on the test machine |
| --- | --- | --- |
| Pane and header background | `COLOR_WINDOW` | `#FFFFFF` |
| Text | `COLOR_WINDOWTEXT` | `#000000` |
| Focused list selection fill and unfocused list selection (hidden) | `COLOR_HIGHLIGHT`, `COLOR_WINDOW` | `#0078D4`, `#FFFFFF` |
| Focused list selection text | `COLOR_HIGHLIGHTTEXT` | `#FFFFFF` |
| Gray text | `COLOR_GRAYTEXT` | `#6D6D6D` |
| Splitter and face | `COLOR_3DFACE` | `#F0F0F0` |
| Dividers and edges | `COLOR_3DSHADOW` | `#A0A0A0` |
| Edge highlight | `COLOR_3DHILIGHT` | `#FFFFFF` |

The tree selection is not in this table. Its fill and border come from the Explorer visual style
(see the next section).

### Selection colors follow classic regedit, not the system highlight

The oracle capture is `docs/parity/oracle/regedit-selection-colors.json`. It was sampled from
`C:\Windows\regedit.exe` on the test machine at 200 percent, read only. The values:

| Pane and state | Fill | Border | Text |
| --- | --- | --- | --- |
| Tree, focused | `#CCE8FF` | 1 px `#0078D4` | `#000000` |
| Tree, not focused | `#D9D9D9` | 1 px `#949494` | `#000000` |
| Tree, hover | no change (tree has no `TVS_TRACKSELECT`) | none | none |
| List, focused | `#0078D4` (`COLOR_HIGHLIGHT`) | none | `#FFFFFF` (`COLOR_HIGHLIGHTTEXT`) |
| List, not focused | no selection drawn (row stays `#FFFFFF`) | none | `#000000` |

The tree uses the Explorer visual style, so its colors are read at runtime from
`Explorer::TreeView` (`BwcExplorerSelection`). The list is the classic list view, so its focused
selection is `COLOR_HIGHLIGHT` and its unfocused selection is hidden. The classic list does not use
the Explorer theme, so `Explorer::ListView` is not used for the list. Keeping it would paint
`#CCE8FF` instead of the measured `#0078D4`.

Failure: the Explorer visual style is off (classic theme), or `OpenThemeData` fails for
`Explorer::TreeView`. The tree falls back to `COLOR_HIGHLIGHT` and `COLOR_BTNFACE`, which is the
pre-change look. The palette does not fail, and the E2E test reports the fallback in its sample
file.

Failure: the Explorer rendering returns a fill that is not the expected value after an OS update.
The E2E test derives the expected values from the same visual style at test time, and it also
checks them against the oracle constants. A mismatch against the oracle is reported as a check
failure, not hidden.

Failure: the DIB is rendered at the wrong DPI. The rendered rectangle is scaled by the system DPI
(`GetDpiForSystem`). The cache is keyed by DPI and visual style state, so a DPI change renders
again. A per-monitor DPI change that the system DPI does not reflect falls back to the cached value
until the next key change. The tree border is one device pixel because the sampled border is read
from the rendered bitmap, not computed from the DPI.

Failure: a theme change that flips the visual style on or off (a classic theme) leaves the cached
value in place. The cache key includes whether a visual style is active, so the next palette apply
re-reads the theme.

Failure: text color. `GetThemeColor(TMT_TEXTCOLOR)` returns a value that is not black on this build.
The tree text would then differ from `COLOR_WINDOWTEXT`. The E2E test samples the darkest pixel in
the selected label, so a theme text color that differs from `#000000` is caught.

Failure: a hover highlight is added for the tree. The oracle has none, so adding one would
reintroduce the mismatch. The hot states are not rendered and the palette has no hover slot.

Failure: the selection border is drawn on the whole row, while classic draws it around the label
only (a box about 64 by 16 DIP at 200 percent). The fill is also full row width in BWC. The box
shape is geometry and is not changed by this package, so the border follows the row. The
difference is recorded as a known gap.

Failure: the dotted focus rectangle is still drawn on the focused tree and list rows. Classic
regedit draws no dotted rectangle on either selection in the oracle. The package keeps it, colored
with the selection text color (`COLOR_WINDOWTEXT` on the light tree, `COLOR_HIGHLIGHTTEXT` on the
list), so it stays visible on the light fill. Removing it is a separate change.

### Dark colors are derived, and the accent is the system accent

Dark has no system color table entry, so the palette is a fixed neutral set plus the Windows
accent (`UISettings`, `UIColorType.Accent`) for the focused selection. The text on the accent
is black when the accent is light, and white otherwise, so the selected text always has contrast.

The dark focused and unfocused selections use the same structure as the light ones: a fill, and
a border that may be transparent. The dark border is transparent in both states, so the dark
look is unchanged.

## Runtime switching

### Brushes are mutated, not replaced

Rows and panes hold references to brush objects. If a switch created new brushes, realized rows
would keep the old colors until they were recycled. `BwcBrushes` owns one `SolidColorBrush` per
role, created by `Themes/Dense.xaml` and looked up by key. A switch writes `Color` on those
objects, so every realized row updates at once.

Failure: a switch that replaces a brush in `Application.Resources` leaves rows on the old one.
The E2E dark test asserts the colors read from brushes that were changed by the `BWC_E2E_THEME`
apply path, which is the same path a runtime switch uses.

### Pane focus changes colors without touching rows

The focused selection and the unfocused selection differ only by brush color. The shell
reports pane focus with `BwcTheme.SetPaneState`, which writes the tree or list selection brush.
Rows never need to be re-applied when focus moves between panes.

Failure: a focus change that is not reported (for example a focus move that bypasses the
`GotFocus` and `LostFocus` handlers) leaves both panes in the wrong state. The shell re-reads the
focused element through `FocusManager.GetFocusedElement` on each event, and defers the read to the
dispatcher so the focus element has already moved when it is read.

### Element theme follows the palette, the application theme does not

Fluent controls (menu flyouts, text boxes, scroll bars) read `RequestedTheme`. The shell sets
`RequestedTheme` on its own root from `BwcTheme.ElementThemeFor`, and updates it on
`BwcTheme.Changed`. `Application.Current.RequestedTheme` is not written. Writing it while the
window exists faulted inside `Microsoft.UI.Xaml.dll` (exit code 0xC000027B on launch), so the
application theme stays with Windows and the other windows are unaffected.

Failure: a popup (menu flyout) that does not inherit the element theme keeps the Windows theme.
The E2E tests sample the pane and selection colors only. Popups are not sampled, so that case is
not covered by a check and is left for a manual look.

### Settings changes arrive on other threads, and may be refused

`UISettings.ColorValuesChanged` and `AccessibilitySettings.HighContrastChanged` may fire off the
UI thread. Handlers marshal to the dispatcher queue captured on the UI thread. Creating a
`SolidColorBrush` or changing a brush off the UI thread is never allowed.

The unpackaged Registry Editor is refused the subscription to these WinRT events (COM error
0x80070490, element not found). The first version let that exception escape the shell
constructor, and XAML failed fast with exit code 0xC000027B. The subscriptions are now wrapped,
and the settings reads return defaults when the object throws. The shell also re-reads the system
state from its `ActualThemeChanged` handler (`BwcTheme.RefreshSystem`), so a refused event costs
only the immediate refresh. High contrast switching therefore relies on that fallback, which is
not covered by E2E.

## Focus rectangle

### The system focus visual surrounds the pane

`DenseTree.xaml` and `DetailsList.xaml` set `UseSystemFocusVisuals="True"`. That draws a rounded
rectangle around the whole pane. The shell sets `UseSystemFocusVisuals = false` on both
controls, and each row draws its own focus rectangle. This is done from the shell because
`DenseTree.xaml` and `DetailsList.xaml` are owned by the tree and list workers.

Failure: a future XAML change re-enables the system visual. The E2E test samples the pane
border and requires the pane's outer edge to be the background color, not the focus color.

### Only the focused row shows the rectangle

A row shows its rectangle only when it is selected and its pane has keyboard focus. The rectangle
is a `Rectangle` child with `StrokeDashArray` of `1,1` and a stroke of one device pixel, computed
as `1 / XamlRoot.RasterizationScale` DIP, so it is one pixel at 200 percent scale. A selected row
sets its stroke to the focus brush, and every other row sets the transparent brush. Toggling
`Visibility` on each selection change was tried first and removed, because it invalidated layout
on every row and the full E2E run then failed the editing rename test once in two runs.

The first attempt used `Stretch.Fill` and an inset of 1.5 device pixels. The dash edges then fell
between device pixels, and anti-aliasing blended every dash into a solid half-tone line, which is
not dotted. The fix keeps the shape at a fixed size with `StrokeDashOffset` of 0.5 so the dash
edges land on pixel boundaries. The measured dots then appeared one device pixel lower than the
nominal inset, so the inset is half a device pixel and the E2E test scans three rows below the
row top for the dotted pattern.

Failure: the list has several selected rows. The list does not expose its focus index, so the
rectangle is shown only when exactly one row is selected. A multi-selection shows no rectangle.
This is a known gap until the list exposes its focus index.

Failure: a rectangle changes the row's measured height. It is an overlay with no effect on the
text measurement, and the row height E2E check (34 px at 200 percent) still applies.

## Layout and metrics

### Colors never change geometry

Metrics stay in `DenseMetrics`. Theme changes write colors only. A border or padding added to
satisfy a color mapping would change the row height, so no border is added to rows or panes.

### Header, menu bar, and address bar colors follow the palette

The oracle menu bar, address bar, and list header are white (`#FFFFFF`), so they map to the
window color, not `COLOR_MENU`. The header background and the menu and address bar styles use the
window brush, and their text uses the window text brush.

## Overrides and environment variables

### The E2E override is compiled out of ship builds

`BWC_E2E_THEME=Light|Dark` is read only inside `#if BWC_E2E` in `BwcTheme`. A ship build contains
no reference to the variable name. The build check greps the ship assembly for the string.

Failure: the override is read in a ship build. The ship grep catches it.

### The override is not persisted here

`BwcTheme.SetOverride` changes the palette and raises `BwcTheme.Changed`. It does not write a
file. An app that does not call `SetOverride` gets System.

## Resource and startup failures

### Missing keys do not crash

If an app does not merge `Themes/Dense.xaml`, the brush lookup creates the brush and registers it
in `Application.Current.Resources`. The palette still applies. The Hub and Registry Editor both
merge `Dense.xaml`, and the E2E test uses the Registry Editor.

### The palette is applied on the UI thread

`BwcTheme.EnsureInitialized` runs from the shell constructor on the UI thread. It reads the
system colors, creates the settings objects, and captures the dispatcher queue. Calls before the
first initialization apply nothing.

## Test strategy

- `Theme_SystemPalette_FollowsWindowsAppMode` (ship and e2e flavors, read-only). It launches the
  Registry Editor with no override, selects `HKEY_CURRENT_USER\Console` with the keyboard, moves
  focus to the list with Tab, selects one value with Down, and samples a PrintWindow capture. The
  expected mode comes from `AppsUseLightTheme` in the current user hive (read only).
- `Theme_E2EOnly_LightOverride_UsesSystemColorTable` (E2EOnly, read-only). It forces Light with
  `BWC_E2E_THEME=Light`. The expected colors come from `GetSysColor` in the test process.
- `Theme_E2EOnly_DarkOverride_UsesDarkPalette` (E2EOnly, read-only). It forces Dark with
  `BWC_E2E_THEME=Dark`. The expected accent is the `AccentColor` DWM value (read only), because
  the test project does not reference the WinRT projection that the app uses.
- The light case also checks the Explorer tree colors that the test derives (drawn with
  `Explorer::TreeView` into a GDI DIB, the same method as the app) against the oracle constants in
  `docs/parity/oracle/regedit-selection-colors.json`, under `oracle.tree.matchesClassic`.
- The tree focused and unfocused fills and borders are checked on the top edge of the row, within
  the first three device pixel rows, because the border sits on the inset line.
- After Tab returns focus to the tree, the list selection must be the palette's unfocused fill
  (the window color on light, muted on dark). This is `list.unfocusedSelection`.
- Every case writes a capture per focus state and a `samples-<case>.json` file to
  `artifacts/e2e/visual/theme/`. `tests/Bwc.E2E/Theme/classic-selection-oracle.ps1` writes the
  classic captures and `compare-*.png` side-by-side images next to them.
- The dotted check counts pixels that match the focus rectangle color (the selection text color),
  not the fill. It requires 30 to 70 percent of the band to be that color, which a solid border
  line fails.

## Known gaps

- High contrast cannot be switched on the test machine without changing system settings, which
  is not allowed. The high contrast path uses the same system color reader and the
  `ActualThemeChanged` fallback, and it is not covered by E2E.
- A runtime switch of the override without restart is implemented but not covered by E2E,
  because the only E2E hook is the launch-time environment variable.
- Multi-select focus rectangle on the list (see above).
- The tree selection box is drawn over the full row width, and classic draws it around the label
  only. The fill and border colors match the oracle, the geometry does not (see the focus
  rectangle section).
- The dotted focus rectangle is drawn on the focused row, and classic draws none (see the focus
  rectangle section).
- The Explorer tree colors are read at runtime. The oracle JSON is for this OS build and this
  DPI. On another build the derived values are the source of truth, and the oracle is not
  re-checked.
- The menu bar labels are clipped at the bottom in both palettes. The clipping predates this
  package (the menu bar height comes from `DenseMetrics`) and is not changed here.

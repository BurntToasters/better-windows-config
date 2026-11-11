# Theme and palette failure modes (UF1)

Scope: `src/Bwc.UI/Theme` (`BwcTheme`, `BwcPalette`, `BwcBrushes`, system color reader),
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
| Focused selection fill | `COLOR_HIGHLIGHT` | `#0078D4` |
| Focused selection text and focus rectangle | `COLOR_HIGHLIGHTTEXT` | `#FFFFFF` |
| Unfocused selection fill | `COLOR_BTNFACE` | `#F0F0F0` |
| Unfocused selection text | `COLOR_WINDOWTEXT` | `#000000` |
| Gray text | `COLOR_GRAYTEXT` | `#6D6D6D` |
| Splitter and face | `COLOR_3DFACE` | `#F0F0F0` |
| Dividers and edges | `COLOR_3DSHADOW` | `#A0A0A0` |
| Edge highlight | `COLOR_3DHILIGHT` | `#FFFFFF` |

### Classic regedit draws a different focused selection

The oracle capture `docs/parity/oracle/regedit-100dpi.png` and a live capture of
`C:\Windows\regedit.exe` on the test machine (read-only, PrintWindow) both show the focused tree
selection as `#CCE8FF`, not `COLOR_HIGHLIGHT`. The edge of the selection was not measured. The
comctl32 visual style used by the Windows 11 tree control draws that fill. The package keeps the mapping in the
table above because the decision was taken with the user, and the E2E test asserts it. The gap is
recorded in the report and in the E2E notes so the decision can be revisited with evidence.

### Dark colors are derived, and the accent is the system accent

Dark has no system color table entry, so the palette is a fixed neutral set plus the Windows
accent (`UISettings`, `UIColorType.Accent`) for the focused selection. The text on the accent
is black when the accent is light, and white otherwise, so the selected text always has contrast.

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
- Every case writes a capture per focus state and a `samples-<case>.json` file to
  `artifacts/e2e/visual/theme/`.
- The dotted check counts pixels that are clearly brighter than the fill on three rows below the
  row top. It requires 30 to 70 percent of the row to be stroke, which a solid half-tone line fails.

## Known gaps

- High contrast cannot be switched on the test machine without changing system settings, which
  is not allowed. The high contrast path uses the same system color reader and the
  `ActualThemeChanged` fallback, and it is not covered by E2E.
- A runtime switch of the override without restart is implemented but not covered by E2E,
  because the only E2E hook is the launch-time environment variable.
- Multi-select focus rectangle on the list (see above).
- The classic focused selection color differs from `COLOR_HIGHLIGHT` (see the palette section).
  The E2E light case asserts the `COLOR_HIGHLIGHT` mapping that was decided, so it passes against
  the mapping and not against the classic capture.
- The unfocused selection color (`COLOR_BTNFACE`) was not compared with classic regedit, because
  focus could not be moved into the classic window without sending input to another window.
- The menu bar labels are clipped at the bottom in both palettes. The clipping predates this
  package (the menu bar height comes from `DenseMetrics`) and is not changed here.

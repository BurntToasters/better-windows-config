# UI shell E2E failure modes

`tests/Bwc.E2E/Shell/ShellTests.cs` drives the published Registry Editor through UI Automation
and keyboard input. It checks the window structure, the classic metrics at the current DPI, the
menu table, the address bar toggle, the splitter keys, and the Alt/F10 menu keyboard model.
The checks are recorded in `artifacts/e2e/result-<rid>-<flavor>.json`. This document lists the ways the harness
or the shell can fail and how the tests handle each one.

## 1. UIA stale elements

Every UIA element reference is a snapshot. A flyout that closes, an address bar that collapses,
or a menu rebuilt by its owner invalidates the old reference, and later property reads throw
`ElementNotAvailableException` or a COM error. Caching a `MenuItem` after opening File and then
reusing it after another menu opens is the usual cause of a flaky test.

The tests call `AppSession.Find` or search from `Window` again after every state change. Polling
helpers catch element exceptions and re-read the tree on the next attempt. A stale reference is
never treated as a failed assertion; the lookup is repeated until the timeout expires.

## 2. Foreground failure

`AppSession.SendKeys` only types when the Registry Editor main window is the foreground window.
If it cannot reach the foreground within three seconds it records a failed
`gui.foreground.bwcregedit` check and throws. It does not type into whatever window happens to
be active.

The shell tests click the window once before the first keystroke so the test follows a normal
user path. They then use `AppSession.SendKeys` for every key. If the foreground check still
fails, the test fails with the harness note instead of bypassing the check with direct
`SendInput`, a synthetic window message, or a retry loop around the keyboard.

## 3. Popup timing

`ExpandCollapsePattern.Expand()` returns before the flyout has created its UIA children. Reading
the menu immediately can see zero items, the old item set, or an intermediate state. The same
applies after `Invoke()`, which closes the flyout asynchronously, and after the address bar
toggle, which changes layout on the next frame.

The tests poll for the expected item count or state for up to five seconds. They do not treat a
single fixed sleep as proof that the menu is ready. When the expected state never arrives, the
check notes the last observed names or bounds so the failure is diagnosable.

## 4. DPI conversion

The parity oracle is captured in physical pixels at 200 percent. UIA returns physical pixels
only when the client process is DPI aware; a DPI-unaware test process sees the same window
through virtualization and reads half-size rectangles. That makes a correct 38 px menu bar look
like 19 px.

The test class sets Per-Monitor V2 awareness before the first UIA call. It reads the window DPI
with `GetDpiForWindow`, converts each expected DIP value with that scale, and allows one pixel
of rounding. At 192 DPI the expected menu bar, address bar, and menu item heights are 38 px, the
tree pane width is 432 px, the splitter width is 8 px, and the header row is 40 px.

The header row has no automation peer of its own, so its height is also measured from a screen
strip of the list pane. The scan finds the first full-width divider row and reports the row
height. This check requires the window to be foreground and is skipped with a failed check when
it is not.

## 5. Menu popup windows are not children of the main window

A `MenuBarItem` flyout is hosted in its own Win32 popup window, surfaced in this build as a
`Microsoft.UI.Content.PopupWindowSiteBridge` pane. It is not a `GW_CHILD` of the main HWND, and
`IsChild` or `GetWindow(GW_CHILD)` enumeration will not find it. UIA currently places the popup
host inside the main window's UIA tree, so searching descendants from `Window` works.

The tests search by automation id or name from the main window after each expansion. They do not
assume the flyout is a child window, do not search only the main window's immediate children,
and do not use Win32 child enumeration to decide whether a menu is open. The top-level
`MenuBarItem` ExpandCollapseState is the source of truth for whether the flyout is open.

## 6. Disabled item detection

A disabled `MenuFlyoutItem` is still present in the UIA tree and still exposes a child `Text`
element whose `IsEnabled` is true. Checking the text element reports the wrong answer. The tests
read `IsEnabled` from the `MenuItem` element itself.

Disabled items are inspected only. The tests never invoke a disabled command, because doing so
could open a file dialog, start a network prompt, or change the registry. Enabled items are
invoked only for Address Bar, Split, and the File menu Escape check.

## 7. Address bar collapse

The shell sets the address bar to `Collapsed` and gives its row zero height. In this WinUI build
the `AddressBar` element disappears from the UIA tree entirely rather than staying present with
zero height. The test accepts any of the three valid symptoms: the element is absent, its
bounding rectangle has zero height, or it is offscreen.

Reopening the bar is a separate check. The test polls until the element returns and then requires
its measured height to be 38 px at 192 DPI, within one pixel. That catches a row that keeps the
old zero height after the visibility flag flips.

## 8. Splitter focus timing

View > Split calls `BeginSplitKeyboardMode`, which focuses the splitter immediately and then
focuses it again from the dispatcher queue after the flyout closes. The flyout can take focus
back for a frame, so sending Left immediately after the invoke can move focus or drop the key.

The test polls `HasKeyboardFocus` on the `Splitter` element until it is true, then measures the
tree pane width, sends Left and Right, and verifies the 8 DIP step at the current DPI. It then
sends Enter and polls until the splitter no longer has keyboard focus. The shell element taking
focus is recorded in the note.

## 9. Test isolation

Menu mode, the address bar visibility, and the tree pane width are process-wide state in the
shell. A test that leaves the File flyout open or hides the address bar can break the next test
if they share a process, and test order is not guaranteed.

Each test opens its own Registry Editor process and disposes it in a `using` block, including
when an assertion fails. The test class keeps a list of the process ids it started so class
cleanup can verify that none survived. This costs a process launch per test, but it makes every
test independent and leaves no menu, focus, or layout state behind.

## 10. Killing only own processes

The machine may already have a Registry Editor running, and another E2E agent may start one
while the tests run. Killing every process named `BwcRegedit` would terminate work that does not
belong to this test.

The process test records `Process.GetProcessesByName("BwcRegedit")` before it launches, records
the new process id, closes the window through `AppSession.Close`, and checks that only that id
disappeared. If the process is still alive after the close timeout, the test kills that one id
only. Class cleanup does the same for the ids it tracked. No test ever kills by process name.

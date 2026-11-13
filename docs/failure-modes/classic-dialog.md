# Classic dialog failure modes

This document covers the classic Win32-style dialog system in `src/Bwc.UI/Dialogs/`:
`ClassicDialog` (owned modal window with a template-driven layout), `ClassicDialogDefinition`
and `ClassicDialogMetrics` (the DLU-to-DIP conversion), `ClassicDialogStyles.xaml` (dense control
styles), `ClassicMessageBox` (the message box used by `ConfirmDialog` and `ErrorDialog`), and the
migrated Edit String, Edit DWORD, and Edit QWORD editors. The oracle is the classic `regedit.exe`
dialog templates in `docs/parity/oracle/regedit-resources.json`. The Fluent `ModalDialogWindow`
layout that the other dialogs still use is covered by `ui-shell.md`.

The rule for this system is that a classic dialog keeps the template's client size, control
rectangles, tab order, mnemonics, and default and cancel buttons, at every DPI. Anything that
breaks one of those is a failure, even when the dialog still opens and still returns a value.

Each failure mode below names the guard that keeps it from shipping. The E2E column refers to
`tests/Bwc.E2E/Dialogs/ClassicDialogTests.cs`.

## 1. Owner window and modality

### 1.1 Owner left disabled

The owner HWND is disabled with `EnableWindow(owner, FALSE)` while the dialog is open. If the
dialog throws during construction, content loading, `Activate`, or result handling, and nothing
re-enables the owner, the main window stays dead until the process exits. The owner then looks
frozen and every later command is lost.

Guard: `ModalDialogWindow.ShowAsync` restores the owner in its `catch` path and in `OnClosed`.
`ClassicDialog` wraps its content build and its `Loaded` work so that an exception closes the
dialog through the same path. A dialog that never reaches `Closed` is a bug.
E2E: the owner is checked disabled while the dialog is open and enabled after each close.

### 1.2 Owner not re-activated

After the dialog closes, the owner must become the foreground window again. Without
`SetActiveWindow(owner)` the focus goes to the desktop or to another application, and keyboard
input stops reaching the Registry Editor. The E2E harness then fails with "foreground lost".

Guard: `RestoreOwner` calls `SetActiveWindow` after `EnableWindow`.

### 1.3 IsModal alone does not disable the owner

`OverlappedPresenter.IsModal` does not block input to the owner window in WinUI 3. Only
`EnableWindow(owner, FALSE)` does. A classic dialog that relies on `IsModal` lets the tree accept
clicks and lets a second dialog open behind the first.

Guard: the owner is disabled explicitly, as in `ModalDialogWindow`.

### 1.4 Owner handle is zero or stale

`ShowAsync` with a zero owner HWND has no window to center on and no owner to disable. A stale
HWND after the main window is destroyed makes `EnableWindow` a silent no-op.

Guard: a zero owner throws `ArgumentException` before anything is shown. The owner is
re-enabled only when it was disabled by this dialog.

### 1.5 Second modal while one is open

A second `ShowAsync` on the same instance must return the first task, not open a second window. A
second dialog opened by the caller while the first is open would disable an already disabled
owner and then re-enable it early when either closes.

Guard: `ShowAsync` is idempotent per instance. `RegeditRootView` refuses a dialog request while
`_dialogOpen` is set.

### 1.6 Owner re-enabled only after the dialog is hidden

`ModalDialogWindow` re-enabled and re-activated the owner in `OnClosed`, which runs after the
dialog window is already hidden and destroyed. For that gap the owner is still disabled, so the
system picks another window to activate. In the Registry Editor run it was the XAML input site
window (`InputSiteWindowClass`, empty title, size 0x0), not the main window. The foreground window
is then not the app's input target, and every keyboard and mouse event after the dialog goes
nowhere. The cascade looks like unrelated bugs: Alt+F does not open File, Enter does not dismiss
the next message, double-click does not open a value, context menus and Find dialogs do not
respond, and the tree does not take focus.

Classic Win32 dialogs do not have this gap. `EndDialog` re-enables the owner before the dialog is
hidden, so the owner is the active window when the dialog goes away.

Guard: `ModalDialogWindow` restores the owner (`EnableWindow(owner, TRUE)`, then
`SetActiveWindow(owner)`) from `AppWindow.Closing`, which runs before the dialog is hidden. That
covers every close path: OK, Cancel, Escape, Enter, the title-bar X, Alt+F4, and a `Close()` from
a subclass or from a progress dialog. `OnClosed` repeats the restore, and the restore is
idempotent and exception-safe, so a close that skipped `Closing` still gives the owner back.
E2E: after each dialog closes, the foreground window is the main window ('Registry Editor'), and
the next keyboard command reaches it (`ImportExportTests`, `gui.foreground.*`).

### 1.7 Keyboard focus not inside the dialog after activation

`Activate` makes the dialog the foreground window, but the XAML focus inside it is not always on a
control. The load-time focus (the default button, or the edit box of a value editor) can be set
before the window is active, and activation then leaves no element focused. Enter and Escape are
delivered to the focused element and bubble to `DialogRoot`, so with no focus inside the dialog they
never reach `OnRootKeyDown`. The dialog then looks open and active but ignores the keyboard. The
message box stayed open after Enter in the Registry Editor run.

Guard: `ModalDialogWindow` raises `OnDialogActivated` on each activation that is not a deactivation.
`ClassicDialog` answers it by putting focus back on the control that had it after load, or on the
first tab stop, when no element inside the dialog has focus. Before the content is loaded the XAML
root does not exist yet, so the activation is skipped and the Loaded handler restores focus once the
window is active. `FocusManager.GetFocusedElement` throws for a null XAML root, so every read checks
it first.
E2E: after the import result message appears, Enter must close it (`dialog.message.dismissed`). The
last run still failed this check: the restored focus is inside the dialog, but Enter never reaches
`DialogRoot`, so the input is lost below the XAML focus, which is still open.

### 1.8 Enter swallowed by a focused button

A focused button marks the Enter key handled on KeyDown without raising Click, so a root `KeyDown` handler
never sees Enter, and the dialog ignores it. The import result message stayed open for that reason.

Guard: the dialog root registers its key handler with `handledEventsToo`. A handled key is ignored, except
Enter while a button has focus, which is answered by the dialog's default or focused button. Escape keeps
the rule that a handled key is ignored. The key-down path for a multi-line edit still lets Enter insert a
newline.
E2E: after the import result message appears, Enter closes it (`dialog.message.dismissed`). Not yet
confirmed in a passing run; the import step failed at the file dialog Save click in the last runs.

### 1.9 A close request re-enables the owner while the window stays open

`Closing` can be canceled. A window that refuses a close (the import progress window while the import
runs) must keep its owner disabled. `ModalDialogWindow` re-enabled the owner in its `Closing` handler
before it checked whether the close was canceled, so Alt+F4 or a title-bar close re-opened the main
window while the progress window still ran, and the user could start a second command during an import.

Guard: `ModalDialogWindow` asks `IsCloseAllowed()` in `Closing`. A refused close sets `Cancel` and
returns with the owner still disabled. The owner is restored only when the close goes ahead. A
programmatic `Close()` does not raise `Closing`, so a dialog that finishes its own work still closes.
E2E: `importprogress.altf4` sends Alt+F4 during the import, then checks the window is still open and the
main window is still disabled (`importprogress.main-disabled`).

### 1.10 Yes clicked before the message box is under the pointer (E2E)

A press sent to a message box that has just appeared can miss it. The window at the pointer is then the
owner or another window, the box does not answer, and the harness had no way to tell that the click never
arrived. One import test failed once with "the Yes button did not close the dialog after 1 attempt(s)".

Guard (E2E harness, `AppServicesSupport.ClickButton`): the box is the foreground window, the button is
enabled, on screen, and has the same bounds on two reads 150 ms apart, and the window at the pointer
resolves to the box (`WindowFromPoint` and its root) before the press. A press is never sent to a point
that does not resolve to the box, so a missed press is a wait, not a second click. A note records the
state at each attempt that does not close the box.
E2E: `ImportResult_MalformedLineIsSkippedWithoutStringThirtyTwo` and the other import result tests.

## 2. DPI and geometry

### 2.1 DLU converted at system DPI instead of window DPI

Dialog units scale with the font's base units, and the base units depend on the DPI the font was
created at. Converting with the system DPI on a monitor at a different scale puts every control in
the wrong place and changes the client size.

Guard: `ClassicDialogMetrics` computes the base units for the dialog window's own DPI, read with
`GetDpiForWindow` after the window exists and before it is sized.

### 2.2 Base units rounded from the wrong metric

The base units are the font's average character width (from the 52 letters, as in `MapDialogRect`)
and the font's height. Using `tmAveCharWidth` or the line spacing instead gives a different width
for the 50-DLU push buttons. A button then renders at 74 or 77 px instead of 75 px at 96 DPI.

Guard: the E2E check compares every control rectangle with `MapDialogRect` results measured on a
hidden dialog built from the same template, within 1 DIP.

### 2.3 Client size confused with outer size

`AppWindow.Resize` takes the outer size, which includes the caption and frame. Passing the DLU
client size there leaves the client area short by the caption height, and the bottom buttons
clip.

Guard: the client area is sized with `AppWindow.ResizeClient`, and the E2E check reads the
client rectangle of the dialog.

### 2.4 Font substitution changes the metrics

`MS Shell Dlg` is substituted by `FontSubstitutes`, on this machine to Microsoft Sans Serif. If the
metrics use the name and the text uses a different face, the controls are sized for one font and
the text is drawn in another. Labels then clip or leave large gaps.

Guard: the metrics and the XAML text use the face resolved from the template font name
(`Microsoft Sans Serif` on this machine), and the size is the template's 8 pt.

### 2.5 Control rectangle off by a fraction of a DIP

WinUI lays out in DIPs. Rounding each control to whole pixels separately can leave adjacent
controls one pixel apart, or overlapping. At 150 % scale a 1-DLU offset is not a whole number of
DIPs.

Guard: `UseLayoutRounding` is on for the dialog root, and positions are computed in pixels and
divided by the scale once.

### 2.6 Text clipped by a fixed DLU width

Static text whose rectangle is narrower than the text, or a label wider than its DLU width, is
clipped. The classic dialogs do not wrap their labels, so the template width is the limit.

Guard: static text uses `NoWrap` and is measured in the E2E check against the template. A label
that does not fit is a template transcription bug, not a runtime fallback.

### 2.7 Native dialog template sized at the wrong DPI

A dialog built from an in-memory `DLGTEMPLATEEX` (the native import progress window, 4.7) takes its
DLU scale from the dialog font, not from BWC's `ClassicDialogMetrics`. The system can convert at a DPI
other than the owner's, which changes the client size.

| Way it could fail | Expected behavior |
| --- | --- |
| Client size uses the system DPI on a window at another DPI | DLG-019 client is 240 x 59 DLU at the window DPI, within 1 px per edge (`importprogress.size`) |
| Font face is not the template face | Template face `MS Shell Dlg` 8 pt, as RT_DIALOG 110 declares, resolved through FontSubstitutes |

## 3. Focus, tab order, and keyboard

### 3.1 Tab order differs from template order

Tab order equals the template control order, skipping controls without `WS_TABSTOP`. WinUI's
default focus navigation follows the visual tree and the layout, which can differ, and it puts
static labels in the sequence. The fix is explicit tab handling on the dialog root.

Guard: the root handles Tab and Shift+Tab, moves focus through the tab-stop list built from the
definition, and marks labels and group boxes as not tab stops.

### 3.2 Focus lost after the dialog closes

When the dialog closes, keyboard focus must return to the main window's last focused element.
Without that, the next key press goes to nothing. Closing a dialog that did not take focus can
also leave the main window focused but its tree control unfocused.

Guard: the owner is re-activated, and the E2E check sends a key to the main window after each
dialog closes.

### 3.3 Initial focus on the wrong control

Win32 gives focus to the first tab stop of the template, which for Edit String is the value data
edit. A read-only name edit that gets focus first is a bug: typing does nothing.

Guard: the first focusable tab stop is the first template tab stop with `WS_TABSTOP` and no
read-only style. Message boxes focus the default button.

### 3.4 Read-only edit selected or typed into

The value name edit in Edit String, Edit DWORD, and Edit QWORD is read-only. A read-only edit that
accepts input would let the user change the name shown, which the save path ignores.

Guard: the edit is `IsReadOnly`, and the E2E check types into the data edit only.

### 3.5 Mnemonic collisions

Two controls with the same `&` letter in one dialog make Alt+letter ambiguous. The second press
then reaches the first control again and the second is unreachable by keyboard.

Guard: `ClassicDialogDefinition` validates mnemonics when the dialog is built and throws a
`InvalidOperationException` naming both controls. The migrated dialogs have unique mnemonics.

### 3.6 Alt+mnemonic not routed

WinUI does not process `&` access keys. Without the root handler, Alt+letter does nothing, or it
reaches the main window's menu through the owner.

Guard: the root handles Alt+letter. A static label routes to the next tab stop after the label
(the Win32 `WM_SYSCHAR` behavior). A button is invoked, a check box or radio button is selected
and focused. The E2E check sends Alt+V, Alt+H, and Alt+D and checks the focused element.

### 3.7 Alt cue left on

The underline cue for mnemonics is shown while Alt is held. If the Alt key-up is lost, for
example when focus moves to another window, the cue stays on.

Guard: the cue is cleared on key-up, on focus loss, and on dialog close.

### 3.8 Enter and Escape

Enter must invoke the default button, unless focus is on another button, in which case that button
is invoked. Enter inside a multi-line edit inserts a newline. Escape must invoke the cancel button
(identifier 2). A message box with OK only treats Escape as OK.

Double activation is a failure: a default button invoked by Enter and then again by a key repeat
or a second Enter. The dialog accepts once, and `Accept` is ignored after the first success.

Guard: `Accept` and `Cancel` are idempotent and check `_accepted` and the closing state. The
E2E check presses Enter and Escape and checks the registry value only changed on Enter.

### 3.9 Default button absent or wrong

The default button must be the template's `BS_DEFPUSHBUTTON` (OK in every migrated dialog). A
dialog with no default button, or with Cancel marked default, would let Enter cancel the dialog.

Guard: the default button comes from the definition. The E2E check tests Enter by its effect
(the value is saved once), not by the button's visual style.

## 4. Placement and sizing

### 4.1 Centered outside the work area

Centering on the owner can put the dialog partly off screen when the owner is near an edge, or
when the owner sits on a secondary monitor with a different work area.

Guard: the position is clamped to the work area of the owner's display (`DisplayArea.WorkArea`).

### 4.2 Owner minimized or zero-sized

An owner with a zero-size rectangle or a minimized state gives a meaningless center. The dialog
then opens at (0, 0) or behind the taskbar.

Guard: a zero-size owner falls back to the work area center.

### 4.3 Message box width not tied to text

A message box with a fixed width clips long messages. A width computed from the longest unbroken
word can exceed the work area. Win32 MessageBox wraps text and widens up to a fraction of the
screen, then wraps again.

Guard: the message text wraps at a maximum width of 3/4 of the work area. The window size is the
measured content size, clamped to the work area.

### 4.4 Buttons narrower than their labels

A push button whose template width is smaller than its label clips the label. A label such as
"Cancel" in a 50-DLU button fits at 8 pt. A longer localized label does not.

Guard: a message box grows its button row to fit the labels, and the template buttons keep their
template width. A localized label that does not fit is a known gap for the locale work.

### 4.5 Heading text leaking into a classic dialog

The Fluent layout shows a bold heading above the body. The classic layout must not. A heading that
appears above the template controls moves them down and breaks every rectangle check.

Guard: classic dialogs do not use the Fluent `DialogHeading` element. The E2E check reads
`DialogHeading` and expects no text.

### 4.6 Title-bar icon shown

The generic exe icon in the title bar is not in the classic frame.

Guard: `WS_EX_DLGMODALFRAME` is set on the dialog window, which removes the icon from the
caption. The window keeps its system menu.

### 4.7 Import progress caption keeps the system menu and Close box

Classic DLG-019 (the import progress window) has style `0x90C001C4`: `WS_CAPTION` and no `WS_SYSMENU`, so its
caption has no Close box. A WinUI window cannot keep that style: WinUI restores `WS_SYSMENU` in its own window
procedure after an outer subclass clears it (measured in the E2E build, UF7 before this change). The WinUI
version showed a Close box that was refused until the import ended.

Guard: DLG-019 is a native Win32 dialog. `CreateDialogIndirectParamW` builds it from an in-memory
`DLGTEMPLATEEX` with the classic style, extended style, and font, so no WinUI window procedure touches the style.
The dialog manager adds `WS_CLIPSIBLINGS` (0x04000000), so the live `GWL_STYLE` is `0x94C001C4`; the check
requires `WS_SYSMENU` absent and `WS_CAPTION` present, the bits the template defines.
The dialog procedure also ignores `WM_CLOSE` and `SC_CLOSE` (Alt+F4 and the system menu) until the host
destroys the window, and the window has no Cancel button. The host restores the owner before it destroys
the window.

E2E: `importprogress.style` (no `WS_SYSMENU`, `WS_CAPTION` present), `importprogress.no-close-button` (no Close
button in UI Automation), `importprogress.class` (window class `#32770`), and `importprogress.altf4` (the window
stays open and the owner stays disabled after Alt+F4).

### 4.8 Native progress window ignores the BWC dark palette

Guard: `NativeProgressDialog` reads `BwcTheme.Current` when it is shown and whenever the palette changes.

| Way it could fail | Expected behavior |
| --- | --- |
| Dark caption stays light | `DWMWA_USE_IMMERSIVE_DARK_MODE` is set in `WM_INITDIALOG`, before the template makes the window visible |
| Dark client or static text stays white or black | `WM_CTLCOLORDLG` and `WM_CTLCOLORSTATIC` return a brush from `BwcPalette.Face` and set `WindowText` as the text color; light and high contrast keep the system colors |
| Progress bar stays light in dark mode | `SetWindowTheme(bar, "DarkMode_Explorer")` is applied; a failure falls back silently |
| Brushes leak | The brush for the previous palette is deleted on a palette change, and both are deleted on destroy |
| Palette changes while the window is open | `BwcTheme.Changed` re-applies the caption attribute and brushes and repaints |
| High contrast shows BWC colors | High contrast uses the default dialog colors only |
| Dark client stays the system face | The default erase paints the system face, so `WM_ERASEBKGND` fills the client with the dark brush and returns TRUE |
| Dark static text stays black on the system face | `WM_CTLCOLORSTATIC` returns the dark brush itself and also stores it in `DWLP_MSGRESULT`; the text color is set on the DC |
| Dark progress track stays light | `CCM_SETBKCOLOR` sets the track to the dark face; light mode resets it with `CLR_DEFAULT` |
| E2E samples the face while the window is still animating | The E2E check reads the bottom-margin samples until they settle on the face (5 s), so a blend with the desktop does not fail or pass it |

## 5. Text and values

### 5.1 Hexadecimal and decimal conversion

Changing the base must convert the shown value, not only the parser. Switching Hexadecimal to
Decimal on 0x1388 must show 5000. A conversion that keeps the old text leaves the box showing
hex under the Decimal button, and a save then parses the wrong number.

Guard: the dialog keeps the parsed value. Each base change re-renders the text from the value,
with hexadecimal shown without a prefix, as classic regedit does.

### 5.2 Input that does not parse

A value that does not parse in the current base must keep the dialog open and report the error.
Typing `1G` in hexadecimal mode, or `4294967296` in a DWORD box, must not close the dialog or save
a truncated value.

Guard: the parse fails before `Value` is set. The error is shown in an error message box owned by
the dialog. The dialog stays open with the typed text.

### 5.3 Value overflow in a base conversion

A QWORD near 2^64 switched to Decimal must not overflow the conversion. A DWORD above 2^32 typed
in hexadecimal must not wrap.

Guard: the parser checks the range for the value width in the current base.

### 5.4 Multi-line editor losing newlines

The expandable string editor is multi-line. Enter must insert a newline, not accept the dialog,
and the saved text must keep its newlines.

Guard: `AcceptsReturn` is on for the multi-line editor, and the default-button Enter path is
skipped when the focused element is that editor.

### 5.5 Automation IDs lost

Callers and E2E checks depend on `DialogRoot`, `DialogPrimary`, `DialogSecondary`,
`DialogMessage`, `DialogErrorText`, and `ValueEditorInput`. A classic dialog that drops one of
these breaks the existing suites even when the dialog works.

Guard: the migrated dialogs keep those IDs. `ErrorDialog` text keeps `DialogErrorText`.

## 6. Styles and resources

### 6.1 Styles file not loaded

If `ClassicDialogStyles.xaml` is not merged into the dialog, the controls use the Fluent defaults
and the layout is correct but the density is wrong.

Guard: the dialog loads the dictionary itself. The E2E check reads the TextBox height.

### 6.2 Missing theme key

A `ThemeResource` that does not exist fails XAML parsing at load time, which throws from the
constructor. The dialog never opens and the owner is never disabled, so the failure is contained.

Guard: every theme key in the styles file is a key the Fluent theme defines. The constructor
catches load failures and reports them through the log instead of leaving the owner disabled.

### 6.3 Face and text not painted

The dialog root is a `ContentControl`, and its face came from the `Background` in
`BwcClassicRootStyle`. That background is set on the root but is not painted, so the window
backing shows through. On this stack the backing is black. The static text uses the palette text
color, which is black on the light palette, so the Hub's About and update dialogs showed their
buttons and nothing else. The Registry Editor dialogs looked right only because their palette
text is white, which is readable on the same black. UI Automation reads each control's text, not
its pixels, so every text check passed while the text was invisible.

Guard: the dialog paints the palette face itself, on the template canvas or on the wrapper grid that
holds a flow layout. Static text, check and radio labels, and the group caption take `BwcBrushes`
(the palette brushes), and the root's element theme follows the palette so the Fluent buttons match
it. The palette brushes are live, so a theme change repaints the dialog. The E2E check
`DialogContrast` captures the client area and fails when the most common color is not the palette
face within 6 per channel, or when a static text control has fewer than 12 pixels whose luminance
differs from its background by more than 40 percent of full scale.

## 7. Message box

### 7.1 Wrong default or cancel for a button set

Yes/No/Cancel with Yes as default, OK/Cancel with OK as default, and OK-only with OK as both
default and cancel are the Win32 sets. A message box with the default and cancel reversed sends
Enter to the destructive choice.

Guard: `ClassicMessageBox` builds each set from one table, and the E2E check covers the Yes/No
set used by the delete confirmation.

### 7.2 Icon not the stock icon

The icons are the system stock icons. A bitmap copied from the app icon is the wrong icon and
the wrong size at high DPI.

Guard: the icons come from `SystemIcons` equivalents (`Information`, `Warning`, `Error`,
`Question`) at 32 DIP, scaled by the window DPI.

### 7.3 Error details not selectable

`ErrorDialog` shows the operation, path, and Win32 message in one block. Users copy that text to
report a failure. A non-selectable block loses it.

Guard: the message text is selectable. `DialogErrorText` names it.

## 8. Re-entrancy and exceptions

### 8.1 Exception in OnDialogLoaded

`Loaded` runs after the window is visible. An exception there is an unhandled event exception,
which tears down the message loop.

Guard: `Loaded` work is wrapped. A failure closes the dialog with a false result and restores the
owner.

### 8.2 Exception in TryAccept

`TryAccept` runs on OK. A parse or validation exception must not close the dialog with a
half-set `Value`.

Guard: `Value` is assigned only after the parse succeeds. An exception keeps the dialog open and
logs the error.

### 8.3 Dialog opened from a closing dialog

A button handler that opens another dialog while the first is closing can disable the owner in
the wrong order.

Guard: callers open follow-up dialogs after `ShowAsync` returns, which the Registry Editor already
does through `await`.

## 9. Classic value editors (RF5)

These are the editors migrated to the classic template in this batch: Edit Multi-String
(RT_DIALOG 114), Edit Binary Value (RT_DIALOG 103, with the HEX control), Find (RT_DIALOG 106),
Remove Favorites (RT_DIALOG 113), and the Load Hive style key-name prompt (RT_DIALOG 104) that
`TextPromptDialog` now uses. The Binary Data viewer (RT_DIALOG 2021) is on the classic template: a
read-only list of 16 bytes per row, the Byte, Word, Dword, and Qword formats, and the Value Type field.
The 2021 template carries no value-name control, so the value name is the list's accessible name.
Its OK button text is the caller's; the classic button is OK, so the caller must pass OK.

### 9.1 Empty strings saved without a warning

Classic Registry Editor does not save a multi-string with empty strings silently. OK with one or
more empty lines shows the message box with string 116 (one empty string) or 117 (more than one),
with the Warning icon and OK. The text is the oracle text, and the box is owned by the Multi-String
dialog, so the owner stays disabled until the box closes. Guard: `MultiStringValueDialog.TryAccept`
never returns the list with empty entries before the warning has been answered.

### 9.2 Empty strings kept after OK on the warning

After OK on the warning the empty strings are removed and the dialog closes with the cleaned list.
A stale result that still holds an empty entry would write it to the registry. Guard: the cleaned
list is the value, and `Value` is the only result read by the caller.

### 9.3 Warning answered by Escape keeps the empty strings

The classic warning is a message box with OK only (MB_OK), so Escape answers it the same way as OK:
the empty strings are removed and the dialog closes. There is no Cancel path to test. Guard: the
warning is shown with `ClassicMessageButtons.Ok`, and `MultiStringValueDialog` removes the empty
strings only after the warning returns. Test: `valueeditors.multi.cleanup`.

### 9.4 Enter in the Multi-String edit presses OK

The Multi-String edit has ES_WANTRETURN, so Enter inserts a line break. Guard: `FocusedIsMultilineEdit`
in `ClassicDialog`. Enter only presses OK when the focus is on a button or outside the edit.

### 9.5 Binary editor hex control trapping Tab

The HEX control is a tab stop with a Tab key handler of its own. Tab must leave it for Cancel and
the value name is read-only and skipped by typing. Guard: the classic root key handler handles Tab
before the control. Test: Edit Binary tab order.

### 9.6 Binary editor value name edited

The value name is read-only (ES_READONLY), so typing in it changes nothing and it is not an edit
the caller reads back. Guard: `ReadOnly` on the template edit. Test: Edit Binary read-only name.

### 9.7 Find layout drifts from the template

Find uses RT_DIALOG 106 geometry. The control set is the classic one: Find what, Look at (Keys,
Values, Data), Match whole string only, Search in subtree, Find Next, and Cancel. The oracle
measures Search in subtree as visible, so it stays; the request's option list did not include it.
Guard: each control rectangle is compared with the oracle rectangle within 1 DIP. Test: Find layout.

### 9.12 Find dialog stays open during a search (known gap)

Classic closes the Find dialog when the search starts and shows "Searching the registry..." (RT_DIALOG
109) with Cancel. The host (`FindController`, not owned by this batch) keeps the Find dialog open and
reports progress in its status line. Closing on start needs the host to show the progress and
finished messages, so it is not claimed here.

### 9.8 Find text and scope lost on reopen

The Find options are saved in the settings when the dialog closes, including an unsubmitted edit.
Guard: `FindDialog.Options` is read in the host's `finally`, unchanged by the layout migration.
Test: Find state survives reopen.

### 9.9 Binary Data viewer still a hex control

Classic Binary Data (RT_DIALOG 2020 and 2021) is a listbox with Byte, Word, Dword, and Qword format
radios. The current viewer is the hex control and keeps that layout until the listbox is migrated.
The gap is reported as a known difference, not verified as parity.

### 9.10 Remove Favorites list not sorted or not multi-select

The list is a multi-select listbox (LBS_MULTIPLESEL with LBS_SORT, style 0x50A1010B). Guard:
`ClassicControlDefinition.MultiSelect` and the sort in `RemoveFavoritesDialog`. OK with no selection
shows the message and keeps the dialog open. Test: Remove Favorites multi-select.

### 9.11 Prompt geometry differs from Load Hive

`TextPromptDialog` uses the Load Hive key-name template (RT_DIALOG 104) for every caller. The
Add to Favorites and new-key prompts have their own templates in classic; those differences are
reported, not claimed. Guard: rectangles are compared with RT_DIALOG 104 only.

## 10. Test coverage

| Area | Guard | E2E check |
| --- | --- | --- |
| Owner disabled and re-enabled | 1.1, 1.2 | Owner `IsEnabled` during and after |
| Refused close keeps the owner disabled | 1.9 | `importprogress.altf4`, `importprogress.main-disabled` |
| Yes pressed before the box is ready | 1.10 | `ImportResult_*` (harness `ClickButton`, no press to a point that is not the button) |
| Caption keeps the system menu and Close box on DLG-019 | 4.7 | `importprogress.style`, `importprogress.no-close-button`, `importprogress.class`, `importprogress.altf4` |
| DLG-019 client size or font off at the window DPI | 2.7 | `importprogress.size` |
| DLG-019 dark caption, face, or text off the BWC palette | 4.8 | `importprogress.dark.caption`, `importprogress.dark.face` |
| Client size and positions | 2.1, 2.2, 2.3, 2.5 | Rectangles within 1 DIP of `MapDialogRect` |
| No heading | 4.5 | `DialogHeading` empty |
| Tab order | 3.1 | Tab sequence matches template |
| Alt mnemonics | 3.6 | Alt+V, Alt+H, Alt+D move focus |
| Enter and Escape | 3.8 | Enter saves once, Escape cancels |
| Base conversion | 5.1 | 0x1388 shows 5000 in Decimal |
| Face and text readable | 6.3 | Client face is the palette face; each static text has 12+ strong pixels (`DialogContrast`) |
| Multi-string warning and cleanup | 9.1, 9.2, 9.3 | `valueeditors.multi.warning`, `valueeditors.multi.cleanup` |
| Multi-string geometry and tab order | 2.5, 3.1 | `valueeditors.multi.geometry`, `valueeditors.multi.focus-and-tab-order` |
| Edit Binary geometry and read-only name | 2.5, 3.4, 9.6 | `valueeditors.binary.geometry`, `valueeditors.binary.value-name` |
| Eight bytes per HEX row | 9.5 (layout) | `valueeditors.binary.eight-bytes` |
| Find layout | 9.7 | Not yet a geometry check (open item) |
| Screenshots | all | `artifacts/e2e/visual/dialogs/*.png` |

Edge cases with no E2E check yet: 100 % and 150 % scale (the E2E machine runs at 200 %), monitors
with different DPI, localized labels wider than the template, and a message box wider than the
work area. These are listed in the report, not claimed as covered.

## Parity package: Import progress and Disconnect

| Way it could fail | Expected behavior |
| --- | --- |
| Import progress is 280 x 82 DLU with a Cancel button | DLG-019: 240 x 59 DLU, `File:` at (10, 10), file name at (10, 22) 220 x 8, bar at (10, 40) 220 x 9, no Cancel |
| Disconnect is a 470 x 360 px window | DLG-013 template, 250 x 111 DLU |
| Disconnect text contrast is wrong | Labels and list use the palette text and face colors (`DialogContrast`) |

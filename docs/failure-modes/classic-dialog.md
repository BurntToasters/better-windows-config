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

Failure mode (0.2.0 audit): only the button case checked `IsEnabled`, so Alt+letter changed a disabled
check box or radio button. In the policy setting dialog of a read-only store Alt+E, Alt+D, or Alt+C
changed the state and marked the dialog dirty; File > Options and View > Customize View toggled their
disabled check boxes. Guard: a disabled button, check box, or radio button takes no access key at all
(no focus, no change). E2E: `Gpedit_Dialog_ReadOnly` sends Alt+D to the disabled radio buttons and checks
that the state, the Apply button, and the list are unchanged.

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

### 3.10 Nested dialog closes and keys go to the main window

A child dialog (Resources from Resource Lists, a detail viewer from Requirements List, or Binary Data from Resources)
is opened by a dialog, not by the main window. When the child closes, keyboard focus must return to the element that
had it in the parent dialog. The symptom was: after a child closes, Escape does not close the owner dialog.

Status: fixed (X1). Root cause: the owner did not handle Escape at all, with or without a child. Escape reached the
owner's dialog root, which called `ClassicDialog.InvokeEscapeButton`. That method looks for a Cancel button (control id 2,
IDCANCEL) and then for a default button. The Resource Lists (RT_DIALOG 116) and Requirements List (RT_DIALOG 1501)
templates have only OK, and it is not the default, so both lookups failed and Escape did nothing. Classic regedit ends a
dialog on IDCANCEL (DefDlgProc), so Escape closes these viewers. The earlier focus evidence was accurate (the owner was
active and focused, and the key was delivered), but the key had no handler. The nested child only made it visible.

Fix: `InvokeEscapeButton` cancels the dialog when it has a button but neither a Cancel nor a default button. Dialogs
with a default button (the OK-only message boxes) still treat Escape as OK. Dialogs with no buttons (progress) are
unchanged. The owner's window is re-enabled in `ModalDialogWindow.Close` before the child is destroyed, which was
checked and is not the cause: with the handler in place, the owner receives Escape after the child closes.

Guard: the ResourceViewers test presses Escape on the Resource Lists viewer with no child open
(`resources.list.escape-without-child`), on the Resource Lists viewer after its Resources child closes
(`resources.list.escape-after-child`), and on the Requirements List viewer after its Memory detail closes
(`resources.requirements.escape-after-child`). The nested owners are no longer closed with OK. The pre-fix build was
not re-run in this session; the cause above comes from the template and the code path, and the post-fix run passes.

### 3.11 Enter from the press that opened the dialog, or auto-repeat

A press of Enter on a row opens a dialog. The dialog activates while the opening key may still be held, and
the key-down that the auto-repeat engine sends for a held key reaches the new dialog with the repeat flag set.
If the dialog treats that as a press, it accepts at once and closes before the user sees it. The same applies
to Escape. A key-down that the dialog itself receives without the repeat flag is a press of its own.

Guard: `ModalDialogWindow` ignores Enter and Escape key-downs whose `KeyStatus.WasKeyDown` is set, so auto-repeat
and a key that was already down when the dialog opened never act. The E2E check is
`ValueEnter_TwoFastPressesOpenOneEditor`, which presses Enter twice with no pause and expects one editor.

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


## 11. Repeated Modify and abandoned show (fix 6)

- A second Enter or double-click while the first Modify is still reading is ignored
  (`ValueCommands.ModifyValueAsync` guard), so no second editor is built.
- A dialog whose `ShowAsync` fails never raises Closed. `ModalDialogWindow` calls `OnShowAbandoned`, and
  `ClassicDialog` drops its `BwcTheme.Changed` subscription there.
- Window setup exceptions are caught (`ModalDialogWindow.ShowAsync` and `ClassicDialog.OnRootLoaded`) and each
  catch calls `UiDiagnostics.Warn`. Bwc.UI does not reference Bwc.Core. `src/Bwc.UI/UiDiagnostics.cs` holds the
  hook, and both app startups set it to `Log.Warn`. A null hook drops the message. The catches are not narrowed
  to typed exceptions any more, so an unexpected exception still gives the owner back.
- **E2E status.** `tests/Bwc.E2E/Dialogs/ValueEnterTests.cs` `ValueEnter_TwoFastPressesOpenOneEditor` presses Enter
  twice with no pause on a string value. It counts the visible "Edit String" windows of the app process (at most
  one), cancels the editor, and checks the owner is enabled and foreground. The log hook has no E2E trigger: a
  failed dialog show cannot be forced with real input, so it is checked by build and by the no-Bwc.Core rule only.

## 12. Small dialogs: Add to Favorites, Remove Favorites, Find (K1b)

### 12.1 Dialog rectangles drift from the classic measurement (VIEW-019)

Failure mode: a control rectangle in Add to Favorites, Remove Favorites, or Find differs from the
measured classic rectangle in `docs/parity/oracle/regedit-dialog-rects.json`. The expected value is the
measured device pixel rectangle at 192 DPI, scaled by the BWC window DPI divided by 192, with a
tolerance of 4 device pixels at 192 DPI per edge, scaled the same way. The fix is in the layout, never
in the tolerance. A mismatch that cannot be fixed is written to the result artifact as a known gap.
Test: `SmallDialogs_RectsMatchClassicMeasurements`.

### 12.2 Dialog units converted with the wrong base units

Failure mode: the DLU of a template is converted with a base unit measured at 96 DPI while the window
is at 192 DPI, so every rectangle is half the classic size. The measured base units at 192 DPI are
3 device pixels per horizontal DLU and 25/8 per vertical DLU for MS Shell Dlg 8 pt, which is the
conversion `ClassicDialogMetrics` already uses.

### 12.3 Binary editor caption from an unused resource (DLG-005)

Failure mode: the resource passed as the Edit Binary title (`DialogHexEditTitle`) reads "Edit Binary
Data" while the dialog shows "Edit Binary Value". The resource value must match the classic caption,
"Edit Binary Value". The existing caption check in `ClassicValueEditorTests` covers the shown text.

### 12.4 Find Next enabled while the search text is empty

Failure mode: Find Next (id 1) is enabled while Find what is empty. Classic disables it (oracle "find"
capture: control 1 enabled=false with no text typed). BWC disables the button while the text is empty and
enables it as the text changes; Alt+N does nothing while it is disabled. Enter and F3 with an empty text
still show the empty-text message. Test: `SmallDialogs_RectsMatchClassicMeasurements` checks the state
at open and the live change (`rects.find.next-enabled`, `rects.find.next-live`).

## 13. Decimal overflow prompt and the QWORD editor (F2a)

Classic regedit does not refuse a decimal number above the width's maximum outright. It asks
"Should the value be truncated in order to continue?" (RT_STRING 119 for DWORD, 120 for QWORD).
The oracle dump records the text and not the buttons, the caption, or the truncation rule, so the
spec is the text: Yes accepts the largest value of the width, and No returns to the editor with the
typed text kept. The caption is RT_STRING 110 (Overflow). RT_STRING 111 (Warning) is not used, see
MSG-053 in `regedit-audit.md`. The prompt is a Yes/No message box owned by the editor. Hexadecimal input
above the width keeps the input error, because the classic prompt text names only decimal input.

### 13.1 Decimal overflow shows the input error instead of the prompt

Failure mode: typing 4294967296 in Decimal and pressing OK shows "Enter a decimal number from 0 to
4294967295." Classic asks the truncation question instead. Guard: `ClassicNumberDialog.TryAccept` checks
an all-digit decimal above the width before it reports the range error. E2E: `NumberOverflow_DwordDecimalAnswers` (Dialogs/NumberOverflowTests.cs).

### 13.2 Prompt text is not the classic text

Failure mode: the prompt is reworded, loses the CRLF between its two sentences, or uses a string that is
not string 119 or 120. Guard: the prompt text is the Resources.resw entry whose comment cites the oracle id,
and `scripts/check-regedit-strings.js` reports it equal. E2E: the message box text is compared with the
oracle string, with line ends normalized to LF on both sides.

### 13.3 Prompt caption is the editor's title

Failure mode: the prompt is captioned "Edit DWORD (32-bit) Value" or "Edit QWORD (64-bit) Value", or the
caption is the application name. Guard: the caption is RT_STRING 110, `Overflow`. E2E: the prompt window
caption is checked on each overflow.

### 13.4 No changes the value or closes the editor

Failure mode: No stores the truncated value, closes the editor, or writes anything to the registry. Guard:
`TryAccept` returns false for No, so nothing is accepted and the editor stays open with the typed text. E2E:
the typed text is still shown after No, and the stored value is unchanged.

### 13.5 Yes stores a value other than the largest value of the width

Failure mode: Yes stores the typed number wrapped to the width (for example 4294967296 as 0), stores zero, or
stores the input with its high bits dropped. Guard: Yes sets the accepted value to `uint.MaxValue` or
`ulong.MaxValue`. The rule is the BWC choice for "truncated" (the dump has no algorithm), and it is written in
`classic-dialog.md` so a change is visible. E2E: the stored DWORD is 4294967295 and the stored QWORD is
18446744073709551615.

### 13.6 Hexadecimal overflow shows the prompt

Failure mode: a hexadecimal value above the width (for example 100000000 in a DWORD) asks the truncation
question. The classic prompt text names decimal input only, so the hexadecimal path keeps the existing
input error. Guard: the prompt is raised only for all-digit input in the Decimal base. E2E:
`NumberOverflow_HexadecimalKeepsInputError` for DWORD and QWORD.

### 13.7 Enter on the prompt reaches the editor

Failure mode: the prompt is not the foreground modal, so Enter (or the editor's default button) answers for
the editor and saves the typed value without the question. Guard: the prompt is a modal `ClassicMessageBox`
owned by the editor, which stays disabled while the prompt is open. E2E: the prompt is found by its caption, and
the editor is checked disabled while the prompt is open.

### 13.8 QWORD editor layout differs from template 117

Failure mode: the QWORD editor takes the DWORD rectangles, the wrong client size, or a different control order
or tab order. Classic RT_DIALOG 117 has the same geometry as 111 (219 x 103 DLU), and its controls are listed
in DLG-035. Guard: `ClassicDialogTemplates.EditQword` uses the 117 template. E2E: `NumberOverflow_QwordLayout` runs
`CheckGeometry` against the 117 template, the tab order, the caption, the mnemonics and the read-only value name.

### 13.9 QWORD editor opens in decimal

Failure mode: the QWORD editor opens with the Decimal radio selected, or shows the stored value in decimal.
Classic opens hexadecimal by default (OQ-12). Guard: the same base default as the DWORD editor. E2E:
`NumberOverflow_QwordLayout` checks the Hexadecimal radio is selected and 5000 shows as 1388.

### 13.10 Base switch loses digits at the top of the QWORD range

Failure mode: converting FFFFFFFFFFFFFFFF from hexadecimal to decimal and back loses digits, because the
conversion goes through a 32-bit value or a signed one. Guard: `TryParse` and `Render` work in `ulong`.
E2E: `NumberOverflow_QwordLayout` converts the QWORD maximum typed in decimal to hexadecimal and back.

### 13.11 String 111 used as the caption

Failure mode: the prompt uses the caption "Warning" (RT_STRING 111). No classic prompt is identified in the dump
as using it, so BWC does not show it. This is declared (MSG-053), not proven, and the reason is recorded in
`regedit-audit.md`.

## 14. 0.2.0 additions for the policy setting dialog (U1b)

The Local Group Policy Editor needs four things the regedit dialogs do not have: a resizable modal dialog, text from
ADML templates shown literally, a numeric spin box, and an editable list grid for "Show Contents". Each is opt-in.
No existing dialog declares any of them, so the regedit Dialogs suite is the regression guard for everything below.
The E2E tests are `tests/Bwc.E2E/Dialogs/ClassicDialogAdditionsTests.cs`. They run the controls in host dialogs that
exist only in the E2E flavor (`E2EDialogHost`, compiled under `BWC_E2E`, started by the environment variable
`BWC_E2E_DIALOG_HOST` in a head process). Each host dialog writes a JSON report (`BWC_E2E_DIALOG_HOST_REPORT`) that the
test reads after the dialog has closed.

### 14.1 Resizable flag leaks into existing dialogs

Failure mode: the presenter hook makes every dialog resizable, or a default changes, and a regedit dialog gets a sizing
border (the outer size changes, so every geometry check moves). Guard: `ModalDialogWindow` asks a virtual
`IsResizableDialog`, false by default; `ClassicDialogDefinition.Resizable` is false by default; only a definition that
sets it turns it on. E2E: the regedit Dialogs suite is unchanged and green, and `Additions_FixedDialogHasNoSizingBorder`
checks that a template dialog without the flag has no `WS_THICKFRAME`.

### 14.2 A resizable dialog can be dragged below its minimum

Failure mode: the user shrinks the dialog until controls overlap or the buttons are clipped. WinUI has no minimum size
on the presenter. Guard: a window subclass answers `WM_GETMINMAXINFO`. The minimum is the client size in DLU converted at
the window's current DPI, plus the non-client frame read from the window at that moment, so it is right at 100, 150 and
200 percent and again after the dialog moves to a monitor with another DPI. `MinWidthDlu` or `MinHeightDlu` of 0 means
the template size. E2E: `Additions_ResizableDialogHonorsMinimumAndGrows` drags the corner far inside the minimum and
checks the client size is the minimum, then drags it outward and checks the client grew and the anchored controls followed.

### 14.3 The subclass outlives the window, or leaks

Failure mode: a window subclass that is never removed keeps a pinned GCHandle, a message reaches a freed handle after
close and crashes the host, or a dialog that fails to show leaves the subclass installed. Guard: the subclass is
installed only after `ApplySizeAndPosition` succeeded, removed in `OnClosed` and in `AbandonShow`, and the handle is freed
once. The callback catches every exception and falls through to `DefSubclassProc`, so an error never unwinds into native
code. E2E: the resizable host is opened twice, closed once by OK and once by Escape, and the process must still be
running and respond afterward.

### 14.4 Content does not follow the window

Failure mode: the window grows but the canvas keeps the template size, so the face color stops at the old edge and controls
stay where they were. Guard: for a resizable template the canvas takes the size of the root on every size change and
`OnClientResized` is raised so the dialog can move its controls. A dialog that does not override it keeps the template
layout and still paints the whole client area. E2E: the host anchors OK and Cancel to the bottom right and stretches the
text box; the test compares the positions before and after the drag.

### 14.5 Resizable dialog opens at the wrong size or is maximizable

Failure mode: turning the sizing border on changes the frame, so the client size no longer equals the template, or the
maximize and minimize boxes appear. Guard: the presenter keeps `IsMaximizable` and `IsMinimizable` false and the client is
sized with `ResizeClient` after the presenter is set, as for the fixed dialogs. E2E: the resizable host is checked for the
template client size at open and for the absence of `WS_MAXIMIZEBOX` and `WS_MINIMIZEBOX`.

### 14.6 ADML text with an ampersand becomes a mnemonic

Failure mode: a policy name such as "Allow R&D access" is parsed as Win32 mnemonic text. The ampersand disappears, a
letter is underlined under Alt, Alt+letter activates the control, and a second text with the same letter makes the dialog
throw "The mnemonic ... is used by both". Guard: `ClassicControlDefinition.LiteralText` (default false). A literal control
gets `ClassicMnemonicText.Literal`, whose text is the source unchanged, with no access key and no entry in the mnemonic
table, so there is no duplicate check. UI Automation names use the same text. Group box captions already do not parse the
marker. Default behavior is unchanged: a non-literal control still removes the marker, still underlines under Alt and
still throws on a duplicate. E2E: `Additions_LiteralTextKeepsAmpersand` checks that the UI Automation names keep `&` for
literal controls whose would-be letters collide (no throw), that Alt+letter does not invoke the literal button, that a
non-literal control in the same dialog keeps its mnemonic, and that two non-literal controls with one letter still throw
(the host reports the exception).

### 14.7 Literal text mixed up with the double ampersand

Failure mode: a literal "A && B" is shown as "A & B" because the escape is still applied, or a trailing "&" is dropped as
Win32 drops it. Guard: literal text is not scanned at all. Two ampersands stay two, and a trailing one stays. E2E: the
literal host label is "Fish && Chips &" and the test expects exactly that text.

### 14.8 Spin box range and keys

Failure mode: Up past the maximum wraps or overflows, Down below the minimum goes negative, a step larger than the range
jumps outside it, or a value near the `long` limits overflows when the step is added. An unsigned policy spinner can accept a
value above `long.MaxValue` and then replace it with an unrelated previous signed value on Up or Down; clipping its maximum
or step also leaves valid `ulong` values unreachable. Signed spinner callers can regress in parsing, stepping, range or
automation behavior. Guard: signed controls keep their `long` path. Unsigned controls parse and format the full `ulong` range,
then use an `Int128` intermediate and clamp to their unsigned minimum and maximum. Each path rejects `min > max` and
`step == 0`. Up and Down on the text box change the value by one step. Page Up and Page Down are not handled (they do not move
a Win32 up-down). Typing never clamps. E2E: `Additions_SpinnerRangeKeysAndText` keeps exercising signed key, button and
automation behavior; `Additions_UnsignedSpinnerFullRange` covers `long.MaxValue + 1`, `ulong.MaxValue`, unsigned boundaries,
and a large step.

### 14.9 Typed text the spin box refuses to hold

Failure mode: the spin box rewrites what the user typed (clamps 5000 to 100, strips letters, pads zeros), so the caller can
never show its own validation message, or `TryGetValue` throws on text that is not a number. Guard: typed text is kept
exactly. `Text` returns it, `TryGetValue` is false for empty, non-numeric or out-of-range text and never throws, and the
caller decides what to do. A key or button press on unparsable text starts from the last valid value. E2E: the test types
"abc" and "5000", reads `Text` back unchanged, reads `TryGetValue` from the report, and presses Up from "abc".

### 14.10 Spin box parts take the tab stop or the focus

Failure mode: the two buttons are tab stops, so Tab visits three stops per spin box, or a click on a button moves focus off
the text. Guard: the buttons are not tab stops and a click returns focus to the text box. The text box is the focus target
of the hosted control, so the dialog's Tab order, Enter, Escape and Alt handling see one stop for the spin box. E2E: the tab
sequence of the spinner host lists each spin box once, and the Up button invoked through UI Automation leaves focus on the
text.

### 14.11 Spin box has no name or value for a screen reader

Failure mode: the control shows as an unnamed group with two unnamed buttons. Guard: the control has an automation peer of
type Spinner with the name from the definition, `RangeValue` (value, minimum, maximum, step) and `Value` (the text); the
text box has the id `<id>.Text`, and the buttons are named "Increase" and "Decrease" with the ids `<id>.Up` and `<id>.Down`.
E2E: the test reads the name, range and value through UI Automation.

### 14.12 Spin box metrics differ from the other classic controls

Failure mode: the control is taller than its template rectangle (padding, a border drawn outside), uses Fluent text box
metrics, or the text is clipped by the buttons. Guard: the spin box takes the template rectangle like every other control,
draws its 1 DIP frame inside it, uses the dialog font, and the buttons take a fixed 15 DIP column inside the frame. E2E:
`CheckGeometry` compares the control rectangles with MapDialogRect for the spinner host template.

### 14.13 Focus mapping breaks for a control whose focus is inside it

Failure mode: the dialog finds the focused control by comparing the focused element with the hosted element. A spin box or a
grid focuses an inner element, so the comparison fails, Tab restarts from the first stop, or Enter never reaches the default
button. Guard: the hosted control has a focus target and recognizes focus inside it. Only the new kinds use that (for the
others the target is the element itself and the old comparison runs unchanged), so existing dialogs behave as before. E2E:
the tab sequence of both new hosts goes through the new controls in template order.

### 14.14 Grid: adding a row

Failure mode: there is no empty row to type into, a new row appears only after Enter and the typed text is lost, or two empty
rows accumulate. Guard: the grid always ends with exactly one empty row. When the user types into it the row becomes a real
row on the first character and a new empty row is appended in the same step, with the focus unmoved. `SetRows` also ends
with the empty row. E2E: `Additions_EditableGridTwoColumns` and `Additions_EditableGridOneColumn` type into the last row and
read the rows from the report.

### 14.15 Grid: Delete removes the wrong thing

Failure mode: Delete while editing deletes the row instead of a character, deletes the empty row, or leaves the focus on a
removed cell. Guard: Delete removes the row only when the focused cell is not being edited and the row is a real row. While
editing, and on the empty row, Delete is a character delete. After a row is removed the focus goes to the same index (the
next row, or the empty row). E2E: Delete on a focused cell removes exactly that row and the next row is focused.

### 14.16 Grid: edit state leaks

Failure mode: a cell stays editable after focus leaves it, so typed keys go to two cells; Escape closes the dialog instead of
cancelling the edit; or Enter in an edit presses OK. Enter or Escape on an untouched trailing row is swallowed, so the dialog
cannot accept or cancel. Enter that commits the last nonempty cell of a two-column row removes that row and advances into the
next row's other column. Guard: only one cell is editing at a time. Enter commits and moves to the next column only while the
edited cell remains in the grid; Escape reverts the cell and is consumed only when it ends an edit (it reaches the dialog when
no edit is open), losing focus commits, and a row whose cells are all empty after a commit is removed. The trailing empty row
accepts text without an edit, but Enter and Escape reach the dialog when it is untouched. E2E: Additions_EditableGridTwoColumns checks
Escape in an edit keeps the old text and clearing a row does not advance the focus into the following row.

### 14.17 Grid: keyboard navigation and typing

Failure mode: arrows are swallowed by the text box so the cell cannot change, a printable key on a cell that is not being
edited is dropped, or F2 does not enter an edit. Guard: Up and Down move to the same column of the previous or next row
(committing an edit first), Left and Right move between columns when not editing, and in an edit only at the start or the end
of the text. F2, a double click, or a printable key enters an edit (F2 and the double click select all, a printable key
replaces the text). Tab leaves the grid. E2E: the two-column test uses Up, Down, Left, Right, F2, typing, Enter, Delete and
Escape.

### 14.18 Grid: names and ids per cell

Failure mode: a screen reader reads cells as unnamed edits, or the ids change after a row is removed so a test hits another
cell. Guard: every cell has the id `<id>.R<row>C<col>` (zero based) and the name "<column title>, row <n>" ("new row" for the
empty one). The ids are renumbered after a row is added or removed. The grid has the id and name of its definition and the
header row has the column titles. E2E: the test reads the names and ids of the cells before and after a delete.

### 14.19 Grid: one column and two columns

Failure mode: a one-column grid draws an empty second column, or a two-column grid gives the value column the whole width.
Guard: the column count is the number of titles in the definition (1 or 2; anything else throws at construction) and columns
share the width equally; the header shows each title. E2E: a one-column host ("Value") and a two-column host ("Value name",
"Value").

### 14.20 Host process and reports

Failure mode: the E2E host hook starts in a ship build or in a process that did not ask for it, or the report is read before
the dialog closed. Guard: the whole host is compiled only under `BWC_E2E`; it starts only when `BWC_E2E_DIALOG_HOST` names a
scenario, and an unknown name or a startup exception is written to the report as a failed state, which the test turns into a
failed check. The test reads the report only after the dialog is gone from the screen.

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

## 9. Test coverage

| Area | Guard | E2E check |
| --- | --- | --- |
| Owner disabled and re-enabled | 1.1, 1.2 | Owner `IsEnabled` during and after |
| Client size and positions | 2.1, 2.2, 2.3, 2.5 | Rectangles within 1 DIP of `MapDialogRect` |
| No heading | 4.5 | `DialogHeading` empty |
| Tab order | 3.1 | Tab sequence matches template |
| Alt mnemonics | 3.6 | Alt+V, Alt+H, Alt+D move focus |
| Enter and Escape | 3.8 | Enter saves once, Escape cancels |
| Base conversion | 5.1 | 0x1388 shows 5000 in Decimal |
| Face and text readable | 6.3 | Client face is the palette face; each static text has 12+ strong pixels (`DialogContrast`) |
| Screenshots | all | `artifacts/e2e/visual/dialogs/*.png` |

Edge cases with no E2E check yet: 100 % and 150 % scale (the E2E machine runs at 200 %), monitors
with different DPI, localized labels wider than the template, and a message box wider than the
work area. These are listed in the report, not claimed as covered.

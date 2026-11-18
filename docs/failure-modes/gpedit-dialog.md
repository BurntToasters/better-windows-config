# Local Group Policy Editor setting dialog failure modes

Scope: piece G3a of release 0.2.0. The policy-setting dialog frame in `src/Bwc.Gpedit.Editor/Dialog`: the title and header row,
Previous Setting and Next Setting, the three state radio buttons, the Comment box, the Supported on box, an empty and correctly
sized Options pane (piece G3b fills it), the Help pane, and OK, Cancel and Apply. In `src/Bwc.Gpedit`: the wiring of the result
list's edit request (`GpeditRootView.Editor.cs`) and the in-memory apply seam. Nothing is written to disk: save wiring is piece
G4, which replaces the one apply seam named in section 8.

Evidence (untracked, local): `docs/parity/oracle/gpedit/gpedit-forms.json` (the Editor form of the WinForms assembly: the client
size, the minimum size, the anchors, the tab indexes, the control texts) and `artifacts/oracle/gpedit/probe.json` (steps
`options-dialog` and `byteorder-*`, classic at 192 DPI, so every pixel value is halved to DIPs). Facts used:

- The client is 684 x 604 DIP. The minimum window size is 700 x 640 DIP, which is a client of 684 x 601 (the frame is 16 x 39).
  The form has a sizing border and a maximize box and no minimize box. BWC keeps its dialog frame without a maximize box
  (declared difference, section 4).
- The header row holds a 16 x 16 icon, the policy name, and the Previous Setting and Next Setting buttons. The name label is 340
  DIP wide and the buttons follow it (they do not move when the form grows).
- Below it: the three radio buttons Not Configured, Enabled and Disabled (access keys C, E, D) in a column, then the Comment
  label with a multi-line box (4095 characters at most) and the Supported on label with a read-only multi-line box.
- Below that a split container: Options (a scrolling pane that holds the option controls) left, Help (read-only text) right,
  split 301 | 5 | 372 DIP at the default size. Both panes grow with the form.
- OK, Cancel and Apply sit at the bottom right. Apply starts disabled. The probe tab order is Previous Setting, Next Setting,
  the selected radio button, Comment, Supported on, Help, OK, Cancel (a disabled control is skipped). Focus starts on
  Previous Setting.

Declared differences from classic (the audit lists them): no maximize box; the Options pane is empty until G3b; the three radio
buttons are each a tab stop (the shared dialog base gives every control a fixed tab stop, classic stops only on the selected
radio button); the 16 x 16 icon is the module icon of a policy setting or the glyph that stands in for it.

Unknown (recorded, not guessed):

- OQ-D1: what classic does with unsaved changes when Previous Setting or Next Setting is pressed. The probe never pressed them
  with a change pending and the forms data holds no message for it. Until oracle group C settles it, BWC applies the pending
  change to the in-memory file exactly like Apply, moves on, and if the change does not validate stays on the current
  policy with the message (section 9). No prompt is shown. This loses nothing and asks nothing.
- OQ-D2: whether Previous Setting is disabled on the first policy and Next Setting on the last, or whether they wrap. BWC
  disables them.
- OQ-D3: the title label for a name longer than 340 DIP. BWC trims with an ellipsis and keeps the full name in the UIA name.
- OQ-D4: whether OK with nothing changed counts as a change for the version. BWC applies nothing and writes nothing, as the
  oracle driver document records for classic ("An OK with no change writes nothing").

This file was written before the code. Each section lists how a behavior can fail, the design choice that prevents it and the
E2E check that catches it. Every `Gpedit_Dialog_*` test writes its checks to the E2E result artifact under the ids
`gpedit.dialog.<test>.<check>` and a JSON measurement file with a picture under `artifacts/e2e/visual/gpedit/`
(`gpedit-<test>.json`, `gpedit-<test>.png`). The host is `BwcGpedit.exe` of the E2E flavor over the fixture templates
(`BWC_E2E_ADMX_ROOT`) and a sandbox policy object (`BWC_E2E_GPO_ROOT`), seeded by `GpeditSandbox.SeedStandard` as the list
tests do. Two seams, compiled only under `BWC_E2E`:

| Variable | Meaning |
| --- | --- |
| `BWC_E2E_GPEDIT_NO_DIALOG` | `1` makes the edit request only raise the event and count (the G1 list test relies on it); the support code sets it unless a test asks for the dialog |
| `BWC_E2E_GPEDIT_READONLY` | `1` makes the in-memory store report read-only, to exercise the read-only path before G4 detects it |

The dialog publishes its state in the E2E flavor as the UIA help text of `DialogRoot` (`Key=Value;` pairs: `Policy`, `Side`,
`State`, `Dirty`, `Index`, `Count`, `ReadOnly`, `Applies`), the way the main view does. The ship flavor sets none of it.

## 1. Wrong initial state

Failure mode: the dialog shows Not Configured for an Enabled policy, reads the wrong side for a class Both policy, shows the
comment of another policy, or shows a state for a file that could not be read.

- Handling: the state is `PolicyStateReader.Read(policy, document)` over the document of the side the request names
  (`GpeditPolicyEditEventArgs.Side`), never over the other side. The comment is `CmtxDocument.GetComment(namespace, name)` of
  the same side. A side whose policy file could not be read (a null document) opens the dialog read-only with Not Configured
  selected and no radio changeable (section 10), because classic does not open in that case and BWC must not invent a state.
  A state that the reader cannot compute is treated the same way.
- E2E: `Gpedit_Dialog_InitialStates` opens a policy of each state (Enabled, Disabled, Not Configured) and a class Both policy on
  both sides (Enabled with a comment on the computer side, Disabled with no comment on the user side) and checks the radio
  buttons, the comment text and that Apply is disabled.

## 2. Header texts

Failure mode: the title bar and the name label disagree, the name is not the policy display name, a long name pushes the
buttons away, the Supported on text or the Help text is blank, or the Help text is the category text.

- Handling: the window title and the name label are `PolicyDefinition.DisplayName`; the Supported on box is
  `SupportedOnText`; the Help box is `ExplainText`. The name label has a fixed width (340 DIP) and trims with an ellipsis;
  its UIA name is the whole text.
- E2E: `Gpedit_Dialog_Frame` checks the window title, the label, the Supported on box and the Help box against the fixture
  strings of `Pol_Class_Machine`.

## 3. An ampersand becomes a mnemonic

Failure mode: the policy "Research & Development flag" shows as "Research  Development flag" or underlines a letter, or a
second dialog text with the same letter makes the dialog throw "The mnemonic ... is used by both".

- Handling: every control that shows template text (the name label) is declared with `LiteralText`. The window title, the Help
  box and the Supported on box are plain text and never parsed. The fixed texts of the dialog (Previous Setting, Not
  Configured and the rest) are parsed as classic does, with distinct letters (P, N, C, E, D, A).
- E2E: `Gpedit_Dialog_Literal` opens the ampersand policy and checks the title, the name label and the UIA name keep the `&`
  exactly, and that Alt+E still selects Enabled (the real access key works).

## 4. Layout and resize drift from the forms data

Failure mode: the client size is not 684 x 604 DIP at the default, a control sits in another place, the panes do not grow with
the window, the buttons do not stay at the bottom right, the dialog can be dragged below its minimum, or the layout is off at 150
or 200 percent because it was built from dialog units instead of DIPs.

- Handling: the layout is computed in DIPs from the form (header fixed, radio column fixed, Comment and Supported on boxes
  stretch to the right edge, split height and width follow the client, the split keeps the 301 : 5 : 372 proportion, OK, Cancel
  and Apply are anchored to the bottom right), in `OnClientResized`, so it is exact at every DPI. The client is sized to
  684 x 604 DIP after the template size, and the minimum client is 684 x 601 DIP. The dialog is `Resizable` and keeps no
  maximize or minimize box.
- E2E: `Gpedit_Dialog_Frame` compares every control rectangle with the forms data (tolerance 2 DIP) and the client size.
  `Gpedit_Dialog_Resize` grows the window by a fixed amount and checks the anchored controls, then shrinks it far below the
  minimum and checks the client stops at 684 x 601 DIP and the controls are still where the form puts them.

## 5. Focus, tab order and keys

Failure mode: focus starts on OK or on the radio buttons, Tab skips Comment, the Comment box eats Enter so OK never runs (or
Enter in the Comment box closes the dialog and loses the line break), Escape does not cancel, Alt+letter does not reach the
buttons, an access key is used twice.

- Handling: the template order is Previous Setting, Next Setting, the three radio buttons, Comment, Supported on, Help, OK,
  Cancel, Apply, and the first control takes the focus. OK is the default button and Cancel is id 2. Comment is multi-line, so
  Enter inserts a line break there and presses the default button elsewhere (the shared base does both).
- E2E: `Gpedit_Dialog_Frame` reads the UIA focus at open (Previous Setting) and after each Tab and compares the order with the
  list above, with Apply disabled and so skipped; it checks the control names (UIA names without the marker).

## 6. Apply enabled when nothing changed

Failure mode: Apply is enabled at open, stays enabled after Apply, is disabled after the user changed a state, ignores a
comment-only change, or stays enabled when the user changed the state and changed it back.

- Handling: the dialog keeps a baseline (the state and the comment as last applied or read). Dirty is "the selected state or the
  comment differs from the baseline" (plus the Options pane through `MarkDirty`, for G3b). Apply is enabled exactly when dirty
  and the dialog is not read-only. A successful Apply or a move to another policy sets the baseline to the new values.
- E2E: `Gpedit_Dialog_ApplyOk` checks Apply disabled at open, enabled after a radio click, disabled after clicking back to the
  start state, enabled after typing a comment, disabled after Apply.

## 7. Apply and OK

Failure mode: OK with no change still applies (changes the document, or the version later), OK with a change does not apply,
Apply closes the dialog, OK leaves the dialog open, the list keeps the old State or Comment, a validation problem is applied
anyway, or the apply throws into the UI thread.

- Handling: Apply builds a `PolicyEdit` from the selected state (the Options pane supplies the element values, none until G3b),
  checks it with `PolicyEditValidator` and applies it to the in-memory document of the side with `PolicyStateWriter.Apply`; the
  comment goes to the side's `CmtxDocument`. Nothing is applied when the edit has a problem. After a success the host rebuilds the
  State and Comment cells (the list is rebuilt with its selection kept by tag). OK applies when dirty and then closes; with no
  change it closes and applies nothing. Cancel, Escape and the caption close box never apply. An exception from the engine (a
  template that the document refuses) is caught, logged, shown as a message and leaves the dialog open and the document as the
  engine left it.
- E2E: `Gpedit_Dialog_ApplyOk` sets Disabled and a comment on `Pol_Class_Machine`, presses Apply (the dialog stays, Apply goes
  disabled, the list behind shows Disabled and Yes), presses OK (the dialog closes), reopens the policy and checks the state and
  the comment stayed in memory. `Gpedit_Dialog_Cancel` changes a state, presses Cancel and checks the list is unchanged.

## 8. A write to disk by accident

Failure mode: Apply or OK writes the policy file, the version file or the comment file, or calls a refresh; the real policy of
the machine changes in a test.

- Handling: `Bwc.Gpedit.Editor` names no file API (the writer guard `scripts/check-policy-writer.js` scans it). The dialog calls one
  interface, `IPolicyEditStore.Apply`, whose only implementation here (`InMemoryPolicyEditStore`) changes the models of the loaded
  snapshot. Piece G4 replaces that one class to save. The harness hashes the real policy folder around every unelevated run
  (`gpo.real-unchanged`).
- E2E: `Gpedit_Dialog_OkWritesNothing` hashes the sandbox policy file, version file and comment file of both sides (SHA-256 of
  the bytes, or "absent"), applies a state and a comment with Apply and OK, and checks all hashes are equal afterwards. The run
  artifact holds `gpo.real-unchanged`.

## 9. Previous Setting and Next Setting

Failure mode: they move through the wrong list (all policies instead of the shown list, or the sorted order ignored), include
the folder rows, do not stop at the ends, move a class Both policy to the other side, lose the typed comment, lose or silently
drop the pending state change, leave the title and the other texts of the old policy, or leave the list selection on the first
policy.

- Handling: when the dialog opens, the host gives the dialog the policy rows of the shown list in display order (folder rows are
  left out) and the position of the opened one. Each row carries its side, so a class Both policy stays on the side of its list.
  Previous Setting is disabled at the first policy and Next Setting at the last. A move first applies a pending change as Apply
  does (OQ-D1); if that fails validation the dialog stays on the policy with the message and nothing moves. A move then reads
  the new policy exactly as at open: the title, the radio buttons, the comment, the Supported on box, the Help box, a clean
  baseline and Apply disabled. When the dialog closes the host selects the row of the last policy shown and gives the list the
  focus.
- E2E: `Gpedit_Dialog_PreviousNext` opens the third row of Basics, checks Next goes to the fourth policy by title, Previous
  comes back, the first policy has Previous disabled and the last has Next disabled; it changes the comment, presses Next and
  Previous and checks the comment is back and the list shows Yes; a category with one policy and folders (the BWC Oracle user
  node) has both disabled; after closing, the selected list row is the last policy shown.

## 10. Read-only mode

Failure mode: in a read-only store the radio buttons still change, Apply or OK writes, or the dialog looks the same and the user
cannot tell why nothing happens.

- Handling: `IPolicyEditStore.IsReadOnly` (false in G3a, G4 fills it from the access check and the Windows edition) and an
  unreadable side both put the dialog in read-only mode: the radio buttons are disabled, the Comment box is read-only, Apply is
  disabled, OK closes the dialog without applying, and Previous and Next still move. The notice text itself is G4's.
- E2E: `Gpedit_Dialog_ReadOnly` starts with `BWC_E2E_GPEDIT_READONLY=1` and checks the radio buttons are disabled, the comment is
  read-only, Apply is disabled, OK closes the dialog and the state of the list row did not change.

## 11. Validation problems

Failure mode: an Enabled edit that the engine refuses (an empty required element) is applied, the message is missing or has no
field name, the dialog closes after the message, or the dirty state is lost.

- Handling: the problems come from the validator. The first problem is shown in a classic message box owned by the dialog
  (caption "Group Policy"); a required element uses the "Field ... is required" caption with the field label of the
  presentation (or the element id). The other kinds use a neutral text until G3b maps every kind. The dialog stays open, keeps
  the selected state and the typed comment and stays dirty. Until G3b adds the Options pane an Enabled policy that has a
  required element cannot be applied, and the message says so.
- E2E: `Gpedit_Dialog_Validation` opens `Pol_Text_Required`, selects Enabled and presses OK; the message box appears with the field
  caption, Enter dismisses it, the dialog is still open with Enabled selected and Apply enabled, the list State is unchanged.

## 12. Closing and focus

Failure mode: the owner stays disabled, focus is lost to another window, the list loses the selected row, a second dialog opens
over the first (Enter twice, a double click that arrives twice), or the process is left with a hidden window.

- Handling: the base dialog restores the owner on every close path. The view keeps one dialog at a time: a request while one is
  open is ignored. After the dialog closes, the list is selected on the last policy shown and has the focus. A dialog that fails
  to build or show is logged and the owner is restored by the base.
- E2E: `Gpedit_Dialog_Frame` closes with Escape and checks the owner window is enabled, the list holds the focus and the same
  row is selected; `Gpedit_Dialog_InitialStates` opens the dialog repeatedly by Enter and a double click.

## 13. Strings

Failure mode: a resource entry is missing and the control shows the key name or nothing; text copied from classic beyond the
control labels.

- Handling: the dialog accessor (`DialogStrings`) returns the key when an entry is missing, so a blank control cannot pass
  review. `Strings/en-US/Dialog.resw` holds only the control labels of the form and the two messages above; the checker
  `npm run validate` regenerates `string-parity.json`.
- E2E: the frame test compares the UIA names of the controls with the expected labels.

## 14. Findings of the first E2E runs

1. Before the product code existed, all 10 `Gpedit_Dialog_*` tests failed (5 checks passed, 27 failed); every `.opens` check failed
   because no dialog appeared.
2. With the dialog, 8 of 10 passed. The Frame and Resize tests failed only on the icon (`pictureBox`) and the Options pane
   (`flowLayoutPanelDynamic`), which were missing: the two extra parts were added to the surface by way of the parent of the OK
   button, which is not the surface. They are now added to the canvas that the dialog base builds, and the dialog throws when it
   cannot find that canvas, so this cannot fail silently again. All other rectangles matched the expected layout.
3. The final run of the 10 tests passed (145 checks passed, 0 failed).
4. For piece G3b: the Options pane (`OptionsPane`) is outside the tab order of the dialog base, which follows the template controls
   only. G3b must either add its controls to the template or extend the tab order. `MarkOptionsDirty` and `ElementValues` are the
   hooks for a changed option and for the values of an Enabled edit.
5. For piece G4: replace `InMemoryPolicyEditStore` with a store that saves; `PolicySettingDialog` and the wiring in
   `GpeditRootView.Editor.cs` need no other change.

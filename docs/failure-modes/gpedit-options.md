# Local Group Policy Editor Options pane failure modes

Scope: piece G3b of release 0.2.0. The Options pane of the policy-setting dialog (`src/Bwc.Gpedit.Editor/Options`, hosted by
`src/Bwc.Gpedit.Editor/Dialog/PolicySettingDialog.cs` of piece G3a, see `docs/failure-modes/gpedit-dialog.md`): one control per
presentation element, the "Show Contents" list dialog, and the validation messages of Apply and OK. Nothing is written to disk
here: the values go through `PolicySettingDialog.ElementValues` to the same in-memory apply seam as G3a (G4 saves).

Evidence (untracked, local): `docs/parity/oracle/gpedit/gpedit-forms.json` (the Editor form and the `ListBoxDialog` form of the
WinForms assembly, and the message resources of the assembly) and `artifacts/oracle/gpedit/probe.json` (step `options-dialog`,
classic at 192 DPI, so every pixel value is halved to DIPs; the probed policy was Not Configured, so every option control was
disabled). Facts used:

- The pane is `flowLayoutPanelDynamic`: a top-down flow panel that scrolls (298 x 329 DIP at the default size). Every element is
  one row. Inline kinds (decimal, drop-down, list) put the label on the left and the control after it; a text box puts its label
  above it; a check box is alone. The probe rectangles at 100 percent, relative to the pane: decimal label 90 x 26, number box
  120 x 23 with a 16 wide spin column, gap label to control 6; text box 220 x 23 below a 26 high label; drop-down 143 x 24
  after a label 6 away; check box 119 x 22 at x 4; list button "Show..." 58 x 28 after its label. Rows are 29 (decimal),
  55 (text), 29 (drop-down), 28 (check box) and 34 (list) high and start 1 DIP from the pane edge.
- The automation ids of the probe are the classic control name: kind prefix plus the label (`labelDecimal option:`,
  `textBoxText option:`, `dropDownListDrop-down option:`, `checkBoxCheck box option`, `buttonList option:`). The UIA name of a
  field is its label. BWC uses the same ids (a number box with spin buttons is `numericUpDown<label>`, an editable combo is
  `comboBox<label>`, a multi-line box with no label is `multiTextBox<element id>`).
- Every option control of the probe was disabled while the policy was Not Configured.
- The Editor form tab indexes: the split container holds the Options pane in Panel1 and Help in Panel2, so the option controls
  come after Supported on and before Help.
- The "Show Contents" form (`ListBoxDialog`): client 494 x 304 DIP, a label above a data grid (12,20 and 12,47, anchored on all
  sides), OK and Cancel at the bottom right (`&OK`, `&Cancel`). Tab indexes: label 0, the button table 1, the grid 3.
  Column titles are "Value name" and "Value".
- The message texts are `SettingControl.Validate` (required), `NumericControl.Validate` (minimum, maximum),
  `MultiTextBoxControl.Validate` (count, length, empty), `ListBoxControl.Validate` (no entries) and `ListBoxDialog.Validate`
  (empty name, empty value, duplicate name, duplicate value) of the forms resources. They are copied into
  `Strings/en-US/Options.resw` through the resource accessor, never into code.

Declared differences from classic (the audit lists them): no UIA pane per element row (the controls sit directly in the
pane); the option controls are laid out by code in DIPs, so they are exact at every DPI; the check box and label text wraps at
the pane width where classic scrolls sideways (OQ-O6).

Unknown (recorded, not guessed). Each has the BWC choice that is made until the oracle settles it:

- OQ-O1: whether a drop-down list is sorted by display name unless `noSort` is true. BWC sorts (culture, ignore case) and keeps
  the original item index as the value; the ADMX schema says items are alphabetized unless `noSort`.
- OQ-O2: which item a drop-down without `defaultItem` selects when it is sorted. BWC selects the item with the original index 0.
- OQ-O3: what a number box does with empty or non-numeric text, and whether it validates when it loses the focus. BWC accepts
  only digits (and a minus sign when the minimum is negative), validates at Apply, OK and a move, and takes the last valid
  value for empty text. The "has been reset" messages of classic say the number is reset, so BWC resets it to the limit, shows
  the message, puts the focus on the field and applies nothing.
- OQ-O4: whether a plain (one column) list rejects equal values when the list has a value prefix. BWC rejects them in every
  plain list.
- OQ-O5: where the focus goes after a validation message. BWC puts it on the offending control (the cell for a list entry), as
  a WinForms validation does.
- OQ-O6: how a label longer than the pane is shown. BWC wraps it.
- OQ-O7: the first focus of the Show Contents dialog. The tab indexes put the buttons before the grid, so BWC focuses OK.
- OQ-O8: the text of the label above the Show Contents grid (the form has no text). BWC shows the label of the list element.
- OQ-O9: whether a number box with `spin="false"` and a long decimal box have spin buttons. BWC gives spin buttons to a
  decimal (or long decimal) box whose presentation says `spin` is not false, and a plain text box otherwise.
- OQ-O10: the height of a multi-line box (`defaultHeight` lines) and whether empty lines inside it are an error. BWC uses
  `defaultHeight` (3 when absent) lines of 15 DIP plus 8, and drops empty lines as the engine does.
- OQ-O11: whether the length limit of a multi-text element is per line (the message says so) or for the whole text (the engine
  says so). BWC checks what the engine checks and quotes its limit.

This file was written before the code. Each section lists how a behavior can fail, the design choice that prevents it and the
E2E check that catches it. Every `Gpedit_Options_*` test writes its checks to the E2E result artifact under the ids
`gpedit.options.<test>.<check>` and a JSON measurement file with a picture under `artifacts/e2e/visual/gpedit/`. The host is the
same as for the dialog tests (the fixture templates and a sandbox policy object, `GpeditSandbox.SeedStandard`). One E2E-only
readout is added: the help text of `flowLayoutPanelDynamic` holds `Enabled`, one `V.<element id>` per element (the value that
Apply would write, as a compact JSON string, percent-escaped) and one `I.<element id>` per drop-down or combo box (the items in
display order). The ship flavor sets none of it.

## 1. A control missing, extra, or in the wrong order

Failure mode: an element has no control, a control appears for a static text that has no element, the controls follow the ADMX
element order instead of the presentation order, the pane shows the controls of the previous policy after Previous or Next, a
presentation control whose `refId` finds no element crashes the dialog, a control of the wrong kind is made for an element
(a text box for a list), or a policy that has elements and no presentation shows nothing and then loses its values on Apply.

- Handling: the pane is built from `PolicyDefinition.Presentation.Controls` in document order, one control for each. A `text`
  control is a label. A control whose element is null or of a kind that does not fit is skipped and logged (never thrown).
  An element that has no control keeps the value that was read for it, so Apply does not delete a value the user cannot see. The
  pane is cleared and rebuilt on every load (open, Previous, Next).
- E2E: `Gpedit_Options_Elements` opens the policy of every element kind and checks the controls in order by their automation
  ids (`Pol_Amp_LabelSingle`: a text box, a check box, a number box, a drop-down; `Pol_MultiText_Basic`: a label then a
  multi-line box; `Pol_Bool_TwoChecksReverseOrder`: Zeta before Alpha, the reverse of their names), checks a policy with no
  elements shows an empty pane, and moves with Next and Previous to see the controls replaced.

## 2. A wrong default

Failure mode: a number box shows 0 where the presentation says `defaultValue`, the default is outside the range and is shown
anyway, a check box ignores `defaultChecked`, a text box or combo box ignores its default, `defaultItem` is ignored or is
taken as an index into the sorted list, a long decimal default above 32 bits is cut, a default with an ampersand loses it.

- Handling: when the policy is not Enabled, or the Enabled policy has no stored value for the element, the control shows the
  presentation default: `DefaultValue` (number: clamped into the minimum and maximum of the element; text and combo box: as
  given), `DefaultChecked` (false when absent), `DefaultItem` as the zero-based index in the element's item order (0 when
  absent). A number box without a default shows the minimum (0 when the element has none). A stored value always wins.
- E2E: `Gpedit_Options_Defaults` opens policies that are Not Configured and checks the readout: `MinMaxNumber` 25 (default and
  range), `RequiredNumber` 0, `LongNumber` 123456789012, `PlainText` "plain default", `ComboText` "Oslo", `StringCheck`
  checked, `UnsortedChoice` item 1, `AmpText` "Fish & Chips".

## 3. The current value is not shown

Failure mode: an Enabled policy shows the defaults instead of the stored values, an element that has no stored value (a
partial file) shows a stale value of another policy, the stored values are lost after an Apply and a reopen, a stored value of
another kind is shown as a number.

- Handling: the values come from `PolicyStateReader.Read` (`Elements`, by element id) of the side of the dialog. After a
  successful Apply the snapshot is read again and becomes the base of the list entries (section 10).
- E2E: `Gpedit_Options_Roundtrip` sets values of every kind in the dialog, presses Apply, closes and reopens the policy and
  checks the controls show what was entered.

## 4. Enabled in the wrong state

Failure mode: the controls are enabled in Not Configured or Disabled, stay disabled after the user selects Enabled, stay enabled
after the user selects Disabled again, a read-only dialog lets the user type, the "Show..." button works while the policy is not
Enabled.

- Handling: `Enabled = (selected state is Enabled) and not read-only`, set on every radio change and every load. The controls keep the
  values the user typed while they are disabled.
- E2E: `Gpedit_Options_Defaults` checks every option control is disabled at Not Configured and Disabled, enabled at Enabled,
  disabled again after Disabled is selected and that the typed value survives; `Gpedit_Options_List` checks the "Show..."
  button.

## 5. Number boxes

Failure mode: the spin step is 1 where `spinStep` says 5, Up passes the maximum or Down passes the minimum, the step is a
multiple of the step instead of a step from the current value, the box has spin buttons when `spin` is false, a long decimal
above 2^63 breaks the control, a letter can be typed, a number above the range is accepted, soft ranges are treated as soft,
the number is shown with a thousands separator.

- Handling: a decimal or long decimal box whose control says `spin` is not false is a `ClassicSpinBox` (minimum from the
  element or 0; maximum from the element or 2^32 - 1, at most 2^63 - 1; step `spinStep` or 1). Otherwise it is a plain text box
  with the same digit filter. The text is the plain invariant number. `soft` does not change the range (the engine ignores it,
  `PolicyRules.SoftIgnoredByValidation`).
- E2E: `Gpedit_Options_Numbers` on `Pol_Decimal_MinMax` (10 to 500, step 5): Up gives 30 from 25, Down twice gives 20, Down from
  10 stays 10 and Up at 498 stops at 500; on `Pol_LongDecimal_Basic` Up adds 1000; `Pol_Decimal_Required` (`spin="false"`) has
  no spin buttons; letters typed are ignored.

## 6. Text, combo box and multi-line boxes

Failure mode: the text box is longer than `maxLength`, the combo box is not editable, its suggestions are unsorted when they
should be sorted or sorted when `noSort` is set, the typed text is lost when a suggestion list opens, a multi-line box joins
its lines, an empty line stays in the value, an ampersand in a default disappears.

- Handling: a text box has `MaxLength = maxLength` (1023 when absent). An editable combo box applies the same `maxLength`
  (1023 when absent) to its inner text box, matching `TextBoxField`. A combo box is editable, its items are the suggestions
  sorted by culture ignore case unless `noSort`, its text is the value. A multi-line box accepts Enter, its value is its lines
  (empty lines are dropped by the engine). Text is never parsed for access keys.
- E2E: `Gpedit_Options_Choices` reads `I.ComboText` (sorted: Cairo, Lima, Oslo, Paris; the document order is Paris, Oslo, Lima,
  Cairo) and `I.ComboNoSortText` (Large, Medium, Small). Gap: the fixture lists the `noSort` combo box suggestions in
  alphabetic order already, so this check cannot tell a sorted list from an unsorted one. The `noSort` drop-down list
  (`Pol_Enum_NoSortDefaultItem`, Zulu, Alpha, Mike) does tell, and both kinds share the sorting code. The fixture is not
  changed in this piece (recorded for the audit). `Gpedit_Options_Numbers` types 13
  characters into `ShortText` (maxLength 12) and sees 12. `Gpedit_Options_Roundtrip` types two lines into `MultiLines`.

## 7. Drop-down lists

Failure mode: `defaultItem` is ignored, the sorted list changes which item is stored (the value is the displayed index instead
of the item), a sorted list is shown in document order, `noSort` is ignored, a required list has no selection.

- Handling: the control holds the display strings sorted unless `noSort`, with the original item index as the tag. The stored
  value is `ChoiceValue(original index)`. The default is `DefaultItem` or 0 (OQ-O2).
- E2E: `Gpedit_Options_Choices` on `Pol_Enum_NoSortDefaultItem` (Zulu, Alpha, Mike; noSort; defaultItem 1): items in document
  order, selected item Alpha (index 1); on `Pol_Enum_DecimalItems` (Zero, One, Two, Three) the items are sorted (One, Three,
  Two, Zero) and choosing "Two" stores index 2.

## 8. Check boxes

Failure mode: the check box ignores `defaultChecked`, a boolean with no false value is stored as a number, the label is
parsed for an access key, a long label is clipped.

- Handling: a check box is a `BooleanValue`; the label is literal text (section 11).
- E2E: `Gpedit_Options_Defaults` (`StringCheck` is checked by default), `Gpedit_Options_Roundtrip` (toggle and reopen).

## 9. List elements and the Show Contents dialog

Failure mode: the "Show..." button is missing or does nothing, it opens an empty grid for a list that has entries, the plain
list shows two columns or the explicit list shows one, the columns are swapped (value first), a new row cannot be added
(the grid always ends with an empty row), a row cannot be edited or deleted, Cancel keeps the changes, OK drops the changes,
an empty value or an empty name is accepted, a duplicate is accepted, a message appears without moving the focus to the row,
the entered order is lost, the dialog does not block the policy dialog, the policy dialog is not marked changed after OK.

- Handling: the "Show..." button opens `ShowContentsDialog` (a classic dialog over `ClassicEditableGrid`): columns "Value" for a
  plain list, "Value name" and "Value" for an `explicitValue` list, the rows of the current list. OK validates the rows
  (empty name: `ListBoxDialog.Validate.EmptyName`; empty value: `EmptyValue`; the same name twice: `DuplicateName`; the same
  value twice in a plain list: `DuplicateValue`; caption "Error in the information entered") and keeps the focus on the
  offending cell. Cancel changes nothing. OK with a changed list calls `MarkOptionsDirty`. A list that is not changed is not
  marked.
- E2E: `Gpedit_Options_List` on `Pol_List_Plain`, `Pol_List_ExplicitValue`: columns and names, add two rows by typing,
  delete one, OK, Apply, reopen and see the rows; Cancel keeps the old list; a duplicate shows the message and the focus is
  on the cell; an empty value shows the message.

## 10. Removed entries of an additive list

Failure mode: `ListValue.Removed` is empty, so the engine cannot remove the entry the user deleted from an additive list and the
policy file keeps it; or it holds every entry; or it holds an entry that the user only edited; or it holds entries of another
policy.

- Handling: the base of a list is the list of the snapshot (`PolicyStateSnapshot.Elements`, with the stored value names). When
  Apply asks for the values, `Removed` holds each base entry that is not in the final list (explicit list: its name is not in
  the final names; plain list: its data is not in the final data). After a successful Apply the base is the new snapshot, so a
  second Apply does not remove twice.
- E2E: `Gpedit_Options_List` seeds `Pol_List_Additive` Enabled with three entries, deletes the middle one in Show Contents,
  presses Apply and reads `V.AdditiveList` (two entries, one removed, the removed one is the deleted one), and the reopened
  dialog shows two entries.

## 11. An ampersand becomes a mnemonic

Failure mode: "Name & address:" shows "Name  address:" with a letter underlined, "Fish && Chips:" collapses to one
ampersand, two labels that share a letter make the dialog throw, Alt+letter focuses an option control, a check box or drop-down
item loses its ampersand.

- Handling: every label, check box text and button text of the pane is created with the literal-text flag; the items of a
  drop-down are plain strings. None of them registers an access key.
- E2E: `Gpedit_Options_Literal` on `Pol_Amp_LabelSingle` and `Pol_Amp_LabelDouble`: the UIA names of the text box, check box,
  number box and drop-down keep the `&`, "Fish && Chips:" keeps both, the dialog does not throw, Alt+N does nothing.

## 12. Apply and dirty wiring

Failure mode: changing an option does not enable Apply, Apply is enabled at open, a typed value is lost by a move, `ElementValues`
returns the values of the previous policy, Apply writes the pane defaults when the policy is switched from Disabled to Enabled
without touching the pane (the defaults must be written), a comment-only change writes the option values, an unchanged OK writes
anything.

- Handling: every control calls `MarkOptionsDirty` on its change event (not while loading). `ElementValues` reads the
  controls (an element without a control keeps its read value). Switching to Enabled is a state change, so the values (the
  defaults) are written by Apply. A comment-only change builds no edit (G3a).
- E2E: `Gpedit_Options_Roundtrip` checks Apply is disabled at open, enabled after one edit of each control kind, disabled after
  Apply; the dirty readout is 1; `Gpedit_Options_Defaults` selects Enabled and applies without touching the pane and the
  reopened controls show the defaults.

## 13. Validation messages and focus

Failure mode: a required field accepts an empty value, the message names no field, a number out of range is accepted or
silently clamped without a message, the message text is another one than classic's, the dialog closes after the message, the
focus is lost or stays on OK, the dirty state is lost, a second message hides the first.

- Handling: Apply and OK run `PolicyEditValidator` over the edit and show the first problem. Mapping (the caption of an
  element problem names the field: the label without its colon, the element id when it has none):

  | Problem | Message of classic |
  | --- | --- |
  | Required (text, combo box, number) | `SettingControl.Validate`, caption "Field "{0}" is required" |
  | Required (multi-line box) | `MultiTextBoxControl.Validate.Empty` |
  | Required (list) | `ListBoxControl.Validate` caption "Error in field "{0}"" |
  | BelowMinimum, AboveMaximum | `NumericControl.Validate.Min` and `.Max` (the limit, and the entered number); the box is reset to the limit |
  | TooManyLines | `MultiTextBoxControl.Validate.Count` |
  | TooLong (multi-line box) | `MultiTextBoxControl.Validate.Length` |
  | TooLong (text, combo box), InvalidCharacter, EntryEmpty, EntryNameMissing, DuplicateEntry | the matching `ListBoxDialog.Validate` text when a list entry; otherwise the neutral text of G3a |

  The dialog stays open, keeps its state and stays dirty; after the message is closed the focus is on the offending control.
- E2E: `Gpedit_Options_Validation`: `Pol_Text_Required` (empty: caption and focus on `textBoxRequired text:`), `Pol_Decimal_MinMax`
  (5: minimum message, box shows 10, focus on the number box; 600: maximum message, box shows 500), `Pol_MultiText_Limits`
  (empty: empty message; four lines: count message), `Pol_Text_ComboBoxNoSortRequired` (empty: required). Each checks the
  list State is still Not configured and Apply is still enabled. The fixture has no required list element, so the
  `ListBoxControl` message is not driven (gap, recorded in the audit).

## 14. Tab order drift

Failure mode: Tab skips the option controls, visits them after Help or after OK, visits them in the wrong order, stops on a
label or on the pane, visits a disabled control, enters the grid cells of a list (the list has only its button), Shift+Tab
goes another way, the base order of G3a breaks.

- Handling: the dialog handles Tab itself (tunneling, before the base): previous, next, the three radio buttons, comment,
  supported on, the enabled option controls in presentation order (the text of a number box; the box of a combo box), help, OK,
  Cancel, Apply (when enabled). Shift+Tab is the reverse. A disabled control is skipped.
- E2E: `Gpedit_Options_TabOrder` enables `Pol_Amp_LabelSingle` and checks the focus after each Tab from Supported on:
  the four option controls, then Help, OK, Cancel; Shift+Tab goes back through them; at Not Configured Tab goes from Supported
  on to Help.

## 15. Layout

Failure mode: a control is not where the probe has it, a number box is as wide as the pane, a long label pushes the control out
of the pane, the pane does not scroll when its controls are taller than it, the pane scrolls sideways, a control overlaps another,
the layout is off at 150 or 200 percent.

- Handling: the rows are placed in DIPs from the probe (section above) and re-laid out when the pane is resized. Labels and
  check boxes wrap at the pane width less the scroll bar (OQ-O6). The pane scrolls vertically.
- E2E: `Gpedit_Options_Layout` on `Pol_Amp_LabelSingle` (text box, check box, number box, drop-down): the sizes of the number
  box (120 x 23), the text box (220 x 23), the drop-down (143 x 24) and the check box height (22), the gap from label to
  control (6), and that no two controls overlap, all with a tolerance of 2.5 DIP; the rows follow each other without a gap
  larger than 6 DIP.

## 16. Previous Setting and Next Setting

Failure mode: the pending option change is lost or applied without validation when the user moves, the new policy shows the
values of the old one, the values typed for one policy appear in the next, a validation problem moves anyway.

- Handling: a move applies a pending change exactly like Apply (G3a, OQ-D1), including the option validation; on a problem it stays
  with the message. The pane is rebuilt from the new policy.
- E2E: `Gpedit_Options_Elements` types into the first policy, presses Next, checks the new controls and defaults, presses
  Previous and checks the applied value came back.

## 17. Strings

Failure mode: a resource entry is missing and the message shows the key name; text of classic is copied into code.

- Handling: `OptionsStrings` returns the key name when an entry is missing, so a blank message shows in review. All messages
  are in `Strings/en-US/Options.resw`; `npm run validate` regenerates `string-parity.json`.
- E2E: the validation test compares the message caption and text with the expected resource text.

## 18. Findings of the first E2E runs

Run 1 (before any product code, the tests compiled): all 11 `Gpedit_Options_*` tests failed, because the pane had no controls.
Run 2 (first product code): 6 passed (Elements, Defaults, Numbers, Literal, TabOrder, Navigation), 5 failed. Run 3 (after the
fixes below): 11 of 11 passed, 252 checks, none failed. What the runs showed:

- Tab through a tunneling `PreviewKeyDown` on the dialog root works: the base handler does not move the focus a second time.
- Layout: a canvas that is shorter than the scroll viewer is centered in it (the first label was 94 DIP down). The pane canvas is
  aligned top left.
- Dirty at open: a check box and a drop-down report their change synchronously while the pane loads, a text box reports it late.
  A flag set by the controls made Apply enabled at open. The dialog now compares the values with the values taken after the load
  (or the last Apply), so only a real difference enables Apply, and a typed value that is typed back counts as no change.
- An editable combo box handles Enter and Escape itself, so the dialog base never saw them and Escape no longer closed the
  dialog. With a closed list, the dialog takes Enter as OK and Escape as Cancel (as classic does for an edit control).
  Focus given to the combo box by automation is passed on to its text box.
- Additive list: the seed of the first draft stored the entries under the names 1, 2, 3, which no list with no `valuePrefix` does
  (the engine names each entry after its text). The engine then wrote the entries again under their own names. The seed now
  uses the engine's names; the check expects the removed entry as `bravo=bravo`. A list with a `valuePrefix` is not driven
  by an E2E check (no such list in the fixture): recorded as a gap.
- The base dialog lays out every control it has created, once, when it is shown, from the template rectangle. The pane lays itself
  out again in `OnDialogLoaded` and after every resize, so the base layout never remains. The base keeps the controls of earlier
  policies in its list (no tab stops, so no effect except a few kilobytes per move).
- Gaps (no fixture element drives them): a required list (the message of `ListBoxControl.Validate`), a list with a
  `valuePrefix`, a `noSort` combo box whose document order is not alphabetic, and a text box longer than its limit pasted at once.

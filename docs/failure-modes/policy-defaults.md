# Option mapping, policy defaults and the group B and C replay: failure modes

Scope: piece E6b of release 0.2.0, written before the code (dispatch T; dispatch C writes `PolicyDefaults.For` and fixes the
rules the new rows show). It extends `docs/failure-modes/policy-save.md` (the save, the replay harness, rows R1 to R19) with
three things:

- **O**: the mapping of the display values of a capture (`options` of an `editPolicy` operation) to element values, in
  `tests/Bwc.Policy.Tests/Golden/GoldenOptions.cs`.
- **F**: the defaults function `PolicyDefaults.For` in `src/Bwc.Policy/Model/PolicyDefaults.cs`: the values classic's dialog
  shows after Enabled was chosen and before anything was typed.
- **B**: the replay of groups B (every fixture policy in every state, 72 cases) and C (sequences, 37 cases).

A capture is the specification. Where this page says what classic does, the named case shows it. Capture text is scrubbed:
a string that is not a fixture string, typed by the case or a label is stored as `#` plus the first 12 hex digits of its
SHA-256 (`tools/Bwc.Oracle.Capture/Gpedit/Matrix/Scrubber.cs`), so a default such as a text default is compared by its hash.

The contract of `PolicyDefaults.For(policy)`: it returns, by element id, the value the dialog shows for each presentation
control that has an element, when the policy has just been switched to Enabled. An element that shows nothing selected (an
enum with no default item) has no entry. It never reads stored values and never changes the policy. A policy with no
presentation, or no element, gives an empty dictionary.

## O. Option mapping

| Id | Way it goes wrong | Expected | Case, test |
|---|---|---|---|
| O1 | An option has no `elementId`, or the id is not an element of the policy. | The row fails naming the operation and the id. | `GoldenOptionsTests.O1_...` |
| O2 | The recorded `expectKind` is not the kind of the element's control (a `textBox` option for a combo box, a `decimalTextBox` for a long decimal element). The wrong element would be edited silently. | The row fails naming both kinds. A `spinner` is a number box with spin buttons and counts as `decimalTextBox` or `longDecimalTextBox`. | `GoldenOptionsTests.O2_...` |
| O3 | An option carries no value member, or more than one of `text`, `checked`, `item`, `itemIndex`, `lines`, `rows` and `clearRows`. | The row fails: the mapping cannot know which one was meant. | `GoldenOptionsTests.O3_...` |
| O4 | An option has a member the mapping does not know (a new driver field). It would be ignored and the replay would pass for the wrong reason. | The row fails naming the member. The known members are `label`, `expectKind`, `elementId`, `index`, `typed` and the value members of O3. | `GoldenOptionsTests.O4_...` |
| O5 | An enum item label matches no item, matches by another case, or matches two items. | No match or two matches fails with the label and the item names. The match is exact on the resolved display name (an ampersand is a literal ampersand: `B/Pol_Amp_LabelSingle`, `Tom & Jerry`). | `GoldenOptionsTests.O5_...` |
| O6 | An enum option names `itemIndex` (a position in the displayed, possibly sorted list). No capture uses it and the position depends on the sort. | The row fails: not supported. | `GoldenOptionsTests.O6_...` |
| O7 | A number box text is not digits (a sign, a letter, a separator, a blank, empty, a value above 18446744073709551615). | It is a refusal of the dialog, not an error of the harness: the number is not mapped, and the row then needs classic to have refused too (a recorded message box, or no save). Digits with leading zeros map to the number. | `GoldenOptionsTests.O7_...`; E group in E6c |
| O8 | A long decimal above 4294967295 is treated as a decimal, or a decimal above 4294967295 is cut to 32 bits. | The mapping keeps the full 64-bit number; the validator, not the mapping, finds an out-of-range value (`AboveMaximum`). | `GoldenOptionsTests.O8_...`; `B/Pol_LongDecimal_Basic` |
| O9 | A combo box `item` is not one of the suggestions of its control, or a text box is given `item`. | The row fails. A combo box accepts `text` (typed) and `item` (a suggestion); a text box accepts `text` only. | `GoldenOptionsTests.O9_...`; `B/Pol_Text_ComboBoxNoSortRequired` |
| O10 | A check box option has no boolean `checked`, or has `text`. | The row fails. | `GoldenOptionsTests.O3_...` |
| O11 | A multi-text option is given as one string with line breaks, or its `lines` hold a non-string. | The row fails. The lines are taken as given; one empty line is an empty value (`lines: [""]` is what classic shows for an empty box). | `GoldenOptionsTests.O11_...`; `B/Pol_MultiText_Basic` |
| O12 | A list option with `clearRows` false or absent replaces the rows instead of adding to the rows already shown, or `clearRows` true keeps old rows. | `clearRows: true` starts from no rows; otherwise the rows are added after the rows the dialog shows (the stored rows of an Enabled policy, or none). `clearRows` with no `rows` is an empty list. | `GoldenOptionsTests.O12_...`; `B/Pol_List_Plain` |
| O13 | A row of an explicit-value list has a number of cells other than 2, or a row of another list has a number other than 1. | The row fails. | `GoldenOptionsTests.O13_...` |
| O14 | An additive list gets no `Removed` entries: values the user cleared stay in the key, because an additive list keeps every foreign value. | The mapping fills `ListValue.Removed` with the stored rows that are no longer present (by name for an explicit list, by data otherwise), as the list field of the dialog does. | `GoldenOptionsTests.O14_...`; `B/Pol_List_Additive` |
| O15 | Two options name the same element in one operation. | The row fails: the dialog has one control per element. | `GoldenOptionsTests.O15_...` |
| O16 | An option names an element that has no control in the presentation, or the policy has no presentation. | The row fails: a person cannot set it. | `GoldenOptionsTests.O16_...` |
| O17 | Options come with the state Disabled or Not Configured. | The row fails: the dialog disables the options then. | `GoldenOptionsTests.O17_...` |

## F. Defaults

The values below are what the captures show in `afterSet.options` of the first step of a case: an `editPolicy` with state
Enabled and no options, on a policy that was not Enabled before (`before.state`). The data-driven test
`PolicyDefaultsTests.For_EqualsWhatClassicsDialogShowed` reads every capture of every group, so a policy that only a case of
another group enables is checked too.

| Id | Way it goes wrong | Expected | Case |
|---|---|---|---|
| F1 | A check box ignores `defaultChecked`, or takes it from the true or false value. | Checked only when `defaultChecked` is true; else unchecked. | `B/Pol_Bool_StringValues` (checked), `B/Pol_Bool_NoFalseValue` (unchecked) |
| F2 | A number with no `defaultValue` shows its minimum (the dialog code today shows 0) or 0. | Classic shows 1 for a number with no default and no minimum. | `B/Pol_Decimal_Required`, `B/Pol_LongDecimal_RequiredSoftText` |
| F3 | A number with a `defaultValue` shows another number, or a long number is cut to 32 bits. | The default as written. | `B/Pol_Decimal_MinMax` (25), `B/Pol_LongDecimal_Basic` (123456789012), `B/Pol_Decimal_StoreAsText` (8080), `B/Pol_Both_WithElements` (42) |
| F4 | A default that lies outside the minimum and maximum is shown clamped or unclamped, whichever the code assumes. | Not decided: the fixture has no such default. The test names the gap and does not guess. | none |
| F5 | A text box ignores its default or shows it truncated to the maximum length. | The `defaultValue` text, else empty. | `B/Pol_Text_Plain`, `B/Pol_Text_Expandable`, `B/Pol_Value_NoValueName_ElementsOnly` (compared by hash), `B/Pol_Text_MaxLength` (empty) |
| F6 | A combo box shows the first suggestion, an empty box, or the default as a suggestion index. | The `default` text of the control, else empty. The suggestion order (sorted or `noSort`) does not change the text. | `B/Pol_Text_ComboBoxSuggestions` (Oslo), `B/Pol_Text_ComboBoxNoSortRequired` (empty) |
| F7 | An enum with no `defaultItem` selects item 0 (the dialog code today does). | Nothing is selected: the element has no entry, and classic then refuses OK with a message box. | `B/Pol_Enum_DecimalItems`, `B/Pol_Enum_StringItems`, `B/Pol_Enum_ItemValueList`, `B/Pol_Enum_Required` |
| F8 | An enum with `defaultItem` selects a place of the sorted list instead of the item of the template. | `defaultItem` is the index of the item in the template; the value is that item (the displayed position follows the sort). | `B/Pol_Enum_NoSortDefaultItem` (item 1, Alpha) |
| F9 | A `defaultItem` past the last item selects an item or throws. | Not decided: no capture. The function selects nothing. | none |
| F10 | A multi-text shows lines from `defaultHeight` or text. | An empty value (one empty line in the capture). | `B/Pol_MultiText_Basic`, `B/Pol_MultiText_Limits` |
| F11 | A list shows rows. | No rows. Classic then refuses OK (D8). | every `B/Pol_List_*` and `B/Pol_SharedList_*` |
| F12 | A static text control gets a value, or an element with no control gets one. | Only a control that has an element gives an entry; an element with no control has none. | `B/Pol_Amp_LabelSingle` (text, check, number, enum) |
| F13 | The presentation is missing. | An empty dictionary. | `B/Pol_Class_Both` and every policy with no element |
| F14 | The function reads the policy's stored values, mutates the policy or its presentation, or returns a dictionary a caller can break by writing. | A new dictionary on every call, same content every time, no state. | `PolicyDefaultsTests.For_IsPureAndFresh` |
| F15 | The test compares a capture made while the policy was already Enabled: the dialog showed stored values, not defaults. | Only an operation whose `before.state` is not Enabled counts. | `PolicyDefaultsTests` |
| F16 | Two captures of one policy disagree about its defaults. | The test fails and names both cases: a capture is damaged or the dialog is not deterministic. | `PolicyDefaultsTests` |
| F17 | The options of a capture and the controls of the presentation differ in number or kind. | The test fails naming the policy. Kinds are compared by the hashes of the driver names (`textBox`, `staticText`, `spinner`, `checkBox`, `dropdownList`, `comboBox`, `multiText`, `list`). | `PolicyDefaultsTests` |
| F18 | A hashed capture text is compared as plain text, or its hash is computed another way. | A captured text equals the engine text, or equals `#` plus the first 12 hex digits of the SHA-256 of the engine text (UTF-8). | `PolicyDefaultsTests`, `GoldenHarnessTests` |
| F19 | The data-driven test finds no policy, or too few, and passes empty. | At least 60 policies must be found; fewer fails. | `PolicyDefaultsTests` |
| F20 | The values have the wrong shape for their element (a number for a text element). | The validator would report `WrongValueKind`; the test checks the shape per element kind. | `PolicyDefaultsTests` |
| F21 | The function is not written yet. | `NotImplementedException`; every defaults test and every replay row that needs defaults fails with it. | all of F |

## B. Group B and C replay

Rows are added in `PolicyGoldenReplayTests` for the 72 cases of group B and the 37 cases of group C. R1 to R19 of
`policy-save.md` apply. Cases on `awaiting-recapture.txt` stay Inconclusive (R10): six of group B (the CAP-4 number box
cases).

| Id | Way it goes wrong | Expected | Case |
|---|---|---|---|
| B1 | A case folder of group B or C has no row, or a row has no folder. | `ReplayRowsCoverEveryCaseOfTheGroups` fails. | all |
| B2 | The press of a disabled Apply (the dialog did not press it) saves, or fails the harness because no change list was recorded. | A press recorded with `pressed: false` is skipped: no save, and the next snapshot must equal the one before. | `C/NoChange_Apply` |
| B3 | The defaults function is called for a policy with no elements, or for an element that already has an option value or a stored value. | It is called once, and only when some element of an Enabled edit has neither an option value nor (for an Enabled policy) a stored value. The result fills those elements. | `B/Pol_Value_Decimal` (no elements: never called), `B/Pol_Text_Plain` |
| B4 | The values the dialog shows are taken from the defaults when the policy is already Enabled, losing the stored values. | An Enabled policy shows its stored values first, defaults for the elements the read gave none. | `C/ReApply_SameValues`, `B/Pol_Decimal_Required` step 2 |
| B5 | The state the engine reads (`before.state`) differs from the dialog's. | R16. For group B this tests the reader on every shape. | D7 below |
| B6 | An Enabled save with an enum or required element left at its default is saved although classic showed a message box. | The engine reports a problem (`Required`) and writes nothing (R6). | `B/Pol_Enum_*` step 1, `B/Pol_Text_Required` step 1 |
| B7 | An Enabled list with no rows is saved (the key is cleared). | The engine reports a problem and writes nothing (D8). Expected to fail until E6b code. | `B/Pol_List_Plain` step 1 and 4 |
| B8 | Not Configured clears a lone `**del.` record of `Pol_Value_EnabledOnly`, or the reader shows it as Disabled. | For a policy with an enabled value only, a lone delete instruction reads as Not Configured and Not Configured writes nothing; for `Pol_Class_Machine` it reads as Disabled (D7). Expected to fail until E6b code. | `B/Pol_Value_EnabledOnly`, `B/Pol_Class_Machine` |
| B9 | An existing `<resources>` element of `comment.cmtx` does not get `minRequiredRevision="1.0"` (D6). | The attribute is added on the first save. Expected to fail until E6b code. | `C/Cmtx_FirstSave_ForeignNamespaces` |
| B10 | A comment-only save of an existing Registry.pol does not raise the version, or the last record of a side leaves the pair, instead of one space (D3, D4). | The version rises and the extension line keeps one space. Already settled by E6a code; the C rows must stay green. | `C/Comment_Enabled_AddEditRemove`, `C/Clear_LastSetting_*`, `C/GptIni_PairRemoval` |
| B11 | The file-rule tests of D2, D3, D4 and D6 for group C fail in isolation but pass in the replay (or the reverse), so the rule is placed in the wrong layer. | `GptIniGoldenTests` and `CmtxGoldenTests` carry one test per rule for a group C case. | `tests/Bwc.Gpo.Tests/Golden` |
| B12 | The harness hides a CAP-4 case that is not on the awaiting list. | R9: the row fails with the CAP-4 text; the case must be listed or recaptured. | any |
| B13 | `PolicySave.Save` writes a partly changed model when `PolicyStateWriter.Apply` returns problems inside the store edit. | Nothing is written, the comment is not set, the problems come back (S2 and S17 of `policy-save.md`). | `PolicySaveContractTests.S17_...` |

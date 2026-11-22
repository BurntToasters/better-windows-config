# Policy filter failure modes

Scope: piece E5 (part 1) of release 0.2.0, `src/Bwc.Policy/Filter`. The filter models classic's Filter Options dialog
(admtmpl.dll dialog 3500) as a predicate over one policy, its state snapshot and its comment, plus an "All Settings"
flattener. It is pure: no file I/O, no UI, no environment reads. The contract is `docs/contracts/policy-engine.md`.
This page was written before the code. Tests are `tests/Bwc.Policy.Tests/Filter*Tests.cs`.

Controls of the dialog (read from the local untracked dump, cited by id, never copied): Managed (3501), Configured
(3502) and Commented (3503) combo boxes with the values Yes, No and Any (`Oracle admtmpl.dll.mui:3000`, `:3001`,
`:3002`); `Enable Keyword Filters` (3504) with the text (3505), the match combo (3517; Any, All, Exact,
`Oracle admtmpl.dll.mui:3003` to `:3005`) and the Within check boxes Title (3506), Help Text (3507) and Comment (3508);
`Enable Requirements Filters` (3509) with a platform list (3511), Select All (3512), Clear All (3513) and the any or all
combo (3510; `Oracle admtmpl.dll.mui:3119`, `:3120`). Messages that stop OK are `:3121` (no platform selected), `:3122`
(no keyword text) and `:3123` (no Within box ticked). The status bar shows `:3124` (N of M shown) and `:3125`.

Every rule is tagged *confirmed* (the resources say so) or *verify against oracle*. The unconfirmed ones are one named
member of `src/Bwc.Policy/Filter/FilterRules.cs`, shown as `[Rule: Name]`, so a capture of group F (filter results)
corrects them in one place.

## Model

- `FilterOptions` holds the three tri-state values, the keyword filter (enabled, text, match mode, three Within flags)
  and the requirements filter (enabled, selected platforms, any or all). `Validate()` returns typed problems and the
  engine formats no text.
- `PolicyFilter` is built from valid options. `Matches(subject)` is true when every enabled part passes (logical AND).
  A disabled keyword or requirements part passes everything, whatever its fields hold (*confirmed*, the check boxes 3504
  and 3509 gate the groups).
- A subject is the policy, a snapshot of its state in one hive (or null), and the comment text (or null).
- A platform is a supported-on definition of the loaded set (key `namespace:name`, text from the ADML). The tree of
  products that classic draws from the `<products>` part of a definition is not parsed by the loader, so the platform
  list is flat.
- `AllSettings` lists every policy that applies to one side, once each, in source order. A class Both policy is listed
  under both sides, and once in each.

## A. Keyword filter

| ID | Case | Risk | Handling |
| --- | --- | --- | --- |
| A1 | Keyword filter enabled with an empty or blank text | The filter would match everything or nothing by accident | `Validate()` returns `KeywordTextMissing` (message 3122). `PolicyFilter` refuses to be built. A disabled keyword part with a blank text is valid. |
| A2 | Keyword filter enabled with no Within box ticked | Nothing can match | `Validate()` returns `KeywordWithinMissing` (message 3123). |
| A3 | Text is only whitespace or only separators | Zero words | Counts as empty (A1). |
| A4 | The keyword is only in the comment, and Within does not include Comment | A comment hit leaks into the result | The comment is searched only when the Comment box is ticked. A policy whose only hit is its comment does not match when Comment is off, and matches when it is on. |
| A5 | The keyword is in the comment and the subject has no comment (null or empty) | A null comment crashes | A null or empty comment is empty text. It never matches a word. |
| A6 | The policy has no help text (empty explain text) | A null or empty text crashes, or an empty text matches an empty word | Empty text matches nothing. The title and comment are still searched. |
| A7 | Case of the match | A search for `Firewall` misses `firewall` | Case-insensitive, `[Rule: KeywordIgnoresCase]` (*verify against oracle*). Culture-invariant ordinal comparison, so the Turkish dotless i does not change results. |
| A8 | Word splitting | `a  b` or tabs and new lines between words | Words are split on white space and empty entries dropped, `[Rule: KeywordWordsSplitOnWhiteSpace]` (*verify against oracle*). |
| A9 | A word inside a longer word (`work` in `network`) | Whole-word versus substring | Substring, `[Rule: KeywordMatchesSubstring]` (*verify against oracle*). |
| A10 | Match Any | One word is enough | True when at least one word is found in at least one ticked text. |
| A11 | Match All | Each word must be found, but may be in different texts | True when every word is found in some ticked text, `[Rule: KeywordAllMayUseDifferentTexts]` (*verify against oracle*). |
| A12 | Match Exact | Whole text as a phrase, punctuation, inner spaces | The trimmed text is searched as one phrase, with inner white space runs collapsed to one space in the phrase and in the searched texts, punctuation kept as written, `[Rule: KeywordExactIsPhrase]` and `[Rule: KeywordExactCollapsesWhiteSpace]` (*verify against oracle*). |
| A13 | A very long help text or a very long keyword | Time spent in the filter | Plain ordinal `IndexOf`, linear. The limit on an ADML string (`AdmxLimits.MaxStringLength`) bounds the text. A keyword longer than 4096 characters is refused by `Validate()` (`KeywordTooLong`). The limit is ours. |
| A14 | The help text has markup or a line break inside a word | The word is split by a line break | Searched as stored. Not normalized except in Exact (A12). |
| A15 | Surrogate pairs, combining marks | Ordinal compare treats them as units | Ordinal compare on UTF-16, no normalization, `[Rule: KeywordNoUnicodeNormalization]` (*verify against oracle*). |

## B. Managed, Configured and Commented

| ID | Case | Risk | Handling |
| --- | --- | --- | --- |
| B1 | Any | The part must not filter | Any matches every subject, including a subject with an unreadable state. |
| B2 | Managed Yes | What "managed" means | The policy key is `Software\Policies` or below, or `Software\Microsoft\Windows\CurrentVersion\Policies` or below, `[Rule: ManagedKeyRoots]` (*verify against oracle*). The key is compared ignoring case on a key boundary, so `Software\PoliciesX` is not managed. Managed No is the complement. |
| B3 | A key with a leading backslash, a trailing backslash or doubled backslashes | A sloppy template defeats the prefix test | The key is trimmed of leading and trailing backslashes before the test. Doubled backslashes are not repaired and the key is then unmanaged. |
| B4 | Configured Yes | Which states count | State Enabled or Disabled. Not Configured is No. |
| B5 | The state is unreadable (the snapshot is null, the file was malformed or the hive failed to read) | The policy is shown as Configured: No when nobody knows | Such a subject matches Any only, `[Rule: UnknownStateMatchesOnlyAny]` (*verify against oracle*, a UI decision). It is never Yes and never No. |
| B6 | Commented Yes | What counts as a comment | A comment with at least one non-white-space character. An empty or white-space comment is No, `[Rule: BlankCommentIsNoComment]` (*verify against oracle*). A null comment is No. |
| B7 | The comments file could not be read | Same as B5 for comments | The caller passes null. A null comment is No, so a failed read can hide a commented policy from Commented Yes. The UI shows its own read failure notice. |
| B8 | Class Both policy | Machine state used for the user side | The caller passes the state and comment of the side it is filtering. The predicate never looks at the other side. |

## C. Requirements filter

| ID | Case | Risk | Handling |
| --- | --- | --- | --- |
| C1 | Requirements filter enabled and no platform selected | Nothing matches | `Validate()` returns `PlatformsMissing` (message 3121). |
| C2 | A selected platform that the set no longer has | A stale selection after a template import or removal | The key stays in the options. It matches no policy. `PlatformList.Prune` drops keys the set does not define. |
| C3 | Match Any | At least one selected platform | The policy supported-on key is one of the selected keys. |
| C4 | Match All | All selected platforms | Every selected key is in the policy platform set. The loader links one supported-on definition to a policy, so the set holds one key, and All with two or more different selected keys matches nothing, `[Rule: RequirementsAllNeedsEverySelected]` (*verify against oracle*, because classic expands products). |
| C5 | A policy with no supported-on definition (none declared, or undefined) | Matches everything or nothing | Excluded when the requirements filter is on, `[Rule: NoSupportedOnFailsRequirements]` (*verify against oracle*). |
| C6 | Two supported-on definitions with the same display text in different files | The list shows a duplicate line | The key is namespace and name, so both are listed. The UI shows the text. The engine does not merge by text. |
| C7 | Namespace and name compared ignoring case | A reference spelled in another case | The key is compared ignoring case, as `TemplateSet` does. |
| C8 | Select All and Clear All | The dialog buttons | `PlatformList.All` returns every key, an empty selection is Clear All. |

## D. Aggregates and the All Settings node

| ID | Case | Risk | Handling |
| --- | --- | --- | --- |
| D1 | A category with zero matches | An empty list, a status of "0 of N" | `Count` returns shown 0 and total N. Whether the tree node stays visible is a UI rule. The engine returns the counts and `[Rule: EmptyCategoryStaysVisible]` is true (*verify against oracle*, group F). |
| D2 | Status counts | Shown versus total | Total is the number of subjects, shown the number that match. The status text is the UI's (messages 3124 to 3126). |
| D3 | A class Both policy | Counted twice in one list | The list of one side holds the policy once. Counts of the two sides are separate calls. |
| D4 | All Settings has the same policy under two categories | One policy has one category | Not possible: a policy has one category reference. A policy with no category (detached) is still listed in All Settings, which is classic's behavior for policies that the tree cannot place, `[Rule: AllSettingsIncludesUncategorized]` (*verify against oracle*). |
| D5 | Huge template sets (thousands of policies) | A filter run that blocks | `Apply` checks the token once per 256 subjects and throws `OperationCanceledException`, as the contract requires. Nothing is partly applied: a cancelled call returns nothing. |
| D6 | Filter built from options that change afterwards | A live options object mutates | `FilterOptions` is an immutable record. `PolicyFilter` copies the platform and word lists when built. |
| D7 | Null policy | Crash | `ArgumentNullException`. A null state or comment is allowed (B5, A5). |

## Blind review additions

Added by the blind test-first pass (tests in `tests/Bwc.Policy.Tests/Blind/FilterStoreBlindTests.cs`). Each item is a way the
filter could fail that the sections above do not state.

| ID | Case | Risk | Handling |
| --- | --- | --- | --- |
| E1 | Several invalid parts at once | Only the first problem is reported, so the dialog needs several OK presses | `Validate()` returns every problem: `KeywordTextMissing`, `KeywordWithinMissing` and `PlatformsMissing` can all be in one list. `TryCreate` returns false with the same list and a null filter. A valid option set gives true, a filter and no problems. |
| E2 | Keyword text with wildcard or pattern characters (`.*`, `?`, `%`, `[a]`) | A pattern engine treats them as syntax | The text is literal. A word `a.c` does not match `abc`. |
| E3 | Exact phrase over the border of two texts (end of the title plus start of the help text) | A phrase that exists in no single text matches | The phrase must lie inside one ticked text. |
| E4 | Match All with a word that is only in an unticked text | A hit in an unticked text counts | Only ticked texts count for every mode. |
| E5 | The same subject twice | A filter that keeps state answers differently the second time | `Matches` is pure: same answer each time and the subject is not changed. |
| E6 | `Apply` and `Count` agree | A status line that does not match the list | `Count(subjects).Shown` equals `Apply(subjects).Shown.Count`, `Total` equals the number of subjects, and `Apply` keeps the input order. An empty input gives 0 of 0. |
| E7 | All parts together | One part overrides another | Managed, Configured, Commented, keyword and requirements are combined with AND. |
| E8 | A null input sequence or null set | A crash with no typed reason | `Apply`, `Count` and `AllSettings.List` throw `ArgumentNullException`. |
| E9 | A culture that changes the case rules (Turkish) | The keyword `TITLE` misses `title` | The result is the same under `tr-TR` as under the invariant culture (A7). |
| E10 | A platform key written with other case or a policy whose definition has another case | A selected platform misses its policy | The key comparison ignores case (C7). A stale key beside a valid key in Any mode still matches through the valid key. |
| E11 | `AllSettings` order and sides | A reordered or duplicated list | Source order, one entry per policy, class Machine only on the Machine side, User only on the User side, Both on each. |

## F. Filter results of the captures (E6c)

`F/Filter_Options` sets 17 filters on the `Sort cases` node of the Computer side, after four saves (one Enabled, one
Disabled, one Not Configured with a comment, one Enabled with a comment), and reads the list after each one. The list shows
display names; a setting with no row in the capture (no `rows` member) means the node showed nothing. The test
`PolicyFilterGoldenTests` replays the four saves in a sandbox through `PolicySave`, builds the subjects from the state and
comment the engine reads, applies `PolicyFilter` with the options of each `setFilter` operation and compares the set of
display names (classic's list is sorted, the engine keeps source order).

| Id | Way it fails | Expected | Case, operation |
|---|---|---|---|
| FC1 | Configured Yes shows a policy that is Not Configured with a comment, or hides Disabled. | Yes: `Policy 2`, `alpha policy`, `Zulu sort case`. No: the other five. | operations 6 and 10 |
| FC2 | Commented counts by the state, or counts a Not Configured policy with a comment as no comment. | Yes: `Policy 10`, `Policy 2`. No: the other six. | operations 14 and 18 |
| FC3 | Managed Yes shows nothing (the fixture keys are below `Software\Policies`) or Managed No shows the fixture. | Yes: all eight. No: none. | operations 22 and 26 |
| FC4 | Two tri-state parts are not combined with AND. | Configured Yes and Commented Yes: `Policy 2`. | operation 30 |
| FC5 | A keyword is searched in a text that is not ticked. | `zulu` in title: `Zulu sort case`. `comment` in comment: `Policy 10`, `Policy 2`. `policy` in title: the seven policies with the word in the name, not `Zulu sort case`. | operations 34, 42, 46 |
| FC6 | `sort` in the help text shows every policy or none. | Every one of the eight shows (the help of every sort case policy holds the word). | operation 38 |
| FC7 | Match Any with two words, in title only. | `zulu alpha`: `alpha policy`, `Alpha Policy`, `Zulu sort case`. | operation 50 |
| FC8 | Match All finds words in different ticked texts. | `policy sort` in title and help: nothing shows, so the words must be found in one text (`KeywordAllMayUseDifferentTexts` is false in classic). | operation 54 |
| FC9 | Match Exact with a phrase that has a digit. | `Policy 2` exact in title: `Policy 2` only (not `Policy 10`, `Policy 1`). | operation 58 |
| FC10 | The requirements filter on with no platform is applied. | Classic shows a message box and keeps the list unfiltered (all eight). The options are invalid with `PlatformsMissing` and `TryCreate` returns no filter. | operation 62 |
| FC11 | The requirements filter with every platform selected. | Classic's platform list holds two products of the machine's own templates (hashed in the capture), none of them a supported-on definition of the fixture, so no fixture policy matched and the node was hidden. The engine's list is the definitions of the loaded set. The row selects two keys no fixture policy uses and expects nothing to show; the difference in the list is recorded in the audit. | operation 66 |
| FC12 | A filter that hides every policy of a category leaves a count the node does not match. | The `Basics` node under Configured Yes shows none (it was hidden in classic, operation 72); the engine counts 0 of the number of its policies. | operation 72 |
| FC13 | The All Settings captures hold results the test does not compare. | `F/AllSettings_Computer` and `F/AllSettings_User` record "untracked" for the status text and the rows (they depend on the machine's own templates), so there is no list to compare. The test checks the capture still says so and fails when it holds rows, so a recapture with rows must extend the test. | `F/AllSettings_*` |
| FC14 | The test passes with no `setFilter` operation read. | The case must hold the 17 `setFilter` operations. | `F/Filter_Options` |

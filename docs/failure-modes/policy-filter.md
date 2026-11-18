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

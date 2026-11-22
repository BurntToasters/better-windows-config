# Policy template (ADMX and ADML) failure modes

Scope: piece E2 of release 0.2.0, `src/Bwc.Policy/Admx`: the hardened reader, the ADMX parser, the ADML and presentation
parser, and the linker that turns one or more template folders into one immutable `TemplateSet`. The contract is
`docs/contracts/policy-engine.md` (section "ADMX and ADML parsing"). State reading and writing, the filter, the import
store and all UI are other pieces.

Rules that hold for every section below:

- The loader never throws for a bad template. A problem becomes a `TemplateFailure` (file path, line, position, reason
  code, detail). The rest of the set still loads. A file with a load failure contributes nothing to the set (section 35),
  as in classic, which shows none of the content of a file it refuses. The only exception that leaves the loader is
  `OperationCanceledException`, checked once per file.
- The parser never validates against the XSD. Elements and attributes it does not read are ignored without a failure.
- The engine never formats user text. A failure carries a code and the faulty identifier in `Detail`.
- Order is the order in the files: sources in the order given, files by file name (ordinal, ignoring case), then
  document order. The set keeps that order. Nothing is sorted for display.
- Names (namespaces, prefixes, category, policy, element and string ids) compare ordinal ignoring case (section 30).
- Real template text is never copied into a tracked file. Tests over the system folder assert counts and invariants only.

Failure codes used below: `FolderNotFound`, `ReparsePoint`, `FileTooLarge`, `FileNameTooLong`, `Unreadable`,
`MalformedXml`, `DtdProhibited`, `XmlTooDeep`, `NotATemplate`, `MissingAdml`, `DuplicateNamespace`, `NamespaceShadowed`,
`NameTooLong`, `StringTooLong`, `MissingAttribute`, `InvalidAttributeValue`, `DuplicatePolicy`, `DuplicateCategory`,
`DuplicateElement`, `DuplicateSupportedOn`, `DuplicateString`, `DuplicatePresentation`, `UndefinedString`,
`UndefinedPresentation`, `UndefinedPresentationRef`, `UndefinedUsing`, `UndefinedPrefix`, `UndefinedCategory`,
`MissingCategory`, `UndefinedSupportedOn`, `CategoryCycle`, `InternalError`, `TooManyFailures`.

## Files and folders

### 1. Malformed XML

A file that is not well formed (a missing end tag, a bad character, two root elements) fails with `MalformedXml`. The
failure holds the file path and the line and position that the XML reader reports. The ADMX or ADML is dropped as a
whole and, because the pair is unusable, so is its template file. Other files load. The fixture `bad/MalformedXml` is the
test.

### 2. DTD and entities

A `DOCTYPE` anywhere is refused: DTD processing is prohibited and no resolver is set, so no external entity, no
internal entity and no parameter entity is ever expanded or fetched. The failure code is `DtdProhibited` (not
`MalformedXml`). The line and position of the `DOCTYPE` are found by a text scan of the first 64 KiB and are 0 when
not found. The XML reader's own refusal carries no position, and the reader's refusal text is learned from the runtime
at start so the classification does not depend on the display language. The template is dropped as a whole. The fixture `bad/DtdEntity` is
the test. Classic's behaviour for the same file is recorded by oracle group H. Difference from classic is allowed here:
the app runs elevated and the files are untrusted input.

### 3. Oversized file

One constant, `AdmxLimits.MaxFileBytes` (16 MiB), is the size cap for an ADMX or an ADML. It is checked from the file
length before the file is opened. A larger file fails with `FileTooLarge` and is never read. The reader also sets the
XML character cap to the same constant, so a file that grows between the check and the read cannot exceed the cap by a
meaningful amount. The largest shipped file is under 2 MiB. A test pins the constant and loads a file of cap plus one
byte, which must fail without parsing.

### 4. Reparse points

A reparse point (junction, symbolic link, mount point) is refused when it is a template file, a source folder or a
language folder, and nothing is read through it. The code is `ReparsePoint` and the path is the reparse point itself.
A refused source folder yields no templates from that source. A refused language folder is reported once and is treated as
missing, so the loader tries the fallback language. A refused ADMX or ADML file drops that template. A test makes a
junction (no administrator rights needed). The file case is tested through the attribute check, because a file symbolic
link needs a privilege the test must not use.

### 5. Missing source folder

A source folder that does not exist fails with `FolderNotFound`. The set still loads from the other folders. The caller
decides whether to pass a folder that may be absent (the import store is passed only when it exists).

### 6. Unreadable file

Any I/O error while checking or reading a file (sharing violation, access denied, a path that vanished) fails with
`Unreadable` and the HRESULT in `Detail`. It never escapes as an exception. Any other unexpected error while loading one
file is `InternalError` with the exception type name in `Detail`, and that file is dropped.

### 7. File name too long

A template file name longer than `AdmxLimits.MaxFileNameLength` (255 characters) fails with `FileNameTooLong` and the file
is skipped. The file system already prevents it, so the check is a guard against odd sources. Files whose extension is
not `.admx` (ignoring case) are not templates and are skipped silently. Sub-folders other than the language folder are
not read.

### 8. Deeply nested XML

An element nesting deeper than `AdmxLimits.MaxXmlDepth` (100) fails with `XmlTooDeep`, so a crafted file cannot exhaust
the stack of any code that walks the tree. Shipped files nest less than 15 levels.

### 9. Not a template

A well formed ADMX whose root is not `policyDefinitions`, or that has no `policyNamespaces/target` with both a
namespace and a prefix, fails with `NotATemplate` (`Detail` names what is missing) and is dropped. An ADML whose root is not `policyDefinitionResources` fails the same way.

## ADML and languages

### 10. Missing ADML

An ADMX with no ADML in the requested language folder and none in `en-US` fails with `MissingAdml`. `FilePath` is the
`en-US` ADML path that was expected and `Detail` is the ADMX path. The template is dropped: without strings no display
name exists. The fixture `bad/MissingAdml` is the test.

### 11. Language falls back to en-US

The ADML is looked up in `<folder>\<culture>\<name>.adml` for the requested UI culture, then in
`<folder>\en-US\<name>.adml`. The fallback is per file, so one folder can mix languages. The language that was used is
recorded on the template file. The fallback is silent (no failure). A null or empty culture means `en-US`. An ADML in
the requested language that is itself bad is not replaced by the `en-US` one: the template fails with the bad file's
failure.

### 12. Undefined string

A `$(string.id)` that the ADML does not define fails with `UndefinedString` and `Detail` is the id. The whole file
is rejected (section 35): it adds no category, policy or supported-on definition. While the file is being read a text
that was not found still takes a fallback (a display name falls back to the item's own name, every other text to an
empty string), but that content is discarded with the file. The fixture `bad/UndefinedString` is the test.

### 13. Undefined presentation

A policy `presentation="$(presentation.id)"` that the ADML does not define fails with `UndefinedPresentation` and
`Detail` is the id. The file is rejected (section 35). A `presentation` value that is not of that form fails
the same way. A policy with elements and no `presentation` attribute has no presentation and no failure. A control whose
`refId` names no element of the policy fails with `UndefinedPresentationRef`, and the file is rejected (section 35).
Elements with no control are kept. Controls keep the order of the ADML.

### 14. Duplicate string or presentation id

Within one ADML the first definition wins and each later one fails with `DuplicateString` or `DuplicatePresentation`.

### 15. String lengths over the limit

One ADML string longer than `AdmxLimits.MaxStringLength` (1,048,576 characters) is not stored. It fails with
`StringTooLong` and is then handled as an undefined string. Names have their own limits (section 16).

## Names and references

### 16. Name and key length limits

All limits are constants in `AdmxLimits`: namespace, prefix, category name, policy name and element id at most
255 characters; a registry key path at most 32,767 characters with each component at most 255; a value name at most
16,383. A value over its limit fails with `NameTooLong` and `Detail` names the attribute (`name`, `key`, `valueName`,
`id`, `namespace`, `prefix`). A category, policy or element with such a value is dropped, and a policy with a dropped
element is dropped as a whole, because a policy that writes only some of its values would write a wrong state. Shipped
files stay below half of every limit.

### 17. Missing required attribute

A policy without `name`, `class` or `key`, a category without `name`, an element without `id`, a list item without
`valueName`, a definition without `name`, a `using` without prefix or namespace, a boolean element without `valueName`
or a list item without `key` (section 36) fails with `MissingAttribute` and `Detail` is the attribute. Every
`MissingAttribute` rejects the file (section 35). A missing `displayName` fails the same way.

### 18. Invalid attribute value

A `class` other than Machine, User or Both (ignoring case), a boolean attribute that is not true, false, 1 or 0, a number
that is not a non-negative integer fails with `InvalidAttributeValue` and `Detail` is the attribute name. A bad class,
a bad boolean or a bad number rejects the file (section 35).

### 19. Undefined `using` prefix

A reference `prefix:name` whose prefix is neither the file's own target prefix nor a `using` prefix fails with
`UndefinedPrefix` and `Detail` is the prefix. The reference stays unresolved (a category parent, a policy category, a
supported-on reference). A `using` whose namespace no loaded file defines fails with `UndefinedUsing` (the file
path is the file that declares it, `Detail` is the namespace) once per declaration. The fixture `bad/UndefinedUsing` is
the test. A dangling reference is a link-level failure and does not reject the file (section 35): two shipped client
files (`ServerManager`, `WindowsBackup`) declare a `using` for a server namespace that no client file defines, and
classic loads them.

### 20. Duplicate names

Inside one file, a second policy with the same name fails with `DuplicatePolicy`, a second category with
`DuplicateCategory`, a second element id of one policy with `DuplicateElement` (and the policy is dropped, see section 16
for the reason), a second supported-on definition with `DuplicateSupportedOn`. The file is rejected (section 35), so the first
occurrence does not stay either. The fixture `bad/DuplicatePolicy` is the test. A second file that defines an already loaded namespace fails with
`DuplicateNamespace` when both are in one source and with `NamespaceShadowed` when the earlier file is in an earlier source
(the system folder wins over the import store). That second file is dropped as a whole. Names are unique per namespace, so
two namespaces may define the same name.

### 21. Category parent cycle

After the parents are resolved, a cycle (including a category that names itself) fails with `CategoryCycle`. The loop is
cut at the member that comes first in source order: that category loses its parent and becomes detached. Detached
means it has a parent reference but no parent. It is not a root and is not reachable from the roots, so no consumer that walks
parents or children can loop. Categories that hang under it stay attached to it.

### 22. Parent in another file

`parentCategory ref="prefix:name"` resolves through the file's `using` prefixes to a category of the other file
(the fixture's `bwcbase:BwcOracle`). A reference without a prefix, or with the file's own prefix, means a category of the same
file. The other file may be loaded later in source order, so linking happens after every file is parsed. An unresolved parent
fails with `UndefinedCategory` and the category is detached. A category with no `parentCategory` is a root.

### 23. Policy whose category is missing

A policy whose `parentCategory` names a category that does not exist fails with `UndefinedCategory`, and a policy with no
`parentCategory` fails with `MissingCategory`. In both cases the policy stays in the policy list with no category, so
an all-settings view can still show it, and the tree never shows it. A policy in a detached category has a category and is
unreachable from the roots. The failure recorded for the category is the explanation.

### 24. Unresolved supported-on

A `supportedOn ref` that names no definition fails with `UndefinedSupportedOn`. The policy stays, with no definition and an
empty supported-on text. A policy with no `supportedOn` element has none and no failure. Product and range
definitions inside a definition are not read; only its display name is.

## Policy and element semantics

### 25. Policy class

Machine, User and Both (ignoring case). A policy of class Both stays once in the policy list and once in its category's
list, with `AppliesToMachine` and `AppliesToUser` both true. The UI shows it under both roots.

### 26. Keys and values

A policy's `key` and `valueName` are recorded as written. `valueName` may be absent (a policy that writes only its
elements or lists). An element's own `key` is recorded as written, and the element's effective key is its key when it has
one and the policy key otherwise. `enabledValue` and `disabledValue` are a decimal, a long decimal, a string or a delete
marker. `enabledList` and `disabledList` have a default key and items; an item's own `key` overrides the default key, and
the effective key of an item is its key, then the list default key, then the policy key. Values are stored as parsed;
what is written for Enabled and Disabled is the writer's job.

### 27. Element attributes

Every attribute that the contract names is recorded. Flags (`required`, `soft`, `storeAsText`, `expandable`, `additive`,
`explicitValue`) are false when absent. Numbers (`minValue`, `maxValue`, `maxLength`, `maxStrings`) and `valuePrefix`
stay null when absent and are never defaulted here (whether a minimum of 0 or a maximum of 9999 applies is decided by
the oracle, not by the parser):

- decimal and longDecimal: `minValue`, `maxValue`, `required`, `soft`, `storeAsText`.
- text: `required`, `expandable`, `maxLength`.
- multiText: `required`, `maxLength`, `maxStrings`.
- boolean: `trueValue`, `falseValue`, `trueList`, `falseList`.
- enum: `required`, and items with a display name, a value and an optional value list. An item `valueList` is a list of
  value items like `enabledList`, with its own default key.
- list: `additive`, `explicitValue`, `expandable`, `valuePrefix` (absent is different from empty), `valueName`.
- every element: `id`, `key`, `valueName`, `clientExtension`.

An element kind the parser does not know is ignored (a future kind). `comboBox` is not an element kind: it is a
presentation control over a text element.

### 28. Presentation controls

The controls of a presentation are read in order: `text`, `checkBox`, `textBox`, `decimalTextBox`, `longDecimalTextBox`,
`comboBox`, `dropdownList`, `listBox`, `multiTextBox`. A control keeps its `refId` and its label (the control's own
text, or a child `label`), the default (`defaultValue`, a child `defaultValue` or `default`), and the attributes
`defaultChecked`, `spin`, `spinStep`, `noSort`, `defaultItem`, `required`, `defaultHeight`, plus the `suggestion` children of a
comboBox. Each attribute is read only for the kinds that use it (`defaultChecked` for a check box, `spin` and `spinStep`
for number boxes, `noSort` for combo and drop-down, `defaultItem` for a drop-down, `required` for a list box,
`defaultHeight` for a multi-line box), so a stray attribute on another kind is never even parsed. Shipped files use `dropdownList@oSort`, `checkBox@defaultItem` and `listBox@required`: all are ignored or kept without a failure,
never an error. An unknown control kind is ignored.

### 29. Text details

- A literal `&` in a label, a display name or an item name stays an `&`. The ADML holds `&amp;`, the XML reader returns
  `&`, and the engine does not turn it into a mnemonic. `&&` stays `&&`. The UI decides how to draw it.
- Explain text keeps its newlines and leading and trailing blanks exactly as the XML reader returns them (line breaks are
  normalized to `\n` by XML). Nothing is trimmed, wrapped or reflowed. An explain text that is an empty string is an empty
  string, and an omitted `explainText` attribute is an empty string too, with no failure.
- Attribute values are not trimmed.

### 30. Case

Namespace, prefix, category, policy, element, string and presentation names compare ordinal ignoring case. The case written
in the file is kept for display and for the identifier. Names that differ only in case are duplicates.

## Set-level

### 31. Determinism

Loading the same folders twice gives the same file list, categories, policies, supported-on definitions and failures in
the same order. Files are read in parallel, but every result goes into a slot indexed by file order before the link
step, and the link step is sequential. A test loads twice and compares the full lists.

### 32. Load time with 222 files

A full system template folder (222 files, 3,403 policies, 416 categories, about 4 MB) must load fast enough for window
start. `SystemTemplatesInvariantTests` measures the load and fails above a limit that is about three times the time
measured on the development machine (the measured time and the limit are stated in the test). A cancelled token stops the
load at the next file.

### 33. Empty and unusual sets

No folders, or folders without templates, give an empty set with no failures (apart from a missing folder). A source
folder that holds an ADMX for a namespace twice (two file names) gives `DuplicateNamespace` for the later file. A set
loaded from the fixture folder gives zero failures, and a good template plus a bad one load together with only the bad one
failing and contributing nothing.

### 34. Dangling references in shipped templates

The shipped client templates carry references that point nowhere: supported-on definitions that no file defines, a
category that only its children name, and `using` declarations for server namespaces that a client does not install.
They are real and common (about 30 on a current client), so the loader records them as `UndefinedSupportedOn`,
`UndefinedCategory` and `UndefinedUsing` and keeps loading. A UI that mirrors classic must not show them as errors by
default; whether classic reports them is oracle group H. The system-folder invariant test therefore allows exactly the
reference codes (`UndefinedUsing`, `UndefinedPrefix`, `UndefinedCategory`, `MissingCategory`, `UndefinedSupportedOn`) and
fails on any other code, because a parse-level failure on a shipped file means the parser is stricter than classic.

## Strictness that classic enforces

### 35. A file with a load failure adds nothing

Classic refuses a template file that it cannot load and then shows none of that file's content. The loader does the same.
Every failure recorded while one template (its ADMX and its ADML) is read rejects that template: it adds no file, no
category, no policy and no supported-on definition to the set, so a file never leaks partial content (this also covers
the dropped items of sections 16, 17 and 18). The failures stay in `TemplateSet.Failures`. Other files load normally, and
only the rejected file's own failures are listed for it.

Which failures reject the file:

- Read-level (recorded while the ADMX and ADML are read): `MalformedXml`, `DtdProhibited`, `XmlTooDeep`, `FileTooLarge`,
  `FileNameTooLong`, `ReparsePoint`, `Unreadable`, `NotATemplate`, `MissingAdml`, `NameTooLong`, `StringTooLong`,
  `MissingAttribute`, `InvalidAttributeValue`, `DuplicatePolicy`, `DuplicateCategory`, `DuplicateElement`,
  `DuplicateSupportedOn`, `DuplicateString`, `DuplicatePresentation`, `UndefinedString`, `UndefinedPresentation`,
  `UndefinedPresentationRef`, `InternalError`, `TooManyFailures`.
- Set-level, which already drop the file: `DuplicateNamespace`, `NamespaceShadowed`.
- Link-level dangling references keep the file: `UndefinedUsing`, `UndefinedPrefix`, `UndefinedCategory`,
  `MissingCategory`, `UndefinedSupportedOn`, `CategoryCycle`. Shipped client templates carry them (section 34) and classic
  loads every shipped file, so rejecting them would remove real policies (`ServerManager` and `WindowsBackup` alone
  declare an undefined `using`). This differs from a literal reading of "any load failure" (which names `UndefinedUsing`);
  it is recorded as uncertain until oracle group H shows what classic does with a dangling reference.

How it can fail (each has a test):

- A file with a good policy before the bad one keeps that good policy, category or supported-on definition (partial
  content leaks). Test: a file with a good policy and a duplicate policy name adds nothing; the same for a file whose
  later policy has an undefined string (the policy used to stay with its name as the display name).
- A failed file drops a good sibling file, or its failures are attributed to the sibling. Test: good and bad files in one
  folder and in two folders; the good file's categories, policies and supported-on definitions are all there and no failure
  names the good file.
- A failed file's category or supported-on definition is still found by another file's reference. Test: a reference to
  it from a sibling gives `UndefinedCategory` and `UndefinedSupportedOn`, not a resolved link.
- A dangling reference rejects its file, or a clean file is rejected. Test: `bad/UndefinedUsing` keeps its file and policy;
  the good fixture folder has zero failures.
- The system folder suddenly fails or loses files. Test: the system-folder invariant test (only dangling reference codes)
  and a count test (every `.admx` of the folder is in `Files`).
- The result depends on file order or on parallel scheduling. Test: the load-twice test keeps passing.

### 36. List items and boolean elements need their attributes

Confirmed by classic captures on this machine:

- A list `item` with no `key` attribute, inside an `enabledList` that has no `defaultKey`, makes classic refuse the file:
  "Expected attribute 'key' not found." with the file, line 171 and column 37 of the capture.
- A `boolean` element with no `valueName` attribute (it had only `trueList` and `falseList`) makes classic refuse the
  file: "Expected attribute 'valueName' not found." with the file, line 242 and column 32 of the capture.
- When a file is refused, classic shows none of its content (section 35).

Rules:

1. An item of `enabledList` needs `key` unless the list has `defaultKey` (confirmed).
2. An item of `disabledList`, `trueList` and `falseList` follows the same rule (inferred, pending Group H capture: they
   are the same value-list shape and classic was only captured with `enabledList`). The `valueList` of an enum item is
   not in this rule.
3. A `boolean` element needs `valueName` (confirmed). Only an absent attribute fails: classic reported a missing
   attribute, so `valueName=""` is accepted.
4. Each is a `MissingAttribute` failure. `Detail` is the attribute name (`key` or `valueName`), `FilePath` is the ADMX, and
   `Line` and `Position` are the one-based line and column of the `>` that closes the start tag of the offending element
   (the `item` or the `boolean`). This convention is confirmed by the two classic captures: `        <item valueName="BothListOn">`
   (8 spaces and 29 characters) gave line 171, column 37, and `        <boolean id="ListCheck">` (8 spaces and 24 characters)
   gave line 242, column 32. A start tag that spans lines reports the line of its `>`, and a self-closing tag reports the
   `>` of its `/>`. Other failures keep reporting the position of the element name. The UI can show the position the way
   classic does.
5. The file is rejected (section 35).

How it can fail (each has a test):

- The rule is missed on one list kind: `enabledList`, `disabledList`, `trueList` or `falseList` (one test per kind).
- A `defaultKey` is honored on the wrong list: a `defaultKey` on `disabledList` does not rescue an item of `enabledList`;
  an element `key`, the policy `key` or a `defaultKey` of a list of another element does not rescue an item either.
- A list whose items all have `key`, or whose list has `defaultKey`, is wrongly refused (each kind).
- Several items miss `key`: one failure per bad item, in document order.
- The wrong attribute, file, line or column is named, for example the column of the element name instead of the `>`, or the
  line of the name when the start tag spans lines. Test: literal positions for the two bad fixtures (21:39 and 21:31), and
  positions computed from the file text for temporary files, including a start tag over two lines and a self-closing tag.
- A `boolean` with `valueName` is wrongly refused, or a `boolean` with no `valueName` is accepted (also with only
  `trueValue` and `falseValue`).
- Extra strictness: a decimal, text, list or enum element with no `valueName`, a policy with no `valueName`, an enum
  item `valueList` item with no `key`, and an `item` with a `key` but no `defaultKey` must all stay accepted, because no
  capture shows classic refusing them.
- Partial content leaks from the refused file, or a good sibling is dropped (section 35).
- The good fixture or the system folder starts to fail. Test: zero failures over the good fixture, and only dangling
  reference codes over the system folder.

The fixtures `bad/ListItemNoKey` and `bad/BooleanNoValueName` are the tests.

## Blind review additions

Added by the blind test-first pass (tests in `tests/Bwc.Policy.Tests/Blind/AdmxBlindTests.cs`). Each item is a way the loader
could fail that the sections above do not state.

### 37. Culture text that is not one safe folder name

The UI culture comes from the caller and names a folder (`<folder>\<culture>`). A culture such as `..\other`, a rooted path
or one that holds a separator must never make the loader read an ADML outside the template folder. The loader treats such a
culture as a language folder that is missing, so the template falls back to `en-US` (section 11). It never throws.

### 38. Failure per occurrence

Section 12 and section 14 name one failure per undefined or duplicated id. Two different undefined string ids in one file give
two `UndefinedString` failures (one per id, document order), and one id used twice gives one failure per use. A file with
several unrelated faults lists every one of them (none hides another), and all of them are attributed to that file.

### 39. DOCTYPE outside the scanned window

A `DOCTYPE` that starts after the first 64 KiB of the file (a long comment before it) is still `DtdProhibited`. Only its line
and position are 0 (section 2). It is never `MalformedXml` and never expanded.

### 40. Empty required attribute

An attribute that is present with an empty value (`name=""`) is not the same as an absent one (section 36 rule 3 says the
same for `valueName`). Section 17 names only the absent case. Whether an empty `name` is a `MissingAttribute` is open; the
test is marked open.

### 41. Extension, case and sub-folders of template files

A file named `X.ADMX` (upper case extension) is a template (section 7: ignoring case). `X.admx.bak`, `X.txt` and a `.admx` file
inside a sub-folder that is not the language folder are not read and give no failure. A `.admx` file that is a directory name
is not a template.

### 42. Several languages in one folder

An ADML that exists for the requested culture but not for one template of the folder is a per-file fallback (section 11).
The language recorded on each template file is the one that was used, so one set can hold files of two languages.

### 43. A stray invalid attribute on a control

A `spin`, `spinStep`, `noSort`, `defaultItem`, `defaultChecked`, `required` or `defaultHeight` attribute with a value that
could not be parsed, on a control kind that does not use it, never fails (section 28). The same attribute on the kind that
uses it follows section 18.

### 44. Property of the immutable set

`TemplateSet` lists are snapshots: `Files`, `Categories`, `Policies`, `SupportedOn` and `Failures` of one set are the same
objects on every access, and the `Children` of a category and `Policies` of a category list are in source order.

### 45. A template whose namespace differs only in case

Two files whose namespaces differ only in case are duplicates (section 30 and 20): `DuplicateNamespace` for the later file.

## Audit fixes (AF1)

### 46. A key that ends in a backslash (ADMX-5)

Windows ships three policies whose `key` ends in a backslash (`AppxPackageManager.admx`: `EnableMsixAllowedZones`,
`EnableMsixSmartScreenCheck`; `TerminalServer.admx`: `TS_RemoteDesktopVirtualGraphics`). The registry layer
(`PolDocument`) refuses a key with a trailing backslash, so a policy whose key kept it always read Not Configured and every
edit threw. The parser therefore trims every trailing backslash from each `key` it reads: the policy `key`, an element
`key`, a list item `key` and a list `defaultKey` (same shape, same defect). The trimmed text is what the model, the
effective keys and the length checks see. Section 26 "recorded as written" holds for everything else.

How it can fail (each has a test):

- A policy, element or list item key keeps its backslash, so the state reads Not Configured over a `Registry.pol` that holds
  the trimmed key, or an edit throws `ArgumentException`. Test: a template whose policy, element, item and default keys end
  in one or several backslashes loads with them trimmed; the state over a document with the trimmed keys is Enabled; an
  edit applies and writes the trimmed keys only.
- The `defaultKey` is missed, so an item that falls back to it keeps the backslash. Same test (an item with no own key).
- Any loaded policy, element or list item of the system folder has a key that ends in a backslash. Test: system folder
  invariant (no specific policy is named, CI has other templates).
- A key made only of backslashes becomes an empty key. For a policy that is a `MissingAttribute` for `key` (as an empty
  `key=""` already was); for an element or item it is an empty key as `key=""` already was. Not otherwise handled.

Open oracle question: a key with a leading backslash, or an empty middle segment (`A\\B`), is left unchanged and still
fails in `PolDocument`. What classic does with it is not captured. The capture cases for the trailing and the leading
backslash are in matrix group K, per fixture `KeyTrailingBackslash` and `KeyLeadingBackslash`: `K/<Name>_Enable` (the bytes
classic writes), `K/<Name>_ReadTrimmed` and `K/<Name>_ReadAsWritten` (whether classic reads the state from a record under the
trimmed key and from one under the key as written). They are in the plan and not yet captured. An empty middle segment has
no case.

### 47. Failure flood from one file (ADMX-1)

Failures of one file are capped at `AdmxLimits.MaxFailuresPerFile` (100). The first 100 are recorded as usual. When the
file records one more, the sink adds one final entry with `TooManyFailures` (file path, no line, `Detail` is the cap) and
drops everything after it, so a file holds at most 101 failures. A file with any failure is rejected (section 35), so
nothing is lost but the later messages. The text of the ADMX, which is needed to place a missing-attribute failure at the
`>` of its start tag (section 36), is read at most once per file for that purpose, through the same name length, reparse
point and size checks as the XML read, never by a second unchecked open of the path. Once the cap is reached no text is
scanned.

How it can fail (each has a test):

- A template with 10,000 faulty items (each missing `key` or `valueName`) takes seconds or gigabytes. Test: it loads
  within a stated limit, records at most the cap plus the final entry, and the text reader seam is called at most once.
- The final entry is missing or repeated, or the cap lets a flood of another code through. Test: exactly one
  `TooManyFailures`, and it comes last.
- A file with fewer failures than the cap gets a `TooManyFailures` entry, or loses a failure. Test: the existing
  per-failure position tests keep passing.
- The one text read skips the checks (a reparse point is followed, or a file over the size cap is read). Test: the text
  reader refuses the same files `SafeXml.Load` refuses.

### 48. Unknown attributes count as a link (ADMX-2)

`SafeXml.IsReparsePointPath` agrees with the other two path checks of the solution (`GpoPathChecks`,
`TemplateStoreChecks`): attributes that exist but cannot be read count as a reparse point, because a path that cannot be
inspected cannot be shown not to be a link. A path that does not exist (file or directory not found) is still not a
link: the loader reports a missing path as missing (`FolderNotFound`, `MissingAdml`, a language folder that falls back),
never as a link. Callers check existence first (`Directory.Exists`, `File.Exists`), so only a path that exists and
cannot be read is refused, as `ReparsePoint`.

How it can fail (each has a test, through an attribute-read seam because a real unreadable path needs elevation):

- Access denied, a sharing violation or any other I/O error while reading the attributes returns "not a link".
- A missing file or missing directory returns "link" and a missing language folder or ADML becomes a `ReparsePoint`
  failure instead of a fallback.

### 49. Comment drift about dropped items (ADMX-3)

The parser class comment said that a text or reference fault is recorded and the item stays. The loader rejects the whole
file when any failure was recorded while it was read (section 35), so the item does not stay. The comment now says the
failure is recorded and the file is rejected by the loader. Documentation only.

### 50. Letter case of `class` and boolean attributes (ADMX-4, open oracle question)

`class` (Machine, User, Both) and the boolean attributes (`required`, `soft`, `storeAsText`, `expandable`, `additive`,
`explicitValue`) are accepted in any letter case, and boolean values also with surrounding blanks. What classic accepts
is not captured: it may refuse `class="machine"` or `required="True"`. Until a capture exists the parser stays lenient.
No code change. The capture cases are `H/ClassLowerCase` (one policy with `class="machine"`) and `H/BoolAttributeUpper` (one
`decimal` element with `required="TRUE"`) and `H/BoolAttributeMixed` (one `decimal` element with `soft="True"`); they are in
the plan and not yet captured.

## Group H captures (E6c)

### 51. What classic does with each bad template (H/*)

Each H case installs one bad fixture of `docs/parity/oracle/gpedit/fixture/bad` next to the good fixture and records the
message boxes at start and the tree. The captures show no message text (it is hashed): they show whether a box appeared,
whether the bad template's category is in the tree, whether its policy can be opened, and that the good fixture is still
there with its seven categories. Tests: `PolicyTemplateGoldenTests` in `tests/Bwc.Policy.Tests/Golden`.

| Case | Classic | Expected in the loader |
|---|---|---|
| `H/MalformedXml`, `H/DtdEntity`, `H/MissingAdml`, `H/DuplicatePolicy`, `H/UndefinedString`, `H/ListItemNoKey`, `H/BooleanNoValueName` | One message box at start; the category of the bad template is not in the tree; selecting it, listing it and opening its policy fail. | At least one `TemplateFailure` whose file is in the bad fixture; no file, category, policy or supported-on definition of its namespace in the set. |
| `H/ClassLowerCase`, `H/BoolAttributeUpper`, `H/BoolAttributeMixed` | The same: one message box and nothing of the template shown. This answers section 50: classic refuses `class="machine"`, `required="TRUE"` and `soft="True"`. | As above. The parser is lenient today (section 50), so these three rows fail until the C dispatch makes the letter case strict and rewrites section 50. |
| `H/UndefinedUsing` | No message box; the category `Bad fixture UndefinedUsing` is in the tree and its policy opens with its text and supported-on text. | No file is rejected; the category and the policy are in the set (section 35, link-level failures keep the file). |

How the loader can fail against them:

| Id | Way it fails | Expected |
|---|---|---|
| H1 | A bad template leaks part of its content (a category without the policy, a policy without its category) into the set. | Nothing of the namespace. Test per case. |
| H2 | The good fixture is hidden or changed because a bad one sits in the next folder. | The good fixture's file, categories and policies are the same as when it loads alone (classic: `BWC Oracle` shows its seven categories in every H case). This is the BWC rule "one bad template does not hide the rest". The captures show classic does the same for these eleven; the inventory has no DIFF row for it, so it is not a declared difference. |
| H3 | A failure of the bad fixture is attributed to the good fixture, or the failures name a file outside the bad folder. | Every failure's file is under the bad fixture's folder. |
| H4 | Classic's one box per bad template becomes several failures reported as several problems. | One bad template gives failures from one folder; the UI groups them (G6). The test checks the folder, not the count. |
| H5 | The root of Administrative Templates shows the category of a bad template. | The root categories of the set are the good fixture's root only (classic: `BWC Oracle`), for the ten refused templates. |
| H6 | `H/UndefinedUsing` is rejected, or its policy loses the display name or the supported-on text. | The category, the policy `Bad fixture policy` and the supported-on text `BWC oracle bad fixture` are in the set. |
| H7 | A capture is missing, incomplete or answers differently after a recapture and the test passes. | The test reads every H case from the capture (the dialog count at start, the tree children, whether the bad category could be selected) and fails when the set of refused templates is not the ten named above. |

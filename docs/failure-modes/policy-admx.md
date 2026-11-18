# Policy template (ADMX and ADML) failure modes

Scope: piece E2 of release 0.2.0, `src/Bwc.Policy/Admx`: the hardened reader, the ADMX parser, the ADML and presentation
parser, and the linker that turns one or more template folders into one immutable `TemplateSet`. The contract is
`docs/contracts/policy-engine.md` (section "ADMX and ADML parsing"). State reading and writing, the filter, the import
store and all UI are other pieces.

Rules that hold for every section below:

- The loader never throws for a bad template. A problem becomes a `TemplateFailure` (file path, line, position, reason
  code, detail). The rest of the set still loads. The only exception that leaves the loader is
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
`MissingCategory`, `UndefinedSupportedOn`, `CategoryCycle`, `InternalError`.

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

A `$(string.id)` that the ADML does not define fails with `UndefinedString` and `Detail` is the id. The item keeps
loading. A text that was not found takes a fallback: a display name falls back to the item's own name, every other text
(explain text, supported-on text, enum item names) falls back to an empty string. The fixture `bad/UndefinedString` is the
test. Whether classic drops the policy is oracle group H.

### 13. Undefined presentation

A policy `presentation="$(presentation.id)"` that the ADML does not define fails with `UndefinedPresentation` and
`Detail` is the id. The policy keeps loading with no presentation. A `presentation` value that is not of that form fails
the same way. A policy with elements and no `presentation` attribute has no presentation and no failure. A control whose
`refId` names no element of the policy fails with `UndefinedPresentationRef` and stays in the list with no element.
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
`valueName`, a definition without `name`, or a `using` without prefix or namespace fails with `MissingAttribute` and `Detail` is the attribute.
The item is dropped (a policy with a bad element is dropped as a whole). A missing `displayName` fails the same way but
the item stays, with the name as its display name.

### 18. Invalid attribute value

A `class` other than Machine, User or Both (ignoring case), a boolean attribute that is not true, false, 1 or 0, a number
that is not a non-negative integer fails with `InvalidAttributeValue` and `Detail` is the attribute name. A bad class drops
the policy. A bad boolean or number is ignored as if absent and the item stays.

### 19. Undefined `using` prefix

A reference `prefix:name` whose prefix is neither the file's own target prefix nor a `using` prefix fails with
`UndefinedPrefix` and `Detail` is the prefix. The reference stays unresolved (a category parent, a policy category, a
supported-on reference). A `using` whose namespace no loaded file defines fails with `UndefinedUsing` (the file
path is the file that declares it, `Detail` is the namespace) once per declaration. The fixture `bad/UndefinedUsing` is
the test. The file itself stays loaded.

### 20. Duplicate names

Inside one file, a second policy with the same name fails with `DuplicatePolicy`, a second category with
`DuplicateCategory`, a second element id of one policy with `DuplicateElement` (and the policy is dropped, see section 16
for the reason), a second supported-on definition with `DuplicateSupportedOn`. The first occurrence stays, the second is
dropped. The fixture `bad/DuplicatePolicy` is the test. A second file that defines an already loaded namespace fails with
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
failing.

### 34. Dangling references in shipped templates

The shipped client templates carry references that point nowhere: supported-on definitions that no file defines, a
category that only its children name, and `using` declarations for server namespaces that a client does not install.
They are real and common (about 30 on a current client), so the loader records them as `UndefinedSupportedOn`,
`UndefinedCategory` and `UndefinedUsing` and keeps loading. A UI that mirrors classic must not show them as errors by
default; whether classic reports them is oracle group H. The system-folder invariant test therefore allows exactly the
reference codes (`UndefinedUsing`, `UndefinedPrefix`, `UndefinedCategory`, `MissingCategory`, `UndefinedSupportedOn`) and
fails on any other code, because a parse-level failure on a shipped file means the parser is stricter than classic.

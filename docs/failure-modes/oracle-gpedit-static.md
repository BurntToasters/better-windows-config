# Static gpedit oracle failure modes

Release 0.2.0 clones classic `gpedit.msc`. This document covers the static half of its oracle: the part that reads
files and needs no administrator rights and no GUI. It is made of `tools/Bwc.Oracle.Resources --tool gpedit`
(native resources, WinForms resources and `gpedit.msc`), `scripts/oracle.js --tool gpedit` (the `resources` step)
and `scripts/check-regedit-strings.js` (per tool string parity). The live capture (`Bwc.Oracle.Capture gpedit ...`)
is covered elsewhere. The existing regedit oracle is described in `docs/failure-modes/oracle.md`.

The static run reads only. It never starts `gpedit.msc`, `mmc.exe` or any Microsoft binary, never loads an
assembly for execution, never writes outside `docs/parity/oracle/gpedit/`, and never searches the disk.

## 1. A required native module is missing

Windows Home has no `gpedit.dll`, `admtmpl.dll` or `gpedit.msc`. A trimmed image can lack `mmcbase.dll`.

- Every module of the fixed list (`gpedit.dll`, `admtmpl.dll`, `mmc.exe`, `mmcndmgr.dll`, `mmcbase.dll`) is required.
  The tool checks all of them first and fails with one message that lists every missing path. It writes nothing,
  so a partial dump is never mistaken for a complete one and the tracked index is never rewritten from partial data.
- Exit code 1. `scripts/oracle.js` stops the run and records the failed step.

## 2. A required MUI is missing

The strings, menus and dialogs live in `%SystemRoot%\System32\en-US\<name>.mui`. A machine whose display language
is not en-US may have only another language folder.

- The `en-US` file is required for each module. The language is fixed to en-US because the parity inventory is en-US.
  A missing file is reported with the same all-at-once message as section 1.
- A module that has no MUI by design (a language-neutral binary) would also fail. The list above was checked on
  this machine: all five have an `en-US` MUI. If a future Windows build drops one, the run fails loudly and the
  list is changed in one place, with this document, rather than silently skipping it.

## 3. Resource enumeration fails or reads garbage

- The module is loaded with `LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE` and never executed. A load
  failure throws with the path and the Win32 error.
- A resource that cannot be read is kept in the inventory with its `error` text and counted in `errorCount`. It is
  not dropped, so a missing dialog is visible as an error and not as an absence.
- A malformed menu, dialog, accelerator or string block is recorded with an `error` and does not stop the others.
- RT_HTML resources (the Extended view pages) are inventoried by name and size only. Their content is Microsoft
  content and is not parsed or copied into any tracked file.

## 4. String ids collide between modules

String id 123 exists in several modules. A bare id cannot be cited.

- Every id is qualified by the module that holds it: `<module>:<id>`, for example `admtmpl.dll.mui:123`. The MUI
  module is named with its `.mui` suffix because the text lives in the MUI, not in the neutral binary.
- The tracked index, `string-parity.json` and the `Oracle` citation in a `.resw` comment all use that one form.
  A citation without a module qualifier is not accepted for gpedit, so it cannot attach to the wrong module.
- A module and its MUI both hold RT_STRING only when the neutral binary is not stripped. They are separate keys.

## 5. The managed assemblies are not where expected

The policy-setting dialog is WinForms, in `Microsoft.GroupPolicy.AdmTmplEditor.dll` and its satellite
`Microsoft.GroupPolicy.AdmTmplEditor.Resources.dll`. The GAC folder differs by architecture: on this ARM64 machine
both are under `GAC_Arm64` and also under `GAC_32`; an x64 machine has `GAC_64`; an architecture neutral build
would be under `GAC_MSIL`.

- Only these four folders are enumerated, under `%SystemRoot%\Microsoft.NET\assembly`: `GAC_MSIL`, `GAC_64`,
  `GAC_Arm64`, `GAC_32`. Nothing else is searched. Each is probed at
  `<root>\<assembly>\<version folder>\<assembly>.dll`.
- When a copy exists in several roots the preference is `GAC_MSIL`, then the root that matches the host
  architecture (`GAC_Arm64` on ARM64, `GAC_64` on x64), then `GAC_32`, so the same machine always picks the same
  copy. The other copies are listed (path and SHA-256) in the untracked forms dump so a difference is visible.
- Several version folders: the highest assembly version wins (numeric compare, ordinal as the tiebreak).
- The satellite is the folder whose culture is `en-US`, else `en`. A missing satellite fails the run: the
  Editor form resources live only in the satellite.
- Either assembly missing fails the whole gpedit run with a message that names the folders that were probed. The
  tool does not fall back to searching, and does not write a dump with an empty forms file.

## 6. The managed resources directory cannot be read

- The assembly is read as a PE file with `System.Reflection.Metadata` (`PEReader`). It is never loaded, so none of
  its code runs and a hostile or mismatched binary cannot execute.
- No managed resources directory, or a manifest resource whose implementation is not embedded (linked file or
  assembly reference), is recorded as a skipped resource with its name and reason. Zero `.resources` streams in
  either assembly fails the run: the Editor form is the reason the extractor exists.
- A manifest resource whose offset or length leaves the directory fails the run with the name. It is never read
  with a clamped length.
- A `.resources` stream that `ResourceReader` rejects (bad magic, bad version) is recorded with an `error` for that
  one stream. Other streams are still dumped. The run still fails when the Editor stream is the one that failed.
- Manifest resources that are not `.resources` (for example embedded files) are listed by name and size only.

## 7. Serialized values of an unknown type

Compiled WinForms resources store Point, Size, Padding, enum values and `TableLayoutSettings` as serialized
objects, not as text. `BinaryFormatter` is removed from current .NET and is never called.

- Every entry keeps its name, its type name and, when it is not decoded, its raw bytes as base64. Nothing is
  dropped and nothing is guessed.
- Strings and primitives are decoded from the resource data format. A decode failure keeps the base64 and sets
  `error`.
- A serialized object is read with a small reader for the .NET remoting binary format (class with members and
  types, primitives, strings, null, library records). It decodes Point, Size, Padding, `AnchorStyles`, `DockStyle`
  and `TableLayoutSettings` into plain fields. Any other record type (arrays of objects, references, generics)
  stops the reader for that entry: the entry keeps type name and base64, plus `error` text that says which record
  type was unsupported.
- The type names are the stable identifier. The tracked index carries only entry name and type name, so an
  undecoded value cannot leak into it.
- Large entries (images, icons) are kept as base64 in the untracked dump only.

## 8. Non-deterministic output

Two runs must give the same tracked file byte for byte.

- Everything is sorted: modules in the fixed list order, resource types by id, resources by id then language,
  WinForms resource files by assembly then name, entries by name, with ordinal comparison. Dictionary and
  hash-table enumeration order of `ResourceReader` is never used as output order.
- Control ids in a dialog keep template order (it is tab order and is data). They are arrays, not sets.
- The tracked index has no timestamp, no machine name, no absolute path, no SHA-256, no version. Those live only in
  the untracked dumps. JSON is two-space indented, LF newlines, trailing newline, no byte-order mark.
- Output is written to a temporary file and renamed, so a reader never sees a partial file, and no output depends on
  the current culture (invariant formatting everywhere).
- The check is `git hash-object` of the index after two consecutive runs.

## 9. Microsoft text in a tracked file

The dumps hold dialog titles, menu text, strings, labels and help text. These are Microsoft content.

- Untracked and listed in `.gitignore` in a commented block: `gpedit-resources.json`, `gpedit-resources.md`,
  `gpedit-forms.json`, `gpedit-msc.json`.
- The only tracked output of the static run is `gpedit-resource-index.json`: resource type and id lists, dialog ids
  with control counts and control ids, menu ids with item counts, string ids, and for each WinForms resource file
  its entry names and type names. Ids, counts and identifiers only. Entry names such as `okButton.Text` are
  identifiers, not text. No label, caption, string or value is written.
- `string-parity.json` for gpedit holds qualified ids and statuses only.
- The index builder takes no text field of the golden model as input, so a later model change cannot add text to it
  without a code change here.

## 10. Regedit output drifts

The same tool and the same scripts also produce the regedit oracle, which other tracked files depend on.

- `--tool` defaults to `regedit`. With it the tool takes the same code path as before: same sources, same file
  names, same JSON and Markdown renderers, no new fields. Shared types get no property that would show up in the
  regedit JSON.
- Check: hash `regedit-resources.json`, `regedit-resources.md` and `string-parity.json` before the change and after
  rebuilding and re-running. They must be identical, and `git status` must show no change to tracked regedit files.
- `scripts/check-regedit-strings.js` with no arguments still writes the regedit `string-parity.json` from the same
  inputs with the same output text.

## 11. `gpedit.msc` is read wrongly

`gpedit.msc` is an XML console file with large base64 icon blocks.

- It is parsed as data with DTD processing prohibited and no resolver. A missing file or invalid XML fails the run
  with the path and the parser message.
- The dump keeps the console settings (`ProgramMode`, status bar, view options, scope pane width), the snap-in
  CLSIDs with their extension flags, and the string table. Base64 blocks are replaced by their length and SHA-256,
  never copied.
- The file is read only. `mmc.exe` is never asked to open it, which would also write user state under
  `%APPDATA%\Microsoft\MMC`.

## 12. String parity against gpedit misleads

- The dump is absent (CI, a fresh clone): the script prints a skip note for that tool, exits 0 and writes no file for
  it. `--tool gpedit` with no dump is the same skip, not a failure.
- No resw files under the gpedit projects yet: that is fine. Every string is `absent`. The status means
  "text not found by exact match", not "missing feature".
- An unknown `--tool` fails with exit 1 and the list of valid names.
- A regedit run is not changed by the gpedit option: the regedit resw list, the regedit citation form (`Oracle 123`)
  and the regedit output are kept as they were.
- A citation `Oracle admtmpl.dll.mui:123` attaches only to that module and id. The ids after the colon may be a list
  (`Oracle admtmpl.dll.mui:123, 124`) and the module applies to all of them.
- A gpedit dump of another schema (no module list, no string tables) fails with exit 1.

## 13. Scope drift

- The run writes only under `docs/parity/oracle/gpedit/` and, for regedit, only the two regedit dump files. The
  `probe` step belongs to the capture tool. `scripts/oracle.js --tool gpedit --only resources` never starts it.
- Nothing here elevates. The `probe` step needs administrator rights and drives GUI, so the manager runs it by
  hand, never a worker.

## 14. The tracked index is written but is not valid JSON, or the one-line rewrite changes data

`ResourceIndex.Serialize` takes the normal JSON text and puts every array of integers on one line with two regular
expressions. The two expressions once lost their backslashes (`s+` instead of the whitespace class, a character class
of `[`, `s`, `*` instead of the array pattern). The rewrite then replaced single letters in the output, the tracked
file was written anyway and it was not valid JSON. Nothing in the run noticed, because the run only wrote the text.

- The rewrite is cosmetic. It must never change data. Before the index is written, the tool parses the formatted text
  and parses the unformatted `GoldenJson.Serialize(index)` text, and compares the two parsed values by content
  (object property names and values, array order, number values).
- The formatted text is not valid JSON, or the two parsed values differ: the tool throws before it writes anything,
  so the tracked index keeps its previous content. It prints `error: ...` that names the check that failed and where
  the first difference is, and exits with code 1. `scripts/oracle.js` stops the run and records the failed step.
- The check runs before the first file of the run is written, so a failed check also leaves the untracked dumps of
  the previous run untouched and a half updated output folder is not produced.
- The error text names a path inside the JSON and the kind of difference only. It never prints a value, so a failure
  cannot copy any text into a log by way of the index.
- Check: `npm run oracle -- --tool gpedit --only resources` twice with equal `git hash-object` results, and a
  `JSON.parse` of the tracked index that succeeds.

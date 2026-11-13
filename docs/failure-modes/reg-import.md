# .reg import failure modes

This document lists every way a `.reg` import can fail or diverge from classic `regedit /s`. It
covers `RegFileReader` (lenient mode), `RegFileImporter`, and the write path it drives through
`RegistryStore`. The grammar rules that do not change are in `reg-files.md`. The sections that
describe the strict parser and the stop-at-first-failure importer in `import-export.md` are
superseded by this document.

The oracle is `docs/parity/oracle/import-behavior.json`. It records what classic `regedit /s` did
for 30 seeded files. Each file ran against the base tree in `HKCU\Software\BWC-Oracle`: `Import\A`
(`Keep` = `"1"`), `Import\A\Sub` (`S` = `"x"`), and `Import\B` (`Other` = dword 1). The test
`RegFileOracleImportTests` replays every case against `HKCU\Software\BWC-E2E\<run-id>`, rewriting
the `HKEY_CURRENT_USER\Software\BWC-Oracle` prefix to the sandbox root, and compares the whole
resulting tree with the recorded one.

Three kinds of outcome are distinct and each has its own result:

- **Refused**: the file is not applied at all. Nothing is written. The result carries a refusal
  reason.
- **Line skipped**: one physical or logical line is ignored and the import continues. The result
  lists the line in `Failures` with kind `MalformedLine`.
- **Write failed**: one native write is rejected and the import continues. The result lists the
  line in `Failures` with kind `WriteFailed`, with the key path, value name, and Win32 error.

A line skipped by the reader is the same thing classic regedit does for the oracle files. Classic
never reports a line error through the exit code, so `Failures` is a BWC addition for the UI.

## 1. Whole-file refusals

These cases apply nothing. The oracle records a classic result of "nothing imported, exit 0" for
the first group. The second group is refused by the oracle capture tool before it runs, so BWC
chooses the safe result.

| ID | Trigger | Classic (oracle) | BWC result | Test |
| --- | --- | --- | --- | --- |
| REFUSE-01 | Zero-byte file | imports nothing, exit 0 (`empty-file`) | refused, `NotRegistryFile` | `empty-file` |
| REFUSE-02 | No header; first line is a key section | imports nothing (`no-header`) | refused, `NotRegistryFile` | `no-header` |
| REFUSE-03 | Header reads `Version 5.0` | imports nothing (`header-version-typo`) | refused, `NotRegistryFile` | `header-version-typo` |
| REFUSE-04 | v5 text in UTF-16LE without a BOM | imports nothing (`v5-utf16le-no-bom`) | refused, `NotRegistryFile` | `v5-utf16le-no-bom` |
| REFUSE-05 | UTF-16 big-endian BOM | not captured | refused, `NotRegistryFile` | none (reader test) |
| REFUSE-06 | UTF-16LE with an odd number of bytes | not captured | refused, `NotRegistryFile` | none (reader test) |
| REFUSE-07 | Key path with a `..` or `.` segment (`refused-dotdot-segment`) | refused by capture tool; not run | refused, `InvalidKeyPath`, nothing written | `refused-dotdot-segment` |
| REFUSE-08 | Key path with `]` inside the path (`header-extra-close-bracket`) | refused by capture tool; not run | refused, `InvalidKeyPath`, nothing written | `header-extra-close-bracket` |
| REFUSE-09 | Key path with an unknown root or an empty segment (`HKCU\\Software`) | not captured | refused, `InvalidKeyPath` | reader test |
| REFUSE-10 | The file cannot be read (missing, locked, access denied) | not captured; classic shows string 129 or 130 | refused, `ReadFailed` | importer test |

The header rule is exact: the first line must equal `Windows Registry Editor Version 5.00` or
`REGEDIT4`. A UTF-8 byte-order mark is removed before the check (see section 3).

Refusal happens before any key is created. A refused file produces zero `Failures` entries of kind
`WriteFailed` and changes no key.

The path rules are the capture tool's rules, not classic's. Classic would try the key and report a
failure. BWC refuses the whole file because a `.reg` file with a bad path is almost always damaged,
and a refused file cannot write to an unintended key.

## 2. Line-level skips

Each of these skips one line and imports the rest of the file. Lines that are not listed are read
as the grammar in `reg-files.md` says.

| ID | Trigger | Classic (oracle) | BWC result | Test |
| --- | --- | --- | --- | --- |
| SKIP-01 | Value line before any key section (`value-before-section`) | the orphan value is dropped; the next section imports | line skipped, `MalformedLine` | `value-before-section` |
| SKIP-02 | A line that is neither a section, a value, a comment, nor blank (`bad-line-middle`) | dropped; the sections after it import | line skipped, `MalformedLine` | `bad-line-middle` |
| SKIP-03 | Key line with no `]` (`header-missing-close-bracket`) | imports nothing for that section; its value lines are not applied | section not started; the value lines that follow are orphans and are skipped | `header-missing-close-bracket` |
| SKIP-04 | Key line with text after the last `]` | not captured | line skipped; the section is not started | reader test |
| SKIP-05 | Empty key path `[]` or `[-]` | not captured | line skipped | reader test |
| SKIP-06 | Value name that is neither `@` nor a quoted string | not captured | value line skipped | reader test |
| SKIP-07 | Quoted string with no closing quote, or an unknown escape such as `\q` | not captured | value line skipped | reader test |
| SKIP-08 | Missing `=` or junk after a value | not captured | value line skipped | reader test |
| SKIP-09 | Hex value with a non-hex digit (`hex-bad-digit`) | the value is dropped; the next line imports | value line skipped (with its continuation lines) | `hex-bad-digit` |
| SKIP-10 | Hex value with an odd number of digits, a bad type number, or no `:` | not captured | value line skipped (with its continuation lines) | reader test |
| SKIP-11 | `dword:` with zero digits or more than eight digits | not captured (one digit is accepted, see ACCEPT-04) | value line skipped | reader test |
| SKIP-12 | A backslash that is not the last character on a hex line, or a continuation at end of file | not captured | value line skipped | reader test |

Recovery for a skipped value line: the skip covers the whole logical value. A hex value that
continues with a trailing `\` also skips every continuation line that belongs to it. A skipped
key line never leaves an open section, so the value lines after it are orphans (SKIP-03).

## 3. Accepted leniencies

The reader accepts these lines. Each one is backed by the oracle, so a change to them is a
regression.

| ID | Input | Classic (oracle) | BWC result | Test |
| --- | --- | --- | --- | --- |
| ACCEPT-01 | UTF-8 byte-order mark before a v5 header | the file imports (`v5-utf8-bom`) | BOM removed; text decoded as UTF-8 | `v5-utf8-bom` |
| ACCEPT-02 | v5 text in UTF-8 without a BOM | decoded with the ANSI code page; `ü` becomes `Ã¼` (`v5-utf8-no-bom`) | same as classic | `v5-utf8-no-bom` |
| ACCEPT-03 | LF line endings and no blank line after the header (`v5-utf16le-bom-lf-no-blank`) | imports | imports | `v5-utf16le-bom-lf-no-blank` |
| ACCEPT-04 | `dword:1` (one hex digit) | stored as a DWORD with value 1 (`dword-short`) | accepted; one to eight digits are read as hex | `dword-short` |
| ACCEPT-05 | `hex:01,02,` (trailing comma at end of line) | stored as `0102` (`hex-trailing-comma`) | accepted; the trailing comma is ignored | `hex-trailing-comma` |
| ACCEPT-06 | `hex:` with no bytes | stored as an empty binary (`hex-empty`) | stored as an empty binary | `hex-empty` |
| ACCEPT-07 | Hex continuation with and without indentation (`hex-continuation-indented`, `hex-continuation-no-indent`) | the bytes join | the bytes join | both cases |
| ACCEPT-08 | REGEDIT4 header with and without a blank line after it (`regedit4-ansi-crlf`, `regedit4-no-blank-line`) | imports as ANSI | imports as ANSI | both cases |
| ACCEPT-09 | Escaped backslash and quote in a string (`escaped-string`) | `a\b"c` | same | `escaped-string` |
| ACCEPT-10 | Whole-line `;` comments and blank lines | documented regedit behavior; not in the oracle | skipped | reader test |

## 4. Sections and values

| ID | Trigger | Classic (oracle) | BWC result | Test |
| --- | --- | --- | --- | --- |
| SECTION-01 | Section for a key whose parents do not exist (`missing-ancestors`) | creates every missing ancestor | creates each missing ancestor, top down, then the key | `missing-ancestors` |
| SECTION-02 | Section for the BWC-Oracle root itself with a default and a named value (`root-section-only`) | writes the default value and the named value | same | `root-section-only` |
| SECTION-03 | Section for a predefined root, `[HKEY_CURRENT_USER]` | not captured | no `CreateKey` (a predefined root always exists); its values are written if the policy allows | importer test |
| SECTION-04 | Delete section for a predefined root, `[-HKEY_CURRENT_USER]` | not captured | one `WriteFailed` at that line; nothing deleted | importer test |
| SECTION-05 | Delete section for a key (`delete-subkey`) | deletes the key and its subtree | same | `delete-subkey` |
| SECTION-06 | Delete section for a key that does not exist | not captured | no failure; `ERROR_FILE_NOT_FOUND` is ignored | existing importer test |
| SECTION-07 | Delete then recreate in one file (`delete-key-then-recreate`) | the new key holds only the new values | same | `delete-key-then-recreate` |
| SECTION-08 | The same key section twice (`duplicate-section`) | values merge; the later value wins | same | `duplicate-section` |
| SECTION-09 | Value lines attached to a delete section | not captured | ignored, as before | importer test |
| VALUE-01 | Delete value `"name"=-` (`delete-value`) | removes the value | same | `delete-value` |
| VALUE-02 | Delete value that does not exist | not captured | no failure | existing importer test |
| VALUE-03 | Default value `@=` in a section | writes the default value | same | `root-section-only` |
| VALUE-04 | Value name with `\0` in it | not captured | `WriteFailed` at the value line (the writer rejects a NUL in the name); the import continues | importer test |
| VALUE-05 | Typed values: dword, qword, binary, `hex(7)` multi-string, string | stored with their types (`v5-utf16le-bom-crlf`) | same | `v5-utf16le-bom-crlf` |

## 5. Write failures and continuation

Classic `regedit /s` keeps going after a line it cannot parse, and the oracle shows it. Classic
also reports a failed write with string 132, "Cannot import %1: Not all data was successfully
written to the registry. Some keys are open by the system or other processes, or you have
insufficient privileges to perform this operation." (the resource text has two spaces after
"registry."). The oracle does not capture a write failure, so the continuation rule for writes is
taken from the task and recorded here as a BWC decision.

| ID | Trigger | BWC result | Test |
| --- | --- | --- | --- |
| WRITE-01 | A section's key cannot be created or opened (for example access denied, or the policy refuses it) | one `WriteFailed` at the section line with the Win32 error; the section's value lines are not attempted | importer test (policy denial) |
| WRITE-02 | A value write is rejected | one `WriteFailed` at the value line; the next lines continue | importer test |
| WRITE-03 | A delete of a key or value is rejected | one `WriteFailed` at the line; the next lines continue | importer test |
| WRITE-04 | The sandbox policy refuses a path outside `HKCU\Software\BWC-E2E\<run-id>` (`refused-other-root`) | `WriteFailed` with Win32 5 at each section line; nothing is written and no native call is made | importer test |
| WRITE-05 | Writes that already succeeded before a failure | stay applied. The import is not transactional, as in classic | importer test |
| WRITE-06 | Failure of a value write under a section whose key was created | the rest of that section still runs | importer test |

The result reports `WriteFailed` entries separately from `MalformedLine` entries so the UI can show
string 132 only when a write failed. Classic shows nothing for skipped lines on the oracle files,
and BWC does not show them either.

## 6. Cancellation and progress

| ID | Trigger | BWC result | Test |
| --- | --- | --- | --- |
| CANCEL-01 | Token cancelled before the import starts | `OperationCanceledException` before any write | existing importer test |
| CANCEL-02 | Token cancelled during the import | checked before each key section. Sections already finished stay applied. The section in progress is finished. Cancellation throws `OperationCanceledException` | importer test |
| PROGRESS-01 | Progress callback | reported once after each key section finishes, with the number of sections done, the total, and the key path | importer test |

Classic stops between complete entries when cancelled (`registry-engine.md`, Threading). BWC does
the same.

## 7. Divergences that are deliberate

- A refused file (section 1) is refused whole, while classic would try each key. The capture tool
  refused these inputs before running them, so there is no classic result to match.
- A write failure removes the value lines of a section whose key could not be opened, and does not
  try them one by one. Classic behavior for that shape is not captured.
- The importer reports line numbers for every failure. Classic shows none.
- A predefined root section never reaches `CreateKey`, because the root always exists. A delete of
  a predefined root is attempted and the writer rejects it, which is reported as a write failure
  (SECTION-04).

## 8. What is not captured

These behaviors are not in the oracle, so BWC's choice above is unverified against classic:

- UTF-16 big-endian and odd-length UTF-16 files (REFUSE-05, REFUSE-06).
- Unknown root names and empty segments (REFUSE-09).
- Read failures (REFUSE-10): the oracle has no unreadable-file case.
- Every SKIP entry except SKIP-01, SKIP-02, SKIP-03, and SKIP-09.
- Write failures of any kind (WRITE-01 to WRITE-06).
- A value line attached to a delete section (SECTION-09).
- A REGEDIT4 file with a UTF-8 byte-order mark (ACCEPT-01 covers only v5).

Each of these needs a new oracle capture before it can be called matched.

# .reg file failure modes

This document covers `RegFileDocument`, `RegFileKey`, `RegFileValue`, `RegFileWriter`,
`RegFileReader`, and `RegFileParseException` in `src/Bwc.Registry/RegFile`. The three files in
`docs/parity/oracle/exports` are the specification. A change to any byte they contain is a
regression unless a new oracle capture replaces them.

## 1. Encoding and BOM

`RegFileWriter` always writes the v5 format as UTF-16 little-endian with a `FF FE` byte order
mark. `RegFileReader` detects that mark and removes it before parsing. A v5 file without a BOM is
decoded as the system ANSI code page; its ASCII header still identifies it as v5, so the format
and the encoding are independent decisions.

The REGEDIT4 format has no BOM and is written in the system ANSI code page. `Encoding.Default`
is UTF-8 on modern .NET and must not be used. The registry code page comes from
`Encoding.GetEncoding(0)` after `CodePagesEncodingProvider` is registered, and on the reference
machine it is 1252. The REGEDIT4 golden proves that choice: `Ünïcødé ✓ 日本` exports as
`Ünïcødé ? ??`, because 1252 represents `Ü`, `ï`, `ø`, and `é` but not the check mark or the
Japanese characters.

A file decoded with the wrong code page either fails the header check or produces different
bytes for non-ASCII key names, value names, and strings. The round-trip test for the REGEDIT4
golden is therefore also an assertion about the machine code page. Running those tests on a
machine with a different ACP is expected to fail until the test fixture is pinned or the golden
is regenerated for that code page.

UTF-16 little-endian with an odd byte count is rejected as malformed. A UTF-16 big-endian BOM is
not accepted; regedit does not produce one.

## 2. ANSI data conversion in REGEDIT4

The document model stores value data in registry form. String-like types (1, 2, and 7) are
UTF-16LE bytes; every other type is raw bytes. The v5 writer dumps string bytes unchanged. The
REGEDIT4 writer converts the UTF-16LE text of types 1, 2, and 7 to ANSI before formatting it.

The REGEDIT4 reader performs the inverse conversion for `hex(1)`, `hex(2)`, and `hex(7)` and for
quoted `REG_SZ` strings. The conversion is not lossless when the text is not representable in
the code page: those characters become `?`. An empty multi-string also changes size, from two
zero bytes in registry form to one zero byte in REGEDIT4, and grows back on import.

This means a REGEDIT4 export followed by an import does not always reproduce the exact registry
bytes that were exported. It does reproduce regedit's output, which is the contract here. The
v5 format preserves the same data without conversion.

## 3. Line endings and the final blank line

Every line is terminated by CRLF. The header is followed by one blank line. Each key section is
a key line, its value lines, and one blank line. The file therefore ends with two CRLF pairs
when the last section has at least one value: one for the value and one for the blank line.

The reader accepts CRLF and LF input so a hand-edited file can be imported. The writer never
emits LF alone and never uses `Environment.NewLine`. A missing or extra blank line changes the
byte-for-byte golden comparison even though the registry effect is identical.

## 4. Header

The first line must be exactly `Windows Registry Editor Version 5.00` or exactly `REGEDIT4`.
Leading spaces, a different version number, or any other text is rejected with
`RegFileParseException` at line 1. The header decides the format; the BOM decides the text
encoding. An empty file has no header and is rejected.

## 5. String escaping

Quoted value names and `REG_SZ` data escape a backslash as `\\` and a quote as `\"`. Those are the
only two escapes regedit writes, and the reader accepts only those two. The goldens show `\\` for
`C:\Path\To\File`, `\"` for `He said "hi" and left`, and a quoted name containing both a quote
and a backslash. Characters other than those two are written literally, including tabs and other
control characters.

The escapes `\0`, `\n`, and `\r` are not regedit syntax, so they are not written and not read. The
reader treats them as unknown escapes and raises a parse exception, as it does for any other escape.
Escaping applies to value names as well as data.

Data that cannot be written in quoted form is written as `hex(1):` instead, with its raw bytes:

- A `REG_SZ` whose text, up to the first NUL, contains a line feed or a carriage return. No golden
  shows this case, so the `hex(1):` form is the safe round-trip choice, not an observed classic
  rule. Recorded as unverified against classic regedit.
- A `REG_SZ` with an embedded NUL keeps the classic truncation: the text stops at the first NUL. The
  golden `EmbeddedNul` exports as `"abc"`, so this is classic behavior and is not changed.
- A value name containing a NUL, a line feed, or a carriage return has no quoted form and no `hex`
  form. The exporter refuses it with `ExportValueName` (Win32 123) and the writer refuses to write
  it. The NUL rule is the existing one in `export.md` section 7.

Key paths are not escaped. They are written between `[` and `]` exactly as the document stores
them, except that one leading `Computer\` prefix in any casing is removed so the output uses the
registry-style form.

A key name containing `]` cannot round-trip. The reader takes the last `]` on the header line as the
closer, as classic does, and then refuses a path segment that still contains `]`. The oracle case
`header-extra-close-bracket` pins that refusal, and `[...\C12]]` is byte-for-byte what a key named
`C12]` exports to. The two cannot be told apart, so the oracle wins: the exporter writes such a key
as classic does, and BWC and classic both refuse to import it. This is a known limit, recorded in
`export.md` section 7, and it is not fixed in the reader.

## 6. Embedded NUL and lossy strings

Classic regedit exports a `REG_SZ` up to its first NUL. The seed value `abc\0def` appears in
both goldens as `"abc"`. A value with no terminator exports its full text, and a zero-length
value exports `""`. Importing `""` always creates a two-byte UTF-16 string with one NUL, so the
zero-length and two-byte empty strings become indistinguishable after one export and import.

The writer follows the golden behavior deliberately: it decodes with `DecodeString`, which stops
at the first NUL. The model does not preserve bytes past that NUL for type 1 export. A caller
that needs a lossless copy must use the registry APIs, not a `.reg` file.

Value names may contain an embedded NUL in the model, but the writer emits `\0` for it. Windows
cannot open a value whose name contains a NUL through the wide-string API, so such a document is
syntactically expressible but not applicable to the registry.

## 7. Line wrapping

Binary, multi-string, expand-string, none, qword, and unknown-type data is written as
comma-separated lowercase hex bytes. A line that continues on the next line ends with a comma
and a backslash. The whole physical line, including that comma and backslash, is at most 80
characters. Continuation lines start with two spaces. The final line has no trailing comma and
no backslash.

The number of bytes on the first line depends on the prefix length: the value name, the `=`,
and the `hex(N):` marker all count. The capacity is
`floor((80 - prefixLength - 1) / 3)`, with a minimum of one byte. Continuation lines have a
two-character prefix, so their capacity is 25 bytes. This formula reproduces the golden line
lengths of 78, 79, and 80 characters; a fixed byte count or a fixed column that ignores the
prefix does not.

Long `REG_SZ` text is not wrapped. The 5000-character seed value is one physical line in the v5
golden.

## 8. Continuations

A backslash inside `hex:` data must be the last non-space character on a line. The reader joins
the next line to the same value and allows leading spaces on the continuation. A backslash at
end of file, or a backslash followed by more data on the same line, is malformed.

A backslash inside a quoted string is an escape, not a continuation. Quoted strings cannot span
lines.

## 9. Hex type numbers

The number inside `hex(...)` is hexadecimal, not decimal. `hex(a)` is type 10, `hex(b)` is type
11, and `hex(12345)` is type 0x12345. The writer emits the number in lowercase with no leading
zeros. The reader accepts either case.

`hex:` with no parentheses means type 3 (`REG_BINARY`). A four-byte type 4 value is written as
`dword:XXXXXXXX` with exactly eight lowercase hex digits. A type 4 value of any other length is
written as `hex(4):`, so a three-byte DWORD survives the round trip as three bytes. Type 11 is
always written as `hex(b):` and its bytes are dumped in the stored little-endian order; the
writer does not swap them. Type 2 is written as `hex(2):` in both formats, matching the goldens,
not as a quoted string. Type 0 is `hex(0):`, and an unknown type is `hex(N):`.

The reader rejects a `dword:` token without exactly eight hex digits. It rejects `hex()` with no
type digits and `hex(N)` without the closing parenthesis or colon.

## 10. Delete lines

A key section whose path starts with `-` is a delete instruction: `[-HKEY_CURRENT_USER\...]`.
A value whose data is `-` is a delete instruction: `"name"=-` or `@=-`. The document records
the `Delete` flag and the writer reproduces the line. Parsing does not touch the registry, so a
delete line has no effect until a separate import step applies the document.

A delete key section may be followed by value lines in a malformed or hand-written file. The
parser keeps them attached to that section and the writer emits them after the delete line. No
oracle golden covers that combination.

## 11. Malformed input

`RegFileReader` has a strict and a lenient entry point for each input. `Parse` and `ParseFile`
are strict: they throw `RegFileParseException` at the first malformed line, with a one-based `Line`
and `Column` and a message that names the problem. `ParseLenient` and `ParseFileLenient` are the
entry points the importer uses, and they match classic regedit. A malformed line is skipped and
listed in `RegFileDocument.Problems`, and the rest of the file is parsed. Only a file-level problem
throws from the lenient entry points, with `RegFileParseException.Refusal` set to
`NotRegistryFile` or `InvalidKeyPath`. The cases covered by tests are:

- missing or wrong header (file-level: the whole file is refused)
- an unknown root, or a `.`, `..`, or `]` in a key path (file-level: `InvalidKeyPath`)
- a key line without `]` (line-level: the section is not started)
- a value line before any key section
- a value name that is not quoted and is not `@`
- a missing `=`
- a missing closing quote or an unknown escape
- a bad hex digit or an odd number of hex digits
- a comma at end of line without a continuation
- a continuation at end of file
- a `dword:` with the wrong number of digits

Argument problems (a null stream or path) raise `ArgumentNullException`. File system failures
from `ParseFile` and `ParseFileLenient` propagate as the original I/O exception and are not wrapped
as parse errors. `RegFileImporter.ApplyFile` maps them to `RegFileRefusal.ReadFailed`.

## 12. Partial parse

Parsing builds the whole document in memory and returns it only after the last line is read. The
strict entry points throw on the first malformed line, so they return no partial document. The
lenient entry points return every line that parsed, plus the `Problems` list for the lines that did
not. A refused file returns no document. This parser never writes to the registry, so a partial
apply is not possible in this layer.

The importer applies the document one key section at a time, in file order. A rejected write does
not stop it: the write is recorded in `RegFileImportResult.Failures` and the next line is applied.
Cancellation is checked between sections, as the registry engine contract requires
(`docs/contracts/registry-engine.md`, Import).

## 13. Huge values

There is no engine-side size cap. The parser holds the complete decoded text, the current line,
and the resulting byte array at the same time, so a very large `.reg` file can use several times
its file size in memory. The wrapping loop is linear in the data length and allocates only the
output builder. The writer does not truncate. A caller exporting a multi-gigabyte value is
limited by memory and by `Stream` behavior, not by an arbitrary constant in this code.

## 14. Case

The writer preserves the exact spelling of key paths, value names, and string data. It does not
uppercase roots, normalize separators, or change the type marker case. The reader preserves path
and name text as decoded. Registry comparison remains case-insensitive when the document is later
applied, but the file text is not normalized. The goldens use `HKEY_CURRENT_USER` and mixed-case
key names, and the round-trip test would catch any normalization.

## 15. Key and value order

The document keeps key sections in their exact relative order and values in their exact order
inside a section. The writer does not sort, merge, or deduplicate. Export order is registry
enumeration order, not the linguistic order the UI shows; the v5 golden lists `Ünïcødé Key ✓`
last even though the tree displays it before `ValueNames`. Duplicate sections for the same key
are preserved, which is useful for imports that intentionally apply several changes in sequence.

## 16. Golden drift

The goldens are checked-in oracle output, not generated by this library. A writer change that
alters spacing, wrapping, escaping, encoding, or final blank lines shows up as a byte mismatch in
the round-trip tests. The fix is to compare against a fresh oracle capture and update all three
files together; editing a golden by hand would hide the regression. The REGEDIT4 golden is also
tied to the system ANSI code page, as described in section 1.

## 17. Behavior not covered by a golden

The goldens contain no comments, no delete lines, no escaped newline, no malformed input, and no
duplicate key section. The parser accepts whole-line `;` comments, which is documented regedit
behavior, and rejects trailing comments after a value line. Delete lines and the `dword:` length
check follow the documented format. Escaped `\n` and `\r` are accepted on read and emitted from
data that contains those characters, but no golden exercises them. These choices are marked here
so a future oracle capture can confirm or change them deliberately.

## 18. Tests

`tests/Bwc.Registry.Tests` copies the three goldens to its output directory and compares bytes;
it never writes the real registry. The tests parse each golden, assert structural counts and
specific raw types and bytes, then write the document back and compare the complete byte array
with the source file. Malformed-input tests build small in-memory files, so they do not depend on
the machine registry or on file system state. `ParseFile` gets one test that writes a temporary
file outside the registry and deletes it in a `finally` block.

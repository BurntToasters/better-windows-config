# Template store failure modes

Scope: piece E5 (part 2) of release 0.2.0, `src/Bwc.Policy/Store`. `TemplateSources` loads the system template folder
and the BWC import store as one set. `TemplateImport` checks a chosen `.admx` and its `.adml`, copies the pair into the
store and removes an imported template. `TemplateStoreWriter` is the only code that writes into the store
(`scripts/check-policy-writer.js`, `docs/failure-modes/policy-writer.md`). The contract is
`docs/contracts/policy-engine.md`, section "`Bwc.Policy`: template sources". This page was written before the code. The
tests are `tests/Bwc.Policy.Tests/TemplateStore*Tests.cs`, `TemplateSources*Tests.cs` and `TemplateImport*Tests.cs`.

Rules for the whole piece:

- The library reads no environment variable. The tool passes the system folder, the store folder and the ACL rules
  (`ProtectedAcl.Ship()` in the ship build, `ProtectedAcl.ForCurrentUser()` in the E2E sandbox and the tests).
- Nothing is written outside the store folder. The system folder is never written, and a store folder that is the
  system folder, inside it or around it is refused.
- A failure is a typed value (`TemplateImportProblem`) or a typed exception (`TemplateStoreException`, with the
  operation, the full path and the Win32 error). The engine formats no text.
- Every import is a copy of bytes that were read once, checked, and read again to prove they did not change.
- A cancelled call returns nothing and writes nothing that it cannot undo.
- Linking `ProtectedAcl.cs` adds a public type `Bwc.Update.ProtectedAcl` to `Bwc.Gpo.dll` (the link moved there from `Bwc.Policy` in AF5, so one
  compiled copy serves both). A project that references both `Bwc.Gpo` and `Bwc.Update` binaries and names the type gets an ambiguity error and must alias one reference.

## Layout

```
<store>\<name>.admx
<store>\<culture>\<name>.adml     the culture that was used (the UI culture, else en-US)
```

The file name is the source ADMX file name with a lowercase `.admx` extension. Temp files are named
`<name>.<guid>.tmp`; the loader ignores everything that does not end in `.admx`.

## A. The store folder

| ID | Case | Risk | Handling |
| --- | --- | --- | --- |
| A1 | The store folder does not exist | A first run looks like an error | Normal. `TemplateSources` loads the system folder alone and reports status `Missing`. The first write creates the store. |
| A2 | The store folder is created | A folder that inherits write access for Users, so a user could plant a template that an elevated app loads | The folder and any missing parent get an explicit protected DACL (`ProtectedAcl.NewSecurity()`): inheritance off, SYSTEM and Administrators full control, no other entry, owner Administrators in the ship build. |
| A3 | The store exists but its ACL or owner is wrong, or it grants write to another trustee | The same, planted by a user before the first run | `ProtectedAcl.Inspect` runs before every read of the store and every write. A failure is status `AclRejected` for reading (the store is not loaded, the system folder still is) and a `StoreAclRejected` exception for writing. The ACL is never repaired silently, because that could hide a planted file. |
| A4 | The store folder is owned by a standard user | Same as A3 | `Inspect` returns a foreign owner. Same handling. |
| A5 | The store folder, a language folder or a template file is a reparse point | A junction sends reads or writes to another folder | Reading: `TemplateLoader` already refuses a reparse folder (`ReparsePoint` failure) and the store is reported as status `ReparsePoint` and not loaded. Writing: refused with `ReparsePoint`, checked on the store, the language folder and the target file immediately before the write. |
| A6 | The store path is a file, not a folder | A create fails with an obscure error | Status `NotADirectory`. The writer refuses with `NotADirectory`. |
| A7 | The store folder is on a network or UNC path | An ACL that no local rule governs | Refused with `StoreRootNotLocal` for `\\` paths and for a network drive. |
| A8 | The store folder is on another volume than the source file | A rename across volumes fails | Never renames across volumes: the source is read into memory and the temp file is made inside the store folder, so the replace is on one volume. |
| A9 | The store is not writable because the process is not elevated | A raw `UnauthorizedAccessException` reaches the UI | Mapped to `TemplateStoreException` with reason `AccessDenied` (Win32 5). Creating a folder with an owner that the token cannot hold is reason `OwnerNotAllowed` (Win32 1307 or 1308). The UI maps both to "needs administrator". Nothing is left behind. |
| A10 | The store folder is deleted between the status check and the write | A late failure | The write re-checks and creates it again, or fails typed. |
| A11 | The store root is the system folder, below it or above it | A write into the system folder | Refused when the writer is built (`StoreRootForbidden`). |
| A12 | Store root text is empty, has invalid characters, is relative or a drive root | A write to the wrong place | Refused when the writer is built. The root is normalized to a full path. |

## B. Sources and duplicates

| ID | Case | Risk | Handling |
| --- | --- | --- | --- |
| B1 | Both folders define one namespace | Two definitions of every policy | The system folder is first and wins. The loader lists the import as `NamespaceShadowed`. `TemplateSources` returns it in `Shadowed` with the winner's path. The import file stays on disk. |
| B2 | Two files of the store define one namespace | One is ignored | The loader reports `DuplicateNamespace`. `Shadowed` does not list it. |
| B3 | The system folder is missing | A broken machine or a sandbox | The loader reports `FolderNotFound` for it and the store is still loaded. |
| B4 | The store has a template that fails to load | One bad file blocks the others | Per-file failure only. The set keeps every other template. The failure is in `TemplateSet.Failures`. |
| B5 | The UI culture has no ADML in the store | An imported template missing its text | The loader falls back to en-US. The import copies the ADML that was used (B-import rules below). |
| B6 | A template in the store uses a namespace that only the system folder has | A using reference | Resolves, the set is linked across both sources. |
| B7 | The system template that the import uses is later removed | `UndefinedUsing` after an update | The loader reports it. The import is not deleted. |

## C. Import checks

`TemplateImport.Check` and `Import` return a list of problems and refuse when it is not empty. Each problem has a
code, the path and a detail.

| ID | Case | Risk | Problem code |
| --- | --- | --- | --- |
| C1 | The path is empty, has invalid characters or does not exist | A crash | `SourceMissing` |
| C2 | The file is a classic `.adm` | A format we declared unsupported | `ClassicAdmNotSupported` |
| C3 | The extension is not `.admx` (a `.adml`, `.xml`, no extension) | A wrong file | `NotAdmx` |
| C4 | The file name has a reserved device name (`CON`, `NUL`, `COM1`), a trailing dot or space, an alternate stream, a path separator, a name longer than 255 characters | An unsafe name in the store | `UnsafeFileName`, checked on the name, never on the path. |
| C5 | The source is a reparse point (file, source folder or language folder) | A link to a file the user cannot read | `SourceReparsePoint` (the loader failure `ReparsePoint` is the same case). |
| C6 | The source is larger than `AdmxLimits.MaxFileBytes` | Memory | `SourceTooLarge`, checked on the length before any read, and the read is capped. |
| C7 | The source cannot be read (in use, access denied) | A crash | `SourceUnreadable` with the HRESULT in the detail. |
| C8 | The ADMX is not well formed, has a DTD, is nested too deep, or is not a template | A bad file reaches the store | `LoadFailed` with the loader's code (`MalformedXml`, `DtdProhibited`, `XmlTooDeep`, `NotATemplate` and the rest). The loader's own hardening applies, because the check loads the file through `TemplateLoader`. |
| C9 | There is no ADML for the UI culture and none for en-US | A template with no text | `AdmlMissing` (the loader's `MissingAdml`). |
| C10 | The ADML exists only for en-US and the UI culture differs | A refusal that classic would not make | Not a problem. The en-US ADML is copied to the `en-US` folder of the store. |
| C11 | The ADML is a bad file (undefined string, malformed, duplicate id) | A broken template in the store | `LoadFailed` with the loader's code. |
| C12 | The namespace is already defined by the system folder | A shadowed import | `NamespaceDefinedBySystem`. Nothing is copied. |
| C13 | The namespace is already in the store | A silent overwrite | `NamespaceAlreadyImported`. To update, remove first. |
| C14 | A different template in the store has the same file name | An overwrite | `FileNameInUse`. |
| C15 | The template uses a namespace that only a neighbour file of the source folder defines | An import that fails to link in the store | `UsingNotInstalled` with the namespace. The loader finds the neighbour (it loads the source folder), so this check compares each using against the system folder and the store. |
| C16 | The template uses a namespace that nothing defines | A broken link | `LoadFailed` with `UndefinedUsing`. |
| C17 | The source file changes between the check and the copy | A different file than the one checked | The bytes are read before the check, read again after it, and compared. A difference is `SourceChanged` and nothing is written. The copy uses the second read. |
| C18 | The check loads the whole source folder, so a bad neighbour file produces failures | A neighbour blocks the import | Only failures that belong to the chosen ADMX, its ADML, or the folders on its path count. Others are ignored. |
| C19 | The source folder is the system folder or the store | A self import | The namespace is already defined (C12, C13). |
| C20 | The source folder holds hundreds of other templates | A slow check | Cancellable. The loader runs in parallel and checks the token once per file. |
| C21 | The store is not usable (A3, A5, A6, A7) | The import writes into an untrusted folder | `StoreUnavailable` with the status, before any copy. |
| C22 | The ADML changes between the load and the copy, or the pair does not load from the store after the copy | A template that loads from the source and not from the store | After the copy the sources are loaded again. When the template is missing or has a failure on its ADMX or ADML, the copy is removed again and the result is `VerifyFailed`. |

## D. Copy

| ID | Case | Risk | Handling |
| --- | --- | --- | --- |
| D1 | Write order | A half imported template | The ADML is written first, then the ADMX. A template is loaded only through its ADMX, so a crash after the first write leaves an orphan ADML that nothing loads. |
| D2 | Every file write | A torn file | A temp file in the same folder with `CreateNew` and no sharing, flushed to disk, then moved into place (`File.Move` for a new file, `File.Replace` for an existing one). A temp file is deleted when the write fails. |
| D3 | The ADMX write fails after the ADML was written | A partial copy | The ADML that this call created is removed again. A remove that fails is reported in the exception detail, and the leftover ADML is harmless (D1). An ADML that existed before the call is never removed. |
| D4 | The target is held open by another process (an MMC session, a virus scanner) | A failed replace | Reason `InUse` (Win32 32 or 33), the temp file is removed, the old file is untouched. |
| D5 | The target is read-only | A refused replace | Reason `AccessDenied`. The attribute is never cleared. |
| D6 | The target name is a folder | A move onto a folder | Reason `Io`, nothing changed, the ADML rolled back (D3). |
| D7 | The disk is full or the write fails midway | A truncated file | The temp file never becomes the target. Reason `Io` with the HRESULT, temp removed. |
| D8 | A file name with `..`, a separator, a drive, or an alternate stream | A write outside the store | Refused before any I/O with `UnsafeName`. The writer also resolves the full path and requires that its parent is exactly the store or a language folder directly under it. |
| D9 | A culture name with a separator or `..` | A write outside the store | The culture must be a single safe name segment, else `UnsafeName`. |
| D10 | The store is replaced by a junction between the check and the write | A write through a link | The reparse check runs again right before each write (A5). The window between that check and the write is a known gap that the protected ACL closes: only administrators can swap a folder inside the store. |
| D11 | The write needs a language folder that does not exist | A failure | The folder is created with the same protected DACL. |
| D12 | A second process imports at the same time | Two writers on one file | The second replace waits for no one: a move onto an existing file fails (`InUse` or `Io`) and the temp is removed. No lock is taken. |
| D13 | Cancellation during the copy | A half write | The token is checked before the first write only. A write that started finishes or rolls back. |

## E. Remove

| ID | Case | Risk | Handling |
| --- | --- | --- | --- |
| E1 | The namespace is not in the store (system only, or unknown) | Deleting a system file | `NotImported`. The writer has no way to name a system path. |
| E2 | Another imported template uses this namespace | A dependent that no longer links | `UsedByTemplate` with the dependent file, and nothing is deleted. |
| E3 | The ADMX is removed, then an ADML delete fails | A half removal | The ADMX goes first, so the template is gone from the set. A failed ADML delete is reported and the orphan is harmless. |
| E4 | The template has ADMLs in several language folders | One left behind | Every direct language folder is searched for `<name>.adml`. A language folder that is a reparse point is skipped, never followed. |
| E5 | The ADMX is open in another process | A failed delete | Reason `InUse`. |
| E6 | The file name from the caller has a separator or `..` | A delete outside the store | `UnsafeName`, same checks as D8. |
| E7 | Remove by file name of a template that does not load | A broken file that cannot be removed | `RemoveFile` takes the file name and does not need the file to load. |

## F. ACL

| ID | Case | Risk | Handling |
| --- | --- | --- | --- |
| F1 | Ship rules in a non-elevated process | Cannot set the owner Administrators | A typed `AccessDenied` or `OwnerNotAllowed` (A9). The unit test checks the security descriptor `ProtectedAcl.Ship().NewSecurity()` in memory: protected, owner Administrators, SYSTEM and Administrators only. |
| F2 | Sandbox rules (current user in place of Administrators) | The tests and E2E need a writable store | `ForCurrentUser()` creates the folder for real and the test reads the ACL back: inheritance off, current user and SYSTEM full control, no other entry. |
| F3 | The ACL class is linked from the update project | A copy that drifts | Linked by `<Compile Include ... Link>` in `Bwc.Gpo.csproj` (once per dependency chain), not copied, so the rules stay one source. |
| F4 | A parent folder that exists and is writable by users | A swap of the store folder | Detected by the owner and DACL check of the store folder (A3). A swapped folder is owned by the swapper. |

## H. Protected data root (AF5, PD-1 and STORE-1)

The store sits in the BWC data root, `%ProgramData%\BetterWindowsConfig`. Standard users may create folders in
`%ProgramData%`, so a user can pre-create that root and own it, then rename the administrator-only store away, plant a
folder that fails the check, or swap in a junction between the folder check and the temp file create. The shared helper
`Bwc.Gpo\Store\ProtectedDataRoot.cs` (see `gpo-store.md`, section K) checks the chain from the root (the parent of the
store folder) to the folder being used. The chain is the root, the store and the language folder.

| ID | Case | Risk | Handling |
| --- | --- | --- | --- |
| H1 | The data root does not exist | A first import looks like an error | The elevated writer creates it with the protected access list (`ProtectedAcl.NewSecurity()`), then the store, then the language folder. Reading treats a missing root as a missing store (A1). |
| H2 | The data root is a junction | Every write goes through the link | Refused with `ReparsePoint`, the message names the root. Nothing is created through it and the target stays untouched. Reading reports the store as `ReparsePoint` and does not load it. |
| H3 | The data root is owned by another user | The owner can rename the store away or plant a folder | Refused with `OwnerNotAllowed` and Win32 1307, the same reason an owner the token cannot hold gives (A9); the message names the root. Never repaired. Reading reports `AclRejected` and does not load the store. |
| H4 | The data root has an allow entry that grants write to another trustee (for example `BUILTIN\Users`), with a trusted owner | Same as H3 | Refused with `StoreAclRejected`, the message names the root. Never repaired. |
| H5 | A language folder (or the store) is a junction | A write through a link between the check and the temp file | Refused with `ReparsePoint`, the message names that folder. The check runs on the whole chain before the folder is created and again before every file write. The junction target stays untouched. |
| H6 | A refusal | The user cannot tell which folder is wrong | The `TemplateStoreException` message and `Path` name the folder that failed. |
| H7 | A device name with a superscript digit: COM or LPT followed by U+00B9, U+00B2 or U+00B3 (six names) | Windows reserves these like `COM1` and `LPT1` | `TemplateFileNames.IsSafeSegment` refuses them, with or without an extension, in any case (STORE-1). |

## Blind review additions

Added by the blind test-first pass (tests in `tests/Bwc.Policy.Tests/Blind/FilterStoreBlindTests.cs`). Each item is a way the
store could fail that the sections above do not state.

| ID | Case | Risk | Handling |
| --- | --- | --- | --- |
| G1 | `Check` on a good template when the store does not exist | A check creates the store | `Check` never writes anything, not even the store folder. |
| G2 | A refused `Import` | A refused import leaves a folder or a file behind | A refusal writes nothing. A store folder that did not exist still does not exist. |
| G3 | The same file imported twice | A silent overwrite | The second import is `NamespaceAlreadyImported` and the store bytes are unchanged. |
| G4 | An import then a removal then an import | A name that stays reserved | After `Remove` the namespace can be imported again, and the store holds no template, no ADML and no temp file. |
| G5 | The extension of the source is upper case (`Foo.ADMX`) | The store holds an extension the loader may not match, or the name differs | The store name keeps the stem and has a lowercase `.admx`. The ADML name matches the stem. |
| G6 | The ADML copied | A changed byte | The ADMX and the ADML in the store are byte equal to the checked source bytes. |
| G7 | `Check` and `Import` with a null path | A crash | A null path is `SourceMissing`, like an empty one. |
| G8 | A namespace given to `Remove` in another case | A template that cannot be removed | The namespace is compared ignoring case (loader rule 30). |
| G9 | `TemplateSources.Load` twice | A different result for the same folders | Same files, failures and `Shadowed` in the same order. `ImportedFiles` all have `SourceIndex` 1 (`StoreSourceIndex`). |
| G10 | The name rules of `TemplateFileNames` | A name that Windows reads as a device or a stream | `CON`, `NUL`, `COM1`, `LPT9` (with or without an extension), a trailing dot or space, a leading space, `:`, `*`, `?`, a separator, a control character, an empty name and a name over 255 characters are not safe. A name such as `Win-Defender.admx` or one with a non-ASCII letter is safe. `IsSafeAdmxFileName` also needs a stem and the `.admx` extension (any case). |
| G11 | Store root text with `..` segments or a trailing separator | A root that is not the folder it names | The root is normalized to a full path (`StoreRoot`), so `a\b\..\store\` and `a\store` are the same. |
| G12 | `EnsureStore` twice | The second call changes the ACL or fails | It is idempotent and the folder keeps its rules. |
| G13 | A stray temp file in the store (`name.<guid>.tmp`) | A failure or a template from a temp file | The loader ignores it and reports nothing. |

## I. Read integrity and the source folder cap (G6, findings F16 and F17)

Written before the fix by the G6 tests-first pass. Tests: `tests/Bwc.Policy.Tests/TemplateImportIntegrityTests.cs`. The rule in
the page header, "every import is a copy of bytes that were read once, checked, and read again to prove they did not change",
is not what the code does. `Check` reads the chosen ADMX (read 1), then `TemplateLoader` opens and parses it again (read 2), then
`Check` reads it a third time (read 3) and compares read 3 with read 1. The loader's bytes (read 2) are never compared with
anything, and the ADML is read once more after the load and compared with nothing. A source that is X at reads 1 and 3 and a
different valid file at read 2 passes the check with the bytes of X, which were never parsed. After the copy `Import` loads the
store again (C22), which catches most of these and rolls the copy back, but the answer of `Check` (what the dialog shows as
"this file can be imported") is wrong, and a pair that the verify step cannot tell apart is kept.

Rule for the fix, whatever its form (one read that is parsed and written, or a comparison of what the loader parsed with what
is written): a check that passes is for the bytes that `Check.Admx` and `Check.Adml` hold, and those are the bytes that were
parsed. A source that cannot be shown to be the parsed one is refused with `SourceChanged`, and nothing is written.

| ID | Case | Risk | Handling |
| --- | --- | --- | --- |
| I1 | The ADMX is X at the first read and at the final read and a valid P while the loader parses it (a flip-flop) | The check reports P's namespace and usings, and the store gets X, which was never parsed. X may use a namespace that only a neighbour defines (C15) or be malformed | `Check` passes only when `Check.Admx` equals the bytes that were parsed. A check that parsed P and would write X is `SourceChanged` (or X is the file that was parsed, and then its own problems show). Import: nothing is written. Test `Check_SourceThatIsOtherBytesWhileItLoadsIsNotChecked_I1` fails today; `Import_SourceThatIsOtherBytesWhileItLoadsLeavesNothingInTheStore_I1` passes today only because the verify step rolls the copy back (C22), and stays as a guard. |
| I2 | The ADML changes after the load and before it is read for the copy | The copied ADML is not the parsed ADML. The load never sees it | The ADML bytes that are written are the ADML bytes that were parsed, or the check is refused with `SourceChanged`. Test `Check_AdmlThatChangesAfterTheLoadIsNotCopiedUnchecked_I2` fails today; the import-level test is a guard (verify catches a malformed copy). |
| I3 | The ADMX changes after the load and stays changed | A copy of other bytes than the parsed ones | Refused as `SourceChanged` today (read 3 differs from read 1). Must stay refused. Guard `Check_AdmxThatChangesAfterTheLoadIsRefusedOrConsistent_I3`. |
| I4 | The ADMX changes between the first read and the load and stays changed | Same | Refused as `SourceChanged` today (read 3 differs from read 1). Must stay refused. Guard `Check_AdmxThatChangesBeforeTheLoadIsRefusedOrConsistent_I4`. |
| I5 | The source does not change | The fix breaks the normal import | The pair is copied byte for byte (G6), `Import` returns `Imported`. Guard `Import_UnchangedSourceStillImportsByteForByte_I5`. |
| I6 | The chosen file sits in a folder with more ADMX files than the cap | The check loads every ADMX of the folder (it has to, to see the neighbours for C15 and C18), so a folder with a very large number of files makes the check read, parse and keep all of them: time, memory, and a long wait in the dialog | The check counts the `.admx` files (any case) of the source folder before it loads anything. More than the cap is `SourceFolderTooLarge` and nothing is read, written or created. Cap: **1000** ADMX files in the chosen file's folder, the chosen file included (the system folder of a machine holds about 600, the Central Store of a large organisation fewer than 1000). The value belongs in a named constant of `Bwc.Policy` (the audit asked for a cap, the number is a G6 decision and can be changed in one place; the test has the same number as a literal). Tests `Check_SourceFolderOverTheCapIsRefused_I6`, `Import_SourceFolderOverTheCapWritesNothing_I6` fail today. |
| I7 | A folder at the cap, or with many files that are not ADMX files | An off-by-one refusal, or files counted that the loader never reads | Exactly the cap is accepted (`Check_SourceFolderAtTheCapIsAccepted_I7`). Only files with the `.admx` extension count (`Check_FilesThatAreNotAdmxDoNotCountTowardTheCap_I7`). Both pass today and are guards. The count never follows a folder (the loader reads only the files of the one folder, language folders are read by name, not listed). |
| I8 | The F16 tests need to change a file between two reads that happen inside `Check` | A race test would be flaky | `TemplateImport` gets an internal constructor `TemplateImport(TemplateSources, Action<TemplateImportStage>)` (visible to `Bwc.Policy.Tests`). The observer is called with `AdmxRead` right after the chosen ADMX was read and before the load starts, and with `Loaded` right after the load returned and before any later read. Each is called once per `Check`, and `Import` calls `Check` once. The seam changes no behavior and is not public. Written here as the contract, because a test that cannot see its stages fails for a wrong reason: every test asserts the two stages first. |
| I9 | `Check` has to refuse a folder over the cap while the source is also being replaced | Two refusals of one check | The cap is checked first. Only one problem, `SourceFolderTooLarge`, is returned. |
| I10 | The cap is checked on a folder whose listing fails (access denied, vanished) | A crash instead of a typed refusal | `SourceUnreadable` with the HRESULT (C7), as today. The cap never throws. |

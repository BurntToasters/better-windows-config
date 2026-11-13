# Registry writer failure modes

This document covers `src/Bwc.Registry`: `RegistryPath`, `RegistryWriter` and its partial files,
the write policies, the hive file scanner, and the static guard `scripts/check-registry-writer.js`.
It was written before the guard code and the guard tests, and it states the intended behavior for
every entry. Each entry names where it is enforced and which test covers it.

The writer is the only code that calls mutating registry APIs. Every mutation first asks
`IRegistryWritePolicy.EnsureWriteAllowed`. The sandbox policy (E2E flavor only) then requires an
extra guard: the writer walks the key path from the predefined root without following symbolic
links, so a link at any component is visible before anything is written.

## Threat model

The guard is a safety net against bugs in the app and in tests running in the same user session.
It is not a security boundary against hostile processes running as the same user, because those
processes can write `HKCU` directly. Section D lists the races that remain.

## Policies

| Policy | Flavor | Allows | Refuses |
| --- | --- | --- | --- |
| `ShippingWritePolicy` | all | every path, follows links, allows `REG_LINK` values | nothing (unchanged) |
| `SandboxWritePolicy` | `BWC_E2E` only | the sandbox root and descendants of `HKCU\Software\BWC-E2E` | everything else, links at any path component, renaming the root, copying from outside the sandbox, `REG_LINK` values |
| `DenyAllWritePolicy` | all | nothing | every mutating call, before any native code |

`RegeditWritePolicy` uses `SandboxWritePolicy` only when `BWC_E2E_SANDBOX_ROOT` names a valid
sandbox root. Otherwise it uses `DenyAllWritePolicy`. The old placeholder root
`HKCU\Software\BWC-E2E\unset` was writable and is no longer used.

## A. Escape paths (writes that could land outside the sandbox)

### A1. Path prefix confusion (`Foo` versus `Foobar`)

`RegistryPath.IsDescendantOf` compares components, not strings. `Software\Foobar` is not a
descendant of `Software\Foo`, and a sandbox root `...\run1` does not authorize `...\run10`.
Enforced by `RegistryPath` and `SandboxWritePolicy`. Test: `SandboxPolicyAllowsOnlyItsSubtree`,
`DescendantCheckComparesComponentsNotStringPrefixes`.

### A2. Symbolic link above the sandbox root

Example: `HKCU\Software\BWC-E2E\<run-id>` is a link to `HKCU\Software\Elsewhere`. The sandbox root
path passes the path check, but every write lands outside the sandbox. Intended behavior: the
guard walks every component from the predefined root (`HKCU`, then `Software`, `BWC-E2E`,
`<run-id>`, and so on) and refuses the write with access denied when any component is a link.
Test: `LinkAboveSandboxRootIsRefused`.

### A3. Symbolic link at the sandbox root

Example: `HKCU\Software\BWC-E2E\<run-id>` is a link. Intended behavior: every write that targets the
root or a descendant is refused. Test: `LinkAtSandboxRootIsRefused`.

### A4. Symbolic link inside the sandbox (intermediate component)

Example: `<root>\Mid` is a link to `<root>\..\Outside`. Writes to `<root>\Mid\Child` would reach
the outside key. Intended behavior: the chain walk refuses. Test: `LinkInMiddleOfPathIsRefused`
(SetValue, CreateKey, DeleteValue, DeleteKey, SaveKey, RestoreKey, ReplaceKey).

### A5. Symbolic link at the leaf

Example: `<root>\Leaf` is a link. Intended behavior: SetValue, DeleteValue, SetKeySecurity,
CreateKey (which would open the link), DeleteKey (recursive or not), RenameKey, and SaveKey are
refused when the target key is a link. Deleting or renaming a link node is refused too, because
the guard does not assume that the native call stops at the link node. Tests:
`LinkAtLeafIsRefusedForEveryKeyOperation`.

### A6. Link created by the code itself

The writer never sets `REG_OPTION_CREATE_LINK`, so it never creates a link key. The one way to
make a link through the writer is `SetValue` with type `REG_LINK` (6) on the link value name.
Intended behavior: the sandbox refuses `REG_LINK` values before native code runs, and the refusal
leaves no value behind (test `LinkValueIsRefusedBeforeNativeCodeAndLeavesNoValue`). `CreateKey`
opens an existing link node with `REG_OPTION_OPEN_LINK` and refuses it, so creating a key over a
link cannot follow the link.

### A7. Recursive delete whose subtree contains a link

Example: `DeleteKey(<root>\Parent, recursive: true)` where `<root>\Parent\Link` is a link to a key
outside the sandbox. `RegDeleteTree` opens children with ordinary (following) opens, so the guard
does not rely on it to stop at the link. Intended behavior: the guard scans the whole subtree with
non-following opens first and refuses the delete with access denied if any link is found. The
target and the link stay in place. Test: `RecursiveDeleteOverLinkIsRefusedAndTargetSurvives`.

### A8. Rename of the sandbox root or an ancestor

Example: `RenameKey(<root>, "Renamed")` moves the sandbox root out of its name, so every descendant
is outside the sandbox. Intended behavior: the sandbox policy refuses a rename whose path is the
sandbox root. Ancestors fail the containment check and are refused. Renaming a link node is refused
by A5. Tests: `RenameOfSandboxRootIsRefused`, `RenameOfAncestorIsRefused`.

### A9. CopyTree or CopyKey with a source outside the sandbox

The copy reads the source tree and writes the destination. A source outside the sandbox can carry
link keys into the sandbox, or can be used to read data the sandbox should not see. Intended
behavior: the writer asks the policy about the source with `RegistryWriteOperation.CopyTree` as well
as the destination, and `SandboxWritePolicy` refuses sources outside the sandbox. Test:
`CopyFromOutsideTheSandboxIsRefusedBeforeNativeCode`.

### A10. CopyTree whose source tree contains a link

Intended behavior: the guard scans the source subtree and refuses the copy with access denied if
any link is found, so no link key reaches the destination. Test:
`CopyTreeWithLinkInSourceIsRefused`.

### A11. CopyTree with a destination equal to or inside the source

Copying a tree into itself can recurse until the tree is huge. Intended behavior: the guard refuses
it with `ArgumentException` before native code. The check applies to every policy, shipping
included, because the native call would recurse into the tree it is writing. Tests:
`CopyIntoOwnSubtreeIsRefused` (sandbox) and `ShippingPolicyRefusesCopyIntoOwnSubtreeBeforeAnyNativeCall`.

### A11a. CopyTree whose destination subtree contains a link

`RegCopyTree` opens a destination child that has the same name as a source child with an ordinary,
following open. A link key already under the destination is therefore an escape route even when the
source tree is clean. Intended behavior: under the sandbox guard the destination subtree is walked
with the same no-follow check as the source, before native code runs, and the copy is refused with
access denied when any destination key is a link. The destination itself is opened through the
guard, so a link at the destination path is refused as before. Test:
`CopyTreeWithLinkInDestinationIsRefusedBeforeAnyCopy`.

### A12. RestoreKey or ReplaceKey with a hive file that contains a link

A hive file can contain link key nodes. Restoring it would bring links into the sandbox. Intended
behavior: before the native restore runs, `HiveFileLinkScanner` walks the key tree in the file
(root key, subkey lists `lf`, `lh`, `li`, and `ri`, with cycle and depth limits) and refuses the
restore with access denied when a node has the `KEY_SYM_LINK` flag. The target key is not
touched. Tests: `RestoreOfHiveWithLinkIsRefusedAndTargetIsUnchanged`,
`ReplaceOfHiveWithLinkIsRefusedBeforeBackup`.

Interpretation: for file sources the question is the content of the file, not where the file is.
A hive file is not a registry location, so the sandbox does not check its path. It checks the
content.

### A13. RestoreKey or ReplaceKey with a file the scanner cannot verify

Example: a file that is not a `regf` hive, a truncated file, a cell whose size does not fit its
`hbin`, a bin chain whose sizes do not add up to the declared bin data, or a subkey list that loops.
Intended behavior: the guard refuses the restore with
`ERROR_BAD_FORMAT` (11) and a detail that says the sandbox cannot verify the file. The guard fails
closed: anything it cannot read is treated as possibly containing a link. Test:
`RestoreOfUnverifiableFileIsRefused`. A file that the scanner verifies is held open for the rest of
the operation for `RestoreKey`: the scanner opens it with `FileShare.Read` and the handle stays open
until the native restore returns (see D3). `ReplaceKey` scans the new file and does not hold it,
because the native replace takes the file over. Test: `ScanHoldsTheHiveFileAgainstWritersUntilReleased`.

### A14. Hive mount and unmount in the sandbox

`LoadHive` and `UnloadHive` mount at a direct child of `HKLM` or `HKU`. Intended behavior:
`SandboxWritePolicy` refuses both before the privilege check, so a sandboxed test never mounts a
hive. Test: `HiveMountsAreRefusedBeforeAnyPrivilegeCheck`.

### A15. Predefined-handle aliases

`HKCU`, `HKEY_CURRENT_USER`, `Computer\HKEY_CURRENT_USER`, and `HKU\<sid>` can name the same key.
The sandbox compares the root identity, not the resolved key. Intended behavior: `HKCU` and
`HKEY_CURRENT_USER` (with or without the `Computer\` prefix) are the same root and are allowed.
`HKU\<sid>\Software\BWC-E2E\...` is a different root and is refused, even though it may resolve to
the same hive. A refusal is the safe direction. Test: `PredefinedAliasesOfTheSandboxRoot`.

### A16. Case, abbreviations, and trailing separators

Names compare with `OrdinalIgnoreCase`, which matches the registry's own case-insensitive
comparison for the ASCII names used here. `hkcu\software\bwc-e2e\run` is inside the sandbox. A
trailing backslash, a doubled backslash, or a leading backslash is rejected by `RegistryPath.Parse`
rather than trimmed, so two spellings of one path cannot compare differently. Tests:
`ParseRejectsInvalidInput`, `EqualityIsCaseInsensitiveAcrossRootAndComponents`.

### A17. `.`, `..`, and embedded NUL in names

`RegistryPath` rejects `.` and `..` components, separators, and NUL. A name with NUL would be cut
at the NUL by the native wide-string API, so the writer rejects it before native code. Tests:
`AppendRejectsUnsafeComponents`, `ValueNamesWithNulAreRejectedBeforeNativeCode`.

### A18. WOW64 redirection

Every open passes `KEY_WOW64_64KEY`, including the guard's probe opens. The sandbox check and the
open therefore see the same view, whatever the bitness of the process. Keys under
`HKCU\Software\BWC-E2E` do not redirect. Shipping behavior is unchanged.

### A19. Remote handles

The writer never connects to a remote computer, and no remote write exists. The unused
`RegistryWriteOperation.Remote` value and the unused `RegLoadAppKey` import were removed. The UI
parses remote display paths (`\\COMPUTER\HKEY_LOCAL_MACHINE\...`) into ordinary `RegistryPath`
values with local roots, so the engine cannot tell a remote node from a local one. An explicit
engine guard needs a computer identity on `RegistryPath`, which is an API change and is open. Until
then the UI must not send remote-tree nodes to a `RegistryWriter`. Remote tests: none for writes.

### A20. Raw handles and direct native calls

`IRegistryWritePolicy` has no handle overload, and handles are internal. The checker blocks native
write names outside `RegistryWriter*.cs` (section E).

### A21. Deny-all

`DenyAllWritePolicy` refuses every mutating operation before native code runs, so nothing is
created. Reads are unaffected. Intended behavior when the sandbox root is missing or invalid: the
`RegeditWritePolicy` uses `DenyAllWritePolicy` and never writes, including to the old placeholder
`HKCU\Software\BWC-E2E\unset`, which is no longer used. Tests:
`DenyAllRefusesEveryWriteWithoutTouchingTheRegistry`, `DenyAllRefusesTheUnsetPlaceholderPath`.
`RegeditWritePolicy` itself is in the WinUI project and is not referenced by the registry tests, so
its selection is checked by code review, not by a test.

## B. Wrongful denials (writes that should succeed)

### B1. Ordinary keys and values in the sandbox

Creating, setting, reading back, renaming, and deleting ordinary keys and values in the run root
must keep working. The E2E suite depends on this. The chain walk opens existing components with
non-following opens and does not create anything. Tests: `CreateSetReadDeleteRenameAndCopyWork...`
(existing), `TypedSettersRoundTripThroughTheRealRegistry` (existing).

### B2. A missing component is not a denial

A missing component produces the native not-found error (2 or 3), not access denied. A missing
parent for `CreateKey` is still not found. Test: `MissingComponentReportsNotFoundNotDenied`.

### B3. Shipping behavior

`ShippingWritePolicy.RejectsSymbolicLinks` is false. Shipping writes follow links, use the native
open path, and do not run the chain walk. Test: `ShippingShipsTheDenyAllPolicyAndKeepsLinkFollowingBehavior`
(tests/Bwc.Registry.Shipping.Tests).

### B4. Probe failures

When the guard cannot read `SymbolicLinkValue` on a node (for example, the caller lacks
`KEY_QUERY_VALUE`), the guard refuses. The sandbox keys are created with full access, so this does
not affect the E2E suite. It is a deliberate fail-closed choice.

### B5. Scanner false positives

The hive scanner follows only subkey lists from the root key. It does not read value data, so a
binary value that happens to contain `nk` bytes does not count as a key node. An unreachable key
node is not loaded by a restore, and the scanner does not look at it.

### B6. Hives with several pages per bin

A hive bin (`hbin`) is a multiple of 4 KiB and may span several pages. A cell can sit in the second
or later page of a bin, where the page header is not an `hbin` signature. Intended behavior: the
scanner walks the bin chain from offset zero, checks each `hbin` header and size, and maps each cell
offset to the bin that contains it. A valid large hive is scanned and accepted when it has no link.
A chain that breaks (a bin size that is not a multiple of 4 KiB, a bin that runs past the bin data,
or a missing `hbin` header) is unverifiable and refused. Tests: `ScanAcceptsAHiveWhoseBinSpansSeveralPages`,
`ScanFindsALinkInASecondPageOfABin`, `ScanRefusesABinChainThatDoesNotAddUp`.

## C. Native behavior the design depends on

These statements are the design's assumptions. Each one has a test in the suite that fails if the
assumption is wrong. Items marked as measured were checked on this Windows build during development.

- C1. `RegOpenKeyEx(predefined root, "")` returns a real key handle that serves as the base of the
  walk. Covered by every guarded write test.
- C2. Ordinary opens follow links (measured). `NtOpenKeyEx` with `REG_OPTION_OPEN_LINK` also
  follows the link on this build, in both relative and absolute form (measured), so the guard does
  not use it. The guard opens a child as a link node with `RegCreateKeyEx` and `REG_OPTION_OPEN_LINK`,
  which returns the link node itself (measured). Covered by `ReadsFollowLinksIntoTheirTarget` and
  every link refusal test.
- C3. A link node holds a value named `SymbolicLinkValue` of type `REG_LINK`, and nothing else
  marks it as a link. The guard uses that value to tell a link from an ordinary key. Covered by
  `NormalKeyWithValueNamedLikeLinkIsNotALink`.
- C4. `RegCreateKeyEx` with `REG_OPTION_OPEN_LINK` creates a normal key when no key exists. The
  guard therefore confirms that the child is listed by its parent before it calls it, so a probe
  never creates a key on the path it checks (see D2 for the residual race).
- C5. `RegDeleteKeyEx` on a link name fails with access denied on this build (measured), so it
  cannot remove a link. `NtDeleteKey` on a handle opened with `REG_OPTION_OPEN_LINK` removes only
  the link node and leaves the target (measured). The test fixture uses the second form for cleanup,
  and the writer never removes a link. Covered by `LinkNodeRemovalLeavesTheTargetAlone`.
- C6. A key listed by enumeration of its parent is found by name, including a link node. The guard
  uses that listing to confirm a name, so it does not depend on following the link.

## D. Races (residual)

- D1. A link or a rename created by another process after the walk. Handles that the writer already
  holds refer to nodes it verified, so a link created at a component after verification is not
  followed by those handles. A component renamed out of the sandbox after verification still
  receives writes through the held handle. Processes running as the same user can write `HKCU`
  directly anyway, so this is documented, not prevented.
- D2. The guard checks a child by listing its parent and then opening it with `REG_OPTION_OPEN_LINK`.
  A link or a deletion can appear between the two calls. If the child disappears in that window, the
  open creates an empty normal key, and the probe leaves it in place. The key is inside the sandbox.
  A later native call on that path then acts on the new empty key, so a delete reports success where
  regedit would report not found. This is the one write the probe can make, and it happens only in
  the race.
- D3. Restore: closed. The hive file could change between the scan and the native restore when the
  scanner closed its handle. `RestoreKey` now keeps the file open with `FileShare.Read` from the scan
  until the native call returns, so a writer cannot open it for write, delete it, or rename it in that
  window. Replace: open. `RegReplaceKey` takes over the new file, so a sharing lock on it would stop
  the native call. `ReplaceKey` therefore scans the file and does not hold it, and a writer can still
  change the file between the scan and the replace.
- D4. Concurrent hive operations and the process privilege. `PrivilegeScope` enables
  `SeBackupPrivilege` and `SeRestorePrivilege` on the process token, which is process-wide. Two
  hive operations on different threads could restore a privilege state that the other still needs.
  Intended behavior: every hive operation in `RegistryWriter` runs under one lock, so the privilege
  is enabled and disabled by one operation at a time. The lock also serializes the scan and the
  native call of one operation with the next. No automated test forces the interleaving; the rule is
  checked by review of `RegistryWriter.Hives.cs`.
- D5. Permission propagation is not atomic. See section I.

## E. Static guard (`scripts/check-registry-writer.js`)

The checker scans every `.cs` file and every `NativeMethods.txt` under `src/` and `tests/`, skipping
`bin/` and `obj/`. `tools/` and `spikes/` are not scanned; `tools/Bwc.Oracle.Capture` writes with
`NtSetValueKey` by design and is a separate review. The writer files `RegistryWriter*.cs` in
`src/Bwc.Registry` are exempt. `NativeMethods.txt` in `src/Bwc.Registry` is exempt because it lists
the writer's own native surface.

Exemptions are explicit and recorded in the checker with a reason:
- Native write names: one file, `tests/Bwc.Registry.Tests/RegistryLinkFixture.cs`.
- Managed writes: `src/` has none. In `tests/`, a fixed list of files, the registry test files
  that seed sandbox keys and the E2E files that seed keys through the harness.
- Reg process launches: none.

- E1. Native write names, with `A`, `W`, `Ex`, `ExA`, and `ExW` suffixes: `RegSetValue`,
  `RegSetValueEx`, `RegCreateKey`, `RegCreateKeyEx`, `RegCreateKeyTransacted`, `RegDeleteKey`,
  `RegDeleteKeyEx`, `RegDeleteKeyTransacted`, `RegDeleteKeyValue`, `RegDeleteValue`, `RegDeleteTree`,
  `RegRenameKey`, `RegCopyTree`, `RegLoadKey`, `RegLoadAppKey`, `RegUnLoadKey`, `RegSaveKey`,
  `RegSaveKeyEx`, `RegReplaceKey`, `RegRestoreKey`, `RegSetKeySecurity`, `RegSetKeyValue`,
  `RegOverridePredefKey`.
- E2. Native NT and Zw writes: `NtSetValueKey`, `NtCreateKey`, `NtCreateKeyTransacted`,
  `NtDeleteKey`, `NtDeleteKeyTransacted`, `NtDeleteValueKey`, `NtRenameKey`, `NtLoadKey`,
  `NtLoadKey2`, `NtLoadKeyEx`, `NtUnloadKey`, `NtUnloadKey2`, `NtUnloadKeyEx`, `NtSaveKey`,
  `NtSaveKeyEx`, `NtSaveMergedKeys`, `NtReplaceKey`, `NtRestoreKey`, `NtSetInformationKey`, and the
  same names with `Zw`.
- E3. Shell and shlwapi writes: `SHSetValue`, `SHDeleteKey`, `SHDeleteValue`, `SHCopyKey`,
  `SHRegSetUSValue`, `SHRegWriteUSValue`, `SHRegDeleteUSValue`, `SHRegCreateUSKey`,
  `SHRegOpenUSKey`, `SHRegDeleteEmptyUSKey`, `SHDeleteEmptyKey`, with `A` or `W`.
- E4. A string literal whose whole content is one of the names above is matched. This covers
  `EntryPoint = "RegSetValueExW"` in `DllImport` declarations. Prose inside string literals is not
  matched, so a message such as `"RegSaveKey did not create the hive"` passes.
- E5. Managed writes in files that reference managed registry types (`RegistryKey`,
  `Microsoft.Win32.Registry`, `Registry.CurrentUser`, `Registry.SetValue`, and similar): `SetValue`,
  `CreateSubKey`, `DeleteSubKey`, `DeleteSubKeyTree`, `DeleteValue`, `SetAccessControl`,
  `OpenSubKey` with `writable: true` or a `true` second argument, and `ReadWriteSubTree`. In `src/`
  nothing is exempt. In `tests/`, only the files named in the checker's allowlist are exempt, and
  each entry records a reason.
- E6. Process launches of `reg` or `regedit` with a write verb (`add`, `delete`, `copy`, `save`,
  `restore`, `load`, `unload`, `import`) or with `/s`, and any `reg` or `regedit` executable literal
  in a file that starts processes.
- E7. Comments are stripped before matching. A line comment or a block comment never triggers the
  checker.

Known gaps: reflection, names built at run time, native libraries, generated code outside `obj/`,
and managed writes through a `RegistryKey` that arrives without a registry-type reference in the
same file. `tools/` and `spikes/` are not scanned.

`--self-test` writes a temporary tree with violating files for each rule family (native names with
`W`, `Ex`, `Nt`, and `SH` forms, a DllImport entry point literal, a NativeMethods entry, managed
writes, a `reg.exe` launch, and a `reg add` command line), one file in the writer directory that
must pass, and one clean file with only comments and prose. The scan must report exactly the expected
violations and nothing from the clean file or the writer file. The self-test is embedded in the script.

## F. Test and cleanup hazards

- F1. Every test writes only under `HKCU\Software\BWC-E2E\<run-id>`. The "outside" keys that link
  tests and copy tests use are sibling keys inside the same run tree, but outside the sandbox root
  the policy was given, so a broken guard still cannot touch another run or another test.
- F2. Link fixtures create and remove link nodes with native calls from one test file,
  `RegistryLinkFixture.cs`, which is the only native-write exemption in the registry test project.
  Cleanup removes link nodes with `NtDeleteKey` on a handle opened with `REG_OPTION_OPEN_LINK`
  before the run root is deleted through the writer, because a recursive delete over a link is
  refused by design.
- F3. The elevated round trip (`ElevatedSaveAndRestoreRoundTripStaysInsideTheSandbox`) keeps only
  the sandboxed `SaveKey` and `RestoreKey` parts. Hive mounts (`LoadHive` and `UnloadHive`) need
  `HKLM` or `HKU`, which the sandbox refuses, so no unit test mounts a hive. The argument-validation
  tests in `RegistryHiveTests` still use `ShippingWritePolicy`, because every call in them fails in
  argument validation or the privilege check before any native call. No automated test in this
  batch mounts a hive. The E2E hive suite only checks that a cancelled load leaves nothing mounted.
  Mount behavior needs a manual elevated run, which is outside the acceptance run.
- F4. Each test removes its run root in `finally`. The parent `HKCU\Software\BWC-E2E` may remain
  empty, and the acceptance check looks for child keys.

## I. Permission propagation (`SetKeySecurity` with `propagateToSubkeys`)

The Permissions dialog can replace the descriptor of a key and of every key below it. The
propagating overload walks the tree itself and applies the same self-relative descriptor to each key,
so every step goes through the write policy and the link guard. It does not call
`TreeSetNamedSecurityInfo`, which would compute inherited entries per child; see I5.

- I1. Sandbox refusal before any change. Under the sandbox guard the subtree is scanned for links
  before the first key is changed, and the call is refused with access denied when any key below
  the target is a link. A refused propagation changes nothing. Test:
  `SetKeySecurityPropagationRefusesLinkedSubtreeAndChangesNothing`.
- I2. Policy per key. Each key is checked with `EnsureWriteAllowed` for `SetKeySecurity`. A
  sandbox policy therefore refuses a propagation that would reach a key outside the sandbox root.
  Test: `SetKeySecurityPropagationStaysInsideTheSandbox`.
- I3. Partial propagation. A failure on one key stops the walk and throws the Win32 error for that
  key. Keys already changed keep the new descriptor, as classic `TreeSetNamedSecurityInfo` can leave
  them. The writer does not roll back, because a rollback could undo a later user change. No automated
  test: a key whose WRITE_DAC is denied cannot be set up in the sandbox, because the owner of a sandbox
  key keeps WRITE_DAC. The rule is checked by review of `RegistryWriter.Security.cs`.
- I4. Cancellation. The token is checked before each key. A cancelled propagation keeps the keys it
  already changed and throws `OperationCanceledException`. Progress is reported after each key with
  the key path and the running count. Tests: `SetKeySecurityPropagationCanBeCancelledAfterTheCurrentKey`
  and `SetKeySecurityPropagationAppliesToEveryKeyAndReportsEachOne` (progress paths).
- I5. Inheritance. Classic "Replace all child object permission entries" recomputes inherited ACEs
  for each child. This walk does the same in managed code: each descendant receives only the entries
  its parent passes down (`RegistrySecurityInheritance`, `failure-modes/permissions.md` section 18), and
  the parent's descriptor is not copied. The walk does not call `TreeSetNamedSecurityInfo`, so the
  computed inherited entries may differ from classic output in edge cases. Recorded here until the
  owner decides whether to switch to `TreeSetNamedSecurityInfo`.
- I7. The walk lists each key's subkeys before that key's DACL changes, so a key whose new DACL denies
  the current user enumeration is still walked. A child left with no entries for the current user is
  recovered by its owner with `WRITE_DAC` (`failure-modes/permissions.md` section 26). The propagation
  is not refused for that reason. No lockout guard exists.
- I6. A non-propagating call is unchanged. The existing `SetKeySecurity(path, information,
  descriptor)` overload keeps its behavior and signature. Test: existing `RegistrySecurityTests`.

## G. Retained failure modes

### G1. Value name length

Registry value names are limited to 16383 characters. The writer rejects longer names with
`ArgumentException` before native code. Key components are limited to 255 characters by
`RegistryPath`.

### G2. Default value

The default value has an empty name. `SetValue` and `DeleteValue` accept `""`. `null` is rejected.

### G3. ERROR_MORE_DATA

The writer is write-only. `RegistryOperationException` keeps the raw Win32 code, and its `HResult`
is `0x8007xxxx`, so a retryable buffer problem can be told apart from a permanent failure.

### G4. Handle leaks

Every opened key is wrapped in `SafeRegistryKeyHandle`, which releases with `RegCloseKey`.
Predefined roots are non-owning handles. The guard's probe handles and its root handle are
disposed on every path, including failures.

### G5. Partial delete and partial copy

`RegDeleteTree` and `RegCopyTree` can fail after changing some keys. The writer reports the Win32
error and does not clean up, because cleanup could delete user data. The guard refuses trees with
links before either call, so the partial state cannot include a link from the source.

`RegCopyTree` copies the source security descriptor. The destination handle needs `WRITE_DAC`.

### G6. Rename across parents

`RenameKey` takes a single new name. A name with a separator is rejected, so a rename cannot move a
key to another parent.

### G7. WOW64 view

Every open uses `KEY_WOW64_64KEY`. The writer never uses `KEY_WOW64_32KEY`.

### G8. 32-bit emulation on ARM64

The explicit 64-bit flag makes the writer independent of process bitness. The ARM64 build is the
acceptance build.

### G9. SandboxWritePolicy in shipping

`SandboxWritePolicy` and the guard's sandbox-only code compile only under `BWC_E2E`. The shipping
test asserts by reflection that the sandbox policy type is absent. `DenyAllWritePolicy` is public
in both flavors because it refuses writes.

### G10. Test cleanup failure

Each registry test removes `HKCU\Software\BWC-E2E\<run-id>` in `finally`. If cleanup throws, the
test fails instead of hiding the leak.

## H. Checker self-test and false positives (retained)

Comments are stripped while string and character literals are respected, so a URL such as
`https://example` does not hide a later call on the same line. The checker's self-test is part of
the acceptance run. A self-test that passes silently would mean the scan is broken.

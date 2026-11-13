# Permissions failure modes

This document covers the B10 security descriptor engine in `src/Bwc.Registry` and the R11
permissions dialog in `src/Bwc.Regedit`. Permissions changes are safety-critical: a bad DACL,
owner, or control flag can lock a user out of a key or grant access to the wrong principal.

## 1. Wrong key or selection

The dialog always receives an explicit `RegistryPath` parsed from the selected tree node. It never
reads the list selection and never falls back to a previous key. When no tree key is selected, the
command is disabled instead of opening a dialog for an unrelated path.

A value row selection is not a key selection. Edit > Permissions is disabled while value rows are
selected, and the value context menu never contains a Permissions command.

## 2. Predefined roots

`RegistryPath` roots are valid paths, so the command may be enabled for HKEY_CURRENT_USER or
HKEY_LOCAL_MACHINE. Opening the dialog is allowed, but the write policy or the operating system may
reject the final write. The error is reported with the HRESULT and Win32 code. Roots are never
silently rewritten to a child path.

## 3. Access denied

Reading a descriptor needs `READ_CONTROL`; writing one needs `WRITE_DAC`, and changing the owner
needs `WRITE_OWNER`. A key that grants none of these produces `ERROR_ACCESS_DENIED` (5). The engine
maps it to `RegistryOperationException` with operation `GetKeySecurity` or `SetKeySecurity` and the
full path. The UI catches the exception and shows an error dialog instead of leaving a partially
open security editor.

## 4. SACL and SeSecurityPrivilege

Reading or writing a SACL requires SeSecurityPrivilege, which a normal user process does not hold.
When aclui requests the SACL, `GetSecurity` first asks for every requested flag. If the read fails
with `ERROR_PRIVILEGE_NOT_HELD` (1314) and SACL was requested, it retries without SACL so the owner,
group, and DACL pages still open. The audit page cannot be saved in that state, and the failure is
reported by aclui rather than by a crash.

## 5. Owner, group, DACL, and protected flags

The requested flags are passed to `RegGetKeySecurity` and `RegSetKeySecurity` exactly as aclui
supplies them, translated to the Win32 `OBJECT_SECURITY_INFORMATION` values. Owner and group are
separate from the DACL. `ProtectedDacl` and `UnprotectedDacl` change inheritance behavior; they are
not access rights and must not be treated as ACE masks. Setting an owner to an account the caller
cannot assign fails with the native error.

## 6. Invalid descriptor length

`RegistrySecurityDescriptor.Validate` requires a non-empty buffer, `IsValidSecurityDescriptor` to
return true, a non-zero `GetSecurityDescriptorLength`, and a declared length that fits in the
buffer. `SetKeySecurity` additionally requires the declared length to equal the span length, so
trailing bytes cannot hide a second descriptor. Invalid input is rejected before a key handle is
opened or any write occurs. A descriptor returned by the registry that fails validation is reported
as `ERROR_INVALID_SECURITY_DESCR` (1338), not accepted.

## 7. Native descriptor ownership and double free

`GetSecurity` copies the managed descriptor into a `LocalAlloc` buffer because aclui releases the
pointer with `LocalFree`. The buffer is never freed by managed code after a successful return: aclui
owns it. `Dispose` frees only the COM access-right names, the access-right array, and the object
name. A double free here would corrupt the process heap and can crash on exit. The engine's own
`byte[]` is a separate copy and is not handed to aclui.

## 8. aclui owner and focus

`EditSecurity` receives the main window handle as its owner. aclui creates the modal property sheet
and disables the owner for the duration of the dialog. If the owner handle is zero, `Show` throws an
`ArgumentException` instead of creating an orphan dialog. The dialog is created on the UI thread;
the menu handler does not pump messages itself.

## 9. Cancel, OK, and partial saves

Cancel discards changes. OK calls `SetSecurity`, which validates the descriptor, calls the write
policy, and then calls `RegSetKeySecurity`. If the write fails, the HRESULT is returned to aclui and
the dialog remains open; the UI does not claim success. If aclui writes some pages and then fails on
another page, the registry can be partially updated. The engine cannot roll that back without
reading and restoring every descriptor first, so the error is surfaced and no automatic rollback is
attempted.

## 10. Policy denial

`RegistryWriter.SetKeySecurity` calls `IRegistryWritePolicy.EnsureWriteAllowed` before opening a
handle. Under a `SandboxWritePolicy`, a security write to any path outside the sandbox root fails
with `ERROR_ACCESS_DENIED` and operation `SetKeySecurity`. The check uses component comparison, so
`...\run1` does not authorize `...\run10`. A sandbox test may change the DACL of its own run key,
but never a key outside it.

## 11. Locking out the user

A DACL that removes the current user's own access does not lock the owner out. A registry key's owner
always has implicit `READ_CONTROL` and `WRITE_DAC` (no `OWNER RIGHTS` ACE is present), so the owner can
open the key with `WRITE_DAC` alone and write a new DACL, without elevation. Classic regedit allows the
same change. The engine does not silently add an administrator ACE or rewrite the owner. Recovery is
described in section 26. Tests grant full control to the current user and restore the original
descriptor in a `finally` block. The UI test cancels the dialog and never presses OK or Apply, so a UI
failure cannot change an ACL.

## 12. Inheritance

A protected DACL stops inheriting from the parent; an unprotected DACL resumes inheritance. aclui
may present both. A plain `SetKeySecurity` call (no propagation) changes only the named key and
does not update existing children, which matches `RegSetKeySecurity`. Recursive propagation is a
separate call (section 15) and is the only path that changes children.

## 13. E2E cleanup

E2E tests create a disposable key under `HKCU\Software\BWC-E2E\<run-id>` and delete the run root in
`finally`. They also remove the empty parent when possible and terminate every launched Regedit
process. A failed restore is a test failure, not a warning, because a changed ACL can outlive the
run. The engine tests delete their run trees through the shared walk described in section 26, which
restores the owner's full control on each key before its subkeys are listed, so a key left with no
entries for the current user is still removed. The UI tests only open and cancel the dialog.

## 14. Native calls and allocations

Read path: `RegOpenKeyEx` with `READ_CONTROL | KEY_WOW64_64KEY`, then `RegGetKeySecurity` twice (size
query and fill, retrying on `ERROR_INSUFFICIENT_BUFFER`), then `RegCloseKey` through the safe handle.
Write path: policy check, `RegOpenKeyEx` with `WRITE_DAC | WRITE_OWNER | READ_CONTROL |
KEY_WOW64_64KEY`, `RegSetKeySecurity`, then `RegCloseKey`. The only security descriptor allocation is
the `LocalAlloc` buffer returned to aclui; aclui frees it with `LocalFree`. No other native
allocation is made by the engine.

## 15. Propagation to subkeys (engine API)

The engine offers `RegistryWriter.SetKeySecurity(path, descriptor, information, propagateToSubkeys,
...)` for the "Replace all child object permission entries" check box. The walk, the per-key policy
check, the sandbox link refusal, cancellation, and progress are described in
`registry-writer.md` section I. Summary for the dialog owner:

- The key receives the descriptor exactly as given. Each descendant then receives only the entries
  it inherits from its parent: explicit entries are removed, the protected state is cleared, and
  inherited entries carry `INHERITED_ACE`. The inherited set is computed from the parent's DACL by
  the rules in `RegistrySecurityInheritance` (section 18), not by copying the parent's descriptor.
- The walk lists each key's subkeys before that key's DACL changes, so a key whose new DACL denies the
  current user enumeration is still walked. A key whose inherited entries give the current user no
  access stays readable only to its owner (section 26). The propagation does not refuse that case.
- Owner and group are copied to descendants only when the caller asks for them in `information`.
  SACL propagation is refused with `ArgumentException` (section 22).
- The first failure stops the walk and throws the Win32 error for that key. Keys already changed
  stay changed, with no rollback.
- The sandbox refuses a propagation whose subtree contains a link, before any key changes.
- Cancelling stops between keys and throws `OperationCanceledException`. Progress is reported
  after each key, including the root.

## 16. aclui flags for the Permissions dialog

The dialog passes `SI_EDIT_ALL | SI_ADVANCED | SI_CONTAINER | SI_RESET_DACL_TREE` in
`GetObjectInformation`. `SI_CONTAINER` makes the Permission Entry page offer the inheritance
choices and makes new ACEs container-inheritable. `SI_ADVANCED` opens the Advanced page with the
inheritance list. `SI_RESET_DACL_TREE` adds the "Replace all child object permission entries"
check box, which the classic MSG-176 text names. Without `SI_CONTAINER`, a new ACE is not inherited
by subkeys, which is the defect this section fixes.

- `SI_RESET_SACL_TREE` (classic MSG-177, audit replace) is not offered. The engine does not
  propagate SACLs (section 22), so the check box would have nothing to do.
- `SI_OWNER_RECURSE` is not offered. The classic inventory has no owner-recursion text (MSG-176 to
  MSG-179 cover only permission and audit replacement), so classic regedit does not show it.
- Test: `PermissionsPropagationTests` opens Advanced on the sandbox key and checks the inheritance
  list and the replace check box exist.

## 17. Inherit types (`GetInheritTypes`)

`GetInheritTypes` returned `E_NOTIMPL`, so the inheritance list in the permission editor had no
entries. The list is now three entries, with ACE flags from `RegistryInheritanceModes`:

- "This key only": no inheritance flags.
- "This key and subkeys": `CONTAINER_INHERIT_ACE`.
- "Subkeys only": `CONTAINER_INHERIT_ACE | INHERIT_ONLY_ACE`.

Failure: a flag value that does not match the ACE constants gives an ACE that a subkey does not
inherit, or that applies to the key itself when only subkeys were chosen. Test:
`InheritModesUseTheWin32AceFlags`. The `GetInheritTypes` COM method returns `S_OK` with
the same three entries. Test: `PermissionsPropagationTests` checks the Advanced page lists them.

## 18. Propagation semantics (classic "Replace all child object permission entries")

Failure: copying the parent's descriptor to every child gives each child the parent's explicit
entries and its protection state, so a child that was never meant to change gets explicit ACEs
and loses its own. The walk now computes each child's DACL from the parent's effective DACL.

- The explicit ACEs on a child are removed and replaced by the inherited ACEs of its parent.
- Rules, applied to each parent ACE in order: an ACE is passed down only with `CONTAINER_INHERIT_ACE`
  (keys have no object children, so an `OBJECT_INHERIT_ACE`-only entry does not apply). The copy is
  marked `INHERITED_ACE`. `INHERIT_ONLY_ACE` is cleared on the copy, because the child applies it.
  `NO_PROPAGATE_INHERIT_ACE` clears the inheritance flags on the copy, so the grandchildren do not
  receive it. The child's DACL is unprotected.
- A child can end with no entries for the current user, when the parent grants that user access only
  to the key itself (no `CONTAINER_INHERIT_ACE`). The propagation is not refused for that reason. The
  owner recovers the child with `WRITE_DAC` (section 26).
- Tests: `PropagationGivesEveryDescendantOnlyInheritedEntries`,
  `PropagationClearsTheProtectedStateOfChildren`,
  `PropagationRemovesTheUsersOwnAccessAndTheOwnerRecoversIt`. The cancellation and progress tests in
  `RegistryWriterHardeningTests` use an inheritable entry for the current user.

## 19. Propagation refused by the link guard

Under the sandbox, a propagation whose subtree contains a symbolic link is refused before the root
or any child is changed. Test: `SetKeySecurityPropagationRefusesLinkedSubtreeAndChangesNothing`.

## 20. Propagation fails part way

When a child fails (for example `WRITE_DAC` denied, or the system refuses the computed DACL), the
walk stops, the error is thrown with the child's path, and the keys already changed keep their new
entries. There is no rollback because a rollback could undo a change the user made after the
dialog opened. The dialog reports the Win32 code. No automated test: a sandbox key owned by the
test user always grants `WRITE_DAC`. The rule is reviewed in `RegistryWriter.Security.cs`.

## 21. Computing inherited entries fails

The inherited DACL is built in managed code, so a malformed parent or child descriptor fails with
`ArgumentException` from `RegistrySecurityDescriptor.Validate`. The engine maps it to
`RegistryOperationException` with the child path and `ERROR_INVALID_SECURITY_DESCR` (1338), and nothing
is written for that child. No automated test: the parent descriptor is always one the engine read
back from the registry, so a malformed parent cannot be produced through the public API.

An earlier version used `ConvertToAutoInheritPrivateObjectSecurity`. For a parent with an inheritable
entry it returned an empty, protected DACL with no inherited entries, so it was replaced and is not
used.

## 22. SACL and audit propagation

Propagating a SACL needs `SeSecurityPrivilege`, which a normal user process does not hold, and
audit entries are not inherited the same way. Passing `Sacl` together with a propagation is refused
with `ArgumentException` before any change. The dialog never sends it: the root audit change goes
through a separate non-propagating call. Test: `PropagationRefusesSaclBeforeAnyChange`.

## 23. Absolute descriptors from aclui

`RegistrySecurityDescriptor.CopyFromNative` assumed a self-relative descriptor. aclui can pass an
absolute one. The copy now converts an absolute descriptor with `MakeSelfRelativeSD` before the
length check, and a descriptor that cannot be converted is rejected with `ArgumentException`.
Tests: `CopyFromNativeConvertsAnAbsoluteDescriptor`, `CopyFromNativeRejectsANullPointer`.

## 24. Background write and reentrancy

The dialog runs the write on a thread-pool thread and waits on the UI thread while it pumps window
messages, so the window keeps painting during a long propagation. aclui can call `SetSecurity`
again while the first call is still pumping. The second call returns `E_PENDING` and changes
nothing. No automated test: aclui cannot be driven reentrantly. Reviewed in
`RegistryPermissionsDialog.cs`.

## 25. Classic error text

The Permissions dialog maps its failures to the classic regedit texts in `Resources.resw` through
`MessageCatalog`. Oracle ids are from `docs/parity/oracle/regedit-resources.md`; inventory ids are from
`docs/parity/regedit-inventory.md`.

- A save or propagation that fails with `ERROR_ACCESS_DENIED` shows MSG-184 (oracle 7070, "could not
  save the security information ... does not give you access to save such information").
- Any other save or propagation failure shows MSG-186 (oracle 7072, "could not set security in the key
  currently selected, or some of its subkeys."). The inventory lists MSG-186 for this case, and it was
  not in the first list of texts for this change.
- A failed retrieval keeps MSG-181 to MSG-183 (oracle 7050 to 7052).
- The "Replace all child object permission entries" check box is drawn by aclui, not by the Registry
  Editor. On this machine aclui labels it "Replace all child object permission entries", so the
  classic MSG-176 text (oracle 7015, "R&eplace Permission on Existing Subkeys") is not shown by any
  control the Registry Editor owns. It is not added to the resources.
- The confirmation before a propagation shows MSG-178 (oracle 7017) with the key path. Section 27.
- Test: the E2E `Permissions_AdvancedReplaceChildEntriesGivesChildrenOnlyInheritedEntries` answers the
  confirmation with Yes and records `permissions.propagation.replace-confirm`.

## 26. Propagation removes the user's own access (owner recovery)

Failure: the parent grants the current user access only to itself (for example `D:(A;;KA;;;<user>)`).
Propagating that DACL leaves each child with no entries, so the current user can no longer enumerate
the child's subkeys or query its values. A walk that lists a child's subkeys after changing the child
fails on that child, so the walk lists the subkeys first.

Recovery: the owner opens the child with `WRITE_DAC` alone and writes a DACL that grants the user access
again. Verified on this machine without elevation: keys whose DACL denied the user everything were opened
by the owner with `WRITE_DAC`, their DACL reset, and then deleted. Earlier failures came from opening a
key with broader access (enumerate or query) than `WRITE_DAC` alone.

Under the sandbox policy the engine cannot do this recovery. Its single-key `SetKeySecurity` opens the
key through the link guard, which asks the last key for `KEY_QUERY_VALUE` and `KEY_ENUMERATE_SUB_KEYS` as
well as `WRITE_DAC` and `READ_CONTROL`, and an emptied key denies those. The shipping policy opens the key
with `WRITE_DAC | READ_CONTROL` and no guard, and was not run on this machine. The test performs the
`WRITE_DAC`-only open through `RegistryLinkFixture.RestoreKeyOrRemoveLink`.

Test: `PropagationRemovesTheUsersOwnAccessAndTheOwnerRecoversIt` checks that the children are left with
no entries, that enumeration is denied to the user, and that a `WRITE_DAC` write restores enumeration.
The children in that test are leaves. Under the sandbox policy the link guard opens every ancestor of a
key with `KEY_READ` before it opens the key, so once the walk has emptied a child, a key below that child
cannot be opened and the propagation stops there with access denied (`ERROR_ACCESS_DENIED`). Keys already
changed keep their new entries (registry-writer.md I3). The shipping policy has no link guard and opens
the key by its path directly. That path is expected to reach the key, because the access check applies to the
key that is opened, but the shipping path is not exercised by these tests and was not verified here. The
sandbox limit could be removed by opening each child relative to its parent's handle; that is not done.

Test: `CleanupRemovesNestedKeysThatDenyTheCurrentUser` covers the cleanup of a nested key whose DACL is
empty, below a key that grants the user access only to itself.

Test cleanup: `SandboxRunCleanup.RemoveRunTree` walks a run tree depth-first. The run root's DACL is set
through the engine. Each child is then opened with `REG_OPTION_OPEN_LINK` and `WRITE_DAC` alone
(`RegistryLinkFixture.RestoreKeyOrRemoveLink`), given a DACL that grants the current user full control with
`CONTAINER_INHERIT_ACE`, and removed instead if it is a link node. Its subkeys are listed only after that,
and the walk recurses. The run root is deleted through the engine. The walk refuses any run id that is not
a single segment under `HKCU\Software\BWC-E2E`.

## 27. Replace confirmation (classic MSG-178)

Failure: the propagation runs without a confirmation, so one click replaces the permissions of every
subkey.

When the "Replace all child object permission entries" check box is set, the dialog asks the classic
MSG-178 question with the key path, with Yes and No buttons and No as the default. Answering No changes
nothing and returns `E_ABORT` to aclui. The question is shown on the UI thread from inside
`SetSecurity`, with a native `MessageBoxW` owned by the main window, because aclui calls back on the UI
thread and cannot wait for a ContentDialog. The text is `MsgReplaceSubkeysConfirm`; the caption is
`MsgCaptionRegistryEditor`. Test: the E2E permissions test records `permissions.propagation.replace-confirm`.
Whether aclui shows its own message after `E_ABORT` is not verified on this machine.

The question is raised by the Advanced OK (section 30), not by the sheet OK. The test answers it while the
Advanced window is still open.

## 28. Edit > Permissions activation in the E2E path

The Edit > Permissions item is enabled when a key is the target, and it runs `Permissions()` when
activated. These are the ways the E2E path can fail before the aclui dialog exists:

- The item is found and enabled, but a raw mouse click at the center of its bounding rectangle lands
  on a separator or on padding while the flyout is still opening. The command does not run, the flyout
  stays open, and no dialog appears. The E2E test clicks the item with the real mouse 0 ms after the flyout opens;
  that press does not activate the item. Measured: a 300 ms or 1000 ms settle before the same click passes.
- The command runs while a value row is selected in the list. Section 1 applies: the list target is
  refused with the classic "no selection" message and no aclui dialog opens.
- The command runs while another modal is open or the window handle is zero. `PermissionsAsync` logs
  and returns, so no dialog opens.
- The aclui dialog opens but is not owned by the main window, is not a #32770 window, or is not visible.
  The E2E finder accepts only a visible #32770 window of the Regedit process owned by the main window.
- The dialog opens after the 10 second wait. The E2E records it as missing.
- The Advanced sheet or the replace confirmation is not found. The child then keeps its explicit entry,
  so the child check fails as a consequence of the missing sheet. The propagation itself did not run.

## 29. Input during the flyout's opening (E2E settle)

WinUI drops a press that arrives while a menu flyout is still opening: the flyout stays open and the item
does not run. Classic menus accept a press at once. Measured on this machine with a real mouse click on
Edit > Permissions: a press 0 ms after the flyout opens did nothing; a press after 300 ms or 1000 ms opened
the dialog. The item's bounding rectangle was identical at 0 ms and 1000 ms. The WinUI cause was not
diagnosed. The E2E waits until the item is enabled and its bounds hold across two reads 150 ms apart
(at least 300 ms, at most 5 s) before the real mouse click. A press inside that window is lost to the user.
Test: `permissions.editmenu.settled` and `permissions.propagation.settled`.

## 30. Applies-to label for container and object inherit entries

Registry entries that HKCU inherits carry `CONTAINER_INHERIT_ACE | OBJECT_INHERIT_ACE` (flags 0x3). The
inherit table aclui uses has three entries, and classic regedit's table is the same as BWC's (measured in
`docs/parity/oracle/permissions-inherit.json`): "This key only" 0x0, "This key and subkeys" 0x2, "Subkeys only" 0xA.
The measured labels per flag set are: 0x0 and 0x1 "This key only", 0x2 and 0x3 "This key and subkeys", 0xA and 0xB
"Subkeys only", 0x9 "Special". Those labels match a table lookup that ignores OBJECT_INHERIT_ACE (0x1).

BWC showed "Special" for 0x3 with the same table, so aclui's lookup was not the difference. The difference is
`ISecurityInformation::MapGeneric`, which aclui calls with a pointer to the ACE flags. BWC left the flags alone.
`MapGeneric` now clears OBJECT_INHERIT_ACE, which a key cannot use. This explains the measured labels, and the E2E
check `permissions.propagation.advanced.container-controls` now passes. The inference is from the labels; classic's
`MapGeneric` was not read. The other flag sets (0x1, 0x9, 0xB) were not re-measured against BWC. Status: fixed; see
section 31 for the write-back question that is not measured.

Replace flow: pressing Advanced OK with the replace box checked makes aclui ask its own question, a `#32770`
window titled "Windows Security" owned by the Advanced window. The test answers it with Yes. BWC's `SetSecurity`
runs inside that OK, so the classic MSG-178 question is shown next, while the Advanced window is still open. It is
a message box owned by the main window, and it takes the foreground. Its text matches oracle string 178
(`Do you want to replace the permission on all existing subkeys within %1?`, `docs/parity/regedit-inventory.md`
MSG-178). The test answers it with Yes before it waits for the Advanced window to close, then presses the sheet OK.
Failure before this fix: the test waited for MSG-178 after the sheet OK, so the foreground check failed and the
Advanced window never closed.

## 31. MapGeneric clears OBJECT_INHERIT_ACE on write-back (not measured)

aclui may call `MapGeneric` with the ACE flags it is about to display or write. If aclui writes the cleared
flags back on OK, an explicit ACE with 0x1, 0x3 or 0xB would be saved as 0x0, 0x2 or 0xA. The oracle cannot
show this, because the oracle never presses OK (every window is cancelled). Classic's labels show the same
clearing, so classic probably behaves the same, but this is not measured. Before shipping the change to a
user's own ACLs, measure it on a scratch key under `HKCU\Software\BWC-E2E` in a sandbox test: an explicit 0x3
ACE, Advanced OK or sheet OK, then read the raw flags. Status: open.

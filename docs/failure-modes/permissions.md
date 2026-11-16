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

Reading or writing a SACL needs `ACCESS_SYSTEM_SECURITY` on the handle, which the system grants only to a token that holds
`SeSecurityPrivilege` (enabled). A normal user process does not hold it. The engine opens a key for a SACL read or write
with `ACCESS_SYSTEM_SECURITY` and, when the token has the privilege (an elevated one), enables it for the call only and
restores the state after (section 42). It never elevates and never prompts. Without the privilege the open fails with
`ERROR_ACCESS_DENIED` (5), not `ERROR_PRIVILEGE_NOT_HELD` (1314); measured, section 37. `GetSecurity` returns the HRESULT of
the failed read and does not retry without the SACL, so the SACL is never dropped silently. aclui then shows "You do not
have permission to view or edit this object's audit settings." and does not crash.

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
entries for the current user is still removed. The UI tests open the dialog and either cancel it, or press OK or
Apply on a sandbox key and read the DACL or the owner back (PermissionsTests: the Allow box, owner recursion,
child replace, and the remote Apply).

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
- A failed key is recorded and the walk continues with the keys after it (section 33). At the end,
  `RegistrySecurityWalkException` lists the failed keys. Keys already changed stay changed, with no rollback.
- The sandbox refuses a propagation whose subtree contains a link, before any key changes.
- Cancelling stops between keys and throws `OperationCanceledException`. Progress is reported
  after each key, including the root.

## 16. aclui flags for the Permissions dialog

The dialog passes `SI_EDIT_ALL | SI_ADVANCED | SI_CONTAINER | SI_RESET_DACL_TREE | SI_OWNER_RECURSE` in
`GetObjectInformation` (section 32 gives the reason for `SI_OWNER_RECURSE`). `SI_CONTAINER` makes the Permission Entry page offer the inheritance
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

When a child fails (for example `WRITE_DAC` denied, or the system refuses the computed DACL), the child
is recorded with its path and Win32 code, the keys below it are not changed, and the walk continues with
the next sibling. The walk then throws `RegistrySecurityWalkException`, and the keys already changed keep
their new entries. There is no rollback because a rollback could undo a change the user made after the
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

- The save and partial-propagation texts are the table in section 33 (oracle 7070 to 7076). `ERROR_ACCESS_DENIED`
  on the root shows 7070 (MSG-184). Any other root failure shows 7072 (MSG-186), owner failures 7073.
  `SaveMessageKey` in `RegistryPermissionsDialog.cs` chooses the key.
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


## Logging on the security callbacks (fix 7)

- `RegistryPermissionsDialog` catches specific types (`InvalidOperationException`, `COMException`,
  `InvalidCastException`, `IOException`, `ArgumentException`) and logs them with `Log` before returning the HRESULT.
- **E2E status.** A forced failure is not reproducible through aclui. No new E2E.

## 32. Sheet and Advanced controls match classic (oracle)

Classic is measured in `docs/parity/oracle/regedit-security.json` (tab count, class, id, visibility, enabled state of
every control). BWC's sheet and Advanced window are captured the same way by
`Permissions_SheetMatchesClassicOracle`, which compares the multisets and records each difference by name.

Flags in `GetObjectInformation`: `SI_EDIT_ALL` (owner `0x1` and audits `0x2`), `SI_ADVANCED`, `SI_CONTAINER`,
`SI_RESET_DACL_TREE`, `SI_OWNER_RECURSE` (`0x100`) and `SI_EDIT_EFFECTIVE` (`0x20000`, section 38). `SI_OWNER_RECURSE` adds
"Replace owner on subcontainers and objects", which classic has (oracle control text). `SI_RESET_SACL_TREE` is not set
(section 36). Values checked against `aclui.h` of Windows SDK 10.0.26100.0.

Sheet rows. Failure: the basic page listed all 13 access rights, one row each (Full Control, Read, Write, Query Value, ...),
where classic shows Full Control, Read and "Special permissions". Cause: `GetAccessRights` ignored its `dwFlags` argument and
returned the whole table on every call. aclui asks three times (logged on this machine: `0`, `SI_ADVANCED` (`0x10`), `0`).
It lists what the basic call returns, whatever the `SI_ACCESS_GENERAL` bit says: with only Full Control and Read marked
GENERAL the page still showed 13 rows (measured at the old flags and at one GENERAL entry fewer). Fix: the call without
`SI_ADVANCED` returns the first two entries (Full Control, Read, default Read); the `SI_ADVANCED` call returns all 13.
Full Control also carries `SI_ACCESS_SPECIFIC` so the Permission Entry page lists it. "Special permissions" is added by
aclui. Status: measured. `permissions.oracle.sheet.controls` passes (34 controls, same multiset as classic) in run 20261115T221624Z. SI_ACCESS is `{pguid, mask, pszName, dwFlags}`: 32 bytes on x64 and Arm64 (8, 4 + 4 pad, 8, 4 + 4 pad), which matches
the C# struct, and the names read back correctly.

Selected entry. The oracle JSON does not record which list entry was selected, but its control text names it:
"Permissions for RESTRICTED" (control 1008), and BWC also selects RESTRICTED (the entry aclui lists last of the inherited
four). The one remaining difference was the enabled state of "Allow Read" (`Button|5`): classic disables it because the
RESTRICTED entry is inherited. BWC did not tell aclui the entries were inherited (section 41). With the inherited marks
presented, the multiset equals classic. The sheet is also captured only after it has finished loading (Advanced enabled,
loading indicator 6024 hidden); an earlier capture sees a transient state.

Advanced: the hidden controls of classic's Effective Access page (five `CtrlNotifySink`, two hidden `Edit`, two `ScrollBar`,
one hidden disabled `Button`) were missing. They appear once `SI_EDIT_EFFECTIVE` is set and the `IEffectivePermission`
IID is right (section 38). Measured: `permissions.oracle.advanced.controls` passed (run 20261115T202755Z).

## 33. Save and retrieve messages (oracle 7050 to 7076)

Failure: a save or propagation failure shows a text that does not match the classic one, or a failure that classic
reports is shown as the generic 7072 text.

- Retrieve (7050, 7051, 7052): the texts have a space before the line break (`information. \n`), as in the oracle.
- Save of the root key (7070 denied, 7071 key marked for deletion, 7072 other). Owner set on the root that fails
  with a non-denied error shows 7073.
- Partial propagation (the root was written, some subkeys were not). The walk continues past a failed subkey and
  reports the failed subkeys: 7074 when any subkey was denied (`ERROR_ACCESS_DENIED`), 7075 when a subkey was marked
  for deletion (`ERROR_KEY_DELETED`), 7076 for any other failed subkey. Owner recursion with a failed subkey shows 7073.
- `RegistrySecurityWalkException` carries the list of failed subkeys after the walk ends.

The texts are generated from the oracle table, and the choice is `RegistryPermissionsDialog.SaveMessageKey`.
Tests: partial DACL propagation is `PropagationContinuesPastAChildOwnedByAdministrators` (Elevated, section 39).
Partial owner recursion is `OwnerRecursionContinuesPastADeniedChild` (unelevated). The message choice itself is not
under an automated test: the dialog's message path needs an aclui failure that the E2E cannot force from outside aclui.

## 34. Remote keys pass the computer name to aclui

`RegistryPermissionsDialog.Show` passes the connection's `ComputerName` in `SI_OBJECT_INFO.pszServerName` for a
remote key, and the descriptor is read and written through the key's store (`KeyLocation.Reader`, `Store`). The single-key
write merges the inherited entries (section 40) through the same remote handle: the parent is opened through the
connection's root.

Test: `Permissions_RemoteKeyApplyChangesTheDaclThroughTheConnection` (categories Permissions, Remote, Mutating). It connects to
127.0.0.1 through File > Connect, navigates to `\\127.0.0.1\HKEY_USERS\<SID>\Software\BWC-E2E\<run-id>`, opens Edit >
Permissions, removes the Everyone Read entry with Apply, and reads the DACL back through the local registry API (the same
key). It asserts that the user's allow rules are unchanged, then restores the original descriptor in `finally`. UI helpers
live in `PermissionsRemoteSupport.cs` (copies of the private helpers of `RemoteCommandTests`; no registry writes).
Failure modes:
- The RemoteRegistry service is stopped or refuses the connection: the test records the note
  `permissions.remote.not-run` ("not run: service stopped ...") and returns without a failure. It never starts or configures
  the service.
- The address bar does not show the sandbox key after navigation: the test stops before any input (same guard as the
  local tests).
- The sheet opens but Apply does not reach the key: `permissions.remote.dacl-changed` fails.
- The restore opens the key with `ReadWriteSubTree` and the rights `READ_CONTROL | WRITE_DAC`. A read-only open made
  `SetAccessControl` throw `UnauthorizedAccessException` in the first run.
Measured (run 20261115T221624Z): connect, navigate, sheet, toggle, `dacl-changed` and `user-entries-kept` pass, RemoteRegistry
was running, no key is left under `HKCU\Software\BWC-E2E`.

## 35. Owner recursion (SI_OWNER_RECURSE)

Failure: owner changes made with "Replace owner on subcontainers and objects" stay on the root key only, or the walk
stops at a child it cannot change.

When `SetSecurity` receives `OWNER_SECURITY_INFORMATION` with `SI_OWNER_RECURSE`, the engine's `SetKeyOwnerTree` sets
the root owner, then walks every subkey with `WRITE_OWNER` alone and `RegSetKeySecurity(OWNER)`. The root's DACL and
group are written before the owner walk, since an owner that is not the current user removes the user's implicit
WRITE_DAC. A child that fails is recorded and the walk continues below it and with its siblings.

Tests:
- `OwnerRecursionContinuesPastADeniedChild` (engine, unelevated). A child with a deny entry for WRITE_OWNER fails, and the
  walk still reaches its child and its sibling (progress reports).
- `Permissions_OwnerRecursionSetsTheOwnerOfEverySubkey` (E2E, unelevated). Sandbox tree of the root plus `OwnerA`,
  `OwnerA\OwnerB` and `OwnerC`. The test opens Advanced, checks "Replace owner on subcontainers and objects", presses OK on
  Advanced and the sheet, then asserts that every key's owner is the current user and that no error window appeared. The
  app log line `Registry permissions SetSecurity ... ownerRecurse=True` proves that aclui sent the owner with the recurse
  request. Each owner is restored in `finally`.
- The test first presses "Change" next to the owner and types the current user in aclui's "Select User or Group" dialog
  (keyboard input into the dialog that has the foreground, then OK), because aclui shows "Replace owner on subcontainers
  and objects" only after an owner was chosen. Checks `permissions.owner.change`, `recurse-checked` and `recurse-sent`
  pass in run 20261115T221624Z.
- Limit: an unelevated user can only assign itself as owner (`whoami /groups` shows no group with the owner attribute), so
  no key changes hands in this test. It shows that the request reaches the engine, that no key ends with another owner and
  that no error is shown; the change to a different owner is the elevated `Permissions_ElevatedOwnerToAdministratorsAndBack`.
  The owner picker only ever receives the current user's own name.

## 36. Declared differences from classic string ids

These classic strings are not shown by BWC, because aclui draws the control and the modern dialog never asks for the text:

- 7013 "Special Access...": aclui's Permission Entry shows "Special permissions" from its own resources. The oracle
  control text list has no "Special Access...", so it is not shown.
- 7014 "Registry &Key": the "Apply to" name for the key in aclui's Permission Entry dialog. aclui owns the text.
- 7015 "R&eplace Permission on Existing Subkeys": aclui shows its own check box text ("Replace all child object permission
  entries with inheritable permission entries from this object"). See section 25.
- 7016 "Audit Permission on Existing Subkeys" and 7018 "Do you want to audit all existing subkeys within %1?": the audit
  replace check box needs `SI_RESET_SACL_TREE`, which BWC does not offer, because the oracle Advanced window has no audit
  replace check box.
- 7017 is shown (section 27), and 7050 to 7052 and 7070 to 7076 are shown (section 33).

## 37. Auditing without SeSecurityPrivilege

Failure: the SACL is dropped silently when the user has no `SeSecurityPrivilege`, or the editor crashes.

`GetSecurity` returns the HRESULT of the failed read for a request that includes the SACL. It does not retry without it,
and it logs `Registry permissions could not read the security descriptor (requested 0x00000008, Win32 error 5)`. Measured:
the registry answers `ERROR_ACCESS_DENIED` (5) here, not `ERROR_PRIVILEGE_NOT_HELD` (1314), because the key is opened for
`READ_CONTROL` only and the SACL needs `ACCESS_SYSTEM_SECURITY`.

Test: `Permissions_AuditingTabWithoutSecurityPrivilegeShowsTheSystemState` (unelevated; Inconclusive when elevated). It
opens Advanced, clicks the "Auditing" tab, records the visible text of the tab or of the message box the system shows
(note `permissions.audit.state` and a screenshot), checks that the Advanced window is still open and the app responds,
and checks that the tab shows the system text "You do not have permission to view or edit this object's audit settings."
(check `permissions.audit.no-permission-text`; measured, passes). The app log lines are recorded as a note. It never presses
a Continue button, because that asks for elevation.
Unreachable: the SACL write error. aclui sends no SACL it could not read, so an unelevated session cannot reach the write
path. The engine refuses a SACL on propagation (section 22), tested in `PropagationRefusesSaclBeforeAnyChange`.

## 38. Effective access

Classic's Advanced window has the Effective Access page ("View effective access" in the oracle). Two defects kept it away
from BWC:
- `IEffectivePermission` was declared with IID `3853DC76-9F35-407c-88A1-D19EFCCAB3A3`, written from memory. `aclui.h` (Windows
  SDK 10.0.26100.0) gives `3853DC76-9F35-407c-88A1-D19344365FBC`. aclui's `QueryInterface` for the real IID failed, so the
  page never existed. Fixed.
- `SI_EDIT_EFFECTIVE` (`0x00020000`) was not set. Added to `GetObjectInformation`. `SI_VIEW_ONLY` is `0x00400000`; it is not
  used, because the editor must stay editable.

`ISecurityObjectTypeInfo` and `IEffectivePermission2` are not needed: with the flag and the right IID the Advanced window shows
the classic control set (`permissions.oracle.advanced.controls` passed, run 20261115T202755Z). `GetEffectivePermission`
computes the mask from the DACL in order, counting the owner's READ_CONTROL and WRITE_DAC, entries for the SID and for
Everyone, and the current user's groups. It is a managed approximation of what `AuthzAccessCheck` returns; groups of a
SID other than the current user are not resolved.

## 39. Elevated tests

Three tests need an administrator token and are tagged `TestCategory("Elevated")`. They compile in the normal build,
and they report Inconclusive when the process is not elevated, so `scripts/test-e2e.js` never runs them as failures:

- `Permissions_ElevatedAddAndRemoveAudit` (E2E): adds and removes an audit entry on a sandbox key (SeSecurityPrivilege).
- `Permissions_ElevatedOwnerToAdministratorsAndBack` (E2E): owner to Administrators and back (SeRestore and SeTakeOwnership).
- `PropagationContinuesPastAChildOwnedByAdministrators` (registry tests): a child owned by Administrators is refused by
  the propagation, and the walk changes its sibling.

They were compiled but not run in this change, because the elevated runner is started by the user.

## 40. Apply drops the inherited entries of a key (defect, fixed in 0.1.2)

Evidence, from `Permissions_AllowBoxChangeAppliesAndRestores` (run 20261115T101414Z), recorded by the check
`permissions.allow.sddl`. Before Apply the DACL is `D:AI(A;;KR;;;WD)(A;OICIID;KA;;;<user>)(A;OICIID;KA;;;SY)(A;OICIID;KA;;;BA)(A;OICIID;KR;;;RC)`.
After Apply it is `D:AI(A;;KR;;;WD)`. Every inherited entry (`ID` flag) is gone, including the current user's own
Full Control. This shipped in 0.1.1.

Cause: the dialog's root write goes through `RegistryWriter.SetKeySecurity`, which calls `RegSetKeySecurity` with the
descriptor aclui returns. aclui returns only the explicit entries (plus `SE_DACL_AUTO_INHERIT_REQ`). `RegSetKeySecurity`
stores the descriptor as given and does not derive inherited entries from the parent, so the inherited entries are lost.

Failure modes the fix has to cover (each has an engine test in `RegistrySecurityPropagationTests`):

- Unprotected DACL write (`DACL` with `UNPROTECTED_DACL`, or a descriptor with `SE_DACL_AUTO_INHERIT_REQ`, which is what
  aclui sends on Apply): the stored DACL must be the explicit entries of the request followed by the entries inherited from
  the parent (the rules of section 18). The parent's DACL is read at write time, so a parent change between dialog open and
  Apply is honored. Measured: aclui's Apply arrives as information `0x4` (DACL only) with the descriptor `D:ARAI(...)`, so the
  auto-inherit bit, not the `UNPROTECTED` flag, is what identifies it.
- Protected DACL write (`DACL | PROTECTED_DACL`, or a descriptor with `SE_DACL_PROTECTED` and no `UNPROTECTED_DACL`): the stored DACL
  is exactly what aclui sent, with no inherited entries. The protected flag is kept.
- A request that carries entries already marked `INHERITED_ACE`: they are stale copies from the time the dialog opened,
  so they are dropped and recomputed from the parent. They are never stored twice.
- A key with no parent (a hive root): there is nothing to inherit, and the request is stored as given.
- The parent cannot be read (`READ_CONTROL` denied): the write fails with the native error. It never falls back to the
  explicit-only write, because that is the defect. No automated test: the owner of a sandbox parent always holds
  `READ_CONTROL`.
- A DACL write with neither the unprotected flag nor the auto-inherit bit is stored as given. The engine's own setup,
  recovery and copy writes (`RemoteCopy` writes a descriptor read from another key) rely on this.
  Test: `PlainDaclWriteIsStoredAsGiven`. Writes that do not touch the DACL (owner, group, SACL only) are unchanged.
- Remote keys use the same merge, through the remote handle for the key and its parent.
- Propagation (section 15) is unchanged: the root write now also merges, and the children are then recomputed.

Why a managed merge and not `SetSecurityInfo(SE_REGISTRY_KEY)` or `CreatePrivateObjectSecurityEx`: `SetSecurityInfo` is a
second native write path that the write policy and the remote handles do not cover, and `CreatePrivateObjectSecurityEx`
needs an object-type mapping and a token. The managed merge reuses `RegistrySecurityInheritance` (section 18), is tested
on its own, and works on a remote handle.

The E2E `Permissions_AllowBoxChangeAppliesAndRestores` checks that the user's own allow rules are the same after Apply
(`permissions.allow.user-entries-kept`) and that the Everyone Read toggle took effect (`permissions.allow.dacl-changed`).
It restores the original DACL in its finally block.

Everyone Read toggle (`permissions.allow.dacl-changed`). With the fix the user's entries survive (`user-entries-kept` passes,
run 20261115T202755Z). The toggle did not take effect at first. Cause: with the 13-row layout, Read (`KEY_READ`) overlapped Query Value, Enumerate
and Notify, so clearing "Allow Read" left the other rows checked and the Everyone Read entry stayed in the request
(`D:ARAI(A;;KR;;;WD)`). On the two-row layout of section 32 Read is one row. Status: fixed and measured, `dacl-changed` and
`user-entries-kept` pass (run 20261115T221624Z). The sheet now also presents the inherited marks (section 41), so aclui
sends only the explicit entries and the merge here puts the parent's entries back without duplicates.

## 41. The registry stores no inherited marks, so aclui sees every entry as explicit

Evidence: run 20261115T213929Z and a raw probe. The sheet matched classic except one control, the Allow Read check box of
the selected entry (RESTRICTED), which was enabled in BWC and disabled in classic. The Advanced window showed
"Inherited from: None" where classic shows `CURRENT_USER\Software` (`docs/parity/oracle/permissions-inherit.json`).

Cause: `RegGetKeySecurity` returns the entries a key inherited as plain copies (ACE flags 0x03, no `INHERITED_ACE`, no
`SE_DACL_AUTO_INHERITED`). The system security API that classic uses (and .NET `GetAccessControl`) recomputes the marks
(`OICIID`). BWC passed the raw descriptor to aclui, which then treated every entry as explicit: no "Inherited from" text,
the check boxes of inherited entries stayed enabled, and Apply sent all four entries as explicit.

Failure modes of the fix (`RegistrySecurityPresentation.MarkInheritedEntries`, used by `GetSecurity`, plus
`ISecurityObjectTypeInfo::GetInheritSource` in `RegistryPermissionsDialog.cs`):
- An entry is marked only when it equals an entry the parent passes down (type, trustee, mask, flags without
  `INHERITED_ACE`). Each parent entry marks at most one key entry, so two identical explicit entries are not both marked.
- A protected DACL is returned unchanged. A key whose DACL has no match is returned unchanged, without the auto-inherit bit.
- A parent that cannot be read (`READ_CONTROL` denied) leaves the descriptor as stored. The sheet still opens.
- `GetInheritSource` returns one `INHERITED_FROM` per entry, in order, in one `LocalAlloc` block that aclui frees. An
  explicit entry has gap 0 and no name. The source of an inherited entry is the nearest ancestor that holds the same entry
  as an explicit one; when none does, the parent is named (gap 1). The name is the root without `HKEY_` and the path.
- Any failure there returns `E_FAIL` and aclui shows "None". It never throws into aclui.
- Apply still sends only explicit entries; the engine merge of section 40 puts the parent's entries back.
Test: `permissions.oracle.sheet.controls` (control states, selected entry as in the oracle) and `permissions.inherit.source`
(the Advanced window shows `CURRENT_USER\Software`) in `Permissions_SheetMatchesClassicOracle`. Both pass.

## 42. Audit findings (inherited marks, creator entries, order, access mask, privileges, aclui contracts)

Each item names the failure first. Items marked "measured" were confirmed on this machine before the fix.

A. Inherited entries stored twice. Failure: `RegGetKeySecurity` returns the entries a key inherited as plain copies (flags
0x03, no `INHERITED_ACE`, no `SE_DACL_AUTO_INHERITED`; measured with a raw probe), so aclui sent them back as explicit and
the merge of section 40 appended the parent's set again: every inherited entry stored twice, once explicit and frozen. Fix
(section 41): the dialog presents the marks, `MarkInheritedEntries` matches an entry to an entry of the inherited set by type,
trustee, mask after generic mapping and flags without `INHERITED_ACE`, and sets `SE_DACL_AUTO_INHERITED`. It works on remote
handles because it only uses descriptors. The matching covers both forms of a creator entry (item B). Test (engine, written
before the code): `ApplyOfThePresentedDescriptorStoresNoDuplicates` reads a freshly created key, marks it, sends only the
explicit entries with `SE_DACL_AUTO_INHERIT_REQ`, and expects the stored DACL to equal the parent's inherited set with no
explicit copies. E2E: `permissions.allow.user-entries-kept`.

B. CREATOR OWNER (S-1-3-0) and CREATOR GROUP (S-1-3-1). Failure: `InheritedAcl` copied `(A;CIIO;GA;;;CO)` as
`(A;CIID;GA;;;CO)`: the creator SID is never replaced by the child's owner, and the copy is no longer inherit-only. Windows
gives a container child two entries: an effective entry `(A;ID;<mask mapped to key rights>;;;<child owner>)` (group for CG),
with no inheritance flags, and the inheritable copy `(A;CIIOID;GA;;;CO)` that keeps the original `CI`/`OI` and adds
`INHERIT_ONLY`. With `NO_PROPAGATE_INHERIT_ACE` only the effective entry is produced. Fix: `InheritedAcl` takes the child's
owner and group; the merge, `ComputeChildDescriptor` (propagation) and the marking use the same list. If the child owner is
unknown the creator entry is copied as it was (still inherit-only). Tests (engine): `InheritedEntriesReplaceCreatorOwner`,
`PropagationReplacesCreatorOwner`.

C. Order. Failure: `WriteSecurity` ran the owner walk before the DACL propagation although the comment says the owner goes
last; an owner that is not the current user removes the user's implicit `WRITE_DAC`, so the propagation then fails. Fix:
root DACL and group, then the propagation, then the owner walk. Covered by the engine tests of section 35 and the elevated
owner test.

D. Access mask. Failure: `SetKeySecurity` always opened with `WRITE_DAC | READ_CONTROL`, so taking ownership of a key that
grants only `WRITE_OWNER` failed on the open. Fix: Owner or Group asks `WRITE_OWNER`, Dacl asks `WRITE_DAC`, Sacl asks
`ACCESS_SYSTEM_SECURITY`; nothing else. The merge reads the parent with its own `READ_CONTROL` handle. Test (engine):
`OwnerChangeNeedsOnlyWriteOwner`, a key whose DACL grants the user only `WRITE_OWNER`.

E. Privileges. Failure: SACL access and take-ownership failed for a token that holds `SeSecurityPrivilege` or
`SeTakeOwnershipPrivilege` but has it disabled. Fix: `OptionalPrivilegeScope` enables the privileges the token already has
for the one call (SACL read or write: `SeSecurityPrivilege`; owner: `SeTakeOwnershipPrivilege`) and restores each to its
previous state in `Dispose`. A privilege the token lacks is skipped: nothing is added or elevated and no prompt is shown. The
SACL read opens with `ACCESS_SYSTEM_SECURITY`. Unelevated the open fails with 5 (section 37). E2E: `permissions.audit.*`.

F. Effective access. Failure: `EffectiveMask` counted `INHERIT_ONLY_ACE` entries, which do not apply to the key itself (the
creator entry of item B is one). Fix: skip them. E2E: the Effective Access controls stay green.

G. `GetEffectivePermission`. aclui frees the granted-access list with `LocalFree`, so `AllocCoTaskMem` was a mismatch. The
`OBJECT_TYPE_LIST` was allocated on every call, never freed, and its object type was NULL. Fix: the granted-access list comes
from `LocalAlloc`; the object type list is one static block that points at a static zero GUID and is never freed.

H. Control bits. Failure: a merged or propagated descriptor lacked `SE_DACL_AUTO_INHERIT_REQ | SE_DACL_AUTO_INHERITED`, and
the `ProtectedDacl` / `UnprotectedDacl` information bits did not change `SE_DACL_PROTECTED` in the stored descriptor.
Fix: merge results and child descriptors carry both bits; `ProtectedDacl` sets and `UnprotectedDacl` clears the protected bit.
Tests (engine): `MergeResultCarriesTheAutoInheritBits`, `UnprotectedInformationClearsTheProtectedBit`,
`ProtectedInformationSetsTheProtectedBit`.

I. Message box owner. Failure: the Replace confirmation was owned by the main window while the aclui dialog was active, so it
could appear behind the editor. Fix: it is owned by the active window of the UI thread (the aclui dialog), the main window
if there is none.

J. `EditSecurity` returns a `BOOL`, not an `HRESULT`. Failure: the zero return (failure) was read as success and the error
path in `HiveRemoteCommands` was dead. Fix: the import returns `bool` with `SetLastError`; `Show` returns `0` on success
and `HRESULT_FROM_WIN32(error)` on failure.

K. Owner recursion test. The auditor's screenshots predate the picker step. The check box "Replace owner on subcontainers and
objects" does appear once the owner was chosen in the picker, even for the same user (measured: `permissions.owner.recurse-checked`
and `recurse-sent` pass in run 20261115T221624Z), so the unelevated GUI test stays. `SetCheckBox` now reports "not found"
separately from "not checked" in the check note. The engine keeps the unelevated `SetKeyOwnerTree` coverage.

L. Test restores. `SetSecurityDescriptorBinaryForm` with the full descriptor includes the SACL and fails unelevated: the
restores use `AccessControlSections.Access`. `RestoreOwner` and the elevated owner restore open the key with the rights that
`SetAccessControl` needs (`ReadWriteSubTree`, plus `TakeOwnership` where the owner is set).

Open. Entries on the basic page carry no `CONTAINER_INHERIT_ACE` in `AccessRights`. The oracle does not record what classic
sets for a principal added from the basic page, so this is not changed. Run the Add flow in classic and record the ACE flags
before changing it.

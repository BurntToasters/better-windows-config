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

A DACL that removes the current user's `READ_CONTROL` or `WRITE_DAC` can make the key impossible to
repair through the same UI. The engine does not silently add an administrator ACE or rewrite the
owner. Tests grant full control to the current user and restore the original descriptor in a
`finally` block. The UI test cancels the dialog and never presses OK or Apply, so a UI failure cannot
change an ACL.

## 12. Inheritance

A protected DACL stops inheriting from the parent; an unprotected DACL resumes inheritance. aclui
may present both. The engine does not recompute inherited ACEs and does not walk subkeys. Changing
one key's DACL therefore does not update existing children, which matches `RegSetKeySecurity`.
Callers that need recursive propagation must do it explicitly, one key at a time, with a failure
path that reports which keys were already changed.

## 13. E2E cleanup

E2E tests create a disposable key under `HKCU\Software\BWC-E2E\<run-id>` and delete the run root in
`finally`. They also remove the empty parent when possible and terminate every launched Regedit
process. A failed restore is a test failure, not a warning, because a changed ACL can outlive the
run. The engine tests restore the original descriptor before deleting the key, and the UI tests only
open and cancel the dialog.

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

- Propagation applies the descriptor to the key and to every key below it. The first failure stops
  the walk and throws the Win32 error for that key. Keys already changed stay changed.
- The sandbox refuses a propagation whose subtree contains a link, before any key changes.
- Cancelling stops between keys and throws `OperationCanceledException`.
- Each descendant gets the exact descriptor, not an inheritance-computed one. This differs from
  classic `TreeSetNamedSecurityInfo` and is recorded as a known difference (`registry-writer.md` I5).

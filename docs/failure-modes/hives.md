# Hive load, unload, save, replace, and restore failure modes

Scope: `PrivilegeScope` in `src/Bwc.Registry/Privileges`, the hive methods on
`RegistryWriter` and `RegistryStore`, the File > Load Hive and File > Unload Hive commands in
`src/Bwc.Regedit`, and the Hives E2E suite. Hive operations change whole registry trees and can
leave a hive mounted or a file locked, so every path is validated before a native call and no hive
file is ever deleted by this code.

## 1. Operation order

Every hive method follows this order:

1. Null argument checks and key component validation.
2. Target path rules for the operation.
3. `IRegistryWritePolicy.EnsureWriteAllowed`.
4. File path validation and existence checks.
5. `PrivilegeScope.TryEnable` for `SeBackupPrivilege` and `SeRestorePrivilege`.
6. The native hive call.

Steps 1 through 5 never call a hive API. A non-elevated process therefore gets a policy,
argument, file, or privilege error and never reaches `RegLoadKey`, `RegUnLoadKey`, `RegSaveKey`,
`RegReplaceKey`, or `RegRestoreKey`. This is what makes the non-elevated tests deterministic
without mounting a real hive.

## 2. Policy denial

`RegistryWriter` calls `EnsureWriteAllowed` before any file or privilege work. Under the E2E
`SandboxWritePolicy`, a hive operation whose target is outside `HKCU\Software\BWC-E2E\<runId>`
fails with `ERROR_ACCESS_DENIED` (5) and the operation name. The target comparison is component
based, so a sibling whose name starts with the sandbox name is not authorized.

One exception exists, for the elevated round trip of section 14: `SandboxWritePolicy` allows
`LoadHive` and `UnloadHive` at exactly `HKEY_USERS\BWC-E2E-<runId>`, where `<runId>` is the last
component of a sandbox root of the form `HKCU\Software\BWC-E2E\<runId>`. Before this rule the
policy refused that mount, so the round trip failed with "Access is denied" in the first elevated
runs. The exception is compiled into E2E builds only, like the whole policy. Failure modes of the
exception, each refused with `ERROR_ACCESS_DENIED` (5) before any file, privilege, or native work:

- Another operation at the mount path (create key, set value, delete key, rename, security, save,
  restore, replace). Only `LoadHive` and `UnloadHive` are excepted.
- A path below the mount (`HKU\BWC-E2E-<runId>\Sub`), for any operation, including the two hive
  operations. The test reads the mounted values; nothing writes into the mounted hive.
- The same name under another root (`HKLM\BWC-E2E-<runId>`).
- Another run's name, a name with extra characters before or after (`BWC-E2E-<runId>x`,
  `xBWC-E2E-<runId>`), or the bare prefix `BWC-E2E-`.
- A sandbox root without a run id (`HKCU\Software\BWC-E2E` itself) or deeper than the run root
  (`HKCU\Software\BWC-E2E\<runId>\Sub`): no hive operation is allowed at all.
- A remote computer, loopback included: the exception applies only to the local registry
  (`RegistryWriter` also refuses remote hive operations before the policy).
- The name differs only in case: the registry compares names case-insensitively, so it names the same
  key and is allowed.

## 3. Target path rules

Load and unload mount a hive at a direct child of a predefined root. The only accepted roots are
`HKEY_LOCAL_MACHINE` and `HKEY_USERS`, and the path must have exactly one component, for example
`HKLM\BwcTestHive`. A predefined root, a deeper path, HKCU, HKCR, or HKCC is rejected with
`ArgumentException` before the policy or native call.

Save, replace, and restore operate on a key tree. They reject predefined roots and accept a
non-root key under any predefined root. Replace and restore do not rewrite the target into a
different path; the native API applies its own hive-root traversal rules.

## 4. File validation

`LoadHive` and `RestoreKey` require an existing regular file. `ReplaceKey` requires the new hive
file to exist and requires a distinct backup file path that does not already exist and whose
parent directory exists, because `RegReplaceKey` creates the backup file. `SaveKey` requires a
destination that does not already exist and whose parent directory exists, because `RegSaveKey`
refuses to overwrite an existing file. Empty paths, NUL characters, paths that name a directory,
relative paths that cannot be resolved, and identical new and backup files are rejected before
privileges are enabled.

The code does not parse or validate the hive format. A file with the wrong content reaches the
native API and fails there, typically with `ERROR_NOT_ENOUGH_MEMORY` (8),
`ERROR_BADDB` (1009), or `ERROR_INVALID_PARAMETER` (87). Native errors are mapped to
`RegistryOperationException` with the operation, target path, and Win32 code.

## 5. Privileges

`PrivilegeScope` opens the current process token with `TOKEN_QUERY | TOKEN_ADJUST_PRIVILEGES`,
looks up `SeBackupPrivilege` and `SeRestorePrivilege`, and enables both with
`AdjustTokenPrivileges`. When a privilege was not already enabled, the scope records that fact and
disables it again on dispose, so the process token returns to its prior state. `TryEnable` returns
false with a clear message when a privilege is absent, and `IsAvailable` queries the token without
changing it.

Hive APIs require these privileges:

| Operation | Required privileges |
| --- | --- |
| LoadHive | SeRestorePrivilege, SeBackupPrivilege |
| UnloadHive | SeRestorePrivilege, SeBackupPrivilege |
| SaveKey | SeBackupPrivilege, SeRestorePrivilege |
| ReplaceKey | SeRestorePrivilege, SeBackupPrivilege |
| RestoreKey | SeRestorePrivilege, SeBackupPrivilege |

The privilege is process-wide, so every hive operation in `RegistryWriter` takes one lock for its
whole run, from the file checks to the native call. Two hive operations never overlap, and one
operation never disables a privilege that another is using (see `registry-writer.md` D4).

If `AdjustTokenPrivileges` reports `ERROR_NOT_ALL_ASSIGNED` (1300) or a lookup fails, the writer
throws `ERROR_PRIVILEGE_NOT_HELD` (1314) without calling a hive API. A non-elevated process
therefore cannot mount a hive by accident. The UI does not request elevation and never shows a UAC
prompt.

## 6. Native ownership and file locks

`RegLoadKey` maps the file into the registry. While the hive is loaded, the file is held by the
system and cannot be deleted or replaced. `UnloadHive` releases it. If the process exits while a
hive is loaded, Windows keeps the hive mounted until the machine restarts or an administrator
unloads it, so the success path always pairs a load with an unload and tests use a `finally`
block that retries the unload.

`RegSaveKey` writes a new file and leaves the key untouched. A failure part way through can leave
a corrupt file. The writer does not delete that file: a corrupt hive file is evidence and may be
the only copy of data. The caller may choose to remove it after inspection. SaveKey never
overwrites an existing file.

`RegReplaceKey` writes the current hive contents to the backup file before installing the new
file. The backup file is created by the system, not by this code, and is never deleted here.
`RegRestoreKey` replaces the key contents from the file and does not create a backup.

When the sandbox guard checks a hive file before `RegRestoreKey`, the scanner keeps that file open
with `FileShare.Read` until the native call returns, so a writer cannot change the file between the
link check and the load. `RegReplaceKey` checks its replacement file without holding it, because the
native call takes the file over; see `registry-writer.md` A12 and D3.

## 7. Load and unload errors

Load fails with `ERROR_ACCESS_DENIED` when the target key exists, when the caller lacks restore
privilege, or when the file is open by another process. It fails with `ERROR_FILE_NOT_FOUND` when
the precheck missed a file removed between validation and the native call. It fails with
`ERROR_SHARING_VIOLATION` (32) or `ERROR_LOCK_VIOLATION` (33) when the file is in use. The error
dialog reports the operation, target path, Win32 code, and system message.

Unload fails with `ERROR_ACCESS_DENIED` when the target is not a loaded hive, a subkey still has
open handles, or a process is using the hive. It fails with `ERROR_BUSY` (170) or
`ERROR_SHARING_VIOLATION` when handles remain. The key stays mounted in all of these cases, so the
UI refreshes the parent and shows the error instead of pretending the unload succeeded. The code
never deletes the hive file to force an unload.

## 8. RegistryWriter and RegistryStore

`RegistryStore.LoadHive`, `UnloadHive`, `SaveKey`, `ReplaceKey`, and `RestoreKey` are thin
wrappers over the writer. They do not add retries, do not delete files, and do not run
asynchronously. The UI calls the writer on the thread pool so the dispatcher is not blocked while
the native API works, then refreshes on the UI thread.

The writer opens save and restore source keys with `KEY_READ` and the 64-bit view. Load and unload
pass the predefined root handle directly to the native API with the single component name. All
native handles are closed through safe handles before the method returns.

## 9. File > Load Hive

The command is enabled at the HKLM and HKU roots. The flow is:

0. Check the privilege with `PrivilegeScope.IsAvailable` for `SeBackupPrivilege` and
   `SeRestorePrivilege`, without enabling either. A process that lacks one shows the classic
   `Cannot Load {0}: Insufficient privileges.` message (RT_STRING 182) and returns. No file dialog
   and no name prompt open first, so the user is not asked for input that cannot succeed. The
   check is refused by the message, not by the native call, and nothing is written.
1. Show the owned `IFileOpenDialog` through `CommonFileDialogs.ShowOpen`.
2. If the user cancels, return without prompting and without changing the registry.
3. Show `TextPromptDialog` for the single key name. Its label is the classic template text `&Key Name:`
   (RT_DIALOG 104, oracle control 65535), so it does not name a root. The hive loads under the root that was
   selected (HKLM or HKU). Before this fix the label read "Key name under HKEY_LOCAL_MACHINE:", which was wrong for a
   load under HKU (section 14).
4. Reject empty names, separators, NUL, `.`, `..`, names over 255 characters, and names that
   already exist under HKLM.
5. Run `LoadHive(HKLM\<name>, file)` under the write gate (`RunRegistryWriteAsync`), so the window
   cannot close while the native load runs.
6. Refresh the tree, select `HKLM\<name>`, and leave focus in the tree.
7. On failure show the classic Load Hive text (`MsgLoadHiveFailed` or the matching Load Hive text,
   RT_STRING 183, 188, or 189) with the hive file path as the subject, not the key path. The Win32
   detail is logged. A file that is missing or is not a hive file therefore names the file the user
   chose. No separate missing-file string exists for Load Hive, so a missing file uses the same text.

Failure modes of the early check: a process that holds the privilege (an elevated run) skips step 0 and
behaves as before. An `IsAvailable` query that fails is treated as missing, which is the safe side: the
command refuses rather than asking for a file it cannot load.

The menu handler owns the modal gate for both dialogs, so a second Load Hive, another modal, or a
context menu cannot run while the file dialog or the name prompt is open. If the window handle is
not attached, the command logs and returns. The command does not retry a failed load and does not
delete the selected file.

Unload Hive runs its native call under the same write gate (section 10).

Check: `Hives_LoadHiveRefusesWithoutPrivilegeBeforeAskingForFile` selects HKLM in a non-elevated run,
invokes Load Hive, and checks that the privilege message appears and that no file dialog opens within
three seconds. Nothing is loaded.

## 10. File > Unload Hive

Unload is enabled only when the selected tree key is a direct child of HKLM or HKU and no value
row is selected. A sandbox key under HKCU, a predefined root, a deeper key, or a value selection
disables the command. Connect Network Registry and Disconnect Network Registry remain disabled.

The flow is:

1. Confirm with the standard owned confirmation dialog.
2. Run `UnloadHive(selected)` under the write gate (`RunRegistryWriteAsync`). Earlier builds ran it
   as a plain read, so closing the window during an unload could exit the process mid-call.
3. Refresh the tree, select the parent, and leave focus in the tree.
4. On failure show the standard registry error dialog. The key remains selected and mounted.

Cancel changes nothing. The command never deletes the hive file.

## 11. Menu and focus state

`UpdateCommandStates` runs after every tree selection, list selection, refresh, and dialog close.
It sets Load Hive enabled, Unload Hive according to the selection rules, and keeps Connect and
Disconnect disabled. The menu's other commands keep their existing states. Dialogs are owned by
the main window, disable the owner for their lifetime, and restore focus to the tree after they
close.

## 12. E2E coverage

The Hives E2E test launches the published E2E application with an isolated `BWC_DATA_ROOT` and the
run sandbox root. It seeds a disposable sandbox key, selects it, and verifies that Load Hive is
enabled while Unload Hive is disabled for that HKCU key. It invokes Load Hive, confirms that the
owned file dialog appears, cancels it, waits for the main window to be enabled again, and verifies
that no new key appeared under HKLM. The test never loads a real hive because a non-elevated run
cannot hold the restore privilege.

The test records an informational check naming the elevation requirement and seed key. It cleans
the run subtree and data root in `finally`, terminates the application, and leaves no HKLM key,
hive file, temporary folder, or sandbox key behind. The elevated round trip that loads a hive is section 14.

## 13. Native calls and token changes

Hive calls are made only by `RegistryWriter`: `RegLoadKeyW`, `RegUnLoadKeyW`, `RegSaveKeyW`,
`RegReplaceKeyW`, and `RegRestoreKeyW`. Token calls are made only by `PrivilegeScope`:
`OpenProcessToken`, `LookupPrivilegeValueW`, `AdjustTokenPrivileges`, `GetTokenInformation`, and
`CloseHandle` through the safe handle. No other module references a mutating registry API, which
`scripts/check-registry-writer.js` enforces.

## 14. Elevated round trip: Export, Load Hive, Unload Hive (E2E, Elevated)

`Hives_ElevatedLoadAndUnloadRoundTripThroughTheMenu` (`tests/Bwc.E2E/Hives/HiveMountTests.cs`) is tagged
`Hives` and `Elevated`. Normal runs exclude it (`scripts/test-e2e.js` adds `TestCategory!=Elevated`), so it runs
only through the elevated runner. It covers MENU-005, MENU-006, DLG-007 and DLG-008. The flow is: seed a sandbox key
under `HKCU\Software\BWC-E2E\<run-id>`, File > Export as the hive format to a file under the run's artifacts folder,
select `HKEY_USERS`, File > Load Hive, pick the file, type `BWC-E2E-<run-id>` in the Key Name prompt, check the
mounted values, select the mount, File > Unload Hive, confirm, and check that the mount is gone. `finally` removes
any mount that is left and the hive file.

Failure modes:

- The process is not elevated, or `SeBackupPrivilege` or `SeRestorePrivilege` is missing from the token. The test
  reports Inconclusive before any input. The check reads the token and changes nothing (no `AdjustTokenPrivileges`).
- Load Hive is enabled only at HKLM and HKU. The test selects `HKEY_USERS` and checks that the command is enabled
  there. It never invokes Load Hive at HKCU, where the command is disabled.
- The mount name collides with an existing HKU key. The name carries the run id, so a key with that name can only come
  from this run. The test checks the name is absent before the load and fails without loading if it is present. The
  prompt also refuses an existing name (`DialogLoadHiveNameExists`).
- The export writes something that is not a hive. The test checks that the file starts with the `regf` signature
  before it loads the file. A file that is not a hive would fail in the native load, not in the test.
- The file dialog reads back a different path than the one typed. The test checks the file name box before it accepts.
  A wrong file would load the wrong data; the mounted values check catches that too.
- The prompt has the wrong layout or wrong control ids. The test compares the prompt with RT_DIALOG 104 (252 x 53 DLU,
  Static `&Key Name:`, Edit 1001, OK 1, Cancel 2, caption `Load Hive`, tab order Edit, OK, Cancel) with the geometry
  check of `ClassicDialogTests`. It types the name into `DialogTextInput` (control 1001) and reads it back before OK.
  Before this fix the label said HKEY_LOCAL_MACHINE for a load under HKU (section 9).
- The mounted key does not show the seeded values. The test checks each seeded value name in the list and reads each
  value through the managed API. Its handles are disposed before the unload.
- Unload fails with access denied because a managed key handle is still open. The test disposes every handle it opened
  on the mount before it presses Unload. The `finally` retries with `RegUnLoadKeyW` after the handles are gone.
- Unload is invoked on a value row instead of the mount. Unload Hive needs the tree key selected and no value row
  selected. The test selects the mount in the tree and checks the command state before it invokes it.
- The mount stays loaded after a failed test. `finally` checks `HKEY_USERS\BWC-E2E-<run-id>` and unloads it with
  `RegUnLoadKeyW` (`HiveMountNative.cs`, the one native unload call of the E2E project, allowlisted in
  `scripts/check-registry-writer.js`). If that call fails the test records the Win32 code and fails.
- The hive file is deleted while a mount holds it. `finally` deletes the file only after the mount check passes.
- A write lands outside the two allowed places. The only registry writes of this test are the sandbox seed under
  `HKCU\Software\BWC-E2E\<run-id>` and the mount `HKEY_USERS\BWC-E2E-<run-id>`, which the app itself makes. The test
  never writes HKLM and never writes any other HKU key.
- The run does not clean its sandbox. `finally` calls `ControlTestSupport.CleanupRegistry` and deletes the run's
  artifact folder.

Evidence: the test records `hives.elevated.*` checks in the E2E result artifact. A run that is not elevated is
Inconclusive, so the artifact never shows a pass for a run that did not load a hive.

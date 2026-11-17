# Remote registry connection failure modes

Scope: `RemoteRegistryConnection` in `src/Bwc.Registry`, the remote root routing in
`RegistryReader`, the File > Connect Network Registry and File > Disconnect Network Registry
commands, the remote computer nodes in the Registry Editor tree, and the remote write store (section 15).
Section 16 covers the Registry Editor commands on a remote key (R2a). Section 17 covers the Connect picker, the
connect error text, and Import into a connected computer (R2b). Load Hive and Unload Hive stay local-only. The
engine can write a remote computer only through `RemoteRegistryConnection.CreateStore` and an explicit write policy.

## 1. Operation order

Every connect follows this order:

1. Null, empty, whitespace, and NUL checks on the computer name.
2. Own-computer rejection.
3. `RegConnectRegistryW` for `HKEY_LOCAL_MACHINE`.
4. `RegConnectRegistryW` for `HKEY_USERS`.
5. Construction of the connection object that owns both handles.

Steps 1 and 2 never call a native API. A rejected name therefore returns the same result on every
machine. Step 3 failure leaves no handle open. Step 4 failure closes the handle from step 3 before
the error is returned.

## 2. Computer name rules

The name is trimmed. It must be non-empty, at most 255 characters, and must not contain NUL,
backslash, or slash. A name with a path separator is rejected because the remote API expects a
computer name, not a registry path. Arguments are validated before the own-computer check so a
malformed name always produces an argument error.

## 3. Own-computer rejection

A connection to the local computer is rejected with `MSG-083` ("You cannot connect to your own
computer.") before any native call. The comparison is ordinal-ignore-case against:

- `Environment.MachineName`;
- `Dns.GetHostName()` when it resolves locally;
- `.` and `localhost`;
- the computer's short name without the DNS suffix.

The DNS lookup is best effort. A DNS failure never blocks a valid remote name. The UI never shows
a UAC prompt for this check.

## 4. Connection handles

`RegConnectRegistryW` returns one handle per root. `RemoteRegistryConnection` owns the
`HKEY_LOCAL_MACHINE` and `HKEY_USERS` handles. A reader does not own them. Each operation that
needs a root takes a pin: `GetRoot` calls `DangerousAddRef` on the root handle and returns a wrapper
that calls `DangerousRelease` when it is disposed. The reader disposes the pin after the open, and a
key handle opened from a predefined root takes its own pin, so the root stays valid while that key
is in use. `Dispose` of the connection drops its own reference. The native `RegCloseKey` runs when
the last pin is released, so a `Disconnect` while a load is in flight cannot close a handle that the
load still uses. A load that starts after `Dispose` gets `ObjectDisposedException` from `GetRoot`.
Test: `RootPinKeepsTheHandleUsableAfterTheConnectionIsDisposed` (no network, no writes; it wraps two
read-only HKCU handles with `CreateForTesting`).

A disposed connection cannot create a reader. Every member that reaches a borrowed handle throws
`ObjectDisposedException` after disposal. Dispose is idempotent and never throws.

## 5. Connect errors

`RegConnectRegistryW` failures are mapped to `RemoteRegistryException` with the computer name, the
root that failed, and the Win32 code. The message names the computer and the system error text.

Failure to connect `HKEY_LOCAL_MACHINE` leaves nothing open. Failure to connect `HKEY_USERS` closes
the `HKEY_LOCAL_MACHINE` handle and reports the second failure, because a connection with only one
root cannot show the required tree. The UI does not retry and does not cache a failed connection.

Typical codes:

- `ERROR_ACCESS_DENIED` (5): the remote registry service refuses the caller.
- `ERROR_BAD_NETPATH` (53): the computer name does not resolve.
- `ERROR_NETWORK_ACCESS_DENIED` (65): network or firewall policy blocks the connection.
- `ERROR_INVALID_PARAMETER` (87): the name reached the API in an invalid form.
- `ERROR_NOT_SUPPORTED` (50) or `ERROR_SERVICE_DISABLED` (1058): the Remote Registry service is
  unavailable.

## 6. Reader root routing

A remote reader accepts only `HKEY_LOCAL_MACHINE` and `HKEY_USERS` paths. `HKEY_CLASSES_ROOT`,
`HKEY_CURRENT_USER`, and `HKEY_CURRENT_CONFIG` are rejected with `ArgumentException`; classic
regedit shows only the first two roots for a connected computer (BEH-023). Local readers keep their
existing five-root behavior.

All read methods use the same routing: key info, subkey enumeration, value enumeration, value
reads, security reads, and existence checks. A reader never falls back to the local registry when a
remote root is missing or the connection is disposed. A missing handle is an error, not a silent
local read.

## 7. Tree and display paths

Remote nodes use a display path of the form `\\COMPUTER\HKEY_LOCAL_MACHINE\...`. The prefix is a
display and identity aid; it is not a local `RegistryPath`. Local commands that parse a path with
`RegistryPath.TryParse` therefore fail closed on remote nodes, which disables editing, deletion,
rename, import, and permissions for remote selections. This is intentional: no hive, writer, or
security call is routed to a remote computer in this implementation.

Children load lazily through a remote tree source. A failed enumeration leaves the node empty and
logs the Win32 error; it does not remove the connection. Expanding a remote node never blocks the
dispatcher. A connection that disappears while the tree is open remains visible until the user
disconnects it or closes the window, but its reads fail with the connection error.

## 8. File > Connect Network Registry

The command is always enabled. The flow is:

1. Show the computer picker, owned by the main window (section 17.1). Only when the picker cannot run, show an owned
   dialog with a computer name text box, OK, and Cancel.
2. If the user cancels, return without changing the tree.
3. Reject an empty or invalid name in the dialog, without closing it.
4. Reject the local computer with `MSG-083`.
5. Run `RemoteRegistryConnection.Connect` on the thread pool.
6. On failure show the owned error dialog with `MSG-082` and the connection detail.
7. On success add one tree node named `\\COMPUTER` under the virtual Computer node, select it, and
   expand it to show `HKEY_LOCAL_MACHINE` and `HKEY_USERS`.
8. If the same computer is already connected, select the existing node instead of opening a second
   connection.

The dialog is owned by the main window and disables the owner for its lifetime. The command is
ignored while another modal is open. The UI never connects to a computer automatically at startup.

## 9. File > Disconnect Network Registry

The command is enabled only when at least one remote computer is connected. The flow is:

1. If no computer is connected, show the standard no-selection message and return.
2. Show an owned list of connected computer names with OK and Cancel.
3. If the user cancels, return without changing anything.
4. Close the selected connection, remove its tree node, and select the virtual Computer node.
5. If the removed computer was not selected, keep the current local selection.

Closing the window disposes every remaining connection. A disconnect never writes to the registry
and never touches a local key.

## 10. Menu and selection state

`UpdateCommandStates` enables Connect Network Registry at all times. Disconnect Network Registry is
enabled only when the connection list is non-empty. File > Import is always enabled and asks for its computer
(section 17.3). Edit, Delete, Rename, Permissions, Print, and Export stay disabled for remote selections because
`TryGetSelectedKeyPath` does not parse
a remote display path. Favorites may still be added for a remote path only if the path resolves;
otherwise the normal "key not found" error is shown.

## 11. E2E and manual coverage

The non-elevated E2E suite drives the remote path on loopback. An E2E build accepts a connect to
`127.0.0.1`, so the remote commands run through the real remote registry RPC path on this machine:

- File > Connect Network Registry opens an owned modal, disables the owner, and cancels cleanly.
- The local computer name is rejected with the own-computer message (string 153) and no tree node appears.
- Empty, NUL, separator, and overlong names are covered by the registry unit tests; the E2E test
  exercises the local-computer validation because it needs no network.
- An unreachable connect shows string 154 and leaves no node (`Remote_ConnectUnreachableShowsStringOneFiftyFour`).
- A loopback connection shows the remote roots and menus, runs the key and value commands, Find, Export, the
  address bar and Disconnect (section 16), and runs Import and Permissions apply through the connection
  (`Remote_ImportWritesThroughTheConnection`, `Permissions_RemoteKeyApplyChangesTheDaclThroughTheConnection`).
- Print of a remote key writes the classic remote key name (`Print_RemoteReportUsesClassicRemoteKeyName`).
- File > Disconnect Network Registry is disabled while no computer is connected.
- The main window is enabled again after cancel and no connection is left behind.

Still manual: a connect to a different computer, and RPC failures during a write or a value read
(sections 15.1 and 16). Those need a second computer or a VM with the Remote Registry service and network
permissions, and must run only on an isolated network. The loopback tests need the Remote Registry service
to start on connect. If it cannot start, they record "not run" and do not fail the run. Every remote write
goes to loopback under `HKU\<SID>\Software\BWC-E2E\<run>`, the same sandbox as the local tests.

## 12. Native calls and process lifetime

`RegistryReader` and `RemoteRegistryConnection` call `RegConnectRegistryW`, `RegOpenKeyEx`, and the
read-only query APIs. No mutating API is added outside `RegistryWriter`, and `RegistryWriter` has no
remote routing. Root pins use `DangerousAddRef` and `DangerousRelease` on the connection's own
handles; no other handle is shared. `scripts/check-registry-writer.js` continues to enforce that boundary.

The connection objects live in the Registry Editor view, not in a static cache. Closing the view
disposes them. No credential, password, or token is stored; authentication uses the process token.

## 13. Expanding a remote key that cannot be enumerated

`RemoteRegistryTreeSource.LoadChildren` no longer turns a failed enumeration into an empty child list.
The `RegistryOperationException` propagates, the tree collapses the node and raises `ExpandFailed`, and
`RegeditContext.ShowExpandFailedAsync` shows the classic `Cannot open key` message (`MsgOpenKeyFailed`
with the system text as details) for any failed enumeration, not only access denied. Earlier builds
expanded a denied remote key to nothing, with only a log line. A connection that was closed
(`ObjectDisposedException`) is still logged only, because the view is going away.

Rule: the node collapses, the message names the key path, and the next expand retries.

Check: no automated check. The E2E suite has no second computer, so a remote expand cannot be driven. The
path is the same code as the local expand failure that the value-list probe covers
(value-display-enumeration.md, section 17), and the log line names the key and the Win32 code.

## Parity package: remote write refusal

| Way it could fail | Expected behavior |
| --- | --- |
| A write command (Load Hive, Import, Edit, New, Delete, Rename) runs with a remote node selected | Refused or disabled. Nothing reaches the writer |
| Disconnect dialog opens with the wrong size or colors | Port of DLG-013 on `ClassicDialog`: 250 x 111 DLU, label at (7, 7), list at (7, 18) 236 x 63, OK at (138, 90), Cancel at (193, 90) |


## 14. Values under a remote key (fix 5)

- Modify, Modify Binary Data, Display Binary Data, Enter, and double-click do nothing for a value under a
  remote key. They no longer report "no selection".
- The menu enable state is set in `RegeditContext.UpdateCommandStates` (Modify, Modify Binary Data, and Display
  Binary Data are disabled when `RegeditSelectionGuards.IsRemoteSelected`). The context menu disables them in
  `ContextMenuController.CreateValueContextMenu`. Enter and double-click reach `ValueCommands`, which returns early.
- **E2E status: driven on loopback.** The rules above are the R1-era state and section 16 (R2a) supersedes
  them: Modify now writes a remote value. `Remote_KeyAndValueCommandsWriteOnlyUnderTheSandbox` modifies String
  and DWORD values under a remote key through the connection, and `Remote_LoopbackConnectShowsRemoteRootsAndMenus`
  checks the remote menu states. A connect to a second computer is still manual (section 11).

## 15. Remote writes (R1 engine)

Scope: `RemoteRegistryConnection.CreateStore`, `RegistryWriterRoots`, the computer argument of
`IRegistryWritePolicy.EnsureWriteAllowed`, `RegistryWriter` on a remote root (including
`RegistryWriter.RemoteCopy.cs`), and the `RegistrySecurityDescriptor` layout check. Writes are limited to the
test sandbox. Hive operations are refused on a remote store (section 15.4). The UI does not call any of this yet.

The write rule: a remote write runs only after the policy accepts `(computer, path, operation)`. A refusal throws
before any native call. Local writes keep their existing rules and error codes.

### 15.1 Ways a remote write can fail, and the expected result

| Way it could fail | Expected result | Test |
| --- | --- | --- |
| Write on a store whose connection was disposed before the call | `ObjectDisposedException` from the root pin. No native call. | `DisposedConnectionRefusesWritesWithoutTouchingTheRegistry` |
| Dispose while a write is in flight | Each native open takes its own pin on the root. Dispose drops only the connection's reference, so an open that holds a pin finishes. Measured: a child key handle stays valid after the root handle is closed (section 15.5). A write that starts after Dispose gets `ObjectDisposedException`. | `RootPinKeepsTheHandleUsableAfterTheConnectionIsDisposed` (existing); `DisposedConnectionRefusesWritesWithoutTouchingTheRegistry` |
| Wrong root on a remote store (`HKEY_CURRENT_USER`, `HKEY_CLASSES_ROOT`, `HKEY_CURRENT_CONFIG`) | `ArgumentException` from the root pin, before any native call. Also refused with a permissive policy. | `RemoteStoreRefusesRootsOtherThanLocalMachineAndUsers` |
| Local path form (`HKCU\Software\BWC-E2E\...`) passed to a remote store | Sandbox policy refuses (access denied). Shipping policy: `ArgumentException` from the root pin. Nothing is written. | `SandboxDeniesRemoteWritesOutsideTheSandboxSubtree`, `RemoteStoreRefusesRootsOtherThanLocalMachineAndUsers` |
| Sandbox path on a non-loopback computer (`192.168.1.20`, `bwc-remote-test`, `127.0.0.2`, `0.0.0.0`, `localhost`, `::ffff:127.0.0.1`, empty) | Sandbox policy refuses with access denied (5). Only the addresses `127.0.0.1` and `::1` are loopback. | `SandboxDeniesRemoteWritesToOtherComputers` |
| Sandbox path under another user's SID, or a SID one digit off | Refused (5). | `SandboxDeniesRemoteWritesOutsideTheSandboxSubtree` |
| Case difference in the SID or the sandbox names (`s-1-5-21-...`, `bwc-e2e`) | Allowed. The registry names are case-insensitive, so it is the same key. | `SandboxAllowsRemoteWritesWhateverTheSidCase` |
| Sandbox base itself, or a sibling such as `BWC-E2E2`, as a remote path | Refused (5). | `SandboxDeniesRemoteWritesOutsideTheSandboxSubtree` |
| `..` or `.` component built with `RegistryPath.AppendEnumeratedName` | Sandbox policy refuses a remote write (5) before native code. Shipping allows it, as it does locally. | `SandboxDeniesDotComponentsOnRemoteWrites` |
| Rename of the remote sandbox root | Refused (5) by the policy. Delete of the root is allowed, as locally. | `SandboxDeniesRenameOfTheRemoteSandboxRoot` |
| Remote write through a symbolic link key, or a tree containing one, under a link-rejecting policy | Refused (5) by the guard, same rules as local. Measured: the remote `RegCreateKeyEx` honors `REG_OPTION_OPEN_LINK` (section 15.5). | `RemoteGuardRefusesSymbolicLinksOnRealLoopback`; `RoutedGuardRefusesSymbolicLinks` (local handles) |
| Connect fails: `ERROR_BAD_NETPATH` (53), `RPC_S_SERVER_UNAVAILABLE` (1722), access denied (5), network access denied (65), and the other codes | `RemoteRegistryException` from `Connect`. The loopback tests report `Inconclusive` with `not run: remote registry unavailable (Win32 <code>)`. They never query or start the service. | `ConnectOrSkip` in every loopback test |
| RPC error on a write (session lost, access denied on a key the user can open) | `RegistryOperationException` with the Win32 code and the operation. No retry, and no fallback to the local registry. | Not automated. Forcing an RPC failure needs a second machine. |
| Rename of a remote key | `RegRenameKey` fails on a remote handle with 6 (`ERROR_INVALID_HANDLE`, measured). The engine runs the managed copy-then-delete fallback for 6, and also for 50, 120 and 1. Any other code throws. | `RemoteRenameKeyLeavesOneKeyAtTheNewName` (loopback); `RenameByCopyMovesTheTree` (route) |
| Fallback: the new name already exists | Refused with 183 before any write. The copy would otherwise merge into the existing key. | `RenameByCopyRefusesAnExistingDestination` |
| Fallback: case-only rename | Refused with 50 before any write. The copy source and destination are the same key. | `RenameByCopyRefusesACaseOnlyRename` |
| Fallback: the copy fails part way with a Win32 error or a refused write | The destination key this call created is removed. The source is not touched. The original exception is rethrown. If the removal also fails, a `RegistryOperationException` names the partial copy and says it could not be removed (writer section J2). | `RenameByCopyRollsBackWhenAChildWriteIsRefused`, `RenameByCopyReportsAFailedRollback` |
| Fallback: the copy fails part way with an argument error (a value name containing NUL) | Same as above: the destination is removed and the `ArgumentException` is rethrown. Before the fix the partial copy stayed. | `RenameByCopyRollsBackWhenAValueNameIsRefused` |
| Fallback: a child named `.` or `..` | Copied. Child paths use `AppendEnumeratedName`. The sandbox policy refuses a remote write with such a component (the `..` and `.` row above), so the test uses a test-only policy that allows only the run root. | `RenameByCopyCopiesDotNamedChildren` |
| Fallback: the copy keeps the owner and class name | Owner written best effort before the DACL. Class name passed to the create. A refused owner is skipped, not reported. SACL, group, volatility and last write time are not copied (writer section J5). | `RenameByCopyCopiesTheOwnerAndTheClassName` |
| Fallback: the copy succeeds but deleting the source fails | The error is thrown with operation `RenameKey` and the message starts "Both keys now exist" and names the original and the copy. The copy is kept: a recursive delete is not atomic, so removing the copy could destroy the only complete tree. The original may be partly removed. | `RenameByCopyKeepsTheCopyWhenTheSourceDeleteFails` (route) |
| Remote `CopyTree` | Runs the managed copy, because `RegCopyTree` fails with 6 on a remote handle (measured). Merges into an existing destination, as `RegCopyTree` does. | `RemoteCopyTreeAndRecursiveDeleteOnRealLoopback` |
| Hive operations (`LoadHive`, `UnloadHive`, `SaveKey`, `ReplaceKey`, `RestoreKey`) on a remote store | Refused with 50 (`ERROR_NOT_SUPPORTED`) after the policy and before any native call. `SaveKey` must not create its file. | `RemoteHiveOperationsAreNotSupportedAndTouchNothing` |
| Counted-name value seam (E2E only) on a remote store | Refused with 50 before any native call. `NtSetValueKey` cannot take a remote handle. | `RemoteStoreRefusesCountedNameValuesBeforeNativeCode` |
| Security descriptor that is not self-relative, is truncated, has a bad declared length, has an offset outside the buffer, or is random bytes | `ArgumentException` before any native registry call, for local and remote writes. Nothing changes. | `SetKeySecurityRejectsMalformedDescriptorsBeforeNativeCode` |
| Propagating a descriptor to a remote subtree that contains a link | Refused by the sandbox before the first key changes, as locally. | Covered by the local propagation tests; not repeated remotely |

### 15.2 Remote rename and copy: measured path

Measured on loopback (`127.0.0.1`) on the R1 machine with a scratch P/Invoke probe under the remote sandbox (the
probe is not kept in the repo):

- `RegRenameKey` on a remote handle: 6 (`ERROR_INVALID_HANDLE`). The source is unchanged. So a remote rename runs
  the fallback.
- `RegCopyTree` (source key handle, null subkey, destination handle): 6 on a remote handle. So the fallback copies
  with managed code, `RegistryWriter.CopyTreeByHand`: each key's values are written with `SetValue`, its children
  with `CreateKey` and recursion, and its DACL last with `SetKeySecurity`. Reads come from the connection's reader.
  Each key's class name and owner are copied (the owner best effort, before the DACL). The DACL is copied per key.
  The SACL, the primary group, volatility and the last write time are not copied (writer section J5).
  Every write passes the policy.
- The fallback deletes the original with `DeleteKey(recursive: true)`, which is `RegDeleteTree`. That works on a
  remote handle (measured 0).
- A failed delete of the original keeps the copy and throws, with a message that names both keys (section 15.1).

### 15.3 Symbolic links on a remote store

The guard opens each component with `RegCreateKeyEx` and `REG_OPTION_OPEN_LINK`, then reads `SymbolicLinkValue` on the
returned handle. Measured on loopback: the remote open returns the link node, not its target. `RegQueryValueEx`
reports `REG_LINK` (type 6), so the option is honored. The loopback test `RemoteGuardRefusesSymbolicLinksOnRealLoopback`
checks the refusal.

### 15.4 Hive operations

Hive operations on a remote store throw `RegistryOperationException` with `ERROR_NOT_SUPPORTED` (50) after the policy
check and before any native call. They are outside the remote write set. A later piece decides the UI.

### 15.5 Measurements and remaining gaps

Measured on loopback, R1 machine, RemoteRegistry trigger-started (a `RegConnectRegistry` starts it):

| Call | Remote result |
| --- | --- |
| `RegConnectRegistry` to `127.0.0.1` and to `::1` | 0 and 0. Both connect. |
| `RegRenameKey` (remote handle) | 6 `ERROR_INVALID_HANDLE` |
| `RegCopyTree` (remote handles) | 6 `ERROR_INVALID_HANDLE` |
| `RegDeleteTree` (remote handle) | 0 |
| `RegGetKeySecurity` (DACL) and `RegSetKeySecurity` | 0 and 0 (size query returns 122, then the read) |
| Child key handle after `RegCloseKey` on the connection's root | Still valid (query returns 0). Supports the per-open pin design. |
| `RegCreateKeyEx` with `REG_OPTION_OPEN_LINK` on a link node | Opens the link node. `SymbolicLinkValue` is read with type 6, not followed. |

Loopback tests (real, not Inconclusive): `RemoteWritesRoundTripOnRealLoopback`, `RemoteWritesRoundTripOverTheIPv6Loopback`,
`RemoteRenameKeyLeavesOneKeyAtTheNewName`, `RemoteGuardRefusesSymbolicLinksOnRealLoopback`,
`RemoteSecurityDescriptorRoundTripsOnRealLoopback`, `RemoteCopyTreeAndRecursiveDeleteOnRealLoopback`.

Not covered by automated tests:

- Real RPC failures on write (section 15.1), other than a failed connect.
- Connect to a second computer. The loopback paths use the same RPC code.
- The timing of an in-flight Dispose. The measured child-handle result and the pin test cover the design.

## 16. Registry Editor commands on a remote key (R2a)

Scope: the key location of a remote tree node, the menus for remote nodes, Modify and the value editors, New,
Delete, Rename, Copy Key Name, Find, Export of a branch, the address bar, Permissions targeting, and the
Disconnect dialog. Import (R2b, section 17.3) and Print also work on a connected computer, and their loopback
tests are listed in section 11. Load Hive and Unload Hive are not covered for remote keys.

Model:

- A key location is a connection (or none for the local registry) and a `RegistryPath` in local form
  (`HKEY_LOCAL_MACHINE\...`). The tree node keeps the display path `\\COMPUTER\HKEY_LOCAL_MACHINE\...`.
- One `RegistryStore` per connection, made with `RemoteRegistryConnection.CreateStore` and the same
  `RegeditWritePolicy` as the local store. It is created on first use and dropped on disconnect.
- A display path that names a connected computer resolves to its location. A computer node has no key, so
  key commands are disabled on it. An unknown computer name does not resolve, so the command is refused.
- The Copy Key Name text is the display path, `\\COMPUTER\HKEY_...`, which is the classic form. Local copies
  keep `HKEY_...`.

Ways a remote command can fail, and the expected result:

| Way it could fail | Expected result | Test |
| --- | --- | --- |
| Computer node selected; New Key, Delete, Rename, Modify, Export of a key | Disabled. Nothing reaches the store | `Remote_LoopbackConnectShowsRemoteRootsAndMenus` |
| Remote hive root selected (`\\COMPUTER\HKEY_USERS`); Delete, Rename | Disabled, as on a local hive root | `Remote_LoopbackConnectShowsRemoteRootsAndMenus` |
| Remote key menu (RT_MENU 104) on a remote hive child or key | Same items and enable rules as the local key menu | `Remote_LoopbackConnectShowsRemoteRootsAndMenus` |
| New Key or a new value of each type under a remote key | Written under `HKU\<SID>\Software\BWC-E2E\<run>`. Read back with `reg query` | `Remote_KeyAndValueCommandsWriteOnlyUnderTheSandbox` |
| New Binary value (zero-length data) under a remote key | Written. The empty write passes a spare byte and length 0, because a null data pointer fails the remote RPC stub with Win32 1780 | `Remote_KeyAndValueCommandsWriteOnlyUnderTheSandbox` |
| New value whose name sorts before an existing value (registry order is creation order, the list is sorted) | The new row is selected and its inline editor opens. The selection uses the displayed position of the row | `Remote_KeyAndValueCommandsWriteOnlyUnderTheSandbox` |
| Modify a String, DWORD, QWORD, Multi-String, or binary value on a remote key | Written with the value's type. The list shows the new data | `Remote_KeyAndValueCommandsWriteOnlyUnderTheSandbox` |
| Rename a remote key (copy-then-delete fallback) and a remote value | One key or value at the new name, data kept | `Remote_KeyAndValueCommandsWriteOnlyUnderTheSandbox` |
| Delete a remote value and a remote key | Removed. The local sandbox is untouched | `Remote_KeyAndValueCommandsWriteOnlyUnderTheSandbox` |
| Remote write refused by the sandbox policy (non-loopback, outside the sandbox) | `RegistryOperationException` 5 shown with the classic message. Nothing written | Engine tests (`RemoteRegistryWriteTests`). The UI reaches only loopback under the sandbox |
| Connection removed while a remote value is selected or being written | `ObjectDisposedException` is logged by the command boundary and the generic command failure is shown. The tree is not left with a stale node after the next refresh | Not automated. Reviewed in code |
| Remote value read fails (RPC or access denied) | The classic "Cannot open key" message for the key, and an empty list. The node collapses | Not automated. Needs a second machine |
| Copy Key Name on a remote key | Clipboard holds `\\127.0.0.1\HKEY_USERS\<SID>\Software\BWC-E2E\<run>` | `Remote_KeyAndValueCommandsWriteOnlyUnderTheSandbox` |
| Find started on a remote key | Searches the remote subtree. A hit under the remote key selects the remote node | `Remote_FindLocatesRemoteValue` |
| Export branch of a remote key | A local .reg file. Its key names are the remote names with `HKEY_USERS\<SID>` written as `HKEY_CURRENT_USER`, so the file matches a local export of the same key line for line | `Remote_ExportBranchMatchesLocalExport` |
| Export of a remote key as Registry Hive Files | Refused before any write with the classic remote text (`DialogExportRemoteFormat`). Nothing is written and no raw error 50 is shown. Hive export stays local (`export.md`, section 8) | `Remote_ExportHiveOfRemoteKeyIsRefused` |
| Export of the Computer node of a connected computer (whole registry) | One .reg document of `HKEY_LOCAL_MACHINE` and `HKEY_USERS`, streamed key by key. An unreadable root or key is skipped with its subtree, as the local whole-registry export does (`export.md`, "Whole-computer export") | Not automated. Reviewed in code |
| Remote key command that reads or writes (New Key, Delete, Rename, value rename) | The registry call runs on a background task. The UI thread stays responsive while the loopback call waits, and the result is shown as before | Not automated. Reviewed in code (`KeyCommands`, `ValueCommands`) |
| Address bar `\\127.0.0.1\HKEY_USERS\<SID>\...` (connected) | Navigates to the remote node | `Remote_AddressBarNavigatesToRemotePath` |
| Address bar with a remote path to a computer that is not connected | "The selected branch does not exist" (DialogAddressBarKeyNotFound). Nothing is connected | `Remote_AddressBarNavigatesToRemotePath` |
| Permissions on a remote key | The dialog targets the remote key (the same store and path). Apply writes through the connection | `Permissions_RemoteKeyApplyChangesTheDaclThroughTheConnection` (loopback: Everyone Read is removed, the user's entries are kept, the original DACL is restored) |
| Add to Favorites on a remote key | Refused with the classic favorite error text. Favorites store only local paths, and no oracle measurement exists for a remote favorite | Not automated |
| Disconnect dialog 107 with one computer selected | The computer leaves the tree and its connection is disposed. The local selection stays | `Remote_DisconnectDialogRemovesComputer` |
| Disconnect dialog 107 with OK and no selection | Classic message, dialog stays open | `Remote_DisconnectDialogRemovesComputer` |
| Connect to `127.0.0.1` in an E2E build | Accepted. The shipping rule for the local name is unchanged | `Remote_LoopbackConnectShowsRemoteRootsAndMenus` |

Check status: the loopback E2E tests need the Remote Registry service to start on connect. A connect that fails
records `not run: remote registry unavailable (Win32 <code>)` as Inconclusive and does not fail the run (the
`ConnectOrSkip` rule of section 15).

## 17. Connect picker, connect errors, and Import into a connected computer (R2b)

Scope: File > Connect Network Registry (the computer picker that replaces the text prompt of section 8, and
the connect error text), and File > Import of a .reg file into a connected computer. Load Hive, Unload Hive,
and Print stay local. A binary hive file restores locally, as before.

The E2E build reads `BWC_E2E_CONNECT_TARGET`. When it is set, Connect skips the picker and uses that name.
A shipping build ignores the variable.

### 17.1 Connect picker

The picker is the system object picker (`IDsObjectPicker`, computers only, single select). It has three scopes:
the target computer, the workgroup, and the joined domains. It is owned by the main window. Cancel leaves no
connection. A picker that cannot be created or initialized falls back to the text prompt of section 8.

Status (R2b): the picker opens with the caption `Select Computer`. The first failure was the struct layout:
`DSOP_FILTER_FLAGS` puts the uplevel flags first (objsel.h), and the first build had the downlevel field first, so
`Initialize` returned E_INVALIDARG (0x80070057). The picker uses two scopes, the downlevel scope (workgroup) and the
uplevel scope, both with the computer filter. Any failure before or after the picker shows falls back to the text
prompt, logged, with no error box. The struct sizes are asserted at startup in E2E builds (56 and 48).

| Way it could fail | Expected result | Test |
| --- | --- | --- |
| File > Connect opens the picker | Caption `Select Computer`. Owned by the main window. Main window disabled while it is open | `Remote_ConnectDialogIsOwnedAndCancelLeavesNoConnection` |
| Picker cancelled (`S_FALSE`) | No connection, no tree node, main window enabled, Disconnect still disabled | `Remote_ConnectDialogIsOwnedAndCancelLeavesNoConnection` |
| Picker class not registered, or activation throws | The text prompt of section 8 opens. Connect continues as before | Not automated: needs an unregistered COM class. Reviewed in code (`ComputerPicker` returns Unavailable) |
| `Initialize` fails | Same as the row above | Not automated. Reviewed in code |
| Picker returns no selection (empty `DS_SELECTION_LIST`) | Treated as cancel | Not automated. Reviewed in code |
| Picker returns a name that is already connected | `That computer is already connected.` No second connection | Not automated. Reviewed in code |
| Picker returns a name with a backslash or slash | Refused by the connect name rules, shown as string 154 | Not automated. Reviewed in code |
| `BWC_E2E_CONNECT_TARGET` set in a shipping build | Ignored. The variable is read only under `BWC_E2E` | Code review (`#if BWC_E2E` in `HiveRemoteCommands`) |

### 17.2 Connect errors (classic text)

Classic text: RT_STRING 152 is the caption, 153 to 156 are the text. The text prompt used to refuse the own
computer inline (`DialogConnectLocal`). Now the name goes to `RemoteRegistryConnection.Connect`, which refuses it
with `OwnComputer` before any native call, and the error box shows 153.

| Way it could fail | Expected result | Test |
| --- | --- | --- |
| Own computer name (E2E target is `Environment.MachineName`) | Error box, caption string 152, text string 153. No tree node | `Remote_ConnectOwnComputerShowsStringOneFiftyThree` |
| Unreachable name (reserved `.invalid` host) | Error box with string 154 naming the computer, within 60 s. The UI thread stays responsive during the wait (`IsHungAppWindow` is false on every probe) | `Remote_ConnectUnreachableShowsStringOneFiftyFour` |
| `RegConnectRegistry` returns access denied (5) | String 156 naming the computer | Not automated: needs a host that refuses the caller. `MessageCatalog.ForConnect` maps it |
| HKEY_LOCAL_MACHINE connects, HKEY_USERS fails | String 155 (no computer name). The HKLM handle is closed first (section 5) | Not automated: needs a host with one root refused. `MessageCatalog.ForConnect` maps it |
| Any other Win32 code from `RegConnectRegistry` | String 154 | Same as the unreachable row |

### 17.3 Import into a connected computer

Flow: File > Import shows the file dialog first. A .reg file then asks dialog 115 (caption `Import Registry File`,
label `Select Computer:`) when at least one computer is connected. The list has the local computer first, then
each connected computer. The local computer is selected. OK imports into the chosen target. Cancel imports
nothing. A remote target writes through that connection's store, with the same write policy as section 16.
Without connections, no dialog 115 appears. The second-launch and command-line .reg import stays local.

| Way it could fail | Expected result | Test |
| --- | --- | --- |
| File > Import with no remote connection | No dialog 115. Unchanged | Existing local import checks in `ImportExportTests` |
| Dialog 115 with one remote connection | Caption and label as above. List: local name, then `127.0.0.1`. Local selected. OK and Cancel | `Remote_ImportWritesThroughTheConnection` |
| Cancel on dialog 115 | Nothing imported. No progress window. Main window enabled | `Remote_ImportCancelImportsNothing` |
| Remote target, every section under `HKEY_USERS\SID` | Values written through the connection. Result is string 128 with the computer name. Values read back through the connection | `Remote_ImportWritesThroughTheConnection` |
| Remote target, a section under `HKEY_CURRENT_USER` | That section is not written: the remote store has no HKCU root (section 6), so its write fails with `ArgumentException`, which the importer records as a rejected write. Other sections are still written. Result is string 132 | `Remote_ImportMixedRootsReportsStringOneThirtyTwo` |
| Remote target, the .reg file is gone before the import starts | String 135 with the computer name. Nothing written | Not automated: the file dialog lists existing files only. Reviewed in code |
| Remote target, the connection cannot read `HKEY_USERS` before the first write (`RegistryOperationException`), or was disposed before the import started | String 131 with the computer name. Nothing written. The pre-check calls `GetKeyInfo` on the `HKEY_USERS` root, which queries the remote key, so a dead connection fails here. It used to call `KeyExists`, which pins the root and makes no network call, so a dead connection gave per-section failures instead. The read runs inside the write gate, and Disconnect is ignored while the gate is held | Success path: `Remote_ImportWritesThroughTheConnection` (the pre-check passes on a live connection). Failure path: not automated, because a dead connection needs the RemoteRegistry service stopped, which the tests never do. Reviewed in code |
| Remote target, rejected write on one key (access denied) | String 132, as for a local import. The import keeps going | Not automated for remote. `ImportResultTests` covers the local case |
| Binary hive file chosen with a remote computer connected | No dialog 115. Local restore onto the selected local key, as before. A remote selected key shows `DialogRemoteReadOnly` | Not automated. Reviewed in code. Deviation: hive operations are refused on a remote store (section 15.4) |

String 134 is not used. Its full text is "The specified file is not intended for use with this version of
Windows." It describes the file, not the root. A key under a root the remote store does not have is a rejected
write, which string 132 reports.

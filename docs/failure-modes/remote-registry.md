# Remote registry connection failure modes

Scope: `RemoteRegistryConnection` in `src/Bwc.Registry`, the remote root routing in
`RegistryReader`, the File > Connect Network Registry and File > Disconnect Network Registry
commands, and the remote computer nodes in the Registry Editor tree. Remote access is read only
in this implementation. It never sends a mutating API to a remote computer.

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

1. Show an owned dialog with a computer name text box, OK, and Cancel.
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
enabled only when the connection list is non-empty. Edit, Delete, Rename, Import, Permissions,
Print, and Export stay disabled for remote selections because `TryGetSelectedKeyPath` does not parse
a remote display path. Favorites may still be added for a remote path only if the path resolves;
otherwise the normal "key not found" error is shown.

## 11. E2E and manual coverage

The non-elevated E2E suite covers:

- File > Connect Network Registry opens an owned modal, disables the owner, and cancels cleanly.
- The local computer name is rejected with the own-computer message and no tree node appears.
- Empty, NUL, separator, and overlong names are covered by the registry unit tests; the E2E test
  exercises the local-computer validation because it needs no network.
- File > Disconnect Network Registry is disabled while no computer is connected.
- The main window is enabled again after cancel and no connection is left behind.

A real remote connection, remote browsing, and the disconnect list require a second computer or a
VM snapshot with the Remote Registry service and network permissions. Those checks are manual and
must run only on an isolated network. The test never writes to a remote computer.

## 12. Native calls and process lifetime

`RegistryReader` and `RemoteRegistryConnection` call `RegConnectRegistryW`, `RegOpenKeyEx`, and the
read-only query APIs. No mutating API is added outside `RegistryWriter`, and `RegistryWriter` has no
remote routing. Root pins use `DangerousAddRef` and `DangerousRelease` on the connection's own
handles; no other handle is shared. `scripts/check-registry-writer.js` continues to enforce that boundary.

The connection objects live in the Registry Editor view, not in a static cache. Closing the view
disposes them. No credential, password, or token is stored; authentication uses the process token.

## Parity package: remote write refusal

| Way it could fail | Expected behavior |
| --- | --- |
| A write command (Load Hive, Import, Edit, New, Delete, Rename) runs with a remote node selected | Refused or disabled. Nothing reaches the writer |
| Disconnect dialog opens with the wrong size or colors | Port of DLG-013 on `ClassicDialog`: 250 x 111 DLU, label at (7, 7), list at (7, 18) 236 x 63, OK at (138, 90), Cancel at (193, 90) |

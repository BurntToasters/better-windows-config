# Registry writer failure modes

This document covers `src/Bwc.Registry`: `RegistryPath`, `RegistryWriter`, the write policies,
and the guard script `scripts/check-registry-writer.js`. The writer is the only code allowed to
call mutating registry APIs. Every mutation passes through `IRegistryWritePolicy` first.

## 1. Path prefix confusion (`Foo` versus `Foobar`)

`RegistryPath.IsDescendantOf` compares root and components, not display strings. `Software\Foo`
is not a descendant of `Software\F`, and `Software\Foobar` is not a descendant of `Software\Foo`.
A string prefix check would allow both. Tests cover these exact pairs.

`SandboxWritePolicy` uses the same component-wise comparison, so a sandbox root of
`HKCU\Software\BWC-E2E\run1` does not authorize `HKCU\Software\BWC-E2E\run10`.

## 2. Relative path escape

`Parse` accepts only absolute forms with a known root (or the optional `Computer\` prefix). It
rejects empty components, `.` and `..`, leading separators, interior doubled separators, and a
trailing separator. `Append` accepts one component and rejects separators, NUL, `.`, and `..`.
There is no API that resolves a relative path against an open handle, so `..` cannot escape a
sandbox after parsing.

A trailing separator is rejected instead of trimmed. Trimming would make two spellings of one
path compare differently depending on entry point.

## 3. Case-insensitive names

Registry key and value names are case-insensitive. `RegistryPath` equality, hashing, and
descendant checks use `StringComparer.OrdinalIgnoreCase`. `HKCU\Software\Foo` equals
`hkcu\software\foo`. The writer never lowercases or case-folds a name before sending it to the
registry; it preserves the caller's spelling for creation while comparing case-insensitively
for policy decisions.

## 4. Default value (`""`)

The default value has an empty name. `SetValue` and `DeleteValue` accept `""` as the value name;
`null` is rejected because the native APIs treat a null name differently and .NET nullability
would hide the difference. Tests write and delete the default value through the real registry.

## 5. REG_LINK values and link keys

A value of type `REG_LINK` (6) is a symbolic link. `SandboxWritePolicy.AllowsLinkValues` is
false, so `SetValue` inspects the type before calling native code and throws access denied. The
check happens before any native mutation, so a refused link leaves no value behind. The shipping
policy allows link values because the applications may need them, but no current caller writes
one.

Link keys are created by passing `REG_OPTION_CREATE_LINK` to `RegCreateKeyEx`. `RegistryWriter`
does not expose that option and never sets it. `CreateKey` only creates normal subkeys. The check
script flags any raw `RegCreateKeyEx` usage outside the writer.

## 6. Hives (B8)

`LoadHive`, `UnloadHive`, `SaveKey`, `ReplaceKey`, and `RestoreKey` touch files and can mount or
replace whole hives. They are not implemented yet. Each method calls the write policy for the
target path, then throws `NotImplementedException` naming B8. No native hive function is called.
The native surface is present in `NativeMethods.txt` so the B8 work does not need to modify the
generated interop setup.

## 7. Security descriptors (B8)

`SetKeySecurity` changes DACLs and can lock out the user or grant broad access. It calls the
write policy, then throws `NotImplementedException` naming B8. `RegSetKeySecurity` and
`RegGetKeySecurity` are in the native surface but are not called by the writer. This behavior is
covered by the B8 guard.

## 8. Remote registry (B10)

`RegConnectRegistry` can attach a remote hive. The writer exposes no remote method in this
batch. Any future remote method must call the policy with `RegistryWriteOperation.Remote` and
name B10 in its guard. The native surface includes `RegConnectRegistry` so that work can start
without changing the interop file.

`RegDisconnectRegistry` is not a documented or exported advapi32 function. Windows metadata does
not contain it. Disconnecting a remote hive is done with `RegCloseKey`, which is what the safe
handle already calls. Adding the name to `NativeMethods.txt` would fail source generation, so it
is intentionally absent.

## 9. WOW64 view

Every `RegOpenKeyEx`, `RegCreateKeyEx`, and `RegDeleteKeyEx` call includes
`KEY_WOW64_64KEY`. This keeps the writer on the 64-bit registry view even when the process is
32-bit. `RegDeleteKeyEx` also receives `KEY_WOW64_64KEY` in its `samDesired` parameter because
that parameter selects the view for the delete. Tests use `RegistryView.Registry64` for
assertions.

Mixing views would create or delete a key in a different view than the caller inspected. The
writer never omits the flag and never uses `KEY_WOW64_32KEY`.

## 10. 32-bit emulation on ARM64

On ARM64 Windows, x86 and ARM32 processes can run under emulation. Registry redirection still
applies. The explicit 64-bit view flag makes the writer independent of the process bitness. The
ARM64 build is the native build used by acceptance. A 32-bit emulated caller would still reach
the 64-bit view because of the flag.

## 11. Open handle leaks

Every opened key is wrapped in `SafeRegistryKeyHandle`, which releases with `RegCloseKey` and is
consumed with `using`. Predefined roots are wrapped as non-owning handles so they are never
closed. No public API returns a handle. A failed native call that produced a handle still
disposes it.

`SafeRegistryKeyHandle` derives from `SafeHandleZeroOrMinusOneIsInvalid`, so a null or `-1`
handle is not released and the finalizer path is safe.

## 12. Partial delete and partial copy

`RegDeleteTree` can delete some children and then fail, for example on a protected subkey. The
writer maps the error but cannot roll back the partial delete. `RegCopyTree` can copy some values
and subkeys before failing, leaving a partial destination. The writer reports the Win32 error and
does not attempt cleanup because cleanup itself could delete user data. Callers that need
atomicity must copy to a new staging key and rename or delete it after success.

`RegCopyTree` also copies the source key security descriptor. The destination handle therefore
needs `WRITE_DAC` in addition to read and write access. With only `KEY_WRITE`, the call fails with
ERROR_ACCESS_DENIED even though the value and subkey rights are present.

Recursive deletion of a predefined root is rejected before native code. Otherwise a policy that
allowed the path could call `RegDeleteTree` on an entire hive.

## 13. Rename across parents

`RegRenameKey` renames a subkey within one parent. `RegistryWriter.RenameKey` takes a full path
and a new name that must be a single component; it does not accept a destination path. This
prevents an accidental cross-parent move, which `RegRenameKey` cannot perform in one call
anyway. A name containing a backslash is rejected. A name that collides with an existing sibling
maps to the native error.

## 14. Value name length

Registry value names are limited to 16383 characters. The writer rejects longer names with
`ArgumentException` before native code. This keeps the failure deterministic instead of relying
on `ERROR_MORE_DATA` or a truncated write. Key components are limited to 255 characters by
`RegistryPath`.

## 15. Embedded NUL

A .NET string can contain U+0000. Native wide-string APIs stop at the first NUL, so a name such
as `Foo\0Bar` would silently operate on `Foo`. `RegistryPath`, `Append`, `SetValue`,
`DeleteValue`, and `RenameKey` reject embedded NUL. Tests cover a NUL in a path, a value name,
and a rename component.

## 16. ERROR_MORE_DATA

Read APIs can return `ERROR_MORE_DATA` (234) when a buffer is too small. The writer is
write-only, but the exception type preserves the raw Win32 code so a future reader can
distinguish a retryable buffer problem from a permanent failure. The check script does not
inspect read APIs. `RegistryOperationException.HResult` is `0x8007xxxx` so tooling can recover
the Win32 code from the HRESULT.

## 17. Policy bypass via raw handles

`RegistryWriter` takes and returns only `RegistryPath`. Handles are internal to
`Bwc.Registry`. The generated P/Invoke surface is internal. `IRegistryWritePolicy` has no
handle-based overload, so a caller cannot pass an already-open handle to skip the path check.

The remaining bypass is adding a new file that calls advapi32 directly. `scripts/check-registry-writer.js`
scans every `.cs` file under `src/` except `RegistryWriter*.cs` and fails on the mutating API
names. The script is a guard, not a security boundary: a determined author could use reflection,
a differently named `DllImport` entry point, or a native library. Code review still matters.

## 18. SandboxWritePolicy leaking into shipping

`SandboxWritePolicy` is compiled only when `BWC_E2E` is defined. The shipping test assembly is
built without `BwcFlavor=E2E` and asserts by reflection that the type is absent. The E2E test
project is built with `BwcFlavor=E2E` and asserts the sandbox behavior against the real registry.
The shipping assembly also has no public API that constructs a sandbox policy, so a shipping
caller cannot enable it at runtime.

## 19. Check script false positives and false negatives

The script strips `//` and `/* */` comments while respecting string and character literals, so a
URL such as `https://example` does not hide a later call on the same line. Identifiers inside
string literals are still matched, which can produce a false positive for an error message or an
`EntryPoint = "RegSetValueEx"` declaration. That is deliberate: a direct P/Invoke with a
different method name would otherwise pass.

False negatives remain for reflection, native libraries, generated code under `obj/`, and
interpolated string holes whose contents are not parsed as code. Generated files under `bin/`
and `obj/` are skipped.

`--self-test` builds a temporary tree containing one violating file, runs the same scan function,
requires exit code 1 and a reported violation, then deletes the tree. A self-test that silently
passes would mean the scan is broken.

## 20. Test cleanup failure

Each registry test creates one `HKCU\Software\BWC-E2E\<guid>` root and deletes it in a `finally`
block using the 64-bit view. If cleanup throws, the test fails instead of hiding the leak. The
parent `HKCU\Software\BWC-E2E` may remain empty; the acceptance check looks for child keys. A
crash that prevents `finally` from running leaves a key whose name contains the guid, which is
easy to identify and remove by hand.

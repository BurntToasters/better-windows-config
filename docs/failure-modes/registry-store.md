# Registry store failure modes

This document covers `RegistryStore` in `src/Bwc.Registry`, the typed convenience layer over
`RegistryWriter` and `RegistryReader`. Every mutation still goes through one `RegistryWriter`
instance and its write policy. `RegistryStore` adds no native calls, no locks, and no
transaction.

## 1. No transaction across calls

`RegistryStore` is a sequence of independent registry operations. A failed call leaves every
earlier successful call in place. For example, if a caller sets three values and the third
`SetValue` fails, the first two values remain. There is no multi-key atomicity, no write-ahead
log, and no rollback of create, delete, or copy work. Callers that need all-or-nothing behavior
must build a staging key, verify each step, and delete or rename the staging key themselves.

The registry serializes each individual native operation. It does not make a group of
operations atomic.

## 2. CreateKey

`CreateKey` creates one key component. The parent must already exist; the store does not create
missing ancestors. Creating a key that already exists succeeds and leaves its values and
subkeys untouched, which matches `RegCreateKeyEx` with `REG_OPTION_NON_VOLATILE`. The write
policy is consulted before native code, so a denied path creates nothing.

A failure after an ancestor was created by a separate call is not undone. Callers that create a
deep tree should be prepared to delete the highest key they own when a later step fails.

## 3. DeleteKey and recursive destruction

`DeleteKey(path, recursive: false)` maps to `RegDeleteKeyEx` and fails when the key still has
subkeys. `DeleteKey(path, recursive: true)` maps to `RegDeleteTree` and removes the key plus all
descendants. A recursive delete can remove some children and then fail, for example on a
protected subkey. The store reports the Win32 error and does not attempt to restore what was
already removed. Predefined roots are rejected by the writer before native code.

Deletion is permanent. The registry has no recycle bin or undo. The store never prompts and
never stages deleted data.

## 4. CopyTree

`CopyTree` maps to `RegCopyTree` and requires the destination key to exist; the store does not
create it. The destination handle needs read, write, and `WRITE_DAC` access because the native
call copies the source key security descriptor. A destination whose ACL denies `WRITE_DAC`
fails with access denied even when value and subkey writes alone would have been allowed.

The copy can partially complete and then fail. Values and subkeys copied before the failure
remain, values already present at the destination may be overwritten, and the destination
security descriptor may already have been replaced. The store does not clean up a partial copy
because cleanup could delete data the caller expected to keep. A careful caller copies into a
new staging key, checks the result, and only then renames or deletes the staging key.

Copying also copies permissions and inheritable access rules. A copy operation is therefore a
security change, not only a data copy.

## 5. RenameKey

`RenameKey` maps to `RegRenameKey` with one new name component. It cannot move a key to another
parent. The native call is a single operation, so there is no partial rename inside the key.
A name collision with an existing sibling returns the native error and leaves both keys in
place. The store does not merge or overwrite a colliding key.

## 6. RenameValue order and rollback

There is no native rename for a value. `RenameValue` reads the old value through
`RegistryReader`, writes the same raw type and data under the new name through
`RegistryWriter`, and then deletes the old name. Names and data are not decoded and re-encoded,
so odd lengths such as a 3-byte `REG_DWORD` survive.

If the write of the new name fails, the old value is untouched and the error is rethrown
unchanged. If the old-value delete fails, the store tries to delete the newly written value. If
that rollback succeeds, the original delete error is rethrown unchanged and the old value remains
in place. If the rollback also fails, the store throws a new `RegistryOperationException` that
keeps the original operation, path, and Win32 code and adds text naming the rollback failure. A
delete failure that is not a `RegistryOperationException` (for example an argument error raised
by the writer) follows the same rollback attempt and is rethrown unchanged when rollback
succeeds; if that rollback also fails, an `AggregateException` carries both errors.

A process crash between the write and the delete leaves two values with the same data. The
operation is not atomic.

## 7. RenameValue target overwrite

If the new name already exists, `RenameValue` refuses before any write. It throws
`RegistryOperationException` with operation `RenameValue`, the key path, and Win32 error 183
(`ERROR_ALREADY_EXISTS`). The UI maps that code to the classic "specified value name already
exists" message. The existing target value is not read, written, or deleted. Tests:
`RenameValueRefusesAnExistingTarget`, which also checks that the target keeps its data.

## 8. RenameValue with the same name

Registry value names compare case-insensitively. Renaming to the identical spelling returns
without a native call. A case-only rename (`Old` to `OLD`) names the same value, so it is not an
"already exists" case. It is done as delete then recreate: the old value is read, the old name is
deleted, and the value is written under the new spelling. If the write fails, the store writes the
old name back with the saved type and data, and rethrows the write error. Writing first would keep
the stored casing, so the spelling would not change. Test: `RenameValuePreservesRawDataAndHandlesTheDefaultName`
(case-only step).

## 9. Typed overflow

`SetDword` and `SetDwordBigEndian` take `uint`; `SetQword` takes `ulong`. A caller cannot pass a
number wider than the destination type without a compile error, so the typed setters never
truncate. Negative values cannot be passed as `uint` without an explicit unchecked conversion
chosen by the caller. `DwordBigEndian` writes the four bytes most-significant first; the type
number alone does not make Windows swap bytes, so the store performs the encoding in the codec.

Raw `SetValue` accepts any byte length. A 3-byte `REG_DWORD` is stored as 3 bytes, reads back
as 3 bytes, and displays as `(invalid DWORD (32-bit) value)`. The store does not pad, widen, or
reject odd lengths.

## 10. String terminator and embedded NUL

`SetString` uses `RegistryValueCodec.EncodeString`, which encodes UTF-16LE and appends exactly
one terminating NUL. An empty string becomes two bytes, `00 00`, which is the default value
data for an empty `REG_SZ`. Embedded NUL characters inside the managed string are preserved by
the encoder; for example `ab\0cd` becomes the bytes for `ab\0cd\0`. The display decoder stops at
the first NUL, so the UI shows `ab`, but the stored bytes and a raw read keep all of them.

`RegSetValueExW` also appends a NUL to string data that does not already end with one. A
no-terminator `REG_SZ` therefore cannot be produced through `SetString` or `SetValue` on this
platform. The codec test suite exercises no-terminator decoding directly. No `EncodeStringRaw`
store method was added because it would only offer an encoding the native write path normalizes
away.

The store does not reject a value string that is empty or contains NUL. Value names follow the
writer rules: the empty name is the default value, names with embedded NUL are rejected, and
the platform length limit is enforced by the writer rather than duplicated in the store.

## 11. Multi-string encoding

`SetMultiString` uses `RegistryValueCodec.EncodeMultiString`. Each entry is written with one
NUL and the list ends with one extra NUL, so `one`, an empty string, and `two` become
`one\0\0two\0\0`. Interior empty strings are preserved. An empty list becomes four zero bytes,
the classic double terminator; a list holding one empty string has the same bytes, and the
format cannot distinguish them. A caller that must tell those two cases apart cannot do so with
`REG_MULTI_SZ` alone.

On read, `DecodeMultiString` reports the strings and whether the data ended with a double NUL.
The store returns raw `RegistryValueInfo` values and leaves that interpretation to the caller.

## 12. Missing key and missing value

`GetValue` returns `null` when the key opens but the requested value does not exist.
`GetValues` returns the current enumeration or throws when the key cannot be opened. Both
delegate to `RegistryReader` and keep its distinction between a missing key and a missing value.

A setter or `DeleteValue` on a missing key fails through `RegistryWriter` with the native
`ERROR_FILE_NOT_FOUND` mapped to `RegistryOperationException`. `DeleteValue` on a missing value
also returns that native error. `RenameValue` treats a missing source value as
`ERROR_FILE_NOT_FOUND` with operation `RenameValue` and does not write anything.

`CreateKey` on a missing parent fails with the native error; there is no implicit ancestor
creation.

## 13. Access denied

A denied path is rejected by `IRegistryWritePolicy` before native code. The policy builds a
`RegistryOperationException` with Win32 error 5 and the operation name. Nothing is created,
changed, or deleted by that call. An operating-system ACL denial reaches the same exception
type through the writer, but it can occur after earlier operations in a caller's sequence have
already succeeded. The store does not convert access denied into a boolean or silently skip it.

Reads follow `RegistryReader` rules: access denied on a read is an exception, not an empty
result.

## 14. Policy bypass

`RegistryStore` holds one writer and does not expose a handle-based overload. Every mutating
method is a thin call to that writer, so the same policy check applies to typed setters,
`RenameValue`, and all key operations. A caller can still construct a `RegistryWriter` with
`ShippingWritePolicy` and hand it to `RegistryStore`; the store applies the policy it was given.
The E2E sandbox is a test build feature, not a security boundary.

The remaining bypasses are the same as for the writer: adding a new source file that calls
advapi32 directly, reflection, a differently named native entry point, or native code outside
the managed assembly. `scripts/check-registry-writer.js` catches the first case inside `src/`.
Code review remains necessary.

## 15. Default value

The default value has the name `""`. Every typed setter and `DeleteValue` accepts that name.
`RenameValue` can move the default value to a named value and can move a named value back to
the default name. Those operations use the same copy-then-delete order, so all rollback and
partial-failure rules above apply. `GetValue(key, "")` reads it and `RegistryReader` also offers
`ReadDefaultValue`.

## 16. Destruction without confirmation

The store performs destructive operations immediately. `DeleteKey` with `recursive: true` can
remove an entire subtree, and `DeleteValue` can remove the only copy of a value. There is no
confirmation parameter and no policy hook that asks a user. The UI layer owns confirmation
prompts and any undo design. Callers that expose these methods to a user must confirm first.

## 17. Concurrent writers

The store takes no lock. Two stores, or two processes, can write the same key. Individual native
calls are serialized by the registry, but a sequence across calls is not. A value can change or
disappear between `RenameValue`'s read and its write or delete:

- If it disappears before the read, the rename reports `ERROR_FILE_NOT_FOUND`.
- If it disappears after the read but before the delete, the delete fails. The rollback then
  deletes the new name, which may remove a value another writer created at that name after this
  rename wrote it.
- If another writer changes the old value after the read, the new name receives the earlier
  bytes and the later bytes are lost when the old name is deleted.

There is no optimistic concurrency check and no version stamp. Callers that share a key with
another writer must coordinate outside the store. Renaming over an existing target has the same
last-writer-wins behavior.

## 18. Exception mapping

Every `RegistryOperationException` from the writer or reader is rethrown unchanged, including
its operation name, full path, Win32 code, and message. The store does not wrap errors in a
higher-level message or a different exception type. The one deliberate exception is the
compound `RenameValue` rollback failure described in section 6, which keeps the original Win32
code and adds rollback text. Argument validation that happens inside the writer still surfaces
as `ArgumentException`, not as a registry error.

## 19. Test cleanup

Each store test creates one `HKCU\Software\BWC-E2E\<unique-run-id>` root with the 64-bit view
and deletes that root in a `finally` block using `DeleteSubKeyTree` with
`throwOnMissingSubKey: false`. The rollback-failure test deliberately leaves an extra value
inside its run root; deleting the root removes it. The sandbox parent may remain empty, and
acceptance checks for child keys. A crash that skips `finally` leaves a key whose name contains
the run id, which is easy to identify and remove by hand.

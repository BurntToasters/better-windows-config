# Favorites failure modes

Scope: the Registry Editor Favorites menu, the Add to Favorites prompt, the Remove Favorites
multi-select dialog, and `FavoritesStore` in `src/Bwc.Regedit/Registry/FavoritesStore.cs`. The
classic storage key is
`HKCU\Software\Microsoft\Windows\CurrentVersion\Applets\Regedit\Favorites` (BEH-001). Each favorite
is a `REG_SZ` value whose name is the display name and whose data is the full
`Computer\HKEY_...` display path. The long display form is used consistently so a stored path can be
handed to `RegistryPath.TryParse` and `RegistryTreeSource.FindPathAsync` without a second format.

## Store contract

### Missing Favorites key

A machine that has never used favorites has no Favorites key. `Reload` treats a missing key as an
empty list, not as an error, so the first launch shows only Add to Favorites and a disabled
Remove Favorite. Add creates the key through the write policy before writing the first value.

### Duplicate names and case

Names are compared with `StringComparer.CurrentCultureIgnoreCase`, matching the Windows registry's
case-insensitive value-name lookup. `Alpha` and `alpha` are the same favorite. The prompt validates
before it accepts and shows RT_STRING 171, "There is already a favorite with that name.", inside the
dialog. `FavoritesStore.Add` repeats the check as a safety net so a caller that bypasses the dialog
still cannot overwrite an existing name. The store never silently replaces a value.

### Invalid and empty names

The dialog rejects an empty or whitespace-only name. The store also rejects names longer than 16383
characters and names containing NUL, matching the value-name limit in `RegistryWriter`. A rejected
name leaves the dialog open and the existing favorites untouched.

### Sorting

`Favorites` is sorted with `StringComparer.CurrentCultureIgnoreCase`. The menu and the Remove list
show the same order. Sorting is a view concern: the registry enumeration order is never assumed.
Adding or removing one entry rebuilds the list from a fresh read, so the order cannot drift after a
classic regedit edit.

### Multi-remove

Remove opens one dialog with a multi-select list. The selected names are removed one at a time.
Deleting a name that disappeared between the read and the delete is treated as already removed, so
a partial race does not leave the dialog stuck or stop the remaining names. Any real write failure
is reported through the standard registry error dialog and the menu is refreshed from disk, which
keeps the view honest even after a partial removal.

## Paths and navigation

### Missing path

Favorites can outlive the key they point at. Clicking such an entry resolves the stored path through
`RegistryTreeSource.FindPathAsync`; when the key no longer exists, the host shows the address bar
key-not-found error with the attempted path, leaves the current selection in place, and stays
usable. It never crashes and never writes the missing path back to the store.

### Computer is not a favorite

The virtual Computer root has no registry path, so Add to Favorites refuses it with a message
instead of writing a value that cannot be navigated. A predefined root such as HKEY_CURRENT_USER is
allowed; its default name is the root display name.

### Real and sandbox Favorites path

Ship builds read and write the classic path. E2E builds honor `BWC_E2E_FAVORITES_ROOT` only when
the value parses as a `RegistryPath` and is the configured `BWC_E2E_SANDBOX_ROOT` or a descendant of
it. An invalid value, a path outside the sandbox, or a missing sandbox root falls back to the
classic path for reads; writes are still constrained by `RegeditWritePolicy`. The environment
variable is compiled out of ship builds.

### Policy denial

Every write goes through `RegeditWritePolicy.CreateStore()`. In an E2E build with
`BWC_E2E_SANDBOX_ROOT` set, `SandboxWritePolicy` denies a favorites path outside that root with
Win32 error 5 before any registry handle is opened. The UI shows the normal registry error dialog
and does not update the in-memory list. A test or a stale environment cannot redirect a write into
the developer's real Favorites key.

### Access denied on read

A locked-down or redirected Favorites key can fail enumeration or value reads. `Reload` catches the
failure, keeps the previous list, and exposes the message through `LoadError`. The host logs it and,
when a window is available, shows an error dialog. A failed refresh must not erase a usable menu or
turn Add and Remove into silent no-ops.

### Stale menu list

The dynamic entries are a snapshot. Editing the Favorites key with classic regedit does not update
the open menu. The list is refreshed at startup, after Add, after Remove, and whenever the host
rebuilds the menu. A refresh failure keeps the previous entries rather than showing a half-built
menu. The two fixed commands and the Favorites parent id 517 keep their resource text and
AutomationIds.

### Classic sharing

Because the same key and value layout are used, favorites created by classic regedit appear here
after the next refresh, and favorites created here appear in classic regedit. Unknown value types
under the Favorites key are skipped with a warning instead of being shown as a blank path. The
classic editor is free to change the key between reads; the next reload reconciles the list.

## Dialog and focus behavior

### Escape and focus return

Escape cancels both favorite dialogs through `ModalDialogWindow`. After Add or Remove completes,
focus returns to the tree so the keyboard path continues where it started. The remove dialog
refuses an empty selection with an inline message instead of closing with no effect.

### Empty remove

Remove Favorite is disabled when the list is empty. If a stale command still reaches the handler,
the host shows a message and does not open an empty list box.

## E2E and cleanup

### Test isolation

The Favorites E2E test sets `BWC_E2E_FAVORITES_ROOT` to
`HKCU\Software\BWC-E2E\<runId>\Favorites` and `BWC_E2E_SANDBOX_ROOT` to the run root. It records the
classic Favorites value names before and after the run and asserts they are unchanged. The test only
runs the registry-backed assertions when `GuiGate.Config.Flavor == "e2e"`; a ship run records an
informational pass because the ship application intentionally uses the real classic key.

### Cleanup

The test removes the run subtree after closing the application session, which closes the process.
It deletes its scoped data root and leaves the empty `HKCU\Software\BWC-E2E` parent for the shared
assembly cleanup. No test leaves a `BwcRegedit` process or writes outside the run subtree. The test
also exercises a manually seeded missing path and dismisses the resulting error dialog instead of
terminating the application.

## Name comparison, default value, and whitespace names (regression set)

### Names compare with the current culture

Failure mode: `FavoritesStore` compared names with `CurrentCultureIgnoreCase`. Culture collation
treats some different strings as equal, such as `ab` and `a` + soft hyphen + `b`, so a new favorite
was refused as a duplicate of an existing one that it does not match. Names now compare with
`OrdinalIgnoreCase`, the same rule the value list uses for duplicate names. Test:
`FavoritesFixes_SoftHyphenNameIsNotADuplicate`.

### A default value under the Favorites key

Failure mode: a value with an empty name under the Favorites key loaded as a favorite named empty, so
the menu showed an entry with no name and the list ordering and removal treated it as a favorite.
The default value is not a favorite and is ignored on load with a log entry. Test:
`FavoritesFixes_DefaultValueIsNotAFavorite`.

### A whitespace-only name can be added but never removed

Failure mode: the add prompt accepted a name made only of spaces. `Remove` skips whitespace names,
so the entry could be seen but never deleted. Add now refuses a whitespace-only name with the
same message as an empty name, so no such value is written. Test: `FavoritesFixes_WhitespaceNameIsRefused`.

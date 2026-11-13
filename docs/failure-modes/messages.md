# Registry Editor message failure modes

Scope: the message catalog in `src/Bwc.Regedit/Controllers/MessageCatalog.cs`, the message strings
in `src/Bwc.Regedit/Strings/en-US/Resources.resw`, the error and confirmation paths in
`src/Bwc.Regedit/Controllers/*` (`RegeditContext`, `KeyCommands`, `ValueCommands`,
`ImportExportCommands`, `HiveRemoteCommands`, `FavoritesController`, `PrintController`,
`ViewCommands`), and the Messages and Editing E2E suites. Every message is shown through
`ErrorDialog` or `ConfirmDialog`, which render through `ClassicMessageBox` in `src/Bwc.UI/Dialogs`.

The source of truth for the text is the oracle string table in
`docs/parity/oracle/regedit-resources.json` (`stringTables`, module `en-US/regedit.exe.mui`). The
resource key for each string is named after its role, not its id. Each `Msg*` entry in
`Resources.resw` has an XML comment with the oracle id, so `grep "Oracle <id>"` finds the key for
any id. The ids that are not in the catalog are listed under "Oracle ids" below.

## Rules for what a user sees

Every message shown by the Registry Editor follows four rules.

1. The text is a classic string or a plain sentence written for a state the classic dialogs do
   not cover. Format placeholders are .NET style (`{0}`), converted from `%1`.
2. The caption is the classic caption for the operation, for example `Error Deleting Key` or
   `Confirm Key Delete`. Operations without a classic caption use `Registry Editor`.
3. The text never contains the words `Operation`, `Path:`-style developer labels from the engine,
   `Win32`, `HRESULT`, a numeric error code, a stack trace, or an exception type name.
4. The developer detail (operation, path, Win32 code, exception message) goes to the log through
   `Log.Warn` or `Log.Exception` only.

`ErrorDialog.ForOperation` adds `Operation:`, `Path:`, and `Win32 error` lines. The controllers no
longer call it. Only `ErrorDialog.ForMessage(caption, text)` is used.

## Mapping from engine failures

The engine throws `RegistryOperationException` with an operation name and a Win32 code. The
controllers pass the action they were performing, and `MessageCatalog` picks the message from the
pair (action, Win32 class). The action comes from the call site, because the same engine
operation means different things: `SetValue` is a create when `ValueCommands` creates a value and
an edit when it changes one.

Win32 classes used by the mapping:

- Access denied (5) and insufficient privilege (1314): the classic "Insufficient privileges" or
  "Access is denied" text where the oracle has one, otherwise the generic text.
- Key not found (2, 3, and 1018 key deleted): the classic "selected branch does not exist" text
  (148), or the "key selected is invalid" text (125, 143) for import and export.
- Already exists (80, 183): the classic duplicate-name text for rename (68, 74).
- Anything else: the generic text for the action.

The action table is in `MessageCatalog` and in the mapping section of the spec below. Each action
has exactly one generic text and one caption, so an unknown code never reaches developer text.

| Action | Caption | Generic text (oracle id) | Specific texts |
| --- | --- | --- | --- |
| RenameKey | Error Renaming Key (64) | 66 | exists 68, empty 70, too long 67, backslash 69 (validation) |
| RenameValue | Error Renaming Value (72) | 73 | exists 74, empty 75 (validation) |
| DeleteKey | Error Deleting Key (80) | 82 | key not found 148 |
| DeleteValue | Error Deleting Values (88) | 89 | none |
| CreateKey | Error Creating Key (160) | 162 | access denied 164, open 161, no unique name 163 |
| CreateValue | Error Creating Value (168) | 169 | no unique name 170 |
| SetValue (edit) | Error Editing Value (112) | 115 | read 114 when the old value cannot be read |
| OpenKey (read key) | Error Opening Key (96) | 97, or 98 with the system text as Details | key not found 148 |
| ReadValue | Error Displaying Value (100) | 101 | none |
| ImportFile | Registry Editor | 132 | access denied 126, key not found 125; file errors: missing 235, open 129, not a registry file 133, other 231 |
| ExportFile | Registry Editor | 141 | access denied 142, key not found 143, open 138 |
| LoadHive | Load Hive (181) | 183 | access denied 189, privilege 182, in use 188 |
| UnloadHive | Unload Hive (186) | 185 | access denied 187, privilege 184 |
| Connect | Error Connecting Network Registry (152) | 154 | own computer 153, roots 155, permission 156 |
| Permissions | Registry Editor | 7052 | access denied 7050, deleted 7051 |
| Print | Registry Editor | 145 | memory 144, read 146 |
| FavoriteAdd | Error Adding Favorite (172) | 169 | duplicate name 171 |

Import partial failure (some writes done, a write refused) uses 132 when the error is not an
access or key error, because classic reports that as "Not all data was successfully written".

## Unknown and non-registry exceptions

- A `RegistryOperationException` that matches no specific row uses the generic text of its action.
- Any other exception from a command (for example `ArgumentException` from a path builder) shows
  the plain sentence `Msg.CommandFailed` under `Registry Editor`. That sentence is written for this
  product because the oracle has no generic command text. The exception is logged with its type
  and message.
- A print failure of any kind uses 145. Oracle 144 (insufficient memory to begin a print job) is
  not separated out, because the print result carries no memory code.

## Validation messages

Name validation runs before a write. Validation results have two shapes.

- Inline validation inside a text prompt (New Key, New Value, Rename Key, Rename Value) shows the
  text under the field and keeps the prompt open. Rename uses the classic texts (68, 74, 70, 75,
  69, 67). New Key and New Value use the existing `DialogNameExists` text because the oracle has no
  duplicate-name text for creating, and a classic create with an existing name was not verified.
- Inline rename from the tree or the list has no prompt. A failed check there shows a message box
  with the rename caption and the classic text, so the user sees `Error Renaming Value` with
  string 74.

Validation texts with no classic equivalent keep their existing BWC wording. They are listed under
"Strings without a classic equivalent" below.

## Unreadable keys

The oracle has two behaviors for a key that cannot be read (inventory BEH-036, contract
`docs/contracts/registry-engine.md` Errors):

- Opening the key (selecting it, or Refresh on it) shows `Error Opening Key` (96) with
  `{key} cannot be opened.` (97), or the 98 text with the system message in `Details:`. That is the
  behavior implemented for selection.
- A subkey that cannot be read still appears in the tree and expands to an empty branch. This is
  the choice implemented for expansion: the branch is empty and the failure is logged.

Choices made here:

- Selection of a key whose probe fails (`GetKeyInfo`, which opens the key and reads its counts)
  shows the 98 text with the system message as `Details`. The value list is empty. The probe runs
  before the value list is read, because `RegistryValueSource` catches its own failures and would
  hide them.
- Refresh on an unreadable key shows the same message.
- Expansion in the tree is owned by the tree-node code and stays silent. It is logged as a warning.
  This is a known difference from the oracle for branches the user expands.

## Confirmations

Every confirmation is a `ConfirmDialog`, which is a `ClassicMessageBox` with the question icon and
Yes and No buttons.

- Delete key: text 48 (the only key delete text in the oracle, used for every key, with or
  without subkeys), caption 49, buttons Yes and No. The BWC addition about system instability and
  subkeys is removed.
- Delete one value: text 52, caption 51.
- Delete several values: text 50, caption 51. The count of selected values decides between 50 and
  52.
- Unload hive: text 53, caption 54.

The default button of the delete-key confirmation was checked on the classic dialog. A probe key
`HKCU\Software\BWC-Oracle\Probe` was selected in classic regedit, Delete was pressed, and the
confirmation was read without changing it: the text is string 48, the buttons are Yes and No, the
window is titled `Confirm Key Delete`, and Yes has keyboard focus when the dialog opens, so Yes is
the default. The probe then pressed No, the key was kept, and `HKCU\Software\BWC-Oracle` was
removed. `ConfirmDialog` already defaults to Yes, so no change was needed. The Messages suite
asserts the default with `messages.deletekey.default`.

## Rename and delete errors inside the list and the tree

Inline rename commits a value or key name. The validation and the write happen after the commit,
so the message appears after the inline editor closes. A refused write leaves the registry
unchanged, and the list or tree keeps the old name.

## Write policy refusals in E2E

The E2E policy (`SandboxWritePolicy`) refuses a rename of the sandbox root itself, with access
denied, before any native call. The Messages suite uses that refusal to reach the access-denied
rename message. The run key is still the only key touched, and the test checks that it still
exists afterwards. No write reaches outside `HKCU\Software\BWC-E2E\<run-id>`.

## Strings without a classic equivalent

These texts stay in `Resources.resw` because the oracle has no matching string. They are shown
only in the cases below.

- `DialogNoSelection`, `DialogRootKeyLocked`, `DialogDefaultValueLocked`: a command that needs a
  selection or a locked root.
- `DialogNameExists`: a new key or value whose name already exists (inline).
- `DialogInvalidKeyName`, `DialogInvalidValueName`: a name that the registry rules refuse.
- `DialogFindSearchFailed`: the Find search could not run.
- `DialogFavorite*`, `DialogConnect*` (except the ones that map to the oracle), `DialogLoadHive*`,
  `DialogUnloadHive*` (except the captions): favorites and hive prompts.
- `DialogPermissionsFailed` and `DialogPrintFailed` keep developer text from earlier versions and
  are no longer used. `DialogDeleteKeyMessage`, `DialogDeleteKeyChildrenWarning`, and
  `DialogUnloadHiveMessage` are also no longer used, because the classic texts 48 and 53 replace
  them. All of these stay in the resource file because the rule is to keep existing keys.

## Oracle ids

Every oracle string id that the catalog uses has a key in `Resources.resw` with a comment naming
the id. The ids that are not in the catalog are listed in the report of the change, with the reason
for each group:

- Menu, accelerator, and status help texts (512 to 1281), owned by the menu code.
- Display and column text (8000 to 8192, 19, 20, 22, 27, 29, 30, 32, 33, 34, 124, 180, 300 to 305):
  not messages.
- Security editor and ACL strings (7001 to 7019, 7070 to 7092): the ACL editor is an OS component.
  Only the retrieve failures 7050 to 7052 are shown by this app.
- Restore and save subtree (55, 56, 212 to 215): no UI path in this app.
- Editor overflow and multi-string prompts (110, 111, 116, 117, 119, 120): shown by the value
  editors in `src/Bwc.UI`, outside this package.
- Success messages (128, 228): the app shows no success dialog for import today. Adding one changes
  the import flow and is a separate change.
- Policy and command line texts (40, 42, 43, 304): not reached in the window.
- Resource and memory messages (102, 118, 144, 306) and the file messages with no matching
  failure in this app (123, 134, 135): see the unmapped list.

## E2E coverage

The Messages suite (`tests/Bwc.E2E/Messages/MessageTests.cs`) covers:

- Delete key: text 48, caption 49, the Yes and No button labels, and the default button.
- Delete several values: text 50, caption 51.
- Rename a value to an existing name: caption 72 and text 74, with the value unchanged.
- New key under a key that exists: the inline validation text (`DialogNameExists`).
- Access denied on rename of the sandbox root: caption 64 and text 66, with the run key still
  present.
- Every message box text the suite sees: no `Operation`, `Win32`, `HRESULT`, or stack trace.

The Editing suite keeps its checks. Its delete-key text check now asserts the oracle text 48
instead of the removed BWC addition.

## Failure modes that stay open

- Expansion of an unreadable key does not show a message (tree code, out of scope here).
- The classic success message after import is not shown.
- The new-key duplicate case has no classic verification.

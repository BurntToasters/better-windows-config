# Find UI failure modes

Scope: `src/Bwc.UI/Dialogs/FindDialog.cs`, `src/Bwc.UI/Dialogs/FindProgressDialog.cs`, the Find
and Find Next commands in `src/Bwc.Regedit/Commands/RegeditMenu.cs`, the wiring in
`src/Bwc.Regedit/Controllers/FindController.cs`, and the persisted Find state in
`src/Bwc.Regedit/Registry/RegeditSettings.cs`.

The dialogs follow the oracle layout rows DLG-010 to DLG-012 (Find, RT_DIALOG 106) and DLG-017/018
(progress, RT_DIALOG 109). The engine contract is `docs/contracts/registry-engine.md` under `Find`;
the inventory rows are BEH-039 to BEH-042, DLG-010 to DLG-012, DLG-017/018, ACC-004, ACC-010,
BEH-003, OQ-10, and OQ-11.

## 1. Classic two-dialog sequence

The Find dialog has the template 106 geometry and controls: the find-what edit, Keys, Values, Data,
Match whole string only, Search in subtree, Find Next, and Cancel. It is built by `ClassicDialog`
from `ClassicDialogTemplates.Find`, so the controls sit at the template rectangles in template
order. Search in subtree is visible and checked by default, matching the oracle capture. There are
no BWC-only options.

Find Next validates the text and the look-at flags, accepts, and closes the Find dialog immediately.
The controller then opens `FindProgressDialog` (template 109, 170 x 64 DLU, Cancel is the default
button) and starts `RegistryFinder.Find` on the thread pool. The progress dialog closes when the
search completes. The hit is applied only after the progress dialog has closed. The Find dialog is
not reopened. Cancel and the close box cancel the engine token, close the progress dialog, restore
the owner, and leave the selection unchanged.

The first Find Next with no previous query opens the Find dialog. F3 and Edit > Find Next with a
previous query start the search directly and never show the Find dialog during the search.

## 2. Progress visibility and cancellation

The progress dialog is modal to the main window. Its only button is Cancel (id 2), which is also the
default button, so Enter, Escape, and the close box all take the cancel path. The host completes the
dialog only after the engine task finishes; the dialog itself never closes on a timer.

The progress dialog is held for a minimum time so the two-window sequence is not a single-frame
flash. In the ship build the floor is 250 ms. In the E2E flavor it is 2000 ms so UI Automation can
observe the dialog and a test can cancel it. The floor does not delay the engine: the search runs
while the dialog is visible and both the result and the floor must finish before the dialog closes.

A cancel during the floor, including after the engine has already produced a hit, suppresses the
hit. The controller checks the dialog's cancel state and the token before it applies anything. A late
result from a cancelled search can never overwrite a newer hit because only one modal search can be
in flight and the owner is disabled.

## 3. No wrap

The search continues after the last hit and never wraps. When `RegistryFinder.Find` returns null, the
controller records the query as exhausted and clears the last hit, then shows the classic message box
`Finished searching through the registry.` with the caption `Find` after the progress dialog has
closed. The selection stays where it was.

A second Find Next with the same exhausted query shows the same finished message and does not start
again from the current selection. The exhausted state is reset when the query text or look-at flags
change.

## 4. Selection start

A new search starts after the current selection, matching BEH-039. When the value list has at least
one selected row, the cursor is `RegistryFindCursor.FromValueSelection` with the selected key's
display path and the raw registry value name recovered from
`RegistryValueSource.RawNamesByName`. The raw name is required because the list shows `(Default)`
for the empty name. When no value is selected, the cursor is `RegistryFindCursor.FromKeySelection`
for the selected tree key. The virtual `Computer` node has no registry path, so a search started
there uses no cursor and begins at the configured roots.

A selection on a value whose data was already read is skipped completely, including that value's
name and data. A selection on a key lets the search see that key's values and descendants but not
its own name. This is the engine's documented interpretation of "after the current selection" and
remains provisional against the oracle.

## 5. Find Next cases

`FindNext` has three cases:

1. A previous query exists and a hit was recorded: continue with
   `RegistryFindCursor.FromHit` through `RegistryFinder.FindNext` semantics.
2. A previous query exists but no hit was recorded, because the last search ended at the finished
   message: show the finished message without searching again.
3. No previous query exists: open the Find dialog with the persisted text and flags. The user
   chooses the first search explicitly.

F3 while the Find dialog is open is handled by the dialog and is equivalent to pressing Find Next.
The owner is disabled by the modal, so the shell accelerator cannot fire twice.

## 6. Finished and error messages

`FindController` uses `ClassicMessageBox` with the caption `Find` for the finished text and for an
engine or no-scope failure. The finished text is exactly the oracle string
`Finished searching through the registry.` with the information icon. A failure uses the error icon
and the mapped resource text. Message boxes are shown after the progress dialog has closed, so the
owner is enabled and the message is owned by the main window.

The Find dialog itself uses a classic message box for its two validations and stays open: empty text
and all of Keys, Values, and Data unchecked. These are the only cases where Find Next does not close
the dialog.

## 7. Value data row mapping

A value-name hit and a value-data hit both resolve to the key and then to one list row. The row
display name comes from `RegistryValueDisplay.FormatName(hit.ValueName)`, so the empty registry
name maps to `(Default)` and a normal name maps to itself. After the hit navigates the tree, the
view loads the values for that key, sets the rows, and selects the matching row through the row
automation peer's `SelectionItem` pattern. If the row is missing because the value was deleted
between the hit and the load, the tree selection still moves to the key, the list shows the current
values, and the finished message is shown because the hit could not be presented. The recorded
hit remains the continuation cursor, matching the stale hit rules below, so a later Find Next does
not restart at the beginning.

## 8. Stale hits after key deletion

A hit is a snapshot. Between the hit and `RegistryTreeSource.FindPathAsync`, a key can be deleted,
renamed, or become unreadable. `FindPathAsync` returns null in all three cases. The controller logs
the path, leaves the current tree selection in place, and shows the finished message. It never
throws and never reports a selection that does not exist. The registry has no cross-key snapshot, so
a search can also return a stale value name; the list load simply omits it and the finished message
is shown.

## 9. Duplicate hits

The engine reports each position once. A value can produce a name hit and a later data hit, and a
key can produce a key hit and a later hit inside its values. `FindController` records the exact hit
returned by the engine and passes `RegistryFindCursor.FromHit` on the next request. It does not
deduplicate by key path, because two hits in the same key at different positions are both
legitimate stops.

## 10. Modal owner and focus

Both dialogs derive from `ModalDialogWindow`. The owner is disabled while either is open and restored
on close. The Find dialog starts in `FindText` with the text selected; the progress dialog starts on
Cancel. `RegeditRootView.ShowDialogAsync` refuses a second modal. If Edit > Find is chosen while
another modal is open, the request is logged and ignored.

When the progress dialog closes, the owner is activated before the hit is applied or the finished
message is shown. Tree and list updates from a hit use `SelectNode` and the row `SelectionItem`
pattern, not focus calls, so the main window keeps keyboard focus after a hit.

## 11. Keyboard accelerators and mnemonics

Ctrl+F opens Find and F3 repeats the last search. Both are handled by a routed `KeyDown` handler on
`RegeditRootView` with `handledEventsToo: true`, so a tree or list control that already handles a
different key does not hide the accelerator. The control state is read with
`InputKeyboardSource.GetKeyStateForCurrentThread`. The handler does not require focus to be in the
tree or list; the address bar also passes Ctrl+F and F3 to the same commands. A modal dialog is a
separate window, so its keystrokes never reach the main view handler.

The menu items Edit > Find (662) and Edit > Find Next (671) invoke the same two methods as the
accelerators. The Find dialog passes the template mnemonic letters to `ClassicMnemonicText`:
`Fi&nd what:`, `&Keys`, `&Values`, `&Data`, `Match &whole string only`, `Search in &subtree`, and
`&Find Next`. Cancel has no mnemonic, as the template shows. Enter presses Find Next, Escape presses
Cancel, and Tab follows the template order. The resource strings carry no ampersands, so
`FindController` marks the template letter when it builds the label object; the shared resource map
is not edited.

## 12. Persisted flags, text, and last hit

`RegeditSettings` stores:

- `FindText` and `FindFlags`: the last text and checkbox state shown in the dialog.
- `FindQueryText` and `FindQueryFlags`: the query that produced the last completed search.
- `FindLastHitKey`, `FindLastHitValue`, `FindLastHitKind`, and `FindLastHitText`: the last hit
  needed to rebuild a `RegistryFindHit` after a restart.

`FindFlags` is a bit mask of Keys, Values, Data, Match whole string only, and Search in subtree.
The defaults are Keys, Values, Data, and Search in subtree enabled, which is the engine default for
OQ-10. The text and flags are persisted when the Find dialog closes, including a draft that was
cancelled without a search. `CaptureSettings` copies the in-memory fields on window close and
`ApplySettings` restores them on startup. Find Next after a restart therefore continues after the
persisted hit when that key still exists, or falls back to the engine cursor rules and eventually
reports the finished message.

### The Applets\Regedit\FindFlags DWORD is not written

The oracle capture in `docs/parity/oracle/find-sequence.json` shows that Windows 11 build
26100.8457 does not update
`HKCU\Software\Microsoft\Windows\CurrentVersion\Applets\Regedit\FindFlags` when the classic Find
dialog is used: every captured run left the DWORD at `0x00000000`. The classic dialog mask is
recorded in the capture as `0x1` whole string, `0x2` keys, `0x4` values, `0x8` data, and `0x10`
subtree, but classic itself did not persist it.

BWC therefore does not write that DWORD. The dialog text and flags live only in BWC's own settings,
and the in-memory F3 query state is built from those settings. This avoids claiming a registry
side effect classic does not have and avoids guessing whether a future Windows build changes the
classic behavior. `RegistryFindFlagsCodec` remains available to the engine, but the Find UI does
not call it. This decision is recorded here because an earlier contract note said BWC writes the
DWORD; that note predates the capture evidence.

## 13. Access denied and missing keys

The engine skips unreadable keys and values without aborting the walk. The UI does not preflight
the roots and does not show an error for a skipped branch; it logs the engine's warning text
through `Bwc.Core.Log` and continues with the next sibling. If the selected start key was deleted
before the search begins, the finder cannot place the cursor in the parent's sorted child list,
falls back to a root walk, and never reaches the cursor position, so the request reports the
finished message. If the selected value was deleted, the cursor key is still valid and the search
continues with the next value. A failure to load the values for a hit key is logged and leaves an
empty list, never an exception to the window.

## 14. E2E roots, seed, and cleanup

The E2E test seeds `HKCU\Software\BWC-E2E\<runId>` with the keys `Alpha`, `Alphabet`, and `Beta`,
the values `AlphaValue`, `DataNeedle`, `MultiNeedle`, and `Dword42`, then launches Regedit with an
isolated `BWC_DATA_ROOT` and `BWC_E2E_SANDBOX_ROOT`.

When `BWC_E2E_SANDBOX_ROOT` parses to a registry path, `RegeditRootView` passes only that path as
`RegistryFindQuery.Roots`. The test never scans or depends on the rest of the machine registry, and
the no-hit search completes in bounded time. This scoping is a test affordance, not a ship
behavior: with the variable unset, the default five roots are used.

The test deletes the run subtree in a `finally` block, then removes the empty
`HKCU\Software\BWC-E2E` parent when possible. Navigation uses `ControlTestSupport.NavigateToSandboxKey`,
which expands the tree with the keyboard, so the test does not depend on the address bar.

`tests/Bwc.E2E/Find/FindTests.cs` drives the sequence with real input. It checks that Find stays
open on open and the owner is disabled, that Find Next closes Find and opens the progress dialog,
that F3 with a prior query shows progress without Find, that a hit selects the tree key and value
row, that Cancel suppresses the hit and restores the owner, that the finished message is the exact
oracle text with the caption `Find`, that a repeated exhausted Find Next does not wrap, and that
the text and flags survive a restart. Each dialog is found by a control id that only it has
(`FindText`, `FindProgressText`, `DialogMessage`), because Find and the progress dialog share the
caption `Find`. Keyboard input goes to the real Find HWND, and each keyboard or mouse action checks
that its window is foreground first. Screenshots are written under `artifacts/e2e/visual/find/`.

The E2E also snapshots the real `Applets\Regedit` values before and after the run and asserts that
the `FindFlags` DWORD is unchanged. The process is closed by the `AppSession` using block, and the
class cleanup is expected to leave no `BwcRegedit` process and no run key behind.

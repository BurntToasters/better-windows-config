# Find UI failure modes

Scope: `src/Bwc.UI/Dialogs/FindDialog.cs`, the Find and Find Next commands in
`src/Bwc.Regedit/Commands/RegeditMenu.cs`, and the search wiring in
`src/Bwc.Regedit/RegeditRootView.xaml.cs`. Persisted Find state lives in
`src/Bwc.Regedit/Registry/RegeditSettings.cs`.

The dialog follows the oracle layout rows DLG-010 to DLG-012 and DLG-017/018. The engine contract is
`docs/contracts/registry-engine.md` under `Find`; the inventory rows are BEH-039 to BEH-042,
DLG-010 to DLG-012, DLG-017/018, ACC-004, ACC-010, BEH-003, OQ-10, and OQ-11.

The dialog exposes `FindText`, `FindKeys`, `FindValues`, `FindData`, `FindWholeString`,
`FindSubtree`, `FindStatus`, `DialogPrimary`, and `DialogSecondary` automation ids. The primary
button is `Find Next` and never closes the dialog. The secondary button is `Cancel`, cancels the
running search, and closes. Enter invokes Find Next; Escape cancels and closes. The dialog reads no
hard-coded colors: the base dialog supplies the themed background and the validation brush comes
from `SystemFillColorCriticalBrush`.

## 1. Background search and cancellation

`RegeditRootView` builds a `RegistryFindQuery`, then runs `RegistryFinder.Find` inside
`Task.Run` with a `CancellationTokenSource` owned by the dialog request. The UI thread only
formats text and updates controls after the awaited task completes. A search never calls the
engine on the dispatcher thread.

Each Find Next request creates a new token source. Starting a second search cancels the first, so
a late result from the older search cannot overwrite a newer hit or status. When the dialog closes,
its `Closed` handler cancels the current token. The command catches `OperationCanceledException`
and leaves the selection, the list, and `LastKey` unchanged. A cancelled request is not an error and
does not show a dialog.

The Find Next button is disabled from the moment a request starts until the host calls
`CompleteSearch`, `ShowFinished`, or `ShowSearchError`. Cancel stays enabled while a search runs so
a scan of a large subtree can always be stopped. If the engine throws
`RegistryOperationException` because a root cannot be enumerated, the view logs a warning and
shows the failure in `FindStatus`; the application does not crash.

## 2. No wrap

The search continues after the last hit and never wraps. When `RegistryFinder.Find` returns null,
the dialog shows the oracle string `Finished searching through the registry.` and stays open. A
second Find Next with the same query reuses the same exhausted cursor and reports the same message
again instead of restarting at the first root. That avoids circular hit sequences when the only
match is the current position.

The dialog keeps the last hit after the completed search. Pressing Find Next after the finished
message does not silently start over. The user starts a new search by changing the text or the
look-at flags, which resets the exhausted state and starts from the current selection.

## 3. Selection start

A new search starts after the current selection, matching BEH-039. When the value list has at
least one selected row, the cursor is `RegistryFindCursor.FromValueSelection` with the selected
key's display path and the raw registry value name recovered from
`RegistryValueSource.RawNamesByName`. The raw name is required because the list shows `(Default)`
for the empty name. When no value is selected, the cursor is
`RegistryFindCursor.FromKeySelection` for the selected tree key. The virtual `Computer` node has
no registry path, so a search started there uses no cursor and begins at the configured roots.

A selection on a value whose data was already read is skipped completely, including that value's
name and data. A selection on a key lets the search see that key's values and descendants but not
its own name. This is the engine's documented interpretation of "after the current selection" and
remains provisional against the oracle.

## 4. Find Next with no prior search

`FindNext` has three cases:

1. A previous query exists and a hit was recorded: continue with
   `RegistryFindCursor.FromHit` through `RegistryFinder.FindNext` semantics.
2. A previous query exists but no hit was recorded, because the last search ended at the
   finished message: show the finished message without searching again.
3. No previous query exists: open the Find dialog with the persisted text and flags. The user
   chooses the first search explicitly. This avoids an invisible search that starts from an
   unrelated selection.

F3 while the Find dialog is open is treated as Find Next inside the dialog. The owner window is
disabled by the modal, so the shell accelerator cannot fire twice.

## 5. Status and progress

`FindStatus` shows three search states:

- `Searching...` after Find Next starts and before the task completes.
- `Found: <display text>` after a hit. The text is `RegistryFindHit.DisplayText`, the same
  key/value rendering the engine uses for logs.
- `Finished searching through the registry.` when no hit remains.

Validation text and an engine failure message use the same status block but replace these states
while the dialog stays open.

The status text is a plain `TextBlock` with `AutomationId="FindStatus"` so UIA and the E2E test can
read it. The dialog does not use the separate progress dialog (DLG-017/018). The contract allows
the progress dialog, but keeping the status in the Find window avoids a second modal owned window
and keeps Cancel in one place. This is a declared difference from the classic two-window sequence.
It is recorded here rather than as a silent omission.

## 6. Value data row mapping

A value-name hit and a value-data hit both resolve to the key and then to one list row. The row
display name comes from `RegistryValueDisplay.FormatName(hit.ValueName)`, so the empty registry
name maps to `(Default)` and a normal name maps to itself. After the hit navigates the tree, the
view loads the values for that key, sets the rows, and selects the matching row through the row
automation peer's `SelectionItem` pattern. If the row is missing because the value was deleted
between the hit and the load, the tree selection still moves to the key, the list shows the current
values, and the status still reports the hit. The next Find Next starts from the recorded hit
cursor; when the deleted value is no longer in the enumeration the cursor position is never
reached, so the search reports the finished message instead of repeating the old hit.

## 7. Stale hits after key deletion

A hit is a snapshot. Between the hit and `RegistryTreeSource.FindPathAsync`, a key can be deleted,
renamed, or become unreadable. `FindPathAsync` returns null in all three cases. The view logs the
path, leaves the current tree selection in place, keeps the recorded hit as the continuation
cursor, and shows the finished status. It never throws and never reports a selection that does not
exist. The registry has no cross-key snapshot, so a search can also return a stale value name;
the list load simply omits it as described above.

## 8. Duplicate hits

The engine reports each position once. A value can produce a name hit and a later data hit, and a
key can produce a key hit and a later hit inside its values. `RegeditRootView` records the exact
hit returned by the engine and passes `RegistryFindCursor.FromHit` on the next request. It does
not deduplicate by key path, because two hits in the same key at different positions are both
legitimate stops. The dialog does not compare display text to decide whether a hit is new.

## 9. Modal owner and focus

`FindDialog` derives from `ModalDialogWindow`. The owner window is disabled while the dialog is
open, the dialog is centered over the owner, and focus starts in `FindText` with the text
selected. The dialog is not closed by Find Next, so the E2E test can read the status and the tree
selection behind it. Tree and list updates from a hit are applied with `SelectNode` and the row
`SelectionItem` pattern, not with focus calls, so keyboard focus stays in the dialog.

`RegeditRootView.ShowDialogAsync` refuses a second modal. If Edit > Find is chosen while another
modal is open, the request is logged and ignored. Pressing Ctrl+F or F3 while the Find dialog is
open goes to the dialog window because the owner is disabled. Cancel restores the owner and sets
the active window back to the main window through the base dialog.

## 10. Keyboard accelerators

Ctrl+F opens Find and F3 repeats the last search. Both are handled by a routed `KeyDown` handler on
`RegeditRootView` with `handledEventsToo: true`, so a tree or list control that already handles a
different key does not hide the accelerator. The control state is read with
`InputKeyboardSource.GetKeyStateForCurrentThread`. The handler does not require focus to be in the
tree or list; the address bar also passes Ctrl+F and F3 to the same commands. A modal dialog is a
separate window, so its keystrokes never reach the main view handler.

The menu items Edit > Find (662) and Edit > Find Next (671) are enabled and invoke the same two
methods as the accelerators. `FindNext` with no previous query opens the dialog instead of
starting a search from the current selection, as described in section 4.

## 11. Persisted flags, text, and last hit

`RegeditSettings` stores:

- `FindText` and `FindFlags`: the last text and checkbox state shown in the dialog.
- `FindQueryText` and `FindQueryFlags`: the query that produced the last hit.
- `FindLastHitKey`, `FindLastHitValue`, `FindLastHitKind`, and `FindLastHitText`: the last hit
  needed to rebuild a `RegistryFindHit` after a restart.

`FindFlags` is a bit mask of Keys, Values, Data, Match whole string only, and Search in subtree.
The defaults are Keys, Values, Data, and Search in subtree enabled, which is the engine default for
OQ-10. `CaptureSettings` copies the in-memory fields on window close; `ApplySettings` restores
them on startup. `FindNext` after a restart therefore continues after the persisted hit when that
key still exists. If the hit key is gone, the engine falls back to its documented cursor
continuation rules and eventually reports the finished message. This is closer to classic BEH-003
than a text and flags only design, but the classic parent key and value encoding are not known, so
the shape is private to BWC.

The text and flags are also updated when the dialog closes without a search, so an edited draft is
restored next time. Find Next does not require the dialog to have been open: the persisted query is
enough.

## 12. Access denied and missing keys

The engine skips unreadable keys and values without aborting the walk. The UI does not preflight
the roots and does not show an error for a skipped branch; it logs the engine's warning text
through `Bwc.Core.Log` and continues with the next sibling. If the selected start key was deleted
before the search begins, the finder cannot place the cursor in the parent's sorted child list,
falls back to a root walk, and never reaches the cursor position, so the request reports the
finished message. If the selected value was deleted, the cursor key is still valid and the search
continues with the next value. A failure to load the values for a hit key is logged and leaves an
empty list, never an exception to the window.

## 13. E2E roots, seed, and cleanup

The E2E test seeds `HKCU\Software\BWC-E2E\<runId>` with the keys `Alpha`, `Alphabet`, and `Beta`,
the values `AlphaValue`, `DataNeedle`, `MultiNeedle`, and `Dword42`, then launches Regedit with an
isolated `BWC_DATA_ROOT` and `BWC_E2E_SANDBOX_ROOT`.

When `BWC_E2E_SANDBOX_ROOT` parses to a registry path, `RegeditRootView` passes only that path as
`RegistryFindQuery.Roots`. The test never scans or depends on the rest of the machine registry, and
the no-hit search completes in bounded time. This scoping is a test affordance, not a ship
behavior: with the variable unset, the default five roots are used.

The test deletes the run subtree in a `finally` block, then removes the empty
`HKCU\Software\BWC-E2E` parent when possible. Navigation uses `ControlTestSupport.NavigateToSandboxKey`,
which expands the tree with the keyboard, so the test does not depend on the address bar. UIA
exposes the owned Find content under the main window element and reports the main window handle for
that element, so the test reads controls through the main UIA root. Keyboard input for Enter and
Escape needs the real Find HWND, which the test finds by process id and the window title `Find`.
Sending keys to the owner would reach a disabled window and do nothing.

The E2E records each check through `E2EResults.Record` and asserts
`E2EResults.FailedChecksSince`. The process is closed by the `AppSession` using block, and the
class cleanup is expected to leave no `BwcRegedit` process and no run key behind.

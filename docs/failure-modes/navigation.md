# Registry navigation failure modes

Scope: `src/Bwc.Regedit/Registry/RegistryTreeSource.cs`, `RegistryValueSource.cs`,
`RegeditSettings.cs` and `RegeditSettingsContext`, the `RegeditRootView` wiring, the Regedit
`MainWindow` settings load and save, and the Controls and Navigation E2E tests under
`tests/Bwc.E2E`.

The view reads the live registry through `Bwc.Registry.RegistryReader`. Every read happens on
the thread pool and the result is applied on the UI thread. The tree and list never perform
registry I/O on the dispatcher thread.

## Last key missing or denied

`RegeditSettings.LastKey` is the display path of the selected key at the previous close, or
`Computer`. On startup `RegistryTreeSource.FindPathAsync` walks the path from the preloaded
`Computer` node, loading only the keys along that path. A key can be deleted, renamed, or become
unreadable between sessions. The walk then returns null. The view keeps the default `Computer`
selection and writes a warning to the log. It never throws from the restore path and never shows
an error dialog at startup.

## Path parse failure

`LastKey` is user-editable JSON. `RegistryPath.TryParse` rejects empty strings, unknown roots,
empty components, backslash-only segments, and components containing `\` or NUL. When parsing
fails, `FindPathAsync` logs the rejected value and returns null. A malformed `LastKey` therefore
falls back to `Computer`, exactly like a missing key. The settings file itself remains untouched
until the next close.

## HKEY_CLASSES_ROOT and other huge child lists

Child enumeration is lazy. `Computer` and the five roots cost no registry read. Expanding
`HKEY_CLASSES_ROOT` asks `RegistryReader.EnumerateSubKeyNames` for the complete child list on the
thread pool, sorts it, builds one `TreeNode` per name, and returns it. `DenseTree` then inserts
the visible rows in 500 row chunks over dispatcher turns, so a huge root does not block input or
allocate one UI element per key. The full child list is still materialized in memory because the
dense tree needs the model list for scroll extent. A key with hundreds of thousands of children
is slower to expand but does not freeze the window. Collapsing during the chunked insert cancels
the remaining chunks.

## Access denied

`RegistryReader` opens keys with `KEY_READ` and reports failures as
`RegistryOperationException`. Both sources catch that exception, log the display path and the
Win32 error with `Bwc.Core.Log.Warn`, and return an empty result. An unreadable key still appears
as a tree node (it has a chevron because `HasChildren` is true), can be selected, and shows an
empty value list. Expanding it again reuses the empty cached child list, so the UI does not retry
in a loop.

## Key deleted between enumeration and selection

The tree is a snapshot. Enumeration can list a name that another process deletes before the user
selects it. Selecting the stale node opens the key for value enumeration; the open fails with
`ERROR_FILE_NOT_FOUND` or `ERROR_ACCESS_DENIED`, so the value source logs the failure and shows no
values. The stale node is not removed automatically. A later refresh or relaunch rebuilds the
tree. This matches classic regedit, which also does not watch for changes.

## Stale async value loads

Each tree selection starts a value load with a monotonically increasing generation number and a
new `CancellationTokenSource`. A previous load is cancelled and its result is ignored. The result
is applied only when its generation still matches the current one, so a slow read for `HKCR` can
never overwrite the values of a key selected later. `SetRows` runs on the UI thread through
`DispatcherQueue.TryEnqueue`. The load itself never calls into XAML from a worker thread.

## Settings corruption

`SettingsStore<RegeditSettings>` uses the source-generated `RegeditSettingsContext`. A missing
file returns defaults. Invalid JSON, a null document, or an unsupported shape is moved aside to a
`.bad-<timestamp>` file beside the original and defaults are returned. A share or permission
failure surfaces as `IOException`; `MainWindow` logs it, keeps defaults, and continues. Saving is
not attempted on every selection. `AppWindow.Closing` captures the current key, address bar
visibility, and column widths, then writes the file atomically through a temporary file. A failed
save is logged and never blocks the close.

## Data root override

`AppPaths` honors `BWC_DATA_ROOT` only when it contains a fully qualified path. A relative or
malformed value is ignored in favor of the per-user local application data folder. Settings live
at `AppPaths.SettingsFile("regedit")`, so the E2E harness can point one process at
`artifacts/e2e/data/<runId>` without touching the developer's real settings. `AppSession` sets
`BWC_DATA_ROOT` from `E2EConfig.DataRoot` unless the caller supplies an override. Scoped
subdirectories keep the tree and list tests independent while the Navigation test deliberately
relaunches against the same directory to exercise LastKey restore.

## Tree source cancellation

Collapsing a node during enumeration cancels the expand token. `Task.Run` receives the token,
`RegistryReader` checks it per key, and `OperationCanceledException` propagates back to
`DenseTree`, which already catches it. The partially built child list is discarded because
`TreeNode` only installs children after the loader completes. `_childrenLoaded` stays false, so a
later expand can retry. Cancellation is not logged as an error.

## Virtual Computer node

`Computer` is a virtual root. It has no `RegistryPath`, is always expanded, and its five children
are added before `SetRoots`, so the tree shows them without a load event. Selecting `Computer`
shows an empty details list and sets the address bar to `Computer`. `FindPathAsync("Computer")`
returns the virtual node directly. No registry API is called for this node, so it cannot fail.

## Sorting and culture

Child key names are sorted with `RegistryNameOrder.Comparer`, which is `RegistryFindCollation.Comparer`: `CompareStringEx`
at the user default locale with `NORM_IGNORECASE`, the same comparison `lstrcmpiW` makes. Classic regedit lists the tree this
way, and Find uses the same comparer. The managed `StringComparer.CurrentCultureIgnoreCase` is not used, because it orders
hyphens and symbols differently from classic (VIEW-009, measured by the oracle capture `tree-order` in
`docs/parity/oracle/tree-order.json`). Sorting uses `OrderBy`, which is stable, so equal comparisons keep enumeration order.

### Failure modes of the sort order (VIEW-009, F2a)

- Failure mode: a hyphen or an underscore sorts as a plain symbol, so `-x` lands after `_x` and before `~x`, where classic
  lists `-x` after `é`. Guard: the native collation. E2E: `Navigation_TreeSiblingOrderMatchesClassic`.
- Failure mode: the tree and the Find results order the same names differently. Guard: one comparer for both.
- Failure mode: the comparer returns 0 for two names that differ. Guard: the native call returns 0 only for an error; the
  fallback is an ordinal ignore-case compare, so the sort stays total.
- Failure mode: a child list is sorted with one comparer and shown with another (the remote tree). Guard: `RemoteRegistryTreeSource`
  uses the same accessor as the local tree.

## E2E isolation

Each `test-e2e.js` invocation gets a timestamp and random run id. Registry data lives under
`HKCU\Software\BWC-E2E\<runId>`, settings live under `artifacts/e2e/data/<runId>`, and each control
test uses a scoped subdirectory so a previous test's LastKey cannot change its first selection.
Seeding and cleanup use `SandboxRegistry`, whose path validation rejects absolute paths, colons,
forward slashes, empty segments, `.` and `..`. Cleanup deletes the run subtree and removes the
`BWC-E2E` parent when it is empty, so no test key remains.

## Process cleanup

Every test disposes `AppSession`, which closes the main window, waits ten seconds, and kills the
process tree only as a fallback. The window close is the normal path and runs the settings save
before exit. The Navigation test closes the first process before launching the second so the
settings file is complete when the restore runs. The assembly GUI mutex is held for the whole
test assembly, so two E2E runs cannot race on the sandbox or on the settings directory.

## Deferred tree width

The shell has no public setter for the tree pane width; the width lives in a private field and is
changed only by the splitter. Persisting `TreeWidth` would require a new public
`ClassicWindowShell` API, so this batch persists only LastKey, address bar visibility, and list
column widths. The splitter continues to reset to the 216 DIP default on every launch.


## Out-of-band changes and F5 (BEH-007, F2a)

The tree and the value list are snapshots. Classic regedit does not watch the registry: a value added by another
program appears only after F5 (View > Refresh, accelerator 648). BWC keeps that contract, so the E2E check first
asserts that the new value is NOT listed, then presses F5 and asserts that it is listed.

### Value added outside the app appears without F5

Failure mode: the list updates on its own after a registry write, which is not classic behavior, and the absence check
before F5 cannot see the value. Guard: no registry watcher exists in the view. E2E: `Navigation_RefreshShowsValueAddedOutside`
asserts the row is absent before F5.

### F5 does not show the value

Failure mode: F5 re-reads the tree but not the list, or the list keeps the old snapshot. Guard: Refresh reloads the
selected key's values through the same reader as a fresh navigation. E2E: the row is present after F5.

### F5 drops the selection or the expansion

Failure mode: F5 selects the root, collapses the tree, or moves the caret to another key. Classic keeps the selected key.
Guard: Refresh keeps the selected key path and expanded nodes. E2E: the sandbox key is still selected after F5.

### Value added to another key shows in the sandbox list

Failure mode: the out-of-band write lands in a different key from the one listed, and a stale row is taken for a refresh.
Guard: the test writes only under the run root, and it checks the listed row by name in the sandbox key. Cleanup removes the
run key.

# Local Group Policy Editor scaffold failure modes

Scope: piece S1a of release 0.2.0. The new tool `gpedit` (display name "Local Group Policy Editor", exe
`BwcGpedit.exe`, assembly `BwcGpedit`, AUMID `BetterWindowsConfig.Gpedit`): the projects `src/Bwc.Gpo`,
`src/Bwc.Policy`, `src/Bwc.Gpedit.Editor`, `src/Bwc.Gpedit`, `src/Bwc.Gpedit.App`, the test projects
`tests/Bwc.Gpo.Tests` and `tests/Bwc.Policy.Tests`, the catalog row, the build and flavor scripts, the E2E harness
constants, and the two E2E tests `Gpedit_Smoke_WindowOpens` and `Hub_ListsBothToolsInCatalogOrder`. No feature code.
Installer, updater, signing and license scripts are piece S1b and are not covered here.

The mechanics reused from the Registry Editor are described in `build-scripts.md`, `e2e-harness.md` (sections 3, 31 and
43) and `hub.md`. This file lists only what a third head adds.

## Build and publish

### A third head breaks the shared publish folder

`scripts/dotnet-build.js` publishes every head into one folder and refuses two different files at one relative path.
Each head carries its own copy of the shared assemblies (`Bwc.UI.dll`, `Bwc.Core.dll`, WinAppSDK files), and a third
head adds a third copy that must match the first two byte for byte.

- `Bwc.Gpedit.App` is added to `PROJECTS` after `Bwc.Regedit.App` and before the NativeAOT `Bwc.Update`, so the helper
  stays last and the runtime-file check runs after every managed head.
- The libraries below the head (`Bwc.Gpo`, `Bwc.Policy`, `Bwc.Gpedit.Editor`, `Bwc.Gpedit`) are built once per publish
  per head. The collision check is the detector: a differing hash fails the build with both project names and the file.
- Acceptance: `dotnet-build.js` passes for win-arm64 e2e, win-arm64 ship and win-x64 e2e with three heads.

### Stale copy-local output from the new projects

Copy-local skips a file whose size and timestamp match. A `Bwc.Gpo.dll` or `Bwc.Policy.dll` left in
`src/Bwc.Gpedit.App/bin/<platform>/Release/<tfm>/<rid>` by a ship build could survive into an e2e publish and differ
from the copy in the other head (section 43 of `e2e-harness.md`). `dotnet-build.js` already deletes the rid-specific
copy-local folder and the R2R intermediates of every project in `PROJECTS`; adding the project to the list is the whole
mitigation. A project missing from the list would keep a stale folder and fail the collision check in some later run
only, so the list is the single place to add it.

### Root namespace and assembly name collide with an existing file

`BwcGpedit.dll` and `BwcGpedit.exe` must not share a name with a library assembly. The head's `AssemblyName` is
`BwcGpedit`; the library `Bwc.Gpedit` keeps the default assembly name `Bwc.Gpedit`, so the published folder holds both
`BwcGpedit.dll` (the head) and `Bwc.Gpedit.dll` (the library) as distinct files. The same applies to `BwcRegedit.dll`
beside `Bwc.Regedit.dll`.

### Engine projects reference the UI

`Bwc.Gpo` and `Bwc.Policy` run elevated and are reused by Local Security Policy in 0.3.0. A reference to WinUI,
`Bwc.UI` or the Windows App SDK would drag the UI stack into the engine and into its test projects.

- Both targets are plain `net10.0-windows` class libraries with no `UseWinUI` and no `Microsoft.WindowsAppSDK`.
- `Bwc.Policy` references `Bwc.Gpo` only. `Bwc.Gpo` references nothing.
- Detector: the project files are read by the quick gate's build; a WinUI reference would add `Bwc.UI.dll` to the
  engine output folder. The scaffold review checks that the `ProjectReference` and `PackageReference` lists contain
  neither.

### Test projects that cannot build or run

The two MSTest projects hold no test yet. The MSTest SDK treats a test project with no tests as a success, so an empty
project hides a broken reference. Each project keeps a `ProjectReference` to its engine project and an
`AssemblyInfo.cs` with `DoNotParallelize` as in `Bwc.Core.Tests`, and is listed in `BetterWindowsConfig.slnx` so the
full build compiles it. They are not added to `scripts/test-all.js` in this piece (the gate steps belong to piece G0).

### Solution file missing a project

A project absent from `BetterWindowsConfig.slnx` is not built by a solution build and not seen by the IDE. The six
new projects are added with both platform mappings (`x64`, `ARM64`) like their neighbours.

## Head, manifest and flavor

### Wrong manifest per flavor

The ship head must request `highestAvailable` and the e2e head `asInvoker`. The e2e head must never raise UAC when an
agent starts it. `Bwc.Gpedit.App.csproj` selects `app.e2e.manifest` under `'$(BwcFlavor)' == 'E2E'` exactly as the
Registry Editor head does, and `scripts/flavor-metadata.js` lists `BwcGpedit.exe` with its source manifest so the
post-publish check reads the manifest embedded in the exe and fails an e2e publish whose level is not `asInvoker`.

### Stale apphost and merged manifest after a flavor switch

The SDK does not rebuild the apphost or the merged manifest in `obj` when only the manifest selection changes, so an
e2e build could leave `asInvoker` in a ship exe (or the reverse). The head carries the same
`BwcDropFlavorManifestOutputs` target as the Registry Editor head: it stamps the flavor in
`obj/.../bwc-flavor.txt` and removes `Manifests` and `apphost.exe` when the stamp changes. The embedded manifest check
in `flavor-metadata.js` is the detector.

### Missing AUMID

A process without an explicit AppUserModelID is grouped by the shell under its exe path, so a pinned shortcut and the
running window split into two taskbar buttons. The head calls
`AppIdentity.SetCurrentProcessAppUserModelId(AppIdentity.GpeditAppUserModelId)` before the first window is created.
`GpeditAppUserModelId` is `BetterWindowsConfig.Gpedit`. The id is a constant in `Bwc.Core`, so the installer shortcut
(piece S1b) and the process use one value. A failed call is traced in ship and thrown in debug, as for the other
heads. After setting it, the head reads the id back with `GetCurrentProcessExplicitAppUserModelID` and logs
`AppUserModelID: <id>`; a set that did not take shows as `(none)` or another id. The smoke test reads that line from
the log under the run's data root (`gpedit.aumid`). See "AUMID is not readable" below for why the log is the evidence.

### AUMID is not readable from another process

A process-level AppUserModelID set with `SetCurrentProcessExplicitAppUserModelID` is not reported by the window
property store (`SHGetPropertyStoreForWindow`) to another process: the smoke run on win-arm64 found no id there (value
type 0). Reading the window store alone would either fail the test for no reason or, worse, be turned into a note that
proves nothing. The in-process read-back above is the check. The window store is still read, and an id found there must
match (`gpedit.aumid.window`); a missing id is a note with the reason, never a pass. The constant is also pinned
outside the GUI: `ToolCatalogTests` in `tests/Bwc.Core.Tests` pins the id, the catalog order, the display name and
exe name, and that the three AUMIDs and the exe names are distinct.

### E2E seams in the ship flavor

The ship head must not contain any E2E seam. At this stage the head has none (no `BWC_E2E_*` read). The window uses
only `BWC_DATA_ROOT`, which `AppPaths` already honors in both flavors for the per-run settings folder. Later pieces add
`GpeditStorePolicy` under `#if BWC_E2E`; the flavor check in `flavor-metadata.js` and the existing seam scan guard that.
The scaffold adds no new seam name.

### Single instance behavior

Regedit forwards a second launch to the first window through `SingleInstance` and a message that carries a .reg path.
A Policy Editor has no file argument and no activation payload in this piece. Taking the gate without a handler would
make a second launch exit silently with no window raised, which is worse than two windows. The scaffold therefore does
not take a single-instance gate: a second launch opens a second window. Single-instance activation (gate, message,
restore and foreground) is added together with the window state in a later piece.

### Window chrome and theme

The window is empty but must look like the other tools at the frame level: the Dense theme dictionary is merged in
`App.xaml` (without the Fluent `XamlControlsResources` the app crashes at startup with `0xC000027B`), the title is
"Local Group Policy Editor" from a resource, and the icon is `bwc-gpo.ico` loaded from the output folder. A missing
icon file leaves the default icon and does not stop startup, as in the Hub. The icon file is copied by a `Content` item
with `CopyToOutputDirectory`; a build that forgets it leaves the default icon. The icon file itself is verified by
`normalize-icons.js --check`, and the exe icon comes from `ApplicationIcon` at build time.
Persisted window placement is not part of this piece.

## Icon

### Missing sizes or a stale icon

`resources/bwc-gpo.ico` is generated from `resources/bwc-gpo.png` by `scripts/normalize-icons.js`. The `APP_ICONS` row
is added and the script run, so the `--check` run finds the file, every size of `ICO_SIZES` (256, 128, 96, 72, 64, 48,
40, 32, 24, 20, 16) and a timestamp not older than the source. A source that is not 1024 px on its longer side is
refused by the script and reported; the source is not edited. The icon and its source are both tracked.

## Hub and catalog

### Hub row order and name

The Hub renders `ToolCatalog.Tools` in order and keeps no second list, so a new row appears with no Hub change. The
order is the order of the catalog: `regedit`, then `gpedit`. A test that sorted by name would reorder them. The Hub
description for the new row is read from `Strings/en-US/Resources.resw` under the key `ToolGpeditDescription`, and the
catalog description is the fallback; both read "Browse and edit local group policy." The Hub test asserts that the
`ToolRow_*` rows of the list are exactly `ToolRow_regedit` then `ToolRow_gpedit` in that order, and that the gpedit
row shows the display name.

### Row for a tool that is not in the publish folder

The Status column shows "Not installed" when the exe is missing. A publish without `BwcGpedit.exe` therefore shows the
row with a wrong status rather than failing. The harness `EnsureExecutablesExist` includes `BwcGpedit.exe`, so the
tests fail with the publish command in the message, and the Hub test checks the gpedit row shows "Installed".

### Hub launches the wrong exe

`HubTools.ExePath` joins the base directory and `Tool.ExeName`. A typo in the catalog name (`BwcGPedit.exe`) would show
"Not installed" on a case-insensitive file system only when the file differs by more than case. The smoke test launches
the exe by the `E2EConfig` constant and the Hub test checks the status of the row, so the two names are checked against
the real file.

## E2E harness

### Harness missing the new exe

`E2EConfig` knows the Hub and the Registry Editor. Without a `GpeditExeName` and `GpeditExePath`, a test would hard code
the name and `EnsureExecutablesExist` would pass on a publish folder that lacks it. The constants are added, the
existence check names all three, and `FlavorMetadata` adds `BwcGpedit.dll` to the assemblies whose `BwcFlavor` value
must be `E2E` (`e2e-harness.md` section 31). `scripts/flavor-metadata.js` lists `BwcGpedit.dll` in
`REQUIRED_ASSEMBLIES` for the same reason: a folder without it is incomplete.

### The smoke test leaves a window running

The smoke test closes its session through `AppSession.Dispose`. A test that fails before disposing would leave a
`BwcGpedit` process holding the GUI lock window. The session is a `using` declaration and the test never kills a process
it did not start.

### Guard order

`GuiGate.Config` takes `Local\BwcGuiTestLock`. The gpedit smoke test and the Hub test use it as the other GUI tests do and
never start a process before the gate is held.

## Run and docs

`scripts/run-app.js` gets `gpedit` in its app table and help text, so `node scripts/run-app.js gpedit` launches the same
folder layout as the others. `npm run validate` must pass: no em dash in any new file, comment lint clean, and the
string checker unchanged (the regedit-only checker is not edited by this piece).

## Verification artifact

Evidence that the scaffold works:

- `artifacts/build/win-arm64-e2e.json`, `win-arm64.json` and `win-x64-e2e.json` show `pass: true`, three managed heads and
  a clean `flavorCheck`.
- `artifacts/e2e/result-win-arm64-e2e.json` holds the checks `gpedit.title`, `gpedit.aumid`, `hub.rows.order` and
  `hub.rows.gpedit.name`.

# Build setup

## Toolchain

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | 10.0.404 (pinned in `global.json`, `rollForward: latestFeature`) | `winget install Microsoft.DotNet.SDK.10` |
| Windows App SDK | 2.6.5 (NuGet `Microsoft.WindowsAppSDK`) | Restored from NuGet; nothing to install |
| Windows SDK projection | `net10.0-windows10.0.26100.0` | Restored from NuGet |
| Node.js | `^26.13.1` (`engines.node` in `package.json`) | Build and release scripts; CI reads the same field |
| NSIS | 3.10+ | Installer (not needed for app builds) |
| GnuPG | 2.4+ | Release signing only |

Visual Studio is not required. `dotnet build`, `dotnet publish`, and `dotnet test` work from a
plain terminal, including the XAML compiler.

Tests run on Microsoft.Testing.Platform. `global.json` opts in with
`"test": { "runner": "Microsoft.Testing.Platform" }`; without it the .NET 10 SDK refuses
`dotnet test` for MSTest.Sdk projects.

## WinUI app project settings

Every WinUI exe head uses these properties (verified in milestone M0 on Windows 11 ARM64):

```xml
<OutputType>WinExe</OutputType>
<TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
<TargetPlatformMinVersion>10.0.19041.0</TargetPlatformMinVersion>
<RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>
<Platforms>x64;ARM64</Platforms>
<UseWinUI>true</UseWinUI>
<WindowsPackageType>None</WindowsPackageType>
<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
<SelfContained>true</SelfContained>
<EnableMsixTooling>true</EnableMsixTooling>
<SatelliteResourceLanguages>en</SatelliteResourceLanguages>
```

`EnableMsixTooling` is required even though BWC is not packaged. Without it, `dotnet publish`
leaves out the app's `.pri` file and compiled XAML, and the published app crashes at startup
in `Microsoft.UI.Xaml.dll` with exception code `0xC000027B`. The plain `bin` build output is
not affected, which hides the problem until publish.

## Commands

Use the npm scripts and `scripts/` tools below. They publish, verify the flavor, and remove stale
ReadyToRun intermediates. The raw commands are for reference only:

```
dotnet publish -c Release -r win-x64   -p:Platform=x64   -p:PublishReadyToRun=true -o publish/win-x64
dotnet publish -c Release -r win-arm64 -p:Platform=ARM64 -p:PublishReadyToRun=true -o publish/win-arm64
```

x64 builds run under emulation on ARM64 machines, so both architectures can be smoke-tested
on an ARM64 development machine. Performance numbers are only valid on the native
architecture.

A minimal self-contained WinUI app published this way is about 280 MB (x64) and 305 MB
(ARM64) before the installer compresses it.

## Quality gate evidence

`node scripts/test-all.js` runs the gate steps in order and stops at the first failure.
`--rid win-x64|win-arm64|all` picks the target (default from `process.arch`).

The shared steps are `sync-version`, `registry-writer`, `no-em-dash`, `changelog-version`, and
`comment-lint`, then `build-arm64`, `build-x64`, and four test projects: `test-core`
(`Bwc.Core.Tests`), `test-registry-e2e` (`Bwc.Registry.Tests`), `test-registry-shipping`
(`Bwc.Registry.Shipping.Tests`), and `test-update` (`Bwc.Update.Tests`).

`--quick` runs the shared steps only. Without it, each RID then runs `publish-ship`,
`verify-flavor-ship`, `e2e-ship`, `publish-e2e`, `verify-flavor-e2e`, and `e2e-flavor`.
`--rid all` runs the shared steps once and the publish, flavor, and E2E steps once for each RID.

The four test projects always build and run for the host architecture (`process.arch`:
ARM64 or x64), whatever `--rid` is. `--rid` only picks the app RID for the publish and E2E
steps. On an ARM64 machine, `--rid win-x64` therefore runs the tests as ARM64 against the
x64 app, which runs under emulation. The x64 app is self-contained, so no x64 .NET runtime
is needed. Both app builds (`build-arm64` and `build-x64`) still compile in every run.

Each run removes the previous run's outputs first, then writes:

| Path | Contents |
|---|---|
| `artifacts/gate/result.json` | Gate result, schema 2 (fields below) |
| `artifacts/gate/logs/<NN>-<step>.log` | Full stdout and stderr of one step, with a header (step, RID, command, start time) and a footer (exit code, duration, pass) |

Schema 2 fields of `result.json`:

- `schema`, `startedUtc`, `durationMs`, `rid` (requested: `win-x64`, `win-arm64`, or `all`), `testPlatform` (the host architecture the four test projects ran as: `ARM64` or `x64`), `quick`, `pass`
- `git`: `commit` (full SHA), `branch`, `dirty`, `dirtyPaths` (from `git status --porcelain -z`; a rename reads `new (from old)`), `error`
- `environment`: `node` (version), `dotnet` (`version`, `error` from `dotnet --version`), `os` (`type`, `version`, `release`), `arch` (`process.arch`)
- `rids`: one entry per requested RID with `platform`, `status` (`pass`, `fail`, `not-run` after an earlier failure, or `skipped` in quick mode), and its step names
- `steps`: every step that ran, in order, with `name`, `rid` (null for shared steps), `command`, `pass`, `exitCode`, `durationMs`, `summary`, `log` (repo-relative path), and `e2eResult` (set on the two E2E steps of each RID, null otherwise)

`e2eResult` is `{ path, exists, pass, passed, failed, total, error }`. `path` is
`artifacts/e2e/result-<rid>-<flavor>.json`. Counts are read only when that file exists,
and the step exit code still decides pass or fail. The gate deletes that file before each
E2E step, so a result left by an earlier run cannot be counted.

### Flavor verification

Every assembly carries `BwcFlavor` from `Directory.Build.props`: `ship` in ship builds and
`E2E` in E2E builds. `scripts/flavor-metadata.js` checks the root of a publish folder:

- Every managed `Bwc*` and `BetterWindowsConfig*` `.dll` or `.exe` must carry exactly the
  folder's value: `publish/<rid>` expects `ship`, and `publish/<rid>-e2e` expects `E2E`.
  A missing value, a value from the other flavor, or two values fails the check.
- The three project main assemblies (`BetterWindowsConfig.dll`, `BwcRegedit.dll`,
  `BwcUpdate.dll`) must be present.
- Native apphost `.exe` files have no managed metadata and are listed as skipped.
- Each exe head's embedded manifest (`BwcRegedit.exe`, `BetterWindowsConfig.exe`,
  `BwcUpdate.exe`) must declare one `requestedExecutionLevel`. In `publish/<rid>-e2e` it must be
  `asInvoker`. In `publish/<rid>` it must be the level in that project's source manifest:
  `highestAvailable` (`src/Bwc.Regedit.App/app.manifest`), `asInvoker` (`src/Bwc.Hub/app.manifest`),
  and `asInvoker` (`src/Bwc.Update/app.manifest`). The check reads the manifest bytes
  in both ASCII and UTF-16LE. It ignores closing tags.

The check runs at the end of every `scripts/dotnet-build.js` publish, where it is recorded
as `flavorCheck` in `artifacts/build/<name>.json` (schema 2). The gate also runs it as the
steps `verify-flavor-ship` and `verify-flavor-e2e`. To check one folder by hand:

```
node scripts/flavor-metadata.js --rid win-arm64 --flavor e2e
```

### The two E2E flavors

- `ship` (`publish/<rid>`): real registry writes are allowed through `ShippingWritePolicy`. The
  E2E run covers only tests that are not tagged `Mutating`, `E2EOnly`, or `Elevated`.
- `e2e` (`publish/<rid>-e2e`): every write is limited by `SandboxWritePolicy` to
  `HKCU\Software\BWC-E2E\<run-id>`. The run covers every test except `Elevated`. Mutating tests run
  only here.
- Each mutating step first checks that the address bar is inside the sandbox root, and fails the
  test if it is not. Tests delete only the keys they created.
- `scripts/test-e2e.js` writes `artifacts/e2e/result-<rid>-<flavor>.json`. The file holds the checks,
  notes, and known gaps, and it records the commit and the run ID.

### E2E flavor never elevates

The E2E flavor must not raise UAC when an agent or developer starts an exe directly.
`src/Bwc.Regedit.App/Bwc.Regedit.App.csproj` and `src/Bwc.Update/Bwc.Update.csproj` select
`app.e2e.manifest` (asInvoker) instead of their ship manifest when `BwcFlavor` is `E2E`.
`src/Bwc.Hub` needs no override because its ship manifest is already asInvoker. The flavor
check above enforces these levels.

The apphost and merged manifest in `obj` are shared by both flavors, and the SDK does not
rebuild them when only the manifest selection changes. The two projects that select a
different manifest (Regedit and Update) therefore run `BwcDropFlavorManifestOutputs` before
`PrepareForBuild`. It records the last flavor in
`obj/.../bwc-flavor.txt` and deletes the `Manifests` folder and `apphost.exe` when the flavor
changes. Without it, the ship publish kept `asInvoker` after an E2E build.

Adding a new exe head or changing a manifest level: set the ship level in its source
manifest, and add an `app.e2e.manifest` with `asInvoker` if the level differs. Then add the
head to `EXE_HEADS` in `scripts/flavor-metadata.js`.

### Launching without a UAC prompt

Start a built exe with `scripts/launch-unelevated.js`, not by double-clicking or running it
directly from a shell:

```
node scripts/launch-unelevated.js publish\win-arm64-e2e\BwcRegedit.exe
node scripts/launch-unelevated.js --wait C:\Windows\regedit.exe
```

The helper sets `__COMPAT_LAYER=RunAsInvoker` on the child only, runs the file directly
(`shell: false`), and starts it detached. `--wait` waits and returns the program's exit code.
It refuses a path that is not an existing `.exe` file, and it refuses to run from an elevated
shell, because RunAsInvoker keeps the caller's token. It does not drop privileges from an
elevated caller. `scripts/run-app.js` also starts the app with RunAsInvoker, and its
`--elevated` option launches through `Start-Process -Verb RunAs`. That option prompts for
UAC, so use it by hand only.

### Elevated tests

Tests that need an administrator token are tagged `[TestCategory("Elevated")]`. The ship
filter, the default E2E filter, and the registry test steps of `scripts/test-all.js` all
exclude them. `node scripts/test-e2e.js --filter TestCategory=Elevated` runs only those tests.
It needs `--flavor e2e` and an elevated process. In a normal shell it exits 3 with NOT RUN,
and it exits 3 when no E2E test carries the tag. The runner that does this unattended is
described in [elevated-runner.md](elevated-runner.md).

## GUI tests and input

- GUI tests share the desktop. Hold the named mutex `Local\BwcGuiTestLock` around any GUI launch.
  `tests/Bwc.E2E/Harness/GuiGate.cs` takes it for E2E runs, and the oracle tools use the same name.
- FlaUI's held-key and held-button helpers (`Keyboard.Pressing`, `TypeSimultaneously`) do not produce
  real input sequences in this environment. Tests that hold a modifier or drag a mouse button use the
  `keybd_event` and `mouse_event` helpers instead, for example `tests/Bwc.E2E/List/RawKeyboard.cs`.
- UI Automation patterns are for reading state. Input paths are tested with real keys and clicks.

## App icons

Each app has a 1024x1024 PNG source in `resources/` and a generated `.ico` beside it.
The Hub uses `bwc.ico`. The Registry Editor uses `bwc-regedit.ico`, which its exe embeds and its
window loads. `npm run icons:normalize` rebuilds every `.ico` with ImageMagick (`magick` on PATH or
under `C:\Program Files\ImageMagick-*`; nothing is downloaded). Each `.ico` holds 256, 128, 96, 72,
64, 48, 40, 32, 24, 20, and 16 px images. `npm run icons:normalize -- --check` fails when an `.ico` is
missing a size or older than its source. Run the normalize command again, then commit the `.ico`.

## npm scripts

| Script | What it runs |
|---|---|
| `npm run build:win:x64`, `build:win:arm64` | `scripts/dotnet-build.js` for one RID (ship flavor) |
| `npm run build:win:e2e` | `scripts/dotnet-build.js` for arm64 in the E2E flavor |
| `npm run test:all`, `npm run gate` | `scripts/test-all.js` (the full gate) |
| `npm run test:e2e` | `scripts/test-e2e.js` |
| `npm run installer:x64`, `installer:arm64` | `scripts/build-installer.js` (needs NSIS 3.10 or newer) |
| `npm run run:hub`, `run:regedit` | `scripts/run-app.js` (starts the app with RunAsInvoker) |
| `npm run oracle` | `scripts/oracle.js` (regenerates the classic regedit captures) |
| `npm run sync-version` | `scripts/sync-version.js` |
| `npm run icons:normalize`, `-- --check` | `scripts/normalize-icons.js` (regenerates or verifies the app `.ico` files) |
| `npm run validate` | em dash check, changelog version check, comment lint |

`npm run oracle` and the oracle files are described in
[docs/parity/oracle/README.md](parity/oracle/README.md). The installer is described in
[installer.md](installer.md).

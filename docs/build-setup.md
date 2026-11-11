# Build setup

## Toolchain

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | 10.0.403 (pinned in `global.json`, `rollForward: latestFeature`) | `winget install Microsoft.DotNet.SDK.10` |
| Windows App SDK | 2.6.5 (NuGet `Microsoft.WindowsAppSDK`) | Restored from NuGet; nothing to install |
| Windows SDK projection | `net10.0-windows10.0.26100.0` | Restored from NuGet |
| Node.js | 22.22.2+, 24.15+, or 26+ | Build and release scripts |
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
`--rid win-x64|win-arm64|all` picks the target (default from `process.arch`), and
`--quick` runs only the builds and the three test projects. `--rid all` runs the shared
steps once and the publish, flavor, and E2E steps once for each RID.

The three test projects always build and run for the host architecture (`process.arch`:
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

- `schema`, `startedUtc`, `durationMs`, `rid` (requested: `win-x64`, `win-arm64`, or `all`), `testPlatform` (the host architecture the three test projects ran as: `ARM64` or `x64`), `quick`, `pass`
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

The check runs at the end of every `scripts/dotnet-build.js` publish, where it is recorded
as `flavorCheck` in `artifacts/build/<name>.json` (schema 2). The gate also runs it as the
steps `verify-flavor-ship` and `verify-flavor-e2e`.

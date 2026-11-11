# Installer

Better Windows Config ships as one per-user setup EXE per architecture. The EXE is built with
NSIS from the published app folder. The failure modes and the intended behavior of every step are
in `docs/failure-modes/installer.md`.

## Install NSIS (once)

NSIS 3.10 or newer is needed. It is not installed by this repository, and the build never
downloads it.

1. Install NSIS from https://nsis.sourceforge.io with the default options.
2. Rerun the build. The build finds `makensis.exe` in this order: the `NSIS_HOME` folder (if set),
   `PATH`, `%ProgramFiles(x86)%\NSIS`, and `%ProgramFiles%\NSIS`.

If none of these has it, the build prints the searched folders and exits with code `3`.

## Build an installer

```powershell
npm run build:win:x64          # optional: publish first (the installer also publishes when needed)
npm run installer:x64          # artifacts/installer/BetterWindowsConfig-<version>-win-x64-setup.exe
npm run installer:arm64        # artifacts/installer/BetterWindowsConfig-<version>-win-arm64-setup.exe
```

Options for `scripts/build-installer.js`:

| Option | Meaning |
| --- | --- |
| `--rid win-x64\|win-arm64` | Required. Selects the publish folder and the architecture check. |
| `--flavor ship\|e2e` | Default `ship`. `e2e` builds the test variant (see below). |
| `--version <semver>` | Default: `package.json` version. Accepts `X.Y.Z` or `X.Y.Z-beta.N`. |
| `--dry-run` | Prints the exact `makensis` command line and exits `0`. NSIS is not needed. |

A real run does these steps in order: it checks for NSIS, removes the previous result, reuses
`publish/<rid>` when the last publish passed or runs `scripts/dotnet-build.js`, generates the
notices, writes the payload include, runs `makensis`, and writes a JSON record next to the EXE.
The record holds the size, the SHA-256 of the EXE and of the notices, the payload file count,
and the NSIS version.

Exit codes: `0` built, `1` build or compile failure, `2` bad arguments, `3` NSIS not found or
older than 3.10.

To test the command without NSIS:

```powershell
node scripts/build-installer.js --rid win-arm64 --dry-run
```

## Third-party notices

`scripts/generate-nuget-licenses.js` reads `obj/project.assets.json` of `Bwc.Hub`,
`Bwc.Regedit.App`, and `Bwc.Update`. It keeps the NuGet packages that ship for the chosen RID
(runtime, native, resource, or runtime-target assets) and reads each license from the local NuGet
cache. Two shipped components are not library entries in the assets, so they are added explicitly:
the .NET runtime pack (version from `publish/<rid>/BetterWindowsConfig.runtimeconfig.json`) and
`Microsoft.WindowsAppSDK.Runtime` (version from the Windows App SDK packages). A RID whose publish
folder does not exist is skipped with a note when no `--rid` is given, and it is an error when one
is given. The output is `artifacts/notices/THIRD-PARTY-NOTICES.txt`, sorted and without timestamps,
so two runs produce identical bytes.

```powershell
node scripts/generate-nuget-licenses.js                  # packages for both RIDs
node scripts/generate-nuget-licenses.js --rid win-arm64  # packages for one RID
```

The installer copies this file into the install folder. The Hub's Help > About dialog opens it.
The generator makes no network calls. It needs the packages in the NuGet cache, so run a restore
or a build first.

## What the installer does

- Per user, into `%LOCALAPPDATA%\Programs\Better Windows Config`. The install does not need
  elevation. `/D=<dir>` changes the folder. Per-machine install is not implemented yet.
- Copies the whole published folder, plus `LICENSE`, `THIRD-PARTY-NOTICES.txt`, and a helper
  under `bwc-tools\`.
- Writes two Start menu shortcuts, each with the exe icon:
  - `Better Windows Config` runs `BetterWindowsConfig.exe`.
  - `Registry Editor (BWC)` runs `BwcRegedit.exe`.
  The shortcuts carry the AppUserModelIDs the apps set at startup (`BetterWindowsConfig.Hub` and
  `BetterWindowsConfig.Regedit`). Failing to write one is a logged warning, not an error.
- Registers an uninstall entry under
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\BetterWindowsConfig`, with
  `DisplayName`, `DisplayVersion`, `Publisher`, `DisplayIcon`, `InstallLocation`,
  `UninstallString`, `QuietUninstallString`, `EstimatedSize`, `NoModify`, and `NoRepair`. It also
  stores `ShortcutDir`, which the uninstaller reads.
- Refuses an ARM64 installer on a non-ARM64 PC, and any PC older than Windows 10 build 19041.
  An x64 installer runs on ARM64 under emulation.

Command line:

| Switch | Effect |
| --- | --- |
| `/S` | Silent install or uninstall. |
| `/D=<dir>` | Install folder. Must be last and unquoted (an NSIS rule). |
| `/RELAUNCH` | Start the Hub after a successful install. The updater does not pass it. |

Uninstall runs from **Apps > Installed apps** or `Uninstall.exe` in the install folder. It removes
what the install placed and leaves files the user added.

## Update in place

Running the new installer over an existing install is the update path.

1. The installer runs `bwc-close-apps.ps1`. It sends `WM_CLOSE` to every visible window of a BWC
   process whose image is inside the install folder, and waits up to 20 seconds.
2. If a process stays open, an interactive run offers Retry or Cancel. A silent run aborts with
   exit code `1` and changes nothing.
3. The installer deletes the files the previous version listed in `installed-files.txt`, then
   copies the new payload and writes a new manifest.
4. It rewrites the uninstall entry and the shortcuts.

The updater (`BwcUpdate.exe`, `docs/contracts/update-manifest.md`) starts this installer with
`/S /D=<install-dir>`. It waits for the apps to exit first, and it starts the Hub itself
afterward. It must not pass `/RELAUNCH`, or two Hubs would start.

## E2E variant

```powershell
node scripts/build-installer.js --rid win-arm64 --flavor e2e
```

The `e2e` flavor is compiled with `-DBWC_E2E`, and its output is named
`BetterWindowsConfig-<version>-<rid>-e2e-setup.exe`. It never writes to the real places:

- It requires `/RUNID=<id>` and `/SHORTCUTS=<dir>`, and aborts before changing anything without
  them. `<id>` must be a simple name.
- The uninstall entry goes to `HKCU\Software\BWC-E2E\<id>\Uninstall\BetterWindowsConfig`.
- The shortcuts go to `<dir>`.
- The uninstaller requires `/RUNID=<id>` to find the same entry.

Never publish the E2E installer. It is for tests only.

## Signing and what remains

The installer is not signed. Windows SmartScreen warns on an unsigned download. The release
signing key belongs to the user, and signing belongs to the release steps (D8).

Still to do:

1. Install NSIS 3.10+ and run the first compile of `installer/bwc.nsi`. The NSIS script, the
   PowerShell helpers, and the generated payload include were written without a compiler, so the
   first compile is part of acceptance.
2. Check that the Start menu AUMID write works on this machine (`bwc-set-aumid.ps1` reads the
   value back and exits `4` on mismatch).
3. Add the E2E install, update, and uninstall test. It should run the E2E installer with a unique
   `/RUNID=` and a temporary `/SHORTCUTS=` and `/D=` folder, then check the shortcuts, the
   uninstall key, the AUMIDs, and the uninstall. It should also produce a verifiable artifact.
4. Sign the EXE and the manifest with the release key (D8), and record the signatures in the
   manifest.
5. Decide whether to offer a per-machine install (it would need elevation and a different uninstall
   root).
6. Test the updater end to end against a real installer, including the case where an app stays
   open.

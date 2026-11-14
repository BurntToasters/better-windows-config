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
| `--dry-run` | Checks the NSIS source (includes and defines), prints the signing plan and the exact `makensis` command line, and exits `0`. NSIS is not needed. |

A real run does these steps in order: it checks for NSIS, removes the previous result, checks the
signing variables and the Artifact Signing Client Tools (unless `SKIP_WIN_CODESIGN=1`), reuses
`publish/<rid>` when the last publish passed or runs `scripts/dotnet-build.js`, requires
`BetterWindowsConfig.exe`, `BwcRegedit.exe`, and `BwcUpdate.exe` in the publish folder, signs the BWC
binaries in that folder, generates the notices, writes the payload include, checks the NSIS source,
runs `makensis` (which signs the setup EXE and the uninstaller), verifies every signed file, and writes
a JSON record next to the EXE. The record holds the size, the SHA-256 of the EXE and of the notices, the
payload file count, the NSIS version, and the list of signed files. Signing is described in
"Signing and what remains" below and in `docs/RELEASE.md`.

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
  elevation. `/D=<dir>` changes the folder. Per-machine install is not implemented yet. A reinstall
  with no `/D=` uses the folder recorded in `InstallLocation` of the uninstall entry.
- Checks the folder before any change (see "Install folder rules" below).
- Copies the whole published folder, plus `LICENSE`, `THIRD-PARTY-NOTICES.txt`, and the uninstaller.
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
- Refuses an ARM64 installer on a non-ARM64 PC, and any PC older than Windows 10 build 19041 (or
  one whose build number cannot be read).

Command line:

| Switch | Effect |
| --- | --- |
| `/S` | Silent install or uninstall. |
| `/D=<dir>` | Install folder. Must be last and unquoted (an NSIS rule). A drive or share root, a file, or a non-empty folder that is not a BWC install is refused (see "Install folder rules"). |
| `/RELAUNCH` | Start the Hub when the run ends. On success it starts the new Hub. On failure it starts the Hub that is still installed with `--update-result InstallerFailed`. Without this switch no Hub starts. The updater passes it. |
| `/PARENTPID=<n>` | The updater process that started this setup. The setup waits for it to exit, then never asks it to close. Digits only. The updater passes it. |

Uninstall runs from **Apps > Installed apps** or `Uninstall.exe` in the install folder. It removes
what the install placed and leaves files the user added.

## Install folder rules

The install folder is checked before any file changes. The same rules apply to `/D=` in silent mode
and to the folder typed on the directory page (interactive mode checks it when you press Next).

- Refused: a drive root (`C:\`, `D:`), a network share root (`\\server\share`), a file, and a folder
  that is not empty and has no `installed-files.txt` (it is not a BWC install). A trailing `\` is
  removed first. A refused folder is never changed, and a silent run exits with code `2`.
- Accepted: a folder that does not exist yet, an empty folder, and a folder with `installed-files.txt`
  from an earlier install of this product.
- `<install folder>.new` must also be absent, empty, or a leftover staging folder of this product (it
  has `installed-files.txt`). Any other folder with that name is refused, and it is never removed.
- A line in `installed-files.txt` that contains `..`, `/`, or `:`, or that starts with `\`, is never
  moved or restored. Such a line is skipped with a log line.

## Update in place

Running the new installer over an existing install is the update path. Nothing is removed until the
new version is complete on disk, and a failure puts the old version back.

1. **Stage.** The new files, the manifest (`installed-files.txt`), `LICENSE`,
   `THIRD-PARTY-NOTICES.txt`, and `Uninstall.exe` are written to `<install folder>.new`. The install
   folder is not touched in this step. A failure here removes the staging folder (only if this run
   confirmed it is ours). A leftover `<install folder>.old` without the upgrade marker (see below) stops
   the install before any change, because it may hold the only copy of the old files.
2. **Close.** When `BwcUpdate.exe` is in the install folder, the installer runs the copy of
   `BwcUpdate.exe` in the staging folder. That copy has its .NET runtime beside it, so it starts
   there. With `/PARENTPID=` first it runs `BwcUpdate.exe --wait-pid <PARENTPID> --timeout-seconds 30`,
   so the updater that launched the setup has exited before any file is replaced. Then it runs
   `BwcUpdate.exe --close-running --install-dir <folder> [--exclude-pid <PARENTPID>] --timeout-seconds 30`.
   That closes every BWC process that runs from the folder (`WM_CLOSE` to its windows) and waits up
   to 30 seconds. Exit `0` continues. Exit `3` (a process stays open or the updater still runs) offers
   Retry or Cancel in an interactive run, and a silent run aborts with exit code `2`. Any other result
   aborts. Nothing installed has changed yet. No PowerShell runs in this step, so a 32-bit installer
   reaches 64-bit and ARM64 processes.
3. **Swap.** The files that the previous manifest lists are moved to `<install folder>.old`, and the
   new files are moved in from `<install folder>.new`. A failed move puts every old file back, removes
   the new files that were moved in, and ends with "The upgrade was rolled back". If the restore
   itself fails, both folders are kept and the message names them.
4. **Finish.** The uninstall entry, shortcuts, and AppUserModelIDs are written. Then the finish step
   writes the marker file `<install folder>\upgrade-cleanup-pending`, removes `<install folder>.old` and
   `<install folder>.new`, and deletes the marker. If `.old` cannot be removed, the install still
   succeeds, a warning is logged, and the marker stays. The next run removes `.old` and the marker
   before it does anything else. The marker is not in `installed-files.txt`; the uninstaller deletes it.

With `/RELAUNCH`, the installer ends by starting the Hub: the new `BetterWindowsConfig.exe` after a
success, or the installed one with `--update-result InstallerFailed` after a failure (only if that
exe still exists). Without `/RELAUNCH` it starts nothing.

Disk space: the staging step needs about the payload size again while both versions exist (roughly
330 MB for the current build).

The updater (`BwcUpdate.exe`, `docs/contracts/update-manifest.md`) starts this installer with
`/S /RELAUNCH /PARENTPID=<its pid> /D=<install-dir>`. The installer waits for the updater to exit,
closes the apps, and starts the Hub itself. The updater does not start the Hub.

## Uninstall

The uninstaller closes running BWC processes with the `BwcUpdate.exe` in the install folder, with the
same exit-code rules. If that copy is missing, the uninstall stops and changes nothing.

It deletes the installed files listed at build time, the two shortcuts, `LICENSE`,
`THIRD-PARTY-NOTICES.txt`, `installed-files.txt`, `upgrade-cleanup-pending`, `Uninstall.exe`, and the
helper that version 0.1 left in `bwc-tools`. It removes folders only when they are empty, removes
`%TEMP%\BWC-Update` (the updater's staging folder; release flavor only), and removes the uninstall entry
last. The entry is removed only when its `InstallLocation` is the folder being uninstalled (compared
without case). An entry that names another folder is kept, and the log says why. The files are still
removed.

## E2E variant

```powershell
node scripts/build-installer.js --rid win-arm64 --flavor e2e
```

The `e2e` flavor is compiled with `-DBWC_E2E`, and its output is named
`BetterWindowsConfig-<version>-<rid>-e2e-setup.exe`. It never writes to the real places:

- It requires `/RUNID=<id>` and `/SHORTCUTS=<dir>`, and aborts before changing anything without
  them. `<id>` is 1 to 64 letters, digits, `-`, or `_`. When a switch is absent, the setup reads
  `BWC_E2E_INSTALLER_RUNID` or `BWC_E2E_INSTALLER_SHORTCUTS` from the environment instead, so an
  in-app update (the helper starts the setup without the switches) can use the same run. A switch
  on the command line always wins. The release build never reads these variables.
- The uninstall entry goes to `HKCU\Software\BWC-E2E\<id>\Uninstall\BetterWindowsConfig`, and the
  shortcuts go to `<dir>`.
- The uninstaller requires `/RUNID=<id>` to find the same entry, and applies the same `<id>` rule.
- It does not read the real uninstall key to choose a folder, so an E2E run without `/D=` cannot
  reach a real install.
- It does not remove `%TEMP%\BWC-Update`, which belongs to the real updater.

Never publish the E2E installer. It is for tests only.

## Signing and what remains

The installer is Authenticode-signed when signing is enforced (the default). The BWC binaries, the setup
EXE, and the uninstaller are signed with Azure Artifact Signing, and every one is verified after the
build. Without signing, Windows SmartScreen warns on the download. The one-time client tools setup and
the variables are in `docs/RELEASE.md`, "Windows code signing (Azure Artifact Signing)".

`SKIP_WIN_CODESIGN=1` (exactly) builds an unsigned installer for testing. A build for release must not
set it. The record then shows `signed: false`.

Status and what remains. NSIS 3.13 is installed on this machine. The E2E flavor compiles, and an
unsigned E2E run passed on Windows 11 ARM64: a fresh install, an install over the top, an install
after a leftover backup with the marker, the refusal of a leftover backup without the marker, and a
silent uninstall. The steps below are still open.

1. Compile the signed flavor (`BWC_SIGN` defined). Only the unsigned build (`SKIP_WIN_CODESIGN=1`)
   has been compiled, so the `!finalize` and `!uninstfinalize` lines of the signing hook are not
   yet checked by NSIS (see "Authenticode signing" in `docs/failure-modes/installer.md`).
2. Check that the Start menu AUMID write works on this machine. `bwc-set-aumid.ps1` reads the value
   back and exits `4` on mismatch, and it also exits `4` under Constrained Language Mode, which blocks
   the `Add-Type` it uses. Either case only logs a warning.
3. Test the upgrade rollback on a disposable machine: make one file in the new payload unwritable
   during the swap and confirm that the previous version runs again.
4. Turn the manual E2E run into a repeatable test that writes a verifiable artifact. It should run the
   E2E installer with a unique `/RUNID=` and a temporary `/SHORTCUTS=` and `/D=` folder, then check the
   shortcuts, the uninstall key, the AUMIDs, and the uninstall. The relaunch paths (`/RELAUNCH` on success
   and on failure) are not yet exercised, because they start a Hub.
5. Run the Authenticode signing once on the release VM (`npm run setup:win:artifact-signing`, as
   Administrator), then build a signed installer and confirm the verify step passes. The minisign
   signatures of the manifest and the installer are separate (D8, `docs/RELEASE.md`).
6. Decide whether to offer a per-machine install (it would need elevation and a different uninstall
   root).
7. Test the updater end to end against a real installer, including the case where an app stays
   open.

# Installer

Better Windows Config ships as one per-machine setup EXE per architecture (since 0.1.2; 0.1.1 and
earlier were per user). The EXE is built with NSIS from the published app folder and asks for
administrator rights. The failure modes and the intended behavior of every step are in
`docs/failure-modes/installer.md`, and the section "Per-machine install and migration (0.1.2)" there
lists every case of the per-machine layout.

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
runs `makensis` (which signs the setup EXE and the uninstaller), audits the manifest level of the built
EXE (below), verifies every signed file, and writes a JSON record next to the EXE. The record holds the
size, the SHA-256 of the EXE and of the notices, the payload file count, the NSIS version, the manifest
level (`manifestLevel`), and the list of signed files. Signing is described in "Signing and what remains"
below and in `docs/RELEASE.md`.

**Execution level by flavor.** `installer/bwc.nsi` sets `RequestExecutionLevel admin` for the ship flavor
and `user` for the E2E flavor (`BWC_E2E`), so no test or dev build can show a UAC prompt. After
`makensis`, the build reads the embedded manifest of the setup EXE (`checkSetupManifestLevel` in
`scripts/flavor-metadata.js`, which uses `readRequestedLevels`) and fails when ship is not
`requireAdministrator` or E2E is not `asInvoker`. The failed EXE is removed. `--dry-run` also checks that
`bwc.nsi` picks the level with that rule. The E2E check helper `scripts/test-installer-e2e.js` applies the
same audit to its second installer.

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

- Per machine. The setup asks for administrator rights (the manifest says `requireAdministrator`, so
  Windows shows the consent prompt after the user starts it). Standard users need an administrator to
  install and to update. It installs into `%ProgramFiles%\Better Windows Config` (the 64-bit Program
  Files folder) for both architectures. Every HKLM access uses the 64-bit registry view
  (`SetRegView 64`).
- The folder is not a choice, and there is no directory page. `InstallDirRegKey` is gone. In
  `.onInit` the setup reads HKLM `InstallLocation`: when it is a plain absolute path (`X:\...`, no
  `..`, no `/`, no quote) strictly inside `%ProgramFiles%`, that is the folder (so a reinstall lands
  where the last one did); when it is missing, invalid, or outside Program Files, the default folder
  is used. `/D=` is never the target. A `/D=` that differs from the target is only a legacy hint: when it
  names a Better Windows Config install (`installed-files.txt` and `BetterWindowsConfig.exe`), the
  setup closes the apps that run from it (see "Update in place") and changes nothing there. Any other
  value is ignored and logged. A custom folder is not supported in 0.1.2.
- Checks the folder before any change (see "Install folder rules" below), and refuses to install over
  a newer version recorded in HKLM `DisplayVersion` (semantic order, `0.1.2` newer than `0.1.2-beta.3`).
  The same version is a repair and installs again. A recorded version that is not `X.Y.Z` or
  `X.Y.Z-beta.N` never blocks.
- Copies the whole published folder, plus `LICENSE`, `THIRD-PARTY-NOTICES.txt`, and the uninstaller.
- Writes two all-users Start menu shortcuts (`SetShellVarContext all`), each with the exe icon:
  - `Better Windows Config` runs `BetterWindowsConfig.exe`.
  - `Registry Editor (BWC)` runs `BwcRegedit.exe`.
  The shortcuts carry the AppUserModelIDs the apps set at startup (`BetterWindowsConfig.Hub` and
  `BetterWindowsConfig.Regedit`). Failing to write the AppUserModelID is a logged warning. Failing to
  write a shortcut is an error: the install rolls back. The AppUserModelID script is written into
  `<install folder>.new` (under Program Files) and run from there, never from `%TEMP%`, because the
  setup is elevated.
- Registers an uninstall entry under
  `HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\BetterWindowsConfig` (64-bit view), with
  `DisplayName`, `DisplayVersion`, `Publisher`, `DisplayIcon`, `InstallLocation`,
  `UninstallString`, `QuietUninstallString`, `EstimatedSize`, `NoModify`, and `NoRepair`. There is no
  `ShortcutDir` in ship: the uninstaller knows the all-users folder. `UninstallString` is
  `"<install folder>\Uninstall.exe" _?=<install folder>` and `QuietUninstallString` adds `/S`, so the uninstaller runs in
  place from the admin-only install folder and not from a copy in `%TEMP%`. A uninstaller that runs in place cannot delete
  itself or its folder, so it asks Windows to remove them at the next restart (`/REBOOTOK`) when it cannot do so itself.
  The NSIS plugins folder of any setup or uninstaller still unpacks into `%TEMP%` (open risk, installer.md M39).
- Refuses an ARM64 installer on a non-ARM64 PC, and any PC older than Windows 10 build 19041 (or
  one whose build number cannot be read). It also refuses a token that is not elevated (for example
  `__COMPAT_LAYER=RunAsInvoker`), before any change.
- Never starts the Hub itself. After an install the Hub is started through
  `"<install folder>\BwcUpdate.exe" --launch-hub` (see "Update in place"), which starts it at the
  shell's integrity level, so an elevated setup never leaves an elevated Hub.

Command line:

| Switch | Effect |
| --- | --- |
| `/S` | Silent install or uninstall. |
| `/D=<dir>` | Never the install folder in ship. A legacy hint only (see above). Must be last and unquoted (an NSIS rule). A drive or share root is still refused. |
| `/RELAUNCH` | Start the Hub when the run ends, through `BwcUpdate.exe --launch-hub`. On success it starts the new Hub. On failure it starts the Hub that is still installed with `--launch-hub --update-result InstallerFailed`. Without this switch no Hub starts. The updater passes it. |
| `/PARENTPID=<n>` | The updater process that started this setup (the elevated helper). The setup waits for it to exit, then never asks it to close. Digits only. The updater passes it. |

Uninstall runs from **Apps > Installed apps** or `Uninstall.exe` in the install folder. It asks for
administrator rights too, removes what the install placed, and leaves files the user added.

Coming from 0.1.1 (a per-user install): 0.1.1 does not apply 0.1.2 in the app. The user downloads this
setup and starts it. The setup does not look at the per-user install (it never reads HKCU, because an
over-the-shoulder administrator has another profile). The Hub removes the old per-user copy at its
next start (`docs/failure-modes/hub.md`). User settings and data stay: they live in the data root, not in
the install folder. Windows may show two entries until then.

## Install folder rules

The install folder is checked before any file changes.

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
2. **Close.** The installer runs the copy of `BwcUpdate.exe` in the staging folder. That copy has its
   .NET runtime beside it, so it starts there. With `/PARENTPID=` first it runs
   `BwcUpdate.exe --wait-pid <PARENTPID> --timeout-seconds 30`, so the updater that launched the setup
   has exited before any file is replaced. When `BwcUpdate.exe` is already in the install folder (an
   earlier version), it then runs
   `BwcUpdate.exe --close-running --install-dir <folder> [--exclude-pid <PARENTPID>] --timeout-seconds 30`.
   That closes every BWC process that runs from the folder (`WM_CLOSE` to its windows) and waits up
   to 30 seconds. The step runs elevated, so it reaches an elevated Registry Editor too. When `/D=` named
   an earlier per-user install, the same command runs once more with `--install-dir <that folder>`, also on
   a first install; nothing there is removed. Exit `0` continues. Exit `3` (a process stays open or the
   updater still runs) offers Retry or Cancel in an interactive run, and a silent run aborts with exit
   code `2`. Exit `4` (`RunningInOtherSession`: a BWC process of the folder runs in another Windows
   session) aborts at once, in an interactive run with a message that names the reason, in a silent run
   with exit code `4`; the helper's output lines above it name the session. Any other result aborts.
   Nothing installed has changed yet. No PowerShell runs in this step, so a 32-bit installer reaches
   64-bit and ARM64 processes.
3. **Swap.** The files that the previous manifest lists are moved to `<install folder>.old`, and the
   new files are moved in from `<install folder>.new`. A failed move puts every old file back, removes
   the new files that were moved in, and ends with "The upgrade was rolled back". If the restore
   itself fails, both folders are kept and the message names them.
4. **Finish.** The all-users shortcuts, the uninstall entry, and AppUserModelIDs are written (a shortcut or
   entry that cannot be written rolls the install back as a whole; a first install then leaves neither).
   Then the finish step
   writes the marker file `<install folder>\upgrade-cleanup-pending`, removes `<install folder>.old` and
   `<install folder>.new`, and deletes the marker. If `.old` cannot be removed, the install still
   succeeds, a warning is logged, and the marker stays. The next run removes `.old` and the marker
   before it does anything else. The marker is not in `installed-files.txt`; the uninstaller deletes it.

With `/RELAUNCH`, the installer ends by starting the Hub through
`"<install folder>\BwcUpdate.exe" --launch-hub` (the helper starts `BetterWindowsConfig.exe` beside it at the
shell's integrity level; see `docs/contracts/update-cli.md`): the new Hub after a success, or the installed one
with `--launch-hub --update-result InstallerFailed` after a failure (only if that exe and the Hub still exist).
The setup never starts the Hub itself, so a setup that runs elevated never leaves an elevated Hub. When the
setup is not elevated (the E2E flavor), the same call is a normal start. The helper's exit code is not read: a
failed launch is not an installer failure. The finish page option "Start Better Windows Config" makes the
same call. Without `/RELAUNCH` it starts nothing. After the swap the setup's working directory is `%WINDIR%`,
so the helpers it starts (`--close-running`, `--launch-hub`) never inherit a folder that a standard user can
write.

Before step 0 a per-machine run removes what a crashed update left in `<install folder>.upd`, the protected
folder of the elevated updater (`<install folder>.upd\<32 hex>\installer.exe`): only direct subfolders named with
32 hex digits, only folders that hold plain files, never through a link or junction, and never the folder
the setup itself runs from (an update runs the installer from there).

Disk space: the staging step needs about the payload size again while both versions exist (roughly
330 MB for the current build).

The updater (`BwcUpdate.exe`, `docs/contracts/update-manifest.md`, `docs/contracts/update-cli.md`) starts this
installer through its elevated step with `/S /RELAUNCH /PARENTPID=<elevated helper pid> /D=<install-dir>`, from
`<install folder>.upd\<32 hex>\installer.exe`. The installer waits for that helper to exit, closes the apps, and
starts the Hub through the helper. The updater does not start the Hub.

## Uninstall

The uninstaller closes running BWC processes with the `BwcUpdate.exe` in the install folder, with the
same exit-code rules. If that copy is missing, the uninstall stops and changes nothing.

It deletes the installed files listed at build time, the two all-users shortcuts, `LICENSE`,
`THIRD-PARTY-NOTICES.txt`, `installed-files.txt`, `upgrade-cleanup-pending`, `Uninstall.exe`, and the
helper that version 0.1 left in `bwc-tools`. It removes folders only when they are empty. It removes the
protected folder of the elevated updater, `<install folder>.upd`: only the `<32 hex>` folders in it, never
through a link or junction, then the folder itself when it is empty. It no longer removes `%TEMP%\BWC-Update`
(an elevated recursive delete in a folder that a standard user writes; the Hub prunes its own staging), and it
never touches a user's data folder (`%LOCALAPPDATA%\BetterWindowsConfig`). The uninstall entry is removed last.
The entry is removed only when its `InstallLocation` is the folder being uninstalled (compared without case).
An entry that names another folder is kept, and the log says why. The files are still removed. A per-user
shortcut of an older per-user install, or its HKCU entry, is not ours to remove here.

## E2E variant

```powershell
node scripts/build-installer.js --rid win-arm64 --flavor e2e
```

The `e2e` flavor is compiled with `-DBWC_E2E`, and its output is named
`BetterWindowsConfig-<version>-<rid>-e2e-setup.exe`. It requests `asInvoker`, never elevation, so no test
or dev build can show a prompt (the build audit checks this). It never writes to the real places:

- It requires `/RUNID=<id>` and `/SHORTCUTS=<dir>` (the latter not with `/MACHINE`), and aborts before
  changing anything without them. `<id>` is 1 to 64 letters, digits, `-`, or `_`. When a switch is absent,
  the setup reads `BWC_E2E_INSTALLER_RUNID` or `BWC_E2E_INSTALLER_SHORTCUTS` from the environment instead,
  so an in-app update (the helper starts the setup without the switches) can use the same run. A switch
  on the command line always wins. The release build never reads these variables.
- The default (per-user sandbox, the layout of 0.1.1) puts the uninstall entry in
  `HKCU\Software\BWC-E2E\<id>\Uninstall\BetterWindowsConfig`, the shortcuts in `<dir>`, and the files in `/D=`.
  It writes `ShortcutDir` into the entry (E2E only), and removes the empty `Uninstall` and `<id>` keys on
  uninstall. Refusing to install over a newer version works here too, against this sandbox entry.
- `/MACHINE` is the per-machine layout in a sandbox, for the elevated runner tests (I4): files in
  `%ProgramFiles%\BWC-E2E\<id>`, entry in `HKLM\Software\BWC-E2E\<id>\Uninstall\BetterWindowsConfig` (64-bit
  view), shortcuts in the all-users Start menu folder `BWC-E2E-<id>`, `<install folder>.upd` as in ship.
  `/D=` is a legacy hint, as in ship. It needs a token that is already elevated and **never asks for
  elevation**: without one, `.onInit` aborts with a non-zero exit before anything is written. The uninstaller
  takes `/MACHINE` too, with the same rule. Only the elevated runner runs it to the end.
- The uninstaller requires `/RUNID=<id>` to find the same entry, and applies the same `<id>` rule.
- It does not read the real uninstall key to choose a folder, so an E2E run without `/D=` cannot
  reach a real install.

Never publish the E2E installer. It is for tests only.

### What the unelevated E2E covers, and what it cannot

`node scripts/test-installer-e2e.js --rid <rid>` (unelevated) builds E2E installers A and B and runs, in the
per-user sandbox: the in-app update from A to B through the Hub (the protected copy of the installer is placed
under the run's temp folder with `BWC_E2E_UPDATE_PROTECTED_ROOT`, and the relaunch goes through
`BwcUpdate.exe --launch-hub`), the same with Registry Editor open, an uninstall, the refusal of an older setup over
a newer one and the repair by the same version, and the refusal of `/MACHINE` without an elevated token
(nothing written). It cannot run the per-machine paths: Program Files and HKLM writes, all-users shortcuts, the
64-bit registry view, `<install folder>.upd` cleanup, the elevated close step, the de-elevated Hub start, and the
elevated uninstall. Those are proved by the `Elevated` tests of the elevated runner (I4).

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
2. Check that the Start menu AUMID write works on this machine. `BwcUpdate.exe --set-aumid` reads the value
   back and exits `4` on a mismatch or a COM failure. Either case only logs a warning.
3. Test the upgrade rollback on a disposable machine: make one file in the new payload unwritable
   during the swap and confirm that the previous version runs again.
4. Turn the manual E2E run into a repeatable test that writes a verifiable artifact. It should run the
   E2E installer with a unique `/RUNID=` and a temporary `/SHORTCUTS=` and `/D=` folder, then check the
   shortcuts, the uninstall key, the AUMIDs, and the uninstall. The relaunch paths (`/RELAUNCH` on success
   and on failure) are not yet exercised, because they start a Hub.
5. Run the Authenticode signing once on the release VM (`npm run setup:win:artifact-signing`, as
   Administrator), then build a signed installer and confirm the verify step passes. The minisign
   signatures of the manifest and the installer are separate (D8, `docs/RELEASE.md`).
6. Run the per-machine installer on a disposable VM by hand once (docs/RELEASE.md checklist): the real
   consent prompt, an install and an update as a standard user over the shoulder, and 0.1.1 to 0.1.2.
7. Test the updater end to end against a real installer, including the case where an app stays
   open (done in the per-user sandbox by `scripts/test-installer-e2e.js`; the per-machine run needs the
   elevated runner).

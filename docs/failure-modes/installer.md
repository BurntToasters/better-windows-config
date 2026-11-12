# Installer failure modes

Scope: `installer/bwc.nsi`, `installer/include/*.nsh`, `installer/scripts/*.ps1`,
`scripts/build-installer.js`, and `scripts/generate-nuget-licenses.js`. Written before the
code, per AGENTS.md. Each entry states the failure and the intended behavior. The NSIS
script has not been compiled on this machine yet (NSIS is not installed), so every
"intended" behavior below is also a check for the first compile and the E2E install test.

Exit codes for `scripts/build-installer.js`: `0` success, `1` build or compile failure,
`2` bad arguments, `3` NSIS not found or too old.

Exit codes for `BwcUpdate.exe --close-running`, which the installer calls: `0` no BWC
process runs from the install folder, `3` a process is still running, `2` bad arguments.
Any other code is treated as a failure of the close step.

## Build script

### NSIS missing

`makensis.exe` is not on PATH, not under `NSIS_HOME`, and not under
`%ProgramFiles(x86)%\NSIS` or `%ProgramFiles%\NSIS`.

- Intended: the script checks for NSIS before it publishes or generates anything, prints
  the searched locations and "Install NSIS 3.10+ from https://nsis.sourceforge.io and
  rerun", and exits `3`. It never downloads or installs NSIS.
- `--dry-run` does not need NSIS. It prints the exact `makensis` command line, and exits `0`
  with `makensis.exe` shown as a placeholder when NSIS is absent.

### NSIS source is internally inconsistent

A file named by `!include` is missing, a `${BWC_...}` define is used but no file or command
line defines it, or a macro is inserted that is not defined.

- Intended: `scripts/build-installer.js` runs a static check of `installer/bwc.nsi` and its
  includes before it prints the dry-run command and before a real run calls makensis. Any
  problem is printed with its file and line, and the script exits `1`. The check does not
  need NSIS.
- Required files are checked too: `BwcUpdate.exe` must be in the publish folder, because the
  installer extracts it to run the close step. A missing one exits `1` before makensis.

### NSIS too old or ANSI-only

Scripts use `Unicode true`, `__FILEDIR__`, and `/WX`. Older NSIS builds, or an ANSI-only
makensis, fail at compile time.

- Intended: the script reads `makensis /VERSION`. It exits `3` when the version is below
  3.10. A compile error exits `1` with the last makensis lines printed.

### Publish folder missing, stale, or from a failed run

`publish/<rid>` may be absent, may come from a build that failed, or may be from another
flavor.

- Intended: the script reuses the folder only when `artifacts/build/<name>.json` says
  `pass: true` with the same flavor and the main EXE exists. Otherwise it runs
  `scripts/dotnet-build.js`. A build failure exits `1` and no installer is written.

### Version that cannot map to a Windows file version

`--version` or `package.json` has a prerelease other than `-beta.N`, build metadata, or a
leading zero.

- Intended: exit `2` before any work. The version rules are the ones in
  `scripts/sync-version-helpers.js`, which also produces the four-part `VIProductVersion`.

### Output name collision and stale installer

An old installer from a previous run may still exist, or it may be open in another program.

- Intended: the script deletes the old `setup.exe` and its record before makensis runs, so a
  failed build never leaves a stale file that looks new. If the file is locked, the delete
  fails and the script exits `1` with the path.

### Publish path or repo path with spaces

- Intended: arguments go to `spawnSync` as an array with no shell. Defines are passed as
  single `/D` arguments. The displayed dry-run line quotes any argument with a space.

### Install path too long

`%LOCALAPPDATA%\Programs\Better Windows Config\` plus the longest payload path can exceed the
classic 260-character limit.

- Intended: each payload path relative to the install folder must be at most 140 characters.
  A longer one exits `1` at build time with the file name, so the problem never reaches a user
  machine. The 140 characters leave room for a typical per-user profile path. The upgrade
  staging folder `<install folder>.new` is one character longer than the install folder, so
  the same budget covers it.

### Reserved payload names

The publish folder contains a file named `LICENSE`, `THIRD-PARTY-NOTICES.txt`,
`installed-files.txt`, or `Uninstall.exe`, or anything under `bwc-tools\`. Or a payload file
name contains `"`, `$`, or a line break.

- Intended: exit `1` before makensis runs. The name would collide with an installer-owned
  file, or it would break the generated NSIS string.

### Notices generation fails

`obj/project.assets.json` is missing (no restore or build yet), a package is not in the local
cache, or a package has no license expression and no license file.

- Intended: the generator exits `1` and names the package. The installer is not built,
  because the About dialog and the license obligation depend on the notices. A package that
  declares a license expression but ships no license file is listed with its expression and
  URL, and the summary counts it.

## Installer runtime

### Host architecture

The ARM64 installer runs on an x64 host, or the x64 installer runs on ARM64.

- ARM64 installer on a non-ARM64 host: refused before any file changes, with the message to
  download the x64 installer.
- x64 installer on ARM64: allowed. The app is x64 and runs under emulation, as the
  architecture notes in `docs/failure-modes/build-scripts.md` describe.
- The check uses `GetNativeSystemInfo`, not `PROCESSOR_ARCHITECTURE`. If the call fails, the
  installer aborts rather than guessing.

### Windows build below the minimum, or unreadable

`CurrentBuildNumber` is missing or not a number, or it is below 19041
(`TargetPlatformMinVersion`).

- Intended: the installer aborts before any change, with the required version in the
  message. An unreadable value is read as 0, which is below the minimum, so it refuses.
- The comparison is `IntCmp <build> 19041 equal-label greater-label less-label` with equal
  and greater going to the continue label and less going to the refusal label. A build of
  19041 or newer is allowed. (An earlier draft had the labels inverted, which refused every
  supported Windows and allowed every old one.)

### Running processes in the install folder

`BetterWindowsConfig.exe`, `BwcRegedit.exe`, or `BwcUpdate.exe` runs from the target folder.
Replacing a running EXE fails.

- Intended: the close step runs the copy of `BwcUpdate.exe` that the installer extracted to
  `$PLUGINSDIR`, which is the version being installed. It is run as
  `BwcUpdate.exe --close-running --install-dir "<folder>" [--exclude-pid <pid>] --timeout-seconds 30`.
  No PowerShell and no WOW64 path are involved, so a 32-bit installer reaches 64-bit and
  ARM64 processes. The helper posts `WM_CLOSE` to visible windows and waits up to 30 seconds.
- An update is detected by `<folder>\BwcUpdate.exe` existing. A first install has nothing to
  close, and the step is skipped.
- `/PARENTPID=<n>` (passed by the updater) becomes `--exclude-pid`, so the updater that
  launched the installer is never asked to close. `n` must be 1 to 10 decimal digits. Any other
  value aborts before the close step with nothing changed.
- The installer does not wait for the parent updater to exit. The updater waits for the
  installer, so waiting here would deadlock. Only processes running from the install folder
  matter for file replacement, and the parent runs from the staging folder.
- Interactive: exit `3` offers Retry or Cancel. Retry runs the close step again. Cancel aborts.
- Silent (`/S`): exit `3` aborts with installer exit code `2` and changes nothing.
- Any other exit code (including `2`, bad arguments, or a missing helper) aborts with the
  code in the log and changes nothing. The installer never skips the close step.

### Process cannot be closed

Causes: a save-changes prompt the app keeps open, a process at a higher integrity level
(elevated `BwcRegedit`, where `PostMessage` is blocked by UIPI), a process with no visible
window (`BwcUpdate` in a hidden state), or an executable whose path cannot be read.

- Intended: the installer aborts before it changes any file. Unreadable paths count as still
  running, so the installer fails closed rather than overwriting a file it cannot see in use.
- The message names the folder. Nothing is rolled back because nothing was changed yet.

### Staging the new payload

Copying the new files could fail (disk full, a locked staging path, antivirus).

- Intended: the new payload, the manifest, `LICENSE`, and `THIRD-PARTY-NOTICES.txt` are written
  to `<install folder>.new` first. The install folder is not touched during staging. A failure
  here removes `<install folder>.new` (through `.onInstFailed`) and leaves the previous version
  working. The staging copy needs about the payload size in extra disk space, roughly 330 MB
  for the current build.
- A leftover `<install folder>.new` from an earlier failed run is removed before staging. It is
  only ever staging data.

### Leftover backup folder

`<install folder>.old` is kept during an upgrade. Its only copies of the previous version's
files are there until the upgrade succeeds, so it is never deleted blindly.

- Intended: if `<install folder>.old` already exists when an upgrade starts, the installer
  aborts before any change and names the folder. The user removes it or runs the installer
  again after checking it, because it may hold the only copy of the previous version.

### Commit and rollback

After staging succeeds, the installer swaps the files in three steps.

1. Move every file that the previous manifest lists into `<install folder>.old`, keeping its
   relative path. The previous manifest moves too. A line that is empty, absolute, or contains
   `..` is skipped with a log line. A missing previous manifest moves nothing.
2. Move each new file from `<install folder>.new` into the install folder, creating its folder
   first. Any existing file with the same name is removed first.
3. Remove `<install folder>.old` and `<install folder>.new`, and remove folders that the
   previous version left empty.

- A failure in step 1 or step 2 runs the rollback: the new files that were moved in are
  deleted, every file in `<install folder>.old` is moved back to its old place, and both
  staging folders are removed. The installer aborts with the message "Upgrade rolled back.
  Nothing was changed." The previous version keeps running.
- If the rollback itself fails (for example, a file is locked again), the installer stops, keeps
  both folders, and its message names both paths and says to restore by hand. It never deletes
  `<install folder>.old` while it may still hold the only copy of a file.
- Files that an older version put in the folder and did not list stay where they are.

### Previous payload removal

`installed-files.txt` from the previous version lists files that the new version no longer
ships. Lines can be missing, empty, or malicious.

- Intended: the installer moves only listed relative paths, and only the ones that exist.
  Lines that are empty, absolute, or contain `..` are skipped with a log line. A missing
  manifest (first install, or a folder copied by hand) moves nothing, and stale files stay.
  Files the user added stay.

### Shortcut AUMID not written

`bwc-set-aumid.ps1` cannot set `System.AppUserModel.ID` on the new `.lnk` (for example, the
COM call fails, the shortcut is locked, or PowerShell runs in Constrained Language Mode, which
blocks the `Add-Type` the helper uses).

- Intended: non-fatal. The shortcut is still created and the installer logs a warning. The
  running apps still set their own AUMIDs at startup, so only taskbar grouping of a pinned
  shortcut can differ.
- The helper runs `%WINDIR%\Sysnative\WindowsPowerShell\v1.0\powershell.exe` when that path
  exists, so a 32-bit installer uses the 64-bit PowerShell. It falls back to `%SYSDIR%`.
- `Add-Type` is kept, because a shell-property write needs a compiled COM interop type. Under
  Constrained Language Mode the helper exits `4` and the installer continues.

### Installer lookup of an invalid switch

`/PARENTPID=` is not digits, or `/RUNID=` contains a character outside letters, digits, `-`,
and `_`.

- Intended: the E2E installer aborts before changing anything. The release installer ignores
  `/RUNID` and `/SHORTCUTS`. A non-numeric `/PARENTPID` aborts in both flavors.

### E2E variant without its switches

The `-DBWC_E2E` build needs `/RUNID=` and `/SHORTCUTS=`. A missing switch would write to the
real Start menu or the real Uninstall key.

- Intended: the E2E installer aborts when either switch is missing. The uninstall key goes
  under `HKCU\Software\BWC-E2E\<RUNID>\Uninstall\BetterWindowsConfig` and shortcuts go to the
  given folder. The release build never reads these switches.
- `/RUNID=` is limited to 1 to 64 letters, digits, `-`, and `_`. The E2E uninstaller applies
  the same rule.
- The E2E build never reads the real uninstall key for a default folder (`InstallDirRegKey` is
  compiled only into the release flavor), so an E2E run without `/D=` cannot pick up a real
  install.
- The uninstaller reads the shortcut folder from the uninstall key, so it removes the same
  shortcuts it created.

### `/RELAUNCH` and the updater

- Intended: `/RELAUNCH` starts the Hub after a successful install. A failed launch is logged
  and does not change the exit code. The updater (`docs/contracts/update-manifest.md`) starts
  the Hub itself after the installer exits, so the updater must not pass `/RELAUNCH`. Passing
  it would start two Hubs.

### Install folder recorded for the next run

An older install wrote `InstallLocation`, or a user cleared it.

- Intended: the release installer reads the install folder from `InstallLocation` in the
  per-user uninstall key (`InstallDirRegKey`), so a reinstall with no `/D=` lands in the same
  folder. A missing value falls back to `%LOCALAPPDATA%\Programs\Better Windows Config`.

### Uninstall

- The uninstaller closes running BWC processes with the `BwcUpdate.exe` in the install folder,
  using the same exit-code rules as the installer. It has no PowerShell step.
- If `BwcUpdate.exe` is missing or does not support `--close-running` (exit `2`), the uninstall
  stops before anything is removed. Reinstall, then uninstall.
- It deletes the payload files listed at build time (generated `BWC_PAYLOAD_DELETE`), the two
  shortcuts recorded in the uninstall key, `LICENSE`, `THIRD-PARTY-NOTICES.txt`,
  `installed-files.txt`, `Uninstall.exe`, and the old `bwc-tools\bwc-close-apps.ps1` if an
  earlier version left it. It removes a folder only when it is empty, so user files in the
  folder stay. The uninstall key is removed last.
- It removes `%TEMP%\BWC-Update` (the updater's staging folder), so no downloaded installer or
  receipt survives an uninstall. A staging file that is still in use stays and does not stop the
  uninstall.
- Files from an older version that the upgrade already removed are gone. A file that an older
  version left behind and that the manifest does not list is not removed by the uninstaller.

### Uninstall key missing or stale

An old version wrote a different uninstall key, or a user deleted the key by hand.

- Intended: the installer always writes the current key. Upgrades overwrite it. Removing the
  key only hides the app from Apps and Features; the installed files still run.

## Notices and licenses

### Notices name a user path

Writing the local NuGet cache path into the notices would leak the user's profile path into
a shipped file.

- Intended: the generator writes package ids, versions, licenses, and source URLs only.
  Output contains no timestamp and no absolute path, so two runs produce identical bytes.

### Ordering and nondeterminism

- Intended: packages are sorted by lowercase id, then by version. Line endings are LF. The
  summary line has a fixed format.

# Installer failure modes

Scope: `installer/bwc.nsi`, `installer/include/*.nsh`, `installer/scripts/*.ps1`,
`scripts/build-installer.js`, and `scripts/generate-nuget-licenses.js`. Written before the
code, per AGENTS.md. Each entry states the failure and the intended behavior. The NSIS
script has not been compiled on this machine yet (NSIS is not installed), so every
"intended" behavior below is also a check for the first compile and the E2E install test.

Exit codes for `scripts/build-installer.js`: `0` success, `1` build or compile failure,
`2` bad arguments, `3` NSIS not found or too old.

## Build script

### NSIS missing

`makensis.exe` is not on PATH, not under `NSIS_HOME`, and not under
`%ProgramFiles(x86)%\NSIS` or `%ProgramFiles%\NSIS`.

- Intended: the script checks for NSIS before it publishes or generates anything, prints
  the searched locations and "Install NSIS 3.10+ from https://nsis.sourceforge.io and
  rerun", and exits `3`. It never downloads or installs NSIS.
- `--dry-run` does not need NSIS. It prints the exact `makensis` command line, and exits `0`
  with `makensis.exe` shown as a placeholder when NSIS is absent.

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
  machine. The 140 characters leave room for a typical per-user profile path.

### Reserved payload names

The publish folder contains a file named `LICENSE`, `THIRD-PARTY-NOTICES.txt`,
`installed-files.txt`, `Uninstall.exe`, or anything under `bwc-tools\`. Or a payload file name
contains `"`, `$`, or a line break.

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

### Windows build below the minimum

- Intended: a build number below 19041 (`TargetPlatformMinVersion`) aborts before any change,
  with the required version in the message.

### Running processes in the install folder

`BetterWindowsConfig.exe`, `BwcRegedit.exe`, or `BwcUpdate.exe` runs from the target folder.
Replacing a running EXE fails.

- Intended: the helper `installer/scripts/bwc-close-apps.ps1` finds processes whose image path is
  inside the install folder, posts `WM_CLOSE` to their visible top-level windows, and polls
  until they exit or 20 seconds pass.
- Interactive: on timeout a Retry/Cancel box appears. Retry runs the close step again.
  Cancel aborts.
- Silent (`/S`): no prompt. The installer logs the remaining processes and aborts with exit
  code `1`.
- The updater already waits for tool processes to exit before it starts the installer, so
  this step is a safety net for manual runs.

### Process cannot be closed

Causes: a save-changes prompt the app keeps open, a process at a higher integrity level
(elevated `BwcRegedit`, where `PostMessage` is blocked by UIPI), a process with no visible
window (`BwcUpdate` in a hidden state), or an executable whose path cannot be read.

- Intended: the installer aborts before it changes any file. Unreadable paths count as still
  running, so the installer fails closed rather than overwriting a file it cannot see in use.
- The message names the remaining process and the folder. Nothing is rolled back because
  nothing was changed yet.

### PowerShell blocked or missing

Group policy can override `-ExecutionPolicy Bypass`, or `powershell.exe` may be absent.

- Intended: the helper cannot start or exits with a nonzero code that is not `0` or `3`. The
  installer aborts with the code. It never skips the close step.

### Failure while copying files

A file is locked by something other than BWC (antivirus, a shell preview), or the disk is
full, after the old payload was removed.

- Intended: the installer aborts. Files already copied stay, and the manifest written before
  the copy lists every intended file, so the next run replaces all of them. The message says
  to rerun the installer. Nothing is silently skipped.

### Previous payload removal

`installed-files.txt` from the previous version lists files that the new version no longer
ships. Lines can be missing, empty, or malicious.

- Intended: the installer deletes only listed relative paths. Lines that are empty, absolute,
  or contain `..` are skipped with a log line. A missing manifest (first install, or a folder
  copied by hand) skips the removal, and stale files stay. Files the user added stay.

### Shortcut AUMID not written

`bwc-set-aumid.ps1` cannot set `System.AppUserModel.ID` on the new `.lnk` (for example, the
COM call fails or the shortcut is locked).

- Intended: non-fatal. The shortcut is still created and the installer logs a warning. The
  running apps still set their own AUMIDs at startup, so only taskbar grouping of a pinned
  shortcut can differ.

### E2E variant without its switches

The `-DBWC_E2E` build needs `/RUNID=` and `/SHORTCUTS=`. A missing switch would write to the
real Start menu or the real Uninstall key.

- Intended: the E2E installer aborts when either switch is missing. The uninstall key goes
  under `HKCU\Software\BWC-E2E\<RUNID>\Uninstall\BetterWindowsConfig` and shortcuts go to the
  given folder. The release build never reads these switches.
- The uninstaller reads the shortcut folder from the uninstall key, so it removes the same
  shortcuts it created.

### `/RELAUNCH` and the updater

- Intended: `/RELAUNCH` starts the Hub after a successful install. A failed launch is logged
  and does not change the exit code. The updater (`docs/contracts/update-manifest.md`) starts
  the Hub itself after the installer exits, so the updater must not pass `/RELAUNCH`. Passing
  it would start two Hubs.

### Uninstall

- The uninstaller closes running BWC processes the same way the installer does.
- It deletes the payload files listed at build time (generated `BWC_PAYLOAD_DELETE`), the two
  shortcuts recorded in the uninstall key, `LICENSE`, `THIRD-PARTY-NOTICES.txt`,
  `installed-files.txt`, and `Uninstall.exe`. It removes a folder only when it is empty, so user
  files in the folder stay. The uninstall key is removed last.
- The uninstaller runs the close helper from `bwc-tools\bwc-close-apps.ps1` in the install
  folder. The uninstaller cannot embed files, so the helper is installed with the app. If that
  copy is missing, the uninstall stops before anything is removed. Reinstall, then uninstall.
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

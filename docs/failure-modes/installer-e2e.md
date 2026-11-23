# Installer E2E: failure modes

The test `scripts/test-installer-e2e.js` proves the real in-app update. It builds two unsigned E2E-flavor
installers, A (the current version) and B (the next patch), installs A silently, starts the installed Hub
against a loopback feed that offers B, presses Download and Install, and checks that the real B installer
replaces the files and relaunches the new Hub. Case 2 does the same with BwcRegedit open. Case 3 uninstalls.

Every way the flow can fail is listed below, with how it is detected and what the run must do. The test
code is in `tests/Bwc.E2E/Installer/`. Its output is `artifacts/e2e/installer-update.json`.

## Build stage (scripts/test-installer-e2e.js)

1. **makensis missing or older than 3.10.** Detected by `findMakensis` and `readNsisVersion`. The script exits
   3 before any build, with the `choco install nsis -y` hint. NSIS on this machine lives in
   `Program Files (x86)\NSIS`, which is not on PATH; the script sets `NSIS_HOME` to that folder when it is
   present.
2. **Build A fails (publish or NSIS).** `build-installer.js` exits non-zero. The script stops and reports its
   exit code. Its artifact record `artifacts/installer/*-setup.json` says why.
3. **Build A reuses a stale publish.** `build-installer.js` reuses `publish/<rid>-e2e` only when
   `artifacts/build/<rid>-e2e.json` says pass for the package.json version. Otherwise it republishes.
4. **B publish carries version A.** The `-p:Version` and `-p:FileVersion` overrides did not apply. The script
   reads the B `BetterWindowsConfig.exe` file version and stops when it is not B.
5. **B publish fails (dotnet error, warning, flavor check).** Each project's exit code and the flavor check
   (`checkPublishFlavor`) are checked. The B publish folder is `publish/<rid>-e2e-next`, separate from A.
6. **B publish reuses A's obj intermediates.** Different version inputs force recompilation. A stale
   ReadyToRun file would make the collision check fail. The script runs the publish with the same
   arguments as `dotnet-build.js`.
7. **NSIS source audit or makensis compile fails for B.** `auditNsisSource` and makensis's exit code are
   checked. The old `artifacts/installer/*-B-setup.exe` is deleted first, so a failure never leaves an old
   setup that looks new.
8. **Signing turns on by accident.** Both builds set `SKIP_WIN_CODESIGN=1`. A real signing run needs
   certificates and the ship flavor, and the test must never sign.
9. **Tracked version files change.** The script never edits package.json, Directory.Build.props, or
   CHANGELOG.md. The B version comes from MSBuild properties on the command line only.
10. **The test host is elevated.** The script checks `elevationState()` and refuses. The Hub must run
    asInvoker, and a UAC prompt is never acceptable.

## Test stage (tests/Bwc.E2E/Installer)

11. **Gate missing.** Without `BWC_INSTALLER_TEST=1` and the installer paths, every Installer test reports
    Inconclusive. The script treats that as failure, because the result file must show `pass: true`.
12. **Result file stale or missing.** The script deletes the old `artifacts/e2e/installer-update.json`
    before running. A missing or unreadable file fails the run.
13. **Filter selects no tests.** `dotnet test` exits 8. The script reports that as a failure, not a pass.

### Install (silent, case 1, 2, 3)

14. **E2E installer refuses to start without `/RUNID` and `/SHORTCUTS`.** `bwc-common.nsh`
    (`BwcReadE2eSwitches`) aborts in `.onInit`, before anything changes. The test passes both switches for
    its own installs. See 22 for the update path.
15. **Install folder is the default folder.** The test always passes `/D=` to a folder under the temp
    directory. The test refuses a path that contains a space, because `/D=` is unquoted.
16. **Install does not write the E2E uninstall key or DisplayVersion is not A.** Checked by reading
    `HKCU\Software\BWC-E2E\<run-id>\Uninstall\BetterWindowsConfig`.
17. **Install writes shortcuts outside `/SHORTCUTS`.** The test counts `.lnk` files in the given folder
    after install. A count of zero fails case 3.

### Hub and feed

18. **Hub does not start, or its Help > Check for Updates item is missing.** `AppSession` times out, or
    `MenuItem_CheckForUpdates` is not found. The test stops that case and names the step.
19. **Update Available dialog does not appear.** The feed served an equal or older version, the manifest was
    rejected, or the trusted key did not match. The check names the dialog and the Hub status text.
20. **Manifest signature refused.** The signed trusted comment must carry the version, the channel, and the
    expiry exactly. The test uses the same signer as the Bwc.Update tests (`minisign.mjs`).
21. **Installer signature or SHA-256 refused by the helper.** The test signs the real B bytes, so a refusal
    here means a real mismatch between the manifest and the bytes served.

### Redirect path (the shape that broke 0.1.0)

22. **Redirect not followed, or query altered.** The feed sends a 302 from `/dl/...` to `/objects/...` with
    a GitHub-shaped query containing `%2F` and `%3D`. The server records the raw query it receives. The test
    fails when the query is missing, has changed, or the hit count is zero.
23. **URL policy refuses the redirect target.** Loopback http is allowed only with
    `BWC_E2E_UPDATE_ALLOW_HTTP=1`. The helper then refuses the download, and the test fails at the Hub
    dialog step.

### Update run

24. **E2E installer aborts when the helper starts it (known blocker).** The helper starts the installer with
    `/S /RELAUNCH /PARENTPID=<n> /D=<dir>` only. The E2E flavor needs `/RUNID` and `/SHORTCUTS` as well, so
    the installer aborts before changing anything, and no relaunch happens. The Hub never passes
    `--installer-switch`. The test detects this as "installer did not run" and stops there.
    Fix: the E2E build reads `BWC_E2E_INSTALLER_RUNID` and `BWC_E2E_INSTALLER_SHORTCUTS` when the switches
    are absent (installer/include/bwc-common.nsh). The test sets both on the Hub it launches. Remaining risk:
    the Hub, helper, or installer does not inherit the environment. The test then reports "installer did not
    run", with the helper log as evidence. The test also checks that the fallback variables were inherited.
25. **Installer waits for the parent (`/PARENTPID`) that never exits.** The Hub stays open after it asked to
    close. The test waits 90 s for the Hub to exit and reports a timeout.
26. **Running app blocks the close step.** Case 2 keeps BwcRegedit from the install folder open. In silent
    mode the installer's close step cannot show its Retry box, so it must close the app itself. The test
    records whether the regedit process exited.
27. **Installer leaves `<dir>.new` or `<dir>.old`.** After success both must be gone. The test checks both
    after the Hub relaunch.
28. **Relaunch does not start the new Hub, or starts it from another path.** The test waits for a process
    whose image is the installed exe and started after the Update press (`WaitForStartedProcess`).
29. **Relaunched Hub is still A, or the file version is not B.** The test reads the file version of the
    installed exe and the process image. Both must read B.
30. **Uninstall key DisplayVersion is not B after the update.** Read from the run's registry key.
31. **Rollback leaves the old install in an unknown state.** An installer failure after the swap must put
    A back. The test checks that the install folder still holds A when the update check fails.

### Case 3 (uninstall)

32. **`Uninstall.exe` returns before the files are gone.** NSIS uninstallers can copy themselves to temp.
    The test polls for the install folder to disappear, for up to 60 s.
33. **Files, shortcuts, or the uninstall key remain.** The test checks the folder, the `.lnk` files, and the
    registry key after uninstall.

### Cleanup and process safety

34. **Wrong process killed.** The test kills only processes it started, by PID and by image path under its
    own temp install folder. It never kills by name.
35. **Process started by the installer is not tracked.** The test takes the PID from
    `WaitForStartedProcess` and stops that one. A leftover process would lock the install folder, and the
    cleanup reports it.
36. **Temp folder or registry key left behind.** The `finally` block removes the install folder, the
    shortcut folder, `HKCU\Software\BWC-E2E\<run-id>`, and the staging folders created during the run.
    A file still held by a process is reported.
37. **Settings written to the real per-user folder.** Every Hub launch sets `BWC_DATA_ROOT` through
    `DataRootEnvironment` and seeds settings with the startup check off.
38. **GUI tests run at the same time.** `GuiGate` serializes GUI tests with a named mutex. Installer tests
    must not start while another run holds it.

### Environment

39. **Antivirus or SmartScreen stalls the unsigned installer.** A run that hangs at the installer step times
    out at 90 s and reports the step name. An unsigned test build is expected.
40. **Slow runner.** The install and relaunch timeouts are generous (90 s to 180 s). A timeout is a failure
    with its step name, not a silent pass.
41. **CI: NSIS installed but not found.** The CI step checks `Get-Command makensis` and the x86 NSIS folder,
    then runs `choco install nsis -y` only when both are missing. The script finds NSIS through `NSIS_HOME`.
42. **Result file written by a crashed test.** The script treats a missing file, a schema other than 1, or
    `pass: false` as a failure.

## Machine mode and the Elevated tests (I4)

The per-machine install (docs/failure-modes/installer.md, "Per-machine install (0.1.2)") writes
Program Files, HKLM, and the all-users Start menu. The unelevated flow above never runs it. The Elevated tests do, only
through the user's elevated runner (`docs/elevated-runner.md`), which starts exactly
`node scripts/test-e2e.js --rid <host> --flavor e2e --filter "TestCategory=Elevated"` with no arguments and no environment.
They are in `tests/Bwc.E2E/Installer/InstallerMachineTests.cs` and write `artifacts/e2e/installer-machine.json`.

The only places they may write: `%ProgramFiles%\BWC-E2E\<run-id>`, `HKLM\Software\BWC-E2E\<run-id>` (64-bit view), the
all-users Start menu folder `BWC-E2E-<run-id>`, and the existing hive mount `HKEY_USERS\BWC-E2E-<run-id>` (not used here).
The installer itself also makes the siblings `<run-id>.new`, `<run-id>.old`, and `<run-id>.upd` next to the run folder, and
its in-place uninstaller may queue `Uninstall.exe` and the run folder for deletion at the next restart (installer.md M38).
Those are the installer's own names, removed in `finally` (the queue entry in `HKLM\SYSTEM` is not ours to remove).

### Prepare step (`scripts/test-installer-e2e.js --prepare --rid <rid>`)

43. **`--prepare` runs elevated.** The same refusal as the normal run (exit 2): the E2E Hub and the build must never
    run elevated, and the inputs must come from the user's own unelevated build.
44. **A build fails during `--prepare`.** Exit 1 and no inputs file. The old inputs file is removed before the build, so a
    failed prepare never leaves inputs that look current.
45. **Inputs file missing.** The Elevated installer tests report Inconclusive (NOT RUN) with the message
    "run `node scripts/test-installer-e2e.js --prepare --rid <rid>`". They never build and never guess a path.
46. **Inputs stale.** A setup file is gone, or its size or SHA-256 differs from the file (a later build replaced it, or it
    was edited after the prepare). NOT RUN, same message. The hash is the staleness check, not a signature.
47. **Inputs for another rid, unreadable JSON, wrong schema, or a missing field.** NOT RUN with the reason. A rid
    mismatch names both rids.
48. **The normal `test-installer-e2e.js` run rebuilds the installers.** It does not write the inputs file, and a rebuild
    can change the bytes, so the user runs `--prepare` last before the runner (docs/elevated-runner.md). A stale result is
    caught by 46, never trusted.

### Gates (every Elevated installer test)

49. **Not elevated.** Checked first, from the process token (`TokenElevation`). Inconclusive "not run", like the existing
    Permissions and Hive tests. A normal `dotnet test` shows them as skipped, never as passed or failed.
50. **Not the E2E flavor.** `RequireE2EOnlyFlavor` fails the test before any install, because the ship setup would ask for
    elevation (`requireAdministrator`) and the ship publish has no `/MACHINE` mode.
51. **A UAC prompt.** The E2E setup is `asInvoker`, the tests pass `/S`, and the helper chain is started with a plain
    start in the E2E flavor, so no step shows a prompt. A step that waits for a prompt hits its timeout (setup 3 minutes,
    helper 2 minutes), the test records the step name, and only the process this test started is stopped.

### Machine run (`InstallerRun` machine mode)

52. **Run id.** `mach-<10 hex>-<case>`, 64 characters at most, letters, digits, `-`. A new id for every run, so a left-over
    from a crashed run never collides, and the force cleanup can match names exactly.
53. **A path outside the three locations.** The run folder is built from `ProgramW6432` and the run id, the Start menu
    folder from the all-users Programs folder and the run id, and the key from the run id. Every delete first checks that the
    path is the run folder, `<run folder>.new`, `.old`, or `.upd`, the run's Start menu folder, or the run's key. Nothing else
    is ever removed. A folder that is a link is removed as a link and never entered.
54. **Cleanup order.** `finally` stops the processes this run started (by pid and by image under the run folder), runs the
    registered uninstall string once, then removes what remains. `<PF>\BWC-E2E` and `HKLM\Software\BWC-E2E` are removed only
    when empty. A cleanup that cannot finish (a locked file) is a failed check that names the path, so the user can remove it
    by hand.
55. **HKLM written through the 32-bit view (`WOW6432Node`).** The fresh install test reads the 64-bit view and also checks
    that `Software\WOW6432Node\BWC-E2E\<run-id>` is absent. If a broken setup wrote it there, the check fails and names the
    key. The test does not delete it (outside the allowed locations): remove it by hand.
56. **Uninstall strings.** `UninstallString` must be `"<dir>\Uninstall.exe" _?=<dir>` and `QuietUninstallString`
    `"<dir>\Uninstall.exe" /S _?=<dir>`. Missing `_?=` means the uninstaller would run from a copy in `%TEMP%` (M37). The E2E
    setup writes no `/RUNID` into them, so the test adds `/MACHINE /RUNID=<id>` before `_?=` when it runs the string.
57. **Shortcuts.** Two `.lnk` files in the all-users `BWC-E2E-<run-id>` folder, none for the run in the current user's Start
    menu, and the AUMIDs `BetterWindowsConfig.Hub` and `BetterWindowsConfig.Regedit` read back through the shell property
    store. A missing AUMID is only a warning in the installer (installer.md), but here it is a failed check.
58. **Folder ACL.** The run folder inherits from Program Files (the DACL is not protected and has inherited entries), and
    no allow entry for Users, Authenticated Users, or Everyone grants a write right on the folder. `icacls` output and the SDDL
    go into the artifact (`evidence`). A protected or Users-writable folder fails.

### `/D=` naming another folder

0.1.x carries no migration code, so there is no test of an upgrade over a 0.1.1 install. The one claim that stays is
installer.md M5 and M6. Numbers 60 and 62 belonged to the 0.1.1 test and are not reused.

59. **The installer changes the other folder.** The test makes a folder of its own below its run folder in the admin-only
    work folder (85) and puts a few files in it. A name, size, or write time in it that differs from the snapshot taken before
    the machine setup is a fail.
61. **`/D=` becomes the target.** The machine setup is started with that other folder as `/D=`. The target must still be the
    run's Program Files folder: `InstallLocation` of the HKLM key is the machine folder, the other folder is not the install
    folder, and the other folder holds no B file.

### Elevated update through the real helper chain

63. **A protected-root override is set.** The E2E seam `BWC_E2E_UPDATE_PROTECTED_ROOT` would run the helper unelevated in a
    temp folder. The test removes it from the helper's environment and asserts it is not set, so the real `<install dir>.upd`
    path and the real elevated token are what is tested.
64. **The staging folder is in the real `%TEMP%`.** The helper is started with `TEMP` and `TMP` of a scoped folder under the
    run's data root, like the Hub E2E, so the real staging root of the user's Hub is never used or pruned.
65. **The stand-in installer is not what the helper verified.** The copy's SHA-256 (from the stand-in's own record) must equal
    the signed one, and the stand-in's path must be `<run folder>.upd\<32 hex>\installer.exe`.
66. **The `.upd` ACL.** The root, the copy folder, and its `tmp` folder: owner Administrators, DACL protected (no
    inheritance), allow entries only for SYSTEM and Administrators with full control, no deny entry. SDDL is recorded.
67. **The installer's TEMP.** The stand-in records `TEMP` and `TMP`. Both must be `<copy folder>\tmp` (installer.md M32,
    update-elevation.md HT1). The command line must be `/S /RELAUNCH /PARENTPID=<helper pid> /D=<run folder>` and the stand-in
    must be elevated (it inherits the helper's token; no verb).
68. **Stale `.upd` folders.** After the first apply the test adds a stale 32-hex folder, an upper-case 32-hex folder, and a
    folder named `keepme`. The second apply removes the first copy and the stale hex folder, and leaves the other two (EA22).
69. **The stand-in starts the Hub.** The chain test sets `BWC_E2E_INSTALLER_NO_RELAUNCH=1` for the stand-in, so no Hub is
    started from the helper's elevated process. Starting the Hub at medium integrity is its own test (71).
70. **The stand-in installer record is missing.** The helper reported ok but no record appears within 60 s: fail with the
    helper's stdout and exit code. The record is `CreateNew`, so a second run never reuses the first.

### `--launch-hub` and `--close-running` from the elevated test

71. **The Hub is started elevated, or not at all.** The test replaces `BetterWindowsConfig.exe` in its own machine folder
    with the stand-in (the stand-in is a harmless process: no window, no file, it only waits; it is the self-contained x64 build, because the
    framework-dependent ARM64 one picks up the app's `hostfxr.dll` beside it and exits 0x80008096). It runs
    `BwcUpdate.exe --launch-hub`, reads the started process's integrity level (`TokenIntegrityLevel`), and requires medium
    (0x2000), while its own token is high (0x3000). A real Hub is never started because a shell-started Hub would not get
    the test's data root and would write the user's real settings folder.
72. **No desktop shell.** `--launch-hub` exits 8 (`DeElevationFailed`, M21). The test records exit code and stdout and
    fails (the runner is meant to run in a logged-on desktop session). Nothing is started, so nothing needs to be stopped.
73. **The wrong process is stopped.** Only the pid from the stand-in's own record is stopped, and only when its image is the
    stand-in in the run folder.
74. **The elevated Regedit stays open.** The test starts `BwcRegedit.exe` from the run folder with a scoped data root, checks
    that its token is elevated (otherwise the test proves nothing and fails), and runs
    `--close-running --install-dir <run folder>`. Exit 0 and the Regedit gone. Exit 3 means it did not close.
75. **SeDebugPrivilege.** The helper prints nothing about it, so the test reads the helper's own token while it runs. A second
    stand-in named `BwcRegedit.exe` in a sub folder never closes, which keeps the helper alive for its 8 s timeout. The test
    samples the helper's `TokenPrivileges` until `SeDebugPrivilege` is enabled. The helper exits 3 and names that pid. If the
    test's own token does not hold the privilege (a group policy), the check records "not held" and does not claim enabled.
    The test never enables the privilege in its own process, so a child inherits it disabled.

### Machine uninstall, version order

76. **Uninstall through the string.** `Uninstall.exe /S /MACHINE /RUNID=<id> _?=<dir>` runs in place and the test waits for the
    process (120 s). Exit code, then: HKLM key gone (and the empty `BWC-E2E\<id>` parents), shortcut folder gone, payload files
    gone, `<dir>.upd` gone (a plain copy folder is planted first), no `<dir>.new` or `.old`.
77. **Leftovers.** The uninstaller cannot delete itself or the folder it runs from, so `Uninstall.exe` (and the folder) may
    stay and be queued for the next restart (M38). Allowed: nothing but `Uninstall.exe` left in the folder, and every new entry
    of `PendingFileRenameOperations` (read before and after) names `Uninstall.exe` or the run folder. Anything else is a fail
    ("undocumented reboot-pending leftover"). The test then removes the file and folder itself. The queue entries stay in
    `HKLM\SYSTEM` and Windows ignores them at the restart because the paths no longer exist.
78. **Downgrade accepted against HKLM.** After B is installed, setup A must exit non-zero, leave the key at B and the Hub exe
    at B, and leave no `.new` or `.old` (M19).
79. **Same version does not repair.** Setup B over B must exit 0, leave key and file at B, and leave no `.new` or `.old` (M18).

### Security review 2 (I5): the Elevated tests and the sandbox AUMID check

Written before the tests and the code. The Elevated tests run as administrator, so everything they run or delete must come
from a place a standard user cannot change. These rows are enforced by code that compiles in every run and executes only
in the elevated runner (the unelevated run reports them as not run).

82. **The setup path comes from a user-writable file (finding 5).** `installer-inputs.json` is in the checkout, so any path
    in it (a UNC path, another folder) with its own size and hash would be accepted. The inputs are used only when each
    setup is a plain file inside `<repo>\artifacts\installer` (the output folder of `build-installer.js`), is named
    `BetterWindowsConfig-<semver>-<rid>-e2e-setup.exe` with the run's rid, is not a link, and its manifest requests only
    `asInvoker` (the E2E setup; a ship setup, which asks for elevation, is refused). Otherwise NOT RUN with the reason.
83. **The hash is checked once and the file is run later.** The test copies each setup into an admin-only run folder,
    `%ProgramFiles%\BWC-E2E\work-<id>\inputs`, hashes the copy against the inputs file (and the size), and runs the copy.
    A copy that differs is NOT RUN. The original is never run.
84. **Program Files from the environment.** `ProgramW6432` is a variable the caller controls. The 64-bit Program Files folder
    is `SHGetKnownFolderPath(FOLDERID_ProgramFiles)`. An existing `%ProgramFiles%\BWC-E2E` that is a link, or is not owned by
    Administrators, SYSTEM, or TrustedInstaller, or that standard users can write, is refused: the Elevated tests are NOT RUN
    (they would otherwise create and delete inside a folder a user controls).
85. **The run's working folder is in `%TEMP%`.** An Elevated run that kept its working folder in `%TEMP%\bwc-installer-e2e-<id>`
    would start elevated children with that folder as the working directory and delete it recursively as administrator. It
    lives in `%ProgramFiles%\BWC-E2E\work-<id>\<run id>` instead, with the inputs. The folder that test 59 and 61 name in
    `/D=` is inside it too.
86. **The recursive delete.** The only recursive deletes of the Elevated tests are of folders inside the work folder, and
    the work folder itself, each checked to be below `%ProgramFiles%\BWC-E2E` by name and not a link. A work folder that
    cannot be removed is a failed cleanup check naming the path, never a silent leftover.
87. **The unelevated run.** None of 82 to 86 runs without an elevated token. `RequireReady` returns NOT RUN first, so the work
    folder is never created, and the unelevated installer E2E (`test-installer-e2e.js`) is unchanged: its sandbox stays in
    `%TEMP%`, because it is unelevated.
88. **The shortcut AUMID (finding 1, installer.md M45) is not checked in the unelevated run.** The user-sandbox install in
    `Installer_Update_HubInstallsRealNewerInstaller` reads both shortcuts back through the shell property store: `<case>.a.aumid` (for example `update.a.aumid`) passes
    only when the Hub shortcut carries `BetterWindowsConfig.Hub` and the Registry Editor shortcut `BetterWindowsConfig.Regedit`.
    A missing AUMID is only a warning in the installer, but here it is a failed check, so a broken `--set-aumid` cannot pass.
89. **Reinstall before the restart (installer.md M51).** After the uninstall of test 76, when the queue now names this run's
    `Uninstall.exe`, the test runs the setup again and requires a non-zero exit, no `installed-files.txt`, and no key. When the
    queue does not name it (the uninstaller deleted itself), the check records that it did not apply.
90. **`_?=` names another folder (installer.md M47).** The Elevated test runs the uninstaller with `_?=` set to a folder in the
    work folder that holds a copy of a BWC payload name. It must exit non-zero, and that folder and the real install and key
    must be untouched.
91. **A `.upd` copy with a `tmp` child (installer.md M50).** Test 76 plants `<dir>.upd\<32 hex>\installer.exe` and
    `<dir>.upd\<32 hex>\tmp\x.dat` (what an update leaves), and the uninstall must remove `.upd` completely.

### Result file

80. **`artifacts/e2e/installer-machine.json`.** Written when at least one machine test passed its gates: every check, the run
    id, the rid, the versions, the evidence text (`icacls`, SDDL, helper output), and `pass`. The old file is deleted when the
    first test passes its gates, so a crashed run leaves none. A run where every test is Inconclusive writes nothing.
81. **A run killed halfway.** Leftovers stay under the three locations. The run id is in the folder name, so the user removes
    `%ProgramFiles%\BWC-E2E\<id>*`, `HKLM\Software\BWC-E2E\<id>`, and the Start menu folder `BWC-E2E-<id>` by hand. The next run
    does not touch them (its id is new).

## Third tool: Local Group Policy Editor (0.2.0)

Written before the tests and the code. The product has three shortcuts and three apps now; the installer E2E reads that from one table
(`InstallerRun.ProductShortcuts`: name, AUMID, exe) and not from three copies of the names. See installer.md M53 to M59.

92. **The shortcut count is three.** Every silent install of the unelevated E2E (`<case>.a.shortcuts`, `<case>.b.shortcuts`) requires
    exactly `ProductShortcuts.Length` shortcuts in the shortcut folder (it was "more than 0"), so a missing `Local Group Policy Editor
    (BWC).lnk` fails the case and an extra stray one does too. The machine test (Elevated) requires the same count in the all-users folder.
93. **Every shortcut carries its AUMID.** `<case>.a.aumid` reads all three through the shell property store: the Hub
    `BetterWindowsConfig.Hub`, Registry Editor `BetterWindowsConfig.Regedit`, Local Group Policy Editor `BetterWindowsConfig.Gpedit`. The Elevated
    run adds `fresh.shortcuts.gpeditAumid`. A missing or wrong id is a failed check, not a warning (88).
94. **Every shortcut shows its own icon.** `<case>.a.icons` reads the icon location of each shortcut through `IShellLinkW` and requires the
    exe of its own row (`BwcGpedit.exe` for the Gpedit shortcut, under the run's install folder) and index 0. A shortcut that points at
    another exe, at the Hub icon file, or at a missing path fails. The check does not draw the icon: that the exe embeds `bwc-gpo.ico` is the
    icon test of the Gpedit app (`resources/bwc-gpo.ico` as `ApplicationIcon`).
95. **The Hub refuses while both tools are open.** The "regedit" update case now also starts `BwcGpedit.exe` from the install folder
    (`regedit.gpedit.open`), and the Hub's refusal text must name `Registry Editor` and `Local Group Policy Editor` (`regedit.hub.refusesWhileToolsOpen`).
    The process names the test stops at cleanup come from the same table.
96. **Uninstall removes the third shortcut.** `uninstall.shortcuts` requires 0 remaining, which now includes the Gpedit shortcut.

## Audit fixes (0.2.0): first-install rollback and template cleanup (installer.md M60 to M66)

Written before the tests and the code. All of these run in the unelevated per-user sandbox. The uninstaller's data root is a
folder inside the run's own folder (`<run folder>\programdata`), passed as `BWC_E2E_INSTALLER_PROGRAMDATA` to the uninstaller
only. No case reads or writes the real `%ProgramData%`, the real registry, or Program Files.

97. **A first install fails after the move-in and leaves a folder behind (M60).** `firstfail` puts a folder where the Hub shortcut
    file goes (`<run>\shortcuts\Better Windows Config.lnk`), so `CreateShortCut` fails after the payload is in place and the setup
    rolls back. Required: a non-zero exit (`firstfail.setup.exit`), no install folder at all (`firstfail.installDir`: before the fix
    it held empty payload folders), no `installed-files.txt`, no uninstall key, no `.new` or `.old` (`firstfail.noLeftovers`). Then
    the blocker is removed and the same setup runs again: exit 0, the Hub exe present, DisplayVersion A, three shortcuts
    (`firstfail.rerun.*`). The rerun is what the old behavior broke: `BwcCheckInstDir` refused the leftover folder.
98. **The first-install abort text (M61).** A silent setup shows no text, so no E2E check can read it. The NSIS source audit does
    (`auditNsisSource`, run by `build-installer.js` and by this script for B): `BwcRollback` must hold the first-install text, and
    that text must not say a previous version is installed. The audit is a build step, not a test of this file.
99. **Uninstall removes the imported templates and keeps the backups (M62, M63).** `uninstallData` seeds the sandbox store
    (`<programdata>\BetterWindowsConfig\PolicyDefinitions`): two `.admx` (one upper case), `.adml` in two language folders, an unrelated
    `readme.txt` in the store and an unrelated `notes.txt` in one language folder, and a backup tree under `gpedit\backup` with
    random bytes. After the uninstall: every `.admx` and `.adml` is gone (`uninstallData.templatesGone`), the language folder that
    held only `.adml` files is gone and the one with `notes.txt` stays (`uninstallData.foldersEmptyOnly`), both unrelated files remain
    byte for byte (`uninstallData.othersKept`), and every backup file is present with the same SHA-256 (`uninstallData.backupsKept`).
100. **Only templates: the data root goes too (M62).** `uninstallClean` seeds the store with templates only. After the uninstall the
    store, the language folders, and `<programdata>\BetterWindowsConfig` are gone, and the sandbox root `<programdata>` itself is
    not touched (`uninstallClean.rootRemoved`). The data root is removed only because it is empty then (case 99 keeps it).
101. **The store folder is a junction (M64).** `uninstallLinkStore` makes `PolicyDefinitions` a junction (`mklink /J`, no elevation)
    to a folder of the run that holds `x.admx` and `en-US\x.adml`. After the uninstall the junction and every file behind it are
    still there with the same SHA-256 (`uninstallLinkStore.targetUntouched`, `uninstallLinkStore.linkKept`). The cleanup removes the
    junction as a link before the run folder is deleted, so the run's own recursive delete never meets it.
102. **A language folder is a junction (M64).** `uninstallLinkLang` has a plain store folder with `a.admx` and `en-US` as a junction to
    a folder that holds `a.adml`. The whole step is skipped: `a.admx` stays, the junction and its target file stay
    (`uninstallLinkLang.skipped`). Skipping all of it, not only the linked folder, is the required behavior (M64).
103. **The step never reads the real ProgramData (M65).** `uninstall.*` (the existing uninstall case) runs the uninstaller without the
    variable, with the E2E flavor. The test lists the real `%ProgramData%\BetterWindowsConfig` (names, sizes, write times; read only)
    before and after and requires the lists to be equal (`uninstall.realProgramDataUntouched`). This machine has no write to it from
    any test.
104. **The owner check (M67).** Cases 99, 100, and 102 already run the template cleanup over folders that the test user created, so
    they prove that the E2E flavor's extra acceptance of the current user's SID lets the cleanup run (a broken `GetNamedSecurityInfoW`
    or `EqualSid` call would skip the step and fail 99 and 100). The reverse, a folder owned by another principal that must make the
    step skip, cannot be built without a privilege that an unelevated test lacks. That claim, and that the ship build has no
    current-user acceptance and no read of the E2E variable, is the NSIS source audit (`auditTemplateCleanup` in
    `scripts/build-installer.js`, run by `build-installer.js` and by this script for B). It is not a test of this file.
105. **The policy-writer guard.** `tests/Bwc.E2E/Installer/InstallerRollbackAndDataTests.cs` names `PolicyDefinitions` and writes files
    and folders, so `scripts/check-policy-writer.js` lists it in `policyPathAllowlist`. The reason: it writes only under the run's
    stand-in `%ProgramData%` folder (`InstallerRun.ProgramDataRoot`, inside the temp run folder), never the real one. Only this file is
    listed; no rule of the guard is widened.
106. **A link left behind by a failed test.** A junction is always removed with a non-recursive delete in `finally`, before
    `InstallerRun.Dispose` deletes the run folder. A junction that cannot be removed is a failed `cleanup.link` check naming the path.

## Update from the 0.1.x layout to 0.2.0 (R1)

Written before the test and the harness change. A 0.1.x install has two tools: two Start menu shortcuts (Hub, Registry Editor) and no
`BwcGpedit.exe`. The claim is only that the 0.2.0 installer installs cleanly over that layout through the in-app update; 0.1.x has no
migration code (the project rule), so nothing is proved about a real old release. The old layout is built from the current tree by
`scripts/test-installer-e2e.js`, never from a checked-out or downloaded release, and never by `scripts/build-installer.js` (unchanged).
The case id prefix is `old`; the test is `Installer_Update_From01xLayoutAddsGpedit` and runs unelevated in the per-user sandbox like case 1.
It needs no elevated runner, and `--prepare` does not build the old setup.

### Building the old layout (scripts/test-installer-e2e.js)

107. **The old payload still carries Gpedit.** The old setup is built from a publish of all four projects at version 0.1.99
     (`publish/<rid>-e2e-old`, so the publish passes the same flavor check as B), and the NSIS file list drops every `BwcGpedit.*` file.
     The script stops when no file was dropped, when `BwcGpedit.exe` is still in the list, or when `BetterWindowsConfig.exe`,
     `BwcRegedit.exe`, or `BwcUpdate.exe` is no longer in it (a filter that took too much). The installed `installed-files.txt` is
     read by the test (`old.a.manifest`): it must not name `BwcGpedit.exe` and must name the other three.
108. **The installer still writes the third shortcut.** `installer/bwc.nsi` writes the Gpedit shortcut and its AppUserModelID
     unconditionally, so the old setup is compiled from a copy of `bwc.nsi` in `artifacts/installer/work/<name>` that lacks exactly
     those two statements. The copy is made by exact line match: each of the two lines must occur once, or the script stops ("the
     Gpedit statements were not found in bwc.nsi"). A changed installer source therefore fails the build instead of producing an old
     setup that quietly keeps three shortcuts. `installer/bwc.nsi` itself is never written.
109. **The copied script cannot find its includes.** `bwc.nsi` takes `BWC_ROOT` from `__FILEDIR__`, which would be the work folder. The
     copy gets the absolute `installer` folder instead (one exact replacement, also required to match once), and the same
     `auditNsisSource` and makensis exit-code checks run as for B.
110. **The old setup is mistaken for a release artifact.** It is an E2E setup only: `BWC_E2E` is defined, the manifest must request
     `asInvoker` (`checkSetupManifestLevel`, as for B), the publish passes the e2e flavor check, the patched script lives only under
     the ignored `artifacts/` folder, and the switch exists only in `scripts/test-installer-e2e.js`. `build-installer.js`, the NSIS
     sources, and the release build take no part in it.
111. **The old version is not below 0.2.0.** The version is the constant 0.1.99, never read from or written to a tracked file. The
     script refuses to run when package.json is not above it. The payload must report 0.1.99 (the `BetterWindowsConfig.dll` check of 4)
     and the uninstall key must show it (`old.a.displayVersion`).
112. **Old and new setups collide.** The old setup name carries 0.1.99 and `old-layout`, the work folder is its own, and the file is
     removed before the build, so a failed build leaves no setup that looks new.

### Before the update (`old.a.*`)

113. **Three shortcuts exist.** Exactly two `.lnk` files, named `Better Windows Config.lnk` and `Registry Editor (BWC).lnk`, with their
     AppUserModelIDs and their own exe as icon (`old.a.shortcuts`, `old.a.aumid`, `old.a.icons`), and no `Local Group Policy Editor
     (BWC).lnk` (`old.a.gpeditShortcutAbsent`).
114. **`BwcGpedit.exe` exists.** It must be absent from the install folder (`old.a.noGpeditExe`). The installed Hub must list the
     Local Group Policy Editor row as "Not installed" and the Registry Editor row as "Installed" (`old.hub.rows.before`), which also
     shows the Hub reads the same folder the test inspects.
115. **Data is seeded wrongly.** The test writes a random 512-byte file under the case's scoped data root (never the real folders) and
     keeps its SHA-256 for 120. Nothing is written to a policy path, so `scripts/check-policy-writer.js` is unaffected.

### The update and after it (`old.*`, `old.after.*`)

The press, the redirect, the helper, the staging folders, the relaunch, and the installed Hub version are the existing checks of case 1
(`old.hub.*`, `old.feed.*`, `old.installer.*`, `old.installed.version`, `old.relaunch.*`; 24 to 30).

116. **A stale shortcut set after the update.** Exactly three shortcuts named from `InstallerRun.ProductShortcuts`, no extra file
     (`old.after.shortcuts`). Each shortcut's target is its own exe in the install folder (`old.after.targets`, read through
     `IShellLinkW.GetPath`), carries its AppUserModelID (`old.after.aumid`) and its own icon (`old.after.icons`). A failure here is a
     product defect: report it, do not change the installer or the test to fit.
117. **`BwcGpedit.exe` is missing or not B after the update.** It must exist, report B's version (`old.after.gpedit.version`), and be
     named by `installed-files.txt` together with the other three (`old.after.manifest`). A missing file is a product defect.
118. **It is there but does not start.** The test starts it from the install folder with the scoped data root and requires the process to
     stay alive and a window titled "Local Group Policy Editor" within 20 s (`old.after.gpedit.starts`). The process is tracked, and
     `InstallerRun.Dispose` stops it by pid and image path before the folder is deleted (34, 35).
119. **The relaunched Hub does not list the third tool.** The Hub the installer started must show the rows `ToolRow_regedit` and
     `ToolRow_gpedit`, both "Installed" (`old.after.hub.rows`). The product is Hub plus two tools, which is the three apps and three
     shortcuts; the Hub list itself has two rows (installer-e2e.md 92).
120. **User data is lost or rewritten.** The seeded file of 115 must still exist with the same SHA-256 (`old.after.userData`).
121. **The case is skipped without notice.** A run where the gate is off writes no checks (11). The script also requires the proof ids
     `old.a.noGpeditExe`, `old.after.shortcuts`, `old.after.gpedit.starts`, and `old.after.userData` to be present and passed in the
     result; a result without them is a failure, so a filter that dropped the case cannot pass.
122. **Elevation.** Everything here is the asInvoker E2E setup in the per-user sandbox, run unelevated. No step prompts for UAC, elevates,
     or touches the elevated runner or its task, and the old setup is not part of `--prepare` or of `installer-inputs.json`.
123. **A leftover from a failed run.** Unchanged cleanup: the run's temp folder, its HKCU key, and its processes go in `finally` (36). The
     new publish folder and setup are build outputs under `publish/` and `artifacts/installer/`, ignored by git and overwritten by
     the next run.

124. **Observed with R1: shortcut AppUserModelIDs are empty after an in-app update (product finding, not fixed here).** The check
     `old.after.aumid` fails on the first full run: all three shortcuts read back an empty AppUserModelID after the update from the Hub.
     The same read passes right after a silent install of the old setup (`old.a.aumid`), after the new setup run directly over the old
     layout, and after the new setup run over itself, so `--set-aumid` and the installer's upgrade path work. A diagnostic run of the
     plain A to B in-app update (case 1) shows the same empty ids, so the loss is in the in-app chain (the helper starts the installer),
     not in the old layout. Case 1 never read the ids after an update, which is why it was not seen. The check stays in the old-layout
     case as the honest proof; it fails until the product is fixed. Targets, icons, the shortcut set, `BwcGpedit.exe` and the rest of
     the case pass.

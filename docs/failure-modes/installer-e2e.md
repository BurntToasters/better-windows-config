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

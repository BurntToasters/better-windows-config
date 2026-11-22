# Real x64 machine checks for 0.2.0

This file is for an agent working on a real x64 Windows 10 (2004 or later) or Windows 11 machine,
ideally a disposable one. The machine that built 0.2.0 is Arm64, so none of the x64 evidence below
exists yet. Work in a clean checkout of the `next-0.2.0` branch, and do not edit tracked files while
you test: the gate records whether the tree was dirty.

Rules for the whole task:

- Use an unelevated shell for every command in steps 1 and 2. The ship E2E refuses an elevated
  shell (it reports NOT RUN, exit 3, and the gate counts that as a failure).
- The E2E runs drive real windows on the desktop. Stay logged on, keep the screen unlocked, and do
  not touch the mouse or keyboard while they run. Run one command at a time, never two at once.
- Do not search the whole disk, and do not start the elevated runner. Step 3 needs a person to
  accept a Windows consent prompt for the setup, so ask a human for that part.
- Report every failure as it is. Do not retry until it passes without saying that you retried.

## 1. Setup

Follow `docs/build-setup.md`, section "Toolchain" (the table of tool versions), then the section
"Commands" for the publish commands, and install:

- the .NET SDK pinned in `global.json`,
- Node.js 26.13.1 or newer on the 26 line, and npm 12.2.2 or newer,
- NSIS 3.10 or newer (`makensis.exe` on PATH, or `NSIS_HOME` set), needed by the installer E2E and
  by the setup build in step 3,
- the C++ x64 and ARM64 build tools of Visual Studio, because `BwcUpdate.exe` is NativeAOT and the
  gate compiles both architectures (see `docs/installer.md`, section "Build an installer").

Then, from the repository root:

```powershell
git status --short        # must print nothing
git rev-parse HEAD        # write this commit down
npm install
```

## 2. Automated runs

Run these in order from the repository root, unelevated.

```powershell
node scripts/test-all.js --rid win-x64
```

This is a full gate (no `--quick`). It runs every step in `docs/build-setup.md`, section "Quality
gate evidence", including `test-policy-release-gate`, then the ship and E2E publish and GUI runs for
`win-x64`. It takes a long time. If a step fails, the gate stops: note the step name, the summary
line, and the log named under `artifacts/gate/logs/`, and carry on to the installer E2E only if you
can, without repeating the gate.

Note that a gate for one rid is not release proof. The release needs a `--rid all` gate that passed
on both architectures (`docs/RELEASE.md`, section "Build, gate, sign, and upload the draft"). This
run gives the x64 half of the evidence.

Then the installer E2E, which builds two unsigned E2E installers and checks the in-app update
between them in a per-user sandbox:

```powershell
node scripts/test-installer-e2e.js --rid win-x64
```

(`npm run test:installer-e2e -- --rid win-x64` is the same command.) It needs NSIS and an unelevated
shell. It exits 0 only when `artifacts/e2e/installer-update.json` says pass.

## 3. By hand

This part installs software and needs a person to accept the Windows consent prompt. Use a
disposable machine or a virtual machine, and take a snapshot first.

1. Build a setup for testing, in an unelevated shell. It is unsigned, so set the signing skip for
   this shell only:

   ```powershell
   $env:SKIP_WIN_CODESIGN = "1"
   npm run installer:x64
   ```

   The setup is `artifacts/installer/BetterWindowsConfig-0.2.0-win-x64-setup.exe`. Check that
   `artifacts/installer/` also holds the JSON record written next to it.

2. Install it (ask a human to accept the consent prompt). Open the Start menu and confirm that
   three shortcuts exist: `Better Windows Config`, `Registry Editor (BWC)`, and
   `Local Group Policy Editor (BWC)`. Each should show its own icon.

3. Start `Better Windows Config` (the Hub) and open the Local Group Policy Editor from it. It needs
   administrator rights to save, so accept the consent prompt for it. If the window shows a
   read-only notice instead, write down why (not elevated, Windows Home, or no write access).

4. Pick one harmless policy and change it. Suggestion: User Configuration > Administrative
   Templates > Control Panel > Personalization > "Prevent changing screen saver" (the name can
   differ between Windows builds; if it is missing, pick another policy under Control Panel >
   Personalization and write down which one you used). Do not touch any other policy.
   - Open its setting dialog, choose Enabled, press OK.
   - Open `gpedit.msc` (the classic editor, on editions that have it), go to the same policy, and
     confirm it shows Enabled. Close and reopen `gpedit.msc` if it shows a stale state.
   - Back in the Local Group Policy Editor (BWC), open the same policy, choose Not Configured, press OK.
   - In `gpedit.msc` confirm the policy shows Not Configured again.
   - If this machine has no `gpedit.msc` (Windows Home), say so and skip the classic check. Also
     note that this is the first run on a real Home machine, if it is one.

5. Update from the released 0.1.5 setup to the new build. Use a fresh snapshot or uninstall first,
   so the machine has no 0.2.0 on it:
   - Download the 0.1.5 setup for x64 from the project's GitHub release `v0.1.5`
     (`BetterWindowsConfig-0.1.5-win-x64-setup.exe`) and install it. Confirm that two shortcuts exist
     (the Hub and the Registry Editor).
   - Run the 0.2.0 setup from step 3.1 over it. Accept the consent prompt.
   - Count the shortcuts: there must be three (Hub, Registry Editor, Local Group Policy Editor).
   - Start the Hub and confirm the Local Group Policy Editor opens from it.
   - Open `Settings` > `Apps` > `Installed apps` and confirm there is one entry,
     `Better Windows Config`, at version 0.2.0.

6. Uninstall from `Installed apps` and confirm the three shortcuts are gone.

Write down anything odd, even when it passed: a slow start, a flash of the wrong theme, a dialog
that looked different from the classic one.

## 4. Files to bring back

Copy these to the person who asked, unchanged:

- `artifacts/e2e/result-win-x64-ship.json` and `artifacts/e2e/result-win-x64-e2e.json`
  (also any `artifacts/e2e/result-win-x64-*.json` written by the same runs).
- `artifacts/gate/result.json`, the gate summary written by `scripts/test-all.js`, and the
  `artifacts/gate/logs/` folder when any step failed.
- `artifacts/e2e/installer-update.json`, the installer E2E result.
- A short text note with the commit from step 1, the Windows edition and build number, the result of
  each hand step in step 3 (pass, fail, or skipped with the reason), and anything odd you saw.

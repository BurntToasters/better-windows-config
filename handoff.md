# Handoff: Better Windows Config

Written 2026-11-11 for branch `experimental`, HEAD `106180a` ("App services, import parity, classic
value editors, integration fixes"). Commits since `4f0f30b` are listed in section 6. Another
worker may have uncommitted source edits in the tree. Run `git status` before you change anything.

## 1. Read first

1. `AGENTS.md`. Its rules bind every change.
2. This file.
3. The plan: `%USERPROFILE%\.claude\plans\we-are-working-on-zany-pearl.md`. It is outside the repo
   and holds waves 0 to 4 and the work package ids.
4. `docs/parity/regedit-inventory.md`. It has 534 rows. All of them are still `open`.
5. `resume.md`. It is local only and excluded by `.git/info/exclude`.

## 2. What BWC is

Better Windows Config (BWC) is a set of Windows tools in C# on .NET 10 and WinUI 3 (Windows App
SDK 2.6.5). The first tool re-implements classic `regedit.exe` with classic layout and density.

Classic regedit is the oracle. A package is done when its behavior matches captured classic output
and the manager has compared screenshots. Passing BWC's own tests is not enough.

Other parts: a Hub launcher, a self-updater (`Bwc.Update`), and an NSIS installer that is written
but not compiled. License: MPL-2.0.

## 3. Repo layout

- `src/Bwc.Core`: `AppPaths`, `Log`, `SettingsStore`, `SingleInstance`, `CrashHandler`.
- `src/Bwc.Registry`: registry engine. `RegistryReader`, and `RegistryWriter`, the only type that
  calls mutating APIs. Write policies, `.reg` import and export, hives, privileges, remote roots,
  and `Find`.
- `src/Bwc.UI`: shared WinUI parts. Dense theme, classic shell, menus, classic dialogs, icons,
  `DenseTree`, `DetailsList`, hex editor.
- `src/Bwc.Regedit` and `src/Bwc.Regedit.App`: the Registry Editor. Feature controllers live in
  `Controllers/`. `Bwc.Regedit.App` is the exe head.
- `src/Bwc.Hub`: launcher, About, and update dialogs.
- `src/Bwc.Update`: updater. Manifest, minisign verification, download, apply helper.
- `installer/`: NSIS script and helper scripts. Not compiled yet.
- `tests/`: `Bwc.Core.Tests`, `Bwc.Registry.Tests`, `Bwc.Registry.Shipping.Tests`,
  `Bwc.Update.Tests`, and `Bwc.E2E` (UI tests by feature folder).
- `tools/`: `Bwc.Oracle.Resources` and `Bwc.Oracle.Capture`, which drive classic regedit.
- `scripts/`: Node.js build, gate, and release scripts, modeled on `..\Zinnia`.
- `docs/`: `contracts/` (engine, update manifest), `decisions/`, `failure-modes/` (written before
  code), `parity/` (inventory and oracle), `build-setup.md`, `elevated-runner.md`, `installer.md`.
- `spikes/`: S1 to S5 experiments, kept for reference.
- Generated and git-ignored: `artifacts/`, `publish/`, `TestResults/`, `bin/`, `obj/`.

## 4. Environment

- Recorded machine (see the gate and E2E artifacts): Windows 11 Enterprise 10.0.26200, ARM64,
  display scale 200% (192 DPI). x64 builds run under emulation.
- .NET SDK: `global.json` pins 10.0.403 with `latestFeature` roll forward. Recorded runs used
  10.0.404. The SDK is not on the shell PATH by default. Refresh it in PowerShell:
  ```
  $env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')
  ```
- Node: `package.json` engines are `^22.22.2 || ^24.15.0 || >=26`. Recorded runs used v26.13.0.
- The build scripts set `MSBUILDDISABLENODEREUSE=1` and `DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1`
  themselves.
- NSIS 3.10 or newer and GnuPG 2.4 or newer are not installed. D1 (compile) and D8 (signing)
  need them. The user installs them.
- `.githooks/*` are tracked with mode 100644, not executable.

## 5. Build, test, and run safely

### Commands

- `node scripts/test-all.js --quick`: static checks, both app builds, and the four test projects.
  Fast. It does not publish.
- `node scripts/test-all.js --rid win-arm64` (or `win-x64`, or `all`): full gate. For each RID it
  adds `publish-ship`, `verify-flavor-ship`, `e2e-ship`, `publish-e2e`, `verify-flavor-e2e`, and
  `e2e-flavor`. It stops at the first failure.
- `node scripts/test-e2e.js --rid <rid> --flavor e2e [--build]`: E2E only, against an existing
  publish folder.
- `node scripts/dotnet-build.js --rid <rid> --flavor ship|e2e`: publish one flavor and run its
  flavor check.
- `node scripts/launch-unelevated.js publish\win-arm64-e2e\BwcRegedit.exe`: start an app by hand.
- `npm run validate`: em dash, changelog, and comment lint.
- `node scripts/check-registry-writer.js` (and `--self-test`): static check for write APIs.
- `npm run oracle`: regenerate the classic captures (section 5.4).

Gate and E2E output: `artifacts/gate/result.json` (schema 2: commit, dirty paths, tool versions,
per-step logs in `artifacts/gate/logs/`) and `artifacts/e2e/result-<rid>-<flavor>.json` (checks,
notes, known gaps). The gate deletes each E2E result before its step runs.

### 5.1 The two E2E flavors

- `ship` (`publish/<rid>`): real registry writes allowed through `ShippingWritePolicy`. Runs only
  tests that are not `Mutating`, `E2EOnly`, or `Elevated`.
- `e2e` (`publish/<rid>-e2e`): writes limited by `SandboxWritePolicy` to
  `HKCU\Software\BWC-E2E\<run-id>`. Runs every test except `Elevated`. Mutating tests run only here.
- Every mutating step first checks that the address bar is inside the sandbox root. If it is not,
  the test fails.
- Exe manifests: E2E builds are `asInvoker`. Ship builds keep their source manifests: Regedit
  `highestAvailable`, Hub and Update `asInvoker`. `scripts/flavor-metadata.js` checks both.

### 5.2 GUI and input rules

- Never trigger UAC. Do not run built exes from a shell or by double-click. Start them with
  `scripts/launch-unelevated.js`, which refuses to run from an elevated shell.
- Do not use `scripts/run-app.js --elevated`. It uses `Start-Process -Verb RunAs`.
- GUI tests and oracle captures share the desktop. Hold the named mutex `Local\BwcGuiTestLock`
  (`tests/Bwc.E2E/Harness/GuiGate.cs`; the oracle uses the same name) around any GUI launch. Never
  run two GUI sessions at once.
- Raw input lesson: FlaUI's held-key helpers (`Keyboard.Pressing`, `TypeSimultaneously`) and its
  held-button helpers do not produce real input sequences here. The list and shell tests send
  held modifiers and drags with the `keybd_event` and `mouse_event` helpers in
  `tests/Bwc.E2E/List/RawKeyboard.cs`, `tests/Bwc.E2E/Shell/ShellKeyboardTests.cs`, and
  `tests/Bwc.E2E/AppServices/AppServicesSupport.cs`. Use those helpers for any held key or drag.
  UIA patterns are for reading state only.
- Elevated tests are tagged `[TestCategory("Elevated")]`. They run only through
  `scripts/elevated-runner/run.js`, after the user installs and enables the task
  (`docs/elevated-runner.md`). Agents never create the task or write `%ProgramData%`.

### 5.3 Registry sandbox

- E2E writes go only to `HKCU\Software\BWC-E2E\<run-id>`. Tests delete only keys they created.
- The oracle writes only to `HKCU\Software\BWC-Oracle`. `npm run oracle` deletes that key at the end.
- Classic regedit writes `FindFlags` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Applets\Regedit`
  on exit. The oracle restores its applet state after each capture.
- Hive APIs need `SeBackupPrivilege` and `SeRestorePrivilege`. Do not elevate to get them.
- Remote registry is read-only until E4. Never route `RegistryWriter` to a remote connection.

### 5.4 Oracle

- `npm run oracle` takes `--only <steps>`, `--list`, and `--skip-build`. Steps: `resources`,
  `export`, `metrics`, `dialogs`, `menus`, `import`, `find`, `sort`.
- Untracked, regenerated from the local `C:\Windows\regedit.exe`: `regedit-resources.json` and
  `.md`, `regedit-100dpi.png`, `dialogs/`, and `local/`. These hold Microsoft content.
- Tracked: metrics, dialog rectangles, menu metrics, row metrics, selection colors, the three
  `.reg` goldens, `seed-tree.md`, `import-behavior.json`, `find-sequence.json`, and `sort-order.json`.
- Captures record `dpi` in their JSON. Divide by `dpi.window / 96` for 96 DPI values.
- A 100% scale capture is still owed. Run record: `artifacts/oracle/last-run.json` (untracked).

### 5.5 Last recorded evidence

- Gate: `artifacts/gate/result.json` is a `--quick` run at `fae4c4f` with a dirty tree. It passed.
  It is not a full gate for `106180a`.
- E2E, win-arm64, e2e flavor: `artifacts/e2e/result-win-arm64-e2e.json` has 902 checks and 1
  failure, `appsvc.font.rowHeight.changed`. Commit `106180a` reports 901 passed and 1 failed.
- E2E, win-x64, e2e flavor: `artifacts/e2e/result-win-x64-e2e.json` is older (347 checks, passed).
  Do not treat it as current.
- E2E check counts by commit: 753 (`0c5f352`), 794 (`fae4c4f`), 901 passed and 1 failed (`106180a`).

## 6. Work package status

Plan ids. "Done" means the commit landed and its message or artifacts record the result. The
plan defines wave 0 as W0-1 to W0-5 only.

Done:

- Wave 0, W0-1 to W0-5 (`4f0f30b`): mutating and E2E-only tags, read-only ship flavor, sandbox
  check before every mutating step, per-flavor result files, always-true checks removed, test hooks
  compiled only into E2E, gate artifact schema 2, sandbox refuses links and root rename.
- E1 value display and enumeration (`a5b4ac4`): DWORD and QWORD oracle formats, one-call
  enumeration, NUL-safe names.
- E2 import parity (`106180a`): missing ancestors created, bad lines skipped, lenient parser,
  oracle cases replayed, refused-file and partial-write messages.
- RF0 controller split (`a5b4ac4`): `RegeditRootView.xaml.cs` split into controllers over
  `RegeditContext`.
- RF1 value and key basics (`adb2e94`): Enter and double-click open Modify, Modify items in Edit,
  Copy Key Name, Refresh, pane-based Delete and Rename, synthetic `(Default)` row.
- RF3 message catalog (`51f1e1e`, `106180a`): classic strings for key, value, import, export,
  hive, favorites, and print errors.
- RF4 app services (`106180a`): `Log`, `CrashHandler`, single instance with `/m`,
  `regedit file.reg`, View > Font, View > Theme, Help > About, persisted window placement,
  splitter, and column widths.
- RF5 partial (`a5b4ac4`, `106180a`): Edit String, DWORD and QWORD with Base radios, Multi-String
  with the empty-string warning, Edit Binary (8 bytes per row) on `ClassicDialog`.
- RF8 partial (`106180a`): Load Hive and Unload enabled only under the right roots. Remote
  browsing is read-only.
- UF1 theme (`a5b4ac4`, `51f1e1e`), UF2 menus (`96de8ba`, `0c5f352`), UF3 icons (`51f1e1e`),
  UF4 classic dialog system (`a5b4ac4`, `adb2e94`), UF5 `DenseTree` (`0c5f352`), UF6 `DetailsList`
  (`fae4c4f`), UF7 hex editor with the classic Edit Binary layout (`106180a`), UF8 shell
  (`0c5f352`).
- T1 oracle regeneration (`96de8ba`).
- H1 Hub redesign (`50c1d35`, `96de8ba`, `adb2e94`).
- D1 script only (`d02c0df`): `installer/bwc.nsi` and `scripts/build-installer.js`. The script
  exits 3 when NSIS is missing. Nothing is compiled yet.
- D2 update verifier and helper (`a5b4ac4`): manifest, minisign verification, download checks,
  apply helper, isolation tests. The release key is a placeholder (section 7).
- D3 Hub updater UI (`96de8ba`).
- D9 third-party notices (`d02c0df`): `scripts/generate-nuget-licenses.js`.

Remaining:

- RF2 inline create and rename: `New Key #1` and `New Value #1` edited inline, and inline Rename.
- RF5 rest: resource list, descriptor, and requirements viewers; Binary Data viewer with
  Byte, Word, and Dword formats.
- RF6 Find: classic Find dialog, separate progress dialog with Cancel, `FindFlags` persisted in
  the classic key, no extra options. Depends on E3.
- RF7 Import and Export UI: export range panel, all file types, import progress, result
  messages, cancellation.
- RF8 rest: Select Computer through the object picker, remote root context menu, remote editing
  (after E4).
- RF9 accelerators and context menus from the oracle tables, text-edit context menu, the
  "Go to HKEY_CURRENT_USER" variant, Expand and Collapse label switch.
- RF10 Print: range panel, progress dialog, print-to-file report test.
- E3 Find parity: resume when the cursor is gone, `CompareStringEx` ordering, data-matching rules
  from an oracle capture, classic `FindFlags` codec.
- E4 Security, remote, and privileges: SACL support, self-relative descriptor check, AddRef on
  remote handles, serialized `PrivilegeScope`, remote write routing behind the write policy.
- E5 Export formats: text export, hive export from the UI, REGEDIT4 selection, whole-registry export.
- C1 Core hardening: `SingleInstance` backoff and timeout, current-user-only client, settings size cap.
- D1 compile and E2E, once NSIS is installed.
- D4 to D8 release tooling: signer and manifest validators, release dry-run harness, session and
  preflight policy, draft, verify, and publish. D8 (signing) needs GnuPG.
- D10 first beta dry run `0.1.0-beta.1` to a draft release. The user runs it on a release VM.
- R15 parity audit: not started. Classify every inventory row as pass, declared difference, or
  gap, and write `docs/parity/regedit-audit.md`. Run it after the remaining packages.
- `docs/RELEASE.md`: not written.
- x64 at 100% scale, and CI green on x64 for HEAD. Not confirmed.
- Standard-user E2E pass, elevated suites (S4 interop, hive round trip), and remote registry tests
  against a second machine.

## 7. Known issues

1. `appsvc.font.rowHeight.changed` fails. After View > Font changes to 16 DIP, the list row
   height reads 0 px (expected 64 px). Live font changes break the window's UI Automation tree.
   The user reports a fix in progress. Re-run the arm64 E2E flavor before trusting `106180a`. The
   screenshot path is in the E2E result file.
2. The update release key is a placeholder. `src/Bwc.Update/Signing/TrustedKeys.cs` sets
   `IsPlaceholder = true`, so updates fail closed. The user must create the real key and replace it.
   A release build must not ship with the placeholder.
3. No update server is configured. `BuildManifestUrl` in `src/Bwc.Hub/Updates/UpdateSources.cs` is
   empty, so the Hub reports "not configured". The hosting URLs are not chosen.
4. NSIS and GnuPG are not installed (section 4).
5. Classic regedit's `HKCU\Software\Microsoft\Windows\CurrentVersion\Applets\Regedit\Favorites`
   subkey disappeared during the session. The cause is unknown. Check the favorites tests and
   captures before relying on them.
6. Find still uses `CurrentCultureIgnoreCase` for matching and ordering
   (`src/Bwc.Registry/Find/RegistryFinder.cs`). Classic uses `CompareStringEx`. E3 covers this.
7. `scripts/test-all.js` help text and its `--quick` description say "three test projects". The gate
   runs four: `test-core`, `test-registry-e2e`, `test-registry-shipping`, and `test-update`.
8. `.githooks/*` are not executable (mode 100644).
9. CI (`.github/workflows/ci.yml`) runs the quick gate and the x64 E2E suite on `experimental`. The
   status for HEAD is not recorded here. Check the latest run before you call it green.
10. The 100% scale oracle capture is still owed. The current captures are at 192 DPI.

## 8. User decisions

These stand until the user changes them.

- Theme follows the system (light, dark, high contrast). View > Theme overrides it (System,
  Light, Dark). Light mode matches classic regedit.
- Microsoft content from the oracle stays untracked. Regenerate it with `npm run oracle`.
- Never prompt UAC. The elevated runner is opt-in, and the user installs it.
- Implementation runs through `/oversee` with Haiku 5.5 workers (`model: "haiku"`,
  `effort: "xhigh"`, `subagent_type: "general-purpose"`). The manager audits every change, runs
  every check, and looks at the screenshots.
- Chat replies are terse. Files, docs, and code use normal prose.
- Commits go on `experimental` only, and only when the user asks. No agent co-author trailers
  (AGENTS.md). No push without the user.
- Open for the user: install NSIS 3.10 or newer and GnuPG; provide a 100% scale session or VM;
  create the release key; choose the update server; decide whether to rewrite history to remove
  the resource dump committed before the oracle change (the plan only stops tracking it going
  forward).

## 9. Rules for agents

- No unit tests written after code. Test end to end first and keep a repeatable artifact.
  For an isolated component, write `docs/failure-modes/<area>.md` first, then tests, then code.
- Edit only the files in your lane. The manager owns shared files: `BetterWindowsConfig.slnx`,
  `Directory.*.props`, `build/*.props`, `package.json`, `NativeMethods.txt`, exe-head `App.xaml`,
  `src/Bwc.UI/Themes/Dense.xaml`, command tables, `.resw` files, `AGENTS.md`, `docs/contracts/**`,
  and `docs/decisions/**`. See `docs/decisions/0002-file-ownership.md`.
- No em dash (U+2014) in any file. Check with `node scripts/validate-no-em-dash.js --root .`.
- Mutating E2E tests run only in the E2E flavor. The ship flavor stays read-only.
- Remote registry stays read-only until E4.
- Never write `%ProgramData%` or create the elevated task from an agent.

## 10. Next steps

1. Confirm or fix `appsvc.font.rowHeight.changed` in the arm64 E2E flavor. Then run the full gate
   for both RIDs with one command: `node scripts/test-all.js --rid all`. Record the result.
2. Start the next three packages, also listed in `resume.md`:
   - RF2 inline create and rename (regedit lane, no open dependencies).
   - E3 Find parity (engine lane; RF6 depends on it).
   - D4 to D7 release tooling (scripts only; no NSIS needed).
3. Then, in any order that the dependencies allow: RF5 rest, RF6, RF7, RF8 rest, RF9, RF10, E4,
   E5, C1, D8, D1 compile, and D10.
4. Run R15 after the remaining packages. Every row is `open` until then, which means unclassified,
   not failing.
5. Owed manual checks: the elevated runner run (the user enables the task), remote registry against
   a second machine, a 100% scale oracle capture, a clean x64 VM install, and x64 CI.

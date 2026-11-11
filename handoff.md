# Handoff: Better Windows Config, Registry Editor parity

Stopped 2026-11-11 UTC at a verified gate. Read `AGENTS.md`, `resume.md`, this file, and
`docs/parity/regedit-inventory.md` before changing code. The next major lane is R15, the parity
audit. Nothing in the working tree is committed; do not commit or push unless the user asks.

## Verified state

- Branch `experimental`, HEAD `d331627 changes` (user commit). Everything after it is uncommitted.
- Full gate: `node scripts/test-all.js --rid win-arm64`
  - **14/14 steps PASS**, 375366 ms.
  - `artifacts/gate/result.json`: `pass: true`, no failed steps.
  - E2E ship and e2e: **345 checks, 0 failed** in the recorded e2e flavor. The gate also ran the
    ship flavor with the same result.
- `dotnet test tests/Bwc.Registry.Tests ... -p:BwcFlavor=E2E`: **119 total, 118 passed,
  1 skipped**. The skip is `ElevatedSaveLoadReadUnloadAndDeleteRoundTrip`, which needs
  `SeBackupPrivilege` and `SeRestorePrivilege`.
- `Bwc.Core.Tests`: 18/18. `Bwc.Registry.Shipping.Tests`: 1/1.
- `dotnet build BetterWindowsConfig.slnx -c Release -p:Platform=ARM64` and x64: 0 warnings.
- `node scripts/check-registry-writer.js` and `--self-test`: pass.
- `validate-no-em-dash.js`, `lint-comments.mjs`, `validate-changelog-version.js`: pass.
- No `BwcRegedit`/`BetterWindowsConfig` processes, no `HKCU\Software\BWC-E2E` children, and no
  `BwcHive*` keys under HKLM after cleanup.
- Verified publish folders: `publish/win-arm64` and `publish/win-arm64-e2e`.

## Completed and accepted

Foundation and process:

- M0 toolchain, M1 oracle/resources/metrics/goldens, M2 contracts and decisions.
- F1 skeleton, F2 build scripts, F4 E2E harness, F5 quality gate and CI.
- Hub launcher, bounds, single instance, crash handler, settings and logging.

Registry engine:

- B1 native surface/writer guard, B2 reader, B3 typed writes, B4 value codec/display.
- B5/B6 `.reg` v5 and REGEDIT4 writer/parser; all three oracle goldens round-trip byte-identical.
- B7 find, live export/import, B8 hives/privileges, B9 remote read-only roots, B10 security.
- `RegistryWriter` remains the only type that calls mutating registry APIs.
- `RegistryReader` now accepts a `RemoteRegistryConnection` and routes HKLM/HKU to borrowed
  remote handles; local behavior is unchanged.

Registry Editor UI:

- Dense theme/shell/menu keyboard, `DenseTree`, `DetailsList`, UIA peers, address bar.
- New/Delete/Rename/value editors, confirmation and error dialogs, sandbox policy.
- Hex viewer and hex editor (U10/R6), context menus and shortcuts (R12).
- Favorites (R10), Permissions/aclui (R11/B10), Print (R14).
- File Import/Export, Find/Find Next, command line `/s /e /a /m`.
- File Load/Unload Hive (B8/R8), including owned dialogs and a conditional elevated round-trip.
- File Connect/Disconnect Network Registry (B9/R8), including read-only remote HKLM/HKU tree
  nodes and remote values.
- E2E coverage for every lane above.

Safety/build:

- `Directory.Build.props` records `BwcFlavor` before `CoreCompile`, so ship/e2e switching always
  recompiles and cannot leave `SandboxWritePolicy` in a shipping DLL.
- E2E flavor refuses writes outside `BWC_E2E_SANDBOX_ROOT`.
- `scripts/dotnet-build.js` and `scripts/test-all.js` disable MSBuild node reuse and build servers
  to prevent the publish-step hang.
- `SandboxRegistry` uses `EntryPoint = "RegQueryInfoKeyW"`; without the `W`, stale-key sweeps
  threw `EntryPointNotFoundException`.

## Current stopping point

R15, the parity audit, is **not started**. Every one of the 534 inventory rows is still `open`.
No audit file or updated inventory exists yet. The previous attempt to split the audit into two
subagent passes was interrupted; no audit output was written.

Do not treat the inventory's `open` statuses as known failures. They are unclassified, not
unimplemented. The app and tests are green.

## Next steps

1. **R15 parity audit.** This is the next lane and the main remaining feature work.
   - Classify every row in `docs/parity/regedit-inventory.md` as `pass`, `declared-difference`,
     or `gap`, with evidence.
   - Evidence sources: `artifacts/gate/result.json`, `artifacts/e2e/result.json`, the E2E and
     registry tests, the three `.reg` goldens, `docs/parity/oracle/*`, `Resources.resw`, the
     failure-mode docs, and `resume.md` known gaps.
   - Write `docs/parity/regedit-audit.md` with the method, summary counts, and every exception
     with a reason. Then update the Status column in `docs/parity/regedit-inventory.md`.
   - Suggested split: MENU/CTX/ACC/DLG first, then MSG/VIEW/BEH/EXP. The MSG section is 276 rows;
     group it by RT_STRING range instead of treating each string as a separate feature.
   - Known declared differences to carry into the audit: no status bar (menu help strings are
     unused), system aclui for permissions, system `PrintDlgEx` for print, Find progress in the
     Find dialog, hex viewer instead of the oracle binary list-box variants, menu item widths
     about 41 DIP versus 32.5 DIP, and 100% DPI capture still owed.
   - Expected gaps to verify: View > Font, Help > About, export range panel, import progress,
     resource/resource-list viewers, the second computer picker (DLG-031/032), and several
     undocumented accelerators (F4/F6, Ctrl+T, Alt+D/Ctrl+L, Alt+1..9, Alt+Up, Backspace).
2. **D1 installer, D2/D3 updater, D4-D9 release tooling.** `Bwc.Update` is still a stub. NSIS and
   GnuPG are not installed, so D1/D8 need the user to install software. D2/D3 can be implemented
   and tested locally without signing.
3. **Manual checks owed by the user or a release VM:**
   - elevated hive load/save/unload round-trip;
   - remote connect, browse, and disconnect against a second machine or VM snapshot with the
     Remote Registry service;
   - elevated S4 suite and a clean x64 VM Program Files install;
   - 100% DPI oracle capture.
4. **D10 beta dry run** on the release VM after D1-D9.

## Environment facts

- Windows 11 25H2 ARM64, build 26200, display scale 200% (192 DPI). Native ARM64; x64 runs under
  emulation.
- .NET SDK 10.0.404 at `C:\Program Files\dotnet`; it is not on the shell PATH.
- Node 26.13.0. Refresh PATH before builds:
  `$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')`
- Set `MSBUILDDISABLENODEREUSE=1` and `DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1` for dotnet commands.
- NSIS and GnuPG are not installed. ImageMagick is at
  `C:\Program Files\ImageMagick-7.1.2-Q16-HDRI\magick.exe`; the icon source is `resources/bwc.png`.
- GUI tests share the desktop. Hold `Local\BwcGuiTestLock` around any GUI launch. Launch
  `highestAvailable` exes with `__COMPAT_LAYER=RunAsInvoker`; never trigger UAC.
- Test writes are limited to `HKCU\Software\BWC-E2E\<run-id>` in E2E, or the oracle key. Classic
  regedit may write `FindFlags` under its Applets key on exit; remove it if it was absent.
- Worker model used for implementation: `opencode-go/deepseek-v4.1-flash#max`, subagent `general`,
  through `/oversee`. The manager plans, briefs, audits, and runs the gate.

## Constraints

- Stay on `experimental`. Do not commit, amend, or push unless the user asks.
- Never add agent co-author trailers.
- Chat replies are caveman-terse; files, docs, and code are normal prose. No em dash (U+2014)
  anywhere. `node scripts/validate-no-em-dash.js --root .` checks the tree.
- AGENTS.md: no unit tests written after code; E2E-first with a repeatable artifact; for isolated
  tests, write `docs/failure-modes/<area>.md` first, then tests, then code.
- File ownership: `docs/decisions/0002-file-ownership.md`. The manager edits shared files
  (`.slnx`, `Directory.*.props`, `build/*.props`, `package.json`, `NativeMethods.txt`, exe-head
  `App.xaml`, theme dictionary, command tables, `.resw`) unless a brief grants one for a batch.
- Remote registry is read-only by design. Do not route `RegistryWriter` calls to a remote
  connection.
- Hive APIs need `SeBackupPrivilege`/`SeRestorePrivilege`. Do not elevate automatically.

## Key files and APIs

- `docs/parity/regedit-inventory.md`: 534-row parity backlog; all rows currently `open`.
- `docs/parity/oracle/`: resources, metrics, seed tree, three export goldens.
- `docs/failure-modes/hives.md`, `remote-registry.md`, `find.md`, `permissions.md`, `print.md`,
  `context-menus.md`: contracts for the newest lanes.
- `src/Bwc.Registry/RegistryWriter.cs` and `RegistryWriter.Hives.cs`: mutations and hives.
- `src/Bwc.Registry/Privileges/PrivilegeScope.cs`: SeBackup/SeRestore enable/revert.
- `src/Bwc.Registry/RemoteRegistryConnection.cs`, `RemoteRegistryException.cs`:
  `RegConnectRegistry` roots and own-computer validation.
- `src/Bwc.Registry/RegistryReader.cs`: local and remote root routing.
- `src/Bwc.Regedit/RegeditRootView.xaml.cs`: the command surface, dialogs, and tree wiring.
- `src/Bwc.Regedit/Commands/RegeditMenu.cs`: the full RT_MENU 103 command table.
- `src/Bwc.Regedit/Registry/RegistryTreeSource.cs`,
  `Registry/RemoteRegistryTreeSource.cs`, `Registry/RegistryValueSource.cs`.
- `src/Bwc.Regedit/Strings/en-US/Resources.resw`: visible strings.
- `tests/Bwc.Registry.Tests/`: 119 tests, including hives, security, and remote validation.
- `tests/Bwc.E2E/`: Shell, Editing, ImportExport, Find, Favorites, Hives, Remote, Permissions,
  Print, Hex, ContextMenus, Navigation, Controls.
- `scripts/test-all.js`, `scripts/dotnet-build.js`, `scripts/check-registry-writer.js`.

## Known gaps and declared differences

- Find uses `CurrentCultureIgnoreCase` as the approximation for regedit's `CompareStringEx`
  order. Find flags/text persist in the app settings file, not the classic `FindFlags` value.
- Find progress uses the Find dialog status and Cancel, not the separate progress dialog.
- `Search in subtree` is provisional; REG_LINK, binary, and REG_MULTI_SZ data matching is
  documented as provisional in `docs/failure-modes/find.md`.
- Menu item widths are about 41 DIP versus oracle 32.5 DIP. Row heights are exact.
- `RegistryPath` rejects components over 255 characters at parse time; the contract says pass
  through to Windows.
- `RegistryWriter.CopyTree` checks only the destination against the policy; the source is
  read-only.
- File-dialog E2E targets Windows 11 control IDs; long paths depend on OS policy.
- Remote registry is read-only. There is no remote hive, write, or permissions routing.
- Actual elevated hive load/save still needs a manual elevated run.
- Remote browsing still needs a second machine or VM snapshot.
- `Bwc.Update` is a stub. Installer/signing is blocked on NSIS/GnuPG.
- 100% DPI oracle capture is still owed; current metrics are 200% halves.

## Commands

```powershell
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = '1'
node scripts/test-all.js --rid win-arm64
```

Targeted checks:

```powershell
node scripts/check-registry-writer.js
node scripts/validate-no-em-dash.js --root .
dotnet test tests/Bwc.Registry.Tests/Bwc.Registry.Tests.csproj -c Release -p:BwcFlavor=E2E --output normal
dotnet test tests/Bwc.Core.Tests/Bwc.Core.Tests.csproj -c Release --output normal
dotnet test tests/Bwc.Registry.Shipping.Tests/Bwc.Registry.Shipping.Tests.csproj -c Release --output normal
```

Resume by reading this file, `resume.md`, and the plan at
`C:\Users\jsmith\.claude\plans\we-are-working-on-zany-pearl.md`, then start R15. Run the full gate
again before changing code if the tree may have moved.

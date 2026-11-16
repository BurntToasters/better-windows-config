# Update elevation failure modes (0.1.2, per-machine install)

Scope: `Bwc.Update` (`BwcUpdate.exe`) when the install is per machine (Program Files, admin-only). The medium
helper (`--apply`) verifies the staged installer as before, then asks Windows for an elevated copy of itself
(`--apply-elevated`). The elevated helper copies the installer into a protected folder, verifies the copy, and
starts it. `--launch-hub` starts the Hub at the shell's integrity level. `--close-running` also looks at other
sessions. Design: `install-design.md` (Fable audit) items 25 to 28, and the user's decisions: option B for the
apply, manual download for 0.1.1 users, all-users shortcuts.

Written before the tests and before the code, per AGENTS.md ("FIRST write all the ways it could fail, THEN
write the code"). Contract: `docs/contracts/update-cli.md`. The older rows (sections A to S) are in
`docs/failure-modes/update-verifier.md`; rows there that this file changes say so.

Terms:
- **Test** = the MSTest method in `tests/Bwc.Update.Tests`. `Doc` = a documented duty, or only an elevated or
  E2E run can show it (those go to the elevated runner tests of the installer E2E, I4).
- The "fake host" is `FakeHost` (`IUpdateProcessHost`). It never starts a process, never shows a prompt, and
  never uses the runas verb. The real host's runas call is compiled out of the E2E flavor and is never run by
  these tests.
- "Refusal" in the elevated helper means: nothing is installed, the protected copy is removed, and the Hub is
  started again at the shell's integrity level with `--update-result <UpdateFailure>`.

## ME. Medium apply (`--apply`) hands off to the elevated helper

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| ME1 | Every earlier check passes (not elevated, paths, receipt, wait for exit, re-verify with held handles) | The medium helper starts the installer itself, or starts nothing | The host is asked once to start `<helper folder>\BwcUpdate.exe --apply-elevated <staging folder>`. The installer is not started by this process. Outcome: failure `None`, `ElevatedHelperStarted` true, `InstallerStarted` false | `Apply_Starts_Elevated_Helper_Not_Installer` |
| ME2 | The elevated helper's command: the only argument is the staging folder | Caller data (install dir, version, switches, installer path) reaches the elevated process | Exactly `--apply-elevated <staging folder>`. The staging folder is the folder of the verified installer. Switches, version, and install dir of the request never appear | `Elevated_Helper_Command_Exact` |
| ME3 | The folder of the running `BwcUpdate.exe` is the install dir | A caller-chosen exe runs elevated | The exe path is `<host.HelperDirectory>\BwcUpdate.exe`, never built from `--install-dir` | `Elevated_Helper_Path_Is_Own_Folder` |
| ME4 | `--install-dir` is not the folder of the running helper | The wait and the Hub relaunch use one folder and the elevated helper another | Refuse `InstallDirUnsafe` before any prompt. Hub relaunched with the code | `Install_Dir_Not_Helper_Folder_Refused` |
| ME5 | Ship build: the helper's folder is not under Program Files | A user-writable `BwcUpdate.exe` is run elevated | Refuse `InstallDirUnsafe` before any prompt. Off only in the E2E flavor (`ProgramFilesRoot` null) | `Helper_Outside_Program_Files_Refused`, `Helper_Inside_Program_Files_Accepted` |
| ME6 | The user declines the consent prompt (Win32 error 1223, `ERROR_CANCELLED`) | The Hub is gone and nothing says why | Failure `ElevationDeclined`. Nothing was installed. Hub relaunched with `--update-result ElevationDeclined`. Exit 3 | `Elevation_Declined_Relaunches_Hub` |
| ME7 | The elevated helper cannot be started for another reason (Win32 error, missing exe) | Silent loss of the app | Failure `InstallerFailed`. Hub relaunched with `--update-result InstallerFailed` | `Elevated_Start_Failure_Relaunches_Hub` |
| ME8 | Any refusal before the elevated start (path, receipt, architecture, downgrade, same version, install dir, switch, still running, elevated) | An elevated helper starts after a bad check | No elevated start. Hub relaunched with the code (N3 and N10 of update-verifier.md still hold) | `Refusal_Relaunches_Hub_With_Message`, `Apply_Elevated_Refused`, every refusal test asserts no start |
| ME9 | The verified installer is held with `FileShare.Read` until the elevated helper has started | Swap between the check and the start | Write-open and delete fail while the host start runs (HT4 now covers the hand-off) | `Verified_Installer_Is_Locked` |
| ME10 | Exception after the elevated helper started | Hub relaunched while the install runs | No relaunch. `Unexpected`, `ElevatedHelperStarted` true | `Doc` (the exception before the start is `Unexpected_During_Elevated_Start_Relaunches_Hub`) |
| ME11 | Bad `--installer-switch` | Switch checks dropped because the switches are not forwarded | Still refused `InstallerArgumentRejected` (same rules as Q2 to Q4) | `Installer_Arg_Validation` |

## EA. Elevated helper (`--apply-elevated <staging folder>`)

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| EA1 | The token is not elevated (and there is no E2E root override) | A non-admin process writes the protected folder or runs an installer | Failure `NotElevated`. Nothing is created, copied, or started, and the Hub is not started. Exit 3 | `Not_Elevated_Does_Nothing` |
| EA2 | No staging folder, a relative folder, an option given twice, an unknown option | Ambiguous request | Usage error, exit 2 | `ApplyElevated_Arguments_Parse` |
| EA3 | `--install-dir`, `--current-version`, `--installer-switch`, `--wait-pid`, or any other option is given | The caller steers the elevated helper | Usage error, exit 2. The install dir is the helper's own folder, the current version is its own version | `ApplyElevated_Rejects_Caller_Options` |
| EA4 | The staged version is older than the helper's own version | Downgrade through a signed old release | Refuse `Downgrade`. No copy is made | `Elevated_Downgrade_Refused` |
| EA5 | The staged version equals the helper's own version | Reinstall loop | Refuse `SameVersion`. No copy is made | `Elevated_Same_Version_Refused` |
| EA6 | Prerelease ordering (`0.2.0-beta.1` over own `0.2.0`) | Wrong precedence | Uses `SemVer` precedence: refused as `Downgrade` | `Elevated_Prerelease_Of_Same_Core_Refused` |
| EA7 | The helper's own version cannot be read as SemVer | Fail-open comparison | Refuse `InvalidOptions` | `Elevated_Own_Version_Invalid_Refused` |
| EA8 | The staging folder is not `%TEMP%\BWC-Update\<32 lowercase hex>`: other root, other name, traversal, a file path, or a path outside the root | The elevated helper copies a file it was handed | Refuse `PathRejected`. Nothing is copied | `Elevated_Staging_Shape_Refused` |
| EA9 | The staging folder, `installer.exe`, `receipt.json`, or any entry in the folder is a reparse point (junction or symlink) | A planted link redirects the read | Refuse `PathRejected`. Nothing is copied | `Elevated_Staging_Link_Refused`, `Elevated_Staging_Entry_Link_Refused` |
| EA10 | `installer.exe` is larger than the receipt size (it grew or was replaced) | Unbounded copy into Program Files | Stop at size plus one byte. Refuse `InstallerTooLarge`. The partial copy is removed | `Elevated_Oversize_Installer_Refused` |
| EA11 | `installer.exe` is smaller than the receipt size | Truncated installer | Refuse `InstallerSizeMismatch`. The copy is removed | `Elevated_Short_Installer_Refused` |
| EA12 | `installer.exe` is open for write by another process when the copy starts | The source changes during the copy | The open fails: refuse `PathRejected`. Nothing is copied. (While the helper holds the source with `FileShare.Read`, no writer can open it.) | `Elevated_Source_Open_For_Write_Refused` |
| EA13 | The installer's bytes differ from the signed hash (same size) | A tampered file runs elevated | The copy fails verification: refuse `InstallerHashMismatch`. The copy is removed and no installer starts | `Elevated_Tampered_Installer_Refused` |
| EA14 | Valid hash, signature by an untrusted key | Forged installer | Refuse `InstallerSignatureInvalid` | `Elevated_Forged_Signature_Refused` |
| EA15 | Signed `version:` or `arch:` differs from the receipt | Replay of another release or architecture | Refuse `InstallerCommentMismatch`. Receipt architecture other than the helper's: `ArchitectureMismatch` | `Elevated_Comment_Mismatch_Refused`, `Elevated_Arch_Mismatch_Refused` |
| EA16 | The source file changes after the copy, before the start | The verified bytes are not the started bytes | Only the copy is verified and started. The started file is in the protected folder and its hash is the signed hash | `Elevated_Starts_Verified_Copy_Not_Source` |
| EA17 | The started path | The installer runs from a user-writable folder | `<protected root>\<32 hex>\installer.exe`, with a new folder name on every run. The protected root is `<install dir>.upd`, a sibling of the install dir | `Elevated_Starts_Protected_Copy`, `Protected_Root_Is_Sibling_Of_Install_Dir` |
| EA18 | The installer command line | The elevated helper forwards caller switches, or loses `/D=` | Exactly `/S /RELAUNCH /PARENTPID=<helper pid> /D=<install dir>`, `/D=` last and unquoted. Started by the host's plain start: no runas verb is part of the start call | `Elevated_Installer_Command_Line_Exact` |
| EA19 | The verified copy is held with `FileShare.Read` until the installer started | Swap of the copy between the check and the start | Write-open and delete fail while the host start runs | `Elevated_Copy_Is_Locked_During_Start` |
| EA20 | `<install dir>.upd` exists and is a reparse point, or is a file | Copy lands somewhere else | Refuse `PathRejected` | `Protected_Root_Link_Refused`, `Protected_Root_Is_File_Refused` |
| EA21 | The parent of the protected root does not exist | Creating folders under an unknown path | Refuse `InstallDirUnsafe` (the install dir itself must exist) | `Elevated_Install_Dir_Missing_Refused` |
| EA22 | Stale `<install dir>.upd\<32 hex>` folders from earlier updates | 330 MB per update left behind | Removed before the new copy. Only 32-lowercase-hex folder names. Files, other names, and uppercase names are left alone | `Stale_Removal_Only_Hex_Folders` |
| EA23 | A stale folder is a junction or holds a junction to a folder outside | Delete through a link | The link entry is not followed. A junction as the stale folder itself is left in place. A junction inside is removed as a link only. The target's files remain | `Stale_Removal_Never_Follows_Links` |
| EA24 | A stale folder holds a file that is in use | One locked file stops every update | That folder is skipped. The update goes on with a new folder | `Stale_Locked_Folder_Skipped` |
| EA25 | The installer does not start (Win32 error) | The Hub is gone, the copy is left | Refuse `InstallerFailed`. The copy is removed. Hub relaunched with the code | `Elevated_Installer_Start_Failure_Relaunches_Hub` |
| EA26 | Any refusal | The copy stays in Program Files | The new copy folder is removed. Older stale folders are handled by EA22 | `Elevated_Refusal_Removes_Copy` |
| EA27 | A refusal relaunches the Hub | An elevated Hub, or no Hub | The Hub is started through `StartAtShellIntegrity` (not `StartDetached`) from the helper's own folder with `--update-result <code>` only | `Elevated_Refusal_Relaunches_Hub_At_Shell_Integrity` |
| EA28 | The Hub cannot be de-elevated (no shell window) | Falling back to an elevated Hub | Nothing is launched. Failure `HubMissing`, detail names the original failure. Exit 6 | `Elevated_Refusal_Cannot_DeElevate_Launches_Nothing` |
| EA29 | `__COMPAT_LAYER` in the environment | Leaks to the installer | Removed before any child starts | `Elevated_Compat_Layer_Removed` |
| EA30 | Unexpected exception before the installer starts | The user has no app | `Unexpected`, copy removed, Hub relaunched at shell integrity, exit 7 | `Elevated_Unexpected_Relaunches_Hub` |
| EA31 | Unexpected exception after the installer started | A second Hub while the installer runs | No relaunch. `installerStarted` true, `Unexpected` | `Doc` (same rule as S18) |
| EA32 | Ship: the helper's own folder is not under Program Files | A user-writable `BwcUpdate.exe` elevated by hand | Refuse `InstallDirUnsafe`, nothing copied. Off only in the E2E flavor | `Elevated_Outside_Program_Files_Refused` |
| EA33 | Standard user with an administrator's credentials (over the shoulder): the elevated process has the administrator's `%TEMP%` | The staging folder the medium helper names is not under the elevated helper's temp | Refuse `PathRejected`. Updating needs the signed-in user to be an administrator in 0.1.2 (documented in docs/installer.md by I1) | `Elevated_Staging_Shape_Refused` (the root is checked against the elevated process's own temp), `Doc` |
| EA34 | The medium helper exits while the elevated helper is still copying | The installer's `--wait-pid` waits on a different pid than the lock holder | The installer waits for `/PARENTPID` (the elevated helper). The medium helper has already exited before the prompt returns control (it exits right after the start returns). Residual risk: a few milliseconds | `Doc` |

### E2E flavor only

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| EE1 | E2E build, `BWC_E2E_UPDATE_PROTECTED_ROOT` names a folder strictly inside the helper's `%TEMP%` (not the temp itself, not the staging root, not inside it) | A non-elevated run writes outside the run's temp | The token need not be elevated. The protected root is that folder, not `<install dir>.upd` | `E2E_Root_Accepted_Inside_Temp`, `Non_Elevated_With_E2E_Root_Proceeds` |
| EE2 | The variable names the temp itself, the staging root, a folder inside the staging root, a relative path, a path outside the temp, or a `..` path | The E2E override reaches a real folder | Not accepted. `--apply-elevated` with a bad override refuses `InvalidOptions` before anything is created; the seam ignores a bad variable, so a non-elevated token is then refused (`NotElevated`) | `Non_Elevated_With_Bad_E2E_Root_Refused`, `E2E_Root_Rule` |
| EE3 | The E2E start of the elevated helper | A UAC prompt in a test | The E2E flavor starts `--apply-elevated` with a plain start and never sets a verb. The runas call is compiled out of the E2E build, and the plain start is compiled out of ship | `Doc` (`#if BWC_E2E` in `WindowsUpdateProcessHost.StartElevatedHelper`; the Hub update E2E drives it) |
| EE4 | The variable in a ship build | A switch that lets a non-admin run the elevated path | The seam is compiled only with `BWC_E2E`. Ship reads no such variable | `Doc` (`UpdateE2ESeam` under `#if BWC_E2E`; the Program.cs call is also under `#if BWC_E2E`) |
| EE5 | Hub update E2E (stand-in installer) | The two-step path breaks the existing test | The same test passes: stand-in ran from `<temp>\BWC-Protected\<32 hex>\installer.exe`, with the signed bytes, the contract's command line, after the Hub exited, and the Hub came back | `Hub_UpdateAvailable_InstallRunsVerifiedInstaller` (E2E) |

## LH. `--launch-hub [--update-result <code>]`

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| LH1 | Elevated token | The installer's Exec starts an elevated Hub | The host's shell-integrity start is used (desktop shell, `IShellDispatch2.ShellExecute`), not the plain start | `Launch_Elevated_Uses_Shell_Integrity` |
| LH2 | Not elevated (the E2E flavor, or the Hub's own test) | A needless shell round trip | The shell-integrity call of the host handles the normal start (the real host starts the Hub normally when the token is not elevated) | `Launch_Not_Elevated_Starts_Hub` |
| LH3 | Elevated and the shell cannot be reached (no Explorer, COM failure) | Falling back to an elevated Hub | Nothing is launched. Failure `DeElevationFailed`, exit 8 | `Launch_Cannot_DeElevate_Launches_Nothing` |
| LH4 | `<own folder>\BetterWindowsConfig.exe` is missing | Crash | Failure `HubMissing`, exit 6 | `Launch_Hub_Missing_Reported` |
| LH5 | `--update-result` is `None`, a number, an unknown name, has spaces or quotes, or is given twice | A command line injection into the shell call | Usage error, exit 2. Only an exact `UpdateFailure` name other than `None` is accepted | `LaunchHub_Update_Result_Validation` |
| LH6 | The Hub path | A caller-chosen exe starts at the user's integrity | Always `<helper folder>\BetterWindowsConfig.exe`; no path option exists. The arguments are only `--update-result <code>` | `Launch_Hub_Path_And_Arguments_Exact` |
| LH7 | The shell call throws, or reports a start that did not happen | Exit 0 with no Hub | Mapped to `CannotDeElevate` by the host; exit 8 when elevated | `Doc` (real host; elevated runner test) |
| LH8 | Unknown option, a value-less `--update-result`, an extra argument | Silent ignore | Usage error, exit 2 | `LaunchHub_Arguments_Parse` |

## NL. No file log in elevated modes

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| NL1 | `--apply-elevated` | `BWC_DATA_ROOT` pointed at a junction gives an elevated write to an arbitrary folder | The file log is never initialized in this mode, with or without elevation. Results go to stdout and the exit code | `Logging_Policy_Matrix` |
| NL2 | `--launch-hub`, `--close-running`, `--wait-pid` with an elevated token (or a token that cannot be read) | Same | No file log when elevated. Not elevated: logged as before | `Logging_Policy_Matrix` |
| NL3 | `--check`, `--download`, `--apply` with an elevated token | Same | No file log (all modes follow the same rule; `--apply` refuses when elevated anyway) | `Logging_Policy_Matrix` |
| NL4 | The token cannot be queried | Fail open | Counts as elevated: no log | `Doc` (`ProcessIdentity` fails closed) |
| NL5 | An unexpected exception in an elevated mode | A crash log write | Printed as one JSON line only, exit 7. `Log` is not initialized, so its calls only trace | `Doc` |

## SS. Close-running across sessions

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| SS1 | A BWC process from the install dir runs in another session | The installer replaces files that an invisible process holds | `--close-running` fails before any change: no `WM_CLOSE` is sent to any process, exit 4, and each such process is printed as `RunningInOtherSession session=<id> <name> pid=<pid>` | `CloseRunning_Other_Session_Fails_Before_Any_Close` |
| SS2 | A BWC-named process of another session runs from another folder | A needless failure | Ignored | `CloseRunning_Other_Session_Other_Folder_Ignored` |
| SS3 | The other-session process is in `--exclude-pid`, or is this process | Blocks the installer's own helpers | Left out, as in the current session | `CloseRunning_Other_Session_Excluded_Pid_Ignored` |
| SS4 | Processes in this session only | A regression of today's behavior | Closed as today (WM_CLOSE once per process, wait, exit 0 or 3) | `CloseRunning_Current_Session_Still_Closed` |
| SS5 | The session of a process cannot be read | Fail open | Counted with this session's processes (S10), not as "other session" | `Unknown_Session_Counts_As_Running` (existing), `CloseRunning_Unknown_Session_Counts_As_Current` |
| SS6 | The other-session scan lists a pid more than once or the process exits during the scan | Duplicate or stale lines | Each pid once. A process that exited is not listed | `CloseRunning_Other_Session_Lists_Each_Pid_Once` |
| SS7 | The medium `--apply` and an other-session process | A consent prompt, then a failed install | `Doc`: the medium helper waits only for this session (S9 unchanged). The installer's elevated close step reports the other session (SS1) before it changes anything, and the Hub relaunches with `InstallerFailed` | `Doc` |

## CD. Codes and parsing

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| CD1 | New failure codes exist and have the names the Hub and installer read: `ElevationDeclined`, `NotElevated`, `RunningInOtherSession`, `DeElevationFailed` | A typo reaches `--update-result` | The names are exactly these, and every name parses back through `LaunchHub_Update_Result_Validation` | `Failure_Names_For_Elevation` |
| CD2 | Own version text with build metadata (`0.1.2+abc`) or a prerelease | Parse fails on the informational version | The metadata is dropped, the prerelease kept | `Helper_Version_Strips_Metadata` |
| CD3 | Program Files rule: sibling prefix (`C:\Program Files X`), another drive, case | A look-alike folder passes | Folder must be inside Program Files at any depth, compared without regard to case | `Program_Files_Rule` |
| CD4 | Help text | A mode missing from the usage | Lists `--apply-elevated` and `--launch-hub` | `Help_Lists_Modes` |

# Update elevation failure modes (0.1.2, per-machine install)

Scope: `Bwc.Update` (`BwcUpdate.exe`) when the install is per machine (Program Files, admin-only). The medium
helper (`--apply`) verifies the staged installer as before, then asks Windows for an elevated copy of itself
(`--apply-elevated`). The elevated helper copies the installer into a protected folder, verifies the copy, and
starts it. `--launch-hub` starts the Hub at the shell's integrity level. `--close-running` also looks at other
sessions. Design: `install-design.md` (Fable audit) items 25 to 28, and the user's decisions: option B for the
apply, all-users shortcuts. 0.1.2 has no 0.1.1 migration: a 0.1.1 install is uninstalled by hand.

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
| ME12 | The elevated helper's console window is visible during the apply (a `cmd`-style window appears with no explanation) | The user sees a stray console window, may close it, and stops the update | The `runas` start is made with `WindowStyle` Hidden, so ShellExecuteEx passes SW_HIDE and the helper's console starts hidden. The consent prompt, the verb, the working directory, the arguments, and the cancel handling (ME6) are unchanged | `Doc` (a visible run shows whether the window is hidden; no unit test can see a window style) |

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

## SA. `--set-aumid <shortcut> <app id>` (I5, security review 2, finding 1)

Written before the tests and the code. The installer used to write the AppUserModelID with an elevated PowerShell, which loads
modules from the user's `DocumentsWindowsPowerShellModules` and compiles with `csc` in `%TEMP%`. The installer now runs this
mode of the installed, NativeAOT `BwcUpdate.exe` instead (installer.md M45). The command is positional:
`BwcUpdate.exe --set-aumid "<absolute path of a .lnk>" <app id>`. It sets `System.AppUserModel.ID` (PKEY_AppUserModel_ID) through
`IPropertyStore` with manual COM, saves the shortcut, loads it again, and compares. Exit codes: 0 written and read back, 2 usage,
3 refused (the rules below), 4 not written (COM failure or the read-back differs), 7 unexpected. One JSON line on stdout.

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| SA1 | Missing value, a third value, an option-like value (`--x`), or a repeated mode | A silent default, or an injection | Usage error, exit 2 | `SetAumid_Arguments_Parse` |
| SA2 | The shortcut path is relative, has a quote or a control character, has a `..` or `.` part or a doubled separator (it differs from its normalized form), is a UNC or device path, or does not end in `.lnk` | A write to a file the caller chose | Usage error, exit 2 | `SetAumid_Arguments_Parse`, `SetAumid_Path_Shape` |
| SA3 | The app id is not exactly `BetterWindowsConfig.Hub` or `BetterWindowsConfig.Regedit` | Any text written into any shortcut | Usage error, exit 2 | `SetAumid_Arguments_Parse` |
| SA4 | The file name and the id do not belong together (`Better Windows Config.lnk` takes the Hub id, `Registry Editor (BWC).lnk` the Regedit id) or the name is another one | The helper edits any shortcut in the Start menu | Refused `PathRejected`, exit 3, nothing written | `SetAumid_Name_And_Id_Rule` |
| SA5 | Ship: the shortcut is not directly in the all-users Programs folder (`SHGetKnownFolderPath(FOLDERID_CommonPrograms)`, not an environment variable). E2E flavor only: also directly in a folder below the process's own temp folder (the per-user sandbox), below the user's own temp folder (`%LOCALAPPDATA%\Temp`, because the installer runs the helper with TEMP and TMP set to its protected copy folder, HT1), below `%ProgramFiles%BWC-E2E` (the Elevated tests' sandbox), or directly in `<all-users Programs>BWC-E2E-<id>` (the `/MACHINE` sandbox) | A file in the user's own Start menu, or anywhere else, is edited by an elevated process | Refused `PathRejected`, exit 3, nothing written. The E2E rule is compiled only under `BWC_E2E` (the ship binary holds none of those folder names) | `SetAumid_Ship_Scope_Is_Common_Programs`, `SetAumid_E2E_Scope`, publish audit |
| SA6 | The shortcut is missing, is a folder, or is a reparse point, or its folder is a reparse point | A link redirects the write | Refused `PathRejected`, exit 3. A link is never followed | `SetAumid_Files_Refused` |
| SA7 | An 8.3 spelling or a junction in the path | The compare is fooled | The path must equal its normalized form (SA2) and its folder must be the allowed folder by exact comparison, so a short name or a junction never matches | `SetAumid_Path_Shape` |
| SA8 | The process is elevated | An elevated write to the log folder (`BWC_DATA_ROOT`) | No file log when elevated, like the other modes (NL2). The result is the JSON line and the exit code | `Logging_Policy_Matrix` |
| SA9 | COM fails, the shortcut is locked, or the value read back differs | A success that is not one | Exit 4, `Io`, the detail names the step. The installer logs a warning and goes on (installer.md M46) | Doc (real COM), E2E for the success path |
| SA10 | An unexpected exception | A crash with no answer | One JSON line, exit 7, nothing else written | Doc |
| SA11 | The mode must not elevate by itself | A prompt | It never asks for elevation and never starts a process. It runs at the token it was given | Doc (no start code in the mode) |
| SA12 | The shortcut keeps its other properties | The target, arguments, icon, or working directory change | Only the AppUserModelID is set. The user-sandbox E2E reads the AUMID back and the Elevated test reads both shortcuts | E2E (`update.a.aumid`), Elevated (`fresh.shortcuts.*`) |

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

## HD. Hardening of the elevated path (0.1.2, security review of the first elevated-apply commit)

Written before the tests and the code of the hardening piece (I2b). The review's threat: a medium-integrity process of the
same user, or of another standard user, gets code run elevated, or makes elevated code write or delete where it should not.
Test names are in `tests/Bwc.Update.Tests` unless marked. "Elevated" means only the user's elevated runner can show it (a real
UAC token). "Doc" is a documented duty.

### HE. Environment-injected code in the elevated helper (review finding 1)

`DOTNET_STARTUP_HOOKS`, `CORECLR_ENABLE_PROFILING` with `CORECLR_PROFILER` and `CORECLR_PROFILER_PATH`, and `COREHOST_TRACE`
with `COREHOST_TRACEFILE` run code or append files inside a .NET process before `Main`. A medium process can put them in
`HKCU\Environment`, and the elevated helper (started with the runas verb) may inherit that environment.

**Closed by NativeAOT (I6).** `BwcUpdate.exe` is a NativeAOT executable in both flavors and both rids. It has no `hostfxr`, no
`hostpolicy`, no CoreCLR, and no startup hook provider, so none of those variables is read. The first NativeAOT attempt was
reverted because this machine had no ARM64 C++ build tools and the helper used classic COM interop (`[ComImport]`,
`InvokeMember`). The build tools are installed now, and the helper uses typed vtable calls (section HN). HE2 to HE4 stay as a
second layer: they scrub what the helper hands to the installer and to the helpers the installer starts.
| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| HE1 | Startup hooks | `DOTNET_STARTUP_HOOKS` loads an assembly in the elevated helper | A NativeAOT exe has no startup hook provider, so the variable is not read. `Bwc.Update.csproj` still sets `StartupHookSupport` false (harmless). The publish folder holds no `BwcUpdate.dll`, `BwcUpdate.deps.json` or `BwcUpdate.runtimeconfig.json`, in either flavor, and the publish audit refuses a folder that does (a managed `BwcUpdate.dll` beside the exe would mean a CoreCLR build) | `release-selftest` (`native_aot_probe`), publish audit, E2E `Hub_Helper_Ignores_Runtime_Variables` |
| HE2 | The medium `--apply` starts the elevated helper | The elevated process inherits `DOTNET_*`, `COREHOST_*`, `CORECLR_*`, `COMPlus_*` | Before the runas start the medium helper removes every variable of those four prefixes from its own process environment. The runas path (`UseShellExecute` true) takes no custom environment block (`ProcessStartInfo.Environment` is only honored with `UseShellExecute` false), so the process environment is the only lever. Windows hands the caller's environment to the elevated process, so this closes the hole unless the elevation service rebuilds the block from the registry. **That cannot be shown unelevated** | `Medium_Apply_Scrubs_Runtime_Variables_Before_Elevated_Start`, `Runtime_Variable_Rule`, Elevated (does the elevated process get the block) |
| HE3 | The elevated helper starts the installer | The same variables reach the installer and the helpers it starts | The installer gets an environment block without those variables (and without `__COMPAT_LAYER`). The same scrub runs at the start of `--apply-elevated`, so the `BwcUpdate` modes the installer starts do not inherit them. It cannot undo what ran before `Main` | `Elevated_Installer_Environment_Scrubbed`, `Elevated_Scrubs_Runtime_Variables_From_Process` |
| HE4 | Variable names in other case, or a lookalike prefix | A name Windows treats as the same variable slips through, or an unrelated variable is removed | The match is case-insensitive on `DOTNET_`, `COREHOST_`, `CORECLR_`, `COMPlus_`. `DOTNETX` and `MYDOTNET_X` are not touched | `Runtime_Variable_Rule` |
| HE5 | The profiler and the host trace | Code, or a file write, in the elevated helper before `Main` | Closed: the exe is NativeAOT, so `CORECLR_ENABLE_PROFILING`, `CORECLR_PROFILER*`, `COREHOST_TRACE`, and `COREHOST_TRACEFILE` do nothing. The publish audit proves the exe is native (HN9). The E2E test starts the shipped E2E binary with the variables set, the trace file path in the test's temp folder, and the profiler path at a file that does not exist: the helper prints its usage, exits 0, and no trace file is created (a CoreCLR apphost creates one). **Only an elevated run shows that the elevated process gets the same result** | HN9, E2E `Hub_Helper_Ignores_Runtime_Variables`, Elevated (same binary) |

### HT. Temp folder and uninstaller of the elevated installer (review finding 2)

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| HT1 | The elevated installer inherits the user's `%TEMP%` | NSIS unpacks its plugins folder (`$PLUGINSDIR`, where the System plugin DLL is loaded from) into a user-writable folder | `StartInstaller` is given a temp folder, `<protected root>\<32 hex>\tmp`, created with the protected ACL (HP1). The installer's `TEMP` and `TMP` both name it. No other process of the user can create or change anything there | `Elevated_Installer_Gets_Protected_Temp` |
| HT2 | The temp folder cannot be created | A fallback to the user's temp | Refuse `InstallerFailed`. Nothing is started, the copy is removed, the Hub is relaunched | `Elevated_Temp_Folder_Failure_Refused` |
| HT3 | The temp folder is removed with the copy | Leftovers in Program Files | It is inside the copy folder, so every refusal, and the next run's stale removal (EA22), removes it | `Elevated_Refusal_Removes_Copy` (also checks `tmp`) |
| HT4 | The uninstaller runs from a copy in `%TEMP%` (the NSIS default) | An elevated program started from a user-writable folder | `UninstallString` and `QuietUninstallString` end in `_?=$INSTDIR`, so the uninstaller runs in place from the admin-only install folder. See installer.md M37 | installer.md M37, `test-installer-e2e.js` |

### HP. Protected root and the copy handle (review finding 3)

The principals are the **Administrators** group and **SYSTEM** in the ship build. The E2E sandbox root
(`BWC_E2E_UPDATE_PROTECTED_ROOT`) uses the same logic with the **current user** (and SYSTEM) in place of Administrators, so it is
testable unelevated. Tests give the principals in the request (`ProtectedAcl`). A request with none gets the ship set.

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| HP1 | Creating `<install dir>.upd` | The folder inherits Program Files rules and is readable, and its files openable with share 0, by users | Created with an explicit DACL: full control for the principals only, inheritance from the parent off (protected), owner Administrators (E2E: the user). No other entry. The copy folder and the temp folder get the same rule | `Root_Is_Created_Protected`, `Copy_Folder_Is_Created_Protected`, `Ship_Principals_Are_System_And_Administrators` |
| HP2 | The root exists and its owner is not a principal | A folder planted by another user | Refuse `PathRejected`. Nothing is copied | `Existing_Root_With_Foreign_Owner_Refused`, `Acl_Rule_Matrix` |
| HP3 | The root exists and its DACL grants write (data, append, attributes, delete, change permissions, take ownership, generic write) to anyone but the principals and TrustedInstaller, on this folder | Another user adds files or replaces the copy | Refuse `PathRejected`. A read-only entry for other users is accepted. An inherit-only entry and a deny entry do not count | `Existing_Root_With_Foreign_Write_Refused`, `Existing_Root_Read_Only_Entry_Accepted`, `Acl_Rule_Matrix` |
| HP4 | The root exists from an earlier run of this code | A second set of rules | A root this code created passes HP2 and HP3, so the next update reuses it | `Existing_Protected_Root_Is_Reused` |
| HP5 | The root is a reparse point or a file | Writes land elsewhere | Never followed. Refuse `PathRejected` (EA20 stays) | `Protected_Root_Link_Refused`, `Protected_Root_Is_File_Refused` |
| HP6 | The ACL cannot be applied or read | A silent fallback to an inherited ACL | Refuse `PathRejected` (`Io` for an IO error). No unprotected fallback | `Root_Acl_Failure_Refused` |
| HP7 | The copy file is closed after the write and opened again | A user opens `installer.exe` with share 0 in the gap (a denial of service), or a write lands between write and verify | One writer handle (`FileShare.Read`) writes and flushes to disk. While it is open, a reader (`FileShare.ReadWrite`, so the writer coexists) is opened and the copy is verified through it. A second reader (`FileShare.Read`) is opened while the first is held, its size and SHA-256 are compared with the verified values, and only then are the first two closed. That reader stays open through the start. There is never a moment without a handle of ours, and the bytes started are the bytes verified | `Copy_Handles_Are_Held_Through_Start`, `Copy_Changed_Between_Handles_Refused` |
| HP8 | A write or delete of the copy while the installer starts | A swap at the last moment | Blocked by the held reader (EA19 stays) | `Elevated_Copy_Is_Locked_During_Start` |
| HP9 | A stale folder that another user holds open | Folders of ours that never go away | With HP1 no other user can open them. A folder still in use stays (EA24) | `Stale_Locked_Folder_Skipped` |

### HC. Close-running and other sessions (review finding 4)

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| HC1 | `--close-running` runs elevated and the token holds `SeDebugPrivilege` | A process of another user cannot be inspected, so a look-alike blocks the install | The privilege is enabled (only if held) before the scan and put back to its earlier state afterwards, also when the scan throws | `CloseRunning_Enables_Debug_Privilege_Around_Scan`, `Privilege_Scope_Restores_State` |
| HC2 | The token does not hold it (a medium token) | A failure of the command | Nothing is enabled. The scan runs as before. Not an error | `CloseRunning_Without_Privilege_Still_Scans`, `Privilege_Not_Held_Is_Not_Enabled` |
| HC3 | An image that still cannot be read | Fail open, or a block by a look-alike | A process counts as running from the install dir only when its **name matches and it has no readable path** (fail closed, as before). A readable path outside the folder never counts. Trade-off: a protected process, or a run without the privilege, that carries a BWC name stops the install until it exits; the message names the process and its session | `Unreadable_Image_With_Matching_Name_Counts`, `Readable_Image_Elsewhere_Does_Not_Count` |
| HC4 | The non-elevated modes | Needless privilege use | Only `--close-running` with an elevated token enables it. `--wait-pid` and the medium modes never do | `CloseRunning_Not_Elevated_Does_Not_Enable_Privilege` |

### HK. `--check`, `--download`, and the staging cleanup (review finding 5)

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| HK1 | `--check` or `--download` runs elevated | The network and the staging root (a user-writable folder) are used by an elevated process | Refused like `--apply`: failure `RunningElevated`, exit 1, in the JSON shape of the mode. Nothing is fetched or written. An unreadable token counts as elevated | `Elevation_Gate_Refuses_Check_And_Download`, E2E `Helper_Check_Refuses_When_Forced_Elevated` |
| HK2 | `StagingCleanup` is given a staging root that is a reparse point | A prune deletes 32-hex folders in the link's target | Nothing is walked or removed. Every level is checked: the root, each staging folder, and each entry inside (a link inside a staging folder is removed as a link, never entered) | `Prune_Root_Link_Refused`, `Discard_Root_Link_Refused`, `Prune_Inner_Link_Not_Followed` |
| HK3 | The staging root cannot be inspected | Fail open | Treated as a link: nothing is removed | `Prune_Unreadable_Root_Refused` |

### HH. The Hub is already elevated (review finding 6)

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| HH1 | The Hub runs elevated (UAC off, the built-in Administrator) and the user checks for updates | A confusing generic message, or an elevated Hub that downloads | `--check` and `--download` refuse (HK1). **Chosen: the elevated Hub cannot update**, and it says so: the Hub maps `RunningElevated` to its own sentence ("Updates are not checked while Better Windows Config runs with administrator rights..."), not the generic "could not be verified" text. A direct hand-off to `--apply-elevated` was considered and not done: the download must run unelevated (it writes a user-writable staging root), so there is no verified staged installer to hand over | `Elevation_Gate_Refuses_Check_And_Download`, Hub string audit (`validate`) |
| HH2 | `--apply` runs elevated | Unchanged | Still `RunningElevated`, the Hub is relaunched with the code, and the Hub shows the existing `UpdateResultElevated` text | Existing tests |

### HR. Program Files rule (review finding 7)

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| HR1 | The dir or the Program Files path makes `Path.GetFullPath` throw (embedded null, too long) | `IsUnder` returned true, because the shared rule counts a path it cannot normalize as inside | `ProgramFilesRule.IsUnder` normalizes itself and returns false on any exception. The shared running-process rule is not changed | `Program_Files_Rule_Fails_Closed_On_Bad_Path` |
| HR2 | Dot parts, case, a sibling prefix (`C:\Program Files X`), another drive | A look-alike passes | Unchanged (CD3) | `Program_Files_Rule` |

### HI. Installer trusted comment (review finding 8)

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| HI1 | The installer comment lacks `product:bwc`, or names another product | A signed installer of another product, made with the same key, is accepted | Refuse `InstallerCommentMismatch` | `Installer_Product_Missing_Rejected`, `Installer_Product_Wrong_Rejected` |
| HI2 | The installer comment lacks `channel:`, or it differs from the channel of the manifest and receipt | A beta installer replayed on the stable channel | Refuse `InstallerCommentMismatch` | `Installer_Channel_Missing_Rejected`, `Installer_Channel_Mismatch_Rejected` |
| HI3 | Release signing | The scripts do not write the new fields | `scripts/assemble-bundle.js` signs `version:<v> arch:<rid> product:bwc channel:<c>`. The validator in `scripts/lib/update-manifest.js` requires both fields and the channel match. The 0.1.1 verifier reads only `version`, `arch`, and `file`, and ignores other keys, so a bundle signed this way is still readable by 0.1.1 | `release-selftest`, `release-assets-selftest` |
| HI4 | The elevated helper | The new fields are checked only on the medium side | The copy is verified with the same expectation (channel from the receipt, product `bwc`) after the copy | `Elevated_Comment_Mismatch_Refused`, `Elevated_Channel_Mismatch_Refused` |

### HB. Ship and E2E code (review finding 9)

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| HB1 | The non-elevated branch of `ElevatedApplier` (E2E root, no token) in a ship build | Dead code that a request could reach | Compiled only under `BWC_E2E`. In ship, a request with an E2E root and a non-elevated host is `NotElevated` | `E2E_Root_Ignored_In_Ship` (ship test run), `Non_Elevated_With_E2E_Root_Proceeds` (run with `-p:BwcFlavor=E2E`) |
| HB2 | `DefineConstants` in the environment of the build | `BWC_E2E` injected into a ship build | `scripts/dotnet-build.js` removes `DefineConstants` (any case) from the environment it gives MSBuild | `release-selftest` (`msbuild_environment_scrubbed`) |
| HB3 | A ship `BwcUpdate` binary that contains an E2E variable name | The seam is in the ship build | The publish audit searches `BwcUpdate.exe` (the only file with code since NativeAOT, HN15) of a ship folder for `BWC_E2E_UPDATE_` as UTF-8 and UTF-16LE and fails when found. It runs where the ship publish is audited (`dotnet-build.js`, `test-all.js`, `test-installer-e2e.js`) | `release-selftest` (`ship_e2e_strings`), publish audit |
| HB4 | The E2E force-elevated seam (`BWC_E2E_UPDATE_FORCE_ELEVATED=1`) | A switch that lets a ship run say it is elevated | Compiled only with `BWC_E2E`, in `ProcessIdentity`. It makes the E2E helper behave as elevated (it elevates nothing), so the refusals can be driven without a real token | HB3 (the name is under the E2E prefix), E2E `Helper_*_Forced_Elevated` |

### HN. NativeAOT helper (I6)

Written before the tests and the code. `BwcUpdate.exe` is published with `PublishAot` in both flavors, so the elevated process
runs no .NET host and no JIT. Three things stop working under NativeAOT and are replaced: reflection-based JSON on anonymous
types (`Program.cs`), classic COM interop (`[ComImport]`, `Activator.CreateInstance(Type.GetTypeFromCLSID)`, `InvokeMember`),
and the managed-assembly proofs of the publish audit (`BwcUpdate.dll`, its `runtimeconfig.json`, the `BwcFlavor` attribute).
The COM calls are typed vtable calls through function pointers, with a `CoCreateInstance` P/Invoke. Nothing is looked up by
name at run time.

**JSON is a contract.** The Hub and the installer parse the stdout lines, so the bytes must not change.

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| HN1 | The result lines of `--check`, `--download`, `--apply`, `--apply-elevated`, `--launch-hub`, `--set-aumid`, and an unexpected error in the other modes | A source-generated or hand-written writer changes a name, the order, the casing, or adds a space | Byte-identical to the reflection output of the 0.1.2 code: no spaces, camelCase names in the order of the contract (`ok`, `failure`, `detail`, then the mode's fields) | `Stdout_Check_Json_Is_Pinned`, `Stdout_Download_Json_Is_Pinned`, `Stdout_Apply_Json_Is_Pinned`, `Stdout_Simple_Json_Is_Pinned` (written and run against the old code first) |
| HN2 | A null `version`, `notesUrl`, or `stagingFolder` | The field is dropped, and the Hub reads a missing property | Written as `null`, as before | `Stdout_Check_Json_Is_Pinned`, `Stdout_Download_Json_Is_Pinned` |
| HN3 | `detail` or a URL holds a quote, a backslash, `<`, `>`, `&`, `'`, `+`, a control character, or text outside ASCII | A different escape (`<` against `<`, a raw non-ASCII character) breaks a parser or changes the bytes | The default encoder rules of `System.Text.Json`: those characters (and every non-ASCII one, as a UTF-16 pair) are written as `\uXXXX` with upper-case hex (`"` is `"`), a backslash is `\\`, and a tab is `\t`. `/` is not escaped. The line is ASCII, so the console code page does not matter | `Stdout_Json_Escaping_Is_Pinned` |
| HN4 | Every `UpdateFailure` name in `failure` | An enum name is trimmed or renamed | The name is the member name, for all members | `Stdout_Json_Failure_Names_Are_Stable` |
| HN5 | The `--download` progress line `{"progress":{"received":<n>,"total":<n>}}` | A nested object is flattened or renamed | Pinned, including a `total` of `-1` | `Stdout_Progress_Json_Is_Pinned` |
| HN6 | `--launch-hub` result and the unexpected-error line | A shape drifts in a mode no test reads | Pinned: `{"ok":..,"failure":"..","detail":".."}` | `Stdout_Simple_Json_Is_Pinned` |
| HN7 | A type is missing from the serializer context | A run-time failure only on one path, in the elevated process | There is no run-time lookup: the context declares each line type, and a type that is not declared does not compile. The tests above write every line type | `Stdout_*` tests, build |
| HN8 | A COM interface pointer is not released, or is released twice | A leaked shell object in the elevated process, or a crash from a double release | Each pointer is owned by one `ComObject`, released once when it is disposed (a second dispose does nothing). Every success path and every failure path (an `HRESULT` that fails, an out pointer that is null, an exception) releases all pointers that were acquired | `ComObject_Dispose_Releases_Once`, `ComObject_QueryInterface_Failure_Returns_Nothing`, `ComObject_QueryInterface_Adds_One_Reference`, `Shell_Chain_Releases_Every_Pointer` |
| HN9 | The shell chain of `--launch-hub` when elevated: `IShellWindows.FindWindowSW`, `IServiceProvider.QueryService(SID_STopLevelBrowser)`, `QueryActiveShellView`, `GetItemObject(SVGIO_BACKGROUND)`, `QueryInterface(IShellFolderViewDual)`, `get_Application`, `QueryInterface(IShellDispatch2)`, `ShellExecute` | A wrong vtable slot, a wrong argument, or a skipped step starts the Hub elevated, starts the wrong file, or crashes | Each call is made with the arguments of the old late-bound code: location variant `VT_I4` 0, root `VT_EMPTY`, class `SWC_DESKTOP`, options `SWFO_NEEDDISPATCH`; `GetItemObject` with item `SVGIO_BACKGROUND` and `IID_IDispatch`; `ShellExecute(file, args joined by a space, working directory, "open", 1)` with BSTRs that are freed afterwards. The slot numbers are pinned against the SDK headers. A fake COM object counts the calls | `Shell_Chain_Calls_Are_Exact`, `Shell_Chain_Slots_Match_Sdk`, E2E `Hub_Helper_LaunchHub_Forced_Elevated_Uses_Shell` (the real shell, from the E2E flavor with the force-elevated seam), Elevated (`launch.medium`) |
| HN10 | Any step of the chain fails: `CoCreateInstance` fails, `FindWindowSW` returns a failure or an `S_FALSE` with no dispatch, a query fails, an out pointer is null, `ShellExecute` fails, or the 30 second wait runs out | A fallback to a plain start gives an elevated Hub | Nothing is started and the call returns false, so `--launch-hub` exits 8 (`DeElevationFailed`, LH3). There is no plain-start fallback anywhere in the chain. Every pointer acquired so far is released (HN8) | `Shell_Chain_Failure_Starts_Nothing` (one case per step), `Launch_Cannot_DeElevate_Launches_Nothing` |
| HN11 | `--set-aumid` through `CoCreateInstance(CLSID_ShellLink)`, `IPersistFile`, `IPropertyStore` | The same values are not written, or a pointer leaks | `System.AppUserModel.ID` is set, committed, saved, and read back through a second `IShellLink` instance, as before. Step failures are `Io` with the step and the `HRESULT` (SA9). The property value memory and the `PROPVARIANT` are freed | `Aumid_Write_Roundtrips_On_Real_Shortcut` (a real `.lnk` in the test's temp folder), `Aumid_Missing_File_Fails_With_Io`, E2E (`ShortcutAumidsAre`) |
| HN12 | The STA thread of the shell call | `CoInitializeEx` is skipped, or `CoUninitialize` is missed or called when init failed | The worker thread is STA, calls `CoInitializeEx` itself, and `CoUninitialize` only after a success (`S_OK` or `S_FALSE`). A changed-mode failure means false, nothing started | `Doc`, E2E `Hub_Helper_LaunchHub_Forced_Elevated_Uses_Shell` |
| HN13 | The publish proof that `BwcUpdate.exe` is native (`scripts/flavor-metadata.js`, `nativeAotProblems`) | A CoreCLR apphost passes as the hardened helper | All three hold: no `BwcUpdate.dll`, `BwcUpdate.deps.json`, or `BwcUpdate.runtimeconfig.json` beside the exe; the PE export table names `DotNetRuntimeDebugHeader` (every NativeAOT image exports it for debuggers; a CoreCLR apphost never does); and the file holds none of `hostfxr`, `hostpolicy`, `coreclr`, `DOTNET_ROOT`, `CORECLR_ENABLE_PROFILING`, `COREHOST_TRACE`, `DOTNET_STARTUP_HOOKS`, `GC_Initialize`, or `GC_VersionInfo`, as UTF-8 or UTF-16LE (the last two are the standalone GC loader). Any one failing fails the publish. Section names do not work as a proof: the linker merges `.managedcode` and `hydrated` into `.text` and `.bss` | `release-selftest` (`native_aot_probe`), publish audit (`dotnet-build.js`, `test-all.js`, `test-installer-e2e.js`, the elevated runner) |
| HN14 | The flavor of a native exe has no `BwcFlavor` attribute | A ship folder holds the E2E helper (a seam), or the reverse, and nothing says so | The exe holds the marker `BwcUpdateFlavor=<ship or E2E>` (a string literal that the unexpected-error log line uses, so it is kept by the trimmer), as UTF-16LE or UTF-8. The folder's flavor must be the only value found. A missing or a mixed marker fails | `release-selftest` (`native_flavor_marker`), publish audit, the E2E harness (`FlavorMetadata`) |
| HN15 | The ship seam scan | The managed file it scanned (`BwcUpdate.dll`) no longer exists, so the scan covers nothing | A ship `BwcUpdate.exe` is scanned for `BWC_E2E_UPDATE_` as UTF-8 and UTF-16LE (HB3). A `BwcUpdate.dll` in a ship folder is refused outright (HN13) | `release-selftest` (`ship_e2e_strings`, `native_aot_probe`) |
| HN16 | The manifest of the native exe | The linker drops `app.manifest`, or the E2E flavor gets the ship one | `requestedExecutionLevel` is embedded in the exe: `asInvoker` in the E2E folder and the source manifest's level in the ship folder (the existing manifest audit reads the exe bytes) | publish audit (`checkExeManifests`) |
| HN17 | The shared publish folder: the Hub, Registry Editor, then the helper publish into one folder | The helper's publish deletes or overwrites a runtime file of the Hub or of Registry Editor, or leaves `BwcUpdate.dll`, `.deps.json`, or `.runtimeconfig.json` from an earlier CoreCLR build. The collision check (a hash of each file after each project) does not see a deletion, so it cannot be the only guard. This happened: see HN25 | The helper is published into its own staging folder (HN25), never into the shared one. Only `BwcUpdate.exe` (and its symbols when kept) is copied from the staging folder into the shared folder. The three stale managed names are removed from the shared folder by exact name, and nothing else is deleted or replaced. The folder is deleted before the first publish, and a folder that lacks a runtime file after any project fails the build (HN26) | `release-selftest` (`update_publish_install`), `dotnet-build.js` (HN26 guard), HN13 |
| HN18 | A reflection, trimming, or AOT analyzer warning (IL2xxx, IL3xxx, SYSLIB) in the helper, `Bwc.Core`, or `BouncyCastle` use | A path that works under the JIT and fails only in the native exe | The warnings are errors (`TreatWarningsAsErrors`), and `dotnet-build.js` fails the publish on any warning line. Nothing is suppressed in code: a suppression needs a row here | build |
| HN19 | The JIT test project (`Bwc.Update.Tests`) compiles the helper sources | A change for AOT breaks the tests, or the tests run code the exe does not | The tests compile the same sources (all files but `Program.cs`) and run them under the JIT. Every behavior that differs between JIT and AOT (JSON, COM, version) has a test that runs under the JIT and an E2E that runs the native exe | `dotnet test tests/Bwc.Update.Tests`, E2E |
| HN20 | The helper's own version | `Assembly` attributes are trimmed in the native exe, so the version is empty and the elevated helper compares update versions against nothing | The version is the informational version of the build without metadata, read in the native exe the same way as under the JIT. The E2E update path runs this binary and refuses a downgrade | `Helper_Version_Strips_Metadata`, E2E `Hub_UpdateAvailable_InstallRunsVerifiedInstaller` |
| HN21 | Signature verification (Ed25519, BLAKE2b through BouncyCastle) in the native exe | A trimmed algorithm class makes every update fail, or accept | The native exe verifies the same signed manifest and installer as the JIT build: the E2E update path downloads and verifies a signed stand-in installer through this binary | E2E `Hub_UpdateAvailable_InstallRunsVerifiedInstaller`, `Hub_UpdateCheck_*` |
| HN22 | The staged copy of `BwcUpdate.exe` in `<install dir>.new` runs the close step | The installer needs the .NET runtime beside the helper | The helper is standalone, so the staged copy starts without any other file. The staged payload still holds everything, and the installed-files list names `BwcUpdate.exe` and its symbols only | `test-installer-e2e.js` |
| HN23 | The native link step of `BwcUpdate` cannot find the C++ tools: `vswhere.exe` is not on PATH (Visual Studio 18's `vcvarsall.bat` runs it by name), or the C++ x64 or ARM64 tools are missing | A publish that dies late with a confusing error, or a link for the wrong architecture | `dotnet-build.js` adds the Visual Studio installer folder to the PATH of the build process only, and only when `vswhere.exe` is there and PATH lacks it. Nothing persistent changes. A missing C++ tool stops the publish with the ILCompiler's own message (nothing is installed or downgraded). Each rid and flavor is a separate link, so one passing does not stand for another | `release-selftest` (`vswhere_on_path`), `dotnet-build.js` for both rids and both flavors |
| HN24 | Other variables the NativeAOT runtime itself reads: `DOTNET_GC*` knobs, `DOTNET_EnableEventPipe`, `DOTNET_gcServer`, and the like | A knob loads code or writes a file in the elevated process | The exe has no standalone GC loader (no `GC_Initialize` or `GC_VersionInfo`, HN13), so `DOTNET_GCName` and `DOTNET_GCPath` load nothing, and EventPipe is off (the `EventSource` feature is trimmed). The remaining GC knobs only change memory use. The E2E test sets `DOTNET_EnableEventPipe` with an output path and `DOTNET_GCName` with a file that does not exist, and requires no output file | E2E `Hub_Helper_Ignores_Runtime_Variables`, HN13 |
| HN25 | The helper's publish after an earlier CoreCLR (self-contained) publish of the same project, with `-o` set to the shared folder. Root cause of a real failure: the first NativeAOT publish removed `hostfxr.dll`, `hostpolicy.dll`, `coreclr.dll`, `System.Private.CoreLib.dll`, and about 190 other runtime files from `publish/<rid>-<flavor>`, so `BetterWindowsConfig.exe` showed the system ".NET Desktop Runtime required" dialog | MSBuild's `IncrementalClean` target (it runs in the build that `publish` starts) reads the list of files the project's previous publish wrote (`obj/.../Bwc.Update.csproj.FileListAbsolute.txt`), which held the whole self-contained runtime set the old CoreCLR helper had copied into the shared folder, and deletes every listed file that the new publish does not write. The NativeAOT publish writes none of them, so it deleted the Hub's copies, wherever they were. A hash snapshot sees only files that exist, so the collision check passed | The helper is published into its own staging folder (`publish/<rid>-<flavor>-bwcupdate-aot`, deleted before each publish). Before that publish `scripts/lib/update-publish.js` deletes the project's own `FileListAbsolute.txt` for the platform and rid (exact name, in the project's `obj`), so no list from an earlier build can name a file of the shared folder. After it `BwcUpdate.exe` is copied into the shared folder (HN17). The same code is used by `dotnet-build.js` and by `test-installer-e2e.js` (the `-e2e-next` publish) | `release-selftest` (`update_publish_install`, `update_publish_stale_list`), `dotnet-build.js` (HN26 guard after every project), `test-installer-e2e.js` |
| HN26 | A publish folder that lacks the runtime of the self-contained apps, for any reason | A folder that passes every flavor and AOT check but cannot start the Hub, and a check that ran against it is void | `scripts/flavor-metadata.js` (`missingRuntimeFiles`) requires `hostfxr.dll`, `hostpolicy.dll`, `coreclr.dll`, `System.Private.CoreLib.dll`, and `BetterWindowsConfig.runtimeconfig.json` in the folder. `checkPublishFlavor` fails on any that is missing (so `dotnet-build.js`, `test-installer-e2e.js`, `test-all.js`, and the elevated runner fail). `dotnet-build.js` also checks after every project, and names the project whose publish removed a file | `release-selftest` (`runtime_files_guard`), `dotnet-build.js`, publish audit |

### HX. Comment (review finding 10)

The comment in `UpdateApplier.VerifyAndStartAsync` said the verified file stays held until the elevated helper has opened it.
After the runas start returns, the medium helper releases both handles (the start call returns when the elevated process was
created, not when it opened the file). The comment now says that. The elevated helper does not rely on the medium helper's
check: it copies, then verifies the copy (EA13 to EA16). Doc.

## 0.2.0: the third tool (`BwcGpedit.exe`)

Written before the tests and the code. `--set-aumid` and `--close-running` know the product's apps by a fixed list, so a new app is refused
until the list names it. The list is one table (`ShortcutAumidRules.Shortcuts`: shortcut name and app id) and `ToolCatalog` for the exe names;
nothing else in `Bwc.Update` hard-codes a tool.

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| SA13 | `--set-aumid "<...>\Local Group Policy Editor (BWC).lnk" BetterWindowsConfig.Gpedit` | The third shortcut is refused (exit 2 or 3) and has no AUMID, so a pinned tool groups wrongly on the taskbar | Accepted by SA2 and SA3, and by SA4 for exactly this pair. The app id is exactly `BetterWindowsConfig.Hub`, `BetterWindowsConfig.Regedit`, or `BetterWindowsConfig.Gpedit`; any other text stays a usage error | `SetAumid_Arguments_Parse`, `SetAumid_Name_And_Id_Rule` |
| SA14 | The three ids of the helper differ from `AppIdentity` (the ids the apps set at startup) or from the installer defines | A shortcut and its window group apart | A test compares every row of `ShortcutAumidRules.Shortcuts` with `AppIdentity.HubAppUserModelId`, `RegeditAppUserModelId`, `GpeditAppUserModelId` and with the shortcut file names of `installer/include/bwc-common.nsh` | `SetAumid_Table_Matches_AppIdentity_And_Installer` |
| SA15 | A shortcut name that belongs to another tool's id (`Local Group Policy Editor (BWC).lnk` with the Regedit id, or `Registry Editor (BWC).lnk` with the Gpedit id) | The helper writes a wrong id into a shortcut | Refused `PathRejected`, exit 3, nothing written (SA4) | `SetAumid_Name_And_Id_Rule` |
| SA16 | The real COM write on the Gpedit shortcut | A third id is not stored | `ShortcutAumid.Write` stores `BetterWindowsConfig.Gpedit` and reads it back, and keeps the target (SA12) | `Aumid_Write_Roundtrips_On_Real_Shortcut` |
| HC5 | `BwcGpedit.exe` runs from the install folder (`--close-running`, the update gate, the installer) | The tool is not found, so a replaced file is locked or an update proceeds over an open editor | `ProcessCommands.ProcessNames` and the Hub's refusal both come from `ToolCatalog`, so each catalog tool is found by name and by readable image path under the folder (HC3), and a copy of the same name elsewhere never counts. Printed lines name the process (`BwcGpedit pid=<n>`) | `CloseRunning_Every_Catalog_Tool_Is_Found`, `Scan_Counts_Every_Catalog_Tool_Under_Install_Dir`, `Image_Rule_Covers_Every_Catalog_Tool` |

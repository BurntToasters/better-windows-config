# Contract: process commands for the installer (`BwcUpdate.exe`)

The installer calls these commands of `BwcUpdate.exe` while it replaces the install folder: `--wait-pid` and
`--close-running` (both poll, and neither ever kills a process), and `--launch-hub` (starts the Hub at the shell's
integrity level). The helper modes `--apply` and `--apply-elevated` are what start the installer (see "Hand-off" below).
The running-process rule is the same one the Hub and the helper use
(`src/Bwc.Update/Process/RunningImageRule.cs`): a process counts as running from a folder when its full
image path (QueryFullProcessImageName, PROCESS_QUERY_LIMITED_INFORMATION) is inside that folder, compared
without regard to case, with the folder given as a full path and a trailing separator. A live process
whose image cannot be read counts as running (fail closed). An exited process never counts.

Failure cases and their tests are in `docs/failure-modes/update-verifier.md`, section P (and O for the rule), and
for everything added in 0.1.2 (per-machine install) in `docs/failure-modes/update-elevation.md`.

## Failure codes added in 0.1.2

These are `UpdateFailure` names. They appear in the `failure` field of the JSON line and, for a refusal that relaunches
the Hub, in `--update-result <code>`.

| Code | Meaning |
|------|---------|
| `ElevationDeclined` | The user declined the consent prompt (Win32 error 1223, `ERROR_CANCELLED`) that the shell showed for `--apply-elevated`. Nothing was installed. |
| `NotElevated` | `--apply-elevated` ran without an elevated token. It did nothing. |
| `RunningInOtherSession` | A BWC process from the install folder runs in another session (`--close-running`). Nothing was changed. |
| `RunningElevated` | `--check`, `--download`, or `--apply` ran with an elevated token. `--check` and `--download` print it in their own JSON shape with exit code `1` and fetch and write nothing (an elevated Hub cannot update: the download must run unelevated, so nothing could be handed to `--apply-elevated`). |
| `DeElevationFailed` | `--launch-hub` is elevated and cannot start the Hub through the desktop shell. Nothing was started. |

## `--close-running`

```
BwcUpdate.exe --close-running --install-dir "<absolute folder>" [--exclude-pid <pid>]... [--timeout-seconds <n>]
```

- Finds the BWC processes (the Hub and every tool in the tool catalog) that run from `--install-dir`.
- Leaves out the processes named by `--exclude-pid` and this command's own process, which is always left out.
- First looks at every session. When a BWC process from `--install-dir` runs in a session other than this process's
  session, the command fails at once, before anything is closed: no `WM_CLOSE` is sent to any process. Each such
  process is printed as `RunningInOtherSession session=<id> <name> pid=<pid>`, and the exit code is `4`. A process whose
  session cannot be read is not "other session": it is handled like a process of this session (fail closed).
- Otherwise sends `WM_CLOSE` to every top-level window of each remaining process in this session, once per process. It does
  not kill anything. A process with no window, or one that ignores the close, stays running.
- Waits up to `--timeout-seconds` (default 30, from 1 to 3600) for none to remain, polling every 250 ms.
- `--install-dir` must be an absolute path. `--exclude-pid` takes a positive integer; it may be given more than once.
- When this process is elevated it writes no log file. The elevated step of the installer sees processes of every
  integrity level, so an elevated Registry Editor is found too.

Exit codes:

| Code | Meaning |
|------|---------|
| `0` | None remain. |
| `2` | Usage: a missing or relative `--install-dir`, a pid or timeout that is not in range, an unknown option, or an option given twice. |
| `3` | Some remain at the timeout. Each one is printed on stdout as `<name> pid=<pid>`. |
| `4` | A BWC process runs in another session. Each is printed as `RunningInOtherSession session=<id> <name> pid=<pid>`. Nothing was closed. |

## `--wait-pid`

```
BwcUpdate.exe --wait-pid <pid> [--timeout-seconds <n>]
```

- Waits up to `--timeout-seconds` (default 30, from 1 to 3600) for the process to exit, polling every 250 ms.
- A pid that does not exist, or whose process has exited, counts as exited at once.
- The pid must be a positive integer. Only one pid is accepted by this command.
- When this process is elevated it writes no log file.

Exit codes:

| Code | Meaning |
|------|---------|
| `0` | The process has exited, or never existed. |
| `2` | Usage: a pid or timeout that is not in range, an unknown option, or an option given twice. |
| `3` | The process still runs at the timeout. The line `pid=<pid> is still running` is printed on stdout. |

## `--launch-hub`

```
BwcUpdate.exe --launch-hub [--update-result <UpdateFailure name>]
```

The installer calls this instead of starting the Hub itself (`Exec`), and the finish page's Start action calls it too.

- Starts `<folder of BwcUpdate.exe>\BetterWindowsConfig.exe`. No option names another file or folder.
- Passes `--update-result <name>` to the Hub when given. The name must be exactly an `UpdateFailure` name other than
  `None` (`InstallerFailed`, `ElevationDeclined`, ...). A number, another spelling, or text with a space or quote is a usage error.
- The Hub starts at the integrity level of the desktop shell:
  - Not elevated: a normal start.
  - Elevated: through the shell. The helper finds the desktop shell window (`IShellWindows.FindWindowSW`), asks its
    active shell view for the desktop folder's automation object, and calls `IShellDispatch2.ShellExecute` on the shell's
    own Application object. The Hub then runs as the signed-in user's normal token, not as administrator.
- There is no fallback. When it is elevated and the shell cannot be used (no Explorer, a COM failure), it starts nothing
  and exits `8`.
- When it is elevated it writes no log file. Stdout is one JSON line: `{"ok":true|false,"failure":"<UpdateFailure>","detail":"..."}`.

Exit codes:

| Code | Meaning |
|------|---------|
| `0` | The Hub start was requested (the shell does not report the new process). |
| `2` | Usage: an unknown option, a bad `--update-result`, an option given twice. |
| `6` | `BetterWindowsConfig.exe` is missing, or the start failed. |
| `7` | An unexpected error. |
| `8` | Elevated and the Hub cannot be started unelevated. Nothing was started (`DeElevationFailed`). |

## Use by the installer

The installer should call `--wait-pid <elevated helper pid> --timeout-seconds 30` first, when it was given `/PARENTPID`,
so the helper's copy of `BwcUpdate.exe` is no longer locked (the helper exits right after it starts the
installer). Then it calls `--close-running` with `--exclude-pid` set to the helper's pid, and only then
replaces the files. A `3` or `4` from either command means the
installer must not replace files that the running process holds: it should stop and report. On `4` the message names the session.
When the install is done, or has failed, it calls `--launch-hub` (with `--update-result InstallerFailed` after a
failure), never `Exec`, so the Hub does not start elevated.

The process name list is fixed in the updater: `BetterWindowsConfig` and the catalog tools. An installer
that starts other programs from the folder must handle them itself.

## Hand-off from the helper (`--apply`, then `--apply-elevated`)

An update takes two helper steps, because the installer must run elevated (the install is per machine, in Program Files)
and the staging folder is in the user's `%TEMP%`, which medium-integrity processes can write.

### Step 1: `--apply` (medium integrity)

The Hub starts it, as before (`--apply <installer.exe> --receipt <receipt.json> --current-version <semver>
--install-dir <folder> [--wait-pid <pid>]... [--installer-switch ...]`). It keeps every check it had (refuses when
elevated, staging paths, receipt, version order, waiting for the Hub and tools to exit, and verifying the installer with
the file held open). It adds: `--install-dir` must be the folder of the running `BwcUpdate.exe`, and in the ship build that
folder must be inside Program Files. Then it asks Windows to start

```
<folder of BwcUpdate.exe>\BwcUpdate.exe --apply-elevated <staging folder>
```

with the `runas` verb, so the shell shows its consent prompt. The install dir is never taken from an argument for that
start. The installer is not started by this process. `--installer-switch` values are still checked, but not forwarded.
The process then exits. Stdout is one JSON line with `ok`, `failure`, `detail`, `installerStarted` (false), `elevatedHelperStarted`
(true when the elevated helper was started), and `hubLaunched`.

When the user declines the prompt (Win32 error 1223) the failure is `ElevationDeclined`: nothing changed, the Hub is
started again with `--update-result ElevationDeclined`, and the exit code is `3`. Any other failure to start the elevated
helper is `InstallerFailed` (exit `5` is not used here: the Hub is relaunched and the exit code is `3`).

In the E2E build flavor the elevated helper is started with a plain start: no verb, no prompt, ever.

### Step 2: `--apply-elevated <staging folder>` (elevated)

```
BwcUpdate.exe --apply-elevated <absolute staging folder>
```

The only option is the staging folder. No install dir, version, installer path, or switch can be given: any other
option is a usage error (exit `2`). It writes no log file; results are one JSON line (same shape as `--apply`) and the exit code.

1. The token must be elevated, or the failure is `NotElevated` and nothing is done (the Hub is not started either).
2. The install dir is this program's own folder. In the ship build it must be inside Program Files.
3. The current version is this program's own assembly version. A staged version that is older (`Downgrade`) or equal
   (`SameVersion`) is refused, whatever the arguments say.
4. The staging folder must be `%TEMP%\BWC-Update\<32 lowercase hex>` (the shape `UpdatePaths` and `InstallerPaths` use), with no
   link or junction on the way, and no link inside it. The receipt is read and validated as in `--apply`.
5. The protected root is `<install dir>.upd`, a sibling of the install dir (for example
   `C:\Program Files\Better Windows Config.upd`). It is created with an explicit protected DACL (SYSTEM and Administrators
   full control, no inheritance, owner Administrators), not by inheriting one. An existing root must not be a link or a file,
   must be owned by SYSTEM, Administrators, or TrustedInstaller, and its DACL must grant write to no one else, or the failure
   is `PathRejected`. Older folders `<protected root>\<32 lowercase hex>` are removed first: only folders
   with such a name, never through a link (a link is not followed, and one inside a folder is removed as a link only), and
   a folder with a file in use is skipped.
6. `installer.exe` is copied into `<protected root>\<new 32 hex>\installer.exe`; the copy folder and its `tmp` folder
   have the same protected DACL. The source is opened with
   `FileShare.Read`; its final path must be the installer inside the checked folder; the copy stops one byte past the receipt
   size (`InstallerTooLarge`).
7. The copy (not the source) is verified: size, SHA-256, minisign signature, and the signed trusted comment `version:`
   (the receipt version, which is newer than this helper's) `arch:`, `product:bwc`, and `channel:` (the manifest's channel). The copy is held by one handle or another from the
   first write until the installer starts: a reader is opened while the writer is open, the verification runs through it, and
   a second reader (`FileShare.Read`) is opened and compared (size and SHA-256) before the first is closed.
8. The copy is started with the raw command line `/S /RELAUNCH /PARENTPID=<this pid> /D=<install dir>` (`/D=` last and
   unquoted), working directory the system folder. The installer's `TEMP` and `TMP` are `<copy folder>\tmp`, and its
   environment has no .NET runtime variable (`DOTNET_*`, `COREHOST_*`, `CORECLR_*`, `COMPlus_*`) and no `__COMPAT_LAYER`.
   There is no verb: the helper is already elevated. The helper then exits.
   `/PARENTPID` is the elevated helper, so the installer waits for the helper that holds the lock.
9. Every refusal removes the new copy and starts the Hub at the shell's integrity level (see `--launch-hub`) with
   `--update-result <code>`. When that is not possible (`DeElevationFailed`), nothing is started and the failure is `HubMissing`.

The copy stays in the protected root after a successful start (the installer runs from it). The next update removes it
(item 5). The uninstaller should remove `<install dir>.upd`.

Exit codes of `--apply` and `--apply-elevated`: `0` the next step or the installer started, `2` usage, `3` refused (a
declined prompt is `3` with `ElevationDeclined`), `4` apps still running (`--apply` only), `5` the installer did not start
(`--apply-elevated`), `6` refused and the Hub did not start, `7` unexpected.

### E2E flavor only

The E2E build never prompts. `--apply-elevated` accepts a non-elevated token only in that build, and only when the
environment variable `BWC_E2E_UPDATE_PROTECTED_ROOT` names a folder strictly inside the process's own temp folder (not
the temp folder, not the staging root, and not inside it). The protected root is then that folder instead of
`<install dir>.upd`. Both are compiled out of the ship build (`UpdateE2ESeam`, `#if BWC_E2E`).

### The installer, after it starts

The installer is started as:

```
installer.exe /S /RELAUNCH /PARENTPID=<elevated helper pid> /D=<install dir>
```

`/RELAUNCH` and `/PARENTPID` are written by the elevated helper. The installer:

1. Waits for `/PARENTPID` to exit (`--wait-pid`, above), closes the running apps (`--close-running`), and
   replaces the files.
2. On success, starts the Hub with `BwcUpdate.exe --launch-hub` from the install folder (not with `Exec`, which would
   start an elevated Hub). `/RELAUNCH` is the request.
3. On failure, starts the still-installed Hub with `--launch-hub --update-result InstallerFailed`, so the Hub shows
   the reason. The Hub is started only after the helper's pid has exited, because a second Hub would
   only activate the first one.

Failure cases: `docs/failure-modes/update-verifier.md`, sections HL9, HL14, N15, and N16, and `update-elevation.md`.

## Unexpected errors (all modes, 0.1.1)

Any exception that escapes a mode is caught at the top of `Main`. The mode then:

- logs the exception type and message through `Log` (only when the mode writes a log: not in an elevated mode);
- prints one JSON line with `"ok":false` and `"failure":"Unexpected"`, in the shape that mode already uses
  (`--check` and `--download` and `--apply` use their own shapes; `--close-running`, `--wait-pid`, `--apply-elevated`,
  `--launch-hub`, and help or usage output print `{"ok":false,"failure":"Unexpected","detail":"..."}`);
- exits with code `7`.

In `--apply` mode, when the elevated helper has not started, the helper also relaunches the Hub with
`--update-result Unexpected` (the same way a refusal does), so the user is never left without the app.
When the elevated helper has started, `--apply` does not relaunch the Hub. `--apply-elevated` handles its own unexpected
errors: before the installer starts it removes its copy and starts the Hub at the shell's integrity level with
`--update-result Unexpected`; after the installer starts it does not relaunch the Hub.

| Code | Meaning |
|------|---------|
| `7` | An unexpected error. The JSON line carries `"failure":"Unexpected"`. Logged unless elevated. |

Failure cases: `docs/failure-modes/update-verifier.md`, section S (S16 to S18).

## File logging in elevated modes

The helper's log folder comes from `BWC_DATA_ROOT`, which a medium-integrity process can aim at a junction, so an elevated
process must not open it. `--apply-elevated` never writes the file log. Every other mode writes it only when the token is
not elevated (an unreadable token counts as elevated). Results of an elevated mode are stdout and the exit code.

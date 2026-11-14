# Contract: process commands for the installer (`BwcUpdate.exe`)

The installer (D1) calls two commands of `BwcUpdate.exe` while it replaces the install folder. Both poll,
and neither ever kills a process. The running-process rule is the same one the Hub and the helper use
(`src/Bwc.Update/Process/RunningImageRule.cs`): a process counts as running from a folder when its full
image path (QueryFullProcessImageName, PROCESS_QUERY_LIMITED_INFORMATION) is inside that folder, compared
without regard to case, with the folder given as a full path and a trailing separator. A live process
whose image cannot be read counts as running (fail closed). An exited process never counts.

Failure cases and their tests are in `docs/failure-modes/update-verifier.md`, section P (and O for the rule).

## `--close-running`

```
BwcUpdate.exe --close-running --install-dir "<absolute folder>" [--exclude-pid <pid>]... [--timeout-seconds <n>]
```

- Finds the BWC processes (the Hub and every tool in the tool catalog) that run from `--install-dir`.
- Leaves out the processes named by `--exclude-pid` and this command's own process, which is always left out.
- Sends `WM_CLOSE` to every top-level window of each remaining process, once per process. It does not
  kill anything. A process with no window, or one that ignores the close, stays running.
- Waits up to `--timeout-seconds` (default 30, from 1 to 3600) for none to remain, polling every 250 ms.
- `--install-dir` must be an absolute path. `--exclude-pid` takes a positive integer; it may be given more than once.

Exit codes:

| Code | Meaning |
|------|---------|
| `0` | None remain. |
| `2` | Usage: a missing or relative `--install-dir`, a pid or timeout that is not in range, an unknown option, or an option given twice. |
| `3` | Some remain at the timeout. Each one is printed on stdout as `<name> pid=<pid>`. |

## `--wait-pid`

```
BwcUpdate.exe --wait-pid <pid> [--timeout-seconds <n>]
```

- Waits up to `--timeout-seconds` (default 30, from 1 to 3600) for the process to exit, polling every 250 ms.
- A pid that does not exist, or whose process has exited, counts as exited at once.
- The pid must be a positive integer. Only one pid is accepted by this command.

Exit codes:

| Code | Meaning |
|------|---------|
| `0` | The process has exited, or never existed. |
| `2` | Usage: a pid or timeout that is not in range, an unknown option, or an option given twice. |
| `3` | The process still runs at the timeout. The line `pid=<pid> is still running` is printed on stdout. |

## Use by the installer

The installer should call `--wait-pid <helper pid> --timeout-seconds 30` first, when it was given `/PARENTPID`,
so the helper's copy of `BwcUpdate.exe` is no longer locked (the helper exits right after it starts the
installer). Then it calls `--close-running` with `--exclude-pid` set to the helper's pid, and only then
replaces the files. A `3` from either command means the
installer must not replace files that the running process holds: it should stop and report.

The process name list is fixed in the updater: `BetterWindowsConfig` and the catalog tools. An installer
that starts other programs from the folder must handle them itself.

## Hand-off from the helper (`--apply`)

The helper starts the installer once, with the raw command line:

```
installer.exe /S /RELAUNCH /PARENTPID=<helper pid> [allowed switches] /D=<install dir>
```

The helper writes `/RELAUNCH` and `/PARENTPID` itself, and drops a caller's copy of either. It then exits
at once. The installer:

1. Waits for `/PARENTPID` to exit (`--wait-pid`, above), closes the running apps (`--close-running`), and
   replaces the files.
2. On success, starts the Hub: `BetterWindowsConfig.exe` in the install folder. `/RELAUNCH` is the request.
3. On failure, starts the still-installed Hub with `--update-result InstallerFailed`, so the Hub shows
   the reason. The Hub is started only after the helper's pid has exited, because a second Hub would
   only activate the first one.

Failure cases: `docs/failure-modes/update-verifier.md`, sections HL9, HL14, N15, and N16.

## Unexpected errors (all modes, 0.1.1)

Any exception that escapes a mode is caught at the top of `Main`. The mode then:

- logs the exception type and message through `Log`;
- prints one JSON line with `"ok":false` and `"failure":"Unexpected"`, in the shape that mode already uses
  (`--check` and `--download` and `--apply` use their own shapes; `--close-running`, `--wait-pid`, and
  help or usage output print `{"ok":false,"failure":"Unexpected","detail":"..."}`);
- exits with code `7`.

In `--apply` mode, when the installer has not started, the helper also relaunches the Hub with
`--update-result Unexpected` (the same way a refusal does), so the user is never left without the app.
When the installer has started, the helper does not relaunch the Hub; the installer owns that.

| Code | Meaning |
|------|---------|
| `7` | An unexpected error. The JSON line carries `"failure":"Unexpected"`. Logged. |

Failure cases: `docs/failure-modes/update-verifier.md`, section S (S16 to S18).

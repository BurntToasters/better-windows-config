# E2E harness failure modes

The E2E harness launches the Hub and the Registry Editor from a publish folder, drives them
with UI Automation, writes sandbox state under `HKCU\Software\BWC-E2E\<runId>`, and writes
`artifacts/e2e/result-<rid>-<flavor>.json`. `scripts/test-e2e.js` is the entry point. This document lists the
ways the harness can fail, how the failure is detected, and what the harness does about it.

## 1. Mutex abandoned or deadlocked

`Local\BwcGuiTestLock` serializes GUI tests across test assemblies. The gate is acquired once in
assembly initialize on a dedicated thread and released from that same thread in assembly
cleanup, because Windows mutex ownership is thread affine.

- An abandoned mutex means a previous holder exited without releasing it. `AbandonedMutexException`
  is treated as acquisition, and the run continues.
- A live holder that never releases causes a 15 minute wait, then a failed `gate.acquire` check
  and a hard assembly initialization failure. The result artifact still records the failure if
  assembly cleanup runs.
- Releasing from the wrong thread would throw `ApplicationException`. The dedicated gate thread
  prevents that.
- Disposing the mutex while it is still owned leaves the next waiter with an abandoned mutex.
  Cleanup always signals the gate thread and joins it before disposing.

## 2. Another GUI test running

The mutex is the only coordination mechanism. A second test assembly waits up to 15 minutes. It
does not poll for windows, click anything, or kill processes while it waits. If the wait times
out, the second assembly fails with an explicit gate timeout instead of interacting with the
first assembly's windows. Tests inside one assembly are marked `DoNotParallelize` so they cannot
race each other even before the gate is acquired.

## 3. UAC prompt from a highestAvailable executable

`BwcRegedit.exe` is marked `highestAvailable`. Launching it directly on an administrator account
raises a UAC prompt, which blocks the test and can change the desktop. Every process launch sets
`__COMPAT_LAYER=RunAsInvoker` and uses `UseShellExecute=false`. The compatibility layer keeps the
process at the current integrity level and the harness never asks for elevation. If a prompt
still appears, the window poll times out and the test fails without clicking the prompt.

## 4. Wrong architecture or publish folder

`BWC_RID` selects `win-x64` or `win-arm64`, and `BWC_PUBLISH_DIR` selects the folder. The test
host and the app are separate. `scripts/test-e2e.js` builds and runs the test project for the host
architecture (`process.arch`), and `--rid` only picks the app and its publish folder. The rule is
that the app rid must be runnable on this machine: `win-x64` runs on x64 and ARM64 Windows (ARM64
runs it under emulation), and `win-arm64` runs only on ARM64 Windows. `E2EConfig` checks this with
`RuntimeInformation.OSArchitecture`, not the process architecture, so an x64 test host on an ARM64
machine still accepts `win-x64`. An unrunnable rid fails assembly initialization, before any test
runs and before the artifact is opened, so no artifact is written and `scripts/test-e2e.js` reports
the missing artifact with the test output tail. If the folder belongs to another rid, the executables
are missing or cannot start. `E2EConfig` validates the rid
and flavor, resolves relative publish paths against the repository root, and `EnsureExecutablesExist`
fails with the exact publish command to run. The executable check is recorded as a failed check, so `result-<rid>-<flavor>.json` is written
with `pass: false` instead of the assembly crashing before the artifact exists.

## 5. Stale publish folder missing .pri files

A partially copied publish folder can contain the executables but omit `BetterWindowsConfig.pri`,
`BwcRegedit.pri`, or the WinUI runtime files. The process may start and then show no window, show
an empty window, or exit with a WinUI activation error. The harness treats a missing main window
within 30 seconds as a failure and includes the process exit code when the process has already
exited. Republishing with `node scripts/dotnet-build.js --rid <rid> --flavor <flavor>` is the
remedy. The harness does not copy files into the publish folder.

## 6. Window not foreground so keys go elsewhere

Synthetic keyboard input goes to the foreground window, not to a chosen HWND. Before sending any
key, `AppSession.SendKeys` checks `GetForegroundWindow`, calls `SetForegroundWindow` and FlaUI's
`SetForeground`, and polls for up to 3 seconds. If the target window still is not foreground, it
records a failed `gui.foreground.<exe>` check and throws. It never types into an unknown window.
The current smoke tests do not send keys, but future tests inherit this guard.

## 7. UIA stale elements after a modal

UI Automation element references can become stale after a window closes, a modal opens, or the
app rebuilds its visual tree. `AppSession.Find` rebuilds the window element from `MainHwnd` on
every call and only then looks up the automation id. `WaitForElement` retries with a deadline and
reports the last UIA error when it times out. Calls do not reuse a cached descendant element.

## 8. Killing the wrong process

`AppSession` only holds the `Process` object returned by its own `Process.Start`. Close first
calls `CloseMainWindow` and waits 10 seconds. Only if that fails does it call
`Kill(entireProcessTree: true)` on that same process. The harness never enumerates processes by
name and never kills a process it did not start, so an older Hub or Registry Editor window
belonging to the developer is left alone.

## 9. Sandbox escaping HKCU

All test registry writes must stay under `HKCU\Software\BWC-E2E\<runId>`. `SandboxRegistry`
rejects rooted paths, drive letters, colon characters, empty segments, `.` segments, and `..`
segments before opening a key. Production settings are never touched. If an operation still
throws, the test records a failed check and assembly cleanup removes only the run subtree.

## 10. Stale sweep deleting a live run

`SweepStale` enumerates only direct children of `HKCU\Software\BWC-E2E` whose names match
`^\d{8}T\d{6}Z-[0-9a-f]{8}$`. It skips the current run id, skips every non matching name, and
deletes only matching keys whose last write time is at least 24 hours old. A live run that is
older than 24 hours is not protected by the age rule, so long running sessions must refresh the
run key or avoid running longer than the sweep threshold. The sweep never recurses into
`HKCU\Software` and never touches keys outside the harness parent.

## 11. Cleanup failure leaving keys

Assembly cleanup calls `SandboxRegistry.Cleanup`, which deletes only the current run subtree and
ignores a missing key. If deletion fails (for example because a handle is still open), the
failure is recorded as `sandbox.cleanup` and the artifact is still written. A failed cleanup is
visible in `result-<rid>-<flavor>.json` and the run is not considered clean. The next run's stale sweep can
remove an old matching key after 24 hours. A harness self-test verifies the create, read, delete
round trip.

## 12. Result artifact missing or partial when the test host crashes

The artifact is written in assembly cleanup, after all tests, and is not streamed incrementally.
If the test host is killed or crashes hard, no artifact or a partial artifact remains.
`scripts/test-e2e.js` deletes the previous `result-<rid>-<flavor>.json` before starting the test host, then
requires both a zero test process exit code and a parseable artifact with `pass: true`. A missing
or malformed artifact prints the tail of the test output and exits 1. This prevents an old
successful artifact from masking a crashed run.

## 13. Screenshot failure

Failed checks try to capture the application window, falling back to the full screen. A capture
can fail because the window closed, the desktop is locked, or the screenshot folder cannot be
written. Screenshot errors are appended to the check notes and never replace the original
failure message. The test still fails for the original reason. `HarnessTests` exercises the
capture helper against a live window, checks that the PNG exists, and deletes it.

## 14. dotnet missing

`scripts/test-e2e.js` looks for `dotnet` on `PATH` and falls back to
`C:\Program Files\dotnet\dotnet.exe`. If neither exists, it prints an actionable message and
exits nonzero before writing any artifact. A test host that cannot start never produces
`result-<rid>-<flavor>.json`, so the script reports the missing artifact and the output tail.

## 15. Filter matching zero tests

A filter that matches no tests can make the test host exit successfully without running assembly
initialize. The harness treats this as a failure: `result-<rid>-<flavor>.json` is missing or has zero checks,
and `E2EResults` marks `pass` false whenever no checks were recorded. `scripts/test-e2e.js`
exits 0 only when the test process succeeded and the artifact says `pass: true`, so a filter
matching zero tests exits nonzero.

## 16. Parallel test assemblies racing

Two assemblies can start at the same time, each with its own run id. The named mutex serializes
GUI work, and the run id isolates the registry sandboxes. The stale sweep skips the current run
id and only deletes old matching keys, so one assembly cannot delete the other's live sandbox.
Artifacts are written to the same path by design, which means the last assembly to finish wins.
Run one E2E assembly at a time in CI if both artifact and registry history matter.

## 17. A passing check that proves nothing

A check is a pass or fail claim about an observed product or environment state. It is recorded
with `E2EResults.Record(id, pass, notes)`. Three things are not checks, and the harness keeps
them out of the pass count:

- `E2EResults.Note(id, text)` records information only: a value that was read for the record,
  a manual step that was not run, a bookkeeping step that succeeded (keys sent, the executable
  found, the session launched), or an informational measurement. A note never affects `pass`.
  A bookkeeping step that fails still records a failed check, so the run fails.
- `E2EResults.KnownGap(id, workPackage, expectedOracleBehavior, observed)` records a difference
  from classic Registry Editor that the parity plan already tracks. `workPackage` must be one of
  `E1`, `E2`, `RF1`, `RF4`, `RF8`, `UF7`, and any other value throws. A known gap never counts as
  a pass and never fails the run. It names the oracle behavior and the value observed, so the
  artifact shows the difference instead of hiding it behind a pass.
- `E2EResults.RecordOrKnownGap(id, matchesOracle, ...)` records a pass when the observed value
  matches the oracle and a known gap when it does not. Use it for a difference the plan already
  owns. When the product is fixed, the same check turns into a pass without a code change.

A literal `true` is never a check. A check whose pass value is fixed by earlier code, such as
a record after an early return, is rewritten so the value is computed at the record point, or it
becomes a note. The artifact keeps the schema 1 `checks` array shape `{id, pass, notes}` for
`scripts/test-all.js`, and adds `notes` (`{id, text}`) and `knownGaps`
(`{id, workPackage, expectedOracleBehavior, observed}`).

## 18. Density checks drift with the window DPI

Menu, tree, list, address bar, and splitter geometry is compared with
`docs/parity/oracle/regedit-metrics.json`. `OracleMetrics` reads that file. Its pixel values are
device pixels at `dpi.window` (192, which is 200 percent), so a value converts to device pixels at
window DPI `d` as `value * d / 192`. The tolerance is one DIP in device pixels, rounded and never
below one pixel: 2 px at 200 percent and 1 px at 100 percent. The header label height has no oracle
measurement, so its 12 DIP minimum is kept and scaled. The development machine runs at 200
percent, so only that scale was run. The 100 percent expectations follow from the same formula
but were not exercised here.

## 19. Save and export writing outside artifacts

Every file the suite writes goes under `artifacts/e2e`. Three guards cover the failure seen with
a stray `<run-id>.reg` file in Documents:

- The Save dialog only accepts after the file name box reads back the full path. The keyboard
  fallback is checked the same way. A dialog accepted with its default name would save to the
  dialog's default folder, which is Documents. Accepting with an unverified name is what produced
  the stray file.
- After an export the test waits up to 10 seconds until the file exists and keeps the same size
  across two reads 250 ms apart. The application writes the file on a background task.
- `files.outside-artifacts` fails the run when a `.reg` file in Documents, Desktop, the repository
  root, or the publish folder was created or changed during the run. It does not count older files.

Clipboard checks clear the clipboard before each copy, poll until the expected text appears, and
compare the full hex text. A mismatch is a `UF7` known gap with the observed clipboard prefix and
length. An unreadable clipboard is a failed check, because that is an environment problem.

## Verification artifact

A successful harness run leaves `artifacts/e2e/result-<rid>-<flavor>.json` with schema 2, the run
id, `rid` and `appRid` (the app rid, the same value), flavor, publish folder, timestamps, OS and
architecture details (`osArchitecture` is the machine, and `processArchitecture` is the test host
process, which is Arm64 on an ARM64 machine even when `appRid` is `win-x64`), every recorded check,
the `notes` array, and the `knownGaps` array (sections 17 to 19 describe each array).
`pass` is true only when at least one check ran and every check passed. Known gaps and notes do
not affect `pass`. A failed run leaves the same artifact with `pass: false`, failed check ids,
notes, and screenshots under `artifacts/e2e/screenshots` when capture succeeded.
`scripts/test-e2e.js` prints passed, failed, and total checks, the known gaps grouped by work
package, and the notes count. It requires schema 2 and at least one check.

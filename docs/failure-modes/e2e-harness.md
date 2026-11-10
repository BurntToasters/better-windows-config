# E2E harness failure modes

The E2E harness launches the Hub and the Registry Editor from a publish folder, drives them
with UI Automation, writes sandbox state under `HKCU\Software\BWC-E2E\<runId>`, and writes
`artifacts/e2e/result.json`. `scripts/test-e2e.js` is the entry point. This document lists the
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

`BWC_RID` selects `win-x64` or `win-arm64`, and `BWC_PUBLISH_DIR` selects the folder. If the rid
does not match the running test host, or the folder belongs to another rid, the executables are
missing or cannot start. `E2EConfig` validates the rid and flavor, resolves relative publish
paths against the repository root, and `EnsureExecutablesExist` fails with the exact publish
command to run. The executable check is recorded as a failed check, so `result.json` is written
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
visible in `result.json` and the run is not considered clean. The next run's stale sweep can
remove an old matching key after 24 hours. A harness self-test verifies the create, read, delete
round trip.

## 12. Result artifact missing or partial when the test host crashes

The artifact is written in assembly cleanup, after all tests, and is not streamed incrementally.
If the test host is killed or crashes hard, no artifact or a partial artifact remains.
`scripts/test-e2e.js` deletes the previous `result.json` before starting the test host, then
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
`result.json`, so the script reports the missing artifact and the output tail.

## 15. Filter matching zero tests

A filter that matches no tests can make the test host exit successfully without running assembly
initialize. The harness treats this as a failure: `result.json` is missing or has zero checks,
and `E2EResults` marks `pass` false whenever no checks were recorded. `scripts/test-e2e.js`
exits 0 only when the test process succeeded and the artifact says `pass: true`, so a filter
matching zero tests exits nonzero.

## 16. Parallel test assemblies racing

Two assemblies can start at the same time, each with its own run id. The named mutex serializes
GUI work, and the run id isolates the registry sandboxes. The stale sweep skips the current run
id and only deletes old matching keys, so one assembly cannot delete the other's live sandbox.
Artifacts are written to the same path by design, which means the last assembly to finish wins.
Run one E2E assembly at a time in CI if both artifact and registry history matter.

## Verification artifact

A successful harness run leaves `artifacts/e2e/result.json` with schema 1, the run id, rid,
flavor, publish folder, timestamps, OS and architecture details, and every recorded check.
`pass` is true only when at least one check ran and every check passed. A failed run leaves the
same artifact with `pass: false`, failed check ids, notes, and screenshots under
`artifacts/e2e/screenshots` when capture succeeded.

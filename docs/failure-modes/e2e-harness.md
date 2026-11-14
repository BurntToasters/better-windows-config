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

Synthetic keyboard input goes to the foreground window, not to a chosen HWND. Every keystroke batch
and every click aimed at the app or one of its dialogs goes through `ForegroundGuard.Ensure`
(`Harness/ForegroundGuard.cs`). The guard reads the foreground window first and decides from its
owner process, never from a forced topmost flag:

- The foreground is the target: nothing is sent and nothing is recorded.
- The foreground is a child control of the target (its root window, `GA_ROOT`, is the target). A
  system dialog such as Advanced Security Settings can report one of its own check boxes as the
  foreground. Input still goes to the target, so this counts as the target.
- The foreground belongs to the app under test (any window of its process, including its own popups
  and dialogs) but is not the target: no re-assert. This is a product focus bug, so the guard records
  a failed check and the caller does not retry. Keys are not sent to the wrong window.
- The foreground belongs to another process (a chat app, for example): the guard records the note
  `harness.foreign-foreground` with the foreign process name, re-asserts the target through the
  attached-thread path in `Win32.ForceForeground`, and waits up to the timeout. If the target still
  is not foreground, the guard records a failed check. A foreign foreground never fails a product
  check by itself.
- The foreground is the desktop or no window (zero handle): treated as foreign, with the name `none`.

`AppSession.SendKeys` uses the guard for `gui.foreground.<exe>`. The guard also counts foreign
foreground events, so retry helpers can tell a foreign event from a product failure (section 21).

The harness never raises the app's windows above other applications. There is no topmost flag and no
watchdog thread, so a dialog that opens behind the main window, or behind another app, stays where
the product put it and the z-order checks (section 20) see it.

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
failure message. The test still fails for the original reason.

Captures come from `WindowCapture` (`Harness/WindowCapture.cs`). `PrintWindow` was tried first and
does not paint WinUI 3 content reliably on this host (it returned false for the main window, and
the pixel reads came back black), so it is not used. A capture is a screen copy of the window
rectangle, and it is taken only after the area is verified:

- A grid of points inside the area is sampled with `WindowFromPoint`. Every point must belong to the
  app process, so the app's popups, flyouts and dialogs count as the app.
- A point covered by another process records the note `harness.foreign-foreground`, re-asserts the
  app window once with `ForegroundGuard.ReassertForCapture`, and checks again. The verification is
  repeated after the copy, because a window can move during it.
- If the area is still covered, the capture is not judged. It throws `WindowCapture.NotJudgedException`,
  which derives from MSTest's `AssertInconclusiveException`. A pixel check is never run on an
  unverified capture, and a covered area is never recorded as a pass or a failure.
- Artifact-only captures call `WindowCapture.SaveArtifact`. A capture that is not judged is a note,
  and the test goes on.

A capture never raises, topmosts or activates a window except for that single re-assert.

Writing a screenshot is not a pass or fail claim about the product. Every "screenshot written"
check is a `Note` that names the path, and the PNG stays as an artifact under
`artifacts/e2e/visual` or `artifacts/e2e/screenshots`. `HarnessTests` still checks the capture helper
against a live window and deletes the PNG, as a harness self-test that is also recorded as a note.

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

## 20. Dialogs that open behind their owner or another app

The harness used to force every window of the app topmost on a watchdog thread, so a dialog that
opened behind the main window or behind another app was lifted before any check saw it. That hid
z-order and activation bugs. The watchdog, the topmost flags, and the forced activation before
captures are gone. Each owned window that the harness waits for now gets two real checks, made
without any harness help, when it appears (`WaitForOwnedWindow`, `WaitForOwnedWindowText`, and
`DismissMessage` in `AppServicesSupport`):

- `dialog.zorder.<title>`: the window is above its owner in the z order. The check walks
  `GW_HWNDPREV` upward from the owner and expects the dialog in that chain.
- `dialog.foreground.<title>`: within 1.5 seconds the dialog is the foreground window. If the
  foreground belongs to the app but is another window, the check fails and names that window. If
  the foreground belongs to another process, the check is not judged: the note
  `harness.foreign-foreground` records the foreign process name, and the z-order check still decides
  the product question.

A check is never made true by a re-assert. The attached-thread path is used only for input.

## 21. Retries are counted and bounded

`AppServicesSupport.ClickButton` and `AppServicesSupport.DismissMessage` are the helpers that retry
a product input. The rules are:

- The first attempt that fails while the foreground belonged to the app is a failed check
  (`gui.foreground.*` or `dialog.foreground.*`). It is not retried, because a retry would hide the
  focus bug.
- A retry happens only after a foreign-foreground event (`ForegroundGuard.ForeignEvents` changed
  during the attempt). The retry count is bounded at three attempts.
- Any helper that retries records the attempt count in a `Note` with the helper name and the target
  button or message.
- `ClickButton` throws when the dialog is still open after the bounded attempts, so the caller fails
  with the button name. It no longer returns silently.

## 22. Harness state and screenshots are notes

A check that reads only the harness state is information, not a product claim, so it is a note:
for example `appsvc.font.window.ready` (the harness's own foreground read), `find.keys.foreground`,
and `icons.capture.foreground`. The product claims that used to hide in those checks are real
checks now: the font dialog closing re-enables the main window (`appsvc.font.window.enabled`), and
the main window regains focus after it (`appsvc.font.focus.returns`, which is a note when the foreign
process holds the foreground). Every "screenshot written" check is a note with the path, and the PNG
is the artifact.

## 23. Inline editor geometry comes from the screen

The rename box geometry used to be read from the `HelpText` the app wrote about itself. It is now
measured from UI Automation and screen pixels:

- The box bounds are `BoundingRectangle` of the UI Automation element. The box must have a
  positive size and lie inside the window rectangle.
- The row that holds the box is found from the UI Automation tree (the tree item or list item
  that contains the box's vertical center). The box must start inside that row.
- The window is captured as a verified screen copy (section 13), and the box interior is scanned for text ink. The
  text must be visible inside the box and must not be clipped by its right edge.

A rename box whose bounds or ink are missing fails the check. `HelpText` is no longer read.

## 24. Edit > Find through the menu

The menu command id 662 (Edit > Find) is exercised with real keyboard input in
`Find_EditMenuFindRunsFromTheKeyboard`. Alt+E opens the Edit menu, `find.menu.item` checks that the
`MenuCommand_662` item is in it, the F mnemonic opens Find (`find.menu.mnemonic`), and the seeded
sandbox value is searched and its row selected (`find.menu.hit`). The check fails when the menu item
is missing or the Find dialog does not open from the mnemonic, so a menu that loses the command or
its mnemonic cannot pass. Only UI Automation reads are used for the checks; the keys are real input.

## 25. Non-finite measurements are failed checks, never JSON

A row or edge measurement that finds no edge is NaN, and a division by a zero extent is infinity.
JSON cannot carry either, and `System.Text.Json` throws on them, which used to abort the whole icon
test after its checks had run. `IconTests.WriteSummary` now records `icons.measure.finite` as a failed
check that names every non-finite measurement, and it writes only the finite values.

## 26. Pixels read before the paint settles

A WinUI 3 window is black on its first frames, then shows its background, then its text. A screen
copy taken as soon as a window, menu, or dialog appears reads that paint in progress. The failures
this caused were a dialog background read as `#000000` (expected `#2B2B2B`), icon ink of zero,
a tree or list selection color of `#000000`, a popup separator that was not found, and an artifact
PNG that was entirely black. The product was correct each time; the sample was early.

- Every capture that is judged or saved goes through `WindowCapture.CaptureArea`. It copies the
  verified area again every 200 ms until five consecutive copies, a quiet period of about 1 second, are
  identical and the area is not unpainted. Only then is the bitmap returned. A capture is never
  judged from a copy that is still changing.
- A copy is unpainted when more than 40 percent of its pixels are pure `#000000` (R, G and B all
  zero). No BWC palette (light, dark, high contrast) uses a pure black background, so a black
  majority means WinUI has not painted yet. Identical black frames never settle.
- The wait is bounded at 10 seconds. When the bound passes while the last copy is still unpainted or
  still changing, the capture is not judged: the note `harness.capture.unsettled` records the area
  and the reason, and `NotJudgedException` is thrown, so the test is inconclusive. No pixel check is
  ever made on a copy that has not settled. A partly painted dialog (chrome drawn, labels missing)
  is a changing copy, so it is never judged.
- A legitimately uniform area that is not black (for example a one pixel background sample) settles
  as soon as five copies match. It does not wait for the bound.
- A window that is not painted because another app holds the foreground is not judged in this
  mode, and the harness does not raise or activate the window to change that.
- Every copy is verified by the foreign-window rule of section 13 after it is taken. A foreign
  window that covers the area during the wait still throws `NotJudgedException`. The settle wait
  adds no topmost flag, watchdog, or extra activation.
- Animated content, such as a classic menu fading in, uses the same rule. A popup is scanned only
  after its frames have been identical for the quiet period, so the separate fade wait in the menu tests is gone.
- A pixel check that fails after an unsettled note is still a real check. Open the PNG from the
  same capture (`artifacts/e2e/visual` or `artifacts/e2e/screenshots`), which is taken through the
  same rule, to see what the product showed. Setting `BWC_E2E_DUMP_JUDGED=1` also writes the last
  judged copy of each area to `artifacts/e2e/judged/`, which is the exact bitmap a check read.

## 27. A window that closes before its capture

A dialog that is finished by a fixture (a manifest the Hub waits for) can close while the test is still
reaching it. The test then holds the handle of a destroyed window, `GetClientRect` fails, and the contrast
check records "The dialog has no client area to capture." The product did its job; the test was late.
The failures this caused in `Hub_CheckForUpdates_LocalManifest_UpToDateAvailableAndCancel` were the progress
window's background and text contrast, while the screenshot showed the next window (Update Available).

- A timed delay (2 seconds) is not enough: the progress window is found, raised, and settled (about 1 second
  of settle wait) before its capture, and on a slow VM the delay ends first. Timing alone is not a fix.
- The fixture holds the manifest response until the test releases it (`UpdateFixtureServer.HoldManifest` and
  `ReleaseManifest`). The release happens after the progress window is checked and captured. The hold is
  bounded (60 seconds), so a test that fails before the release still lets the Hub finish, and the Hub's own
  3 minute limit still applies.
- A held response that is never released is a test failure, not a skipped check. The progress window check
  (`hub.update.progress.dialog`) and both contrast checks still run and are judged.
- A window found by title can belong to another window with the same title. The progress window and the
  message box share "Check for Updates", so the lookup for the progress window must run before the release.

## 28. UIA properties that a control does not support

FlaUI's `AutomationElement.Name` (and any `Properties.X.Value`) throws `PropertyNotSupportedException` when the
control does not expose that property. A text or document peer with no name hits this, and the exception is
not one of the COM or invalid-operation types a caller usually catches, so it aborts the whole check.

- Read a property that may be unsupported through `Properties.Name.TryGetValue` or `ValueOrDefault`, never
  through the plain property, when the control type is not known to expose it.
- An element whose name is unsupported or empty is skipped by the text contrast check, because it has no
  text the check can identify. The check still fails when no visible text is left at all
  ("No visible static text control was found").
- A skipped element is not a pass. The check that reads the pixels still decides the contrast of every
  text rectangle that was found.

## 29. Closing or adopting a Registry Editor the test did not start

Name matching is not ownership. `Process.GetProcessesByName("BwcRegedit")` returns the developer's own
Registry Editor, a Registry Editor from the ship folder, and a Registry Editor from an earlier run. The Hub
test used to close every one of them before its launch check, and it adopted the first new one it found.

- A process is the test's only when its image is the `BwcRegedit.exe` in the publish folder under test
  (`InstallEvidence.HasImage`) and it started after this test run began (`E2EConfig.StartedUtc`).
- `CloseRegeditLeftovers` closes only processes that meet both rules. Each close is a note that names the pid.
- `WaitForNewRegedit` adopts only a process that meets both rules and started after the launch check began.
- A Registry Editor from another folder or another user is never closed, adopted, or waited for.

## 30. Enter on the Hub tool row does not reach the launch path

`hub.regedit.process` failed in the full suite and passed when the run was filtered to the Hub tests. The Hub
log for that run had no launch line, the screenshot showed the Hub with the Registry Editor row selected and the
status line still "Ready.", and no leftover Registry Editor was running when the check started
(`hub.regedit.leftover` was never noted, and the About check recorded `bwcRegeditRunning=0`). The launch check
therefore did not fail because a process was forwarded to an older instance.

- The Hub's launch runs only from `OnToolListKeyDown` when the Enter reaches the tool list, and the Hub ignores
  a key-down with `KeyStatus.WasKeyDown` set. A key that is still held from an earlier step makes the next
  Enter look like a repeat.
- Before the Enter, the check records the foreground window, the focused element, and whether VK_RETURN is still
  down (`hub.regedit.preenter`). A held Enter is released and the check waits for it to come up before sending a key.
- The Enter is sent only after the row is verified selected and the Hub is foreground. If the Hub log then has
  no launch line, the check fails with the status text and the recorded foreground window.

## 31. Flavor read from the binary, not from configuration

The e2e flavor guard used to read the `BWC_FLAVOR` value, which the harness itself sets. An e2e test could run
against a ship folder when the environment said e2e. `RequireE2EFlavor` now reads the `BwcFlavor`
`AssemblyMetadata` attribute from `BetterWindowsConfig.dll`, `BwcRegedit.dll` and `BwcUpdate.dll` in the
publish folder with `System.Reflection.Metadata`. The assemblies are never loaded or executed. A missing
attribute, a missing assembly, or a value other than `E2E` fails the mutating or E2E-only test.

## 32. Classic Registry Editor state changed by a test

`C:\Windows\regedit.exe` saves its state (LastKey, View, FindFlags, window placement) under
`HKCU\...\Applets\Regedit` when it exits. Tests that start it, and the classic oracle script, changed the
developer's state.

- `AppletState` captures the whole `Applets\Regedit` tree before a classic run: subkeys, value names, and
  exact `RegistryValueKind` with the data read without environment expansion. Restore writes every value
  back with its kind, deletes values and subkeys that were not in the snapshot, and deletes the key when it
  did not exist. Restore runs in `finally`, and a mismatch after restore fails the check.
- `classic-selection-oracle.ps1` starts its own `regedit.exe` and uses only the process object it got from
  `Start-Process`. It refuses to run when another regedit is already running, instead of attaching to it, and it
  restores `Applets\Regedit` the same way. Its header states that the applet state is restored.

## 33. Probe key outside the sandbox left behind

The command-line test probes `HKCU\Software\BWC-CLI-Outside` to show the write policy refuses it. A fixed name
can collide with another run or a developer key. The probe key name now includes the run id
(`Software\BWC-E2E-Outside-<run-id>`), which is still outside `Software\BWC-E2E\<run-id>`. The cleanup deletes it
only when this test saw the app create it (`commandline.import.outside.denied` observed the key). It is not
expected to exist, so a found key is a failed check.

## 34. Seeds above the 5000 entry limit

A test seed is at most 5000 keys and values in total, counting the root key. `LargeListSelectionTests` seeded
10,001 values; it now seeds 4,999 values plus the root. The product sorts in the background only above 10,000
rows, so that path cannot be seeded under the limit. It is recorded as the known gap `list.large.backgroundSort`.
`ExportRangeTests` seeded 5,002 keys; it now seeds 4,997 keys under `CancelExport`, plus that key and the root.

## 35. Test data and staging folders left behind

- `artifacts/e2e/data/<run-id>` is deleted at the end of every run. It is kept only when the run failed and
  `BWC_E2E_KEEP_DATA=1` is set, so a failure can be inspected.
- The `<run-id>` parent folders made by the AppServices and ImportResult tests are deleted with the test.
- The Hub's update helper stages to `%TEMP%\BWC-Update`, which is shared with the developer's own installs.
  The Hub under test gets a per-run `TEMP` and `TMP` inside its data root, so its staging folders are created
  there. Only a staging folder the test can prove it made (the snapshot diff in `InstallEvidence`, and a
  receipt that names the fixture version) is removed from the shared root.

## 36. Inherited environment reaching the test host

`scripts/test-e2e.js` used to pass the whole parent environment to `dotnet test`. A shell that had
`BWC_DATA_ROOT`, `BWC_E2E_*`, or `NODE_OPTIONS` set could redirect the test's data or change how Node ran.
The test host now receives a filtered environment. `BWC_E2E_*`, `BWC_PUBLISH_DIR`, `BWC_DATA_ROOT`, and
`NODE_OPTIONS` are dropped from the inherited set. `BWC_PUBLISH_DIR` is read once, then the runner sets
`BWC_PUBLISH_DIR`, `BWC_RID`, `BWC_FLAVOR`, and `BWC_RUN_ID` itself. A run id of `.` or `..` is refused, because
it would name the parent of the sandbox.

## 37. Registry writes outside the scanned C# files

`scripts/check-registry-writer.js` scanned only C# files, so a PowerShell or JavaScript helper could write the
registry without being seen. The checker now also scans `tools/`, `scripts/`, `.ps1`, `.js`, and `.mjs` files for
`reg add|delete|import` (in code, not only in literals), `Set-ItemProperty`, `New-Item`, and the other registry
cmdlets on `HKCU:` or `HKLM:` paths, and `RegSetValueEx` or `NtSetValueKey`. A `CreateSubKey(` or `SetValue(`
call in a file that does not name a registry type is flagged, unless the file is on the allowlist with a reason.
Each allowlist entry names the sandbox the file writes into or why the call is not a registry write.

## 38. Fixed sleeps before a negative check

A check that a dialog or a sort does not appear used `Thread.Sleep` and then looked once. A slow machine can
show the dialog after the sleep, and a fast one wastes the sleep. Negative checks now poll for the whole window
(`DialogsThatAppear` in the Hub test, and a loop in the AppServices test), and the wait for the re-sort polls until
the realized rows are in name order.

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

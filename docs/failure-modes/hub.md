# Hub failure modes

Scope: `src/Bwc.Hub` startup, the launcher window, tool launching, settings, single instance
activation, and the Hub E2E checks. This document describes how each failure is detected and what
the Hub does about it.

## Startup and identity

The Hub manifest keeps `requestedExecutionLevel` at `asInvoker`, so launching the Hub never raises
a UAC prompt. `AppIdentity.SetCurrentProcessAppUserModelId` runs before the first window and keeps
the existing `BetterWindowsConfig.Hub` id. A failed identity call is traced in ship builds and does
not stop startup.

`Log.Initialize("hub")` runs first, then `CrashHandler.Install("hub", showDialog: true)`, then the
single-instance gate. A startup log entry records that the Hub opened. If the log directory cannot
be created, logging falls back to `Trace` and startup continues.

## Missing tool executable

The launch path is always `<publish directory>\<Tool.ExeName>`, where the publish directory is
`AppContext.BaseDirectory`. Before starting anything the Hub checks `File.Exists`. When the file is
missing, `HubStatus` shows a message that names the expected full path, the failure is logged with
`Log.Warn`, and no process is started. The rest of the tool list stays usable.

## Launch failure

`Process.Start` uses `UseShellExecute = true` so Windows applies the target executable's own
manifest, exactly as Explorer would. The working directory is `AppContext.BaseDirectory`, and the
returned `Process` object is disposed immediately because the Hub does not manage the child
lifetime. No `--elevated` argument is passed.

A `Win32Exception` is caught and its message, which includes the Win32 error text, is written to
`HubStatus`. The exception is also logged with `Log.Exception`. Other unexpected start failures use
the same status path with the exception message. A successful start clears the error and shows a
short launched message. Startup and launch errors never open a MessageBox.

## UAC and elevation

The Hub stays at medium integrity. Because `UseShellExecute` is true, the child's manifest decides
whether it elevates. If the tool requests `highestAvailable` and the user is an administrator, the
child runs at high integrity after the normal consent prompt. The Hub does not wait for the child
and does not try to elevate itself. If the user cancels the consent prompt, `Process.Start` fails
with the cancelled error and that text appears in `HubStatus`. E2E runs set `__COMPAT_LAYER` to
`RunAsInvoker` so the same code path is tested without a prompt.

## Single instance race

The first Hub instance creates the named mutex and starts the pipe server. A second instance opens
the same mutex, sees that it is not the owner, sends `activate`, disposes its own gate, and exits
without creating a window. The mutex creation is atomic, so two simultaneous starts cannot both
become first.

`SendActivate` retries three times with a 200 ms pause, which covers the small window where the
first instance owns the mutex but has not created its pipe yet. If every attempt fails, the second
instance logs a warning and exits. It never shows a dialog and never creates a second window. If
the first instance exits during the handoff, the second instance still exits, and the next start
becomes first because the kernel objects are gone.

## Activation restore

The first instance receives `activate` on the thread pool and marshals the work through
`DispatcherQueue.TryEnqueue`. It calls `Activate` and restores the window when the presenter is
minimized. An unknown message is ignored. If the window reference is not assigned yet, the message
is dropped without crashing. If the window has already closed, the dispatcher queue no longer
accepts the work and nothing happens. `Window.Closed` calls `App.Shutdown`, which unsubscribes the
message handler and disposes the gate so a later start is a clean first instance.

## Duplicate launch

Every Launch click starts a new child process. The Hub does not serialize clicks or keep a child
handle, so two clicks produce two processes. Whether the second process becomes another window or
activates an existing one is up to the tool. The status line reflects the most recent click. The
Launch buttons are ordinary buttons, so Enter and Space activate them, Tab moves through the rows,
and the first Launch button receives focus when the window loads.

## Settings corruption

Window bounds load from `AppPaths.SettingsFile("hub")` through the source-generated
`HubSettingsContext`. `SettingsStore` moves malformed JSON aside to a `.bad-<timestamp>` file and
returns default settings. An IO error is logged and the Hub opens with default bounds. Bounds are
clamped to the current display work area, with a 640 by 480 minimum where space allows, so a saved
position from a removed monitor cannot put the window off screen. Saving during close is best
effort: a failure is logged and does not block the close, and a minimized window does not overwrite
the last normal bounds.

## Crash handler

`Application.UnhandledException` reports through `CrashHandler` and deliberately leaves `Handled`
false, so a fatal error still terminates after the log entry. `AppDomain.UnhandledException` and
`TaskScheduler.UnobservedTaskException` remain installed for the process lifetime. The crash
handler is the only Hub code path that shows a MessageBox. `App.Shutdown` does not uninstall it,
because the process is already shutting down and a late failure should still be reported.

## Logging failure

Every `Log` call is non-throwing. If initialization failed, or the disk, permissions, or an
antivirus lock blocks a write, entries go to `Trace` instead. Startup, launch errors, settings
warnings, and shutdown still run; only the file destination is lost. `Log.Shutdown` runs after the
window closes and is safe to call more than once.

## Version string

The header reads `AssemblyInformationalVersionAttribute` from the running assembly. The SDK may add
build metadata after a `+` sign, and that full value is displayed as the informational version.
When the attribute is absent, `Assembly.GetName().Version` is used. If both are unavailable, the
header shows `unknown`. The version is display only: the Hub performs no network call and starts no
process for it. The E2E check only requires a non-empty `HubVersion`.

## High contrast and DPI

The window uses 12 DIP Segoe UI text and the shared `Bwc.UI` Dense theme resources. It does not add
a second theme dictionary. Secondary text uses Fluent theme brushes, so the high contrast palette
replaces them when high contrast is enabled. The manifest declares PerMonitorV2 awareness, and
layout uses auto and star columns with wrapping text, so 200 percent scale and a narrow work area
keep the name, description, and Launch button readable. Window bounds are clamped to the display
work area on restore.

## Stale process matching in E2E

The Hub test snapshots `BwcRegedit` process ids immediately before invoking `Launch_regedit`, and
then only considers ids that did not exist before and whose start time is not older than the test
start. This prevents the test from closing a Registry Editor window that the user or another test
opened. If no matching process appears within 15 seconds, the test records a failed check and
closes nothing. A matched child is closed with `CloseMainWindow` first; a kill is only used on that
specific new pid and only after the graceful close times out. The Hub itself is closed through its
`AppSession`.

## Verification

`tests/Bwc.E2E/Hub/HubTests.cs` covers the window title, the stable automation ids, the version
text, the Launch button state, the About text, the child window title, and cleanup of the child and
the Hub. Release builds with `-p:Platform=ARM64` must have zero warnings. Run the checks with:

```
node scripts/dotnet-build.js --rid win-arm64 --flavor ship
node scripts/test-e2e.js --rid win-arm64 --flavor ship --filter Hub
```

# Hub failure modes

Scope: `src/Bwc.Hub` startup, the launcher window, the menu bar and tool list, tool launching,
the About dialog, the update check and install, settings, single instance activation, and the Hub
E2E checks. This document describes how each failure is detected and what the Hub does about it.

## Startup and identity

The Hub manifest keeps `requestedExecutionLevel` at `asInvoker`, so launching the Hub never raises
a UAC prompt. `AppIdentity.SetCurrentProcessAppUserModelId` runs before the first window and keeps
the existing `BetterWindowsConfig.Hub` id. A failed identity call is traced in ship builds and does
not stop startup.

`Log.Initialize("hub")` runs first, then `CrashHandler.Install("hub", showDialog: true)`, then the
single-instance gate. A startup log entry records that the Hub opened. If the log directory cannot
be created, logging falls back to `Trace` and startup continues. The window icon is `bwc.ico`
beside the executable, and the exe carries the same icon through `ApplicationIcon`. A missing icon
file leaves the default window icon and does not stop startup.

## Tool registry and tool icons

`ToolCatalog.Tools` (in `Bwc.Core`) is the registry: id, display name, executable name, and
description. The Hub keeps no second list. The tool icon is not stored in the registry. It is read
at run time from the tool executable's own icon through the shell thumbnail API at 32 DIP. When the
executable is missing or the shell returns nothing, the row shows no icon and the name and
description still show.

The Status column shows `Installed` when the executable exists beside the Hub and `Not installed`
otherwise. The status is read when the window opens and again before each launch. A missing
executable never removes the row, so the list has the same shape on every machine.

## Menu bar and keyboard

The menu bar is built from `Strings/en-US/Resources.resw`. Each mnemonic is an ampersand in the
resource text. The menu bar and its items use the shared Dense styles (`BwcMenuBarStyle`,
`BwcMenuBarItemStyle`, `BwcMenuFlyoutItemStyle`), looked up through the merged theme dictionaries.
Each leaf registers its action with `Bwc.UI.Menu.MenuItemActions`, so a mnemonic letter runs the
same action as a click. A menu command that throws is logged and the window stays open, because a
fire-and-forget task would otherwise drop the exception. The menu router is
`Bwc.UI.Menu.ClassicMenuKeyboard`, created when the window root loads and disposed when the window closes. Alt followed by a mnemonic opens a top level menu (Alt+H
opens Help), Alt alone or F10 activates the bar, Left and Right move between top level items, Down
opens the flyout, and Escape closes the flyout and restores the previous focus.

The tool list is a `ListView` in single selection mode. Enter and a double click launch the
selected row. The Launch button launches the same row. The list is the first tab stop when the
window opens. Tab moves to the Launch button and then the status line, so every command is reachable
without a pointer.

If the menu router cannot be created (the window handle is not available), the menu bar still
works with a pointer, the Hub writes a warning to the log, and only the Alt mnemonics are lost.

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

The update helper (`BwcUpdate.exe`) is started with `UseShellExecute = false` and
`CreateNoWindow = true`. Its manifest is `asInvoker`, so it never raises a prompt. E2E runs set
`__COMPAT_LAYER` to `RunAsInvoker` on the helper as well.

## About dialog

About is a classic dialog (`ClassicDialog`, owned by the Hub window, which is disabled while it is
open) with the product name, the short version, the copyright, and the license (MPL-2.0 with its
URL). Every update message is a `ClassicMessageBox` or the update dialogs in `Dialogs/UpdateDialogs.cs`. The short version is the display version without build metadata:
`0.1.0`, plus the prerelease label when one is set (`0.1.0-beta.1`), plus ` (commit abc1234)` when
the build recorded a full commit hash. The full 40-character hash is never shown. A missing or
non-hexadecimal commit omits the commit part.

When `THIRD-PARTY-NOTICES.txt` is in the install folder, About shows a second button that opens
that file with the shell. When the file is absent, the button is not shown and the text says that
no notices file is included. A failure to open the file is logged and the About dialog stays open.

## Window size and position

The window restores its saved bounds (physical pixels, clamped to the work area and to the minimum
size) from the Hub settings, and saves them when it closes. On the first run there are no saved
bounds, so the window is sized to fit the tool list: the list is as tall as its header and rows, with
no empty band under the last row. The width is 600 DIP, and the height is measured from the layout
after the first pass, so the client area matches the content at the current DPI.

## Update check

Help > Check for Updates runs `BwcUpdate.exe --check --manifest-url <url> --signature-url <url>.minisig
--channel <stable|beta> --current-version <version>` on a worker task. The UI thread never waits for
it. The progress dialog has one Cancel button. Cancel kills the helper's process tree and reports
nothing, so no result dialog follows a cancel. The check never blocks the window.

Outcomes, in the order the Hub checks them:

1. No manifest URL is configured (none in the build, none in settings, no E2E override). The Hub
   shows the "not configured" message and starts no process.
2. The helper is not in the Hub folder. The Hub shows the expected path and starts no process.
3. The helper cannot be started. The Win32 message is shown and logged.
4. The helper does not finish within 3 minutes. Its process tree is killed and the Hub shows a
   timeout message.
5. The helper prints no JSON line, or the line is not the expected shape. The Hub shows the exit
   code and that no result was returned.
6. `TrustedKeysInvalid` (the release key is still the placeholder). The Hub shows the same
   deliberate "not configured" message as case 1, not an error, because the build has no release key
   yet.
7. `SameVersion`, `Downgrade`, or `PrereleaseOnStable`: nothing newer is offered for this channel.
   The Hub shows "up to date".
8. `ok: true`: an update is staged and verified by the helper. The Hub shows the version, the
   release notes link from the manifest (only the https URL the helper validated), and the buttons
   "Download and Install" and "Later".
9. Any other failure (URL rejected, download failed, signature invalid, and so on): the Hub shows
   the failure name and the helper's detail text.

The staged folder is found by the `receipt.json` version match under `%TEMP%\BWC-Update`. The Hub
never runs an installer itself.

## Update install

Download and Install starts `BwcUpdate.exe --apply <installer.exe> --receipt <receipt.json>
--current-version <version> --install-dir <Hub folder> --wait-pid <Hub pid>`. Before starting it,
the Hub checks whether any tool process runs from the Hub folder. If one does, the Hub shows a
message that names it and stops. The helper would otherwise refuse with `ProcessesStillRunning`
after the Hub has already closed, and the user would see nothing happen.

When the helper starts, the Hub closes its window and exits so the installer can replace files. If
the helper cannot be started, the Hub stays open and shows the error. The Hub does not wait for the
helper and does not elevate. The helper's own exit codes (refused, installer failed, Hub not
started) are logged by the helper and are not shown in the Hub, which has already exited.

If the staged folder is gone (the temp folder was cleaned after the check), the Hub shows "The
update is no longer staged. Check for updates again." and starts nothing.

## Update settings

Help > Update Settings... stores the channel (`stable` or `beta`, default `stable`) and the startup
check flag (default off) in the Hub settings. An unknown channel value loads as `stable`. Cancel
changes nothing. A save failure is shown in the dialog and logged.

When the startup check is on and a manifest URL is configured, the Hub starts a check after the
window opens. An available update opens the update dialog. Other results (up to date, not
configured, or a failure) only set the status line, so startup never shows an error dialog.

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

Every launch (button, Enter, or double click) starts a new child process. The Hub does not
serialize launches or keep a child handle, so two launches produce two processes. Whether the
second process becomes another window or activates an existing one is up to the tool. The status
line reflects the most recent launch.

## Settings corruption

Window bounds and update settings load from `AppPaths.SettingsFile("hub")` through the
source-generated `HubSettingsContext`. `SettingsStore` moves malformed JSON aside to a
`.bad-<timestamp>` file and returns default settings. An IO error is logged and the Hub opens with
default settings. Bounds are clamped to the current display work area, with a minimum size where
space allows, so a saved position from a removed monitor cannot put the window off screen. Saving
during close is best effort: a failure is logged and does not block the close, and a minimized
window does not overwrite the last normal bounds. The settings file lives under `BWC_DATA_ROOT`
when that variable is set.

## Crash handler

`Application.UnhandledException` reports through `CrashHandler` and deliberately leaves `Handled`
false, so a fatal error still terminates after the log entry. `AppDomain.UnhandledException` and
`TaskScheduler.UnobservedTaskException` remain installed for the process lifetime. The crash
handler is the only Hub code path that shows a MessageBox. `App.Shutdown` does not uninstall it,
because the process is already shutting down and a late failure should still be reported.

## Logging failure

Every `Log` call is non-throwing. If initialization failed, or the disk, permissions, or an
antivirus lock blocks a write, entries go to `Trace` instead. Startup, launch errors, settings
warnings, update results, and shutdown still run; only the file destination is lost. `Log.Shutdown`
runs after the window closes and is safe to call more than once.

## High contrast and DPI

The window uses 12 DIP Segoe UI text and the shared `Bwc.UI` Dense theme resources. The palette
comes from `BwcTheme`, so light, dark, and high contrast follow Windows, and the window's
`RequestedTheme` follows `BwcTheme.Changed`. The manifest declares PerMonitorV2 awareness, and
layout uses auto and star columns with wrapping text, so 200 percent scale and a narrow work area
keep the name, description, and status readable. Window bounds are clamped to the display work area
on restore.

## Stale process matching in E2E

The Hub test snapshots `BwcRegedit` process ids immediately before it presses Enter on the Registry
Editor row, and then only considers ids that did not exist before and whose start time is not older
than the test start. This prevents the test from closing a Registry Editor window that the user or
another test opened. If no matching process appears within 15 seconds, the test records a failed
check and closes nothing. A matched child is closed with `CloseMainWindow` first; a kill is only
used on that specific new pid and only after the graceful close times out. The Hub itself is closed
through its `AppSession`.

## E2E-only seams

These seams are compiled only in the E2E flavor (`BWC_E2E`). A ship build ignores the variables
and never reads them.

- `BWC_E2E_UPDATE_MANIFEST_URL` (Hub): replaces the configured manifest URL. The signature URL is
  the same URL with `.minisig` appended. The value must be an http or https URL on loopback.
- `BWC_E2E_UPDATE_TRUSTED_KEY` (BwcUpdate, E2E builds only): base64 of a minisign public key text.
  When set, the check trusts only this key instead of the placeholder list.
- `BWC_E2E_UPDATE_ALLOW_HTTP` (BwcUpdate, E2E builds only): when `1`, the check accepts http URLs.
  Only the check honors it; the apply command still requires https.

The E2E test generates the key pair, the signed manifests, and the signed installer files in a
temporary folder at run time, serves them from a loopback listener in the test process, and
removes the folder afterwards. No private key is committed.

## Verification

`tests/Bwc.E2E/Hub/HubTests.cs` covers: the window title, the stable automation ids, the version
text (short form, no 40-character hash), the tool list with Registry Editor, Enter on the tool row
launching BwcRegedit and the child window title, the Launch button, the About text and its notices
button state, Alt+H opening Help, the "not configured" check result, the "update available" result
for a higher version, the "up to date" result for the same version, and cleanup of the child and
the Hub, and the update settings save. The test process is per-monitor DPI aware, and the Hub's
windows are found by enumerating the visible windows of the Hub's process, because the dialogs are
owned windows that the UIA desktop list can miss. A mouse click on a flyout item hovers the item first
and is repeated while the item is still showing, because a click that arrives with the first pointer
move was sometimes dropped. The interactive check's progress window has the same title as its result
message, so the result lookup waits for the window that holds the message text. Captures go to
`artifacts/e2e/visual/hub/`: each capture is the window's visible frame read from the screen, so the
whole window is in the image. The captures are the main window in light and dark, About, the progress
dialog, and the update dialogs. Release builds with `-p:Platform=ARM64` must have zero
warnings. Run the checks with:

```
node scripts/dotnet-build.js --rid win-arm64 --flavor e2e
node scripts/test-e2e.js --rid win-arm64 --flavor e2e --filter Hub
```

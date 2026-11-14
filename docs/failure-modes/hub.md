# Hub failure modes

Scope: `src/Bwc.Hub` startup, the launcher window, the menu bar and tool list, tool launching,
the About dialog, the update check and install, settings, single instance activation, and the Hub
E2E checks. This document describes how each failure is detected and what the Hub does about it.

## Startup and identity

The Hub manifest keeps `requestedExecutionLevel` at `asInvoker`, so launching the Hub never raises
a UAC prompt. `AppIdentity.SetCurrentProcessAppUserModelId` runs before the first window and keeps
the existing `BetterWindowsConfig.Hub` id. A failed identity call is traced in ship builds and does
not stop startup.

`CrashHandler.Install("hub", showDialog: true)` runs first, then the single-instance gate, and
`Log.Initialize("hub")` runs only in the first instance, after the gate. A second instance never opens
the shared daily log file, so its messages (the activation handoff and its failures) go to `Trace`
through the safe trace helper. A startup log entry records that the Hub opened. If the log directory
cannot be created, logging falls back to `Trace` and startup continues. The window icon is `bwc.ico`
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

Native error 1223 (`ERROR_CANCELLED`) means the user canceled the UAC consent prompt of a tool that
requests elevation. It is a quiet cancel: `HubStatus` shows "Launch was canceled.", the event is
logged at Info (not as an exception), and no error dialog is shown. The rest of the list stays usable.

## Launch triggers

Enter on the list launches the selected tool once per press. Key auto-repeat (a held Enter) is
ignored, so holding the key does not start a process per repeat. A double tap launches only when the
tap lands on a tool row. A tap on the column header, on the empty area under the last row, or on the
scroll bar does nothing. The row under the double tap becomes the selected row before it launches.
The Launch button always launches the selected row.

## UAC and elevation

The Hub stays at medium integrity. Because `UseShellExecute` is true, the child's manifest decides
whether it elevates. If the tool requests `highestAvailable` and the user is an administrator, the
child runs at high integrity after the normal consent prompt. The Hub does not wait for the child
and does not try to elevate itself. If the user cancels the consent prompt, `Process.Start` fails
with the cancelled error and that text appears in `HubStatus`. E2E runs set `__COMPAT_LAYER` to
`RunAsInvoker` so the same code path is tested without a prompt.

The update helper (`BwcUpdate.exe`) is started with `UseShellExecute = false` and
`CreateNoWindow = true`. Its manifest is `asInvoker`, so it never raises a prompt. The Hub does not set
`__COMPAT_LAYER` on the helper, and the helper removes it from its own environment before it starts the
installer or the Hub, so the compatibility layer does not leak into them.

## About dialog

About is a classic dialog (`ClassicDialog`, owned by the Hub window, which is disabled while it is
open) with the product name, the short version, the copyright, and the license (MPL-2.0 with its
URL). Every update message is a `ClassicMessageBox` or the update dialogs in `Dialogs/UpdateDialogs.cs`. The short version is the display version without build metadata:
`0.1.0`, plus the prerelease label when one is set (`0.1.0-beta.1`), plus ` (commit abc1234)` when
the build recorded a full commit hash. The full 40-character hash is never shown. A missing or
non-hexadecimal commit omits the commit part.

The About and update dialogs paint the palette face and palette text through `ClassicDialog`, so
their text is readable in light and dark. A dialog that paints its text without its face (black
text on the window backing) passes UI Automation text checks, so the E2E checks also read the
client-area pixels: the most common color must be the palette face, and each static text control
must have strong contrast against it. A failure names the dialog and the text rectangle.

When `THIRD-PARTY-NOTICES.txt` is in the install folder, About shows a second button that opens
that file with the shell. When the file is absent, the button is not shown and the text says that
no notices file is included. A failure to open the file is logged and the About dialog stays open.

## Window size and position

The window restores its saved bounds (physical pixels, clamped to the work area and to the minimum
size) from the Hub settings, and saves them when it closes. The display is chosen from the saved
rectangle with `DisplayArea.GetFromRect(saved, Nearest)`, the same rule Regedit uses, so a window saved
on a second monitor returns to that monitor and not to the primary display. A saved rectangle on a
display that is gone resolves to the nearest remaining display and is clamped into its work area.

While the window is maximized, the saved bounds are the restored (normal) rectangle, read from
`GetWindowPlacement`, and not the maximized rectangle. The settings file also records
`WindowMaximized`. A later start restores the normal rectangle and then maximizes when that flag is
set. A minimized window still saves nothing. On the first run there are no saved
bounds, so the window is sized to fit the tool list: the list is as tall as its header and rows, with
no empty band under the last row. The width is 600 DIP, and the height is measured from the layout
after the first pass, so the client area matches the content at the current DPI.

## Update check

Help > Check for Updates runs `BwcUpdate.exe --check --manifest-url <url> --signature-url <url>
--channel <stable|beta> --current-version <version>` on a worker task. The check verifies the signed
manifest only, so it downloads no installer and stages nothing. The UI thread never waits for it. The
progress dialog has one Cancel button. Cancel kills the helper's process tree and reports nothing, so no
result dialog follows a cancel. The check never blocks the window. The Hub kills a check that runs longer
than 3 minutes (two manifest reads of at most 60 seconds each, with a margin).

Each channel has its own manifest URL in the build (`UpdateSources.BuildStableManifestUrl` and
`BuildBetaManifestUrl`). A URL set in Hub settings applies to the channel that is selected.

Outcomes, in the order the Hub checks them:

1. No manifest URL is configured for the selected channel (none in the build, none in settings, no E2E
   override). The Hub shows the "not configured" message and starts no process.
2. The helper is not in the Hub folder. The Hub shows the expected path and starts no process.
3. The helper cannot be started. The Win32 message is shown and logged.
4. The helper does not finish within 3 minutes. Its process tree is killed and the Hub shows a
   timeout message.
5. The helper prints no JSON line with an `ok` field, or the line is not the expected shape. The Hub
   shows the exit code and that no result was returned.
6. `TrustedKeysNotConfigured` (the release key is still the placeholder). The Hub shows the same
   deliberate "not configured" message as case 1, not an error, because the build has no release key
   yet.
7. `TrustedKeysInvalid` (a key list that is set but malformed). The Hub shows a distinct message that
   the update key list is invalid. It is not "not configured".
8. `ChannelMismatch`: the server's manifest is for the other channel. The Hub shows a message to check
   the update channel in Update Settings.
9. `ManifestExpired`: the signed manifest has passed its expiry. The Hub shows that and suggests trying again later.
10. `SameVersion`, `Downgrade`, or `PrereleaseOnStable`: nothing newer is offered for this channel.
   The Hub shows "up to date".
11. `ok: true`: an update is offered by the verified manifest. The Hub shows the version, the
   release notes link from the manifest (only the https URL the helper validated), and the buttons
   "Download and Install" and "Later".
12. Any other failure (URL rejected, signature invalid, and so on): the Hub shows a plain sentence that the
   update could not be verified and was not installed. The failure name and the helper's detail go to the log
   only. A manual check shows the sentence; the startup check logs it.

Each failure in 6 to 9 and 12 has its own plain sentence in `Strings/en-US/Resources.resw`, and the code name never
reaches the dialog. A manual check that is clicked while the startup check runs waits for that check and shows its
result (see "Settings version and startup check").

## Update install

Download and Install first refuses while a tool process runs from the Hub folder (the Hub shows a message
that names it and stops; the installer would refuse too, after the Hub had closed).

Then the download step runs: `BwcUpdate.exe --download` with the same arguments as the check. It
re-verifies the manifest, reuses a verified staged installer for the same version and SHA-256 when one
exists, or downloads and verifies the installer. A progress window with Cancel is shown, and it shows the
percentage from the helper's progress lines (the status line shows it too). A failed download is titled
"Update Download Failed"; a canceled download says so and changes nothing. The download is killed after 32 minutes
(the manifest pair plus the helper's 30 minute installer limit, with a margin). A cancel or a timeout
kills the helper, and the Hub then removes any staging folder without a receipt. A failure shows the
helper's failure name and detail.

When the download finishes, the Hub checks that the staged version is still the offered version
(otherwise "The update server now offers a different version. Check for updates again."). It then
starts `BwcUpdate.exe --apply <installer.exe> --receipt <receipt.json> --current-version <version>
--install-dir <Hub folder> --wait-pid <Hub pid>`, closes its window, and exits, so the installer can
replace files. The Hub does not wait for the helper and does not elevate. The installer starts the Hub
again when it finishes. The installer is started with `/S /RELAUNCH /PARENTPID=<helper pid> /D=<install dir>`.
`/RELAUNCH` tells the installer to start the new Hub. A stand-in must do the same only when the switch is present.

If the helper refuses before the installer starts (the path, receipt, downgrade, processes still running,
an elevated helper, and so on), it starts the Hub again with `--update-result <failure>`. The Hub then
shows a window titled "Update Not Installed". "Apps still running" and "elevated" have their own sentences.
`InstallerFailed` (the installer did not finish) says that the update did not install and that the current
version is still in place. Every other code gets a generic sentence that says the same thing. The failure name
goes to the log only. Each message names the current version, so the user can see what is still installed.

### Install from the Hub: failure modes (written before the E2E test)

The GUI path is covered by `Hub_UpdateAvailable_InstallRunsVerifiedInstaller` and
`Hub_UpdateAvailable_TamperedInstallerIsRefused` (`tests/Bwc.E2E/Hub/HubTests.cs`). Both start the E2E
Hub against the loopback fixture server, open Help > Check for Updates and press "Download and Install"
with real mouse clicks, and read the evidence below. The installer is the stand-in
`tests/Bwc.E2E.FakeInstaller`. It writes one record file, starts the Hub again from the `/D=` folder (the
installer does that per the update contract), and exits. The fixture signs the stand-in's real bytes.

| ID | Failure | What the user sees | Detection (check id) |
|----|---------|--------------------|----------------------|
| HI1 | The install press is dropped, or the Available window stays open | Nothing happens after Download and Install | `hub.install.available.closed`, `hub.install.hubClosed` (Hub still running after 60 s) |
| HI2 | The helper started is not the publish copy (wrong or stale `BwcUpdate.exe`) | The update does not run from this build | `hub.install.helperPath`: a `BwcUpdate.exe` from the publish folder is seen, and its pid is the pid the Hub logged |
| HI3 | The helper starts the installer while the Hub still runs (files replaced under a live Hub) | Locked files, half-installed tools | `hub.install.hubExitedFirst`: the Hub pid has exited when the record appears |
| HI4 | The installer command line is not `/S /RELAUNCH /PARENTPID=<helper pid> /D=<install dir>` (a switch missing or extra, `/D=` quoted or not last, the parent pid not a positive number) | Silent install with wrong options, or no relaunch | `hub.install.commandLine`: the record holds exactly four arguments in that order; `/PARENTPID=` is followed by a positive integer, and `/D=<publish folder>` is unquoted. `hub.install.parentPid`: the parent pid is the helper pid the Hub logged |
| HI4a | The stand-in starts the Hub without `/RELAUNCH` in its command line, or does not start it when `/RELAUNCH` is present | The Hub does not come back after an install, or comes back when it should not | `hub.install.relaunched`: a Hub starts from the publish folder when the record has `/RELAUNCH`; `hub.refuse.noRelaunch` for the refusal |
| HI5 | The bytes that run are not the signed bytes (staged file changed after verify, or a tampered download) | Code that was never signed runs | `hub.install.signedBytes`: the record's SHA-256 equals the signed SHA-256 |
| HI6 | A tampered installer is downloaded and the helper refuses, but the Hub shows nothing useful | The user sees no reason | `hub.refuse.message`: the message says the downloaded update did not match the signed release, in plain words |
| HI7 | The refused installer runs anyway | Unverified code runs | `hub.refuse.neverRan`: no record file exists, and no install process starts |
| HI8 | After a successful install the Hub is not started again | The user is left with no app | `hub.install.relaunched`: a new Hub from the publish folder shows its main window |
| HI9 | The Hub's window stays after a refused install or closes early on a refusal | Confusing state | `hub.refuse.hubStays`, and the message box is dismissed |
| HI10 | A staged installer that failed verification is kept in staging | Staged bytes stay around | `hub.refuse.staging`: no staging folder created by the test holds a receipt |

Not covered by these two tests (each is covered elsewhere or is a documented limit):

- A tool running from the install folder blocks the install before the download (`UpdateInstallBlocked`).
  It is a separate Hub check, not part of the handoff.
- An elevated helper. The E2E run is never elevated, and the helper refuses `RunningElevated`
  (`update-verifier.md` H and N).
- A staged installer from an earlier run that was signed by another key. `UpdateChecker.TryReuse` checks the
  signature again before reuse (`update-verifier.md` M). The tests use a fresh key pair per run.
- Installer signature failure by a key the helper does not trust. The update-verifier tests cover it.

Test-side failure modes, so a red run is not blamed on the product:

- The helper runs for well under a second. The test samples process lists every 10 ms, and it cross-checks the
  helper pid that the Hub logged. A miss is reported as `hub.install.helperPath`, not as a pass.
- Logs and the record file are read from the test's own data root, so an old run cannot satisfy a check.
- Staging folders are removed only when the test created them (the folder set is compared before and after).
- The record file is opened with `CreateNew`. A second stand-in run fails rather than overwrite the first.

The E2E test adds no product seam. The install folder is the publish folder, and it passes the install
folder rules in `InstallDirectory.Validate` (absolute, no trailing separator, no quote). The stand-in reads
`BWC_E2E_INSTALLER_RECORD`, which only a test sets, and it starts the Hub only when its command line has
`/RELAUNCH`. A ship build reads neither.

## Update settings

Help > Update Settings... stores the startup check flag (default on) and, when a beta manifest URL is
built in, the channel (`stable` or `beta`). An unknown channel value loads as `stable`. Cancel changes
nothing. A save failure is shown in the dialog and logged.

While `UpdateSources.BuildBetaManifestUrl` is empty, the dialog does not offer Beta (no channel group is
shown), and a saved `beta` channel is treated as `stable` when it is read. Saving the dialog writes `stable`.

When the startup check is on, the Hub starts a check after the window opens. See "Settings version and
startup check" below for what the user sees.

## Settings version and startup check (0.1.1)

`HubSettings.SettingsVersion` is 2 for settings written by 0.1.1. A settings file without it was written by
0.1.0, which always saved `CheckForUpdatesOnStartup: false` because that was the default and not a choice.
On load, a file below version 2 gets `CheckForUpdatesOnStartup = true` and `SettingsVersion = 2`, and the
file is saved once. A new install (no file) starts with the startup check on and version 2 in memory. A user
who later turns the check off keeps it off, because the migration runs only once per file.

The startup check is silent. It shows a dialog only when it finds an update, and then it shows the normal
Update Available window. "Up to date", "not configured", and every failure go to the log only, and the status
line is not changed. A check that was started by the menu is never silent.

A manual Help > Check for Updates that is clicked while the startup check is still running does not start a
second check. It shows the progress window and waits for the running check, then shows that check's result. The
startup check's result is shown once: the waiting manual check handles it, so there is never a second window.

| ID | Failure | What the user sees | Detection (check id) |
|----|---------|--------------------|----------------------|
| SS1 | A 0.1.0 settings file keeps the startup check off after the upgrade | Updates are never checked at startup | `hub.settings.migrate.on`: after launch the file has `CheckForUpdatesOnStartup` true and `SettingsVersion` 2 |
| SS2 | The migration runs on every launch and turns a later "off" back on | The user's choice is lost | `hub.settings.migrate.keepsOff`: a version 2 file with `false` is still `false` after launch |
| SS3 | A new install (no file) does not check at startup | Startup check silently absent | `hub.startup.default`: with no settings file the check runs (the log says it finished) |
| SS4 | The startup check shows a dialog for up to date, not configured, or a failure | A popup on every launch | `hub.startup.noDialog`: after the check finished (log), no Check for Updates or Update Check Failed window of the Hub exists |
| SS5 | A manual check during the startup check does nothing, or shows two results | The click seems ignored, or two windows | Not covered by E2E (timing). Code review only: `_pendingCheck` and `_waiterAttached` in `MainWindow.Updates.cs` |
| SS6 | A saved `beta` channel with no beta manifest fails the check | The check reports a channel error | `hub.settings.betaIsStable`: a file with `UpdateChannel` `beta` is saved as `stable` after OK |

## Update messages (0.1.1)

Every update message is text from `Strings/en-US/Resources.resw`. The helper's failure name (for example
`InstallerHashMismatch`) is written to the Hub log only. A dialog shows one plain sentence per known failure,
and an unknown failure shows a generic sentence. The download and the check have separate titles.

| ID | Failure | What the user sees | Detection (check id) |
|----|---------|--------------------|----------------------|
| MS1 | A failed download is reported as "Update Check Failed" | The user thinks the check failed | `hub.refuse.title`: the window title is "Update Download Failed", and the status is "Update download failed." |
| MS2 | The dialog shows the failure name (`InstallerHashMismatch`) | Technical text for the user | `hub.refuse.message`: the text has the plain sentence and not the failure name |
| MS3 | The progress window has no percentage (the percentage is only in the status line behind the modal) | No sign of progress | Not covered by E2E (the fixture has no slow installer). Code review: `UpdateProgressDialog.SetProgress` |
| MS4 | The expired-manifest message is unclear or names the code | Confusing message | Not covered by E2E. Code review: `UpdateManifestExpired` text in `Resources.resw` |
| MS5 | `--update-result InstallerFailed` shows nothing, or a code name, or says the install worked | The user does not know the update did not install | `hub.updateResult.installerFailed`: the dialog says the update did not install and names the current version, and it does not show `InstallerFailed` |
| MS6 | The Hub with `--update-result` opens without its main window | No way to see the message | `hub.updateResult.window`: the Hub main window and the message both show |
| MS7 | A cancel reads as an error | Cancel looks like a failure | `hub.update.cancel.status` (existing check) reads the cancel wording, not a failure |
| MS8 | `--update-result Unexpected` or a check/download failure with code `Unexpected` shows the generic sentence, or the code name | The user gets a vague message with no hint of an internal error | `hub.updateResult.unexpected`: the dialog says the update did not install because of an unexpected error, names the current version, and does not show `Unexpected` |

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

Before the handoff the second instance calls `AllowSetForegroundWindow(ASFW_ANY)`. Windows only lets the
first instance take the foreground for a process that holds that permission, and the second instance is
the process the user just started, so it grants it. A failed call is traced and the handoff still runs;
the first instance then shows its window without the foreground rights.

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
handler is the only Hub code path that shows a MessageBox. The MessageBox is owned by the Hub
window while that window exists (`CrashHandler.SetOwnerWindow`), and is unowned otherwise. The Hub
clears the owner when its window closes. An unobserved task exception can arrive on the finalizer
thread, so it is logged only and never shows a dialog. `App.Shutdown` does not uninstall it,
because the process is already shutting down and a late failure should still be reported.

## Logging failure

Every `Log` call is non-throwing. If initialization failed, or the disk, permissions, or an
antivirus lock blocks a write, entries go to `Trace` instead. Startup, launch errors, settings
warnings, update results, and shutdown still run; only the file destination is lost. `Log.Shutdown`
runs after the window closes and is safe to call more than once.

## System theme change

The Hub pins `RequestedTheme` on its root so Fluent controls follow the palette override, and a pinned
element never changes its own `ActualTheme` when Windows switches theme. The Hub therefore keeps a
zero-size probe in the window root, outside the pinned `HubRoot`, with no pinned theme. The probe's
`ActualThemeChanged` calls `BwcTheme.RefreshSystem`. A refused WinRT settings subscription then costs
nothing: the probe still reports the change. A change with no palette difference does nothing.

Failure modes: the probe is inside `HubRoot` by mistake (then the handler never fires), or the window
root is pinned (same result). Code review only: E2E cannot switch the system theme without writing
per-user Personalize settings outside `HKCU\Software\BWC-E2E`.

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
- `BWC_E2E_UPDATE_ALLOW_HTTP` (BwcUpdate, E2E builds only): when `1`, the check and the download accept
  http URLs. The apply command honors it too, so the E2E installer's `/RUNID=` and `/SHORTCUTS=` switches
  can be passed; the ship build never reads it.

The E2E test generates the key pair, the signed manifests, and the signed installer files in a
temporary folder at run time, serves them from a loopback listener in the test process, and
removes the folder afterwards. No private key is committed.

## Verification

`Hub_UpdateResult_UnexpectedSaysNotInstalled` covers MS8 (`hub.updateResult.unexpected`), the same way as MS5.
`Hub_WindowBounds_RestoresMaximizedNormalRect` covers the maximized saved state (`hub.bounds.*`): a saved
normal rectangle plus `WindowMaximized` restores maximized, and closing it again keeps the normal rectangle.
Launch triggers, the 1223 quiet cancel, the crash owner, and the theme probe are code review only; they need an
interactive shell or a system theme change that the E2E harness cannot make.

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
message, so the result lookup waits for the window that holds the message text. The progress window is held
open by the fixture: the manifest response waits until the test has checked and captured the window, then it
is released (`UpdateFixtureServer.HoldManifest` and `ReleaseManifest`, bounded at 60 seconds). A window that
closes before its capture fails the contrast checks with "no client area" (`e2e-harness.md` section 27).
`Hub_UpdateAvailable_InstallRunsVerifiedInstaller` and `Hub_UpdateAvailable_TamperedInstallerIsRefused` cover the
install handoff from the Hub (section "Install from the Hub"): the mouse press, the helper from the publish folder,
the stand-in's record (the exact `/S /RELAUNCH /PARENTPID=<n> /D=` command line, the signed hash, the staging path, the Hub exit before the
run), the Hub started again, and the refusal of a tampered download.
The 0.1.1 checks are in `HubTests.cs` too: `Hub_SettingsMigration_LegacyStartupCheckTurnsOnOnce` (SS1),
`Hub_SettingsVersionTwo_StartupCheckOffStaysOff` (SS2), `Hub_StartupCheck_NoUpdateShowsNoDialog` (SS3, SS4),
and `Hub_UpdateResult_InstallerFailedSaysNotInstalled` (MS5, MS6). The settings test covers SS6 and the Beta
absence (BE). Every E2E launch writes its own settings file first (version 2, startup check off) through the
shared `Environment` helper, so no test reaches the feed through the startup check. Only the startup and
migration tests change that, and the startup test opts in with no file. A cancel shows "canceled" as soon as
Cancel is pressed; the helper stop that follows does not delay the status. The E2E stand-in installer starts the Hub only when the command line has `/RELAUNCH`.
Captures go to
`artifacts/e2e/visual/hub/`: each capture is the window's visible frame read from the screen, so the
whole window is in the image. The captures are the main window in light and dark, About in light and
dark, the progress dialog, and the update dialogs. Every dialog check also runs `DialogContrast` on the
client area (see the About section). `Hub_DarkTheme_DialogsAreReadable` covers About and the
not-configured message in the dark palette. The loopback update checks
run in light only. Release builds with `-p:Platform=ARM64` must have zero
warnings. Run the checks with:

```
node scripts/dotnet-build.js --rid win-arm64 --flavor e2e
node scripts/test-e2e.js --rid win-arm64 --flavor e2e --filter Hub
```

# Registry Editor app services failure modes

Scope: the Registry Editor startup path in `src/Bwc.Regedit.App` (`App`, `MainWindow`), the
command line in `RegeditCommandLine`, and the services it wires up: `Log`, `CrashHandler`,
`SingleInstance`, window placement, View > Font, View > Theme, Help > About, `regedit file.reg`
import, and the Load Hive and Unload Hive enable rules. This document was written before the code.
Each entry says how the failure is detected and what the app does about it.

Settings are stored in `AppPaths.SettingsFile("regedit")`, which is `settings.json` under the BWC
data root (`BWC_DATA_ROOT` when set, otherwise `%LOCALAPPDATA%\BetterWindowsConfig\regedit`). The
application never writes classic regedit's `HKCU\Software\Microsoft\Windows\CurrentVersion\Applets\Regedit`
key.

## Startup and command line

### Console operations never open a window

`/s`, `/e`, and `/a` run in `RegeditCommandLine.TryRun` and return an exit code. The GUI services
below are not started on that path: no `CrashHandler`, no `SingleInstance`, no window. A console
operation with a bad switch returns exit code 2 and writes the usage text to standard error.

### Positional .reg path selects the GUI import path

A single positional argument that ends in `.reg` (case insensitive) is not a console operation. The
GUI path starts, and the window asks to import it (see the import section). A `.reg` path combined
with `/s`, `/e`, or `/a`, a switch without its file, or a second positional argument returns the
usage error with exit code 2. A `.reg` path that does not exist is reported by the import
confirmation path, not by a usage error.

### Positional path that is not a .reg file

Any positional argument is an import file, not only a path ending in `.reg`. Classic regedit offers
to import any file it is given, so a file with another extension opens the same confirmation as a
`.reg` file. The usage error (exit 2) is kept for a second positional path, a switch without its
file, and `/s`, `/e`, or `/a` combined with a positional path. A GUI launch writes nothing to
standard error, so a usage error there was invisible to the user.

Check: no automated check. The E2E launcher does not pass a file argument to the window; the command line
checks cover the exit codes of the console path.

### Whole-registry export on the command line

`/e file` and `/a file` without a key export every root classic regedit shows under Computer, in
tree order, through `RegistryExportScope.WholeRegistry`. The earlier build exported HKEY_CURRENT_USER
only. With a key, the export covers that key and its descendants, as before. A root or subkey that
cannot be read is skipped with its subtree, and the other roots are written.

Check: `RobustnessTests.Robustness_WholeRegistryExportWritesEveryRoot` runs `/e` with no key on the
e2e flavor, where HKCU maps to the run sandbox and the other roots are empty, and checks that the file
has `[HKEY_LOCAL_MACHINE` and `[HKEY_CURRENT_USER` headers.

### DisableRegistryTools policy

Classic regedit honors `HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\System\DisableRegistryTools`
and the same value under HKLM. The larger of the two values counts. A value of 1 or 2 means registry
editing is disabled: the window is not opened. The application shows the classic message (RT_STRING
40, `Registry editing has been disabled by your administrator.`) in a message box with the caption
`Registry Editor`, and exits with code 1. A value of 2 also refuses a silent import with `/s`: the
message goes to standard error and the exit code is 1, and no key is written. `/e` and `/a` are not
affected by the policy. The application only reads the policy and never writes it.

For E2E only, the `BWC_E2E_DISABLE_REGISTRY_TOOLS` environment variable (1 or 2) replaces the policy
value. The variable is read only in E2E builds, so the ship build never honors it. Tests never set
the real policy values.

Check: `RobustnessTests.Robustness_PolicyOverrideShowsRegistryEditingDisabled` launches the window with the
override set to 1 and checks the message box text, that no window is created, and the exit code 1.
`RobustnessTests.Robustness_PolicyLevelTwoRefusesSilentImport` runs `/s` with the override set to 2 and
checks the refusal text, exit code 1, and that nothing is written.

### Unhandled exception in a window handler

An exception thrown by a synchronous XAML handler reaches `Application.UnhandledException`. The
handler reports it through `CrashHandler.Report`, which logs it and shows the classic error box that
names the log file. The exception is marked handled, so the window stays usable. Out-of-memory
exceptions are not handled and end the process as before.

Check: no automated check. A forced XAML handler exception needs a test-only hook in the window,
which this change does not add. The log entry is the evidence.

### Log initialization failure

`Log.Initialize("regedit")` never throws. If the data root cannot be created or the daily file
cannot be opened for append, the logger writes to `Trace` and every later entry goes there too.
The window still opens. The Registry Editor does not show an error for a logging failure.

### Crash reporting

`CrashHandler.Install("regedit", showDialog: true)` runs once in the GUI path, before the window is
created. An unhandled exception is logged with its stack, then shown in one message box that names
the log file. A second exception raised while the message box is open is skipped by the reentry
guard and written to `Trace` only. Console operations do not install the handler, so a failed
export still reports through its exit code and standard error.

## Single instance

### Second launch activates the running window

The first GUI launch creates `SingleInstance.Create("regedit")`, which owns the mutex
`Local\Bwc.regedit.<sid-hash>.<integrity>` and the pipe `Bwc.regedit.<sid-hash>.<integrity>`. A second
launch without `/m` sends one message and exits with code 0 without creating a window. The message is
`activate` when the command line names no file, or `import:<path>` when it names a `.reg` file. The
running instance restores the window when it is minimized, brings it to the front, and performs the
import when a path is given.

### Message delivery fails

`SendMessage` returns false when the pipe cannot be reached within five seconds, when the first
instance has exited between the mutex check and the connect, or when the message is over 64 KB. The
second launch then continues as a normal window. It never exits silently, so a launch always
produces either a window or an activation.

### Stale mutex after a crash

A crashed first instance abandons its mutex. Windows gives the mutex to the next caller, which then
becomes the first instance and owns the pipe. A stale pipe name is not an error.

### Message arrives before the window exists

The pipe listener starts before the window. A message that arrives while the window is still
loading is posted to the UI dispatcher and handled once the window is created. A message that
arrives after the window closed is dropped, because the process is exiting.

### Multiple instances on purpose

`/m` skips `SingleInstance` entirely: the process neither takes the mutex nor listens on the pipe,
and it always opens its own window. A later launch without `/m` can still become the first instance
while a `/m` process is running. This matches classic regedit, where `/m` allows several copies.

## Window placement

### No saved placement

When `settings.json` has no window fields, the window uses the 1000 by 700 DIP default scaled to the
monitor, as before. Only the first window placement with saved values replaces that default.

### Saved rectangle is off every display

Placement is fitted before the window is shown. The saved rectangle is compared with the work area of
the display it overlaps most (`DisplayArea.GetFromRect` with nearest fallback). The window is moved so
that the whole rectangle lies inside that work area, and a size larger than the work area is reduced
to it. A monitor that was unplugged therefore falls back to a fully visible window on a remaining
display.

### Invalid saved values

A width or height that is zero, negative, or above 32767 is ignored and the default size is used. A
missing `WindowX` or `WindowY` centers the window on the work area. Fields that do not parse are
handled by `SettingsStore`, which moves the damaged file aside as `settings.json.bad-<stamp>` and
starts from defaults.

### Maximized window

The restored rectangle is the last rectangle seen while the window was not maximized, from
`AppWindow.Changed`. A maximized window saves `WindowMaximized: true` and keeps the restored
rectangle, so restoring from the saved state returns to the normal size and then maximizes. A
minimized window never overwrites the saved rectangle.

### DPI change between sessions

The saved rectangle is in physical pixels, the unit `AppWindow.MoveAndResize` uses. A window moved to
a monitor with a different scale keeps its physical size, which is the same behavior Windows applies
to a moved window. The DIP default is still scaled from the XamlRoot scale when nothing is saved.

## Splitter and columns

### Column widths

Column widths are saved under their column keys in `ListColumnWidths`. A saved width that is not finite
or not positive is ignored by `ViewCommands.ApplyColumnWidths`. A key that no longer matches a
column is ignored. The width the list enforces with its 40 DIP minimum still applies.

### Splitter position

The tree pane width is read from the tree host's actual width when settings are captured. The shell
keeps its own private width and exposes no setter, so the saved value is not applied on the next
launch until `ClassicWindowShell` exposes one. Until then a restart uses the 216 DIP default. A saved
value below 120 or above 600 is clamped by the shell's own limits when a setter exists.

## View > Font

### Dialog cancel

The ChooseFont dialog returns false on Cancel and on Escape. Nothing changes, nothing is saved, and
the metrics keep their previous values.

### Chosen size

The chosen size in points is converted to DIPs (points times 96 divided by 72) and applied through
`DenseMetrics.ApplyFont`. The default 9 point face is 12 DIP, the oracle size. Sizes outside the
6 to 72 point range that ChooseFont enforces with `CF_LIMITSIZE` are not produced. A non-finite or
non-positive size is rejected by `DenseMetrics.ApplyFont` with `ArgumentOutOfRangeException`, which
the command reports as a command failure and logs.

### Saved size

`FontSize` is saved in DIPs. A saved value that is not finite or not positive is replaced by 12 on
load. The size applies at startup before the shell is created, so the first layout uses it.

### Face name

Only the size is applied. `DenseMetrics.FontFamily` is fixed at Segoe UI in `Bwc.UI`, and the dialog's
face selection is not stored. This is a known limit reported to the parity plan, not a silent
failure: the saved file keeps only the size.

## View > Theme

### Override values

The Theme submenu has three radio items, System, Light, and Dark (command ids 9101 to 9103 under
the 9100 Theme submenu, which are BWC additions outside the oracle ids). Each calls
`BwcTheme.SetOverride`. A saved value that is not one of the three names falls back to System.

### Radio state

The check mark follows the effective override. It is set when the menu is built and after each
selection. A change made by the system theme does not change the radio state, because the override
is independent of the system theme.

### Persistence

The override is saved when the window closes, with the other view state. A crash before close keeps
the previous value. The override applies before the shell is created, so the first paint uses it.

## Help > About

### Native About box

Help > About calls `ShellAboutW` with the product name, the version, and the copyright, and the icon
from the running executable. This is what classic regedit does, and the oracle has no RT_DIALOG for
it (inventory OQ-07). A `ShellAboutW` return of zero is logged as a warning and does not show an
error. Another modal open skips the call, as the other commands do.

### Version text

The version comes from the assembly's informational version (`0.1.0` or `0.1.0-beta.1`), the same
source the Hub uses. An assembly with no informational version shows the file version
`major.minor.build`. A build with neither shows `unknown`.

## regedit file.reg

### Confirmation

The confirmation uses RT_STRING 304 with `%1` replaced by the full file path, the caption from
RT_STRING 16 (Registry Editor), and Yes and No buttons. Yes is the default. Escape and No leave the
registry unchanged.

### Import result

After Yes, the file is imported through `RegFileImporter.ApplyFile` on a worker thread. A success
shows RT_STRING 228 with the path, caption Registry Editor. A parse, IO, or access failure shows the
existing file error text from `MessageCatalog.ForFileError`, and the engine detail is logged.

### Missing file

A path that does not exist shows RT_STRING 235 (`Cannot import %1: The file specified does not
exist.`) and does not ask for confirmation.

### Writes limited to the path given

The import writes only the keys the file names. The file itself is not modified. The E2E tests use
a file inside the run's artifact folder whose keys sit under the sandbox run root.

## Window close and settings save

### Settings save on each font or theme change

Every View > Font or View > Theme change wrote `settings.json` with a flush on the UI thread. A burst
of changes, such as a theme change followed by a font step, wrote the file once per change.

Rule: a change schedules one save 500 ms later on the UI thread. A later change in that window
restarts the delay, so a burst writes once. Closing the window writes at once, so the last change is
never lost.

Check: no automated check; the close save is the evidence in the settings file after a run.

### Close during a registry write

A recursive delete can run for seconds. Closing the window while it ran tore the window and its
remote connections down under the write.

Rule: a close request during a registry write is held. The window is not closed, the settings are not
saved yet, and the close runs when the write ends. The write is not cancelled.

Check: `RobustnessTests.Robustness_BusyGateDisablesWindowDuringRecursiveDelete` posts a close request during a
recursive delete and checks that the window closes after the delete finished.

## Favorites name

### Failure: a favorite name loses its trailing space

`FavoritesStore.Add` trimmed the name before the duplicate check and the write. Classic regedit keeps
the name as typed, so `Name ` and `Name` were one favorite, and the stored name was not the one typed.

Rule: `Add` writes the name as given. The name must not be empty and must be at most the value name
limit, with no NUL character. The duplicate check is case-insensitive on the name as given.

Check: no automated check. The favorites key is the classic applet store, which the E2E sandbox does not
redirect, so the test does not write it.

## Load Hive and Unload Hive

### Load Hive enable rule

Load Hive is enabled only when the tree selection is the root of HKEY_LOCAL_MACHINE or HKEY_USERS.
It is disabled at the Computer node, at HKEY_CURRENT_USER and its subkeys, at remote computer roots,
and at any other key. The oracle rule is in BEH-022.

### Load Hive target

The hive loads under the selected root. The name prompt validates a single component, and the
target is `HKEY_LOCAL_MACHINE\<name>` or `HKEY_USERS\<name>` accordingly. A name that already exists
is refused by the existing check, which runs against the same root.

### Unload Hive enable rule

Unload Hive is enabled only for a direct child of HKEY_LOCAL_MACHINE or HKEY_USERS that is not one
of the system hives that Windows loads at boot. The excluded keys are SAM, SECURITY, SOFTWARE,
SYSTEM, HARDWARE, COMPONENTS, and `.DEFAULT` under HKEY_USERS, plus the service profile keys
`S-1-5-18`, `S-1-5-19`, and `S-1-5-20`. Windows does not expose a documented check for a mounted
hive root, so this list is the rule used. Inventory OQ-21 stays open for the case where a user
profile hive is selected but is in use by the system.

### Unload of a user hive that fails

A user hive that passes the enable rule but still fails to unload, for example because a process
holds a handle to it, reports the existing unload error text, and the tree is refreshed from the
parent key. Nothing is retried.

### Elevation

Loading or unloading a hive needs `SeRestorePrivilege` and `SeBackupPrivilege`. The E2E tests do not
load hives: they check only the enable rules at the four selections, and they never write the
registry outside the sandbox. A non-elevated load fails with the existing privilege message.

## Settings file

### Corrupt file

`SettingsStore` moves a file that does not parse to `settings.json.bad-<stamp>` and starts with the
defaults. The application logs a warning when the file cannot be read at all, and then uses the
defaults for this session.

### Classic LastKey migration

On the first run with no `settings.json`, the application reads classic regedit's `LastKey` value
from `HKCU\Software\Microsoft\Windows\CurrentVersion\Applets\Regedit` read only, and uses it as the
starting key. The read happens only when `BWC_DATA_ROOT` is not set, so isolated test runs never depend
on the developer's classic state. A missing value, a key that no longer resolves, or a read error
leaves Computer selected. The migration never writes classic's key, and it runs once because the
first save creates `settings.json`.

# Core component failure modes

Scope: `AppPaths`, `Log`, `CrashHandler`, `SettingsStore<T>`, `SingleInstance`, and the Hub
startup wiring that uses them. This document lists how each component can fail, how the failure
is detected, and what the component does about it.

## AppPaths

### Data root environment override

`BWC_DATA_ROOT` is read on every lookup. If the value is blank, relative, malformed, or cannot
be converted to a full path, the component ignores it and falls back to
`%LOCALAPPDATA%\BetterWindowsConfig`. A bad override therefore never produces a relative data
directory. The per-user fallback comes from `Environment.GetFolderPath`. If the operating system
returns an empty or relative value for `LocalApplicationData`, the base is
`Path.GetTempPath()\BetterWindowsConfig` instead. `GetTempPath` is under the same user profile
(`%LOCALAPPDATA%\Temp`), so the fallback stays per user and never returns a relative path. If
`GetTempPath` is also empty, `BaseDirectory` throws `InvalidOperationException` with a clear message;
`Log.Initialize` and `SettingsStore` callers already handle that case as a failed start.

### Tool id traversal

`ForTool`, `SettingsFile`, `LogDirectory`, and `Combine` validate the tool id against
`^[a-z0-9][a-z0-9-]*$`. Uppercase letters, underscores, empty strings, leading hyphens, slashes,
backslashes, colons, and `..` segments are rejected with `ArgumentException`. Concatenation is
never used, so a rejected id cannot escape the base directory. Lookup creates no directory: a
tool that has never written anything leaves the filesystem untouched.

### Shared base directory and elevation

All data is per tool under the base directory. When a tool runs elevated as the same user, it
finds the same files. When a tool runs as a different user, `LocalApplicationData` resolves to
that user's profile, so it gets a separate tree instead of an access-denied error. A shared
machine-wide override of `BWC_DATA_ROOT` deliberately opts out of that separation.

## Log

### Relative or invalid directory override

`LogOptions.Directory` is a diagnostic and test seam. When it is null or blank, the per-tool
directory under `AppPaths.BaseDirectory` is used. A relative override is rejected because it would
write against the process working directory, which can be outside the data root. An override that
is not a valid absolute path falls back the same way. A failed `Initialize` clears `Directory` and
`CurrentFile` so a later crash report says the log path is unavailable instead of naming a path the
logger never opened.

### Initialization failure

`Initialize` never throws. If the directory cannot be created, the path is an existing file, or
the daily file cannot be opened for append, the logger records the failure with the internal safe
trace helper, leaves itself without a destination file, and every later write goes to that helper.
This keeps a logger failure from turning a diagnostic path into a second crash.

### Entry larger than the file limit

Files are rolled when the next complete entry, including its prefix and trailing newline, would
exceed `MaxFileBytes`. A single entry larger than the limit is still written to an empty file so
the message is not lost. The next entry triggers the roll. Part files are named
`log-YYYYMMDD.1.txt`, `.2`, and so on; an existing part is never overwritten. A part created by
another process between the existence check and the open is skipped by retrying the next index.

### Daily cap

A runaway writer (a loop that logs on every frame) must not fill the disk. `LogOptions.MaxFilesPerDay`
(default 10, which is 50 MB with the default part size) limits the files for one UTC day, counting the
daily file as the first. When the next part would be index `MaxFilesPerDay` or higher, the logger writes
one final WARN line ("Log cap reached ...") to the current file, even if that file is over its size, and
then drops every later entry for that day. The flag resets when the UTC day changes. A process restarted on
a capped day sees the full part list, reaches the cap on its first write, and drops entries the same way.
Drops are silent by design: the final line says so, and no entry is written through `Trace`.

### Retention

Retention runs at initialization and when the UTC day changes. It enumerates only files matching
`log-*.txt` directly in the log directory and deletes those whose last write time is older than
`RetainDays`. Non-matching files such as `notes.txt`, settings, and crash reports are left alone.
The daily file currently being written has a fresh timestamp and is therefore not deleted. A
deletion failure goes to the safe trace helper and does not stop logging.

### Concurrent writes and date changes

A single lock serializes initialization, shutdown, day changes, rolls, and writes. Concurrent
callers cannot interleave a prefix with another entry. Each entry is written through a
short-lived append handle that is flushed and closed before the public call returns. This lets
tests and diagnostic tools read `CurrentFile` with an ordinary file read instead of hitting a
sharing violation. The UTC day is checked before every entry; the first entry after midnight
creates the new day's file and runs retention. A system clock moving backwards can reopen an
earlier day's file, which is acceptable because files are append-only.

### Shutdown and reinitialization

`Shutdown` stops the logger under the lock. It never throws. Tests and tools can call
`Initialize` again afterwards. `CurrentFile` and `Directory` keep pointing at the last path so
diagnostics can report where the log went even after shutdown. No file handle is held between
entries, so shutdown does not need to drain a buffer. A write after shutdown falls through to the
safe trace helper instead of touching the filesystem.

### Disk and handle failures

A full disk, a revoked handle, a deleted directory, or an antivirus lock can make a write fail.
Every public method catches the failure, writes to the safe trace helper, and returns. Logging
never propagates an exception to the caller. Exceptions passed to `Exception` are expanded with
their type, message, and stack trace on following lines.

### Trace listener failure

`Trace.WriteLine` is not guaranteed to be quiet: a registered `TraceListener` can throw. Every
trace call in this assembly goes through `SafeTrace`, which catches listener failures. A broken
listener therefore cannot escape `Log`, `CrashHandler`, or `SingleInstance`, including while a
process is shutting down.

## CrashHandler

### Exception while reporting

`Report` writes the log entry first, on every call, including a nested one. Only the dialog is
guarded by an atomic flag. A dialog that pumps messages can dispatch a second unhandled exception on
the same thread; that nested report is still logged, but it sees the flag and skips its dialog instead
of recursing. All work is wrapped, so a failing logger or a failing `MessageBoxW` cannot throw out of
the crash path. Trace fallback calls use `SafeTrace`, so even a throwing trace listener cannot escape
during shutdown.

### Unobserved task exception

`TaskScheduler.UnobservedTaskException` can fire on the finalizer thread, where a modal dialog would
block the finalizer. Its handler reports with dialogs disabled: the exception is logged and nothing is
shown. The `AppDomain` handler is fatal and keeps its dialog.

### Owner window

`CrashHandler.SetOwnerWindow(hwnd)` records the main window handle. The dialog uses it as the owner
so it appears over that window. A zero handle (the default, and what the Hub sets when its window
closes) means the MessageBox is unowned, as before. The owner is read when the dialog is shown, so a
stale handle after close is cleared by the caller and never used for a later report.

### Logging was never initialized

If `Log.Initialize` failed, did not run, or already shut down, `Log.Exception` falls back to the
safe trace helper. The report still reaches a debugger or trace listener. The dialog text reports
that the log path is unavailable instead of failing.

### Dialog in a non-interactive session

`showDialog` is a choice made by the host. Tools that run unattended pass false. The Hub passes
true. When true and no interactive desktop exists, `MessageBoxW` returns an error code or
immediately; it is not used for flow control. Closing the dialog cannot resume a process that is
already terminating.

### Handler lifetime

`Install` subscribes to `AppDomain.CurrentDomain.UnhandledException` and
`TaskScheduler.UnobservedTaskException`. Repeated installs update the tool id and dialog choice
without adding duplicate handlers. `Uninstall` removes both handlers and is safe to call when
nothing is installed. A handler that outlives its intended scope keeps reporting under the last
installed tool id.

### Fatal versus handled exceptions

`AppDomain.UnhandledException` is notification only. `Report` logs and optionally shows a
dialog, but it cannot mark the exception handled. The Hub's `Application.UnhandledException`
handler also deliberately leaves `Handled` false, so a fatal error still terminates after the
report is written.

## SettingsStore

### Missing file

A missing settings file is not an error. `Load` returns a fresh `T` with default values. The
directory is not created by a load.

### Corrupt or wrongly shaped JSON

Invalid JSON, a JSON null root, or a document whose shape does not match `T` is treated as
corruption. The original file is moved, never deleted, to
`<file>.bad-<yyyyMMddTHHmmssZ>` in the same directory. If a file with that exact name already
exists, a numeric suffix is added so the older corrupt copy is preserved. The original bytes are
unchanged by the move. `Load` then returns a fresh `T`.

### Oversized file

`Load` checks the file length before allocating the read buffer. The documented cap is
`SettingsStore<T>.MaxFileBytes`, 1 MiB. A file larger than the cap is treated as corruption: it
is moved aside to `<file>.bad-<stamp>`, the original bytes and length are preserved, and a fresh
`T` is returned. The oversized content is never read into memory. The length check and the read
happen through one `FileStream` opened with `FileShare.Read`, so a writer cannot grow the file
between the check and the read.

### IO errors during load

Errors other than malformed JSON or an oversized file, such as a sharing violation, access
denial, or a failed move, are wrapped in an `IOException` that names the settings path and keeps
the original exception as the inner exception. They are not silently converted into defaults,
because doing so could hide a permissions problem and later overwrite a file the user cannot
read.

### Atomic save

`Save` creates the parent directory, serializes with the source-generated `JsonTypeInfo<T>`, and
writes to `<file>.tmp-<guid>` in the same directory. The stream is flushed to disk, closed, and
the temporary file is moved over the destination with overwrite enabled. Same-directory rename
keeps the replacement atomic on NTFS. A failure deletes the temporary file and rethrows. No
temporary file remains after success or after a handled failure.

### Process termination during save

A hard kill between creating the temporary file and the move can leave a `.tmp-<guid>` file.
Normal failures and normal exits clean it up. Leftover temporary files are ignored by `Load` and
are not treated as settings. Two processes saving at the same time do not corrupt the
destination, but the last successful move wins.

### Trimming and AOT

Only the supplied `JsonTypeInfo<T>` is used. There is no reflection-based serializer call, so
trimming and Native AOT cannot remove members that the serializer needs at runtime.

## SingleInstance

### Identity key construction

The instance key contains the tool id, a short hash of the current user's SID, a short hash of the
install folder (the full path of the application base directory, case-insensitive) and of `BWC_DATA_ROOT`
when it is set, and the process integrity level. Copies installed in different folders, or runs with
different data roots, never forward to each other. If the SID cannot be read, the user name is hashed instead so creation still
succeeds. If the integrity level query fails, the token `unknown` is used. A failure to query the
integrity level never makes `Create` throw, and it cannot make two different integrity levels
share a pipe by accident beyond the explicit `unknown` fallback.

### Mutex ownership

The first instance creates the named mutex with initial ownership. A second instance opens the
same mutex, sees `createdNew == false`, and starts no server. Disposal releases the mutex only
when this object owns it and disposal happens on the creating thread, because Windows mutex
ownership is thread affine. Closing the handle from another thread still releases the kernel
object; it simply skips `ReleaseMutex` to avoid `ApplicationException`.

### Pipe protocol abuse

The server reads a four-byte little-endian length, rejects negative lengths and lengths above
64 KiB with `ArgumentException`, and only then allocates the payload. A client that announces a
larger message is disconnected without the server reading the announced bytes into memory. The
client rejects messages whose UTF-8 encoding exceeds 64 KiB before connecting. Zero-length
messages are legal and raise an empty string.

### Connection failures, timeouts, and named result

`TrySendMessage` connects with a three-second timeout and returns a named
`SingleInstanceSendResult`: `Sent`, `ServerUnavailable`, `TimedOut`, `AccessDenied`, `Disposed`, or
`Failed`. A missing or non-accepting server ends as `TimedOut`, a rejected ACL as `AccessDenied`,
and a broken pipe as `ServerUnavailable`. Every failure therefore has a name, throws no
exception, and has no retry loop of its own. `SendMessage` remains as a compatibility wrapper
that returns true only for `Sent`. The Hub retries `activate` three times with 200 ms gaps. A
server that is between accept iterations has a small window where a client waits inside
`Connect`; the timeout and retries cover it.

### Current-user-only client

The client always uses the local machine name `.` and the pipe name. It never tries the
global pipe namespace, a remote server name, or an impersonation token. The client also passes
`PipeOptions.CurrentUserOnly` (0.1.1 audit), so Windows refuses a server that another user owns:
the client checks the owner before it sends. The constructed name contains no path separators,
and the server and client both use the same identity suffix.

### Session-scoped pipe name (0.1.1 audit)

The pipe name ends with the id of the Windows session that created the instance, for example
`Bwc.<tool>.<identity suffix>.s<session id>`. The mutex keeps its own name. Two sessions of one
user therefore never share a pipe name, and a process in another session cannot take the name
that this session's server needs. Rows:

| ID | Case | Risk | Required result | Test |
|----|------|------|-----------------|------|
| SI1 | Second launch in the same session | Second copy runs | Activates the first; message delivered | `Second_Instance_Message_Reaches_First` (Bwc.Update.Tests) |
| SI2 | Client connects to a server owned by another user | Message to a squatter | Refused by the client (`CurrentUserOnly`); result `AccessDenied` | `Doc` (needs a second account) |
| SI3 | Two sessions of one user | Shared pipe, one session's activation lands in the other | Different pipe names; no cross-session delivery | `Doc` (needs a second session) |

### Accept loop backoff

A pipe name can be taken while the mutex says this process is first, for example when a stale
process still holds the pipe or another component uses the same name. Creating the server then
fails repeatedly. The accept loop applies exponential backoff from 50 ms to 1000 ms and stops
retrying when disposal cancels it. The loop therefore cannot spin at 100 percent CPU while the
name is unavailable. A client that connects and then disconnects without a complete message is
treated as an abnormal client: the server disposes that connection and waits 25 ms before
listening again, so a fast connect-disconnect stream cannot drive an unbounded tight loop.

### Server read timeout

After accepting a connection the server allows 1.5 seconds for the complete four-byte length and
payload. A client that connects and stalls is disconnected when the timeout expires. The next
client is accepted normally. The timeout uses a cancellable `WaitAsync` over the asynchronous
read, and the connection is disposed on every path, so an abandoned read cannot hold the pipe.

### Same-user ACL

The server uses `PipeOptions.CurrentUserOnly`, which asks Windows for a DACL that grants access
only to the current user SID. A different user cannot send activation messages even if it guesses
the pipe name. Because the key also includes the integrity level, a medium-integrity process and
an elevated high-integrity process of the same user use different instances rather than fighting
over one pipe.

### Dispose while accepting

`Dispose` cancels the accept loop, disposes the active server to break a pending
`WaitForConnectionAsync` or read, briefly waits for the loop, and then closes the mutex. It is
idempotent and every cleanup step is wrapped, so disposal cannot throw. If the loop does not
finish within the brief wait, the process can still exit because the loop runs on a background
task. A message handler that throws is caught and traced; it cannot kill the accept loop. A
retry delay is cancellable, so disposal does not wait for a full backoff interval.

### Stale state after a crash

Named kernel objects disappear when the last handle closes. A crashed first instance releases its
mutex and pipe handles, so the next start becomes first. An abandoned mutex can only be observed
while another handle keeps the object alive; the next owner treats it as a normal mutex and
proceeds.

## Hub startup wiring

### Logger failure

`Log.Initialize("hub")` runs before any window exists. A failed log directory does not prevent
startup; messages go to the safe trace helper. The crash handler is installed immediately after
logging.

### Second instance

After setting the AppUserModelID the Hub acquires the single-instance key. A second instance
sends `activate` up to three times, disposes its own instance object, and calls `Exit` without
creating a window. The first instance receives the message on the thread pool, marshals it with
`DispatcherQueue.TryEnqueue`, activates the window, and restores it if it is minimized. Unknown
messages are ignored.

### Settings load and save failures

Window bounds are loaded from `AppPaths.SettingsFile("hub")` using the source-generated
`HubSettingsContext`. A corrupt file is moved aside by `SettingsStore`; an IO error is logged and
the Hub opens with default bounds. Saving during `AppWindow.Closing` is best effort. A minimized
window does not overwrite the last normal bounds. A failed save is logged and does not block the
close.

### Bounds off screen or too small

Restored bounds are clamped to the display work area that contains the window, falling back to
the primary display. Width and height are at least 640 by 480, and at most the work area size. X
and Y are clamped so the window remains fully visible. A work area smaller than the minimum size
wins over the minimum, which avoids an invalid rectangle. The defaults are used if the display
cannot be queried.

### Fatal exceptions

The Hub subscribes to `Application.UnhandledException` and reports through `CrashHandler`.
`Handled` is not set, so the process still terminates after the log entry and optional dialog.
The existing launcher rows, automation ids, and merged resource dictionaries are unchanged.

## Verification

`tests/Bwc.Core.Tests` covers path validation, log writing, rolling, retention, failed
initialization, relative log directory fallback, throwing trace listeners, settings round trips,
corrupt file preservation, oversized file rejection without a large allocation, temporary file
cleanup, single instance behavior, message size rejection, named send results, disposal and
recreation, pipe-name conflict backoff with a CPU-time bound, server read timeout, abnormal
client disconnects, and crash report logging after shutdown. The tests use a per-run temporary
folder and unique tool ids, and they clean up files, pipes, and mutexes. Release builds with
`-p:Platform=ARM64` must have zero warnings.
